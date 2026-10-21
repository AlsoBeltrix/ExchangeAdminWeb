# cdt-1: backup cleanup can delete a verified backup another run just wrote

**Severity**: MEDIUM. Needs two `db backup` runs racing on one destination, but the file at
the destination is the deploy's only rollback snapshot and S2's PowerShell will trust exit 0.
**Status**: Admitted; fix pending
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`
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

`<pending>`
