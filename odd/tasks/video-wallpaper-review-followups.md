# Video wallpaper: review follow-ups

## Objective

Close the five non-blocking findings from the approved reliability review of
`odd/tasks/video-wallpaper-repick-and-slideshow.md` (lineage `review-bef9cebedab5988f`).

## Why

Maintainer request, 2026-09-22. Two are real behavioral gaps (WARNING), three are proof or
robustness gaps (SUGGESTION).

## Scope

In scope: exactly the five findings below. Out of scope: anything else in the video wallpaper.

## Constraints

- `CosmicWin.Interop` is the only project touching Win32.
- A failed pick must never leave the wallpaper dead, and must never damage the previous import.
- Desktop tests run only with `COSMICWIN_RUN_DESKTOP_TESTS=1`, with CosmicWin.App closed.

## TDD mode

**Strict TDD: enabled** — source: user's global instructions. Runner:
`dotnet test <Project>.Tests/<Project>.Tests.csproj` per project.

## Delivery

Strategy: `ask-on-risk` (default). Forecast: under 400 authored lines. RDD: on (global).

## Tasks

- [x] **F1 — Clear the active flag on a failed pick** (`R3-stale-active-after-failed-pick`,
  WARNING). After `Stop()` the wallpaper is not playing; an import failure with no previous
  path must leave `videoWallpaperActive` false so the watch tick stops posting keep-alives.
  **Done** `e9b3fb7`. RED not observable: the exact finding scenario is unreachable (a true
  active flag implies a non-null previous path, and `ActivateVideoWallpaper` recomputes the flag);
  the flag is now cleared right after `Stop()` as an invariant, with 2 tests for the reachable
  variants (restore also fails; first-ever pick fails).
- [x] **F2 — Import through a temporary file** (`R3-restore-replays-partial-copy`, WARNING).
  Copy to a temp file beside the destination, then move it into place, so a copy that fails
  midway leaves the previous import intact.
  **Done** `bf4ee27`. RED observed: locked destination made the final move fail, leaving partial
  state (`UnauthorizedAccessException`); GREEN after. Mid-transfer corruption (disk full, media
  ejected) cannot be forced in a unit test; Windows copy fails at open time for injectable faults.
- [x] **F3 — Prove `Stop()` releases the file** (`R3-stop-release-unproved`, SUGGESTION).
  Desktop test: play a real tiny H.264 MP4 fixture, `Stop()`, then open it for exclusive write.
  **Done** `066264f`. Fixture `CosmicWin.Interop.Tests/Fixtures/tiny-h264.mp4` (2.8 KB, ffmpeg).
  RED observed with `Stop()` sabotaged to a no-op (needs a 300 ms settle after TryPlay), GREEN
  restored.
- [x] **F4 — Real-attach test not applicable on the legacy layout**
  (`R3-realattach-assumes-raised-layout`, SUGGESTION). Skip instead of failing when DefView is
  not under the resolved host parent.
  **Done** `e1b144c`. `RequiresRaisedDesktopLayoutFact` + 7 `DesktopGateTests`. RED not
  observable on this raised-layout machine; the test still runs and passes here.
- [x] **F5 — Keep-alive flags safe across threads** (`R3-keepalive-flag-cross-thread`,
  SUGGESTION). Volatile/Interlocked for the flags the tick and the video thread share.
  **Done** `cce2839`. `VolatileFlag` holder (Volatile.Read/Write). No test: a visibility race is
  not deterministically testable.

Route for all: delegated direct, one writer (writer trigger: composition, import, interop tests).

## Progress

- 2026-09-22: branch `fix/video-wallpaper-review-followups` created; doc written.

## Verification

`dotnet build CosmicWin.sln` OK; App.Tests 766 passed / 6 skipped / 0 failed (writer + parent
rerun); Interop.Tests 171 / 33 skipped / 0 failed; desktop-enabled MF player + real-attach tests
11 / 0 skipped / 0 failed. Branch diff: 486 insertions, 14 deletions.

## Next step

RDD review of the slice, then local merge on the maintainer's word.
