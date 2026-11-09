# rev-f3: deploy.ps1 -PlanOnly ends on an unguarded read instead of printing the refusal and the remaining steps

**Severity**: LOW - the plan writes nothing, but the item-36 record's claim "refusals are printed and counted ...
prints every remaining step" does not hold for two reads: the app-pool `processModel` property and the config-store
path setting are unguarded, so an unreadable or unparsable `appsettings.json` or a failing pool property read
terminates the plan before the stop, robocopy, start, cleanup and post-check steps are printed.
**Status**: Admitted 2026-11-09 from codex review F (`.agents/review/rev-f.result.txt`); awaiting a slice
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
