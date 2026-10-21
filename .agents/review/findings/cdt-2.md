# cdt-2: the installer skips the missing-binary guard when there is no database to back up

**Severity**: MEDIUM. A fresh install, or an instance that has never started, with
`ExchangeAdminWeb.dll` absent from staging after publish is mirrored into the publish folder
anyway. No data is lost, but the installer cuts over an unusable payload and reports success.
Plan AC4 names the installer explicitly.
**Status**: Admitted; fix pending
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`
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

`<pending>`
