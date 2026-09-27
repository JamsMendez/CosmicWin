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
- Slices: D2 -> `demo/html-wallpaper-d2-processing-page`; D2b -> `demo/html-wallpaper-d2b-hardening`; D3 -> `demo/html-wallpaper-d3-switch`; D6a -> `demo/html-wallpaper-d6a-shared-overlay-explorer`; D6b -> `demo/html-wallpaper-d6b-raphael`; D6c -> `demo/html-wallpaper-d6c-idle`; D6d -> `demo/html-wallpaper-d6d-settings`.
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
- [x] D6 - explorer, raphael, idle scenes with their own see-through layer + scene selection.
      Sliced 2026-09-26 (each ~2000 lines of ported scene code):
  - [x] D6a - Extract the alert overlay into a shared module with a per-scene "see-through" hook
        (processing keeps its bands, suites stay green) + explorer scene (rising sparks).
        Route: delegated writer.
  - [x] D6b - raphael scene (golden glyph ring; its fake bands are dropped).
  - [x] D6c - idle scene (first-ring constellations).
  - [x] D6d - Scene selection wiring + fps cap + hardware check of all four. DECIDED by the
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

- 2026-09-27: D6b done on `demo/html-wallpaper-d6b-raphael` (delegated writer).
  - e3d28b4 + 99c6053 chore(wallpaper): import the raphael scene sources (1504 + 1970 lines,
    split for the reviewer budget). Verbatim except config.js, which drops the single-overlay
    FAILURE_* state/constants (the shared overlay declares the same const names) and hardcodes
    viewZoom = 1 -- stated in the commit message; parent re-diffed every file.
    Left out: failure-overlay.js, invariants.js, test/, README, Google Fonts links.
  - 78da9ac feat(wallpaper): add the raphael scene page (771 authored). Seam in the copied
    layers.js: `goldGlyphRingDrawParams(progress)` (the real ring paints pre-baked sprites onto the
    global ctx, so the hook redraws with context-parameterized `drawGlyphRing`). RED first (page
    missing), GREEN 6/6, mutation checks on rotation speed, annulus, extra ring, rethrow, and the
    alert clock outside its try.
  `dotnet test CosmicWin.App.Tests`: 1123 passed / 6 skipped / 0 failed. Parent spot check:
  RaphaelSceneNodeTests passed.
  Reviews (one per commit, from temporary worktrees, removed): review-a59fc3b45536395a,
  review-0b030828d1fed2e9, review-acc4407391c8f2b2 -- all APPROVED + acknowledged.
  Open, non-blocking, worth a look on hardware (D6d):
  - WARNING R2-gold-ring-params-parallel-geometry + R3-rotation-parity-proved-against-hardcoded-
    replica: `goldGlyphRingDrawParams` keeps a second copy of the gold ring math instead of
    `drawGlyphRings` using it; if either changes, the see-through ring silently drifts. Fix idea:
    make the copied drawGlyphRings read the same params.
  - Writer-noted: the see-through glyphs are plain strokes, the on-screen ring uses a 3-pass
    outline font, so the silhouettes differ slightly.
  - WARNING R3-count-size-linewidth-center-unasserted; SUGGESTIONs: center fractions duplicated
    in hook and main.js, tile offset asserted with some(), harness script order hardcoded.
  - Copy-level: R3-untested-port / R3-untested-pure-geometry, header seam wording in the import
    commit, coreRadius discontinuity on tiny viewports, counter/glyph-pool guards.

- 2026-09-27: D6c done on `demo/html-wallpaper-d6c-idle` (delegated writer).
  - 07b8494 + f753385 chore(wallpaper): import the idle scene sources (1076 + 1235 lines,
    verbatim; parent re-diffed every js file: nothing lost).
  - 341be41 feat(wallpaper): add the idle scene page (+746/-106). Seam: the scene's ring loop
    records `constellationRingStamp = {cache, cx, cy, angle}` right after stamping the constellation
    ring (found by name, `CONSTELLATION_RING_INDEX`); the hook re-stamps that exact object -- one
    geometry calculation per frame, no parallel copy (the D6b lesson). RED first (page missing),
    GREEN 5/5; mutation checks: wrong ring index, hardcoded angle, recomputed center, extra ring (a
    first weak partition let it pass; the test was fixed to spy per tile and then caught it),
    alert clock outside its try, rethrow.
  `dotnet test CosmicWin.App.Tests`: 1124 passed / 6 skipped / 0 failed. Parent spot check:
  IdleSceneNodeTests passed (27 s).
  Reviews (one per commit, temporary worktrees, removed): review-aa128b6be346d24c,
  review-f1f0e418bdda4bd3, review-b5bdfd651a3cecc2 -- all APPROVED + acknowledged. Several copy
  WARNINGs are artifacts of per-commit slicing (missing script deps, loop not guarded yet) and are
  resolved by the page commit. Open, non-blocking: WARNING R2-constellation-index-silent-miss (a
  renamed ring would make the see-through vanish with no log; add a load-time assert), WARNING
  R3-canvasScale-untested (fractional DPR), earth.js copy notes (disc off-center by a texel,
  per-pixel allocation), stale stamp after a failed frame, harness timeouts near 90 s budget.

- 2026-09-27: D6d code done on `demo/html-wallpaper-d6d-settings` (delegated writer, two rounds).
  - 2d34e61 feat(settings): add the demo wallpaper-scene and wallpaper-fps settings (386 lines).
    `wallpaper-scene = processing|explorer|idle|raphael` (default processing),
    `wallpaper-fps = 30|60` (default 60; anything else keeps 60). Only `SceneFolderName(enum)` turns
    the setting into a folder literal; URL `https://cosmicwin-scene.example/<scene>/index.html?fps=N`.
    TDD DEVIATION (honest): the C# RED was retroactive (implemented first, then stashed to watch the
    tests fail against the original code). The JS side was RED first.
  - dc47db5 feat(wallpaper): cap the html wallpaper frame rate (271 lines): `scheduleFrame` moved
    into shared/js/render-loop.js for all four scenes; skipped frames reschedule and never invoke
    the callback.
  - Review review-73b3de6c7d02dce5 APPROVED + acknowledged, but three lenses flagged the 1-of-N
    pacing and the false "60 = today's uncapped behaviour" comments. Parent measured the display:
    RTX 4060 Ti at 3440x1440, 164 Hz -> so D4's html numbers were UNCAPPED at 164 fps, and the
    1-of-N cap gave ~54.7 fps for 60 and ~27.3 for 30.
  - a02140a fix(wallpaper): pace the html wallpaper to its fps on any refresh rate (target-time
    scheduler with resync after a full-interval lag). RED first: 5/33 (164 Hz 60 -> 274/300,
    75 Hz 60 -> 188/300, ...), GREEN 33/33, mutation-checked resync and interval. Review
    review-f00dbe7a07a144eb APPROVED + acknowledged; open: the epsilon constant's comment still describes the
    old rule (WARNING R2-001), resync comment understates the slow-display path, no test for a
    display slower than the cap.
  `dotnet build`: 0 errors. App 1163 passed / 6 skipped / 0 failed; Interop 384 / 42 / 0.
  Parent spot checks: 190 focused tests passed; processing harness re-run passed.

- 2026-09-27: D6d hardware done. Release build of c577891 (tracker tip), elevated shell; settings
  backup at %TEMP%\settings.conf.before-d6d.
  Cost (processing, no alert, clean windows -- no `phase=http` lines):
  | Run | CPU tree | GPU 3D tree (perf counter) | nvidia-smi power | GPU clock |
  |---|---|---|---|---|
  | html @ 60 fps cap | 1.30 cores | 30.0 % | 25.6 W | 1020 MHz |
  | html @ 30 fps cap | 0.90 cores | 28.0 % | 24.1 W | 680 MHz |
  | video mode | (D4: 0.07 cores) | (D4: 0.6 %) | 19.9 W | 668 MHz |
  Reading: the "% GPU" counter is relative to the CURRENT clock, so it overstated the cost in D4;
  in watts the scene adds ~4-6 W over the video, and 30 fps keeps the GPU at video-level clocks
  and cuts CPU ~30 %. The 60 fps cap costs about the same as D4's uncapped run, which suggests the
  WebView2 page was not actually rendering at 164 fps before (not investigated).
  Scenes: processing, explorer, raphael, idle each shown with `failed:1` and `warning:1`
  (6 s): every page ready, every alert shown and ended on time, no alert-layer error. The
  maintainer watched and confirmed all four see-through layers (bands, sparks, golden glyphs,
  constellations), blue for failed and violet for warning: "Todo bien".
  Note: raphael's warning ended by the queue's hide without a page `done` line (second time, the
  first was the D5 mosaic). No visible effect; not investigated.
  App left running in html mode, scene idle, 60 fps.

## Next step

Demo complete. Optional follow-ups: the open review findings above (raphael parallel gold-ring
geometry, idle silent ring-name miss, pacing comment), the missing `done` line, pausing the scene
while the desktop is covered, and the D3 limitation (HTTP/tray video switch starts a player under
the page). Merging anything to main is the maintainer's call.
