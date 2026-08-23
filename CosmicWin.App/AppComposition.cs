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
/// composition into a plain, directly-testable class. Four consecutive closures (,
/// ) defended the composition site from OUTSIDE a WPF <see
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
        Action<Action>? scheduleOnOwningThread = null)
    {
        var primary = treeManager.Primary;
        treeManager.TryGetTree(primary, out var primaryTree);
        var workArea = WorkAreaResolver.Resolve(primary);

        var (dispatcher, executor) = CompositionRoot.Build(primaryTree!, registry, foreground, workArea);
        executor.TreeManager = treeManager;
        executor.FocusTrace = focusTrace;
        executor.VirtualDesktops = virtualDesktops;
        executor.DesktopTrace = desktopTrace;

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

        var sessionAdapter = new MultiMonitorWorkspaceAdapter(
            workspace, treeManager, registry, () => exceptionStore.Current, () => hook.IsPaused,
            executor.ResolveFocusedLeaf)
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
        workspace.Open();
        hook.Start();

        // TC-3-W1: Salir stops the logon trigger BEFORE tearing the process down -- after shutdown
        // there is no guarantee anything still runs. Disable, not uninstall: TC-3 says "disable the
        // Scheduled Task trigger" where ES-4 says "remove", so quitting once must not throw the
        // user's installation away.
        var trayController = CompositionRoot.BuildTrayMenuController(
            hook, exceptionStore, loadExceptions,
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

        // The arriving desktop's own layout, applied to the work area in force NOW. Its windows
        // were left exactly where they were when the user walked away, so without this they would
        // still be wearing the previous desktop's geometry.
        void ApplyArrivingLayout()
        {
            if (treeManager.TryGetTree(treeManager.Primary, out var arriving) && arriving is not null)
            {
                TreeArranger.ArrangeAndPosition(
                    arriving, registry, WorkAreaResolver.Resolve(treeManager.Primary));
            }
        }

        // Applied on the chord itself, not left to the timer. The timer remains the safety net for
        // a switch CosmicWin did not make -- Win+Ctrl+arrow, or Task View -- but waiting for it
        // after our own chord showed the user a loose window for up to a full interval.
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
        void UpdateFocusBorder()
        {
            if (focusBorder is null)
            {
                return;
            }

            var focusedLeaf = executor.ResolveFocusedLeaf();
            if (hook.IsPaused
                || focusedLeaf is null
                || !registry.TryGetWindow(focusedLeaf.Window.Handle, out var focusedWindow)
                || focusedWindow is not { IsAlive: true })
            {
                focusBorder.Hide();
                return;
            }

            var onDisplay = treeManager.ResolveDisplay(focusedWindow.Bounds);
            focusBorder.ShowAround(focusedWindow.Bounds, onDisplay.Scaling, BorderGeometry.DefaultThickness);
        }

        // The dispatcher runs chords on a pool thread; the overlay is a WPF window and must be
        // touched on the thread that owns it, which is the same one this timer already runs on.
        // Unset -- as every test does -- it runs inline, which is what a test on one thread wants.
        var onOwningThread = scheduleOnOwningThread ?? (work => work());
        executor.AfterAction = () => onOwningThread(UpdateFocusBorder);

        // Following the window itself, not just the chord that moved it. An application may take
        // several frames to settle where it was put -- Windows Terminal animates its resize -- so a
        // single refresh after the chord lands before the window has finished arriving, and the
        // border visibly trails it. This arrives once per frame of that movement, on the hook's own
        // thread, and costs one placement each.
        EventHandler<WindowEventArgs>? followFocusedWindow = null;
        if (focusBorder is not null)
        {
            followFocusedWindow = (_, e) =>
            {
                // Only the window being framed. During a reflow every tile reports a move, and
                // redrawing the border for windows it is not on is work with nothing to show for it.
                if (executor.ResolveFocusedLeaf() is { } leaf && leaf.Window.Handle == e.Window.Handle)
                {
                    UpdateFocusBorder();
                }
            };

            workspace.WindowBoundsChanged += followFocusedWindow;
        }

        var watchTick = 0;
        var reconcile = scheduleReconcile(WatchInterval, () =>
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
                }
            }

            // Keeps the executor's focus record within one interval of the real foreground. Without
            // it the record only advances on a chord, so a user who clicks between windows with the
            // mouse and then opens a new one would have it split whichever tile they last used a
            // hotkey on (LE-4 placement). One native read, so it belongs on the cheap tick.
            executor.ResolveFocusedLeaf();
            UpdateFocusBorder();
        });

        // A separate path on purpose, sharing only the pause flag. Modal dialogs never reach the
        // workspace above -- its trackability gate drops every owned window, which is what keeps
        // tooltips and context menus out of the tiling engine -- so seeing them at all takes a
        // second, narrower event source that touches none of it.
        FloatingDialogAdapter? dialogAdapter = null;
        if (windowShown is not null)
        {
            dialogAdapter = new FloatingDialogAdapter(
                windowShown, treeManager, () => exceptionStore.Current, () => hook.IsPaused)
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
            scheduleOnOwningThread: RunOnUiThread);
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
