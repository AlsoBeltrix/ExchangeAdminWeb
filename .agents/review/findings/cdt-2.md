# cdt-2: the installer skips the missing-binary guard when there is no database to back up

**Severity**: MEDIUM. A fresh install, or an instance that has never started, with
`ExchangeAdminWeb.dll` absent from staging after publish is mirrored into the publish folder
anyway. No data is lost, but the installer cuts over an unusable payload and reports success.
Plan AC4 names the installer explicitly.
**Status**: Closed on coder-side guard proof (no reviewer verification round below CRITICAL, decisions 2026-08-31)
**Branch**: -- (direct-to-main)
**Commit**: `96af3aa`
**Reviewer**: codex / configured default (gpt-5.5-dzs via gateway) / xhigh / standard, over `9e7f9f9..b1577f7`, 2026-10-21

## Evidence

`tools/Install-ExchangeAdminWeb.ps1` (S2, `b1577f7`), the backup step: when
`Test-IsSqliteConfigDbPresent` is false and no `ConfigStore:Path` is configured, the block
writes "nothing to back up" and `return`s. The action-mode `Resolve-ConfigDbTool -AppPath
$stagingPath` sits below that return, so it only runs when a database exists. `deploy.ps1`
got a fail-fast resolve right after publish in S2; the installer did not.

## Approach

Resolve the tool from `$stagingPath` in action mode once, immediately after the publish
step and before the backup/no-backup branch. Keep the "no database, nothing to back up"
path; it skips the backup, not the payload check. Guard: `InstallerConfigBackup.Tests.ps1`
asserts the action-mode resolve precedes the backup block and is not inside its
no-database branch; mutate by moving it back below the return.

## Guard proof

Fix as the Approach: `tools/Install-ExchangeAdminWeb.ps1` gains one step right after the
publish step and before the `if ($SkipConfigBackup)` backup/skip decision:
`Invoke-PlanOrAction "Confirm the published build carries the config DB tool at ..." {
Resolve-ConfigDbTool -AppPath $stagingPath | Out-Null }`. The redundant action-mode resolve
inside the backup block (below the no-database `return`) is removed; `Backup-SqliteConfigDb
-AppPath $stagingPath` still resolves the same folder internally. The plan-mode
`$configDbToolPlan = Resolve-ConfigDbTool -AppPath $stagingPath -PlanOnly` is unchanged. The
step also now covers a `-SkipConfigBackup` run, which previously never checked the payload.

The old test "checks for an existing database before demanding the app binary" pinned the
buggy order (presence check before the resolve) and is replaced by the guard below.

| Test (`tests/ps/InstallerConfigBackup.Tests.ps1`) | Pins |
| --- | --- |
| `checks the published build for the app binary before deciding whether there is a database to back up` | Parses the installer AST: exactly one action-mode `Resolve-ConfigDbTool -AppPath $stagingPath` (no `-PlanOnly`); it starts after the publish `Invoke-PlanOrAction` ends and before the `if ($SkipConfigBackup)` statement; no ancestor is the backup `Invoke-PlanOrAction` or that `if`; its enclosing command is `Invoke-PlanOrAction` (Invariant 4) |
| `treats a missing app binary as fatal, not as a warning` (existing) | Still green: an action-mode resolve from staging exists and the backup runs from staging |

Mutation at the call site, on `tools/Install-ExchangeAdminWeb.ps1` (original md5
`567A01A662F69FC775C976F6BC4E4E9C`, pre-comment), run, restored, touched, restored md5
re-confirmed equal to the original:

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| New step removed; `Resolve-ConfigDbTool -AppPath $stagingPath \| Out-Null` put back below the no-database `return` (the pre-fix shape) | `334AD6A11BE0ADFF3D368CC9652204AD` | `checks the published build for the app binary before deciding whether there is a database to back up` ("Expected the actual value to be less than 36294, but got 38421": the resolve sits after the skip/backup decision) |
