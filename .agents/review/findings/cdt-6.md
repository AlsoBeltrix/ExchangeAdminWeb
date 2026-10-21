# cdt-6: a pre-deploy backup failure leaves the staging publish behind

**Severity**: LOW. No deploy proceeds and nothing is mirrored; the leak is a
`<PublishPath>.staging.<timestamp>` folder next to the site holding a full published build and
the repo's `appsettings.json`, which the script's own comments call "real environment values".
It accumulates one folder per failed run and is cleaned by nobody. The cleanup the script
promises on failure paths ("Clean it on failure paths too") covered only failures inside the
mirror try.
**Status**: Fixed on master; awaiting verdict
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`
**Reviewer**: implementer (surfaced by the S2 agent, confirmed by the supervisor)

## Evidence

`deploy.ps1` at `426e137` (after cdt-5). Staging is created by the publish:

```
405: $StagingPath = "$PublishPath.staging.$timestamp"
...
409:     dotnet publish -c Release -o $StagingPath --nologo
410:     if ($LASTEXITCODE -ne 0) { Write-Fail "dotnet publish failed (exit $LASTEXITCODE)" }
```

and removed in exactly three places: the catch around `Resolve-ConfigDbTool` (lines 423-428,
which handles only that call), and the two inner `finally` blocks that restart the pool
(upgrade line 606, fresh install line 833), which only cover throws from inside their own
`try` (robocopy onward). Exception flow traced, every failure below was outside all three:

| Line (426e137) | Failure | Mechanism |
| --- | --- | --- |
| 409-410 | `dotnet publish` fails after writing part of staging | native exit code converted by `Write-Fail` (throw); the enclosing finally only runs `Pop-Location` |
| 443 | appsettings backup `Copy-Item` fails | cmdlet error, terminating under `$ErrorActionPreference = "Stop"` |
| 466 | `ConfigStore:Path` set but the database is missing | `Write-Fail` (throw) |
| 473 | `Backup-SqliteConfigDb`: tool missing/too old (`Resolve-ConfigDbTool` throw), backup or integrity failure (`Assert-ConfigDbToolSucceeded` throw) | throw from `tools/SqliteConfigBackup.psm1` |
| 483-485, 510 | config file copy or appsettings `ConvertFrom-Json` fails | cmdlet error under "Stop" |
| 523, 734 | IIS app bound to a different pool | `Write-Fail` (throw) |
| 530, 741 | `Stop-AppPoolVerified`: state unreadable or pool will not stop | throw from `tools/AppPoolControl.psm1` |
| 686-718 | fresh install: parent site missing (`Write-Fail`), or an IIS cmdlet fails | throw / cmdlet error under "Stop" |
| 725 | fresh install: the unset `-ConfigDir` (cdt-5, now fixed) | parameter-binding error |
| 599, 602 / 827, 830 | inside the inner finally: `Start-AppPoolWithRetry` rethrows after its last attempt, or `Write-DeploymentManifest` throws | throw inside the finally skips that finally's own `Remove-Item $StagingPath` (Known Failure Class 1) |

## Approach

One outer `try { ... } finally { Remove-Item $StagingPath -Recurse -Force -ErrorAction
SilentlyContinue }` from just after `$StagingPath` is assigned (before `Push-Location` and the
publish) to the end of the mode-specific `if ($isUpgrade) { } else { }`; the region is
re-indented one level (a `git diff -w` shows the real change). The now-redundant catch around
`Resolve-ConfigDbTool` is removed. Unchanged: what is backed up, the backup-before-mirror order
(Invariant 3), and the inner finally blocks, which still restart the pool, write the manifest
and remove staging first, so the upgrade's post-deploy checks still run with staging gone and
the tool resolved from the publish folder. A second `Remove-Item` of an absent folder is a
no-op under `-ErrorAction SilentlyContinue`. No new plan-mode surface (deploy.ps1 has no plan
mode; `tools/deploy-pipeline.ps1 -PlanOnly` skips it).

Shape probe under Windows PowerShell 5.1.20348 (scratch script, same structure: outer
try/finally, `Write-Fail` throw inside an `if`, inner try/finally): the caller caught "simulated
backup failure" and zero staging folders were left.

## Guard proof

| Test (`tests/ps/DeployInvariants.Tests.ps1`) | Pins |
| --- | --- |
| `removes the staging publish on every failure after it, the pre-deploy backup included (cdt-6)` | Parses the AST: every command from `dotnet publish ... -o $StagingPath` to `# --- Final output ---`, other than the staging `Remove-Item`s themselves, lies inside the BODY (not a finally) of a try whose finally removes `$StagingPath`; the covered set includes `Resolve-ConfigDbTool`, `Backup-SqliteConfigDb`, `Assert-NoActiveBulkJobsBeforeRecycle`, `Stop-AppPoolVerified`, `Start-AppPoolWithRetry`, `Write-DeploymentManifest` |
| `cleans up the staging folder inside finally blocks (reached on failure paths)` (existing) | Still green: the two inner cleaning finally blocks remain |

Mutations on `deploy.ps1` (fixed md5 `21E060144EB732BA0DEFED6BC11522A1`, pre-comment), each
run, then restored, touched, restored md5 re-confirmed, test re-run green. Post-comment md5
`0B562E9220418988C35C42A3E085E606`.

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| Pre-fix shape (`426e137`) | `0FD63FDAA0F3CB2DEBE459A4DA040380` | the cdt-6 test (uncovered list starts "line 409: dotnet", "line 424: Resolve-ConfigDbTool", ...98 entries) |
| Outer try closed before `# --- Mode-specific deployment ---` (publish and resolve covered, backup and mirror not) | `2A84FB6CE1D8F1E69C2E04B2D676519B` | the cdt-6 test (uncovered from "line 435: Join-Path" on, including the ConfigStore `Write-Fail`) |
| Outer finally body replaced by `Write-Host 'cleanup'` (no removal) | `A42AAB59ACB7F1F6D6232F9F030A4D7A` | the cdt-6 test (everything outside the inner try bodies uncovered) |
| `$dbBackup = Backup-SqliteConfigDb ...` moved out of the try, just above `# --- Final output ---` | `A0F4F39DF652D8D9468DCF8065FFF5C9` | the cdt-6 test ("got 'line 835: Backup-SqliteConfigDb'") |
