# cdt-59: deploy.ps1's pool-sharing report throws, and refuses the deploy, when another application sits on a drive the host lacks; the installer reads no bulk jobs

**Severity**: MEDIUM for (1) - a refusal where a warning was meant. (1) `deploy.ps1`'s cdt-54 report (`Write-AppPoolSharingReport`
or its equivalent) joins each other application's folder with `Join-Path`, which throws `DriveNotFoundException` for a drive
the host does not have; the throw refuses the deploy before the stop. The cdt-58 reports in promotion and cutover guard this
with an on-disk check; deploy.ps1 does not. Confirmed by the cdt-58 agent under Windows PowerShell 5.1 and PowerShell 7.
(2) LOW: `tools/Install-ExchangeAdminWeb.ps1` reads no bulk jobs at all before stopping the pool - neither its own instance's
nor the other applications' (cdt-57's known gap, carried into cdt-58). (3) Trivial: `tools/JobStateWarning.psm1`'s header lists
`deploy-pipeline.ps1` as a caller, which it is not, and omits the cutover.
**Status**: Fixed on branch, one commit per sub-item; verified on branch (Pester 526 of 526, 7 mutants caught); not merged;
UNREVIEWED - admitted 2026-10-27 from the cdt-58 agent's hand-back; inside the mandate
**Branch**: `fix/cdt59` (worktree `D:\source\ExchangeAdminWeb-wt\cdt59`), from master `1d61ab9b`
**Commit**: (1) deploy.ps1 `bbfad818`; (2) the installer `b12496fa`; (3) the module header, and this closeout, in the
commit that adds this line (its SHA in the implementer's hand-back)
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

**(2) the installer.** Line numbers at the fix:

- 861-865: `Import-Module (Join-Path $PSScriptRoot 'JobStateWarning.psm1') -Force`, straight after the `AppPoolControl.psm1`
  import and in its form, with a comment: a sibling in `tools\` that names no host, domain or configuration, so importing it
  keeps the installer standalone and environment-neutral (Invariant 1; cdt-48's precedent). Unconditional, like that
  import: loading it reads nothing. No word of deploy.ps1 enters the installer (the DeployInvariants pin still passes).
- `Write-AppPoolSharingReport` (690-729): a cdt-59 comment (699-703); a new `-AppPath` (707); the unreadable-listing warning
  now ends "and their active bulk jobs are not read" (713), the promotion's words, since the report now reads them; and,
  after the warning, the promotion's loop (722-728), line for line: for each other application whose folder is on disk, a
  grey line naming it, then `Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir <its folder>\config -AppPath $AppPath`.
- The cdt-58 step: a cdt-59 comment (1163-1167), then the step (1168-1171), whose description now ends "and warn about
  the active bulk jobs the stop interrupts, theirs and this instance's"; its body is the report, now with
  `-AppPath $stagingPath`, then
  `Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $configDir -AppPath $stagingPath | Out-Null`. The folder check stays the
  statement right before the stop (cdt-32's pin).

The choices, each read from the code:

- Where: in the cdt-58 step, so the order is the promotion's - the others and their jobs, this instance's jobs, the folder
  check, the stop - and a plan prints the one step line and reads nothing (Invariant 4). In a plan there is no staged
  build to read with anyway: the publish is itself a plan line.
- Which folder: `$configDir`, the installer's `<PublishPath>\config` (795). The app keeps its jobs database at
  `<content root>\config\exchangeadmin-jobs.db` (`Program.cs` 88), and cdt-15's check has already refused a run that would
  move the application off `-PublishPath`.
- Which tool: the build just published to staging, as deploy.ps1 reads with its staged build (cdt-49). cdt-2's check has
  confirmed the .NET host under Program Files and the staged `ExchangeAdminWeb.dll` on every real run, `-SkipConfigBackup`
  included. `db active-jobs` opens the database read-only (`Services/Storage/ConfigDbMaintenance.cs` 133-140).
- Never a refusal: the owner's warn-not-block policy (`tools/JobStateWarning.psm1`). Invariant 5: `$ErrorActionPreference =
  "Stop"` stands; the jobs check itself turns a missing tool, an old build or an unreadable database into a warning; the
  report's one catch is the listing's, as before, and the folder check that reads the same listing refuses an unreadable
  one before the stop.

**Operator-visible change.** Before the stop, a real run now also shows the active bulk jobs of each other application
whose folder holds a jobs database, each introduced by a grey line naming it, and then this instance's own - the existing
jobs warning, loud and listing them. A fresh install has no jobs database, so nothing more is shown. The listing-failure
warning names the jobs as unread. Nothing new is refused; a plan's line for the step says what a real run reads.

**(3) the module header.** `tools/JobStateWarning.psm1` 6-9 now names as its callers the four scripts that import it,
found by searching for the import: deploy.ps1, the promotion, the cutover (`tools/Move-ConfigDbToShared.ps1`) and, since
(2), the installer - every script that stops a pool, each before its stop. It says that `tools/deploy-pipeline.ps1` does
not import it, since it stops a pool only through deploy.ps1 and the promotion. A comment; nothing else in the module
changes.

## Files changed

Each file at the base `1d61ab9b` (its blob), and the md5 of the working-tree bytes (CRLF) after:

- `deploy.ps1`: blob `d783b31e22fc`; after `3B28AE6D8769704DBDCCDF886CDCAFAC`
- `tests/ps/DeployInvariants.Tests.ps1`: blob `3a2a3e93f2c5`; after `F109BA6E1FFDEEB02D8E76D7C3D94D1B`
- `tools/Install-ExchangeAdminWeb.ps1`: blob `389edde40b67`; after `63BFEAAFB3982393C373377965977A86`
- `tests/ps/InstallerConfigBackup.Tests.ps1`: blob `7eccd31ef998`; after `2F6E68C88F712DEA7DE6E2F0029F2E89`
- `tools/JobStateWarning.psm1`: blob `5829a762c1d7`; after `D314D1F768D6A7A18BFD53E62F45361A`
- this record and its row in `.agents/review/index.md`

No version bump: ops scripts, nothing the app ships changes.

## Guard proof

Tests:

| Test | Pins |
| --- | --- |
| `tests/ps/DeployInvariants.Tests.ps1`, `names another application whose folder is not on disk and reads none of its jobs, so a drive this host lacks is a warning and each branch goes on to the stop (cdt-59)` (1579) | Each branch's real stop step, lifted from its `Write-Step` to `Write-Success "App pool $AppPoolName confirmed stopped"` and run under `Stop`, with the real report and module; IIS through the module's probe, which records that it was consulted; the jobs check, the binding read, the folder check and the stop stubbed and recorded. The test first proves `Join-Path` throws on the missing drive. Three other applications: one on disk under `TestDrive:`, one whose folder is gone, one on the missing drive. In each branch: no throw; exactly one warning, naming the three in IIS order; one grey line, for the one on disk; the calls exactly `probe`, its jobs (its `config`, the staging tool), this instance's jobs, `binding`, `serves`, `stop(ExamplePool)`; the warning before the stop |
| same file, cdt-54's report test (1487), updated | Its other application, `%CDT54_ROOT%\other`, was at `D:\example\other`, which is not on disk and so is no longer read; it now has a real folder under `TestDrive:`, and the expectation reads that folder's `config`. The `%VAR%` expansion and the staging tool stay pinned |
| `tests/ps/InstallerConfigBackup.Tests.ps1`, Describe `cdt-59: the installer warns about the active bulk jobs its stop interrupts, ...` (1703), five tests | The installer's real report and step, lifted, with the real `AppPoolControl.psm1`; IIS only through the module's probe, which records that it was consulted; the jobs check a recording stand-in. (1778) other applications' jobs: two on disk under `TestDrive:` (one under a differently cased pool name) named in one warning and read, each from its own `config` with the staged build, each read after its grey line; the instance and another pool's application neither named nor read. (1806) not on disk: one with no folder, one whose folder is gone, one on a drive this host does not have (the test first proves `Join-Path` throws on it), under `Stop` - named in the warning, none read, no grey line, no throw. (1832) own jobs, the real region from the publish to the restart check: the calls exactly `probe`, the other's jobs, this instance's jobs (`<publish>\config`, the staged build), `serves`, `stop(ExamplePool)`, `mirror`, `start`, `manifest`; one warning naming the two others, one of them on the missing drive; no throw. (1857) plan only, the same region with `-PlanOnly`: the step's one plan line, word for word, and no probe, no jobs, no folder check, no stop. (1867, AST) the import, once, at script level, `Import-Module (Join-Path $PSScriptRoot 'JobStateWarning.psm1') -Force`, after `AppPoolControl.psm1`'s and before the step; the step's body exactly two statements, the second `Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $configDir -AppPath $stagingPath \| Out-Null`; two jobs calls in the script, one inside the report, none with `-PlanOnly` |
| same file, cdt-58's Describe (1613), updated | The report test stubs the jobs check, which it does not test, and expects the listing-failure warning's new words; the AST test expects the step's new description and the report with `-AppPath $stagingPath` as its first statement (the count of its statements moved to the cdt-59 AST test) |
| same file, cdt-32's region test (1447), updated | The region run for real: the report and the jobs check record what they were given, so the calls are `report(ExamplePool\|<root>\publish.staging.x),jobs(<root>\publish\config\|<root>\publish.staging.x),serves(Refuse)` when the folder check refuses and the same followed by `stop,mirror,start,manifest` when it passes; a plan runs none of them. cdt-23's (1335) and cdt-33's (1553) region tests stub both silently and set `$configDir`, their expectations unchanged |

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

Mutations on the fixed installer, by the same harness, each run against the two suites that load the installer,
`InstallerConfigBackup.Tests.ps1` and `DeployInvariants.Tests.ps1`:

| # | Mutation | md5 mutant | Failing |
| --- | --- | --- | --- |
| I1 | installer: this instance's jobs read removed from the step (the record's mutation, "the warning call removed") | `1A4EF5B1D9E5E0C702E90E203CE95727` | 3 of 214: cdt-32's region test (1447, no `jobs(...)` in the calls), cdt-59's own-jobs test (1832) and its AST test (1867, one statement in the step, not two) |
| I2 | installer: the other applications' jobs read removed from the report | `15EFF2DE87D8D2196A04AF80FBE2FC82` | 3: cdt-59 (1778, nothing read), (1832) and the AST test (1867, one jobs call, not two) |
| I3 | installer: the report's on-disk guard removed | `85842A90143325E6D5EF278DC1DF509E` | 3: cdt-59 (1806), which sees the throw "Cannot find drive. A drive with the name 'Q' does not exist." from `Join-Path $otherFolder 'config'`; (1832), the same throw ending the region before the stop; and cdt-58's report test (1635), whose `D:\example\other` is not on disk here and was given a grey line |
| I4 | installer: the `JobStateWarning.psm1` import removed | `0D998D84614D10D6AD776B09EE6B851C` | 1: the AST test (1867). The behavioural tests stand the jobs check in, so only that pin can see the import; without it a real run would end at the step on an unknown command |
| I5 | installer: this instance's jobs read moved out of the step, after it, where a plan runs it | `74101618360D57D6EB77D5959791D5EC` | 4: cdt-32's plan (1447, `jobs(...)` in a plan), cdt-58's AST test (1682, the statement before the folder check is no longer the step), cdt-59's plan test (1857) and its AST test (1867) |
| I6 | installer: this instance's jobs read with the installed build, `-AppPath $PublishPath` | `AE0139CE965256EDB4A081F27BF4F945` | 3: cdt-32 (1447), cdt-59 (1832) and the AST test (1867) |

Restored after each: md5 `63BFEAAFB3982393C373377965977A86`, the fixed installer's; the two suites 214 of 214.

Windows PowerShell 5.1.20348.5756 and pwsh 7.6.9, on the fixed installer (scratch `bin/cdt59/probe-installer-51.ps1`): the
installer parses with no errors. Its report, lifted, with the real `AppPoolControl.psm1`, on six nodes - the instance, one
on disk, one with no folder, one gone, one on the missing drive, one under another pool: one warning naming the four of
this pool; a grey line and a jobs read, with the staged build, only for the one on disk; no throw. An unreadable listing:
the new warning, no throw, nothing read. `JobStateWarning.psm1` imports under 5.1, and its real
`Assert-NoActiveBulkJobsBeforeRecycle`, called as the step calls it for this instance, returns 0 silently when there is no
jobs database, and gives its soft "db tool was not found" warning and returns 0 when there is one but no db tool in
staging - no throw either way.

Gates after (1), pwsh 7.6.9: `Invoke-Pester tests/ps` (Pester 6.2.0) 521 passed, 0 failed, 0 skipped (base `1d61ab9b`: 520);
`Invoke-ScriptAnalyzer -Path . -Recurse -Severity Error` (1.25.0) 0; `tools/Test-AsciiOnly.ps1` passed; `git diff --check
HEAD` clean; deploy.ps1 and the test file parse under Windows PowerShell 5.1 with 0 errors. Both files stay CRLF.

Gates after (2), pwsh 7.6.9: `Invoke-Pester tests/ps` 526 passed, 0 failed, 0 skipped (+5); `Invoke-ScriptAnalyzer -Path .
-Recurse -Severity Error` 0; `tools/Test-AsciiOnly.ps1` passed; `git diff --check HEAD` clean; the installer and its test
file parse under Windows PowerShell 5.1 with 0 errors. Both files stay CRLF. With warnings, against the base files: one
new, `PSAvoidUsingWriteHost` on the report's grey line (726) - the scripts' convention for read-only report lines, as with
cdt-54's and cdt-58's; none new in deploy.ps1 or either test file.

(3) is a comment, so it has no test and no mutant. Its four callers are the files that hold
`Import-Module (Join-Path $PSScriptRoot '...JobStateWarning.psm1') -Force`: deploy.ps1 (307), the promotion (25), the
cutover (88) and the installer (865). Gates after (3), on the final code: `Invoke-Pester tests/ps` 526 passed, 0 failed, 0
skipped; `Invoke-ScriptAnalyzer -Path . -Recurse -Severity Error` 0, and the module's warnings unchanged (2 and 2);
`tools/Test-AsciiOnly.ps1` passed; `git diff --check HEAD` clean; the module parses under Windows PowerShell 5.1 with 0
errors and stays CRLF.

The first full Pester run built the app once: `tests/ps/Get-BuiltApp.ps1` runs `dotnet build ExchangeAdminWeb.csproj -c
Release` when the worktree has no build output, and this fresh worktree had none (20:39:45-20:39:56; `bin/Release` and
`obj/`, both git-ignored). Every later run used that output; the brief had reserved the compile slot, so this is reported
to the supervisor. `JobStateWarning.Tests.ps1` ran the real module against that build in every full run.

Not run: a real deploy or install against IIS with a shared pool.

## Coder dispute

None.

## Known gaps

Found and not fixed - candidates, outside this record:

- **This instance's own jobs warning follows the others' grey lines with no line of its own.** In the promotion, the
  cutover, deploy.ps1's upgrade branch and now the installer, the report's last grey line ("Active bulk jobs of IIS
  application X ..., which stops with the pool:") is followed by X's read and then by this instance's, which prints no
  heading. When X has no active jobs and this instance has some, the jobs warning reads on screen as X's. The warnings are
  right and nothing is refused; only the attribution is ambiguous. deploy.ps1's fresh-install branch already names its own
  (cdt-49). The installer follows the promotion's shape, as briefed.
- A folder that `[System.IO.Directory]::Exists` cannot see - a share this account cannot reach, a folder it may not list -
  is named and not read, as one that is not there; its jobs database could not have been read either.
- cdt-58's installer report test (1635) names `D:\example\other` and assumes it is not on disk, as the suite's other
  `D:\example` paths are fake. On a host where it existed, the report would also print a grey line for it and that test
  would fail (mutation I3 shows the line).
- A plan of the installer shows no active jobs: there is no staged build to read them with, and a plan reads nothing.

Closed here, from the cdt-58 record's known gaps: deploy.ps1's report on a missing drive (1) and the installer reading no
bulk jobs (2), which was also cdt-57's known gap. Those records are not edited here.

## Reviewer comments

None yet.

## Closeout

Pending: merge into `master`, codex review, branch deletion.
