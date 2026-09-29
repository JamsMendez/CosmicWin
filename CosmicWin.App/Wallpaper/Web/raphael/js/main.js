// html-wallpaper-demo D6b: adapted from docs/great-sage/background-raphael/js/main.js (reference-
// only, excluded from git -- see the feature doc, "Source material"), the same technique
// CosmicWin.App/Wallpaper/Web/processing/index.html (D2) and .../explorer/index.html (D6a) already
// use. Dropped entirely, per "Drop what a wallpaper does not need" (D2/D6a precedent): the
// invariants self-check (js/invariants.js is not ported -- assertSceneInvariants() inspects
// render()'s own source text and the single-overlay failureState, neither of which survives the
// shared, tile-driven overlay -- see the feature doc), the keyboard shortcuts (Ctrl+F fullscreen,
// Ctrl+E/Ctrl+A alert toggles, Ctrl+-/Ctrl++ zoom), the fullscreen toggle itself, and the DEBUG_RING
// query-param path (debug-only, never reached in normal operation per its own source comment). The
// alert overlay is now driven by CosmicWin.App/Wallpaper/Web/shared/js/alert-overlay.js's
// renderAlertOverlay(), called last every frame with the SAME progress `p` drawGlyphRings used this
// frame (the real gold ring, not a copy -- see that file's own header remarks and
// js/see-through-hook.js).
//
// main.js -- the render loop (render(ms)) and startup. Loads last, after every other file.
//
// Fault isolation (D2b/D6a lesson): this page IS the wallpaper, so a single throwing frame must never
// freeze it. render() runs the scene and the alert overlay in their OWN try/catch below (see
// CosmicWin.App/Wallpaper/Web/shared/js/render-loop.js's own header remarks for the full original
// D2b reasoning), and scheduleFrame(render) always runs last, unconditionally, so the loop itself
// never stops. alertSceneMs (shared alert-overlay.js) shares the scene's own try, per the D6a review
// fix (R4-alertSceneMs-outside-fault-isolation): a throw there must only fail THIS frame's "scene"
// stage, never skip scheduleFrame entirely.

var reportRenderError = createRenderStageReporter("[raphael-scene]");

// Progress in base cycles: it advances at a constant speed of one cycle every ANIMATION_CYCLE_DURATION
// seconds, up to ONE_WAY_DURATION seconds, then plays back in reverse (ping-pong).
function animationProgress(ms) {
  return pingpong01((ms / 1000) / ONE_WAY_DURATION) * (ONE_WAY_DURATION / ANIMATION_CYCLE_DURATION);
}

function render(ms) {
  // cx/cy are this scene's own center, used only inside the try block below (the scene's own draw
  // calls); computing them up front changes nothing when the scene succeeds. `p` is a safe, cheap
  // default for the value the alert overlay call needs below -- see processing/js/main.js's own
  // render() for the identical reasoning (a throw before `p` is assigned must still hand the
  // overlay SOMETHING rather than a ReferenceError from a half-initialized variable).
  var cx = W * 0.505;
  var cy = H * 0.515;
  var p = 0;

  try {
    // alertSceneMs (shared/js/alert-overlay.js) freezes this scene's own clock while a FAILED tile is
    // shaking and drives that tile's per-frame shake wobble; it returns `ms` unchanged whenever no
    // kind is currently shaking. D6a lesson (R4-alertSceneMs-outside-fault-isolation): this call must
    // share the scene's own try/catch -- a throw here must only fail this frame's "scene" stage, never
    // skip scheduleFrame(render) entirely.
    const sceneMs = alertSceneMs(ms);
    // Mini has no #nebula WebGL background (the page must stay transparent).
    if (!isMiniVariant) renderNebula(sceneMs);
    p = animationProgress(sceneMs);
    const phase = p * TAU;
    const pulse = hexadecagonPulse(sceneMs);

    ensureSprites();
    ctx.clearRect(0, 0, W, H);

    // No zoom UI on the wallpaper (D6b, same as D2): viewZoom is a constant 1 (see config.js).
    const zoom = isMiniVariant ? MINI_SCENE_ZOOM : viewZoom;
    ctx.save();
    ctx.translate(cx, cy);
    ctx.scale(zoom, zoom);
    ctx.translate(-cx, -cy);

    // Mini keeps only the gold glyph ring, the golden hexadecagon and the central core: no feathers,
    // ovals, stars, streaks, flares, chroma loops, perspective rays, grain, vignette or counters.
    if (!isMiniVariant) {
      drawFeathers(cx, cy, p, phase);
      drawSoftOvalFields(cx, cy, phase);
      drawCircularOvalFields(cx, cy, phase);
      drawStars(cx, cy, p, phase);
      drawRadialStreaks(cx, cy, p);
      drawLensFlares(cx, cy, phase);
      drawChromaticSideLoops(phase);
    }
    drawGlyphRings(cx, cy, p);
    drawGoldenHexadecagon(cx, cy, p, pulse);
    if (!isMiniVariant) drawPerspectiveRays(cx, cy, phase);
    drawCentralCore(cx, cy, phase, pulse);

    ctx.restore();

    if (!isMiniVariant) {
      drawFilmGrain(phase);
      drawVignette();
      drawGlyphCounters(sceneMs);
    }
  } catch (error) {
    resetCanvasStateForFrame();
    reportRenderError("scene", error);
  }

  try {
    // Same `p` drawGlyphRings used above (or the safe default 0, if the scene just failed) -- see
    // js/see-through-hook.js for how this scene's own see-through layer reuses it.
    renderAlertOverlay(ms, W, H, p);
  } catch (error) {
    resetCanvasStateForFrame();
    reportRenderError("alert-overlay", error);
  }

  scheduleFrame(render);
}

scheduleFrame(render);
