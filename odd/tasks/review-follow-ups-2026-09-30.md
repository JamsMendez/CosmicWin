# Review follow-ups from 2026-09-30

## Objective

Close the non-blocking findings left by the reviews of the Explorer-restart reconnect and the
maximize block, and finish their hardware coverage.

## Why

Maintainer request, 2026-09-30: "Vamos a darle a los pendientes no bloqueantes".

## Scope

1. `R3-create-desktop-retry-nonidempotent` -- `Win32NativeVirtualDesktops.CreateDesktop` is retried
   after a disconnect HRESULT. For an ambiguous failure (`0x800706BE` RPC_S_CALL_FAILED,
   `0x80010012`, `0x80004018`) the first call may have run in Explorer, so a retry can create TWO
   desktops. Only an error that proves the call never reached the server may be retried.
2. `R3-static-seam-parallel-tests` -- `Win32VirtualDesktopQueriesReconnectTests` swaps a STATIC
   factory seam on `Win32VirtualDesktopQueries`; xUnit runs test classes in parallel, so another
   class using the real static path can observe the fake (or vice versa).
3. Hung-window edge cases of the maximize block (only reachable when a bounded style write times
   out on a hung target): `R3-late-strip-after-giveback` (a strip that timed out lands AFTER the
   give-back already ran, leaving a disabled button) and
   `R3-admission-verdict-ignores-late-strip` (`IsExcludedAsAdmitted` re-adds the bit only for
   `_boxStripped`, not for a timed-out strip that landed).
4. README: CosmicWin always runs elevated (`app.manifest` requireAdministrator), so the limit
   "windows running as administrator are not touched while CosmicWin is not elevated" describes a
   case that does not occur; reword it truthfully.
5. Hardware coverage not yet measured: a Chromium browser, VS Code, F11 fullscreen.

Out of scope: bounding the pre-existing `SetWindowPosition` (same class as item 3, separate change).

## TDD mode

**Strict TDD: enabled**, source: user's global instructions. Runner: xUnit 2.9.3.

## Tasks

- [x] T1: CreateDesktop retried only when the call provably never ran. Route: delegated writer.
- [x] T2: Queries reconnect tests no longer race other test classes. Route: same writer.
- [x] T3: Maximize block hung-window edge cases. Route: same writer.
- [x] T4: README elevation limit reworded. Route: same writer.
- [x] T5: Hardware: Edge/Chrome, VS Code, F11 on own probe instances (separate user-data-dir,
  killed by PID). Route: inline, parallel to T1-T4 against the running build (code == main bea8a6c).

## Progress

- 2026-09-30: branch `fix/review-follow-ups-2026-09-30` from main bea8a6c.
- 2026-09-30 T1 (route: delegated writer, trigger "2+ non-trivial files"): `ShellDisconnect.CallNeverRan`
  admits only `0x800706BA` (binding failed, request never delivered) and `0x80010108` (proxy already
  knew its channel was gone). `0x800706BE`, `0x80010012`, `0x80004018` are what a call lost WHILE
  running reports, so `CreateDesktop` reconnects but is not retried (`TryInvoke(idempotent: false)`);
  LastError says "reconnected, not retried because the call may already have run". The service then
  re-reads the set: if the create ran it grew and resolution proceeds, otherwise "CreateDesktop did not
  grow the set ... not retried". RED: 5 tests failed (the 3 ambiguous HRESULTs, the next-call and the
  service test; retry made Created 1 instead of 0). GREEN: 27/27 in the reconnect class. Mutation:
  `CallNeverRan` made accept all five -> the same 5 failed; reverted.
- 2026-09-30 T2 (route: delegated writer): users of `Win32VirtualDesktopQueries` found: the reconnect
  tests, `Win32VirtualDesktopService.ResolveWindowDesktop`, `Win32NativeWindowSource` (line 84) and the
  desktop-gated tests (VirtualDesktopMove/Membership/UncloakEvent); only the reconnect class was in the
  `VirtualDesktopQueriesStatic` collection, so the others were unprotected. Chose to REMOVE the shared
  mutable seam: new internal `VirtualDesktopQueryClient(factory)` holds the logic and cached manager;
  the static class delegates to one `Shared` instance; tests build their own client. RED: the test
  project did not compile (type missing). GREEN: 10/10 in the queries classes. No mutation check
  required for T2 (structural refactor).
- 2026-09-30 T3 (route: delegated writer, trigger "2+ non-trivial files"). Design: the native style write
  is now tri-state (`StyleWriteOutcome` Applied / Refused / TimedOut) through `INativeWindowSource` and
  `IWindow`. (a) Adapter: a TimedOut strip is added to `_boxStripped` (owed, possibly landed), so
  `IsExcludedAsAdmitted` keeps the bit; a re-strip is skipped while the handle is in `_boxStripped`;
  only a proven Refused goes to `_boxRefused`. (b) New `StyleCallQueue`: per-hwnd ordering -- a style call
  starts only after an earlier call on the same window finished, even an abandoned one. The wait is on
  the call's own worker thread, so the dispatcher thread stays bounded by the 250 ms budget and nothing
  needs marshalling back; the queued give-back reports TimedOut now and lands after the strip. Proof is
  headless: `StyleCallQueueTests.ALaterCallOnTheSameWindow_RunsOnlyAfterTheAbandonedOneFinished` parks a
  strip, times it out, issues the give-back, asserts it has not run, releases the strip and asserts the
  order [strip-landed, give-back]. RED: adapter `AStripThatTimedOutAndLanded_KeepsTheAdmissionVerdict_...`
  failed (window evicted); queue ordering test failed against an unchained skeleton (give-back ran,
  reported Applied). GREEN: MaximizeBlockTests 34/34, StyleCallQueueTests 5/5, Interop 487 passed, App
  1355 passed. Mutations: removed the chain wait -> ordering test failed; TimedOut mapped to
  `_boxRefused` -> admission test failed; both reverted. `BoundedStyleCallTests` removed (its three facts
  moved to `StyleCallQueueTests`, `RunStyleCall` is gone).
- 2026-09-30 T4: README limit reworded (CosmicWin always runs elevated; remaining case is a window that
  refuses or does not answer in time) and the custom-title-bar bullet corrected with the hardware
  facts the parent measured (Chromium may draw an active-looking button that does nothing; VS Code hides
  it; nothing flashes; the restore is a safety net). Other README elevation sentences (lines 42-47) are
  true and unchanged.
- T5 hardware (route: inline, parallel to T1-T4, running build == main bea8a6c, elevated shell, own
  probe instances with separate user-data-dir, killed by PID):
  - Brave (Chromium, custom title bar): tiled `maxbox=False`; button DRAWN AS ACTIVE; a real click on
    it did nothing (IsZoomed false); the same click with tiling off maximized (3408x1456); tiling on
    put it back in its slot with `maxbox=False`. Win+Up blocked. F11: real fullscreen 3440x1440 with
    the box given back; leaving F11 returned it to its slot, box disabled again. Title-bar
    double-click: INCONCLUSIVE (the control with tiling off did not maximize either, so the click
    missed a draggable area).
  - VS Code (Electron, ran as Administrator): tiled `maxbox=False`; the maximize button is HIDDEN
    (only minimize/close drawn); Win+Up and SW_MAXIMIZE refused; Alt+T off gives it back, on
    disables it again. Elevated target handled normally (CosmicWin always runs elevated).
  - No `maximize undone`/`kept` trace line in any case: Windows refused every maximize itself.

- Review: the base-diff against origin/main (3519 lines) stopped with `lens_context_budget_exceeded`
  (nothing created); re-run scoped to this branch (`--base-ref bea8a6c`, 1136 lines, consent granted):
  lineage review-1e52b56243ca78a3, APPROVED and acknowledged (authority burned) on 03568ef. Advisory,
  hung-window only, left open on purpose: R3-queue-thread-bound-claim (the StyleCallQueue remark says
  at most a strip and a give-back per window; restores and repeated Alt+T also queue, so a hung window
  can park more idle threads), R3-restore-queued-behind-pending-strip (a restore waits behind a pending
  strip on the same hung window and reports a refusal).
