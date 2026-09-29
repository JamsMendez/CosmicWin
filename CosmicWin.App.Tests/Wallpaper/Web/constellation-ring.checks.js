"use strict";

// Constellation-ring checks shared by idle-scene.tests.js and explorer-scene.tests.js. Both scenes
// ship their own copy of the same ring (CONSTELLATION_FIGURES and its drawing code in js/rings.js,
// its CONSTELLATION_* tunables in js/config.js), so the checks live here once and each harness
// calls them, instead of two pasted copies that could drift like the code they guard.
// ConstellationRingParityTests.cs separately keeps the two scenes' ring CODE identical.

const vm = require("vm");
const fs = require("fs");
const path = require("path");
const assert = require("assert");

// js/rings.js draws each edge as points[from] -> points[to], so an out-of-range index in the
// hand-written CONSTELLATION_FIGURES table would only surface as a TypeError while the ring bakes.
// `context` is a loaded page's vm context; `const` bindings are not properties of it, so they are
// read through the context itself.
function checkConstellationTable(context) {
  var figures = vm.runInContext("CONSTELLATION_FIGURES", context);
  var pool = vm.runInContext("CONSTELLATION_POOL", context);
  var ringSlots = vm.runInContext("CONSTELLATION_COUNT", context);

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

  assert.ok(ringSlots > 0, "expected the ring to hold at least one constellation");
  assert.strictEqual(pool.length, ringSlots, "expected one built figure per ring slot");
  pool.forEach(function (built, i) {
    var figure = figures[i % figures.length];
    assert.strictEqual(built.segments.length, figure.edges.length,
      "ring slot " + i + " (" + figure.name + "): expected one brush segment per edge");
  });
}

// Evaluates a scene's js/config.js on its own and returns every CONSTELLATION_* tunable by VALUE,
// so the comparison is immune to comments, line breaks, and semicolons inside strings. Only the
// declared names are found textually (line-start const/let/var, the only form config.js uses).
//
// config.js's only host dependency at load is the scene canvas:
//   const canvas = document.getElementById('scene');
//   const ctx = canvas.getContext('2d', ...);
// which makeConfigHostStub stands in for. A load failure cannot be reliably told apart as "the stub
// lacks something" versus "config.js itself is wrong" (a missing stub method is a TypeError, a typo
// in config.js is a ReferenceError), so no guess is made: the original error is reported as is,
// with the stub mentioned only as a possible cause.
var CONFIG_HOST_STUB_DESCRIPTION = "document.getElementById('scene').getContext()";
function makeConfigHostStub() {
  return {
    document: { getElementById: function () { return { getContext: function () { return {}; } }; } },
  };
}

function readConstellationTunables(configPath) {
  var source = fs.readFileSync(configPath, "utf8");
  var context = vm.createContext(makeConfigHostStub());
  try {
    vm.runInContext(source, context, { filename: configPath });
  } catch (error) {
    throw new Error(configPath + " failed to load for the tunables comparison: " + String(error) +
      ". If it now needs a host API beyond the stub (" + CONFIG_HOST_STUB_DESCRIPTION +
      "), extend makeConfigHostStub in constellation-ring.checks.js.", { cause: error });
  }

  var names = Array.from(source.matchAll(/^(?:const|let|var)\s+(CONSTELLATION_\w+)/gm), function (m) { return m[1]; });
  var values = {};
  // structuredClone re-creates each value in THIS realm: every vm context has its own
  // Array.prototype/Object.prototype, and deepStrictEqual compares prototypes, so array or object
  // tunables read straight from two contexts would never compare equal even with identical values.
  // Tunables are plain data. A function or symbol cannot be cloned, and the error names which
  // tunable instead of surfacing a bare DataCloneError. (A class instance does clone, into a plain
  // object without its prototype, which is still a fair by-value comparison for data.)
  names.forEach(function (name) {
    var value = vm.runInContext(name, context);
    try {
      values[name] = structuredClone(value);
    } catch (error) {
      throw new Error(configPath + ": " + name + " is not plain data (numbers, strings, arrays, objects), " +
        "so it cannot be compared by value. " + error.message, { cause: error });
    }
  });
  return values;
}

// The twin scene is expected as a sibling directory of sceneDir, which is how both scenes ship
// (Wallpaper/Web/idle and Wallpaper/Web/explorer).
function checkTunablesMatchTwin(sceneDir, twinScene) {
  var twinConfig = path.join(sceneDir, "..", twinScene, "js", "config.js");
  assert.ok(fs.existsSync(twinConfig),
    "expected the " + twinScene + " scene next to this one, at " + twinConfig);
  var own = readConstellationTunables(path.join(sceneDir, "js", "config.js"));
  var twin = readConstellationTunables(twinConfig);
  assert.ok(Object.keys(own).length > 0, "expected CONSTELLATION_* tunables in this scene's config.js");
  assert.deepStrictEqual(own, twin, "this scene's CONSTELLATION_* tunables differ from the " + twinScene + " scene's");
}

// mini-scene-window T2g/T2h: the constellation dots and brush-stroke lines (CONSTELLATION_DOT_RADIUS,
// CONSTELLATION_LINE_WIDTH) and the hieroglyph band's glyph stroke (HIEROGLYPH_STROKE_PX) are absolute px
// sizes, so in the ~6x smaller mini ring they read as blobs / dense hatching. Mini scales each by
// (that ring's thickness) / (its thickness in the full scene at the 3440x1440 reference screen), with a
// MINI_CONSTELLATION_MIN_PX floor; the full variant keeps the exact tunables.

function cfg(page, name) { return vm.runInContext(name, page.sandbox); }

// A recording 2D context that keeps a real save()/restore() stack of the current scale factor. Every
// arc()/lineWidth is recorded together with the scale in force, so a test can tell what was drawn
// inside a scale(boxSize) block (the dots: radius in box units) from arcs at scale 1 (boundary lines).
function makeRecordingContext() {
  var scaleFactor = 1;
  var stack = [];
  var arcs = [];
  var context = new Proxy({}, {
    get: function (target, prop) {
      if (prop === "save") return function () { stack.push(scaleFactor); };
      if (prop === "restore") return function () { scaleFactor = stack.length ? stack.pop() : 1; };
      if (prop === "scale") return function (sx) { scaleFactor *= sx; };
      if (prop === "arc") return function (x, y, r) { arcs.push({ radius: r, scale: scaleFactor }); };
      return function () {};
    },
    set: function () { return true; },
  });
  return { context: context, arcs: arcs };
}

// The dot radius in px that drawConstellationRing really draws: arcs recorded while the box scale
// (== boxSize) is in force. The circular boundary arcs are drawn after restore(), at scale 1.
function drawnDotRadiiPx(page, boxSize) {
  var rec = makeRecordingContext();
  page.sandbox.drawConstellationRing(rec.context, 0, 0, 100, 100 + boxSize, 0);
  return rec.arcs
    .filter(function (a) { return Math.abs(a.scale - boxSize) < 1e-6; }) // (100 + boxSize) - 100 is not exactly boxSize
    .map(function (a) { return a.radius * a.scale; });
}

// The stroke width in px drawHieroglyphBand passes to drawHieroglyph for its glyphs.
function drawnHieroglyphStrokesPx(page, bandThickness) {
  var widths = [];
  var original = page.sandbox.drawHieroglyph;
  page.sandbox.drawHieroglyph = function (context, strokes, x, y, size, angle, color, lineWidth) { widths.push(lineWidth); };
  try {
    page.sandbox.drawHieroglyphBand(makeRecordingContext().context, 0, 0, 100, 100 + bandThickness, 0);
  } finally {
    page.sandbox.drawHieroglyph = original;
  }
  return widths;
}

function checkConstellationDetailScale(loadPage) {
  var mini = loadPage({ innerWidth: 288, innerHeight: 288, search: "?variant=mini" });
  var full = loadPage({ innerWidth: 1000, innerHeight: 500 });
  var dot = cfg(full, "CONSTELLATION_DOT_RADIUS");
  var line = cfg(full, "CONSTELLATION_LINE_WIDTH");
  var floor = cfg(full, "MINI_CONSTELLATION_MIN_PX");
  var thickness = cfg(full, "CONSTELLATION_RING_OUTER_RADIUS_FRACTION") - cfg(full, "CONSTELLATION_RING_INNER_RADIUS_FRACTION");
  var reference = cfg(full, "MINI_CONSTELLATION_REFERENCE_SCREEN");

  // the ring thickness in the mini window, and in the full scene at the reference screen
  var miniBox = thickness * mini.sandbox.miniSceneBasis(288, 288);
  var referenceBox = thickness * full.sandbox.sceneBasis(reference.width, reference.height);
  var ratio = miniBox / referenceBox;
  assert.ok(ratio > 0 && ratio < 1, "the mini ring must be smaller than the reference ring, ratio " + ratio);

  // below the floor: the 288px window (dot*ratio and line*ratio are both under it)
  assert.ok(dot * ratio < floor && line * ratio < floor, "expected the 288px mini ring to sit under the floor");
  var sizes = mini.sandbox.constellationDetailSizes(miniBox);
  assert.strictEqual(sizes.dotRadius, floor, "mini dot radius must be the floor " + floor + ", got " + sizes.dotRadius);
  assert.strictEqual(sizes.lineWidth, floor, "mini line width must be the floor " + floor + ", got " + sizes.lineWidth);
  var tiny = mini.sandbox.constellationDetailSizes(1);
  assert.ok(tiny.dotRadius >= floor && tiny.lineWidth >= floor, "a tiny ring must never drop under the floor, got " + JSON.stringify(tiny));

  // ABOVE the floor: a ring 90% of the reference ring, where the proportional branch decides the value
  var bigBox = referenceBox * 0.9;
  var bigRatio = bigBox / referenceBox;
  assert.ok(dot * bigRatio > floor && line * bigRatio > floor, "expected the big ring to be above the floor");
  var big = mini.sandbox.constellationDetailSizes(bigBox);
  assert.ok(Math.abs(big.dotRadius - dot * bigRatio) < 1e-9, "proportional dot radius " + big.dotRadius + ", expected " + dot * bigRatio);
  assert.ok(Math.abs(big.lineWidth - line * bigRatio) < 1e-9, "proportional line width " + big.lineWidth + ", expected " + line * bigRatio);
  drawnDotRadiiPx(mini, bigBox).forEach(function (r) {
    assert.ok(Math.abs(r - dot * bigRatio) < 1e-9, "drawn dot radius " + r + ", expected " + dot * bigRatio);
  });

  // the dots actually drawn in the 288px ring use the floored radius
  var drawn = drawnDotRadiiPx(mini, miniBox);
  assert.ok(drawn.length > 0, "expected constellation dots to be drawn");
  drawn.forEach(function (r) { assert.ok(Math.abs(r - sizes.dotRadius) < 1e-9, "drawn dot radius " + r + ", expected " + sizes.dotRadius); });

  // full: unchanged, whatever the ring size
  [40, miniBox, referenceBox].forEach(function (box) {
    var f = full.sandbox.constellationDetailSizes(box);
    assert.strictEqual(f.dotRadius, dot, "the full dot radius must stay " + dot);
    assert.strictEqual(f.lineWidth, line, "the full line width must stay " + line);
  });
  var fullDrawn = drawnDotRadiiPx(full, referenceBox);
  assert.ok(fullDrawn.length > 0);
  fullDrawn.forEach(function (r) { assert.ok(Math.abs(r - dot) < 1e-9, "the full drawn dot radius must stay " + dot + ", got " + r); });
}

// The hieroglyph band's glyph stroke follows the same ratio and floor, and the full stroke stays 1.6.
function checkHieroglyphStrokeScale(loadPage) {
  var mini = loadPage({ innerWidth: 288, innerHeight: 288, search: "?variant=mini" });
  var full = loadPage({ innerWidth: 1000, innerHeight: 500 });
  var stroke = cfg(full, "HIEROGLYPH_STROKE_PX");
  var floor = cfg(full, "MINI_CONSTELLATION_MIN_PX");
  var reference = cfg(full, "MINI_CONSTELLATION_REFERENCE_SCREEN");
  var bandFraction = cfg(full, "HIEROGLYPH_BAND_OUTER_RADIUS_FRACTION") - cfg(full, "HIEROGLYPH_BAND_INNER_RADIUS_FRACTION");
  var referenceBand = bandFraction * full.sandbox.sceneBasis(reference.width, reference.height);
  var miniBand = bandFraction * mini.sandbox.miniSceneBasis(288, 288);

  // 288px: under the floor
  assert.ok(stroke * (miniBand / referenceBand) < floor, "expected the 288px band stroke to sit under the floor");
  var miniWidths = drawnHieroglyphStrokesPx(mini, miniBand);
  assert.ok(miniWidths.length > 0, "expected hieroglyphs to be drawn");
  miniWidths.forEach(function (w) { assert.strictEqual(w, floor, "mini hieroglyph stroke " + w + ", expected the floor " + floor); });

  // above the floor: proportional
  var bigBand = referenceBand * 0.9;
  assert.ok(stroke * 0.9 > floor);
  drawnHieroglyphStrokesPx(mini, bigBand).forEach(function (w) {
    assert.ok(Math.abs(w - stroke * 0.9) < 1e-9, "proportional hieroglyph stroke " + w + ", expected " + stroke * 0.9);
  });

  // full: unchanged at any band size
  [miniBand, referenceBand].forEach(function (band) {
    var widths = drawnHieroglyphStrokesPx(full, band);
    assert.ok(widths.length > 0);
    widths.forEach(function (w) { assert.strictEqual(w, stroke, "the full hieroglyph stroke must stay " + stroke + ", got " + w); });
  });
}

module.exports = {
  checkConstellationTable: checkConstellationTable,
  checkTunablesMatchTwin: checkTunablesMatchTwin,
  checkConstellationDetailScale: checkConstellationDetailScale,
  checkHieroglyphStrokeScale: checkHieroglyphStrokeScale,
};
