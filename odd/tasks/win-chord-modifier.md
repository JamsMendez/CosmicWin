# Win as the chord modifier

Objective: an optional tray switch that moves every chord from Alt to Win, and while on, disables Windows' manual lock (`DisableLockWorkstation`) so Win+L reaches CosmicWin.
Branch: `feat/win-chord-modifier`. Delivery: ask-on-risk. Test runner: `dotnet test` (xUnit).

## Specs

- S1. "agreguemoslo como opcion en tiling de CosmicWin": a ticked tray item next to "Modo tiling" turns Win-as-modifier on and off. Persisted in `settings.conf` (`chord-modifier = alt|win`, default `alt`).
- S2. "activar el Win con una confirmacion de que se desactiva el LockWorkStation()": turning it ON first shows a confirmation that says manual locking (Win+L, the Lock menu entries, `LockWorkStation()`) is disabled while it is on. Declining changes nothing.
- S3. While on, every chord in the table answers to Win instead of Alt (Win+H, Win+Shift+H, Win+Ctrl+H, Win+1..9, Win+T, ...); Alt combinations go back to the applications. Off, behaviour is exactly today's.
- S4. Releasing Win after a chord does not open the Start menu.
- S5. Lock policy is owned safely: CosmicWin restores the previous `DisableLockWorkstation` value when the switch goes off and on clean exit; a start after an unclean exit reconciles (re-applies when on, restores when off). A failed registry write leaves the switch off and says so.

## Tasks

- T1 [S3,S4] hook: Win bit, translation, Start-menu mask — inline — done 7bab6cc (RED: compile; GREEN: Input 104/105, 1 pre-existing skip)
- T2 [S5] lock policy guard + marker — inline — done 2e721ca (RED: compile; GREEN 7/7)
- T3 [S1,S2,S5] settings key, tray item + confirmation, composition wiring, README — inline — done 6483e26 (RED: compile; GREEN full suite App 1070 / Interop 252 / Layout 208)
- T5 [S5] review follow-ups: lock record inside the protected key + kind preserved (2511944); failures traced, restore in finally, stale mask disarmed (f0e2d20) — inline — done (RED 4 failing -> GREEN; suite App 1077 / Interop 252 / Layout 208; hardware 10/10 rerun)
- T4 [S1-S5] independent verification (Risk item 2) + automated hardware check — done (verifier: no blockers; hardware 10/10). Physical Win+L press and the tray confirmation dialog remain for the maintainer.

## Log

- L1 (2026-10-31, user): "Vamos a implementarlo pero agreguemoslo como opcion en tiling de CosmicWin, activar el Win con una confirmacion de que se desactiva el LockWorkStation()"
- L2: context from the same session: "Podemos usar Autohotkey v2 para poder usar la tecla Win en lugar de Alt en nuestros atajos de teclado." Decided natively instead of AHK (the app already owns a WH_KEYBOARD_LL hook). Evidence gathered: wake-from-sleep sign-in is a separate setting and survives `DisableLockWorkstation`.
- L3: Risk item 2 (security: disables the OS manual lock) — independent verifier required; hardware check required (registry ACL, immediate effect, Win+L reaching the hook, Start menu mask).
- L4: `Serialize_WritesOnlyTheNewKeys` updated to include `chord-modifier` — an intended new key, not a regression.
- L5: Registry ACL measured: the user token has only ReadKey on `HKCU\Software\Microsoft\Windows\CurrentVersion\Policies`; `Policies\System` does not exist on this machine (no prior value). Writes work only because CosmicWin runs elevated (requireAdministrator manifest). Corrected the RegistryLockPolicyStore comment that claimed no elevation was needed.
- L6: Independent verifier: S1-S5 PASS, no blockers; exhaustive differential (256 keys x every modifier set) against 7ce5c1a: 0 differences in Alt mode, Win mode identical to baseline Alt. Advisories kept open (not fixed): a failed TryRestore and a failed startup TryDisable are silent (no trace line); a non-DWORD prior value is deleted rather than restored; over-the-shoulder elevation writes the admin's hive; a policy change made by the user while CosmicWin owns it is overwritten on restore.
- L7: Automated hardware check (Release from run/, injected input via SendInput, own WinForms probes, injection aborted unless a probe owns the foreground): Alt+L moves focus; Alt mode leaves the policy alone; stored win disables the lock at startup (policy 1, marker present); Win+L moves focus without locking; releasing Win after the chord does not open Start; Win+H moves focus; Alt+L is no longer a chord in Win mode; control: a lone Win tap does open Start; a kill leaves the policy at 1 (documented); a start with alt restores it (value absent, marker gone). Injected Win+L is not proof for a physical press.
- L8 (2026-10-31, user): "Sí, corrígelos en esta rama" — about the non-blocking review findings (marker confused deputy, non-DWORD prior deleted, silent restore/disable failures, Dispose throw skipping restore, stale mask).
- L9: Native review (risk + reliability) of 7ce5c1a..63e8843 approved and acknowledged; f2c8de5 removed a BOM it flagged. The confused deputy was real: an unelevated same-user program could write the LocalAppData marker and make the elevated restore write 1 into the admin-only policy. Fixed by keeping ownership (`CosmicWinOwnsDisableLockWorkstation`) and the prior value with its kind (`CosmicWinPriorDisableLockWorkstation`) in HKCU Policies\System itself.
