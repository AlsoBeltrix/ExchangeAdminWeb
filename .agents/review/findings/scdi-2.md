# scdi-2: Configured DB path validation accepts drive-relative paths

**Severity**: MEDIUM - `D:exchangeadmin.db` (no backslash after the colon) is
drive-relative and resolves against the process's current directory on that drive, so
the app or a script would open a different file than the one the operator meant, or
fail later with the missing-file error instead of the intended validation error.
**Status**: Verified
**Branch**: -
**Commit**: the commit that completes this record; read it from `git log -1 -- .agents/review/findings/scdi-2.md`

## Evidence

`docs/SharedConfigDb-Plan.md` AC1 requires an absolute file path and refuses relative
ones. `Services/Storage/ConfigStorePath.cs:37-48` checks only `Path.IsPathRooted`
(true for `D:x.db`) and rejects roots of `\`/`/`; `ExchangeAdminWeb.Tests/ConfigStorePathTests.cs:41-50`
covers `config\...`, `..\...`, `\config\...` but no drive-relative value. The same
`IsPathRooted`-only pattern: `tools/Install-ExchangeAdminWeb.ps1:515-523`,
`tools/Move-ConfigDbToShared.ps1:108-117`. Trigger: `ConfigStore:Path`,
`-ConfigStorePath` or `-SharedDbPath` set to `D:exchangeadmin.db`.

## Predicted observable failure

Two instances given `D:exchangeadmin.db` with different working directories open two
different files while both believe they share one; or the cutover script creates the
shared file at an unintended location.

## What

Validation used "rooted" where "fully qualified" was meant.

## Approach

`ConfigStorePath.Resolve` now tests `Path.IsPathFullyQualified(value)` instead of
`IsPathRooted` plus a root-shape check; the UNC refusal and the key-naming message are
unchanged. Both scripts must stay Windows PowerShell 5.1-compatible
(`[IO.Path]::IsPathFullyQualified` does not exist on .NET Framework), so they test
`$Path -notmatch '^[A-Za-z]:\\'` after the existing UNC refusal. In the installer the
inline block became a function, `Assert-LocalAbsoluteFilePath`, so the Pester test can
lift it by AST and run it against the drive-relative inputs the way the existing
`New-AppSettingsObject` test does; the main body calls it on `-ConfigStorePath` and the
test asserts that call site too. `Move-ConfigDbToShared.ps1` keeps its
`Assert-LocalAbsolutePath` name and gains the regex.

## Files changed

- `Services/Storage/ConfigStorePath.cs:15-18,45-50` - `IsPathFullyQualified`; remarks name the drive-relative case
- `ExchangeAdminWeb.Tests/ConfigStorePathTests.cs:52-63` - `Resolve_DriveRelative_Throws` theory (two rows)
- `tools/Install-ExchangeAdminWeb.ps1:183-197,531` - new `Assert-LocalAbsoluteFilePath` (UNC + `^[A-Za-z]:\\`), called for `-ConfigStorePath`
- `tools/Move-ConfigDbToShared.ps1:115-120` - `Assert-LocalAbsolutePath` uses `^[A-Za-z]:\\`
- `tests/ps/MoveConfigDbToShared.Tests.ps1:127-139` - behavioural rows `refuses a drive-relative -SharedDbPath` for both inputs
- `tests/ps/DeployInvariants.Tests.ps1:388-410` - installer rows lifting the function, plus the call-site and no-`IsPathRooted` asserts

## Guard proof

- `ExchangeAdminWeb.Tests/ConfigStorePathTests.cs::Resolve_DriveRelative_Throws` (2 rows) -
  mutation: put the old `!IsPathRooted(value) || GetPathRoot(value) is null or "" or @"\" or "/"`
  test back in place of `!IsPathFullyQualified(value)` -> 2 FAIL (12 pass); restore -> 14/14.
- `tests/ps/MoveConfigDbToShared.Tests.ps1::refuses a drive-relative -SharedDbPath (<Path>)` (2 rows) -
  mutation: `if (-not [System.IO.Path]::IsPathRooted($Path))` in place of the regex -> 2 FAIL; restore -> 74/74.
- `tests/ps/DeployInvariants.Tests.ps1::-ConfigStorePath refuses a drive-relative path (<Path>)` (2 rows) -
  mutation A: same `IsPathRooted` swap inside `Assert-LocalAbsoluteFilePath` -> 2 FAIL;
  mutation B: delete the `Assert-LocalAbsoluteFilePath -Path $ConfigStorePath` call from the main body -> 2 FAIL; restore -> 74/74.
- Full suite after the restore and a rebuild: 2322 passed / 0 failed / 3 skipped; ScriptAnalyzer 0 errors.

## Coder dispute (if any)

None.

## Known gaps

None.

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
  (same dispatch as scdi-1)
Harness: codex-cli 0.152.1. Reviewed `8d9ccb0..b9e944f`, verdict `findings`,
capability_ok true. Envelope `.agents/review/scd-impl.result.json`.
