# rev-f3: deploy.ps1 -PlanOnly ends on an unguarded read instead of printing the refusal and the remaining steps

**Severity**: LOW - the plan writes nothing, but the item-36 record's claim "refusals are printed and counted ...
prints every remaining step" does not hold for two reads: the app-pool `processModel` property and the config-store
path setting are unguarded, so an unreadable or unparsable `appsettings.json` or a failing pool property read
terminates the plan before the stop, robocopy, start, cleanup and post-check steps are printed.
**Status**: Merged dde1b7fb; closed by codex review G 2026-11-09
**Branch**: `fix/rev-f` (worktree `D:\source\ExchangeAdminWeb-wt\rev-f`, from master `454fa2b7`)
**Commit**: `e45731d5` (`fix(rev-f3): deploy.ps1 -PlanOnly turns every failing real-run read into a counted refusal`) |
**Implementer**: Claude (supervised agent)
**Reviewer**: codex (Portkey) / configured route / xhigh
**Range reviewed**: 341750c0..59522ca6 (merge sweep-g)

## Evidence (master 59522ca6)

- `deploy.ps1:464-467`: `$planProcessModel = Get-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel`,
  unguarded.
- `deploy.ps1:514-516`: `$planConfigStorePathSetting = Get-ConfigStorePathSetting -PublishPath $servingFolder`,
  unguarded.
- `deploy.ps1:556-560`: only `Assert-AppPoolServesPath` sits in `try { ... } catch { Write-PlanRefusal ... }`.

## Fix shape (reviewer's proposal; verify first)

Wrap each real-run read made in the plan block in try/catch, emit `Write-PlanRefusal`, continue with placeholders.
Test: the plan tests stub successful reads and one serving-pool refusal; add a failing pool-identity read and a failing
config-store read, each expecting the refusal line plus the later steps.

## Verified real (master 454fa2b7)

`deploy.ps1:467` `$planProcessModel = Get-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel` and `:514`
`$planConfigStorePathSetting = Get-ConfigStorePathSetting -PublishPath $servingFolder` sat outside any try, under the
script's `$ErrorActionPreference = "Stop"` (`:50`); `Get-ConfigStorePathSetting` throws on an unreadable or unparsable
`appsettings.json` (`tools/SqliteConfigBackup.psm1`, sweep item 37). Either throw ended the plan before the stop step.

## As built

- `deploy.ps1` plan block: the pool `processModel` read, the config-store reads (`Get-ConfigStorePathSetting`,
  `Resolve-ConfigDbPath`, `Test-IsSqliteConfigDbPresent`) and the `Resolve-ConfigDbTool -PlanOnly` lookup (which throws
  when `ProgramFiles` is unset) are each in try/catch: `Write-PlanRefusal` names the read and its error, a placeholder
  stands in (`<the identity of app pool X, not read>`, `<the config database ... names, not read>`, `<the config DB tool,
  not located>`), and the plan prints every later step. The backup line says "its path was not read: see the refusal
  above". A `processModel` read that returns nothing is handled as the real run handles it (not a refusal).
- Refusal count: the folder verdict of `Resolve-ConfigDbTool -PlanOnly` was printed with `Write-Plan`, so a "real run
  REFUSES" verdict was not counted or marked `!`. `Resolve-ConfigDbTool` now returns `AclRefuses` (true for findings or
  unreadable permissions, plan mode), and deploy's plan prints it through `Write-PlanRefusal` when true. Additive; the
  promotion, cutover and installer plans still print `AclSummary` as before.
- The other reads in the block were checked: `Write-AppPoolSharingReport` and `Assert-NoActiveBulkJobsBeforeRecycle
  -PlanOnly` catch internally and never throw; `Assert-AppPoolServesPath` was already guarded; `Test-Path` does not throw.
- README deploy section ("a read that fails included ... is printed, marked `!` and counted").

## Guard proof (`tests/ps/DeployInvariants.Tests.ps1`, "sweep item 36"; `tests/ps/ToolFolderAcl.Tests.ps1`)

The plan-block tests gained stand-ins that can fail the pool read, the config-store read and the tool lookup.

| Mutation | Failing tests |
|---|---|
| `deploy.ps1` at `454fa2b7` | 3 of 9: "a pool identity that cannot be read ...", "a config-store path that cannot be read ...", "a db tool that cannot be located, and a folder verdict that refuses ..." (each: the throw ended the plan) |
| deploy's verdict line back to `Write-Plan $planTool.AclSummary` | 1 of 9: "a db tool that cannot be located, and a folder verdict that refuses, are each a counted refusal" |
| `tools/SqliteConfigBackup.psm1` at `454fa2b7` (no `AclRefuses`) | 1 of 42 in ToolFolderAcl: "rev-f3: in plan mode says whether a real run refuses" |
| Restored | all pass |

Each new plan test also asserts no writer stand-in was called (the existing write-detection shape).

## Deploy-time checks

- `deploy.ps1 -PlanOnly` with a deliberately broken copy of `appsettings.json` in a scratch publish folder would show the
  refusal line and the remaining steps; not run on the host.