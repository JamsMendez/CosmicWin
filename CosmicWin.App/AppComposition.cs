using System.IO;
using System.Threading.Channels;
using System.Windows.Threading;
using CosmicWin.App.Diagnostics;
using CosmicWin.App.Input;
using CosmicWin.App.Startup;
using CosmicWin.App.Tray;
using CosmicWin.Interop;
using CosmicWin.Interop.Win32;
using CosmicWin.Interop.Win32.VirtualDesktops;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App;

/// <summary>
/// Extracts <c>App.xaml.cs</c>'s
/// composition into a plain, directly-testable class. Four consecutive closures defended the
/// composition site from OUTSIDE a WPF <see
/// cref="System.Windows.Application"/> subclass -- three by tightening the type system, the last
/// (<c>CompositionSiteArchitectureTests</c>, now deleted) by reading its source text -- and each
/// closure fell to a mutation the previous one had not anticipated. This class does not add a
/// fifth outside-in inspection: it moves the composition logic itself somewhere a real test can
/// reach it. <see cref="Wire"/> is a plain static method with every collaborator supplied by the
/// caller (mirroring the existing <see cref="CompositionRoot.Build"/>/<see
/// cref="CompositionRoot.BuildPauseGatedSession"/> idiom), so <c>AppCompositionTests</c> drives it
/// end to end with a real <see cref="LowLevelKeyboardHook"/> (via a fake <see
/// cref="IKeyboardHookPlatform"/>, no live desktop needed) and a real <see
/// cref="TrayMenuController"/>, asserting actual pause-gate BEHAVIOR rather than call-site spelling.
/// <see cref="WireProduction"/> is the sole place that supplies the real Win32 collaborators; <see
/// cref="App.xaml.cs"/> now does nothing but call it and dispose the result -- see
/// <c>AppEntryPointThinnessTests</c> for the guard that keeps it that way.
/// </summary>
public sealed class AppComposition : IDisposable
{
    private readonly ActionDispatcher _dispatcher;
    private readonly LowLevelKeyboardHook _hook;
    private readonly IWorkspace _workspace;
    private readonly MultiMonitorWorkspaceAdapter _sessionAdapter;
    private readonly IDisposable _tray;
    private readonly IDisposable _reconcile;

    /// <summary>Both null when no shown-window watcher was supplied -- dialogs are then simply left where they open.</summary>
    private readonly IFocusBorder? _focusBorder;
    private readonly Action _unfollowFocusedWindow;
    private readonly IWindowShownWatcher? _windowShown;
    private readonly FloatingDialogAdapter? _dialogAdapter;

    private AppComposition(
        ActionDispatcher dispatcher, LowLevelKeyboardHook hook, IWorkspace workspace,
        MultiMonitorWorkspaceAdapter sessionAdapter, IDisposable tray, IDisposable reconcile,
        IWindowShownWatcher? windowShown, FloatingDialogAdapter? dialogAdapter,
        IFocusBorder? focusBorder, Action unfollowFocusedWindow)
    {
        _dispatcher = dispatcher;
        _hook = hook;
        _workspace = workspace;
        _sessionAdapter = sessionAdapter;
        _tray = tray;
        _reconcile = reconcile;
        _windowShown = windowShown;
        _dialogAdapter = dialogAdapter;
        _focusBorder = focusBorder;
        _unfollowFocusedWindow = unfollowFocusedWindow;
    }

    /// <summary>
    /// How often the cheap checks run. Everything on this tick must stay cheap, because this is the
    /// responsiveness floor for anything CosmicWin can only notice by ASKING -- a desktop closing
    /// and handing its windows away raises no event we subscribe to.
    /// </summary>
    private static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// How often the FULL reconciliation runs, expressed in watch ticks. <c>Poll</c> enumerates
    /// every top-level window on the system, so it stays at its original two seconds while the
    /// desktop check -- a handful of lookups over tracked windows -- runs five times as often.
    /// Measured as the cause of a one-second lag before windows from a closed desktop were tiled:
    /// the work was cheap, the wait was not.
    /// <para>
    /// WT-1's "polling fallback reconciliation pass on a bounded interval" is this slower tick.
    /// Frequent enough that a window the hook dropped is picked up before the user notices, cheap
    /// enough to ignore: a pass only raises events for windows whose bounds or membership ACTUALLY
    /// changed, so a steady desktop costs one enumeration and nothing else.
    /// </para>
    /// </summary>
    internal const int PollEveryNthWatch = 5;

    /// <summary>
    /// Wires every collaborator, in <c>App.OnStartup</c>'s exact order: <see
    /// cref="CompositionRoot.Build"/> against <paramref name="treeManager"/>'s <see
    /// cref="TreeManager.Primary"/> tree, then <paramref name="treeManager"/> and <paramref
    /// name="focusTrace"/> are ALSO assigned onto
    /// the returned executor (closes: a hotkey mutation on a secondary monitor's
    /// focused window now arranges that SAME secondary tree, not always the primary one; <paramref
    /// name="focusTrace"/> is mandatory rather than optional so the MR-2 diagnostic cannot be
    /// silently dropped from the one composition site that has to carry it), then the hook, then <see cref="MultiMonitorWorkspaceAdapter"/> --
    /// 's real production caller of <paramref name="treeManager"/>
    /// -- then <paramref name="workspace"/>.Open(), <paramref name="hook"/>.Start(), the tray, then
    /// the dispatcher loop.
    /// </summary>
    public static AppComposition Wire(
        IWorkspace workspace,
        TreeManager treeManager,
        WindowRegistry registry,
        IForegroundWindowSource foreground,
        ExceptionListStore exceptionStore,
        IFocusTrace focusTrace,
        Action disableTaskTrigger,
        Func<TimeSpan, Action, IDisposable> scheduleReconcile,
        Func<ChannelWriter<HotkeyAction>, LowLevelKeyboardHook> hookFactory,
        Func<ExceptionList> loadExceptions,
        Action shutdown,
        Func<TrayMenuController, IDisposable> buildTray,
        IVirtualDesktopService? virtualDesktops = null,
        Diagnostics.IDesktopTrace? desktopTrace = null,
        Func<nint, Guid>? resolveWindowDesktop = null,
        IWindowShownWatcher? windowShown = null,
        IFocusBorder? focusBorder = null,
        Action<Action>? scheduleOnOwningThread = null,
        bool focusBorderEnabled = true,
        Action<bool>? persistFocusBorder = null,
        uint? focusBorderColor = null,
        Action<uint?>? persistBorderColor = null,
        bool tilingEnabled = true,
        Action<bool>? persistTiling = null,
        // The desktop's windows, TOPMOST FIRST -- what ActionExecutor.ResolveFloatingWindows needs
        // to answer an untiled focus chord's stack pass. A delegate rather than a new IWorkspace
        // member: IWorkspace.Snapshot is dictionary-insertion order, not z-order, and every
        // implementation and every test double of that interface would have to grow a second
        // ordering guarantee to carry ONE optional composition-site fact that only this one caller
        // needs. Unset -- as in every test that predates it -- ResolveFloatingWindows stays unset
        // too, so the behaviour is exactly what it is today.
        Func<IReadOnlyList<nint>>? zOrder = null)
    {
        // The live answer to "is CosmicWin laying windows out", owned here for the same reason the
        // border flag below is: the tray item, the executor's chord gate and both window adapters
        // all have to read ONE decision, and whichever of them kept its own copy would become a
        // second owner of it.
        var tiling = tilingEnabled;

        // The live answer to "is the border on", owned here because BOTH the tray item and
        // UpdateFocusBorder need it and neither may become the other's source of truth.
        var borderEnabled = focusBorderEnabled;

        // Same shape, same reason. The overlay knows what it is painting but cannot be asked, and
        // the tray has to seed its picker with what is on screen right now.
        var borderColor = focusBorderColor;
        var primary = treeManager.Primary;
        treeManager.TryGetTree(primary, out var primaryTree);
        var workArea = WorkAreaResolver.Resolve(primary);

        // A chord that throws no longer kills the pump; this is where it says so. Without a sink
        // the manager would drop the chord in silence and look perfectly healthy doing it.
        var (dispatcher, executor) = CompositionRoot.Build(
            primaryTree!, registry, foreground, workArea,
            onActionFailed: (action, error) => desktopTrace?.Record(
                $"action-failed {action.Kind} arg={action.Argument} " +
                $"{error.GetType().Name}: {error.Message}"));
        executor.TreeManager = treeManager;
        executor.FocusTrace = focusTrace;
        executor.VirtualDesktops = virtualDesktops;
        executor.DesktopTrace = desktopTrace;
        executor.TilingEnabled = () => tiling;

        // The dimension is inert until something answers these. Left unset -- as every test does --
        // every tree is filed under Guid.Empty and the model behaves exactly as it did before.
        if (virtualDesktops is not null)
        {
            treeManager.CurrentDesktop = () => virtualDesktops.CurrentDesktopId;
        }
        var hook = hookFactory(dispatcher.Writer);

        // Which desktop the USER is on, and where it sits. Kept here rather than asked of the shell
        // on demand, because the one moment it is needed -- a window arriving -- is the one moment
        // the shell's answer is wrong: Windows can have followed the new window before CosmicWin
        // hears about it, so asking then reports where the window took the user.
        //
        // Refreshed from two places, and BOTH are required. The reconciliation tick catches a switch
        // CosmicWin did not make (Win+Ctrl+arrow, Task View); the switch chord refreshes it at once,
        // because leaving that to the tick would leave the answer a full interval stale and send a
        // window opened straight after Alt+2 back to where the user just left.
        var lastDesktop = virtualDesktops?.CurrentDesktopId ?? Guid.Empty;
        var lastDesktopIndex = virtualDesktops?.CurrentIndex ?? 0;

        // The dispatcher runs chords on a pool thread; the overlay is a WPF window and must be
        // touched on the thread that owns it, which is the same one the reconciliation timer already
        // runs on. Unset -- as every test does -- it runs inline, which is what a test on one thread
        // wants. Declared HERE, above the first collaborator that has to reach the border, rather
        // than beside its other use further down.
        var onOwningThread = scheduleOnOwningThread ?? (work => work());

        // What the border was last told to do. Three focus-border defects have been fixed so far and
        // every one of them was verified by a unit fact or by eye, never by a timestamp -- and the
        // whole defect class is "the border is late, or on the wrong rectangle", measured inside the
        // 400ms reconciliation interval, right at the edge of what an eye reliably catches.
        //
        // Recorded on CHANGE only, deliberately. UpdateFocusBorder runs on every tick, so tracing
        // each call would write a line every 400ms for the life of the session and bury the four
        // lines that matter. A transition is the whole signal: when the border let go, and what it
        // moved to.
        var lastBorderDecision = string.Empty;

        // The rectangle the framed window was last seen PASSING THROUGH, while a hand drag or
        // resize is still in flight, and the handle it belongs to.
        //
        // It exists because the live report is an event and the tick is not. Measured on hardware
        // the moment the live follow started working: over one three-second resize the border
        // reached the new width fifteen times and snapped back to the pre-drag width five times --
        // once per reconciliation interval, because the tick reads the only rectangle it has, the
        // window's own, and that one is a gesture behind ON PURPOSE. Two answers, alternating, for
        // the length of the drag.
        //
        // So the live rectangle outlives the event that carried it, until the gesture ends. It is a
        // stand-in for a cache that is deliberately behind, and it is dropped the moment that cache
        // catches up -- otherwise it would be the same staleness pointing the other way.
        nint gestureHandle = 0;
        var gestureBounds = default(Rectangle);

        /// <summary>Where <paramref name="handle"/> is mid-gesture, or null if it is not in one.</summary>
        Rectangle? GestureBoundsFor(nint handle) =>
            gestureHandle != 0 && gestureHandle == handle ? gestureBounds : null;

        /// <summary>
        /// The gesture on <paramref name="handle"/> is over -- it was dropped, or the window it was
        /// measured on is gone. Windows reuses handles, so a rectangle left behind here would land
        /// on whatever takes this one next.
        /// </summary>
        void ForgetGesture(nint handle)
        {
            if (gestureHandle == handle)
            {
                gestureHandle = 0;
            }
        }

        void RecordBorderDecision(string decision)
        {
            if (decision == lastBorderDecision)
            {
                return;
            }

            lastBorderDecision = decision;
            desktopTrace?.Record($"border {decision}");
        }

        /// <summary>
        /// Takes the border off the screen right now, without deciding anything about where it
        /// belongs next.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Called on the way IN to a desktop change, so nothing stale can be seen on a desktop that
        /// has not arrived yet. <see cref="IFocusBorder.Hide"/> is a bare SetWindowPos with no WPF
        /// object behind it, so it needs no marshalling -- which is the whole reason this can run
        /// before the switch instead of being queued behind it.
        /// </para>
        /// <para>
        /// It RECORDS as well as hides, and that is not decoration. RecordBorderDecision suppresses
        /// a repeat, so a border hidden here and re-shown on the same rectangle a moment later
        /// would trace nothing at all -- and a border path that quietly does nothing reads exactly
        /// like a broken one. This repository has already been bitten by that twice.
        /// </para>
        /// </remarks>
        void ReleaseBorder(string why)
        {
            if (focusBorder is null || !borderEnabled)
            {
                return;
            }

            RecordBorderDecision($"released: {why}");
            focusBorder.Hide();
        }

        // Late-bound on purpose. The border is drawn by a local function that has to exist before
        // the adapter does -- it is handed to TreeManager as a callback on the way past -- so the
        // question it asks is routed through a variable rather than through the adapter itself.
        Func<nint, bool> isConstrained = _ => false;

        // Handed to the collaborators that reflow WITHOUT a chord -- a window closing, one leaving
        // for another desktop, a group collapsing -- which is precisely the set that had no path to
        // the border at all and left it a full reconciliation interval behind.
        //
        // Filtered by what actually MOVED, not merely by "a reflow happened". A reflow re-applies
        // geometry to every tile, so an unfocused window snapping back from a drag runs one too, and
        // answering that would place the border on its own unchanged rectangle once per frame of
        // somebody else's animation.
        //
        // Nothing focused is NOT that case, and it is the one the moved list cannot express. Closing
        // the framed window does not move it, it ends it: its handle is in no moved list, so a plain
        // "did the focused window move" test reads false and leaves the border drawn over a
        // rectangle whose window is gone until the next tick. So the early return asks a narrower
        // question -- is there a focused window that DIDN'T move -- and everything else, including
        // having no focused window at all, goes to UpdateFocusBorder, which answers that state by
        // drawing nothing. The extra work when nothing is focused is a Hide the tick was already
        // making every interval anyway.
        void AfterArrange(IReadOnlyList<nint> moved)
        {
            if (executor.ResolveFocusedLeaf() is { } leaf && !moved.Contains(leaf.Window.Handle))
            {
                return;
            }

            onOwningThread(UpdateFocusBorder);
        }

        // Built by this method's CALLER, so it cannot take the callback at construction. Its own
        // reflows are the hotplug and work-area ones -- dormant today, live the moment MM-2/MM-3
        // land, and there is no reason to leave a known-blind call site behind for them to find.
        treeManager.AfterArrange = AfterArrange;

        // TWO switches, ONE gate, and the collapse is deliberate. Pausing stops everything; turning
        // tiling off stops the layout and leaves the desktop chords alone -- but to anything that
        // MOVES a window those are the same instruction, and giving the adapters a second flag to
        // check would be two chances to check only one of them.
        //
        // The difference between the two switches lives where it belongs instead: in the chord path,
        // which is the only place that can tell a desktop chord from a layout one.
        bool LayoutIsFrozen() => hook.IsPaused || !tiling;

        var sessionAdapter = new MultiMonitorWorkspaceAdapter(
            workspace, treeManager, registry, () => exceptionStore.Current, LayoutIsFrozen,
            executor.ResolveFocusedLeaf, AfterArrange)
        {
            ResolveWindowDesktop = resolveWindowDesktop,
            Trace = File.Exists(TraceMarkerPath) ? desktopTrace : null,
        };

        if (virtualDesktops is { } desktopsForArrivals)
        {
            sessionAdapter.ResolveUserDesktop = () => lastDesktop;
            sessionAdapter.SendWindowToDesktop = (handle, desktop) =>
            {
                // Positional, so only the desktop this composition is actually tracking can be
                // aimed at -- an id it has no index for is not something to go looking for.
                if (desktop != lastDesktop || lastDesktopIndex < 1)
                {
                    return false;
                }

                var moved = desktopsForArrivals.TryMoveWindowTo(handle, lastDesktopIndex);
                if (moved)
                {
                    // The move alone leaves the user wherever the window took them. Putting the view
                    // back is the other half of the reported defect, and costs nothing when the
                    // shell never moved them -- switching to the desktop already shown is a no-op.
                    desktopsForArrivals.TrySwitchTo(lastDesktopIndex);
                }

                desktopTrace?.Record(
                    $"ArrivingWindow hwnd=0x{handle:X} sentTo={lastDesktopIndex} ok={moved} " +
                    $"error={desktopsForArrivals.LastError ?? "(none)"}");

                return moved;
            };
        }
        executor.WindowMovedToDesktop = sessionAdapter.RehomeToDesktop;

        // Registry first, workspace second. The registry answers for a tiled window immediately;
        // the workspace tracks every top-level window, which is the only place a NON-tiled one can
        // be found. Snapshot is walked rather than indexed because these run once per chord, and a
        // dictionary kept in step would be more state to get wrong than the walk costs.
        IWindow? resolveAnyWindow(nint handle) =>
            registry.TryGetWindow(handle, out var tracked) && tracked is { IsAlive: true }
                ? tracked
                : workspace.Snapshot.FirstOrDefault(candidate => candidate.Handle == handle)
                    is { IsAlive: true } other ? other : null;

        // One lookup, two verbs -- put back on a window whose move the shell refused, and asked to
        // close by Alt+Q. Both have to reach windows the tree does not contain.
        executor.ActivateUntrackedWindow = handle => resolveAnyWindow(handle)?.TryActivate() ?? false;
        executor.CloseWindowAt = handle => resolveAnyWindow(handle)?.TryClose() ?? false;

        // Three verbs now, and this is the read-only one: WHERE a window is, including one the tree
        // does not hold. A focus chord pressed while an untiled window has the foreground measures
        // the direction from here, which is the difference between landing on the tile the user
        // pointed at and landing on whatever survived.
        executor.ResolveWindowBounds = handle => resolveAnyWindow(handle)?.Bounds;

        // Where a window LIVES, which the untiled handover has to ask directly. The tiled one gets
        // the same answer for free -- its trees are keyed by desktop, so being in one is the
        // answer -- and with no tree there is nothing to read it from.
        executor.ResolveWindowDesktop = resolveWindowDesktop;

        // The fourth, and the only one that reads the adapter rather than the workspace: these are
        // measured from how a window BEHAVED, which is the adapter's business alone.
        executor.ResolveSizeLimits = sessionAdapter.LimitsOf;

        // The resize chord's answer when there is no tree to move a boundary in -- which, with the
        // tiling switch off, is every window on the desktop. The arithmetic is
        // FloatingResize.Apply's; everything here is finding the three things it needs and refusing
        // the windows it must never be pointed at.
        executor.ResizeFloatingWindow = (handle, direction) =>
        {
            if (resolveAnyWindow(handle) is not { IsAlive: true, CanReposition: true } window)
            {
                return;
            }

            // The FULL exclusion rule, user list included -- unlike the focus border, which reads
            // only the automatic half. The difference is that this one MOVES the window. The
            // automatic half keeps the chord off shell chrome: the taskbar is the foreground window
            // the moment it is clicked, and it is visible and unowned, so nothing else would. The
            // user's own list is what "leave this app alone" means, and a resize is exactly the
            // kind of touching it asks CosmicWin not to do.
            if (WindowFilters.IsExcluded(WindowDescriptorBuilder.Build(window), exceptionStore.Current))
            {
                return;
            }

            var display = treeManager.ResolveDisplay(window.Bounds);
            if (FloatingResize.Apply(
                    window.Bounds, display.WorkArea, direction,
                    LayoutTree.DefaultResizeStep, sessionAdapter.LimitsOf(handle)) is { } resized)
            {
                window.SetPosition(resized);
            }
        };

        // The focus chord's answer when there is no tree to walk -- FloatingFocus.Toward reads this
        // once per chord for its candidate list. Left unset when zOrder is null, exactly like every
        // other floating-mode delegate above: unwired, ActionExecutor's own untiled focus branch
        // stays the no-op it already was for every caller that has not opted in.
        if (zOrder is not null)
        {
            executor.ResolveFloatingWindows = () => zOrder()
                .Select(handle => (Handle: handle, Window: resolveAnyWindow(handle)))
                // ALIVE only, the same liveness gate every other resolution in this file applies --
                // a handle Windows has already reused for something else must not be handed to the
                // geometry as if it were still the window that used to live there.
                .Where(candidate => candidate.Window is { IsAlive: true })
                // The FULL exclusion rule, user list included -- the same reasoning written above
                // executor.ResizeFloatingWindow, and for the same reason it applies here too: this
                // is a chord that MOVES focus onto whatever it names, and the taskbar (auto-excluded,
                // visible and unowned the instant it is clicked) and the user's own "leave this app
                // alone" list must both be honoured, not only the automatic half the focus border
                // reads.
                .Where(candidate => !WindowFilters.IsExcluded(
                    WindowDescriptorBuilder.Build(candidate.Window!), exceptionStore.Current))
                .Select(candidate => (candidate.Handle, candidate.Window!.Bounds))
                .ToList();
        }

        // Deliberately NOT filtered to the origin's own monitor, unlike the tiled path's
        // NearestTileToward, which searches only trees.ResolveDisplay(from). That restriction is a
        // property of the TREE -- a tree belongs to one display, so its own leaves are the only
        // candidates it could ever offer -- not a property of the question being asked. With no
        // layout in force there is no tree to be scoped to, and a window sitting on the monitor to
        // the right genuinely IS the window to the right; refusing to look past a display edge that
        // exists only in the tiled model would make this path answer a narrower question than the
        // one the user actually asked by pressing a direction.

        isConstrained = sessionAdapter.IsConstrained;
        workspace.Open();
        hook.Start();

        // Stated at STARTUP, and stated even when it is null. Left to the first repaint, the first
        // window framed after launch would wear the accent for a moment before the stored colour
        // caught up; and saying "the accent" out loud keeps the two paths identical, so the one
        // nobody exercises cannot rot.
        onOwningThread(() => focusBorder?.UseColor(borderColor));

        // Everything that has to happen the moment the layout is put back on duty.
        void ResumeTiling()
        {
            // The windows that opened while it was off were refused by the adapter, and the
            // workspace considers them announced -- so nothing will ever mention them again. Asking
            // for them BY NAME is the only route back; one already tiled costs a single lookup.
            sessionAdapter.AdoptOpenWindows();

            // Then EVERY display, not only the ones that gained a window. While tiling was off the
            // user was free to drag and resize with the mouse, and switching it back on is a request
            // to put the layout back -- which for a display where nothing opened or closed is a
            // request nothing else in this composition would ever make.
            foreach (var display in treeManager.Displays)
            {
                if (treeManager.TryGetTree(display, out var tree) && tree is not null)
                {
                    TreeArranger.ArrangeAndPosition(
                        tree, registry, WorkAreaResolver.Resolve(display), AfterArrange);
                }
            }
        }

        // TC-3-W1: Salir stops the logon trigger BEFORE tearing the process down -- after shutdown
        // there is no guarantee anything still runs. Disable, not uninstall: TC-3 says "disable the
        // Scheduled Task trigger" where ES-4 says "remove", so quitting once must not throw the
        // user's installation away.
        var trayController = CompositionRoot.BuildTrayMenuController(
            hook, exceptionStore, loadExceptions,
            getFocusBorder: () => borderEnabled,
            setFocusBorder: enabled =>
            {
                borderEnabled = enabled;

                // Persisted BEFORE the redraw, so the choice survives even if the refresh throws.
                persistFocusBorder?.Invoke(enabled);

                // Straight through the ordinary refresh, on the thread that owns the overlay. A
                // menu click must reach the screen now, not on the next tick -- a setting the user
                // waits half a second to see reads as one that did not work.
                onOwningThread(UpdateFocusBorder);
            },
            getTiling: () => tiling,
            setTiling: enabled =>
            {
                tiling = enabled;

                // Persisted BEFORE the layout is put back, the same order the border toggle uses
                // and for the same reason: the choice must survive even if the reflow throws.
                persistTiling?.Invoke(enabled);

                if (enabled)
                {
                    // On the thread that owns the trees and the overlay. This arrives from a tray
                    // click, and a reflow places windows and refreshes the border -- the WinEvent
                    // hook's own thread does both everywhere else in this file.
                    onOwningThread(ResumeTiling);
                }

                // Turning it OFF does nothing else on purpose. Every window is left exactly where
                // the layout last put it, which is the honest starting point for a mode whose whole
                // promise is that nothing moves any more.
            },
            getBorderColor: () => borderColor,
            setBorderColor: rgb =>
            {
                borderColor = rgb;

                // Persisted before the repaint, the same order the toggle uses and for the same
                // reason: the choice must survive even if the refresh throws.
                persistBorderColor?.Invoke(rgb);

                // On the overlay's own thread. A WPF window may only be touched by the thread that
                // created it, and this arrives from a tray click.
                onOwningThread(() => focusBorder?.UseColor(rgb));
            },
            exit: () =>
            {
                disableTaskTrigger();
                shutdown();
            });
        var tray = buildTray(trayController);

        _ = dispatcher.RunAsync(CancellationToken.None);

        // WT-1: SetWinEventHook is a best-effort notifier, not a guarantee -- a window created
        // hidden, an event dropped under load, or a hook briefly not pumped all leave the tree
        // disagreeing with the desktop, and nothing else ever looks again.
        string? lastReportedUnmatched = null;
        var lastReportedReinstalls = 0;

        // The arriving desktop's own layout, applied to the work area in force NOW. Its windows
        // were left exactly where they were when the user walked away, so without this they would
        // still be wearing the previous desktop's geometry.
        void ApplyArrivingLayout()
        {
            // The one layout path a desktop chord can still reach with tiling off, and it had to be
            // stopped here rather than at the chord: the desktop chords are deliberately answered in
            // that mode, and this hangs off a successful one. Caught by a fact -- Alt+2 with tiling
            // off was placing two windows -- which is exactly the shape of defect a mode switch
            // wired at only one of its seams produces.
            //
            // Paused counts too, and that closes a smaller pre-existing hole on the way past: the
            // tick calls this for a switch CosmicWin did not make, so Win+Ctrl+arrow while paused
            // used to re-lay the arriving desktop that nothing was supposed to be touching.
            if (LayoutIsFrozen())
            {
                return;
            }

            if (treeManager.TryGetTree(treeManager.Primary, out var arriving) && arriving is not null)
            {
                TreeArranger.ArrangeAndPosition(
                    arriving, registry, WorkAreaResolver.Resolve(treeManager.Primary), AfterArrange);
            }
        }

        // Applied on the chord itself, not left to the timer. The timer remains the safety net for
        // a switch CosmicWin did not make -- Win+Ctrl+arrow, or Task View -- but waiting for it
        // after our own chord showed the user a loose window for up to a full interval.
        // Let go on the way IN. The border used to be refreshed on the way out, through AfterAction,
        // which runs after the shell has already changed desktops, after the arriving layout, and
        // after the handover's activations -- a window of time bounded only by how slow those are.
        // Nothing stale can be seen on a desktop that has not arrived yet.
        executor.BeforeDesktopChange = () => ReleaseBorder("the desktop is about to change under it");

        executor.DesktopSwitched = () =>
        {
            // Before the layout, and not left to the tick: until this runs, "the desktop the user is
            // on" still names the one they just left, and the next window to open would be sent back
            // there -- the reported defect, re-created by its own fix.
            if (virtualDesktops is not null)
            {
                lastDesktop = virtualDesktops.CurrentDesktopId;
                lastDesktopIndex = virtualDesktops.CurrentIndex;
            }

            ApplyArrivingLayout();
        };

        // Called from BOTH the chord and the tick, and it must be both. The chord is what makes the
        // border keep up -- Alt+O moves every window at once, and waiting for the tick left the
        // border on the old rectangle for up to half a second. The tick is the safety net for the
        // changes no chord caused: a mouse click landing on another window.
        void UpdateFocusBorder() => DrawFocusBorder(live: null);

        /// <param name="live">
        /// A rectangle the framed window is passing THROUGH, mid hand-drag, when there is one.
        /// Null everywhere else, which means "read it from the window", and that is the ordinary
        /// case: the window's own bounds are the answer for every change except the one the
        /// workspace deliberately withholds until the user lets go.
        /// </param>
        void DrawFocusBorder(Rectangle? live)
        {
            if (focusBorder is null)
            {
                return;
            }

            // Turned off, it HIDES rather than merely skipping. The overlay is created once and
            // reused forever, so a switch that only stopped drawing would strand the last frame it
            // drew on screen -- an outline around a window nobody is on.
            if (!borderEnabled)
            {
                RecordBorderDecision("hidden: the focus border is turned off");
                focusBorder.Hide();
                return;
            }

            // Resolved STRICTLY from the real foreground, never through
            // executor.ResolveFocusedLeaf(). That one deliberately falls back to the last known leaf
            // when the foreground is untracked -- its remarks name "a dialog or a non-tiled app" --
            // so a focus chord still works from outside the tiled world instead of being dropped.
            //
            // Right for a chord, wrong here. Reported with Sticky Notes, listed in exceptions.conf
            // and made fullscreen: the fallback kept naming the tiled window underneath, and the
            // border went on framing it while another app held the foreground. A border says which
            // window is ACTIVE, and when the active one is not tiled the honest answer is to draw
            // nothing.
            var foregroundHandle = foreground.GetForegroundHandle();

            // With no layout in force there is no tree to ask, so the border answers from the
            // WINDOW instead. Checked before the leaf lookup below rather than folded into it,
            // because that lookup is the very thing that cannot succeed in this mode.
            if (!tiling)
            {
                FrameTheForegroundWindow(foregroundHandle, live);
                return;
            }

            if (hook.IsPaused
                || foregroundHandle == 0
                || !registry.TryGetLeaf(foregroundHandle, out var focusedLeaf)
                || focusedLeaf is null
                || !registry.TryGetWindow(focusedLeaf.Window.Handle, out var focusedWindow)
                || focusedWindow is not { IsAlive: true })
            {
                RecordBorderDecision(
                    $"hidden: no framed window (paused={hook.IsPaused} foreground=0x{foregroundHandle:X})");
                focusBorder.Hide();
                return;
            }

            var onDisplay = treeManager.ResolveDisplay(focusedWindow.Bounds);
            if (!treeManager.TryGetTree(onDisplay, out var visibleTree)
                || visibleTree is null
                || !treeManager.TryGetTreeHolding(onDisplay, focusedLeaf, out var owningTree)
                || !ReferenceEquals(visibleTree, owningTree))
            {
                // GetForegroundWindow can keep naming the cloaked window from the desktop being
                // left during the shell's transition. The registry spans every desktop, so a valid
                // handle is not enough: only a leaf in the tree currently being viewed may be framed.
                RecordBorderDecision($"hidden: 0x{foregroundHandle:X} is on another desktop's tree");
                focusBorder.Hide();
                return;
            }

            // The DISPLAY is still resolved from the settled bounds above, not from this. A window
            // dragged across a boundary has not changed which tree holds it -- that is decided at
            // the drop -- and asking mid-gesture would have the border answer a question the layout
            // has not answered yet.
            var framed = live ?? GestureBoundsFor(focusedWindow.Handle) ?? focusedWindow.Bounds;
            RecordBorderDecision(
                $"around 0x{foregroundHandle:X} [L={framed.Left} T={framed.Top} " +
                $"W={framed.Width} H={framed.Height}]");
            // Broken rather than solid for a window that will not take every size it is offered.
            // Its tile is not the whole story about where it actually sits -- it may overflow the
            // slot, or leave a gap inside it -- and a border identical to every other one would be
            // claiming a precision the layout does not have over it.
            focusBorder.ShowAround(
                focusedWindow.Handle, framed, onDisplay.Scaling, BorderGeometry.DefaultThickness,
                dashed: isConstrained(focusedWindow.Handle));
        }

        /// <summary>
        /// Frames whatever the user is looking at, for the mode where nothing is tiled.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Reported the day the tiling switch landed: with tiling off, the windows on a second
        /// virtual desktop wore no border. It was never about that desktop -- the trace read
        /// "no framed window", not "is on another desktop's tree". The border only ever framed a
        /// LEAF, and with the layout off nothing new becomes one; the first desktop kept its
        /// borders only because its tree had been built before the switch was flipped.
        /// </para>
        /// <para>
        /// So the coupling is cut where it was never wanted. The border is its own switch and says
        /// which window is ACTIVE -- a fact about the desktop, not about the layout.
        /// </para>
        /// </remarks>
        void FrameTheForegroundWindow(nint foregroundHandle, Rectangle? live)
        {
            // Resolved through the WORKSPACE, not the registry: the registry holds tiled leaves,
            // which in this mode is precisely the set that is empty. The workspace tracks every
            // top-level window, which is what "the window in front of the user" means here.
            if (hook.IsPaused
                || foregroundHandle == 0
                || resolveAnyWindow(foregroundHandle) is not { IsAlive: true } window)
            {
                RecordBorderDecision(
                    $"hidden: nothing to frame without a tree " +
                    $"(paused={hook.IsPaused} foreground=0x{foregroundHandle:X})");
                focusBorder!.Hide();
                return;
            }

            // The tree used to answer "is this window on the desktop in view", and it was doing real
            // work: GetForegroundWindow keeps naming the cloaked window from the desktop being left
            // during the shell's transition. With no tree, the shell is asked directly.
            //
            // Guid.Empty is the shell DECLINING to say, which it answers for any window merely
            // mid-creation. Reading that as "somewhere else" is a mistake this repository has
            // already paid for once -- every arriving window was filed under a desktop nobody was
            // looking at, and tiling stopped outright -- so it is read as "here" exactly as the
            // arrival path reads it.
            var named = resolveWindowDesktop?.Invoke(foregroundHandle) ?? Guid.Empty;
            if (named != Guid.Empty && named != lastDesktop)
            {
                RecordBorderDecision($"hidden: 0x{foregroundHandle:X} is on another desktop");
                focusBorder!.Hide();
                return;
            }

            // Reported the same day: with tiling off the TASKBAR wore a border, and so did the
            // notification-area flyout -- clicking either makes it the foreground window, and this
            // path frames whatever the foreground is.
            //
            // Interop's trackability gate is no help and never claimed to be: Shell_TrayWnd is
            // visible, unowned, uncloaked and not a child, so it clears that gate outright and sits
            // in the snapshot like any application window. WS_EX_TOOLWINDOW is what separates
            // chrome from a window, and IsAutoExcluded is where that verdict already lives -- the
            // very same one that keeps the taskbar out of the TREE.
            //
            // Which is the point: with tiling ON, chrome went unframed only because it could never
            // become a leaf. That was the tree answering a question about the LAYOUT and getting a
            // question about the DESKTOP right by accident. With no tree to lean on, the border
            // asks directly, and now both modes answer from the same rule.
            //
            // AUTO exclusions only, not the user's list. A listed app is one the user asked not to
            // TILE; it is still a window they can be looking at, and the border says which window
            // is active.
            if (WindowFilters.IsAutoExcluded(WindowDescriptorBuilder.Build(window)))
            {
                RecordBorderDecision($"hidden: 0x{foregroundHandle:X} is shell chrome, not a window");
                focusBorder!.Hide();
                return;
            }

            var framed = live ?? GestureBoundsFor(window.Handle) ?? window.Bounds;
            if (framed.Width <= 0 || framed.Height <= 0)
            {
                RecordBorderDecision($"hidden: 0x{foregroundHandle:X} has no rectangle to frame");
                focusBorder!.Hide();
                return;
            }

            RecordBorderDecision(
                $"around 0x{foregroundHandle:X} untiled [L={framed.Left} T={framed.Top} " +
                $"W={framed.Width} H={framed.Height}]");

            // SOLID, always. The broken border means "a size this window refuses BITES the tile it
            // is holding right now", and a window holding no tile makes no such claim -- drawing it
            // dashed here would drift the mark back to "this window once refused a size", which is
            // exactly what it was corrected away from.
            focusBorder!.ShowAround(
                window.Handle, framed, treeManager.ResolveDisplay(framed).Scaling,
                BorderGeometry.DefaultThickness, dashed: false);
        }

        // Unfiltered on purpose, unlike AfterArrange: a chord can change WHICH window is focused
        // without moving anything at all, and the border still has to go and find it.
        // Two things after every chord, in this order. A chord that reshapes the layout reaches the
        // adapter through no event at all -- a compliant window moving writes its own new bounds
        // into the cache, so the reconciliation pass compares the rectangle against itself and
        // reports nothing -- which is the same reason the border below cannot wait for one either.
        // Parked windows are asked FIRST so the border is drawn on the layout that results.
        executor.AfterAction = () =>
        {
            sessionAdapter.RetryParkedWindows();
            onOwningThread(UpdateFocusBorder);
        };

        // Following the window itself, not just the chord that moved it. An application may take
        // several frames to settle where it was put -- Windows Terminal animates its resize -- so a
        // single refresh after the chord lands before the window has finished arriving, and the
        // border visibly trails it. This arrives once per frame of that movement, on the hook's own
        // thread, and costs one placement each.
        // WHICH window the border is on, and asking the wrong way is not a near-miss: with tiling
        // off there is no focused leaf at all, so a leaf-shaped question answers "none" for every
        // window on the desktop and the border stops following anything. Dragging is the movement
        // that mode exists to allow, so it is where trailing by a tick would be most visible.
        nint FramedHandle() => tiling
            ? executor.ResolveFocusedLeaf()?.Window.Handle ?? 0
            : foreground.GetForegroundHandle();

        EventHandler<WindowEventArgs>? followFocusedWindow = null;
        EventHandler<WindowBoundsChangingEventArgs>? followDraggedWindow = null;
        EventHandler<WindowEventArgs>? forgetGestureOnRemoval = null;
        if (focusBorder is not null)
        {
            followFocusedWindow = (_, e) =>
            {
                // The settled report is the drop, and it carries the caught-up bounds -- so
                // whatever was being held over for this window has served its purpose. Done before
                // the framed check, because a window dragged while ANOTHER holds focus still ends
                // its own gesture here.
                ForgetGesture(e.Window.Handle);

                // Only the window being framed. During a reflow every tile reports a move, and
                // redrawing the border for windows it is not on is work with nothing to show for it.
                var framedHandle = FramedHandle();

                if (framedHandle != 0 && framedHandle == e.Window.Handle)
                {
                    UpdateFocusBorder();
                }
            };

            workspace.WindowBoundsChanged += followFocusedWindow;

            // The other half, and without it the border sat frozen for the length of a hand-resize.
            // WindowBoundsChanged is withheld for every frame between MOVESIZESTART and MOVESIZEEND
            // -- deliberately, and it stays that way: the layout answers a gesture ONCE, at the
            // drop, and the alternative was measured as a tiled window flickering against its own
            // snap-back dozens of times a second. The border moves nothing, so it may follow.
            //
            // The rectangle comes off the EVENT rather than off the window, because the window's
            // own cached bounds are a gesture behind on purpose.
            followDraggedWindow = (_, e) =>
            {
                gestureHandle = e.Window.Handle;
                gestureBounds = e.Bounds;

                var framedHandle = FramedHandle();

                if (framedHandle != 0 && framedHandle == e.Window.Handle)
                {
                    DrawFocusBorder(e.Bounds);
                }
            };

            workspace.WindowBoundsChanging += followDraggedWindow;

            // One handler, two jobs, one event -- and both are about a window that has just ceased
            // to exist.
            forgetGestureOnRemoval = (_, e) =>
            {
                // The one gesture that never reaches the drop above: the window is destroyed while
                // the user is still holding it. Windows reuses handles, so leaving the rectangle
                // behind would frame the next window to take this one at a size it never had.
                ForgetGesture(e.Window.Handle);

                // Reported from real use, and measured on hardware at 249ms: a closing window kept
                // its border for a moment after it was gone. A close used to reach the border only
                // through the ARRANGE pass -- remove, reflow the survivors, refresh -- and with
                // tiling off there is no reflow at all, so nothing but the tick ever noticed.
                //
                // Unconditional rather than filtered to the framed window, and that is the cheaper
                // reading as well as the honest one. The handle CANNOT be compared: by the time
                // this arrives the foreground already names something else, so "is this the window
                // the border is on" answers no for the very window that just closed. Windows close
                // rarely -- there is no storm here to guard against, unlike a bounds change.
                //
                // Called directly rather than marshalled, exactly as followFocusedWindow is: this
                // arrives on the hook's thread, and queuing it would put the tick's own latency
                // back in front of the fix.
                UpdateFocusBorder();
            };

            workspace.WindowRemoved += forgetGestureOnRemoval;
        }

        var watchTick = 0;

        // The tick drives the same tiling and focus work a CHORD does -- workspace polling, the
        // arrange pass, the arrival handover, a WPF redraw -- and a chord's throw is caught, one
        // action at a time, by ActionDispatcher. This one was not. It runs on a DispatcherTimer, so
        // anything it raised was an unhandled exception on the WPF UI thread: the whole process,
        // not one dropped chord. The asymmetry is the argument -- the two entry points reach the
        // same executor and the same TreeManager, and only one of them was allowed to fail.
        //
        // Recorded rather than swallowed, for the reason written on FileDesktopTrace: this
        // repository has twice paid for failures that left no trace. A tick that quietly does
        // nothing is indistinguishable, from outside, from one that worked.
        var reconcile = scheduleReconcile(WatchInterval, () =>
        {
            try
            {
                ReconcileOnce();
            }
            catch (Exception error)
            {
                desktopTrace?.Record($"tick-failed {error.GetType().Name}: {error.Message}");
            }
        });

        void ReconcileOnce()
        {
            if (++watchTick >= PollEveryNthWatch)
            {
                watchTick = 0;
                workspace.Poll();
            }

            // Publishes off the hook thread, so the hook itself never waits on a file.
            if (hook.LastUnmatchedChord is { } unmatched && unmatched != lastReportedUnmatched)
            {
                lastReportedUnmatched = unmatched;
                desktopTrace?.Record($"unmatched chord: {unmatched}");
            }

            // Windows silently uninstalls a low-level keyboard hook whose callback overruns
            // LowLevelHooksTimeout. A ghosted hook delivers nothing, so the watchdog puts it back a
            // few seconds later and the keyboard "comes good on its own" -- which is what a dead
            // stretch of chords looks like from the user's side, and it used to leave no record at
            // all. Without this line a stretch caused by a ghosted hook and one caused by a lost
            // modifier read identically in the trace.
            //
            // `foundGone` is the half that decides whether the watchdog is the cure or the disease.
            // Its trigger is silence, not death, and a keyboard is silent nearly all the time -- so
            // a total that climbs while this stays at zero says every one of those reinstalls tore
            // down a hook Windows was still holding, and opened a gap for nothing.
            var reinstalls = hook.WatchdogReinstalls;
            if (reinstalls != lastReportedReinstalls)
            {
                desktopTrace?.Record(
                    $"hook reinstalled by watchdog -- total={reinstalls} " +
                    $"(+{reinstalls - lastReportedReinstalls} since last report) " +
                    $"foundGone={hook.WatchdogFoundHookGone}");
                lastReportedReinstalls = reinstalls;
            }

            if (virtualDesktops is not null)
            {
                // Windows moves windows on its own -- closing a desktop hands its windows to
                // another, and Task View can drag one across. Neither raises anything we listen to,
                // so the only way to notice is to ask.
                sessionAdapter.ReconcileDesktops();

                var nowOn = virtualDesktops.CurrentDesktopId;
                if (nowOn != lastDesktop)
                {
                    lastDesktop = nowOn;
                    lastDesktopIndex = virtualDesktops.CurrentIndex;
                    ApplyArrivingLayout();

                    // The switch chord answers itself; this is the same handover for the switches
                    // CosmicWin did not make -- Win+Ctrl+arrow, Task View -- which raise nothing we
                    // subscribe to. AFTER the layout, so the tree it searches is the arriving one.
                    executor.HandFocusToArrivingDesktop();
                }
            }

            // Keeps the executor's focus record within one interval of the real foreground. Without
            // it the record only advances on a chord, so a user who clicks between windows with the
            // mouse and then opens a new one would have it split whichever tile they last used a
            // hotkey on (LE-4 placement). One native read, so it belongs on the cheap tick.
            executor.ResolveFocusedLeaf();

            // Files the foreground under the desktop in view, every pass. The switch CHORD records
            // this itself, precisely and race-free; this is for the switches CosmicWin does not
            // make -- Win+Ctrl+arrow, Task View -- where by the time the block above notices, the
            // current desktop id already names the ARRIVING desktop and it is far too late to
            // record where the user was.
            //
            // AFTER the handover above on purpose. On a tick that detects an arrival the foreground
            // has just been handed on, so what gets filed is the arriving desktop's own window --
            // which is true. Noted BEFORE it, the departing desktop's foreground would be filed
            // under the arriving desktop's key: the record would be not merely stale but wrong.
            executor.NoteFocusOnCurrentDesktop();

            UpdateFocusBorder();
        }

        // A separate path on purpose, sharing only the pause flag. Modal dialogs never reach the
        // workspace above -- its trackability gate drops every owned window, which is what keeps
        // tooltips and context menus out of the tiling engine -- so seeing them at all takes a
        // second, narrower event source that touches none of it.
        FloatingDialogAdapter? dialogAdapter = null;
        if (windowShown is not null)
        {
            dialogAdapter = new FloatingDialogAdapter(
                windowShown, treeManager, () => exceptionStore.Current, LayoutIsFrozen)
            {
                // Off unless asked for: one line per owned window shown anywhere on the desktop is
                // diagnostic volume, not something to write during ordinary use.
                //
                // A marker FILE, not an environment variable. CosmicWin always runs elevated, and an
                // elevated process launched through UAC gets a fresh environment block rather than
                // its launcher's -- so a variable set beside the launch would silently never arrive,
                // and the diagnostic would look like it had proven the path dead.
                Trace = File.Exists(TraceMarkerPath) ? desktopTrace : null,
            };
            // The other half of the executor's untracked-foreground rule: a move chord aimed at a
            // window the tree does not contain is offered here instead of being dropped.
            executor.MoveFloatingWindow = dialogAdapter.TrySnap;
            windowShown.Open();
        }

        return new AppComposition(
            dispatcher, hook, workspace, sessionAdapter, tray, reconcile, windowShown, dialogAdapter,
            focusBorder,
            unfollowFocusedWindow: () =>
            {
                if (followFocusedWindow is not null)
                {
                    workspace.WindowBoundsChanged -= followFocusedWindow;
                }

                if (followDraggedWindow is not null)
                {
                    workspace.WindowBoundsChanging -= followDraggedWindow;
                }

                if (forgetGestureOnRemoval is not null)
                {
                    workspace.WindowRemoved -= forgetGestureOnRemoval;
                }
            });
    }

    /// <summary>The sole production caller of <see cref="Wire"/>: supplies the real Win32 collaborators, including a real <see cref="Win32DisplayManager"/>-backed <see cref="TreeManager"/>. Called exactly once, from <c>App.OnStartup</c>.</summary>
    public static AppComposition WireProduction(Action shutdown)
    {
        var registry = new WindowRegistry();
        var foreground = new Win32ForegroundWindowSource();
        var displayManager = new Win32DisplayManager();
        var treeManager = new TreeManager(displayManager.Displays, displayManager.Primary, registry);
        var exceptionStore = new ExceptionListStore(ExceptionListFile.Load());
        var workspace = new Win32Workspace();

        // Spacing is a production choice, not a property of the tiling arithmetic -- the engine and
        // every geometry fact in the suite work in exact, gapless rectangles. Opting in here keeps
        // the knob in one visible place instead of baked into TreeArranger's default.
        TreeArranger.Gap = TreeArranger.DefaultGap;

        var desktops = new Win32VirtualDesktopService();

        // Read ONCE here rather than inside Wire, so every test drives the same composition with the
        // value stated explicitly instead of whatever this machine's file happens to say.
        var settings = SettingsFile.Load();

        // The file carries MORE THAN ONE setting now, so each save has to start from the whole
        // record. Rebuilding it from the one value that changed -- which is what
        // `new Settings(FocusBorder: enabled)` did -- would write the colour back to the accent
        // every time somebody toggled the border, and vice versa.
        var stored = settings;

        // ONE hoisted instance for the life of the process, read once per untiled focus chord --
        // not reconstructed per chord, which would pay Win32NativeWindowSource's own construction
        // cost on every keypress for no benefit, since it carries no per-call state to keep fresh.
        var zOrderSource = new Win32ZOrderSource();

        return Wire(
            workspace, treeManager, registry, foreground, exceptionStore,
            focusTrace: new FileFocusTrace(FileFocusTrace.ResolveDefaultPath()),
            disableTaskTrigger: DisableScheduledTaskTrigger,
            scheduleReconcile: ScheduleOnUiThread,
            hookFactory: writer => new LowLevelKeyboardHook(writer),
            loadExceptions: ExceptionListFile.Load,
            shutdown: shutdown,
            buildTray: controller => new TrayIconHost(controller),
            // Gated internally: an unrecognised Windows build reports unsupported and the desktop
            // chords become inert, rather than calling through a vtable that may have moved.
            virtualDesktops: desktops,
            desktopTrace: new FileDesktopTrace(FileDesktopTrace.ResolveDefaultPath()),
            resolveWindowDesktop: desktops.ResolveWindowDesktop,
            windowShown: new Win32WindowShownWatcher(),
            focusBorder: new FocusBorderOverlay(),
            scheduleOnOwningThread: RunOnUiThread,
            focusBorderEnabled: settings.FocusBorder,
            persistFocusBorder: enabled => SettingsFile.Save(stored = stored with { FocusBorder = enabled }),
            focusBorderColor: settings.BorderColor,
            persistBorderColor: rgb => SettingsFile.Save(stored = stored with { BorderColor = rgb }),
            tilingEnabled: settings.Tiling,
            persistTiling: enabled => SettingsFile.Save(stored = stored with { Tiling = enabled }),
            zOrder: zOrderSource.EnumerateTopLevelWindows);
    }

    /// <summary>
    /// Marshals work onto the WPF UI thread, which owns the overlay window and the WinEvent hook.
    /// </summary>
    /// <remarks>
    /// A chord is answered on <see cref="ActionDispatcher.RunAsync"/>'s pool thread, and a WPF
    /// window may only be touched by the thread that created it. Without this, the very refresh that
    /// makes the border keep up would throw from another thread instead.
    /// </remarks>
    private static void RunOnUiThread(Action work) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(work);

    /// <summary>
    /// Runs the reconciliation pass on the WPF UI thread -- the SAME thread that installs the
    /// WinEvent hook and therefore receives its callbacks. Both mutate the same trees and registry,
    /// so a pool-thread timer would race them; a <see cref="DispatcherTimer"/> serialises the two by
    /// construction.
    /// </summary>
    private static IDisposable ScheduleOnUiThread(TimeSpan interval, Action callback)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) => callback();
        timer.Start();
        return new TimerStopper(timer);
    }

    private sealed class TimerStopper(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }

    private const string TaskName = "CosmicWin";

    /// <summary>Create this file to make the window paths trace themselves; delete it to stop.</summary>
    internal static string TraceMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CosmicWin",
        "trace-dialogs");

    /// <summary>
    /// The ONE delegating call <c>App.OnStartup</c> makes for
    /// <c>--install-task</c>/<c>--uninstall-task</c>, called BEFORE <see cref="WireProduction"/>.
    /// <paramref name="runner"/> defaults to the real runner, overridable only for tests.
    /// </summary>
    public static bool TryHandleTaskCommand(IReadOnlyList<string> args, IProcessRunner? runner = null)
    {
        if (args.Count == 0)
        {
            return false;
        }

        var installer = CreateInstaller(runner);

        switch (args[0])
        {
            case "--install-task":
                installer.Install();
                return true;
            case "--uninstall-task":
                installer.Uninstall();
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The one place the real <see cref="TaskInstaller"/> is constructed, shared by <see
    /// cref="TryHandleTaskCommand"/> and <see cref="DisableScheduledTaskTrigger"/> so the task name,
    /// executable path and XML location cannot drift apart between the two.
    /// </summary>
    private static TaskInstaller CreateInstaller(IProcessRunner? runner = null) =>
        new(
            TaskName,
            Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve the current process path."),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CosmicWin", "CosmicWinTask.xml"),
            runner ?? new Win32ProcessRunner());

    /// <summary>
    /// TC-3-W1's production trigger-disable. A failure here is DELIBERATELY swallowed: the user asked
    /// to quit, and Salir's own contract -- remove the hook and exit -- must not be held hostage by
    /// schtasks. <see cref="TaskInstaller.Disable"/> already treats an absent task as success, so
    /// what reaches this catch is a genuine refusal (no elevation, service stopped), not routine.
    /// </summary>
    private static void DisableScheduledTaskTrigger()
    {
        try
        {
            CreateInstaller().Disable();
        }
        catch (InvalidOperationException)
        {
            // Declared residual: quitting still works, but the trigger may fire again next logon.
        }
    }

    /// <summary>Mirrors <c>App.OnExit</c>'s exact disposal order, after stopping WT-1's reconciliation pass: tray, hook, adapter, workspace, dispatcher.</summary>
    public void Dispose()
    {
        // Stopped FIRST: a pass that fires mid-teardown would reconcile against a disposed workspace.
        _reconcile.Dispose();
        _tray.Dispose();
        _hook.Dispose();

        // The adapter first, then the source it listens to: unsubscribing before the hook is torn
        // down means no event can arrive against a half-disposed adapter.
        // Unsubscribed before the workspace it listens to is torn down, so no late event can arrive
        // against a disposed overlay.
        _unfollowFocusedWindow();
        _dialogAdapter?.Dispose();
        _windowShown?.Dispose();
        _focusBorder?.Dispose();
        _sessionAdapter.Dispose();
        _workspace.Dispose();
        _dispatcher.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
