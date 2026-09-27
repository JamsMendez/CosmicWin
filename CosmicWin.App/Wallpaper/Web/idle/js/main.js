// html-wallpaper-demo D6c: copied verbatim from docs/great-sage/background-idle/js/main.js
// (reference-only, excluded from git -- see the feature doc, "Source material"). Not restyled:
// only this header comment was added, the source's own header/body follow unchanged.

// main.js — canvas resize (device-pixel-ratio aware) bootstrap. The actual per-frame render
// (renderFrame) and the requestAnimationFrame loop (startAnimation) live in animate.js, loaded
// just before this file; resize() below only resizes the backing store and invalidates the ring
// caches (by changing W/H/DPR, which renderFrame checks against on its very next frame) — it does
// not force an immediate redraw, since the running RAF loop already redraws every frame.
// Loads last, after every other file.

function resize() {
  const cssWidth = Math.max(1, window.innerWidth || 1);
  const cssHeight = Math.max(1, window.innerHeight || 1);
  const dpr = Math.min(window.devicePixelRatio || 1, 2);
  W = cssWidth;
  H = cssHeight;
  DPR = dpr;
  canvas.width = Math.round(cssWidth * dpr);
  canvas.height = Math.round(cssHeight * dpr);
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  // Ring/Earth caches are keyed by pixel size AND DPR (see animate.js/earth.js); resizing the
  // backing store invalidates them, and the next renderFrame() call rebuilds them before drawing.
  watchDprChange();
}

// A DPR change alone (e.g. dragging the window to a display with a different scale factor,
// or an OS-level display-scale change) does not necessarily fire a `resize` event if the CSS
// pixel dimensions stay the same, so the stale caches would otherwise linger until some other
// resize happened to also change W/H. `matchMedia('(resolution: ...)')` fires when the current
// DPR stops matching, at which point re-running resize() picks up the new devicePixelRatio (and,
// since the listener itself is one-shot per media query, re-registers itself for the next value).
let dprMediaQuery = null;
function watchDprChange() {
  const dpr = window.devicePixelRatio || 1;
  // resize() calls this on every resize event; drop the previous one-shot listener first so
  // same-DPR resizes (which never fire it) do not accumulate listeners.
  dprMediaQuery?.removeEventListener('change', resize);
  dprMediaQuery = window.matchMedia(`(resolution: ${dpr}dppx)`);
  dprMediaQuery.addEventListener('change', resize, { once: true });
}

window.addEventListener('resize', resize, { passive: true });

// IDL-13: Ctrl+F toggles fullscreen, mirroring the Ctrl+F handler in
// backgroud-processing/js/main.js. Ctrl only (no Alt/Meta) so it does not fire alongside other
// modifier combos; preventDefault keeps the browser's own find bar from opening.
function toggleFullscreen() {
  if (document.fullscreenElement) {
    document.exitFullscreen?.()?.catch(() => {});
  } else {
    document.documentElement.requestFullscreen?.()?.catch(() => {});
  }
}

window.addEventListener('keydown', (event) => {
  if (!event.ctrlKey || event.altKey || event.metaKey) return;
  if (event.key?.toLowerCase() !== 'f') return;
  // Prevent first so the find bar never opens, even if the toggle below throws.
  event.preventDefault();
  // Holding the keys auto-repeats keydown; toggle only on the initial press.
  if (event.repeat) return;
  toggleFullscreen();
});

// Entering/exiting fullscreen does not always fire a 'resize' event at the same time the
// viewport dimensions actually change (browser-dependent timing) — re-running resize() directly
// on 'fullscreenchange' guarantees the canvas backing store and ring/Earth caches stay in sync,
// same as backgroud-processing/js/nebula.js does for its own canvas.
document.addEventListener('fullscreenchange', resize);

resize();
startAnimation();
