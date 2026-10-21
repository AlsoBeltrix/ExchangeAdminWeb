# cdt-4: the old-binary Pester fixtures depend on what happens to be in $PSHOME

**Severity**: LOW. Test reliability only. Two tests pick "any DLL in `$PSHOME` whose file
version is below 2.34" as a stand-in for a pre-db-mode app; a runner image whose PowerShell
folder has no such DLL fails the test for a reason unrelated to the resolver.
**Status**: Admitted; fix pending
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`
**Reviewer**: codex / configured default (gpt-5.5-dzs via gateway) / xhigh / standard, over `9e7f9f9..b1577f7`, 2026-10-21

## Evidence

`tests/ps/SqliteConfigBackup.Tests.ps1` ("refuses an app binary older than the db mode") and
`tests/ps/JobStateWarning.Tests.ps1` ("warns and returns nothing ... predates the db mode")
both `Get-ChildItem $PSHOME -Filter *.dll` and filter on version.

## Approach

A deterministic fixture: an empty (or short text) file named `ExchangeAdminWeb.dll`.
`FileVersionInfo` reports major/minor/build 0 for it, the resolver builds version `0.0.0`,
which is below the floor, and throws the "older than the backup tool" message before
executing anything - verified by reading `Resolve-ConfigDbTool`. The jobs test uses the same
fixture and expects the soft warning. No production change.

## Guard proof

`<pending>`
