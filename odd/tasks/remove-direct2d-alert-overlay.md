# Remove the Direct2D alert overlay

## Objective

Delete the Direct2D alert renderer so the preloaded WebView2 alert layer is the only alert path.

## Problem / why

The Direct2D overlay (feature `live-alert-wallpaper`) was superseded by `webview-alert-layer`. It is
unwired dead code kept only for rollback. The maintainer decided (2026-09-25): Direct2D was efficient,
but its styling fell short; the web page is easier to style and to preview in a browser. Only the
Direct2D renderer goes. Direct3D/DirectComposition stays: it presents the video and hosts the WebView
visual.

## Scope

Remove:
- `CosmicWin.Interop/Win32/Direct2DAlertOverlay.cs` + `CosmicWin.Interop.Tests/Win32/Direct2DAlertOverlayTests.cs`
- `CosmicWin.Interop/IFrameOverlay.cs`, `CosmicWin.Interop/FrameOverlayTile.cs`, the frame-overlay seam in
  `MediaFoundationVideoWallpaperPlayer.cs` + `MediaFoundationVideoWallpaperPlayerFrameOverlayTests.cs`
- `CosmicWin.App/Alerts/AlertTileLayout.cs` + `CosmicWin.App.Tests/Alerts/AlertTileLayoutTests.cs`
- `AppComposition.cs`: `clearAlertOverlay` / `alertOverlay` parameters and the Direct2D branch of
  `UpdateAlertOverlay`; `startAlertLayer` becomes the only path
- Direct2D wiring tests in `CosmicWin.App.Tests/AlertWallpaperWiringTests.cs` and any Direct2D references
  in `CosmicWin.App.Tests/Alerts/AlertDesktopVisibilityWiringTests.cs`

Keep:
- The composition shake transform (webview T4); it is not part of the overlay seam.
- `Microsoft.Web.WebView2`, the `spike/alert-overlay-t0` branch, history in docs.

## Constraints

- Local only: no push, no gh.
- Queue / FIFO / covered-hold behavior that is still valid must stay covered, by WebView wiring tests.
- Docs: mark `live-alert-wallpaper` superseded; do not rewrite history.

## TDD mode

**Strict TDD: enabled** — source: user's global instructions. Runner:
`dotnet test <Project>.Tests/<Project>.Tests.csproj` (full: `dotnet test CosmicWin.sln`).
For a deletion, RED is a structural test (or a failing build) asserting the removed types/seams are
gone; GREEN is the deletion with every suite still passing.

## Tasks

- [x] T1 — Remove the Direct2D overlay, its seam, tile layout and wiring; migrate still-valid
  queue/FIFO/covered-hold coverage to WebView wiring tests. Route: delegated writer (writer trigger,
  8+ non-trivial files). Commit `dc80b4f` (15 files, +132/-1731).
  - Strict TDD: RED `RemovedDirect2DAlertOverlayTests` 6/6 failed before deletion (e.g. `Assert.Null()`
    got `typeof(Direct2DAlertOverlay)`); GREEN after.
  - Ported: the 3 `AlertDesktopVisibilityWiringTests` (covered hold, uncovered shows, queued-while-covered
    shown once uncovered) re-wired to `startAlertLayer`/`endAlertLayer`; they were the only coverage of
    the real `isPrimaryMonitorCovered` seam.
  - Dropped: `AlertWallpaperWiringTests` (tile order/offset has no WebView equivalent; enable/disable is
    covered by `WebViewAlertCompositionWiringTests`) and `AlertTileLayoutTests`.
  - Also removed 26 Direct2D/DirectWrite CsWin32 entries from `NativeMethods.txt`; kept
    `D2D_MATRIX_3X2_F` (used by the DirectComposition shake). No NuGet package removed.
  - Checks (writer): build 0 errors/0 warnings; `dotnet test CosmicWin.sln` 0 failures (Layout 190,
    Alert 13, Interop 241+40 skipped, App 891+6 skipped). Parent spot check: App tests 891 passed,
    0 failed. `rg` for removed names: only the structural guard.
  - Review: RDD on; assess vs `b2f0624` = medium, `slice_budget_reached` (1863 lines) -> review due.
    Consent granted by the maintainer. Lineage `review-38ecb53f520d985d`, one lens (reliability):
    APPROVED, acknowledged, authority burned. Reviewed boundary advances to `7f6d0e0`.
    Advisory (non-blocking) findings: dropped malformed-command wiring coverage (WARNING, accepted ->
    T1b); vacuous `Type.GetType` structural guard (WARNING, accepted -> T1b); per-frame tick pipeline
    order no longer unit-tested after inlining `TickCore` (SUGGESTION, not taken: the inlined code is
    equivalent and the old test existed only through the removed overlay seam).
- [x] T1b — Port the malformed-command wiring test (error reply, `alert rejected` trace, no layer
  start) to `WebViewAlertCompositionWiringTests`; make the structural guard resolve assemblies from a
  known type with a positive control. Route: delegated writer (same writer, context reuse).
  Commit `e1fc934` (2 test files, +71/-7).
  - New `MalformedAlertCommand_ReturnsErrorAndDoesNotStartTheLayer`. Production already behaved, so
    RED was by mutation (both reverted): dropping the `alert rejected` trace -> `Assert.Contains()`
    failed; starting the layer on rejection -> `Assert.Empty()` failed with `start:warning:1000`.
  - Guard now resolves via `typeof(MediaFoundationVideoWallpaperPlayer).Assembly` /
    `typeof(AppComposition).Assembly`, plus 2 positive-control facts.
  - Checks (writer): build 0 errors; full suite 0 failures (App 894+6 skipped). Parent spot check:
    App tests 894 passed, 0 failed.
- [ ] T2 — Docs: mark `live-alert-wallpaper` superseded in its plan and feature doc; note the removal
  in the webview feature doc; clean doc comments in `AlertQueue.cs` / `AlertCommandParser.cs`.
- [ ] T3 — Hardware re-check with the preloaded layer: warning, failed with shake, FIFO, covered hold.

## Acceptance criteria

- `rg "Direct2DAlertOverlay|IFrameOverlay|FrameOverlayTile|AlertTileLayout|clearAlertOverlay"` finds
  nothing in code (only historical docs).
- `dotnet build CosmicWin.sln` 0 errors; `dotnet test CosmicWin.sln` 0 failures.
- Hardware: all four alert scenarios behave as before.

## Progress

- 2026-09-24: inventory re-verified by `rg`; matches the plan plus `AlertDesktopVisibilityWiringTests.cs`.
- Engram mirror `odd/remove-direct2d-alert-overlay/tasks`: PENDING (mem_save refused: multiple active
  runtime sessions match the project).

## Next step

T1.
