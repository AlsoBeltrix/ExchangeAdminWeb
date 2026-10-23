# cdt-45: a run the cdt-36 check refuses has already created the backup folder and copied appsettings.json

**Severity**: LOW - `deploy.ps1 -Force` with an `appsettings.json` under the run's `-PublishPath` creates `$BackupDir` (about 259) and copies `appsettings.json` (about 268) before the IIS comparison at about 296; the run then refuses with "Nothing was deployed" while filesystem artefacts remain, possibly for the wrong folder. Nothing in IIS moves; the "read-only check first" expectation is broken and misleading residue is left.
**Status**: Merged to master `0c9917d5`; codex defect review ops-batch-13 in flight. Was: Verified on branch - admitted 2026-10-23 evening from codex ops-batch-12 on cdt-36; inside the released scope
**Branch**: `fix/cdt36` (worktree `D:\source\ExchangeAdminWeb-wt\cdt36`), after the cdt-43 commit `de48aff4`
**Commit**: the `fix(cdt-45)` commit on `fix/cdt36` that carries this record (not merged)
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base eb017102 head 493c270d (merged 72430407); 2026-10-23T19:06-19:13Z; capability proof: AGENTS.md read and git show --stat 493c270d both succeeded

## Evidence

Codex ops-batch-12 finding 2 at `72430407`. Lines as above. (Implementer's note: `$BackupDir` was
lost from the admitted text, `$` and all, in this paragraph and in the outline below, and is restored
from the file.)

## Approach

Written before the code; copied from master's record and filled in here. Line numbers are `deploy.ps1`
at `de48aff4`, where cdt-42 has moved them on: the two writes are at 269-281, the check at 299-317.

```
269: $BackupDir = Join-Path $LogRoot "ExchangeAdminWeb\ConfigBackups"
270: if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null }
...
277: if ($Force -and (Test-Path $configPath)) {
278:     $backupName = "appsettings.${timestamp}.pre-force.bak"
279:     Copy-Item $configPath (Join-Path $BackupDir $backupName)
280:     Write-Warn "Existing config backed up to $BackupDir\$backupName"
281: }
...
288: if (-not $isUpgrade -and -not $Force -and -not $ConfirmFreshInstall) {
...
310: Write-Step "Checking IIS application $ParentSite/$AppAlias"
...
317: Assert-ApplicationTargetUnchanged -Location "$ParentSite/$AppAlias" ...
```

These two are the only writes before the check: everything else above 299 computes, imports or
prints.

**Change.** Move the two writes, not the check. The block at 269-281 - the `$BackupDir` assignment,
the folder's creation, the pre-force comment and the copy - goes, unchanged, to right after the
assertion at 317 and before "Collect & validate config". So the order becomes: mode, consent check,
banner, the check, then the backups, then everything that configures.

Moving the check up instead would put it ahead of the consent check. An unconsented fresh install
would then be refused by the IIS comparison rather than by the consent check that names it, and
cdt-36's order pin (consent first) would have to change for no gain. The two writes need only come
before the fresh-install branch regenerates `appsettings.json`, which is far below. With nothing else
writing above the check, the check comes first.

Constraints: Invariants 5 and 6; no behaviour change except the order (the same folder is created,
the same file copied, under the same names and messages). No version bump.

Guards, in `tests/ps/DeployInvariants.Tests.ps1`:

- The cdt-36 AST test (the structural-order test), extended: the backup folder's creation (the
  `if (-not (Test-Path $BackupDir))` statement) and the pre-force copy (the statement holding
  `pre-force.bak`) are each one script-level statement after the assertion. No script-level statement
  before the check's step line, function definitions apart, runs a command that writes (`New-Item`,
  `Copy-Item`, `Move-Item`, `Remove-Item`, `Set-Content`, `Add-Content`, `Out-File`,
  `Set-ItemProperty`, `New-WebAppPool`, `New-WebApplication`, `Set-WebConfigurationProperty`,
  `Remove-WebConfigurationProperty`, `Set-AclChecked`, `icacls`, `robocopy`, `dotnet`,
  `Backup-SqliteConfigDb`, `Write-AppSettingsFile`).
- Behavioural, stubbed-step: the real script-level statements from the one after the banner through
  the pre-force copy, run under `Stop` as a `-Force` run with an `appsettings.json` present, with
  `Get-WebApplication`, `Test-Path`, `New-Item` and `Copy-Item` stubbed to record each call. A pool
  or folder the application is not served from refuses with the read as the only call - no folder
  created, nothing copied. The same pool and folder reach the backup folder's creation and the copy.

Mutation: the check moved back below the copy - that is, the two writes returned to their old place
above the consent check.

Files: `deploy.ps1`, `tests/ps/DeployInvariants.Tests.ps1`, this record.

**After the fix**, as built: `deploy.ps1` 269 (`$mode`), 274 (the consent check), 297-304 (the
check), 306-322 (a header, a two-line comment, then the moved block unchanged: the `$BackupDir`
assignment and the folder's creation at 310-311, the pre-force copy at 318-322), 324 ("Collect &
validate config"). The check's comment gains a clause naming the two writes.

## Files changed

Before the repair at `de48aff4` (blob; md5 of the committed LF content), and after:

- `deploy.ps1` - `88b064b13fac`, `B1F4B8BE24E2E86AB9965912D904D158`; after `0543F56D8D987B795E33C38464868E4B`
- `tests/ps/DeployInvariants.Tests.ps1` - `697a4b831afb`, `CB3C760D11B06C0B39D31565314FF6EB`; after `5EE4ECDDDD4CDD4BEF05CC41C2BE79EF`
- this record - copied from master's (`fe83c3b2`) and filled in

No version bump.

## Guard proof

Tests, `tests/ps/DeployInvariants.Tests.ps1`, Describe `deploy.ps1`:

| Test | Pins |
| --- | --- |
| `checks the application at script level, after the consent check and before anything is configured, and nothing catches its refusal (cdt-36, AST)` (extended) | Besides its cdt-36 and cdt-42 pins: one script-level `if (-not (Test-Path $BackupDir))` and one script-level `if` holding `"appsettings.${timestamp}.pre-force.bak"`, each after the assertion; and no script-level statement before the check's step line, function definitions apart, runs `New-Item`, `Copy-Item`, `Move-Item`, `Remove-Item`, `Set-Content`, `Add-Content`, `Out-File`, `Set-ItemProperty`, `New-WebAppPool`, `New-WebApplication`, `Set-WebConfigurationProperty`, `Remove-WebConfigurationProperty`, `Set-AclChecked`, `icacls`, `robocopy`, `dotnet`, `Backup-SqliteConfigDb` or `Write-AppSettingsFile` |
| `runs a -Force run's real steps from the check through the pre-force copy: a run the check refuses creates no backup folder and copies nothing (cdt-45)` (1119) | The real script-level statements from the one after the banner through the pre-force copy, lifted by AST and run under `Stop` as `-Force` over a folder with an `appsettings.json`, with `Get-WebApplication`, `Test-Path` (no backup folder yet), `New-Item` and `Copy-Item` stubbed to record each call. Another pool, and another folder: the check refuses, and the read is the only call. The same pool and folder: exactly the read, `exists(<backup folder>)`, `New-Item(<backup folder>)`, `exists(<run>\appsettings.json)`, `Copy-Item(<run>\appsettings.json\|<backup folder>\appsettings.20261023000000.pre-force.bak)`, and "Existing config backed up to ..." is printed |

Mutations on the fixed `deploy.ps1` (md5 `0543F56D8D987B795E33C38464868E4B`), applied, sanity-checked,
run against `DeployInvariants.Tests.ps1`, then the original bytes restored, the mtime refreshed, the
md5 re-confirmed and the file re-run green each time (121 passed):

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| R0 the whole fix reverted: `deploy.ps1` as at `de48aff4`, both writes above the check - "the check moved back below the copy" (byte-identical: that commit's md5) | `B1F4B8BE24E2E86AB9965912D904D158` | 2: the AST test ("Expected the actual value to be less than 25, because a refused run creates no backup folder (cdt-45), but got 38") and the steps test (the pre-force copy is not after the banner: "Expected the actual value to be greater than 33, but got 26") |
| W1 the pre-force copy alone moved back above the consent check, with the `$BackupDir` assignment it reads | `052B299DEAEC2A5D66F053A45A6B7E20` | 2: the AST test ("... because a refused run copies no appsettings.json (cdt-45), but got 37") and the steps test (as R0) |
| W2 the backup folder's creation alone moved back above the consent check | `AB13FA246A39669F59723489AAD97814` | 2: the AST test ("... because a refused run creates no backup folder (cdt-45), but got 37") and the steps test - here because the lifted copy found `$BackupDir` unset ("Cannot bind argument to parameter 'Path' because it is null."), not because it recorded a write; the AST pin is what names the order |

Gates on the final tree: `Invoke-Pester tests/ps` (Pester 6.2.0, pwsh 7) 484 passed, 0 failed, 0
skipped; `Invoke-ScriptAnalyzer -Path . -Recurse -Severity Error,Warning` (1.25.0) 0 errors, 317
warnings, none new; `git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` passed; `deploy.ps1`
parses under Windows PowerShell 5.1.20348.5756 with no errors and stays CRLF.

Not run: a real deploy.

## Coder dispute

None. The outline said "move the cdt-36 IIS read and comparison ahead of" the two writes; the writes
were moved after the check instead, for the reason in Approach (the consent check stays first).

## Known gaps

None in this finding. cdt-44 adds to the backups block, after the check, the copy of the
`appsettings.json` a moved application runs on now.

## Reviewer comments

None yet.

## Closeout

Pending: commit on `fix/cdt36`, codex review, merge into `master`, branch deletion.
