using Xunit;

// Serialises this whole assembly, and it is not belt-and-braces on top of RealDesktopCollection --
// it closes a hole that collection never covered.
//
// [CollectionDefinition(DisableParallelization = true)] means "do not parallelise the tests INSIDE
// this collection". It says nothing about OTHER collections, and xunit happily runs them alongside
// it. So every headless fact in this assembly -- 87 of them, in their own implicit collections --
// was running concurrently with the desktop facts the collection exists to protect.
//
// That is not a theoretical cost. Win32NativeWindowSource.Activate hands its work to a dedicated
// thread and gives it worker.Join(250ms); SpawnedAlacrittyWindow.Spawn and the message-pump helpers
// are on similar budgets. Under concurrent load those budgets expire before the worker is even
// scheduled, and the fact reports a foreground that "did not move" when nothing was wrong with the
// product at all.
//
// MEASURED on this machine, full Interop assembly, desktop opt-in and terminal path set:
//   parallel collections ON  -> 2 of 3 runs failed, a different test each time, every one of them
//                               green in isolation (5 activation facts in one run, 2 virtual-desktop
//                               facts in another)
//   parallel collections OFF -> 3 of 3 runs green, 113/113
// The same subset run WITHOUT its headless neighbours was 3/3 green with parallelism on, which is
// what points at the neighbours rather than at the desktop facts themselves.
//
// The cost is about three seconds on this assembly (24-25s -> 27-28s). A desktop suite that cannot
// be trusted is worth considerably less than three seconds.
//
// Deliberately an assembly ATTRIBUTE rather than a runsettings entry or xunit.runner.json: this
// repository has already been bitten by a guard that lived somewhere a build could silently proceed
// without -- nine facts carried a RequiresDesktop trait and no gate, and the trait did nothing
// locally. A compile-time attribute cannot be dropped by running the suite a different way.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
