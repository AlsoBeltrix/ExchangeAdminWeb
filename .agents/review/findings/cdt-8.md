# cdt-8: a -Force deploy drops ConfigStore:Path and silently repoints the config database

**Severity**: HIGH. `deploy.ps1 -Force` over an existing deployment regenerates
`appsettings.json` without `ConfigStore:Path`. On a shared-database instance (Architectural
Invariant 2: dev and prod open one `exchangeadmin.db` named by that key) the app then opens its
own `config\exchangeadmin.db` instead: the stale pre-cutover copy the cutover leaves in place,
or, if there is none, a new database seeded at defaults. That is the instance's authorization
and settings store - section access, protected principals, module enablement, module admins -
silently replaced, while the other instance keeps using the shared file. Nothing errors and
nothing says so.
**Status**: Merged to master; codex group review pending
**Branch**: `worktree-agent-a4611345a15a8c0bd`
**Commit**: `6297f1b` (merged)
**Reviewer**: implementer (surfaced by the cdt-5/6 agent, confirmed by the supervisor)

## Evidence

`deploy.ps1` at `0e18583`. `-Force` sends an existing deployment down the fresh-install branch:

```
17:     # Config values (used on fresh install; ignored on upgrade unless -Force)
...
151: $isUpgrade = (Test-Path $configPath) -and (-not $Force)
...
678: } else {
680:     # --- FRESH INSTALL ---
```

That branch mirrors with `'/XF', 'appsettings*.json'` (line 755), so the existing file survives
the mirror, and then replaces it with a literal that has no `ConfigStore` key:

```
766:             $config = [ordered]@{
...
819:                 AllowedHosts = "*"
820:             }
821:
822:             $config | ConvertTo-Json -Depth 10 | Set-Content $configPath -Encoding UTF8
```

The literal's eleven top-level keys are `Serilog`, `ExchangeOnline`, `OnPremExchange`,
`Delinea`, `Audit`, `OperationTrace`, `Email`, `Security`, `ServiceNow`, `Application` and
`AllowedHosts`. Nothing in the branch reads the file it replaces, and the only key-loss check
(`$lostKeys`, lines 640-645) sits inside the upgrade branch.

The app side, `Services/Storage/ConfigStorePath.cs`: an absent or blank key resolves to
`<ContentRoot>\config\exchangeadmin.db` with `MustExist: false`, "created on first start if
missing". `tools/Move-ConfigDbToShared.ps1` backs up each instance's own database but leaves it
in place (its rollback is "remove the key"), so after the cutover every instance still has a
pre-cutover `config\exchangeadmin.db`, and that is what a `-Force`d instance opens. With none,
the app creates one, which the same file's comment describes as serving "with no protected
principals and no section access while looking healthy" (scd-1).

**Mechanism: no error at all.** `Set-Content` succeeds, the app starts, and an absent key is
the supported per-instance default, not a fault. Nothing throws, nothing is written to the
error stream, and the run ends on "Deploy complete (INSTALL)."

## Plan (approved by the owner's EVERYTHING go)

**Decision, recorded as an assumption** (taken by the supervisor under the owner's go; the
owner did not rule on this case): the `-Force` path preserves the existing instance's
`ConfigStore:Path` exactly as `Get-ConfigStorePathSetting` reads it (trimmed, as the app trims
it). When the key is set it is carried into the regenerated file unchanged and the run prints
it; when it is absent nothing is added. No new parameter. Every other key is still regenerated
from parameters, which is what `-Force` is for; the `.pre-force.bak` copy keeps the old file.

1. Read the value once, at the top of the fresh-install branch: after the publish, before the
   branch reconfigures IIS, stops the pool or mirrors. Same reader as the upgrade path and the
   backup module (`Get-ConfigStorePathSetting -PublishPath $PublishPath`). A fresh install has
   no `appsettings.json`, so the value is `$null` there and the file is generated as before.
2. A file that does not parse stops the run at that read. It used to be overwritten blind;
   overwriting it now would risk exactly this loss. No wrapper is needed: `ConvertFrom-Json`
   inside the module raises a terminating error, and the script's `Stop` makes it fatal even
   though the module does not see that preference. Reproduced, not reasoned, under Windows
   PowerShell 5.1.20348 and PowerShell 7.6.6 in deploy's own shape (script-scope `Stop`, the
   call inside a `try` that has only a `finally`, global preference `Continue`): the `finally`
   ran, the next statement did not, the caller received the `ArgumentException`. Nothing is
   stopped or mirrored by then, and the outer `finally` (cdt-6) removes staging. The upgrade
   path makes the identical unwrapped call.
3. Move the write into a small function, `Write-AppSettingsFile -Settings -Path
   -ConfigStorePath`, that adds `ConfigStore = [ordered]@{ Path = ... }` when the value is
   non-empty and then serializes exactly as before (`ConvertTo-Json -Depth 10 | Set-Content
   -Encoding UTF8`). The branch calls it with the value from step 1. Being a function is what
   lets a test run the real write against a temporary file.
4. Print the kept value when it is read and again on the "appsettings.json generated" line.
5. Not in this finding: refusing, before the stop, when the key names a missing file. cdt-9
   needs that check for its backup and lands it. Until then a key naming a missing file is
   carried as-is and the app refuses to start on it, which is loud rather than silent.
6. While in that region: the fresh-install inner `finally` comment says staging holds "the
   generated appsettings.json". It holds the published copy of the checkout's own
   `appsettings.json`; the generated file is written to the publish folder, never to staging.
   Corrected in this commit.

Constraints: `deploy.ps1` has no plan mode (`tools/deploy-pipeline.ps1 -PlanOnly` skips it), so
Invariant 4 adds no plan line; nothing new prompts, so `-NonInteractive` is unaffected. Pure
ASCII, no BOM, LF in the index, no ternary, no `??`, no PS7-only parameters; parsed under
Windows PowerShell 5.1. No version bump: ops-script-only (precedent `aud-a34`, `aud-a35`,
`cdt-5`, `cdt-6`).

Files: `deploy.ps1`, `tests/ps/DeployInvariants.Tests.ps1`, this record.

Guards: an AST test pinning the read (once, unconditional within the branch, before the stop
and the mirror) and the only write in the branch (`Write-AppSettingsFile -Path $configPath
-ConfigStorePath $configStorePathSetting`, after the read); and a behavioural test that lifts
`Write-AppSettingsFile` from the script and runs it against a temporary `appsettings.json`,
round-tripping through `Get-ConfigStorePathSetting`. Mutations: drop the carry-over inside the
function; drop the argument at the call site; drop the read; restore the `0e18583` shape.

## Approach

As planned. In `deploy.ps1` after the fix:

- Lines 135-152: `Write-AppSettingsFile` (comment, then the function). It adds
  `$Settings['ConfigStore'] = [ordered]@{ Path = $ConfigStorePath }` when the value is
  non-empty, then `ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $Path -Encoding UTF8`.
  The only change to the serialization is `-LiteralPath` for the positional `-Path`.
- Lines 701-709, first statements of the fresh-install branch: `$configStorePathSetting =
  Get-ConfigStorePathSetting -PublishPath $PublishPath`, printed with `Write-Success` when set.
- Lines 851-856: the write goes through `Write-AppSettingsFile ... -ConfigStorePath
  $configStorePathSetting`; the success line names the kept value.
- Lines 865-866: the corrected staging comment (plan step 6).

Order in the branch after the fix: read and print (706-709), IIS web application (711-749), job
check, pool binding check, `Stop-AppPoolVerified` (770), robocopy (788), the write (851). The
upgrade branch is untouched; the existing "does not rewrite appsettings.json during upgrade
reconciliation" test gains one line so the moved writer cannot be called from it either.

Shape probe under Windows PowerShell 5.1.20348 (the deploy's runtime; Pester runs under 7): the
function lifted from `deploy.ps1` by the 5.1 parser (0 parse errors) wrote a file whose
`ConfigStore:Path` read back as `D:\shared\config\exchangeadmin.db` (top-level keys `Serilog,
AllowedHosts, ConfigStore`), and without the value wrote only `AllowedHosts`.

## Guard proof

| Test (`tests/ps/DeployInvariants.Tests.ps1`) | Pins |
| --- | --- |
| `reads ConfigStore:Path before -Force changes anything and writes it into the regenerated appsettings.json (cdt-8)` | Parses the AST of the fresh-install branch (the else clause of the `if ($isUpgrade)` statement): exactly one assignment to `$configStorePathSetting`, exactly `Get-ConfigStorePathSetting -PublishPath $PublishPath`, under no if/loop/switch/catch/trap/script block/function inside the branch, ending before the first `Stop-AppPoolVerified`/`robocopy`; exactly one writer of a file in the branch (`Set-Content`, `Out-File`, `Add-Content` or `Write-AppSettingsFile`), and it is `Write-AppSettingsFile` with `-Path $configPath` and `-ConfigStorePath $configStorePathSetting`, after the read |
| `Write-AppSettingsFile carries ConfigStore:Path exactly as read, and adds none without it (cdt-8)` | Lifts the function from the script and runs it against a temporary `appsettings.json`: with the key set, `Get-ConfigStorePathSetting` reads the same value back and the file's top-level keys are exactly `AllowedHosts,ConfigStore` (nothing else of the old file is carried); without it, exactly `AllowedHosts` |
| `does not rewrite appsettings.json during upgrade reconciliation` (existing, one line added) | The upgrade block never calls `Write-AppSettingsFile` |

Mutations on `deploy.ps1` (fixed md5 `4D6A20B7AF1D6454AF37C6596FC4B710`, pre-comment; md5s are of
the content as committed, LF, the convention of the cdt-5/cdt-6 records), each run against the
whole `DeployInvariants.Tests.ps1`, then the original bytes restored, touched, md5 re-confirmed;
the restored file re-run green (91 passed). Post-comment md5 `A822D66C1E9234382D36685CECA1095F`.

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| M1 carry-over dropped inside `Write-AppSettingsFile` (the `if ($ConfigStorePath)` block removed) | `5ABC9AB7B5D86F968CEBD9CEAF1F0F50` | the behavioural test ("Expected exactly 'D:\shared\config\exchangeadmin.db', but got $null") |
| M2 `-ConfigStorePath $configStorePathSetting` dropped at the call site | `97FFCDF436B74BEB59999B55E152B168` | the AST test ("Expected exactly '$configStorePathSetting' ... but got $null") |
| M3 the read dropped | `4B1C2DB1E35215F8C8E8BBA95A8402AE` | the AST test ("Expected 1 ... but got 0") |
| M4 the read moved after the mirror, just before the write | `89F1FD5FCCA109DC4BE252A839465214` | the AST test ("Expected the actual value to be less than 41688 ... but got 44986") |
| M5 the read made conditional, `if (-not $Force) { ... }` | `67B0D73FC7E3C160BFBF266A21641B00` | the AST test ("Expected $null or empty, because every run down this branch must read it, but got if (-not $Force) ...") |
| M6 wrong nesting, `$Settings['ConfigStore'] = $ConfigStorePath` | `E6CDB36EF838A605B2737695FA9D1C8C` | the behavioural test ("... but got $null") |
| M7 a second, key-less `ConvertTo-Json | Set-Content $configPath` after the good write | `6272E0BC256DCB2A49A7B75C24CF0ED2` | the AST test ("Expected 1, because appsettings.json is written in exactly one place on this path, but got 2") |
| M8 pre-fix shape (`0e18583`) | `0B562E9220418988C35C42A3E085E606` | both cdt-8 tests ("Expected 1 ... but got 0"; "Expected a value, but got $null or empty") |

A first M5, `if ($isUpgrade) { ... }`, was discarded: it failed, but for the wrong reason (it
added a second `if ($isUpgrade)` statement, which tripped the helper's uniqueness check before
the conditional-read assertion ran). The `if (-not $Force)` form above fails on the assertion
it targets.
