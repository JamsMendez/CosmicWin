"use strict";

// T1 (webview-alert-layer): trimmed, offline port of the great-sage page's alert layer drawing
// code (backgroud-processing/script.js). Only the failure/warning overlay survives -- the
// nebula/scene, stars, orbits, keyboard shortcuts, fullscreen, zoom and the self-check block are
// all out of scope here (see odd/tasks/webview-alert-layer.md, T1). The canvas shake
// (applyFailureShake) is dropped too: T4 shakes the VIDEO natively, and this page only waits out
// the same 120ms shake duration before it reveals, so the two line up.

var TAU = Math.PI * 2;

var canvas = document.getElementById("alert-layer-canvas");
var ctx = canvas.getContext("2d", { alpha: true });
var scheduleFrame = window.requestAnimationFrame.bind(window);

// alert-tile-mosaic (2026-09-26): W/H used to mean "the whole canvas' CSS size" -- every drawing
// function below (drawFailureOverlay, drawFailureModules, foldingBandGeometry, ...) reads them to
// size and center its own drawing. They now mean "the CURRENT TILE's CSS size" instead, set by
// renderTile() right before each tile is drawn -- canvasW/canvasH hold the whole canvas' own CSS
// size, which N=1 (see tileRects) still maps straight onto W/H, so a single tile looks exactly like
// the old single-layer page. tileDeviceX/Y/W/H hold the current tile's PHYSICAL-pixel rect within
// the whole canvas, needed only by drawPixelated's final blit (see its own remarks).
var W = 0;
var H = 0;
var canvasW = 0;
var canvasH = 0;
var canvasScaleX = 1;
var canvasScaleY = 1;
var tileDeviceX = 0;
var tileDeviceY = 0;
var tileDeviceW = 0;
var tileDeviceH = 0;

function resize() {
  var cssWidth = Math.max(1, window.innerWidth || 1);
  var cssHeight = Math.max(1, window.innerHeight || 1);
  var dpr = Math.min(window.devicePixelRatio || 1, 2);
  var pixelWidth = Math.round(cssWidth * dpr);
  var pixelHeight = Math.round(cssHeight * dpr);
  canvasW = cssWidth;
  canvasH = cssHeight;
  W = cssWidth;
  H = cssHeight;
  canvasScaleX = pixelWidth / cssWidth;
  canvasScaleY = pixelHeight / cssHeight;
  canvas.width = pixelWidth;
  canvas.height = pixelHeight;
  ctx.setTransform(canvasScaleX, 0, 0, canvasScaleY, 0, 0);
}
window.addEventListener("resize", resize, { passive: true });
resize();

// ---- Failure layer data (backgroud-processing/script.js, drawFailureLayer et al.) -------------

var FAILURE_SHAKE_MS = 120; // T4 shakes the video natively for exactly this long before reveal
var FAILURE_REVEAL_MS = 350;
var FAILURE_REVEAL_MAX_CELL = 48;
var FAILURE_COUNTER_STEP_MS = 100;
var FAILURE_REVEAL_LINE_SHARE = 0.25;

var FAILURE_OVERLAY_BITS = "0100010101010010010100100100111101010010";
var WARNING_OVERLAY_BITS = "01010111010000010101001001001110010010010100111001000111";

// Renamed from the source page's 'error' key to 'failed', matching CosmicWin's own AlertKind and
// the #kind=failed hash value -- same theme data, source: backgroud-processing/script.js.
var FAILURE_OVERLAY_THEMES = {
  failed: {
    title: "FAILED",
    bits: FAILURE_OVERLAY_BITS,
    wash: "rgba(196,12,30,0.52)",
    letters: "rgb(112,0,16)",
    intersections: "rgb(0,160,196)",
    shakeMs: FAILURE_SHAKE_MS,
    revealMs: FAILURE_REVEAL_MS,
  },
  warning: {
    title: "WARNING",
    bits: WARNING_OVERLAY_BITS,
    wash: "rgba(255,200,20,0.58)",
    letters: "rgb(150,96,0)",
    // Violet is the complement of the amber wash, so the bands stay readable through it.
    intersections: "rgb(88,40,196)",
    shakeMs: 0,
    revealMs: 700,
  },
};

// Ping-pong band animation driving drawFailureBandIntersections -- kept from the source page so
// the intersections mask animates exactly as it always did (backgroud-processing/script.js).
var ONE_WAY_DURATION = 60 * 5;
var ANIMATION_CYCLE_DURATION = 30;
var FOLDING_BAND_SPEEDS = [3, -3, 3]; // [outer, middle, inner]

function pingpong01(x) {
  var cycle = x % 2;
  return cycle <= 1 ? cycle : 2 - cycle;
}

function animationProgress(ms) {
  return pingpong01((ms / 1000) / ONE_WAY_DURATION) * (ONE_WAY_DURATION / ANIMATION_CYCLE_DURATION);
}

function foldingBandCompression(fold) {
  return 0.10 + 0.90 * Math.pow(Math.cos(fold), 2);
}

function foldingBandGeometry(rx, ry, width, foldPhase) {
  var segmentCount = Math.max(48, Math.min(84, Math.round(Math.min(W, H) * 0.075)));
  var points = [];
  for (var i = 0; i <= segmentCount; i++) {
    var a = (i / segmentCount) * TAU;
    var x = Math.cos(a) * rx;
    var y = Math.sin(a) * ry;
    var tx = -Math.sin(a) * rx;
    var ty = Math.cos(a) * ry;
    var tangentLength = Math.hypot(tx, ty);
    var nx = -ty / tangentLength;
    var ny = tx / tangentLength;
    var fold = a * 2 + foldPhase;
    var compression = foldingBandCompression(fold);
    var bandWidth = width * compression;
    var skew = Math.sin(fold) * width * 0.22;
    points.push({
      left: [x + nx * bandWidth + (tx / tangentLength) * skew, y + ny * bandWidth + (ty / tangentLength) * skew],
      right: [x - nx * bandWidth - (tx / tangentLength) * skew, y - ny * bandWidth - (ty / tangentLength) * skew],
    });
  }
  return points;
}

function fillFoldingBandGeometry(g, points, fillStyle) {
  g.fillStyle = fillStyle;
  for (var i = 0; i < points.length - 1; i++) {
    var a = points[i];
    var b = points[i + 1];
    g.beginPath();
    g.moveTo(a.left[0], a.left[1]);
    g.lineTo(b.left[0], b.left[1]);
    g.lineTo(b.right[0], b.right[1]);
    g.lineTo(a.right[0], a.right[1]);
    g.closePath();
    g.fill();
  }
}

function foldingBandParameters(progress) {
  var minD = Math.min(W, H);
  var phase = progress * TAU;
  var outerBandSpeed = FOLDING_BAND_SPEEDS[0];
  var middleBandSpeed = FOLDING_BAND_SPEEDS[1];
  var innerBandSpeed = FOLDING_BAND_SPEEDS[2];
  return [
    [minD * 0.385, minD * 0.255, -0.76 + phase * outerBandSpeed, minD * 0.025, phase * outerBandSpeed],
    [minD * 0.235, minD * 0.365, 0.36 + phase * middleBandSpeed, minD * 0.023, phase * middleBandSpeed + 0.9],
    [minD * 0.315, minD * 0.225, 0.10 + phase * innerBandSpeed, minD * 0.018, phase * innerBandSpeed + 1.8],
  ];
}

var FAILURE_TITLE_FONT = '"Archivo Black", "Arial Black", "Helvetica Neue", sans-serif';
var failureLayers = [];

// Offscreen layers: 0 letters, 1 band intersections, 2 full overlay (used only during the reveal),
// 3 the reveal's own pixelation buffer.
function failureLayer(slot) {
  if (!failureLayers[slot]) {
    var layer = document.createElement("canvas");
    failureLayers[slot] = { canvas: layer, g: layer.getContext("2d") };
  }
  // Sized to the CURRENT TILE's physical-pixel rect (tileDeviceW/H), not the whole canvas -- for
  // N=1 tileDeviceW/H equal canvas.width/height exactly (see renderTile), so this is unchanged from
  // before the mosaic; for N>1 each tile gets its own correctly-sized offscreen buffer.
  var entry = failureLayers[slot];
  if (entry.canvas.width !== tileDeviceW || entry.canvas.height !== tileDeviceH) {
    entry.canvas.width = tileDeviceW;
    entry.canvas.height = tileDeviceH;
  }
  entry.g.setTransform(1, 0, 0, 1, 0, 0);
  entry.g.globalCompositeOperation = "source-over";
  entry.g.globalAlpha = 1;
  entry.g.clearRect(0, 0, entry.canvas.width, entry.canvas.height);
  entry.g.setTransform(canvasScaleX, 0, 0, canvasScaleY, 0, 0);
  return entry.g;
}

function drawFailureTitle(g, frame, title) {
  // Size the word so it spans the frame, then show only two clipped fragments of it.
  g.font = "400 100px " + FAILURE_TITLE_FONT;
  var fontSize = 100 * (frame.w * 0.97) / Math.max(1, g.measureText(title).width);
  var capHeight = fontSize * 0.72;
  g.font = "400 " + fontSize + "px " + FAILURE_TITLE_FONT;
  g.textAlign = "center";
  g.textBaseline = "alphabetic";

  // Upper fragment: vertically inverted, its letter bases touch the top frame edge.
  g.save();
  g.beginPath();
  g.rect(frame.x, frame.y, frame.w, H * 0.23);
  g.clip();
  g.translate(0, frame.y - capHeight * 0.42);
  g.scale(1, -1);
  g.fillText(title, W * 0.5, 0);
  g.restore();

  // Lower fragment: upright, only the top part of the letters rises above the bottom frame edge.
  g.save();
  g.beginPath();
  g.rect(frame.x, frame.y + frame.h - H * 0.23, frame.w, H * 0.23);
  g.clip();
  g.fillText(title, W * 0.5, frame.y + frame.h + capHeight * 0.42);
  g.restore();
}

function drawFailureBandIntersections(g, letters, cx, cy, progress, color) {
  // Reuses the animated band geometry, then keeps it only where the letters are ('destination-in').
  g.save();
  g.translate(cx, cy);
  var bands = foldingBandParameters(progress);
  for (var i = 0; i < bands.length; i++) {
    var rx = bands[i][0], ry = bands[i][1], rot = bands[i][2], width = bands[i][3], foldPhase = bands[i][4];
    g.save();
    g.rotate(rot);
    fillFoldingBandGeometry(g, foldingBandGeometry(rx, ry, width, foldPhase), color);
    g.restore();
  }
  g.restore();
  g.save();
  g.setTransform(1, 0, 0, 1, 0, 0);
  g.globalCompositeOperation = "destination-in";
  g.drawImage(letters, 0, 0);
  g.restore();
}

// Thin rails above and below the frame, cut out of the wash: the wash shows white there instead.
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

  // Fit four stacked boxes inside the frame height, keeping the digits no wider than the box.
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

function drawFailureOverlay(g, cx, cy, progress, counter, theme) {
  var minD = Math.min(W, H);
  var frame = { x: W * 0.038, y: H * 0.064 };
  frame.w = W - frame.x * 2;
  frame.h = H - frame.y * 2;
  var rails = failureRails(frame);

  g.save();
  // Translucent wash with the rails cut out: whatever moves underneath keeps showing through them.
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

  // Letters are rendered into an offscreen mask so the bands can be clipped to them.
  var letters = failureLayer(0);
  letters.fillStyle = theme.letters;
  drawFailureTitle(letters, frame, theme.title);
  g.save();
  g.shadowColor = "rgba(0,0,0,0.8)";
  g.shadowBlur = minD * 0.03 * canvasScaleX;
  g.shadowOffsetY = minD * 0.008 * canvasScaleY;
  g.globalAlpha = 0.86;
  g.drawImage(letters.canvas, 0, 0, W, H);
  g.restore();
  var intersections = failureLayer(1);
  drawFailureBandIntersections(intersections, letters.canvas, cx, cy, progress, theme.intersections);
  g.globalAlpha = 0.95;
  g.drawImage(intersections.canvas, 0, 0, W, H);
  g.globalAlpha = 1;

  g.strokeStyle = "rgba(255,255,255,0.9)";
  g.lineWidth = Math.max(2, minD * 0.004);
  g.strokeRect(frame.x, frame.y, frame.w, frame.h);
  drawFailureModules(g, frame, counter, theme.bits);
  g.restore();
}

// Downscale `source` (device pixels) by `cell` and draw it back with hard pixel edges -- the
// reveal's own pixelation effect on the overlay itself. Unrelated to (and kept separate from) the
// source page's BACKDROP pixelation, which is out of scope here: a WebView2 page has no access to
// the video pixels behind it (see the feature doc's "Not in v1").
// alert-tile-mosaic: sized/blitted against the CURRENT TILE's physical rect (tileDeviceX/Y/W/H),
// not the whole canvas -- the final drawImage below runs after ctx.setTransform(1,0,0,1,0,0), which
// discards both the devicePixelRatio scale AND renderTile's translate, so it must place the tile
// explicitly rather than relying on the (already-clipped) current transform. For N=1 tileDeviceX/Y
// are 0 and tileDeviceW/H equal canvas.width/height exactly, so this is byte-for-byte what the
// single-layer page always did.
function drawPixelated(source, cell, slot, alpha) {
  var pixelWidth = Math.max(1, Math.ceil(tileDeviceW / cell));
  var pixelHeight = Math.max(1, Math.ceil(tileDeviceH / cell));
  if (!failureLayers[slot]) {
    var layer = document.createElement("canvas");
    failureLayers[slot] = { canvas: layer, g: layer.getContext("2d") };
  }
  var pixels = failureLayers[slot];
  pixels.canvas.width = pixelWidth;
  pixels.canvas.height = pixelHeight;
  pixels.g.imageSmoothingEnabled = true;
  pixels.g.clearRect(0, 0, pixelWidth, pixelHeight);
  pixels.g.drawImage(source, 0, 0, pixelWidth, pixelHeight);
  ctx.save();
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.imageSmoothingEnabled = false;
  ctx.globalAlpha = alpha;
  ctx.drawImage(pixels.canvas, tileDeviceX, tileDeviceY, tileDeviceW, tileDeviceH);
  ctx.restore();
}

function drawFailureLayer(cx, cy, progress, ms, kind) {
  var entry = kindState[kind];
  if (entry.state === "hidden" || entry.state === "shaking") return;
  var theme = FAILURE_OVERLAY_THEMES[kind];
  var shownMs = Math.max(0, ms - entry.startMs - theme.shakeMs);
  var counter = Math.floor(shownMs / FAILURE_COUNTER_STEP_MS) % 100;

  if (entry.state === "shown") {
    drawFailureOverlay(ctx, cx, cy, progress, counter, theme);
    return;
  }

  // Reveal: a horizontal line spreads from the center across the width, then splits open
  // vertically, pixelating the overlay itself while the blocks shrink.
  var t = Math.min(1, shownMs / theme.revealMs);
  var eased = 1 - Math.pow(1 - t, 3);
  var lineT = Math.min(1, t / FAILURE_REVEAL_LINE_SHARE);
  var openT = Math.max(0, (t - FAILURE_REVEAL_LINE_SHARE) / (1 - FAILURE_REVEAL_LINE_SHARE));
  var cell = Math.max(1, Math.round(FAILURE_REVEAL_MAX_CELL * Math.pow(1 - eased, 1.4)));
  var overlay = failureLayer(2);
  drawFailureOverlay(overlay, cx, cy, progress, counter, theme);

  var revealW = W * (1 - Math.pow(1 - lineT, 2));
  var revealH = Math.max(H * 0.04, H * (1 - Math.pow(1 - openT, 3)));
  ctx.save();
  ctx.beginPath();
  ctx.rect((W - revealW) * 0.5, (H - revealH) * 0.5, revealW, revealH);
  ctx.clip();
  drawPixelated(overlay.canvas, cell, 3, 0.3 + 0.7 * eased);
  ctx.restore();
}

// ---- State machine: hidden -> shaking (failed only) -> revealing -> shown ----------------------
// backgroud-processing/script.js's advanceFailureState, with applyFailureShake dropped entirely --
// T4 shakes the VIDEO natively for the same FAILURE_SHAKE_MS, so this page only has to WAIT that
// long before it reveals, exactly what the shakeMs check below already does, unchanged from the
// source page. T1 ran this once per page load, then the host disposed the whole WebView2. T9b
// (webview-alert-layer) makes the run REPEATABLE: the host now preloads this page once and keeps
// it alive (feature doc, "Idle cost"), driving it with "show"/"hide" messages instead -- see below.

// alert-tile-mosaic (2026-09-26): was a SINGLE state machine (one kind shown at a time). A mosaic
// can show failed and warning tiles TOGETHER, each with its own shakeMs/revealMs (theme, above), so
// this is now one small state machine PER KIND instead of per tile-instance -- every tile of the
// SAME kind started at the same ms and shares the same theme, so they always report the identical
// state/counter at any given ms; tracking more than one entry per kind would be redundant.
var kindState = {
  failed: { state: "hidden", startMs: null },
  warning: { state: "hidden", startMs: null },
};

function advanceKindState(ms, kind) {
  var entry = kindState[kind];
  if (entry.startMs === null) {
    entry.startMs = ms;
    entry.state = "shaking";
  }

  var theme = FAILURE_OVERLAY_THEMES[kind];
  var shakeMs = theme.shakeMs;
  if (entry.state === "shaking" && ms - entry.startMs >= shakeMs) {
    entry.state = "revealing";
  }
  if (entry.state === "revealing" && ms - entry.startMs >= shakeMs + theme.revealMs) {
    entry.state = "shown";
  }
}

// ---- Message-driven show/hide API (T9b, tile grid added by alert-tile-mosaic) --------------------
// The preloaded host (T9c) drives this page after navigation by posting JSON through the WebView2
// message bridge: {type:"show", tiles, columns, rows, gap, duration} / {type:"hide"}. IDLE means
// nothing runs at all -- canvas cleared, no requestAnimationFrame loop -- so a hidden preloaded
// layer costs ~0% GPU (the feature doc's Idle cost condition). "show" while already showing restarts
// from zero with the new tiles/grid/gap/duration, same as a fresh "show" on an idle page.

var animating = false;
var doneSignaled = false;
var durationMs = 5000;
var showStartMs = null;

// ---- Tile layout (alert-tile-mosaic, 2026-09-26) --------------------------------------------------
// One tile fills the whole canvas exactly as before (tiles.length <= 1): no outer gap applied --
// "N=1 must look exactly like today" (feature doc, acceptance criteria). N>1 lays tiles out like the
// tiling engine: outer gap around the whole grid, inner gap between cells, equal cell sizes,
// row-major fill. The C# side (AlertTileLayout/AppComposition) already drops slots past 8 and orders
// failed before warning -- this file only draws the list it is given, in that order.

var tiles = ["warning"]; // wire kinds ("failed"/"warning"), one per slot
var gridColumns = 1;
var gridRows = 1;
var gapPx = 0; // PHYSICAL pixels, as posted by the host -- converted to CSS pixels below

// T7 (alert-tile-mosaic, 2026-09-26): the monitor's work area, PHYSICAL pixels, RELATIVE to this
// page's own surface (the whole canvas at devicePixelRatio) -- posted by the host alongside gap
// (AlertLayerWorkArea/AppComposition, read at show time so a moved/auto-hidden taskbar is picked up
// on the NEXT alert). workAreaWidth/Height <= 0 means "could not be read, or does not fit the
// surface" (AlertLayerWorkArea.Unavailable's own shape) -- gridAreaRect() below then falls back to
// the whole canvas, exactly the pre-T7 behaviour for N>1.
var workAreaLeft = 0;
var workAreaTop = 0;
var workAreaWidth = 0;
var workAreaHeight = 0;

// The rect (CSS pixels) the N>1 grid is laid out inside: the work area, converted with the same
// devicePixelRatio-derived scale resize() already uses, and clamped to the canvas -- or the whole
// canvas when the work area is absent/degenerate. N=1 never calls this (tileRects returns early).
function gridAreaRect() {
  if (workAreaWidth <= 0 || workAreaHeight <= 0) {
    return { x: 0, y: 0, w: canvasW, h: canvasH };
  }
  var x = Math.max(0, Math.min(canvasW, workAreaLeft / canvasScaleX));
  var y = Math.max(0, Math.min(canvasH, workAreaTop / canvasScaleY));
  var w = Math.max(1, Math.min(canvasW - x, workAreaWidth / canvasScaleX));
  var h = Math.max(1, Math.min(canvasH - y, workAreaHeight / canvasScaleY));
  return { x: x, y: y, w: w, h: h };
}

// Returns each tile's CSS-pixel rect {x, y, w, h} within the canvas, row-major, in `tiles` order.
// gapPx arrives in PHYSICAL pixels (the same unit TreeArranger.Gap uses); canvasScaleX/Y are the
// same devicePixelRatio-derived factors resize() already uses to size the canvas, so dividing by
// them keeps the gap's CSS size consistent with how everything else on this page is sized. N>1 lays
// the outer gap + grid out inside gridAreaRect() (T7: the work area, or the whole canvas as a
// fallback) instead of always the whole canvas.
function tileRects() {
  if (tiles.length <= 1) {
    return [{ x: 0, y: 0, w: canvasW, h: canvasH }];
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
  // not exist -- the manual check the feature doc asks for
  // (alert-layer.html#kind=warning&duration=5000 in Edge).
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.postMessage(message);
  }
}

function clearCanvas() {
  ctx.save();
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  ctx.restore();
}

function stopAndClear() {
  animating = false;
  clearCanvas();
}

function resetKindState() {
  kindState = {
    failed: { state: "hidden", startMs: null },
    warning: { state: "hidden", startMs: null },
  };
}

// newTiles/columns/rows/gap: alert-tile-mosaic's grid (see tileRects above). A caller with only a
// single kind/duration (the old contract, and the hash API's back-compat form) passes a one-tile
// list with a 1x1 grid and zero gap, which tileRects already renders exactly like the old
// single-layer page. workArea (T7): {left, top, width, height} in PHYSICAL pixels, or missing/null --
// treated the same as an unresolved work area (gridAreaRect falls back to the whole canvas).
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
  if (!animating) {
    animating = true;
    scheduleFrame(render);
  }
}

function hide() {
  stopAndClear();
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

// Draws ONE tile: sets the tile-local W/H (see the remarks above their declaration), clips and
// translates ctx to the tile's rect so every existing drawing function -- unaware anything changed
// -- draws exactly as it always did, just inside this tile instead of the whole canvas.
function renderTile(rect, kind, progress, ms) {
  W = rect.w;
  H = rect.h;
  tileDeviceX = Math.round(rect.x * canvasScaleX);
  tileDeviceY = Math.round(rect.y * canvasScaleY);
  tileDeviceW = Math.round(rect.w * canvasScaleX);
  tileDeviceH = Math.round(rect.h * canvasScaleY);
  var cx = W * 0.5;
  var cy = H * 0.5;

  ctx.save();
  ctx.beginPath();
  ctx.rect(rect.x, rect.y, rect.w, rect.h);
  ctx.clip();
  ctx.translate(rect.x, rect.y);
  drawFailureLayer(cx, cy, progress, ms, kind);
  ctx.restore();
}

function render(ms) {
  if (!animating) return;
  if (showStartMs === null) showStartMs = ms;
  var rects = tileRects();
  var progress = animationProgress(ms);

  clearCanvas();
  for (var i = 0; i < tiles.length && i < rects.length; i++) {
    advanceKindState(ms, tiles[i]);
    renderTile(rects[i], tiles[i], progress, ms);
  }

  if (signalDoneIfElapsed(ms)) {
    stopAndClear();
    return;
  }

  scheduleFrame(render);
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
      // Back-compat: the old single-kind message shape, {type:"show", kind, duration} -- kept
      // trivial since production (WebViewAlertLayerController) always sends "tiles" now; this only
      // matters for a manual check that posts the old shape by hand. No work area either -- N=1
      // ignores it anyway (tileRects returns early).
      startShowing([data.kind === "failed" ? "failed" : "warning"], 1, 1, 0, Number(data.duration));
    }
  } else if (data.type === "hide") {
    hide();
  }
}

if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener("message", handleHostMessage);
}

// ---- Hash API ------------------------------------------------------------------------------------
// alert-tile-mosaic: alert-layer.html#tiles=failed,warning&columns=2&rows=1&gap=8&duration=5000
// Old single-tile form, still supported: alert-layer.html#kind=failed&duration=5000
// T7: optional work area param, tiles form only -- alert-layer.html#tiles=...&work=L,T,W,H (physical
// pixels, comma-separated -- distinct from the trace's "work=L,T,WxH" wording, chosen so this reads
// as an ordinary flat hash param like every other one here). Omitted or malformed falls back to the
// whole canvas, same as when the host never posts one.
// Either form keeps this file working when opened directly in a browser tab for the manual check the
// feature doc asks for -- reads location.hash, falling back to location.search. A preloaded host
// page (T9c) navigates with NO hash/query at all, so this must NOT auto-show: only an EXPLICIT
// tiles/kind/duration param starts the layer; otherwise the page stays idle until a "show" message
// arrives, exactly like a freshly preloaded page must.

function parseParams() {
  var raw = (location.hash || location.search || "").replace(/^[#?]/, "");
  return new URLSearchParams(raw);
}

// Parses the "work=L,T,W,H" hash param into the same {left, top, width, height} shape startShowing
// already accepts from the host message; returns undefined (treated as "no work area") when the
// param is missing or not exactly four finite numbers.
function parseWorkAreaParam(params) {
  if (!params.has("work")) return undefined;
  var parts = params.get("work").split(",").map(Number);
  if (parts.length !== 4 || parts.some(function (n) { return !isFinite(n); })) return undefined;
  return { left: parts[0], top: parts[1], width: parts[2], height: parts[3] };
}

var params = parseParams();
var hasExplicitParams = params.has("kind") || params.has("duration") || params.has("tiles");

// Tells the host a live, running script exists on the other end of the bridge -- not just that
// navigation completed -- before it trusts a pending "show" was actually received (T9c).
postToHost("ready");

if (hasExplicitParams) {
  var requestedDuration = Number(params.get("duration"));
  if (params.has("tiles")) {
    var requestedTiles = params.get("tiles").split(",")
      .map(function (tile) { return tile.trim() === "failed" ? "failed" : "warning"; })
      .filter(function (tile) { return tile.length > 0; });
    startShowing(
      requestedTiles,
      Number(params.get("columns")) || 1,
      Number(params.get("rows")) || 1,
      Number(params.get("gap")) || 0,
      requestedDuration,
      parseWorkAreaParam(params));
  } else {
    var requestedKind = params.get("kind") === "failed" ? "failed" : "warning";
    startShowing([requestedKind], 1, 1, 0, requestedDuration);
  }
}
