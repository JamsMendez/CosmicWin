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
  Revised 2026-10-22 (see Log): repair runs only when the previous run ended without a clean exit
  while CosmicWin had windows layered. A marker file in `%LOCALAPPDATA%\CosmicWin\` exists while
  CosmicWin may have layered windows; a clean exit removes it. When it is present at startup, every
  window CosmicWin adopts is made fully opaque (at 100) or rewritten (below 100). When it is
  absent, `unfocused-opacity = 100` touches no window at all.
- S4. Assumption (agreed in the reply): CosmicWin gives every window full opacity back on a clean
  exit, when tiling is turned off (tray / Alt+T), and when a window leaves the tree (closed,
  minimized/evicted, untiled, fullscreen). The keyboard pause does not, matching the maximize block.
- S5. Never break a window that layers itself: one already using `UpdateLayeredWindow` (no
  readable layered attributes) is left alone. `WS_EX_LAYERED` is removed on restore only if
  CosmicWin added it.

## Tasks

- [x] T1 (S5) Interop: `TrySetOpacity` / `TryClearOpacity` through `StyleCallQueue`, on
  `INativeWindowSource` and `IWindow`, doubles + tests. Route: inline (no writer trigger).
- [x] T2 (S1) `Settings.UnfocusedOpacity` parse/serialize + tests. Route: inline.
- [x] T3 (S2-S4) Adapter lifecycle mirroring the maximize block + focus wiring in
  `AppComposition.DrawFocusBorder` + startup value + tests. Route: inline.
- [x] T5 (S3) Crash marker: written when the first window is layered, removed on a clean exit
  and when nothing layered remains; startup repair only when it is present. Route: inline.
- [x] T4 README + hardware check (Terminal, Chrome/Brave, Store) from an elevated session.

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
- T1 done, commit `e4ab3d8`. RED 5/6 against a Refused stub (dead-window fact a guard), GREEN 6/6.
  Native core (`SetOpacityCore`/`ClearOpacityCore`) has no unit test; proved on hardware below.
- T2 done, commit `b9abdd8`. RED 6/16, GREEN; `Serialize_WritesOnlyTheNewKeys` updated to the new key list.
- T3 done, commit `f9f7517`. Adapter: RED 9/13 (4 guards), GREEN 13/13 + MaximizeBlockTests 35/35.
  Wiring (`TilingModeTests`): RED 3/4, GREEN. Focus driven through the reconcile tick; opacity is
  applied at the top of `DrawFocusBorder`, before its border-only early returns.
  `opacityAdapter` reference because `AfterArrange` becomes a delegate before the adapter exists.
- Headless suite: 208 + 984 + 248 (3 skipped), 0 failed.
- T4: README "What works" bullet + Settings table row.
- Hardware (elevated, Release, probes on a fresh desktop 4, read with GetLayeredWindowAttributes):
  focused probe alpha 255, others 230 (90%); clicking A then B moved the 255 with focus; Alt+T off
  -> WS_EX_LAYERED removed from all three; Alt+T on -> 230/255 again; Stop-Process -Force (crash)
  -> A/C stayed 230; restart at 100 -> all 255 (layered bit kept, as designed). A clean tray exit
  was not driven on hardware (covered by `ExitingNormally_MakesEveryWindowOpaque`). Real apps
  (Terminal, Chrome/Brave) not yet looked at for flicker. `settings.conf` left at 90 for the user.
- Independent verifier (whole document, base 6c06096): no blockers; S1-S5 met; build 0 errors,
  suite 208 + 984 + 248 (3 skipped). Advisories kept as follow-ups:
  A1 a window evicted by ANOTHER window's arrange (TreeArranger.Apply drops it from the registry)
  can stay translucent after tiling off/exit -- same pre-existing gap as the maximize box;
  A2 tray "tiling on" applies opacity with the taskbar as foreground, so every tiled window is
  translucent until one is focused; A3 at 100 the S3 repair also sets 255 on windows that layer
  themselves with readable attributes; A4 give-back sets 255, not an app's own original alpha;
  A5 `_layeringIsOurs` read from the cached ExStyle (tiny race); A6 native cores, Untile,
  !CanReposition and rehome opacity paths have no dedicated tests.
- Native review (base 6c06096 committed-only, assessed high via escalated item 1; lenses risk,
  resilience, reliability): consent granted, APPROVED and acknowledged (lineage
  `review-938f12b1d2cf0b64`, authority burned). Non-blocking advisories: R3-partial-layering-left-
  behind (SetOpacityCore adds WS_EX_LAYERED, then SetLayeredWindowAttributes fails -> window left
  layered with no attributes, which Windows does not draw, and it is never given back);
  R3-tray-toggle-all-translucent (= verifier A2); R1/R3/R4 default-100 adoption repair overwrites an
  app's own layered alpha (= verifier A3, a product decision against S1 "nothing touched").
- 2026-10-22 user: "Arregla los defectos". Fixed R3-partial-layering (`001930e`: SetOpacityCore
  removes the WS_EX_LAYERED it added when SetLayeredWindowAttributes fails; no runnable RED -- the
  failure cannot be forced from the doubles or on hardware) and A2/R3-tray-toggle (`6ad3824`:
  ResumeTiling goes through ApplyUnfocusedOpacity's tiled-foreground gate; RED 1/7 then GREEN; the
  old toggle test, which pinned the defect, now uses a tiled foreground). Suite 208 + 985 + 248
  (3 skipped). Native review (base e851616, lenses risk + reliability): consent granted, APPROVED,
  acknowledged. Advisories: the rollback path is untested, and its SetWindowLong result is not
  checked. Open decision: A3 (default-100 adoption repair also resets apps' own layered alpha).
- L-decision (2026-10-22, user, verbatim): "Que me recomiendas?" -> recommended a crash marker so
  that 100 touches nothing unless the previous run crashed with opacity active -> "Dale asi".
  S3 rewritten; T5 opened.
- T5 done. Adapter: RED 4/17 then GREEN (`RepairOpacityOnAdoption`, `OpacityInUseChanged`
  reported on change only; a clean exit always reports false). Composition: `OpacityMarkerFile`
  (`%LOCALAPPDATA%/CosmicWin/opacity-in-use`, best-effort), RED 3/29 then GREEN. Suite 208 + 995
  + 248 (3 skipped). settings.conf comment and README updated.
  Hardware: probe X layering itself at alpha 200, adopted at 100 with no marker -> stayed 200;
  at 90 -> marker created, A 255 / B,X 230; Stop-Process -Force -> marker stayed, B,X 230;
  restart at 100 -> A,B,X 255. Left at 90 for the user.
- T5 native review (base 7978e71; lenses risk, resilience, reliability): consent granted,
  APPROVED, acknowledged. Advisories: the marker is cleared BEFORE the last window's give-back and
  without checking it landed; Dispose clears it even if a give-back timed out or a crashed run's
  windows on unvisited desktops were never adopted; marker IO failures are silent; production
  wiring of the marker has no test.
