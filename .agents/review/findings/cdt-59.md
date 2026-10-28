# cdt-59: deploy.ps1's pool-sharing report throws, and refuses the deploy, when another application sits on a drive the host lacks; the installer reads no bulk jobs

**Severity**: MEDIUM for (1) - a refusal where a warning was meant. (1) `deploy.ps1`'s cdt-54 report (`Write-AppPoolSharingReport`
or its equivalent) joins each other application's folder with `Join-Path`, which throws `DriveNotFoundException` for a drive
the host does not have; the throw refuses the deploy before the stop. The cdt-58 reports in promotion and cutover guard this
with an on-disk check; deploy.ps1 does not. Confirmed by the cdt-58 agent under Windows PowerShell 5.1 and PowerShell 7.
(2) LOW: `tools/Install-ExchangeAdminWeb.ps1` reads no bulk jobs at all before stopping the pool - neither its own instance's
nor the other applications' (cdt-57's known gap, carried into cdt-58). (3) Trivial: `tools/JobStateWarning.psm1`'s header lists
`deploy-pipeline.ps1` as a caller, which it is not, and omits the cutover.
**Status**: In progress on branch - (1) fixed and verified on branch (Pester 521 of 521, its mutant caught); (2) and (3)
pending; not merged; UNREVIEWED - admitted 2026-10-27 from the cdt-58 agent's hand-back; inside the mandate
**Branch**: `fix/cdt59` (worktree `D:\source\ExchangeAdminWeb-wt\cdt59`), from master `1d61ab9b`
**Commit**: (1) deploy.ps1, in the commit that adds this line
**Reviewer**: implementer (candidate)

## Evidence

At `857accaf`: the cdt-58 record's "Found and not fixed".

Reproduced at `1d61ab9b`, before any change, by the implementing agent:

- (1) deploy.ps1's real `Write-AppPoolSharingReport` and `Write-Warn`, lifted, with the real `tools/AppPoolControl.psm1`, under
  `$ErrorActionPreference = 'Stop'` as deploy.ps1 runs (44); scratch `bin/cdt59/probe-deploy-missing-drive.ps1`. The pool
  serves two other applications: one with a folder on disk, one at `Q:\lost`, a drive this host does not have. Under Windows
  PowerShell 5.1.20348.5756 and pwsh 7.6.9 alike, the report read the first one's jobs and then threw
  `DriveNotFoundException`, "Cannot find drive. A drive with the name 'Q' does not exist.", from
  `$otherConfigDir = Join-Path $otherFolder 'config'` (291). Each branch calls the report at the top of its stop step (789,
  1126) and nothing catches the throw, so the deploy ends there, before the stop.
- (2) The installer imports `SqliteConfigBackup.psm1` (790) and `AppPoolControl.psm1` (846), and not `JobStateWarning.psm1`;
  it calls `Assert-NoActiveBulkJobsBeforeRecycle` nowhere, and its report says so (698: "This installer reads no bulk jobs,
  its own included, so it reads none of theirs").
- (3) `tools/JobStateWarning.psm1` 6-7: "Shared by every deploy/promote script that stops or recycles the app pool
  (deploy.ps1, tools/deploy-pipeline.ps1, tools/promote-dev-to-prod.ps1)." The scripts that import it are `deploy.ps1` (304),
  `tools/promote-dev-to-prod.ps1` (25) and `tools/Move-ConfigDbToShared.ps1` (88). `tools/deploy-pipeline.ps1` does not: it
  stops no pool itself, only through deploy.ps1 and the promotion.

## Approach

- (1) the same on-disk guard the cdt-58 reports use: a folder not on disk is named in the warning and not read for jobs;
  Pester: another application on a nonexistent drive - the deploy warns and proceeds to the stop; mutation: the guard removed
  (the test sees the throw).
- (2) the installer imports `JobStateWarning.psm1` the way it imports `AppPoolControl.psm1` (cdt-48 precedent; Invariant 1 -
  no coupling to deploy.ps1) and warns about its own instance's active jobs and the other applications' before the stop,
  never refusing; `-PlanOnly` prints the plan line and reads nothing; Pester per path; mutation: the warning call removed.
- (3) the header comment lists the real callers.
- Gates: PSScriptAnalyzer (0 errors), Pester, ASCII lint, `git diff --check`; parse under Windows PowerShell 5.1.

### As built

**(1) deploy.ps1.** Line numbers at the fix:

- 269-271: a cdt-59 comment after cdt-54's, saying why a folder not on disk is not read.
- 293: `if (-not $otherFolder -or -not [System.IO.Directory]::Exists($otherFolder)) { continue }` in place of
  `if (-not $otherFolder) { continue }` - the guard line of the promotion's and the cutover's reports, character for character.

The one warning still names every other application, its folder on disk or not: it is built before the loop. A folder that
is not on disk gets no grey "Active bulk jobs of ..." line and no jobs read, and the run goes on to this instance's own jobs,
the binding read, the folder check and the stop. Beside the fix, one visible difference: a folder that is gone from a drive
that exists no longer gets a grey line followed by nothing (it has no jobs database, so the read reported nothing before
either). deploy.ps1 still has no plan mode, and the report still refuses nothing.

**(2) the installer.** Pending.

**(3) the module header.** Pending.

## Files changed

Each file at the base `1d61ab9b` (its blob), and the md5 of the working-tree bytes (CRLF) after:

- `deploy.ps1`: blob `d783b31e22fc`; after `3B28AE6D8769704DBDCCDF886CDCAFAC`
- `tests/ps/DeployInvariants.Tests.ps1`: blob `3a2a3e93f2c5`; after `F109BA6E1FFDEEB02D8E76D7C3D94D1B`
- this record and its row in `.agents/review/index.md`

No version bump: ops scripts, nothing the app ships changes.

## Guard proof

Tests:

| Test | Pins |
| --- | --- |
| `tests/ps/DeployInvariants.Tests.ps1`, `names another application whose folder is not on disk and reads none of its jobs, so a drive this host lacks is a warning and each branch goes on to the stop (cdt-59)` (1579) | Each branch's real stop step, lifted from its `Write-Step` to `Write-Success "App pool $AppPoolName confirmed stopped"` and run under `Stop`, with the real report and module; IIS through the module's probe, which records that it was consulted; the jobs check, the binding read, the folder check and the stop stubbed and recorded. The test first proves `Join-Path` throws on the missing drive. Three other applications: one on disk under `TestDrive:`, one whose folder is gone, one on the missing drive. In each branch: no throw; exactly one warning, naming the three in IIS order; one grey line, for the one on disk; the calls exactly `probe`, its jobs (its `config`, the staging tool), this instance's jobs, `binding`, `serves`, `stop(ExamplePool)`; the warning before the stop |
| same file, cdt-54's report test (1487), updated | Its other application, `%CDT54_ROOT%\other`, was at `D:\example\other`, which is not on disk and so is no longer read; it now has a real folder under `TestDrive:`, and the expectation reads that folder's `config`. The `%VAR%` expansion and the staging tool stay pinned |

Mutation, by `bin/cdt59/Invoke-Mutation.ps1` (scratch, git-ignored): one ordinal replacement that must match exactly once,
the suite run on the mutant, the original bytes restored with a fresh mtime and the md5 re-checked equal to the fixed
file's, the suite run again.

| # | Mutation | md5 mutant | Failing |
| --- | --- | --- | --- |
| D1 | deploy.ps1: the on-disk guard removed, `if (-not $otherFolder) { continue }` restored (the record's mutation) | `61B09570FB83EE434E60FC073FA5EBBA` | 1 of 131: the cdt-59 test, which sees the throw - "Expected $null or empty, because an application on a drive this host lacks is named in a warning, and must not refuse the upgrade deploy before the stop, but got 'Cannot find drive. A drive with the name 'Q' does not exist.'" |

Restored: md5 `3B28AE6D8769704DBDCCDF886CDCAFAC`, the fixed file's; `DeployInvariants.Tests.ps1` 131 of 131.

Windows PowerShell 5.1.20348.5756 and pwsh 7.6.9, on the fixed file (scratch `bin/cdt59/probe-deploy-missing-drive.ps1`):
deploy.ps1 parses with no errors; the lifted report, with the real module, names the three others in one warning, prints the
grey line and reads jobs only for the folder on disk, and throws on neither the gone folder nor the missing drive.

Gates after (1), pwsh 7.6.9: `Invoke-Pester tests/ps` (Pester 6.2.0) 521 passed, 0 failed, 0 skipped (base `1d61ab9b`: 520);
`Invoke-ScriptAnalyzer -Path . -Recurse -Severity Error` (1.25.0) 0; `tools/Test-AsciiOnly.ps1` passed; `git diff --check
HEAD` clean; deploy.ps1 and the test file parse under Windows PowerShell 5.1 with 0 errors. Both files stay CRLF.

Not run: a real deploy against IIS with a shared pool.

## Coder dispute

None.

## Known gaps

Pending the remaining sub-items.

## Reviewer comments

None yet.

## Closeout

Pending: (2) and (3), merge into `master`, codex review, branch deletion.
