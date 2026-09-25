# Watchdog gate

## Objective

Stop the keyboard-hook watchdog from reinstalling a live hook when it has no reason to. Close the
one real recovery gap the current gate leaves.

## Problem and evidence

Trace 2026-09-10..09-25: 3432 watchdog reinstalls, `foundGone=0` on every one (the watchdog has
never found a dead hook). Since 2026-09-20 there were 320 reinstalls: 114 gaps of ~300 s and 155
gaps over 310 s, so most come from the 5-minute backstop, which fires whenever the user goes 5
minutes without typing.

The backstop exists because of a gap in the gate (`LowLevelKeyboardHook.ShouldReinstall`). The
gate treats input as key-shaped only if the cursor has not moved since the hook's LAST KEY. If the
hook dies, the user then moves the mouse and then types, the cursor move is later than that stale
last key, so the gate blames the mouse forever. Only the backstop recovers, after 5 minutes.

## Decision (maintainer, 2026-09-24)

- Gate: compare against the MOST RECENT session input (`now - sessionAge`), not the hook's last
  key. If the cursor has not moved since that latest input, the latest input was key-shaped and
  the hook missed it, so reinstall. Clicks and wheel with a still cursor remain the documented
  residue.
- Backstop: raise `DefaultWatchdogBackstop` from 5 to 30 minutes and keep it as a safety net for a
  silently wrong reading (never observed). A refused reading still reinstalls, as before.

## Scope

`CosmicWin.App/Input/LowLevelKeyboardHook.cs` (gate, backstop default, remarks) and
`CosmicWin.App.Tests/Input/KeyboardHookTests.cs`. No other behavior changes.

## TDD mode

Strict TDD: enabled. Source: the user's global instructions. Runner:
`dotnet test CosmicWin.App.Tests/CosmicWin.App.Tests.csproj`.

## Tasks

- [x] **W1 -- Gate compares against the latest session input.** RED: a dead hook, then mouse,
  then typing, must reinstall before the backstop. Keep mouse-only input from reinstalling.
  Route: delegated writer (2 non-trivial files). Commit `da454ef`.
- [x] **W2 -- Backstop default 30 minutes.** RED on the default value. Route: same writer.
  Commit `4b160ec`.
- [ ] **W3 -- Hardware check.** Watch the trace for a working period and compare the reinstall
  rate with the baseline (about 64/day since 09-20).

## Progress

2026-09-24: document created; W1 next.

2026-09-24: W1 done, commit `da454ef`. RED: added
`Watchdog_WhenTheCursorMovedBeforeAKeyThatFollowed_ReinstallsWithoutWaitingForTheBackstop` (dead
hook -> mouse move at ~1s -> a 100ms-old key the hook never saw at clock 6s) -- confirmed failing
under the old gate (`Assert.True() Failure: Expected True, Actual False`) before implementing.
Sabotage check after GREEN: reverted the gate line to the old `<= lastActivity` comparison,
reconfirmed the same test fails, restored the fix. Also fixed two pre-existing mouse-only tests
(`Watchdog_WhenTheMissedInputWasTheCursorMoving_LeavesTheHookAlone` and the backstop test's phase
1) that jumped the fake clock in one large step, which is racy under the new gate (it reads the
clock fresh in `ShouldReinstall`, so a test's `Advance` can land between that read and
`SampleCursor`'s own read of the same pass); replaced with a shared
`AdvanceWhileCursorKeepsMoving` helper that steps in increments smaller than `SystemInputAge`.
Verified they still passed under the OLD gate before the gate changed, and pass under the new one
too. `dotnet test CosmicWin.App.Tests/CosmicWin.App.Tests.csproj --filter
FullyQualifiedName~KeyboardHookTests` run 30x after a real build: every run showed exactly 30
passed / 1 failed (the not-yet-implemented W2 default-value test), no flakiness in the other 30.
W2 next.

2026-09-24: W2 done, commit `4b160ec`. RED confirmed earlier (test added alongside W1's edits,
`DefaultWatchdogBackstop_IsThirtyMinutes`, failed `Expected 00:30:00, Actual 00:05:00`) before
raising the default and rewriting its doc comment. `dotnet build CosmicWin.sln -c Debug`: 0
errors. `dotnet test CosmicWin.App.Tests/CosmicWin.App.Tests.csproj`: 776 passed / 6 skipped (774
baseline + the 2 tests added for this feature), 0 failed. `--filter
FullyQualifiedName~KeyboardHookTests` run 30x after a real build: 30/30 runs clean, 0 flaky
failures. `git diff --check` clean on both commits. W1 and W2 complete; W3 (hardware trace watch)
is out of scope for this writer -- left for the maintainer.

2026-09-24 parent check: the writer had committed the W2 test inside the W1 commit, so the W1
commit failed on its own (`DefaultWatchdogBackstop_IsThirtyMinutes`, verified in a throwaway
worktree). The unpushed branch was rebuilt with the same messages and the test moved into the W2
commit. The final tree is identical. W1 is now `da454ef` (App 775 passed / 6 skipped at that
commit), W2 is `4b160ec` (776 / 6), and the hashes above were updated. W3 (a hardware trace
watch) is next.

2026-09-24 native review of `main..99a197c` (10 files, 597 lines, risk high: the only evidence was
process-starting code in `SlowAdmissionDiagnostic.cs`, where this branch changed one comment
word). The maintainer granted consent. Lineage `review-1b81838a03f749ea`, four lenses (risk,
resilience, readability, reliability) captured concurrently. Result: **approved** with no
correction, and the acknowledgement burned authority. The reviewed boundary is now `99a197c`.
W3 (the hardware trace watch) remains.
