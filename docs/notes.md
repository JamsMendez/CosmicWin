# CosmicWin — operating notes

Things that are true about this repository and were previously only discoverable by reading source
or by getting them wrong once. Behaviour lives in the code; this file is the map to it.

## Keybindings

Mirrors `CosmicWin.App/Input/ChordTable.cs` (`CreateDefault`). That file is the source of truth —
if the two disagree, the file wins and this table is stale.

| Chord | Action |
| --- | --- |
| `Alt` + `H`/`J`/`K`/`L` or arrows | Move focus |
| `Alt+Shift` + direction | Move the window (or the whole group, after `Alt+[`) |
| `Alt+Ctrl` + direction | Resize — grows toward a neighbour on that side, shrinks when there is none |
| `Alt+[` | Ascend scope (act on the parent group) |
| `Alt+]` | Descend scope |
| `Alt+O` | Toggle the focused group's split axis |
| `Alt+1`..`Alt+9` | Go to that virtual desktop, creating desktops until it exists |
| `Alt+Shift+1`..`Alt+Shift+9` | Send the focused window to that desktop, without following it |

### Two collisions with Windows' own keyboard behaviour

**AltGr is `Ctrl+Alt`, so the right Alt is not the left one.** On a layout that has AltGr — Spanish
(Mexico), US-International — Windows synthesises `Ctrl+Alt` for the right Alt key. `AltGr+1`
therefore arrives as `Alt+Ctrl+1`, matches nothing, and does nothing. Measured, not assumed: the
trace recorded `unmatched chord: Alt, Control+D2`. Two consequences follow. Desktop chords work only
with the LEFT Alt on such a layout, and `Alt+Ctrl+direction` — the resize chord — is by construction
the same key combination as `AltGr+direction`.

**`Alt+Shift` is Windows' default language-switch hotkey**, and every move chord starts with it. The
hook suppresses the matched key but not the modifiers, so Windows sees `Alt+Shift` pressed and
released with nothing in between — exactly the switch gesture. That is why the input language flips
by itself during a run. Turn the hotkey off in Settings → Time & language → Typing → Advanced
keyboard settings → Input language hot keys → *Not Assigned*.

**An unregistered chord is indistinguishable from a broken feature.** The low-level hook only
swallows chords it matches; everything else passes through to whatever has focus. `Ctrl+Shift` +
direction is *not* bound, so pressing it does nothing and looks exactly like a bug. Move is
`Alt+Shift`.

`Alt+[` is not required to move a window out of its group — `MoveNode` walks up the tree on its own
(the reference implementation's ancestor walk). Its remaining purpose is deliberately moving a whole group as
one unit.

## Running the app

```powershell
./scripts/run.ps1
```

`CosmicWin.App.exe` declares `requireAdministrator`, so launching it raises a UAC prompt that must
be accepted by hand — no script can bypass that, and an unelevated shell cannot stop the running
process afterwards either (`Access is denied`; exit it from the tray).

The script exists for a second reason. A running instance holds a file lock on every assembly in the
directory it started from, so launching straight out of `bin\Debug` makes the next build fail:

```
error MSB3027: Could not copy "CosmicWin.Layout.dll" ...
The file is locked by: "CosmicWin.App.exe (24260)"
```

`run.ps1` builds, copies the output to `run/` (git-ignored), and launches *that*. The build tree
stays unlocked, so `dotnet build` and `dotnet test` keep working while the app is open.

## Running the tests

```powershell
$env:COSMICWIN_RUN_DESKTOP_TESTS = '1'
$env:COSMICWIN_DESKTOP_TEST_TERMINAL = 'C:\path\to\Alacritty-v0.17.0.exe'
dotnet test CosmicWin.Layout.Tests/CosmicWin.Layout.Tests.csproj
dotnet test CosmicWin.Interop.Tests/CosmicWin.Interop.Tests.csproj
dotnet test CosmicWin.App.Tests/CosmicWin.App.Tests.csproj
```

Both variables are required together, and **setting only the first produces a false red**: the
real-desktop integration test throws from `SpawnedAlacrittyWindow.ResolveExecutablePath`, and five
Interop tests skip silently while the run still reports "Passed". The terminal path is deliberately
not hardcoded, so a committed test never carries one developer's absolute path. `notepad.exe` cannot
substitute for it: on this Windows build it is a tabbed packaged app and a second launch opens a tab
rather than a second top-level window.

One test is expected to skip: `TaskInstallerElevatedTests` needs elevation.

Run the projects one at a time. The desktop-gated tests spawn real windows and move the real
foreground, so they are serialised within a project and do not tolerate a second project racing
them.

**Close the app first.** A live window manager tiles and activates the very windows those tests
spawn, so they fail on geometry that was never theirs — a misleading red that cost several rounds of
diagnosis. They now detect a running `CosmicWin.App` and SKIP with that reason instead. Skipped is
not passed: a run that reports skips has not verified the desktop facts at all.

## Diagnostics

Four test-shaped files assert nothing on purpose. They answer a question with measured data instead
of a hypothesis, and each was written because a guess had already been wrong once.

| File | Answers |
| --- | --- |
| `CosmicWin.App.Tests/Desktop/DesktopSnapshotDiagnostic.cs` | Which windows does the filter chain actually admit right now? (Found a minimized window holding a whole tile.) |
| `CosmicWin.Layout.Tests/MoveSequenceDiagnostic.cs` | What does the tree look like after each move chord? (Found a degenerate single-child group stranding the window.) |
| `CosmicWin.Interop.Tests/Win32/FrameBoundsDiagnostic.cs` | How far is the drawn frame from `GetWindowRect`? (Measured the 7px invisible border, 0 on top.) |
| `CosmicWin.Interop.Tests/Win32/VirtualDesktopProbeDiagnostic.cs` | Does this Windows build expose the virtual-desktop vtable we declare? |

A fourth trace is written by the running app rather than a test:
`%LOCALAPPDATA%\CosmicWin\desktop-trace.log` records every virtual-desktop chord — action, count
and index before and after, and the error — plus any chord that matched NO entry, which is otherwise
invisible and reads exactly like a broken feature.

Run one with the detailed logger, or its output is swallowed:

```powershell
dotnet test <project> --filter "FullyQualifiedName~DesktopSnapshotDiagnostic" --logger "console;verbosity=detailed"
```

## Reference material

Behaviour was modelled on other window managers, read and reimplemented rather than copied. Who
they are and why is in the root [README](../README.md); the code no longer repeats it, and it names
no upstream project.

Optional checkouts can live under `docs/reference/`, which is git-ignored and never distributed --
nothing here needs them to build, run or test. One translation trap is worth carrying regardless:
the `Orientation` naming is INVERTED between the two projects. The reference implementation's
`Orientation::Vertical` measures width and means side-by-side, which is CosmicWin's
`SplitAxis.Horizontal`. Translate deliberately.

## Virtual desktops

Windows' own desktops, addressed by POSITION — `CreateDesktop` appends at the end and desktops carry
no durable number, so "desktop 3" means "the third one". An emptied desktop is NOT auto-deleted.

**A layout belongs to a desktop.** `TreeManager` holds one tree per (monitor, desktop) pair, and
`CurrentDesktop` decides which one the rest of the app sees. A window is filed under the desktop it
is actually on — which is not always the one being viewed — and a tree on a hidden desktop is never
repositioned, because moving windows nobody can see applies geometry that may be stale by the time
they are.

Two separate causes had to be fixed for that to hold. `Win32Workspace.Poll` read "absent from the
enumeration" as "destroyed", and DWM cloaks every window on the desktop being left, so the tree was
dismantled on the way out and rebuilt in enumeration order on the way back. Fixing that made the
tree SURVIVE; the per-desktop key is what stops every desktop's windows from being laid out
together.

`MoveWindowToDesktop` is documented but useless here: it moves only windows owned by the CALLING
process and returns `E_ACCESSDENIED` otherwise, and a window manager owns none of the windows it
manages. `GetWindowDesktopId` on the same interface DOES work cross-process — measured, because on
this interface "documented" plainly does not imply "works for other processes".

Microsoft publishes no API for creating or switching desktops, and that is deliberate:
`IVirtualDesktopManager` has three methods, none of which can, and its own remarks say applications
"should avoid automatically switching the user from one virtual desktop to another". Moving a window
DOES use that documented interface. Only create and switch use the undocumented
`IVirtualDesktopManagerInternal`, declared by hand in `CosmicWin.Interop/Win32/VirtualDesktops/` —
no third-party package, no runtime code generation, and the COM object comes from the already-running
shell.

**The declaration order in those interfaces IS the vtable.** A Windows update that inserts or
reorders a method produces no compile error and no exception — later calls silently dispatch to the
wrong function, in an elevated process. Two defences, and neither is optional:

- Every member CosmicWin does not call is a slot holder with a deliberately wrong signature. It
  holds its position and cannot be invoked by accident. `RemoveDesktop` is one of them.
- `VirtualDesktopProbe` runs once and makes three independent slots corroborate each other —
  `GetCount`, `GetDesktops` and `GetCurrentDesktop` must all agree. A shifted vtable can satisfy any
  one by luck. Failing that check makes the feature inert rather than wrong.

`VirtualDesktopVTableTests` re-proves the layout across MORE than one desktop, arranging the extra
one through Windows' documented `Win+Ctrl+D` rather than through `CreateDesktop` — verifying a
mutator by calling it would be circular. It cleans up after itself, and your screen will flash to a
new desktop and back while it runs.

Verified on OS 10.0.26200 (the build-26100 layout still holds).

## Spacing

`TreeArranger.Gap` is the single spacing knob and **defaults to zero**; `AppComposition` opts
production in at `TreeArranger.DefaultGap` (8px). Zero is the default because every geometry fact in
the suite asserts tiling arithmetic, not spacing — giving it a non-zero default changed the expected
rectangle of 27 tests at once.

Tests must never assign it. xUnit runs test classes in parallel, so mutating that static races every
other class's geometry assertions; use the explicit-gap overload of `ArrangeAndPosition` instead.

## Licence

MIT. Two facts make that the right fit rather than a default.

Every line of product source here is ORIGINAL. Reference projects were read, never copied, and none
of them is committed -- `.gitignore` excludes `docs/reference/`, so this repository distributes only
its own code. That matters for two of them -- one is GPL-3 and another GPL-2 -- and not for the
third, which is MIT and would have been compatible anyway. The README says which is which.

The single dependency is `Microsoft.Windows.CsWin32`, itself MIT and a source generator with no
runtime surface (see `CosmicWin.Interop.csproj`). The virtual-desktop support adds no dependency at
all -- the COM object comes from the shell that is already running.

## Signing

Unsigned, CosmicWin shows UAC's orange **"Unknown publisher"** prompt, because it requests
administrator. That is a different warning from SmartScreen, which appears when someone DOWNLOADS
the binary and never for one built locally. Signing addresses both, but not equally: UAC names the
publisher immediately, while SmartScreen has to build a reputation for the certificate first.

**A self-signed certificate is worse than nothing here.** It is trusted only where its root was
installed by hand, so it changes nothing for anyone else while looking like protection.

Two routes that actually work:

| | Cost | Gate |
| --- | --- | --- |
| [SignPath Foundation](https://signpath.org/terms.html) | Free for OSS | OSI licence, no proprietary components, actively maintained, and **already publicly released** |
| [Azure Artifact Signing](https://azure.microsoft.com/en-us/pricing/details/trusted-signing/) | ~$9.99/month | Identity validation; individuals supported in the USA and Canada |

CosmicWin meets every SignPath condition except the release one, which is simply the next step:
MIT is OSI-approved, the only dependency is build-time and MIT, and nothing here is proprietary.

Once a certificate exists:

```powershell
dotnet build -c Release
./scripts/sign.ps1 -Thumbprint <cert thumbprint>   # or -PfxPath <file.pfx>
```

The script signs only this project's own binaries -- signing a dependency would put this project's
name on code it did not write -- always timestamps, and verifies the result afterwards rather than
trusting signtool's exit code. The timestamp is not optional: without one the signature stops being
valid the day the certificate expires.
