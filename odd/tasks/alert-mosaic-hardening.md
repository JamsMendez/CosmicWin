# Alert mosaic hardening (review follow-ups)

## Objective

Close the five still-open, behavior-relevant SUGGESTIONs left by the alert-tile-mosaic reviews
(`odd/tasks/alert-tile-mosaic.md`, reviews review-fab54d0470d004ef, review-bd6d059ce4a7b711,
review-5d86ba584a739fe1), re-audited against main e8ac6ae on 2026-10-01.

## Why

Decided by the maintainer 2026-10-01 ("haz la de valor"): only the items with behavior or
flakiness value. Pure doc nits (stale line anchors, "13 cases", "(below)", T15 duplicate entry,
deferred-capture list, hash cap order comment) are OUT of scope.

## Scope and constraints

- TDD strict (global CLAUDE.md). Runners: `dotnet test CosmicWin.App.Tests/CosmicWin.App.Tests.csproj`
  (Node harnesses run through it; `node <harness> <page.js>` for focused runs).
- Each task = one work-unit commit, Conventional Commit, no AI attribution.
- ~400 changed lines per task is advisory only.

## Tasks

- [x] T1 -- Hash API grid: `columns`/`rows` below 1 clamp to 1 (alert-layer.js hash parser ~593-612),
  so a negative grid never drops tiles; pin the empty-filtered-list fallback (one "warning" tile)
  with a harness case. (R3-hash-tiles-empty-or-negative-grid-unproved, R4-hash-tiles-empty-list)
- [x] T2 -- Node harness runner: bounded stdout/stderr reads after a timeout kill
  (AlertLayerLayoutNodeTests.cs ~121-126); prove the kill takes the whole process tree (a child
  spawned by the script dies too); prove NodeAvailability's probe timeout path.
  (R3/R4-runnode-timeout-branch-unbounded-result, R3-timeout-test-proves-root-only,
  R3-probe-timeout-path-unproved)
- [x] T3 -- A swallowed gap/exceptions reload failure is never silent when no desktop trace is wired
  (AppComposition.cs ~1387 ReloadGap, CompositionRoot.cs ~166/175).
  (R4-reload-gap-swallow-depends-on-optional-trace)

Route: delegated direct, one writer (writer trigger: 2+ non-trivial files).

## Acceptance

- Each task shows observed RED then GREEN (or a mutation that fails the new test when behavior
  already existed).
- App suite green with the same skip count; Interop untouched.

## Progress

- 2026-10-01: branch `fix/alert-mosaic-hardening` off main e8ac6ae.
- 2026-10-01 T1: baseline App suite 1384 passed / 6 skipped. RED: negative-columns and two-negatives
  harness cases failed (slice(0,-2) dropped tiles; -2*-3 gave a cap of 6). GREEN after clamping
  columns/rows to >= 1 in the hash parser (20/20 harness cases). All-junk fallback case is pinned
  behavior: proved meaningful by mutating startShowing's fallback on a scratch copy (it failed).
- 2026-10-01 T2: RED (a): with the post-kill reads still unbounded, the survivor-holds-the-pipes
  test failed ("Expected RunNode to return after the timeout kill...", 20 s). A real Windows tree kill
  also reaches orphaned grandchildren, so the survivor comes from an injected root-only kill
  (`killEntireProcessTree: false` seam on the test runner). GREEN after Task.WaitAll with a drain bound
  and empty-string fallback. (b)/(c) described behavior already existed: proved by mutation --
  RunNode using Kill(false) failed the child-process test; removing the probe's Kill failed the
  probe-timeout test; both restored from backup copies. NodeAvailability gained an internal
  `TryRunProbe(fileName, arguments, timeout)` overload (production probe unchanged). Focused: 5/5 pass,
  no stray node.exe left from the runs.
- 2026-10-01 T3: RED: three new tests (exceptions-half and gap-half through the tray Reload, plus the
  deferred ReloadGap catch) failed with "Filter not matched in collection" when no desktop trace was
  wired. GREEN after `CompositionRoot.ReportSwallowedFailure`: records on the trace when present, else
  `Trace.WriteLine` (chosen over Debug.WriteLine because it is compiled in Release and a test can
  observe it through a TraceListener; no existing fallback sink existed beyond Debug.WriteLine in the
  WebView controller). Full App suite 1390 passed / 6 skipped (baseline 1384 / 6); `dotnet build
  CosmicWin.sln` 0 errors, no warnings in touched files.
- 2026-10-01: parent spot check: App 1390 passed / 6 skipped / 0 failed (baseline 1384/6), node.exe
  count unchanged (6 before and after). Assessed HIGH (`high_risk`, process-spawning tests); consent
  granted by the maintainer; review-e4ffb2db5cb77ab7 (4 lenses, main..c7f3797) APPROVED and
  acknowledged, no correction. Merged into local main fast-forward.

## Follow-ups (non-blocking SUGGESTIONs from review-e4ffb2db5cb77ab7)

- R4-001: in a Release run the Trace fallback only reaches OutputDebugString; a swallowed reload
  failure still leaves no lasting record without a debugger attached.
- R2-runnode-kill-comment-now-conditional: the "Kill the WHOLE tree" comment above
  `process.Kill(killEntireProcessTree)` no longer mentions the root-only test seam.
- R2-hash-clamp-relies-on-nan-propagation: `Math.max(1, Math.floor(Number(x))) || 1` -- the `|| 1`
  only fires for NaN; add a note so nobody deletes it as dead code.
- R2-trace-fallback-doc-misstates-debug-compilation: `ReportSwallowedFailure` doc calls Trace the
  sink family Debug.WriteLine belongs to; Debug is [Conditional("DEBUG")], Trace is TRACE.
- R3-pid-handshake-shares-timeout-budget: the process tests start Node, spawn and write the pid inside
  the same 3 s kill bound, then poll 2 s; a slow cold start could fail them spuriously.

## Follow-up pass 2026-10-01 (branch docs/alert-mosaic-followups)

- R4-001 CLOSED, no code change: its premise is false in production. AppComposition always builds
  FileDesktopTrace and passes it to Wire and the tray controller, so a swallowed reload failure already
  lands in desktop-trace.log; the Trace fallback only runs in trace-less test compositions. The
  misleading ReportSwallowedFailure doc ("the default: the trace needs its marker file") is fixed.
- R2-trace-fallback-doc-misstates-debug-compilation: fixed in the same doc (Debug is
  [Conditional("DEBUG")], Trace is [Conditional("TRACE")]).
- R2-runnode-kill-comment-now-conditional: comment names the root-only drain-test seam.
- R2-hash-clamp-relies-on-nan-propagation: note added on why `|| 1` is not dead code.
- Checks: Release build 0 errors; AlertLayer|Reload tests 135/135. Comment-only, passive.
- Review review-b193de9588f975e8 (4 lenses, 5139cc1) APPROVED and acknowledged. Advisory WARNING
  R2-nan-clamp-comment-misstates-evaluation-order: fixed in the next commit (comment only).
- Review review-2761ded7f0de6e58 (4 lenses, main..e506ee2) APPROVED and acknowledged. SUGGESTION
  R2-nan-clamp-comment-missing-param-is-zero-not-nan fixed in the next commit (missing/empty -> 0, not NaN).
- Review review-e74183a916eacd9e (4 lenses, main..73459d3) APPROVED and acknowledged. SUGGESTIONs
  R2-nan-clamp-comment-zero-lifted-by-or-not-max / R3-nan-clamp-comment-misattributes-zero-lift: the 0 is
  lifted by `|| 1` (0 is falsy), not by Math.max; fixed in the next commit, values checked with node.
- R3-pid-handshake-shares-timeout-budget DONE: RunNodeOnceReady / TryRunProbe(readyFile) start the hang
  bound only once the pid file holds a pid (NodeAvailability.WaitUntilReady, 30 s readiness bound, also
  ends on process exit). TDD: RED = two new slow-start tests (pid written after 1.5 s, bound 500 ms) failed
  with the parameter present but unused; GREEN after the gate. Trap caught on the way: a
  RunNode(timeout, string readyFile, params string[]) overload silently captured "-e" of plain calls
  (2 tests failed), so it is a distinct name. App suite 1422 passed / 6 skipped (= main 5a9c7e7's 1420 + the 2 new tests; the 1390 above was T3's count, before later main work added tests);
  node.exe count 7 before and after; build 0 errors (3 pre-existing nullable warnings elsewhere).
- Review review-1c54d782319b85bb (4 lenses, 88c7964..2210594) APPROVED and acknowledged. Open SUGGESTIONs:
  R2-ready-wait-silent-on-readiness-bound (WaitUntilReady says nothing when the 30 s bound runs out),
  R2-slow-start-timing-constants-coupled-by-comment (1500 ms vs 500 ms tied only by a comment),
  R3-isready-catches-only-ioexception (UnauthorizedAccessException not caught),
  R3-readiness-deadline-wall-clock (DateTime.UtcNow instead of Stopwatch),
  R3-survivor-outer-wait-shorter-than-readiness-bound (survivor test waits 20 s, readiness may take 30 s).
- Review review-1eedf1ed3d26142a (4 lenses, main..7cbb7e2) APPROVED and acknowledged; it repeated three of
  the suggestions above and added R2-task-log-test-count-delta-unexplained (the 1422 line).
- All six suggestions fixed in one commit: WaitUntilReady kills the never-ready script and throws
  TimeoutException (TDD: RED = new never-ready test got no exception; GREEN after), Stopwatch instead of
  wall clock, IsReady also absorbs UnauthorizedAccessException, survivor test waits ReadinessBound + 20 s,
  slow-start delay and hang bound derive from SlowStartDelayMs, 1422 line explained. App suite 1423 passed /
  6 skipped; node.exe 7 before and after.
- Reviews review-dfe36f633c071c52 / review-0b2658a3e399eaf0 (00bceed) approved. Their suggestions fixed in the
  next commit: R3-tryrunprobe/probe-never-ready-propagation-unproved (new test: TryRunProbe lets the
  never-ready TimeoutException out; mutation adding TimeoutException to its catch fails it),
  R2-ready-wait-post-loop-recheck-unexplained (comment). review-8bbcafe4921de9b2 WARNING
  R3-never-ready-probe-pid-race: that new test first had the very start-up race R3-pid-handshake removed;
  now propagation only (no pid file). R2-readiness-bound-param-undocumented and the test summary wording
  fixed; review-d1033b667731dc61 literal suggestions answered with comments.
- Doc nits the maintainer approved on 2026-10-01 (delegated writer, same commit): stale line anchors
  (WebViewAlertCompositionWiringTests.cs), "13 cases" in HarnessTimeout doc, ReloadGap "(below)"
  (AppComposition.cs Wire parameter comment), T15 third entry (hash now bogus,,bogus,warning,failed,warning
  -> [warning, failed]; mutations no-cap / keep-last-two / map-before-filter each fail it),
  R3-deferred-reload-test-weak-capture (GapReloadTests keeps every scheduled action, exactly one traces;
  disabling the ReloadGap catch fails it), hash cap order comment (alert-layer.js). Harness 20/20.
- review-57d4a078c543f8c9 (all of the above, uncommitted) APPROVED and acknowledged. Left open by choice
  (each fix reopens another review round): R2-loadgap-comment-line-overlong (AppComposition.cs:174),
  R3-probe-never-ready-test-no-observable-cleanup (kill is proved by the WaitUntilReady test instead).
- Full App suite on that tree: 1424 passed / 6 skipped / 0 failed; node.exe 7 before and after.
