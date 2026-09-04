# scdi-2: Configured DB path validation accepts drive-relative paths

**Severity**: MEDIUM - `D:exchangeadmin.db` (no backslash after the colon) is
drive-relative and resolves against the process's current directory on that drive, so
the app or a script would open a different file than the one the operator meant, or
fail later with the missing-file error instead of the intended validation error.
**Status**: Open
**Branch**: -
**Commit**: (filled by the fix)

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

(To be filled by the fix.) Direction: C# uses `Path.IsPathFullyQualified`; the two
scripts must stay Windows PowerShell 5.1-compatible (`IsPathFullyQualified` is absent
from .NET Framework), so they use an explicit `^[A-Za-z]:\\` test plus the existing
UNC refusal. Tests: C# `Resolve_DriveRelative_Throws` for `D:exchangeadmin.db` and
`C:config\exchangeadmin.db`; Pester rows for both scripts with the same inputs.

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
