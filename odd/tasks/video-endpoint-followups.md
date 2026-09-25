# Video endpoint follow-ups

## Objective

Fix the three pre-existing problems found while building the video wallpaper HTTP endpoint
(`odd/tasks/video-wallpaper-http-endpoint.md`, V5 notes). The maintainer authorized them on
2026-09-25, right after merging that feature into local main (`f03b7f4`).

## Problems

1. **The desktop trace drops lines.** `FileDesktopTrace.Record` uses `File.AppendAllText` and
   swallows every `IOException`. When another process holds `desktop-trace.log` open without write
   sharing, the append fails and the line is lost with no sign. `File.ReadAllLines`, many editors
   and `Get-Content` without `-Wait` do this. Seen in V5: a harness that polled the trace every
   50 ms lost the line of a successful 6.6 GB switch.
2. **The video wallpaper thread swallows exceptions untraced.** `MtaActionThread.Run` (private,
   nested in `AppComposition`) runs posted work in `try { work(); } catch { }`. A work item that
   throws after the import (persist, attach, play) leaves no trace line at all, and the wallpaper
   may already be stopped.
3. **`HttpAlertCommandServer` is misnamed.** Since V3 it serves `/v1/alerts` AND
   `/v1/wallpaper/video`.

## Approach

- P1: a bounded retry on a sharing violation, bounded by elapsed TIME (150 ms), not by a count of
  sleeps (see the F1 progress note). The line is dropped only if every attempt fails, as today. The writer's own
  share mode cannot help: a reader that denies writers blocks every writer. Retrying is the smallest
  fix that keeps `Record` synchronous. A background writer queue was rejected: more moving parts,
  and lines could be lost at exit. Keep the lock; `Record` must still never throw.
- P2: the loop's catch records the exception TYPE through the desktop trace (never the message,
  which can hold an absolute path), and the thread keeps running. Make the class testable
  (`internal`, or extract it) with an injected sink.
- P3: rename the class, its test class, the files, and the `createHttpAlertCommandServer` factory
  seam. Trace and diagnostic text (`alert-http ...`, `alert http: ...`) stays unchanged, because
  people and tests read it. `IAlertCommandServer` is shared with the named pipe server: out of scope.

## Tasks

- [x] F1 `FileDesktopTrace`: bounded retry on a sharing violation. RED first: a test that holds the
  file open with `FileShare.Read`, releases it after ~10 ms on another thread, and expects the line
  on disk. Plus a test that a permanent hold still never throws and gives up within the bound.
- [x] F2 `MtaActionThread`: trace the exception type from a failed posted work item; the loop keeps
  serving. RED first.
- [x] F3 Rename `HttpAlertCommandServer` to `LocalHttpCommandServer` (maintainer's choice, 2026-09-25). Pure refactor:
  the suites are the guard.

## Constraints

- Everything stays local: no push, no gh.
- Strict TDD (runner `dotnet test`). Tests go with each work unit.
- No absolute paths in any trace line.

## Progress

- Branch `fix/video-endpoint-followups` from local main `f03b7f4`. Baseline: Layout 198, Alert 13,
  Interop 387/42 skipped, App 991/6 skipped; build has only the 3 pre-existing warnings.
- F1 done, commits `53cbf50` (writer) and `887dbd8` (parent correction). The writer retried 5
  times with 5 ms sleeps; its RED was real (`Assert.Single` empty), but its test released the file
  after 10 ms. The parent measured the real case: `File.ReadAllLines` on the 9 MB trace holds it
  22-47 ms, and 4 x `Sleep(5)` took 66 ms at the default timer resolution but is only ~20 ms once
  Media Foundation (the playing wallpaper) raises it to 1 ms. So the count-based retry would miss
  exactly the case it was built for, while the wallpaper plays. Fix: retry until 150 ms have
  elapsed (Stopwatch), with the window injectable (`retryWindow`). Parent RED first: a 200 ms hold
  inside a 2 s window, with the parameter present but unused, failed (`Assert.Single` empty), then
  GREEN. The writer's 10 ms test passed 30/30 runs in a loop.
- F2 done, commit `54cb089`. `MtaActionThread` is now `internal`, and its constructor takes
  `onWorkFailed`. The `Run` catch reports `exception.GetType().Name` only. Wired at construction:
  `video-wallpaper-thread work-failed error=<Type>` into the desktop trace (already built a few
  lines earlier). RED `CS0122` (inaccessible), then a test: an `IOException` whose message holds a
  path; the sink gets the type, never the path, and the next work item still runs.
- F3 done, commit `babf720`. `git mv` of the class and its tests to `LocalHttpCommandServer`; the
  factory seam is now `createLocalHttpCommandServer`. Trace strings and `IAlertCommandServer` are
  unchanged. `grep HttpAlertCommandServer --include=*.cs` finds 0 matches.
- Checks after all three: build clean (3 pre-existing warnings); `dotnet test CosmicWin.sln` Layout
  198, Alert 13, Interop 387/42 skipped, App 999/6 skipped.
- Review (2026-09-25): assess (base `f03b7f4`) medium, 4278 lines (inflated: the F3 `git mv` counts
  as delete + add; about 300 real lines). Maintainer granted. Lens review-reliability, lineage
  `review-a7b989665b0ed223`: APPROVED, acknowledged, authority burned. The reviewed boundary is
  now `5155350`. Both findings were fixed with maintainer approval:
  - R3-001 (WARNING): the retry sleeps while holding `_gate`, and `Record` runs on chord, layout
    and alert paths (53 call sites). A LONG hold would stall every line by up to 150 ms. Fix
    `362efe1`: a breaker. When a line uses up its window, later lines fail at once until a write
    succeeds again. RED: the second line under a permanent hold waited the full window. A
    breaker-reset test (a later short hold is ridden out again) passed first; its mutation check
    (drop the reset) compiled and failed, then was reverted.
  - R3-002 (SUGGESTION): a throwing `onWorkFailed` sink escaped `Run`. Fix `a896b98`: the sink call
    is guarded. RED: the test host process CRASHED (`Unhandled exception ...
    InvalidOperationException: sink failed`), then GREEN.
  - Checks: build clean (3 pre-existing warnings); `dotnet test CosmicWin.sln` Layout 198, Alert
    13, Interop 387/42 skipped, App 1002/6 skipped.
- FEATURE COMPLETE on `fix/video-endpoint-followups`. Not merged, not pushed.
