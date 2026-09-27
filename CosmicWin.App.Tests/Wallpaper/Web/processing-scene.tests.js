"use strict";

// html-wallpaper-demo D2: a committed vm-sandbox harness proving js/alert-overlay.js's own contract
// against the REAL shipped processing-scene page -- modelled on
// CosmicWin.App.Tests/Alerts/Web/alert-layer-layout.tests.js (that file's own remarks explain why a
// Node subprocess exists at all: no .NET JS engine or headless browser exists in this repo). Run via
// CosmicWin.App.Tests/Wallpaper/ProcessingSceneNodeTests.cs:
//   node processing-scene.tests.js <path-to-the-REAL-shipped-wallpaper-processing-directory>
//
// Unlike alert-layer-layout.tests.js (one file), the processing wallpaper page is MULTI-FILE (see
// index.html's own <script> order) -- this harness loads every script from that directory, in
// index.html's exact order, into ONE vm sandbox realm, mirroring how a real <script> tag sequence
// populates one shared global object. Top-level `function`/`var`/`const` declarations in each script
// become properties of that one sandbox object (same technique alert-layer-layout.tests.js already
// uses), so nothing here needed a module/export system added to be reachable.

const vm = require("vm");
const fs = require("fs");
const path = require("path");
const assert = require("assert");
const { URLSearchParams } = require("url");

const sceneDir = process.argv[2];
if (!sceneDir) {
  console.error("usage: node processing-scene.tests.js <path-to-wallpaper-processing-directory>");
  process.exit(2);
}

// Exact order index.html loads these in -- see CosmicWin.App/Wallpaper/Web/processing/index.html.
const SCRIPT_FILES = [
  "js/config.js",
  "js/math.js",
  "js/nebula.js",
  "js/sphere.js",
  "js/scene-data.js",
  "js/sprites.js",
  "js/layers.js",
  "js/alert-overlay.js",
  "js/main.js",
];

// ---- Minimal DOM/canvas/host mock ------------------------------------------------------------
// One shared 2D-context mock, structurally permissive (a Proxy whose unknown properties are no-op
// functions), extended beyond alert-layer-layout.tests.js's own mock with the gradient/conic-gradient
// factories the full scene (layers.js, sprites.js) actually calls -- without these, drawCentralCore,
// drawVignette, drawRadialStreaks and every baked sprite in sprites.js would throw calling
// .addColorStop on an undefined return value.
function make2dContext() {
  var slots = {};
  var fillStyleHistory = [];
  var gradient = { addColorStop: function () {} };
  return new Proxy({}, {
    get: function (target, prop) {
      if (prop === "measureText") {
        return function (text) { return { width: String(text).length * 8 }; };
      }
      if (prop === "createRadialGradient" || prop === "createLinearGradient" || prop === "createConicGradient") {
        return function () { return gradient; };
      }
      if (prop === "__fillStyleHistory") return fillStyleHistory;
      if (prop in slots) return slots[prop];
      return function () { /* no-op: save/restore/beginPath/rect/clip/fill/drawImage/ellipse/... */ };
    },
    set: function (target, prop, value) {
      slots[prop] = value;
      if (prop === "fillStyle") fillStyleHistory.push(value);
      return true;
    },
  });
}

function makeCanvasElement(ctx2d) {
  return {
    width: 0,
    height: 0,
    style: {},
    getContext: function () { return ctx2d; },
    addEventListener: function () { /* webglcontextlost/restored on #nebula; never fired here */ },
  };
}

// Loads a FRESH copy of the real multi-file page into its own sandbox, mirroring a fresh page load.
function loadPage(options) {
  options = options || {};
  var ctx2d = make2dContext();
  var sceneCanvas = makeCanvasElement(ctx2d);
  var nebulaCanvasElement = makeCanvasElement(ctx2d);
  var postedMessages = [];

  var windowMock = {
    innerWidth: options.innerWidth || 1000,
    innerHeight: options.innerHeight || 500,
    devicePixelRatio: options.devicePixelRatio || 1,
    // No WebGLRenderingContext global -- initializeNebulaRenderer (nebula.js) bails out before ever
    // touching a 'webgl' context, exactly like a browser with WebGL disabled would.
    WebGLRenderingContext: undefined,
    requestAnimationFrame: function () { },
    addEventListener: function () { /* "resize" only; never fired here */ },
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
    getElementById: function (id) {
      if (id === "scene") return sceneCanvas;
      if (id === "nebula") return nebulaCanvasElement;
      return null;
    },
    createElement: function (tag) {
      if (tag !== "canvas") throw new Error("unexpected document.createElement(" + tag + ")");
      return makeCanvasElement(ctx2d);
    },
    addEventListener: function () { /* "fullscreenchange"; never fired here */ },
    fonts: undefined, // alert-overlay.js guards this with a truthiness check
  };

  var locationMock = { hash: options.hash || "", search: options.search || "" };

  var sandbox = {
    window: windowMock,
    document: documentMock,
    location: locationMock,
    URLSearchParams: URLSearchParams,
  };
  vm.createContext(sandbox);
  for (var i = 0; i < SCRIPT_FILES.length; i++) {
    var filePath = path.join(sceneDir, SCRIPT_FILES[i]);
    var source = fs.readFileSync(filePath, "utf8");
    vm.runInContext(source, sandbox, { filename: filePath });
  }

  return { sandbox: sandbox, postedMessages: postedMessages, fillStyleHistory: ctx2d.__fillStyleHistory };
}

// ---- Tiny test runner -------------------------------------------------------------------------
var tests = [];
function test(name, fn) { tests.push({ name: name, fn: fn }); }

// vm.createContext gives the sandbox its OWN realm -- see alert-layer-layout.tests.js's own remarks
// on why a JSON round-trip is needed before assert.deepStrictEqual against an outer-realm literal.
function plain(value) { return JSON.parse(JSON.stringify(value)); }

// ---- Case 1: readiness handshake ----------------------------------------------------------------

test("the page posts 'ready' once loaded, before any show request", function () {
  var page = loadPage({ withWebview: true });
  assert.deepStrictEqual(page.postedMessages, ["ready"]);
});

// ---- Case 2: show (mixed failed+warning mosaic) -> shown per kind -> done after duration ---------

test("a show request with failed+warning tiles reaches shown per kind, then posts done after duration", function () {
  var page = loadPage({ innerWidth: 1000, innerHeight: 500, withWebview: true });
  page.sandbox.startShowing(["failed", "warning"], 2, 1, 0, 1000);

  page.sandbox.render(0);
  page.sandbox.render(300);
  // failed: shakeMs(230) + FAILURE_REVEAL_MS(700) = 930 -> shown by 950. warning: shakeMs 0, shown by 700.
  page.sandbox.render(950);
  assert.strictEqual(page.sandbox.kindState.failed.state, "shown");
  assert.strictEqual(page.sandbox.kindState.warning.state, "shown");
  assert.strictEqual(page.postedMessages.indexOf("done"), -1, "done must not fire before duration elapses");

  page.sandbox.render(1000); // showStartMs was latched at ms=0 (first render after startShowing); duration=1000
  assert.notStrictEqual(page.postedMessages.indexOf("done"), -1, "expected 'done' once the duration elapsed");
});

// ---- Case 3: the band-intersection layer reuses the SCENE's own progress p (real, not a copy) ----
// Spies on foldingBandParameters (js/layers.js) -- called once by the scene's own drawAtomicOrbits
// AND once per currently-drawing tile by alert-overlay.js's drawFailureBandIntersections. If the
// overlay ever computed its own, tile-scaled progress instead of reusing the scene's, the two calls
// would disagree; this only proves the two AGREE within one frame, not that either one is right.

test("the overlay's band-intersection layer calls foldingBandParameters with the scene's own progress p", function () {
  var page = loadPage({ innerWidth: 800, innerHeight: 600 });
  var calls = [];
  var original = page.sandbox.foldingBandParameters;
  page.sandbox.foldingBandParameters = function (progress) {
    calls.push(progress);
    return original(progress);
  };

  page.sandbox.startShowing(["failed"], 1, 1, 0, 5000);
  page.sandbox.render(0);
  page.sandbox.render(300);
  page.sandbox.render(1200); // shaking(230) + reveal(700) = 930 -> shown
  assert.strictEqual(page.sandbox.kindState.failed.state, "shown");

  calls.length = 0; // only inspect the NEXT frame's calls, in isolation
  page.sandbox.render(1300);
  assert.ok(calls.length >= 2,
    "expected both the scene's drawAtomicOrbits and the overlay's band-intersection layer to call " +
    "foldingBandParameters this frame, saw " + calls.length + " call(s)");
  var distinctValues = calls.filter(function (value, index) { return calls.indexOf(value) === index; });
  assert.strictEqual(distinctValues.length, 1,
    "expected the overlay to reuse the scene's own progress p this frame, saw distinct values: " + JSON.stringify(calls));
});

// ---- Case 4: hash API parses like alert-layer.js (CosmicWin.App/Alerts/Web/alert-layer.js) -------

test("hash API: tiles=/columns=/rows=/gap=/duration= all parse", function () {
  var page = loadPage({ hash: "#tiles=failed,warning&columns=2&rows=1&gap=20&duration=3000" });
  assert.deepStrictEqual(plain(page.sandbox.tiles), ["failed", "warning"]);
  assert.strictEqual(page.sandbox.gridColumns, 2);
  assert.strictEqual(page.sandbox.gridRows, 1);
  assert.strictEqual(page.sandbox.gapPx, 20);
  assert.strictEqual(page.sandbox.durationMs, 3000);
});

test("hash API: the old single-kind #kind= form still maps to one full-canvas tile", function () {
  var page = loadPage({ innerWidth: 640, innerHeight: 480, hash: "#kind=failed&duration=2000" });
  assert.deepStrictEqual(plain(page.sandbox.tiles), ["failed"]);
  assert.strictEqual(page.sandbox.gridColumns, 1);
  assert.deepStrictEqual(plain(page.sandbox.tileRects()), [{ x: 0, y: 0, w: 640, h: 480 }]);
});

// ---- Case 5 (regression sanity): the mosaic tile-layout math itself, ported from alert-layer.js ---

test("2x1 grid: outer + inner gap, equal cells, row-major (same math as alert-layer.js)", function () {
  var page = loadPage({ innerWidth: 1000, innerHeight: 400 });
  page.sandbox.startShowing(["failed", "warning"], 2, 1, 20, 5000);
  assert.deepStrictEqual(plain(page.sandbox.tileRects()), [
    { x: 20, y: 20, w: 470, h: 360 },
    { x: 510, y: 20, w: 470, h: 360 },
  ]);
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
