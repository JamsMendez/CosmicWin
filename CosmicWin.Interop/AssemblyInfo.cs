using System.Runtime.CompilerServices;

// Lets the test project reach the internal Win32 native-source seam (INativeWindowSource and
// friends) and the internal Win32Workspace(INativeWindowSource) constructor used to substitute
// a fake for unit testing WT-1's tracking algorithm without a real desktop session.
[assembly: InternalsVisibleTo("CosmicWin.Interop.Tests")]
