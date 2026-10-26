# cdt-57: the installer has cdt-55's shape - it generates appsettings.json with no ConfigStore:Path over an application IIS already serves

**Severity**: MEDIUM - the cdt-55 class in `tools/Install-ExchangeAdminWeb.ps1` (Architectural Invariant 3). The
installer's cdt-21 check refuses a CONFIGURED path whose database is missing, but not a MISSING appsettings.json over an
application IIS already serves: it generates a new file with no `ConfigStore:Path` and the instance opens a fresh
default database, leaving the shared one it was using behind and unbacked-up. cdt-55 closed this in `deploy.ps1` only,
because its record named that script and the installer is standalone (Invariant 1).
**Status**: Verified on branch; awaiting merge and codex review - admitted 2026-10-26 from the cdt-36 agent's fourth hand-back; inside the released scope
**Branch**: `fix/cdt36` (worktree `D:\source\ExchangeAdminWeb-wt\cdt36`), from `584eb2fc`
**Commit**: the `fix(cdt-57)` commit on `fix/cdt36` that carries this record (not merged)
**Reviewer**: implementer (cdt-36 agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `84a31608`; raised by the cdt-55 agent from its own change.

Confirmed by reading the installer at `584eb2fc`, whose `tools/` and `tests/ps/` are the same as at
`84a31608`.

The config-store decision reads only the publish folder's file, and a missing file is simply "no key":

```
570:     $appSettingsExists = Test-Path -LiteralPath (Join-Path $PublishPath 'appsettings.json') -PathType Leaf
571:     $existingPath = Get-ConfigStorePathSetting -PublishPath $PublishPath
572:     # Write-AppSettings' own rule: it writes when there is no file, or when told to regenerate.
573:     $writesAppSettings = (-not $appSettingsExists) -or $RegenerateAppSettings
```

With no file and no `-ConfigStorePath`, `Resolve-ConfigStorePlan` returns `Path = $null`,
`Source = 'None'`, and `DbPath` the per-instance default. `Assert-ConfigStorePlanDatabase` (cdt-21)
then returns at once - `if (-not $Plan.Path) { return }` (636) - because there is no key to check.
The run goes on to `Write-AppSettings` (941) with the plan's null path, so the generated file names
no key and the restarted instance opens `<PublishPath>\config\exchangeadmin.db`, creating it if need
be (`Services/Storage/ConfigStorePath.cs` 37, `MustExist: false`).

Nothing between the two asks whether IIS already serves an application from that folder. If it does,
and its process was started with a `ConfigStore:Path` naming a shared database, that database is
silently left behind, with no verified backup of it taken. This is what cdt-55 closed in the deploy
script.

The installer's own shape makes the rule simpler than cdt-55's. It has no serving-folder concept: the
cdt-15/cdt-48 check immediately above the insertion point refuses any run whose `-PublishPath` is not
the folder IIS serves the application from (825). So for an existing application, `-PublishPath` **is**
the serving folder, and that folder's `appsettings.json` is the configuration it runs on.

## Approach

Written before the code. Line numbers are `tools/Install-ExchangeAdminWeb.ps1` at `584eb2fc`.

**Change.**

1. A new function, `Assert-ExistingApplicationConfigKnown`, defined after
   `Assert-ApplicationTargetUnchanged` (which ends at 688):

   ```
   param([string]$Location, [bool]$Exists, [string]$PublishPath, [string]$SuppliedConfigStorePath)
   ```

   It returns when the application does not exist, when the folder has an `appsettings.json`, or when
   a `-ConfigStorePath` was supplied; otherwise it calls `Write-Fail`.

2. Called once at script level, straight after the cdt-15 check (825), before anything is written -
   the first change is the pool step at 873:

   ```
   Assert-ExistingApplicationConfigKnown -Location "$ParentSite/$AppAlias" -Exists ($null -ne $existingApplication) -PublishPath $PublishPath -SuppliedConfigStorePath $ConfigStorePath
   ```

3. **`-ConfigStorePath` is the way out, and that is why it is an exemption rather than a hole.** The
   deploy script has no such parameter, so its cdt-55 message can only say "write an appsettings.json
   naming the key". This installer has one, and supplying it states which database the instance opens:
   the generated file carries it (`Source = 'Supplied'`), and cdt-21 then refuses if that database is
   not there. So the operator's intent is explicit and verified, which is what the rule asks for.
   `[string]::IsNullOrWhiteSpace` decides "supplied", so whitespace is not a path.

4. The refusal names the application and the missing file, says that nothing on disk now says which
   database the running process opened, and gives three ways out: re-run with `-ConfigStorePath`
   naming the database this instance opens, restore its `appsettings.json`, or remove the application
   from IIS first if it has no configuration to keep. It ends "Nothing was changed.", as this
   script's refusals do.

5. It refuses in plan mode as in apply, like cdt-21 ("in plan and apply mode alike"): a plan must show
   what a real run does. When the plan cannot read IIS at all, `$existingApplication` is `$null` and
   the check does not fire - the existing cdt-15 warning at 820 already says the plan could not check.

**What does not change:** the cdt-21 check stays exactly as it is, and so do cdt-20, cdt-29 and the
decision in `Resolve-ConfigStorePlan`. This check is additional and reads nothing they read.

Constraints:

- **Invariant 1, standalone.** Nothing is imported from, or named after, the deploy script. The
  comments name cdt-55 by finding id only - `DeployInvariants.Tests.ps1`'s `is standalone: never
  references deploy.ps1` greps this file's text, and it must keep passing.
- Invariant 6: pure ASCII.
- Invariant 4: the check is read-only, so there is no action to gate on `-PlanOnly`.
- No version bump.

Guards, in `tests/ps/InstallerConfigBackup.Tests.ps1`, a new Describe `cdt-57: ...` modelled on the
cdt-21 one:

- The real function, lifted by AST with `Write-Fail`, against real temporary folders:
  - an existing application whose folder has no `appsettings.json` and no supplied path: refused,
    with the whole message;
  - the same with `-ConfigStorePath` supplied: not refused;
  - the same with whitespace supplied: refused, because whitespace is not a path;
  - an existing application whose folder has its `appsettings.json`: not refused, with or without a
    supplied path;
  - no existing application (a fresh install), folder empty: not refused.
- AST: the call is at script level exactly once, immediately after the `Assert-ApplicationTargetUnchanged`
  statement, with those four arguments, and before the first `Invoke-PlanOrAction` or
  `Set-DirectoryAcl`.
- End to end under Windows PowerShell 5.1, as the cdt-21 suite does: a `-PlanOnly` run over a publish
  folder with no `appsettings.json` completes with exit 0 and "Plan only. No changes were made.", and
  the folder is byte-identical afterwards. On a machine without the IIS provider - this one - that run
  exercises the path where the application cannot be read, and proves the new check raises no false
  refusal there. Probed before writing this: the plan completes, warning "This plan cannot read IIS
  application Default Web Site/<alias>".

Mutations:

- M1: the refusal removed (the call site deleted) - the record's mutation.
- M2: the `$Exists` guard removed, so a fresh install is refused too.
- M3: the supplied-path exemption removed, so `-ConfigStorePath` no longer answers the refusal.
- M4: the exemption widened to `$null -ne $SuppliedConfigStorePath`, so whitespace answers it.

Files: `tools/Install-ExchangeAdminWeb.ps1`, `tests/ps/InstallerConfigBackup.Tests.ps1`, this record.

**After the fix**, as built: `tools/Install-ExchangeAdminWeb.ps1` 690-719 (the function) and 857-860
(the comment and the call); `tests/ps/InstallerConfigBackup.Tests.ps1` 1141-1238 (the Describe).

## Files changed

Each file before the repair, at `584eb2fc` (its blob, then the md5 of its committed LF content), and the
md5 after:

- `tools/Install-ExchangeAdminWeb.ps1`: blob `2493edcaac79`, md5 `362DCC172A426437717106C5E32245D7`; after `BE20D0C00A8DE6527FBEAD59431813B7`
- `tests/ps/InstallerConfigBackup.Tests.ps1`: blob `4c0347b94cd1`, md5 `1731A6C8F1A4630D4D858ED491042438`; after `F9D07329BDFAAA4D6A98165C110F48D0`
- this record: copied from master's (`f940a87c`) and filled in

No version bump.

## Guard proof

Tests, in `tests/ps/InstallerConfigBackup.Tests.ps1`, Describe `cdt-57: an application IIS already
serves is not deployed over while its folder has no appsettings.json` (1141):

| Test | Pins |
| --- | --- |
| `refuses only an existing application whose folder has lost the file, and takes -ConfigStorePath as the answer (real files)` | The installer's real function, lifted by AST with `Write-Fail`, against real temporary folders. Refused, with the whole message from "IIS application Site/App already exists, but there is no appsettings.json at '<folder>\appsettings.json'" through "Nothing was changed."; refused again for a whitespace `-ConfigStorePath`. Not refused: a supplied path, a folder that has its `appsettings.json` (with or without a supplied path), and a fresh install. The refused folder deliberately still holds a `config\exchangeadmin.db`, so the test also pins that a per-instance database present does not excuse the run |
| `is asked once, right after the move check, before anything is written (AST)` | The call is the statement immediately after `Assert-ApplicationTargetUnchanged`, with those four arguments, exactly once in the script, and before the first `Invoke-PlanOrAction` or `Set-DirectoryAcl` |
| `raises no false refusal on a -PlanOnly run with no appsettings.json and no application to read (Windows PowerShell 5.1)` | The real installer, run end to end with `-PlanOnly` over a temporary publish folder with no `appsettings.json`: exit 0, "Plan only. No changes were made.", no refusal text, and the folder byte-identical afterwards |

Each mutation was applied to the fixed installer (md5 `BE20D0C00A8DE6527FBEAD59431813B7`) by a script
that fails unless its edit matches exactly once and the mutant passes a sanity check of its own
shape. Each was run against the four suites that read the installer: `InstallerConfigBackup`,
`DeployInvariants`, `AppPoolControl` and `SqliteConfigBackup`. After each one the original bytes were
restored, the mtime refreshed and the md5 re-confirmed, and all four suites were re-run green (352
passed, 0 failed, 0 skipped):

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| M1: the refusal removed - the call site deleted (the record's mutation) | `7182637B913B1EB6A35FADB680E0A826` | 1: the AST test |
| M2: the `$Exists` guard removed, so a fresh install is refused too | `A182CED329D6B9C4554D9DC1523ED26A` | 3: the behavioural test (the fresh-install case), the new plan run, and the existing cdt-34 plan run from another location |
| M3: the supplied-path exemption removed | `9738D96ED5071347F73D49EF36EF95E3` | 1: the behavioural test (a supplied path no longer answers it) |
| M4: the exemption widened to any non-null path, so whitespace answers it | `2F419E2EA8D8E4916BE24C16D4C9D201` | 1: the behavioural test (whitespace no longer refused) |

The first run of this proof threw on M2's own sanity check instead of reporting: the predicate looked
for the bare line `if (-not $Exists) { return }`, which the older cdt-15 function also contains, so
"the guard is gone" could never be true. The predicate now names the line below it as well, and the
runner records an unbuildable mutant and carries on instead of discarding the whole log. The table
above is from the corrected run.

Windows PowerShell 5.1.20348.5756: the installer parses with no errors, and the end-to-end plan test
above is itself a 5.1 run of the shipped script.

Gates on the final tree:

- `Invoke-Pester tests/ps` (Pester 6.2.0, pwsh 7): 508 passed, 0 failed, 0 skipped.
- `Invoke-ScriptAnalyzer -Path . -Recurse` (1.25.0): 0 errors and 321 warnings; the per-file findings
  are identical to those before this change.
- `git diff --check`: clean.
- `tools/Test-AsciiOnly.ps1`: passed.
- The installer names no deploy script: `DeployInvariants.Tests.ps1`'s `is standalone: never
  references deploy.ps1` passes, and the file contains no such string.
- Both files stay CRLF.

Not run: a real install against IIS (it needs elevation and an IIS host). The positive case - an
application IIS actually serves - is covered at the function level, not end to end, because this
machine has no IIS provider and the brief forbids touching one.

## Coder dispute

None.

## Known gaps

- The refusal fires on the folder's `appsettings.json` alone. An instance whose file is present but
  names no `ConfigStore:Path`, and which was in fact started on a shared database before someone
  removed the key, is not caught - nothing on disk distinguishes it from an instance that always
  used its own database. The same limit applies to the deploy script's cdt-55 check.
- The installer reads no bulk jobs at all, its own included: it has no `JobStateWarning` import,
  where the deploy, promotion and cutover scripts all have one. So its stop interrupts durable jobs
  with no warning. Out of this finding's scope; a candidate.

## Reviewer comments

None yet.

## Closeout

Pending: commit on `fix/cdt36`, codex review, merge into `master`, branch deletion.
