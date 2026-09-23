# Video wallpaper: survive an Explorer restart

## Objective

After Explorer restarts, the video wallpaper comes back by itself, with no app restart.

## Why

Maintainer request, 2026-09-23, after the live-alert T0 spike (`odd/tasks/live-alert-wallpaper.md`)
found it on hardware. Today the video is lost for good until CosmicWin is restarted.

## Problem (observed on hardware, 2026-09-23)

Repro: CosmicWin running with the video wallpaper, `Stop-Process -Name explorer -Force`, Explorer
restarts. Window snapshots of the Release build (main):

- Before: the host (`CosmicWinVideoWallpaperHost-*`, `WS_CHILD|WS_VISIBLE`, 3392x1440) is a child
  of Progman; the hidden `TaskbarCreated` receiver is a separate top-level 1x1 popup.
- After (+1 s, +6 s, +21 s): the host window **no longer exists**; it was destroyed with its
  Progman parent. Only the receiver remains.

Cause (code): `Win32VideoWallpaperHost.TryAttach()` only creates the window when `_hwnd.IsNull`.
After the restart `_hwnd` is a stale handle to a destroyed window, so every retry (the
`TaskbarCreated` handler and the 400 ms keep-alive tick) runs `AttachToDesktop` against a dead
HWND and fails forever. The D3D swapchain is also bound to that dead HWND.

## Scope

In scope: `Win32VideoWallpaperHost` recovers a destroyed host window: new window, re-attached,
new swapchain on the SAME D3D11 device (the Media Foundation engine's DXGI device manager was built
on it, so replacing the device would break playback).
Out of scope: UIPI filtering of `TaskbarCreated` for the elevated app (the 400 ms tick already
retries), multi-monitor, any other wallpaper behavior.

## Constraints

- `CosmicWin.Interop` is the only project touching Win32.
- `TryAttach` never throws.
- The device is multithread-protected (`MediaFoundationVideoWallpaperPlayer`), and the player
  fetches `GetBackBuffer()` on every tick.
- Desktop tests run only with `COSMICWIN_RUN_DESKTOP_TESTS=1`, with CosmicWin.App closed.

## TDD mode

**Strict TDD: enabled** — source: user's global instructions. Runner:
`dotnet test CosmicWin.Interop.Tests/CosmicWin.Interop.Tests.csproj`.

## Delivery

Strategy: `ask-on-risk` (default). Forecast: under 400 authored lines. RDD: on (global).

## Tasks

- [x] **R1 — Recreate a destroyed host window on the same device.** RED: a desktop-gated real
  attach test destroys the host window and calls `TryAttach()` again; it must return true with a
  new live, parented window and the same `Device`. Then GREEN, then REFACTOR. Route: delegated
  writer (host + test file).
- [x] **R2 — Hardware check.** Parent runs the Release build, restarts Explorer, and captures the
  desktop and the window tree.

## Progress

2026-09-23: branch `fix/explorer-restart-reattach` created from `main`, cause identified.

2026-09-23: R1 implemented.

- Blocked, then unblocked: `CosmicWin.App` was running when R1 started, which makes every
  `[RequiresDesktopSessionFact]` in `Win32VideoWallpaperHostRealAttachTests` SKIP outright
  (`DesktopGate.SessionSkipReason`) rather than fail — confirmed by running the gated filter and
  observing all 4 facts (including the new one) report `[SKIP]`. Reported this and stopped before
  GREEN per the task brief; the parent closed `CosmicWin.App` and R1 continued.
- RED (`COSMICWIN_RUN_DESKTOP_TESTS=1`, app closed): 3 passed, 1 failed —
  `TryAttach_AfterTheHostWindowIsDestroyed_RecreatesItOnTheSameDevice` failed with
  `TryAttach should recover a destroyed host window instead of failing forever.` — the documented
  cause, confirmed live: `TryAttach()` returned `false` after the host window was destroyed.
- Fix: `Win32VideoWallpaperHost.TryAttach()` now checks `PInvoke.IsWindow(_hwnd)`; when the stored
  handle is dead it calls a new `RecreateDestroyedHostWindow()`, which drops only the window-bound
  D3D resources (`ReleaseSwapChainResources()`, new — releases swapchain/back buffer/RTV, NOT
  device/context), creates and attaches a fresh window, then calls a new `EnsureSwapChain()` /
  `CreateSwapChainOnExistingDevice()` that builds a fresh swapchain on the SAME `_device`/`_context`.
  `CreateSwapChainAndPresentTestPattern` (full device+swapchain create) is untouched and still runs
  unconditionally for the first-ever attach; `EnsureSwapChain` is the only new caller-facing
  decision point, and it is also now used on the ordinary reattach path (previously that path could
  have released a live device if D3D wasn't ready — now it never does).
- GREEN (`COSMICWIN_RUN_DESKTOP_TESTS=1`, app still closed): all 4 real-attach facts passed,
  including the 3 pre-existing ones (no regression).
- Verification: `dotnet build CosmicWin.sln -c Debug` succeeded (pre-existing warnings only);
  `dotnet test CosmicWin.Interop.Tests` (ungated) 171 passed / 34 skipped / 0 failed; gated
  real-attach filter 4/4 passed; `dotnet test CosmicWin.App.Tests` 766 passed / 6 skipped / 0
  failed.
- Files: `CosmicWin.Interop/Win32/Win32VideoWallpaperHost.cs`, `CosmicWin.Interop/IVideoWallpaperHost.cs`,
  `CosmicWin.Interop.Tests/Win32/Win32VideoWallpaperHostRealAttachTests.cs`.
- Commit: `322b8fb`.

2026-09-23: R2 passed on hardware (RTX 4060 Ti, 3440x1440 @ 164 Hz, Debug build of `322b8fb`
run from a `run/` copy). Parent re-ran the gated real-attach filter first: 4/4 passed.

- Before: host `0x2035E` (`WS_CHILD|WS_VISIBLE`, 3392x1440) child of Progman `0x4055C`.
- `Stop-Process -Name explorer -Force`, Explorer restarts; ~8 s later: a NEW host `0x50422`,
  `WS_CHILD|WS_VISIBLE`, 3392x1440, visible, child of the NEW Progman `0xB08DA`.
- Desktop capture after the restart shows the video wallpaper back and animating (the same build
  on `main` showed the static Windows wallpaper indefinitely). GPU 3D 6.59 / 7.18 / 7.01 %,
  the normal cost of the visible video.

RDD assess (`--base-ref fadfda7 --committed-only`): risk `medium`, 399 changed lines,
`review_due: false` (`under_budget`); no review ran, the slice stays pending under the budget.

## Next step

None for this fix. Unblocks the Explorer-restart leg of `odd/tasks/live-alert-wallpaper.md` T0.
