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
  - idle: only the central ring (disc/rings), no starfield/vignette/black fill.
  - explorer: only the blue ring + sparks.
  - processing: only bands, wait, octagon, orbits, center, lines. No nebula, background, soft oval,
    chroma fans, stars, streaks, flares, grain or vignette.
  - raphael: only the circle, center and golden lines.
- The HTTP scene route switches the mini window in mini mode and persists `wallpaper-scene`.
- Chord `Alt+M` cycles the corner clockwise (TL -> TR -> BR -> BL) and persists `mini-corner`.

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
- [ ] T3 Mini window host: topmost/noactivate/click-through DComp popup + transparent WebView2 controller (route: delegated writer)
- [ ] T4 Wiring: mini mode in WireProduction, HTTP scene switch + persist (route: delegated writer)
- [ ] T5 Chord `Alt+M` cycle corner + persist; README keybindings/settings (route: delegated writer)
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

## Next step
Maintainer visual approval of mini previews + alert look, then T3.
