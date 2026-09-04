# scdi-3: Cutover script reports success when both appsettings point at a missing shared DB

**Severity**: MEDIUM - after a deleted or never-created shared file, re-running the
cutover script prints "nothing to do" and exits 0 while both instances will refuse to
start on the missing configured file.
**Status**: Verified
**Branch**: -
**Commit**: the commit that completes this record; read it from `git log -1 -- .agents/review/findings/scdi-3.md`

## Evidence

`tools/Move-ConfigDbToShared.ps1:305-326`: resolves the current dev and prod DB paths
and returns immediately when both equal `-SharedDbPath`; the shared-file existence
check sits below that return (`:331-335`). Trigger: both `appsettings.json` files
already carry `ConfigStore:Path` equal to the target, but the shared database file is
absent.

## Predicted observable failure

"Both instances already name ... nothing to do", exit 0; then both app pools fail
startup with the must-exist error (scd-1) and the operator's one recovery tool has
just told them everything is fine.

## What

The idempotent no-op shortcut was placed ahead of the invariant it should have
verified.

## Approach

A new `Assert-SharedDbUsable` runs INSIDE the "both already name the shared path" branch,
before its `return`: the file must exist and pass `Test-SqliteConfigDbIntegrity` (from
`SqliteConfigBackup.psm1`), else it prints the same return-to-two-databases commands the
cutover prints (`Write-RestoreInstructions`, pointed at the newest `dev.exchangeadmin.*.db`
/ `prod.exchangeadmin.*.db` under `-BackupRoot` and the newest
`appsettings.json.pre-shared.*.bak`, with placeholders when none are found) and
`Write-Fail`s naming the path and the problem. It runs in plan mode too - the check is
read-only, and a missing configured file is a failure to report, never "nothing to do".
The success line now says the file passed `PRAGMA integrity_check`. The
not-yet-shared path's own "file already exists" refusal below is unchanged.

## Files changed

- `tools/Move-ConfigDbToShared.ps1:294-331` - `Assert-SharedDbUsable` (exists + integrity, restore commands, `Write-Fail` naming the path)
- `tools/Move-ConfigDbToShared.ps1:366-372` - the no-op shortcut calls it before returning; success wording names the integrity check
- `tests/ps/MoveConfigDbToShared.Tests.ps1:11-20` - discovery-time sqlite3 resolution (same fallback as `SqliteConfigBackup.Tests.ps1`) for the two integrity rows
- `tests/ps/MoveConfigDbToShared.Tests.ps1:198-246` - context "both instances already name the shared path": missing file fails naming the path in default (plan) mode; garbage file fails naming `integrity_check`; a real database is the only "nothing to do"

## Guard proof

- `tests/ps/MoveConfigDbToShared.Tests.ps1::fails naming the path when the shared file does not exist (plan mode, scdi-3)`
  and `::fails naming the path when the shared file is not a sound database (scdi-3)` -
  mutation A: delete the `Assert-SharedDbUsable ...` call so the shortcut returns first
  (the shipped shape) -> both FAIL ("got $null" - the script exited 0); restore -> 77/77.
- `::fails naming the path when the shared file is not a sound database (scdi-3)` -
  mutation B: replace `Test-SqliteConfigDbIntegrity -DbPath $Path | Out-Null` with a no-op
  (existence-only check) -> 1 FAIL; restore -> 77/77.
- `::reports nothing to do only when the shared file exists and passes integrity_check`
  pins the no-op branch still reachable (a real sqlite3-created file passes).
- ScriptAnalyzer 0 errors; .NET suite unchanged at 2322/0/3 (no C# in this commit).

## Coder dispute (if any)

None.

## Known gaps

None.

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
  (same dispatch as scdi-1)
Harness: codex-cli 0.152.1. Reviewed `8d9ccb0..b9e944f`, verdict `findings`,
capability_ok true. Envelope `.agents/review/scd-impl.result.json`.
