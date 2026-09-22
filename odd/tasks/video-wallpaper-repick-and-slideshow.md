# Video wallpaper: re-pick failure and slideshow disappearance

## Objective

Two defects reported by the maintainer on 2026-09-22, after the v1 video wallpaper shipped
(`odd/tasks/video-wallpaper.md`):

1. Re-picking a video from the tray while one is already playing fails with a Windows
   "file in use by another process" error, and the new video never starts.
2. With the Windows wallpaper slideshow enabled, the video disappears the first time the
   slideshow changes the background image.

## Problem and evidence

- **Bug 1 (confirmed from code + trace).** `setVideoWallpaperPath` in
  `CosmicWin.App/AppComposition.cs` calls `importVideoWallpaper(path)` (a `File.Copy` onto the
  FIXED destination `%LOCALAPPDATA%\CosmicWin\video-wallpaper.mp4`) on the tray thread BEFORE
  playback is stopped. Media Foundation still holds that destination open, so the copy throws a
  sharing violation. `desktop-trace.log` shows `phase=startup ... tryPlay=True` at 18:13:00Z and
  no `phase=pick` line afterwards: the import threw before activation. Also: the import copies
  the whole file (3.3 GB here) on the tray thread, and picking the imported file itself would
  make `File.Copy` copy a file onto itself.
- **Bug 2 (hypothesis, NOT verified).** Only `TaskbarCreated` re-attaches the host window.
  A slideshow wallpaper change makes Explorer re-render its wallpaper layer, which on this
  raised-desktop layout (Win11 26200, host parented to Progman) plausibly lands above or
  replaces our child window. Needs a live repro with a window-tree snapshot before/after.
  `SetLoop(true)` is already set, so looping is not the cause.

## Scope

In scope: bug 1 fix; bug 2 investigation and, once the cause is proven, its fix.
Out of scope: anything else from the v1 out-of-scope list.

## Constraints

- `CosmicWin.Interop` is the only project touching Win32.
- Picking a video must never leave the wallpaper dead: if the import fails, the previous video
  resumes.

## TDD mode

**Strict TDD: enabled** — source: user's global instructions. Runner:
`dotnet test <Project>.Tests/<Project>.Tests.csproj` per project.

## Delivery

Strategy: `ask-on-risk` (default). Forecast: well under 400 authored lines.
RDD: on (global).

## Tasks

- [x] **T1 — Stop playback before importing on re-pick.** Route: delegated direct (writer
  trigger: interface + MF player + 2 fakes + composition + tests). Add `IVideoWallpaperPlayer.Stop()`
  (idempotent, never throws, releases the file); run the whole pick on the video-wallpaper
  thread: stop → import → persist → activate; on import failure, trace it and resume the
  previous video. `VideoWallpaperImport.Import` skips the copy when source == destination.
  **Done** — commits `c9ffd62` (writer) and `2cd2720` (parent: the fallback was read on the
  tray thread at click time, so a second pick queued behind a first-ever one had null to fall
  back to; RED `TwoQueuedPicks_WhenTheSecondImportThrows_RestoresTheVideoTheFirstPickLanded`
  observed failing, then GREEN). TDD RED observed for Stop (CS0535), self-copy (IOException),
  ordering and import-failure tests. Checks: `dotnet build CosmicWin.sln` OK;
  `dotnet test CosmicWin.App.Tests` 757 passed / 6 skipped / 0 failed;
  `dotnet test CosmicWin.Interop.Tests` 167 passed / 31 skipped / 0 failed (writer run).
  Review assess (base main, committed-only): medium, `under_budget` (351 lines) — pending in slice.
  Known residual: a copy that fails midway leaves a partial destination, which the restore then
  replays.
- [x] **T2 — Reproduce and diagnose the slideshow disappearance.** Route: inline, live on
  hardware. Capture the Progman/WorkerW/host tree before and after a slideshow change.
  **Done — cause proven 2026-09-22 19:28Z** (app launched via `scripts/run.ps1`, elevated shell,
  slideshow advanced with `IDesktopWallpaper::AdvanceSlideshow`, tree sampled every 150 ms):
  before: Progman children = DefView, **host**, WorkerW(0xD20990, wallpaper). At t+300ms Explorer
  creates a NEW wallpaper WorkerW (0x32096E) and inserts it directly after DefView, i.e. ABOVE
  the host; at t+900ms the old WorkerW is destroyed. Final order: DefView, WorkerW(new), host ->
  the host is covered by the new wallpaper layer. Nothing re-raises it: `AttachToDesktop`
  returns early when the parent already matches, so even a re-attach skips the z-order step.
- [x] **T3 — Keep the host directly below DefView.** Route: delegated direct (writer trigger:
  Interop host + composition + tests). `AttachToDesktop`'s early return must also verify the host
  sits directly after DefView and re-apply the z-order when it does not; the existing 400 ms
  watch tick re-runs `TryAttach` on the video thread while a video is active. Accepted cost: up
  to one tick of the static wallpaper showing after a slideshow change.
  **Done** — commit `b26769a`. RED observed (App: 2 keep-alive tests `Expected 1, Actual 0`;
  desktop: host handle not after DefView), then GREEN. Checks: `dotnet build CosmicWin.sln` OK;
  App.Tests 761 passed / 6 skipped / 0 failed; Interop.Tests 167 / 32 skipped / 0 failed;
  `COSMICWIN_RUN_DESKTOP_TESTS=1` real-attach tests 3/3 passed. Live E2E 19:42Z (parent):
  slideshow advanced, new WorkerW covered the host at t+300ms, host back directly after DefView
  at t+450ms and stayed there after the old WorkerW was destroyed.

## Progress

- 2026-09-22: branch `fix/video-wallpaper-repick-and-slideshow` created; doc written.

## Review

RDD: medium, `slice_budget_reached` (808 lines, main..`372d77c`+T3 doc). Consent granted by the
maintainer; lens `review-reliability`; **approved**, acknowledged, authority burned (lineage
`review-bef9cebedab5988f`). Non-blocking follow-ups (not in this slice):

- WARNING `R3-stale-active-after-failed-pick`: an import failure with no previous path leaves
  `videoWallpaperActive` true after `Stop()`, so the tick keeps posting TryAttach for nothing.
- WARNING `R3-restore-replays-partial-copy`: copy to a temp file then rename, so a copy that
  fails midway cannot damage the previous import.
- SUGGESTION `R3-stop-release-unproved`: no desktop test proves `Stop()` releases the file.
- SUGGESTION `R3-realattach-assumes-raised-layout`: the new real-attach test fails instead of
  skipping on the legacy WorkerW layout.
- SUGGESTION `R3-keepalive-flag-cross-thread`: the keep-alive flags cross threads without
  volatile/Interlocked.

## Next step

Delivery under ordinary policy (the maintainer pushes). Then, if wanted, the follow-ups above.

Engram mirror: PENDING (mem_save failed: multiple active runtime sessions).
