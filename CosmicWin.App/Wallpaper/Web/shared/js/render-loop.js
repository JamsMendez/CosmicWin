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

// mini-scene-window T2: the scene variant, read once at load from the same query/hash params as fps
// (`?variant=mini`). "mini" is the small always-on-top corner window: transparent page, no background
// layers, composition fitted to the (square) viewport; the alert overlay is KEPT (alerts show inside the
// window's square, after the edge fade). Anything other than the
// literal "mini" -- a missing param or garbage -- is "full", i.e. the unchanged wallpaper rendering.
//
// Reads BOTH location.search and location.hash (parseParams() only reads the hash when one exists), so
// `index.html?variant=mini#tiles=failed` -- the manual alert hash API -- still selects the mini variant.
// The basis of a mini composition whose disc border (outer radius = `discOuterRadiusFraction` x basis) is
// fitted to `ringRadiusFraction` of the short side. Shared by idle and explorer, whose mini fit is the
// same rule with their own constants; a scene's config.js calls it from its miniSceneBasis (resolved at
// call time, so load order does not matter).
function miniRingBasis(W, H, ringRadiusFraction, discOuterRadiusFraction, minBasisPx) {
  const room = Math.min(W, H) * ringRadiusFraction;
  return Math.max(minBasisPx, room / discOuterRadiusFraction);
}

function readSceneVariantFromUrl() {
  try {
    var raw = String(location.search || "").replace(/^[#?]/, "") + "&" + String(location.hash || "").replace(/^[#?]/, "");
    return new URLSearchParams(raw).get("variant") === "mini" ? "mini" : "full";
  } catch (parseError) {
    return "full";
  }
}

var sceneVariant = readSceneVariantFromUrl();
var isMiniVariant = sceneVariant === "mini";

// mini-scene-window T2c: the mini edge fade. The corner window is see-through, so nothing may reach its
// edge or the square shape shows. After the scene layers (and BEFORE the alert overlay, whose frame is
// rectangular by design) each scene's mini frame multiplies the canvas by a radial alpha mask:
// fully opaque out to MINI_EDGE_FADE_INNER of the short side from the center, easing to fully
// transparent at MINI_EDGE_FADE_OUTER (so every edge pixel and corner is 0). The mini fits keep the
// rings inside the opaque radius (see each scene's config.js). Fractions of min(W, H).
var MINI_EDGE_FADE_INNER = 0.41;
var MINI_EDGE_FADE_OUTER = 0.48;

function applyMiniEdgeFade(context, width, height) {
  var side = Math.min(width, height);
  var outerRadius = side * MINI_EDGE_FADE_OUTER;
  var span = MINI_EDGE_FADE_OUTER - MINI_EDGE_FADE_INNER;
  var cx = width / 2;
  var cy = height / 2;
  var mask = context.createRadialGradient(cx, cy, 0, cx, cy, outerRadius);
  var innerStop = MINI_EDGE_FADE_INNER / MINI_EDGE_FADE_OUTER;
  mask.addColorStop(0, "rgba(0,0,0,1)");
  mask.addColorStop(innerStop, "rgba(0,0,0,1)");
  // Smoothstep-like easing over the band (1 - smoothstep at 25/50/75%).
  [[0.25, 0.84], [0.5, 0.5], [0.75, 0.16]].forEach(function (step) {
    mask.addColorStop(innerStop + (1 - innerStop) * step[0], "rgba(0,0,0," + step[1] + ")");
  });
  mask.addColorStop(1, "rgba(0,0,0,0)");
  context.save();
  context.globalCompositeOperation = "destination-in";
  context.fillStyle = mask;
  context.fillRect(0, 0, width, height);
  context.restore();
}

// mini-scene-window T2j: the occluding base of the processing and raphael mini scenes. The mini window is
// topmost and see-through, so text and icons of the windows behind it read through the scene's structure. This
// fills ONE dark disc (the scenes' own #01040a background at MINI_SCENE_BASE_ALPHA) beneath everything already
// drawn (destination-over: it never lightens, tints or dims the bright content), solid out to solidRadius and
// falling to 0 at falloffRadius, so there is no hard circular edge. Drawn last, right before the edge fade.
// mini-scene-window T2l: the line thickness of the mini central polygons, so processing's octagon reads as thin
// as raphael's hexadecagon. Measured: raphael mini's gold ring stroke is coreRadius(288)=40 * 0.012 * 1.3 = 0.624px
// at rest, drawn magnified by MINI_SCENE_ZOOM 1.2 = 0.749px on screen; both scenes' tests assert their polygon
// against this one number.
var MINI_POLYGON_STROKE_PX = 0.75;

var MINI_SCENE_BASE_RGB = "1,4,10";
var MINI_SCENE_BASE_ALPHA = 0.9;

function drawMiniSceneBase(context, cx, cy, solidRadius, falloffRadius) {
  var fade = context.createRadialGradient(cx, cy, 0, cx, cy, falloffRadius);
  var solid = "rgba(" + MINI_SCENE_BASE_RGB + "," + MINI_SCENE_BASE_ALPHA + ")";
  fade.addColorStop(0, solid);
  fade.addColorStop(solidRadius / falloffRadius, solid);
  fade.addColorStop(1, "rgba(" + MINI_SCENE_BASE_RGB + ",0)");
  context.save();
  context.globalCompositeOperation = "destination-over";
  context.fillStyle = fade;
  context.beginPath();
  context.arc(cx, cy, falloffRadius, 0, Math.PI * 2);
  context.fill();
  context.restore();
}

// The stylesheets key the transparent page background (and the hidden #nebula canvas) off this class.
if (isMiniVariant) {
  try { document.documentElement.classList.add("scene-mini"); } catch (classError) { /* no DOM root */ }
}

// html-wallpaper-demo D6d hardening (three review lenses WARNING, post-merge): the FIRST cut of this
// throttle (dc47db5) skipped a frame whenever less than one interval had passed since the last DRAWN
// frame's own rAF timestamp -- i.e. "draw 1 of every N real display frames", where N depends on the
// DISPLAY's refresh rate, not on wallpaperFrameIntervalMs. That makes the actually-observed draw rate
// a function of the monitor: measured FACT on the maintainer's own 164Hz (6.0976ms/frame) display,
// wallpaper-fps=60 drew every 3rd real frame (~54.7fps, not 60) and wallpaper-fps=30 drew every 6th
// (~27.3fps, not 30); a 75Hz display turned the 60fps cap into 37.5fps. See
// CosmicWin.App.Tests/Wallpaper/Web/processing-scene.tests.js's "Case 9" for the tests that pin this
// down across several simulated refresh rates.
//
// The fix below is a target-time (accumulator) scheduler instead: `wallpaperNextDueFrameTimeMs` is a
// FIXED schedule that always advances by exactly one interval per draw -- never re-derived from "now"
// -- so the AVERAGE drawn rate converges on the configured fps on any refresh rate at or above it
// (every real frame lands on one side or the other of the next fixed due-time, and which side varies
// frame to frame in a way that cancels out over time, instead of a constant N baked in by the first
// real interval it happened to measure).

// The next fixed point in time (rAF's own `ms` timeline) at which a frame is due to be DRAWN. `null`
// means no frame has drawn yet (always draws the very first one -- nothing to be "due" against yet).
var wallpaperNextDueFrameTimeMs = null;

// Tolerates rAF's own ordinary sub-millisecond delivery jitter without ever letting a full extra
// frame slip through: a real interval a hair under the exact target (e.g. 33.29ms against a 33.33ms
// 30fps target) still counts as "enough time passed", the same way a hair OVER never lets two frames
// through where only one fpscap-worth of time has elapsed.
var WALLPAPER_FRAME_INTERVAL_EPSILON_MS = 1;

// The ONE place every scene's render loop schedules its next frame (see this function's own header
// remarks above for what it replaces, and the D6d hardening remarks just above for the pacing rule
// itself). At the default 60fps on a 60Hz-or-slower display this draws every real animation frame,
// same as before this file existed; on any FASTER display (including today's common 120-165Hz
// monitors), or under the 30fps setting, some real frames are SKIPPED: `callback` is not invoked at
// all (so it draws nothing and cannot advance whatever clock it reads from its own `ms` argument, e.g.
// alertSceneMs), but the skip itself still reschedules via requestAnimationFrame -- unconditionally,
// exactly like every scene's own render()/renderFrame() unconditionally calls scheduleFrame as its
// last statement (D2b/D6a fault isolation) -- so the loop never stops. Because a DRAWN frame always
// carries its REAL rAF timestamp, an alert's own duration (measured in wall-clock ms, see
// alert-overlay.js's startShowing/render) still elapses correctly regardless of how many frames were
// skipped in between; only the DRAWING rate is capped, never the clock a drawn frame is handed.
function scheduleFrame(callback) {
  window.requestAnimationFrame(function (frameTimeMs) {
    if (wallpaperNextDueFrameTimeMs !== null &&
        frameTimeMs < wallpaperNextDueFrameTimeMs - WALLPAPER_FRAME_INTERVAL_EPSILON_MS) {
      scheduleFrame(callback);
      return;
    }

    if (wallpaperNextDueFrameTimeMs === null ||
        frameTimeMs - wallpaperNextDueFrameTimeMs >= wallpaperFrameIntervalMs) {
      // Either the very first frame ever (nothing scheduled yet), or the loop fell far behind its
      // own fixed schedule by a full interval or more -- a throttled background tab, a long GC/layout
      // hitch, the wallpaper host briefly not pumping messages, ... Resync to NOW + one interval
      // rather than leaving the old due-time in place, which would otherwise make EVERY frame from
      // here look "overdue" and fire a burst of consecutive catch-up draws until the schedule caught
      // back up to real time.
      wallpaperNextDueFrameTimeMs = frameTimeMs + wallpaperFrameIntervalMs;
    } else {
      // The ordinary case: advance the FIXED schedule by exactly one interval, not to "now + interval"
      // (that would re-base off whichever real frame happened to cross the line, letting the drawn
      // rate drift to whatever the display's refresh rate divides into -- the exact bug this hardening
      // fixes). Keeping the schedule fixed is what makes the AVERAGE drawn rate converge on the
      // configured fps on any display: some real frames land a little early against the next due
      // time, some a little late, and those differences cancel out over time instead of compounding.
      wallpaperNextDueFrameTimeMs += wallpaperFrameIntervalMs;
    }

    callback(frameTimeMs);
  });
}
