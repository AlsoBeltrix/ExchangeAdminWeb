# aud-a38d: under PowerShell 7 the promotion cannot see any app pool, and does not notice

**Severity**: MEDIUM. On `master` as it stands (`aud-a38` landed, this branch not merged), an
applied promotion run from PowerShell 7 stops prod, mirrors dev over it, and then cannot
start it: prod is down, and the message blames a pool that "does not exist". After `aud-a38b`
the same run refuses before anything changes - safe, but with the same wrong explanation.
Loud either way; only reachable from PowerShell 7.
**Status**: Closed - codex group review (services-batch-2, 2026-10-21) raised no defect in this change
**Branch**: `worktree-agent-a4a7066146993eb49`
**Commit**: `7f469d1` (merged)
**Reviewer**: implementer (found while fixing `aud-a38b`)

## Evidence

- `tools/promote-dev-to-prod.ps1` imports WebAdministration in its `-Apply` branch
  (`Import-Module WebAdministration -ErrorAction Stop`, line 291 at `0e18583`) and checks
  nothing after it. It has no `#Requires -PSEdition`, and neither does
  `tools/deploy-pipeline.ps1`, which calls it in-process for `-Prod`.
- WebAdministration on this host declares `CompatiblePSEditions = Desktop`
  (`C:\Windows\system32\WindowsPowerShell\v1.0\Modules\WebAdministration\WebAdministration.psd1`,
  read here). PowerShell 7 therefore loads it through the Windows PowerShell compatibility
  session - it is not on the deny list in `$PSHOME\powershell.config.json` (`PSScheduledJob`,
  `BestPractices`, `UpdateServices`) - so the import succeeds, the cmdlets are proxied, and the
  `IIS:` provider does not come with them. That last point is the repo's own recorded
  observation, not re-observed here: `.agents/machines.md` ("the `WebAdministration` IIS
  provider does not load under PowerShell 7") and the guard `deploy.ps1` carries for exactly
  this (lines 45-49):

  ```powershell
  Import-Module WebAdministration -ErrorAction Stop
  if (-not (Get-PSDrive -Name IIS -ErrorAction SilentlyContinue)) {
      ...
      throw "Run this script in Windows PowerShell 5.1 on the IIS server - the WebAdministration provider does not load under PowerShell 7."
  }
  ```

- Both verified pool helpers in `tools/AppPoolControl.psm1` decide existence with
  `Test-Path "IIS:\AppPools\$PoolName"`. Reproduced here in pwsh 7.6.6 and in Windows
  PowerShell 5.1.20348, in sessions with no `IIS:` drive: `Test-Path 'IIS:\AppPools\<name>'`
  returns **False**, with no error. So without the drive every pool reads as absent.

## Predicted observable failure

On `master`: an operator runs `deploy-pipeline.ps1 -Prod` (or the promotion directly) from
an elevated PowerShell 7 window. The bare `Stop-WebAppPool`, proxied, stops prod. The mirror
runs. `Start-AppPoolVerified`'s probe finds no pool and throws "App pool 'ExchangeAdminWeb'
does not exist, so it cannot be started. The application is DOWN." The promotion ends with
"Promotion completed and prod now holds the new binaries, but the app pool did not start ...
Prod is DOWN until it is started by hand." The pool exists; nothing in the output says the
shell was the problem.

After `aud-a38b`: the stop step's probe finds no pool first, and the promotion refuses with
"Prod app pool 'ExchangeAdminWeb' does not exist ... check -ProdAppPoolName". Nothing has
changed, but the operator is sent to check a name that is correct.

## Plan (approved by the owner's EVERYTHING go)

Owner, 2026-10-21: "fix all the bugs that you know about and any more you find while doing
so" and "... EVERYTHING." (`.agents/decisions.md` 2026-10-21, second entry). Written before
the code.

1. In the `-Apply` branch, straight after `Import-Module WebAdministration`, refuse unless the
   `IIS:` drive exists - `deploy.ps1`'s check, with a message that names the cause and the
   remedy (Windows PowerShell 5.1) and says prod was not touched. It sits before the backups
   and the stop, so a PowerShell 7 run fails before any work.
2. The dry run imports nothing and stays that way; its plan line says the check will be made.
3. Not changed: the module's probes. Making `Test-Path IIS:\...` edition-proof inside the
   module would hide the real problem - every other IIS step in these scripts needs the
   provider too - and `deploy.ps1` already handles it at the import, where the cause is known.
4. Guards in `tests/ps/AppPoolControl.Tests.ps1`: AST - the statement after the import is the
   drive check, it throws through `Write-Fail`, and it precedes the backup and the stop;
   behavioural - the exact shipped statement, lifted from the file, refuses in a session with
   no `IIS:` drive and passes once a drive of that name exists.
5. No version bump: ops-script-only.

Implemented as planned.

## Files changed

- `tools/promote-dev-to-prod.ps1` - the drive check after the import, its dry-run plan line,
  a comment.
- `tests/ps/AppPoolControl.Tests.ps1` - the
  `aud-a38d: the promotion refuses to run where it cannot see the app pools` block.

## Guard proof

| Test (`tests/ps/AppPoolControl.Tests.ps1`) | Pins |
| --- | --- |
| `imports WebAdministration once, in the apply path` | one `Import-Module WebAdministration -ErrorAction Stop` |
| `checks for the IIS: drive straight after the import, and refuses through Write-Fail` | AST: the next statement in the same block is `if (-not (Get-PSDrive -Name IIS -ErrorAction SilentlyContinue))` whose body starts with `Write-Fail` (throws) |
| `refuses in a session with no IIS: drive - which this test session is` | **the finding**: the shipped statement, lifted and run, throws with the 5.1 remedy; asserts its precondition (no drive) first |
| `lets the promotion carry on once a drive of that name exists` | the check keys on the drive, nothing else; a temporary `IIS:` drive over `$TestDrive`, removed after |
| `refuses before the backup, the mirror and the stop` | AST: every `Invoke-RobocopyChecked`, `Backup-SqliteConfigDb` and `Stop-AppPoolChecked` call starts after the check |

Mutations on `tools/promote-dev-to-prod.ps1`, fixed md5 `FB7AAAD7791A797E5D24BAE6345A455B`
(pre-comment, CRLF working-tree bytes). Same procedure as `aud-a38b`/`aud-a38c`: exact-text
replacement asserted once, md5 confirmed, both suites run, restored byte-identical, touched,
md5 re-confirmed, re-run green (149 passed, 0 failed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| D1: the check removed (the pre-fix shape) | `3C6DD8589B748F3B1DA0F31847C53B20` | 3: the AST check (the next statement is now `Assert-BackupFreeSpace`), and both behavioural tests - which then lift that statement instead, so they fail on a missing command; the AST failure is the clean one |
| D2, present but dead: `Write-Fail` downgraded to `Write-Warn` | `95A685D57D769E50D9660E7C33BA41E4` | 2: the AST check; `refuses in a session with no IIS: drive` ("no exception was thrown") |
| D3, a plausible wrong fix: check that the MODULE loaded (`-not (Get-Module -Name WebAdministration)`) - true under PowerShell 7, where it loads without the drive | `1C45DAA59E52A60B784DB50D142D6C26` | 2: the AST check; `lets the promotion carry on once a drive of that name exists` |

Post-comment md5 `7303DFBA36246818FDD84289A90F394A`.

Two things the mutation round showed, both recorded rather than smoothed over:

- **The first D2 run failed for an incidental reason.** The block lifted only `Write-Fail`, so
  the downgraded guard died on "`Write-Warn` is not recognized" instead of failing to throw.
  The block now lifts `Write-Warn` as well, and D2 fails as it should. (The same trap the
  `aud-a38b` tests met and fixed the same way.)
- **Under D3 the refusal test still passes**, because this test session has no
  WebAdministration loaded either, so "module not loaded" and "no drive" coincide here. The
  AST check and the pass-direction test are what catch D3 - which is why the pass-direction
  test exists.

**Dry run, end to end.** The promotion was run with no `-Apply` against two temp folders
holding only `appsettings.json`, under pwsh 7.6.6 and Windows PowerShell 5.1.20348. Both
print the drive check, `Assert-AppPoolServesPath ... -PhysicalPath <the prod folder>` (the
call site's `-PublishPath $prod` binding, exercised) and the verified stop as plan lines, and
both create nothing: the backup root was not created and a before/after snapshot of the
fixture tree (names, sizes, write times) is identical.

## Gates

One pwsh 7.6.6 session, Pester 6.2.0 imported before the analyzer, on the final code:

- `Invoke-ScriptAnalyzer -Path . -Recurse`: 0 errors; 298 warnings, unchanged.
- `Invoke-Pester tests/ps`: 300 passed, 0 failed, 0 skipped (295 after `aud-a38c`; 5 new).
- Windows PowerShell 5.1.20348 `Parser::ParseFile`: `tools/promote-dev-to-prod.ps1`,
  `tools/AppPoolControl.psm1`, `deploy.ps1`, `tools/Install-ExchangeAdminWeb.ps1` - 0 errors.
- `tools/Test-AsciiOnly.ps1` after `git add`: passed. `git diff --check HEAD`: clean.
- The dry run above, in both editions.

## Known gaps

- **That PowerShell 7 loads WebAdministration without the `IIS:` drive was not re-observed
  here.** The import needs elevation on this host, and this agent runs nothing elevated. The
  edition metadata and the deny list were read; the drive's absence is the repo's recorded
  observation (`.agents/machines.md`, `deploy.ps1`'s guard). If PowerShell 7 ever does bring
  the drive along, the check simply passes.
- **No elevated or `-Apply` run.** The refusal was exercised by lifting the statement, not by
  running the promotion past its elevation check.
- **Other scripts that import WebAdministration were not changed.** `deploy.ps1` has this
  check already. `tools/deploy-pipeline.ps1` imports it without one, but its `-Dev` path runs
  `deploy.ps1` and its `-Prod` path runs this script, so both are now covered.
  `tools/Move-ConfigDbToShared.ps1` reads `IIS:\AppPools\...` through `Get-ItemProperty`,
  which throws without the drive rather than reading False (`DriveNotFoundException`,
  reproduced here in both editions; that script runs under `$ErrorActionPreference = "Stop"`,
  line 74) - loud, not silent. It is reported to the supervisor with that script's other
  findings.
