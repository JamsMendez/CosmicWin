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
// declared names are found textually; config.js touches the DOM once at load (the scene canvas),
// which the minimal document stub below satisfies.
function readConstellationTunables(configPath) {
  var source = fs.readFileSync(configPath, "utf8");
  var context = vm.createContext({
    document: { getElementById: function () { return { getContext: function () { return {}; } }; } },
  });
  vm.runInContext(source, context, { filename: configPath });

  var names = Array.from(source.matchAll(/^(?:const|let|var)\s+(CONSTELLATION_\w+)/gm), function (m) { return m[1]; });
  var values = {};
  names.forEach(function (name) { values[name] = vm.runInContext(name, context); });
  return values;
}

function checkTunablesMatchTwin(sceneDir, twinScene) {
  var own = readConstellationTunables(path.join(sceneDir, "js", "config.js"));
  var twin = readConstellationTunables(path.join(sceneDir, "..", twinScene, "js", "config.js"));
  assert.ok(Object.keys(own).length > 0, "expected CONSTELLATION_* tunables in this scene's config.js");
  assert.deepStrictEqual(own, twin, "this scene's CONSTELLATION_* tunables differ from the " + twinScene + " scene's");
}

module.exports = { checkConstellationTable: checkConstellationTable, checkTunablesMatchTwin: checkTunablesMatchTwin };
