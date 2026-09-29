// html-wallpaper-demo D6a: copied verbatim from docs/great-sage/background-explorer/js/rising-sparks.js
// (reference-only, excluded from git -- see the feature doc, "Source material"). Not restyled:
// only this header comment was added, the source own header/body follow unchanged.

// rising-sparks.js — the explorer variant's blue layer and its field of rising star sparks.
// Loads after config.js and math.js (uses mulberry32, clamp01, smoothstep, mix). Stateless:
// every spark is derived from (slot index, time), so the field loops forever with no bookkeeping.
//
// Each of RISING_SPARK_COUNT slots owns a fixed respawn period. Within one period the slot's spark
// lives for `lifetime` seconds, then rests briefly and is replaced by a fresh spark whose origin,
// speed, and incline are rehashed from (slot, cycle). The spark rises at a constant speed; its
// horizontal velocity holds steady (an almost straight, inclined path) until BEND_START, then
// eases to zero by BEND_END so the path bends to straight up before the spark fades out.

function risingSparkRandom(index, cycle) {
  return mulberry32((RISING_SPARK_SEED ^ Math.imul(index + 1, 0x9e3779b1) ^ Math.imul(cycle + 1, 0x85ebca6b)) >>> 0);
}

function risingSparkAt(index, timeSeconds, width, height) {
  const slotRandom = risingSparkRandom(index, -1);
  const lifetime = mix(RISING_SPARK_LIFETIME_MIN_SECONDS, RISING_SPARK_LIFETIME_MAX_SECONDS, slotRandom());
  const period = lifetime * (1 + RISING_SPARK_REST_FRACTION);
  const localTime = timeSeconds + slotRandom() * period; // stagger slots so they never pulse together
  const cycle = Math.floor(localTime / period);
  const age = localTime - cycle * period;

  const random = risingSparkRandom(index, cycle);
  const x0 = random();
  const y0 = mix(RISING_SPARK_SPAWN_TOP_FRACTION, 1.02, random());
  const offCenter = (0.5 - x0) * 2; // -1 at the right edge, +1 at the left edge
  const tilt = Math.sign(offCenter || 1)
    * (RISING_SPARK_TILT_BASE + RISING_SPARK_TILT_EDGE * Math.abs(offCenter))
    * mix(0.8, 1, random());
  const bendJitter = (random() - 0.5) * 0.1;
  return {
    index,
    cycle,
    age,
    lifetime,
    period,
    x0,
    y0,
    speed: mix(RISING_SPARK_SPEED_MIN, RISING_SPARK_SPEED_MAX, random()),
    tilt,
    bendStart: RISING_SPARK_BEND_START + bendJitter,
    bendEnd: RISING_SPARK_BEND_END + bendJitter,
    size: mix(0.6, 1.4, random()),
    brightness: mix(0.45, 1, random()),
  };
}

// Normalized horizontal travel at life fraction u: the integral of (1 - smoothstep(a, b, s)) ds
// from 0 to u, i.e. full-speed drift before the bend, eased drift through it, none after.
function risingSparkDrift(u, a, b) {
  const span = b - a;
  const t = clamp01((u - a) / span);
  const easedAway = span * (t * t * t - (t * t * t * t) / 2) + Math.max(0, u - b);
  return u - easedAway;
}

function risingSparkPosition(spark, age, width, height) {
  const rise = spark.speed * height * age;
  const u = age / spark.lifetime;
  const drift = spark.tilt * spark.speed * height * spark.lifetime * risingSparkDrift(u, spark.bendStart, spark.bendEnd);
  return { x: spark.x0 * width + drift, y: spark.y0 * height - rise };
}

function risingSparkEnvelope(ageFraction) {
  return smoothstep(0, 0.1, ageFraction) * (1 - smoothstep(0.55, 1, ageFraction));
}

const RISING_SPARK_TRAIL_SAMPLES = 6;

function drawRisingSparks(context, timeSeconds) {
  context.save();
  context.globalCompositeOperation = 'lighter';
  context.lineCap = 'round';
  for (let i = 0; i < RISING_SPARK_COUNT; i++) {
    const spark = risingSparkAt(i, timeSeconds, W, H);
    if (spark.age > spark.lifetime) continue;
    const alpha = risingSparkEnvelope(spark.age / spark.lifetime) * spark.brightness;
    if (alpha <= 0.01) continue;

    // Streak: a tapered polyline through the spark's recent positions, so the tail follows the
    // bend instead of cutting straight across it.
    let previous = risingSparkPosition(spark, Math.max(0, spark.age - RISING_SPARK_TRAIL_SECONDS), W, H);
    for (let s = 1; s <= RISING_SPARK_TRAIL_SAMPLES; s++) {
      const t = s / RISING_SPARK_TRAIL_SAMPLES;
      const sampleAge = Math.max(0, spark.age - RISING_SPARK_TRAIL_SECONDS * (1 - t));
      const point = risingSparkPosition(spark, sampleAge, W, H);
      context.strokeStyle = `rgba(${RISING_SPARK_COLOR}, ${alpha * t})`;
      context.lineWidth = RISING_SPARK_WIDTH * spark.size * mix(0.4, 1, t);
      context.beginPath();
      context.moveTo(previous.x, previous.y);
      context.lineTo(point.x, point.y);
      context.stroke();
      previous = point;
    }

    context.fillStyle = `rgba(255, 255, 255, ${alpha})`;
    context.beginPath();
    context.arc(previous.x, previous.y, RISING_SPARK_HEAD_RADIUS * spark.size, 0, TAU);
    context.fill();
  }
  context.restore();
}

// mini-scene-window T2: the mini variant's blue ring. drawBlueLayer's 'color' fill paints an opaque
// blue wherever the backdrop is transparent (and its 'screen' glow paints the whole canvas), which
// would fill the see-through window; 'source-atop' only tints pixels the ring already drew.
function drawBlueRingTint(context) {
  context.save();
  context.globalCompositeOperation = 'source-atop';
  context.globalAlpha = MINI_BLUE_TINT_ALPHA;
  context.fillStyle = BLUE_LAYER_TINT_COLOR;
  context.fillRect(0, 0, W, H);
  context.restore();
}

// Blue wash over the finished monochrome composition: 'color' keeps each pixel's luminance but
// takes the tint's hue/saturation, then a centered 'screen' glow brightens the middle.
function drawBlueLayer(context, cx, cy) {
  context.save();
  context.globalCompositeOperation = 'color';
  context.fillStyle = BLUE_LAYER_TINT_COLOR;
  context.fillRect(0, 0, W, H);

  context.globalCompositeOperation = 'screen';
  const radius = Math.max(W, H) * BLUE_LAYER_GLOW_RADIUS_FRACTION;
  const glow = context.createRadialGradient(cx, cy, 0, cx, cy, radius);
  glow.addColorStop(0, BLUE_LAYER_GLOW_COLOR);
  glow.addColorStop(1, 'rgba(0, 0, 0, 0)');
  context.fillStyle = glow;
  context.fillRect(0, 0, W, H);
  context.restore();
}
