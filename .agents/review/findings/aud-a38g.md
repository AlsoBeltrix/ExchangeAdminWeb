# aud-a38g: the cutover stops the pool it is named, not the pool that serves the instance

**Severity**: MEDIUM. The `aud-a38c` class, in the cutover. It needs a wrong but existing
pool name, so an operator error starts it; from there the verified stop (`aud-a38e`)
truthfully confirms the WRONG pool stopped while dev keeps running, and the run's own restore
commands then overwrite dev's live database with the stale snapshot.
**Status**: Fixed on branch; awaiting merge
**Branch**: -- (delivered as a patch against `master` `1c6038d` from the worktree
`agent-af538bba9cd55306a`, whose own branch predates `aud-a38c`; the coordinator applies and
commits it)
**Commit**: `<pending>`
**Reviewer**: implementer (found while fixing `aud-a38e`)

## Evidence

`tools/Move-ConfigDbToShared.ps1` at `1c6038d`. The two pool names are free parameters:

```powershell
67:     [string]$DevAppPoolName = "ExchangeAdminWebDev",
68:     [string]$ProdAppPoolName = "ExchangeAdminWeb",
```

and every IIS action is keyed on the NAME. Nothing in the file asks IIS which pool serves
`-DevPublishPath` or `-ProdPublishPath` - no `Assert-AppPoolServesPath`, no `Get-Website`, no
`Get-WebApplication`:

```powershell
702:     $poolIdentities = @(
703:         (Get-AppPoolIdentity -Name $DevAppPoolName),
704:         (Get-AppPoolIdentity -Name $ProdAppPoolName)
705:     ) | Select-Object -Unique
...
717:     Stop-AppPoolChecked -Name $DevAppPoolName -ConfigDir $devConfigDir -AppPath $DevPublishPath
718:     Stop-AppPoolChecked -Name $ProdAppPoolName -ConfigDir $prodConfigDir -AppPath $DevPublishPath
...
747:     Invoke-PlanOrAction "Grant $identityLabel (OI)(CI)M on $sharedDir" {
...
767:     Start-AppPoolChecked -Name $DevAppPoolName
768:     Start-AppPoolChecked -Name $ProdAppPoolName
```

Step 1b (702-705; the plan-mode copy at 682-685) resolves the named pools' identities, step 2
stops the named pools through `Stop-AppPoolVerified` (`aud-a38e`), step 5 grants the named
pools' identities Modify, step 7 starts the named pools (`aud-a38h`).

The promotion had the same gap and `aud-a38c` closed it with `Assert-AppPoolServesPath` in
`tools/AppPoolControl.psm1` (274-351), which refuses unless IIS serves the folder from the
named pool and from no other, and fails closed when IIS cannot be read or nothing serves the
folder. `tools/promote-dev-to-prod.ps1` calls it before its stop:

```powershell
227:         Write-Plan "Assert-AppPoolServesPath -Name $Name -PhysicalPath $PublishPath (refuse unless IIS serves that folder from this pool and no other)"
240:     $null = Assert-AppPoolServesPath -Name $Name -PhysicalPath $PublishPath
249:     $poolState = Stop-AppPoolVerified -Name $Name
```

`deploy.ps1` and the installer make the same call (`deploy.ps1:616`, `:929`;
`tools/Install-ExchangeAdminWeb.ps1:1090`). The cutover makes none.

**Mechanism: no error at all.** A wrong but existing pool name is a valid IIS object; every
cmdlet the cutover runs against it succeeds.

## Predicted observable failure

`-DevAppPoolName DefaultAppPool` is passed by mistake (or dev's pool has been renamed and the
default no longer matches). Step 1b resolves `IIS AppPool\DefaultAppPool`. Step 2 stops
DefaultAppPool and IIS confirms it Stopped. The real dev application keeps serving under its
own pool while steps 3 and 4 snapshot its database; step 5 grants the wrong identity; step 6
repoints dev's `appsettings.json`, which the running dev application does not reread for
`ConfigStore:Path`; step 7 starts DefaultAppPool. Step 8 then waits 90 s for a "Config store
schema ready" line dev never writes - it never restarted - and fails, printing the restore
commands. The first of those is
`Copy-SqliteDbFile -SourceDbPath '<shared>' -DestDbPath '<dev's database>'`: the snapshot copied
back over dev's live file, erasing every write dev made after it.

Reasoned from the code, not reproduced.

## Plan (approved by the owner's EVERYTHING go)

Owner, 2026-10-21: "fix all the bugs that you know about and any more you find while doing
so" and "... EVERYTHING." (`.agents/decisions.md`, 2026-10-21 "Every open review follow-up
is approved for implementation"). This record's approach was approved under that go when it
was filed; the supervisor dispatched it on a base that has `Assert-AppPoolServesPath`. Written
before the code.

1. **One call into the module, no copy of its rule.** A new function in the cutover,
   `Assert-PoolServesInstance -Label -Name -PublishPath -PoolParameter`, makes exactly the
   promotion's call - `$null = Assert-AppPoolServesPath -Name $Name -PhysicalPath $PublishPath`,
   the same two parameters - so this branch and `aud-a38f` (which is making that function
   tree-aware) merge cleanly. `tools/AppPoolControl.psm1` is not edited. Any throw from it - a
   different serving pool, nothing serving the folder, IIS unreadable - becomes `Write-Fail`
   naming the parameter, the folder and the pool that was named, carrying the module's message
   (which names the pool that does serve the folder, or lists what IIS reported), and saying
   nothing has been stopped or copied. A pass prints an OK line.
2. **Step 1b, before either stop, dev then prod.** The record's approved placement: beside the
   identity read, which sits there because "a bad lookup costs nothing" before the stop. In
   apply mode the two calls are direct statements of the apply branch, after
   `Import-Module WebAdministration` and before the identity read, inside no `try`: a refusal is
   a terminating throw at script level, ahead of the top-level `try` that holds the stops, so
   nothing has been stopped or copied and the catch that restarts both pools is not reached.
3. **Plan mode asserts read-only, following the script's own precedent.** Step 1b's plan
   branch already reads IIS - the identity read - whenever the session is elevated and
   WebAdministration is installed, and degrades any failure to "-Apply would FAIL here"
   warnings; unelevated, it says it skipped. The serving-pool check joins that read, first, in
   the same `try`; the skip warning names both checks. Two plan lines - one per instance, the
   promotion's dry-run wording - are printed whatever happens next, so every plan shows the
   check.
4. **Help text**: step 1 and the "run the plan elevated" paragraph name the check.
5. **Guards** in `tests/ps/MoveConfigDbToShared.Tests.ps1`, a new `aud-a38g` Describe in the
   `aud-a38e` block's idiom. Behavioural: `Assert-PoolServesInstance` and the `Write-*` helpers
   lifted out of the parsed file unchanged and run over the real module, its
   `-ApplicationsProbe` injected through `$PSDefaultParameterValues` (wrapped in a block that
   returns it), each test asserting its own probe was asked - the pass; THE FINDING (another
   pool serves the folder: refused, naming folder, serving pool, named pool and parameter); the
   fail-closed cases (nothing serves it; IIS unreadable). The real script in plan mode prints
   both plan lines before the first stop line and changes nothing. AST: exactly one
   `Assert-AppPoolServesPath -Name $Name -PhysicalPath $PublishPath`, inside the wrapper's `try`;
   exactly four wrapper calls (plan dev, plan prod, apply dev, apply prod) with their exact
   arguments, every one ending before the first `Stop-AppPoolChecked` starts; the apply pair
   inside no `try`.
6. **No version bump**: ops-script-only (`aud-a34`, `aud-a35`, `aud-a38`, `aud-a38b`, `aud-a38c`,
   `aud-a38e` precedent). Pure ASCII, LF, no PS7-only syntax; parsed under Windows PowerShell
   5.1.

Mutations planned, each at the call site: the apply-branch dev check removed; the same check
moved below the dev stop; the wrapper's module call removed; the wrapper's refusal downgraded
to a warning; the two plan lines removed.

## Approach

As planned. `tools/Move-ConfigDbToShared.ps1` after the fix:

- Line 89: the import's comment names `Assert-AppPoolServesPath` beside `Stop-AppPoolVerified`.
- Lines 210-229: `Assert-PoolServesInstance`. Its one statement in the `try` is
  `$null = Assert-AppPoolServesPath -Name $Name -PhysicalPath $PublishPath`; its `catch` is
  `Write-Fail "-$PoolParameter names app pool '$Name', but IIS does not confirm that this pool
  alone serves the $Label folder $PublishPath. <the module's message> The cutover stops that pool
  and then copies the $Label database, so it must be the pool that serves the instance. Nothing
  has been stopped or copied."`; a pass prints "`<Label>` folder `<path>` is served by app pool
  `<name>` and no other".
- Lines 679-690: the step 1b comment covers both reads.
- Lines 695-697: the two plan lines, first in the plan branch, with their comment.
- Lines 706 and 710-712: the skip warning names both checks; elevated with WebAdministration,
  the two checks run first in the existing `try`, whose warning now reads "Could not confirm the
  IIS app pools: ..." then "-Apply would FAIL here".
- Lines 733-736: the apply pair, after `Import-Module WebAdministration` and
  `Resolve-ConfigDbTool`, before the identity read, inside no `try`. The top-level `try` that
  holds the stops opens at 751; the first stop is 753.
- Help: step 1 and the "run the plan elevated" paragraph name the check.

Steps 5 and 7 stay keyed on the names, and that is now sound: by step 2 each name has been
proved to be the pool IIS serves its instance from, and from no other.

## Files changed

- `tools/Move-ConfigDbToShared.ps1` - the function, step 1b, the import comment, the help.
- `tests/ps/MoveConfigDbToShared.Tests.ps1` - the `aud-a38g` Describe (7 tests).
- This record.

## Guard proof

The check is behaviourally tested over the real module. The cutover runs at the top level, so
it cannot be dot-sourced; the tests lift `Assert-PoolServesInstance` and the `Write-*` helpers
out of the parsed file, unchanged, import `tools/AppPoolControl.psm1`, and inject the module's
`-ApplicationsProbe` through `$PSDefaultParameterValues`, wrapped in a block that returns it;
every behavioural test asserts its own probe was asked.

| Test (`tests/ps/MoveConfigDbToShared.Tests.ps1`, `aud-a38g: ...`) | Pins |
| --- | --- |
| `passes, and says so, when IIS serves the folder from the named pool alone` | the check goes through the module (its probe was asked) and reports the pass |
| `refuses a folder another pool serves, naming the folder, the pool that serves it and the pool that was named` | **the finding**: `DefaultAppPool` named, the folder served by `aud-a38g-dev` - refused; the message names `-DevAppPoolName` and `'DefaultAppPool'`, the folder, "runs under 'aud-a38g-dev'", and ends "Nothing has been stopped or copied." |
| `fails closed when nothing in IIS serves the folder` | the module's empty-match refusal reaches the cutover as a `Write-Fail` |
| `fails closed when IIS cannot be read` | an unreadable IIS is a refusal too, naming `-ProdAppPoolName` and the reason |
| `prints the check for both instances in plan mode, before either stop, and changes nothing` | the real script in plan mode: both plan lines with the real folders, dev before prod, both before the first `PLAN Stop app pool`; no shared file, no backups |
| `asks the module, with the promotion's parameters, from one place` | AST: exactly one `Assert-AppPoolServesPath -Name $Name -PhysicalPath $PublishPath`, inside the wrapper's one `try` |
| `checks both instances before either pool is stopped, and lets an apply-mode refusal end the run` | AST: four wrapper calls - dev, prod, dev, prod - with their exact arguments, each ending before the first `Stop-AppPoolChecked`; the apply pair direct statements of step 1b's `else` (of `if ($PlanOnly)`) inside no `try`; the plan pair inside the plan branch |

Mutations on `tools/Move-ConfigDbToShared.ps1`, fixed md5 `413FE7304672CEC79FE7B05F2AE379A1`
(pre-comment). All md5s here are of the LF form - the content as committed; the working tree
holds CRLF (`.gitattributes`: `*.ps1 text eol=crlf`). Each mutation was applied by exact-text
edits asserted to match once, confirmed by md5, run in a fresh pwsh against
`MoveConfigDbToShared.Tests.ps1` and `DeployInvariants.Tests.ps1` (183 tests), then the fixed
bytes restored, touched and md5-confirmed. Every run also showed the three environmental
`DeployInvariants` failures described under Gates, identical to the unchanged export; the column
below counts only the others.

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| G1: the apply-branch dev check removed | `BFC539BFA786788A5AA05D27934F7A34` | 1: `checks both instances before either pool is stopped ...` (the call list) |
| G2: the apply-branch dev check moved below the dev stop | `3A7A3383EC5DF71FBBE41285B9C5AF23` | 6: `checks both instances before either pool is stopped ...` (the order), and - because the moved call now runs inside the top-level `try` in plan mode too, where nothing can read IIS - `prints all eight steps and changes nothing`, `is plan mode by default ...`, both A39 accepting cases, and the `aud-a38g` plan-mode test, each with the check's refusal |
| G3: the module call removed from the wrapper (it passes without asking IIS) | `D566D76CF14985015A05503E58D1A62C` | 5: all four behavioural tests ("Expected 'applications probe' to be found"; "no exception was thrown") and `asks the module ...` (0 calls) |
| G4: the wrapper downgrades a refusal to a warning | `55D353EEF0667069D527A1F836D2F17A` | 3: the finding test and both fail-closed tests |
| G5: the two plan lines removed | `F9D0791BDCE2D179EEADB98CC4340912` | 1: the plan-mode test ("the plan shows the dev check, but got -1") |

Post-comment md5s: `tools/Move-ConfigDbToShared.ps1` `A1BEB5AB9D2EA044AEDC0F046720891A`,
`tests/ps/MoveConfigDbToShared.Tests.ps1` `BE790CB83BA992D21B30E4E24ECCDE3C`.

## Gates

On an export (`git archive`) of `master` `1c6038d` with this change applied, the app built in
Release; one pwsh 7.6.6 session, Pester 6.2.0 imported before the analyzer:

- `Invoke-ScriptAnalyzer -Path . -Recurse`: 0 errors. The change adds two findings, both in the
  test file: `PSUseShouldProcessForStateChangingFunctions` on the stand-in `New-ServingProbe`
  (the `aud-a38e` `New-PoolProbe` precedent) and `PSAvoidUsingPositionalParameters` on the
  `Join-Path` of its `AppPoolControl.psm1` import (the existing blocks' idiom). The script itself:
  39 findings before, 39 after.
- `Invoke-Pester tests/ps`: 423 passed, 3 failed, 0 skipped, 0 not run (426: 419 before, plus
  the 7 new). **The 3 failures are environmental and identical on the unchanged export**:
  `deploy.ps1.leaves every SQLite database ... (cdt-10)`, `deploy.ps1.writes and reports the
  manifest even when git cannot answer (cdt-14)` and `No sqlite3 on the deploy path ... no tracked
  ops script resolves sqlite3 with Get-Command` each run `git -C <repo root> ls-files` or
  `rev-parse HEAD` and assert exit 0; an archive export is not a repository (exit 128). This
  change touches neither those tests nor what they read. They are expected to pass in a checkout.
- Windows PowerShell 5.1.20348.5756 `Parser::ParseFile` (`powershell.exe -NoProfile -Command`):
  `tools/Move-ConfigDbToShared.ps1`, `tests/ps/MoveConfigDbToShared.Tests.ps1`,
  `tools/AppPoolControl.psm1` - 0 errors each.
- ASCII: `tools/Test-AsciiOnly.ps1` needs git, so its rule was applied by a script to every
  `.cs`, `.ps1` and `.psm1` of the export outside `bin\`/`obj\` (486 files): passed.
- `git diff --check`: not runnable outside a repository. The patch is LF-normalised and its added
  lines were checked against the default rules (blank-at-eol, space-before-tab, blank-at-eof):
  clean.
- No .NET run for its own sake - no C# or Razor changed.

The supervisor re-runs the gates in a checkout after applying the patch.

## Known gaps

- **No cutover was run and no IIS was touched.** The check was driven through the module's
  injected probe; the call-site order is a source guard. Plan mode was exercised unelevated,
  where the IIS reads are skipped with a warning - so the elevated plan-mode attempt, and the
  default probe against a real IIS, have not run here (`aud-a38c`'s known gap, unchanged).
- **The check matches the folder's own IIS nodes** - site roots and application roots at the
  folder. `aud-a38f`, in flight, makes `Assert-AppPoolServesPath` tree-aware (an application
  nested inside the folder under another pool); the cutover calls it with the promotion's
  parameters, so it inherits that when both land.
- **It is read once, before the stops.** A binding moved in the seconds between step 1b and
  step 2 is not seen.
