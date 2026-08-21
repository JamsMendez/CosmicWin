using CosmicWin.App.Input;
using CosmicWin.App.Tray;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App;

/// <summary>
/// Task 2.18: wires an injected <see cref="ITilingEngine"/>/<see cref="WindowRegistry"/>/<see
/// cref="IForegroundWindowSource"/> into a connected <see cref="ActionDispatcher"/> -&gt; <see
/// cref="ActionExecutor"/> pipeline. Closes verify-report #21 finding C1 / SUGGESTION 1: before
/// this factory existed, only tests ever constructed a wired dispatcher/executor pair -- <see
/// cref="CosmicWin.App.App"/> is the sole production caller.
/// </summary>
/// <remarks>
/// Owns no lifetime beyond wiring: disposing the returned <see cref="ActionDispatcher"/> (and any
/// disposable dependency passed in) remains the caller's responsibility.
/// </remarks>
public static class CompositionRoot
{
    /// <summary>
    /// <paramref name="workArea"/> is assigned directly onto the returned <see
    /// cref="ActionExecutor.WorkArea"/> (verify-report #21 CRITICAL C1: previously never assigned
    /// on any production path, so the default <c>Rect(0,0,0,0)</c> zeroed every window on the
    /// first Move/Toggle/Resize chord). Callers resolve it once, e.g. via <see
    /// cref="WorkAreaResolver.Resolve"/>, and MUST reuse the returned executor's
    /// <see cref="ActionExecutor.WorkArea"/> as the single source of truth for any other component
    /// (e.g. <see cref="WorkspaceSessionAdapter"/>) that also needs to arrange the tree.
    /// </summary>
    public static (ActionDispatcher Dispatcher, ActionExecutor Executor) Build(
        ITilingEngine engine, WindowRegistry registry, IForegroundWindowSource foreground, Rect workArea)
    {
        var executor = new ActionExecutor(engine, registry, foreground) { WorkArea = workArea };
        var dispatcher = new ActionDispatcher(executor);
        return (dispatcher, executor);
    }

    /// <summary>
    /// Task V10-W2: wires a <see cref="WorkspaceSessionAdapter"/> against the SAME executor-held
    /// work area <see cref="Build"/> assigned (single source of truth, unchanged from 2.27) and the
    /// SAME <paramref name="exceptions"/> store <see cref="App.OnStartup"/> loaded from disk (WE-2)
    /// -- extracting this joint out of the untestable WPF <see cref="App"/> class, matching the
    /// pattern <see cref="Build"/> already established for <paramref name="executor"/>'s work area.
    /// </summary>
    /// <remarks>
    /// V12-W1: <paramref name="isPaused"/> is a MANDATORY parameter -- it used to default to
    /// never-paused, which meant a future edit could swap a <see cref="BuildPauseGatedSession"/>
    /// call site for this method and simply omit the last argument, compiling cleanly and silently
    /// restoring hotkeys-only pause (verify-report #21 mutation MR4). There is no longer a
    /// never-paused default to fall back on: every caller, including every test, must state its
    /// gate explicitly (production callers always via <see cref="BuildPauseGatedSession"/>).
    /// </remarks>
    public static WorkspaceSessionAdapter BuildSessionAdapter(
        IWorkspace workspace, LayoutTree tree, WindowRegistry registry, ActionExecutor executor,
        ExceptionListStore exceptions, Func<bool> isPaused) =>
        new(workspace, tree, registry, () => executor.WorkArea, () => exceptions.Current, isPaused);

    /// <summary>
    /// V11-W1: wires <see cref="WorkspaceSessionAdapter"/>'s pause gate directly onto <paramref
    /// name="hook"/>.<see cref="LowLevelKeyboardHook.IsPaused"/> -- the SAME hook instance the tray
    /// controller's <c>TogglePause</c> writes and the keyboard processor's <c>Process</c> reads
    /// (settled full-pause semantics, decision #62). Closes verify-report #21 V11-W1: previously
    /// <see cref="BuildSessionAdapter"/>'s <c>isPaused</c> parameter was optional and typed as a
    /// lambda re-written at the untestable <see cref="App"/> call site, so an edit dropping the
    /// argument compiled cleanly and left the whole suite green. <paramref name="hook"/> is a
    /// mandatory parameter here, so the call site has no isPaused argument left to silently drop --
    /// and per V12-W1, <see cref="BuildSessionAdapter"/>'s own <c>isPaused</c> parameter is now also
    /// mandatory, so swapping this factory for that one at the <see cref="App"/> call site no longer
    /// compiles either.
    /// </summary>
    public static WorkspaceSessionAdapter BuildPauseGatedSession(
        IWorkspace workspace, LayoutTree tree, WindowRegistry registry, ActionExecutor executor,
        ExceptionListStore exceptions, LowLevelKeyboardHook hook) =>
        BuildSessionAdapter(workspace, tree, registry, executor, exceptions, () => hook.IsPaused);

    /// <summary>
    /// Tasks 3.16/3.17/3.18/3.36/3.37 (WU11): wires a <see cref="TrayMenuController"/> against the
    /// SAME <paramref name="hook"/> instance that gates hotkey processing AND (via <see
    /// cref="BuildSessionAdapter"/>'s <c>isPaused</c> parameter) new-window auto-tiling -- settled
    /// full-pause semantics (Engram decision #62). <paramref name="loadExceptions"/> mirrors
    /// <c>workArea</c>/<c>exceptions</c>'s injected-delegate idiom (production wires <see
    /// cref="ExceptionListFile.Load()"/>) so Reload is testable against an isolated file instead of
    /// the real on-disk exception list -- closes verify-report #21 V10-W4. <paramref name="exit"/>
    /// is Salir's trigger; the caller supplies it (WPF's <c>Application.Shutdown</c>) so this class
    /// stays WPF-free otherwise.
    /// </summary>
    public static TrayMenuController BuildTrayMenuController(
        LowLevelKeyboardHook hook, ExceptionListStore exceptions, Func<ExceptionList> loadExceptions, Action exit) =>
        new(
            () => hook.IsPaused,
            paused => hook.IsPaused = paused,
            () => exceptions.Reload(loadExceptions()),
            exit);
}
