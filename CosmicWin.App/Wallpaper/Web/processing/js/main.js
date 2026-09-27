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
  const sceneMs = alertSceneMs(ms);
  renderNebula(sceneMs);
  const p = animationProgress(sceneMs);
  const phase = p * TAU;
  const pulse = octagonPulse(sceneMs);
  const cx = W * 0.505;
  const cy = H * 0.515;

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
  // Same `p` drawAtomicOrbits used above, in scene (W, H) coordinates -- see alert-overlay.js's
  // drawFailureBandIntersections for how the band-intersection layer reuses it.
  renderAlertOverlay(ms, cx, cy, W, H, p);
  scheduleFrame(render);
}

scheduleFrame(render);
