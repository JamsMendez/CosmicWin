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
- [ ] T2 -- Host side: poll the covered state in html mode, send pause/resume only on change,
  never in mini/video mode, trace transitions. Wiring tests with a fake detector.
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
