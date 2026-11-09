# sweep-g-12: elevated scripts run the app DLL from folders any authenticated user can modify (sweep item 12, code half)

**Severity**: HIGH impact, a security exposure - `.agents/review/live-risk-sweep-2026-10-30.md` item 12: the deploy, promotion,
cutover and installer scripts run `<ProgramFiles>\dotnet\dotnet.exe <app folder>\ExchangeAdminWeb.dll db ...` elevated, and on the
dev host that folder inherits Authenticated Users Modify from the drive. `tools/SqliteConfigBackup.psm1`'s premise ("no new
principal can put code in this path") rested on the host's ACLs. The ACL change on the host is the owner's half and is not here.
**Status**: Fixed on branch; awaiting review; codex review F 2026-11-09 found a remainder (rev-f1)
**Branch**: `fix/sweep-g` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-g`, from master `145f513f`)
**Commit**: `ff681201` (`fix(sweep-g, item 12): ...`) | **Reviewer**: implementer (candidate)

## Approach (as built)

- New `tools/ToolFolderAcl.psm1`: reads the app folder and everything in it except `config\` and `logs\` at its top, and names
  every owner and every allow entry outside Administrators, SYSTEM, TrustedInstaller and the running account that carries a
  content-changing right (write, append, delete child, delete, change permissions, take ownership, generic all/write), inherited
  or explicit. Unreadable means refused. For a folder not created yet (a staging publish), what it would inherit from the nearest
  existing folder above it.
- `Resolve-ConfigDbTool` refuses such a folder before anything runs, naming the grants; with `-PlanOnly` it reports the verdict
  (`AclSummary`) and refuses nothing. The promotion dry run, the cutover plan, the installer plan (and, since item 36,
  `deploy.ps1 -PlanOnly`) print it.
- `JobStateWarning` does not run the tool from such a folder: a warning, as for its other cannot-check cases (it never blocks).
- No override switch: the repo's `Allow*` switches override provenance and intent checks, none a privilege guard.
- `docs/ConfigDbOperations.md` ("Who can change the app folder"), README deploy section.

## Where the code goes beside the fix shape

- The trusted set includes the account running the script (the brief names "the pool identity with read-only" as allowed, which
  this keeps: any principal may read and execute; only content-changing rights are findings).
- The suites run the tool from a narrowed copy of the build (`tests/ps/Get-BuiltApp.ps1` runs `icacls /inheritance:r /grant:r`
  on a temp copy), and `tests/ps/ToolFolderAcl.Tests.ps1` builds its fixtures with real `icacls` on temp folders. Both touch
  only folders the tests create under the temp directory.

## Assumptions not verified live

- That the dev and prod publish folders, once the owner narrows them, show only the trusted set with write rights - the owner's
  check: `deploy.ps1 -PlanOnly` (or the promotion's dry run) prints the verdict line "App folder permissions: ..." for each folder.

## Guard proof (`tests/ps/ToolFolderAcl.Tests.ps1`)

Re-run by the closing agent: `tools/SqliteConfigBackup.psm1` and `tools/JobStateWarning.psm1` restored to `ff681201^` - the
sweep item 12 tests went 11 of 11 to 4 passed, 7 failed (the refusal, each tool-running function's refusal, the plan verdicts and
the job-check warning); restored, all pass. The committing agent recorded no per-mutation table.

| Mutation | Failing tests |
|---|---|
| Both modules at `ff681201^` (no folder check) | 7 of the 11 sweep item 12 tests |

## Deploy-time checks

- Before the owner narrows the folders: `deploy-pipeline.ps1 -Dev -PlanOnly` should print a "REFUSES to run the db tool" verdict
  naming Authenticated Users. After: the verdict says only the administrators, SYSTEM, TrustedInstaller and the running account
  can change the code.
