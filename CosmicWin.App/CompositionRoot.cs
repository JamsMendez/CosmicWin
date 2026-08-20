using CosmicWin.App.Input;
using CosmicWin.Layout;

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
    public static (ActionDispatcher Dispatcher, ActionExecutor Executor) Build(
        ITilingEngine engine, WindowRegistry registry, IForegroundWindowSource foreground)
    {
        var executor = new ActionExecutor(engine, registry, foreground);
        var dispatcher = new ActionDispatcher(executor);
        return (dispatcher, executor);
    }
}
