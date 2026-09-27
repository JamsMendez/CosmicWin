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
- Slices: D2 -> `demo/html-wallpaper-d2-processing-page`; D2b -> `demo/html-wallpaper-d2b-hardening`; D3 -> `demo/html-wallpaper-d3-switch`.
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
- [ ] D3 - Demo switch (`wallpaper-mode = html`): no video playback, the page stays visible
      permanently, alerts only toggle the overlay; shake moves into the canvas.
      Route: delegated writer (several non-trivial C# files: settings, AppComposition, controller).
- [ ] D4 - Measure GPU/CPU of the scene full screen vs the video wallpaper.
- [ ] D5 - Hardware check with the maintainer watching (failed, warning, mixed mosaic).
- [ ] D6 - explorer, raphael, idle scenes with their own see-through layer + scene selection.

## Open questions

- How the 4 scenes are selected (setting? tray?).
- fps cap (30 vs 60) if D4 is expensive; pause the scene while the desktop is covered?

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

## Next step

D3 (demo switch: host-only attach, controller navigates to the scene page, page permanently
visible).
