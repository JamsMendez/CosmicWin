using CosmicWin.App.Input;
using CosmicWin.App.Tray;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App;

/// <summary>
/// Task 2.18: wires an injected <see cref="ITilingEngine"/>/<see cref="WindowRegistry"/>/<see
/// cref="IForegroundWindowSource"/> into a connected <see cref="ActionDispatcher"/> -&gt; <see
/// cref="ActionExecutor"/> pipeline. Before
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
    /// cref="ActionExecutor.WorkArea"/> (: previously never assigned
    /// on any production path, so the default <c>Rect(0,0,0,0)</c> zeroed every window on the
    /// first Move/Toggle/Resize chord). Callers resolve it once, e.g. via <see
    /// cref="WorkAreaResolver.Resolve"/>, and MUST reuse the returned executor's
    /// <see cref="ActionExecutor.WorkArea"/> as the single source of truth for any other component
    /// (e.g. <see cref="WorkspaceSessionAdapter"/>) that also needs to arrange the tree.
    /// </summary>
    /// <param name="onActionFailed">
    /// Where a chord that THREW is reported. Optional, and the pump survives either way -- but a
    /// composition that leaves it null gets a window manager that drops a chord in total silence,
    /// which is the failure this repository has paid to diagnose more than once. Passed at
    /// construction rather than assigned afterwards so a dispatcher cannot exist, however briefly,
    /// in a state where a failure would go unreported.
    /// </param>
    public static (ActionDispatcher Dispatcher, ActionExecutor Executor) Build(
        ITilingEngine engine, WindowRegistry registry, IForegroundWindowSource foreground, Rect workArea,
        Action<HotkeyAction, Exception>? onActionFailed = null)
    {
        var executor = new ActionExecutor(engine, registry, foreground) { WorkArea = workArea };
        var dispatcher = new ActionDispatcher(executor) { OnActionFailed = onActionFailed };
        return (dispatcher, executor);
    }

    /// <summary>
    /// Task: wires a <see cref="WorkspaceSessionAdapter"/> against the SAME executor-held
    /// work area <see cref="Build"/> assigned (single source of truth, unchanged from 2.27) and the
    /// SAME <paramref name="exceptions"/> store <see cref="App.OnStartup"/> loaded from disk (WE-2)
    /// -- extracting this joint out of the untestable WPF <see cref="App"/> class, matching the
    /// pattern <see cref="Build"/> already established for <paramref name="executor"/>'s work area.
    /// </summary>
    /// <remarks>
    /// <paramref name="isPaused"/> is a MANDATORY parameter -- it used to default to
    /// never-paused, which meant a future edit could swap a <see cref="BuildPauseGatedSession"/>
    /// call site for this method and simply omit the last argument, compiling cleanly and silently
    /// restoring hotkeys-only pause. There is no longer a
    /// never-paused default to fall back on at THIS factory: every caller of
    /// <see cref="BuildSessionAdapter"/> must state its gate explicitly (production callers always
    /// via <see cref="BuildPauseGatedSession"/>). Correction: this factory being mandatory
    /// did NOT, by itself, make every caller state its gate -- the underlying <see
    /// cref="WorkspaceSessionAdapter"/> constructor this factory delegates to still defaulted
    /// <c>isPaused</c> to never-paused, so a caller that bypassed this factory entirely and
    /// constructed the adapter directly still compiled with the gate silently omitted. That
    /// terminal defaulting site is now also mandatory, so the claim above is finally true at every
    /// layer, not just this one.
    /// </remarks>
    public static WorkspaceSessionAdapter BuildSessionAdapter(
        IWorkspace workspace, LayoutTree tree, WindowRegistry registry, ActionExecutor executor,
        ExceptionListStore exceptions, Func<bool> isPaused) =>
        new(workspace, tree, registry, () => executor.WorkArea, () => exceptions.Current, isPaused);

    /// <summary>
    /// Wires <see cref="WorkspaceSessionAdapter"/>'s pause gate directly onto <paramref
    /// name="hook"/>.<see cref="LowLevelKeyboardHook.IsPaused"/> -- the SAME hook instance the tray
    /// controller's <c>TogglePause</c> writes and the keyboard processor's <c>Process</c> reads
    /// (settled full-pause semantics, an earlier decision). Closes: previously
    /// <see cref="BuildSessionAdapter"/>'s <c>isPaused</c> parameter was optional and typed as a
    /// lambda re-written at the untestable <see cref="App"/> call site, so an edit dropping the
    /// argument compiled cleanly and left the whole suite green. <paramref name="hook"/> is a
    /// mandatory parameter here, so the call site has no isPaused argument left to silently drop --
    /// and per, <see cref="BuildSessionAdapter"/>'s own <c>isPaused</c> parameter is now also
    /// mandatory, so swapping this factory for that one at the <see cref="App"/> call site no longer
    /// compiles either. Per, <see cref="WorkspaceSessionAdapter"/>'s own constructor
    /// <c>isPaused</c> parameter is now ALSO mandatory, so bypassing both factories and constructing
    /// the adapter directly at the <see cref="App"/> call site no longer compiles either -- the
    /// permissive default is gone at every layer of this chain.
    /// </summary>
    public static WorkspaceSessionAdapter BuildPauseGatedSession(
        IWorkspace workspace, LayoutTree tree, WindowRegistry registry, ActionExecutor executor,
        ExceptionListStore exceptions, LowLevelKeyboardHook hook) =>
        BuildSessionAdapter(workspace, tree, registry, executor, exceptions, () => hook.IsPaused);

    /// <summary>
    /// Wires a <see cref="TrayMenuController"/> against the
    /// SAME <paramref name="hook"/> instance that gates hotkey processing AND (via <see
    /// cref="BuildSessionAdapter"/>'s <c>isPaused</c> parameter) new-window auto-tiling -- settled
    /// full-pause semantics. <paramref name="loadExceptions"/> mirrors
    /// <c>workArea</c>/<c>exceptions</c>'s injected-delegate idiom (production wires <see
    /// cref="ExceptionListFile.Load()"/>) so Reload is testable against an isolated file instead of
    /// the real on-disk exception list -- closes. <paramref name="exit"/>
    /// is Salir's trigger; the caller supplies it (WPF's <c>Application.Shutdown</c>) so this class
    /// stays WPF-free otherwise. <paramref name="getFocusBorder"/>/<paramref name="setFocusBorder"/>
    /// are MANDATORY for the reason every gate on this factory is: defaulted, a call site that
    /// dropped them would compile cleanly and silently ignore the user's persisted choice, which is
    /// the exact failure this factory's other parameters were made mandatory to prevent.
    /// </summary>
    public static TrayMenuController BuildTrayMenuController(
        LowLevelKeyboardHook hook, ExceptionListStore exceptions, Func<ExceptionList> loadExceptions,
        Func<bool> getFocusBorder, Action<bool> setFocusBorder, Action exit,
        Func<uint?>? getBorderColor = null, Action<uint?>? setBorderColor = null) =>
        new(
            () => hook.IsPaused,
            paused => hook.IsPaused = paused,
            getFocusBorder,
            setFocusBorder,
            () => exceptions.Reload(loadExceptions()),
            exit,
            getBorderColor,
            setBorderColor);
}
