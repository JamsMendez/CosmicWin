using Xunit;

// The other half of the same fix -- see CosmicWin.Interop.Tests/TestParallelism.cs for the measured
// reasoning, which applies here for the same reason: this assembly also declares a RealDesktop
// collection, and [CollectionDefinition(DisableParallelization = true)] does not stop the ~340
// headless facts around it from running at the same time as the desktop ones.
//
// Both assemblies need it. The RealDesktopSession mutex serialises the two assemblies against EACH
// OTHER; it does nothing about the collections running in parallel INSIDE either of them. Fixing
// only one would leave the other still starving its own desktop facts.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
