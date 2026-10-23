# cdt-17: the shared backup module accepts a ConfigStore:Path the app refuses, and backs up whatever it resolves to

**Severity**: MEDIUM. `tools/SqliteConfigBackup.psm1` returns any non-blank `ConfigStore:Path` from
an instance's `appsettings.json` - relative (`config\exchangeadmin.db`), drive-relative
(`D:exchangeadmin.db`), rooted without a drive (`\config\exchangeadmin.db`) or UNC - and every
caller then tests for, backs up and checks the file that value names, resolved against the
calling process's current directory. The app's own `ConfigStorePath.Resolve` refuses every one of
those forms at startup. So `deploy.ps1` (both paths), the promotion script, the cutover script and
the installer can take a VERIFIED backup of an unrelated database, report success, mirror, and
restart an app that then refuses to start: a misleading rollback snapshot and an outage. From the
codex review of the cdt-8..10 batch (its finding 2).
**Status**: Closed - merged to master; codex extension cdt-30 closed (and its cdt-37). Row closed 2026-10-23 evening.
**Branch**: `cdt-11-14-installer-deploy` (worktree `agent-a1b04b356d91eeed2`)
**Commit**: `dc1ba6e` (merged)
**Reviewer**: implementer (codex review of the cdt-8..10 batch, admitted by the supervisor)

## Evidence

`tools/SqliteConfigBackup.psm1` at `02aea82`:

```
183:     $value = [string]$pathProperty.Value
184:     if ([string]::IsNullOrWhiteSpace($value)) { return $null }
185:     return $value.Trim()
...
197:     $configured = Get-ConfigStorePathSetting -PublishPath $PublishPath
198:     if ($configured) { return $configured }
```

Callers that act on the result: `deploy.ps1:499-500` (upgrade) and `:752`, `:759` (fresh install
and `-Force`), each followed by `Test-IsSqliteConfigDbPresent` and `Backup-SqliteConfigDb` on it;
`tools/promote-dev-to-prod.ps1:326-327`; `tools/Move-ConfigDbToShared.ps1:603-604`; and the
installer's `Resolve-ConfigStorePlan` (cdt-11). None validates the form.

The app (`Services/Storage/ConfigStorePath.cs`): a value starting `\\` or `//` throws
`InvalidOperationException` ("is a network (UNC) path"); a value that is not
`Path.IsPathFullyQualified` throws ("must be an absolute local file path"). The two writers of
the key already enforce a rule of their own: the installer's `Assert-LocalAbsoluteFilePath` and the
cutover's `Assert-LocalAbsolutePath` both refuse `\\` and `//` and require `^[A-Za-z]:\\`.

**Mechanism: no error at all**, reproduced under Windows PowerShell 5.1.20348 (deploy's runtime)
with the built app as the tool: a publish folder whose `appsettings.json` says `"ConfigStore":
{ "Path": "config\\exchangeadmin.db" }`, the process's current directory holding an unrelated
`config\exchangeadmin.db`:

```
Get-ConfigStorePathSetting -> [config\exchangeadmin.db]
Resolve-ConfigDbPath       -> [config\exchangeadmin.db]
Test-IsSqliteConfigDbPresent -> True
Backup-SqliteConfigDb      -> verified backup written: True (exchangeadmin.probe.db)
the file it copied         -> <root>\cwd\config\exchangeadmin.db (not under the publish folder)
```

and the app's rule on the same values (`[IO.Path]::IsPathFullyQualified` under PowerShell 7.6.6,
the check `ConfigStorePath.Resolve` makes):

```
config\exchangeadmin.db                UNC=False IsPathFullyQualified=False
D:exchangeadmin.db                     UNC=False IsPathFullyQualified=False
\config\exchangeadmin.db               UNC=False IsPathFullyQualified=False
\\server\share\exchangeadmin.db        UNC=True  IsPathFullyQualified=True
D:\shared\config\exchangeadmin.db      UNC=False IsPathFullyQualified=True
D:/shared/config/exchangeadmin.db      UNC=False IsPathFullyQualified=True
```

A deploy run from a source checkout is exactly this shape: running the app locally creates
`config\exchangeadmin.db` in the content root.

## Plan (approved by the owner's EVERYTHING go)

1. A private module function, `Assert-ConfigStorePathValue`, with the writers' rule and the
   installer's wording: a value starting `\\` or `//` is refused as "a local path, not a network
   share"; a value not matching `^[A-Za-z]:\\` is refused as not "an absolute local file path
   (drive letter, colon, backslash - for example ...)". The message names the value and the
   `appsettings.json` it came from, says the app refuses it at startup, and that nothing was
   changed. It throws (the module's error model).
2. `Get-ConfigStorePathSetting` calls it on the trimmed value before returning. `Resolve-ConfigDbPath`
   goes through `Get-ConfigStorePathSetting`, so every caller - both deploy paths, promotion, the
   cutover and the installer - is refused at the read, before any presence or backup check, and
   none of them changes.
3. The module help says the value is validated.

Assumptions, recorded:

- The rule is the writers' (`X:\` only), as briefed, so the scripts and the tools that write the
  key agree. It is stricter than the app in one form: the app also accepts `X:/...`, which no tool
  writes; a hand-written forward-slash key is now refused by the scripts with a message naming it.
  Fail-closed, and recorded rather than widened.
- An invalid existing key now also stops the installer before anything changes, even when
  `-RegenerateAppSettings -ConfigStorePath` would replace it (the installer reads the existing key
  first, cdt-11): the recovery is to fix or remove the key by hand, which the message points at.
- Only the form is checked here; whether the file exists stays each caller's decision (key set and
  file missing is already refused by every caller).

Constraints: the module is imported by `deploy.ps1` under Windows PowerShell 5.1: pure ASCII, no
BOM, no ternary, no `??`, `Set-StrictMode -Version Latest`-safe. No version bump: ops-script-only.

Files: `tools/SqliteConfigBackup.psm1`, `tests/ps/SqliteConfigBackup.Tests.ps1`, this record.

Guards: module tests - relative, drive-relative, rooted-without-drive and both UNC forms throw from
`Get-ConfigStorePathSetting` and `Resolve-ConfigDbPath`, naming the value and the file; an absolute
local path, with surrounding blanks, is accepted trimmed; the reproduced shape (a matching file in
the current directory) throws instead of resolving; and over one list of inputs the module agrees
with the installer's and the cutover's own validators and never accepts a value the app's rule
refuses. Mutations: the call dropped; only the UNC test kept; the drive regex loosened to `^[A-Za-z]:`;
the check moved to `Resolve-ConfigDbPath` only.

## Approach

As planned. In `tools/SqliteConfigBackup.psm1` after the fix:

- Lines 165-166: the `Get-ConfigStorePathSetting` help says a set value is validated.
- Lines 186-190: the trimmed value goes through `Assert-ConfigStorePathValue` before it is
  returned.
- Lines 193-213: `Assert-ConfigStorePathValue` (comment, then the two checks, each a `throw` naming
  the value and the file). Not exported: `Export-ModuleMember` lists functions explicitly.

No caller changed. The same 5.1 probe as in Evidence, against the fixed module, now stops at the
first read: "ConfigStore:Path in <publish>\appsettings.json must be an absolute local file path
(drive letter, colon, backslash - for example ...): config\exchangeadmin.db. The app refuses this
value at startup; fix the key in that file. Nothing was changed." - before any presence check or
backup, and from every caller, because each one's first contact with the key is this reader.

## Guard proof

| Test (`tests/ps/SqliteConfigBackup.Tests.ps1`, Context `cdt-17: ...`) | Pins |
| --- | --- |
| `refuses <Value> (<Form>) from both readers, naming the value and the file` (5 cases: relative, drive-relative, rooted without a drive, UNC, UNC with forward slashes) | `Get-ConfigStorePathSetting` and `Resolve-ConfigDbPath` each throw, the message naming the value, the `appsettings.json` path and "Nothing was changed" |
| `accepts an absolute local path, trimmed as the app trims it` | `'  D:\shared\config\exchangeadmin.db  '` reads back exactly `D:\shared\config\exchangeadmin.db` from both |
| `refuses instead of resolving against the current directory when a matching file is there (the reproduced case)` | With a relative key and `config\exchangeadmin.db` present under the current directory, `Resolve-ConfigDbPath` throws "must be an absolute local file path ... config\exchangeadmin.db" |
| `agrees with the installer and the cutover, and never accepts what the app refuses` | Over eleven inputs (both accepted forms, the refused forms, `\\?\` long paths, `X:/`, `1:\`, `DD:\`): the module's accept/refuse decision equals the installer's `Assert-LocalAbsoluteFilePath` and the cutover's `Assert-LocalAbsolutePath` (both lifted by AST and run), and every value it accepts is one the app's rule accepts (not UNC, `[IO.Path]::IsPathFullyQualified`) |
| existing `Resolve-ConfigDbPath (SharedConfigDb-Plan AC5)` tests | Still green: an absolute key, an absent key, a blank key and no file behave as before |

Mutations on `tools/SqliteConfigBackup.psm1` (fixed md5 `468823603BF9D0FBDB3A0C689426152F`,
pre-comment; md5s of the content as committed, LF), each applied by a script that fails unless
every edit matches exactly once, run against the whole `SqliteConfigBackup.Tests.ps1` (with the
built app), then the original bytes restored, touched, md5 re-confirmed; the restored file re-run
green (33 passed). Post-comment md5 `7E9CE5F00E597665947B03CD048F5414` (33 passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| V1 the call in `Get-ConfigStorePathSetting` dropped | `17EAF906553B0B399348F45D0D64C1EE` | 7: all five refusal cases ("Expected a value, because Get-ConfigStorePathSetting must refuse a value the app refuses at startup"), the reproduced case ("no exception was thrown"), the agreement test ("the module and the installer must make the same call on 'config\exchangeadmin.db'") |
| V2 only the UNC check kept | `7FDBEB0DE2EA299F5B8ADA1CA7072F13` | 5: the relative, drive-relative and rooted-without-drive cases, the reproduced case, the agreement test |
| V3 the drive regex loosened to `^[A-Za-z]:` | `F67C7B31B92C4F10E76B91249460E169` | 2: the drive-relative case, and the agreement test ("... the same call on 'D:exchangeadmin.db'") |
| V4 the check moved to `Resolve-ConfigDbPath` only | `BA8838B6BFD481A7E1A387F51903BB23` | 6: all five refusal cases (from `Get-ConfigStorePathSetting`, which the installer and the deploy decisions read directly), the agreement test |
| V5 pre-fix shape (`02aea82`) | `31598F541E7F0D8863F689FA3BDBA6EE` | 7, as V1 |
