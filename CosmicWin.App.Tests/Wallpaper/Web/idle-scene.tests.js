"use strict";

// html-wallpaper-demo D6c: a committed vm-sandbox harness proving the shared alert-overlay.js's own
// contract against the REAL shipped idle-scene page -- modelled on
// CosmicWin.App.Tests/Wallpaper/Web/explorer-scene.tests.js (that file's own remarks, and
// processing-scene.tests.js before it, explain why a Node subprocess exists at all: no .NET JS
// engine or headless browser exists in this repo). Run via
// CosmicWin.App.Tests/Wallpaper/IdleSceneNodeTests.cs:
//   node idle-scene.tests.js <path-to-the-REAL-shipped-wallpaper-idle-directory>
//
// This harness intentionally does NOT re-prove the shared state machine/tile-layout/message-parsing
// contract processing-scene.tests.js already covers in depth (same shared module, same behavior),
// nor the offscreen save()/restore() balance explorer-scene.tests.js already covers (also shared
// module behavior, not idle-specific) -- it proves the ONE thing that is genuinely idle-specific:
// that this scene's own js/see-through-hook.js stamps the EXACT SAME constellation-ring cache/
// angle/center the scene's own js/animate.js render loop drew THIS FRAME (RING_ANIMATIONS[0], the
// "constellation" ring), for EVERY tile, offset by that tile's own origin, and never a different
// ring -- plus the readiness/show/hide/malformed-message basics and this scene's own render-loop
// fault isolation (js/animate.js, D6a/D6b precedent).

const vm = require("vm");
const fs = require("fs");
const path = require("path");
const assert = require("assert");
const { URLSearchParams } = require("url");

const sceneDir = process.argv[2];
if (!sceneDir) {
  console.error("usage: node idle-scene.tests.js <path-to-wallpaper-idle-directory>");
  process.exit(2);
}

const sharedDir = path.join(sceneDir, "..", "shared");

// D6c improvement over explorer-scene.tests.js/raphael-scene.tests.js's own hardcoded SCRIPT_FILES
// list (an open finding on both -- "harness script order hardcoded"): read the REAL shipped
// index.html's own <script src="..."> tags, in order, instead of maintaining a parallel list here
// that could silently drift from what the page actually loads. "../shared/..." sources resolve
// against sharedDir, exactly like the "shared:" prefix explorer/raphael's own hardcoded lists used.
function scriptFilesFromIndexHtml(directory) {
  const html = fs.readFileSync(path.join(directory, "index.html"), "utf8");
  const scriptTagPattern = /<script\s+src="([^"]+)"\s*>\s*<\/script>/g;
  const entries = [];
  let match;
  while ((match = scriptTagPattern.exec(html)) !== null) {
    const src = match[1];
    entries.push(src.indexOf("../shared/") === 0 ? "shared:" + src.slice("../shared/".length) : src);
  }
  return entries;
}

const SCRIPT_FILES = scriptFilesFromIndexHtml(sceneDir);
if (SCRIPT_FILES.length === 0) {
  console.error("expected at least one <script src=\"...\"> tag in " + path.join(sceneDir, "index.html"));
  process.exit(2);
}

// ---- Minimal DOM/canvas/host mock ------------------------------------------------------------
// One shared 2D-context mock, structurally permissive (a Proxy whose unknown properties are no-op
// functions) -- same technique explorer-scene.tests.js/processing-scene.tests.js already use,
// extended here with the same tiny translation-only CTM (current transformation matrix) tracker
// explorer-scene.tests.js's own make2dContext uses (see that file's own remarks): save/restore/
// translate/setTransform are the ONLY transform-affecting calls anywhere in the call chain this
// harness cares about (the shared overlay's own tile offset translate, and js/see-through-hook.js's
// own draw call).
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
        // (reading .data off undefined) instead of ever reaching this scene's later draw calls.
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
    addEventListener: function () { /* unused: this scene's own main.js drops fullscreenchange (D6c) */ },
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

// ---- Cases 1-2: readiness / show-shown-done / hide / malformed messages -- same shared-module
// contract processing-scene.tests.js/explorer-scene.tests.js already cover in depth; the value here
// is only "this scene's own wiring exists and reaches the same shared module".

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

// ---- Case 3 (KEY property): the see-through hook stamps the SAME constellation-ring cache/angle/
// center the scene's own render loop drew THIS FRAME, for EVERY tile (not just one -- D6b review
// lesson: checking only a single non-origin tile with some() is too weak), offset by that tile's own
// origin, and stamps NO OTHER ring (not even an extra draw alongside the right one).
//
// Two spies, not one: drawCachedRingContent (js/animate.js) records every call site-wide, and
// sceneSeeThroughLayer (js/see-through-hook.js) is wrapped separately to slice OUT exactly the
// drawCachedRingContent calls that happened during each INDIVIDUAL tile's own hook invocation --
// this is what actually proves "no other ring, no extra draw" per tile, rather than assuming the
// hook's calls are always exactly the trailing N entries of the flat call list (a mutation that adds
// one extra draw during hook processing would silently pass a trailing-N-slice check by shifting the
// window instead of failing it -- caught by exactly this shape while writing this test).
//
// Identifying the constellation ring by OBJECT IDENTITY (the hook's stamped cache must literally be
// a scene call's own cache argument), cross-checked against the scene's own CONSTELLATION_RING_INDEX
// (js/animate.js, deliberately declared `var` for this), so this test cannot pass by coincidence if
// the hook stamped some OTHER ring that merely happens to be internally consistent across tiles.

test("the see-through hook stamps the SAME constellation-ring cache/angle/center the scene drew this frame, for every tile, and no other ring", function () {
  var page = loadPage({ innerWidth: 1200, innerHeight: 800 });
  var calls = [];
  var originalDraw = page.sandbox.drawCachedRingContent;
  page.sandbox.drawCachedRingContent = function (context, cache, cx, cy, angle) {
    calls.push({ cache: cache, cx: cx, cy: cy, angle: angle, offset: page.currentOffset() });
    return originalDraw.apply(this, arguments);
  };
  var hookInvocations = []; // [{ startIndex, ownCalls }, ...] -- one entry per sceneSeeThroughLayer call
  var originalHook = page.sandbox.sceneSeeThroughLayer;
  page.sandbox.sceneSeeThroughLayer = function () {
    var startIndex = calls.length;
    var result = originalHook.apply(this, arguments);
    hookInvocations.push({ startIndex: startIndex, ownCalls: calls.slice(startIndex) });
    return result;
  };

  // 4 "warning" tiles (shakeMs 0): all reach "shown" together, avoiding any FAILED-shake timing
  // asymmetry between tiles, and every tile actually renders the see-through layer.
  page.sandbox.startShowing(["warning", "warning", "warning", "warning"], 2, 2, 0, 5000);
  page.sandbox.renderFrame(0);
  page.sandbox.renderFrame(750);
  assert.strictEqual(page.sandbox.kindState.warning.state, "shown");

  var rects = page.sandbox.tileRects();
  assert.strictEqual(rects.length, 4, "test setup sanity: expected a 2x2 grid of 4 tiles");

  calls.length = 0;
  hookInvocations.length = 0; // isolate the NEXT frame's calls/invocations
  page.sandbox.renderFrame(800);

  assert.strictEqual(hookInvocations.length, rects.length,
    "expected exactly one sceneSeeThroughLayer invocation per tile, saw " + hookInvocations.length);

  // Everything drawn BEFORE the first hook invocation started is the scene's own per-ring loop.
  var sceneCalls = calls.slice(0, hookInvocations[0].startIndex);

  // Identify the scene's OWN constellation-ring call this frame by object identity with each tile's
  // own stamped cache -- never by array index/position alone.
  hookInvocations.forEach(function (invocation, i) {
    var rect = rects[i];
    assert.strictEqual(invocation.ownCalls.length, 1,
      "tile " + i + ": expected the hook to draw exactly one ring, saw " + invocation.ownCalls.length);
    var call = invocation.ownCalls[0];

    var matchingSceneCalls = sceneCalls.filter(function (c) { return c.cache === call.cache; });
    assert.strictEqual(matchingSceneCalls.length, 1,
      "tile " + i + ": expected exactly one of the scene's own ring draws this frame to match the " +
      "hook's stamped cache (found " + matchingSceneCalls.length + ")");
    var sceneStamp = matchingSceneCalls[0];

    // Pin down WHICH ring that is, independent of the hook's own self-consistency: the matched call
    // must sit at the scene's own CONSTELLATION_RING_INDEX, not merely be SOME ring the hook happens
    // to agree with itself about across tiles.
    assert.strictEqual(sceneCalls[page.sandbox.CONSTELLATION_RING_INDEX], sceneStamp,
      "tile " + i + ": expected the stamped ring to be the constellation ring specifically (scene's " +
      "own CONSTELLATION_RING_INDEX), not merely some other ring the hook agrees with itself about");

    assert.strictEqual(call.cx, sceneStamp.cx, "tile " + i + ": expected the same center x");
    assert.strictEqual(call.cy, sceneStamp.cy, "tile " + i + ": expected the same center y");
    assert.strictEqual(call.angle, sceneStamp.angle, "tile " + i + ": expected the same rotation angle");
    // "+ 0" normalizes a -0/+0 mismatch (e.g. the origin tile's -rect.x is -0, a real translate(0,0)
    // never produces -0) without weakening the check: any REAL offset mismatch is still a distinct
    // nonzero number and still fails strictEqual.
    assert.strictEqual(call.offset.x + 0, -rect.x + 0,
      "tile " + i + ": expected the hook call translated by this tile's own x origin");
    assert.strictEqual(call.offset.y + 0, -rect.y + 0,
      "tile " + i + ": expected the hook call translated by this tile's own y origin");
  });
});

// ---- Case 4: render loop fault isolation (D6a/D6b precedent), one loadPage() --

test("a throwing scene layer does not stop the render loop, nor the alert overlay running the same frame", function () {
  var page = loadPage({ innerWidth: 1000, innerHeight: 500 });
  page.sandbox.startShowing(["warning"], 1, 1, 0, 5000);
  var thrown = 0;
  // Throwing on EVERY frame -- not just the first -- is what actually proves the per-stage dedup
  // (one console.error for N throws) and that the overlay keeps advancing while the scene keeps
  // failing on every single frame, matching what the assertions below claim (D6a review lesson).
  page.sandbox.drawStarfield = function () {
    thrown++;
    throw new Error("D6c-fault-isolation-scene");
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

// ---- Case 5: the alert clock itself (alertSceneMs) must not be able to freeze the loop (D6a review
// R4-alertSceneMs-outside-fault-isolation / R3-alertSceneMs-outside-try precedent) -- a throw here
// must only fail this one frame's "scene" stage, never skip scheduleFrame(renderFrame) entirely.

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
    throw new Error("D6c-alert-clock-throws");
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

// ---- Constellation table integrity (real-constellations follow-up) ------------------------------
// js/rings.js draws each edge as points[from] -> points[to], so an out-of-range index in the
// hand-written CONSTELLATION_FIGURES table would only surface as a TypeError while the ring bakes.
// This catches it here instead. `const` bindings are not sandbox properties, so read them through
// the context itself.

test("every constellation figure only joins stars it actually has, and leaves no star unjoined", function () {
  var page = loadPage({ innerWidth: 800, innerHeight: 600 });
  var figures = vm.runInContext("CONSTELLATION_FIGURES", page.sandbox);
  var pool = vm.runInContext("CONSTELLATION_POOL", page.sandbox);

  assert.ok(figures.length > 0, "expected at least one constellation figure");
  figures.forEach(function (figure) {
    var starCount = figure.stars.length;
    assert.ok(starCount >= 2, figure.name + ": expected at least two stars, got " + starCount);
    var joined = new Array(starCount).fill(false);
    figure.edges.forEach(function (edge, e) {
      assert.strictEqual(edge.length, 2, figure.name + " edge " + e + ": expected [from, to]");
      edge.forEach(function (index) {
        assert.ok(Number.isInteger(index) && index >= 0 && index < starCount,
          figure.name + " edge " + e + ": star index " + index + " is outside 0.." + (starCount - 1));
        joined[index] = true;
      });
      assert.notStrictEqual(edge[0], edge[1], figure.name + " edge " + e + ": joins a star to itself");
    });
    var loose = joined.map(function (isJoined, i) { return isJoined ? -1 : i; }).filter(function (i) { return i >= 0; });
    assert.deepStrictEqual(loose, [], figure.name + ": stars with no edge: " + loose.join(", "));
  });

  pool.forEach(function (built, i) {
    var figure = figures[i % figures.length];
    assert.strictEqual(built.segments.length, figure.edges.length,
      "ring slot " + i + " (" + figure.name + "): expected one brush segment per edge");
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
