// html-wallpaper-demo D2: adapted from docs/great-sage/backgroud-processing/js/main.js (reference-
// only, excluded from git -- see the feature doc, "Source material"). render()'s own layer order and
// the animation-progress math are an intact port; dropped entirely, per "Drop what a wallpaper does
// not need": the invariants self-check (invariants.js is not ported here -- assertSceneInvariants()
// inspects render()'s and drawFailureOverlay()/drawFailureBandIntersections()'s own source text and
// a single global `failureState`, none of which still exist once the alert layer becomes a
// tile-driven, per-kind state machine; see the task report for the full reasoning), the keyboard
// shortcuts (Ctrl+F fullscreen, Ctrl+E/Ctrl+A alert toggles, Ctrl+-/Ctrl++ zoom), and the fullscreen
// toggle itself. The alert overlay is now driven by
// CosmicWin.App/Wallpaper/Web/shared/js/alert-overlay.js's renderAlertOverlay(), called last every
// frame with the SAME progress `p` drawAtomicOrbits used this frame (real bands, not a copy -- see
// that file's own header remarks and js/see-through-hook.js).
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
//
// D6a: resetCanvasStateForFrame/createRenderStageReporter moved to the shared
// CosmicWin.App/Wallpaper/Web/shared/js/render-loop.js (used unchanged by
// CosmicWin.App/Wallpaper/Web/explorer/js/animate.js too) -- see that file's own header remarks for
// the full original reasoning; this file keeps only the scene-specific render(ms) sequence and its
// two try/catch blocks. renderAlertOverlay's own signature also dropped its sceneCx/sceneCy
// parameters (D6a): only this scene's own see-through hook (js/see-through-hook.js) needs a center,
// and it now computes one itself from sceneW/sceneH, the same formula this file always used.

var reportRenderError = createRenderStageReporter("[processing-scene]");

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
  // cx/cy are this scene's own center, used only inside the try block below (the scene's own draw
  // calls); computing them up front changes nothing when the scene succeeds. `p` is a safe, cheap
  // default for the value the alert overlay call needs below: if the scene itself throws before
  // computing its own progress (the try block below), the overlay must still receive SOMETHING rather
  // than a ReferenceError from a half-initialized variable -- the fault-isolation try/catch pair below
  // only isolates each layer's OWN draw calls, not one layer's inputs from the other's failure.
  var cx = W * 0.505;
  var cy = H * 0.515;
  var p = 0;

  try {
    const sceneMs = alertSceneMs(ms);
    // Mini renders the nebula too: transparent WebGL buffer, green only, faded out well before the
    // window edges (see the u_mini branch in nebula.js).
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

    // Mini keeps only the structure (sphere bands, orbits, octagon, prism, rays, core): none of the
    // background layers (soft ovals, stars, streaks, flares, chroma loops), grain or vignette.
    if (!isMiniVariant) {
      drawSoftOvalFields(cx, cy, phase);
      drawStars(cx, cy, p, phase);
      drawRadialStreaks(cx, cy, p);
      drawLensFlares(cx, cy, phase);
      drawChromaticSideLoops(phase);
    }
    drawSegmentedSphere(cx, cy, p);
    drawAtomicOrbits(cx, cy, p);
    drawOrbitBlocks(cx, cy, phase);
    drawCentralOctagon(cx, cy, p, pulse);
    drawTriangularPrism(cx, cy, p);
    drawPerspectiveRays(cx, cy, phase);
    drawCentralCore(cx, cy, phase);

    ctx.restore();

    if (!isMiniVariant) {
      drawFilmGrain(phase);
      drawVignette();
    } else {
      // Mini: fade everything (rays, blocks, bands) out before the window edges; the nebula has its
      // own fade in its shader. Runs before the unmasked alert overlay below.
      // Occlude the background under the whole structure: solid to 0.32 of the short side, easing to 0 at
      // 0.40 (about the widest orbit, 0.385), inside the window fade's opaque radius (0.41).
      drawMiniSceneBase(ctx, cx, cy, Math.min(W, H) * 0.32, Math.min(W, H) * 0.40);
      applyMiniEdgeFade(ctx, W, H);
    }
  } catch (error) {
    resetCanvasStateForFrame();
    reportRenderError("scene", error);
  }

  try {
    // Same `p` drawAtomicOrbits used above (or the safe default 0 above, if the scene just failed) --
    // see js/see-through-hook.js for how this scene's own see-through layer reuses it.
    renderAlertOverlay(ms, W, H, p);
  } catch (error) {
    resetCanvasStateForFrame();
    reportRenderError("alert-overlay", error);
  }

  scheduleFrame(render);
}

initializeNebulaRenderer();
scheduleFrame(render);
