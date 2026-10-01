# Ignore an alert request while another alert is in progress

## Objective

While a warning/failed alert is showing, or waiting to show, a new alert request is ignored
instead of queued behind it. Same spirit as `same-video-noop.md`: a repeat request never stacks.

## Problem

`AlertQueue` is a bounded FIFO (capacity 8): every accepted command is queued and shown one after
another, so a burst of requests plays a long chain of alerts.

## Decisions (maintainer, 2026-09-26)

1. ANY alert is ignored while one is in progress, not only an identical one.
2. BOTH front doors: the named pipe (`--alert`) and the HTTP endpoint. The change lives in
   `AlertQueue.Enqueue`, which both reach through `HandleAlertCommand`.
3. SAME response: an ignored request still answers `ok` (HTTP 202). Only a trace line tells.
4. An alert WAITING while the desktop is covered (fullscreen) also counts as in progress: at most
   one alert exists at a time, showing or waiting.

## Approach

- `Enqueue(command, now)`: if an alert is current and its display window has NOT elapsed at `now`,
  or a queued alert exists that has NOT expired at `now`, record a diagnostic
  (`alert ignored: ...`) and return accepted without queueing.
- Judge "in progress" with `now`, not with state only `Advance` clears: an alert whose duration
  ended since the last tick, or a waiting one past its max age, must not swallow a new request.
- Capacity/queue-full stays in the code; with at most one waiting alert it is no longer reachable
  through the composition. Not removed in this feature.

## Constraints

- Strict TDD, runner `dotnet test`. Mutation-check any test that passes on its first run.
- Existing FIFO tests that encode the old stacking behavior are rewritten to the new rule, with a
  reason, not deleted silently.
- Everything stays local: no push, no gh.

## Tasks

- [x] A1 `AlertQueue`: ignore while showing or waiting, judged at `now`; diagnostic line; tests
  (ignored while showing, accepted right after the window ends before any Advance, ignored while
  waiting covered, accepted once the waiting one expired, ignored request never shows later).
  Update existing tests that relied on stacking.
- [x] A2 Composition/protocol check: pipe and HTTP both answer `ok`/202 for an ignored request and
  the trace records it (wiring test through `HandleAlertCommand`).
- [x] A3 Hardware: two HTTP alerts back to back -> 202 both, only the first shows, trace shows the
  ignore; a request after the first ends shows normally.

- [x] A4 Remove the now-dead queue-full path (maintainer, 2026-09-26, after review finding
  R3-capacity-false-path-unreachable-untested): `AlertQueue` capacity parameter/field/guard/
  `DefaultCapacity`, `Enqueue`'s false return, `HandleAlertCommand`'s `QueueFullReply` branch,
  `AlertPipeProtocol.QueueFullReply`, the 429 mapping, their tests, README row, client comment.

## Acceptance criteria

- A request during a showing or waiting alert: `ok`/202, never shown, trace `alert ignored`.
- A request after the previous alert ended (or expired unshown): shown as today.
- All suites green.

## Progress

- 2026-09-26: branch `feat/alert-busy-ignore` created from main 53160fd. Route: A1+A2 delegated to
  one writer (queue + composition + their tests, writer trigger).
- 2026-09-26: A1+A2 done, TDD strict (runner `dotnet test`).

  **A1** -- `CosmicWin.App/Alerts/AlertQueue.cs`. `Enqueue` now runs `DropExpired(now)` first, then
  ignores (returns `true`, does not queue) when `_current` is showing at `now`, or a pending alert
  exists and was not just dropped as expired; otherwise queues as before. Class remarks and
  `Enqueue`'s XML doc rewritten for the new rule. Commit `4e03a2c`.

  Tests added to `CosmicWin.App.Tests/Alerts/AlertQueueTests.cs`: `ASecondRequest_IsIgnoredWhileOneIsShowing`,
  `ARequestRightAfterTheWindowEnds_IsAcceptedBeforeAnyAdvanceRuns_AndStartsOnTheNextAdvance`,
  `ASecondRequest_IsIgnoredWhileOneIsWaiting` (also proves the ignored one never shows later),
  `ARequest_IsAcceptedOnceThePreviouslyWaitingOneHasExpired`.

  RED (before implementing, `dotnet test --filter FullyQualifiedName~AlertQueueTests`): 3 new tests
  failed (`ASecondRequest_IsIgnoredWhileOneIsShowing`, `ASecondRequest_IsIgnoredWhileOneIsWaiting`,
  `ARequest_IsAcceptedOnceThePreviouslyWaitingOneHasExpired` -- each on `Assert.Single` finding no
  "alert ignored"/"alert dropped" message), 14 passed. GREEN after implementing: 17/17.

  Rewritten (encoded the old stacking/capacity behavior, no longer reachable): removed
  `UpToCapacity_EveryEnqueueSucceeds_AndOneMoreIsRejected`, `ARejectionFromAFullQueue_IsReported`,
  `ARejectedAlert_NeverDisplacesAnOlderQueuedOne` (capacity-full is unreachable through `Enqueue`
  now: with at most one alert ever pending, `_pending.Count` can only reach `_capacity` when
  `capacity` is 1, and the busy-ignore check intercepts that case first) and
  `WhenOneAlertEnds_TheNextEligibleOneStartsOnTheSameAdvanceCall` (enqueued both alerts back to back
  at the same instant -- exactly the stacking the new rule forbids; replaced by
  `ARequestRightAfterTheWindowEnds_...`, which enqueues the second alert only once the first's
  window has elapsed). `TheFirstEnqueuedAlert_IsTheFirstShown` kept, comment updated: its second
  `Enqueue` call is now an ignored request, not a queued one.

  **Capacity decision**: kept the `capacity` constructor parameter, its validation, and the internal
  capacity-full branch in `Enqueue` (not removed), even though the busy-ignore check now always
  intercepts a second `Enqueue` call before that branch can run for any `capacity >= 1`. Documented
  as a defensive invariant guard in the class remarks and `Enqueue`'s XML doc, per the task's
  "not removed in this feature" decision.

  Mutation-checked `ARequestRightAfterTheWindowEnds_...` (passed on first run, since `Advance`'s
  existing FIFO-handoff logic already covered part of it): changed `now <` to `now <=` in the
  showing-boundary check -> test failed (`Assert.NotNull` on a null active alert); reverted -> green
  again.

  **A2** -- `CosmicWin.App.Tests/Alerts/WebViewAlertCompositionWiringTests.cs`. No production wiring
  change needed: `HandleAlertCommand` already returns `AlertPipeProtocol.OkReply` whenever
  `Enqueue` returns `true`, and an ignored request now returns `true` by design (A1), so the pipe
  and HTTP replies are already identical for a queued or an ignored request. Added
  `ASecondCommandWhileShowing_StillRepliesOk_ButIsIgnored` (proves it through the shared
  `HandleAlertCommand` entry, exercised here via the pipe's `Server.Send` -- the HTTP route calls
  the exact same delegate, see `AppComposition.cs` ~1065, so no separate HTTP-level test is added).
  Rewrote `CoveredQueueWaitsAndNextAlertStartsAfterPreviousEnds` (encoded two alerts queued while
  covered both eventually showing) into `ASecondCommandWhileCoveredAndWaiting_IsIgnored_AndOnlyTheFirstEverShows`.
  Commit `1519fff`.

  This new test passed on first run (A1's fix already covers it) -- mutation-checked by short-
  circuiting the showing-ignore branch in `AlertQueue.Enqueue` (`if (false && ...)`) -> test failed
  (`Assert.Contains` found no "alert ignored" trace line); reverted -> green again.

  **Verification** (full solution):
  - `dotnet build CosmicWin.sln`: succeeded, 2 pre-existing warnings (both
    `MultiMonitorWorkspaceAdapter.cs`, CS8604/CS8602, unrelated to this change) -- the task doc's
    "expect only 3" baseline does not match; only 2 were observed on a clean `--no-incremental`
    rebuild, verified as pre-existing (not touched by this feature).
  - `dotnet test CosmicWin.sln`: `CosmicWin.Layout.Tests` 198/198 (0 skipped) -- matches baseline.
    `CosmicWinAlert.Tests` 13/13 (0 skipped) -- matches baseline. `CosmicWin.Interop.Tests`
    387 passed/42 skipped, 429 total -- matches baseline. `CosmicWin.App.Tests` 1022 passed/6
    skipped, 1028 total -- baseline was 1021/6 skipped (1027 total); the +1 net is expected (A1:
    net 0, four tests added and four removed; A2: net +1, one test added and one rewritten-in-place
    removed). All suites green, 0 failed.

  Status: **done** (A1, A2). A3 (hardware) intentionally out of scope for this writer.

- 2026-09-26: review `review-6da54c7583a74229` (medium, reliability, main..2421b89) APPROVED and
  acknowledged (authority burned). Two non-blocking SUGGESTIONs, left as follow-ups:
  - R3-capacity-false-path-unreachable-untested: `Enqueue` can no longer return false for any valid
    capacity; the guard and the caller's `QueueFullReply` path are dead and untested. Remove, or add
    a seam that proves it.
  - R3-http-202-claim-unproved: the ignored-request 202 is proven through the shared handler (pipe
    `Server.Send`), not by an HTTP-level test.
- 2026-09-26: A3 on hardware, Release build of 2421b89 (PID 34172, replacing 26960), `alert-http = on`.
  18:53:49 `{"warning":2,"duration":8}` -> 202 ok, `alert-layer show kind=warning duration=8000`.
  18:53:51 `{"failed":1,"duration":5}` -> 202 ok, trace `alert ignored: one is already showing`, never shown.
  18:53:57 warning done/hide. 18:54:00 `{"failed":1,"duration":3}` -> 202, shown, done/hide at 18:54:03.
  The covered/waiting case was NOT exercised on hardware (unit + wiring tests only).

- 2026-09-26: A4 done, one writer, deletion refactor (no RED needed, no new behavior). Commit
  `79c3028` -- `refactor(app): remove the unreachable alert queue-full path`.

  Removed from `CosmicWin.App/Alerts/AlertQueue.cs`: `DefaultCapacity` const, the `_capacity` field,
  the `capacity` constructor parameter and its `ArgumentOutOfRangeException` validation, the
  capacity-full branch in `Enqueue` (`if (_pending.Count >= _capacity) { ...; return false; }`).
  `Enqueue` now returns `void` instead of `bool` (it always accepts, by queueing or by ignoring).
  Class remarks and `Enqueue`'s XML doc rewritten to describe the removal instead of the old
  "defensive invariant guard, not removed" language; no more capacity/FIFO/bounded-queue framing.

  `CosmicWin.App/AppComposition.cs` (~590-600): `HandleAlertCommand`'s
  `if (!alertQueue.Enqueue(...)) return AlertPipeProtocol.QueueFullReply;` collapsed to a plain
  `alertQueue.Enqueue(...)` call, falling through to `OkReply` as before. The `new AlertQueue(...)`
  call site (~317) uses a named `onDiagnostic:` argument and needed no change.

  `CosmicWin.Interop/AlertPipeProtocol.cs:43`: `QueueFullReply` constant and its doc removed.
  `CosmicWin.Interop/AlertHttpProtocol.cs` (~101-107): the `QueueFullReply => 429` mapping arm and
  its doc mention removed. `README.md:152`: the `429 | The alert queue is full` row removed (no
  other README mention of capacity/queue-full/FIFO stacking was found). `CosmicWinAlert/Program.cs:35`:
  comment updated to drop "or 'queue full'".

  Tests removed (existed only to prove the now-deleted path):
  - `CosmicWin.Interop.Tests/AlertHttpProtocolTests.cs:75`: the
    `[InlineData(AlertPipeProtocol.QueueFullReply, 429)]` theory row.
  - `CosmicWin.Interop.Tests/AlertPipeProtocolTests.cs`: `QueueFullReply_IsAWellFormedErrorLine`.
  - `CosmicWin.Interop.Tests/LocalHttpCommandServerTests.cs`: `HandlerReplyQueueFull_PassesThroughAs429`.
  - `CosmicWin.App.Tests/Alerts/AlertQueueTests.cs`: `Constructor_NonPositiveCapacity_Throws`
    (`[Theory]` with 2 cases, `capacity: 0` and `capacity: -1` -- the constructor no longer takes a
    `capacity` argument to validate).

  Tests adapted (kept, since they prove real busy-ignore behavior, just no longer assert a `bool`
  return from `Enqueue`): `ASecondRequest_IsIgnoredWhileOneIsShowing`,
  `ARequestRightAfterTheWindowEnds_IsAcceptedBeforeAnyAdvanceRuns_AndStartsOnTheNextAdvance`,
  `ASecondRequest_IsIgnoredWhileOneIsWaiting`, `ARequest_IsAcceptedOnceThePreviouslyWaitingOneHasExpired`
  -- each dropped its `var accepted = queue.Enqueue(...); Assert.True(accepted);` pair for a plain
  `queue.Enqueue(...)` call, keeping the rest of the assertion (the diagnostic message / resulting
  `Advance` behavior). The doc comment above `ASecondRequest_IsIgnoredWhileOneIsShowing` (explaining
  why the old capacity-rejection tests were rewritten) updated to note A4 removed the capacity path
  entirely rather than keeping it as a dead defensive guard. The class-level remark's "small
  capacities/max ages" updated to "small max ages" (no more capacity parameter to vary).

  `rg -n "QueueFullReply|queue full|DefaultCapacity|_capacity"` (repo-wide, `.cs`/`.md`): only the
  unrelated chord-queue lines (`AppComposition.cs:1524` and `DroppedChordWiringTests.cs`, "chord
  dropped -- queue full", a different queue) and historical `odd/tasks/http-alert-endpoint.md` /
  this file's own earlier task description remain. No production or test code left.

  Verification:
  - `dotnet build CosmicWin.sln` (clean, `--no-incremental`): succeeded, 3 warnings -- the 2
    pre-existing `MultiMonitorWorkspaceAdapter.cs` CS8604/CS8602 (baseline, untouched by this
    change) plus 1 pre-existing `CosmicWinAlert.Tests/ProgramTests.cs:200` CA2022 (a file this
    change never touched; not newly introduced here). No new warning from this change.
  - `dotnet test CosmicWin.sln`: `CosmicWin.Layout.Tests` 198/198 (matches baseline).
    `CosmicWinAlert.Tests` 13/13 (matches baseline). `CosmicWin.Interop.Tests` 384 passed/42 skipped,
    426 total (baseline 387/42, 429 total; -3 = the 3 removed queue-full tests). `CosmicWin.App.Tests`
    1020 passed/6 skipped, 1026 total (baseline 1022/6, 1028 total; -2 = the removed
    `Constructor_NonPositiveCapacity_Throws` theory's 2 cases). All suites green, 0 failed.

  Status: **done** (A4). All tasks A1-A4 complete; A3's covered/waiting case on real hardware
  remains the only acceptance-criteria gap, unchanged from before this task.
- 2026-09-26: review `review-4c0ef26e22b851af` (medium, reliability, 2421b89..67edcf0, covers A4) APPROVED
  with no findings, acknowledged (authority burned).

- 2026-10-01: covered/waiting case confirmed on hardware (Release of main 0f2161a). It cannot happen in
  html-mini mode (the mini window is topmost; AppComposition's desktopVisible for mini has no coverage
  check), so wallpaper-mode was switched to `html` for the run and restored afterwards (settings.conf
  byte-identical to the backup). A borderless topmost WinForms probe covered the primary monitor
  (`wallpaper-scene paused: desktop covered`). 22:25:52.54 `{"warning":1,"duration":5}` -> 202;
  22:25:52.64 `{"failed":1,"duration":5}` -> 202, trace `alert ignored: one is already waiting to
  show`. Probe closed 22:25:58 -> `wallpaper-scene resumed`, `alert-layer show tiles=warning ...
  duration=5000`, done/hide at 22:26:03-04. The ignored failed alert never showed (0 `tiles=failed`
  shows in the window; 1 show in total). A3's covered half is now closed.
