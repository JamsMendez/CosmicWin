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
- **Virtual desktops** — switch by number, send a window to one, each with its own layout.
- **Windows that fight back** — a window dragged out of its slot snaps back on drop; a window that
  resizes itself is put back; a window that refuses to be positioned is left alone rather than
  fought.

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

## Keybindings

| Chord | Action |
| --- | --- |
| `Alt` + `H`/`J`/`K`/`L` or arrows | Move focus |
| `Alt+Shift` + direction | Move the window |
| `Alt+Ctrl` + direction | Resize — grows toward a neighbour, shrinks when there is none |
| `Alt+[` / `Alt+]` | Ascend / descend scope, to move a whole group |
| `Alt+O` | Toggle the focused group's split axis |
| `Alt+1`..`Alt+9` | Go to that virtual desktop, creating desktops until it exists |
| `Alt+Shift+1`..`Alt+Shift+9` | Send the focused window there, without following it |

Two collisions with Windows itself are worth knowing before you file a bug:

- On a layout with **AltGr** (Spanish, US-International), Windows reports the right Alt as
  `Ctrl+Alt`, so desktop chords answer only to the LEFT Alt — and `AltGr+arrow` is the resize chord.
- **`Alt+Shift` is Windows' default language-switch hotkey.** Every move chord starts with it, so the
  input language will flip unless you set that hotkey to *Not Assigned*.

## Relationship to COSMIC

Behaviour is modelled on [COSMIC](https://github.com/pop-os/cosmic-epoch)'s `cosmic-comp`, and the
Win32 window-tracking shape follows [FancyWM](https://github.com/FancyWM/fancywm)'s WinMan. Both were
**read and reimplemented, never copied** — their licences are incompatible with this one, and neither
is vendored, distributed or required to build. Where an algorithm was ported the source is cited in
the code, because knowing where a rule came from is what makes it maintainable.

## Documentation

[`docs/operating-notes.md`](docs/operating-notes.md) — the things that were only discoverable by
getting them wrong once: how to run the tests and the false red that awaits if you set only one of
the two environment variables, why some diagnostics assert nothing on purpose, and how the
virtual-desktop interop defends itself against an undocumented vtable moving underneath it.

## Licence

MIT. See [LICENSE](LICENSE).
