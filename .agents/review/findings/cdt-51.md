# cdt-51: -AllowRepoint strands an old default config database when the old appsettings.json is gone

**Severity**: MEDIUM - Architectural Invariant 3. `deploy.ps1` about 333 and 983: the cdt-44 stranded-database guard and the fresh-install backup are both gated on `Test-Path` of the old folder's `appsettings.json`. With the IIS app serving `<old>` whose `appsettings.json` is missing but whose `config\exchangeadmin.db` still exists, `-ConfirmFreshInstall -AllowRepoint` (or `-Force -AllowRepoint`) passes the move check, skips both, re-points IIS to `<new>` and writes new appsettings without `ConfigStore:Path`, so the app opens a fresh default database while the old operational one is left behind, not verified-backed-up.
**Status**: Verified on branch; awaiting merge and codex review - admitted 2026-10-23 evening from codex ops-batch-13 on cdt-42..46; inside the released scope
**Branch**: `fix/cdt36` (worktree `D:\source\ExchangeAdminWeb-wt\cdt36`), after the cdt-50 commit `ff36a296`
**Commit**: the `fix(cdt-51)` commit on `fix/cdt36` that carries this record (not merged)
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base 493c270d head 42c2690b (merged 0c9917d5); 2026-10-23 evening; capability proof: AGENTS.md read and git show --stat 42c2690b both succeeded

## Evidence

Codex ops-batch-13 finding 1 at `0c9917d5`.

## Approach

Written before the code. Line numbers are `deploy.ps1` at `ff36a296`.

**Mechanism.** Both checks key on the old folder's `appsettings.json` existing:

```
332: $servingConfigPath = Join-Path $servingFolder "appsettings.json"
333: if ($movesFolder -and (Test-Path -LiteralPath $servingConfigPath)) {
334:     $servingDbPath = Resolve-ConfigDbPath -PublishPath $servingFolder
335:     if (-not (Get-ConfigStorePathSetting -PublishPath $servingFolder) -and (Test-IsSqliteConfigDbPresent -DbPath $servingDbPath)) {
336:         Write-Fail "... keeps its config database inside the folder it is being moved from ..."
...
983:         if (Test-Path -LiteralPath $servingConfigPath) {
984:             Write-Step "Backing up the existing runtime configuration"
```

`Resolve-ConfigDbPath` already answers without the file. With no `appsettings.json` there is no
`ConfigStore:Path`, so it returns `<old>\config\exchangeadmin.db`. But 333 never asks when the file
is missing, and 983 skips the verified backup. The move then goes ahead: IIS is re-pointed, and the
regenerated `appsettings.json` names no database, so the application opens a new, empty one in
`<new>\config`. The database it ran on is left in `<old>`, with no verified backup.

**Change.**

1. 333: the guard asks on every folder move. It resolves the old folder's database
   (`Resolve-ConfigDbPath`), which is the default `config\exchangeadmin.db` when no `ConfigStore:Path`
   is named. When nothing names a `ConfigStore:Path` - the file is missing, or names none - and that
   database exists, the move is refused, before anything is written. The refusal says which of the
   two it is ("its appsettings.json names no ConfigStore:Path" / "it has no appsettings.json to name
   one"). It names the database. It says what to do: restore the `appsettings.json` that names it, or
   move the database to a shared path first (`tools\Move-ConfigDbToShared.ps1`), or move the
   application in IIS by hand. With neither an `appsettings.json` nor a database in the old folder,
   the move proceeds - nothing is stranded, the half-removed case. The "-AllowRepoint: the
   configuration is backed up from ..." warning still prints only when there is an `appsettings.json`
   to back up.
2. 983: the verified backup's gate becomes "the serving folder's `appsettings.json` exists, or the
   database `$configDbPath` resolves to does". The finding names this gate too. In a move, every case
   that would need it now passes or is refused before it. Outside a move, it closes the same gap for
   a `-Force` or `-ConfirmFreshInstall` run over a folder whose `appsettings.json` is gone but whose
   database remains: that database is now backed up, verified, before the deploy (Invariant 3). It
   was not before, though the instance goes on opening it from the same folder.

Not changed: the pre-move copy of `appsettings.json` (361), which copies the file only when there is
one.

Constraints: Invariants 3, 5 and 6; nothing written before a refusal (cdt-45). No version bump.

Guards, in `tests/ps/DeployInvariants.Tests.ps1`:

- Behavioural, real files: the script-level statements from `$servingFolder = $PublishPath` through
  the pre-move copy, run under `Stop` against temporary folders with the real modules. A move off a
  folder with no `appsettings.json` but a `config\exchangeadmin.db` is refused, naming the database
  and "has no appsettings.json". No backup folder is created. A move off a folder with neither
  proceeds, with nothing copied.
- Behavioural, real files: the fresh-install branch's statements from the `ConfigStore:Path` read
  through the verified backup, with only `Backup-SqliteConfigDb` stubbed. A serving folder that has a
  `config\exchangeadmin.db` but no `appsettings.json` is backed up from that database. One with
  neither is not backed up.
- The cdt-9 AST pin on the gate follows the new condition.

Mutations: the default-path check removed (the guard back to `$movesFolder -and (Test-Path ...)`,
the record's mutation); the backup gate back to `appsettings.json` alone.

Files: `deploy.ps1`, `tests/ps/DeployInvariants.Tests.ps1`, this record.

**After the fix**, as built: `deploy.ps1` 333-350 (the comment and the guard, on every folder move,
with the reason chosen by whether the file exists), 994 (the widened gate, its comment above).

## Files changed

Before the repair at `ff36a296` (blob; md5 of the committed LF content), and after:

- `deploy.ps1` - `701c2fd8c03d`, `8ED4217FA9641D2A5E54518C666DF53B`; after `FB108AD1005FA20B94F84A70E4EA4DF7`
- `tests/ps/DeployInvariants.Tests.ps1` - `897a54432d30`, `1E00F2238817CA5408D8A7D863791F8E`; after `20B7D27FA87DC11B518CA4DF4D1A73CE`
- this record - copied from master's and filled in

No version bump.

## Guard proof

Tests, `tests/ps/DeployInvariants.Tests.ps1`, Describe `deploy.ps1`:

| Test | Pins |
| --- | --- |
| `refuses a -AllowRepoint move off a folder whose appsettings.json is gone but whose own config database remains, and lets one with neither go (cdt-51, real files)` (1480) | The real statements from `$servingFolder = $PublishPath` through the pre-move copy, under `Stop`, against temporary folders with the real modules, as a `-Force -AllowRepoint` move. No `appsettings.json` but a `config\exchangeadmin.db`: refused - "keeps its config database inside the folder it is being moved from ('<old>\config\exchangeadmin.db'): it has no appsettings.json to name one ... with no verified backup taken. Restore the appsettings.json that names it, or move the database to a shared path first (tools\Move-ConfigDbToShared.ps1) ... Nothing was deployed." - and no backup folder is created. An `appsettings.json` naming no `ConfigStore:Path`, with the database: refused, with "its appsettings.json names no ConfigStore:Path". Neither: proceeds; the serving folder is the old one; no pre-move copy; no "backed up from" line |
| `takes the verified backup of a serving folder's own database when its appsettings.json is gone (cdt-51, real files)` (1554) | The fresh-install branch's real statements from the `ConfigStore:Path` read through the verified backup, with only `Backup-SqliteConfigDb` stubbed. A folder with a `config\exchangeadmin.db` and no `appsettings.json`: one backup, of `<folder>\config\exchangeadmin.db`. A folder with neither: no backup |
| `takes the verified online backup on -Force ... (cdt-9)` (updated pin) | The backup's one gate is `(Test-Path -LiteralPath $servingConfigPath) -or (Test-IsSqliteConfigDbPresent -DbPath $configDbPath)` |

Mutations on the fixed `deploy.ps1` (md5 `FB108AD1005FA20B94F84A70E4EA4DF7`), each applied by a script that
fails unless every edit matches exactly once and the mutant passes a sanity check of its own shape,
run against `DeployInvariants.Tests.ps1`; then the original bytes restored, the mtime refreshed, the
md5 re-confirmed and the file re-run green each time (127 passed):

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| R0 the whole fix reverted (`deploy.ps1` as at `ff36a296`; byte-identical) | `8ED4217FA9641D2A5E54518C666DF53B` | 3: both cdt-51 tests and the cdt-9 pin |
| D1 the default-path check removed: the guard asked only when the old `appsettings.json` exists (the cdt-44 shape) - the record's mutation | `A8CF8531FC3FCC9C229EDB0DC4F1E811` | 1: the guard test (the folder with no `appsettings.json` and its own database moved without a word) |
| D2 the backup's gate back to `appsettings.json` alone | `7E5D7D9667F391D65540FD977B767C18` | 2: the backup test ("Expected 1 ... but got 0") and the cdt-9 pin |

Windows PowerShell 5.1.20348.5756: `deploy.ps1` parses with no errors, and the cdt-44 scratch harness,
run with four cases against temporary folders and the real modules, gave the Pester results. No
`appsettings.json` with a database: refused, no backup folder. Neither: proceeds, no copy. An
`appsettings.json` naming no `ConfigStore:Path` with a database: refused. One naming a shared
database: proceeds, with the pre-move copy.

Gates on the final tree: `Invoke-Pester tests/ps` (Pester 6.2.0, pwsh 7) 496 passed, 0 failed, 0
skipped; `Invoke-ScriptAnalyzer -Path . -Recurse -Severity Error,Warning` (1.25.0) 0 errors, 319
warnings, none new; `git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` passed; both files stay
CRLF.

Not run: a real move (it needs elevation and IIS).

## Coder dispute

None. The gate widening (change 2) goes slightly past the record's outline, and Approach says why:
the finding's severity names that gate as well.

## Known gaps

- The jobs and usage databases a moved-off folder keeps stay where they are (Invariant 3, as in
  cdt-44). Only the config database is guarded here.

## Reviewer comments

None yet.

## Closeout

Pending: commit on `fix/cdt36`, codex review, merge into `master`, branch deletion.
