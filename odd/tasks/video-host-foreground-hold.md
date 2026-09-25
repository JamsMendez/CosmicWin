# Video host foreground hold

## Objective

Alerts must not be held just because CosmicWin's own video wallpaper host window is foreground.

## Problem

`Win32VideoWallpaperHost.CreateHostWindow` creates a visible, caption-less, monitor-sized
`WS_POPUP` with no `WS_EX_NOACTIVATE`. Right after start it takes the foreground; after
`SetParent` it stays the foreground window. `PrimaryMonitorFullscreenDetector` then classifies it as
a fullscreen window covering the primary monitor, and the alert queue holds every alert (pipe and
HTTP) until another window takes the foreground. Measured 2026-09-25 (see
`odd/tasks/http-alert-endpoint.md`, "Follow-ups"). The Explorer-restart path
(`RecreateDestroyedHostWindow`) creates the window the same way.

## Scope

- Detector: never count CosmicWin's own video wallpaper host as covering the desktop.
- Host: create the window hidden and `WS_EX_NOACTIVATE`; show it without activation after attach.
- Out of scope: any other covered-desktop rule.

## TDD

Mode: enabled (session configuration, Strict TDD). Runner: `dotnet test`.

## Tasks

- [x] T1 Detector excludes the host class (unit test, RED then GREEN). Route: inline (1 file + test).
  Commit 7c433a0. RED: `TheVideoWallpaperHostClass_IsExcludedFromCoverage` failed; GREEN 14/14.
- [x] T2 Host created hidden + `WS_EX_NOACTIVATE`, shown with `SW_SHOWNA`. Route: inline (1 file).
  Commit 7042caf. RED: `TryAttach_CreatesAndRecreatesTheHostWindowAsNonActivatable` failed on the
  ex-style bit (desktop opt-in run); GREEN 20/20 host tests. Full suites: Layout 190, Alert 13,
  Interop 315/41 skipped (341/15 with `COSMICWIN_RUN_DESKTOP_TESTS=1`), App 958/6 skipped.
- [x] T3 Hardware check: after start, an alert plays immediately with no other window focused.
  A/B with the desktop shown (`Shell.Application.MinimizeAll`) before launch, see Progress.

## Acceptance criteria

- An alert sent right after start plays without first focusing another window.
- `dotnet build CosmicWin.sln` 0 errors; `dotnet test CosmicWin.sln` 0 failures.

## Progress

- 2026-09-25 hardware probe (elevated shell, scratchpad `probe.ps1`): launch, wait for
  `alert-layer page ready`, send `warning:1`, measure `alert-layer show`.
  - base (`main` 24fc980): shown after 197 ms -- did NOT reproduce: the user's Chrome held the
    foreground, so the new process could not take it.
  - fix (7042caf): shown after 223 ms; `video-wallpaper phase=startup tryAttach=True tryPlay=True`.
  - A/B with the desktop shown first (maintainer authorized taking the focus), foreground before
    launch `Shell_TrayWnd`:
    - base (`main`): foreground became `CosmicWinVideoWallpaperHost-<guid>` "CosmicWin Video
      Wallpaper"; the alert was NOT shown within 8 s (held). Bug reproduced.
    - fix (7042caf): foreground stayed `Shell_TrayWnd`; alert shown after 69 ms.

## Review

- 2026-09-25: medium risk, maintainer granted. One lens (reliability), approved and acknowledged,
  lineage `review-88186af44431b0ae` (base 24fc980, candidate ae9f166).
- Advisory, non-blocking (`R3-host-classname-format-unlinked`, SUGGESTION): the detector test
  hardcodes the host class-name shape; a real-attach assertion that `IsExcludedFromCoverage` accepts
  the live host's `GetClassName` would link the two. Done on `test/live-host-class-exclusion`:
  `TheLiveHostWindowClass_IsExcludedFromCoverage`. Green on first run, so proven by mutation (host
  class format without the hyphen): the new test failed while the 7 detector unit tests passed.

## Next step

Merged into local `main` (fast-forward, acd9478) 2026-09-25; branch deleted. Follow-up test on
`test/live-host-class-exclusion`.
