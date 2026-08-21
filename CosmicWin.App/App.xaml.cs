using System.Windows;

namespace CosmicWin.App;

/// <summary>
/// Production entry point. All composition logic now lives in <see cref="AppComposition"/>
/// (verify-report #21 revision 15 SUGGESTION V15-S1) -- this class does nothing but delegate to
/// <see cref="AppComposition.WireProduction"/> and dispose the result. Four consecutive closures
/// (V11-W1, V12-W1, V13-W1, V14-W1) tried to defend a composition site living directly in this WPF
/// <see cref="Application"/> subclass from OUTSIDE it, and each fell to a mutation the previous one
/// had not anticipated -- the last kept ONLY a source-text guard (<c>CompositionSiteArchitectureTests</c>,
/// deleted) that three separate probes defeated while building with 0 Warning(s) on a green suite.
/// This class no longer has anything left to mis-wire: no mutable composition-collaborator fields,
/// no direct <see cref="CompositionRoot"/> calls, a single delegating call each in <see
/// cref="OnStartup"/>/<see cref="OnExit"/>. <see cref="AppEntryPointThinnessTests"/> guards that
/// this stays true; <see cref="AppComposition"/>'s own tests (<c>AppCompositionTests</c>) prove the
/// wiring itself is correct, as a real behavioral fact rather than a spelling check.
/// </summary>
public partial class App : Application
{
    private AppComposition? _composition;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _composition = AppComposition.WireProduction(Shutdown);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _composition?.Dispose();
        base.OnExit(e);
    }
}
