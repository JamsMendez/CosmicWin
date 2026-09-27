# HTML wallpaper demo

## Objective

A demo (branch `demo/html-wallpaper`, not for merge unless the maintainer decides) where the
wallpaper is an animated HTML scene instead of a video, and the alert layer lives in the SAME page,
so what shows through the FAILED/WARNING letters is the real scene, not a copy.

## Problem and why

Today the alert layer is a separate WebView2 page composed above the video. It cannot see the video,
so any "see-through" effect is fake (the fake band copy was removed in remove-fake-letter-bands).
The original scene pages drew the scene and the alert on one canvas and one clock; there the effect
is real by construction. The maintainer wants to see that for real on the desktop.

## Source material

`docs/great-sage/` (reference only, NEVER tracked -- excluded locally in `.git/info/exclude`; it is
its own git repo). Four scenes: `backgroud-processing` (sic), `background-explorer`,
`background-idle`, `background-raphael`. Modular pages (index.html + js/*.js, node tests).
Scenes used by the demo are COPIED (without `.git`) into the app.

## Decisions (maintainer, 2026-09-26)

- Every scene gets the alert overlay, and each scene has its own layer seen through the letters:
  - processing: the folding bands (already real in the source: same geometry, same progress).
  - explorer: the rising sparks (`drawRisingSparks(context, timeSeconds)`, seeded, deterministic).
  - raphael: the golden glyph ring (`drawGlyphRing(context, ...)`); its fake bands go away.
  - idle: the constellations of the first ring (`RING_ANIMATIONS[0]`, already cached in a canvas
    that is only rotated per frame).
- Technique for all four: draw that layer into the offscreen letters layer, clip with
  `destination-in`. Per-kind color (blue failed, violet warning) assumed to stay; confirm on hardware.

## Constraints

- Strict TDD, source: project convention, runner `dotnet test` (node harnesses run through the App
  test project). Mutation-check any test that passes on its first run. Spikes are exploratory and
  are not committed as production code.
- Everything stays local: no push, no PR.
- Delivery strategy: ask-on-risk. Forecast: well over 400 authored lines (4 scene ports).
  Chain strategy chosen by the maintainer 2026-09-26: `feature-branch-chain`. Tracker branch =
  `demo/html-wallpaper` (never merged into main unless the maintainer decides); each task is a
  child branch `demo/html-wallpaper-<task>` cut from the previous one and fast-forwarded into the
  tracker when done. Local only: no PRs, no push.
- Slices: D2 -> `demo/html-wallpaper-d2-processing-page`; D2b -> `demo/html-wallpaper-d2b-hardening`; D3 -> `demo/html-wallpaper-d3-switch`; D6a -> `demo/html-wallpaper-d6a-shared-overlay-explorer`; D6b -> `demo/html-wallpaper-d6b-raphael`.
- Coverage caveat: ring-shaped layers (bands, constellations) sit around the screen center, so on
  3392x1440 only the middle letters cross them, and off-center mosaic tiles may show none.

## Tasks

- [x] D1 - SPIKE: can the wallpaper host exist and show the WebView2 overlay visual with NO video
      playing? Answer from code first, then on hardware. Decides how D3 is wired.
- [x] D2 - processing scene page in the app (scene + alert in one loop, real bands), driven by the
      existing hash/postMessage alert API.
- [x] D2b - Fix the two D3-relevant D2 review WARNINGs (authorized by the maintainer 2026-09-26):
      render loop fault isolation (a throwing frame must not freeze the wallpaper) and a harness
      test that delivers real WebView2 show/hide messages. Route: delegated writer.
- [x] D3 - Demo switch (`wallpaper-mode = html`): no video playback, the page stays visible
      permanently, alerts only toggle the overlay; shake moves into the canvas.
      Route: delegated writer (several non-trivial C# files: settings, AppComposition, controller).
- [x] D4 - Measure GPU/CPU of the scene full screen vs the video wallpaper.
- [x] D5 - Hardware check with the maintainer watching (failed, warning, mixed mosaic).
- [ ] D6 - explorer, raphael, idle scenes with their own see-through layer + scene selection.
      Sliced 2026-09-26 (each ~2000 lines of ported scene code):
  - [x] D6a - Extract the alert overlay into a shared module with a per-scene "see-through" hook
        (processing keeps its bands, suites stay green) + explorer scene (rising sparks).
        Route: delegated writer.
  - [ ] D6b - raphael scene (golden glyph ring; its fake bands are dropped).
  - [ ] D6c - idle scene (first-ring constellations).
  - [ ] D6d - Scene selection wiring + fps cap + hardware check of all four. DECIDED by the
        maintainer 2026-09-26: settings.conf, not the tray -- `wallpaper-scene = processing |
        explorer | idle | raphael` and `wallpaper-fps = 30 | 60` for the html wallpaper (the fps
        cap is the first D4 cost lever). Defaults to decide in D6d (proposal: processing, 60 =
        today's behavior).

## Open questions

- Pause the scene while the desktop is covered? (fps cap decided: `wallpaper-fps`, D6d.)

## Progress

- 2026-09-26: branch `demo/html-wallpaper` created from main 9d1e638. `docs/great-sage/` excluded
  locally. Feature document created.

- 2026-09-26: D1 answered from code (delegated read-only explorer; key claims re-checked by the
  parent). YES, no video is needed:
  - `Win32VideoWallpaperHost` and `WebViewAlertLayerController` are built unconditionally in
    `WireProduction`; only startup `ActivateVideoWallpaper` (AppComposition.cs ~1797) is gated on a
    configured video path, so with no video the host is never attached.
  - The DComp tree needs a swapchain (`RebuildCompositionTargetUnlocked`, host ~1165), but
    `TryAttach()` alone creates it (D3D device + composition swapchain + one black test-pattern
    Present), with no player involved.
  - The overlay is a sibling visual above the swapchain visual (`AddCompositionOverlayVisual`,
    host ~290); it never reads swapchain content, so it renders over the static black.
  - The alert controller only polls `IsCompositionReady` + hwnd/generation; visibility is its own
    `IsVisible` (hidden after create, shown on Start, hidden on End and on the page's "done").
  - Explorer restart: the host's own WndProc calls `TryAttach()` on TaskbarCreated (not video
    gated); the app-level keep-alive re-raise (AppComposition ~1750) IS gated on
    `videoWallpaperActive` and must be relaxed for html mode.
  - Shake: `Shake()` only records state consumed by the MF frame tick, so with no player it is a
    silent no-op; the shake moves into the page.
  Wiring chosen for D3: host-only activation -- in html mode, call `TryAttach()` on the pumped
  video wallpaper thread without `TryPlay`; keep the controller visible permanently. Hardware
  confirmation of "no video" folds into D3/D5.

- 2026-09-26: D2 done on `demo/html-wallpaper-d2-processing-page` (delegated writer, route:
  writer trigger, many non-trivial files). Commits 0f77d90 (verbatim scene sources, 1851 lines,
  parent-checked: only a header comment differs from docs/great-sage) and 6ad4606 (page, alert
  overlay with the alert-layer.js contract, csproj Content, node harness + runner; 1408 lines).
  Page: `CosmicWin.App/Wallpaper/Web/processing/`. invariants.js dropped (it inspects source text
  the tile rewrite changes). Canvas shake restored (FAILURE_SHAKE_MS 230).
  TDD DEVIATION (honest): tests were written after the implementation, so no RED was observed
  before code. Compensated by a mutation check per test: all 6 went RED on a targeted break and
  GREEN on revert. `dotnet build`: 0 errors. `dotnet test CosmicWin.App.Tests`: 1095 passed,
  6 skipped, 0 failed. Parent spot check: ProcessingSceneNodeTests re-run, passed.
  Review: assess high (process boundary in the node runner), consent granted, 4 lenses, APPROVED
  and acknowledged (review-e05452a05004ec76, authority burned). Non-blocking findings:
  - WARNING R4-render-loop-no-fault-isolation: one throwing frame stops scheduleFrame -> the whole
    wallpaper freezes for good. Matters for D3 (the page IS the wallpaper).
  - WARNING R3-host-message-path-unproved: the harness never delivers a WebView2 message, so the
    show/hide message contract D3 relies on is untested.
  - WARNING R4-stale-shake-transform-on-reshow; SUGGESTION R3-shake-outlives-done (shake cleanup).
  - WARNING R3-band-scale-test-vacuous (band test uses one full-screen tile only).
  - WARNING R2-misleading-scene-center-param; R2-stale-tuning-comments (ported config comments).
  - SUGGESTION R3-message-tiles-not-capped; R2-dead-invariant-helpers; R2-dangling-check-reference.

- 2026-09-26: D2b done on `demo/html-wallpaper-d2b-hardening` (delegated writer, two rounds).
  - b18378b fix(wallpaper): keep the scene loop alive when a frame throws. RED observed first
    (4 new cases failed: the error escaped render(), no reschedule), GREEN 10/10.
  - 7327cf7 test(wallpaper): deliver real host messages in the scene harness. RED observed
    (0 listeners registered). Shapes checked against WebViewAlertLayerController PostShow
    (`{type:"show",tiles,columns,rows,gap,workArea,duration}`) and End (`{type:"hide"}`); the
    legacy kind form is no longer sent by the controller.
  - Review review-551b5ebf7dcea236 (4 lenses) APPROVED + acknowledged; three lenses flagged the
    same WARNING (single-slot error dedup still floods when scene and overlay both throw), fixed:
  - 62d6587 fix(wallpaper): report each failing render stage once (per-stage dedup map). RED
    observed (8 !== 2). Overlay-only throw case passed first run, mutation-checked (RED without
    the reset, reverted). Review review-9c6d27a99fafd66c (4 lenses) APPROVED + acknowledged,
    SUGGESTIONs only (long dedup comment, test comment pointer, two test-strength notes).
  `dotnet build`: 0 errors. `dotnet test CosmicWin.App.Tests`: 1095 passed, 6 skipped, 0 failed.
  Parent spot checks: ProcessingSceneNodeTests re-run after each round, passed.
  Tracker `demo/html-wallpaper` fast-forwarded to the D2b tip.

- 2026-09-26: D3 done on `demo/html-wallpaper-d3-switch` (delegated writer).
  - d4c5a10 feat(settings): add the demo wallpaper-mode setting (`wallpaper-mode = video|html`,
    default video, unknown keeps default). RED: compile failure before the type existed.
  - 78e90d3 feat(alerts): keep the scene page visible permanently in html mode (pure
    `WebViewAlertLayerVisibility` policy; html mode maps `cosmicwin-scene.example` to
    `Wallpaper/Web/processing`). RED: compile failure first.
  - 53723a0 feat(wallpaper): attach the host without video in html mode (`AttachHtmlWallpaper`,
    TryAttach only, trace `video-wallpaper phase=startup mode=html attached=<bool>`; keep-alive
    runs on video OR html). Mutation-checked the three new boolean decisions.
  - Writer decision, accepted by the parent: `desktopVisible` in UpdateAlertOverlay
    (AppComposition ~730) is now `videoWallpaperActive || htmlWallpaperActive`; without it no alert
    could ever show in html mode. AlertQueue and the fullscreen detector are unchanged.
  `dotnet build`: 0 errors. App tests 1121 passed / 6 skipped / 0 failed; Interop 384 passed /
  42 skipped / 0 failed. Parent spot check: 156 focused tests passed.
  Review (assess medium, slice budget reached, base 62d6587): consent granted, 1 lens
  (reliability), APPROVED + acknowledged (review-4d021e8aa82c22c5). Findings:
  - WARNING R3-html-mode-video-switch-not-guarded: tray pick and HTTP video switch still start a
    player in html mode (known limitation, out of D3 scope), and a comment claims the modes never
    mix.
  - SUGGESTION R3-html-mode-without-alerts-layer: html mode with alerts disabled attaches the host
    but creates no WebView2 -> black desktop.

- 2026-09-26: D5 done. Release build of a4b8269 (tracker tip) launched from bin\Release (PID 29408,
  elevated shell) with `wallpaper-mode = html` appended to settings.conf (backup of the previous
  file: %TEMP%\settings.conf.before-html-demo; video-wallpaper-path is still configured and was
  correctly NOT played). Trace: `video-wallpaper phase=startup mode=html attached=True`, page
  ready in ~0.55 s. Sent via CosmicWinAlert.exe: `failed:1 duration:6`, `warning:1 duration:6`,
  `failed:2 warning:2 duration:8` -> grids 1x1, 1x1, 2x2, each ended on time, no alert-layer
  error. The maintainer watched and confirmed: the scene is the wallpaper and stays after each
  alert; real bands through the letters, blue for failed and violet for warning; the 4-tile mosaic
  looked right. Note: the mosaic ended by the queue's own hide at 8.0 s with no page `done` line;
  the maintainer switched virtual desktops during it. Not investigated, no visible effect.

- 2026-09-26: D4 measured (Release a4b8269-based build, 3392x1440, 16 cores, no alert showing,
  script in the session scratchpad: Get-Counter `GPU Engine(*)` per pid over the CosmicWin.App
  process tree incl. its msedgewebview2 children, CPU from TotalProcessorTime deltas, 20 samples).
  The first two html runs were contaminated by external HTTP video switches (the maintainer's own
  tooling; the known D3 limitation started a player under the page) and were discarded; the clean
  runs had no `video-wallpaper phase=http` line in their window.

  | Mode | CPU (tree) | GPU 3D (tree) | GPU video decode | System 3D | Working set |
  |---|---|---|---|---|---|
  | html (processing scene) | 1.28 cores (8.0 % of machine) | 29.1 % | 0 % | 33.4 % avg, 36.4 % max | 1055 MB |
  | video (MF hardware decode) | 0.07 cores (0.4 %) | 0.6 % | 30.9 % | 4.4 % avg | 567 MB |

  Reading: the scene costs ~19x the CPU and keeps ~30 % of the GPU's 3D engine busy all the time;
  the video uses the dedicated fixed-function decoder, which does not compete with 3D work (games,
  other GPU apps). The app was left in VIDEO mode after measuring (the `wallpaper-mode = html` line
  was removed; the app itself keeps a comment line for the key).
  Levers if the idea goes further (not decided): fps cap (30 would roughly halve it), pause the
  scene while the desktop is covered (detector exists), render at lower DPR, WebGL port.

- 2026-09-26: D6a done on `demo/html-wallpaper-d6a-shared-overlay-explorer` (delegated writer).
  - 5e1cdb6 refactor(wallpaper): share the alert overlay across scenes (+303/-183). Shared
    `Wallpaper/Web/shared/js/alert-overlay.js` + `render-loop.js` + font. Hook:
    `sceneSeeThroughLayer(g, sceneW, sceneH, sceneTime)`; the overlay translates by the tile origin,
    recolors with source-in (kind color) and clips with destination-in. C#: the scene host now maps
    the `Wallpaper/Web` root and navigates to `/<HtmlWallpaperSceneName>/index.html`
    (const "processing", the single place D6d turns into a setting).
    Processing harness 14/14 green on the extracted code; mutation-checked (removing the hook call
    turned the band test red).
  - 9fd43e7 chore(wallpaper): import the explorer scene sources (2467 lines, verbatim; parent
    re-diffed 5 files against docs/great-sage: no source line lost).
  - c1e011a feat(wallpaper): add the explorer scene page (+629/-110). Sparks hook forwards to
    `drawRisingSparks(g, sceneTimeSeconds)`. RED first (page did not exist: ENOENT/ReferenceError),
    then GREEN; the key test (same time as the scene, offset by a non-origin tile) mutation-checked
    on both assertions. Harness mock gained createImageData/putImageData (earth.js bakes noise at
    load); that bake makes the explorer harness ~23 s, timeout raised to 90 s.
  `dotnet build`: 0 errors. App tests 1122 passed / 6 skipped / 0 failed. Parent spot check: both
  scene node tests passed (23 s).

- 2026-09-26: D6a review: the whole range (4832 lines) hit `lens_context_budget_exceeded` (no
  authority created). Split into three candidates, each reviewed from a temporary detached
  worktree under `../CosmicWin-worktrees/` (removed afterwards): 53723a0..5e1cdb6
  (review-2d413b075b2c1d9a), 5e1cdb6..9fd43e7 (review-8051b558acd73565), 9fd43e7..17fcf8c
  (review-e3aa3796a2b86428). All consent granted, all APPROVED + acknowledged.
  Two lenses flagged a real WARNING: explorer's `alertSceneMs` ran outside the per-stage
  try/catch (a throw would freeze the wallpaper). Fixed in ac5a70c (explorer clock inside the
  guard; shared `drawSeeThroughIntersections` restores in finally; the fault-isolation test now
  throws every frame). RED observed first (uncaught error; save 1 / restore 0). Review
  review-291d897d3ccb3304 (4 lenses) APPROVED + acknowledged, SUGGESTIONs only.
  Other open, non-blocking D6a findings: comment accuracy in shared/alert-overlay.js header
  (explorer pointer, W/H restore attribution), render-loop/main.js circular pointer, stale
  drawFailureBandIntersections name in processing config.js, recolor composite unasserted,
  mapping checked only by source text, no page-load smoke test, RunNode duplicated in 3 runners.
  `dotnet test CosmicWin.App.Tests`: 1122 passed / 6 skipped / 0 failed.

## Next step

D6b (raphael), D6c (idle), D6d (settings + fps + hardware).
