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

- [ ] **F1 — Clear the active flag on a failed pick** (`R3-stale-active-after-failed-pick`,
  WARNING). After `Stop()` the wallpaper is not playing; an import failure with no previous
  path must leave `videoWallpaperActive` false so the watch tick stops posting keep-alives.
- [ ] **F2 — Import through a temporary file** (`R3-restore-replays-partial-copy`, WARNING).
  Copy to a temp file beside the destination, then move it into place, so a copy that fails
  midway leaves the previous import intact.
- [ ] **F3 — Prove `Stop()` releases the file** (`R3-stop-release-unproved`, SUGGESTION).
  Desktop test: play a real tiny H.264 MP4 fixture, `Stop()`, then open it for exclusive write.
- [ ] **F4 — Real-attach test not applicable on the legacy layout**
  (`R3-realattach-assumes-raised-layout`, SUGGESTION). Skip instead of failing when DefView is
  not under the resolved host parent.
- [ ] **F5 — Keep-alive flags safe across threads** (`R3-keepalive-flag-cross-thread`,
  SUGGESTION). Volatile/Interlocked for the flags the tick and the video thread share.

Route for all: delegated direct, one writer (writer trigger: composition, import, interop tests).

## Progress

- 2026-09-22: branch `fix/video-wallpaper-review-followups` created; doc written.

## Next step

F1–F5 via one writer, one commit per task.
