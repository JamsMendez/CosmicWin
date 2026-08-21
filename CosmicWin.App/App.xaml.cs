using System.Windows;
using CosmicWin.App.Input;
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
/// as its single source of truth — CRITICAL C2), starts the global keyboard hook, and runs the
/// dispatcher loop as a background task on the WPF Dispatcher thread (design D5: UI-thread-owned
/// tree mutation). No <c>MainWindow</c> -- this app is a background tiling manager, not a
/// window-driven UI.
/// </summary>
public partial class App : Application
{
    private ActionDispatcher? _dispatcher;
    private LowLevelKeyboardHook? _hook;
    private Win32Workspace? _workspace;
    private WorkspaceSessionAdapter? _sessionAdapter;
    private ExceptionListStore? _exceptionStore;

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
        // reference is kept so a later reload trigger (the tray "Reload" item, tasks 3.15-3.16)
        // has something to call Reload() on -- WE-3's mechanism is real and wired here; only the
        // live re-invocation trigger remains open (documented WU10 scope boundary).
        _exceptionStore = new ExceptionListStore(ExceptionListFile.Load());

        _workspace = new Win32Workspace();
        _sessionAdapter = CompositionRoot.BuildSessionAdapter(_workspace, tree, registry, executor, _exceptionStore);
        _workspace.Open();

        _hook = new LowLevelKeyboardHook(dispatcher.Writer);
        _hook.Start();

        _ = dispatcher.RunAsync(CancellationToken.None);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();
        _sessionAdapter?.Dispose();
        _workspace?.Dispose();
        _dispatcher?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
