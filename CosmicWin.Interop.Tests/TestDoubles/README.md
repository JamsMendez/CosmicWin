# TestDoubles — Task 0.9 Decision Record

This project deliberately does **not** depend on a mocking library (Moq, NSubstitute, or
otherwise). All test doubles under `TestDoubles/` (`FakeWindow`, `FakeWorkspace`, `FakeDisplay`,
`FakeDisplayManager`) and `Win32/` (`FakeNativeWindowSource`, `FakeNativeDisplaySource`) are
hand-rolled in-memory implementations of the `IWindow`/`IWorkspace`/`IDisplay`/`IDisplayManager`
and `INativeWindowSource`/`INativeDisplaySource` seams.

## Why (task 0.9)

This was evaluated explicitly, not skipped by oversight:

- The hand-rolled fakes already fully cover every contract exercised by WU1/WU2's 29 tests,
  including stateful scenarios a naive mock wouldn't express cleanly for free (e.g.
  `FakeNativeWindowSource.SimulateWindowDestroyedSilently` vs `SimulateWindowDestroyedWithEvent`
  to distinguish "hook delivered the event" from "hook missed it, only `Poll()` will catch it" —
  spec WT-1's "Hook misses an event" scenario). Expressing that kind of scripted, multi-call
  in-memory state machine in Moq/NSubstitute would not be materially simpler than the current
  ~50-100 line fakes, and would add a runtime/dev dependency to express behavior the fakes
  already express directly in C#.
- Design decision D8 and the Dependency & Supply-Chain Hygiene section (`sdd/cosmic-win/design`)
  explicitly prefer minimizing third-party surface, given every dependency of
  `CosmicWin.App`/`CosmicWin.Interop` ultimately runs inside an always-elevated process. A
  mocking library is dev/test-time only and never ships in the elevated binary, but it is still
  net-new dependency surface (and, per the design doc's own note, Moq specifically has a history
  — the 4.20.0–4.20.1 `SponsorLink` incident — worth avoiding by default rather than pulling in
  without a concrete justification).
- No concrete testing gap was found during WU1–WU3 that the existing hand-rolled fakes could not
  cover. Interface-shaped test doubles for four small, stable interfaces do not need a general
  mocking framework's dynamic proxy/verification machinery.

## When to revisit

If a future work unit needs deep call-sequence verification (e.g. "assert `SetPosition` was
called exactly twice, in this order, with these exact arguments" across many call sites) that
becomes awkward to hand-roll, re-evaluate NSubstitute (preferred over Moq for a fresh addition,
given the `SponsorLink` history) at that point — pin an explicit, current, non-vulnerable
version and re-run `dotnet list package --vulnerable --include-transitive` when it is added.
