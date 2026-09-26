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
function make2dContext() {
  var slots = {};
  return new Proxy({}, {
    get: function (target, prop) {
      if (prop === "measureText") {
        return function (text) { return { width: String(text).length * 8 }; };
      }
      if (prop in slots) return slots[prop];
      return function () { /* no-op: save/restore/beginPath/rect/clip/fill/drawImage/... */ };
    },
    set: function (target, prop, value) {
      slots[prop] = value;
      return true;
    },
  });
}

// Loads a FRESH copy of the real page into its own sandbox, mirroring a fresh page load -- the
// production page is a preloaded singleton, but nothing here needs state to survive across cases.
function loadPage(options) {
  options = options || {};
  var ctx2d = make2dContext();
  var canvasElement = { width: 0, height: 0, getContext: function () { return ctx2d; } };
  var postedMessages = [];

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
            addEventListener: function () { },
          },
        }
      : undefined,
  };

  var documentMock = {
    getElementById: function (id) { return id === "alert-layer-canvas" ? canvasElement : null; },
    createElement: function (tag) {
      if (tag !== "canvas") throw new Error("unexpected document.createElement(" + tag + ")");
      return { width: 0, height: 0, getContext: function () { return ctx2d; } };
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

  return { sandbox: sandbox, postedMessages: postedMessages };
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
