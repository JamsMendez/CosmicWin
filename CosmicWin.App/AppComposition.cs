using System.Threading.Channels;
using CosmicWin.App.Input;
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
    private readonly WorkspaceSessionAdapter _sessionAdapter;
    private readonly IDisposable _tray;

    private AppComposition(
        ActionDispatcher dispatcher, LowLevelKeyboardHook hook, IWorkspace workspace,
        WorkspaceSessionAdapter sessionAdapter, IDisposable tray)
    {
        _dispatcher = dispatcher;
        _hook = hook;
        _workspace = workspace;
        _sessionAdapter = sessionAdapter;
        _tray = tray;
    }

    /// <summary>
    /// Wires every collaborator of the production entry point, in the exact order <c>App.OnStartup</c>
    /// used to: <see cref="CompositionRoot.Build"/>, then the hook (needs the dispatcher's writer,
    /// hence the factory rather than an already-built instance), then <see
    /// cref="CompositionRoot.BuildPauseGatedSession"/> with that SAME hook, then <paramref
    /// name="workspace"/>.Open(), then <paramref name="hook"/>.Start(), then the tray controller and
    /// its host, then the dispatcher loop. Every collaborator is caller-supplied so this method has
    /// no dependency on a live Win32 desktop and can be driven end to end by a plain unit test.
    /// </summary>
    public static AppComposition Wire(
        IWorkspace workspace,
        LayoutTree tree,
        WindowRegistry registry,
        IForegroundWindowSource foreground,
        Rect workArea,
        ExceptionListStore exceptionStore,
        Func<ChannelWriter<HotkeyAction>, LowLevelKeyboardHook> hookFactory,
        Func<ExceptionList> loadExceptions,
        Action shutdown,
        Func<TrayMenuController, IDisposable> buildTray)
    {
        var (dispatcher, executor) = CompositionRoot.Build(tree, registry, foreground, workArea);
        var hook = hookFactory(dispatcher.Writer);
        var sessionAdapter = CompositionRoot.BuildPauseGatedSession(
            workspace, tree, registry, executor, exceptionStore, hook);
        workspace.Open();
        hook.Start();

        var trayController = CompositionRoot.BuildTrayMenuController(hook, exceptionStore, loadExceptions, shutdown);
        var tray = buildTray(trayController);

        _ = dispatcher.RunAsync(CancellationToken.None);

        return new AppComposition(dispatcher, hook, workspace, sessionAdapter, tray);
    }

    /// <summary>The sole production caller of <see cref="Wire"/>: supplies the real Win32 collaborators. Called exactly once, from <c>App.OnStartup</c>.</summary>
    public static AppComposition WireProduction(Action shutdown)
    {
        var tree = new LayoutTree();
        var registry = new WindowRegistry();
        var foreground = new Win32ForegroundWindowSource();
        var workArea = WorkAreaResolver.Resolve(new Win32DisplayManager().Primary);
        var exceptionStore = new ExceptionListStore(ExceptionListFile.Load());
        var workspace = new Win32Workspace();

        return Wire(
            workspace, tree, registry, foreground, workArea, exceptionStore,
            hookFactory: writer => new LowLevelKeyboardHook(writer),
            loadExceptions: ExceptionListFile.Load,
            shutdown: shutdown,
            buildTray: controller => new TrayIconHost(controller));
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
