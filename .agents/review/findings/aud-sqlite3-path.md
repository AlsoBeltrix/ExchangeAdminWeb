# aud-sqlite3-path: an elevated deploy runs a user-writable, unsigned sqlite3.exe found on PATH

**Severity**: HIGH. A privilege-escalation path on the deploy host, measured, not inferred.
**Status**: S1 landed, S2/S3 pending (`docs/ConfigDbToolInApp-Plan.md`)
**Branch**: -- (direct-to-main)
**Commit**: S1 `<pending>`

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

## Known gaps

- Until S2 lands, every deploy still runs the per-user `sqlite3.exe`. The finding is open.
- The per-user binary on the deploy host should be removed once S2 is deployed; owner's host
  action (plan section 10).

## Closeout

S1 on `master`. Independent review not yet run.
