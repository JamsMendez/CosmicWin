"use strict";

// html-wallpaper-demo D2: a committed vm-sandbox harness proving the shared alert-overlay.js's own
// contract against the REAL shipped processing-scene page -- modelled on
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
//
// D6a: js/alert-overlay.js moved out of this directory into ../shared/js/ (shared by every scene) --
// this harness resolves that sibling directory from sceneDir itself (`Wallpaper/Web/<scene>` and
// `Wallpaper/Web/shared` are always siblings, see CosmicWin.App/Wallpaper/Web/), rather than taking a
// second command-line argument.

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

const sharedDir = path.join(sceneDir, "..", "shared");

// Exact order index.html loads these in -- see CosmicWin.App/Wallpaper/Web/processing/index.html.
// Entries starting with "shared:" resolve against sharedDir instead of sceneDir.
const SCRIPT_FILES = [
  "js/config.js",
  "js/math.js",
  "js/nebula.js",
  "js/sphere.js",
  "js/scene-data.js",
  "js/sprites.js",
  "js/layers.js",
  "js/see-through-hook.js",
  "shared:js/alert-overlay.js",
  "shared:js/render-loop.js",
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
  // D2b (R4-render-loop-no-fault-isolation): records every scheduleFrame(render) call, so a test can
  // prove the loop keeps scheduling frames across a throw instead of dying on the spot.
  var requestAnimationFrameCalls = [];
  // D2b (R4-render-loop-no-fault-isolation): the sandbox has no `console` global unless provided here
  // (unlike Math/JSON/Date, `console` is a Node/browser host object, not part of the JS realm) -- a
  // throwing frame's console.error report would otherwise itself throw a ReferenceError inside the
  // sandbox. Recorded instead of forwarded to the real console, so a deliberately-thrown test error
  // does not spam this harness's own stdout.
  var consoleErrorCalls = [];
  var consoleMock = {
    error: function () { consoleErrorCalls.push(Array.prototype.slice.call(arguments)); },
    log: function () { /* no-op: unused by the scene */ },
    warn: function () { /* no-op: unused by the scene */ },
  };
  // D2b (R3-host-message-path-unproved): used to be a no-op, so handleHostMessage (js/alert-overlay.js)
  // never ran in any test -- every "show"/"hide" case above only proved startShowing()/hide() work when
  // called DIRECTLY, never that a real WebView2 message reaches them. Registered listeners are recorded
  // here so a test can dispatch a message shaped exactly like the ones
  // CosmicWin.App/Alerts/WebViewAlertLayerController.cs posts (see dispatchHostMessage below).
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
    fillStyleHistory: ctx2d.__fillStyleHistory,
    requestAnimationFrameCalls: requestAnimationFrameCalls,
    consoleErrorCalls: consoleErrorCalls,
    // config.js declares `ctx`/`canvas` with `const`, so -- unlike the `var`/`function` top-level
    // declarations the rest of this harness already reaches via page.sandbox.* -- they never become
    // properties of the vm context's global object (a top-level `const` in a classic, non-module
    // script stays in its own lexical environment record, same as in a real browser <script> tag).
    // Returned directly from this closure instead, since it already holds the exact same references.
    ctx: ctx2d,
    canvas: sceneCanvas,
    messageListenerCount: messageListeners.length,
    // Dispatches a message to every registered "message" listener, exactly like a real WebView2
    // CoreWebView2.WebMessageReceived -> window.chrome.webview "message" event delivers one: `data` is
    // already the deserialized object (WebView2 parses PostWebMessageAsJson's JSON before the page ever
    // sees it), so callers pass the same shape WebViewAlertLayerController.cs posts, not a JSON string.
    dispatchHostMessage: function (data) {
      messageListeners.forEach(function (listener) { listener({ data: data }); });
    },
  };
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

// ---- Case 6: render loop fault isolation (D2b, R4-render-loop-no-fault-isolation) -----------------
// Before this fix, scheduleFrame(render) was render()'s LAST statement -- a throwing frame never
// reached it, and the wallpaper's loop (this page IS the wallpaper) stopped for good. These monkey-
// patch one scene layer to throw and prove the loop, and the alert overlay, both survive it.

test("a throwing scene layer does not stop the render loop from scheduling the next frame", function () {
  var page = loadPage({ innerWidth: 800, innerHeight: 600 });
  var original = page.sandbox.drawSoftOvalFields;
  var thrown = 0;
  page.sandbox.drawSoftOvalFields = function () {
    thrown++;
    if (thrown === 1) throw new Error("D2b-fault-isolation-scene");
    return original.apply(this, arguments);
  };

  var before = page.requestAnimationFrameCalls.length;
  page.sandbox.render(0); // this frame throws inside drawSoftOvalFields
  assert.strictEqual(page.requestAnimationFrameCalls.length, before + 1,
    "expected scheduleFrame(render) to still run once even though this frame threw");

  page.sandbox.render(16); // the loop must keep going, not just survive the one throwing frame
  assert.strictEqual(page.requestAnimationFrameCalls.length, before + 2,
    "expected the loop to keep scheduling frames after recovering from a throw");
});

test("a throwing scene layer does not stop the alert overlay from running the same frame", function () {
  var page = loadPage({ innerWidth: 1000, innerHeight: 500 });
  page.sandbox.startShowing(["warning"], 1, 1, 0, 5000);
  page.sandbox.drawSoftOvalFields = function () { throw new Error("D2b-fault-isolation-scene"); };

  // warning has shakeMs 0 and FAILURE_REVEAL_MS 700 -- shown well before 950ms.
  page.sandbox.render(0);
  page.sandbox.render(300);
  page.sandbox.render(950);
  assert.strictEqual(page.sandbox.kindState.warning.state, "shown",
    "expected the alert overlay to keep advancing even while the scene layer above it keeps throwing");
});

test("a throwing frame is reported once via console.error, not flooded on every repeat", function () {
  var page = loadPage({ innerWidth: 800, innerHeight: 600 });
  page.sandbox.drawSoftOvalFields = function () { throw new Error("D2b-fault-isolation-repeat"); };

  page.sandbox.render(0);
  page.sandbox.render(16);
  page.sandbox.render(32);
  assert.strictEqual(page.consoleErrorCalls.length, 1,
    "expected the SAME repeating error to be reported once, not once per frame");
});

// D2b hardening review (R4-error-dedup-single-slot-flood / R3-error-dedup-single-slot /
// R2-render-error-dedup-comment-misleading): reportRenderError used to remember only ONE last-reported
// message, shared by BOTH render() try/catch blocks (scene and alert-overlay, see render() below). If
// the two stages fail on every frame, the remembered message alternates scene/overlay/scene/overlay --
// so neither throw ever matches what was remembered a moment ago, and console.error fires for EVERY
// frame of EACH stage: exactly the 60x/sec flood this guard exists to stop, even though the comment
// claimed "once per DISTINCT message". Deduping PER STAGE (one remembered message per render() try/
// catch) fixes this: each stage's own repeats are compared only against that SAME stage's last message.

test("a scene error and an overlay error that both fire on every frame are each reported once, not flip-flopped into a flood", function () {
  var page = loadPage({ innerWidth: 800, innerHeight: 600 });
  page.sandbox.drawSoftOvalFields = function () { throw new Error("D2b-dedup-scene"); };
  page.sandbox.renderAlertOverlay = function () { throw new Error("D2b-dedup-overlay"); };

  page.sandbox.render(0);
  page.sandbox.render(16);
  page.sandbox.render(32);
  page.sandbox.render(48);

  assert.strictEqual(page.consoleErrorCalls.length, 2,
    "expected exactly one console.error for the repeating scene error and one for the repeating " +
    "overlay error (2 total across 4 frames), saw " + page.consoleErrorCalls.length);
});

// Only the overlay throws (scene keeps succeeding every frame): proves the overlay's OWN catch path
// (D2b, R3-overlay-catch-path-unproved) -- every fault-isolation test above only ever made the SCENE
// throw, leaving renderAlertOverlay's own try/catch (report, resetCanvasStateForFrame, scheduleFrame
// still running, the scene still drawing) unproved.

test("only the alert overlay throwing is reported once, does not stop the loop or the scene, and does not leak canvas state", function () {
  var page = loadPage({ innerWidth: 800, innerHeight: 600 });
  var sceneDrawCalls = 0;
  var originalDrawSoftOvalFields = page.sandbox.drawSoftOvalFields;
  page.sandbox.drawSoftOvalFields = function () {
    sceneDrawCalls++;
    return originalDrawSoftOvalFields.apply(this, arguments);
  };
  page.sandbox.renderAlertOverlay = function () { throw new Error("D2b-overlay-only-throw"); };
  page.ctx.globalAlpha = 0.33;
  page.ctx.globalCompositeOperation = "difference";
  page.canvas.style.transform = "translate(999px, 999px)"; // stale shake, as if mid-throw

  var before = page.requestAnimationFrameCalls.length;
  page.sandbox.render(0);
  page.sandbox.render(16);
  page.sandbox.render(32);

  assert.strictEqual(page.requestAnimationFrameCalls.length, before + 3,
    "expected scheduleFrame(render) to keep running every frame while only the overlay throws");
  assert.strictEqual(sceneDrawCalls, 3,
    "expected the scene to keep drawing every frame while only the overlay throws");
  assert.strictEqual(page.consoleErrorCalls.length, 1,
    "expected the SAME repeating overlay error to be reported once, not once per frame");
  assert.strictEqual(page.ctx.globalAlpha, 1, "expected globalAlpha reset after the overlay throws");
  assert.strictEqual(page.ctx.globalCompositeOperation, "source-over",
    "expected the composite mode reset after the overlay throws");
  assert.strictEqual(page.canvas.style.transform, "",
    "expected the stale shake transform cleared after the overlay throws");
});

test("a throwing scene layer does not leak canvas state (alpha/composite/shake) into later frames", function () {
  var page = loadPage({ innerWidth: 800, innerHeight: 600 });
  page.ctx.globalAlpha = 0.33;
  page.ctx.globalCompositeOperation = "difference";
  page.canvas.style.transform = "translate(999px, 999px)"; // stale shake, as if mid-throw
  page.sandbox.drawSoftOvalFields = function () { throw new Error("D2b-fault-isolation-leak"); };

  page.sandbox.render(0);

  assert.strictEqual(page.ctx.globalAlpha, 1, "expected globalAlpha reset after a throwing frame");
  assert.strictEqual(page.ctx.globalCompositeOperation, "source-over",
    "expected the composite mode reset after a throwing frame");
  assert.strictEqual(page.canvas.style.transform, "",
    "expected the stale shake transform cleared after a throwing frame");
});

// ---- Case 7: the REAL WebView2 host message path (D2b, R3-host-message-path-unproved) -------------
// Case 2 above only proves startShowing()/hide() work when called DIRECTLY -- the mock's
// chrome.webview.addEventListener used to be a no-op, so handleHostMessage (js/alert-overlay.js) never
// ran in any test, leaving the show/hide message contract D3 relies on unproved. These dispatch EXACTLY
// the JSON shapes CosmicWin.App/Alerts/WebViewAlertLayerController.cs posts: PostShow's
// {type:"show",tiles,columns,rows,gap,workArea:{left,top,width,height},duration} (~line 147-150) and
// End's {type:"hide"} (~line 127). No "kind"/duration legacy message shape is exercised here: the
// controller itself never sends it any more (see its own CreateAsync remarks, "No kind/duration hash
// any more (T9b)") -- the page's fallback for that shape (handleHostMessage's else branch) exists only
// for a manual hand-posted check, same as alert-layer.js's own back-compat branch.

test("a real 'show' host message reaches shown per kind, and a real 'hide' message stops it", function () {
  var page = loadPage({ innerWidth: 1000, innerHeight: 500, withWebview: true });
  assert.strictEqual(page.messageListenerCount, 1,
    "expected handleHostMessage to register exactly one 'message' listener");

  page.dispatchHostMessage({
    type: "show",
    tiles: ["failed", "warning"],
    columns: 2,
    rows: 1,
    gap: 0,
    workArea: { left: 0, top: 0, width: 0, height: 0 },
    duration: 1000,
  });

  page.sandbox.render(0);
  page.sandbox.render(300);
  page.sandbox.render(950); // failed: 230+700=930 -> shown by 950. warning: shown by 700.
  assert.strictEqual(page.sandbox.kindState.failed.state, "shown");
  assert.strictEqual(page.sandbox.kindState.warning.state, "shown");

  page.dispatchHostMessage({ type: "hide" });
  assert.strictEqual(page.sandbox.animating, false, "expected the real 'hide' message to stop the overlay");
});

test("a malformed host message is ignored without throwing", function () {
  var page = loadPage({ innerWidth: 800, innerHeight: 600, withWebview: true });
  page.sandbox.startShowing(["warning"], 1, 1, 0, 5000);
  page.sandbox.render(0);

  assert.doesNotThrow(function () {
    page.dispatchHostMessage(null);
    page.dispatchHostMessage("not-an-object");
    page.dispatchHostMessage({ type: "unrecognized-type" });
  });

  // None of the above malformed messages should have stopped or reset the still-running overlay.
  assert.strictEqual(page.sandbox.animating, true);
});

// ---- Case 8: shared render-loop fps cap (D6d, html-wallpaper-demo) --------------------------------
// shared/js/render-loop.js's scheduleFrame is the ONE place every scene schedules its next animation
// frame (see that file's own header remarks). These drive the REAL requestAnimationFrame mock
// directly -- page.requestAnimationFrameCalls records each pushed callback (see loadPage's own
// windowMock.requestAnimationFrame), so a test can fire it with a chosen timestamp exactly like the
// browser would, instead of calling render(ms) directly (which bypasses scheduleFrame entirely, as
// every earlier case in this file does on purpose -- see Case 2/3/6/7).

test("readWallpaperFpsFromUrl parses fps=30 as 30", function () {
  var page = loadPage({ search: "?fps=30" });
  assert.strictEqual(page.sandbox.readWallpaperFpsFromUrl(), 30);
});

test("readWallpaperFpsFromUrl parses fps=60 as 60", function () {
  var page = loadPage({ search: "?fps=60" });
  assert.strictEqual(page.sandbox.readWallpaperFpsFromUrl(), 60);
});

test("readWallpaperFpsFromUrl treats an unrecognized fps value as 60", function () {
  var page = loadPage({ search: "?fps=45" });
  assert.strictEqual(page.sandbox.readWallpaperFpsFromUrl(), 60);
});

test("readWallpaperFpsFromUrl treats a missing fps param as 60", function () {
  var page = loadPage({});
  assert.strictEqual(page.sandbox.readWallpaperFpsFromUrl(), 60);
});

test("shared render loop: at the default 60fps, every scheduled animation frame draws", function () {
  var page = loadPage({});
  var drawnCount = 0;
  function loop() { drawnCount++; page.sandbox.scheduleFrame(loop); }
  page.sandbox.scheduleFrame(loop);

  var frameTimesMs = [0, 16.7, 33.3, 50.0, 66.7];
  frameTimesMs.forEach(function (ms) {
    var wrapper = page.requestAnimationFrameCalls[page.requestAnimationFrameCalls.length - 1];
    wrapper(ms);
  });

  assert.strictEqual(drawnCount, frameTimesMs.length,
    "expected every rAF callback to draw at the default 60fps, same as before this cap existed");
});

test("shared render loop: fps=60 explicitly in the URL behaves the same as the default", function () {
  var page = loadPage({ search: "?fps=60" });
  var drawnCount = 0;
  function loop() { drawnCount++; page.sandbox.scheduleFrame(loop); }
  page.sandbox.scheduleFrame(loop);

  [0, 16.7, 33.3].forEach(function (ms) {
    var wrapper = page.requestAnimationFrameCalls[page.requestAnimationFrameCalls.length - 1];
    wrapper(ms);
  });

  assert.strictEqual(drawnCount, 3);
});

test("shared render loop: at 30fps, rAF callbacks spaced 16.7ms apart draw about every other frame", function () {
  var page = loadPage({ search: "?fps=30" });
  var drawnTimesMs = [];
  function loop(ms) { drawnTimesMs.push(ms); page.sandbox.scheduleFrame(loop); }
  page.sandbox.scheduleFrame(loop);

  for (var i = 0; i < 10; i++) {
    var wrapper = page.requestAnimationFrameCalls[page.requestAnimationFrameCalls.length - 1];
    wrapper(i * 16.7);
  }

  assert.strictEqual(drawnTimesMs.length, 5,
    "expected about half of 10 60fps-spaced rAF callbacks to draw under a 30fps cap, drew at: " + JSON.stringify(drawnTimesMs));
  for (var j = 1; j < drawnTimesMs.length; j++) {
    assert.ok(drawnTimesMs[j] - drawnTimesMs[j - 1] >= 30,
      "expected consecutive DRAWN frames to be at least ~30ms apart (30fps), got " + JSON.stringify(drawnTimesMs));
  }
});

test("shared render loop: an unrecognized fps value in the URL keeps every frame drawing (default 60fps)", function () {
  var page = loadPage({ search: "?fps=45" });
  var drawnCount = 0;
  function loop() { drawnCount++; page.sandbox.scheduleFrame(loop); }
  page.sandbox.scheduleFrame(loop);

  [0, 16.7, 33.3].forEach(function (ms) {
    var wrapper = page.requestAnimationFrameCalls[page.requestAnimationFrameCalls.length - 1];
    wrapper(ms);
  });

  assert.strictEqual(drawnCount, 3);
});

test("shared render loop: a skipped frame still reschedules the next animation frame, without invoking the callback", function () {
  var page = loadPage({ search: "?fps=30" });
  var calls = 0;
  function loop() { calls++; page.sandbox.scheduleFrame(loop); }
  page.sandbox.scheduleFrame(loop);

  var before = page.requestAnimationFrameCalls.length;
  var firstWrapper = page.requestAnimationFrameCalls[page.requestAnimationFrameCalls.length - 1];
  firstWrapper(0); // first frame always draws (nothing drawn yet to compare against)
  assert.strictEqual(calls, 1);
  assert.strictEqual(page.requestAnimationFrameCalls.length, before + 1,
    "expected the drawn frame's own reschedule to push exactly one new rAF call");

  var secondWrapper = page.requestAnimationFrameCalls[page.requestAnimationFrameCalls.length - 1];
  secondWrapper(16.7); // well under the ~33.3ms 30fps interval -- must be skipped
  assert.strictEqual(calls, 1, "expected the skipped frame to NOT invoke the callback");
  assert.strictEqual(page.requestAnimationFrameCalls.length, before + 2,
    "expected the skipped frame to still reschedule the next animation frame on its own");
});

test("an alert's 'done' still fires on its own duration under a 30fps cap, not distorted by skipped frames", function () {
  var page = loadPage({ innerWidth: 1000, innerHeight: 500, withWebview: true, search: "?fps=30" });
  page.sandbox.startShowing(["warning"], 1, 1, 0, 1000);

  var doneFiredAtMs = null;
  for (var i = 0; i <= 70 && doneFiredAtMs === null; i++) {
    var ms = i * 16.7;
    var wrapper = page.requestAnimationFrameCalls[page.requestAnimationFrameCalls.length - 1];
    wrapper(ms);
    if (page.postedMessages.indexOf("done") !== -1) doneFiredAtMs = ms;
  }

  assert.strictEqual(page.sandbox.kindState.warning.state, "shown");
  assert.ok(doneFiredAtMs !== null, "expected 'done' to fire eventually under the 30fps cap");
  assert.ok(doneFiredAtMs >= 1000,
    "expected 'done' to fire only once REAL elapsed time reached the alert's own 1000ms duration, got " + doneFiredAtMs);
  assert.ok(doneFiredAtMs < 1050,
    "expected 'done' close to the actual duration boundary, not badly delayed by the 30fps cap, got " + doneFiredAtMs);
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
