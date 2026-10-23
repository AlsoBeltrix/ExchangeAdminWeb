# cdt-44: -AllowRepoint takes the verified config backup from the NEW -PublishPath, not from the folder the live application serves

**Severity**: MEDIUM - with `deploy.ps1 -Force -AllowRepoint -PublishPath <new>` where the new folder has no `appsettings.json`, the cdt-36 guard warns and proceeds, but the backup and config carry-forward read from the new path (`deploy.ps1` about 893, 900, 916), so the OLD live `appsettings.json` and its `ConfigStore:Path` database are neither verified-backed-up nor carried; the app is then re-pointed (about 947-948) and gets generated appsettings (about 1103), likely without the old `ConfigStore:Path`. Explicit consent lowers the likelihood; the impact is a live app moved onto a fresh or empty config store with no verified backup of the store it was serving (Architectural Invariant 3).
**Status**: Merged to master `0c9917d5`; codex ops-batch-13 clean on this finding (it raised cdt-51 against the cdt-44 move guard); closes when cdt-51 lands for cdt-44, Complete for the others. Was: Verified on branch - admitted 2026-10-23 evening from codex ops-batch-12 on cdt-36; inside the released scope
**Branch**: `fix/cdt36` (worktree `D:\source\ExchangeAdminWeb-wt\cdt36`), after the cdt-45 commit `59e2c31e`
**Commit**: the `fix(cdt-44)` commit on `fix/cdt36` that carries this record (not merged)
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base eb017102 head 493c270d (merged 72430407); 2026-10-23T19:06-19:13Z; capability proof: AGENTS.md read and git show --stat 493c270d both succeeded

## Evidence

Codex ops-batch-12 finding 1 at `72430407`. Trigger and predicted failure as above.

## Approach

Written before the code; copied from master's record and filled in here. Line numbers are
`deploy.ps1` at `59e2c31e`. cdt-42 and cdt-45 moved them: 893/900/916 are now 910/917/933, the re-point
964-965, and the regeneration 1120.

**Mechanism, read from the file.** Everything the fresh-install branch protects or carries is read
from `-PublishPath`:

```
588:     $runtimeConfigDir = Join-Path $PublishPath "config"
...
910:         $configStorePathSetting = Get-ConfigStorePathSetting -PublishPath $PublishPath
...
917:         $configDbPath = Resolve-ConfigDbPath -PublishPath $PublishPath
...
933:         if ($Force -and (Test-Path $configPath)) {
...
938:             $dbBackup = Backup-SqliteConfigDb -DbPath $configDbPath -DestDir $configDirBackup -Timestamp $timestamp -AppPath $StagingPath
...
946:                 Get-ChildItem -LiteralPath $runtimeConfigDir -File |
...
964:             Set-ItemProperty "IIS:\Sites\$ParentSite\$AppAlias" -Name physicalPath    -Value $PublishPath
...
1120:             Write-AppSettingsFile -Settings $config -Path $configPath -ConfigStorePath $configStorePathSetting
```

When `-AllowRepoint` moves an application to a new folder, `-PublishPath` is the new folder, not the
one the application runs from. There is no `appsettings.json` there (or not the live one), so 910
finds no `ConfigStore:Path`. 917 resolves to the new folder's `config\exchangeadmin.db`. 933 skips
the verified backup, because the new folder has no `appsettings.json`; with `-ConfirmFreshInstall`
the `$Force` half skips it as well. 946 copies the new folder's `config\`, not the live one. 964
then moves the application, and 1120 writes an `appsettings.json` with no `ConfigStore:Path`. The
application starts on the new folder's empty database, and the database it served from was never
backed up. The same door opens with `-ConfirmFreshInstall -AllowRepoint`, which cdt-36 covers too.

**Change.**

1. `deploy.ps1`, script level, right after the check (304) and before the backups (306): the folder
   whose configuration the application runs on now.

   ```
   $servingFolder = $PublishPath
   if (-not $isUpgrade -and $AllowRepoint -and $null -ne $existingApplication) {
       <the folder IIS reports, when it is not the run's folder>
   }
   $servingConfigPath = Join-Path $servingFolder "appsettings.json"
   ```

   The comparison is the check's own, a small `Test-SameDeployFolder` that `Assert-ApplicationTargetUnchanged`
   also calls, so the two cannot disagree; cdt-46 then changes how folders compare in one place.
   *(As built: the helper is `Test-SamePhysicalFolder` in `tools/AppPoolControl.psm1`, exported, not a
   deploy.ps1 function. Every test that lifts deploy.ps1's functions already imports that module, so
   none of them has to lift a new helper. It is also where cdt-46's canonicalisation belongs, beside
   `Assert-AppPoolServesPath`. Its semantics are the check's old inline comparison, unchanged.)*
   Outside a move this is `-PublishPath`, as before: no application, the same folder, an upgrade, or
   no `-AllowRepoint`. Without `-AllowRepoint` the check has already refused any other folder.
2. A move whose configuration names no `ConfigStore:Path`, while a database exists in the old
   folder's `config\exchangeadmin.db`, is refused right there, before anything is written. That
   database would be left behind, and the moved application would open a new, empty one in the new
   folder. The refusal says to move the database to a shared path first
   (`tools\Move-ConfigDbToShared.ps1`), so that `appsettings.json` names it, or to move the
   application in IIS by hand. This is the record's "refuse the move unless an explicit config-store
   value is supplied": a `ConfigStore:Path` is that value. A move whose configuration names one
   carries it, as below. One whose old folder has no database yet has nothing to strand.
3. The backups block, after the pre-force copy: in a move, the `appsettings.json` the application
   runs on now is copied to `appsettings.<timestamp>.pre-move.bak` and the warning names it. The
   pre-force copy stays as it was, of the file `-Force` regenerates.
4. `$runtimeConfigDir` (588) becomes `Join-Path $servingFolder "config"`. That is the live
   configuration directory in every case, and the same folder outside a move. The rest-of-config
   backup copy (946) and the active-jobs check before the stop read it; the upgrade branch is
   unaffected, because an upgrade's serving folder is its `-PublishPath`.
5. The fresh-install branch reads the serving folder:
   `Get-ConfigStorePathSetting -PublishPath $servingFolder` (910) and
   `Resolve-ConfigDbPath -PublishPath $servingFolder` (917). The missing-database refusal names
   `$servingConfigPath`, and its words stay those cdt-43's document quotes. The verified backup's gate
   (933) becomes `if (Test-Path -LiteralPath $servingConfigPath)`: there is a live configuration to
   protect. Outside a move this is the old gate. A `-ConfirmFreshInstall` run that is not a move has
   no `appsettings.json` there, or it would be an upgrade. The regenerated file (1120) is still written to
   `$configPath` in the new folder, and carries the `ConfigStore:Path` read from the serving folder.

Not changed: the re-point itself; the post-deploy database check (1178, which reads the regenerated
file and so the carried key); the upgrade branch.

Constraints: Invariant 3 (the verified online backup of the database the instance opens, before the
deploy - the point of the fix); Invariants 5 and 6; nothing written before the check (cdt-45). No
version bump.

Guards, in `tests/ps/DeployInvariants.Tests.ps1`:

- Behavioural, real files: the script-level statements from `$servingFolder = $PublishPath` through
  the pre-move copy, lifted by AST and run under `Stop` against temporary folders with the real
  `SqliteConfigBackup` and `AppPoolControl` modules. In a move whose old folder's `appsettings.json`
  names a shared database, the serving folder is the old one and `appsettings.<ts>.pre-move.bak`
  holds the old file. In a move whose old folder keeps its own database, the run is refused and no
  backup folder is created. In a move whose old folder has no database yet, the run proceeds. With
  the same folder, or on an upgrade, the serving folder is `-PublishPath` and nothing is copied.
- Behavioural, real files: the fresh-install branch's statements from `$configStorePathSetting = ...`
  through the verified backup, lifted by AST and run against temporary folders, with only
  `Backup-SqliteConfigDb` stubbed to record its `-DbPath`. In a move, the key read is the old
  folder's (the shared database), the backup is taken of that database, and the rest of the old
  folder's `config\` is copied. Outside a move, it reads `-PublishPath`, as before.
- The cdt-8 and cdt-9 AST pins follow the variables: `-PublishPath $servingFolder` in the two reads,
  and the gate `Test-Path -LiteralPath $servingConfigPath`.

Mutation: the backup source back to `-PublishPath` (in the two reads). Also: the gate back to
`$Force -and (Test-Path $configPath)`; the stranded-database refusal removed; `$runtimeConfigDir`
back to `-PublishPath`.

Files: `deploy.ps1`, `tests/ps/DeployInvariants.Tests.ps1`, this record - and, as built,
`tools/AppPoolControl.psm1` (the helper).

**After the fix**, as built: `deploy.ps1` 232 (the check's folder comparison through
`Test-SamePhysicalFolder`), 307-331 (the serving-folder choice, `$servingConfigPath` at 324, the
stranded-database refusal and the warning at 325-331), 351-357 (the pre-move copy), 625
(`$runtimeConfigDir`), 949 and 956 (the branch's two reads), 975 (the verified backup's gate).
`tools/AppPoolControl.psm1` 279-293 (`Test-SamePhysicalFolder` and its export).

## Files changed

Before the repair at `59e2c31e` (blob; md5 of the committed LF content), and after:

- `deploy.ps1` - `46bc3069bea7`, `0543F56D8D987B795E33C38464868E4B`; after `AEED2CD068888AB6F67948793743C3AF`
- `tools/AppPoolControl.psm1` - `8bb5bf890c67`, `5FA32F59DE86C57282B1EE5A65C18253`; after `407F15DB6D68E6FD33DB915C69CBC03D`
- `tests/ps/DeployInvariants.Tests.ps1` - `9d38117840c7`, `5EE4ECDDDD4CDD4BEF05CC41C2BE79EF`; after `9863A1640638E51F32FEA0F3D5C5B74B`
- `README.md` (one sentence, added after the gates: what a move does with the configuration it
  leaves) - `0fd997836244`, `ACE78A17B0FB267B260D46904A41EC84`; after `548A8D2437B9FCDC0CC745F4BFBE9E76`
- this record - copied from master's (`fe83c3b2`) and filled in

No version bump.

## Guard proof

Tests, `tests/ps/DeployInvariants.Tests.ps1`, Describe `deploy.ps1`:

| Test | Pins |
| --- | --- |
| `takes a -AllowRepoint move's configuration from the folder IIS serves now, and refuses a move that would strand its database (cdt-44, real files)` (1188) | The real statements from `$servingFolder = $PublishPath` through the pre-move copy, lifted by AST and run under `Stop` against temporary folders with the real `AppPoolControl` and `SqliteConfigBackup` modules and real file operations. A move off a folder whose `appsettings.json` names a shared database: the serving folder is the old one, and `appsettings.20261023000000.pre-move.bak` holds the old file (it has the `ConfigStore` section), with the warning naming the old folder. A move off a folder keeping its own `config\exchangeadmin.db` with no `ConfigStore:Path`: refused, "keeps its config database inside the folder it is being moved from ('<old>\config\exchangeadmin.db') ... names no ConfigStore:Path ... Nothing was deployed.", with no backup folder created. A move off a folder with no database yet: proceeds, with the copy. The same folder, an upgrade, no `-AllowRepoint`, or no folder reported: the serving folder is `-PublishPath` and nothing is copied. A pool-only move (the same folder, spelled with a trailing separator, keeping its own database): not refused, nothing copied |
| `backs up, and carries ConfigStore:Path from, the serving folder in the -Force branch, not the new -PublishPath (cdt-44, real files)` (1288) | The single `$runtimeConfigDir` assignment is `Join-Path $servingFolder "config"`. The fresh-install branch's real statements from the `ConfigStore:Path` read through the verified backup, run against temporary folders with the real module and only `Backup-SqliteConfigDb` stubbed: `$configStorePathSetting` is the old folder's shared database; the one backup is of that database, into `config.20261023000000.pre-force.bak`; the old `config\sectionaccess.json` is copied and `exchangeadmin-jobs.db` is not |
| `reads ConfigStore:Path before -Force changes anything ... (cdt-8)` and `takes the verified online backup on -Force ... (cdt-9)` (updated pins) | The read is `Get-ConfigStorePathSetting -PublishPath $servingFolder`; the database resolved for the backup is `Resolve-ConfigDbPath -PublishPath $servingFolder`; the backup's one gate is `Test-Path -LiteralPath $servingConfigPath` |

Mutations on the fixed files (md5s above, with the test file at `7A0DBFAC23C838EF1A3802831430AA26`:
the only later change to it adds `$servingFolder` to a `$null = ...` line that silences the analyzer,
and changes no assertion), each applied by a script that fails unless every edit matches exactly once
and the mutant passes a sanity check of its own shape, run against `DeployInvariants.Tests.ps1` (with
`AppPoolControl.Tests.ps1` for R0); then the original bytes restored, the mtimes refreshed, the md5s
re-confirmed and the suites re-run green each time (123 passed; 210 for R0).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| R0 the whole fix reverted: `deploy.ps1` and the module as at `59e2c31e` (byte-identical: those commits' md5s) | `0543F56D8D987B795E33C38464868E4B`; module `5FA32F59DE86C57282B1EE5A65C18253` | 4: both cdt-44 tests, and the cdt-8 and cdt-9 pins |
| S1 the backup source back to `-PublishPath` in the branch's two reads (the record's mutation) | `8D09DE2641F0DD2843717A57EE3A0841` | 3: the branch test ("Expected exactly '<root>\shared\exchangeadmin.db' ... but got $null"), and the cdt-8 and cdt-9 pins |
| S2 the backup's gate back to `$Force -and (Test-Path $configPath)` | `A1A00D28FE7142B8EC2653EC12E11B7B` | 2: the branch test (no backup taken) and the cdt-9 pin |
| S3 the stranded-database refusal disabled (`if ($false)`) | `D194036E5DCE27FAD2311C50257997FA` | 1: the script-level test (the stranded case was not refused) |
| S4 `$runtimeConfigDir` back to `Join-Path $PublishPath "config"` | `56E0484E03A79DDDDB9957F424DA1C24` | 1: the branch test's assignment pin |
| S5 the pre-move copy disabled (`if ($false)`) | `5EDA5CAD9E80C81E48B2A289666327DC` | 1: the script-level test ("Expected $true, because the appsettings.json the application runs on now is kept") |
| S6 the serving-folder choice without the folder comparison (`if ($reportedFolder)`) | `21F8C2F8CB8B553AA54A81519F6678E2` | 1: the script-level test (the pool-only move was refused as stranding its database) |

A first run of the seven was kept as evidence of a gap, not as the proof: S6 passed every test.
The script-level test's same-folder case had no `appsettings.json` in the folder, so wrongly
treating the same folder as a move had nothing to act on. The pool-only case - an `appsettings.json`
and an own database in a folder that does not move - was added, and all seven were re-run (the table
above, test file `7A0DBFAC...`).

Windows PowerShell 5.1.20348.5756: `deploy.ps1` and the module parse with no errors, and a scratch
harness lifting the script-level statements with the 5.1 parser, run against temporary folders with
the real modules, gave the Pester results for six cases (a move with a shared database; with its own
database; with none yet; the same folder; an upgrade; no `-AllowRepoint`).

Gates on the final tree: `Invoke-Pester tests/ps` (Pester 6.2.0, pwsh 7) 486 passed, 0 failed, 0
skipped; `Invoke-ScriptAnalyzer -Path . -Recurse -Severity Error,Warning` (1.25.0) 0 errors, 317
warnings, none new; `git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` passed.

Not run: a real move (it needs elevation and IIS, and would change this host).

## Coder dispute

None. The record's "refuse the move unless an explicit config-store value is supplied" is read as:
the configuration being moved must name its database (`ConfigStore:Path`), or have none yet. No new
parameter was added to supply one; `tools\Move-ConfigDbToShared.ps1` is the way to give a
per-instance database a shared path first.

## Known gaps

- `-ConfirmFreshInstall -AllowRepoint` now takes the verified backup as well: the gate no longer
  needs `-Force`. That is intended - the same door - but it is a behaviour change for that pair.
- The per-instance jobs and usage databases (`config\exchangeadmin-jobs.db`,
  `config\exchangeadmin-usage.db`) stay in the folder a move leaves. That follows Invariant 3: they
  are never backed up or promoted, and the moved instance starts with new ones. Other files in the
  old `config\` are backed up, not carried.
- The active-jobs warning before the stop now reads the serving folder's `config\`, so in a move it
  sees the instance that runs today. The pool it then stops is `-AppPoolName`, which in a pool move
  is not the pool running those jobs; the warning only warns.
- The regenerated `appsettings.json` carries `ConfigStore:Path` and nothing else from the old file.
  That is `-Force`'s design (cdt-8), unchanged.

## Reviewer comments

None yet.

## Closeout

Pending: commit on `fix/cdt36`, codex review, merge into `master`, branch deletion.

## Reviewer comments (supervisor, ops-batch-13)

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base 493c270d head 42c2690b (merged 0c9917d5); 2026-10-23 evening; capability proof: AGENTS.md read and git show --stat 42c2690b both succeeded. One candidate across the five-finding range, admitted as cdt-51 (the cdt-44 move guard skips when the old appsettings is absent). Codex confirmed: the IIS read/assertion now precedes pool writes, ACL grants, backup-folder creation, appsettings copies, publish, stop, mirror and appsettings generation; the consent refusal leaves no deploy-side state; the cdt-46 final-path helper fails closed when paths cannot resolve or compilation fails; no stale deploy.ps1 line pointer remains in the touched docs. Artefacts: `.agents/review/ops-batch-13.result.txt`, `.stream.log`, `prompt-ops-batch-13.md`.
