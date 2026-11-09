# sweep-g-35: -Prod refused "Dev is stale" for any commit since dev's, records-only ones included (sweep item 35)

**Severity**: MEDIUM - sweep item 35: `tools/deploy-pipeline.ps1 -Prod` and `-Prod -PlanOnly` threw "Dev is stale" whenever HEAD
differed from the dev manifest's commit, so an `.agents/`-only commit forced a full dev redeploy (a pool stop) before even a plan
could print.
**Status**: Merged 59522ca6; reviewed by codex review F 2026-11-09
**Branch**: `fix/sweep-g` (from master `145f513f`)
**Commit**: `aa0082f0` (`fix(sweep-g, item 35): ...`) | **Reviewer**: implementer (candidate)

## Approach (as built)

- The tracked paths whose changes never reach the build are listed once: `$script:NonBuildInputPrefixes` (`.agents/`, `docs/`,
  `tests/ps/`) and `$script:NonBuildInputSuffixes` (`.md`).
- `Get-BuildInputTreeId` hashes the mode, blob id and path of every other tracked file at a commit (`git ls-tree -r`, exit code
  read). `Assert-DevBuildCurrent` treats equal hashes as current; different hashes, or a dev commit git cannot list, are stale.
- A real `-Prod` still refuses a stale dev; `-PlanOnly` warns and goes on. Script help, README deploy section.

## Where the code goes beside the fix shape

- None. Everything else - `tools\`, the C# test project - stays a build input, so the list errs toward stale.

## Assumptions not verified live

- None beyond git being on PATH where the pipeline already needs it.

## Guard proof (`tests/ps/DeployInvariants.Tests.ps1`, "sweep item 35")

Re-run by the closing agent: `tools/deploy-pipeline.ps1` restored to `aa0082f0^` - 0 passed, 16 failed; restored, all pass. The
tests drive the lifted functions against a throwaway git repository.

| Mutation | Failing tests |
|---|---|
| Script at `aa0082f0^` (HEAD compared) | all 16 rows: the list, each path class, not stale for records-only commits, the real refusal, the plan warning, the unknown commit, the -Prod wiring |

## Deploy-time checks

- After a records-only commit on master: `deploy-pipeline.ps1 -Prod -PlanOnly` prints the dry run without "Dev is stale".
