# Unfocused opacity

Objective: tiled windows without focus are drawn at a configurable opacity. Branch
`feat/unfocused-opacity` (from `fix/drop-aim-by-cursor` 6c06096). Delivery: `ask-on-risk`.
Test runner: `dotnet test CosmicWin.sln -c Release --filter "Category!=RequiresDesktop"`.

## Specs

- S1. The user asked: "es posible hacer que cuando esta activo el cosmicwin las ventanas que no
  tiene el focus se vuelvan transparentes?" and "quiero que tengan las ventanas un porcentage de
  alfa en el settings de cosmicwin por ejemplo 95% 90% etc."
  A `settings.conf` key gives the opacity of every tiled window except the focused one, as a
  percent. Assumption: key `unfocused-opacity`, whole numbers 10-100, default 100 (= off, nothing
  touched); out-of-range or unreadable keeps the default, like `gap`.
- S2. The focused tiled window is always fully opaque; focus moving makes the old one translucent
  and the new one opaque.
- S3. The user asked: "Si se cierra mal puede ir a ponerle 100% y cierrarlo yo se reiniciaria no?"
  Starting CosmicWin with `unfocused-opacity = 100` makes every window it adopts fully opaque again,
  repairing windows a crash left translucent.
- S4. Assumption (agreed in the reply): CosmicWin gives every window full opacity back on a clean
  exit, when tiling is turned off (tray / Alt+T), and when a window leaves the tree (closed,
  minimized/evicted, untiled, fullscreen). The keyboard pause does not, matching the maximize block.
- S5. Never break a window that layers itself: one already using `UpdateLayeredWindow` (no
  readable layered attributes) is left alone. `WS_EX_LAYERED` is removed on restore only if
  CosmicWin added it.

## Tasks

- [ ] T1 (S5) Interop: `TrySetOpacity` / `TryClearOpacity` through `StyleCallQueue`, on
  `INativeWindowSource` and `IWindow`, doubles + tests. Route: inline (no writer trigger).
- [ ] T2 (S1) `Settings.UnfocusedOpacity` parse/serialize + tests. Route: inline.
- [ ] T3 (S2-S4) Adapter lifecycle mirroring the maximize block + focus wiring in
  `AppComposition.DrawFocusBorder` + startup value + tests. Route: inline.
- [ ] T4 README + hardware check (Terminal, Chrome/Brave, Store) from an elevated session.

Risk: item 1 (a style written onto other processes' windows outlives CosmicWin if it crashes) ->
one independent verifier at the feature's end, plus native review.

## Log

- L1 (2026-10-22, user, verbatim): "Aprovechando. es posible hacer que cuando esta activo el
  cosmicwin las ventanas que no tiene el focus se vuelvan transparentes?" / "quiero que tengan las
  ventanas un porcentage de alfa en el settings de cosmicwin por ejemplo 95% 90% etc. Si se cierra
  mal puede ir a ponerle 100% y cierrarlo yo se reiniciaria no?" / "Sí, commitea el fix y empieza
  la transparencia".
- L2: mapped. Precedent = maximize-box lifecycle (`StripMaximizeBox`/`RestoreMaximizeBox`/
  `ReleaseMaximizeBlock`/`ApplyMaximizeBlock`, `MultiMonitorWorkspaceAdapter.cs`). No foreground
  hook: focus is resolved in `AppComposition.DrawFocusBorder` (chord, 400 ms tick, bounds events),
  so an opacity change after a mouse click may lag up to ~400 ms, same as the border.
  `WindowFilters` ignores `WS_EX_LAYERED`, so layering a window does not evict it.
