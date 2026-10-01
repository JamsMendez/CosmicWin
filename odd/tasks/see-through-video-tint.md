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
- 2026-10-01, maintainer (S2): the PAGE exports the letters mask (single source of truth for the
  layout: font fit, mirrored fragments, mosaic). During the pixelated reveal the letters look as
  today; the tint takes over when the reveal ends. C# re-rendering with DirectWrite was rejected
  (layout duplicated in two languages, drift risk).
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

- [x] S1 SPIKE (throwaway, measures, no production wiring): on the video back buffer, before
  `Present`, run a Direct2D pass that draws the current frame tinted by luminance (ColorMatrix)
  inside a HARDCODED rectangle mask. Answer: (a) can D2D target the back buffer surface directly,
  or does the frame need an intermediate texture (an effect cannot read the surface it draws to);
  (b) per-frame cost at the real resolution; (c) visual check on hardware. Route: delegated writer.
- [x] S2 Decide the mask source from S1's numbers: page exports a mask bitmap per show (letters
  are static after reveal, but reveal/pixelation/shake animate) vs C# re-renders the letters with
  DirectWrite (layout drift risk). Maintainer decision if the tradeoff is real.
- [ ] S3 Interop tint pass (production, TDD): a `VideoTintPass` seam owned by the player, OFF unless a
  mask is set; frame -> intermediate texture -> back buffer, then ColorMatrix(luminance x tint) through
  an AlphaMask effect with the mask bitmap. Carry the spike review findings: release the target
  bitmap around ResizeBuffers/re-attach, back off after a device failure, keep it out of the inline
  frame pump. API sketch: `SetTint(mask pixels+size, tint color)` / `ClearTint()`, thread-safe
  handoff to the worker thread.
- [ ] S4 Page + controller: in video mode only (C# says so in the `show` message), when the reveal
  ends the page renders a dedicated mask canvas (opaque letters, no shadow/alpha/pixelation, back
  buffer pixel size) and posts it (PNG data URL) to C#; the controller decodes it and calls SetTint
  with the kind's color (failed blue, warning violet); after C# acks, the page stops drawing the
  letter fill so the tinted video reads through. `hide`/`done`/scene reload -> ClearTint. Mosaic:
  one mask covers every tile.
- [ ] S5 Hardware check in `Video` mode (failed blue, warning violet, mosaic, shake), restore settings.

## Acceptance

- In Video mode the letters show the moving video, tinted by luminance; failed blue, warning violet.
- No visible frame-time regression while no alert shows (the pass is skipped).
- Html / HtmlMini unchanged; all suites green.

## Progress

- 2026-10-01: branch `feat/see-through-video-tint` off main 8fe9ed3; map done; tint decision taken.
- 2026-10-01 S1 DONE (throwaway branch `spike/see-through-tint`, c41e558, NOT to be merged; delegated
  writer). Approach: with a marker file `%LOCALAPPDATA%\CosmicWin\spike-tint`, TransferVideoFrame
  writes into an INTERMEDIATE B8G8R8A8 texture (RENDER_TARGET | SHADER_RESOURCE); a D2D device
  context (device from the host's D3D11 device) targets a bitmap over the back buffer, draws the
  intermediate full-size (SOURCE_COPY), then a CLSID_D2D1ColorMatrix of it inside
  PushAxisAlignedClip(rect). Answers: (a) yes, an intermediate texture is needed and works;
  (b) hardware, 3440x1440, ~1 min: D2D pass CPU avg ~0.35 ms, max 1.13 ms; whole tick avg ~0.7 ms,
  max 2.5 ms; 0 failures across every 120-tick window (GPU time not measured); (c) screenshot:
  the centered rect shows the moving video tinted by luminance (white -> full blue, grey -> dark
  blue, black stays black), outside it unchanged. CsWin32 has no D2D1_MATRIX_5X4_F: the matrix is
  passed as raw bytes through ID2D1Effect.SetValue. Review review-7ef316d4ddfb0c6b (spike) approved;
  WARNINGs worth carrying into the real design: the target bitmap pins the swapchain buffer
  (would break ResizeBuffers -- release/recreate around resize), device creation must not be
  retried every tick after a failure (back off), and the pass must not live inline in the frame
  pump (own seam). Settings restored byte-identical; CosmicWin back on html-mini.

## Next step

S2: choose the mask source.
