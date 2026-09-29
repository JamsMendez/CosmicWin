# Mini scene window

## Objective
New wallpaper mode `mini`: instead of painting an HTML scene as the desktop wallpaper, CosmicWin shows a
small, always-on-top, square window with a transparent background in one of the 4 corners of the primary
monitor. It renders a reduced "mini" variant of the current scene, and the same HTTP scene request
(`POST /v1/wallpaper/scene`) switches it. A chord moves it between corners.

## Problem / why
The maintainer wants the scene as an ambient indicator on top of everything, not hidden behind windows as
a wallpaper. It also makes the Alacritty per-scene theme unnecessary (that branch is abandoned).

## Scope
- Setting `wallpaper-mode=mini` (third value next to `video` and `html`), and a new setting `mini-corner`
  (`top-left|top-right|bottom-left|bottom-right`, default `top-right`, changed from bottom-right by the maintainer on 2026-09-29).
- Window side = primary monitor height / 5 (1440 -> 288x288). It is anchored to the chosen corner of the
  work area, so it stays clear of the taskbar.
- Topmost, never takes focus, not in taskbar/Alt+Tab, click-through, transparent background.
- Mini scene variants (same pages, `?variant=mini`): no background, no alert overlay.
  - idle: the central ring (disc/rings) + chroma fan + planet CENTERED in the ring; no starfield/vignette/black fill.
  - explorer: the blue ring + sparks + chroma fan + planet CENTERED in the ring.
  - processing: only bands, wait, octagon, orbits, center, lines + the green nebula, radially faded so it
    never reaches the window edges (edge pixels fully transparent). No background, soft oval,
    chroma fans, stars, streaks, flares, grain or vignette.
  - raphael: only the circle, center and golden lines. Blue and gold glyphs read as vertical strokes; the gold
    character count is tripled (mini only, maintainer 2026-09-29).
- The HTTP scene route switches the mini window in mini mode and persists `wallpaper-scene`.
- Chord `Alt+M` cycles 8 positions clockwise (TL, top-center, TR, right-center, BR, bottom-center, BL, left-center)
  and persists `mini-corner` (the 4 midpoints were added by the maintainer on 2026-09-29).

## Constraints
- No wallpaper host and no video playback in mini mode. The desktop background stays as Windows has it.
- Alerts: the maintainer wants to SEE the alert overlay inside the mini window (2026-09-29), so the mini
  variant keeps `renderAlertOverlay()`. Legibility at 288x288 is judged from previews.
- `video` and `html` modes must behave exactly as before.
- Transparency needs a DirectComposition-hosted WebView2 on a no-redirection-bitmap popup, the same
  composition-controller path the html layer already uses. A windowed WebView2 cannot be see-through.

## Acceptance criteria
- Settings parse/serialize round-trip for `mini` and `mini-corner`. Unknown values fall back to the defaults.
- If the taskbar sits on the chosen edge (e.g. top), the window stays inside the work area, below/beside it. Tested.
- Placement math: side = floor(monitorHeight / 5), and each corner is flush with the work-area edges. Tested.
- Corner cycle order tested; chord `Alt+M` registered and dispatched even when tiling is off.
- Each scene in `?variant=mini` draws only the listed layers, and the canvas stays transparent (node tests).
- HTTP scene switch in mini mode returns 202 and switches the mini window; the scene is persisted.
- Hardware: the window stays above a maximized app, ignores clicks, moves with Alt+M, and switches scenes over HTTP.

## TDD
Mode: enabled (session config "Strict TDD Mode: enabled"). Runner: `dotnet test CosmicWin.sln`
(CI filter `Category!=RequiresDesktop`). Node scene harnesses run through the C# node tests.
Known base failure: `ExplorerSceneNodeTests.ExplorerSceneHarness_PassesAgainstTheRealShippedPage` (node
harness timeout on 67a0aae).

## Delivery
Branch `feat/mini-scene-window` from main 67a0aae. Forecast ~900 authored lines (over the ~400 budget).
Strategy: one local feature branch with one work-unit commit per task. Everything stays local and the
maintainer merges/pushes (established precedent). RDD: on (global).

## Tasks
- [x] T1 Settings `mini` mode + `mini-corner`; pure placement math + corner cycle (route: delegated writer)
- [x] T1a Default corner `top-right` + explicit top-taskbar placement test (route: inline, mechanical)
- [x] T2 Mini variants of the 4 scenes via `?variant=mini` + node tests + headless preview for visual approval (route: delegated writer)
- [x] T2b Keep chroma fan (idle, explorer) + planet (explorer), planet centered in the ring in mini; processing green nebula with radial fade clear of the edges (route: delegated writer)
- [x] T2c Shared mini radial edge fade on all 4 scenes (before the alert overlay); explorer planet not dimmed by the tint (route: delegated writer)
- [x] T2d Raphael mini: blue and gold glyphs stretched vertically (tall strokes, not dashes), gold glyph count x3 (mini only) (route: delegated writer)
- [x] T2e Explorer mini planet blue-tinted like the ring; raphael mini gold nebula faded before the edges + inclined rays rotating independently like clock hands (route: delegated writer, parallel with T3, JS only)
- [x] T3 Mini window host: topmost/noactivate/click-through DComp popup + transparent WebView2 controller (route: delegated writer)
- [x] T4 Wiring: mini mode in WireProduction, HTTP scene switch + persist (route: delegated writer)
- [x] T5 Chord `Alt+M` cycle corner + persist; README keybindings/settings (route: delegated writer)
- [x] T7 Review follow-ups cleanup (comments, duplicated fit check, stale docs) (route: delegated writer)
- [x] T2f Idle mini ring white like the original instead of grey; losing metallic detail is fine (maintainer 2026-09-29) (route: delegated writer)
- [x] T9 Alt+M cycles 8 positions (corners + side midpoints, clockwise from top-left); new mini-corner values top-center/right-center/bottom-center/left-center (maintainer 2026-09-29) (route: delegated writer, parallel with T2f)
- [x] T2g Idle/explorer mini constellation dots and lines scale with the ring (CONSTELLATION_DOT_RADIUS 2.75 px and line width are absolute, so they blob at 288px) (maintainer 2026-09-29) (route: delegated writer)
- [ ] T8 Third review follow-ups (ProcessFailed kind-aware teardown, source guard scope, recovery budget, naming) (route: delegated writer)
- [ ] T6 Hardware check with the maintainer's visual approval (route: inline, supervised run)

## Progress / evidence
- Mapping (explorer agent, main 67a0aae): scenes in `CosmicWin.App/Wallpaper/Web/<scene>/`, one `#scene`
  canvas rAF loop per scene, and processing/raphael also have a WebGL `#nebula` canvas. HTML layer =
  `Alerts/WebViewAlertLayerController.cs` (composition controller, transparent bg, virtual host
  `cosmicwin-scene.example`). Scene route: `AppComposition.HandleWallpaperSceneHttpSwitch` (~772) guards
  on html mode. Chords: `Input/ChordTable.cs`, `ToggleTiling` is the app-level template
  (`ActionExecutor.cs:184`, `AppComposition.cs:1379`). No topmost WebView2 window exists yet.
- Product decision 2026-09-29: new mode (replaces the wallpaper), not coexisting and not a toggle.

- T1 c34d651 (delegated writer): WallpaperMode.Mini, MiniCorner enum + `mini-corner` key, MiniWindowPlacement
  (Compute/Next, uses CosmicWin.Layout.Rect; IDisplay.WorkArea is a Rectangle, convert in T4). RED 25 failed ->
  GREEN 73 passed (filter Mini). Full non-desktop suite 0 failed (worktree build; the main checkout's Release output
  is locked by a running CosmicWin.App.exe). Parent spot check: Mini filter 73 passed.
  T4 gap: Mini currently falls through to the video path (AppComposition ~597 pick guard, ~2034 startup).

- Scope change 2026-09-29: default corner top-right (T1a, inline after T2 commit); keep alert overlay in mini (sent to T2 writer).
- T2 945a133 (delegated writer): `?variant=mini` via readSceneVariantFromUrl (query or hash), transparent
  html/body/canvas, nebula hidden, alert overlay + see-through hook kept. Kept layers: idle disc/rings/lighting
  mask/inner ring/earth; explorer rings + rising sparks + source-atop blue tint (drawBlueLayer and earth dropped,
  judgement calls); processing sphere/orbits/orbit blocks/octagon/prism/rays/core; raphael glyph rings/gold
  hexadecagon/core with MINI_SCENE_ZOOM 1.4. RED 4/4 SceneNodeTests failed -> GREEN; Scene filter 63 passed; full
  non-desktop Debug suite 0 failed. Idle/explorer harness timeouts raised 90s -> 240s. Previews in scratchpad
  (mini-*.png, mini-*-alert-{failed,warning}.png). Alert at 288px: title legible, side counters ~5px unreadable,
  wash covers the whole square, failed shake (scale 1.18) clips at the window edge. Nebula WebGL context still created.
- T1a 919406c (inline): default corner top-right. RED 4 failed (default) -> GREEN 297 passed (Mini|Settings).
  Top- and right-docked taskbar placement tests passed on first run: guard tests, behavior already used the work area.
- Preview feedback 2026-09-29: idle/explorer mini must keep the chroma fan, explorer must keep the planet, and in
  mini the planet sits at the exact ring center (full variant keeps it higher). T2 reopened as T2b (same writer).
- Review of slice 67a0aae..df0cb88 (high, 960 lines): consent envelope relayed, maintainer answered with scope
  feedback instead of a choice -> neither invocation run; re-relay on the corrected candidate. `nul` added to
  .git/info/exclude (the intended-untracked selection JSON was rejected as invalid_request, same as last session).
- T2b 252e0e8 (same writer): idle+explorer mini keep chroma fan (additive `lighter`, MINI_CHROMATIC_GLOW_GAIN 4,
  activeSceneBasis) and planet at exact ring center (144,144); processing mini keeps the green part of the nebula,
  premultiplied alpha, smoothstep(0.5,0.9,r) fade, WebGL alpha:true + alpha-0 clear in mini only; nebula init moved
  to main.js. RED (fan/planet/nebula cases failing) -> GREEN idle 15, explorer 16, processing 38, raphael 14; Scene
  filter 63 passed; full non-desktop Debug suite 0 failed. Edge check: nebula alone 0 edge pixels; full processing
  composite 4540 edge pixels because perspective rays + orbit blocks reach the edges (pre-existing, open decision).
- T2c b739f7e: shared `applyMiniEdgeFade` (render-loop.js, destination-in radial gradient, alpha 1 to 0.41 of the
  short side, 0 at 0.48), once per frame after layers and before the alert overlay; full variant never calls it.
  idle/explorer disc fit 0.40 of the side; raphael MINI_SCENE_ZOOM 1.4 -> 1.2. Explorer tint now runs before
  planet + fan (planet no longer blue). RED 4/4 mini-layer cases -> GREEN; edge check 0 differing pixels on all 4.
  Alert overlay stays unmasked: its rectangular wash shows the window square while an alert is up (transient).
- T2d 7051ee9: raphael mini glyph margins/gaps scale with glyphRingMiniScale (~0.19). Blue h:w 0.40 -> 3.40,
  gold 0.33 -> 3.00; gold count 13 -> 105 (3x the 35 of the standard rule), blue 71 -> 92. Full variant pinned
  (gold 35, blue 92). RED 2 -> GREEN raphael 17/17. Both commits: Scene filter 63 passed, full non-desktop suite 0 failed.
- Maintainer approved the 4 mini previews incl. alerts in their square frame (2026-09-29).
- Review slice 67a0aae..563d669 (high, 31 files, 1401 lines): consent granted, 4 lenses, APPROVED, acknowledged
  (lineage review-fef0cf6e6c8bfd60, authority burned). Reviewed boundary is now 563d669.
  Advisory follow-ups (non-blocking):
  - WARNING: mini mode falls through to video until wired (T4 covers it).
  - WARNING: render-loop.js:78-81 variant comment contradicts the kept overlay.
  - WARNING: explorer-scene.tests.js:550-578 duplicated fit check with a wrong threshold comment.
  - Suggestions: duplicated mini basis (idle config.js), glow sprites hardwired gold count (raphael sprites.js:182),
    stale timeout doc (IdleSceneNodeTests.cs:44), gold cap branch unproved (glyph-rings.js:205), placement accepts
    unbounded inputs, doc scope line 19.
  -> T7 cleanup task.
- T3 14d6530 (delegated writer): Win32MiniSceneWindow (TOPMOST|TOOLWINDOW|NOACTIVATE|TRANSPARENT|LAYERED|
  NOREDIRECTIONBITMAP + SetLayeredWindowAttributes 255; without LAYERED WindowFromPoint still hit our window),
  own DComp device/target/root visual; ICompositionOverlaySurface shared with Win32VideoWallpaperHost;
  WebView2MiniSceneBrowser (WebView2Mini user data, transparent bg, RasterizationScale 1, square zero-origin viewport);
  MiniSceneWindowController / IMiniSceneWindow: Show/SwitchScene/MoveTo/ShowAlert/HideAlert/Dispose; alert JSON moved
  to AlertLayerMessages. RED: controller tests did not compile -> GREEN 18 controller cases (writer reported 19; counted 18) + 2 style guards (first-run pass, guards).
  Manual: processing mini top-right 288x288 over Chrome, no black box, WindowFromPoint(center) = Chrome_WidgetWin_1,
  foreground unchanged, double Dispose clean (scratchpad minih/mini-crop.png). Only checked at 3440x1440 scale.
  Suite: App 1263, Interop 424 (3 skipped), Layout 198, Alert 13, 0 failed.
  T4 gaps: build on UI STA via CreateProduction + Show(Compute(...)); IDisplay.WorkArea Rectangle -> Rect; Show false
  on create failure (retry?); route alert queue show/hide to ShowAlert/HideAlert and do not create the wallpaper alert
  layer in mini; HTTP switch calls SwitchScene + persist; Alt+M -> MoveTo(Compute(Next)).
- T2e 0be0c34 (scene writer, parallel with T3): explorer mini order ring, earth, blue tint, fan, sparks (planet
  blue, fan untinted); raphael mini gold nebula (shader's own vec3(1.0,0.82,0.38)), fade smoothstep(0.75,0.98,r)
  via u_miniFadeStart/End; inclined rays back (48 rays, alternating direction, per-ray speed 0.10-0.42 x 5, already
  independent in the full scene). RED raphael 3 + explorer order -> GREEN raphael 19, explorer 16; Scene 63 passed,
  full suite 0 failed (verified in a scratchpad worktree). Edge check 0 on explorer and raphael.
- T4 3f2d07b (delegated writer): mini startup builds the window on the STA, Show posted to the UI thread at
  Compute(primary work area, bounds height, MiniCorner); no host/player/wallpaper alert layer; video pick/HTTP video
  skipped in mini (trace reason=mini-mode, /v1/wallpaper/video 503); HTTP scene switch + persist in mini; alert queue
  routed to ShowAlert/HideAlert (fullscreen-coverage check ignored in mini, ready only after Show succeeded);
  ReconcileOnce display change -> MoveTo; Show failure traced, no retry. RED 16/25 -> GREEN 25/25.
- T5 0c92d47: KeyboardKey.M + CycleMiniCorner, Alt+M before the tiling gate, Next -> MoveTo -> persist mini-corner;
  outside mini traces skipped. README documents mini mode, mini-corner and Alt+M. RED 9 -> GREEN 97.
  Suite after T5: App 1301, Interop 424 (3 skipped), Layout 198, Alert 13, 0 failed. Parent spot check: MiniMode|ChordTable 77 passed.
  WireProduction mini path only covered by a source-text guard (real Win32/WebView2).
- Review slice 563d669..fc811a5 (high, 27 files, 2178 lines): consent granted, 4 lenses, APPROVED, acknowledged
  (lineage review-29d1a21eef02224c, authority burned). Reviewed boundary is now fc811a5.
  Advisory follow-ups (non-blocking) -> T7:
  - WARNINGs:
    - AppComposition.cs:499 misleading failure filter.
    - AppComposition.cs:524-529 alert thread affinity unproved.
    - AppComposition.cs:1493-1514 Alt+M acts after a failed Show.
    - MiniSceneWindowController.cs:114-116 Show ignores the place result and returns true before the WebView attaches.
    - WebView2MiniSceneBrowser.cs:93-98 no recovery after a WebView2 process failure.
  - Suggestions:
    - stale 288 comment (WebView2MiniSceneBrowser.cs:59).
    - stale renderer comment (AppComposition.cs:1007).
    - partial attach leak (WebView2MiniSceneBrowser.cs:57-68).
- T7 922ef05 + 8d4f604 (delegated writer):
  - IMiniSceneWindow.IsReady (true only after the browser attached): drives the alert queue and Alt+M
    (skipped reason=not-ready).
  - Show false + dispose on a Place failure.
  - WebView2 ProcessFailed -> release + re-attach + re-navigate, capped at 2 per window lifetime ("recovery exhausted").
  - AttachAsync cleans up in finally.
  - Mini ShowAlert/HideAlert always posted to the owning thread (pool-thread test).
  - IsRecoverableAlertLayerFailure renamed to IsRecoverableFailure.
  - Shared miniRingBasis.
  - Placement side clamped to the work area's short side and >= 0.
  - Raphael gold cap test.
  - Stale comments fixed.
  - Skipped sprites.js:182: already derived from goldGlyphRingCount and covered by a test.
  - RED: 10 compile errors + 7 failures -> GREEN. The browser source guards and the cap test only passed as guards.
  - Suite: App 1318, Interop 424 (3 skipped), Layout 198, Alert 13, 0 failed.
  - Parent spot check: Mini* 81 passed.
  - T6 watch items:
    - kill the mini WebView2 child: it recovers twice, and the third kill stays down.
    - Alt+M right at startup traces not-ready.
    - an early alert shows once ready.
- Review slice fc811a5..2838866 (high, 18 files, 606 lines): consent granted, 4 lenses, APPROVED, acknowledged
  (lineage review-d61cf33a9828d414, authority burned). Boundary now 2838866. Advisory (non-blocking, parked as T8):
  - WARNINGs:
    - ProcessFailed teardown ignores the failure kind (WebView2MiniSceneBrowser.cs:113-124).
    - The source-guard finally scope is too wide (SourceGuardTests.cs:27-33).
    - The miniRingBasis placement splits the variant comment (render-loop.js:87-94).
  - Suggestions:
    - IsReady vs ready naming.
    - A failed recovery attach is silent (Controller.cs:240).
    - The recovery budget never replenishes (Controller.cs:78).
- T6 hardware run 2026-09-29 (agent-driven, Release build of 2838866+, elevated session, 3440x1440, right-docked
  taskbar):
  - The old instance (alacritty-branch build, PID 14240) was stopped. settings.conf was backed up to the scratchpad
    and wallpaper-mode set to mini.
  - Startup: processing mini top-right, transparent over Chrome, left of the vertical taskbar.
  - HTTP scene raphael/idle: 202 and persisted (wallpaper-scene = idle).
  - Alt+M (synthetic keybd_event): TR->BR->BL->TL->TR, each step persisted mini-corner; bottom-right sits flush
    against the taskbar.
  - Alert 2 failed + 1 warning: 202 and shown inside the window.
  - Killing the mini WebView2 browser process: recovered with a new PID and the scene came back.
  - NOT run: third-kill exhaustion, Alt+M at startup (not-ready trace), other DPI scales, user's own visual check.
  - Screenshots are in the scratchpad (t6-*.png).
- T2f b68bf45: the idle grey came from drawCombinedLightingMask (multiply over a transparent backdrop leaves an opaque
  grey disc); idle mini now skips it, so the ring draws white cells/dots/rims on transparent (full variant keeps it).
  Explorer mini still applies the mask. RED 14/15 -> GREEN 15/15; Scene 95 passed; full suite 0 failed (worktree).
  Edge check 0. Outer bands now faint; a white source-atop lift is the next step if wanted.
- T9 4a8d752 (delegated writer): MiniCorner holds 8 clockwise positions (TL, TopCenter, TR, RightCenter, BR,
  BottomCenter, BL, LeftCenter); new literals top-center/right-center/bottom-center/left-center; Compute centers
  midpoints with integer math; README updated; 3 wiring tests moved to the 8-step order. TDD deviation: production
  code was written before RED; RED confirmed afterwards by stashing (20 compile errors). Suite: App 1337, Interop 424
  (3 skipped), Layout 198, Alert 13, 0 failed.
- T6 rerun after T2f+T9 (Release rebuilt, PID 45756): Alt+M x8 persisted BC, BL, LC, TL, TC, TR, RC, BR in
  order; a 9th press moved to bottom-center, and the full-screen shot shows it centred on the bottom edge with the
  white idle ring. settings.conf had moved TR -> BR at 13:51 while the old build ran (one Alt+M press, likely the maintainer).
- Review slice 2838866..(T2f+T9+docs) (high, 9 files, 220 lines): consent granted, 4 lenses, APPROVED, acknowledged
  (lineage review-01ff429cdb886451, authority burned). Advisory suggestions only (-> T8): MiniCorner name now drifts
  from its meaning (Settings.cs:28); Next() reaches LeftCenter implicitly (MiniWindowPlacement.cs:42-48).
- T2g 259ac58: constellationDetailSizes(boxSize) in idle/explorer rings.js scales dot radius and line width in mini
  by mini ring thickness / full ring thickness at the 3440x1440 reference (~10.5/63 = 0.166), floor 0.5px: dot
  2.75 -> 0.5, line 2.5 -> 0.5 at 288px. The ring cache bakes via the same function. RED 1+1 -> GREEN idle 16,
  explorer 17; Scene 95; full suite 0 failed. Other px-absolute details found, not changed: hieroglyph band 1.6px
  strokes (dense white hatching, next candidate), 1-1.5px hairlines. Caution: the edge check also passes on a blank render.

## Next step
Maintainer's own look at the running app; then T8 follow-ups or merge decision (maintainer-owned).
