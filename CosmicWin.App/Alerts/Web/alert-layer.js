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

var W = 0;
var H = 0;
var canvasScaleX = 1;
var canvasScaleY = 1;

function resize() {
  var cssWidth = Math.max(1, window.innerWidth || 1);
  var cssHeight = Math.max(1, window.innerHeight || 1);
  var dpr = Math.min(window.devicePixelRatio || 1, 2);
  var pixelWidth = Math.round(cssWidth * dpr);
  var pixelHeight = Math.round(cssHeight * dpr);
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
  var entry = failureLayers[slot];
  if (entry.canvas.width !== canvas.width || entry.canvas.height !== canvas.height) {
    entry.canvas.width = canvas.width;
    entry.canvas.height = canvas.height;
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
function drawPixelated(source, cell, slot, alpha) {
  var pixelWidth = Math.max(1, Math.ceil(canvas.width / cell));
  var pixelHeight = Math.max(1, Math.ceil(canvas.height / cell));
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
  ctx.drawImage(pixels.canvas, 0, 0, canvas.width, canvas.height);
  ctx.restore();
}

function drawFailureLayer(cx, cy, progress, ms) {
  if (failureState === "hidden" || failureState === "shaking") return;
  var theme = FAILURE_OVERLAY_THEMES[failureKind];
  var shownMs = Math.max(0, ms - failureStartMs - theme.shakeMs);
  var counter = Math.floor(shownMs / FAILURE_COUNTER_STEP_MS) % 100;

  if (failureState === "shown") {
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

var failureState = "hidden";
var failureKind = "warning";
var failureStartMs = null;

function advanceFailureState(ms) {
  if (failureStartMs === null) {
    failureStartMs = ms;
    failureState = "shaking";
  }

  var theme = FAILURE_OVERLAY_THEMES[failureKind];
  var shakeMs = theme.shakeMs;
  if (failureState === "shaking" && ms - failureStartMs >= shakeMs) {
    failureState = "revealing";
  }
  if (failureState === "revealing" && ms - failureStartMs >= shakeMs + theme.revealMs) {
    failureState = "shown";
  }
}

// ---- Message-driven show/hide API (T9b) ---------------------------------------------------------
// The preloaded host (T9c) drives this page after navigation by posting JSON through the WebView2
// message bridge: {type:"show", kind, duration} / {type:"hide"}. IDLE means nothing runs at all --
// canvas cleared, no requestAnimationFrame loop -- so a hidden preloaded layer costs ~0% GPU (the
// feature doc's Idle cost condition). "show" while already showing restarts from zero with the new
// kind/duration, same as a fresh "show" on an idle page.

var animating = false;
var doneSignaled = false;
var durationMs = 5000;

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

function startShowing(kind, duration) {
  failureKind = kind === "failed" ? "failed" : "warning";
  durationMs = isFinite(duration) && duration > 0 ? duration : 5000;
  failureStartMs = null;
  failureState = "hidden";
  doneSignaled = false;
  if (!animating) {
    animating = true;
    scheduleFrame(render);
  }
}

function hide() {
  stopAndClear();
  failureStartMs = null;
  failureState = "hidden";
}

function signalDoneIfElapsed(ms) {
  if (doneSignaled || failureStartMs === null || ms - failureStartMs < durationMs) {
    return false;
  }

  doneSignaled = true;
  postToHost("done");
  return true;
}

function render(ms) {
  if (!animating) return;
  advanceFailureState(ms);
  var progress = animationProgress(ms);
  var cx = W * 0.5;
  var cy = H * 0.5;

  clearCanvas();
  drawFailureLayer(cx, cy, progress, ms);

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
    startShowing(data.kind, Number(data.duration));
  } else if (data.type === "hide") {
    hide();
  }
}

if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener("message", handleHostMessage);
}

// ---- Hash API: alert-layer.html#kind=failed&duration=5000 --------------------------------------
// Still supported so this same file keeps working when opened directly in a browser tab for the
// manual check the feature doc asks for -- reads location.hash, falling back to location.search.
// A preloaded host page (T9c) navigates with NO hash/query at all, so this must NOT auto-show:
// only an EXPLICIT kind or duration param starts the layer; otherwise the page stays idle until a
// "show" message arrives, exactly like a freshly preloaded page must.

function parseParams() {
  var raw = (location.hash || location.search || "").replace(/^[#?]/, "");
  return new URLSearchParams(raw);
}

var params = parseParams();
var hasExplicitParams = params.has("kind") || params.has("duration");

// Tells the host a live, running script exists on the other end of the bridge -- not just that
// navigation completed -- before it trusts a pending "show" was actually received (T9c).
postToHost("ready");

if (hasExplicitParams) {
  var requestedKind = params.get("kind") === "failed" ? "failed" : "warning";
  var requestedDuration = Number(params.get("duration"));
  startShowing(requestedKind, requestedDuration);
}
