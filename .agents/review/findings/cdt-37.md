# cdt-37: the installer refuses a forward-slash -ConfigStorePath the app and the read side accept

**Severity**: LOW. `tools/Install-ExchangeAdminWeb.ps1` validates `-ConfigStorePath` with its own
copy of the old writers' rule - a drive letter, a colon and a BACKSLASH - so
`-ConfigStorePath D:/inetpub/ExchangeAdminWebShared/config/exchangeadmin.db` is refused ("must be
an absolute local file path (drive letter, colon, backslash ...)"), although the app
(`ConfigStorePath.Resolve`) opens that file and the scripts' reader (`tools/SqliteConfigBackup.psm1`,
cdt-30) reads that key back. A legitimate first configuration of a shared database is refused until
the operator respells it. The installer's copy is also case-insensitive (`-notmatch`), so it passes
a Kelvin-sign "drive letter" the app refuses; the existence check stops that one later. From the
codex review of the cdt-28..cdt-34 merge (ops-batch-4), extending cdt-30.
**Status**: Closed - codex group review (batch-5, 2026-10-21) raised no defect in this change
**Branch**: `worktree-agent-ae79adccaaf3eeb47` (worktree `agent-ae79adccaaf3eeb47`)
**Commit**: `cc998bb` (merged)
**Reviewer**: codex / gpt-5.5-dzs / xhigh, ops-batch-4, 2026-10-21

## Evidence

`tools/Install-ExchangeAdminWeb.ps1` at `0d040ed`:

```
229: function Assert-LocalAbsoluteFilePath {
...
234:     param([string]$Path, [string]$Name)
236:     if ($Path.StartsWith('\\') -or $Path.StartsWith('//')) {
237:         Write-Fail "$Name must be a local path, not a network share: $Path"
238:     }
239:     if ($Path -notmatch '^[A-Za-z]:\\') {
240:         Write-Fail "$Name must be an absolute local file path (drive letter, colon, backslash - for example D:\inetpub\ExchangeAdminWebShared\config\exchangeadmin.db): $Path"
241:     }
...
730: Import-Module (Join-Path $PSScriptRoot 'SqliteConfigBackup.psm1') -Force
...
737: if (-not [string]::IsNullOrWhiteSpace($ConfigStorePath)) {
738:     Assert-LocalAbsoluteFilePath -Path $ConfigStorePath -Name 'ConfigStorePath'
739:     $ConfigStorePath = [System.IO.Path]::GetFullPath($ConfigStorePath)
```

`Write-Fail` is `throw` (line 132), and the script runs under `$ErrorActionPreference = "Stop"`:
**mechanism: a terminating throw at script level**, before the banner and before anything changes.

Reproduced under Windows PowerShell 5.1.20348 (the installer's runtime): a `-PlanOnly` first
configuration (an empty publish folder, no `appsettings.json`) naming an existing database file,
once with forward slashes and once with backslashes, then the module's reader on a key holding the
forward-slash spelling:

```
=== -ConfigStorePath C:/Users/.../cdt37probe_.../shared/exchangeadmin.db
exit 1
  ConfigStorePath must be an absolute local file path (drive letter, colon, backslash - for example ...
=== -ConfigStorePath C:\Users\...\cdt37probe_...\shared\exchangeadmin.db
exit 0
    Config DB   : C:\Users\...\shared\exchangeadmin.db (shared, from -ConfigStorePath)
  PLAN Write appsettings.json at C:\Users\...\publish\appsettings.json with ConfigStore:Path = C:\Users\...\shared\exchangeadmin.db
    !  Plan only. No changes were made.
module reads the forward-slash key back as: C:\Users\...\shared\exchangeadmin.db
```

The app accepts the same forward-slash value (cdt-30's Evidence; cdt-35's fixed `Resolve` returns it
as `D:\...`). Line 739's `GetFullPath` already turns `/` into `\` under .NET Framework and .NET 10
alike (`D:/x/y.db -> D:\x\y.db`, shown in cdt-35's Evidence), so only the validator stands between
the forward-slash form and a correctly written key.

## Plan (approved by the owner's EVERYTHING go)

The installer stops keeping a copy of the rule and asks the module that already holds it - the one
the app's `ConfigStorePath.Resolve` is pinned to (cdt-30, cdt-35):

1. `tools/SqliteConfigBackup.psm1`: the rule's two tests move, unchanged, into a new exported
   function `Get-ConfigStorePathForm -Value`, which returns `NetworkShare` (any two leading
   separators), `NotAbsolute` (anything that is not an ASCII letter, a colon, then `\` or `/`,
   matched case-sensitively) or `Local`. It does not trim; each caller passes the value it means.
   The TRIPWIRE comment moves with the two tests, its pinned wording unchanged, so both suites' pins
   on the rule still hold.
2. `Assert-ConfigStorePathValue` (the reader's check) calls it and keeps its two messages; anything
   but `Local` throws (fail-closed: an unexpected result is a refusal).
3. `tools/Install-ExchangeAdminWeb.ps1`, `Assert-LocalAbsoluteFilePath`: no rule of its own - it
   calls `Get-ConfigStorePathForm` and keeps its two messages, the second now naming the form with
   either separator; anything but `Local` fails. The module is imported (line 730) before the one
   call (line 738); unchanged.
4. Normalisation: line 739's `GetFullPath`, unchanged, writes the backslash spelling; its comment
   says so. The banner, `Resolve-ConfigStorePlan` (cdt-11/20/29 comparisons), the existence check and
   the `appsettings.json` write all use that value, so plan mode prints what apply mode writes.
5. The installer stays standalone and environment-neutral (Invariant 1): the module it already
   imports names no host or environment, and nothing names `deploy.ps1`.

Assumptions, recorded:

- A `-ConfigStorePath` with leading blanks is still refused (the installer does not trim a
  parameter, the reader trims a file value). Fail-closed and unchanged; not widened here.
- The cutover's `-SharedDbPath` check (`tools/Move-ConfigDbToShared.ps1`, `Assert-LocalAbsolutePath`)
  has the same backslash-only, case-insensitive copy. It is a different script and outside this
  finding; reported as a follow-up, not changed.

Constraints: the installer and the module are read by Windows PowerShell 5.1: pure ASCII, no BOM,
no ternary or `??`, `Set-StrictMode -Version Latest`-safe. No version bump: ops-script-only
behaviour.

Files: `tools/SqliteConfigBackup.psm1`, `tools/Install-ExchangeAdminWeb.ps1`,
`tests/ps/InstallerConfigBackup.Tests.ps1` (a new Describe), `tests/ps/SqliteConfigBackup.Tests.ps1`
(the export list; the agreement test's comment on the writers), `tests/ps/DeployInvariants.Tests.ps1`
(its scdi-2 test lifts `Assert-LocalAbsoluteFilePath`, which now needs the module imported), this
record.

Guards: lifted by AST and run with the module imported - `Assert-LocalAbsoluteFilePath` accepts
`D:/...`, `D:\...`, mixed and lower-case forward-slash forms, and refuses UNC (`\\`, `//`, `\/`,
`/\`) as a network share and relative, drive-relative, driveless rooted, `\??\UNC\...` and
Kelvin-sign values as not absolute, naming each; on one list it makes exactly the module's call; by
AST it holds no `-match`/`-notmatch`/`StartsWith` of its own and calls `Get-ConfigStorePathForm`,
after the import; a real `-PlanOnly` first configuration under Windows PowerShell 5.1 given a
forward-slash `-ConfigStorePath` exits 0, prints the backslash spelling in the banner and the
`appsettings.json` plan line, never the forward-slash one, and creates nothing; real `-PlanOnly`
runs given a UNC or a relative `-ConfigStorePath` are refused before anything, naming why.
Mutations: the backslash-only rule restored in the installer; the normalisation (`GetFullPath`)
dropped; the module's rule narrowed back (the installer follows it); the export dropped; the
`0d040ed` installer.

## Approach

As planned. `tools/SqliteConfigBackup.psm1` after the fix:

- Lines 224-246: `Get-ConfigStorePathForm` (help block at 215-223): `if ($Value -match '^[\\/][\\/]')
  { return 'NetworkShare' }`, `if ($Value -cnotmatch '^[A-Za-z]:[\\/]') { return 'NotAbsolute' }`,
  `return 'Local'` - the two tests as they were, moved; the TRIPWIRE comment (lines 229-242) moved
  with them, its pinned phrase unchanged, now also naming the installer as a caller and the cutover
  as the one writer that keeps its own stricter copy.
- Lines 195-213: `Assert-ConfigStorePathValue` keeps its history comment and both messages; its
  body is `$form = Get-ConfigStorePathForm -Value $Value`, a throw for `NetworkShare`, a throw for
  anything else that is not `Local`.
- Line 421: `Get-ConfigStorePathForm` exported.

`tools/Install-ExchangeAdminWeb.ps1` after the fix:

- Lines 230-248: `Assert-LocalAbsoluteFilePath` - a comment saying why there is no copy here, then
  `$form = Get-ConfigStorePathForm -Value $Path`; `Write-Fail "$Name must be a local path, not a
  network share: $Path"` for `NetworkShare`; `Write-Fail "$Name must be an absolute local file path
  (a drive letter, a colon, then \ or / - for example D:\...): $Path"` for anything else that is not
  `Local`.
- Lines 736 and 744: the module import and the one call, unchanged and in that order.
- Lines 745-748: unchanged `$ConfigStorePath = [System.IO.Path]::GetFullPath($ConfigStorePath)`,
  with a comment that it also turns `/` into `\` for every later use.
- Lines 40-42 (help): the parameter takes a drive letter, a colon, then `\` or `/`, and is written
  with backslashes.

Tests: `tests/ps/InstallerConfigBackup.Tests.ps1` gains the `cdt-37` Describe;
`tests/ps/SqliteConfigBackup.Tests.ps1` gains the export check and a contract test for
`Get-ConfigStorePathForm`, and the agreement test's comment on the writers is corrected (its logic
is unchanged: whatever either writer accepts must read back); `tests/ps/DeployInvariants.Tests.ps1`:
the scdi-2 test imports the module before lifting `Assert-LocalAbsoluteFilePath`, and - beyond the
plan, found when that suite failed on the fix - the scd-1/scd-4 test's source pin
`StartsWith('\\')`, which pinned the old copy's shape, now pins `$form = Get-ConfigStorePathForm
-Value $Path` followed by the `NetworkShare` refusal (the UNC refusal itself stays proved
behaviourally by the scdi-2 test).

The same 5.1 probe as in Evidence, against the fixed installer: the forward-slash `-ConfigStorePath`
now exits 0 and prints exactly what the backslash one prints - `Config DB   : C:\...\shared\exchangeadmin.db
(shared, from -ConfigStorePath)` and `PLAN Write appsettings.json at C:\...\publish\appsettings.json with
ConfigStore:Path = C:\...\shared\exchangeadmin.db` - and the module reads that key back as the same
spelling.

## Guard proof

| Test | Pins |
| --- | --- |
| `InstallerConfigBackup.Tests.ps1`, Describe `cdt-37: ...`, `accepts <Value> (<Form>)` (4 cases) | The real `Assert-LocalAbsoluteFilePath` (lifted by AST, the module imported) accepts `D:/inetpub/.../exchangeadmin.db`, `D:\inetpub\...`, `D:\shared/config/...` and `d:/x/exchangeadmin.db` |
| `refuses <Form>, saying why and naming the value` (10 cases) | `\\server\share`, `//server/share`, `\/server/share`, `/\server\share` fail "ConfigStorePath must be a local path, not a network share"; `config\...`, `config/...`, `D:exchangeadmin.db`, `/config/...`, `\??\UNC\...` and the Kelvin-sign drive fail "ConfigStorePath must be an absolute local file path"; each message names the value |
| `makes the module's call on every value, holds no rule of its own, asks only after the import, and writes the backslash spelling (AST)` | On 21 values the installer's check accepts exactly what `Get-ConfigStorePathForm` calls `Local`; the function calls `Get-ConfigStorePathForm` once and has no `-match`/`-like` operator and no `StartsWith`/`IsPathFullyQualified`/`IsPathRooted` call of its own; the script imports the module before its one call; the next statement is `$ConfigStorePath = [System.IO.Path]::GetFullPath($ConfigStorePath)`, which turns `D:/inetpub/...` into exactly `D:\inetpub\...` |
| `a -PlanOnly first configuration given a forward-slash -ConfigStorePath reports and plans the backslash spelling, and creates nothing (Windows PowerShell 5.1)` | The real installer under `powershell.exe`, a fresh publish folder, an existing database named with forward slashes: exit 0; the banner says `Config DB   : <the backslash spelling> (shared, from -ConfigStorePath)` and the plan says `PLAN Write appsettings.json at <publish>\appsettings.json with ConfigStore:Path = <the backslash spelling>`; the forward-slash spelling appears nowhere; "Plan only. No changes were made."; nothing under the test root changed |
| `a -PlanOnly run given <Form> -ConfigStorePath is refused before anything, saying why (Windows PowerShell 5.1)` (UNC, relative) | Non-zero exit, the refusal's reason, no "Plan only" line, nothing created |
| `SqliteConfigBackup.Tests.ps1`, `Get-ConfigStorePathForm is the rule as one function, shared with the installer: it classifies, never trims, never throws (cdt-37)` | `Local` for `D:\...` and `d:/...`; `NetworkShare` for `\\`, `//`, `\/`, `/\`, `\\?\`, `\\.\`; `NotAbsolute` for drive-relative, a drive alone, relative, driveless rooted, `\??\UNC\`, a leading blank, empty, `$null` and the Kelvin-sign drive |
| `SqliteConfigBackup.Tests.ps1`, `parses and exports the expected functions` | `Get-ConfigStorePathForm` is exported |
| `DeployInvariants.Tests.ps1`, the scdi-2 test and the scd-1/scd-4 test | The installer's check still refuses both drive-relative cases and `\\server\share` and accepts `D:\shared\...`; the `NetworkShare` refusal sits in `Assert-LocalAbsoluteFilePath` |
| `ConfigStorePathTests.ModuleRule_IsPinnedInItsSource_AndNamesThisMethod` (xUnit, cdt-35) | Still green with the rule moved: the module still holds `$Value -match '^[\\/][\\/]'`, `$Value -cnotmatch '^[A-Za-z]:[\\/]'` and its TRIPWIRE naming `ConfigStorePath.cs`, so the app and the scripts are still held to one rule (57 passed) |

Mutations, each applied by a script that fails unless every edit matches exactly once, run against
`InstallerConfigBackup.Tests.ps1`, `SqliteConfigBackup.Tests.ps1` and `DeployInvariants.Tests.ps1`
(230 tests), then the original bytes restored, touched and md5 re-confirmed. md5s are of the LF
content (as committed). Fixed, pre-comment: installer `D31046A753D50971C7A0AE515CDD1019`, module
`1014263D66390656ACC00ABF14F5CA9C`. After the round, restored: 230 passed, 0 failed.

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| N1 the backslash-only regex restored at the call site: `if ($form -ne 'Local' -or $Path -notmatch '^[A-Za-z]:\\')` (installer) | `7B6F677554E636AEEA650BA2953371E9` | 4: the forward-slash and lower-case forward-slash acceptance cases ("Expected no exception to be thrown, because the app opens it and the read side reads it back, but an exception "ConfigStorePath must be an absolute local file path (...): D:/inetpub/..." was thrown"), the AST test ("Expected $true, because the installer writes exactly what the module reads back and the app opens ('D:/a/b.db'), but got $false"), the 5.1 plan-mode run ("Expected 0, because ConfigStorePath must be an absolute local file path ...") |
| N2 the normalisation dropped: `$ConfigStorePath = $ConfigStorePath` for the `GetFullPath` line (installer) | `739EB338E242FDA1E61ADFB41B82A1FA` | 2: the AST test ("Expected strings to be the same, because the accepted value is normalised before anything uses it") and the 5.1 plan-mode run (the banner shows the forward-slash spelling: "Expected regular expression 'ConfigDB:C:\\Users\\...\\shared\\exchangeadmin\.db\(shared,from-ConfigStorePath\)' to match ...") |
| N3 the module's rule narrowed back to backslash-only: `-cnotmatch '^[A-Za-z]:\\'` in `Get-ConfigStorePathForm` (the installer follows it) | `B87040B5FF2D74BCF9E6B6A02AC50C39` (module) | 8: the installer's two forward-slash acceptance cases and its 5.1 plan-mode run (the installer has no rule of its own, so it fails with the module), and in `SqliteConfigBackup.Tests.ps1` the agreement test, the new contract test, the two forward-slash read-back cases and the real forward-slash backup case. The installer's "same call as the module" test passes, by design |
| N4 `Get-ConfigStorePathForm` no longer exported (module) | `E5AF91113CDBED60B9B5A2F5430C5A38` (module) | 23: every cdt-37 installer test ("The term 'Get-ConfigStorePathForm' is not recognized ..."), the cdt-29 5.1 plan-mode run, the export test, the contract test, both scdi-2 cases |
| N5 the `0d040ed` installer, byte-identical (its LF md5 is the pre-fix file's) | `535BC2C5A299C92B92333046A8283245` | 8: the two forward-slash acceptance cases, both mixed-separator UNC cases (refused, but as "absolute local file path", not as a network share), the Kelvin-sign case ("Expected a value, because the installer must not write a non-ASCII drive letter (U+212A KELVIN SIGN), but got $null or empty" - the old case-insensitive `-notmatch` passed it), the AST test, the 5.1 plan-mode run, and DeployInvariants' scd-1/scd-4 pin |

Comment pass after the round (the module's TRIPWIRE moved with the rule into
`Get-ConfigStorePathForm` and its help block; the installer's function comment, the `GetFullPath`
comment and the help text), then the gates below. Post-comment md5s (LF):
`tools/Install-ExchangeAdminWeb.ps1` `317591542718BEF2B87380F38F09DB58`,
`tools/SqliteConfigBackup.psm1` `483AFD0EC1D143FF304BB2E5AA79D849`,
`tests/ps/InstallerConfigBackup.Tests.ps1` `61B3984F73F2DB28F2239C67F79FC085`,
`tests/ps/SqliteConfigBackup.Tests.ps1` `85AB6881F038C3CAFD2DC62240712669`,
`tests/ps/DeployInvariants.Tests.ps1` `3A07D58D731D4D3B62F27DD7542F5CCC`.

Gates, on the content as committed, in one pwsh 7.6.6 session with Pester 6.2.0 imported first:
`Invoke-ScriptAnalyzer -Path . -Recurse` 0 errors (333 warnings and 457 findings repo-wide, the same
counts as before this change); `Invoke-Pester tests/ps` 419 passed, 0 failed, 0 skipped (400 before,
plus the 19 new); Windows PowerShell 5.1.20348 (`powershell.exe -NoProfile`) `ParseFile` of
`tools/Install-ExchangeAdminWeb.ps1` and `tools/SqliteConfigBackup.psm1` 0 errors each;
`./tools/Test-AsciiOnly.ps1` after `git add` passed; `git diff --check` clean;
`Select-String -Path tools/Install-ExchangeAdminWeb.ps1 -Pattern 'deploy\.ps1'` returns nothing.
No C# changed; `ConfigStorePathTests` (which pins the module's rule lines) rerun against the moved
rule: 57 passed.

Not run: a real (apply-mode) install against a forward-slash `-ConfigStorePath`: it needs elevation
and IIS. The value it writes is the one the 5.1 plan run prints: the write is
`New-AppSettingsObject -ConfigStorePath $configStorePlan.Path` (cdt-11's AST pin), and the plan
line prints that same `$configStorePlan.Path`.
