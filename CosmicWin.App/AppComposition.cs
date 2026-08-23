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
/// Closes verify-report #21 revision 15 SUGGESTION V15-S1: extracts <c>App.xaml.cs</c>'s
/// composition into a plain, directly-testable class. Four consecutive closures (V11-W1, V12-W1,
/// V13-W1, V14-W1) defended the composition site from OUTSIDE a WPF <see
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

    private AppComposition(
        ActionDispatcher dispatcher, LowLevelKeyboardHook hook, IWorkspace workspace,
        MultiMonitorWorkspaceAdapter sessionAdapter, IDisposable tray, IDisposable reconcile)
    {
        _dispatcher = dispatcher;
        _hook = hook;
        _workspace = workspace;
        _sessionAdapter = sessionAdapter;
        _tray = tray;
        _reconcile = reconcile;
    }

    /// <summary>
    /// WT-1's "polling fallback reconciliation pass on a bounded interval". Frequent enough that a
    /// window the hook dropped is picked up before the user notices, cheap enough to ignore: a pass
    /// only raises events for windows whose bounds or membership ACTUALLY changed, so a steady
    /// desktop costs one enumeration and nothing else.
    /// </summary>
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Wires every collaborator, in <c>App.OnStartup</c>'s exact order: <see
    /// cref="CompositionRoot.Build"/> against <paramref name="treeManager"/>'s <see
    /// cref="TreeManager.Primary"/> tree, then <paramref name="treeManager"/> and <paramref
    /// name="focusTrace"/> are ALSO assigned onto
    /// the returned executor (WU18, closes V17-W1: a hotkey mutation on a secondary monitor's
    /// focused window now arranges that SAME secondary tree, not always the primary one; <paramref
    /// name="focusTrace"/> is mandatory rather than optional so the MR-2 diagnostic cannot be
    /// silently dropped from the one composition site that has to carry it), then the hook, then <see cref="MultiMonitorWorkspaceAdapter"/> --
    /// WU17's real production caller of <paramref name="treeManager"/> (closes carried finding W3)
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
        Func<nint, Guid>? resolveWindowDesktop = null)
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
        var sessionAdapter = new MultiMonitorWorkspaceAdapter(
            workspace, treeManager, registry, () => exceptionStore.Current, () => hook.IsPaused,
            executor.ResolveFocusedLeaf)
        {
            ResolveWindowDesktop = resolveWindowDesktop,
        };
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
        // Which desktop the last reconciliation saw, so a change can be noticed. The shell offers
        // no event for this, and asking on a timer is enough: the only thing that must happen on a
        // switch is laying out the tree the user just arrived at.
        var lastDesktop = virtualDesktops?.CurrentDesktopId ?? Guid.Empty;
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
        executor.DesktopSwitched = ApplyArrivingLayout;

        var reconcile = scheduleReconcile(ReconcileInterval, () =>
        {
            workspace.Poll();

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
                    ApplyArrivingLayout();
                }
            }

            // Keeps the executor's focus record within one interval of the real foreground. Without
            // it the record only advances on a chord, so a user who clicks between windows with the
            // mouse and then opens a new one would have it split whichever tile they last used a
            // hotkey on (LE-4 placement).
            executor.ResolveFocusedLeaf();
        });

        return new AppComposition(dispatcher, hook, workspace, sessionAdapter, tray, reconcile);
    }

    /// <summary>The sole production caller of <see cref="Wire"/>: supplies the real Win32 collaborators, including a real <see cref="Win32DisplayManager"/>-backed <see cref="TreeManager"/> (WU17). Called exactly once, from <c>App.OnStartup</c>.</summary>
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
            resolveWindowDesktop: desktops.ResolveWindowDesktop);
    }

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

    /// <summary>
    /// Tasks 3.20/3.21/3.22 (ES-2/ES-4): the ONE delegating call <c>App.OnStartup</c> makes for
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
        _sessionAdapter.Dispose();
        _workspace.Dispose();
        _dispatcher.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
