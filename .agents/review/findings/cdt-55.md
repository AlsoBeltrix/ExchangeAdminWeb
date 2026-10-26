# cdt-55: with the serving appsettings.json missing and no old default database, a move still drops a live shared ConfigStore

**Severity**: MEDIUM - the cdt-51 class, Architectural Invariant 3. `deploy.ps1` about 337 and 994, `Program.cs` about 59: an existing application whose process started with `ConfigStore:Path` on a shared database, whose `appsettings.json` was then deleted while it ran, and whose old folder has no default `config\exchangeadmin.db`, passes the cdt-51 guard ("nothing stranded"); the run skips backup and carry-forward, generates appsettings without `ConfigStore:Path`, and the restarted app opens a new default database. The shared one still exists but the app silently leaves it, with no verified pre-deploy backup of the store it was using.
**Status**: Closed - merged to master `84a31608`; codex ops-batch-15 clean. Complete once fix/cdt36 is deleted
**Branch**: `fix/cdt36` (worktree `D:\source\ExchangeAdminWeb-wt\cdt36`), after the cdt-54 commit `15066df8`
**Commit**: the `fix(cdt-55)` commit on `fix/cdt36` that carries this record (not merged)
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base 42c2690b head 7f1890f3 (merged fb2b288f); 2026-10-23 evening; capability proof succeeded (AGENTS.md read, git show --stat 7f1890f3)

## Evidence

Codex ops-batch-14 finding 1 at `fb2b288f`.

Confirmed by reading `deploy.ps1` at `15066df8`. Codex's 337 and 994 are 374 and 1034 here, after
cdt-54's additions.

The cdt-51 guard asks only about a move, and it refuses only when a database would be stranded:

```
374: if ($movesFolder) {
375:     $servingHasSettings = Test-Path -LiteralPath $servingConfigPath
376:     $servingDbPath = Resolve-ConfigDbPath -PublishPath $servingFolder
377:     if (-not (Get-ConfigStorePathSetting -PublishPath $servingFolder) -and (Test-IsSqliteConfigDbPresent -DbPath $servingDbPath)) {
```

With no `appsettings.json` and no `config\exchangeadmin.db` in the old folder, nothing refuses.
The fresh-install branch then has nothing to carry and nothing to back up:

```
1006:         $configStorePathSetting = Get-ConfigStorePathSetting -PublishPath $servingFolder
...
1034:         if ((Test-Path -LiteralPath $servingConfigPath) -or (Test-IsSqliteConfigDbPresent -DbPath $configDbPath)) {
...
1243:             Write-AppSettingsFile -Settings $config -Path $configPath -ConfigStorePath $configStorePathSetting
```

The appsettings.json it writes names no `ConfigStore:Path`. `ConfigStorePath.Resolve` reads a blank
key as the default `config\exchangeadmin.db`, a file it may create
(`Services/Storage/ConfigStorePath.cs` 37: `MustExist: false`). So the restarted application opens a
new database and leaves the shared one it was running on. No verified backup of that database is
taken.

The same is true in place, for an existing application that is not being moved. A `-Force` or
`-ConfirmFreshInstall` run over it takes the fresh-install branch whenever `-PublishPath` has no
appsettings.json (313: `$isUpgrade = (Test-Path $configPath) -and (-not $Force)`). Nothing on that
path asks whether the running application had a configuration. If its default database is still
there, the backup gate takes that one, which may not be the database the application was using.

## Approach

Written before the code. Line numbers are `deploy.ps1` at `15066df8`.

**Change.** This is the coordinator's rule: an existing IIS application whose serving
`appsettings.json` is missing is refused, whatever its config folder holds.

1. A new script-level check goes straight after the cdt-51 block (385), before the backups (387), so
   that nothing is written first (cdt-45):

   ```
   if (-not $isUpgrade -and $null -ne $existingApplication -and -not (Test-Path -LiteralPath $servingConfigPath)) {
       Write-Fail "..."
   }
   ```

   - `$servingConfigPath` is the folder the application runs from: the old folder in a move,
     `-PublishPath` otherwise. So moves and in-place runs are covered alike.
   - An upgrade is never asked, because it has the file by definition (313).
   - A fresh install that IIS has no application for is never asked either, because it has no
     running configuration to lose.
2. The refusal names the application and the missing file, and gives the reason. Nothing on disk
   says which database the running process opened, the appsettings.json this run would write names
   none, and the restarted application would leave the one it was using, with no verified backup of
   that one taken. It then lists the ways out:
   - restore the file. Earlier deploys keep copies of it under
     `<LogRoot>\ExchangeAdminWeb\ConfigBackups`, and the message names that folder.
   - or write one naming the `ConfigStore:Path` the application uses, so the run carries it over.
     That is the explicit way to supply the path: deploy.ps1 has no parameter for it.
   - or, if the application has no configuration to keep, remove it from IIS, or move it by hand.
   - It ends "Nothing was deployed."
3. Order with cdt-51: its guard runs first and keeps its own words. A move off a folder that has no
   appsettings.json but does have its own database is still refused as stranding that database.
   Every other case with no file is refused by this check. The cdt-51 comment's sentence "With
   neither a file nor that database the move proceeds" is no longer true, so it is corrected to
   say that the next check refuses that case.

Constraints:

- Invariant 6: pure ASCII.
- Invariant 3: nothing is written before the check.
- Fail-closed: when the file is not there, the run is refused.
- No version bump.

Guards, all in `tests/ps/DeployInvariants.Tests.ps1`:

- A new real-files test lifts the script-level statements from the serving-folder choice through
  the pre-move copy, as the cdt-44 and cdt-51 tests do, and runs them with the real modules.
  - Refused, with the whole message, and with no backup folder written:
    - a move off a folder with neither an appsettings.json nor a database (codex's case);
    - an in-place run with neither;
    - an in-place run whose default database is there.
  - The cdt-51 stranding refusal keeps its words.
  - Not refused:
    - an in-place run with its appsettings.json;
    - a move whose old folder has its appsettings.json;
    - a fresh install that IIS has no application for;
    - an upgrade.
- Existing tests change with the code:
  - The cdt-51 test's "neither" case went ahead and now refuses. It moves to the new test, and the
    cdt-51 test loses "and lets one with neither go" from its title.
  - The cdt-44 test's cases outside a move deployed over an existing application whose folder had
    no appsettings.json in the fixture. That folder now gets one, as a real deploy over an existing
    application needs.
  - The cdt-45 ordering test stubs `Test-Path` without `-LiteralPath`. The stub gains the parameter,
    and the call sequence it pins gains the new check's read of the folder's appsettings.json,
    after the IIS read and before the backup folder is created.

Mutations:

- M1: the check removed - the record's "fail-closed branch removed".
- M2: the check asked only on a move (`-and $movesFolder`).
- M3: the check skipped when the default database exists (`-and -not (Test-IsSqliteConfigDbPresent ...)`),
  which is the cdt-51 shape.

Files: `deploy.ps1`, `tests/ps/DeployInvariants.Tests.ps1`, this record.

**After the fix**, as built:

- `deploy.ps1` 389-399: the comment and the check. 374-375: the corrected sentence in the cdt-51
  comment.
- `tests/ps/DeployInvariants.Tests.ps1` 1644-1729: the new test. 1144-1146 and 1182: the cdt-45
  stub and its call sequence. 1210, 1219 and 1271-1273: the cdt-44 fixture. 1574 and 1636-1637: the
  cdt-51 title and the note where its "neither" case went.

## Files changed

Each file before the repair, at `15066df8` (its blob, then the md5 of its committed LF content), and the
md5 after:

- `deploy.ps1`: blob `891e9af72f06`, md5 `BF9EE7C3F3D9A9560E4F22DDB3C769C3`; after `BA766EC479555BF6B87AFADB40BC5E0E`
- `tests/ps/DeployInvariants.Tests.ps1`: blob `9bef6a0d98db`, md5 `D694B0CFB2B40223CB642D7A4CB12A12`; after `C13C2ADA853086313D8A2AB717B27F09`
- this record: copied from master's (`8e61dbf0`) and filled in

No version bump.

## Guard proof

Tests, in `tests/ps/DeployInvariants.Tests.ps1`:

| Test | Pins |
| --- | --- |
| `refuses to deploy over an existing application whose folder has no appsettings.json, moved or not, whatever its config folder holds (cdt-55, real files)` (1644) | deploy.ps1's real script-level statements, from the serving-folder choice through the pre-move copy, lifted by AST and run with the real modules against temporary folders. Refused, with the whole message from "IIS application Example Site/ExampleApp exists, but the folder it runs from has no appsettings.json ('<folder>\appsettings.json')" through the restore hint naming `<LogRoot>\ExchangeAdminWeb\ConfigBackups` to "Nothing was deployed.", and with no backup folder written: a move off a folder with neither an appsettings.json nor a database (codex's case), an in-place run with neither, and an in-place run whose default database is there. The cdt-51 stranding refusal keeps its own words. Not refused: an in-place run with its appsettings.json, a move whose old folder has its appsettings.json, a fresh install IIS has no application for, and an upgrade |
| the cdt-51 test (1574, updated) | Its two refusals are unchanged. Its "neither" case moved to the test above, and its title no longer claims such a run is let go |
| the cdt-44 test (updated) | Its cases outside a move now give the run's own folder an appsettings.json, as a deploy over an existing application must have since this fix; their expectations are unchanged |
| the cdt-45 ordering test (updated) | Its `Test-Path` stub takes `-LiteralPath`, and the call sequence it pins gains this check's read of the serving folder's appsettings.json, after the IIS read and before the backup folder is created |

Each mutation was applied to the fixed `deploy.ps1` (md5 `BA766EC479555BF6B87AFADB40BC5E0E`) by a script
that fails unless every edit matches exactly once and the mutant passes a sanity check of its own
shape. Each was run against the five suites that read `deploy.ps1`: `DeployInvariants`,
`AppPoolControl`, `InstallerConfigBackup`, `MoveConfigDbToShared` and `SqliteConfigBackup`. After each
one the original bytes were restored, the mtime refreshed and the md5 re-confirmed, and all five
suites were re-run green (442 passed, 0 failed, 0 skipped):

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| M1: the check removed (the record's mutation) | `5DF77F6D4BD49C0C75B9B19BA4019567` | 2: the cdt-55 test (the first case is not refused) and the cdt-45 ordering test (the read is gone) |
| M2: the check asked only on a move | `46D59E98A32072B0CA63DB26FA850555` | 2: the cdt-55 test (the in-place case is not refused) and the cdt-45 ordering test |
| M3: the check skipped when the default database exists, the cdt-51 shape | `CB49C7D5625C79F2B2D4DACFE5C315BA` | 1: the cdt-55 test (the in-place run whose default database is there is not refused) |

Windows PowerShell 5.1.20348.5756: `deploy.ps1` parses with no errors, and a scratch harness lifting
the same region with the 5.1 parser and the real modules refused an in-place run whose folder has no
appsettings.json but does have the default database, refused a move off a folder with neither, and
let an in-place run with its appsettings.json and a fresh install IIS has no application for go.

Gates on the final tree:

- `Invoke-Pester tests/ps` (Pester 6.2.0, pwsh 7): 505 passed, 0 failed, 0 skipped.
- `Invoke-ScriptAnalyzer -Path . -Recurse` (1.25.0): 0 errors and 321 warnings, none new - the
  per-file findings are identical to those before this change.
- `git diff --check`: clean.
- `tools/Test-AsciiOnly.ps1`: passed.
- Both files stay CRLF.

Not run: a real deploy (it needs elevation and IIS).

## Coder dispute

None.

## Known gaps

- `tools/Install-ExchangeAdminWeb.ps1` has the same shape: it reads `ConfigStore:Path` from the
  publish folder and generates an appsettings.json without it when none is there. Its cdt-21 check
  (`Assert-ConfigStorePlanDatabase`) refuses a configured path whose database is missing, but not a
  missing file over an application IIS already serves. Not fixed: this record names `deploy.ps1`,
  and the installer is standalone (Architectural Invariant 1). A candidate for the queue.

## Reviewer comments

None yet.

## Closeout

Pending: commit on `fix/cdt36`, codex review, merge into `master`, branch deletion.

## Reviewer comments (supervisor, ops-batch-15)

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base 15066df8 head 584eb2fc (merged 84a31608); 2026-10-26; capability proof succeeded (AGENTS.md read, git show --stat 584eb2fc). Clean. Codex confirmed the cdt-55 refusal reads the IIS-served folder on a repoint, runs after cdt-51 so that refusal keeps its wording, and still precedes the backup folder, the appsettings copies, the pool and ACL work, the publish, the stop and the mirror; upgrades and true no-IIS fresh installs are allowed while -Force, -AllowRepoint and -ConfirmFreshInstall over an existing application with no serving appsettings fail closed. For cdt-56 the hidden branch is gone, the fixture cleanup stays in AfterAll, and fsutil failures are reported with their exit code and output rather than collapsed into `8.3 disabled`. Artefacts: `.agents/review/ops-batch-15.result.txt`, `.stream.log`, `prompt-ops-batch-15.md`.
