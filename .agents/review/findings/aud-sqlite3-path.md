# aud-sqlite3-path: an elevated deploy runs a user-writable, unsigned sqlite3.exe found on PATH

**Severity**: HIGH. A privilege-escalation path on the deploy host, measured, not inferred.
**Status**: S1 landed, S2 landed (scripts use the app's db mode; no script runs sqlite3.exe), S3 pending (`docs/ConfigDbToolInApp-Plan.md`)
**Branch**: -- (direct-to-main)
**Commit**: S1 `86970a5`; cdt-1 follow-up `872f211`

## Evidence

Found from four owner questions at the end of the 2026-10-20 session; measured on the deploy
host 2026-10-21.

The elevated deploy (`tools/deploy-pipeline.ps1` carries `#Requires -RunAsAdministrator`)
executes `sqlite3.exe` resolved from PATH by `Get-Sqlite3Path` in
`tools/SqliteConfigBackup.psm1` (and the same lookup in `Get-Sqlite3PathForJobs`,
`tools/JobStateWarning.psm1`):

```powershell
$cmd = Get-Command sqlite3 -CommandType Application -ErrorAction SilentlyContinue
```

On the deploy host that resolves to
`C:\Users\mcoelho\AppData\Local\Microsoft\WinGet\Packages\SQLite.SQLite_Microsoft.Winget.Source_8wekyb3d8bbwe\sqlite3.exe`
- inside a user profile. Measured ACL: FullControl for `ANALOG\mcoelho`,
`ANALOG\SVC_SCRIPTADM` and `ANALOG\VNatesan-2`. Authenticode status: **NotSigned**.

So any code running as any of those three accounts, with no elevation, can replace that
binary; the next deploy runs it as administrator against the live config database, which
holds section-access authorization.

**Root cause is the repo's own documented instruction**: `winget install SQLite.SQLite`.
winget on this host is itself per-user (`...\AppData\Local\Microsoft\WindowsApps\winget.exe`),
so it installs into a writable profile. It also ties the dependency to one person's profile -
a deploy by another admin may not find `sqlite3` and fails at the backup step - and may not
work at all on Server Core or under a service account. There was also no minimum-version
check, and `VACUUM INTO` needs 3.27+.

The first proposal (install machine-wide, refuse a user-writable binary when elevated, PATH as
a warning fallback) was not adopted: the owner ruled 2026-10-21 to replace the dependency
rather than harden its lookup (plan D1).

## Approach

`docs/ConfigDbToolInApp-Plan.md` (Approved 2026-10-21). The app already ships its own SQLite
engine; it gains a `db` command-line mode (`backup`, `check`, `active-jobs`, `init`) and the
scripts call the binary they are about to deploy through `<ProgramFiles>\dotnet\dotnet.exe`,
which only administrators can write. No PATH search remains.

### S1 (this commit): the app's `db` mode

- `Services/Storage/ConfigDbMaintenance.cs` - the operations. Raw unpooled `SqliteConnection`s
  with explicit modes; never `SqliteConnectionFactory`, never `journal_mode=WAL`. `backup`
  opens the source read-only, `VACUUM INTO`, integrity-checks the copy read-only, and deletes
  the copy (and any sidecar) on any failure. It refuses a destination that exists, and also a
  stale `-wal`/`-shm`/`-journal` beside a missing one.
- `Services/Storage/ConfigDbCli.cs` - argument parsing; exit 0 ok, 1 usage (to stderr), 2
  failure. `active-jobs` runs the exact query `JobStateWarning.psm1` runs today and prints the
  same five pipe-delimited columns, NULL as empty.
- `Program.cs` - dispatch after `RegisterProvider`, before `Log.Logger` and
  `WebApplication.CreateBuilder`; `return 0;` after the `finally`. The existing
  try/catch/finally compiled unchanged with that shape, so the local-function fallback in
  plan 3.1 was not needed. The fall-through still exits 0 exactly as before, including after
  a caught fatal exception.
- Base version 2.33.0 -> 2.34.0. No module bumps.
- No script changes: `sqlite3.exe` is still what the scripts use after S1.

### Verified behaviour worth keeping

- The `VACUUM INTO` copy of a WAL-mode source has header bytes 18/19 = 1 (rollback journal,
  not WAL) and no `-wal`/`-shm` sidecars after the read-only integrity check. Asserted.
- A read-only open of a WAL database whose rows live only in an un-checkpointed `-wal`
  reads them correctly (the copy has them) and leaves the main file's bytes and mtime
  untouched. A read-write open checkpoints them into the source on close - mutation 2 below.

## Guard proof

Tests (`ExchangeAdminWeb.Tests/ConfigDbMaintenanceTests.cs`, `ConfigDbCliTests.cs`, 31 cases):

| Test | Pins |
| --- | --- |
| `Backup_OfWalDatabaseWithOpenUncommittedWriter_CopiesOnlyCommittedRowsAndPassesIntegrity` | open writer, uncommitted rows excluded, copy passes integrity |
| `Backup_LeavesSourceBytesAndLastWriteTimeUnchanged` | source untouched with committed rows only in the WAL |
| `Backup_CopyHasNoSidecarsAndIsNotInWalMode` | no `-wal`/`-shm`; header bytes 18/19 = 1 |
| `Backup_OfCorruptSource_ThrowsAndLeavesNoDestination`, `ConfigDbCliTests.Backup_OfCorruptSource_ExitsTwoAndLeavesNoFileBehind` | index page overwritten in place; `VACUUM INTO` copies it through, the copy's integrity check fails, exit 2, no file |
| `Backup_WhenDestinationExists_ExitsTwoAndLeavesItUntouched` | content and mtime unchanged |
| `Backup_WhenSourceMissing_*` | exit 2 / throw, nothing created |
| `Backup_Succeeds_PrintsOkThenTheDestinationPath` | stdout contract; a quote in the dest path |
| `Check_*` | good -> ok/0; corrupt, missing, garbage -> 2 |
| `ActiveJobs_*` | Running+Queued only, submission order, five columns, NULL ticket empty; none -> empty/0; missing/no table -> 2 |
| `Init_*` | creates, passes integrity, prints path; refuses existing |
| `TryRun_ReturnsFalseForNoArgumentsAndForAnyOtherFirstArgument` | hosting untouched |
| `AnyOtherArgumentShape_PrintsUsageToStderrAndExitsOne` (11 shapes) | usage/exit 1 |
| `Program_DispatchesTheDbModeBeforeTheLoggerAndBeforeTheWebHostBuilder` | Program.cs tripwire |

Mutation round (each: md5 before/after confirmed changed, rebuilt, named test failed, restored,
md5 matched, file touched, rebuilt; final restored build 31/31 green):

| Mutation | Failed |
| --- | --- |
| 1. `backup` skips the integrity check | `Backup_OfCorruptSource_ThrowsAndLeavesNoDestination`, `Backup_OfCorruptSource_ExitsTwoAndLeavesNoFileBehind` |
| 2. `backup` opens the source `ReadWriteCreate` (call site) | `Backup_LeavesSourceBytesAndLastWriteTimeUnchanged` |
| 3. dispatch moved below `CreateBuilder` (unindented, so the regex still matches and the ordering assertion is what fails) | `Program_DispatchesTheDbModeBeforeTheLoggerAndBeforeTheWebHostBuilder` |
| 4. `active-jobs` filter drops `Queued` (xUnit half of plan row 7) | `ActiveJobs_ReturnsRunningAndQueuedOnlyInSubmissionOrderAsFivePipeColumns` |
| 5. destination-exists refusal removed | `Backup_WhenDestinationExists_ExitsTwoAndLeavesItUntouched` |

Plan rows 4-7 (resolver, module throw, skip gate, jobs Pester) belong to S2.

### S2 - the scripts use it

`tools/SqliteConfigBackup.psm1` gains `Resolve-ConfigDbTool -AppPath [-PlanOnly]` (host from
`$env:ProgramFiles\dotnet\dotnet.exe`, assembly from the folder given; action mode throws
naming a missing file or an app older than 2.34.0) and loses `Get-Sqlite3Path` /
`Assert-Sqlite3Available`. Backup, copy and integrity check run `db backup` / `db check` and
trust the exit code only. `tools/JobStateWarning.psm1` runs `db active-jobs`; every failure,
including a missing or too-old tool, stays a warning. Callers: `deploy.ps1` (staging for the
backup and job check, resolved right after publish; the publish folder for the post-deploy
check, because staging is gone by then), the installer (staging), promote (dev), cutover
(dev). Pester drives the built app (`tests/ps/Get-BuiltApp.ps1`); no suite skips; CI builds
the app instead of installing sqlite3. Pester 271 passed / 0 failed / 0 skipped (was 255) with
no sqlite3 on PATH.

Mutation round (each: applied at the call site, md5 changed, named test failed, restored, md5
matched the original, file touched, named test green):

| Mutation | md5 before -> mutated | Failed |
| --- | --- | --- |
| Plan row 4: resolver uses `Get-Command dotnet` | `7EEC462E -> 63E874E3` | `resolves the .NET host from Program Files, never from PATH` |
| Plan row 5: Backup's throw replaced by `Write-Warning` | `7EEC462E -> F5F394F2` | `THROWS (aborts) when the source DB is corrupt, and leaves no backup file` |
| Plan row 5: integrity throw replaced by `Write-Warning` | `7EEC462E -> 21213952` | `integrity helper passes on a good DB and throws on a corrupt one`; cutover `fails naming the path when the shared file is not a sound database (scdi-3)` |
| Plan row 6: one `-Skip:` reintroduced | `5415573B -> 663810EF` | full suite `SkippedCount` 1 (what the CI gate fails on) |
| Plan row 7: `active-jobs` filter drops `Queued` (C#, rebuilt) | `44BB8146 -> DD86A99A` | Pester `detects Running and Queued jobs but not terminal ones, with every column parsed`; xUnit `ActiveJobs_ReturnsRunningAndQueuedOnlyInSubmissionOrderAsFivePipeColumns` |
| CI build step disabled | `97FA06E7 -> E3E2705F` | `builds the app before running Pester` |
| CI `setup-dotnet` removed from the powershell job | `97FA06E7 -> 697A4005` | `installs the .NET 10 SDK in the powershell job before building` |
| `Get-Command sqlite3` added to a tools script | `ACB4CDA2 -> 784BEF8A` | `no tracked ops script resolves sqlite3 with Get-Command` |
| installer's fatal resolve made `-PlanOnly` | `5E1479A2 -> 35F258AC` | `treats a missing app binary as fatal, not as a warning` |
| deploy backup run from `$PublishPath` instead of staging | `31BCBE2A -> 50D51520` | `runs the db tool from the binary it is about to deploy, resolved before anything changes` |
| resolver returns without the action-mode throw | `7EEC462E -> D7E045C2` | `throws naming the missing assembly in action mode` |
| resolver version floor removed | `7EEC462E -> C06806C1` | `refuses an app binary older than the db mode, before running it` |
| jobs version floor removed | `23C9BE2B -> 1CD4EA64` | `warns and returns nothing, without running it, when the app binary predates the db mode` |
| jobs read failure throws instead of warning | `46FB5300 -> BB9937F6` | `warns and returns nothing, never throws, when the jobs DB is unreadable` |

Plan risk 4 corrected in S2: an app older than the db mode would not exit 1. Its Program.cs
passes the arguments to the web host builder, whose command-line configuration skips bare
words, so it would try to start the app (inferred from the provider's behaviour; an old build
was not run). Both modules therefore check the assembly's file version (>= 2.34.0) before
running it; the exit-1 message is kept for any other usage error.

## Known gaps

- The per-user binary on the deploy host should be removed once S2 is deployed; owner's host
  action (plan section 10).
- The first elevated dev deploy after S2 is the only end-to-end proof that a deploy no longer
  touches `sqlite3.exe`; `tools/deploy-pipeline.ps1 -Prod -PlanOnly` (AC7) is also the
  owner's. Neither was run in S2.

## Closeout

S1 and S2 on `master`. Independent review of S2 not yet run.
