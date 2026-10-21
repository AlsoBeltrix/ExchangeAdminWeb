# cdt-4: the old-binary Pester fixtures depend on what happens to be in $PSHOME

**Severity**: LOW. Test reliability only. Two tests pick "any DLL in `$PSHOME` whose file
version is below 2.34" as a stand-in for a pre-db-mode app; a runner image whose PowerShell
folder has no such DLL fails the test for a reason unrelated to the resolver.
**Status**: Fixed on master; awaiting verdict
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

Fix as the Approach, tests only: both tests now write a short text file named
`ExchangeAdminWeb.dll` (`'not an assembly'`) into their temp folder instead of copying a DLL
picked from `$PSHOME`. `FileVersionInfo` reads 0/0/0 for it, so both resolvers see `0.0.0`,
below the 2.34.0 floor. No production change.

Neither old test asserted "nothing was executed" directly, and neither relied on the fixture
being a real DLL. The backup test calls only `Resolve-ConfigDbTool`, which never executes
anything; it now also pins the version it read (`version 0.0.0`). The jobs test's not-run
evidence is its warning: the not-found warning is returned before `&` runs, whereas running
the fixture yields "Could not read the bulk jobs database" (seen under M2 below).

| Test | Pins |
| --- | --- |
| `tests/ps/SqliteConfigBackup.Tests.ps1` `refuses an app binary older than the db mode, before running it` | `Resolve-ConfigDbTool` throws `*version 0.0.0, older than the backup tool*deploy dev first*` for the fixture |
| `tests/ps/JobStateWarning.Tests.ps1` `warns and returns nothing, without running it, when the app binary predates the db mode` | `Get-ActiveBulkJobs` returns nothing and warns `*version 2.34.0 or later*` (the not-run branch) |

Mutations at the call site, run, restored, touched, restored md5 re-confirmed equal to the
original:

| Mutation | md5 before -> after | Failed |
| --- | --- | --- |
| M1: `tools/SqliteConfigBackup.psm1` `if ($version -lt $script:ConfigDbToolMinimumVersion) {` -> `if ($false) {` | `31598F541E7F0D8863F689FA3BDBA6EE` -> `A952AE205D0F361ACB5D020DCD96B82F` | backup test: "Expected an exception ... to be thrown, but no exception was thrown" (fails on the condition, not the text) |
| M2: `tools/JobStateWarning.psm1` `if ($version -lt [version]'2.34.0') { return $null }` removed | `5E48E40D633BA8B7FE8081957624EAF0` -> `C9E00F71D9D37F8E9BC7C41B533BFD64` | jobs test: the fixture was executed through dotnet (exit -2147450749, hostpolicy.dll not found) and the warning was "Could not read the bulk jobs database", not the not-found one |
