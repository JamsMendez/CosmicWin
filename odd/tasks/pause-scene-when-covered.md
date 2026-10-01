# Pause the html wallpaper scene while the desktop is covered

## Objective

In html wallpaper mode, stop drawing the scene while a fullscreen window covers the primary monitor,
and resume as soon as it no longer does.

## Problem and why

The html scene keeps rendering at its fps cap (default 60) while nobody can see it: measured in
html-wallpaper-demo D6d at ~4-6 W and 0.9-1.3 CPU cores over video mode. Decided by the maintainer
2026-10-01: PAUSE completely (not a low fps) while covered.

## Definition of "covered" (reuse, do not redefine)

`CosmicWin.Interop/Win32/PrimaryMonitorFullscreenDetector.cs`, the same signal that already holds
alerts (`AlertQueue`): the FOREGROUND window has no caption, is not maximized, and covers the
primary monitor (1 px tolerance); the shell, click-through overlays and CosmicWin's own hosts are
excluded. Maximized or tiled windows do NOT count (the wallpaper shows in the gaps).

## Scope

- html wallpaper mode only. The mini scene window (topmost, always visible) is NOT paused.
- Video mode untouched.
- Resume instantly on uncover; no visible jump beyond the scene continuing from where it is
  (or from current time -- whichever the render loop already does naturally after a gap; document it).
- Alerts: an alert is never started while covered (queue holds it). An alert already showing when
  the desktop becomes covered must not break: host-side timing stays authoritative.
- Trace a line on each transition (e.g. `wallpaper-scene paused: desktop covered` / `resumed`), only
  on change, so hardware runs can see it.

## Constraints

- TDD strict. Runner: `dotnet test CosmicWin.App.Tests/CosmicWin.App.Tests.csproj` (scene Node
  harnesses run through it; `node <harness> <scene dir>` for focused runs).
- Reuse existing seams: the covered-desktop detector, the existing host->page web message channel
  of the html layer, the existing reconcile tick (or the alert queue's covered polling) for polling.
- Each task one work-unit commit, Conventional Commits, no AI attribution.

## Tasks

- [x] T1 -- Page side: a pause/resume web message handled by the shared render loop (all four
  scenes); while paused no frame is drawn and no rAF work beyond the minimum; on resume drawing
  restarts. Node harness coverage.
- [x] T2 -- Host side: poll the covered state in html mode, send pause/resume only on change,
  never in mini/video mode, trace transitions. Wiring tests with a fake detector.
- [x] T2b -- Review follow-up (review-9fa143cab060eddf): apply-then-commit, failed sends retried.
- [ ] T3 -- Hardware: fullscreen browser/video over the desktop -> trace `paused`, CPU/GPU drop;
  leave fullscreen -> `resumed`, scene animates. Maximized window -> no pause.

Route: T1+T2 delegated direct (one writer; mapping + 2+ non-trivial files). T3 parent, on hardware.

## Progress

- 2026-10-01: branch `feat/pause-scene-when-covered` off main a4ee8f5.
- 2026-10-01 T1 (page side) done. Host->page messages: `{type:"pause"}` / `{type:"resume"}`, handled in
  `shared/js/alert-overlay.js` handleHostMessage -> `setWallpaperPaused` in `shared/js/render-loop.js`
  (all four scenes schedule through the shared `scheduleFrame`). Paused: `scheduleFrame` arms NO rAF and holds
  the callbacks; a frame already in flight when the pause arrives draws nothing and holds its callback;
  resume re-arms each held callback once and resets the fps schedule (first resumed frame draws at once).
  Scenes derive time from the rAF timestamp, so after a gap they continue at the CURRENT time (no catch-up,
  no rewind). Mini variant ignores both messages. `hide` is a message handler, so it still works while paused.
  Shared checks: `CosmicWin.App.Tests/Wallpaper/Web/pause-resume.checks.js`, used by all four harnesses.
  RED: processing/raphael harness "pause/resume host messages ..." failed with "expected a paused page to
  draw no frame and arm no further requestAnimationFrame". GREEN after the change (processing 44/44,
  raphael 26/26, idle 21/21, explorer 22/22). Mini test pre-existed green, proven by mutation: removing
  `if (isMiniVariant) return;` failed it ("expected the mini variant to keep drawing after a pause message").
  Judgment call: an alert showing at pause time is not special-cased (page frozen, host timing authoritative).
- 2026-10-01 T2 (host side) done. `AppComposition.Wire` gains `setHtmlWallpaperScenePaused: Action<bool>?`
  (production: `WebViewAlertLayerController.SetScenePaused`). `UpdateHtmlScenePause()` runs on the existing watch
  tick (right before `UpdateAlertOverlay`), reads the same `isPrimaryMonitorCovered` that holds alerts, and acts
  only when `wallpaperMode == Html`, the host is attached (`htmlWallpaperActive`) and the state CHANGED.
  Messages: `AlertLayerMessages.Pause` = `{"type":"pause"}`, `Resume` = `{"type":"resume"}`. Trace lines (on
  change only): `wallpaper-scene paused: desktop covered`, `wallpaper-scene resumed`; failures:
  `wallpaper-scene cover-check-failed ...`, `wallpaper-scene pause-failed ...`.
  Controller keeps the desired state (`_scenePaused`) and re-posts it from `TryMarkReady`, so a page that is
  recreated (Explorer restart, process failure) or re-navigated (scene switch) while covered is paused again.
  Exclusions: video mode and mini mode have `htmlWallpaperActive == false` (mini builds no alert layer and no
  wallpaper host; also the explicit `wallpaperMode != Html` guard); the mini window is never sent these messages
  and its page also ignores them.
  Alert rule chosen: the queue never starts an alert while covered; one already showing is NOT special-cased.
  Pause freezes the page, host timing stays authoritative (queue ends it with `hide`, which the page processes
  as a message), a short cover resumes with correct rAF-based alert timing.
  RED: with the `UpdateHtmlScenePause()` call disabled, 3 HtmlScenePauseWiringTests failed on assertions
  (pause/resume/pause-again). GREEN: all pass. Mutation: dropping `!htmlWallpaperActive.Value` fails
  `WhenTheHostNeverAttaches_...`; dropping the `wallpaperMode != Html` guard does NOT fail a test (redundant
  with htmlWallpaperActive; kept as an explicit second lock).
  Suites: App tests 1390 -> 1399 passed (6 skipped unchanged); `dotnet build CosmicWin.sln` 0 errors, only the
  pre-existing CA2022 warning in CosmicWinAlert.Tests/ProgramTests.cs.
- 2026-10-01 T2b (apply-then-commit) done. Fixes WARNINGs R2-trace-before-effect, R3-composition-state-before-send,
  R3-post-failure-not-retried; covers SUGGESTION R3-failure-paths-untested.
  `UpdateHtmlScenePause` calls the setter FIRST; only on success does it store `htmlScenePaused` and trace
  `wallpaper-scene paused: desktop covered` / `wallpaper-scene resumed`. A recoverable setter failure leaves the
  state unchanged (next 400 ms tick retries) and traces `wallpaper-scene pause-failed <Type>: <message>`.
  Rate-limit rule: pause-failed is traced only for the FIRST failure of a streak (`htmlScenePauseFailing`); a
  success clears it, so a later streak traces again. Detector failure keeps `wallpaper-scene cover-check-failed`
  (state kept, no transition; unchanged, not rate-limited).
  Controller: `SetScenePaused` now stores the desired `_scenePaused` unconditionally and posts when ready and
  `_scenePostedPaused != _scenePaused`; `PostScenePause` no longer swallows (the composition traces and retries),
  and sets `_scenePostedPaused` only after the post succeeded. `TryMarkReady` resets `_scenePostedPaused = false`
  (fresh page runs) and re-posts the desired state, tracing `post-scene-pause` errors itself.
  RED (before fix): 3 new wiring tests failed (resume/pause failed once traced as done; persistent failure
  traced `paused`). GREEN after. Controller test is structural only (WebView2 is not runnable in tests).

