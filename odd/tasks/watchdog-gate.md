# Watchdog gate

## Objective

Stop the keyboard-hook watchdog from reinstalling a live hook when it has no reason to. Close the
one real recovery gap the current gate leaves.

## Problem and evidence

Trace 2026-09-10..09-25: 3432 watchdog reinstalls, `foundGone=0` on every one (the watchdog has
never found a dead hook). Since 2026-09-20 there were 320 reinstalls: 114 gaps of ~300 s and 155
gaps over 310 s, so most come from the 5-minute backstop, which fires whenever the user goes 5
minutes without typing.

The backstop exists because of a gap in the gate (`LowLevelKeyboardHook.ShouldReinstall`). The
gate treats input as key-shaped only if the cursor has not moved since the hook's LAST KEY. If the
hook dies, the user then moves the mouse and then types, the cursor move is later than that stale
last key, so the gate blames the mouse forever. Only the backstop recovers, after 5 minutes.

## Decision (maintainer, 2026-09-24)

- Gate: compare against the MOST RECENT session input (`now - sessionAge`), not the hook's last
  key. If the cursor has not moved since that latest input, the latest input was key-shaped and
  the hook missed it, so reinstall. Clicks and wheel with a still cursor remain the documented
  residue.
- Backstop: raise `DefaultWatchdogBackstop` from 5 to 30 minutes and keep it as a safety net for a
  silently wrong reading (never observed). A refused reading still reinstalls, as before.

## Scope

`CosmicWin.App/Input/LowLevelKeyboardHook.cs` (gate, backstop default, remarks) and
`CosmicWin.App.Tests/Input/KeyboardHookTests.cs`. No other behavior changes.

## TDD mode

Strict TDD: enabled. Source: the user's global instructions. Runner:
`dotnet test CosmicWin.App.Tests/CosmicWin.App.Tests.csproj`.

## Tasks

- [ ] **W1 -- Gate compares against the latest session input.** RED: a dead hook, then mouse,
  then typing, must reinstall before the backstop. Keep mouse-only input from reinstalling.
  Route: delegated writer (2 non-trivial files).
- [ ] **W2 -- Backstop default 30 minutes.** RED on the default value. Route: same writer.
- [ ] **W3 -- Hardware check.** Watch the trace for a working period and compare the reinstall
  rate with the baseline (about 64/day since 09-20).

## Progress

2026-09-24: document created; W1 next.
