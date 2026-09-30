// html-wallpaper-demo D2: verbatim port of docs/great-sage/backgroud-processing/js/layers.js (that
// tree is reference-only, excluded from git -- see the feature doc, "Source material"). Not
// restyled: only this header comment was added, the source's own header/body follow unchanged.
// foldingBandParameters/foldingBandGeometry/fillFoldingBandGeometry/drawAtomicOrbits below are the
// REAL band geometry -- js/alert-overlay.js's band-intersection layer calls the exact same
// functions, with the exact same progress `p` the scene's own drawAtomicOrbits call uses this
// frame, so what shows through the FAILED/WARNING letters is the real animation, not a copy.
//
// layers.js — the scene's visual layers: soft ovals, stars, radial streaks, lens flares,
// chromatic side loops, segmented-sphere piece drawing, orbit blocks, the tetrahedron,
// the central octagon, atomic orbit bands, the central core, film grain, and vignette.
// Loads after config.js, math.js, sphere.js, scene-data.js, and sprites.js (calls
// stampGlow/stampSprite/stampGlowLine and reads the seeded descriptor arrays).
//
// A path with moveTo/lineTo pairs preserves independent segments while sharing one shadowed stroke.
function drawGlowSegments(segments, width, alpha, blur = 8) {
  if (segments.length === 0) return;
  ctx.save();
  ctx.strokeStyle = `rgba(${CENTRAL_RAY_STROKE_COLOR},${alpha})`;
  ctx.lineWidth = structurePx(width, 0.5);
  ctx.lineCap = 'round';
  ctx.shadowColor = CENTRAL_RAY_GLOW_COLOR;
  ctx.shadowBlur = structurePx(blur, 1);
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

function drawFoldingBand(cx, cy, rx, ry, rot, width, foldPhase) {
  const points = foldingBandGeometry(rx, ry, width, foldPhase);

  ctx.save();
  ctx.translate(cx, cy);
  ctx.rotate(rot);
  ctx.lineCap = 'round';
  ctx.lineJoin = 'round';

  for (let i = 0; i < points.length - 1; i++) {
    const a = points[i];
    const b = points[i + 1];
    const front = (a.front + b.front) * 0.5;
    ctx.fillStyle = 'rgb(255,255,255)';
    ctx.beginPath();
    ctx.moveTo(a.left[0], a.left[1]);
    ctx.lineTo(b.left[0], b.left[1]);
    ctx.lineTo(b.right[0], b.right[1]);
    ctx.lineTo(a.right[0], a.right[1]);
    ctx.closePath();
    ctx.fill();

    ctx.strokeStyle = `rgba(18,36,44,${0.08 + (1 - front) * 0.22})`;
    ctx.lineWidth = Math.max(structurePx(0.75, 0.4), width * 0.045);
    ctx.beginPath();
    ctx.moveTo(a.right[0], a.right[1]);
    ctx.lineTo(b.right[0], b.right[1]);
    ctx.stroke();
  }

  ctx.shadowColor = 'rgba(255,255,255,0.78)';
  ctx.shadowBlur = structurePx(12, 1);
  ctx.lineWidth = Math.max(structurePx(1.15, 0.5), width * 0.11);
  ctx.strokeStyle = 'rgb(255,255,255)';
  ctx.beginPath();
  ctx.moveTo(points[0].left[0], points[0].left[1]);
  for (let i = 1; i < points.length; i++) ctx.lineTo(points[i].left[0], points[i].left[1]);
  for (let i = 0; i < points.length; i++) ctx.lineTo(points[i].right[0], points[i].right[1]);
  ctx.closePath();
  ctx.stroke();
  ctx.restore();
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

function segmentedSpherePieceDimensions(descriptor, minD, projectedDepth = descriptor.projectedDepth) {
  const depthScale = 0.86 + (projectedDepth + 1) * 0.10;
  const styleScale = descriptor.style === 'solid-square' ? SOLID_SQUARE_SIZE_MULTIPLIER : 1;
  const width = minD * SEGMENTED_SPHERE_BASE_WIDTH * descriptor.sizeVariation * depthScale * styleScale;
  return {
    width,
    height: descriptor.style === 'solid-square' ? width : width / SPHERE_RECTANGLE_ASPECT_RATIO,
  };
}

function drawSolidSquareSpherePiece(x, y, dimensions, state) {
  const fillAlpha = (SOLID_SQUARE_SPHERE_ALPHA - state.travel * 0.10) * state.depthAlpha;
  const strokeAlpha = SOLID_SQUARE_SPHERE_STROKE_ALPHA * state.depthAlpha;
  ctx.save();
  ctx.translate(x, y);
  ctx.rotate(state.rotation);
  stampGlow('solid-fill', dimensions.width, dimensions.height, SEGMENTED_SPHERE_GLOW_ALPHA * fillAlpha);
  stampGlow('solid-stroke', dimensions.width, dimensions.height, SEGMENTED_SPHERE_GLOW_ALPHA * strokeAlpha);
  ctx.globalAlpha = 1;
  ctx.fillStyle = `rgba(255,255,255,${fillAlpha})`;
  ctx.fillRect(-dimensions.width / 2, -dimensions.height / 2, dimensions.width, dimensions.height);
  ctx.strokeStyle = `rgba(255,255,255,${strokeAlpha})`;
  ctx.lineWidth = structurePx(1.0, 0.5);
  ctx.strokeRect(-dimensions.width / 2, -dimensions.height / 2, dimensions.width, dimensions.height);
  ctx.restore();
}

function drawOutlineRectangleSpherePiece(x, y, dimensions, state) {
  const strokeAlpha = (OUTLINE_RECTANGLE_SPHERE_STROKE_ALPHA - state.travel * 0.16) * state.depthAlpha;
  ctx.save();
  ctx.translate(x, y);
  ctx.rotate(state.rotation);
  stampGlow('outline-stroke', dimensions.width, dimensions.height, SEGMENTED_SPHERE_GLOW_ALPHA * strokeAlpha);
  ctx.globalAlpha = 1;
  ctx.strokeStyle = `rgba(218,250,255,${strokeAlpha})`;
  ctx.lineWidth = structurePx(0.8, 0.5);
  ctx.strokeRect(-dimensions.width / 2, -dimensions.height / 2, dimensions.width, dimensions.height);
  ctx.restore();
}

function griddedRectangleInternalLineCoordinates(dimensions) {
  const left = -dimensions.width / 2;
  const right = dimensions.width / 2;
  const top = -dimensions.height / 2;
  const bottom = dimensions.height / 2;
  return {
    left,
    right,
    top,
    bottom,
    vertical: Array.from({ length: GRIDDED_RECTANGLE_VERTICAL_LINE_COUNT }, (_, index) =>
      left + (index + 1) / (GRIDDED_RECTANGLE_VERTICAL_LINE_COUNT + 1) * dimensions.width
    ),
    horizontal: Array.from({ length: GRIDDED_RECTANGLE_HORIZONTAL_LINE_COUNT }, (_, index) =>
      top + (index + 1) / (GRIDDED_RECTANGLE_HORIZONTAL_LINE_COUNT + 1) * dimensions.height
    ),
  };
}

function drawGriddedRectangleSpherePiece(x, y, dimensions, state) {
  const grid = griddedRectangleInternalLineCoordinates(dimensions);
  const fillAlpha = (GRIDDED_RECTANGLE_SPHERE_FILL_ALPHA - state.travel * 0.08) * state.depthAlpha;
  const strokeAlpha = (GRIDDED_RECTANGLE_SPHERE_STROKE_ALPHA - state.travel * 0.12) * state.depthAlpha;
  ctx.save();
  ctx.translate(x, y);
  ctx.rotate(state.rotation);
  stampGlow('gridded-fill', dimensions.width, dimensions.height, SEGMENTED_SPHERE_GLOW_ALPHA * fillAlpha);
  stampGlow('gridded-stroke', dimensions.width, dimensions.height, SEGMENTED_SPHERE_GLOW_ALPHA * strokeAlpha);
  ctx.globalAlpha = 1;
  ctx.fillStyle = `rgba(255,255,255,${fillAlpha})`;
  ctx.fillRect(grid.left, grid.top, dimensions.width, dimensions.height);
  ctx.strokeStyle = `rgba(255,255,255,${strokeAlpha})`;
  ctx.lineWidth = structurePx(0.72, 0.5);
  ctx.strokeRect(grid.left, grid.top, dimensions.width, dimensions.height);
  ctx.beginPath();
  for (const xCoordinate of grid.vertical) {
    ctx.moveTo(xCoordinate, grid.top);
    ctx.lineTo(xCoordinate, grid.bottom);
  }
  for (const yCoordinate of grid.horizontal) {
    ctx.moveTo(grid.left, yCoordinate);
    ctx.lineTo(grid.right, yCoordinate);
  }
  ctx.stroke();
  ctx.restore();
}

function drawSegmentedSphere(cx, cy, progress) {
  const minD = Math.min(W, H);
  ctx.save();
  ctx.globalCompositeOperation = 'screen';
  for (const descriptor of SEGMENTED_SPHERE_DESCRIPTORS) {
    const state = segmentedSpherePieceState(progress, descriptor);
    const dimensions = segmentedSpherePieceDimensions(descriptor, minD, state.projectedDepth);
    const x = cx + state.normalizedPosition[0] * minD;
    const y = cy + state.normalizedPosition[1] * minD;
    if (descriptor.style === 'solid-square') {
      drawSolidSquareSpherePiece(x, y, dimensions, state);
    } else if (descriptor.style === 'outline-rectangle') {
      drawOutlineRectangleSpherePiece(x, y, dimensions, state);
    } else {
      drawGriddedRectangleSpherePiece(x, y, dimensions, state);
    }
  }
  ctx.restore();
}

function orbitBlockState(cx, cy, phase, block) {
  const ring = ORBIT_BLOCK_RINGS[block.ring];
  const angle = block.phase + phase * ORBIT_BLOCK_RING_SPEEDS[block.ring];
  const baseR = Math.min(W, H) * 0.205;
  const rx = baseR * ring.radiusX * block.radialJitter;
  const ry = baseR * ring.radiusY * block.radialJitter;
  const localX = Math.cos(angle) * rx;
  const localY = Math.sin(angle) * ry;
  const planeCos = Math.cos(ring.planeTilt);
  const planeSin = Math.sin(ring.planeTilt);
  const front = 0.5 + 0.5 * Math.sin(angle);
  return {
    x: cx + localX * planeCos - localY * planeSin,
    y: cy + localX * planeSin + localY * planeCos,
    angle,
    front,
    size: structurePx(block.size, 1.5) * (0.90 + front * 0.74),
    opacity: 0.50 + front * 0.40,
    rotation: angle + ring.planeTilt + block.tilt,
  };
}

function drawOrbitBlocks(cx, cy, phase) {
  ctx.save();
  for (const block of orbitBlocks) {
    const state = orbitBlockState(cx, cy, phase, block);

    ctx.save();
    ctx.translate(state.x, state.y);
    ctx.rotate(state.rotation);
    ctx.fillStyle = `rgba(255,252,238,${state.opacity})`;
    stampGlow('orbit-block', state.size * 1.64, state.size * 1.08, ORBIT_BLOCK_GLOW_ALPHA * state.opacity);
    ctx.globalAlpha = 1;
    ctx.fillRect(-state.size * 0.82, -state.size * 0.54, state.size * 1.64, state.size * 1.08);
    ctx.restore();
  }
  ctx.restore();
}

function rotate3(v, ax, ay, az) {
  let [x, y, z] = v;

  let y1 = y * Math.cos(ax) - z * Math.sin(ax);
  let z1 = y * Math.sin(ax) + z * Math.cos(ax);
  let x1 = x;

  let x2 = x1 * Math.cos(ay) + z1 * Math.sin(ay);
  let z2 = -x1 * Math.sin(ay) + z1 * Math.cos(ay);
  let y2 = y1;

  let x3 = x2 * Math.cos(az) - y2 * Math.sin(az);
  let y3 = x2 * Math.sin(az) + y2 * Math.cos(az);
  return [x3, y3, z2];
}

function perspectiveScale(z) {
  const denominator = Math.max(CAMERA_DENOMINATOR_MIN, CAMERA_DISTANCE - z);
  return 2.7 / denominator;
}

function project3(v, cx, cy, scale) {
  const f = perspectiveScale(v[2]);
  return [cx + v[0] * scale * f, cy + v[1] * scale * f, f];
}

const TETRAHEDRON_VERTICES = [
  [0.00, -1.20, 0.00],
  [-1.05, 0.72, -0.78],
  [1.05, 0.72, -0.78],
  [0.00, 0.72, 1.05],
];
const TETRAHEDRON_EDGES = [[0,1], [0,2], [0,3], [1,2], [2,3], [3,1]];

function tetrahedronState(progress, minD = Math.min(W, H)) {
  const approach = smoothstep(0, 1, pingpong01(progress));
  const rotationPhase = progress * TAU * TRIANGULAR_PRISM_ROTATION_SPEED;
  const ax = 0.86 + Math.sin(rotationPhase) * 0.13;
  const ay = 0.48 + rotationPhase;
  const az = -0.10 + Math.cos(rotationPhase) * 0.10;
  const cameraTravel = mix(TETRAHEDRON_CAMERA_FAR, TETRAHEDRON_CAMERA_NEAR, approach);
  return {
    desiredScale: minD * mix(TETRAHEDRON_FAR_SCALE, TETRAHEDRON_NEAR_SCALE, approach),
    vertices: TETRAHEDRON_VERTICES.map((v) => {
      const rotated = rotate3(v, ax, ay, az);
      return [rotated[0], rotated[1], rotated[2] + cameraTravel];
    }),
  };
}

function tetrahedronProjectionMetrics(width, height, progress) {
  const cx = width * 0.505;
  const cy = height * 0.515;
  const state = tetrahedronState(progress, Math.min(width, height));
  const safeMargin = Math.max(14, Math.min(width, height) * 0.035);
  const xLimit = Math.max(1, Math.min(cx, width - cx) - safeMargin);
  const yLimit = Math.max(1, Math.min(cy, height - cy) - safeMargin);
  const maxProjectedX = Math.max(...state.vertices.map((v) => Math.abs(v[0]) * perspectiveScale(v[2])));
  const maxProjectedY = Math.max(...state.vertices.map((v) => Math.abs(v[1]) * perspectiveScale(v[2])));
  const safeScale = Math.min(xLimit / maxProjectedX, yLimit / maxProjectedY);
  const scale = Math.min(state.desiredScale, safeScale);
  const project = (s) => state.vertices.map((v) => project3(v, cx, cy, s));
  const measuredSize = (points) => {
    const xs = points.map((point) => point[0]);
    const ys = points.map((point) => point[1]);
    return Math.hypot(Math.max(...xs) - Math.min(...xs), Math.max(...ys) - Math.min(...ys));
  };
  const result = {
    safeMargin,
    points: project(scale),
    unclampedSize: measuredSize(project(state.desiredScale)),
  };
  return result;
}

function drawCentralOctagon(cx, cy, progress, pulse) {
  const r = Math.min(W, H) * 0.168;
  const rot = progress * TAU * 0.18;
  const pulseStroke = 1 + pulse * 0.72;
  // Mini: the octagon's line is exactly MINI_POLYGON_STROKE_PX thick (as thin as raphael's hexadecagon); its
  // chroma copies, offsets and glow keep their full-scene proportions to that stroke.
  const miniK = isMiniVariant ? MINI_POLYGON_STROKE_PX / 5.1 : 1;
  const pulseBlur = (20 + pulse * 18) * miniK;
  ctx.save();
  ctx.translate(cx, cy);
  ctx.rotate(rot);
  ctx.lineCap = 'round';
  ctx.lineJoin = 'round';

  for (const [offset, color] of [
    [-2.1 * miniK, 'rgba(40,220,255,0.20)'],
    [ 2.1 * miniK, 'rgba(255,72,120,0.16)'],
  ]) {
    ctx.strokeStyle = color;
    ctx.lineWidth = 6.8 * miniK * pulseStroke;
    ctx.beginPath();
    for (let i = 0; i < 8; i++) {
      const a = -Math.PI / 2 + i * TAU / 8;
      const wobble = pulse * r * 0.025 * Math.sin(a * 3 + progress * TAU);
      const x = Math.cos(a) * (r + wobble) + offset;
      const y = Math.sin(a) * (r + wobble);
      if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    }
    ctx.closePath();
    ctx.stroke();
  }

  ctx.strokeStyle = 'rgba(255,255,244,0.95)';
  ctx.lineWidth = 5.1 * miniK * pulseStroke;
  ctx.shadowColor = 'rgba(255,255,245,0.90)';
  ctx.shadowBlur = pulseBlur;
  ctx.beginPath();
  for (let i = 0; i < 8; i++) {
    const a = -Math.PI / 2 + i * TAU / 8;
    const wobble = pulse * r * 0.025 * Math.sin(a * 3 + progress * TAU);
    const x = Math.cos(a) * (r + wobble);
    const y = Math.sin(a) * (r + wobble);
    if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
  }
  ctx.closePath();
  ctx.stroke();
  ctx.restore();
}

function drawTriangularPrism(cx, cy, progress) {
  // Tetrahedron / triangular pyramid: all four faces are triangles.
  // The established function name is retained for the render call site.
  const state = tetrahedronState(progress);
  const safeMargin = Math.max(structurePx(14, 3), Math.min(W, H) * 0.035);
  const xLimit = Math.max(1, Math.min(cx, W - cx) - safeMargin);
  const yLimit = Math.max(1, Math.min(cy, H - cy) - safeMargin);
  const maxProjectedX = Math.max(...state.vertices.map((v) => Math.abs(v[0]) * perspectiveScale(v[2])));
  const maxProjectedY = Math.max(...state.vertices.map((v) => Math.abs(v[1]) * perspectiveScale(v[2])));
  const safeScale = Math.min(xLimit / maxProjectedX, yLimit / maxProjectedY);
  const scale = Math.min(state.desiredScale, safeScale);
  const pts = state.vertices.map((v) => project3(v, cx, cy, scale));
  const edges = TETRAHEDRON_EDGES;

  ctx.save();
  ctx.globalCompositeOperation = 'screen';
  ctx.lineCap = 'round';
  ctx.lineJoin = 'round';

  // Aberración cromática sutil.
  for (const [offX, offY, color] of [
    [-structurePx(1.7, 0.3), structurePx(0.6, 0.1), 'rgba(40,220,255,0.22)'],
    [ structurePx(1.7, 0.3), -structurePx(0.6, 0.1), 'rgba(255,72,120,0.18)'],
  ]) {
    ctx.strokeStyle = color;
    ctx.lineWidth = structurePx(3.8, 0.5);
    for (const [a, b] of edges) {
      ctx.beginPath();
      ctx.moveTo(pts[a][0] + offX, pts[a][1] + offY);
      ctx.lineTo(pts[b][0] + offX, pts[b][1] + offY);
      ctx.stroke();
    }
  }

  ctx.strokeStyle = 'rgba(255,255,244,0.88)';
  ctx.lineWidth = structurePx(3.2, 0.5);
  for (const [a, b] of edges) {
    const depthAlpha = clamp01((pts[a][2] + pts[b][2]) * 0.46);
    const edgeAlpha = 0.54 + depthAlpha * 0.40;
    stampGlowLine('prism-edge', structurePx(3.2, 0.5), 14 / 3.2, pts[a][0], pts[a][1], pts[b][0], pts[b][1], 0.68 * 0.88 * edgeAlpha);
    ctx.globalAlpha = edgeAlpha;
    ctx.beginPath();
    ctx.moveTo(pts[a][0], pts[a][1]);
    ctx.lineTo(pts[b][0], pts[b][1]);
    ctx.stroke();
  }
  ctx.restore();
}

function foldingBandParameters(progress) {
  const minD = Math.min(W, H);
  const phase = progress * TAU;
  const [outerBandSpeed, middleBandSpeed, innerBandSpeed] = FOLDING_BAND_SPEEDS;
  const thin = isMiniVariant ? MINI_FOLDING_BAND_WIDTH_FACTOR : 1; // T2m: mini bands are thinner, nothing else changes
  return [
    [minD * 0.385, minD * 0.255, -0.76 + phase * outerBandSpeed, minD * 0.025 * thin, phase * outerBandSpeed],
    [minD * 0.235, minD * 0.365,  0.36 + phase * middleBandSpeed, minD * 0.023 * thin, phase * middleBandSpeed + 0.9],
    [minD * 0.315, minD * 0.225,  0.10 + phase * innerBandSpeed, minD * 0.018 * thin, phase * innerBandSpeed + 1.8],
  ];
}

function drawAtomicOrbits(cx, cy, progress) {
  // Keep every band's inner edge outside the octagon so its silhouette stays readable.
  for (const [rx, ry, rot, width, foldPhase] of foldingBandParameters(progress)) {
    drawFoldingBand(cx, cy, rx, ry, rot, width, foldPhase);
  }
}

function drawCentralCore(cx, cy, phase) {
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

  const g = ctx.createRadialGradient(cx, cy, 0, cx, cy, r * 5.1);
  g.addColorStop(0.00, 'rgba(255,255,247,1.00)');
  g.addColorStop(0.16, 'rgba(255,255,242,0.98)');
  g.addColorStop(0.30, 'rgba(240,255,245,0.72)');
  g.addColorStop(0.55, 'rgba(120,255,230,0.28)');
  g.addColorStop(1.00, 'rgba(40,220,220,0)');
  ctx.fillStyle = g;
  ctx.beginPath();
  ctx.arc(cx, cy, r * 5.1, 0, TAU);
  ctx.fill();

  ctx.fillStyle = 'rgba(255,255,245,0.96)';
  ctx.shadowColor = 'rgba(255,255,245,0.80)';
  ctx.shadowBlur = structurePx(20, 1);
  ctx.beginPath();
  ctx.arc(cx, cy, r * 0.88, 0, TAU);
  ctx.fill();
  ctx.shadowBlur = 0;
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
