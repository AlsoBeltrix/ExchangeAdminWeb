# scdi-3: Cutover script reports success when both appsettings point at a missing shared DB

**Severity**: MEDIUM - after a deleted or never-created shared file, re-running the
cutover script prints "nothing to do" and exits 0 while both instances will refuse to
start on the missing configured file.
**Status**: Open
**Branch**: -
**Commit**: (filled by the fix)

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

(To be filled by the fix.) Direction: before the early return, require the shared file
to exist and pass `Test-SqliteConfigDbIntegrity`; otherwise `Write-Fail` with the
restore-from-backup instructions the script already prints on other failures. Pester:
a fake pair of publish dirs whose appsettings both name a non-existent shared path
makes the script fail (plan mode included), and the message names the path.

## Files changed

(filled by the fix)

## Guard proof

(filled by the fix)

## Coder dispute (if any)

None.

## Known gaps

None.

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
  (same dispatch as scdi-1)
Harness: codex-cli 0.152.1. Reviewed `8d9ccb0..b9e944f`, verdict `findings`,
capability_ok true. Envelope `.agents/review/scd-impl.result.json`.
