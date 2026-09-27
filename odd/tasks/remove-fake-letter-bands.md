# Remove the fake letter bands

## Objective

Stop drawing the fake blue/violet band intersections over the FAILED and WARNING letters of the
alert layer.

## Problem and why

`drawFailureOverlay` (`CosmicWin.App/Alerts/Web/alert-layer.js`) draws a COPY of the wallpaper's
folding-band animation, clipped to the letters, in a fixed color (`theme.intersections`: failed
`rgb(0,160,196)`, warning `rgb(88,40,196)`). It does not reflect the real video behind the page, so
the effect is fake. The maintainer decided on 2026-09-26 to remove it now; the real see-through
tint (a Direct2D pass on the video back buffer) is parked and out of scope.

## Scope (authorized 2026-09-26)

- Remove from `alert-layer.js`: `theme.intersections`, `ONE_WAY_DURATION`,
  `ANIMATION_CYCLE_DURATION`, `FOLDING_BAND_SPEEDS`, `pingpong01`, `animationProgress`,
  `foldingBandCompression`, `foldingBandGeometry`, `fillFoldingBandGeometry`,
  `foldingBandParameters`, `drawFailureBandIntersections`, offscreen slot 1 and its blit.
- Drop the parameters that only fed the bands (`cx`, `cy`, `progress`) along
  `render` -> `renderTile` -> `drawFailureLayer` -> `drawFailureOverlay`.
- Update the comments that name the removed pieces (slot list, header comment).
- Keep unchanged: wash, dark letters with shadow, rails, frame, binary modules, counter, pixelated
  reveal, native shake, tile mosaic, the requestAnimationFrame loop (counter and reveal need it).

## Constraints

- Strict TDD, source: project convention (previous ODD features), runner `dotnet test`
  (the node harness runs through `AlertLayerLayoutNodeTests`). Mutation-check any test that passes
  on its first run.
- Everything stays local: no push, no PR.
- Delivery strategy: ask-on-risk. Forecast: well under 400 authored changed lines.

## Tasks

- [x] B1 - RED: node harness test that renders a `shown` tile of each kind and fails if the
      intersection color is ever painted. Then GREEN: remove the bands and the dead parameters.
      Route: delegated direct (writer trigger: 2 non-trivial files, the harness and the page).
- [ ] B2 - Hardware check: failed, warning and a mixed mosaic show no bands; everything else looks
      as before.

## Acceptance criteria

- No pixel of the old intersection colors is painted by the page, in any state.
- All suites green (App, including the node harness).
- On hardware the alert looks the same except for the missing bands.

## Progress

- 2026-09-26: branch `feat/remove-fake-letter-bands` created from main d8094d9. Feature document
  created.
- 2026-09-26: B1 done in 036f961 (delegated writer; +57/-114, 2 files). RED observed: both new
  harness cases failed (13/15). GREEN 15/15. Mutation check: a reintroduced band paint was caught.
  `dotnet build CosmicWin.sln`: 0 warnings, 0 errors. `dotnet test CosmicWin.App.Tests`: 1094
  passed, 6 skipped (pre-existing hardware-gated), 0 failed. Parent spot check: node harness
  re-run, 2/2 passed. Slot numbers 0/2/3 kept (slot 1 documented as unused).
  Review assess (base d8094d9, committed-only): medium, `under_budget`.

## Next step

B2 hardware check.
