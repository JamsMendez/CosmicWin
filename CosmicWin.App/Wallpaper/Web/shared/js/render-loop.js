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
