# cdt-58: promote, install and cutover stop a pool without naming its other applications

**Severity**: LOW - cdt-54 closed this for `deploy.ps1` and exported `Get-AppPoolOtherApplication`; the promotion,
installer and cutover paths still stop a pool with no word about the other applications it serves or their bulk jobs.
**Status**: Fixed on branch, one commit per script; verified on branch (Pester 520 of 520, 10 mutants caught); not merged;
UNREVIEWED - admitted 2026-10-26 from the cdt-36 agent's fourth hand-back, reopened as a bug of the cdt-54 class by the
owner's 2026-10-26/27 ruling (`.agents/decisions.md`, item 1); inside the released scope
**Branch**: `fix/a29g-cdt58-psl10` (worktree `D:\source\ExchangeAdminWeb-wt\a29g-cdt58-psl10`), from master `84c729d8`
**Commit**: approach `a68ce534`; promotion `48bad5bb`; installer `99b3763d`; cutover `3a86ec07`; this closeout in the
commit that adds this line (its SHA in the implementer's hand-back)
**Reviewer**: implementer (cdt-36 agent, candidate; not yet confirmed by the supervisor); implemented by the
a29g-cdt58-psl10 agent

## Evidence

At `84a31608`; raised by the cdt-54/cdt-55 agent. `Get-AppPoolOtherApplication` is exported from
`tools/AppPoolControl.psm1` and has no caller outside deploy.ps1.

## Approach (to be written by the implementing agent before code)

- Each remaining stop path calls the same helper and reports what else the pool serves, the cdt-54 shape; Pester per
  path; mutation: one call removed.

### As written before the code (branch `fix/a29g-cdt58-psl10`, from master `84c729d8`)

Line numbers are the base's.

**What each stop looks at today.** Only its own instance:

- `tools/promote-dev-to-prod.ps1` `Stop-AppPoolChecked` (218). The apply path: `Write-Step` (233), prod's jobs (235),
  `Assert-AppPoolServesPath ... -NestedNodes Refuse` (243), `Stop-AppPoolVerified` (252). The dry run (226-231) prints plan
  lines and reads nothing from IIS.
- `tools/Install-ExchangeAdminWeb.ps1`: the folder check, a step of its own (1120-1122), then the stop step (1124-1133). The
  installer reads no bulk jobs at all, its own included (cdt-57's known gap).
- `tools/Move-ConfigDbToShared.ps1` `Stop-AppPoolChecked` (245): one `Invoke-PlanOrAction` holding the instance's jobs (249)
  and `Stop-AppPoolVerified` (261), called for dev and for prod (773-774). The serving check, `Assert-PoolServesInstance`
  (217), runs at step 1b (755-756; 731-732 in a plan that can read IIS), before either stop.

`Assert-AppPoolServesPath` keeps only the nodes at or inside the folder, so an application elsewhere under the same pool is
never looked at. The stop takes it down unannounced, and if it is another instance of this app its bulk jobs are never read.

**Change: the cdt-54 report, in each script's terms.** Each script gets its own `Write-AppPoolSharingReport`, which calls the
exported `Get-AppPoolOtherApplication` (cdt-54 exported it for these callers) and says:

- none: one DarkGray line, that the pool serves no IIS application other than the instance at the folder;
- others: a `Write-Warn` naming each (its location and its files) and saying that it stops with the pool. Then, where the
  script reads bulk jobs (the promotion and the cutover), for each one whose folder is on disk: a DarkGray line naming it,
  and `Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir <its folder>\config -AppPath <the script's tool folder>`;
- an unreadable listing: a `Write-Warn` that the stop may take down other applications that are not named.

Never a refusal, for cdt-54's reasons: the owner's warn-not-block policy for what a recycle interrupts
(`tools/JobStateWarning.psm1` 12), nothing of theirs is replaced, and a switch would add no safety.

1. **Promotion.** One call in `Stop-AppPoolChecked`'s apply path, straight after `Write-Step "Stopping prod app pool"`, so
   before prod's jobs, the folder check and the stop. The jobs are read with `$AppPath` (dev, the build being promoted), as
   prod's own are. The dry run gets one plan line, before the folder check's, and still reads nothing from IIS. An unreadable
   listing is refused by the folder check after the report, which reads the same listing.
2. **Installer.** A new step, `Invoke-PlanOrAction`, right before the folder check: a plan prints it, and the folder check
   stays the statement right before the stop (cdt-32's pin). It names the others and reads no jobs, because this installer
   reads none, its own included, and imports no `JobStateWarning.psm1`; adding that import and the jobs read is cdt-57's
   candidate, outside this record. It uses only what it already imports: `tools/AppPoolControl.psm1` (818), which names no
   environment and which cdt-48 used the same way (Invariant 1). No word of deploy.ps1 enters the installer (the
   DeployInvariants pin). An unreadable listing is refused by the folder check after the report.
3. **Cutover.** `Stop-AppPoolChecked` gains `-PublishPath`, and each call passes its own instance's folder. The report is the
   first statement of the step's action, before the instance's own jobs and the stop. A plan prints the step, whose
   description now says it names the others, and runs nothing, as for the stop. The jobs are read with `$AppPath` (the dev
   build, as for every db-tool call in the cutover). An unreadable listing at the stop is a warning only: the serving check
   refused one at step 1b, before anything was stopped.

One deliberate difference from deploy.ps1's report: a folder that is not on disk is not read for jobs. It holds no jobs
database, and `Join-Path` throws on a drive that does not exist (measured: `Join-Path 'Q:\x' 'config'` throws
`DriveNotFoundException` under `Stop` in Windows PowerShell 5.1.20348 and pwsh 7.6.9). In deploy.ps1's report that throw is
outside the listing's catch, so an application left on a missing drive turns the warning into a refusal. deploy.ps1's twin
is a candidate, not fixed here.

Constraints: Invariant 4 (as above); Invariant 5 (`$ErrorActionPreference = "Stop"` stands; the one catch is the listing's,
which warns, and the gate that reads the same listing fails closed); Invariant 6 (pure ASCII); Invariant 7 (no pool, site or
host is named; the list is what IIS reports). No version bump: ops scripts, nothing the app ships changes.

Guards, through the module's `-ApplicationsProbe` (set through `$PSDefaultParameterValues`), real folders under `TestDrive:`,
and each script's real functions lifted from the file:

- `tests/ps/AppPoolControl.Tests.ps1`, the aud-a38b Describe (the promotion): others named in one warning, before the stop,
  and the one with a folder on disk read for jobs from its own `config` with dev's tool; one with no folder, one whose
  folder is gone and one on a missing drive named and not read, with no throw; none: the DarkGray line; an unreadable
  listing: the warning, then the folder check's refusal and no stop; the dry run: the plan line and no IIS read; AST: one
  call, in the apply path, after the `Write-Step` and before the jobs, the folder check and the stop.
- `tests/ps/InstallerConfigBackup.Tests.ps1`, a new Describe: the report (none; others; unreadable - a warning, no throw);
  AST: a step of its own right before the folder check, the helper's only caller. The cdt-23, cdt-32 and cdt-33 region tests
  stub the report; cdt-32's stub records it, so the order `report, serves, stop, ...` is pinned by running the region, and a
  plan runs neither.
- `tests/ps/MoveConfigDbToShared.Tests.ps1`, the aud-a38e Describe: as for the promotion (others named and read before the
  instance's own jobs and the stop; none; unreadable: a warning and the stop goes on); a plan runs no report; AST: the first
  statement of the action, and each call site passes its own folder.

Mutations, each to fail its script's tests: the report call removed (the record's "one call removed"), per script; for the
promotion and the cutover also the jobs loop removed and the on-disk guard removed.

**After the fix, as built.** As the approach says, with no departure. Line numbers are at `3a86ec07`:

- `tools/promote-dev-to-prod.ps1`: `Write-AppPoolSharingReport` 218-255; in `Stop-AppPoolChecked` (257) the dry run's plan
  line 267, and the call 277, straight after `Write-Step "Stopping prod app pool: $Name"` (274).
- `tools/Install-ExchangeAdminWeb.ps1`: `Write-AppPoolSharingReport` 690-715; the step 1144-1146, right before the folder
  check (1156) and the stop step (1160).
- `tools/Move-ConfigDbToShared.ps1`: `Write-AppPoolSharingReport` 246-283; `Stop-AppPoolChecked` 285, its step's
  description (288) and the call, the step's first statement (291); the call sites 816-817. The help's step 2 says it names
  the other applications.

The two reports that read jobs pass `-AppPath` through unchanged: the promotion's is `$dev` (377 at the base, now 421), and
the cutover's is `$DevPublishPath` for both calls, as before.

## Files changed

Each file at the base `84c729d8` (its blob), and the md5 of the working-tree bytes (CRLF) after:

- `tools/promote-dev-to-prod.ps1`: blob `8c5c02db37eb`; after `F752042A5CB453018076CF3DB553A116`
- `tests/ps/AppPoolControl.Tests.ps1`: blob `1a20374658bc`; after `682DFBA96A7ED195CEADC52DE1D65581`
- `tools/Install-ExchangeAdminWeb.ps1`: blob `c07588357c6f`; after `AA90AF978935DDC0D3ECEC0DC1416FD8`
- `tests/ps/InstallerConfigBackup.Tests.ps1`: blob `c35fd7397435`; after `9E123F1392F9303D0A155D71E9B9BD57`
- `tools/Move-ConfigDbToShared.ps1`: blob `cf96766cfb6b`; after `0BF384ACC5D56F69140A940D4F37B33C`
- `tests/ps/MoveConfigDbToShared.Tests.ps1`: blob `39a92d398ee4`; after `BC86EB53E8DF79CC7F8E5313007DB8F5`
- this record and its row in `.agents/review/index.md`

`tools/AppPoolControl.psm1` and `deploy.ps1` are unchanged. No version bump: ops scripts, nothing the app ships changes.

**Operator-visible change.** Before each of these stops, the run says what else the pool serves: one grey line when nothing,
otherwise one yellow warning naming each other IIS application and its folder, followed - in the promotion and the cutover -
by the active bulk jobs of each one whose folder holds a jobs database (the existing jobs warning, per application). A
listing IIS will not give is a yellow warning. Nothing new is refused; a plan or dry run shows the step as a plan line.

## Guard proof

Tests (all through the module's `-ApplicationsProbe`, set by `$PSDefaultParameterValues`; no IIS is read):

| Test | Pins |
| --- | --- |
| `tests/ps/AppPoolControl.Tests.ps1`, aud-a38b Describe, Context `cdt-58: what else the pool serves is named, and its jobs read, before the stop` (538), five tests | The promotion's real `Stop-AppPoolChecked` and report, lifted, with the real module. (558) four others named in one warning, in IIS order - one under a differently cased pool name, one with no folder, one whose folder is gone, one on a drive this host does not have (the test first proves `Join-Path` throws on it); the other pool's node and prod itself not named; jobs read exactly for the one on disk, from its `config` with dev's tool, then prod's; probe, their jobs, prod's jobs, the folder check and the stop in that order. (591) none: exactly the grey line, no warning, only prod's jobs. (605) the report's read alone failing: exactly the warning, no throw, the stop made; IIS unreadable for the folder check too: its refusal, no stop. (629) the dry run: the plan line first, before the folder check's, and no IIS read. (642, AST) one call in the script, a statement of the stop step straight after its `Write-Step`, before the jobs, the folder check and the stop; `Get-AppPoolOtherApplication` called once, inside the report |
| `tests/ps/InstallerConfigBackup.Tests.ps1`, Describe `cdt-58: the installer names the IIS applications that stop with its pool` (1602), two tests | (1624) the installer's report, lifted, with the real module: none (another pool's application beside the instance) - exactly the grey line; others - exactly one warning naming the two (one differently cased pool, one with no folder), not the instance nor the node inside its folder; unreadable - exactly the warning, no throw. (1668, AST) the statement right before the folder check is the step `Invoke-PlanOrAction "Name the IIS applications app pool $AppPoolName serves besides $PublishPath, which stop with it"`, whose body is the one call; one call and one helper call in the script, the latter inside the report |
| same file, cdt-32's region test (1444), updated | The region run for real: its report stand-in records `report(ExamplePool)`, so the calls are `report(ExamplePool),serves(Refuse)` when the folder check refuses and `report(ExamplePool),serves(Refuse),stop,mirror,start,manifest` when it passes; a plan runs neither and prints the report's line before the check's. cdt-23's and cdt-33's region tests stub the report silently, their expectations unchanged |
| `tests/ps/MoveConfigDbToShared.Tests.ps1`, aud-a38e Describe, Context `cdt-58: ...` (874), five tests | The cutover's real step, lifted, with the real module. (894) as the promotion's first, and the step's header line before the probe: others named, the one on disk read before the instance's own jobs, then the stop. (928) none. (942) unreadable: exactly the warning, no throw, the stop made (step 1b is the gate). (955) a plan: exactly the one plan line, which names the folder, and no report run. (967, AST) the report is the action's first statement and the jobs check its second; one call and one helper call; the two call sites pass `-PublishPath $DevPublishPath` and `-PublishPath $ProdPublishPath` |
| the aud-a38e stop-step tests (updated) | Each call passes `-PublishPath 'X:\dev'`; the mocked-stop test now sets the probes too, so its report asks the stand-in rather than the default `Get-Website` |

Mutations, by `bin/cdt58/Invoke-Mutation.ps1` (scratch, git-ignored): one ordinal replacement that must match exactly once,
the script's test file run on the mutant, the original bytes restored with a fresh mtime and the md5 re-checked equal to the
fixed file's, the file run again. Every restored run passed whole (101 of 101, 78 of 78, 98 of 98).

| # | Mutation | md5 mutant | Failing |
| --- | --- | --- | --- |
| P1 | promotion: the report call removed (the record's mutation) | `A36E9C8A1A7D74905525704C60F81B99` | 4 of 101: the four behavioural and AST cdt-58 tests that run or find the call |
| P2 | promotion: the others' jobs read removed | `B70B7988F049D68EC3598E6453533446` | 1: (558), no jobs read for the application on disk |
| P3 | promotion: the on-disk guard removed | `DFBFBB6A621E9B3C96A628705DBFE202` | 1: (558) - the gone folder read, and the missing drive's `Join-Path` throws |
| P4 | promotion: the dry run's plan line removed | `830A10AE40C79DC90BC1F4A730F062AE` | 1: (629) |
| I1 | installer: the report call removed from its step (the record's mutation) | `ED2617723F662296823FBB2D60A51EDC` | 2 of 78: cdt-32's region test (no `report` call) and the cdt-58 AST test |
| I2 | installer: the report rethrows an unreadable listing instead of warning | `2B1CAC69C7271AF3414B8F6CCAB5FDF6` | 1: (1624) |
| C1 | cutover: the report call removed (the record's mutation) | `95097828DC31630525B0082F7B83360C` | 4 of 98: the four cdt-58 tests that run or find the call |
| C2 | cutover: the others' jobs read removed | `C1BCB36B7AEB605B976E26DDAD7E911A` | 1: (894) |
| C3 | cutover: the on-disk guard removed | `78B62ABA6D2D02C81D5A122091740EDE` | 1: (894) |
| C4 | cutover: the prod stop handed the dev folder | `C000C775B7EEBC607D3E21EF552CCCED` | 1: (967, AST) |

Windows PowerShell 5.1.20348.5756 (scratch harness `bin/cdt58/probe-reports-51.ps1`): all three scripts parse with no
errors; each report, lifted with the real module, on none / six nodes / an unreadable listing: the grey line; one warning
naming the four others of the pool (the other pool's node and the instance left out); jobs read only for the folder on disk;
no throw on the missing drive or the unreadable listing.

Gates on the branch tip (pwsh 7.6.9):

- `Invoke-Pester tests/ps` (Pester 6.2.0): 520 passed, 0 failed, 0 skipped (base `84c729d8`: 508 passed; +12 new).
- `Invoke-ScriptAnalyzer -Path . -Recurse -Severity Error` (1.25.0): 0. With warnings, against the base files: five new,
  all `PSAvoidUsingWriteHost` on the reports' grey lines (promotion 2, installer 1, cutover 2) - the scripts' convention for
  read-only report lines, as with cdt-54's; no other new finding in the six files.
- `pwsh -NoProfile -File tools/Test-AsciiOnly.ps1`: passed. `git diff --check HEAD`: clean. All six files stay CRLF.

Not run: a real promotion, install or cutover against IIS with a shared pool.

## Coder dispute

None.

## Known gaps

Found and not fixed - candidates, outside this record:

- **deploy.ps1's own report throws on a drive that is not there.** cdt-54's `Write-AppPoolSharingReport` builds
  `Join-Path $otherFolder 'config'` outside the listing's catch, so another application of the pool whose folder is on a
  drive the host does not have ends the deploy, before the stop, with "Cannot find drive. A drive with the name 'Q' does
  not exist." - a refusal where the report is meant to warn. Measured with deploy.ps1's real function, lifted, under
  Windows PowerShell 5.1.20348.5756 and pwsh 7.6.9 (scratch `bin/cdt58/probe-deploy-missing-drive.ps1`). The three reports
  here skip a folder that is not on disk; deploy.ps1's twin is the candidate.
- **The installer reads no bulk jobs**, its own included (cdt-57's known gap), so its report names the others and reads none
  of theirs. Closing it means importing `tools/JobStateWarning.psm1` into the installer - environment-neutral, the same
  sibling import as its other two - and calling the jobs check for its own instance and the others.
- **The cutover's nested nodes.** Its serving check uses `-NestedNodes Allow` (aud-a38j): a node rooted inside an instance's
  folder under the same pool is allowed and stops with it. The report uses the shared helper's rule, under which a node at
  or inside the folder is the instance's own, so it does not name such a node. In the promotion and the installer such a
  node is refused before the stop.
- A plan or dry run prints the report as a step and reads nothing from IIS for it, as for the stop it precedes.

## Reviewer comments

None yet.

## Closeout

Pending: merge into `master`, codex review, branch deletion.