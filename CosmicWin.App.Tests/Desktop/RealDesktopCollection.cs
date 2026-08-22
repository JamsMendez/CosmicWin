namespace CosmicWin.App.Tests.Desktop;

/// <summary>
/// Serialises every fact that touches the one real desktop this machine has. xunit runs test
/// CLASSES in parallel by default, and a desktop fact is not isolated from its neighbours: spawning
/// a window, activating one, or injecting synthetic Alt taps changes the SAME foreground another
/// class is asserting on. Concurrently, those facts fail in ways that look like product defects and
/// are not -- measured directly when the activation suite landed and destabilised the pre-existing
/// Notepad-based one. Every <c>RequiresDesktop</c> class joins this collection so they run one at a
/// time.
/// </summary>
/// <remarks>
/// This only serialises WITHIN an assembly. <c>dotnet test</c> on the solution still runs the test
/// PROJECTS in parallel, so a desktop run must invoke one project at a time:
/// <c>dotnet test CosmicWin.Interop.Tests/CosmicWin.Interop.Tests.csproj</c> then
/// <c>dotnet test CosmicWin.App.Tests/CosmicWin.App.Tests.csproj</c>.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealDesktopCollection
{
    public const string Name = "RealDesktop";
}
