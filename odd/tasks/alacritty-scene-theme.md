# Alacritty scene theme

## Objective
When the HTML wallpaper scene changes (processing, idle, raphael, explorer), CosmicWin writes a per-scene
Alacritty color file so terminal text stays legible over the scene at window opacity 0.85-0.90.

## Problem / why
The current theme (astrodark) drops to 4.26:1 foreground contrast when a bright scene highlight sits behind
the translucent terminal, and `bright.black` (comments, autosuggestions) drops to ~1:1. Alacritty opacity only
affects the background: effective bg = opacity * theme bg + (1 - opacity) * scene pixel. Legibility is a
luminance problem, so palettes use a dark bg, light fg and a raised `bright.black`, tuned per scene hue.

## Scope
- Opt-in setting `alacritty-theme-file` (empty = off, default). Absolute path of the file CosmicWin owns.
- Pure renderer: scene -> TOML `[colors.*]` text.
- Atomic writer (temp + replace), never throws, only touches that one file.
- Write at startup (html mode) and after every successful scene switch over HTTP.
- README documents the key and the one-time `import` line in alacritty.toml.

## Constraints
- Never touch the user's alacritty.toml from the app.
- Writer failures are diagnostics, never crashes.
- Only in html wallpaper mode.

## Acceptance criteria
- Each scene's palette: foreground >= 7:1 and every text color except `black` >= 4.5:1 against the
  worst-case composite (opacity 0.85 over the scene's brightest colors). Asserted by tests.
- Setting parse/serialize round-trips; empty/absent = off.
- Scene switch writes the matching file; failed switch does not.

## TDD
Mode: enabled (session config "Strict TDD Mode: enabled"). Runner: `dotnet test CosmicWin.sln`.

## Delivery
Forecast ~350 authored lines. Strategy: ask-on-risk. Branch `feat/alacritty-scene-theme`.

## Tasks
- [x] T1 Palette + TOML renderer with contrast tests (route: delegated writer, 2+ non-trivial files)
- [x] T2 Setting `alacritty-theme-file` + atomic writer (route: delegated writer)
- [x] T3 Wiring at startup + HTTP scene switch, wiring tests, README (route: delegated writer)

## Progress / evidence
- Mapping done (explorer agent): choke points `AppComposition.HandleWallpaperSceneHttpSwitch` (~770-803, next to
  `persistWallpaperScene`) and startup `settings.WallpaperScene` (~2106-2160). No tray scene pick.

- T1 6c267c1: palettes + renderer; RED 13 failed (stub) -> GREEN 13 passed.
- T2 8ee0d9b: setting + atomic writer (blank path rejected, identical bytes skipped); RED -> GREEN 210 + 6.
- T3 b337e08: Wire seam applyTerminalSceneTheme after successful HTTP switch (own try/catch,
  trace `terminal-theme-failed`), startup write in WireProduction (html + non-empty path), README.
- Full suite: all pass except ExplorerSceneNodeTests.ExplorerSceneHarness_PassesAgainstTheRealShippedPage
  (node harness timeout), which fails identically on base 67a0aae. Parent spot check: Alacritty|HttpSceneSwitch 39 passed.
- Actual size ~635 authored lines (over forecast, mostly tests).

## Next step
Native review of the slice; user adds the import to alacritty.toml and sets the key; hardware check.
