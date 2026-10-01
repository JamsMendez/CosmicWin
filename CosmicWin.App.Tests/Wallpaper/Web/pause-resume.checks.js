"use strict";

// pause-scene-when-covered T1: the `{type:"pause"}` / `{type:"resume"}` host message checks shared by
// the four scene harnesses (idle/explorer/processing/raphael-scene.tests.js), so the contract is
// asserted once, the same way, for every scene.
//
// Contract (feature doc odd/tasks/pause-scene-when-covered.md): while the desktop is covered the host
// pauses the html wallpaper page. A paused page draws NO frame and keeps NO requestAnimationFrame
// pending (nothing re-arms until resume). Resume re-arms exactly once, whatever the number of
// duplicate messages. The mini variant is always visible and ignores both messages. The scenes derive
// time from the rAF timestamp, so after a gap the scene simply continues at the current time.

const assert = require("assert");

// Loads a page and lets its real render loop run one frame, so exactly one rAF callback is in flight
// (the way a running page always is). Returns the page and a helper that fires the latest one.
function startRunningPage(loadPage, options) {
  var page = loadPage(Object.assign({ innerWidth: 1000, innerHeight: 500, withWebview: true }, options || {}));
  var calls = page.requestAnimationFrameCalls;
  function fireLatest(ms) { calls[calls.length - 1](ms); }
  fireLatest(0);
  return { page: page, calls: calls, fireLatest: fireLatest };
}

// A paused page: the in-flight frame draws nothing and re-arms nothing; resume re-arms exactly once
// (a duplicate resume, or a resume that was never preceded by a pause, must not start a second loop).
function checkPauseStopsTheLoopAndResumeRestartsIt(loadPage) {
  var run = startRunningPage(loadPage);
  var page = run.page;

  run.fireLatest(16.7);
  var armed = run.calls.length;

  page.dispatchHostMessage({ type: "pause" });
  run.fireLatest(33.4); // the frame that was already in flight when the pause arrived
  assert.strictEqual(run.calls.length, armed,
    "expected a paused page to draw no frame and arm no further requestAnimationFrame");
  page.dispatchHostMessage({ type: "pause" }); // duplicate pause: still nothing
  assert.strictEqual(run.calls.length, armed, "a duplicate pause must stay inert");

  page.dispatchHostMessage({ type: "resume" });
  assert.strictEqual(run.calls.length, armed + 1, "expected resume to re-arm the loop exactly once");
  page.dispatchHostMessage({ type: "resume" });
  assert.strictEqual(run.calls.length, armed + 1, "a duplicate resume must not start a second loop");

  run.fireLatest(5000); // the scene continues at the CURRENT rAF time after the gap
  assert.strictEqual(run.calls.length, armed + 2, "expected the resumed frame to draw and arm the next one");
  run.fireLatest(5016.7);
  assert.strictEqual(run.calls.length, armed + 3, "expected the loop to keep running after the resume");
}

// A resume arriving while no frame was ever held back (pause and resume between two frames) leaves the
// still-in-flight frame to carry on: no second loop.
function checkPauseResumeBetweenFramesDoesNotDuplicateTheLoop(loadPage) {
  var run = startRunningPage(loadPage);
  var armed = run.calls.length;

  run.page.dispatchHostMessage({ type: "pause" });
  run.page.dispatchHostMessage({ type: "resume" });
  assert.strictEqual(run.calls.length, armed,
    "expected no extra requestAnimationFrame while the in-flight frame is still pending");
  run.fireLatest(16.7);
  assert.strictEqual(run.calls.length, armed + 1, "expected exactly one frame to follow, not two loops");
}

// A host that never sends pause (every current page load) is unaffected by a stray resume.
function checkResumeWithoutPauseIsInert(loadPage) {
  var run = startRunningPage(loadPage);
  var armed = run.calls.length;
  run.page.dispatchHostMessage({ type: "resume" });
  assert.strictEqual(run.calls.length, armed, "a resume without a preceding pause must do nothing");
}

// Host-side timing stays authoritative for an alert on screen when the desktop becomes covered: the
// page must still process `hide` while paused (it is a message handler, not a frame), and a frame after
// the resume must find the overlay gone.
function checkHideIsStillProcessedWhilePaused(loadPage) {
  var run = startRunningPage(loadPage);
  var page = run.page;

  page.dispatchHostMessage({
    type: "show",
    tiles: ["warning"],
    columns: 1,
    rows: 1,
    gap: 0,
    workArea: { left: 0, top: 0, width: 0, height: 0 },
    duration: 5000,
  });
  assert.strictEqual(page.sandbox.animating, true, "precondition: the alert is showing");

  page.dispatchHostMessage({ type: "pause" });
  page.dispatchHostMessage({ type: "hide" });
  assert.strictEqual(page.sandbox.animating, false, "expected hide to take effect while the page is paused");

  page.dispatchHostMessage({ type: "resume" });
  assert.strictEqual(page.sandbox.animating, false, "expected the hidden alert to stay hidden after the resume");
}

// The mini scene window is topmost and always visible: it must never be paused, even by a stray message.
function checkMiniVariantIgnoresPause(loadPage) {
  var run = startRunningPage(loadPage, { innerWidth: 288, innerHeight: 288, search: "?variant=mini" });
  var armed = run.calls.length;
  run.page.dispatchHostMessage({ type: "pause" });
  run.fireLatest(16.7);
  assert.strictEqual(run.calls.length, armed + 1, "expected the mini variant to keep drawing after a pause message");
}

function checkPauseResume(loadPage) {
  checkPauseStopsTheLoopAndResumeRestartsIt(loadPage);
  checkPauseResumeBetweenFramesDoesNotDuplicateTheLoop(loadPage);
  checkResumeWithoutPauseIsInert(loadPage);
  checkHideIsStillProcessedWhilePaused(loadPage);
}

module.exports = {
  checkPauseResume,
  checkMiniVariantIgnoresPause,
};
