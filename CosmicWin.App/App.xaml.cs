using System.Windows;
using CosmicWin.App.Input;
using CosmicWin.App.Tray;
using CosmicWin.Interop.Win32;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App;

/// <summary>
/// Task 2.18/2.20 (WU7D-corrected): production entry point. Builds the real dependencies (<see
/// cref="LayoutTree"/>, <see cref="WindowRegistry"/>, <see cref="Win32ForegroundWindowSource"/>),
/// resolves the primary display's real work area via <see cref="Win32DisplayManager"/>/<see
/// cref="WorkAreaResolver"/> (verify-report #21 CRITICAL C1), wires them through <see
/// cref="CompositionRoot"/>, connects a <see cref="Win32Workspace"/> to the shared tree/registry
/// via <see cref="WorkspaceSessionAdapter"/> (task 2.20, reading the SAME executor-held work area
/// as its single source of truth — CRITICAL C2), starts the global keyboard hook, constructs the
/// tray (tasks 3.16-3.18/3.37, WU11), and runs the dispatcher loop as a background task on the WPF
/// Dispatcher thread (design D5: UI-thread-owned tree mutation). No <c>MainWindow</c> -- this app
/// is a background tiling manager, not a window-driven UI.
/// </summary>
public partial class App : Application
{
    private ActionDispatcher? _dispatcher;
    private LowLevelKeyboardHook? _hook;
    private Win32Workspace? _workspace;
    private WorkspaceSessionAdapter? _sessionAdapter;
    private ExceptionListStore? _exceptionStore;
    private TrayIconHost? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var tree = new LayoutTree();
        var registry = new WindowRegistry();
        var foreground = new Win32ForegroundWindowSource();
        var workArea = WorkAreaResolver.Resolve(new Win32DisplayManager().Primary);

        var (dispatcher, executor) = CompositionRoot.Build(tree, registry, foreground, workArea);
        _dispatcher = dispatcher;

        // Task 3.34: loads the manual exception list from disk at startup (WE-2). The stored
        // reference is kept so the tray "Reload" item (tasks 3.36/3.37) has something to call
        // Reload() on.
        _exceptionStore = new ExceptionListStore(ExceptionListFile.Load());

        _hook = new LowLevelKeyboardHook(dispatcher.Writer);

        // Task 3.15/3.16 (WU11), settled full-pause semantics: gates new-window auto-tiling the
        // same way the hotkey path is gated, via the SAME hook instance the tray toggles.
        _workspace = new Win32Workspace();
        _sessionAdapter = CompositionRoot.BuildSessionAdapter(
            _workspace, tree, registry, executor, _exceptionStore, () => _hook?.IsPaused ?? false);
        _workspace.Open();

        _hook.Start();

        // Task 3.16/3.17/3.18/3.36/3.37 (WU11): Reload/Exit run on this same UI/Dispatcher thread
        // that already owns tree mutation (design D5) -- no synchronization needed for either.
        // Pausar's flag is the sole cross-thread hazard, covered by LowLevelKeyboardHook.IsPaused's
        // volatile-backed pass-through (written here, read on the hook's dedicated STA thread and
        // on this same UI thread's OnWindowAdded).
        var trayController = CompositionRoot.BuildTrayMenuController(
            _hook, _exceptionStore, ExceptionListFile.Load, Shutdown);
        _tray = new TrayIconHost(trayController);

        _ = dispatcher.RunAsync(CancellationToken.None);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _hook?.Dispose();
        _sessionAdapter?.Dispose();
        _workspace?.Dispose();
        _dispatcher?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
