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
- [x] **T2 — CI hang-guard timeouts.** Route: delegated writer (mapping + edits across several
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
- T2 done. Route: delegated writer (trigger: mapping across three test projects + edits in 11 test
  files). Test-first exception: no meaningful RED exists, the failure only appears on the slow
  2-core hosted runner; the check is the headless suite staying green and a diff that changes only
  hang-guard values and their comments. Every raised value is `TimeSpan.FromMinutes(1)` with a
  trailing `// was Ns: hang guard sized for the 2-core GitHub Actions runner`.
  - Raised (old -> 1 min): AppCompositionMtaActionThreadTests.cs:23, :45 (Wait 5s), :51
    (AssertEventually deadline 5s); AppCompositionTests.cs:212 (WaitUntil 2s);
    CompositionRootTests.cs:49, :69 (run-loop cts 5s, raised with the wait it guards), :54, :74
    (WaitUntil 2s); ArrivingWindowDesktopWiringTests.cs:155 (WaitUntil 5s);
    FocusBorderWiringTests.cs:360 (WaitUntil 2s), :1193 (WaitUntil() deadline 2s);
    TilingModeTests.cs:511 (WaitUntil() deadline 2s); SynchronizedSettingsStoreTests.cs:50, :51
    (Join 5s); Input/KeyboardHookTests.cs:155, :158, :199, :201, :238, :239, :264, :266, :294,
    :322 (SpinUntil / SecondInstall.Wait 2s; fake clock, so only real waits);
    WatchdogReinstallWiringTests.cs:126, :127 (2s); StyleCallQueueTests.cs:13 `Plenty` (5s, shared
    constant: success budgets and positive waits); BoundedActivationTests.cs:30
    `LongEnoughForAnImmediateReturn` (5s; budget for a delegate that returns at once, its expiry is
    never the asserted outcome; doc comment updated to "a full minute").
  - Left alone: StyleCallQueueTests.cs:187 `older.Wait` pinned to an explicit 5s instead of
    `Plenty` (must stay under the older call's 30 s budget or the budget expiring would pass too);
    every 1-50 ms `TimedOut` budget (the outcome under test); `LowLevelKeyboardHook(..., 5s, ...)`
    and watchdog backstops/intervals (KeyboardHookTests, WatchdogReinstallWiringTests:84,
    AppComposition*/DroppedChordWiringTests; production config driven by a fake clock);
    CompositionRootTests.cs:57, :77 `WhenAny(runTask, Delay(1s))` (shutdown courtesy, nothing
    asserted on it); AppCompositionTests.cs:292 `Interval <= 30s` (upper bound);
    FileDesktopTraceTests.cs (elapsed upper bounds, retryWindow config, fixed settle delays);
    DesktopSwitchSettleTests.cs:179 (total waits <= 500 ms upper bound);
    SpawnedNotepadWindowSweepTests.cs:26 `GraceWorthManyPasses` 1s (config the sweep always
    consumes in full, so raising it slows every run; already 10x its 100 ms poll);
    RealDesktopLock 10 min (already >= 1 min); everything in RequiresDesktop classes/facts
    (MediaFoundationVideoWallpaperPlayerTests, DesktopSwitchVisibility, SlowAdmission,
    Win32NativeWindowSource*, LowLevelKeyboardHookDesktopTests, ModalDialogSnapshot, Spawned*
    window helpers) because CI filters them out.
  - Verification: `dotnet build CosmicWin.sln -c Release` 0 errors (4 pre-existing warnings);
    `dotnet test CosmicWin.sln -c Release --no-build --filter "Category!=RequiresDesktop"`
    Layout 198/198, App 944/944, Interop 240 passed + 3 skipped of 243, 0 failed.
