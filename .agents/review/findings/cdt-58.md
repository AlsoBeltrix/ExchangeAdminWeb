# cdt-58: promote, install and cutover stop a pool without naming its other applications

**Severity**: LOW - cdt-54 closed this for `deploy.ps1` and exported `Get-AppPoolOtherApplication`; the promotion,
installer and cutover paths still stop a pool with no word about the other applications it serves or their bulk jobs.
**Status**: Open - admitted 2026-10-26 from the cdt-36 agent's fourth hand-back; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: implementer (cdt-36 agent, candidate; not yet confirmed by the supervisor)

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

## Guard proof

Pending.