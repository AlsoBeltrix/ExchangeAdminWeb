# cdt-1: backup cleanup can delete a verified backup another run just wrote

**Severity**: MEDIUM. Needs two `db backup` runs racing on one destination, but the file at
the destination is the deploy's only rollback snapshot and S2's PowerShell will trust exit 0.
**Status**: Closed on coder-side guard proof (MEDIUM; no reviewer verification round per decisions 2026-08-31)
**Branch**: -- (direct-to-main)
**Commit**: `872f211`
**Reviewer**: codex / configured default (gpt-5.5-dzs via gateway; id not emitted in envelope) / xhigh / standard, over `6960b09..86970a5`, 2026-10-21

## Evidence

`Services/Storage/ConfigDbMaintenance.cs` (S1, `86970a5`): `Backup` prechecks that the
destination and its sidecars do not exist (line ~59), then runs `VACUUM INTO dest`, then
integrity-checks `dest`. Any exception in that block reaches `catch { DeletePartial(dest); throw; }`
(line ~87), and `DeletePartial` deletes `dest` and its sidecars (line ~211).

Trigger: two invocations target the same destination and both pass the precheck. The first
writes and verifies the file and returns exit 0 with the path. The second's `VACUUM INTO`
fails because the destination now exists, enters the catch, and deletes the first run's
verified backup. Verified by reading the code: nothing distinguishes "a file this process
created" from "a file that was already there".

## Approach

`VACUUM INTO` a unique temp file in the destination directory (`<dest>.tmp-<guid>`),
integrity-check the temp, then `File.Move(tmp, dest, overwrite: false)`. On any failure
delete only the temp this process owns; if `dest` exists at move time, fail with "destination
appeared" and leave it. Same shape `Copy-SqliteDbFile` already uses on the PowerShell side.

## Guard proof

Fix as the Approach: `Backup` copies to `<dest>.tmp-<guid>`, integrity-checks the temp, then
`File.Move(temp, dest, overwrite: false)`; a move that fails because dest exists throws
`ConfigDbMaintenanceException` ("Destination appeared during backup, left untouched"). The
catch deletes only the temp and its sidecars. Precheck kept. Test seam: internal overload
`Backup(source, dest, Action<string>? afterPrecheck)`, run between precheck and copy.

| Test (`ExchangeAdminWeb.Tests/ConfigDbMaintenanceTests.cs`) | Pins |
| --- | --- |
| `Backup_WhenDestinationAppearsAfterPrecheck_ThrowsAndLeavesItUntouched` | Hook writes known bytes to dest after the precheck; Backup throws; dest still has those exact bytes; no `*.tmp-*` left |
| `Backup_OfCorruptSource_LeavesNoTempFile` | Integrity failure leaves no `*.tmp-*` and no dest |
| `Backup_OnSuccess_AddsOnlyTheDestination` | A good backup adds exactly dest to the directory, nothing else |

Mutations at the call site, each applied to `Services/Storage/ConfigDbMaintenance.cs`
(original md5 `AD1D01EB3824D588F846A918B002C77F`, pre-comment), rebuilt, run, restored,
touched, restored md5 re-confirmed equal to the original:

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| M1: catch `DeletePartial(temp)` -> `DeletePartial(dest)` (old behaviour) | `7559CA913999E30B2CF0A7CE09A42D1E` | race test; corrupt-source no-temp test |
| M2: `overwrite: false` -> `overwrite: true` | `F3D3B03A73CA31C69185DB5673297543` | race test |
| M3: catch `DeletePartial(temp)` removed | `1C4B3625B78F9BFE8E0EA8CF8D18464B` | corrupt-source no-temp test; race test |
