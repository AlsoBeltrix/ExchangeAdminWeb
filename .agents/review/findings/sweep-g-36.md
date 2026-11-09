# sweep-g-36: plan mode meant something different in each ops script, so a plan could not catch a cdt-61-class refusal (sweep item 36)

**Severity**: MEDIUM - sweep item 36: the promotion's dry run printed its IIS checks as plan lines and read nothing from IIS; the
cutover's plan stopped after a live `db check` on a host already cut over, before its IIS checks; `deploy.ps1` had no plan mode
(and `deploy-pipeline.ps1 -Dev -PlanOnly` skipped the dev deploy entirely).
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/sweep-g` (from master `145f513f`)
**Commit**: `ab7904fb` (`fix(sweep-g, item 36): ...`) | **Reviewer**: implementer (candidate)

## Approach (as built)

- **`deploy.ps1 -PlanOnly`** (new switch). The read-only checks the script already makes first (the IIS application lookup,
  cdt-36/42, cdt-44, cdt-51, cdt-55) run as they are. Then one script-level `if ($PlanOnly) { ... return }` block, placed before
  the first write (the backup folder), prints every remaining step in order and makes only the real run's reads: the pool's
  identity (`Test-Path`/`Get-ItemProperty` on `IIS:\AppPools`), `Resolve-ConfigDbTool -PlanOnly` (paths and the item 12 folder
  verdict), the config DB path, `Write-AppPoolSharingReport -PlanOnly`, the job check as a plan line, the binding from the lookup
  already made, and `Assert-AppPoolServesPath -NestedNodes Refuse`. A refusal one of those reads would make is printed
  (`Write-PlanRefusal`, counted in the closing line) and the plan goes on. Nothing is prompted for: a fresh-install plan names the
  values a real run would prompt for. Where the real run creates or moves the application first, the binding and serving checks
  are named, not read (IIS does not show that state yet). The real run's code is unchanged.
- **`deploy-pipeline.ps1 -Dev -PlanOnly`** runs `deploy.ps1 -PlanOnly`; help example added.
- **Promotion dry run**: when elevated with the IIS: drive, it now makes `Get-AppPoolOtherApplication` (via the sharing report,
  jobs as plan lines) and `Assert-AppPoolServesPath` before the stop and prints their answers, a refusal printed not thrown;
  otherwise it says the IIS checks were not run and why.
- **Cutover plan on a host already cut over**: the shared-DB check's answer becomes a plan line, the step 1b IIS checks run, and
  the plan ends after them - with "nothing to do", or with the check's failure (still a failure in plan mode, scdi-3). A real run
  is unchanged.
- README deploy section ("`-PlanOnly` means the same in each ops script").

## Where the code goes beside the fix shape

- An earlier attempt on this branch (uncommitted, left by a stopped agent) wrapped each write in `deploy.ps1` in its own
  `if ($PlanOnly) ... else` in place. It broke 20 existing structural tests that lift the mode branches by AST (cdt-16, -19, -28,
  -31, aud-a38j/k ...) and changed the real run's shape. It was discarded for the single plan block, which leaves the real run
  byte-for-byte as it was; the cost is that the plan's step list is a second description of the steps (drift risk, noted).
- Three count tests in `DeployInvariants.Tests.ps1` (cdt-5, cdt-28, cdt-54) now leave the plan block out of their counts, via
  `Test-InDeployPlan`; the plan's own calls are pinned by the item 36 tests.
- The promotion's dry run imports WebAdministration when elevated (loading a module writes nothing).

## Assumptions not verified live

- `deploy.ps1 -PlanOnly`, the promotion's IIS-reading dry run and the cutover plan have run only under Pester stand-ins; none has
  been run on the host (it needs elevation and IIS). The owner's check: the deploy-time checks below.

## Guard proof

| Mutation | Failing tests |
|---|---|
| deploy.ps1 plan block's backup-folder line replaced by `New-Item` | 4 of 6 item 36 deploy tests (AST no-writer, run writes nothing x3) |
| deploy.ps1 plan's sharing report without `-PlanOnly` | "an upgrade plan makes the reads ..." |
| `tools/Move-ConfigDbToShared.ps1` and `tools/promote-dev-to-prod.ps1` at the pre-fix version | cutover: "reports nothing to do only when ..." (IIS checks reached), "a plan whose shared file is missing still prints the IIS checks"; promotion: the 4 dry-run tests and the import AST test (7 failed) |

## Deploy-time checks

- Elevated, Windows PowerShell 5.1 on the host: `tools\deploy-pipeline.ps1 -Dev -PlanOnly` prints every step, "IIS serves ... from
  app pool ... alone", the db tool verdict, and "Plan complete (UPGRADE) ... 0 refusal(s)"; nothing in the dev folder, the backup
  folder or IIS changes (compare timestamps).
- `tools\deploy-pipeline.ps1 -Prod -PlanOnly`: "Dry run: IIS serves D:\inetpub\... from app pool ... alone".
- `tools\Move-ConfigDbToShared.ps1` (plan) on the cut-over host: the shared-DB line, the two `Assert-AppPoolServesPath` lines and
  their answers, then "nothing to do".
