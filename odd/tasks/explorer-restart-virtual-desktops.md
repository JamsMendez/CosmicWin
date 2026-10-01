# Virtual desktops and tiling: survive an Explorer restart

## Objective

After Explorer restarts, CosmicWin keeps tiling new windows and switching desktops, with no app
restart.

## Why

Maintainer report, 2026-09-30: the slideshow fix script restarted Explorer, and CosmicWin stopped
arranging windows and ignored Alt+N until it was restarted.

## Problem (observed on hardware, 2026-09-30, `desktop-trace.log`)

- `18:49:13Z SwitchDesktop arg=2 ok=False count=0->0 ... CreateDesktop: COMException 0x800706BA
  The RPC server is unavailable.` CosmicWin saw ZERO desktops and even tried to create one.
- `18:48:04Z border hidden: 0x50A22 is on another desktop's tree`, repeated: with the current
  desktop reading `Guid.Empty`, every window is filed under "another desktop" and is not tiled.

Cause (code): `Win32NativeVirtualDesktops` resolves `IVirtualDesktopManagerInternal`,
`IVirtualDesktopManager` and `IApplicationViewCollection` ONCE (`IsAvailable` caches `_available`)
and never re-resolves them. They are proxies into the old `explorer.exe`; after the restart every
call fails with an RPC disconnect HRESULT, which `IsInteropFailure` swallows (`GetDesktopIds`
returns `[]`, `GetCurrentDesktopId` returns `Guid.Empty`). `Win32VirtualDesktopQueries._manager`
is a static cached `IVirtualDesktopManager` with the same problem.

WinEvent hooks are out-of-context and global; they are not affected (verified: no
`RegisterShellHookWindow` in the repo).

## Scope

In scope: reconnect the shell COM objects when a call fails with a disconnect-class HRESULT, then
retry that call once. Both `Win32NativeVirtualDesktops` and `Win32VirtualDesktopQueries`.
Out of scope: a `TaskbarCreated` trigger (the reactive reconnect already covers it), the wallpaper
host (fixed in 322b8fb), re-running `VirtualDesktopProbe` (the vtable does not change with a
restart of the same build).

## Constraints

- `CosmicWin.Interop` is the only project touching Win32.
- Nothing here throws; failures stay in `LastError` / `error`.
- Disconnect-class HRESULTs: `0x800706BA` (RPC_S_SERVER_UNAVAILABLE), `0x800706BE`
  (RPC_S_CALL_FAILED), `0x80010108` (RPC_E_DISCONNECTED), `0x80010012` (RPC_E_SERVER_DIED_DNE),
  `0x80004018` (CO_E_SERVER_STOPPING). They can arrive as a `COMException` OR as a returned
  `hr < 0` from `PreserveSig` methods.
- At most one reconnect + one retry per call: a dead shell must not loop.

## TDD mode

**Strict TDD: enabled**, source: user's global instructions. Runner: xUnit 2.9.3,
`dotnet test CosmicWin.Interop.Tests`.

## Delivery

Strategy: `ask-on-risk`. Forecast: about 250 authored changed lines, one slice.

## Tasks

- [x] T1: `Win32NativeVirtualDesktops` reconnects on a disconnect HRESULT and retries once
  (injectable resolver seam, tests with a fake). Route: delegated writer.
- [x] T2: `Win32VirtualDesktopQueries` drops and recreates its static manager on a disconnect and
  retries once. Route: same delegated writer.
- [x] T3: Hardware check: CosmicWin running, `Stop-Process -Name explorer -Force`, then Alt+N and
  a new window are tiled; trace shows no `0x800706BA` after the reconnect. Route: inline.

## Acceptance criteria

- After an Explorer restart, the next `SwitchTo`, `GetDesktopIds`, `GetCurrentDesktopId`,
  `MoveWindowTo`, `TryGetWindowDesktopId` and `TryIsWindowOnCurrentDesktop` succeed without an app
  restart.
- A non-disconnect failure does NOT trigger a reconnect.
- `dotnet test CosmicWin.sln` green.

## Progress

- 2026-09-30: branch `fix/explorer-restart-virtual-desktops` from main 6570723.
- T1 (route: delegated writer; trigger: 2 non-trivial files): seam = internal constructor on
  `Win32NativeVirtualDesktops(Func<bool> probe, ManagerResolver resolver)` returning a
  `ShellManagers` record; the `[ComImport]` interfaces are internal and implementable by test fakes
  (InternalsVisibleTo), so no higher seam was needed. Shared `ShellDisconnect.IsDisconnect(hr)`.
  All calls go through one `TryInvoke` (reconnect once + retry once, probe never re-run).
  RED (observed, `Win32NativeVirtualDesktopsReconnectTests`, IsDisconnect stubbed to false):
  13 of 21 failed: `IsDisconnect_is_true_for_every_listed_hresult` (x5, expected True),
  `GetDesktopIds_reconnects...` (got `[]`), `CreateDesktop_reconnects...` (0 creates),
  `SwitchTo_reconnects...` (switched Guid.Empty), `MoveWindowTo_reconnects_when_the_disconnect_is_a_returned_hresult`
  (false), `A_shell_that_is_still_dead...` and `A_reconnect_that_cannot_resolve...` (1 resolve, expected 2).
  GREEN: 21/21 after implementing `IsDisconnect`.
  T1 commit: 9251d55.
- T2 (route: same delegated writer): seam = `internal static UseFactoryForTests(Func<IVirtualDesktopManager?>?)`
  on `Win32VirtualDesktopQueries` (public static API unchanged); one private `Invoke` handles thrown
  and returned (`hr < 0`) disconnects, drops `_manager`, recreates, retries once.
  RED (observed, `Win32VirtualDesktopQueriesReconnectTests`, seam present, no retry): 7 of 9 failed:
  `TryGetWindowDesktopId_reconnects_once_and_answers` (thrown True/False, returned False),
  `TryIsWindowOnCurrentDesktop_reconnects_once_and_answers` (x2, False),
  `A_shell_that_is_still_dead...`, `A_reconnect_that_cannot_create...`, `The_recreated_manager_is_kept...`
  (factory called 1 time, expected 2). The 2 non-disconnect tests passed (no reconnect, as required).
  GREEN: 9/9 after adding `Invoke`.
  T2 commit: 51a5d6b. Checks: build 0 errors; Interop.Tests 458 passed/42 skipped (desktop-gated); Layout 198, Alert 13, App.Tests: the writer's "14 passed, testhost hangs" was its 120 s tool timeout, not a hang.
  Parent re-run, `dotnet test CosmicWin.App.Tests --blame-hang-timeout 5m`: 1317 passed, 6 skipped
  (desktop-gated), 0 failed, 3 m 45 s, exit 0.
- Review (RDD, medium, 967 lines, consent granted): lineage review-4ab1fded09ef6185, lens
  review-reliability, APPROVED and acknowledged (authority burned) on candidate tree a06a2d3 (fe5f3bb).
  Advisory, non-blocking follow-ups: R3-create-retry-nonidempotent (a retried CreateDesktop after
  0x800706BE could create two desktops), R3-empty-reconnect-reason (reconnect-failed message can be
  empty), R3-static-seam-parallel-tests (static factory seam in the Queries tests vs xUnit parallel
  classes).
- T3 hardware check, 2026-09-30, elevated shell, combined local test branch `test/hw-2026-09-30`,
  Debug build run from `run/` (PID 30076): before the restart `Alt+2`/`Alt+1` -> `SwitchDesktop ok=True
  count=3->3`. `Stop-Process -Name explorer -Force`, Explorer back as PID 33564, CosmicWin kept the
  same PID. Same switches -> `ok=True count=3->3 error=(none)`, no 0x800706BA / 0x8001xxxx in the
  trace. A new probe window opened after the restart was tiled (`border around 0xF099A`, slot
  415x708). Before this fix the same restart gave `count=0` and `0x800706BA`.

