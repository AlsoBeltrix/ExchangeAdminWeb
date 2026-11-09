# sweep-g-37: the tools modules did not inherit the caller's "Stop", so an unreadable appsettings.json read as "no key" (sweep item 37)

**Severity**: MEDIUM - sweep item 37: a module's functions read the module's `$ErrorActionPreference`, then the global one
(Continue), never the calling script's "Stop". So `Get-ConfigStorePathSetting` (`tools/SqliteConfigBackup.psm1`) answered an
unreadable `appsettings.json` with `$null` and every caller backed up or checked the stale per-instance `config\exchangeadmin.db`
(fail-open); a failed `Move-Item -Force` in `Copy-SqliteDbFile` still returned success; a failed `Get-WebAppPoolState`
(`tools/AppPoolControl.psm1`) gave `$null` and the stop timed out 60 s later; `Get-Website`/`Get-WebApplication` errors were
written and the listing went on short.
**Status**: Merged 59522ca6; reviewed by codex review F 2026-11-09
**Branch**: `fix/sweep-g` (from master `145f513f`)
**Commit**: `7d3cf358` (`fix(sweep-g, item 37): ...`) | **Reviewer**: implementer (candidate)

## Approach (as built)

- `$ErrorActionPreference = 'Stop'` at module scope in `AppPoolControl.psm1`, `SqliteConfigBackup.psm1` and
  `JobStateWarning.psm1` (`ToolFolderAcl.psm1` already had it).
- `-ErrorAction Stop` on: `Get-WebAppPoolState` in both pool probes (stop and start), `Get-Website` in both listings,
  `Get-WebApplication` in `Get-AppPoolOtherApplication`'s listing, `Move-Item` and the sidecar `Remove-Item` in
  `Copy-SqliteDbFile`, and `Get-Content`/`ConvertFrom-Json` of `appsettings.json`.
- An `appsettings.json` that is there but cannot be read or parsed throws "Cannot read ... Refusing to fall back to the
  instance's own config\exchangeadmin.db ...". A missing file still means no key (unchanged).
- `JobStateWarning` keeps its warn-only policy: `Assert-NoActiveBulkJobsBeforeRecycle` turns any failure of the check into a
  warning and returns 0.
- `docs/ConfigDbOperations.md`: an "Unreadable" row in the which-database table.

## Where the code goes beside the fix shape

- The try/catch around the job check is new: with the module's "Stop", a failure that used to cascade into a parameter-binding
  throw (a config dir on a drive the host lacks) would otherwise refuse a deploy, against the module's documented "never blocks".
  The test shows the old code threw there too, so this also closes a latent block.
- `Invoke-ConfigDbTool` and `Get-ActiveBulkJobs` keep their local `Continue` around the native call (exit code is the contract).

## Assumptions not verified live

- That real WebAdministration cmdlets report the failures modelled (a non-terminating error, no output). Under "Stop" either kind
  throws, so the outcome does not depend on which.

## Guard proof

All three modules restored to the pre-fix version: 9 of 9 new tests failed; restored, all pass.

| Mutation | Failing tests |
|---|---|
| SqliteConfigBackup without module "Stop" and the read's try | "refuses an appsettings.json it cannot read" (locked file), "refuses an appsettings.json that does not parse" |
| SqliteConfigBackup Move-Item without "Stop" | "throws when the copy cannot be moved over the destination" |
| AppPoolControl without module "Stop" / -ErrorAction Stop | the stop and start state reads refuse at once; the site-list and application-list errors refuse `Get-AppPoolOtherApplication`; the site-list error refuses `Assert-AppPoolServesPath` (5) |
| JobStateWarning without "Stop" and the catch | "a check that cannot run warns and returns 0" (the old code threw) |

## Deploy-time checks

- None beyond the normal plan: a healthy host reads exactly as before. To see the refusal: none safe to provoke on the host.
