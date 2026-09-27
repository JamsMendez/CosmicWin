"use strict";

// html-wallpaper-demo D2: the alert layer, ported from
// docs/great-sage/backgroud-processing/js/failure-overlay.js (reference-only, excluded from git --
// see the feature doc, "Source material") and re-driven by the SAME external contract
// CosmicWin.App/Alerts/Web/alert-layer.js exposes to WebViewAlertLayerController.cs: the same hash
// parameters (tiles=/columns=/rows=/gap=/duration=/work=, and the old kind=/duration= form) and the
// same two web messages ({type:"show",...}/{type:"hide"} in, "ready"/"done" out).
//
// Unlike alert-layer.js (a separate, transparent WebView2 page composited ABOVE the video, which can
// only draw a COPY of the band geometry), this file draws directly into the SAME canvas/ctx the scene
// itself uses, from inside the SAME render loop (js/main.js's render(ms) calls renderAlertOverlay()
// last, right after the scene's own drawAtomicOrbits call) -- so drawFailureBandIntersections below
// calls the scene's own foldingBandParameters/foldingBandGeometry (js/layers.js) with the exact same
// progress `p` the scene used THIS frame. What shows through the FAILED/WARNING letters is the real
// animation, not a copy.
//
// Mosaic tiles still size their own wash/letters/modules against THEIR OWN width/height, exactly like
// alert-layer.js's renderTile does (temporarily repurposing the shared W/H globals) -- but the band
// intersections must NOT use tile-local W/H: foldingBandParameters/foldingBandGeometry derive their
// radii and segment count from the SCENE's own minD, so band geometry stays correctly scaled to the
// real, full-screen bands. drawFailureBandIntersections restores the scene's W/H just for that one
// call (see the sceneW/sceneH save/restore below) and positions the scene's own center relative to
// each tile's own origin -- since the bands are centered on the whole screen, a tile only shows
// whichever bands actually cross it, exactly as the feature doc's "Coverage caveat" describes.

var FAILURE_TITLE_FONT = '"Archivo Black", "Arial Black", "Helvetica Neue", sans-serif';
// Fetch the title face up front; the canvas falls back to Arial Black until it arrives.
if (typeof document !== "undefined" && document.fonts && document.fonts.load) {
  document.fonts.load("400 64px " + FAILURE_TITLE_FONT).catch(function () {});
}

// Offscreen layers, tile-sized (device pixels) except 'backdrop' which is captured ONCE per frame at
// the FULL canvas size (see captureBackdropIfNeeded) -- source's slot numbering (0 letters,
// 1 intersections, 2 full overlay, 3 backdrop pixels, 4 backdrop, 5 overlay pixels) is kept as
// string keys here since a mosaic frame can have a FAILED tile and a WARNING tile revealing at once,
// each needing its own 'overlay'/'overlayPixels' buffer (see renderAlertTile/drawFailureLayer).
var failureLayers = {};

function failureLayer(slot, pixelWidth, pixelHeight, sceneTransform) {
  if (sceneTransform === undefined) sceneTransform = true;
  if (!failureLayers[slot]) {
    var layer = document.createElement("canvas");
    failureLayers[slot] = { canvas: layer, g: layer.getContext("2d") };
  }
  var entry = failureLayers[slot];
  if (entry.canvas.width !== pixelWidth || entry.canvas.height !== pixelHeight) {
    entry.canvas.width = pixelWidth;
    entry.canvas.height = pixelHeight;
  }
  entry.g.setTransform(1, 0, 0, 1, 0, 0);
  entry.g.globalCompositeOperation = "source-over";
  entry.g.globalAlpha = 1;
  entry.g.clearRect(0, 0, pixelWidth, pixelHeight);
  if (sceneTransform) entry.g.setTransform(canvasScaleX, 0, 0, canvasScaleY, 0, 0);
  return entry.g;
}

// ---- Title / wash / modules: tile-local, unchanged from the source overlay ---------------------
// These three read the shared W/H globals directly, exactly like the source page did -- renderAlertTile
// temporarily repurposes W/H to the CURRENT TILE's own CSS size before calling into drawFailureOverlay,
// the same technique alert-layer.js's renderTile already uses for its own mosaic.

function drawFailureTitle(g, frame, title) {
  g.font = "400 100px " + FAILURE_TITLE_FONT;
  var fontSize = 100 * (frame.w * 0.97) / Math.max(1, g.measureText(title).width);
  var capHeight = fontSize * 0.72;
  g.font = "400 " + fontSize + "px " + FAILURE_TITLE_FONT;
  g.textAlign = "center";
  g.textBaseline = "alphabetic";

  g.save();
  g.beginPath();
  g.rect(frame.x, frame.y, frame.w, H * 0.23);
  g.clip();
  g.translate(0, frame.y - capHeight * 0.42);
  g.scale(1, -1);
  g.fillText(title, W * 0.5, 0);
  g.restore();

  g.save();
  g.beginPath();
  g.rect(frame.x, frame.y + frame.h - H * 0.23, frame.w, H * 0.23);
  g.clip();
  g.fillText(title, W * 0.5, frame.y + frame.h + capHeight * 0.42);
  g.restore();
}

function failureRails(frame) {
  var railH = Math.max(6, H * 0.012);
  var rails = [];
  var railYs = [frame.y * 0.72, frame.y + frame.h + (H - frame.y - frame.h) * 0.28];
  var spans = [[0.09, 0.35], [0.65, 0.91]];
  for (var i = 0; i < railYs.length; i++) {
    for (var j = 0; j < spans.length; j++) {
      var from = spans[j][0], to = spans[j][1];
      rails.push([W * from, railYs[i] - railH * 0.5, W * (to - from), railH]);
    }
  }
  return rails;
}

function drawFailureModules(g, frame, counter, overlayBits) {
  var minD = Math.min(W, H);
  var boxW = Math.max(16, W * 0.018);
  var gap = H * 0.012;
  var pad = boxW * 0.35;
  var longestLabel = "00:99 " + overlayBits.slice(0, 8);

  g.font = "700 100px ui-monospace, SFMono-Regular, Consolas, monospace";
  var labelPerPx = g.measureText(longestLabel).width / 100;
  var digitSize = Math.min(boxW * 0.62, (frame.h * 0.92 - gap * 3) / 4 / labelPerPx - pad * 2 / labelPerPx);
  var boxH = labelPerPx * digitSize + pad * 2;
  var stackTop = H * 0.5 - (boxH * 4 + gap * 3) * 0.5;

  g.lineWidth = Math.max(1, minD * 0.0018);
  g.font = "700 " + digitSize + "px ui-monospace, SFMono-Regular, Consolas, monospace";
  g.textAlign = "center";
  g.textBaseline = "middle";

  var columns = [frame.x * 0.62, frame.x + frame.w + (W - frame.x - frame.w) * 0.38];
  for (var column = 0; column < columns.length; column++) {
    var centerX = columns[column];
    for (var box = 0; box < 4; box++) {
      var x = centerX - boxW * 0.5;
      var y = stackTop + box * (boxH + gap);
      g.fillStyle = "rgb(8,10,22)";
      g.fillRect(x, y, boxW, boxH);
      g.strokeStyle = "rgba(170,190,235,0.85)";
      g.strokeRect(x, y, boxW, boxH);
      var bitOffset = (column * 4 + box) * 8;
      var bits = "";
      for (var i = 0; i < 8; i++) {
        bits += overlayBits[(bitOffset + i) % overlayBits.length];
      }
      g.save();
      g.translate(centerX, y + boxH * 0.5);
      g.rotate(column === 0 ? -Math.PI / 2 : Math.PI / 2);
      g.fillStyle = "rgb(210,220,255)";
      g.fillText("00:" + counter + " " + bits, 0, 0);
      g.restore();
    }
  }
}

// ---- Band intersections: SCENE coordinates, not tile-local (html-wallpaper-demo D2) -------------
// tileCx/tileCy are the scene's own center, expressed relative to the CURRENT TILE's own origin
// (sceneCx - tile.x, sceneCy - tile.y); sceneW/sceneH are the scene's true CSS size, saved by the
// caller (renderAlertTile) BEFORE it repurposed W/H for this tile's own title/wash/modules layout.
function drawFailureBandIntersections(g, letters, tileCx, tileCy, sceneW, sceneH, progress, color) {
  var tileW = W, tileH = H;
  W = sceneW;
  H = sceneH;
  try {
    g.save();
    g.translate(tileCx, tileCy);
    g.scale(viewZoom, viewZoom);
    var bands = foldingBandParameters(progress);
    for (var i = 0; i < bands.length; i++) {
      var rx = bands[i][0], ry = bands[i][1], rot = bands[i][2], width = bands[i][3], foldPhase = bands[i][4];
      g.save();
      g.rotate(rot);
      fillFoldingBandGeometry(g, foldingBandGeometry(rx, ry, width, foldPhase), color);
      g.restore();
    }
    g.restore();
  } finally {
    W = tileW;
    H = tileH;
  }
  g.save();
  g.setTransform(1, 0, 0, 1, 0, 0);
  g.globalCompositeOperation = "destination-in";
  g.drawImage(letters, 0, 0);
  g.restore();
}

function drawFailureOverlay(g, tileCx, tileCy, sceneW, sceneH, progress, counter, theme, tileDeviceW, tileDeviceH) {
  var minD = Math.min(W, H);
  var frame = { x: W * 0.038, y: H * 0.064 };
  frame.w = W - frame.x * 2;
  frame.h = H - frame.y * 2;
  var rails = failureRails(frame);

  g.save();
  g.beginPath();
  g.rect(0, 0, W, H);
  for (var i = 0; i < rails.length; i++) g.rect(rails[i][0], rails[i][1], rails[i][2], rails[i][3]);
  g.fillStyle = theme.wash;
  g.fill("evenodd");
  g.save();
  g.globalCompositeOperation = "difference";
  g.fillStyle = "rgba(255,255,255,0.9)";
  for (var r = 0; r < rails.length; r++) g.fillRect(rails[r][0], rails[r][1], rails[r][2], rails[r][3]);
  g.restore();

  var letters = failureLayer("letters", tileDeviceW, tileDeviceH);
  letters.fillStyle = theme.letters;
  drawFailureTitle(letters, frame, theme.title);
  g.save();
  g.shadowColor = "rgba(0,0,0,0.8)";
  g.shadowBlur = minD * 0.03 * canvasScaleX;
  g.shadowOffsetY = minD * 0.008 * canvasScaleY;
  g.globalAlpha = 0.86;
  g.drawImage(letters.canvas, 0, 0, W, H);
  g.restore();

  var intersections = failureLayer("intersections", tileDeviceW, tileDeviceH);
  drawFailureBandIntersections(intersections, letters.canvas, tileCx, tileCy, sceneW, sceneH, progress, theme.intersections);
  g.globalAlpha = 0.95;
  g.drawImage(intersections.canvas, 0, 0, W, H);
  g.globalAlpha = 1;

  g.strokeStyle = "rgba(255,255,255,0.9)";
  g.lineWidth = Math.max(2, minD * 0.004);
  g.strokeRect(frame.x, frame.y, frame.w, frame.h);
  drawFailureModules(g, frame, counter, theme.bits);
  g.restore();
}

// Aggressive, decaying shake of the whole canvas -- ported unchanged from the source overlay. This
// wallpaper has no separate video layer to shake (unlike alert-layer.js, which dropped the shake
// entirely because T4 shook the video natively): the canvas IS the wallpaper, so a FAILED alert
// shakes it directly, same as the source page always did.
function applyFailureShake(elapsed) {
  var transform = "";
  if (elapsed !== null) {
    var decay = 1 - elapsed / FAILURE_SHAKE_MS;
    var amplitude = Math.min(W, H) * (0.5 + 0.5 * decay);
    var t = elapsed / 1000;
    var dx = amplitude * 0.1 * (Math.sin(t * 131) * 0.65 + Math.sin(t * 211 + 1.3) * 0.35);
    var dy = amplitude * 0.04 * (Math.sin(t * 109 + 2.1) * 0.6 + Math.sin(t * 197 + 0.4) * 0.4);
    var angle = 3.5 * decay * Math.sin(t * 89 + 0.7);
    transform = "translate(" + dx.toFixed(2) + "px, " + dy.toFixed(2) + "px) rotate(" + angle.toFixed(3) + "deg) scale(1.18)";
  }
  canvas.style.transform = transform;
  if (nebulaCanvas) nebulaCanvas.style.transform = transform;
}

// Downscale `source`'s (sourceX, sourceY, sourceW, sourceH) device-pixel region by `cell` and draw it
// back over the tile's own device rect with hard pixel edges. `source` is either the whole-canvas
// backdrop snapshot (cropped to this tile) or this tile's own already-tile-sized overlay buffer.
function drawTilePixelated(source, sourceX, sourceY, sourceW, sourceH, cell, slot, alpha, tileDeviceX, tileDeviceY, tileDeviceW, tileDeviceH) {
  var pixelWidth = Math.max(1, Math.ceil(tileDeviceW / cell));
  var pixelHeight = Math.max(1, Math.ceil(tileDeviceH / cell));
  var pixels = failureLayer(slot, pixelWidth, pixelHeight, false);
  pixels.imageSmoothingEnabled = true;
  pixels.clearRect(0, 0, pixelWidth, pixelHeight);
  pixels.drawImage(source, sourceX, sourceY, sourceW, sourceH, 0, 0, pixelWidth, pixelHeight);
  ctx.save();
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.imageSmoothingEnabled = false;
  ctx.globalAlpha = alpha;
  ctx.drawImage(pixels.canvas, tileDeviceX, tileDeviceY, tileDeviceW, tileDeviceH);
  ctx.restore();
}

// ---- State machine: hidden -> shaking (failed only) -> revealing -> shown, PER KIND -------------
// One small state machine per kind (not per tile instance): every tile of the same kind started at
// the same ms and shares the same theme, so they always report the identical state/counter at any
// given ms -- same design alert-layer.js's mosaic already uses.

var kindState = {
  failed: { state: "hidden", startMs: null },
  warning: { state: "hidden", startMs: null },
};
var scenePausedMs = 0;

function resetKindState() {
  kindState = {
    failed: { state: "hidden", startMs: null },
    warning: { state: "hidden", startMs: null },
  };
}

// Idempotent per ms: every transition is guarded by the CURRENT state, so calling this more than
// once for the same (ms, kind) -- once early from alertSceneMs, once again from renderAlertOverlay's
// own per-tile loop -- never double-applies a transition (see alertSceneMs's own remarks).
function advanceKindState(ms, kind) {
  var entry = kindState[kind];
  var theme = FAILURE_OVERLAY_THEMES[kind];
  if (entry.startMs === null) {
    entry.startMs = ms;
    entry.state = "shaking";
  }
  if (entry.state === "shaking" && ms - entry.startMs >= theme.shakeMs) {
    scenePausedMs += theme.shakeMs;
    entry.state = "revealing";
    if (kind === "failed") applyFailureShake(null);
  }
  if (entry.state === "revealing" && ms - entry.startMs >= theme.shakeMs + FAILURE_REVEAL_MS) {
    entry.state = "shown";
  }
}

// Called FIRST, before the scene renders this frame (js/main.js's render()) -- mirrors the source
// overlay's advanceFailureState: while a FAILED tile is shaking, the scene's own clock (and the
// nebula/animation progress derived from it) freezes, and the canvas visibly shakes. Only 'failed'
// ever has a non-zero shakeMs (see FAILURE_OVERLAY_THEMES), so at most one kind actually shakes/pauses
// at a time -- same assumption the source's single-overlay design made.
function alertSceneMs(ms) {
  if (kindState.failed.startMs !== null) advanceKindState(ms, "failed");
  if (kindState.warning.startMs !== null) advanceKindState(ms, "warning");
  if (kindState.failed.state === "shaking") {
    applyFailureShake(ms - kindState.failed.startMs);
    return kindState.failed.startMs - scenePausedMs;
  }
  return ms - scenePausedMs;
}

// ---- Message-driven show/hide API (same contract as alert-layer.js) -----------------------------

var animating = false;
var doneSignaled = false;
var durationMs = 5000;
var showStartMs = null;

var tiles = ["warning"]; // wire kinds ("failed"/"warning"), one per slot
var gridColumns = 1;
var gridRows = 1;
var gapPx = 0; // PHYSICAL pixels, as posted by the host -- converted to CSS pixels below

var workAreaLeft = 0;
var workAreaTop = 0;
var workAreaWidth = 0;
var workAreaHeight = 0;

// The rect (CSS pixels) the N>1 grid is laid out inside: the work area, converted with the same
// devicePixelRatio-derived scale resize() already uses, and clamped to the canvas -- or the whole
// canvas when the work area is absent/degenerate. Reads W/H/canvasScaleX/Y at call time, which is
// always the TRUE scene size: renderAlertOverlay always calls this BEFORE repurposing W/H per tile.
function gridAreaRect() {
  if (workAreaWidth <= 0 || workAreaHeight <= 0) {
    return { x: 0, y: 0, w: W, h: H };
  }
  var x = Math.max(0, Math.min(W, workAreaLeft / canvasScaleX));
  var y = Math.max(0, Math.min(H, workAreaTop / canvasScaleY));
  var w = Math.max(1, Math.min(W - x, workAreaWidth / canvasScaleX));
  var h = Math.max(1, Math.min(H - y, workAreaHeight / canvasScaleY));
  return { x: x, y: y, w: w, h: h };
}

function tileRects() {
  if (tiles.length <= 1) {
    return [{ x: 0, y: 0, w: W, h: H }];
  }
  var area = gridAreaRect();
  var gapCssX = gapPx / canvasScaleX;
  var gapCssY = gapPx / canvasScaleY;
  var cellW = Math.max(1, (area.w - gapCssX * (gridColumns + 1)) / gridColumns);
  var cellH = Math.max(1, (area.h - gapCssY * (gridRows + 1)) / gridRows);
  var rects = [];
  for (var i = 0; i < tiles.length; i++) {
    var col = i % gridColumns;
    var row = Math.floor(i / gridColumns);
    rects.push({
      x: area.x + gapCssX + col * (cellW + gapCssX),
      y: area.y + gapCssY + row * (cellH + gapCssY),
      w: cellW,
      h: cellH,
    });
  }
  return rects;
}

function postToHost(message) {
  // Guarded: this same file also opens in a plain browser tab, where window.chrome.webview does
  // not exist -- the manual check the feature doc asks for.
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.postMessage(message);
  }
}

// Unlike alert-layer.js's stopAndClear (which owns a transparent, alert-only canvas and clears it),
// this page's canvas is shared with the scene: stopping the overlay must never clear the canvas --
// the scene's own render() already clears/redraws it every frame regardless of alert state.
function stopOverlay() {
  animating = false;
  applyFailureShake(null);
}

function hide() {
  stopOverlay();
  showStartMs = null;
  resetKindState();
}

function signalDoneIfElapsed(ms) {
  if (doneSignaled || showStartMs === null || ms - showStartMs < durationMs) {
    return false;
  }
  doneSignaled = true;
  postToHost("done");
  return true;
}

function startShowing(newTiles, columns, rows, gap, duration, workArea) {
  tiles = Array.isArray(newTiles) && newTiles.length > 0
    ? newTiles.map(function (tile) { return tile === "failed" ? "failed" : "warning"; })
    : ["warning"];
  gridColumns = Math.max(1, Math.floor(columns) || 1);
  gridRows = Math.max(1, Math.floor(rows) || 1);
  gapPx = Math.max(0, gap || 0);
  if (workArea && isFinite(workArea.width) && isFinite(workArea.height)
    && workArea.width > 0 && workArea.height > 0) {
    workAreaLeft = Math.max(0, Number(workArea.left) || 0);
    workAreaTop = Math.max(0, Number(workArea.top) || 0);
    workAreaWidth = Math.max(0, Number(workArea.width) || 0);
    workAreaHeight = Math.max(0, Number(workArea.height) || 0);
  } else {
    workAreaLeft = 0;
    workAreaTop = 0;
    workAreaWidth = 0;
    workAreaHeight = 0;
  }
  durationMs = isFinite(duration) && duration > 0 ? duration : 5000;
  showStartMs = null;
  resetKindState();
  doneSignaled = false;
  // No separate render loop to (re)start (unlike alert-layer.js): the scene's own js/main.js
  // render() loop is already running continuously, and picks up renderAlertOverlay() on the next
  // frame purely because `animating` is now true.
  animating = true;
}

function handleHostMessage(event) {
  var data = event.data;
  if (!data || typeof data !== "object") return;
  if (data.type === "show") {
    if (Array.isArray(data.tiles)) {
      startShowing(
        data.tiles, Number(data.columns), Number(data.rows), Number(data.gap), Number(data.duration),
        data.workArea);
    } else {
      startShowing([data.kind === "failed" ? "failed" : "warning"], 1, 1, 0, Number(data.duration));
    }
  } else if (data.type === "hide") {
    hide();
  }
}

if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener("message", handleHostMessage);
}

// ---- Rendering: called once per animation frame from js/main.js's render(), AFTER the scene -----

// Whole-canvas backdrop snapshot, captured at most once per frame, only when at least one visible
// tile's theme actually pixelates it (only 'failed' does, see FAILURE_OVERLAY_THEMES) -- mirrors the
// source overlay's own per-frame-once capture, generalized to run before the FIRST tile that needs it
// instead of unconditionally (this page draws every frame; alert-layer.js's separate canvas did not).
function captureBackdropIfNeeded() {
  var needed = false;
  for (var i = 0; i < tiles.length; i++) {
    var entry = kindState[tiles[i]];
    if (FAILURE_OVERLAY_THEMES[tiles[i]].pixelateBackdrop && entry.state !== "hidden" && entry.state !== "shaking") {
      needed = true;
      break;
    }
  }
  if (!needed) return null;
  var backdrop = failureLayer("backdrop", canvas.width, canvas.height, false);
  backdrop.drawImage(canvas, 0, 0);
  return backdrop;
}

// Tile-scoped replacement for the source overlay's drawFailureLayer: `sceneCx`/`sceneCy`/`sceneW`/
// `sceneH` are the scene's own center/size (untouched by any tile), `tileDeviceX/Y/W/H` are this
// tile's own device-pixel rect within the whole canvas (needed only by drawTilePixelated's backdrop
// crop), and W/H are ALREADY this tile's own CSS size (set by the caller, renderAlertTile).
function drawFailureLayer(ms, kind, sceneCx, sceneCy, sceneW, sceneH, progress, tileDeviceX, tileDeviceY, tileDeviceW, tileDeviceH, backdrop) {
  var entry = kindState[kind];
  if (entry.state === "hidden" || entry.state === "shaking") return;
  var theme = FAILURE_OVERLAY_THEMES[kind];
  var shownMs = Math.max(0, ms - entry.startMs - theme.shakeMs);
  var counter = Math.floor(shownMs / FAILURE_COUNTER_STEP_MS) % 100;
  var backdropCell = Math.max(1, Math.round(FAILURE_BACKDROP_CELL * canvasScaleX));

  if (entry.state === "shown") {
    if (backdrop && theme.pixelateBackdrop) {
      drawTilePixelated(backdrop.canvas, tileDeviceX, tileDeviceY, tileDeviceW, tileDeviceH,
        backdropCell, "backdropPixels", 1, tileDeviceX, tileDeviceY, tileDeviceW, tileDeviceH);
    }
    drawFailureOverlay(ctx, sceneCx, sceneCy, sceneW, sceneH, progress, counter, theme, tileDeviceW, tileDeviceH);
    return;
  }

  var t = Math.min(1, shownMs / FAILURE_REVEAL_MS);
  var eased = 1 - Math.pow(1 - t, 3);
  var lineT = Math.min(1, t / FAILURE_REVEAL_LINE_SHARE);
  var openT = Math.max(0, (t - FAILURE_REVEAL_LINE_SHARE) / (1 - FAILURE_REVEAL_LINE_SHARE));
  var cell = Math.max(1, Math.round(FAILURE_REVEAL_MAX_CELL * Math.pow(1 - eased, 1.4)));
  var overlay = failureLayer("overlay:" + kind, tileDeviceW, tileDeviceH);
  drawFailureOverlay(overlay, sceneCx, sceneCy, sceneW, sceneH, progress, counter, theme, tileDeviceW, tileDeviceH);

  var revealW = W * (1 - Math.pow(1 - lineT, 2));
  var revealH = Math.max(H * 0.04, H * (1 - Math.pow(1 - openT, 3)));
  ctx.save();
  ctx.beginPath();
  ctx.rect((W - revealW) * 0.5, (H - revealH) * 0.5, revealW, revealH);
  ctx.clip();
  if (backdrop && theme.pixelateBackdrop) {
    drawTilePixelated(backdrop.canvas, tileDeviceX, tileDeviceY, tileDeviceW, tileDeviceH,
      Math.max(backdropCell, cell), "backdropPixels", 1, tileDeviceX, tileDeviceY, tileDeviceW, tileDeviceH);
  }
  drawTilePixelated(overlay.canvas, 0, 0, tileDeviceW, tileDeviceH,
    cell, "overlayPixels:" + kind, 0.3 + 0.7 * eased, tileDeviceX, tileDeviceY, tileDeviceW, tileDeviceH);
  ctx.restore();
}

// Draws ONE tile: repurposes the shared W/H to this tile's own CSS size (same technique
// alert-layer.js's renderTile uses), clips/translates ctx to the tile's rect so drawFailureOverlay's
// title/wash/modules draw exactly as if this tile were the whole canvas -- then restores W/H so the
// NEXT tile (or the next frame's scene render) sees the true scene size again.
function renderAlertTile(rect, kind, ms, sceneCx, sceneCy, sceneW, sceneH, progress, backdrop) {
  var tileW = W, tileH = H;
  W = rect.w;
  H = rect.h;
  var tileDeviceX = Math.round(rect.x * canvasScaleX);
  var tileDeviceY = Math.round(rect.y * canvasScaleY);
  var tileDeviceW = Math.round(rect.w * canvasScaleX);
  var tileDeviceH = Math.round(rect.h * canvasScaleY);

  ctx.save();
  ctx.beginPath();
  ctx.rect(rect.x, rect.y, rect.w, rect.h);
  ctx.clip();
  ctx.translate(rect.x, rect.y);
  drawFailureLayer(ms, kind, sceneCx - rect.x, sceneCy - rect.y, sceneW, sceneH, progress,
    tileDeviceX, tileDeviceY, tileDeviceW, tileDeviceH, backdrop);
  ctx.restore();

  W = tileW;
  H = tileH;
}

// Called once per animation frame from js/main.js's render(), with the SAME sceneCx/sceneCy/progress
// the scene's own drawAtomicOrbits call used this frame -- see this file's header remarks.
function renderAlertOverlay(ms, sceneCx, sceneCy, sceneW, sceneH, progress) {
  if (!animating) return;
  if (showStartMs === null) showStartMs = ms;
  var rects = tileRects();
  var backdrop = captureBackdropIfNeeded();

  for (var i = 0; i < tiles.length && i < rects.length; i++) {
    advanceKindState(ms, tiles[i]);
    renderAlertTile(rects[i], tiles[i], ms, sceneCx, sceneCy, sceneW, sceneH, progress, backdrop);
  }

  if (signalDoneIfElapsed(ms)) {
    stopOverlay();
  }
}

// ---- Hash API (manual check, same shape as alert-layer.js) ---------------------------------------
// processing/index.html#tiles=failed,warning&columns=2&rows=1&gap=8&duration=5000
// Old single-tile form, still supported: processing/index.html#kind=failed&duration=5000
// Optional work area (tiles form only): #tiles=...&work=L,T,W,H (physical pixels).
// A preloaded host page (T9c-equivalent, see WebViewAlertLayerController) navigates with NO
// hash/query at all, so this must NOT auto-show: only an explicit tiles/kind/duration param starts
// the layer; otherwise the scene renders alone until a "show" message arrives.

function parseParams() {
  var raw = (location.hash || location.search || "").replace(/^[#?]/, "");
  return new URLSearchParams(raw);
}

function parseWorkAreaParam(params) {
  if (!params.has("work")) return undefined;
  var parts = params.get("work").split(",").map(Number);
  if (parts.length !== 4 || parts.some(function (n) { return !isFinite(n); })) return undefined;
  return { left: parts[0], top: parts[1], width: parts[2], height: parts[3] };
}

var alertOverlayParams = parseParams();
var alertOverlayHasExplicitParams =
  alertOverlayParams.has("kind") || alertOverlayParams.has("duration") || alertOverlayParams.has("tiles");

// Tells the host a live, running script exists on the other end of the bridge -- not just that
// navigation completed -- before it trusts a pending "show" was actually received.
postToHost("ready");

if (alertOverlayHasExplicitParams) {
  var requestedDuration = Number(alertOverlayParams.get("duration"));
  if (alertOverlayParams.has("tiles")) {
    var requestedColumns = Number(alertOverlayParams.get("columns")) || 1;
    var requestedRows = Number(alertOverlayParams.get("rows")) || 1;
    var requestedTiles = alertOverlayParams.get("tiles").split(",")
      .map(function (tile) { return tile.trim(); })
      .filter(function (tile) { return tile === "failed" || tile === "warning"; })
      .slice(0, requestedColumns * requestedRows);
    startShowing(
      requestedTiles,
      requestedColumns,
      requestedRows,
      Number(alertOverlayParams.get("gap")) || 0,
      requestedDuration,
      parseWorkAreaParam(alertOverlayParams));
  } else {
    var requestedKind = alertOverlayParams.get("kind") === "failed" ? "failed" : "warning";
    startShowing([requestedKind], 1, 1, 0, requestedDuration);
  }
}
