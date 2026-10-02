"use strict";

// T9 (alert-tile-mosaic, 2026-09-26): a committed vm-sandbox harness proving alert-layer.js's own
// tile-layout math (tileRects/gridAreaRect) and hash API, closing review follow-ups
// R3-js-mosaic-behavior-unproved and R3-js-workarea-layout-only-structurally-guarded -- until now
// this math was only checked by AlertLayerWebPageTests' substring assertions (proving the code
// EXISTS) and a hardware run (proving it LOOKS right), never actual numbers.
//
// Run via CosmicWin.App.Tests/Alerts/AlertLayerLayoutNodeTests.cs:
//   node alert-layer-layout.tests.js <path-to-the-REAL-shipped-alert-layer.js>
// The path is the exact AppContext.BaseDirectory-resolved copy AlertLayerWebPageTests already
// reads -- there is no second copy of the page's logic in this file, only the DOM/canvas/host mock
// its top-level code needs to run at all outside a browser. Node was chosen only because this repo
// has no in-process JS engine or headless browser (feature doc, "JS has no test harness").
//
// Top-level `function`/`var` declarations in a script run via vm.runInContext become properties of
// the sandbox object passed to vm.createContext (the sandbox IS that realm's global object) --
// alert-layer.js needed no export/module system added to be reachable this way; that was checked
// first, per this task's own instruction, before writing anything here.

const vm = require("vm");
const fs = require("fs");
const assert = require("assert");
const { URLSearchParams } = require("url");

const scriptPath = process.argv[2];
if (!scriptPath) {
  console.error("usage: node alert-layer-layout.tests.js <path-to-alert-layer.js>");
  process.exit(2);
}
const pageSource = fs.readFileSync(scriptPath, "utf8");

// ---- Minimal DOM/canvas/host mock ------------------------------------------------------------
// Only what alert-layer.js's top level and the functions under test actually touch: a canvas
// element with a 2D context (every method/property is a harmless no-op/slot -- nothing here draws
// a single pixel; only tileRects()/gridAreaRect()/the hash parser are asserted on), window sizing
// and devicePixelRatio, location.hash, and an optional chrome.webview bridge.
// T16 (remove-fake-letter-bands, B1): fillStyle history, added only to let the "no fake band color
// is ever painted" cases below assert on every color the page ever set, not just the last one a
// plain slot would keep -- drawFailureOverlay sets fillStyle several times per frame (wash, rail
// cutout, letters, and -- before this task -- the band intersections), so a single latched slot
// value could never have caught a color painted mid-frame and overwritten before the frame ends.
function make2dContext() {
  var slots = {};
  var fillStyleHistory = [];
  return new Proxy({}, {
    get: function (target, prop) {
      if (prop === "measureText") {
        return function (text) { return { width: String(text).length * 8 }; };
      }
      if (prop === "__fillStyleHistory") return fillStyleHistory;
      if (prop in slots) return slots[prop];
      return function () { /* no-op: save/restore/beginPath/rect/clip/fill/drawImage/... */ };
    },
    set: function (target, prop, value) {
      slots[prop] = value;
      if (prop === "fillStyle") fillStyleHistory.push(value);
      return true;
    },
  });
}

// Loads a FRESH copy of the real page into its own sandbox, mirroring a fresh page load -- the
// production page is a preloaded singleton, but nothing here needs state to survive across cases.
function loadPage(options) {
  options = options || {};
  var ctx2d = make2dContext();
  // see-through-video-tint (S4): toDataURL records the canvas size at call time so a case can prove
  // which canvas was exported and at what pixel size; createdCanvases lists every offscreen canvas.
  var toDataUrlCalls = [];
  var createdCanvases = [];
  function stubCanvas() {
    var element = {
      width: 0, height: 0,
      getContext: function () { return ctx2d; },
      toDataURL: function (type) {
        toDataUrlCalls.push({ type: type, width: element.width, height: element.height });
        return "data:image/png;base64,TUFTSw==";
      },
    };
    return element;
  }
  var canvasElement = stubCanvas();
  var postedMessages = [];
  var hostHandlers = [];

  var windowMock = {
    innerWidth: options.innerWidth || 1000,
    innerHeight: options.innerHeight || 500,
    devicePixelRatio: options.devicePixelRatio || 1,
    // Never actually scheduled: every case below drives tileRects()/gridAreaRect() (or the hash
    // parser, at load time) directly, never the render() animation loop.
    requestAnimationFrame: function () { },
    addEventListener: function () { /* only "resize" in production; never fired here */ },
    chrome: options.withWebview
      ? {
          webview: {
            postMessage: function (message) { postedMessages.push(message); },
            addEventListener: function (type, handler) { hostHandlers.push(handler); },
          },
        }
      : undefined,
  };

  var documentMock = {
    getElementById: function (id) { return id === "alert-layer-canvas" ? canvasElement : null; },
    createElement: function (tag) {
      if (tag !== "canvas") throw new Error("unexpected document.createElement(" + tag + ")");
      var element = stubCanvas();
      createdCanvases.push(element);
      return element;
    },
  };

  var locationMock = { hash: options.hash || "", search: options.search || "" };

  var sandbox = {
    window: windowMock,
    document: documentMock,
    location: locationMock,
    URLSearchParams: URLSearchParams,
  };
  vm.createContext(sandbox);
  vm.runInContext(pageSource, sandbox, { filename: scriptPath });

  return {
    sandbox: sandbox,
    postedMessages: postedMessages,
    fillStyleHistory: ctx2d.__fillStyleHistory,
    toDataUrlCalls: toDataUrlCalls,
    createdCanvases: createdCanvases,
    canvas: canvasElement,
    // Delivers a host -> page message the way WebView2 does (event.data is the parsed JSON).
    sendHostMessage: function (data) { hostHandlers.forEach(function (h) { h({ data: data }); }); },
  };
}

// ---- Tiny test runner -------------------------------------------------------------------------
var tests = [];
function test(name, fn) { tests.push({ name: name, fn: fn }); }

// vm.createContext gives the sandbox its OWN realm, with its OWN Object/Array prototypes -- a
// structurally identical {x,y,w,h} object or ["failed"] array returned from it is not
// assert.deepStrictEqual to an outer-realm literal (Node reports "same structure but are not
// reference-equal", a cross-realm prototype-identity check, not a real content difference). Values
// here are always plain numbers/strings, so a JSON round-trip safely rebuilds them as ordinary
// outer-realm objects/arrays before comparison.
function plain(value) { return JSON.parse(JSON.stringify(value)); }

// ---- Cases: tileRects()/gridAreaRect() ---------------------------------------------------------

test("N=1 fills the whole canvas, ignoring gap and work area entirely", function () {
  var page = loadPage({ innerWidth: 800, innerHeight: 600 });
  page.sandbox.startShowing(
    ["warning"], 1, 1, /* gap, physical px, must be ignored */ 999, /* duration */ 5000,
    { left: 10, top: 10, width: 100, height: 100 }); // must also be ignored
  assert.deepStrictEqual(plain(page.sandbox.tileRects()), [{ x: 0, y: 0, w: 800, h: 600 }]);
});

test("2x1 grid: outer + inner gap, equal cells, row-major", function () {
  var page = loadPage({ innerWidth: 1000, innerHeight: 400 });
  page.sandbox.startShowing(["failed", "warning"], 2, 1, /* gap */ 20, 5000);
  assert.deepStrictEqual(plain(page.sandbox.tileRects()), [
    { x: 20, y: 20, w: 470, h: 360 },
    { x: 510, y: 20, w: 470, h: 360 },
  ]);
});

test("3x2 grid (5 tiles): equal cells, row-major fill wraps into the second row", function () {
  var page = loadPage({ innerWidth: 900, innerHeight: 600 });
  page.sandbox.startShowing(
    ["failed", "failed", "failed", "warning", "warning"], 3, 2, /* gap */ 30, 5000);
  var rects = plain(page.sandbox.tileRects());
  assert.strictEqual(rects.length, 5); // only as many rects as tiles, not the full 3x2=6 capacity
  assert.deepStrictEqual(rects[0], { x: 30, y: 30, w: 260, h: 255 }); // column 0, row 0
  assert.deepStrictEqual(rects[2], { x: 610, y: 30, w: 260, h: 255 }); // last column, row 0
  assert.deepStrictEqual(rects[3], { x: 30, y: 315, w: 260, h: 255 }); // wraps to column 0, row 1
});

test("gridAreaRect offsets the grid into a non-origin work area", function () {
  var page = loadPage({ innerWidth: 1000, innerHeight: 500 });
  page.sandbox.startShowing(
    ["failed", "warning"], 2, 1, 0, 5000, { left: 200, top: 100, width: 600, height: 300 });
  assert.deepStrictEqual(plain(page.sandbox.gridAreaRect()), { x: 200, y: 100, w: 600, h: 300 });
});

test("gridAreaRect clamps a work area that spills past the canvas", function () {
  var page = loadPage({ innerWidth: 1000, innerHeight: 500 });
  page.sandbox.startShowing(
    ["failed"], 1, 1, 0, 5000, { left: 900, top: 450, width: 500, height: 300 });
  assert.deepStrictEqual(plain(page.sandbox.gridAreaRect()), { x: 900, y: 450, w: 100, h: 50 });
});

test("gridAreaRect falls back to the full canvas when no work area is posted", function () {
  var page = loadPage({ innerWidth: 1000, innerHeight: 500 });
  page.sandbox.startShowing(["failed"], 1, 1, 0, 5000); // workArea omitted entirely
  assert.deepStrictEqual(plain(page.sandbox.gridAreaRect()), { x: 0, y: 0, w: 1000, h: 500 });
});

test("gridAreaRect falls back to the full canvas for a degenerate (zero-width) work area", function () {
  var page = loadPage({ innerWidth: 1000, innerHeight: 500 });
  page.sandbox.startShowing(
    ["failed"], 1, 1, 0, 5000, { left: 10, top: 10, width: 0, height: 400 });
  assert.deepStrictEqual(plain(page.sandbox.gridAreaRect()), { x: 0, y: 0, w: 1000, h: 500 });
});

test("devicePixelRatio != 1 converts both the gap and the work area to CSS pixels", function () {
  var page = loadPage({ innerWidth: 1000, innerHeight: 500, devicePixelRatio: 1.5 });
  assert.strictEqual(page.sandbox.canvasScaleX, 1.5);
  assert.strictEqual(page.sandbox.canvasScaleY, 1.5);
  page.sandbox.startShowing(
    ["failed", "warning"], 2, 1, /* gap, PHYSICAL px */ 12, 5000,
    { left: 150, top: 75, width: 1200, height: 600 }); // physical px == 100,50,800,400 CSS at 1.5x
  assert.deepStrictEqual(plain(page.sandbox.gridAreaRect()), { x: 100, y: 50, w: 800, h: 400 });
  assert.deepStrictEqual(plain(page.sandbox.tileRects()), [
    { x: 108, y: 58, w: 388, h: 384 },
    { x: 504, y: 58, w: 388, h: 384 },
  ]);
});

// ---- Cases: hash API ----------------------------------------------------------------------------

test("hash API: tiles=/columns=/rows=/gap=/duration= all parse", function () {
  var page = loadPage({ hash: "#tiles=failed,warning&columns=2&rows=1&gap=20&duration=3000" });
  assert.deepStrictEqual(plain(page.sandbox.tiles), ["failed", "warning"]);
  assert.strictEqual(page.sandbox.gridColumns, 2);
  assert.strictEqual(page.sandbox.gridRows, 1);
  assert.strictEqual(page.sandbox.gapPx, 20);
  assert.strictEqual(page.sandbox.durationMs, 3000);
});

test("hash API: work= parses into the same shape the host message uses", function () {
  var page = loadPage({
    hash: "#tiles=failed,warning&columns=2&rows=1&gap=0&duration=1000&work=100,50,800,400",
  });
  assert.strictEqual(page.sandbox.workAreaLeft, 100);
  assert.strictEqual(page.sandbox.workAreaTop, 50);
  assert.strictEqual(page.sandbox.workAreaWidth, 800);
  assert.strictEqual(page.sandbox.workAreaHeight, 400);
});

test("hash API: a malformed work= (wrong element count) is ignored, not partially applied", function () {
  var page = loadPage({ hash: "#tiles=failed&columns=1&rows=1&gap=0&duration=1000&work=1,2,3" });
  assert.strictEqual(page.sandbox.workAreaWidth, 0);
  assert.strictEqual(page.sandbox.workAreaHeight, 0);
});

test("hash API: the old single-kind #kind= form still maps to one full-canvas tile", function () {
  var page = loadPage({ innerWidth: 640, innerHeight: 480, hash: "#kind=failed&duration=2000" });
  assert.deepStrictEqual(plain(page.sandbox.tiles), ["failed"]);
  assert.strictEqual(page.sandbox.gridColumns, 1);
  assert.strictEqual(page.sandbox.gridRows, 1);
  assert.deepStrictEqual(plain(page.sandbox.tileRects()), [{ x: 0, y: 0, w: 640, h: 480 }]);
});

// T12 (alert-tile-mosaic, review follow-up R3-hash-tiles-filter-noop): tiles= used to .map() every
// comma-separated entry (including "" and an unrecognised word) STRAIGHT to "failed"/"warning"
// before filtering, so the filter (checking tile.length > 0 on the already-mapped word) never
// actually dropped anything -- an empty or bogus entry silently became an extra "warning" tile
// instead of being ignored, and nothing capped the result at the grid's own capacity. Fixed: filter
// to exactly "failed"/"warning" BEFORE mapping, then cap at columns*rows.
//
// T15 (review follow-up R3-t12-filter-fix-not-discriminated): the ORIGINAL input here
// ("failed,,bogus,warning,failed", cap 2) does not actually tell the fix apart from the bug it
// fixed -- under the OLD map-then-filter order the junk entries still map to "warning" and the cap
// still keeps only the first two results, which happen to start "failed", "warning" (the raw
// "failed" then the raw "" defaulting to "warning") for THIS input, matching the correct answer by
// coincidence. Putting the junk entries FIRST makes the two orders diverge for real: filtering
// first drops them and the two REAL tiles survive the cap; mapping first turns both into extra
// "warning" tiles that fill the cap and push the real tiles out entirely. Confirmed by temporarily
// restoring the old map-then-filter order in alert-layer.js: this fact failed with
// ["warning", "warning"] instead of ["failed", "warning"], then passed again once reverted.
//
// Review follow-up R3-t15-cap-no-longer-exercised: with only two real entries left after the
// filter, a 2x1 grid never had to cap anything, so deleting the cap still passed. A THIRD real entry
// after the junk makes the cap load-bearing: without it the result is three tiles instead of two.
// Confirmed by temporarily deleting the .slice(0, columns * rows) call: this fact failed, then passed
// again once restored.
//
// Review follow-up R3-t15-third-entry-duplicates-first: the real entries are "warning", "failed",
// "warning" -- the third repeats the FIRST (only two kinds exist, so some repeat is unavoidable), but
// the order is chosen so the expected result ["warning", "failed"] is distinguishable from every
// plausible cap bug: a cap that keeps the LAST two gives ["failed", "warning"], a failed-first
// reorder (the C# mosaic's rule, deliberately not applied to the manual hash API) gives
// ["failed", "warning"] too, and deleting the cap gives three tiles. Confirmed by temporary
// mutations of alert-layer.js: no cap, keep-last-two and map-before-filter each failed this fact.
test("hash API: tiles= drops empty/unknown entries before mapping, then caps at columns*rows (T12/T15)", function () {
  var page = loadPage({
    hash: "#tiles=bogus,,bogus,warning,failed,warning&columns=2&rows=1&gap=0&duration=1000",
  });
  assert.deepStrictEqual(plain(page.sandbox.tiles), ["warning", "failed"]);
});

// Review follow-up R3-hash-tiles-empty-or-negative-grid-unproved: a negative columns=/rows= used to
// reach .slice(0, columns * rows) unclamped -- columns=-2 turned the cap into slice(0, -2), which
// DROPS the last two tiles instead of capping, and two negatives multiplied into a positive cap
// larger than the 1x1 grid startShowing then actually builds. Both now clamp to 1 first, so the
// hash cap and the grid agree.
test("hash API: a negative columns= clamps to 1 so the cap never drops the wrong tiles", function () {
  var page = loadPage({ hash: "#tiles=failed,warning&columns=-2&rows=1&gap=0&duration=1000" });
  assert.deepStrictEqual(plain(page.sandbox.tiles), ["failed"]);
  assert.strictEqual(page.sandbox.gridColumns, 1);
  assert.strictEqual(page.sandbox.gridRows, 1);
});

test("hash API: two negative grid sizes do not multiply into a positive cap", function () {
  var page = loadPage({ hash: "#tiles=failed,warning,failed&columns=-2&rows=-3&gap=0&duration=1000" });
  assert.deepStrictEqual(plain(page.sandbox.tiles), ["failed"]);
  assert.strictEqual(page.sandbox.gridColumns, 1);
  assert.strictEqual(page.sandbox.gridRows, 1);
});

test("hash API: zero and non-numeric columns=/rows= clamp to a 1x1 grid with one tile", function () {
  var page = loadPage({ hash: "#tiles=failed,warning&columns=0&rows=abc&gap=0&duration=1000" });
  assert.deepStrictEqual(plain(page.sandbox.tiles), ["failed"]);
  assert.strictEqual(page.sandbox.gridColumns, 1);
  assert.strictEqual(page.sandbox.gridRows, 1);
});

// Review follow-up R4-hash-tiles-empty-list: pins the fallback startShowing applies when the filter
// leaves nothing (an all-junk tiles= list) -- exactly one "warning" tile, never an empty mosaic.
test("hash API: an all-junk tiles= list falls back to exactly one warning tile", function () {
  var page = loadPage({ hash: "#tiles=bogus,,nope&columns=2&rows=2&gap=0&duration=1000" });
  assert.deepStrictEqual(plain(page.sandbox.tiles), ["warning"]);
});

// ---- Cases: the fake band intersections must never be painted (remove-fake-letter-bands, B1) ----
// drawFailureOverlay used to draw a copy of the wallpaper's folding-band animation, clipped to the
// letters, in a fixed color per kind (FAILURE_OVERLAY_THEMES[kind].intersections) -- a fake effect
// unrelated to the real video behind the page, removed by this task. These cases drive a tile of
// each kind all the way to "shown" (and through "revealing" on the way there, since drawFailureLayer
// paints the SAME overlay, via the pixelation buffer, during the reveal too) and fail if the page
// ever sets fillStyle to either kind's old band color -- a stronger check than grepping the source
// for the color literal, since it proves the color is never actually PAINTED, in any state.
var FAILED_BAND_COLOR = "rgb(0,160,196)";
var WARNING_BAND_COLOR = "rgb(88,40,196)";

// Review follow-up R3-no-positive-paint-control: an absence check alone also passes when the page
// paints nothing at all, so every case below also requires the kind's own wash and letter colors to
// have been painted -- read from the page's real theme table, never restated here.
function assertKindPainted(page, kind) {
  var theme = page.sandbox.FAILURE_OVERLAY_THEMES[kind];
  assert.notStrictEqual(page.fillStyleHistory.indexOf(theme.wash), -1,
    "expected the " + kind + " wash color " + theme.wash + " to be painted");
  assert.notStrictEqual(page.fillStyleHistory.indexOf(theme.letters), -1,
    "expected the " + kind + " letter color " + theme.letters + " to be painted");
}

test("shown failed tile never paints the fake band intersection color", function () {
  var page = loadPage({ innerWidth: 800, innerHeight: 600 });
  page.sandbox.startShowing(["failed"], 1, 1, 0, 5000);
  page.sandbox.render(0); // shaking (FAILURE_SHAKE_MS = 120)
  page.sandbox.render(200); // revealing (120 <= 200 < 120 + FAILURE_REVEAL_MS(350) = 470)
  assert.strictEqual(page.sandbox.kindState.failed.state, "revealing");
  page.sandbox.render(600); // shown (600 >= 470)
  assert.strictEqual(page.sandbox.kindState.failed.state, "shown");
  assert.strictEqual(page.fillStyleHistory.indexOf(FAILED_BAND_COLOR), -1,
    "expected the failed band color to never be painted, but fillStyle was set to it");
  assertKindPainted(page, "failed");
});

test("shown warning tile never paints the fake band intersection color", function () {
  var page = loadPage({ innerWidth: 800, innerHeight: 600 });
  page.sandbox.startShowing(["warning"], 1, 1, 0, 5000);
  page.sandbox.render(0); // shaking is skipped for warning (shakeMs = 0), straight to revealing
  page.sandbox.render(400); // revealing (0 <= 400 < FAILURE_REVEAL_MS(700))
  assert.strictEqual(page.sandbox.kindState.warning.state, "revealing");
  page.sandbox.render(800); // shown (800 >= 700)
  assert.strictEqual(page.sandbox.kindState.warning.state, "shown");
  assert.strictEqual(page.fillStyleHistory.indexOf(WARNING_BAND_COLOR), -1,
    "expected the warning band color to never be painted, but fillStyle was set to it");
  assertKindPainted(page, "warning");
});

// Review follow-up R3-mosaic-not-covered: a mixed failed+warning mosaic, both tiles driven to
// "shown" in the same frames, paints neither band color and does paint both kinds.
test("mixed failed+warning mosaic never paints either fake band color", function () {
  var page = loadPage({ innerWidth: 1000, innerHeight: 400 });
  page.sandbox.startShowing(["failed", "warning"], 2, 1, 20, 5000);
  page.sandbox.render(0);
  page.sandbox.render(200); // failed revealing (120..470), warning revealing (0..700)
  assert.strictEqual(page.sandbox.kindState.failed.state, "revealing");
  assert.strictEqual(page.sandbox.kindState.warning.state, "revealing");
  page.sandbox.render(800); // both shown
  assert.strictEqual(page.sandbox.kindState.failed.state, "shown");
  assert.strictEqual(page.sandbox.kindState.warning.state, "shown");
  assert.strictEqual(page.fillStyleHistory.indexOf(FAILED_BAND_COLOR), -1,
    "expected the failed band color to never be painted in a mixed mosaic");
  assert.strictEqual(page.fillStyleHistory.indexOf(WARNING_BAND_COLOR), -1,
    "expected the warning band color to never be painted in a mixed mosaic");
  assertKindPainted(page, "failed");
  assertKindPainted(page, "warning");
});

// ---- Cases: see-through video tint (S4) -----------------------------------------------------------
// In video mode C# sends tint:true plus a per-show seq. When the reveal ends the page posts ONE mask
// (as a JSON string, so the host's string-message path stays the only one) and, once C# answers
// tint-ready with the same seq, stops painting the letter fill. tint false/absent changes nothing.

function maskMessages(page) {
  return page.postedMessages
    .filter(function (m) { return typeof m === "string" && m.charAt(0) === "{"; })
    .map(function (m) { return JSON.parse(m); })
    .filter(function (m) { return m.type === "mask"; });
}

function showTinted(page, tilesList, columns, seq) {
  page.sendHostMessage({
    type: "show", tiles: tilesList, columns: columns, rows: 1, gap: 0, duration: 5000,
    workArea: { left: 0, top: 0, width: 0, height: 0 }, tint: true, seq: seq,
  });
}

function paintedLetterColorSince(page, kind, historyLength) {
  var theme = page.sandbox.FAILURE_OVERLAY_THEMES[kind];
  return page.fillStyleHistory.slice(historyLength).indexOf(theme.letters) !== -1;
}

test("tint: no mask is posted while the reveal is still running", function () {
  var page = loadPage({ withWebview: true, innerWidth: 800, innerHeight: 600 });
  showTinted(page, ["failed"], 1, 7);
  page.sandbox.render(0);
  page.sandbox.render(200); // revealing
  assert.strictEqual(maskMessages(page).length, 0);
});

test("tint: exactly one mask is posted when the reveal ends, with the canvas pixel size and a png data url", function () {
  var page = loadPage({ withWebview: true, innerWidth: 800, innerHeight: 600, devicePixelRatio: 1.5 });
  showTinted(page, ["failed"], 1, 7);
  page.sandbox.render(0);
  page.sandbox.render(200);
  page.sandbox.render(600); // shown
  page.sandbox.render(700);
  page.sandbox.render(800);
  var masks = maskMessages(page);
  assert.strictEqual(masks.length, 1);
  assert.strictEqual(masks[0].seq, 7);
  assert.strictEqual(masks[0].kind, "failed");
  assert.strictEqual(masks[0].width, 1200);
  assert.strictEqual(masks[0].height, 900);
  assert.strictEqual(masks[0].width, page.canvas.width);
  assert.strictEqual(masks[0].height, page.canvas.height);
  assert.ok(/^data:image\/png;base64,/.test(masks[0].png));
  assert.strictEqual(page.toDataUrlCalls.length, 1);
  assert.strictEqual(page.toDataUrlCalls[0].type, "image/png");
  assert.strictEqual(page.toDataUrlCalls[0].width, 1200);
  assert.strictEqual(page.toDataUrlCalls[0].height, 900);
});

test("tint off (flag absent): never exports a mask and keeps painting the letter fill", function () {
  var page = loadPage({ withWebview: true, innerWidth: 800, innerHeight: 600 });
  page.sendHostMessage({
    type: "show", tiles: ["failed"], columns: 1, rows: 1, gap: 0, duration: 5000,
    workArea: { left: 0, top: 0, width: 0, height: 0 },
  });
  page.sandbox.render(0);
  page.sandbox.render(200);
  page.sandbox.render(600);
  var before = page.fillStyleHistory.length;
  page.sandbox.render(700);
  assert.strictEqual(maskMessages(page).length, 0);
  assert.strictEqual(page.toDataUrlCalls.length, 0);
  assert.ok(paintedLetterColorSince(page, "failed", before));
  // A tint-ready without a tinted show is ignored too.
  page.sendHostMessage({ type: "tint-ready", seq: 0 });
  before = page.fillStyleHistory.length;
  page.sandbox.render(800);
  assert.ok(paintedLetterColorSince(page, "failed", before));
});

test("tint: a mosaic of failed+warning exports ONE mask for the first tile's kind", function () {
  var page = loadPage({ withWebview: true, innerWidth: 1000, innerHeight: 400 });
  showTinted(page, ["failed", "warning"], 2, 3);
  page.sandbox.render(0);
  page.sandbox.render(200);
  page.sandbox.render(800); // both shown
  page.sandbox.render(900);
  var masks = maskMessages(page);
  assert.strictEqual(masks.length, 1);
  assert.strictEqual(masks[0].kind, "failed");
});

test("tint: no mask after hide, and a new show re-arms the export", function () {
  var page = loadPage({ withWebview: true, innerWidth: 800, innerHeight: 600 });
  showTinted(page, ["failed"], 1, 1);
  page.sandbox.render(0);
  page.sandbox.render(200);
  page.sendHostMessage({ type: "hide" });
  page.sandbox.render(600); // animation stopped: must not export
  assert.strictEqual(maskMessages(page).length, 0);

  showTinted(page, ["warning"], 1, 2);
  page.sandbox.render(1000);
  page.sandbox.render(1800); // shown (700 ms reveal)
  page.sandbox.render(1900);
  var masks = maskMessages(page);
  assert.strictEqual(masks.length, 1);
  assert.strictEqual(masks[0].seq, 2);
  assert.strictEqual(masks[0].kind, "warning");
});

test("tint: tint-ready stops painting the letter fill but keeps the wash", function () {
  var page = loadPage({ withWebview: true, innerWidth: 800, innerHeight: 600 });
  showTinted(page, ["failed"], 1, 5);
  page.sandbox.render(0);
  page.sandbox.render(200);
  page.sandbox.render(600);
  var before = page.fillStyleHistory.length;
  page.sandbox.render(700);
  assert.ok(paintedLetterColorSince(page, "failed", before), "letters are painted until tint-ready");

  page.sendHostMessage({ type: "tint-ready", seq: 5 });
  before = page.fillStyleHistory.length;
  page.sandbox.render(800);
  assert.ok(!paintedLetterColorSince(page, "failed", before), "letter fill must not be painted after tint-ready");
  var theme = page.sandbox.FAILURE_OVERLAY_THEMES.failed;
  assert.ok(page.fillStyleHistory.slice(before).indexOf(theme.wash) !== -1, "the wash is still painted");
});

test("tint: a tint-ready with another seq (or before any mask) is ignored", function () {
  var page = loadPage({ withWebview: true, innerWidth: 800, innerHeight: 600 });
  showTinted(page, ["failed"], 1, 5);
  page.sandbox.render(0);
  page.sandbox.render(200);
  page.sendHostMessage({ type: "tint-ready", seq: 5 }); // no mask exported yet
  page.sandbox.render(600);
  page.sendHostMessage({ type: "tint-ready", seq: 4 }); // stale seq
  var before = page.fillStyleHistory.length;
  page.sandbox.render(700);
  assert.ok(paintedLetterColorSince(page, "failed", before), "letters keep painting");
});

test("tint: hide and a new untinted show restore the letter fill", function () {
  var page = loadPage({ withWebview: true, innerWidth: 800, innerHeight: 600 });
  showTinted(page, ["failed"], 1, 5);
  page.sandbox.render(0);
  page.sandbox.render(200);
  page.sandbox.render(600);
  page.sendHostMessage({ type: "tint-ready", seq: 5 });
  page.sendHostMessage({ type: "hide" });
  page.sendHostMessage({
    type: "show", tiles: ["failed"], columns: 1, rows: 1, gap: 0, duration: 5000,
    workArea: { left: 0, top: 0, width: 0, height: 0 },
  });
  page.sandbox.render(2000);
  page.sandbox.render(2200);
  page.sandbox.render(2600); // shown, untinted
  var before = page.fillStyleHistory.length;
  page.sandbox.render(2700);
  assert.ok(paintedLetterColorSince(page, "failed", before), "untinted show paints the letters again");
  assert.strictEqual(maskMessages(page).length, 1); // only the first show's mask
});

// ---- Run ----------------------------------------------------------------------------------------

var failures = [];
tests.forEach(function (t) {
  try {
    t.fn();
    console.log("PASS " + t.name);
  } catch (error) {
    failures.push(t.name);
    console.log("FAIL " + t.name);
    console.log(error && error.stack ? error.stack : String(error));
  }
});

console.log((tests.length - failures.length) + "/" + tests.length + " passed");
process.exit(failures.length > 0 ? 1 : 0);
