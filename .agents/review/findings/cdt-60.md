# cdt-60: the cutover's pool-sharing report hides the nested IIS applications it stops; -PlanOnly still queries the live jobs database

**Severity**: LOW. (1) `tools/Move-ConfigDbToShared.ps1` about 239 allows nested applications under the instance's folder
(`-NestedNodes Allow`), but the report at about 265 uses `Get-AppPoolOtherApplication`, which excludes descendants
(`tools/AppPoolControl.psm1` about 628), so the cutover prints "stopping it stops nothing else" and stops a nested
application with active jobs unnamed. The cdt-58 record already noted the nested gap. (2) Pre-existing: `tools/JobStateWarning.psm1`
about 151 calls `Get-ActiveBulkJobs` (which runs the app's `db active-jobs` tool, about 88) BEFORE it branches on `-PlanOnly`,
so a plan-only cutover or promotion opens the real jobs databases (read-only, but a real read - Invariant 4).
**Status**: (1) fixed on branch, verified; (2) in progress
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
