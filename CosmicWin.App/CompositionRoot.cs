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
    public static WorkspaceSessionAdapter BuildSessionAdapter(
        IWorkspace workspace, LayoutTree tree, WindowRegistry registry, ActionExecutor executor,
        ExceptionListStore exceptions, Func<bool>? isPaused = null) =>
        new(workspace, tree, registry, () => executor.WorkArea, () => exceptions.Current, isPaused);

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
