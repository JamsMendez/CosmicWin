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
- [ ] T3 -- A swallowed gap/exceptions reload failure is never silent when no desktop trace is wired
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
