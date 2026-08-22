using System.IO;
using System.Threading.Channels;
using CosmicWin.App.Input;
using CosmicWin.App.Startup;
using CosmicWin.App.Tray;
using CosmicWin.Interop;
using CosmicWin.Interop.Win32;
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

    private AppComposition(
        ActionDispatcher dispatcher, LowLevelKeyboardHook hook, IWorkspace workspace,
        MultiMonitorWorkspaceAdapter sessionAdapter, IDisposable tray)
    {
        _dispatcher = dispatcher;
        _hook = hook;
        _workspace = workspace;
        _sessionAdapter = sessionAdapter;
        _tray = tray;
    }

    /// <summary>
    /// Wires every collaborator, in <c>App.OnStartup</c>'s exact order: <see
    /// cref="CompositionRoot.Build"/> against <paramref name="treeManager"/>'s <see
    /// cref="TreeManager.Primary"/> tree, then <paramref name="treeManager"/> is ALSO assigned onto
    /// the returned <see cref="ActionExecutor.TreeManager"/> (WU18, closes V17-W1: a hotkey
    /// mutation on a secondary monitor's focused window now arranges that SAME secondary tree, not
    /// always the primary one), then the hook, then <see cref="MultiMonitorWorkspaceAdapter"/> --
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
        Func<ChannelWriter<HotkeyAction>, LowLevelKeyboardHook> hookFactory,
        Func<ExceptionList> loadExceptions,
        Action shutdown,
        Func<TrayMenuController, IDisposable> buildTray)
    {
        var primary = treeManager.Primary;
        treeManager.TryGetTree(primary, out var primaryTree);
        var workArea = WorkAreaResolver.Resolve(primary);

        var (dispatcher, executor) = CompositionRoot.Build(primaryTree!, registry, foreground, workArea);
        executor.TreeManager = treeManager;
        var hook = hookFactory(dispatcher.Writer);
        var sessionAdapter = new MultiMonitorWorkspaceAdapter(
            workspace, treeManager, registry, () => exceptionStore.Current, () => hook.IsPaused);
        workspace.Open();
        hook.Start();

        var trayController = CompositionRoot.BuildTrayMenuController(hook, exceptionStore, loadExceptions, shutdown);
        var tray = buildTray(trayController);

        _ = dispatcher.RunAsync(CancellationToken.None);

        return new AppComposition(dispatcher, hook, workspace, sessionAdapter, tray);
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

        return Wire(
            workspace, treeManager, registry, foreground, exceptionStore,
            hookFactory: writer => new LowLevelKeyboardHook(writer),
            loadExceptions: ExceptionListFile.Load,
            shutdown: shutdown,
            buildTray: controller => new TrayIconHost(controller));
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

        var installer = new TaskInstaller(
            TaskName,
            Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve the current process path."),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CosmicWin", "CosmicWinTask.xml"),
            runner ?? new Win32ProcessRunner());

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

    /// <summary>Mirrors <c>App.OnExit</c>'s exact disposal order: tray, hook, adapter, workspace, dispatcher.</summary>
    public void Dispose()
    {
        _tray.Dispose();
        _hook.Dispose();
        _sessionAdapter.Dispose();
        _workspace.Dispose();
        _dispatcher.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
