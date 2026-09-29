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

// mini-scene-window T2g: the constellation dots and brush-stroke lines are absolute px sizes
// (CONSTELLATION_DOT_RADIUS, CONSTELLATION_LINE_WIDTH), so in the ~5x smaller mini ring they read as
// blobs. Mini scales both by (this ring's thickness) / (its thickness in the full scene at the reference
// screen, 3440x1440), with a 0.5px floor; the full variant keeps the exact values.
function constellationSizes(page, boxSize) {
  return page.sandbox.constellationDetailSizes(boxSize);
}

// Records the dot radius (in px, i.e. arc radius x the box scale) drawConstellationRing really draws.
function drawnDotRadiiPx(page, boxSize) {
  var scaleFactor = 1;
  var radii = [];
  var context = new Proxy({}, {
    get: function (target, prop) {
      if (prop === "scale") return function (sx) { scaleFactor = sx; };
      if (prop === "arc") return function (x, y, r) { radii.push(r * scaleFactor); };
      return function () {};
    },
    set: function () { return true; },
  });
  page.sandbox.drawConstellationRing(context, 0, 0, 100, 100 + boxSize, 0);
  // arcs with a radius drawn under the box scale are the dots; the 1px boundary arcs run at scale 1 with radius ~100
  return radii.filter(function (r, i) { return r < boxSize; });
}

function checkConstellationDetailScale(loadPage) {
  var cfg = function (page, name) { return vm.runInContext(name, page.sandbox); };

  var mini = loadPage({ innerWidth: 288, innerHeight: 288, search: "?variant=mini" });
  var full = loadPage({ innerWidth: 1000, innerHeight: 500 });
  var dot = cfg(full, "CONSTELLATION_DOT_RADIUS");
  var line = cfg(full, "CONSTELLATION_LINE_WIDTH");
  var thickness = cfg(full, "CONSTELLATION_RING_OUTER_RADIUS_FRACTION") - cfg(full, "CONSTELLATION_RING_INNER_RADIUS_FRACTION");

  // the ring thickness in the mini window, and in the full scene at the 3440x1440 reference screen
  var miniBox = thickness * mini.sandbox.miniSceneBasis(288, 288);
  var referenceBox = thickness * full.sandbox.sceneBasis(3440, 1440);
  var ratio = miniBox / referenceBox;
  assert.ok(ratio > 0.1 && ratio < 0.25, "expected the mini ring to be ~1/6 of the reference ring, ratio " + ratio);

  var expectedDot = Math.max(0.5, dot * ratio);
  var expectedLine = Math.max(0.5, line * ratio);
  var sizes = constellationSizes(mini, miniBox);
  assert.ok(Math.abs(sizes.dotRadius - expectedDot) < 1e-9, "mini dot radius " + sizes.dotRadius + ", expected " + expectedDot);
  assert.ok(Math.abs(sizes.lineWidth - expectedLine) < 1e-9, "mini line width " + sizes.lineWidth + ", expected " + expectedLine);
  assert.ok(sizes.dotRadius < dot / 4, "the mini dots must be far smaller than the full 2.75px, got " + sizes.dotRadius);
  // floor: a tiny ring never makes the dots vanish
  var tiny = constellationSizes(mini, 1);
  assert.ok(tiny.dotRadius >= 0.5 && tiny.lineWidth >= 0.5, "expected the 0.5px floor, got " + JSON.stringify(tiny));
  // the dots actually drawn use that radius
  var drawn = drawnDotRadiiPx(mini, miniBox);
  assert.ok(drawn.length > 0, "expected constellation dots to be drawn");
  drawn.forEach(function (r) { assert.ok(Math.abs(r - expectedDot) < 1e-9, "drawn dot radius " + r + ", expected " + expectedDot); });

  // full: unchanged, whatever the ring size
  [40, 63, 120].forEach(function (box) {
    var f = constellationSizes(full, box);
    assert.strictEqual(f.dotRadius, dot, "the full dot radius must stay " + dot);
    assert.strictEqual(f.lineWidth, line, "the full line width must stay " + line);
  });
  var fullDrawn = drawnDotRadiiPx(full, 63);
  assert.ok(fullDrawn.length > 0);
  fullDrawn.forEach(function (r) { assert.ok(Math.abs(r - dot) < 1e-9, "the full drawn dot radius must stay " + dot + ", got " + r); });
}

module.exports = {
  checkConstellationTable: checkConstellationTable,
  checkTunablesMatchTwin: checkTunablesMatchTwin,
  checkConstellationDetailScale: checkConstellationDetailScale,
};
