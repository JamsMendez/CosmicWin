using Xunit;

// Serialises this whole assembly, and it is not belt-and-braces on top of RealDesktopCollection --
// it closes a hole that collection never covered.
//
// [CollectionDefinition(DisableParallelization = true)] means "do not parallelise the tests INSIDE
// this collection". It says nothing about OTHER collections, and xunit happily runs them alongside
// it. So every headless fact in this assembly -- 87 of them, in their own implicit collections --
// was running concurrently with the desktop facts the collection exists to protect.
//
// The mechanism that makes that expensive is concrete. Win32NativeWindowSource.Activate hands its
// work to a dedicated thread and gives it worker.Join(250ms); SpawnedAlacrittyWindow.Spawn and the
// message-pump helpers are on similar budgets. Under concurrent load such a budget can expire
// before the worker is scheduled at all, and the fact then reports a foreground that "did not move"
// with nothing wrong in the product.
//
// MEASURED on one machine, full Interop assembly, desktop opt-in and terminal path set:
//   parallel collections ON  -> 2 of 3 runs failed, a different test each time, every one of them
//                               green in isolation (5 activation facts in one run, 2 virtual-desktop
//                               facts in another)
//   parallel collections OFF -> 3 of 3 runs green, 113/113
// Two sub-experiments corroborate the reading, and are corroboration rather than proof at n=3: the
// activation facts alone were 3/3 green with parallelism ON, and so were the activation facts run
// together with every virtual-desktop-switching class. Both point AWAY from the desktop facts and
// their obvious neighbours, which is what left the headless ones.
//
// What this does NOT establish: that no second cause remains. It does not, and the caveat this
// comment used to end on has since been cashed in -- so the fuller sample is recorded here rather
// than the flattering prefix of it.
//
//   full solution BEFORE, parallel collections ON   1 of 5 runs green
//   full solution AFTER,  parallel collections OFF   6 of 10 runs green
//
// The first five of those ten were consecutive greens and were briefly reported as "5 of 5". They
// were a streak, not a result. Later runs failed again -- activation facts and the virtual-desktop
// vtable fact, the same cast as before.
//
// So: this change removes a REAL and measured cause, and roughly triples the green rate. It is not
// the whole story. At least one more cause remains, and one contributor is already identified: a
// SpawnedNotepadWindow was found still running after a suite finished, its title still carrying the
// cosmicwin-<guid> marker of the run that spawned it. A stray top-level window is exactly what
// breaks a foreground assertion. Clearing that debris moved a 0-of-2 stretch to 1-of-2, which is
// another contributing cause rather than the remaining one.
//
// Cost, measured for both configurations rather than only the one that flatters the change:
//   desktop runs   24-25s -> 28-30s
//   headless runs  ~780ms -> ~900ms   (CosmicWin.App.Tests, the heavier assembly, goes ~130ms ->
//                                      ~410ms; the whole headless suite stays under 1.4s)
// The headless figure is the honest one to look at, since CI only ever runs that: roughly 3x in
// relative terms on App.Tests, 280ms in absolute terms. A desktop suite that cannot be trusted is
// worth considerably less than that.
//
// Deliberately an assembly ATTRIBUTE rather than a runsettings entry or xunit.runner.json: this
// repository has already been bitten by a guard that lived somewhere a build could silently proceed
// without -- nine facts carried a RequiresDesktop trait and no gate, and the trait did nothing
// locally. A compile-time attribute cannot be dropped by running the suite a different way.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
