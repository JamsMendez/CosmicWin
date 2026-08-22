using System.Runtime.CompilerServices;

// Lets the test project reach the internal Win32 native-source seam (INativeWindowSource and
// friends) and the internal Win32Workspace(INativeWindowSource) constructor used to substitute
// a fake for unit testing WT-1's tracking algorithm without a real desktop session.
[assembly: InternalsVisibleTo("CosmicWin.Interop.Tests")]

// WU28: lets CosmicWin.App.Tests construct the real Win32Window/Win32NativeWindowSource for a
// desktop integration test against real spawned windows -- test-only, never ships in the
// elevated binary, no runtime behavior change.
[assembly: InternalsVisibleTo("CosmicWin.App.Tests")]
