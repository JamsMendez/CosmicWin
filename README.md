<img src="docs/image/icon-256.png" alt="CosmicWin" width="120" align="right" />

# CosmicWin

A tiling window manager for Windows 11, modelled on COSMIC's tiling behaviour, with an optional
looping video wallpaper.

- **Tiling** — windows are arranged in a tree and given the whole work area between them. Focus,
  movement, resizing and Windows' own virtual desktops are driven from the keyboard.
- **Video wallpaper** — a looping MP4, picked from the tray, as the desktop wallpaper.

The animated HTML scenes, the mini scene window, build alerts, the `CosmicWinAlert` CLI and the
local HTTP API were removed from CosmicWin. That feature set is preserved on the branch
`bk/cosmicwin-full`, and the scenes, alerts, HTTP API and mini window live on in the sibling project
CielWin.

## Contents

- [Quick start](#quick-start)
- [Keybindings](#keybindings)
- [Tray menu](#tray-menu)
- [Recommended Windows setting](#recommended-windows-setting)
- [Tiling](#tiling)
- [Video wallpaper](#video-wallpaper)
- [Settings](#settings)
- [Start at logon](#start-at-logon)
- [Prior art](#prior-art) · [Documentation](#documentation) · [Licence](#licence)

## Quick start

Requirements:

- Windows 11 (developed against build 26200)
- .NET 10 SDK

```powershell
git clone <this repo>
cd CosmicWin
./scripts/run.ps1
```

`run.ps1` builds, copies the output to a git-ignored `run/`, and launches that copy elevated — accept
the UAC prompt. Running the copy leaves the build tree unlocked, so builds and tests keep working
while the app is open. Exit from the tray icon.

CosmicWin runs elevated: it manages windows belonging to other processes, and Windows will not allow
that from a normal process.

## Keybindings

Keybindings are compile-time (`CosmicWin.App/Input/ChordTable.cs`); there is no file to remap them.

**Focus and layout**

| Chord | Action |
| --- | --- |
| `Alt` + `H`/`J`/`K`/`L` or arrows | Move focus |
| `Alt+Shift` + direction | Move the window |
| `Alt+Ctrl` + direction | Resize — grows toward a neighbour, shrinks when there is none |
| `Alt+[` / `Alt+]` | Ascend / descend scope, to move a whole group |
| `Alt+O` | Toggle the focused group's split axis |
| `Alt+Q` | Ask the focused window to close — it may refuse, and that is its right |

**Virtual desktops**

| Chord | Action |
| --- | --- |
| `Alt+1`..`Alt+9` | Go to that virtual desktop, creating desktops until it exists |
| `Alt+Shift+1`..`Alt+Shift+9` | Send the focused window there, without following it |
| `Alt+Shift+Q` | Close the desktop you are on — Windows hands its windows to a neighbour |

**CosmicWin itself**

| Chord | Action |
| --- | --- |
| `Alt+T` | Turn tiling on or off — the same switch as the tray's **Modo tiling**, saved to `tiling` |

While tiling is off, the layout is left exactly where it was: focus and resize still work on the
windows' own rectangles, desktop chords and `Alt+Q` work as usual, and move, scope and split-axis
chords do nothing. `Alt+T` always works, so it turns tiling back on too.

Two collisions with Windows itself are worth knowing before you file a bug:

- On a layout with **AltGr** (Spanish, US-International), Windows reports the right Alt as
  `Ctrl+Alt`, so desktop chords answer only to the LEFT Alt — and `AltGr+arrow` is the resize chord.
- **`Alt+Shift` is Windows' default language-switch hotkey.** Every move chord starts with it, so the
  input language will flip unless you set that hotkey to *Not Assigned*.

## Tray menu

Right-click the CosmicWin icon in the notification area.

| Item | What it does |
| --- | --- |
| **Modo tiling** | Tiling on or off; the tick shows the current state. Same switch as `Alt+T`. |
| **Borde de foco** | Draw CosmicWin's own focus border, or not. |
| **Color del borde** | **Elegir...** picks a colour; **Acento del sistema** goes back to the accent colour. |
| **Wallpaper de video...** | Pick an MP4 to loop as the desktop wallpaper — see [Video wallpaper](#video-wallpaper). |
| **Quitar wallpaper de video** | Shown only while a video wallpaper is set. Stops it and brings back Windows' own wallpaper. |
| **Pausar** / **Reanudar** | Stop, or restart, the keyboard hook: while paused no chord works and new windows are not tiled. |
| **Reload** | Re-read the window exception list and `gap` from `settings.conf`. |
| **Salir** | Exit CosmicWin. |

Tiling, the focus border and its colour are saved to `settings.conf` as you change them.

## Recommended Windows setting

Turn off **Settings → System → Multitasking → "Snap windows"**. Windows' own edge snapping competes
with the tree: dragging a window to an edge hands it half the screen behind CosmicWin's back, and the
snap layout flyout appears over a work area CosmicWin has already divided. With it off, a dragged
window does what CosmicWin says it does. The setting takes effect at your next sign-in.

CosmicWin never touches this setting itself — it is yours to set, and it stays set after CosmicWin
exits. Nothing depends on it either: the guards that keep a maximized or self-resizing window from
breaking the layout run the same whether Snap is on or off, because maximize also arrives from the
maximize button, a double click on the title bar, and `Win+Up`.

## Tiling

### What works

- **Tiling** — every window gets a share of the work area, with a configurable uniform gap
  (`gap`, default 8, 0–64 pixels).
- **Focus** — move between windows by direction.
- **Movement** — move a window through the layout. The walk climbs the tree, so a window leaves its
  group when it runs out of room rather than dead-ending, and the walk is reversible.
- **Resize** — grow toward a neighbour, or shrink when there is none.
- **Virtual desktops** — switch by number, send a window to one, each with its own layout. A new
  window opens on the desktop you are on, even when Windows would have put it somewhere else.
- **Dialogs float** — a modal opens centred at its own size and is never tiled. The move chord snaps
  it to half the screen or the whole work area instead of walking the tree, and returns it to the
  size it opened at. Resize does nothing while one holds the foreground, rather than rearranging the
  window behind it.
- **Focus border** — the active window is outlined in the system accent colour, thicker than the
  one Windows draws, with corners matching its own.
- **Windows that fight back** — a window dragged out of its slot snaps back on drop; a window that
  resizes itself is put back; a window that refuses to be positioned is left alone rather than
  fought.
- **No maximizing while tiling** — a tiled window's maximize button is greyed out, which also
  disables the double click on the title bar and `Win+Up`. A window that still gets maximized is
  restored and put back in its slot.

### Maximize while tiling

While tiling is on, CosmicWin disables (greys out) the maximize button of every tiled window that
has one; the button stays visible. It never enables one on a window that never had it, and it leaves
fullscreen (`F11`) alone. A window with no minimize button (rare, some dialogs) shows neither button
while tiled, because Windows draws the two as a pair.

The button is enabled again whenever a window stops being tiled: you turn tiling off (`Alt+T` or the
tray), the window closes, is minimized or hidden, goes fullscreen, is left floating because it does
not fit or will not stay in its slot, or CosmicWin exits normally. Turning tiling back on disables it
again. **Pausar** leaves the buttons as they are — it is a short suspension, not a change of
mode.

Limits worth knowing:

- Apps that draw their own title bar show the disabled button their own way. Chromium browsers
  (Chrome, Edge, Brave) may keep drawing it as if it were active, though clicking it does nothing;
  Electron apps such as VS Code hide it while the window is tiled and show it again when it is not.
  Windows itself refuses the maximize in these apps, so nothing flashes. If an app does manage to
  maximize anyway, CosmicWin restores it and puts it back in its slot.
- CosmicWin runs elevated, so administrator windows are handled like any other. The remaining case
  is a window that refuses the change or does not answer in time: its button stays, and a maximize
  is still undone after the fact when Windows lets CosmicWin restore it.
- If CosmicWin crashes or is killed, it cannot give the buttons back: affected windows keep a
  disabled maximize button until they are reopened.

### Not yet

- **One monitor.** The layout engine is monitor-aware and multi-monitor requirements exist, but
  nothing beyond a single display is exercised or claimed.
- **An emptied virtual desktop is not removed.** Deliberate — see the notes.

## Video wallpaper

Loops a single MP4 as the desktop wallpaper. The video plays whenever `video-wallpaper-path` is set
in `settings.conf`; while it is blank, CosmicWin plays nothing and your desktop background stays
exactly as Windows has it.

The tray menu's **Wallpaper de video...** entry opens a file picker restricted to `.mp4` files. The
picked file is imported into `%LOCALAPPDATA%\CosmicWin\video-wallpaper<ext>` before it plays:

- **Same drive as `%LOCALAPPDATA%`** — the import is a hard link, so switching to a multi-gigabyte
  file is instant. A hard-linked import is the same data as the original, so editing the source file
  in place changes the wallpaper too.
- **Different drive, or linking fails** — CosmicWin falls back to copying the file, which can take
  minutes for a large video.

Either way the wallpaper keeps playing if you later move or delete the original. Re-picking
overwrites the previous import; there is only ever one active video, recorded at
`video-wallpaper-path` in `settings.conf`. Playback goes through Media Foundation.

**Quitar wallpaper de video** stops the video, takes it off the desktop so Windows' own wallpaper
shows again, and blanks `video-wallpaper-path`, so the video does not come back on the next start.
The imported `video-wallpaper<ext>` file stays in `%LOCALAPPDATA%\CosmicWin`; delete it by hand if
you want the space back. Picking a video again works as before.

If Explorer restarts (a crash, or `explorer.exe /restart`), CosmicWin listens for the shell's own
`TaskbarCreated` broadcast and re-attaches the video host to the desktop automatically, without
losing the running video.

## Settings

CosmicWin has no installer: the first time it runs, it writes `%LOCALAPPDATA%\CosmicWin\settings.conf`
itself, with every default already filled in. An existing file is never rewritten except by a
tray-menu change, `Alt+T`, or a hand edit of your own. A hand edit takes effect at CosmicWin's
next start; **Reload** in the tray applies `gap` without one.

| Key | Default | Meaning |
|---|---|---|
| `tiling` | `on` | Lay windows out at all; off leaves them where they open and gives maximize back. |
| `gap` | `8` | Whole pixels of space around and between tiled windows (0–64). |
| `focus-border` | `on` | Draw CosmicWin's own thicker focus border. |
| `border-color` | `accent` | `#RRGGBB`, or `accent` to follow Windows' own accent colour. |
| `video-wallpaper-path` | *(blank)* | Absolute path to the imported video wallpaper. Set by the tray menu, not meant to be hand-edited; blank plays no video. |

### Retired settings

Files written by older versions keep working. Lines for features that no longer exist —
`wallpaper-mode`, `wallpaper-scene`, `wallpaper-fps`, `mini-position`, `mini-corner`, `alerts`,
`alerts-enabled`, `http-server`, `http-server-port`, `alert-http`, `alert-http-port`,
`video-wallpaper-http` and `wallpaper-scene-http` — are skipped like any unknown key, and the next
save rewrites the file with only the current keys.

## Start at logon

CosmicWin does not install itself. Autostart is opt-in, and it is a Scheduled Task rather than a
`Run` registry key because the app must start elevated — a `Run` entry cannot, and would hand you a
UAC prompt at every logon.

```powershell
CosmicWin.exe --install-task     # register the logon task
CosmicWin.exe --uninstall-task   # remove it
```

Both commands do their work and exit immediately; neither starts the window manager. Run them from
an elevated shell — registering a task that runs with highest privileges is itself a privileged
operation.

Four things worth knowing before you rely on it:

- **The task points at the executable you invoked**, resolved at install time. Install from the copy
  you actually run — `run\CosmicWin.exe` after `run.ps1`, not the build tree, which `run.ps1`
  deliberately leaves unlocked. Move or delete that copy and the task starts nothing.
- **Quitting from the tray disables the trigger.** Exiting is read as "not right now", so the task
  stays registered but stops firing, and the next logon is quiet. Re-run `--install-task` to turn it
  back on; it re-registers over the existing task and re-enables the trigger.
- **`--uninstall-task` is idempotent.** A task that was never installed counts as success, so it is
  safe to run twice, or on a machine you are not sure about.
- **The task XML is written to** `%LOCALAPPDATA%\CosmicWin\CosmicWinTask.xml`. It is an artefact of
  installation, not a configuration file — editing it changes nothing until the next install, which
  overwrites it.

## Prior art

Three projects were read while building this one. None is vendored, distributed, or needed to build,
run or test CosmicWin, and no code was copied from any of them.

- **[COSMIC](https://github.com/pop-os/cosmic-epoch)'s `cosmic-comp`** (GPL-3) — the tiling behaviour
  this project models: how a move walks the tree, how a resize splits its intent, and how a new
  window's split axis follows the tile it lands on. Rust to C#, so the algorithms were reimplemented
  by definition.
- **[WinMan](https://github.com/fancywm/winman)** by Veselin Karaganev (MIT) — the shape of the
  window, display and workspace abstractions. MIT is compatible with this project's licence.
- **WinMan.Windows** (GPL-2) — its enumerate-plus-hook window-tracking approach, reimplemented at
  roughly a fifth of the size and a different shape.

## Documentation

[`docs/notes.md`](docs/notes.md) — the things that were only discoverable by
getting them wrong once: how to run the tests and the false red that awaits if you set only one of
the two environment variables, why some diagnostics assert nothing on purpose, and how the
virtual-desktop interop defends itself against an undocumented vtable moving underneath it.

## Licence

MIT. See [LICENSE](LICENSE).

<p align="right">
  <a href="https://github.com/Gentleman-Programming/gentle-ai"><img src="https://raw.githubusercontent.com/Gentleman-Programming/gentle-ai/main/docs/assets/brand/built-with-gentle-ai.png" alt="Built with Gentle-AI" width="180"></a>
</p>
