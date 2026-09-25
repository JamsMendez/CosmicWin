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

- [ ] T1 Detector excludes the host class (unit test, RED then GREEN). Route: inline (1 file + test).
- [ ] T2 Host created hidden + `WS_EX_NOACTIVATE`, shown with `SW_SHOWNA`. Route: inline (1 file).
- [ ] T3 Hardware check: after start, an alert plays immediately with no other window focused.

## Acceptance criteria

- An alert sent right after start plays without first focusing another window.
- `dotnet build CosmicWin.sln` 0 errors; `dotnet test CosmicWin.sln` 0 failures.

## Progress

(none yet)
