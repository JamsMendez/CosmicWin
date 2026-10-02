# GitHub release workflow

## Objective

Publish CosmicWin from GitHub Actions the same way CielWin does, and give the tests that run on
the hosted runner the same generous hang-guard timeouts CielWin needed.

## Problem / why

- CosmicWin only has `ci.yml` (build + headless tests + artifact upload). There is no release:
  pushing a version tag produces nothing users can download.
- CielWin (`.github/workflows/release.yml`, commits `be82fb3`, `79520ac`) publishes a
  self-contained win-x64 zip on `v*` tags, gated on the headless tests.
- CielWin's tests timed out on the 2-core GitHub Actions runner; `07b3eea` raised its harness
  waits to a 15 min hang guard ("a hang guard, not a performance budget"). CosmicWin's headless
  suite has several short positive waits (2-10 s) that are the same kind of risk.

## Scope

- Add `.github/workflows/release.yml` mirroring CielWin's (tag via env, test gate, idempotent
  re-run, pre-release tags), adapted to `CosmicWin.sln` / `CosmicWin.App`.
- Raise HANG-GUARD waits (wait-until-condition deadlines that pass as soon as the condition
  holds) in tests that run in CI (not `Category=RequiresDesktop`).

## Constraints

- Never raise an upper-bound assertion (`elapsed < X`, "must return immediately", expected
  `TimedOut` with a 1-50 ms budget): those are the behaviour under test, and raising them would
  weaken the test.
- Real-desktop facts are excluded in CI by trait; leave them alone.
- Nothing is pushed or tagged; the maintainer does all GitHub work.

## Tasks

- [x] **T1 — release workflow.** Route: inline (one mechanical file copied from CielWin).
  Check: YAML parses; paths/names match the repo.
- [ ] **T2 — CI hang-guard timeouts.** Route: delegated writer (mapping + edits across several
  test files). Check: headless suite `dotnet test CosmicWin.sln -c Release --filter
  "Category!=RequiresDesktop"` green; diff shows only hang-guard values changed.

## Acceptance criteria

- A `v*` tag would build, run the headless suite, publish self-contained win-x64, zip and
  create/update the release (pre-release for tags with `-`).
- No upper-bound assertion was loosened.

## Delivery

Strategy: `ask-on-risk`. Forecast ~80 authored changed lines, one PR slice.

## Progress

- 2026-10-02: branch `ci/release-workflow` from main 4515feb; doc written.
- T1 done: YAML parses (js-yaml); local `dotnet publish CosmicWin.App -c Release -r win-x64
  --self-contained true -p:Version=0.0.0-test` produced CosmicWin.App.exe (271 files); diff vs
  CielWin's workflow is names only (6 lines). Commit be146d6.
- T1 review: assessed high (shell in workflow); consent granted; 4-lens native review APPROVED and
  acknowledged (lineage review-603d73372e203ef6, authority burned). Reviewed boundary -> be146d6.
  Non-blocking advisories (follow-ups, not in this candidate): R3-001 WARNING release gated on the
  headless suite before T2 lands (land T2 before the first tag); R4 no job timeout-minutes / no
  blame-hang; R1 persist-credentials: false on checkout or split the write job; R1/R3 validate
  the tag as SemVer before publish; R2 doc said one slice while T1 shipped alone.
