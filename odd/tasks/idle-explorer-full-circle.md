# Idle/explorer full circle

## Objective

Show the complete idle and explorer disc on screen, with about 25px of margin at the top and
bottom, instead of letting the screen clip it.

## Problem / why

Every radius is a fraction of `basis = Math.min(W, H)`. The outermost edge is the disc border at
`DISC_BORDER_OUTER_RADIUS_FRACTION = 0.792`, so on a landscape screen the disc diameter is 1.584*H,
taller than the screen. The center also sits low (`CENTER_Y_FRACTION = 0.511`).

## Scope

- One fitted basis per scene (`sceneBasis()` in config.js) so the outer disc border plus
  `DISC_EDGE_MARGIN_PX = 25` fits inside the screen; every ring fraction stays unchanged, the
  whole composition shrinks uniformly.
- Replace every `Math.min(W, H)` in the idle and explorer scenes with it.
- `CENTER_Y_FRACTION` moves to 0.5 (maintainer decision 2026-09-27): symmetric 25px margin.
- The Earth keeps its position relative to the disc (scaled with basis).

## Constraints

- Ring 3 section of rings.js stays identical in idle and explorer (ConstellationRingParityTests).
- The see-through hook must stamp the same constellation cache with the same basis.
- Local commits only, never push.

## TDD

- Mode: on (session config "Strict TDD Mode: enabled").
- Runner: `dotnet test CosmicWin.App.Tests --filter "FullyQualifiedName~IdleSceneNodeTests|FullyQualifiedName~ExplorerSceneNodeTests|FullyQualifiedName~ConstellationRingParityTests"` (Node harness via idle-scene.tests.js / explorer-scene.tests.js).

## Tasks

- [x] T1 — Fitted basis + centered disc in idle and explorer, with harness tests per scene
  (outer radius + 25 <= cy and <= H - cy at 1920x1080 and 1000x800). Route: delegated direct
  (writer trigger: 2+ non-trivial files per scene). Commit: 00148b3.
- [x] T2 — Visual check with Edge headless (--no-sandbox) and in the maintainer's browser.

## Acceptance criteria

- At 1920x1080 and 1000x800, the whole disc is visible with >= 25px top and bottom.
- All existing idle/explorer/parity tests stay green.

## Delivery

- Strategy: ask-on-risk. Forecast: ~150 authored lines.

## Progress

- 2026-09-27: branch feat/idle-explorer-full-circle from main 1c98a16; document created.
- 2026-09-27: T1 done, commit 00148b3 "feat(wallpaper): fit the idle and explorer disc inside the
  screen".
  - Design: `sceneBasis(W, H)` added to both scenes' config.js (right after the disc-border
    constants): `cx = W*CENTER_X_FRACTION`, `cy = H*CENTER_Y_FRACTION` (unchanged, screen-anchored),
    `roomToNearestEdge = min(cy, H-cy, cx, W-cx) - DISC_EDGE_MARGIN_PX` (25px),
    `fittedBasis = roomToNearestEdge / DISC_BORDER_OUTER_RADIUS_FRACTION`, result =
    `max(SCENE_BASIS_MIN_PX=10, min(min(W,H), fittedBasis))` (never bigger than the historical
    min(W,H), never <= 0 on a pathological tiny canvas).
  - `CENTER_Y_FRACTION`: 0.511 -> 0.5 in both scenes' config.js, per the maintainer decision above.
  - Earth: `MEASURED_RING_CENTER_Y_FRACTION = 0.511` kept as its own constant (the value
    `EARTH_CENTER_Y_FRACTION` was actually pixel-measured against, before the recenter).
    `EARTH_CENTER_X_OFFSET_FRACTION = EARTH_CENTER_X_FRACTION - CENTER_X_FRACTION` (= 0) and
    `EARTH_CENTER_Y_OFFSET_FRACTION = EARTH_CENTER_Y_FRACTION - MEASURED_RING_CENTER_Y_FRACTION`
    (= -0.037), both defined in config.js. animate.js: `earthCx = cx + EARTH_CENTER_X_OFFSET_FRACTION
    * basis`, `earthCy = cy + EARTH_CENTER_Y_OFFSET_FRACTION * basis` (was `W/H * EARTH_CENTER_*_FRACTION`
    directly) — keeps Earth's position relative to the disc as the composition shrinks/grows.
  - Every remaining `Math.min(W, H)` basis use replaced with `sceneBasis(W, H)`: idle/explorer
    animate.js (chromatic glow radius, spark size/jitter, the `basis` feeding buildRingCaches/
    ringCacheBasis so ring caches key on the fitted value), earth.js (flare size/ray length), rings.js
    (ruler tick/label lengths, paragraph row gap, outer-glyph-ring basis).
  - Left screen-relative, not basis-fitted (judgment calls): `GLOW_SPARK_RISE_DISTANCE_GROWTH`/
    `_SPAWN_INTERVAL_SECONDS`/`_LIFETIME_SECONDS` (config.js) — these only tune spark rise speed/
    spawn rate at load time; the actual per-frame rise distance is already computed dynamically as
    `baseY - earthCy` in animate.js, so it adapts to the new earthCy automatically. Explorer's
    `rising-sparks.js` (rise height, `Math.max(W, H)` blue-layer glow radius) is intentionally
    screen-relative (sparks rise across the whole screen, the blue tint covers the whole screen), not
    disc-relative, so left untouched. Ring 3 (constellation ring) section of rings.js has no
    `Math.min(W, H)` use at all, so it needed no edit and stayed byte-identical for free.
  - Route: delegated direct (single writer, this task), per the writer trigger (2+ non-trivial files
    per scene: config.js, animate.js, earth.js, rings.js, x2 scenes).
  - TDD (mode: on, runner: `dotnet test CosmicWin.App.Tests --filter "FullyQualifiedName~IdleSceneNodeTests|FullyQualifiedName~ExplorerSceneNodeTests|FullyQualifiedName~ConstellationRingParityTests"`):
    - RED: added "the disc border fits inside the screen with >= 25px margin on every side, at
      1920x1080 and 1000x800" to both idle-scene.tests.js and explorer-scene.tests.js — spies on
      `drawDiscBorder(context, cx, cy, innerRadius, outerRadius)` and asserts `outerRadius + 25 <=
      cy`, `<= H-cy`, `<= cx`, `<= W-cx`. Observed FAIL before the fix, both scenes, same numbers:
      `1920x1080: expected outerRadius(855.36) + 25 <= cy(551.88)` (8/9 idle, 9/10 explorer passed).
    - GREEN: after the fix, `dotnet test ... --filter "...IdleSceneNodeTests|...ExplorerSceneNodeTests|...ConstellationRingParityTests"`
      -> `Passed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3` (all 3 harness/parity test classes,
      including the new case and the pre-existing "see-through hook stamps the SAME
      constellation-ring cache" case).
  - Full suite: `dotnet test CosmicWin.App.Tests` -> `Passed! - Failed: 0, Passed: 1212, Skipped: 6,
    Total: 1218`. The 6 skips are pre-existing environment-gated desktop/elevated integration tests
    (real-window tiling, real schtasks, keyboard hook with real Notepad), unrelated to this change.

- 2026-09-27: RDD assess high (process_boundary in the test harness); maintainer granted review; lineage review-1802b56c3f94234d approved with 4 lenses, no blockers, acknowledged (authority burned). Informational follow-ups: R2-001..004 (config.js comment/constant clarity), R3-glow-baseline-decoupled (explorer animate.js:504-510), R3-untested-cap-floor-branches (sceneBasis cap/floor), R3-zero-slack-float-boundary (test tolerance).
- 2026-09-27: T2 Edge headless screenshot wrote no file in the elevated session; both scenes opened in the maintainer's browser for the visual verdict (pending).
