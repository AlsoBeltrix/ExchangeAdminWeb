# cdt-29: the installer's -RegenerateAppSettings -ConfigStorePath silently moves an instance off the database its appsettings.json names

**Severity**: HIGH. An existing instance whose `appsettings.json` names shared database A, run
through `tools/Install-ExchangeAdminWeb.ps1 -RegenerateAppSettings -ConfigStorePath B` with B an
existing file, is rewritten to open B. Nothing refuses or warns: the banner reports B, the shared
folder grant goes to B's directory, the pre-upgrade backup snapshots B, and after the restart the
instance runs on a different authorization and settings store (section access, protected
principals, module enablement, module admins, admin settings) while its sibling keeps A - the one
shared config database (Architectural Invariant 2) split in two, with no backup of the store the
instance was actually using. cdt-11 decided "a supplied `-ConfigStorePath` wins" when the file is
written, and cdt-20's refusal for a kept file points the operator at exactly this run ("Re-run with
`-RegenerateAppSettings` to write it"). From the codex review of the cdt-11..cdt-27 merge
(ops-batch-3, its finding 2).
**Status**: Merged to master; codex group review pending
**Branch**: `worktree-agent-a80bd818fc511a34f` (worktree `agent-a80bd818fc511a34f`)
**Commit**: `fce5c5e` (merged)
**Reviewer**: codex / gpt-5.5-dzs / xhigh, ops-batch-3, 2026-10-21

## Evidence

`tools/Install-ExchangeAdminWeb.ps1` at `2478ede`, inside `Resolve-ConfigStorePlan`:

```
558:     $existingPath = Get-ConfigStorePathSetting -PublishPath $PublishPath
559:     # Write-AppSettings' own rule: it writes when there is no file, or when told to regenerate.
560:     $writesAppSettings = (-not $appSettingsExists) -or $RegenerateAppSettings
...
566:     if (-not $writesAppSettings -and -not [string]::IsNullOrWhiteSpace($SuppliedPath)) {
...
571:             Write-Fail "-ConfigStorePath $SuppliedPath was given, but this run keeps the existing appsettings.json ... Re-run with -RegenerateAppSettings to write it ...
...
577:     if ($writesAppSettings -and -not [string]::IsNullOrWhiteSpace($SuppliedPath)) {
578:         $path = $SuppliedPath
579:         $source = 'Supplied'
```

The only refusal (566, cdt-20) is for a file the run keeps. When the run writes the file (577), a
supplied path replaces the existing key with no comparison at all; `$existingPath`, read at 558, is
never consulted on that branch. The plan's `Path` is then what `Write-AppSettings` writes (920-926),
`Split-Path -Parent $configStorePlan.Path` is the shared folder granted Modify (737-740, 884-889),
and `$configStorePlan.DbPath` is what the pre-upgrade backup snapshots (1023, 1050).

**Mechanism: no error at all**, reproduced under Windows PowerShell 5.1.20348 (the installer's
runtime). Scratch probe: the installer's script-level `-ConfigStorePath` block,
`Assert-LocalAbsoluteFilePath`, `Resolve-ConfigStorePlan`, `Assert-ConfigStorePlanDatabase`,
`New-AppSettingsObject`, `Write-AppSettings` and the output helpers, lifted from the `2478ede` file by
the 5.1 parser and run as the script runs them against a temporary publish folder whose
`appsettings.json` names `<root>\a\exchangeadmin.db`; `<root>\b\exchangeadmin.db` also exists:

| Run | What happened |
| --- | --- |
| `-RegenerateAppSettings -ConfigStorePath <root>\b\exchangeadmin.db` | plan `Source=Supplied Path=DbPath=<root>\b\exchangeadmin.db`; banner "Config DB : <root>\b\exchangeadmin.db (shared, from -ConfigStorePath)"; shared-folder grant on `<root>\b`; after the write the file names `<root>\b\exchangeadmin.db`; the backup would snapshot `<root>\b\exchangeadmin.db`. No error, no warning |
| the same path, upper-cased, as `-ConfigStorePath` | the same, with the key re-spelled - the same file |
| `-ConfigStorePath <root>\b\...` without `-RegenerateAppSettings` | refused (cdt-20), advising "Re-run with -RegenerateAppSettings to write it" - the first row |
| an `appsettings.json` with no key, `-RegenerateAppSettings -ConfigStorePath <root>\b\...` | plan `Supplied`, the file names B (the normal first configuration) |
| no `appsettings.json` (a fresh install), `-ConfigStorePath <root>\b\...` | plan `Supplied`, the file names B |

## Plan (approved by the owner's EVERYTHING go)

**Decision, recorded as an assumption** (taken under the owner's go, as briefed; the owner did not
rule on this case): when an existing `appsettings.json` carries `ConfigStore:Path`, a supplied
`-ConfigStorePath` that names a different database is REFUSED, before anything is written, in
every mode - with or without `-RegenerateAppSettings`, in plan mode as in apply mode. The message
names both paths and says that `tools\Move-ConfigDbToShared.ps1` is the only sanctioned way to
change the database an existing instance opens. A supplied path that names the same database as
the existing key is fine. No existing key plus a supplied path is the normal first configuration
(a fresh install, or an instance that has never had the key) and stays allowed. This narrows
cdt-11's "a supplied `-ConfigStorePath` wins when the file is written" to the case where there is
no key to replace.

In `Resolve-ConfigStorePlan`:

1. `$suppliedIsExisting`: the supplied path and the existing key, compared as cdt-20 compares them
   (`[System.IO.Path]::GetFullPath`, trailing `\` dropped, ordinal case-insensitive, by equality).
2. The refusal, before the cdt-20 check and the choice of path: `if` a path was supplied, the
   existing file names a key, and they differ, `Write-Fail` with the message above; in plan mode
   the same text is first printed as a `PLAN REFUSED: ...` line.
3. The cdt-20 refusal keeps its condition, narrowed to the case it now alone covers (a kept file
   with no key): a kept file whose key differs is the refusal above, whose advice is not "re-run
   with `-RegenerateAppSettings`" - that advice now leads to the same refusal.
4. Nothing else changes: the decision still runs once, at script level, before the banner and
   every change (cdt-11), so the refusal precedes any write in both modes.

Assumptions, recorded:

- Plan mode prints the refusal as a `PLAN` line and then stops the same way the real run does
  (non-zero exit, nothing created), like the cdt-20 and cdt-21 refusals: a plan must not end "Plan
  only. No changes were made." over a run that would be refused.
- `tools\Move-ConfigDbToShared.ps1` moves both instances together (both stopped, both databases
  backed up, the new one seeded from dev's, both keys rewritten); the message names it as the way,
  without claiming it fits every move an operator might want. Its own guards (it refuses a
  destination that already exists) are its business.

Constraints: Invariant 1 (standalone; the string naming the ADI deploy script does not appear);
Invariant 4 (the refusal is a read-only decision made in plan and apply mode alike, before any
step); Invariant 6 (pure ASCII, no BOM, no ternary, no `??`, no PS7-only parameters; parsed under
5.1). No version bump: ops-script-only.

Files: `tools/Install-ExchangeAdminWeb.ps1`, `tests/ps/InstallerConfigBackup.Tests.ps1` (the new
context; two cdt-11 cases that pinned the old outcome - a differing supplied path written over the
existing key, and a differing supplied path on a kept file refused as cdt-20's "would not take
effect"), this record.

Guards: behavioural cases on the lifted resolver - a differing supplied path refuses with and
without `-RegenerateAppSettings`, naming both paths and the cutover script, and leaves the file
untouched; the existing key restated in another case is accepted and written; no key plus a
supplied path is still taken (regenerated file, fresh install); in plan mode the refusal is
printed as a `PLAN REFUSED:` line before the throw. And a `-PlanOnly` run of the real installer
under Windows PowerShell 5.1 against temporary paths: exit non-zero, the `PLAN REFUSED:` line
naming both paths, no "Plan only. No changes were made.", the temporary tree unchanged.
Mutations: the refusal removed; the refusal limited to kept files; the comparison made
case-sensitive; the plan line dropped; the `2478ede` shape.

## Approach

As planned. In `tools/Install-ExchangeAdminWeb.ps1` after the fix:

- Lines 46-50: the help text's shared-database paragraph says a `-ConfigStorePath` naming a
  different database from an existing key is refused, `-RegenerateAppSettings` or not, and names
  the cutover script.
- Lines 549-554: `Resolve-ConfigStorePlan`'s header says when a supplied path is written.
- Lines 565-566: `$suppliedPathGiven` and `$suppliedIsExisting` (cdt-20's comparison, now made
  once).
- Lines 568-579: a comment and the refusal: `$refusal = "-ConfigStorePath $SuppliedPath was given,
  but the existing appsettings.json in $PublishPath names ConfigStore:Path = $existingPath, a
  different config database. ..."`, `if ($PlanOnly) { Write-Plan "REFUSED: $refusal" }`,
  `Write-Fail $refusal`.
- Line 585: the cdt-20 refusal, its condition narrowed to a kept file with no key (`-not
  $existingPath`) and its message to "which names no ConfigStore:Path" - the only text its
  `$keptNames` could still produce.

The same 5.1 probe as in Evidence, against the fixed file:

| Run | What happens |
| --- | --- |
| `-RegenerateAppSettings -ConfigStorePath <root>\b\...` over a file naming A | refused: "-ConfigStorePath <root>\b\exchangeadmin.db was given, but the existing appsettings.json in <root>\publish names ConfigStore:Path = <root>\a\exchangeadmin.db, a different config database. This installer does not move an existing instance from one config database to another, with or without -RegenerateAppSettings: ... Changing the database an existing instance opens is tools\Move-ConfigDbToShared.ps1's job - the only sanctioned way. Re-run without -ConfigStorePath to keep <root>\a\exchangeadmin.db, or with -ConfigStorePath <root>\a\exchangeadmin.db. Nothing was changed."; the file still names A |
| the same without `-RegenerateAppSettings` | the same refusal (no longer cdt-20's advice to re-run with `-RegenerateAppSettings`) |
| A itself, upper-cased | accepted; the file names the same database |
| no key, or no file, with `-ConfigStorePath <root>\b\...` | the plan takes B, as before |

## Guard proof

| Test (`tests/ps/InstallerConfigBackup.Tests.ps1`, Describe `cdt-11: ...`) | Pins |
| --- | --- |
| Context `cdt-29: ...`, `refuses a different database, with or without -RegenerateAppSettings, naming both and the cutover, before anything is written` | For `-RegenerateAppSettings` true and false, the lifted resolver throws over a file naming the shared key when a different path is supplied, the message carrying "-ConfigStorePath <other> was given", "names ConfigStore:Path = <shared>, a different config database", "with or without -RegenerateAppSettings", `tools\Move-ConfigDbToShared.ps1`, "the only sanctioned way", "with -ConfigStorePath <shared>." and "Nothing was changed"; the file is byte-identical afterwards |
| `accepts the existing key restated, however it is cased, and writes the same database` | The shared key, supplied upper-cased with `-RegenerateAppSettings`: the plan's database and the written key are the same file (case-insensitive) |
| `still takes a supplied path when there is no key to replace (the first configuration)` | A key-less file with `-RegenerateAppSettings`, and a fresh install: plan `Supplied`, the supplied database |
| `in plan mode prints the refusal as a PLAN line, then stops as the real run does` | With `$PlanOnly`, one `PLAN REFUSED: ` line naming both paths, then the throw |
| `a -PlanOnly run naming a different database prints the refusal and creates nothing (Windows PowerShell 5.1)` | The real installer under `powershell.exe` with `-PlanOnly -NonInteractive -RegenerateAppSettings -ConfigStorePath <b>` over a temporary instance naming `<a>` (both files exist) and an unused pool/application name: exit non-zero; the output carries `PLAN REFUSED: -ConfigStorePath <b> was given` and `names ConfigStore:Path = <a>, a different config database` (compared with whitespace removed, as 5.1 wraps errors); no "Plan only. No changes were made."; the temporary tree unchanged (paths, sizes, SHA-256) |
| `lets a supplied -ConfigStorePath win when the file is written` (cdt-11, changed) | Now over a key-less file (was: over a file naming the shared key, the case this finding refuses) |
| `leaves a kept file untouched, and its own key is the database the plan reports` (cdt-11, one expectation changed) | A differing supplied path on a kept file now throws "a different config database" (was cdt-20's "would not take effect") |
| Context `cdt-20: ...` (unchanged) | A kept file with no key still refuses as before; a kept file naming a different key is refused with both paths ("-ConfigStorePath <other> was given", "names ConfigStore:Path = <shared>,") - now by the cdt-29 message |

Mutations on `tools/Install-ExchangeAdminWeb.ps1` (fixed md5 `CF20AC6E6137A31AAE06543CD4D9A1DC`,
pre-comment; md5s of the content as committed, LF), each applied by a script that fails unless
every edit matches exactly once, run against `InstallerConfigBackup.Tests.ps1` and
`DeployInvariants.Tests.ps1`, then the original bytes restored, touched, md5 re-confirmed, and both
rerun green (152 passed) each time. Post-comment md5 `53099B9DD6A47A947596B6141E36B28C` (152
passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| P1 the refusal removed | `1CC9FC6A4DE152C663A167889DE7DAE4` | 5: the cdt-29 refusal case ("Expected a value, because a different database must not replace the existing key (-RegenerateAppSettings: True)"), the plan-mode case, the 5.1 `-PlanOnly` run ("Expected 0 to be different from the actual value" - the plan completed), the cdt-20 different-key case, and the cdt-11 kept-file case ("no exception was thrown") |
| P2 the refusal limited to kept files (`-not $writesAppSettings -and` added) | `C16754CDD9D0E3B7A199A8A7D111B360` | 3: the cdt-29 refusal case (with `-RegenerateAppSettings`), the plan-mode case, the 5.1 `-PlanOnly` run |
| P3 the comparison made case-sensitive (`Ordinal`) | `268D0731CCB37FA044BB13E5EEABC697` | 2: `accepts the existing key restated, however it is cased, ...` and cdt-20's `accepts the key the kept file already names, however it is cased` (both refused "-ConfigStorePath D:\SHARED\CONFIG\EXCHANGEADMIN.DB was given, but ... a different config database") |
| P4 the plan line dropped | `325AD1AFE6A0FAF8188E04BA6D8DD061` | 2: the plan-mode case ("Expected 1, because the plan says the real run refuses here, but got 0") and the 5.1 `-PlanOnly` run (no `PLAN REFUSED:` in the output) |
| P5 pre-fix shape (`2478ede`, byte-identical: the same md5 as that commit's file) | `FC9225F449E2C37C0E8904382A4E35F6` | 4: the cdt-29 refusal case, the plan-mode case, the 5.1 `-PlanOnly` run, and the cdt-11 kept-file case (it got cdt-20's "would not take effect") |

Not covered by a test: the help text's wording.
