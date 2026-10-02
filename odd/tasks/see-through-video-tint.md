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
- Delivery strategy: `ask-on-risk`. Forecast S3+S4 ~700-900 lines. Chain strategy chosen by the
  maintainer 2026-10-01: `feature-branch-chain` -- slices S3 (Interop tint pass) and S4 (page +
  controller) are reviewable slices on `feat/see-through-video-tint`; main gets the whole feature at
  the end. PRs/push stay the maintainer's.

## Tasks

- [x] S1 SPIKE (throwaway, measures, no production wiring): on the video back buffer, before
  `Present`, run a Direct2D pass that draws the current frame tinted by luminance (ColorMatrix)
  inside a HARDCODED rectangle mask. Answer: (a) can D2D target the back buffer surface directly,
  or does the frame need an intermediate texture (an effect cannot read the surface it draws to);
  (b) per-frame cost at the real resolution; (c) visual check on hardware. Route: delegated writer.
- [x] S2 Decide the mask source from S1's numbers: page exports a mask bitmap per show (letters
  are static after reveal, but reveal/pixelation/shake animate) vs C# re-renders the letters with
  DirectWrite (layout drift risk). Maintainer decision if the tradeoff is real.
- [x] S3 Interop tint pass (production, TDD): a `VideoTintPass` seam owned by the player, OFF unless a
  mask is set; frame -> intermediate texture -> back buffer, then ColorMatrix(luminance x tint) through
  an AlphaMask effect with the mask bitmap. Carry the spike review findings: release the target
  bitmap around ResizeBuffers/re-attach, back off after a device failure, keep it out of the inline
  frame pump. API sketch: `SetTint(mask pixels+size, tint color)` / `ClearTint()`, thread-safe
  handoff to the worker thread.
- [x] S4 Page + controller: in video mode only (C# says so in the `show` message), when the reveal
  ends the page renders a dedicated mask canvas (opaque letters, no shadow/alpha/pixelation, back
  buffer pixel size) and posts it (PNG data URL) to C#; the controller decodes it and calls SetTint
  with the kind's color (failed blue, warning violet); after C# acks, the page stops drawing the
  letter fill so the tinted video reads through. `hide`/`done`/scene reload -> ClearTint. Mosaic:
  one mask covers every tile.
- [x] S3b S3 review follow-ups (review-a59141cc1a4f388a, review-e3244d34e2dab4cb): WARNING
  R4-recreate-target-bypasses-backoff (a repeating RECREATE_TARGET rebuilds every tick), WARNING
  R3-transfertinted-fallback-untested, WARNING R3-tint-target-pin-blocks-resize-while-engaged, SUGGESTION
  R4-tint-failures-silent (no trace of tint failures); plus S4 review review-909e6b6f8dc7fd6e: WARNING
  R3-export-throw-kills-render-loop (a throwing toDataURL kills the page render loop -> alert stuck),
  WARNING R3-seq-zero-applies-when-no-show-active (a mask with seq 0 and no active show still tints).
  Route: delegated writer, before S5 so the hardware run has traces.
- [x] S5 Hardware check in `Video` mode (failed blue, warning violet, mosaic, shake), restore settings.

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

- 2026-10-01 S3 DONE (delegated writer; route: delegated direct, writer trigger 2+ non-trivial files).
  `MediaFoundationVideoWallpaperPlayer.SetTint(ReadOnlyMemory<byte> maskAlpha, width, height, r, g, b)` /
  `ClearTint()` (concrete class, like Shake; IVideoWallpaperPlayer untouched). Mask: 8-bit alpha,
  row-major, tightly packed, top row first, copied at the call; other sizes resampled nearest-neighbour
  once per request. Lock-free handoff (volatile immutable VideoTintRequest, latest wins). Off path = one
  volatile read, then today's TransferVideoFrame; no D2D object exists unless a tint is set. Pass:
  frame -> intermediate texture; back buffer <- intermediate (SOURCE_COPY); then
  AlphaMask(ColorMatrix(intermediate), A8 mask) SOURCE_OVER. Classes: VideoTintMatrix, VideoTintRequest,
  VideoTintMaskResampler, VideoTintDriver (+IVideoTintRenderer), D2DVideoTintRenderer. Spike findings:
  back-buffer-bound objects released on a changed buffer/size and the renderer disposed on ClearTint and
  worker stop (nothing pins the swapchain outside an alert); 5 s back-off after any failure with fallback
  to the untinted path; RECREATE_TARGET rebuilds next tick; nothing throws out of Tick.
  TDD: RED observed for matrix/resampler/driver (20/24 failing on stubs), Abort/ReleaseGpu, and the 6 GPU
  tests on a stub renderer; VideoTintRequest and player-level API tests are characterization (passed
  first run). GPU tests (real hardware D3D11, offscreen, no desktop): masked pixels == L*tint within
  2/255, unmasked unchanged, soft mask, resampled mask, back-buffer pin released (native refcounts).
  Parent spot check: Interop 529 passed / 42 skipped / 0 failed (was 493/42); App 1434/6/0; build 0
  errors. Not covered yet: the live Tick/TransferTinted path on the host's shared MT-protected device
  (S5 hardware); GPU frame cost; RECREATE_TARGET real HRESULT path.

- 2026-10-01 S4 DONE (delegated writer). `show` gains `tint:true, seq` only in Video mode
  (AlertLayerMessages.ShowTinted; html/mini output byte-identical). Page -> C#: one
  `{type:mask, seq, kind, width, height, png}` when the reveal ends; C# -> page `{type:tint-ready, seq}`.
  The page painted a translucent per-tile WASH over the letters too, so on tint-ready it cuts the letter
  shape out of the wash (destination-out) and stops the letter fill; frame and modules unchanged. Mixed
  mosaic: one mask for the first tile's kind (failed sorts first); the other kind keeps normal letters.
  AlertTintCoordinator: BeginShow clears + new seq; mask decoded (WPF PNG, alpha only) on the pool, seq
  rechecked under lock before SetTint; bounded sizes; malformed/stale/duplicate traced and ignored; Clear on
  Start, End, done, SwitchScene, TearDown. Colors: AlertTintColors.Failed (40,110,255), Warning
  (150,70,255). Sink only in Video mode (VideoPlayerAlertTintSink.For). TDD RED: harness 5 failing of 28,
  C# 14 of 34 on stubs, wiring 2 of 5; characterization cases noted. Parent spot check: harness 28/28,
  App 1473 passed / 6 skipped / 0 failed (was 1434/6). S5 risks: toDataURL hitch at 3440x1440, halo at
  the wash cut-out edge, <=1 px mask offset at tile edges, a possible one-frame gap at tint-ready.

- 2026-10-01 S3b DONE (delegated writer; 8 findings incl. two from review-7ffcd538e07b286b sent mid-task):
  (1) a repeated RECREATE_TARGET inside the back-off window now backs off 5 s (one immediate rebuild
  allowed); (2) TransferTinted ordering extracted to TintedFrameTransfer.Run, 5 tests; (3) no code change:
  Win32VideoWallpaperHost never calls ResizeBuffers, it builds a new swapchain, so a pinned old buffer
  blocks nothing and Prepare drops it next tick (covered by the existing GPU rebind test; documented in
  D2DVideoTintRenderer remarks); (4) `video-tint failed hr=.. backoff=5s` / `retry` / `recovered` lines on
  the desktop trace (onTintDiagnostic wired to desktopTrace.Record); (5) a mask with no active show is
  ignored and traced (before and after decode); (6) a throwing mask export posts `mask-failed`, keeps the
  normal letters, still ends with done; (7) tint-ready is posted only when the player raises TintRendered
  (first successful tinted frame of the current request), and TintLost -> `tint-lost` makes the page
  restore the letter fill (no alert with invisible letters); (8) the mask export takes tile geometry as
  parameters (no global W/H swap). RED observed for each (or characterization noted). Known residual:
  a TintRendered for an already-replaced request could post tint-ready for the newer show (narrow race).
  Writer's full App run hung in the wallpaper scene node harnesses (its own overlapping runs left a stray
  node.exe running explorer-scene.tests.js); parent killed the stray and re-ran clean: App 1483 passed /
  6 skipped / 0 failed, node.exe 7 before and after; Interop 551/42/0; harness 32/32.

- 2026-10-01 S3b review review-9cdfe0e80db33b47 APPROVED and acknowledged. WARNING
  R3-post-under-lock-from-worker fixed inline (parent): OnRendered/OnLost build the message under the lock
  and post outside it. RED: a post that waits on a Clear() from another thread timed out (deadlock);
  GREEN after. WARNING R3-rendered-event-cross-show-race ACCEPTED as residual: the event carries no request
  identity, so in a narrow window a stale Rendered could let the NEW show drop its letter fill up to one
  frame before its own tint renders (a later failure still sends tint-lost). A full fix needs a request
  token through the Interop API; revisit only if S5 shows a visible flash.

- 2026-10-01 whole-branch review review-436e4f349beef0fa (9047f36..8e7154a) APPROVED and acknowledged. Fixed
  inline (parent): WARNING R3-releasegpu-drops-announced-without-lost / R4-releasegpu-swallows-lost-signal --
  ReleaseGpu (playback stopping mid-alert, e.g. a video re-pick) now raises Lost when a tint was announced,
  so the page paints its letters again. RED: `ReleaseGpu_AfterTheTintWasAnnounced_RaisesLost` got only
  [rendered]; GREEN after. WARNING R2-tint-transport-doc-and-trace-label-stale: transport doc names the
  worker-thread caller and both messages; trace label `post-tint`. Interop 553/42/0, App 1484/6/0.
  Open SUGGESTIONs (not chased): max message chars rationale, source-scan wiring tests, coordinator never
  unsubscribes sink events, WPF PNG decoder on the thread pool.

- 2026-10-02 S5 DONE on hardware (Release of feature tip 1d4bf60, wallpaper-mode switched to `video` with
  settings.conf backed up and restored byte-identical; CosmicWin back on html-mini from main). Alerts over
  HTTP with windows minimized, screenshots taken 3 s after the 202:
  - failed: `show` 00:54:42.153 -> `tint requested kind=failed size=3440x1440` +580 ms (reveal end) ->
    `tint rendered` +27 ms. Inside FAILED the real video shows tinted blue by luminance (bright ring
    segments/moon/stars blue, dark space black); the red wash is cut out exactly along the letters.
  - warning: same timeline (+750 ms, +24 ms), violet inside WARNING, yellow wash cut out.
  - mixed mosaic failed+warning (2x1): the failed tile tinted blue, the warning tile keeps its normal
    letters (by design: one mask for the first tile's kind).
  - 0 `video-tint` failure lines; done/hide followed each alert.
  Not observable in screenshots: a one-frame gap at tint-ready (the accepted cross-show residual).
  Product note for the maintainer: with this mostly-dark scene the tinted letters read mostly black with
  blue/violet highlights (that is the chosen every-brightness mapping).

- 2026-10-02 merged into main (ff 670fe11) and pushed by the maintainer.
- 2026-10-02 follow-up (branch feat/tint-brightness-floor): after seeing the dark scene the maintainer chose
  a 25% BRIGHTNESS FLOOR: out = (0.25 + 0.75 * L) * tint (VideoTintMatrix.BrightnessFloor; the constant row
  carries 0.25 * tint, the luma rows are scaled by 0.75), so black video reads as a dark blue/violet and
  white stays the full tint. TDD: RED 6 failing (3 matrix facts incl. the new black-input fact, 3 GPU pixel
  facts with the floored expectation); GREEN after: Interop 555 passed / 42 skipped / 0 failed (GPU facts
  measure real pixels).

- 2026-10-02 floor hardware check (Release of feat/tint-brightness-floor a854c92, wallpaper-mode `video`,
  settings.conf restored byte-identical afterwards): failed -> black space now reads as navy blue inside
  the letters, bright ring segments/stars stay bright blue; warning -> dark violet with bright violet
  highlights. Mask +576/+753 ms after show, rendered +31/+17 ms; 0 video-tint failures.

## Next step

Done. Merged into main. (ask the maintainer before switching wallpaper-mode to video).
