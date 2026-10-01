# Hung-window style queue follow-ups

## Objective

Close the last two advisory findings of review-1e52b56243ca78a3 on `StyleCallQueue`, both only
reachable when a target window is hung.

## Why

Maintainer request, 2026-09-30: "podemos atacar los pendientes?".

## Scope

1. `R3-queue-thread-bound-claim` -- `StyleCallQueue`'s remarks claim at most a strip and a give-back
   per window, but restores and repeated Alt+T also queue: a hung window can park an unbounded
   number of idle threads (one per queued call). Bound it for real (e.g. coalesce: at most one
   pending call per window per kind, a newer request superseding a not-yet-started one), or prove a
   bound and correct the remark.
2. `R3-restore-queued-behind-pending-strip` -- a restore (`TryRestoreFromMaximized`) waits behind a
   pending strip on the same hung window and reports a refusal, which the adapter then counts.
   Decide and justify whether restores belong in the same ordering queue at all.

## TDD mode

**Strict TDD: enabled**, source: user's global instructions. Runner: xUnit 2.9.3.

## Tasks

- [x] T1: Real bound on parked threads per window. Route: delegated writer.
- [x] T2: Restore no longer blocked by a pending strip (or justified). Route: same writer.

## Progress

- 2026-09-30: branch `fix/hung-window-style-queue` from main 9af2c0c.
- 2026-09-30 T1 (route: delegated writer, trigger "2+ non-trivial files"). `StyleCallQueue` rewritten: per
  window ONE running call plus ONE pending call held as data (no parked thread); the same worker starts
  the pending one when the running one finishes, so a hung window parks ONE thread however many callers
  pile up. A newer pending call replaces the older (older never runs, reports TimedOut); sound because
  ordered calls are idempotent "set box to X" writes. Order running -> pending kept, so give-back still
  lands after a strip. Remarks now state the real bound. RED (skeleton members, old chain impl):
  `ManyCallsOnAParkedWindow_ParkOneWorker_AndOnlyTheLatestRequestLandsAfterTheFirst`,
  `ASupersededPendingCall_StopsWaitingAtOnce_AndReportsTimedOut` failed (4 failed of 9 in the class
  incl. the two T2 ones). GREEN: 9/9. Mutation: `Pending = request` -> `Pending ??= request` (keep the
  oldest) -> both T1 tests failed; reverted. 25 consecutive runs of the class: 25/25 pass.
- 2026-09-30 T2 (same writer). Decision: a restore does NOT share the ordering queue. Reasoning: it only
  undoes a maximize (touches WS_MAXIMIZE state); the adapter's maximize fallback and the tiling-on batch
  call it without needing a box write to have landed or not, so ordering buys no correctness, while
  queuing it behind a hung strip yields a false refusal that counts toward Judge eviction. New
  `StyleCallQueue.RunIndependent` lane: own worker, at most one in flight per window, extra calls while
  one is in flight answer TimedOut without starting a thread (so a hung window parks at most 2 threads:
  one per lane). `TryRestoreFromMaximized` uses it. RED: `AnIndependentCall_IsNotQueuedBehindAParkedOrderedCall_OnTheSameWindow`
  and `RepeatedIndependentCalls_OnAWindowThatNeverAnswers_ParkOneWorker` failed against a skeleton that
  delegated to `Run`. Mutation: `RunIndependent` delegating to `Run` -> both failed; reverted.
  Verification: dotnet build CosmicWin.sln 0 errors; Interop.Tests 491 passed/42 skipped; App.Tests 1355
  passed/6 skipped. Race noted: a strip racing a restore in parallel reads then writes the whole style
  word, so it could re-set a stale WS_MAXIMIZE bit; only possible when the target was hung, and the
  adapter's next bounds change re-checks the maximized bit.
