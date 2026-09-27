"use strict";

// html-wallpaper-demo D6a: fault-isolated render-loop helpers shared by every scene's own render
// loop (CosmicWin.App/Wallpaper/Web/processing/js/main.js, CosmicWin.App/Wallpaper/Web/explorer/js/
// animate.js) -- ported unchanged from processing/js/main.js's own D2b fix. See that file's D2b
// remarks for the full original reasoning:
// - resetCanvasStateForFrame(): a throwing frame can leave the 2D context's own save/restore stack,
//   transform, and globalAlpha/composite mode -- and, separately, the shake CSS transform
//   applyFailureShake sets on the canvas element itself -- in whatever state the failing draw call
//   left them, which would otherwise corrupt every later frame. There is no way to query the 2D
//   context's own save/restore stack depth, so restore() defensively, several times more than any
//   scene's own deepest save()/restore() nesting: restore() on an empty stack is a documented no-op
//   (HTML Canvas 2D spec), so this is always safe, even for a frame that never called save() at all.
// - createRenderStageReporter(logPrefix): reports a throwing frame once per DISTINCT message, not
//   once per frame -- a failing layer can run at up to 60fps, and without this guard the same error
//   would flood the console 60x/sec instead of being reported once, the way a wallpaper host actually
//   needs to see it. Deduped PER STAGE (one remembered message per call site, e.g. "scene" and
//   "alert-overlay") in a map private to the returned closure -- if the scene and the overlay BOTH
//   fail on every frame, each stage's own repeats are only ever compared against that SAME stage's
//   own last message, so neither ever floods the other's slot (the bug a single SHARED slot used to
//   cause, fixed on 62d6587 before this file existed).
//
// Depends on the calling scene's own `ctx`/`canvasScaleX`/`canvasScaleY` globals (each scene's own
// config.js) and this shared module's own applyFailureShake (alert-overlay.js) -- resolved at CALL
// time, so load order relative to alert-overlay.js does not matter (both are loaded, and idle, well
// before the first animation frame runs).

function resetCanvasStateForFrame() {
  for (var i = 0; i < 16; i++) {
    try { ctx.restore(); } catch (restoreError) { /* no-op: nothing left to restore */ }
  }
  try { ctx.setTransform(canvasScaleX, 0, 0, canvasScaleY, 0, 0); } catch (transformError) { /* ctx gone */ }
  try { ctx.globalAlpha = 1; } catch (alphaError) { /* ctx gone */ }
  try { ctx.globalCompositeOperation = "source-over"; } catch (compositeError) { /* ctx gone */ }
  try { applyFailureShake(null); } catch (shakeError) { /* ctx/canvas gone */ }
}

// Returns a reportRenderError(stage, error) function with its own PRIVATE per-stage last-message
// map, so each scene's own render loop gets an independent dedup slot set (never shared across scenes
// or across multiple calls to this factory).
function createRenderStageReporter(logPrefix) {
  var lastMessageByStage = {};
  return function reportRenderError(stage, error) {
    var message = stage + ": " + (error && error.message ? error.message : String(error));
    if (lastMessageByStage[stage] === message) return;
    lastMessageByStage[stage] = message;
    console.error(logPrefix + " render frame failed (" + stage + ")", error);
  };
}

// html-wallpaper-demo D6d: the shared frame-rate cap. Every scene used to declare its own
// `const scheduleFrame = window.requestAnimationFrame.bind(window);` (processing/js/config.js,
// raphael/js/config.js, explorer/js/animate.js, idle/js/animate.js) -- moved HERE, as the single
// place every scene's render loop schedules its next animation frame, so the smallest possible
// change (each scene's own file just stops declaring it) routes all four through one throttle. This
// file already loads before every scene's own main.js/animate.js (see the SCRIPT_FILES order in each
// scene's own index.html, right after shared/js/alert-overlay.js), so nothing needs to change about
// load order.
//
// readWallpaperFpsFromUrl() reuses alert-overlay.js's own parseParams() (loaded just before this
// file -- see that file's own header remarks) rather than re-parsing location.hash/location.search a
// second way: `fps` is just one more query/hash param on the same navigated URL (see
// WebViewAlertLayerController.cs's own Navigate call, `.../index.html?fps=<30|60>`), read exactly
// once at load (settings take effect at startup, like wallpaper-mode; D6d needs no live reload).
// Anything other than the literal "30" keeps the default 60 -- including "60" itself, a missing
// param, or garbage -- exactly like Settings.cs's own TryReadWallpaperFps treats anything other than
// exactly 30/60 as the default.
function readWallpaperFpsFromUrl() {
  try {
    return Number(parseParams().get("fps")) === 30 ? 30 : 60;
  } catch (parseError) {
    return 60;
  }
}

var wallpaperFrameIntervalMs = 1000 / readWallpaperFpsFromUrl();

// The timestamp (rAF's own `ms` argument) of the last frame actually DRAWN -- i.e. the last time
// `scheduleFrame`'s own wrapper decided to invoke its callback, never a frame it skipped. `null`
// means no frame has drawn yet (always draws the very first one, with nothing to compare against).
var wallpaperLastDrawnFrameTimeMs = null;

// Tolerates rAF's own ordinary sub-millisecond delivery jitter without ever letting a full extra
// frame slip through: a real interval a hair under the exact target (e.g. 33.29ms against a 33.33ms
// 30fps target) still counts as "enough time passed", the same way a hair OVER never lets two frames
// through where only one fpscap-worth of time has elapsed.
var WALLPAPER_FRAME_INTERVAL_EPSILON_MS = 1;

// The ONE place every scene's render loop schedules its next frame (see this function's own header
// remarks above for what it replaces). At the default 60fps this is exactly one
// requestAnimationFrame per drawn frame -- unchanged in effect from before this file existed. At
// 30fps, roughly every other real animation frame is SKIPPED: `callback` is not invoked at all (so it
// draws nothing and cannot advance whatever clock it reads from its own `ms` argument, e.g.
// alertSceneMs), but the skip itself still reschedules via requestAnimationFrame -- unconditionally,
// exactly like every scene's own render()/renderFrame() unconditionally calls scheduleFrame as its
// last statement (D2b/D6a fault isolation) -- so the loop never stops. Because a DRAWN frame always
// carries its REAL rAF timestamp, an alert's own duration (measured in wall-clock ms, see
// alert-overlay.js's startShowing/render) still elapses correctly regardless of how many frames were
// skipped in between; only the DRAWING rate is capped, never the clock a drawn frame is handed.
function scheduleFrame(callback) {
  window.requestAnimationFrame(function (frameTimeMs) {
    if (wallpaperLastDrawnFrameTimeMs !== null &&
        frameTimeMs - wallpaperLastDrawnFrameTimeMs < wallpaperFrameIntervalMs - WALLPAPER_FRAME_INTERVAL_EPSILON_MS) {
      scheduleFrame(callback);
      return;
    }
    wallpaperLastDrawnFrameTimeMs = frameTimeMs;
    callback(frameTimeMs);
  });
}
