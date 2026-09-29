"use strict";

// mini-scene-window T2: the `?variant=mini` checks shared by the four scene harnesses
// (idle/explorer/processing/raphael-scene.tests.js), so the contract is asserted once, the same way,
// for every scene instead of four pasted copies that could drift.
//
// Contract (feature doc odd/tasks/mini-scene-window.md): the mini variant is the small always-on-top
// corner window -- the page and canvas are transparent (clearRect, never an opaque full-canvas fill),
// only the scene's kept layer functions run, there is no #nebula, and the alert overlay STILL renders
// (scope change 2026-09-29: the maintainer wants to see alerts in the mini window). The default
// (no-param) rendering must keep drawing every layer.

const vm = require("vm");
const fs = require("fs");
const path = require("path");
const assert = require("assert");
const { URLSearchParams } = require("url");

// (a) variant parse -----------------------------------------------------------------------------
// Runs ONLY shared/js/render-loop.js in a tiny sandbox (parseParams is what alert-overlay.js would
// provide), so the parse cases do not each pay a full scene load (idle/explorer bake an Earth texture).
function readVariant(sharedDir, location) {
  var source = fs.readFileSync(path.join(sharedDir, "js", "render-loop.js"), "utf8");
  var sandbox = {
    window: { requestAnimationFrame: function () {} },
    document: {}, // no documentElement: the class toggle must be guarded
    location: location,
    URLSearchParams: URLSearchParams,
    parseParams: function () {
      var raw = (location.hash || location.search || "").replace(/^[#?]/, "");
      return new URLSearchParams(raw);
    },
  };
  vm.createContext(sandbox);
  vm.runInContext(source, sandbox, { filename: "render-loop.js" });
  return { variant: sandbox.sceneVariant, isMini: sandbox.isMiniVariant, read: sandbox.readSceneVariantFromUrl };
}

function checkVariantParse(sharedDir) {
  var cases = [
    [{ search: "", hash: "" }, "full", "no params"],
    [{ search: "?variant=mini", hash: "" }, "mini", "?variant=mini"],
    [{ search: "", hash: "#variant=mini" }, "mini", "#variant=mini"],
    [{ search: "?variant=mini", hash: "#tiles=failed&duration=5000" }, "mini", "?variant=mini with an alert hash"],
    [{ search: "?fps=30&variant=mini", hash: "" }, "mini", "variant next to fps"],
    [{ search: "?variant=full", hash: "" }, "full", "?variant=full"],
    [{ search: "?variant=garbage", hash: "" }, "full", "garbage value"],
    [{ search: "?variant=MINI", hash: "" }, "full", "case-sensitive: MINI is not mini"],
    [{ search: "?variant=", hash: "" }, "full", "empty value"],
  ];
  cases.forEach(function (c) {
    var result = readVariant(sharedDir, c[0]);
    assert.strictEqual(typeof result.read, "function", "expected readSceneVariantFromUrl() to exist");
    assert.strictEqual(result.variant, c[1], c[2] + ": expected variant " + c[1] + ", got " + result.variant);
    assert.strictEqual(result.isMini, c[1] === "mini", c[2] + ": isMiniVariant mismatch");
  });
}

// Spies -----------------------------------------------------------------------------------------
function coversCanvas(args, width, height) {
  var x = args[0], y = args[1], w = args[2], h = args[3];
  return x <= 0 && y <= 0 && x + w >= width && y + h >= height;
}

// Wraps every named page-global function with a call counter (function declarations are properties
// of the vm context's global, so reassigning them reroutes the scene's own calls).
function spyOnFunctions(page, names) {
  var counts = {};
  names.forEach(function (name) {
    counts[name] = 0;
    var original = page.sandbox[name];
    if (typeof original !== "function") return; // reported as "missing" by the assertions below
    page.sandbox[name] = function () {
      counts[name]++;
      return original.apply(this, arguments);
    };
  });
  return counts;
}

// Records every fillRect on the shared 2D context mock together with the composite operation and
// fill style live at that moment. The mock stores plain property sets, so page.sandbox.ctx.fillRect
// can be replaced with a recorder.
function spyOnFillRect(page) {
  var ctx = pageContext(page);
  var calls = [];
  ctx.fillRect = function () {
    calls.push({
      args: Array.prototype.slice.call(arguments),
      // The mock returns a no-op function for a property nobody has set yet, so only a string counts.
      op: typeof ctx.globalCompositeOperation === "string" ? ctx.globalCompositeOperation : "source-over",
      style: ctx.fillStyle,
    });
  };
  return calls;
}

// `const ctx` is not a property of the vm context (top-level const), so read it through the context.
function pageContext(page) {
  return vm.runInContext("ctx", page.sandbox);
}

function drive(page, frameFunctionName, times) {
  times.forEach(function (ms) { page.sandbox[frameFunctionName](ms); });
}

// (b) mini: only the kept layers, transparent canvas, overlay still rendered --------------------
// options: { loadPage, frameFunction, keep[], drop[], width, height, fit(page) optional }
function checkMiniLayers(options) {
  var size = { innerWidth: options.width || 288, innerHeight: options.height || 288 };
  var page = options.loadPage({ innerWidth: size.innerWidth, innerHeight: size.innerHeight, search: "?variant=mini" });
  assert.strictEqual(page.sandbox.isMiniVariant, true, "expected ?variant=mini to select the mini variant");

  // Warm-up frame, unspied: it builds the scene's offscreen caches (ring/lighting/sprites). The mock
  // shares ONE 2D context between the canvas and every offscreen canvas, so a cache bake's own
  // fillRect would otherwise look like a full-canvas fill on the visible canvas.
  drive(page, options.frameFunction, [0]);

  var watched = options.keep.concat(options.drop, ["renderAlertOverlay", "renderNebula"]);
  var counts = spyOnFunctions(page, watched);
  var fills = spyOnFillRect(page);
  var clears = [];
  pageContext(page).clearRect = function () { clears.push(Array.prototype.slice.call(arguments)); };

  drive(page, options.frameFunction, [100, 200]);

  assert.deepStrictEqual(page.consoleErrorCalls, [], "expected no render errors in the mini frame");
  options.keep.forEach(function (name) {
    assert.strictEqual(typeof page.sandbox[name], "function", "expected the kept layer " + name + " to exist");
    assert.ok(counts[name] >= 1, "mini must still draw " + name + " (ran " + counts[name] + " times)");
  });
  options.drop.forEach(function (name) {
    assert.strictEqual(counts[name] || 0, 0, "mini must NOT run " + name + " (ran " + counts[name] + " times)");
  });
  // The nebula is a mini layer only where the scene says so (processing: green, edge-faded).
  if (options.keepNebula) {
    assert.ok(counts.renderNebula >= 1, "mini must still call renderNebula (ran " + counts.renderNebula + " times)");
  } else {
    assert.strictEqual(counts.renderNebula || 0, 0, "mini must NOT run renderNebula (ran " + counts.renderNebula + " times)");
  }
  assert.ok(counts.renderAlertOverlay >= 1, "mini must still call renderAlertOverlay (ran " + counts.renderAlertOverlay + " times)");

  assert.ok(clears.some(function (args) { return coversCanvas(args, size.innerWidth, size.innerHeight); }),
    "expected the mini frame to clearRect the whole canvas");
  // No opaque background: a full-canvas fillRect may only be a 'source-atop' tint, which cannot add
  // pixels where the canvas is transparent (source-over/color/screen/... would paint the window).
  var opaque = fills.filter(function (call) {
    return coversCanvas(call.args, size.innerWidth, size.innerHeight) && call.op !== "source-atop";
  });
  assert.strictEqual(opaque.length, 0,
    "mini must not fill the whole canvas (found " + JSON.stringify(opaque.map(function (c) { return { op: c.op, style: String(c.style) }; })) + ")");

  if (options.fit) options.fit(page);
  return page;
}

// (c) full: the default page still draws every layer, and still paints its own background ---------
// options: { loadPage, frameFunction, keep[], drop[], hasBackgroundFill }
function checkFullLayers(options) {
  var page = options.loadPage({ innerWidth: 1000, innerHeight: 500 });
  assert.strictEqual(page.sandbox.isMiniVariant, false, "expected the default page to be the full variant");

  // Warm-up frame, unspied: it builds the scene's offscreen caches (ring/lighting/sprites). The mock
  // shares ONE 2D context between the canvas and every offscreen canvas, so a cache bake's own
  // fillRect would otherwise look like a full-canvas fill on the visible canvas.
  drive(page, options.frameFunction, [0]);

  var watched = options.keep.concat(options.drop, ["renderAlertOverlay", "renderNebula"]);
  var counts = spyOnFunctions(page, watched);
  var fills = spyOnFillRect(page);
  drive(page, options.frameFunction, [100, 200]);

  assert.deepStrictEqual(page.consoleErrorCalls, [], "expected no render errors in the full frame");
  options.keep.concat(options.drop).forEach(function (name) {
    assert.strictEqual(typeof page.sandbox[name], "function", "expected the layer " + name + " to exist");
    assert.ok(counts[name] >= 1, "the full scene must still draw " + name + " (ran " + counts[name] + " times)");
  });
  assert.ok(counts.renderAlertOverlay >= 1, "the full scene must still call renderAlertOverlay");
  if (typeof page.sandbox.renderNebula === "function") {
    assert.ok(counts.renderNebula >= 1, "the full scene must still render the nebula");
  }
  if (options.hasBackgroundFill) {
    // (Matched by fill style, not composite op: the mock does not restore canvas state on restore().)
    var background = vm.runInContext("BACKGROUND_COLOR", page.sandbox);
    assert.ok(fills.some(function (call) { return coversCanvas(call.args, 1000, 500) && call.style === background; }),
      "the full scene must still paint its opaque background (" + background + ")");
  }
}

// Stylesheet: the mini class makes the page transparent (and hides #nebula where the scene has one).
// nebulaMode: "hidden" (display: none), "transparent" (background: transparent) or absent (no nebula).
function checkMiniStylesheet(sceneDir, nebulaMode) {
  var css = fs.readFileSync(path.join(sceneDir, "styles.css"), "utf8");
  var block = /html\.scene-mini,\s*html\.scene-mini body,\s*html\.scene-mini #scene\s*\{([^}]*)\}/.exec(css);
  assert.ok(block, "expected an html.scene-mini rule covering html, body and #scene");
  assert.ok(/background:\s*transparent/.test(block[1]), "expected the mini rule to set background: transparent");
  if (nebulaMode === "hidden") {
    assert.ok(/html\.scene-mini #nebula\s*\{[^}]*display:\s*none/.test(css), "expected html.scene-mini #nebula to be display: none");
  } else if (nebulaMode === "transparent") {
    assert.ok(/html\.scene-mini #nebula\s*\{[^}]*background:\s*transparent/.test(css), "expected html.scene-mini #nebula to be background: transparent");
    assert.ok(!/html\.scene-mini #nebula\s*\{[^}]*display:\s*none/.test(css), "expected the mini #nebula to stay displayed");
  }
}

module.exports = {
  checkVariantParse: checkVariantParse,
  checkMiniLayers: checkMiniLayers,
  checkFullLayers: checkFullLayers,
  checkMiniStylesheet: checkMiniStylesheet,
  coversCanvas: coversCanvas,
};
