namespace CosmicWin.Interop.Tests.Win32;

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
/// This serialises within an assembly only -- <c>DisableParallelization</c> stops at the assembly
/// boundary and the desktop does not. <c>dotnet test</c> on the solution runs the test PROJECTS in
/// parallel (measured here as three concurrent <c>testhost</c> processes), so the two desktop
/// collections would otherwise drive the SAME foreground at once. The
/// <see cref="RealDesktopSession"/> fixture closes that gap with a session-wide named lock, so a
/// solution-wide run now WAITS instead of racing. Running the projects one at a time is still the
/// faster way to do a desktop run; it is no longer the only correct one.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealDesktopCollection : ICollectionFixture<RealDesktopSession>
{
    public const string Name = "RealDesktop";
}
