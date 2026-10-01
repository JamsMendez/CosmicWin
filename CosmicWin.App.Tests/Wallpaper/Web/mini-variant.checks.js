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
function spyOnFunctions(page, names, log) {
  var counts = {};
  names.forEach(function (name) {
    counts[name] = 0;
    var original = page.sandbox[name];
    if (typeof original !== "function") return; // reported as "missing" by the assertions below
    page.sandbox[name] = function () {
      counts[name]++;
      if (log) log.push(name);
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

  var miniOnly = options.miniOnly || [];
  var watched = options.keep.concat(options.drop, miniOnly, ["renderAlertOverlay", "renderNebula", "applyMiniEdgeFade"]);
  var log = [];
  var counts = spyOnFunctions(page, watched, log);
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
  // mini-only layers run in mini (and never in the full variant, see checkFullLayers)
  miniOnly.forEach(function (name) {
    assert.strictEqual(typeof page.sandbox[name], "function", "expected the mini-only layer " + name + " to exist");
    assert.ok(counts[name] >= 1, "mini must run " + name + " (ran " + counts[name] + " times)");
  });
  // The nebula is a mini layer only where the scene says so (processing: green, edge-faded).
  if (options.keepNebula) {
    assert.ok(counts.renderNebula >= 1, "mini must still call renderNebula (ran " + counts.renderNebula + " times)");
  } else {
    assert.strictEqual(counts.renderNebula || 0, 0, "mini must NOT run renderNebula (ran " + counts.renderNebula + " times)");
  }
  assert.ok(counts.renderAlertOverlay >= 1, "mini must still call renderAlertOverlay (ran " + counts.renderAlertOverlay + " times)");

  // The edge fade: once per frame, after every layer and before the (unmasked) alert overlay.
  var frame = [];
  var frames = 0;
  log.forEach(function (name) {
    if (name === "renderAlertOverlay") {
      frames++;
      assert.strictEqual(frame.filter(function (n) { return n === "applyMiniEdgeFade"; }).length, 1,
        "mini must apply the edge fade exactly once per frame, got: " + frame.join(","));
      assert.strictEqual(frame[frame.length - 1], "applyMiniEdgeFade",
        "the edge fade must come after every scene layer and right before renderAlertOverlay, got: " + frame.join(","));
      frame = [];
    } else if (name !== "renderNebula") {
      frame.push(name);
    }
  });
  assert.ok(frames >= 2, "expected two overlay calls, saw " + frames);
  (options.order || []).forEach(function (pair) {
    var seq = log.slice(0, log.indexOf("renderAlertOverlay"));
    assert.ok(seq.indexOf(pair[0]) >= 0 && seq.indexOf(pair[0]) < seq.indexOf(pair[1]),
      "expected " + pair[0] + " before " + pair[1] + " in the mini frame, got: " + seq.join(","));
  });
  checkEdgeFadeMask(page);

  assert.ok(clears.some(function (args) { return coversCanvas(args, size.innerWidth, size.innerHeight); }),
    "expected the mini frame to clearRect the whole canvas");
  // No opaque background: a full-canvas fillRect may only be a 'source-atop' tint or the 'destination-in' edge fade, neither of which can add
  // pixels where the canvas is transparent (source-over/color/screen/... would paint the window).
  var opaque = fills.filter(function (call) {
    return coversCanvas(call.args, size.innerWidth, size.innerHeight) && call.op !== "source-atop" && call.op !== "destination-in";
  });
  assert.strictEqual(opaque.length, 0,
    "mini must not fill the whole canvas (found " + JSON.stringify(opaque.map(function (c) { return { op: c.op, style: String(c.style) }; })) + ")");

  if (options.fit) options.fit(page);
  return page;
}

// The mask itself: a destination-in fill of the whole canvas with a radial gradient centered on the
// window, alpha 1 out to MINI_EDGE_FADE_INNER of the short side, easing to 0 at MINI_EDGE_FADE_OUTER,
// with the context state saved and restored around it.
function checkEdgeFadeMask(page) {
  var side = 288;
  var gradients = [];
  var fills = [];
  var depth = 0;
  // A plain recording context (the shared 2D mock special-cases createRadialGradient, so it cannot record it).
  var ctx = {
    globalCompositeOperation: "source-over",
    fillStyle: null,
    createRadialGradient: function () {
      var g = { args: Array.prototype.slice.call(arguments), stops: [], addColorStop: function (o, c) { g.stops.push([o, c]); } };
      gradients.push(g);
      return g;
    },
    save: function () { depth++; },
    restore: function () { depth--; },
    fillRect: function () { fills.push({ args: Array.prototype.slice.call(arguments), op: ctx.globalCompositeOperation, style: ctx.fillStyle }); },
  };
  page.sandbox.applyMiniEdgeFade(ctx, side, side);

  assert.strictEqual(depth, 0, "applyMiniEdgeFade must balance save()/restore()");
  assert.strictEqual(gradients.length, 1, "expected one radial gradient");
  assert.strictEqual(fills.length, 1, "expected one fillRect");
  assert.strictEqual(fills[0].op, "destination-in", "the mask must composite with destination-in");
  assert.ok(coversCanvas(fills[0].args, side, side), "the mask must cover the whole canvas");
  assert.strictEqual(fills[0].style, gradients[0], "the mask must be filled with the gradient");
  var g = gradients[0].args; // x0, y0, r0, x1, y1, r1
  assert.ok(g[0] === 144 && g[1] === 144 && g[3] === 144 && g[4] === 144, "the fade must be centered on the window");
  var inner = page.sandbox.MINI_EDGE_FADE_INNER, outer = page.sandbox.MINI_EDGE_FADE_OUTER;
  assert.ok(inner >= 0.38 && inner <= 0.42 && outer >= 0.46 && outer <= 0.49 && outer > inner, "unexpected fade band " + inner + ".." + outer);
  assert.ok(Math.abs(g[5] - outer * side) < 1e-6, "the gradient must end at the outer radius");
  var alphaOf = function (c) { return Number(/,\s*([\d.]+)\)$/.exec(c)[1]); };
  var stops = gradients[0].stops;
  assert.strictEqual(alphaOf(stops[0][1]), 1, "opaque at the center");
  var lastFull = stops.filter(function (s) { return alphaOf(s[1]) === 1; }).pop();
  assert.ok(Math.abs(lastFull[0] * outer - inner) < 1e-6, "alpha 1 must hold out to the inner radius");
  assert.strictEqual(alphaOf(stops[stops.length - 1][1]), 0, "transparent at the outer radius");
  assert.strictEqual(stops[stops.length - 1][0], 1);
  var previous = 1;
  stops.forEach(function (s) { var a = alphaOf(s[1]); assert.ok(a <= previous + 1e-9, "alpha must not increase outward"); previous = a; });
}

// mini-scene-window T2j: processing and raphael draw ONE occluding dark base disc beneath the whole mini scene,
// so text and icons of windows behind the topmost mini window cannot read through the structure. Shared
// function in shared/js/render-loop.js: destination-over (beneath everything already drawn), a radial
// gradient solid (alpha in [0.85, 0.95]) out to solidRadius that falls to 0 at falloffRadius, no hard edge.
function checkMiniSceneBaseFunction(page) {
  var gradients = [];
  var fills = [];
  var depth = 0;
  var ctx = {
    globalCompositeOperation: "source-over",
    fillStyle: null,
    createRadialGradient: function () {
      var g = { args: Array.prototype.slice.call(arguments), stops: [], addColorStop: function (o, c) { g.stops.push([o, c]); } };
      gradients.push(g);
      return g;
    },
    save: function () { depth++; },
    restore: function () { depth--; },
    beginPath: function () {},
    arc: function (x, y, r) { ctx.lastArc = [x, y, r]; },
    fill: function () { fills.push({ op: ctx.globalCompositeOperation, style: ctx.fillStyle, arc: ctx.lastArc }); },
  };
  page.sandbox.drawMiniSceneBase(ctx, 100, 120, 80, 96);

  assert.strictEqual(depth, 0, "drawMiniSceneBase must balance save()/restore()");
  assert.strictEqual(gradients.length, 1);
  assert.strictEqual(fills.length, 1);
  assert.strictEqual(fills[0].op, "destination-over", "the base must be drawn beneath the frame (destination-over)");
  assert.strictEqual(fills[0].style, gradients[0]);
  var g = gradients[0].args; // x0, y0, r0, x1, y1, r1
  assert.ok(g[0] === 100 && g[1] === 120 && g[3] === 100 && g[4] === 120, "the base must be centered on the scene");
  assert.strictEqual(g[5], 96, "the gradient ends at the falloff radius");
  assert.ok(fills[0].arc[0] === 100 && fills[0].arc[1] === 120 && fills[0].arc[2] === 96, "the filled disc must reach the falloff radius");
  var alphaOf = function (c) { return Number(/,\s*([\d.]+)\)$/.exec(c)[1]); };
  var stops = gradients[0].stops;
  var solid = stops.filter(function (s) { return alphaOf(s[1]) > 0.5; });
  assert.ok(solid.length >= 2, "expected a solid core");
  solid.forEach(function (s) {
    var a = alphaOf(s[1]);
    assert.ok(a >= 0.85 && a <= 0.95, "the base alpha must hide what is behind it (0.85-0.95), got " + a);
  });
  assert.ok(Math.abs(solid[solid.length - 1][0] - 80 / 96) < 1e-9, "the base must stay solid out to the solid radius");
  assert.strictEqual(alphaOf(stops[stops.length - 1][1]), 0, "the base must fall to 0 at the falloff radius (no hard edge)");
  assert.strictEqual(stops[stops.length - 1][0], 1);
}

// Runs one mini frame and returns the (solidRadius, falloffRadius, cx, cy) the scene passed to drawMiniSceneBase.
function captureMiniSceneBaseArgs(loadPage, frameFunction) {
  var page = loadPage({ innerWidth: 288, innerHeight: 288, search: "?variant=mini" });
  page.sandbox[frameFunction](0);
  var calls = [];
  var original = page.sandbox.drawMiniSceneBase;
  page.sandbox.drawMiniSceneBase = function (context, cx, cy, solidRadius, falloffRadius) {
    calls.push({ cx: cx, cy: cy, solid: solidRadius, falloff: falloffRadius });
    return original.apply(this, arguments);
  };
  page.sandbox[frameFunction](100);
  assert.strictEqual(calls.length, 1, "expected one drawMiniSceneBase call per mini frame");
  return { page: page, call: calls[0] };
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

  var miniOnlyNames = options.miniOnly || [];
  var watched = options.keep.concat(options.drop, miniOnlyNames, ["renderAlertOverlay", "renderNebula", "applyMiniEdgeFade"]);
  var counts = spyOnFunctions(page, watched);
  var fills = spyOnFillRect(page);
  drive(page, options.frameFunction, [100, 200]);
  miniOnlyNames.forEach(function (name) {
    assert.strictEqual(counts[name], 0, "the full variant must never run the mini-only layer " + name + " (ran " + counts[name] + " times)");
  });
  assert.strictEqual(counts.applyMiniEdgeFade, 0, "the full variant must never apply the mini edge fade");

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
  checkMiniSceneBaseFunction: checkMiniSceneBaseFunction,
  captureMiniSceneBaseArgs: captureMiniSceneBaseArgs,
  checkMiniLayers: checkMiniLayers,
  checkFullLayers: checkFullLayers,
  checkMiniStylesheet: checkMiniStylesheet,
  coversCanvas: coversCanvas,
};
