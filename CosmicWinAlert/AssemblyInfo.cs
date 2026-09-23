using System.Runtime.CompilerServices;

// Lets CosmicWinAlert.Tests reach the internal arg/exit-code helpers (TryBuildCommand, Interpret)
// directly, the same InternalsVisibleTo convention CosmicWin.Interop already uses for its own
// test project.
[assembly: InternalsVisibleTo("CosmicWinAlert.Tests")]
