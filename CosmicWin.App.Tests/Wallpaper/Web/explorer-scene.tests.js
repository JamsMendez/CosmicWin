"use strict";

// html-wallpaper-demo D6a: a committed vm-sandbox harness proving the shared alert-overlay.js's own
// contract against the REAL shipped explorer-scene page -- modelled on
// CosmicWin.App.Tests/Wallpaper/Web/processing-scene.tests.js (that file's own remarks, and
// CosmicWin.App.Tests/Alerts/Web/alert-layer-layout.tests.js before it, explain why a Node
// subprocess exists at all: no .NET JS engine or headless browser exists in this repo). Run via
// CosmicWin.App.Tests/Wallpaper/ExplorerSceneNodeTests.cs:
//   node explorer-scene.tests.js <path-to-the-REAL-shipped-wallpaper-explorer-directory>
//
// This harness intentionally does NOT re-prove the shared state machine/tile-layout/message-parsing
// contract processing-scene.tests.js already covers in depth (same shared module, same behavior) --
// it proves the ONE thing that is genuinely explorer-specific: that this scene's own
// js/see-through-hook.js reuses the SAME sceneTime value the scene's own drawRisingSparks call used
// this frame, translated by the CORRECT tile origin offset for a non-origin tile (see the "KEY
// property" test below), plus the readiness/show/hide/malformed-message basics and this scene's own
// render-loop fault isolation (js/animate.js, D6a -- see that file's own header remarks).

const vm = require("vm");
const fs = require("fs");
const path = require("path");
const assert = require("assert");
const { URLSearchParams } = require("url");
const constellationRingChecks = require(path.join(__dirname, "constellation-ring.checks.js"));

const sceneDir = process.argv[2];
if (!sceneDir) {
  console.error("usage: node explorer-scene.tests.js <path-to-wallpaper-explorer-directory>");
  process.exit(2);
}

const sharedDir = path.join(sceneDir, "..", "shared");

// Exact order index.html loads these in -- see CosmicWin.App/Wallpaper/Web/explorer/index.html.
// Entries starting with "shared:" resolve against sharedDir instead of sceneDir.
const SCRIPT_FILES = [
  "js/config.js",
  "js/math.js",
  "js/glyphs.js",
  "js/earth.js",
  "js/rings.js",
  "js/rising-sparks.js",
  "js/see-through-hook.js",
  "shared:js/alert-overlay.js",
  "shared:js/render-loop.js",
  "js/animate.js",
  "js/main.js",
];

// ---- Minimal DOM/canvas/host mock ------------------------------------------------------------
// One shared 2D-context mock, structurally permissive (a Proxy whose unknown properties are no-op
// functions) -- same technique processing-scene.tests.js already uses, extended here with a tiny
// translation-only CTM (current transformation matrix) tracker: save/restore/translate/setTransform
// are the ONLY transform-affecting calls anywhere in the call chain this harness cares about (the
// shared overlay's own tile offset translate, and js/see-through-hook.js's own draw call) -- neither
// ever rotates or scales along that specific path, so tracking a plain (tx, ty) pair, pushed/popped
// on save/restore and reset by setTransform's own (e, f) translation components, exactly reproduces
// what a real canvas's current transform would report at each drawRisingSparks call this harness spies
// on. Every OTHER real drawing call in this scene (rings.js/earth.js's own animation) already wraps
// its own save()/rotate()/scale() in a balanced save/restore pair, so by the time control reaches this
// harness's own instrumented call sites, those calls have already popped back off the stack.
function make2dContext() {
  var slots = {};
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
        // js/earth.js (verbatim) bakes its globe texture through a REAL ImageData round-trip
        // (createImageData -> write .data -> putImageData) -- a real, minimally-functional
        // Uint8ClampedArray-backed buffer is needed here, unlike every other drawing call this mock
        // otherwise no-ops, or renderFrame's own scene try/catch would report a TypeError every frame
        // (reading .data off undefined) instead of ever reaching this scene's later draw calls
        // (drawChromaticGlowAnimated/drawBlueLayer/drawRisingSparks/drawVignette).
        if (prop === "createImageData") {
          return function (width, height) { return { width: width, height: height, data: new Uint8ClampedArray(width * height * 4) }; };
        }
        if (prop === "putImageData") {
          return function () { /* no-op: nothing reads pixels back in this harness */ };
        }
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
        return function () { /* no-op: beginPath/rect/clip/fill/drawImage/arc/rotate/scale/... */ };
      },
      set: function (target, prop, value) {
        slots[prop] = value;
        return true;
      },
    }),
    // Live snapshot of the CTM's current translation, read at the exact moment a spied call fires.
    currentOffset: function () { return { x: tx, y: ty }; },
  };
}

function makeCanvasElement(ctx2d) {
  return {
    width: 0,
    height: 0,
    style: {},
    getContext: function () { return ctx2d; },
  };
}

// Loads a FRESH copy of the real multi-file page into its own sandbox, mirroring a fresh page load.
function loadPage(options) {
  options = options || {};
  var made = make2dContext();
  var ctx2d = made.context;
  var sceneCanvas = makeCanvasElement(ctx2d);
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
      return null;
    },
    createElement: function (tag) {
      if (tag !== "canvas") throw new Error("unexpected document.createElement(" + tag + ")");
      return makeCanvasElement(ctx2d);
    },
    addEventListener: function () { /* unused: this scene's own main.js drops fullscreenchange (D6a) */ },
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

function plain(value) { return JSON.parse(JSON.stringify(value)); }

// ---- Cases 1-4, consolidated into TWO loadPage() calls -------------------------------------------
// js/earth.js's own one-time equirectangular noise bake (load-time, verbatim reference code -- see
// that file's own header remarks) costs several real seconds PER FRESH vm sandbox realm under Node
// (no JIT warm-up across separate vm.createContext() realms, unlike a real browser tab that loads
// this scene exactly once) -- loadPage() is deliberately called as few times as this file's actual
// test independence needs, rather than once per assertion the way processing-scene.tests.js's own
// (much lighter) page affords. These two tests already fully retrace processing-scene.tests.js's own
// thorough coverage of the SHARED module (readiness/show/done/hide/malformed-message); the
// EXPLORER-specific value here is only "this scene's own wiring exists and reaches the same shared
// module", not new edge-case coverage of that module itself.

test("the page posts 'ready', a show request reaches shown, and posts 'done' after the duration elapses", function () {
  var page = loadPage({ innerWidth: 1000, innerHeight: 500, withWebview: true });
  assert.deepStrictEqual(page.postedMessages, ["ready"]);

  page.sandbox.startShowing(["warning"], 1, 1, 0, 1000);
  page.sandbox.renderFrame(0);
  page.sandbox.renderFrame(300);
  page.sandbox.renderFrame(750); // warning: shakeMs 0 + FAILURE_REVEAL_MS 700 -> shown by 750
  assert.strictEqual(page.sandbox.kindState.warning.state, "shown");
  assert.strictEqual(page.postedMessages.indexOf("done"), -1, "done must not fire before duration elapses");

  page.sandbox.renderFrame(1000); // showStartMs latched at ms=0 (first renderFrame after startShowing)
  assert.notStrictEqual(page.postedMessages.indexOf("done"), -1, "expected 'done' once the duration elapsed");
});

test("hide stops the overlay (direct call and via a real host message), and malformed messages are ignored", function () {
  var page = loadPage({ innerWidth: 1000, innerHeight: 500, withWebview: true });
  assert.strictEqual(page.messageListenerCount, 1,
    "expected handleHostMessage to register exactly one 'message' listener");

  // hide(), called directly.
  page.sandbox.startShowing(["warning"], 1, 1, 0, 5000);
  page.sandbox.renderFrame(0);
  assert.strictEqual(page.sandbox.animating, true);
  page.sandbox.hide();
  assert.strictEqual(page.sandbox.animating, false, "expected the direct hide() call to stop the overlay");

  // A real 'show' host message, exactly as WebViewAlertLayerController.cs's PostShow posts it.
  page.dispatchHostMessage({
    type: "show",
    tiles: ["warning"],
    columns: 1,
    rows: 1,
    gap: 0,
    workArea: { left: 0, top: 0, width: 0, height: 0 },
    duration: 5000,
  });
  page.sandbox.renderFrame(0);
  page.sandbox.renderFrame(750);
  assert.strictEqual(page.sandbox.kindState.warning.state, "shown");

  // Malformed messages must not disturb the still-running overlay.
  assert.doesNotThrow(function () {
    page.dispatchHostMessage(null);
    page.dispatchHostMessage("not-an-object");
    page.dispatchHostMessage({ type: "unrecognized-type" });
  });
  assert.strictEqual(page.sandbox.animating, true,
    "expected malformed messages to leave the still-running overlay untouched");

  // A real 'hide' host message, exactly as WebViewAlertLayerController.cs's End posts it.
  page.dispatchHostMessage({ type: "hide" });
  assert.strictEqual(page.sandbox.animating, false, "expected the real 'hide' message to stop the overlay");
});

// ---- Case 5 (KEY property): the see-through hook reuses the scene's own timeSeconds, offset by the
// tile origin -- js/see-through-hook.js's sceneSeeThroughLayer(g, sceneW, sceneH, sceneTimeSeconds)
// forwards straight into drawRisingSparks(g, sceneTimeSeconds) (js/rising-sparks.js), which this test
// spies on. A 2x1 grid makes the second tile's own origin offset non-zero, so this also proves the
// shared overlay's own tile-offset translate (drawSeeThroughIntersections, shared/js/alert-overlay.js)
// is actually applied before the hook draws -- see this file's own make2dContext() CTM tracker.

test("the see-through hook draws rising sparks with the scene's own timeSeconds, offset by the tile origin", function () {
  var page = loadPage({ innerWidth: 1000, innerHeight: 400 });
  var calls = [];
  var original = page.sandbox.drawRisingSparks;
  page.sandbox.drawRisingSparks = function (context, timeSeconds) {
    calls.push({ timeSeconds: timeSeconds, offset: page.currentOffset() });
    return original.apply(this, arguments);
  };

  // Two "warning" tiles (shakeMs 0): both reach "shown" together, avoiding any FAILED-shake timing
  // asymmetry between the two tiles.
  page.sandbox.startShowing(["warning", "warning"], 2, 1, 0, 5000);
  page.sandbox.renderFrame(0);
  page.sandbox.renderFrame(750);
  assert.strictEqual(page.sandbox.kindState.warning.state, "shown");

  calls.length = 0; // isolate the NEXT frame's calls
  page.sandbox.renderFrame(800);

  assert.ok(calls.length >= 3,
    "expected the scene's own direct call plus one call per shown tile (2), saw " + calls.length);

  var distinctTimes = calls.map(function (c) { return c.timeSeconds; })
    .filter(function (value, index, arr) { return arr.indexOf(value) === index; });
  assert.strictEqual(distinctTimes.length, 1,
    "expected the hook to reuse the SAME timeSeconds the scene used this frame, saw distinct values: " +
    JSON.stringify(distinctTimes));

  var rects = page.sandbox.tileRects();
  var nonOriginTile = rects[1];
  assert.ok(nonOriginTile.x > 0, "test setup sanity: expected the second tile to sit at a non-zero x");
  var nonOriginOffsetSeen = calls.some(function (c) {
    return c.offset.x === -nonOriginTile.x && c.offset.y === -nonOriginTile.y;
  });
  assert.ok(nonOriginOffsetSeen,
    "expected at least one hook call translated by the non-origin tile's own offset (" +
    JSON.stringify({ x: -nonOriginTile.x, y: -nonOriginTile.y }) + "), saw offsets: " +
    JSON.stringify(plain(calls.map(function (c) { return c.offset; }))));
});

// ---- Case 6: render loop fault isolation (D6a, mirrors processing's own D2b fix), one loadPage() --

test("a throwing scene layer does not stop the render loop, nor the alert overlay running the same frame", function () {
  var page = loadPage({ innerWidth: 1000, innerHeight: 500 });
  page.sandbox.startShowing(["warning"], 1, 1, 0, 5000);
  var thrown = 0;
  // D6a review (R3-fault-isolation-test-overclaims / R2-fault-isolation-test-misleading-claims):
  // throwing on every frame -- not just the first -- is what actually proves the per-stage dedup (one
  // console.error for N throws) and that the overlay keeps advancing while the scene keeps failing on
  // every single frame, matching what the assertions below claim.
  page.sandbox.drawStarfield = function () {
    thrown++;
    throw new Error("D6a-fault-isolation-scene");
  };

  var before = page.requestAnimationFrameCalls.length;
  page.sandbox.renderFrame(0); // this frame throws inside drawStarfield
  assert.strictEqual(page.requestAnimationFrameCalls.length, before + 1,
    "expected scheduleFrame(renderFrame) to still run once even though this frame threw");

  page.sandbox.renderFrame(300);
  page.sandbox.renderFrame(750); // warning: shown well before 750ms
  assert.strictEqual(page.requestAnimationFrameCalls.length, before + 3,
    "expected the loop to keep scheduling frames while the scene layer keeps throwing every frame");
  assert.strictEqual(thrown, 3, "test setup sanity: drawStarfield should have thrown on all 3 frames");
  assert.strictEqual(page.sandbox.kindState.warning.state, "shown",
    "expected the alert overlay to keep advancing even while the scene layer above it keeps throwing every frame");
  assert.strictEqual(page.consoleErrorCalls.length, 1,
    "expected the repeating error (thrown on every frame) to be reported once, not once per frame");
});

// ---- Case 7: the alert clock itself (alertSceneMs) must not be able to freeze the loop (D6a review
// R4-alertSceneMs-outside-fault-isolation / R3-alertSceneMs-outside-try) -- unlike drawStarfield (Case
// 6, a call made INSIDE the scene's own try/catch), alertSceneMs used to run BEFORE and OUTSIDE both
// try/catch blocks, so a throw here used to skip scheduleFrame(renderFrame) entirely and freeze the
// wallpaper for good, instead of only failing this one frame's "scene" stage like Case 6 does.

test("a throwing alert clock (alertSceneMs) does not freeze the render loop, and the scene renders again once it recovers", function () {
  var page = loadPage({ innerWidth: 1000, innerHeight: 500 });
  var originalAlertSceneMs = page.sandbox.alertSceneMs;
  var originalDrawStarfield = page.sandbox.drawStarfield;
  var sceneDrawCalls = 0;
  page.sandbox.drawStarfield = function () {
    sceneDrawCalls++;
    return originalDrawStarfield.apply(this, arguments);
  };
  page.sandbox.alertSceneMs = function () {
    throw new Error("D6a-alert-clock-throws");
  };

  var before = page.requestAnimationFrameCalls.length;
  page.sandbox.renderFrame(0); // alertSceneMs throws before the scene draws anything this frame
  assert.strictEqual(page.requestAnimationFrameCalls.length, before + 1,
    "expected scheduleFrame(renderFrame) to still run even though alertSceneMs threw");
  assert.strictEqual(sceneDrawCalls, 0,
    "test setup sanity: the scene must not have drawn anything on the frame the clock threw");

  page.sandbox.renderFrame(16);
  page.sandbox.renderFrame(32);
  assert.strictEqual(page.requestAnimationFrameCalls.length, before + 3,
    "expected the loop to keep scheduling frames while the alert clock keeps throwing every frame");
  assert.strictEqual(page.consoleErrorCalls.length, 1,
    "expected the repeating alert-clock error to be reported once, not once per frame");

  page.sandbox.alertSceneMs = originalAlertSceneMs;
  page.sandbox.renderFrame(48);
  assert.strictEqual(page.requestAnimationFrameCalls.length, before + 4,
    "expected the loop to keep scheduling frames after the alert clock recovers");
  assert.strictEqual(sceneDrawCalls, 1,
    "expected the scene to render again once the alert clock stopped throwing");
});

// ---- Case 8: the shared overlay's own see-through hook call must not leave the offscreen
// "intersections" context with an unbalanced save()/restore() when the hook throws (D6a review
// R3-hook-throw-leaves-offscreen-save, shared/js/alert-overlay.js's drawSeeThroughIntersections).
// Exercised directly (not through the full render loop) with a small dedicated spy context, since the
// scene-wide mock above shares ONE 2D-context mock across every canvas/offscreen layer, which would
// confound a save()/restore() count with every OTHER draw call in the same frame.

test("drawSeeThroughIntersections balances its own save/restore even when the hook throws", function () {
  var page = loadPage({ innerWidth: 1000, innerHeight: 500 });

  function makeSpyContext() {
    var counts = { save: 0, restore: 0 };
    return {
      width: 10, height: 10, counts: counts,
      save: function () { counts.save++; },
      restore: function () { counts.restore++; },
      translate: function () {},
      setTransform: function () {},
      fillRect: function () {},
      drawImage: function () {},
    };
  }

  // A throwing hook: drawSeeThroughIntersections must still leave its OWN save()/restore() pair around
  // the hook balanced, even though the error must still propagate to the caller (the scene's own
  // "alert-overlay" render stage is what actually reports it -- see animate.js/main.js).
  page.sandbox.sceneSeeThroughLayer = function () { throw new Error("R3-hook-throws"); };
  var throwingG = makeSpyContext();
  assert.throws(function () {
    page.sandbox.drawSeeThroughIntersections(throwingG, { width: 10, height: 10 }, 0, 0, 1000, 500, 0, "rgb(0,0,0)");
  }, /R3-hook-throws/);
  assert.strictEqual(throwingG.counts.save, throwingG.counts.restore,
    "expected the hook's own save()/restore() to stay balanced even though the hook threw, saw " +
    JSON.stringify(throwingG.counts));

  // A later, non-throwing call (a fresh offscreen context, as failureLayer() always hands out a reset
  // backing size/transform, never the same throwingG above) still balances normally.
  page.sandbox.sceneSeeThroughLayer = function () { /* no-op: only the save/restore balance matters here */ };
  var normalG = makeSpyContext();
  page.sandbox.drawSeeThroughIntersections(normalG, { width: 10, height: 10 }, 0, 0, 1000, 500, 0, "rgb(0,0,0)");
  assert.strictEqual(normalG.counts.save, normalG.counts.restore,
    "expected a normal (non-throwing) call to also balance save()/restore(), saw " + JSON.stringify(normalG.counts));
});

// ---- Shared render-loop fps cap smoke check (D6d, html-wallpaper-demo) ----------------------------
// The throttle itself (skip-still-reschedules, exact 30fps spacing, fps parsing, alert duration
// unaffected) is exhaustively covered against the shared code in processing-scene.tests.js; this only
// proves THIS scene's own render loop actually routes through it (animate.js's own
// `scheduleFrame(renderFrame)`, not a local override) rather than re-deriving the throttle's own
// correctness.

test("shared render loop: fps=30 in the URL caps this scene's OWN renderFrame() loop, not just the shared helper", function () {
  var page = loadPage({ innerWidth: 800, innerHeight: 600, search: "?fps=30" });
  var sceneDrawCalls = 0;
  var original = page.sandbox.drawStarfield;
  page.sandbox.drawStarfield = function () { sceneDrawCalls++; return original.apply(this, arguments); };

  var frameTimesMs = [0, 16.7, 33.3, 50.0, 66.7, 83.3];
  frameTimesMs.forEach(function (ms) {
    var wrapper = page.requestAnimationFrameCalls[page.requestAnimationFrameCalls.length - 1];
    wrapper(ms);
  });

  assert.ok(sceneDrawCalls < frameTimesMs.length,
    "expected renderFrame() to run fewer times than rAF callbacks under a 30fps cap, ran " + sceneDrawCalls + " of " + frameTimesMs.length);
  assert.ok(sceneDrawCalls >= 2, "expected at least two frames to still render, ran " + sceneDrawCalls);
});

// ---- Constellation ring checks (shared with the idle scene) ------------------------------
// The checks themselves live once in constellation-ring.checks.js; see that file for why.

test("every constellation figure only joins stars it actually has, and leaves no star unjoined", function () {
  var page = loadPage({ innerWidth: 800, innerHeight: 600 });
  constellationRingChecks.checkConstellationTable(page.sandbox);
});

test("the constellation tunables match the idle scene's, value for value", function () {
  constellationRingChecks.checkTunablesMatchTwin(sceneDir, "idle");
});

// ---- Full-circle fit (2026-09-27): the disc border must stay fully on screen, with >= 25px margin
// on every side, instead of the old fixed min(W,H) basis clipping its top/bottom on a landscape
// screen. Asserted against what the scene actually passes to drawDiscBorder (spied), not a
// re-derivation of the fit formula, so this only fails when the real draw call would clip.

test("the disc border fits inside the screen with >= 25px margin on every side, at 1920x1080 and 1000x800", function () {
  var margin = 25;
  [{ innerWidth: 1920, innerHeight: 1080 }, { innerWidth: 1000, innerHeight: 800 }].forEach(function (size) {
    var page = loadPage(size);
    var borderCalls = [];
    var original = page.sandbox.drawDiscBorder;
    page.sandbox.drawDiscBorder = function (context, cx, cy, innerRadius, outerRadius) {
      borderCalls.push({ cx: cx, cy: cy, outerRadius: outerRadius });
      return original.apply(this, arguments);
    };
    page.sandbox.renderFrame(0);
    var label = size.innerWidth + "x" + size.innerHeight;
    assert.strictEqual(borderCalls.length, 1, label + ": expected exactly one drawDiscBorder call per frame");
    var call = borderCalls[0];
    assert.ok(call.outerRadius + margin <= call.cy,
      label + ": expected outerRadius(" + call.outerRadius + ") + " + margin + " <= cy(" + call.cy + ")");
    assert.ok(call.outerRadius + margin <= size.innerHeight - call.cy,
      label + ": expected outerRadius(" + call.outerRadius + ") + " + margin + " <= H-cy(" + (size.innerHeight - call.cy) + ")");
    assert.ok(call.outerRadius + margin <= call.cx,
      label + ": expected outerRadius(" + call.outerRadius + ") + " + margin + " <= cx(" + call.cx + ")");
    assert.ok(call.outerRadius + margin <= size.innerWidth - call.cx,
      label + ": expected outerRadius(" + call.outerRadius + ") + " + margin + " <= W-cx(" + (size.innerWidth - call.cx) + ")");
  });
});

// ---- Chroma width (2026-09-27): the full-circle fit shrank sceneBasis, and the chromatic glow
// sized itself from it, so its bottom fan got narrower too. The bottom sample must keep its
// screen-relative width (the pre-fit look), the tip must stay sized from sceneBasis (tied to the
// Earth), and the fan must thin monotonically on the way up.

test("the chromatic glow starts screen-wide at the bottom and thins toward the Earth, at 1920x1080 and 1000x800", function () {
  [{ innerWidth: 1920, innerHeight: 1080 }, { innerWidth: 1000, innerHeight: 800 }].forEach(function (size) {
    var page = loadPage(size);
    var label = size.innerWidth + "x" + size.innerHeight;
    var halfWidth = page.sandbox.chromaticGlowSampleHalfWidth;
    assert.strictEqual(typeof halfWidth, "function", label + ": expected chromaticGlowSampleHalfWidth(t)");
    var screenBasis = Math.min(size.innerWidth, size.innerHeight);
    // Read the shipped tunables from the page realm (top-level consts share its global lexical
    // scope) so retuning them never leaves a stale copy here.
    var radiusFraction = vm.runInContext("CHROMATIC_GLOW_RADIUS_FRACTION", page.sandbox);
    var baseWidthScale = vm.runInContext("CHROMATIC_GLOW_BASE_WIDTH_SCALE", page.sandbox);
    var bottom = halfWidth(0);
    var tip = halfWidth(1);
    var expectedBottom = screenBasis * radiusFraction * baseWidthScale;
    var expectedTip = page.sandbox.sceneBasis(size.innerWidth, size.innerHeight) * radiusFraction;
    assert.ok(Math.abs(bottom - expectedBottom) < 1e-6,
      label + ": expected bottom half-width " + expectedBottom + ", got " + bottom);
    assert.ok(Math.abs(tip - expectedTip) < 1e-6,
      label + ": expected tip half-width " + expectedTip + ", got " + tip);
    var previous = Infinity;
    for (var i = 0; i <= 10; i++) {
      var w = halfWidth(i / 10);
      assert.ok(w < previous, label + ": half-width must shrink on the way up (t=" + (i / 10) + ": " + w + " >= " + previous + ")");
      previous = w;
    }
  });
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
