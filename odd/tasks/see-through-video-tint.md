# See-through video tint (alert letters show the real video, tinted)

## Objective

In `Video` wallpaper mode, where an alert's FAILED/WARNING letters are, the REAL video frame behind
shows through, tinted by luminance: white -> full blue (failed) / full violet (warning), grey -> a
darker shade, black -> black.

## Problem and why

The alert layer page (WebView2) is a DirectComposition visual added ABOVE the video swapchain visual
(`Win32VideoWallpaperHost.AddCompositionOverlayVisualUnlocked`, `root.AddVisual(overlay, true,
swapChainVisual)`), so the page can only alpha-composite over the video; it cannot sample it (no
backdrop path in DComp v1). The letters today are a flat dark color. The maintainer asked for the
real effect (parked since 2026-09-26, resumed 2026-10-01).

## Decisions

- 2026-10-01, maintainer: tint EVERY brightness (luminance mapped to the tint color), not only
  near-white pixels.
- Only `Video` mode has a video back buffer; `Html` / `HtmlMini` never start the player. This
  feature changes nothing there (html scenes already redraw their own content via
  `sceneSeeThroughLayer`).

## Current evidence (mapped 2026-10-01 against main 8fe9ed3)

- Frame path: worker thread in `MediaFoundationVideoWallpaperPlayer` (~16 ms software tick):
  `OnVideoStreamTick` -> `host.GetBackBuffer()` -> `TransferVideoFrame(backBuffer, ...)` ->
  `host.Present()` (`_swapChain.Present(1, 0)`).
- Swapchain: `CreateSwapChainForComposition`, `B8G8R8A8_UNORM`, `FLIP_SEQUENTIAL`, 2 buffers,
  `RENDER_TARGET_OUTPUT`, `AlphaMode IGNORE`. D3D11 device created with `BGRA_SUPPORT`,
  multithread-protected, shared with the engine through an `IMFDXGIDeviceManager`.
- No Direct2D/DirectWrite at runtime (the Direct2D alert overlay was deleted in dc80b4f).
- Letters: canvas 2D in `alert-layer.js` (`drawFailureTitle`, `drawFailureOverlay`, per-tile
  `renderTile`, reveal pixelation, shake wait); C# -> page messages `show`/`hide`; page -> C# only
  `done`. No mask/rect export exists.

## Scope and constraints

- Strict TDD for production code (global instructions). Runners:
  `dotnet test CosmicWin.App.Tests/CosmicWin.App.Tests.csproj`,
  `dotnet test CosmicWin.Interop.Tests/CosmicWin.Interop.Tests.csproj`; real-desktop facts need
  `COSMICWIN_RUN_DESKTOP_TESTS=1` and CosmicWin stopped.
- Hardware runs need `wallpaper-mode = video` temporarily (the maintainer runs `html-mini`);
  back up and restore `settings.conf`, relaunch CosmicWin afterwards.
- The frame tick must stay cheap: measure before wiring.
- Work-unit commits, Conventional Commits, no AI attribution; ~400 changed lines per task is advisory.
- Delivery strategy: `ask-on-risk`. Forecast: well over 400 lines in total -> ask for the chain
  strategy before the running count passes ~400.

## Tasks

- [ ] S1 SPIKE (throwaway, measures, no production wiring): on the video back buffer, before
  `Present`, run a Direct2D pass that draws the current frame tinted by luminance (ColorMatrix)
  inside a HARDCODED rectangle mask. Answer: (a) can D2D target the back buffer surface directly,
  or does the frame need an intermediate texture (an effect cannot read the surface it draws to);
  (b) per-frame cost at the real resolution; (c) visual check on hardware. Route: delegated writer.
- [ ] S2 Decide the mask source from S1's numbers: page exports a mask bitmap per show (letters
  are static after reveal, but reveal/pixelation/shake animate) vs C# re-renders the letters with
  DirectWrite (layout drift risk). Maintainer decision if the tradeoff is real.
- [ ] S3 Page side: in video mode the letters become holes/neutral so the tinted video reads through.
- [ ] S4 Wire per tile (mosaic), reveal and shake; clear the tint on hide/done.
- [ ] S5 Hardware check in `Video` mode (failed blue, warning violet, mosaic, shake), restore settings.

## Acceptance

- In Video mode the letters show the moving video, tinted by luminance; failed blue, warning violet.
- No visible frame-time regression while no alert shows (the pass is skipped).
- Html / HtmlMini unchanged; all suites green.

## Progress

- 2026-10-01: branch `feat/see-through-video-tint` off main 8fe9ed3; map done; tint decision taken.

## Next step

S1 spike.
