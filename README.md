<img src="docs/image/icon-256.png" alt="CosmicWin" width="120" align="right" />

# CosmicWin

A tiling window manager for Windows 11, modelled on COSMIC's tiling behaviour.

Windows are arranged in a tree and given the whole work area between them. Focus, movement, resizing
and Windows' own virtual desktops are driven from the keyboard.

## What works

- **Tiling** — every window gets a share of the work area, with a configurable uniform gap.
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
- **Start at logon** — an opt-in Scheduled Task starts CosmicWin elevated when you log on, without a
  UAC prompt every time.

## What it does not do yet

- **One monitor.** The layout engine is monitor-aware and multi-monitor requirements exist, but
  nothing beyond a single display is exercised or claimed.
- **An emptied virtual desktop is not removed.** Deliberate — see the notes.
- **No configuration file.** Keybindings and the gap are compile-time.

## Requirements

- Windows 11 (developed against build 26200)
- .NET 10 SDK

CosmicWin runs elevated: it manages windows belonging to other processes, and Windows will not allow
that from a normal process.

## Build and run

```powershell
git clone <this repo>
cd CosmicWin
./scripts/run.ps1
```

`run.ps1` builds, copies the output to a git-ignored `run/`, and launches that copy elevated — accept
the UAC prompt. Running the copy leaves the build tree unlocked, so builds and tests keep working
while the app is open. Exit from the tray icon.

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

## Keybindings

| Chord | Action |
| --- | --- |
| `Alt` + `H`/`J`/`K`/`L` or arrows | Move focus |
| `Alt+Shift` + direction | Move the window |
| `Alt+Ctrl` + direction | Resize â€” grows toward a neighbour, shrinks when there is none |
| `Alt+[` / `Alt+]` | Ascend / descend scope, to move a whole group |
| `Alt+O` | Toggle the focused group's split axis |
| `Alt+Q` | Ask the focused window to close â€” it may refuse, and that is its right |
| `Alt+1`..`Alt+9` | Go to that virtual desktop, creating desktops until it exists |
| `Alt+Shift+1`..`Alt+Shift+9` | Send the focused window there, without following it |
| `Alt+Shift+Q` | Close the desktop you are on â€” Windows hands its windows to a neighbour |

Two collisions with Windows itself are worth knowing before you file a bug:

- On a layout with **AltGr** (Spanish, US-International), Windows reports the right Alt as
  `Ctrl+Alt`, so desktop chords answer only to the LEFT Alt — and `AltGr+arrow` is the resize chord.
- **`Alt+Shift` is Windows' default language-switch hotkey.** Every move chord starts with it, so the
  input language will flip unless you set that hotkey to *Not Assigned*.

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

Copyright covers expression, not ideas or algorithms, and the code here shares no expression with
any of them: a line-level comparison against the whole reference tree finds six identical lines out
of 830, every one of them boilerplate that any Windows C# project writes the same way — the required
signatures of a WPF startup override and a Win32 hook procedure, the canonical way to locate
AppData, a standard guard clause, and two interface declarations from the MIT-licensed part.

## Documentation

[`docs/notes.md`](docs/notes.md) — the things that were only discoverable by
getting them wrong once: how to run the tests and the false red that awaits if you set only one of
the two environment variables, why some diagnostics assert nothing on purpose, and how the
virtual-desktop interop defends itself against an undocumented vtable moving underneath it.

## Licence

MIT. See [LICENSE](LICENSE).
