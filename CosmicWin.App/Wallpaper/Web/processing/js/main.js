// html-wallpaper-demo D2: adapted from docs/great-sage/backgroud-processing/js/main.js (reference-
// only, excluded from git -- see the feature doc, "Source material"). render()'s own layer order and
// the animation-progress math are an intact port; dropped entirely, per "Drop what a wallpaper does
// not need": the invariants self-check (invariants.js is not ported here -- assertSceneInvariants()
// inspects render()'s and drawFailureOverlay()/drawFailureBandIntersections()'s own source text and
// a single global `failureState`, none of which still exist once the alert layer becomes a
// tile-driven, per-kind state machine; see the task report for the full reasoning), the keyboard
// shortcuts (Ctrl+F fullscreen, Ctrl+E/Ctrl+A alert toggles, Ctrl+-/Ctrl++ zoom), and the fullscreen
// toggle itself. The alert overlay is now driven by js/alert-overlay.js's renderAlertOverlay(), called
// last every frame with the SAME progress `p` drawAtomicOrbits used this frame (real bands, not a
// copy -- see alert-overlay.js's own header remarks).
//
// main.js — the render loop (render(ms)) and startup. Loads last, after every other file.
//
// D2b (html-wallpaper-demo, R4-render-loop-no-fault-isolation): this page IS the wallpaper (D3), so a
// single throwing frame used to freeze it forever -- scheduleFrame(render) was render()'s LAST
// statement, never reached once anything above it threw. render() now runs the scene and the alert
// overlay in their OWN try/catch below, so a failure in one cannot take the other down (both still
// share the one canvas, so "cannot" is only as far as practical -- see resetCanvasStateForFrame's own
// remarks), and scheduleFrame(render) always runs afterwards, unconditionally, so the loop itself
// never stops.

// One remembered message PER STAGE -- see reportRenderError below for why a single shared slot floods.
var lastRenderErrorMessageByStage = {};

// A throwing frame can leave the 2D context's own save/restore stack, transform and globalAlpha/
// composite mode -- and, separately, the shake CSS transform applyFailureShake sets on the canvas
// element itself -- in whatever state the failing draw call left them, which would otherwise corrupt
// every later frame (a stray clip, an alpha stuck below 1, a shake transform frozen mid-shake). There
// is no way to query the 2D context's own save/restore stack depth, so restore() defensively, several
// times more than this page's own deepest save()/restore() nesting (drawFailureOverlay's own pairs,
// well under this): restore() on an empty stack is a documented no-op (HTML Canvas 2D spec), so this
// is always safe, even for a frame that never called save() at all.
function resetCanvasStateForFrame() {
  for (var i = 0; i < 16; i++) {
    try { ctx.restore(); } catch (restoreError) { /* no-op: nothing left to restore */ }
  }
  try { ctx.setTransform(canvasScaleX, 0, 0, canvasScaleY, 0, 0); } catch (transformError) { /* ctx gone */ }
  try { ctx.globalAlpha = 1; } catch (alphaError) { /* ctx gone */ }
  try { ctx.globalCompositeOperation = "source-over"; } catch (compositeError) { /* ctx gone */ }
  try { applyFailureShake(null); } catch (shakeError) { /* ctx/canvas gone */ }
}

// Reports a throwing frame once per DISTINCT message, not once per frame -- a failing layer can run at
// up to 60fps, and without this guard the same error would flood the console 60x/sec instead of being
// reported once, the way a wallpaper host actually needs to see it. Deduped PER STAGE, in
// lastRenderErrorMessageByStage[stage] -- render()'s two try/catch blocks below call this with two
// fixed stage names ("scene", "alert-overlay"), so the map holds at most one entry per call site (two,
// today) no matter how many distinct messages a stage ever throws: each new call for a stage simply
// OVERWRITES that stage's one remembered message, it never accumulates a history, so this cannot grow
// unbounded even if a message embeds changing data (a counter, a timestamp). A single SHARED slot used
// to compare a stage's message against whichever stage reported last: with the scene and the overlay
// BOTH failing every frame, the remembered message alternates scene/overlay/scene/overlay, so neither
// throw ever matches what was remembered a moment ago and console.error fires every frame for EACH
// stage -- the exact flood this guard exists to stop. One slot per stage fixes that: a stage's repeats
// are only ever compared against that SAME stage's own last message. If a stage's error stops for a
// frame (recovers) and then returns with the SAME text, it stays suppressed, by design -- only a
// genuinely different message for that stage (or a different stage) is reported again.
function reportRenderError(stage, error) {
  var message = stage + ": " + (error && error.message ? error.message : String(error));
  if (lastRenderErrorMessageByStage[stage] === message) return;
  lastRenderErrorMessageByStage[stage] = message;
  console.error("[processing-scene] render frame failed (" + stage + ")", error);
}

function octagonPulse(ms) {
  const duration = OCTAGON_PULSE_DURATION * 1000;
  const elapsed = ms % (OCTAGON_PULSE_INTERVAL * 1000);
  if (elapsed <= 0 || elapsed >= duration) return 0;
  return Math.sin(Math.PI * elapsed / duration);
}

// Progress in base cycles: it advances at a constant speed of one cycle every ANIMATION_CYCLE_DURATION
// seconds, up to ONE_WAY_DURATION seconds, then plays back in reverse (ping-pong).
function animationProgress(ms) {
  return pingpong01((ms / 1000) / ONE_WAY_DURATION) * (ONE_WAY_DURATION / ANIMATION_CYCLE_DURATION);
}

function render(ms) {
  // Safe, cheap defaults for the values the alert overlay call needs below: if the scene itself throws
  // before computing its own cx/cy/p (the try block below), the overlay must still receive SOMETHING
  // rather than a ReferenceError from a half-initialized variable -- the fault-isolation try/catch
  // pair below only isolates each layer's OWN draw calls, not one layer's inputs from the other's
  // failure. cx/cy only ever depend on W/H, so computing them up front changes nothing when the scene
  // succeeds.
  var cx = W * 0.505;
  var cy = H * 0.515;
  var p = 0;

  try {
    const sceneMs = alertSceneMs(ms);
    renderNebula(sceneMs);
    p = animationProgress(sceneMs);
    const phase = p * TAU;
    const pulse = octagonPulse(sceneMs);

    ensureSprites();
    ctx.clearRect(0, 0, W, H);

    // No zoom UI on the wallpaper (html-wallpaper-demo D2): viewZoom is a constant 1 (see config.js).
    const zoom = viewZoom;
    ctx.save();
    ctx.translate(cx, cy);
    ctx.scale(zoom, zoom);
    ctx.translate(-cx, -cy);

    drawSoftOvalFields(cx, cy, phase);
    drawStars(cx, cy, p, phase);
    drawRadialStreaks(cx, cy, p);
    drawLensFlares(cx, cy, phase);
    drawChromaticSideLoops(phase);
    drawSegmentedSphere(cx, cy, p);
    drawAtomicOrbits(cx, cy, p);
    drawOrbitBlocks(cx, cy, phase);
    drawCentralOctagon(cx, cy, p, pulse);
    drawTriangularPrism(cx, cy, p);
    drawPerspectiveRays(cx, cy, phase);
    drawCentralCore(cx, cy, phase);

    ctx.restore();

    drawFilmGrain(phase);
    drawVignette();
  } catch (error) {
    resetCanvasStateForFrame();
    reportRenderError("scene", error);
  }

  try {
    // Same `p` drawAtomicOrbits used above (or the safe default 0 above, if the scene just failed), in
    // scene (W, H) coordinates -- see alert-overlay.js's drawFailureBandIntersections for how the
    // band-intersection layer reuses it.
    renderAlertOverlay(ms, cx, cy, W, H, p);
  } catch (error) {
    resetCanvasStateForFrame();
    reportRenderError("alert-overlay", error);
  }

  scheduleFrame(render);
}

scheduleFrame(render);
