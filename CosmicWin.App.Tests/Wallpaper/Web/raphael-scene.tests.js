"use strict";

// html-wallpaper-demo D6b: a committed vm-sandbox harness proving the shared alert-overlay.js's own
// contract against the REAL shipped raphael-scene page -- modelled on
// CosmicWin.App.Tests/Wallpaper/Web/processing-scene.tests.js (structurally the closer template: this
// scene, like processing, has no explorer-style expensive load-time bake, so one loadPage() call per
// case is affordable). Run via CosmicWin.App.Tests/Wallpaper/RaphaelSceneNodeTests.cs:
//   node raphael-scene.tests.js <path-to-the-REAL-shipped-wallpaper-raphael-directory>
//
// This harness intentionally does NOT re-prove the shared state machine/tile-layout/message-parsing
// contract processing-scene.tests.js already covers in depth (same shared module, same behavior) --
// it proves the ONE thing genuinely raphael-specific: that this scene's own js/see-through-hook.js
// redraws the REAL gold glyph ring (same radius/rotation/glyph pool the scene's own drawGlyphRings
// used this frame -- js/layers.js's goldGlyphRingDrawParams, the one small D6b seam that file adds),
// translated by the CORRECT tile origin offset for a non-origin tile, and draws NO other ring, plus
// the readiness/show/hide/malformed-message basics and this scene's own render-loop fault isolation
// (js/main.js, mirroring processing's own D2b fix and explorer's own D6a alert-clock fix).

const vm = require("vm");
const fs = require("fs");
const path = require("path");
const assert = require("assert");
const { URLSearchParams } = require("url");

const sceneDir = process.argv[2];
if (!sceneDir) {
  console.error("usage: node raphael-scene.tests.js <path-to-wallpaper-raphael-directory>");
  process.exit(2);
}

const sharedDir = path.join(sceneDir, "..", "shared");

// Exact order index.html loads these in -- see CosmicWin.App/Wallpaper/Web/raphael/index.html.
// Entries starting with "shared:" resolve against sharedDir instead of sceneDir.
const SCRIPT_FILES = [
  "js/config.js",
  "js/math.js",
  "js/hexadecagon.js",
  "js/nebula.js",
  "js/sphere.js",
  "js/scene-data.js",
  "js/feathers.js",
  "js/glyphs.js",
  "js/glyph-rings.js",
  "js/digits.js",
  "js/central-core.js",
  "js/sprites.js",
  "js/layers.js",
  "js/see-through-hook.js",
  "shared:js/alert-overlay.js",
  "shared:js/render-loop.js",
  "js/main.js",
];

// ---- Minimal DOM/canvas/host mock ------------------------------------------------------------
// One shared 2D-context mock, structurally permissive (a Proxy whose unknown properties are no-op
// functions), extended with the gradient/conic-gradient factories the full scene (layers.js,
// sprites.js) actually calls -- same technique processing-scene.tests.js's own mock uses -- PLUS a
// tiny translation-only CTM (current transformation matrix) tracker (same technique
// explorer-scene.tests.js's own mock uses): save/restore/translate/setTransform are the only
// transform-affecting calls anywhere in the call chain this harness's own KEY test cares about (the
// shared overlay's own tile-offset translate, and js/see-through-hook.js's own drawGlyphRing call),
// so tracking a plain (tx, ty) pair, pushed/popped on save/restore and reset by setTransform's own
// (e, f) translation components, exactly reproduces what a real canvas's current transform would
// report at each spied call.
function make2dContext() {
  var slots = {};
  var fillStyleHistory = [];
  var gradient = { addColorStop: function () {} };
  var stack = [];
  var tx = 0, ty = 0;
  return {
    context: new Proxy({}, {
      get: function (target, prop) {
        if (prop === "measureText") {
          return function (text) { return { width: String(text).length * 8 }; };
        }
        if (prop === "createRadialGradient" || prop === "createLinearGradient" || prop === "createConicGradient") {
          return function () { return gradient; };
        }
        if (prop === "__fillStyleHistory") return fillStyleHistory;
        if (prop === "save") {
          return function () { stack.push({ tx: tx, ty: ty }); };
        }
        if (prop === "restore") {
          return function () { var entry = stack.pop(); if (entry) { tx = entry.tx; ty = entry.ty; } };
        }
        if (prop === "translate") {
          return function (dx, dy) { tx += dx; ty += dy; };
        }
        if (prop === "setTransform") {
          return function (a, b, c, d, e, f) { tx = e || 0; ty = f || 0; };
        }
        if (prop in slots) return slots[prop];
        return function () { /* no-op: beginPath/rect/clip/fill/drawImage/ellipse/arc/rotate/scale/... */ };
      },
      set: function (target, prop, value) {
        slots[prop] = value;
        if (prop === "fillStyle") fillStyleHistory.push(value);
        return true;
      },
    }),
    // Live snapshot of the CTM's current translation, read at the exact moment a spied call fires.
    currentOffset: function () { return { x: tx, y: ty }; },
    fillStyleHistory: fillStyleHistory,
  };
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
  var made = make2dContext();
  var ctx2d = made.context;
  var sceneCanvas = makeCanvasElement(ctx2d);
  var nebulaCanvasElement = makeCanvasElement(ctx2d);
  var postedMessages = [];
  var requestAnimationFrameCalls = [];
  var consoleErrorCalls = [];
  var consoleMock = {
    error: function () { consoleErrorCalls.push(Array.prototype.slice.call(arguments)); },
    log: function () { /* no-op: unused by the scene */ },
    warn: function () { /* no-op: unused by the scene */ },
  };
  var messageListeners = [];

  var windowMock = {
    innerWidth: options.innerWidth || 1000,
    innerHeight: options.innerHeight || 500,
    devicePixelRatio: options.devicePixelRatio || 1,
    // No WebGLRenderingContext global -- initializeNebulaRenderer (nebula.js) bails out before ever
    // touching a 'webgl' context, exactly like a browser with WebGL disabled would.
    WebGLRenderingContext: undefined,
    requestAnimationFrame: function (callback) { requestAnimationFrameCalls.push(callback); },
    addEventListener: function () { /* "resize" only; never fired here */ },
    chrome: options.withWebview
      ? {
          webview: {
            postMessage: function (message) { postedMessages.push(message); },
            addEventListener: function (type, listener) {
              if (type === "message") messageListeners.push(listener);
            },
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
    console: consoleMock,
  };
  vm.createContext(sandbox);
  for (var i = 0; i < SCRIPT_FILES.length; i++) {
    var entry = SCRIPT_FILES[i];
    var isShared = entry.indexOf("shared:") === 0;
    var filePath = path.join(isShared ? sharedDir : sceneDir, isShared ? entry.slice("shared:".length) : entry);
    var source = fs.readFileSync(filePath, "utf8");
    vm.runInContext(source, sandbox, { filename: filePath });
  }

  return {
    sandbox: sandbox,
    postedMessages: postedMessages,
    requestAnimationFrameCalls: requestAnimationFrameCalls,
    consoleErrorCalls: consoleErrorCalls,
    canvas: sceneCanvas,
    ctx: ctx2d,
    messageListenerCount: messageListeners.length,
    currentOffset: made.currentOffset,
    dispatchHostMessage: function (data) {
      messageListeners.forEach(function (listener) { listener({ data: data }); });
    },
  };
}

// ---- Tiny test runner -------------------------------------------------------------------------
var tests = [];
function test(name, fn) { tests.push({ name: name, fn: fn }); }

// ---- Case 1: readiness handshake ----------------------------------------------------------------

test("the page posts 'ready' once loaded, before any show request", function () {
  var page = loadPage({ withWebview: true });
  assert.deepStrictEqual(page.postedMessages, ["ready"]);
});

// ---- Case 2: show -> shown per kind -> done after duration, then hide ----------------------------

test("a show request reaches shown, posts done after duration, and hide stops it", function () {
  var page = loadPage({ innerWidth: 1000, innerHeight: 500, withWebview: true });
  page.sandbox.startShowing(["failed", "warning"], 2, 1, 0, 1000);

  page.sandbox.render(0);
  page.sandbox.render(300);
  // failed: shakeMs(230) + FAILURE_REVEAL_MS(700) = 930 -> shown by 950. warning: shakeMs 0, shown by 700.
  page.sandbox.render(950);
  assert.strictEqual(page.sandbox.kindState.failed.state, "shown");
  assert.strictEqual(page.sandbox.kindState.warning.state, "shown");
  assert.strictEqual(page.postedMessages.indexOf("done"), -1, "done must not fire before duration elapses");

  page.sandbox.render(1000); // showStartMs latched at ms=0 (first render after startShowing)
  assert.notStrictEqual(page.postedMessages.indexOf("done"), -1, "expected 'done' once the duration elapsed");

  page.sandbox.hide();
  assert.strictEqual(page.sandbox.animating, false, "expected hide() to stop the overlay");
});

// ---- Case 3: the real WebView2 host message path, and malformed messages are ignored -------------

test("a real 'show'/'hide' host message drives the overlay, and a malformed message is ignored", function () {
  var page = loadPage({ innerWidth: 1000, innerHeight: 500, withWebview: true });
  assert.strictEqual(page.messageListenerCount, 1,
    "expected handleHostMessage to register exactly one 'message' listener");

  page.dispatchHostMessage({
    type: "show",
    tiles: ["warning"],
    columns: 1,
    rows: 1,
    gap: 0,
    workArea: { left: 0, top: 0, width: 0, height: 0 },
    duration: 5000,
  });
  page.sandbox.render(0);
  page.sandbox.render(750); // warning: shown well before 750ms
  assert.strictEqual(page.sandbox.kindState.warning.state, "shown");

  assert.doesNotThrow(function () {
    page.dispatchHostMessage(null);
    page.dispatchHostMessage("not-an-object");
    page.dispatchHostMessage({ type: "unrecognized-type" });
  });
  assert.strictEqual(page.sandbox.animating, true,
    "expected malformed messages to leave the still-running overlay untouched");

  page.dispatchHostMessage({ type: "hide" });
  assert.strictEqual(page.sandbox.animating, false, "expected the real 'hide' message to stop the overlay");
});

// ---- Case 4 (KEY property): the see-through hook redraws the REAL gold ring -----------------------
// js/see-through-hook.js's sceneSeeThroughLayer(g, sceneW, sceneH, progress) must call
// js/glyphs.js's drawGlyphRing with the SAME radius/rotation/glyph pool js/layers.js's own
// drawGlyphRings(cx, cy, p) used to draw the gold ring THIS frame -- proved two ways: (a) spying on
// drawGlyphRings itself captures the scene's own real `p` this frame, independently recomputing the
// expected rotation/radius from that `p` via the SAME pure geometry functions (coreRadius/
// glyphRingAnnuli) the scene's own gold-ring draw and the hook both read; (b) spying on drawGlyphRing
// (glyphs.js) captures what the hook ACTUALLY drew with. A 2x2 grid makes tile[3] (bottom-right) a
// non-origin tile, proving the shared overlay's own tile-offset translate (drawSeeThroughIntersections,
// shared/js/alert-overlay.js) is applied before the hook draws -- see this file's own CTM tracker.
// "draws NO other ring": drawGlyphRing (unlike drawOutlineGlyphRing, the SPRITE-based real paint path
// for BOTH gold and blue) is called ONLY by this hook in the shipped page -- one call per shown tile
// proves the hook draws exactly one ring, never a second (blue) one.

test("the see-through hook redraws the gold ring with the scene's own radius/rotation/pool, offset by a non-origin tile, and draws no other ring", function () {
  var page = loadPage({ innerWidth: 1000, innerHeight: 800 });

  var sceneProgressCalls = [];
  var originalDrawGlyphRings = page.sandbox.drawGlyphRings;
  page.sandbox.drawGlyphRings = function (cx, cy, progress) {
    sceneProgressCalls.push(progress);
    return originalDrawGlyphRings.apply(this, arguments);
  };

  var hookCalls = [];
  var originalDrawGlyphRing = page.sandbox.drawGlyphRing;
  page.sandbox.drawGlyphRing = function (context, pool, cx, cy, radius, count, glyphSize, startAngle, rotation, color, lineWidth, lightFn) {
    hookCalls.push({ pool: pool, radius: radius, rotation: rotation, offset: page.currentOffset() });
    return originalDrawGlyphRing.apply(this, arguments);
  };

  // Four "warning" tiles (shakeMs 0): all reach "shown" together, avoiding any FAILED-shake timing
  // asymmetry between tiles.
  page.sandbox.startShowing(["warning", "warning", "warning", "warning"], 2, 2, 0, 5000);
  page.sandbox.render(0);
  page.sandbox.render(750);
  assert.strictEqual(page.sandbox.kindState.warning.state, "shown");

  sceneProgressCalls.length = 0;
  hookCalls.length = 0; // isolate the NEXT frame's calls
  page.sandbox.render(800);

  assert.strictEqual(sceneProgressCalls.length, 1,
    "test setup sanity: expected exactly one real drawGlyphRings call (the scene's own) this frame");
  var sceneProgress = sceneProgressCalls[0];

  assert.strictEqual(hookCalls.length, 4,
    "expected exactly one drawGlyphRing call per shown tile (4), proving the hook draws no other ring, saw " + hookCalls.length);

  // Independently recompute the TRUE gold-ring radius/rotation from the scene's own captured
  // progress, via the same pure geometry (function declarations -- reachable on the sandbox global,
  // unlike config.js's `let W`/`H` and `const TAU`/`GLYPH_RING_GOLD_ROTATION_SPEED`, none of which
  // attach to the vm context's global object -- see the RING_GLYPH_POOL remark below for the same
  // reasoning) the scene's own drawGlyphRings and the hook both read. W/H/TAU are recomputed from
  // this test's own known loadPage() inputs instead (resize()'s cssWidth/cssHeight fall back to
  // window.innerWidth/innerHeight when the mock canvas has no getBoundingClientRect, i.e. exactly
  // the 1000x800 passed above); GLYPH_RING_GOLD_ROTATION_SPEED is config.js's own documented literal
  // (0.48, "RAP-17... x4").
  var sceneW = 1000, sceneH = 800;
  var r = page.sandbox.coreRadius(Math.min(sceneW, sceneH));
  var gold = page.sandbox.glyphRingAnnuli(r)[1];
  var expectedRadius = (gold.innerRadius + gold.outerRadius) / 2;
  var GLYPH_RING_GOLD_ROTATION_SPEED = 0.48;
  var expectedRotation = sceneProgress * (Math.PI * 2) * GLYPH_RING_GOLD_ROTATION_SPEED;

  // RING_GLYPH_POOL (js/glyphs.js) is a top-level `const` -- like config.js's own `ctx`/`canvas`
  // (see processing-scene.tests.js's own remarks), it never becomes a property of the vm sandbox's
  // global object, so it cannot be read back as page.sandbox.RING_GLYPH_POOL for a direct identity
  // check. Proved instead by IDENTITY ACROSS CALLS: every tile's hook call this frame must receive
  // the exact SAME pool object (never a fresh/copied one built per call) -- the only way that can
  // hold is if the hook keeps reading the one real RING_GLYPH_POOL reference every time -- plus its
  // length matching RING_GLYPH_POOL_SIZE (96, glyphs.js -- also const, same reasoning) and its shape
  // matching a real hieroglyph stroke pool (an array of stroke arrays, each stroke a 'line'/'curve'/
  // 'dot' descriptor).
  var expectedPool = hookCalls[0].pool;
  assert.ok(Array.isArray(expectedPool) && expectedPool.length === 96,
    "expected the hook's pool to be the real 96-glyph RING_GLYPH_POOL, saw length " + (expectedPool && expectedPool.length));
  assert.ok(Array.isArray(expectedPool[0]) && expectedPool[0].every(function (stroke) {
    return stroke && (stroke.type === 'line' || stroke.type === 'curve' || stroke.type === 'dot');
  }), "expected the hook's pool entries to be real hieroglyph stroke arrays");

  hookCalls.forEach(function (call, index) {
    assert.strictEqual(call.pool, expectedPool,
      "expected hook call #" + index + " to reuse the SAME pool object every other tile's call used this frame");
    assert.ok(Math.abs(call.radius - expectedRadius) < 1e-9,
      "expected hook call #" + index + " radius " + call.radius + " to equal the true gold radius " + expectedRadius);
    assert.ok(Math.abs(call.rotation - expectedRotation) < 1e-9,
      "expected hook call #" + index + " rotation " + call.rotation + " to equal the scene's own rotation " + expectedRotation);
  });

  var rects = page.sandbox.tileRects();
  var nonOriginTile = rects[3];
  assert.ok(nonOriginTile.x > 0 && nonOriginTile.y > 0,
    "test setup sanity: expected tile[3] (bottom-right of a 2x2 grid) to sit at a non-zero (x, y)");
  var nonOriginOffsetSeen = hookCalls.some(function (call) {
    return call.offset.x === -nonOriginTile.x && call.offset.y === -nonOriginTile.y;
  });
  assert.ok(nonOriginOffsetSeen,
    "expected at least one hook call translated by the non-origin tile's own offset (" +
    JSON.stringify({ x: -nonOriginTile.x, y: -nonOriginTile.y }) + "), saw offsets: " +
    JSON.stringify(hookCalls.map(function (c) { return c.offset; })));
});

// ---- Case 5: render loop fault isolation -- a scene layer throwing on EVERY frame ------------------

test("a scene layer that throws on every frame does not stop the render loop or the alert overlay, and is reported once", function () {
  var page = loadPage({ innerWidth: 800, innerHeight: 600 });
  page.sandbox.startShowing(["warning"], 1, 1, 0, 5000);
  var thrown = 0;
  page.sandbox.drawStars = function () {
    thrown++;
    throw new Error("D6b-fault-isolation-scene");
  };

  var before = page.requestAnimationFrameCalls.length;
  page.sandbox.render(0);
  assert.strictEqual(page.requestAnimationFrameCalls.length, before + 1,
    "expected scheduleFrame(render) to still run once even though this frame threw");

  page.sandbox.render(300);
  page.sandbox.render(750); // warning: shown well before 750ms
  assert.strictEqual(page.requestAnimationFrameCalls.length, before + 3,
    "expected the loop to keep scheduling frames while the scene layer keeps throwing every frame");
  assert.strictEqual(thrown, 3, "test setup sanity: drawStars should have thrown on all 3 frames");
  assert.strictEqual(page.sandbox.kindState.warning.state, "shown",
    "expected the alert overlay to keep advancing even while the scene layer above it keeps throwing every frame");
  assert.strictEqual(page.consoleErrorCalls.length, 1,
    "expected the repeating error (thrown on every frame) to be reported once, not once per frame");
});

// ---- Case 6: the alert clock itself (alertSceneMs) must not be able to freeze the loop -------------

test("a throwing alert clock (alertSceneMs) does not freeze the render loop, and the scene renders again once it recovers", function () {
  var page = loadPage({ innerWidth: 800, innerHeight: 600 });
  var originalAlertSceneMs = page.sandbox.alertSceneMs;
  var originalDrawStars = page.sandbox.drawStars;
  var sceneDrawCalls = 0;
  page.sandbox.drawStars = function () {
    sceneDrawCalls++;
    return originalDrawStars.apply(this, arguments);
  };
  page.sandbox.alertSceneMs = function () {
    throw new Error("D6b-alert-clock-throws");
  };

  var before = page.requestAnimationFrameCalls.length;
  page.sandbox.render(0); // alertSceneMs throws before the scene draws anything this frame
  assert.strictEqual(page.requestAnimationFrameCalls.length, before + 1,
    "expected scheduleFrame(render) to still run even though alertSceneMs threw");
  assert.strictEqual(sceneDrawCalls, 0,
    "test setup sanity: the scene must not have drawn anything on the frame the clock threw");

  page.sandbox.render(16);
  page.sandbox.render(32);
  assert.strictEqual(page.requestAnimationFrameCalls.length, before + 3,
    "expected the loop to keep scheduling frames while the alert clock keeps throwing every frame");
  assert.strictEqual(page.consoleErrorCalls.length, 1,
    "expected the repeating alert-clock error to be reported once, not once per frame");

  page.sandbox.alertSceneMs = originalAlertSceneMs;
  page.sandbox.render(48);
  assert.strictEqual(page.requestAnimationFrameCalls.length, before + 4,
    "expected the loop to keep scheduling frames after the alert clock recovers");
  assert.strictEqual(sceneDrawCalls, 1,
    "expected the scene to render again once the alert clock stopped throwing");
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
