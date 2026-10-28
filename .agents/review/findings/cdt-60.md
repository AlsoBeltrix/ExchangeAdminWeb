# cdt-60: the cutover's pool-sharing report hides the nested IIS applications it stops; -PlanOnly still queries the live jobs database

**Severity**: LOW. (1) `tools/Move-ConfigDbToShared.ps1` about 239 allows nested applications under the instance's folder
(`-NestedNodes Allow`), but the report at about 265 uses `Get-AppPoolOtherApplication`, which excludes descendants
(`tools/AppPoolControl.psm1` about 628), so the cutover prints "stopping it stops nothing else" and stops a nested
application with active jobs unnamed. The cdt-58 record already noted the nested gap. (2) Pre-existing: `tools/JobStateWarning.psm1`
about 151 calls `Get-ActiveBulkJobs` (which runs the app's `db active-jobs` tool, about 88) BEFORE it branches on `-PlanOnly`,
so a plan-only cutover or promotion opens the real jobs databases (read-only, but a real read - Invariant 4).
**Status**: Fixed on branch, verified; closes on guard proof - the single review dispatch is spent
**Branch**: `fix/review-findings` (worktree `D:\source\ExchangeAdminWeb-wt\review-findings`, from master `ec5a19f7`)
**Commit**: one per sub-item - each is the commit that adds its section below (`git log -- .agents/review/findings/cdt-60.md`)
**Reviewer**: codex-commercial / gpt-6-astra / max / read-only; session 01a18016-55c8-7262-bdc3-d3d86c927988; 2026-10-27

## Evidence

`.agents/review/final-review-2026-10-27.result.md` findings 4 and 5, at `b9c8f19b`.

## Approach

- (1) the cutover excludes only the instance itself from the report, keeping nested applications and their job warnings
  (a parameter on the helper, or a cutover-local filter). Pester: a nested application with jobs is named and its jobs
  read; mutation: the exclusion restored.
- (2) branch on `-PlanOnly` before `Get-ActiveBulkJobs` and print the intended check. Pester: plan mode launches no tool
  (the tool path stand-in records no call); mutation: the branch moved back.

## As implemented - (1) the cutover's report (implementer, 2026-10-27; lines at this commit)

Read first, confirmed at `ec5a19f7`: the cutover's serving check passes `-NestedNodes Allow` (`tools/Move-ConfigDbToShared.ps1`
239), so a node rooted inside an instance's folder under the same pool is accepted and stopped with it (aud-a38j). Its
report (`Write-AppPoolSharingReport`, 246) asked `Get-AppPoolOtherApplication`, which leaves out every node whose folder is at
or inside the instance's (`tools/AppPoolControl.psm1` 628, `Test-PhysicalPathInside`, which is true for equal paths too). So
such a node was not named, its jobs not read, and with nothing else the report printed "... stopping it stops nothing else."
deploy.ps1 (819, 1162), the promotion and the installer pass `-NestedNodes Refuse`: a nested node never reaches their stop.

- **A switch on the shared helper**, chosen over a cutover-local filter so one module keeps one listing rule and its path
  comparisons (lexical and final path, cdt-46) stay private to it. `-IncludeNested` (`tools/AppPoolControl.psm1` 621-622,
  described at 592-596) leaves out only the nodes AT the folder - equal lexically, or by final path when both resolve, so a
  junction to the folder is still the instance - and lists those inside it. The comparison is chosen once (633-638) and
  used for the lexical and the final path alike (643, 646). Without the switch the rule is today's, so deploy.ps1, the
  promotion and the installer are unchanged.
- **The cutover passes it** (`tools/Move-ConfigDbToShared.ps1` 270), with a comment (258-262). A nested application is then
  named in the report's warning and, its folder being on disk, read for bulk jobs from its own `config` folder with the dev
  build's tool, before the instance's own, as every other application of the pool is (cdt-58). Still a warning, never a
  refusal.

**What the operator sees**: a cutover whose pool also serves an IIS application rooted inside an instance's folder names it
in the yellow warning and shows its active bulk jobs, where it said the stop stopped nothing else. Nothing new is refused.

### Guard proof - (1)

- `tests/ps/AppPoolControl.Tests.ps1`, cdt-54's Describe, 2 tests:
  `with -IncludeNested, lists the pool's applications inside the folder and leaves out only the instance itself, however IIS
  spells it (cdt-60)` (1529) - the instance under three spellings left out, two nested nodes of the pool (one differently
  cased pool) and a sibling listed, another pool's nested node not; the same probe without the switch lists only the
  sibling. `with -IncludeNested, takes a junction to the folder for the instance and lists a real folder inside it (cdt-60,
  real files)` (1552).
- `tests/ps/MoveConfigDbToShared.Tests.ps1`, cdt-58's Context, 1 test: `names an application rooted inside the instance's
  folder under the same pool, and reads its jobs before the instance's own (cdt-60)` (928) - the cutover's real stop step,
  lifted, with the real module; the pool serves the instance at a `TestDrive:` folder and `Reports` inside it: no "stops
  nothing else" line, one warning naming `Web/Reports`, its jobs read from `Reports\config` with the instance's tool, then the
  instance's, then the stop.

Mutations, by `bin/rf-scratch/Invoke-Mutation.ps1` (scratch, git-ignored): one ordinal replacement that must match once, the
suites run on the mutant, the fixed bytes written back and their MD5 checked. Fixed-file MD5s (CRLF):
`tools/Move-ConfigDbToShared.ps1` `22147094BF49CAEFBBA7585018164E25`, `tools/AppPoolControl.psm1`
`D3F9F4BCA7B533758F7A6F0B9F7EBB1E`; every restore byte-exact.

| # | Mutation | md5 mutant | Failing |
| --- | --- | --- | --- |
| C60a (the record's) | the exclusion restored: the cutover calls the helper without `-IncludeNested` | `75BE24ACD9B33D5760340ABB136924BB` | 1 of 99 (`MoveConfigDbToShared.Tests.ps1`): the cutover test - the report said "... stopping it stops nothing else." |
| C60b | the switch made inert: `-IncludeNested` compares at-or-inside, as the default does | `D9EAF798D7327D00DDCD6F1922548401` | 3 of 202 (the module's and the cutover's suites): both helper tests and the cutover test |
| C60c | the default changed to the switch's rule | `B050AFD0B927522D9C1649DFE7F22191` | 3 of 416 (with `DeployInvariants` and `InstallerConfigBackup`): cdt-54's "leaves out ... anything inside it", the new helper test's default half, and cdt-58's installer report test |

Checks on the fixed files: `AppPoolControl.Tests.ps1` with `MoveConfigDbToShared.Tests.ps1`, 202 of 202. PSScriptAnalyzer on
the four changed files: 0 errors, and 69 findings against 69 on the base files (none new). All four parse under Windows
PowerShell 5.1.20348.5756 with 0 errors; the helper, imported under 5.1 and 7.6.9 (scratch `bin/rf-scratch/probe-cdt60-51.ps1`),
lists the nested node with the switch and leaves it out without it. All four stay CRLF and ASCII. No version bump: ops
scripts, nothing the app ships changes.

Not run: a real cutover against IIS with a nested application.

## As implemented - (2) a plan reads no jobs database (implementer, 2026-10-27; lines at this commit)

Read first, confirmed at `ec5a19f7`: `Assert-NoActiveBulkJobsBeforeRecycle` (`tools/JobStateWarning.psm1` 138) ran
`Get-ActiveBulkJobs` (151) - which launches `<ProgramFiles>\dotnet\dotnet.exe <AppPath>\ExchangeAdminWeb.dll db active-jobs
<db>` when the database exists (88) - and only then branched on `-PlanOnly` (153). Two callers pass `-PlanOnly`: the cutover's
plan (`tools/Move-ConfigDbToShared.ps1` 318) and the promotion's dry run (`tools/promote-dev-to-prod.ps1` 270). deploy.ps1
has no plan mode and the installer never passes it (cdt-59's AST pin).

- **The plan branch first** (152-159). With `-PlanOnly` the function prints one DarkGray plan line naming the database a real
  run reads and the build whose db tool reads it - "PLAN: would check for active bulk jobs before recycle:
  exchangeadmin-jobs.db in <ConfigDir>, read with the db tool of the app in <AppPath> (a plan reads no jobs database)." - and
  returns 0, nothing checked. The line is built by interpolation, not `Join-Path`, so a folder on a drive the host lacks
  cannot throw from a plan (the cdt-58/59 lesson). A real run is unchanged from the read on (161).
- The module header (25-27) and the help for `-PlanOnly` and the output (133-137) say so.

**What the operator sees - a behaviour change in plan output, named here for the supervisor.** A plan-only cutover or a
promotion dry run no longer lists the active bulk jobs a real run would interrupt ("PLAN: recycle WOULD interrupt N active
bulk job(s): ..."); it says which jobs database a real run reads. Listing them needs the live read the review found, so the
two cannot both stand. A real run is unchanged: it reads and warns, loudly, before the stop. Briefed by the supervisor under
the mandate; the plan's preview of active jobs was the only thing it showed that it no longer shows.

### Guard proof - (2)

- `tests/ps/JobStateWarning.Tests.ps1`, Describe `cdt-60 (2): a plan launches no db tool and reads no jobs database` (167),
  2 tests. The module's tool resolver (`Get-ConfigDbToolForJobs`, private) is mocked to hand back a recording stand-in - a
  script the module runs as it runs the .NET host, which logs each call's arguments and answers one active job - over a jobs
  database that exists. (191) plan mode: no call logged, the resolver not even asked, no warning, the one plan line word for
  word, 0 returned. (202) the control, a real run: one call, `stand-in.dll db active-jobs <config>\exchangeadmin-jobs.db`, 1
  returned and the ACTIVE warning given.
- The existing plan test (111) followed the code: it asserted that a plan over the real built app counted the database's
  running job (1), which is the live read this fixes; it now asserts 0, with a comment saying why.

| # | Mutation | md5 mutant | Failing |
| --- | --- | --- | --- |
| C60d (the record's) | the branch moved back after the read | `CE69F31969340E621D559B1A19AB7623` | 1 of 11: (191), "a plan launches no db tool", got a logged call |

Fixed-file MD5 `tools/JobStateWarning.psm1` `AF12732365488F0F6F6A1FE770DB6CFE` (CRLF); restore byte-exact. Under the mutant the
real-app plan test (111) still passed - the mutant still returns 0 after its read - so the stand-in's log is the pin.

Checks: `JobStateWarning.Tests.ps1` 11 of 11; the whole of `tests/ps` - deploy.ps1's, the promotion's, the cutover's and the
installer's suites among them - 531 passed, 0 failed, 0 skipped (526 at the base, +3 for (1), +2 here). Under Windows
PowerShell 5.1.20348.5756 and pwsh 7.6.9 (scratch `bin/rf-scratch/probe-cdt60b-51.ps1`): the module imports, a plan over a real
folder and over a drive the host lacks returns 0 with no throw; the module and its tests parse with 0 errors. CRLF, ASCII.
No version bump: ops scripts.
