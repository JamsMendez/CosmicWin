// html-wallpaper-demo D6b: copied verbatim from docs/great-sage/background-raphael/js/layers.js
// (reference-only, excluded from git -- see the feature doc, "Source material"). Not restyled:
// only this header comment was added, the source's own header/body follow unchanged -- INCLUDING
// drawGlyphRings/drawOutlineGlyphRing, the gold ring's real paint path. This scene's fake folding
// bands (foldingBandGeometry/fillFoldingBandGeometry/foldingBandCompression(Derivative)/
// foldingBandParameters) go away per the feature doc's D6 decision -- Raphael's own render() (see
// js/main.js) never calls drawFeathers'/drawAtomicOrbits'-equivalent for them; they simply go unused
// here (dead code, same as every OTHER helper this file's own header already marks unused after
// RAP-2b/RAP-8's earlier removals) rather than deleted, to keep this commit a pure, verbatim, provably
// lossless port. The one PAGE-COMMIT seam (goldGlyphRingDrawParams) that lets
// js/see-through-hook.js redraw the real gold ring lands in the NEXT commit, not here -- see that
// commit's own message.

// layers.js — the scene's visual layers: soft ovals, stars, radial streaks, lens flares,
// chromatic side loops, the golden hexadecagon core, the central core, film grain, and vignette.
// Loads after config.js, math.js, sphere.js, scene-data.js, and sprites.js (calls stampSprite and
// reads the seeded descriptor arrays).
//
// RAP-2b: the segmented sphere, orbit blocks, and the visible "atomic orbit" folding bands were
// removed from the scene — user: no sphere, bands or orbits in Raphael. foldingBandGeometry/
// fillFoldingBandGeometry/foldingBandCompression(Derivative)/foldingBandParameters stay: the
// Ctrl+E/Ctrl+A failure overlay (js/failure-overlay.js) reuses this band geometry to clip its
// band-intersection layer, unrelated to whether the bands are painted into the main scene.
//
// A path with moveTo/lineTo pairs preserves independent segments while sharing one shadowed stroke.
function drawGlowSegments(segments, width, alpha, blur = 8) {
  if (segments.length === 0) return;
  ctx.save();
  ctx.strokeStyle = `rgba(${CENTRAL_RAY_STROKE_COLOR},${alpha})`;
  ctx.lineWidth = width;
  ctx.lineCap = 'round';
  ctx.shadowColor = CENTRAL_RAY_GLOW_COLOR;
  ctx.shadowBlur = blur;
  ctx.beginPath();
  for (const [x1, y1, x2, y2] of segments) {
    ctx.moveTo(x1, y1);
    ctx.lineTo(x2, y2);
  }
  ctx.stroke();
  ctx.restore();
}

// Normalize the full-loop endpoint before any trigonometry, so wet-light states
// are bit-for-bit identical at the two ends of the ping-pong animation.
function opticalCycle(phase) {
  const cycle = phase / TAU;
  return cycle - Math.floor(cycle);
}

function opticalEnvelope(phase, stagger) {
  const local = fract(opticalCycle(phase) + stagger);
  const appear = smoothstep(0.025, 0.18, local);
  const disappear = 1 - smoothstep(0.68, 0.92, local);
  return 0.018 + 0.982 * appear * disappear;
}

function prominentRayGrowthEnvelope(phase, rayIndex) {
  if (CENTRAL_CORE_PROMINENT_RAY_COUNT === 0) return 0;
  const stagger = rayIndex / CENTRAL_CORE_PROMINENT_RAY_COUNT * TAU;
  return 0.5 + 0.5 * Math.sin(phase * CENTRAL_CORE_PROMINENT_RAY_PULSE_SPEED + stagger);
}

function viewportSafeRayLength(cx, cy, dx, dy) {
  const horizontalLimit = Math.abs(dx) < 1e-9 ? Infinity : (dx > 0 ? W - cx : cx) / Math.abs(dx);
  const verticalLimit = Math.abs(dy) < 1e-9 ? Infinity : (dy > 0 ? H - cy : cy) / Math.abs(dy);
  return Math.min(horizontalLimit, verticalLimit) * CENTRAL_CORE_PROMINENT_SAFE_REACH;
}

function centralCoreRaySafeLength(cx, cy, angle) {
  return viewportSafeRayLength(cx, cy, Math.cos(angle), Math.sin(angle));
}

// Rendering is zoomed about (cx, cy), so these untransformed bounds map exactly to the visible edge.
function visibleCanvasBounds(cx, cy, width = W, height = H, zoom = viewZoom) {
  return {
    left: cx - cx / zoom,
    right: cx + (width - cx) / zoom,
    top: cy - cy / zoom,
    bottom: cy + (height - cy) / zoom,
  };
}

function firstRayBoundaryLength(cx, cy, dx, dy, bounds) {
  const horizontalLimit = Math.abs(dx) < 1e-9 ? Infinity : (dx > 0 ? bounds.right - cx : cx - bounds.left) / Math.abs(dx);
  const verticalLimit = Math.abs(dy) < 1e-9 ? Infinity : (dy > 0 ? bounds.bottom - cy : cy - bounds.top) / Math.abs(dy);
  return Math.min(horizontalLimit, verticalLimit);
}

function normalizedCentralCoreRadius(phase) {
  const cyclePhase = opticalCycle(phase) * TAU;
  return (0.050 + Math.sin(cyclePhase) * 0.003) * (0.92 + 0.08 * Math.cos(cyclePhase));
}

function drawSpectralHalo(cx, cy, radius, alpha) {
  ctx.save();
  ctx.translate(cx, cy);
  for (let i = 0; i < WET_HALO_LAYERS.length; i++) {
    const layer = WET_HALO_LAYERS[i];
    const sprite = sprites.halo[i];
    stampSprite(sprite, radius * layer.scale / sprite.hw, alpha * layer.alpha);
  }
  ctx.restore();
}

function chromaEllipse(cx, cy, rot, alpha, loopIndex, unit) {
  const loop = CHROMATIC_LOOP_GROUPS[loopIndex];
  ctx.save();
  ctx.translate(cx, cy);
  ctx.rotate(rot);
  ctx.globalCompositeOperation = 'screen';
  drawSpectralHalo(0, 0, Math.max(W * loop.rx, H * loop.ry) * unit * 1.48, alpha);
  stampRainbowRing(sprites.chromaRings[loopIndex], unit, alpha * 0.74);
  ctx.restore();
}

// Squared cosine retains the old 0.10..1 range and pi-period frequency without abs(cos)'s cusps.
function foldingBandCompression(fold) {
  return 0.10 + 0.90 * Math.cos(fold) ** 2;
}

function foldingBandCompressionDerivative(fold) {
  return -0.90 * Math.sin(fold * 2);
}

function foldingBandGeometry(rx, ry, width, foldPhase) {
  const segmentCount = Math.max(48, Math.min(84, Math.round(Math.min(W, H) * 0.075)));
  const points = [];

  for (let i = 0; i <= segmentCount; i++) {
    const a = (i / segmentCount) * TAU;
    const x = Math.cos(a) * rx;
    const y = Math.sin(a) * ry;
    const tx = -Math.sin(a) * rx;
    const ty = Math.cos(a) * ry;
    const tangentLength = Math.hypot(tx, ty);
    const nx = -ty / tangentLength;
    const ny = tx / tangentLength;
    const fold = a * 2 + foldPhase;
    const compression = foldingBandCompression(fold);
    const bandWidth = width * compression;
    const skew = Math.sin(fold) * width * 0.22;

    points.push({
      left: [x + nx * bandWidth + (tx / tangentLength) * skew, y + ny * bandWidth + (ty / tangentLength) * skew],
      right: [x - nx * bandWidth - (tx / tangentLength) * skew, y - ny * bandWidth - (ty / tangentLength) * skew],
      front: 0.5 + 0.5 * Math.cos(fold),
    });
  }
  return points;
}

function fillFoldingBandGeometry(g, points, fillStyle) {
  g.fillStyle = fillStyle;
  for (let i = 0; i < points.length - 1; i++) {
    const a = points[i];
    const b = points[i + 1];
    g.beginPath();
    g.moveTo(a.left[0], a.left[1]);
    g.lineTo(b.left[0], b.left[1]);
    g.lineTo(b.right[0], b.right[1]);
    g.lineTo(a.right[0], a.right[1]);
    g.closePath();
    g.fill();
  }
}

function auroraState(cx, cy, phase, b) {
  const cyclePhase = opticalCycle(phase) * TAU;
  const angle = b.a + cyclePhase + Math.sin(cyclePhase + b.phase) * 0.20;
  const depth = 0.5 + 0.5 * Math.sin(cyclePhase + b.phase);
  const radialDistance = b.orbit * (0.68 + depth * 0.44);
  return {
    x: cx + Math.cos(angle) * W * radialDistance,
    y: cy + Math.sin(angle) * H * radialDistance * 0.92,
    rx: W * b.rx * (0.72 + depth * 0.48),
    ry: H * b.ry * (0.72 + depth * 0.48),
    rotation: angle * 0.16,
    color: b.green ? AURORA_GREEN : AURORA_BLUE,
    alpha: b.alpha * (0.68 + depth * 0.58),
  };
}

function softOvalViewportAxes(width = W, height = H) {
  const cappedAspect = Math.min(
    SOFT_OVAL_VIEWPORT_AXIS_STRETCH_CAP,
    Math.max(1 / SOFT_OVAL_VIEWPORT_AXIS_STRETCH_CAP, width / height)
  );
  const sharedViewportSize = Math.sqrt(width * height);
  return {
    x: sharedViewportSize * Math.sqrt(cappedAspect),
    y: sharedViewportSize / Math.sqrt(cappedAspect),
  };
}

function softOvalFieldState(cx, cy, phase, field) {
  const cyclePhase = opticalCycle(phase) * TAU;
  const orbit = cyclePhase + field.phase;
  const breath = 0.5 + 0.5 * Math.sin(orbit);
  const scale = Math.max(0.2, 0.99 + (breath - 0.5) * 0.30 * SOFT_OVAL_BREATH_FACTOR);
  const viewportAxes = softOvalViewportAxes();
  return {
    x: cx + W * (field.x - 0.505) + Math.cos(orbit) * W * field.orbitX * SOFT_OVAL_MOTION_FACTOR,
    y: cy + H * (field.y - 0.515) + Math.sin(orbit) * H * field.orbitY * SOFT_OVAL_MOTION_FACTOR,
    rx: viewportAxes.x * field.rx * SOFT_OVAL_SIZE_FACTOR * scale,
    ry: viewportAxes.y * field.ry * SOFT_OVAL_SIZE_FACTOR * scale,
    rotation: field.rotation + Math.sin(orbit) * 0.12 * SOFT_OVAL_MOTION_FACTOR,
    alpha: field.alpha * (0.62 + breath * 0.46),
    color: field.color,
    blur: field.blur,
    scale,
  };
}

function drawSoftOvalField(field, sprite) {
  ctx.save();
  ctx.translate(field.x, field.y);
  ctx.rotate(field.rotation);
  ctx.globalCompositeOperation = 'screen';
  const visibleAlpha = Math.min(
    field.alpha * SOFT_OVAL_VISIBLE_ALPHA_MULTIPLIER,
    SOFT_OVAL_VISIBLE_ALPHA_CAP
  );
  stampSprite(sprite, field.scale, visibleAlpha);
  ctx.restore();
}

function drawSoftOvalFields(cx, cy, phase) {
  SOFT_OVAL_FIELDS.forEach((field, index) => {
    drawSoftOvalField(softOvalFieldState(cx, cy, phase, field), sprites.softOvals[index]);
  });
}

// RAP-4: the additional circular-oval field shares softOvalFieldState's drift/breathe math (same
// field shape) but its own independent size/motion/breath factors and visible-alpha mapping, so
// tuning it never moves the original soft ovals.
function circularOvalFieldState(cx, cy, phase, field) {
  const cyclePhase = opticalCycle(phase) * TAU;
  const orbit = cyclePhase + field.phase;
  const breath = 0.5 + 0.5 * Math.sin(orbit);
  const scale = Math.max(0.2, 0.99 + (breath - 0.5) * 0.30 * CIRCULAR_OVAL_BREATH_FACTOR);
  const viewportAxes = softOvalViewportAxes();
  return {
    x: cx + W * (field.x - 0.505) + Math.cos(orbit) * W * field.orbitX * CIRCULAR_OVAL_MOTION_FACTOR,
    y: cy + H * (field.y - 0.515) + Math.sin(orbit) * H * field.orbitY * CIRCULAR_OVAL_MOTION_FACTOR,
    rx: viewportAxes.x * field.rx * scale,
    ry: viewportAxes.y * field.ry * scale,
    rotation: field.rotation + Math.sin(orbit) * 0.12 * CIRCULAR_OVAL_MOTION_FACTOR,
    alpha: field.alpha * (0.62 + breath * 0.46),
    color: field.color,
    blur: field.blur,
    scale,
  };
}

function drawCircularOvalField(field, sprite) {
  ctx.save();
  ctx.translate(field.x, field.y);
  ctx.rotate(field.rotation);
  ctx.globalCompositeOperation = 'screen';
  const visibleAlpha = Math.min(field.alpha * CIRCULAR_OVAL_VISIBLE_ALPHA_MULTIPLIER, CIRCULAR_OVAL_VISIBLE_ALPHA_CAP);
  stampSprite(sprite, field.scale, visibleAlpha);
  ctx.restore();
}

function drawCircularOvalFields(cx, cy, phase) {
  CIRCULAR_OVAL_FIELDS.forEach((field, index) => {
    drawCircularOvalField(circularOvalFieldState(cx, cy, phase, field), sprites.circularOvals[index]);
  });
}

// RAP-4: translucent white "feathers" (a quill spine with soft vanes) that drift and tint toward
// gold as they near the core and/or a golden inclined ray. Geometry/tint math (featherTint) lives
// in js/feathers.js, unit-tested there; this only paints it.
// Angular closeness (0..1) of a point to the nearest currently-visible golden inclined ray.
function featherRayProximity(phase, x, y, cx, cy) {
  const pointAngle = Math.atan2(y - cy, x - cx);
  let minDelta = Math.PI;
  for (const ray of PERSPECTIVE_RAYS) {
    const rayAngle = perspectiveRayAngle(phase, ray);
    let delta = Math.abs(pointAngle - rayAngle) % TAU;
    if (delta > Math.PI) delta = TAU - delta;
    if (delta < minDelta) minDelta = delta;
  }
  return 1 - smoothstep(0, FEATHER_RAY_ANGLE_WINDOW, minDelta);
}

// RAP-10 (user feedback): "las plumas deben tener el mismo efecto que las partículas que salen del
// centro" — position/growth/fade now come from js/feathers.js's featherOffset/featherSizeScale/
// featherAlphaEnvelope (mirroring drawStars' particle motion); only the tint and final screen
// position are computed here, same as before.
function featherState(cx, cy, p, phase, feather) {
  const offset = featherOffset(feather, p, W, H);
  const x = cx + offset.dx;
  const y = cy + offset.dy;
  const distance = Math.hypot(offset.dx, offset.dy) / (coreRadius(Math.min(W, H)));
  const rayProximity = featherRayProximity(phase, x, y, cx, cy);
  return {
    x,
    y,
    // RAP-23: flutter (fast wobble) adds to the existing tilt + slow spin. RAP-30: featherRockAngle
    // is a rocking tilt exactly in phase with the sway (same swayRate/swayPhase), so the feather
    // always tilts toward its own current sway direction. RAP-31 (user: "Saquemos la pirueta y
    // dejemos el 3D"): the pirouette is removed; featherFlipScaleX (the 3D-flip squash) is restored
    // as a SCALE, coexisting with the rocking tilt (angle) — no conflict. Every term is a real,
    // independently-tested feathers.js function of the SAME `p`.
    // User elimination test: flutter disabled (kept, not deleted) — restore by re-adding
    // `+ featherFlutterAngle(feather, p)` to the sum below.
    angle: feather.angle + feather.tiltSeed + featherSpinAngle(feather, p) /* + featherFlutterAngle(feather, p) */ + featherRockAngle(feather, p),
    tint: featherTint(distance, rayProximity),
    alphaEnvelope: featherAlphaEnvelope(offset.z),
    sizeScale: featherSizeScale(offset.z),
    flipScaleX: featherFlipScaleX(feather, p),
  };
}

// Both colors are plain 'r,g,b' strings (never rgba), so a per-channel linear mix is exact.
// RAP-12 follow-up (user reference: plumas.avif — "quiero unos diseños así") — a cached sprite per
// feather-shape variant (js/sprites.js's paintFeatherVariant/paintFeatherGoldMask, built once in
// buildSprites) is stamped here with per-instance rotation/scale/alpha; no per-frame path
// building. The gold tint is a second, scaled-down sprite pass over the white one, skipped
// entirely for blurred (background depth-cue) feathers so they stay plain and soft.
function drawFeather(feather, state, size) {
  const unit = (feather.length * size * state.sizeScale) / FEATHER_SPRITE_REFERENCE_LENGTH;
  const alpha = feather.alpha * state.alphaEnvelope;
  const spriteSet = feather.blurred ? sprites.featherBlurred : sprites.featherSharp;
  ctx.save();
  ctx.translate(state.x, state.y);
  ctx.rotate(state.angle);
  ctx.scale(state.flipScaleX, 1);
  stampSprite(spriteSet[feather.variantIndex], unit, alpha);
  if (!feather.blurred && state.tint > 0) {
    stampSprite(sprites.featherGoldMask[feather.variantIndex], unit, alpha * state.tint * FEATHER_TINT_DISPLAY_SCALE);
  }
  ctx.restore();
}

// RAP-5: four concentric annuli outward from the hexadecagon core: (a) clear, (b) thick bold gold
// glyphs, (c) clear again (the green/gold nebula shows through from the WebGL canvas behind
// #scene), (d) the same glyphs, larger and translucent blue. Thin gold circle lines delimit every
// ring boundary. Geometry lives in js/glyph-rings.js (pure, unit-tested); glyph generation/drawing
// reuses js/glyphs.js (copied from background-idle) unchanged.
// RAP-33 (user feedback): "agrégales efecto de luz y hazlos un poco más gruesos" — a soft additive
// glow (shadowBlur, 'screen' compositing) plus ~2x the previous stroke width. The delimiter
// geometry itself only changes on resize (coreRadius/annuli depend solely on min(W,H), not on any
// per-frame animation value), so there is no expensive per-frame recomputation to cache here —
// this stays as cheap as 5 plain arc strokes, same as the pre-RAP-33 version, with shadowBlur
// reusing the same established glow technique drawGoldenHexadecagon already uses elsewhere.
function drawGlyphRingDelimiters(cx, cy, annuli) {
  ctx.save();
  ctx.globalCompositeOperation = 'screen';
  ctx.strokeStyle = GLYPH_RING_DELIMITER_COLOR;
  ctx.lineWidth = GLYPH_RING_DELIMITER_WIDTH;
  ctx.shadowColor = GLYPH_RING_DELIMITER_GLOW_COLOR;
  ctx.shadowBlur = GLYPH_RING_DELIMITER_GLOW_BLUR;
  const radii = [
    annuli[0].innerRadius,
    annuli[0].outerRadius,
    annuli[1].outerRadius,
    annuli[2].outerRadius,
    annuli[3].outerRadius,
  ];
  for (const radius of radii) {
    ctx.beginPath();
    ctx.arc(cx, cy, radius, 0, TAU);
    ctx.stroke();
  }
  ctx.shadowBlur = 0;
  ctx.restore();
}

// html-wallpaper-demo D6b seam: drawGlyphRings/drawOutlineGlyphRing below draw the gold ring through
// pre-baked sprite bitmaps (js/sprites.js's buildOutlineRingSprites/stampSprite), stamped straight
// onto the module-global `ctx` -- there is no way to redirect that path into an arbitrary offscreen
// context, so it cannot be reused by the shared alert overlay's see-through hook, which must draw
// into the tile-local context the overlay hands it (CosmicWin.App/Wallpaper/Web/shared/js/
// alert-overlay.js's drawSeeThroughIntersections), never the scene's own canvas. This factors out
// ONLY the gold ring's own draw parameters -- the exact same radius/rotation/count values
// drawGlyphRings computes just below, from the same pure, already-tested geometry (coreRadius,
// js/hexadecagon.js; glyphRingAnnuli/glyphRingCountForRing/glyphRingGlyphBounds, js/glyph-rings.js)
// and the same RING_GLYPH_POOL (js/glyphs.js) the real sprite bake reads -- so
// js/see-through-hook.js can redraw the identical ring, at the identical position, through
// glyphs.js's own context-parameterized drawGlyphRing, without touching drawGlyphRings/
// drawOutlineGlyphRing above or duplicating any of their geometry.
function goldGlyphRingDrawParams(progress) {
  const r = coreRadius(Math.min(W, H));
  const annuli = glyphRingAnnuli(r);
  const gold = annuli[1];
  const targetWidth = (gold.outerRadius - gold.innerRadius) * GLYPH_RING_GOLD_SLIM_SIZE_FRACTION;
  const bounds = glyphRingGlyphBounds(gold, GLYPH_RING_GOLD_SLIM_SIZE_FRACTION);
  return {
    radius: (gold.innerRadius + gold.outerRadius) / 2,
    count: goldGlyphRingCount(gold, targetWidth),
    rotation: progress * TAU * GLYPH_RING_GOLD_ROTATION_SPEED,
    pool: RING_GLYPH_POOL,
    glyphSize: bounds.size,
    lineWidth: Math.max(GLYPH_RING_BODY_WIDTH_FLOOR_PX, targetWidth * GLYPH_RING_STROKE_WIDTH_FRACTION_GOLD),
  };
}

function drawGlyphRings(cx, cy, progress) {
  const r = coreRadius(Math.min(W, H));
  const annuli = glyphRingAnnuli(r);
  drawGlyphRingDelimiters(cx, cy, annuli);

  // RAP-20b: count is no longer a fixed config constant (it's derived from the ring's own
  // circumference at bake time — glyphRingCountFromCircumference, glyph-rings.js), so it's read
  // here from the actual baked sprite array length, always in sync with what was baked.
  const gold = annuli[1];
  drawOutlineGlyphRing(cx, cy, (gold.innerRadius + gold.outerRadius) / 2, sprites.outlineGlyphsGold.length, sprites.outlineGlyphsGold, progress * TAU * GLYPH_RING_GOLD_ROTATION_SPEED);

  const blue = annuli[3];
  drawOutlineGlyphRing(cx, cy, (blue.innerRadius + blue.outerRadius) / 2, sprites.outlineGlyphs.length, sprites.outlineGlyphs, progress * TAU * GLYPH_RING_BLUE_ROTATION_SPEED);
}

// RAP-13/21/26: both rings' glyphs are pre-baked outline-font sprites (js/sprites.js's
// paintTransformedOutlineGlyph, built in buildSprites); this only positions and stamps them, same
// angular layout glyphs.js's drawGlyphRing uses (even spacing, tangential orientation).
function drawOutlineGlyphRing(cx, cy, radius, count, spriteSet, rotation) {
  for (let i = 0; i < count; i++) {
    const angle = rotation + (i / count) * TAU;
    const x = cx + Math.cos(angle) * radius;
    const y = cy + Math.sin(angle) * radius;
    ctx.save();
    ctx.translate(x, y);
    ctx.rotate(glyphRingOrientationAngle(angle));
    stampSprite(spriteSet[i], 1, 1);
    ctx.restore();
  }
}

function drawFeathers(cx, cy, p, phase) {
  const size = Math.min(W, H);
  ctx.save();
  ctx.globalCompositeOperation = 'screen';
  for (const feather of FEATHER_FIELDS) {
    drawFeather(feather, featherState(cx, cy, p, phase, feather), size);
  }
  ctx.restore();
}

function fillCircle(x, y, radius) {
  ctx.beginPath();
  ctx.arc(x, y, radius, 0, TAU);
  ctx.fill();
}

function drawStars(cx, cy, p, phase) {
  ctx.save();
  ctx.globalCompositeOperation = 'screen';
  for (const s of stars) {
    const z = fract(s.z + p * s.speed * 0.24 * BACKGROUND_PARTICLE_SPEED_MULTIPLIER);
    const d = 0.018 + Math.pow(z, 1.82) * 1.18;
    const x = cx + Math.cos(s.a) * W * 0.77 * d * s.lane;
    const y = cy + Math.sin(s.a) * H * 0.91 * d;
    const tw = 0.32 + 0.68 * Math.sin(phase * 1.5 + s.twinkle) ** 2;
    const alpha = (0.08 + z * 0.78) * tw;
    const r = s.r * (0.42 + z * 1.38) * BACKGROUND_PARTICLE_SIZE_MULTIPLIER;
    const color = s.warm ? '255,246,192' : '238,250,255';
    ctx.fillStyle = `rgba(${color},${alpha})`;
    fillCircle(x, y, r);
    if (s.glint && z > 0.52) {
      const offset = (0.8 + z * 1.5) * BACKGROUND_PARTICLE_SIZE_MULTIPLIER;
      ctx.fillStyle = `rgba(58,220,255,${alpha * 0.48})`;
      fillCircle(x - offset, y, r);
      ctx.fillStyle = `rgba(255,86,158,${alpha * 0.42})`;
      fillCircle(x + offset, y, r);
    }
  }
  ctx.restore();
}


function perspectiveRayAngle(phase, ray, speed = FOREGROUND_INCLINED_RAY_SPEED) {
  return ray.angularOffset + phase * ray.angularSpeed * speed;
}

function perspectiveRayDirection(phase, ray, speed = FOREGROUND_INCLINED_RAY_SPEED) {
  const angle = perspectiveRayAngle(phase, ray, speed);
  // Foreshorten the vertical axis as a 45–60° plane would project onto the screen.
  return [Math.cos(angle), Math.sin(angle) * Math.sin(ray.inclination)];
}

function perspectiveRayEndpoint(cx, cy, phase, ray, width = W, height = H, zoom = viewZoom, speed = FOREGROUND_INCLINED_RAY_SPEED) {
  const [dx, dy] = perspectiveRayDirection(phase, ray, speed);
  const length = firstRayBoundaryLength(cx, cy, dx, dy, visibleCanvasBounds(cx, cy, width, height, zoom));
  return [cx + dx * length, cy + dy * length];
}

function perspectiveRayStart(cx, cy, phase, ray, speed = FOREGROUND_INCLINED_RAY_SPEED) {
  const [dx, dy] = perspectiveRayDirection(phase, ray, speed);
  const innerRadius = Math.min(W, H) * normalizedCentralCoreRadius(phase) * CENTRAL_CORE_RAY_INNER_RADIUS_FACTOR;
  return [cx + dx * innerRadius, cy + dy * innerRadius];
}

function drawPerspectiveRays(cx, cy, phase) {
  const segments = PERSPECTIVE_RAYS.map((ray) => {
    const [x1, y1] = perspectiveRayStart(cx, cy, phase, ray);
    const [x2, y2] = perspectiveRayEndpoint(cx, cy, phase, ray);
    return [x1, y1, x2, y2];
  });
  ctx.save();
  ctx.globalCompositeOperation = 'screen';
  for (let start = 0; start < segments.length; start += FOREGROUND_INCLINED_RAY_BATCH_SIZE) {
    drawGlowSegments(segments.slice(start, start + FOREGROUND_INCLINED_RAY_BATCH_SIZE), CENTRAL_RAY_LINE_WIDTH, CENTRAL_RAY_OPACITY, CENTRAL_RAY_GLOW_BLUR);
  }
  ctx.restore();
}

function drawRadialStreaks(cx, cy, p) {
  ctx.save();
  ctx.globalCompositeOperation = 'screen';
  ctx.lineCap = 'round';
  for (const s of radialStreaks) {
    const z = fract(s.z + p * s.speed * 0.64);
    if (z < 0.72) continue;
    const z0 = Math.max(0, z - 0.022 - s.speed * 0.014);
    const d1 = Math.pow(z, 2.18) * s.lane;
    const d0 = Math.pow(z0, 2.18) * s.lane;
    const x1 = cx + Math.cos(s.a) * W * 0.80 * d0;
    const y1 = cy + Math.sin(s.a) * H * 0.94 * d0;
    const x2 = cx + Math.cos(s.a) * W * 0.80 * d1;
    const y2 = cy + Math.sin(s.a) * H * 0.94 * d1;
    const width = s.width * RADIAL_STREAK_WIDTH_MULTIPLIER;
    const strokeAlpha = s.alpha * (z - 0.62) * 1.9;
    // Gradiente simétrico: rojo en las puntas, luego amarillo, verde y azul en el centro del trazo.
    const gradient = ctx.createLinearGradient(x1, y1, x2, y2);
    for (const [stop, rgb] of RADIAL_STREAK_GRADIENT) {
      gradient.addColorStop(stop, `rgb(${rgb})`);
      gradient.addColorStop(1 - stop, `rgb(${rgb})`);
    }
    ctx.strokeStyle = gradient;
    for (const [widthScale, layerAlpha] of RADIAL_STREAK_SOFT_LAYERS) {
      ctx.globalAlpha = strokeAlpha * layerAlpha;
      ctx.lineWidth = width * widthScale;
      ctx.beginPath();
      ctx.moveTo(x1, y1);
      ctx.lineTo(x2, y2);
      ctx.stroke();
    }
  }
  ctx.restore();
}

function drawSpectralFlare(cx, cy, radius, alpha, phase, index) {
  ctx.save();
  ctx.translate(cx, cy);
  ctx.globalCompositeOperation = 'screen';
  drawSpectralHalo(0, 0, radius * 1.72, alpha);
  stampSprite(sprites.flareBodies[index], 1, alpha);
  ctx.rotate(phase * 0.05);
  stampRainbowRing(sprites.flareRings[index], 1, alpha * 0.82);
  ctx.restore();
}

function spectralFlareState(cx, phase, index) {
  const flare = SPECTRAL_FLARES[index];
  const orbit = (opticalCycle(phase) + flare.orbitPhase) * TAU;
  return {
    x: cx + W * (flare.x - 0.505) + Math.cos(orbit) * W * flare.orbitX,
    y: H * flare.y + Math.sin(orbit) * H * flare.orbitY,
    radius: W * flare.radius,
    alpha: flare.alpha * opticalEnvelope(phase, flare.stagger),
    rotation: phase + flare.orbitPhase * TAU,
  };
}

function drawLensFlares(cx, cy, phase) {
  ctx.save();
  ctx.globalCompositeOperation = 'screen';
  for (let i = 0; i < SPECTRAL_FLARES.length; i++) {
    // Círculo cromático derecho (SPECTRAL_FLARES[0], x: 0.775) oculto. Borrá esta línea para mostrarlo de nuevo.
    if (i === 0) continue;
    const flare = spectralFlareState(cx, phase, i);
    drawSpectralFlare(flare.x, flare.y, flare.radius, flare.alpha, flare.rotation, i);
  }
  ctx.restore();
}

function chromaticLoopState(phase, index) {
  const loop = CHROMATIC_LOOP_GROUPS[index];
  const cycle = opticalCycle(phase);
  const orbit = (cycle + loop.stagger) * TAU;
  const depth = 0.5 + 0.5 * Math.sin(orbit + loop.depthPhase);
  const orbitScale = 0.98 + depth * 0.10;
  const ellipseScale = 0.74 + depth * 0.46;
  const selfRotation = Math.sin(cycle * Math.PI) * CHROMATIC_SELF_ROTATION * loop.spinDirection;
  return {
    x: W * 0.5 + Math.cos(orbit) * W * loop.orbitX * orbitScale,
    y: H * 0.5 + Math.sin(orbit) * H * loop.orbitY * orbitScale,
    rx: W * loop.rx * ellipseScale,
    ry: H * loop.ry * ellipseScale,
    rot: loop.rot + Math.sin(orbit) * 0.045 + selfRotation,
    alpha: loop.alpha * (0.56 + depth * 0.44),
    diffusion: 8 + depth * 11,
    orbitScale,
    selfRotation,
  };
}

function drawChromaticSideLoops(phase) {
  for (let i = 0; i < CHROMATIC_LOOP_GROUPS.length; i++) {
    const loop = CHROMATIC_LOOP_GROUPS[i];
    const state = chromaticLoopState(phase, i);
    chromaEllipse(state.x, state.y, state.rot, state.alpha, i, state.rx / (W * loop.rx));
  }
}

// RAP-8: the center prism/tetrahedron (rotate3/perspectiveScale/project3/tetrahedronState/
// tetrahedronProjectionMetrics/drawTriangularPrism and its camera/scale config constants) was
// removed — user: "hay que quitar el prisma del centro".

// The golden hexadecagon core: Raphael's 16-sided evolution of the inherited octagon. Vertex
// geometry and pulse timing live in js/hexadecagon.js (pure, unit-tested); this function only
// paints them as a thick bright gold/white-hot glowing ring. Per RAP-9 user feedback, the polygon
// itself stays a true regular 16-gon (js/hexadecagon.js's hexadecagonVertices) and grows only
// slightly with the pulse — intensity reads mainly through stroke width and glow blur here.
// Stroke width and chromatic offset are r-relative (not absolute pixels) so
// hexadecagonDrawnExtent's bound (the polygon must stay inside its own ring) holds at any
// viewport size; keep this function's math in exact sync with that pure function.
function drawGoldenHexadecagon(cx, cy, progress, pulse) {
  const r = coreRadius(Math.min(W, H));
  const rot = progress * TAU * 0.18;
  const pulseStroke = 1 + pulse * HEXADECAGON_PULSE_STROKE_FACTOR;
  const pulseBlur = HEXADECAGON_PULSE_BLUR_BASE + pulse * HEXADECAGON_PULSE_BLUR_RANGE;
  const vertices = hexadecagonVertices(r, pulse);
  const chromaticStrokeWidth = r * HEXADECAGON_STROKE_WIDTH_FACTOR * pulseStroke;
  const chromaticOffsetPx = r * HEXADECAGON_CHROMATIC_OFFSET_FACTOR;
  ctx.save();
  ctx.translate(cx, cy);
  ctx.rotate(rot);
  ctx.lineCap = 'round';
  ctx.lineJoin = 'round';

  const chromaticOffsets = [-chromaticOffsetPx, chromaticOffsetPx];
  for (let i = 0; i < chromaticOffsets.length; i++) {
    const offset = chromaticOffsets[i];
    ctx.strokeStyle = HEXADECAGON_CHROMATIC_COLORS[i];
    ctx.lineWidth = chromaticStrokeWidth;
    ctx.beginPath();
    for (let v = 0; v < vertices.length; v++) {
      const x = vertices[v].x + offset;
      const y = vertices[v].y;
      if (v === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    }
    ctx.closePath();
    ctx.stroke();
  }

  ctx.strokeStyle = HEXADECAGON_RING_CORE_COLOR;
  ctx.lineWidth = chromaticStrokeWidth * 1.3;
  ctx.shadowColor = HEXADECAGON_RING_GLOW_COLOR;
  ctx.shadowBlur = pulseBlur;
  ctx.beginPath();
  for (let v = 0; v < vertices.length; v++) {
    if (v === 0) ctx.moveTo(vertices[v].x, vertices[v].y); else ctx.lineTo(vertices[v].x, vertices[v].y);
  }
  ctx.closePath();
  ctx.stroke();
  ctx.restore();
}

function foldingBandParameters(progress) {
  const minD = Math.min(W, H);
  const phase = progress * TAU;
  const [outerBandSpeed, middleBandSpeed, innerBandSpeed] = FOLDING_BAND_SPEEDS;
  return [
    [minD * 0.385, minD * 0.255, -0.76 + phase * outerBandSpeed, minD * 0.025, phase * outerBandSpeed],
    [minD * 0.235, minD * 0.365,  0.36 + phase * middleBandSpeed, minD * 0.023, phase * middleBandSpeed + 0.9],
    [minD * 0.315, minD * 0.225,  0.10 + phase * innerBandSpeed, minD * 0.018, phase * innerBandSpeed + 1.8],
  ];
}

// RAP-24: fills a full circle with a radial gradient built from a [offset, alpha] stop list (see
// js/central-core.js's CENTRAL_CORE_*_STOPS) — every stop list is proven monotonically
// non-increasing and reaches ~0 alpha at its outer stop, so every layer painted this way has a
// smooth falloff with no hard edge, regardless of how many layers combine on top of each other.
function paintRadialGlowLayer(cx, cy, radius, stops, colorRgb, compositeOperation, shadow) {
  ctx.save();
  ctx.globalCompositeOperation = compositeOperation;
  const gradient = ctx.createRadialGradient(cx, cy, 0, cx, cy, radius);
  for (const [offset, alpha] of stops) {
    gradient.addColorStop(offset, `rgba(${colorRgb},${alpha})`);
  }
  ctx.fillStyle = gradient;
  if (shadow) {
    ctx.shadowColor = shadow.color;
    ctx.shadowBlur = shadow.blur;
  }
  ctx.beginPath();
  ctx.arc(cx, cy, radius, 0, TAU);
  ctx.fill();
  ctx.restore();
}

// RAP-24 (user feedback): "faint... starburst/glare streaks" — thin gold lines crossing the core,
// fading to nothing at both tips (CENTRAL_CORE_GLARE_FADE_STOPS), slowly rotating
// (centralCoreGlareStreakAngle), boosted subtly on each hexadecagon pulse (flare).
function drawCoreGlareStreaks(cx, cy, r, phase, flare) {
  ctx.save();
  ctx.globalCompositeOperation = 'screen';
  ctx.lineWidth = CENTRAL_CORE_GLARE_STREAK_WIDTH;
  const length = r * CENTRAL_CORE_GLARE_STREAK_LENGTH_FACTOR;
  for (let i = 0; i < CENTRAL_CORE_GLARE_STREAK_COUNT; i++) {
    const angle = centralCoreGlareStreakAngle(i, CENTRAL_CORE_GLARE_STREAK_COUNT, phase, CENTRAL_CORE_GLARE_ROTATION_SPEED);
    const x1 = cx - Math.cos(angle) * length;
    const y1 = cy - Math.sin(angle) * length;
    const x2 = cx + Math.cos(angle) * length;
    const y2 = cy + Math.sin(angle) * length;
    const gradient = ctx.createLinearGradient(x1, y1, x2, y2);
    for (const [offset, alpha] of CENTRAL_CORE_GLARE_FADE_STOPS) {
      gradient.addColorStop(offset, `rgba(${CENTRAL_CORE_GLARE_COLOR},${alpha * CENTRAL_CORE_GLARE_STREAK_ALPHA * flare})`);
    }
    ctx.strokeStyle = gradient;
    ctx.beginPath();
    ctx.moveTo(x1, y1);
    ctx.lineTo(x2, y2);
    ctx.stroke();
  }
  ctx.restore();
}

function drawCentralCore(cx, cy, phase, pulse) {
  const minD = Math.min(W, H);
  const r = minD * normalizedCentralCoreRadius(phase);

  // The optional prominent count controls the complete central spoke layer, including minor spokes.
  if (CENTRAL_CORE_PROMINENT_RAY_COUNT > 0) {
    const prominentSegments = [];
    for (let i = 0; i < CENTRAL_CORE_PROMINENT_RAY_COUNT; i++) {
      const a = i * TAU / CENTRAL_CORE_PROMINENT_RAY_COUNT + Math.sin(phase) * 0.04;
      const desiredLength = r * mix(
        CENTRAL_CORE_PROMINENT_MIN_REACH_FACTOR,
        CENTRAL_CORE_PROMINENT_MAX_REACH_FACTOR,
        prominentRayGrowthEnvelope(phase, i)
      );
      const l = Math.min(desiredLength, centralCoreRaySafeLength(cx, cy, a));
      prominentSegments.push([
        cx + Math.cos(a) * r * CENTRAL_CORE_RAY_INNER_RADIUS_FACTOR,
        cy + Math.sin(a) * r * CENTRAL_CORE_RAY_INNER_RADIUS_FACTOR,
        cx + Math.cos(a) * l,
        cy + Math.sin(a) * l,
      ]);
    }
    drawGlowSegments(prominentSegments, CENTRAL_RAY_LINE_WIDTH, CENTRAL_RAY_OPACITY, CENTRAL_RAY_GLOW_BLUR);

    const minorSegments = [];
    for (let i = 0; i < CENTRAL_CORE_MINOR_RAY_COUNT; i++) {
      const a = (i + 0.5) * TAU / CENTRAL_CORE_MINOR_RAY_COUNT - Math.sin(phase) * 0.025;
      const l = r * CENTRAL_CORE_MINOR_RAY_FACTOR;
      minorSegments.push([
        cx + Math.cos(a) * r * 0.20,
        cy + Math.sin(a) * r * 0.20,
        cx + Math.cos(a) * l,
        cy + Math.sin(a) * l,
      ]);
    }
    drawGlowSegments(minorSegments, 0.70, 0.22, 5);
  }

  // RAP-14 (user feedback): "el centro ya no es luz blanca, debe ser dorada" — warm gold/amber,
  // white-hot at most only at the very center (CENTRAL_CORE_DISC_COLOR).
  // RAP-24 (user feedback): "La luz del centro: dale más efecto de luz, se ve como un círculo
  // amarillo" — replaces the old single flat gradient + near-opaque disc (which read as a flat
  // yellow circle) with: a strong soft multi-layer bloom (two additive 'lighter' radial layers,
  // wide-then-tight, each smoothly falling off to ~0 with no hard edge), faint slowly-rotating
  // glare streaks, and a very small white-gold hot core — all subtly boosted in sync with the
  // hexadecagon's own pulse.
  const flare = centralCoreFlareEnvelope(pulse);
  paintRadialGlowLayer(cx, cy, r * CENTRAL_CORE_BLOOM_OUTER_RADIUS_FACTOR * flare, CENTRAL_CORE_BLOOM_OUTER_STOPS, CENTRAL_CORE_GLOW_COLOR_OUTER, 'lighter');
  paintRadialGlowLayer(cx, cy, r * CENTRAL_CORE_BLOOM_INNER_RADIUS_FACTOR * flare, CENTRAL_CORE_BLOOM_INNER_STOPS, CENTRAL_CORE_GLOW_COLOR_MID, 'lighter');
  // A tight near-white-hot halo immediately around the hot core itself (CENTRAL_CORE_GLOW_COLOR_INNER,
  // #fff3c4), between the amber/gold bloom above and the white-hot center below.
  paintRadialGlowLayer(cx, cy, r * CENTRAL_CORE_HOT_RADIUS_FACTOR * 1.8 * flare, CENTRAL_CORE_HOT_STOPS, CENTRAL_CORE_GLOW_COLOR_INNER, 'lighter');
  drawCoreGlareStreaks(cx, cy, r, phase, flare);
  paintRadialGlowLayer(cx, cy, r * CENTRAL_CORE_HOT_RADIUS_FACTOR, CENTRAL_CORE_HOT_STOPS, CENTRAL_CORE_DISC_COLOR, 'lighter',
    { color: `rgba(${CENTRAL_CORE_DISC_GLOW_COLOR},0.85)`, blur: 20 * flare });
}

// RAP-6/RAP-11: two persistent glyph-style vertical counters on the left/right viewport edges,
// laid out like the Ctrl+E/Ctrl+A error layer's bit-counter modules (js/failure-overlay.js's
// drawFailureModules) but always visible, using procedural digit glyphs (js/digits.js) instead of
// a monospace font. Drawn outside the zoomed/translated scene transform, like drawVignette, so the
// counters stay pinned to the viewport edges regardless of user zoom.
//
// RAP-11 (user feedback): "¿dónde están los contadores? ... también agrégale el background." Each
// counter now sits on its own near-opaque dark panel (drawGlyphCounterPanel) with larger, bold,
// pale-cream glyphs sized to the panel's own width (js/digits.js's glyphCounterGlyphSize) plus a
// subtle chromatic fringe, instead of small gold glyphs floating directly on the busy scene.
// RAP-18 (review advisory fix): CanvasRenderingContext2D.roundRect is not universally available;
// calling it unconditionally would throw inside the RAF loop on an engine without it. Feature-
// checked here once per call (cheap), with a manual arcTo-based fallback path.
function pathRoundedRect(context, x, y, width, height, radius) {
  if (typeof context.roundRect === 'function') {
    context.roundRect(x, y, width, height, radius);
    return;
  }
  const r = Math.min(radius, width / 2, height / 2);
  context.moveTo(x + r, y);
  context.lineTo(x + width - r, y);
  context.arcTo(x + width, y, x + width, y + r, r);
  context.lineTo(x + width, y + height - r);
  context.arcTo(x + width, y + height, x + width - r, y + height, r);
  context.lineTo(x + r, y + height);
  context.arcTo(x, y + height, x, y + height - r, r);
  context.lineTo(x, y + r);
  context.arcTo(x, y, x + r, y, r);
  context.closePath();
}

function drawGlyphCounterPanel(rect) {
  ctx.save();
  ctx.shadowColor = GLYPH_COUNTER_PANEL_SHADOW_COLOR;
  ctx.shadowBlur = GLYPH_COUNTER_PANEL_SHADOW_BLUR;
  ctx.fillStyle = GLYPH_COUNTER_PANEL_COLOR;
  ctx.beginPath();
  pathRoundedRect(ctx, rect.x, rect.y, rect.width, rect.height, GLYPH_COUNTER_PANEL_CORNER_RADIUS);
  ctx.fill();
  ctx.restore();
}

// Three passes (faint red, faint cyan, the main cream glyph) at a 1px-ish horizontal offset, same
// pattern as drawGoldenHexadecagon's chromatic offset copies, for the reference's subtle fringe.
function drawGlyphCounterColumn(rect, side, digits, glyphSize) {
  const columnX = rect.x + rect.width / 2;
  const spacing = rect.height / (digits.length + 1);
  const rotation = side === 'left' ? -Math.PI / 2 : Math.PI / 2;
  for (const [offsetX, color] of [
    [-GLYPH_COUNTER_CHROMATIC_OFFSET, GLYPH_COUNTER_CHROMATIC_RED],
    [GLYPH_COUNTER_CHROMATIC_OFFSET, GLYPH_COUNTER_CHROMATIC_CYAN],
    [0, `rgba(${GLYPH_COUNTER_GLYPH_COLOR},0.96)`],
  ]) {
    digits.forEach((entry, index) => {
      const strokes = entry === 'separator' ? DIGIT_SEPARATOR_GLYPH : DIGIT_GLYPHS[entry];
      const y = rect.y + spacing * (index + 1);
      drawHieroglyph(ctx, strokes, columnX + offsetX, y, glyphSize, rotation, color, GLYPH_COUNTER_LINE_WIDTH);
    });
  }
}

function drawGlyphCounters(ms) {
  const counterA = Math.floor(ms / GLYPH_COUNTER_STEP_MS) % 10 ** GLYPH_COUNTER_GROUP_DIGIT_COUNT;
  const counterB = Math.floor(ms / GLYPH_COUNTER_STEP_MS_B) % 10 ** GLYPH_COUNTER_GROUP_DIGIT_COUNT;
  const digits = formatPanelDigits(counterA, counterB);
  for (const side of ['left', 'right']) {
    const rect = glyphCounterPanelRect(side, W, H);
    drawGlyphCounterPanel(rect);
    drawGlyphCounterColumn(rect, side, digits, glyphCounterGlyphSize(rect.width, rect.height, digits.length));
  }
}

function drawVignette() {
  const v = ctx.createRadialGradient(
    W * 0.5, H * 0.5, Math.min(W, H) * 0.20,
    W * 0.5, H * 0.5, Math.max(W, H) * 0.72
  );
  v.addColorStop(0.00, 'rgba(0,0,0,0)');
  v.addColorStop(0.64, 'rgba(0,0,0,0.04)');
  v.addColorStop(1.00, 'rgba(0,0,0,0.38)');
  ctx.fillStyle = v;
  ctx.fillRect(0, 0, W, H);
}

function drawFilmGrain(phase) {
  ctx.save();
  ctx.globalCompositeOperation = 'screen';
  for (const grain of filmGrain) {
    const x = fract(grain.x + Math.sin(phase + grain.phase) * 0.018) * W;
    const y = fract(grain.y + Math.cos(phase + grain.phase) * 0.014) * H;
    ctx.fillStyle = `rgba(218,245,235,${grain.alpha})`;
    ctx.fillRect(x, y, grain.size, grain.size);
  }
  ctx.restore();
}
