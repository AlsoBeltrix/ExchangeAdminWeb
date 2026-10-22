# cdt-30: the backup module refuses a forward-slash ConfigStore:Path the app accepts, and accepts a drive letter the app refuses

**Severity**: MEDIUM. cdt-17 made `tools/SqliteConfigBackup.psm1` validate the `ConfigStore:Path` it
reads, so no ops script backs up or checks a file the app would never open. It took the rule of
the two scripts that WRITE the key - a drive letter, a colon and a backslash - rather than the app's
own. The app (`Services/Storage/ConfigStorePath.cs`) accepts any fully qualified local path, so a
running instance whose `appsettings.json` says `D:/inetpub/ExchangeAdminWebShared/config/exchangeadmin.db`
works, and every script that reads it - both `deploy.ps1` paths, the promotion, the cutover and the
installer - refuses it ("The app refuses this value at startup"): a valid configuration blocks every
deploy and upgrade until the key is rewritten by hand. The same check is case-insensitive, so it
also passes a drive "letter" the app refuses (the Kelvin sign, U+212A, folds onto `k`) - the
opposite direction, cdt-17's own class. Separately, the module returns the value as spelled, so a
key with forward or mixed slashes reaches each caller's comparisons in a spelling the others do not
use. From the codex review of the cdt-11..cdt-27 merge (ops-batch-3, its finding 3).
**Status**: Merged to master; codex ops-batch-4 extends it as cdt-37 (the installer WRITER validator still refuses forward slashes) - fix in progress
**Branch**: `worktree-agent-a80bd818fc511a34f` (worktree `agent-a80bd818fc511a34f`)
**Commit**: `e3eab89`; test correction `957fe8b` (merged)
**Reviewer**: codex / gpt-5.5-dzs / xhigh, ops-batch-3, 2026-10-21

## Evidence

The app's rule, `Services/Storage/ConfigStorePath.cs` (`ConfigStorePath.Resolve`), quoted:

```
36:         var value = configured.Trim();
38:         if (value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal))
...
40:             throw new InvalidOperationException(
41:                 $"{Key} is a network (UNC) path: '{value}'. ...
...
49:         if (!System.IO.Path.IsPathFullyQualified(value))
...
51:             throw new InvalidOperationException(
52:                 $"{Key} must be an absolute local file path (for example D:\\inetpub\\ExchangeAdminWebShared\\config\\exchangeadmin.db); got '{value}'.");
...
55:         return new Resolution(value, MustExist: true);
```

`Path.IsPathFullyQualified` on Windows accepts `X:\` and `X:/` for an ASCII letter X, any path
starting with two separators, and a separator followed by `?`.

The module at `2478ede`:

```
188:     $value = $value.Trim()
189:     Assert-ConfigStorePathValue -Value $value -AppSettingsPath $appsettings
190:     return $value
...
205:     if ($Value.StartsWith('\\') -or $Value.StartsWith('//')) {
206:         throw "ConfigStore:Path in $AppSettingsPath must be a local path, not a network share: ...
207:     }
208:     if ($Value -notmatch '^[A-Za-z]:\\') {
209:         throw "ConfigStore:Path in $AppSettingsPath must be an absolute local file path (drive letter, colon, backslash - ...
```

**Mechanism: a `throw` from the module's reader** (terminating: every caller runs under `Stop`, and
the first contact with the key is this reader - e.g. `deploy.ps1:537`, before anything is backed up
or mirrored). Reproduced both sides on one list of values: the app's REAL `ConfigStorePath.Resolve`,
loaded from the built `ExchangeAdminWeb.dll` and called by reflection under PowerShell 7.6.6 on
.NET 10.0.12; and the `2478ede` module's `Get-ConfigStorePathSetting` under Windows PowerShell
5.1.20348 (deploy's runtime) on a temporary `appsettings.json` holding each value:

| Value | App (`Resolve`) | Module (`2478ede`) |
| --- | --- | --- |
| `D:/inetpub/ExchangeAdminWebShared/config/exchangeadmin.db` | accepts | **refuses** ("must be an absolute local file path (drive letter, colon, backslash ...)") |
| ` D:/x.db ` (blanks around) | accepts (`D:/x.db`) | **refuses** |
| `D:\inetpub\ExchangeAdminWebShared\config\exchangeadmin.db` | accepts | accepts |
| `D:\shared/config/exchangeadmin.db` | accepts | accepts, returned with the mixed slashes |
| `<U+212A KELVIN SIGN>:\x.db` | refuses (not an ASCII drive letter) | **accepts** (also under PowerShell 7: `-notmatch` is case-insensitive and folds U+212A onto `k`) |
| `\\server\share\exchangeadmin.db`, `//server/share/exchangeadmin.db` | refuses (UNC) | refuses (UNC) |
| `D:exchangeadmin.db`, `config\exchangeadmin.db`, `\config\exchangeadmin.db` | refuses | refuses |
| `\/server/share/x.db`, `/\server\share\x.db` | **accepts** | refuses |
| `\??\D:\x.db`, `\??\UNC\server\share\x.db` | **accepts** | refuses |

The last two rows are the app's gaps, not the module's: Windows reads any two leading separators
as a UNC path and `\??\UNC\` as the NT namespace's form of one, so the app's own UNC refusal (two
backslashes or two forward slashes only) lets a network database through in those spellings. The
app is outside this batch (and a change there is a versioned app change); reported, not fixed here.

A caller that compares the value as a string meets the unnormalised spelling: the cutover's
no-op check is `$devDb -ieq $SharedDbPath` (`tools/Move-ConfigDbToShared.ps1:634`), with
`$SharedDbPath` validated as `X:\...`, so an instance already on the shared database through a
`D:/...` key would not be recognised as such.

## Plan (approved by the owner's EVERYTHING go)

In `tools/SqliteConfigBackup.psm1`, `Assert-ConfigStorePathValue` mirrors the app's rule for every
local path, and stays stricter only where the app's rule lets a network path through:

1. After the trim (as now, the app's `Trim()`): a value starting with ANY two separators (`\\`,
   `//`, `\/`, `/\`) is refused as a network share - the app's two forms plus the two it misses.
2. A value is accepted only if it matches `^[A-Za-z]:[\\/]` case-SENSITIVELY (`-cmatch`): the
   app's drive form exactly - `IsValidDriveChar` is ASCII letters only - with either separator.
   Everything else is refused as not an absolute local file path; that includes the app's other
   fully qualified forms, `\?...`/`/?...` (`\??\D:\...`, `\??\UNC\...`), which can name a network
   share and which no tool writes. The message names the accepted form with either separator.
3. `Get-ConfigStorePathSetting` returns the accepted value with `/` replaced by `\`, so every
   caller's `Test-Path`, backup and comparison sees one spelling of the file the app opens. The
   messages still quote the value as written in the file.
4. Tripwire comments in both places: the module names `ConfigStorePath.cs` as the rule it mirrors
   and lists its two deliberate differences; `ConfigStorePath.cs` names the module as the copy that
   must change with it (comment only, no behaviour change, no version bump).

Assumptions, recorded:

- Normalising only the separators (not dot segments or doubled separators, which `GetFullPath`
  would also fold) is the brief's scope: it makes the module's result one spelling for the forms
  this finding is about, and is identical under 5.1 and 7, where `GetFullPath`'s edge cases differ.
- A key read back with `/` turned into `\` is written back that way by the two scripts that
  regenerate `appsettings.json` from what they read (`deploy.ps1 -Force`, the installer's
  `-RegenerateAppSettings`): the same file, one spelling.
- The writers (`-ConfigStorePath` in the installer, `-SharedDbPath` in the cutover) keep their
  stricter `X:\` rule: what they write the module reads back. Their case-insensitive drive test
  shares the Kelvin-sign gap, but neither can write such a key - no such drive exists, so the
  installer's existence check refuses it and the cutover's copy cannot create it; not changed here.

Constraints: the module is imported by `deploy.ps1` under Windows PowerShell 5.1: pure ASCII, no
BOM, no ternary, no `??`, `Set-StrictMode -Version Latest`-safe. The C# comment keeps the file ASCII
and LF and `dotnet format`-clean. No version bump: ops-script-only behaviour; the C# change is a
comment.

Files: `tools/SqliteConfigBackup.psm1`, `Services/Storage/ConfigStorePath.cs` (comment),
`tests/ps/SqliteConfigBackup.Tests.ps1` (the new context; cdt-17's agreement test, which pinned the
module to the writers' `X:\`-only rule), this record.

Guards: module tests - `D:/...` and `D:\...` (and mixed, and blank-padded) accepted from both
readers and returned with backslashes; `\\server\share`, `//server/share`, `\/server/share`,
`/\server\share` refused as network shares; `D:foo`, `D:`, `foo\bar`, `\foo\bar`, `\??\D:\...`,
`\??\UNC\...` and a Kelvin-sign drive refused as not absolute local paths; a forward-slash key naming
a real database is found, and backed up by the real tool, as its backslash spelling. And cdt-17's
agreement test rewritten against the app's REAL `ConfigStorePath.Resolve` (loaded from the build):
over one list, the module accepts exactly what the app accepts except the listed network and
device forms, which the app accepts today and the module refuses; and everything the two writers'
validators accept, the module reads back. Mutations: forward slashes refused again; the drive test
case-insensitive again; the normalisation dropped; the network check narrowed to the app's two
prefixes; the `2478ede` shape.

## Approach

As planned. In `tools/SqliteConfigBackup.psm1` after the fix:

- Lines 165-168: `Get-ConfigStorePathSetting`'s help says an accepted value comes back with its
  forward slashes turned into backslashes.
- Line 192: `return $value.Replace('/', '\')`, after the validation of the trimmed value as written.
- Lines 196-212: `Assert-ConfigStorePathValue`'s comment, now with the tripwire naming
  `Services/Storage/ConfigStorePath.cs`, what is mirrored and the two deliberate differences.
- Lines 215-217: `if ($Value -match '^[\\/][\\/]')` - any two leading separators - refused as a
  network share ("SQLite's locking is not reliable over a network share"; the old message's "The
  app refuses this value at startup" is not true of `\/` and `/\`).
- Lines 218-220: `if ($Value -cnotmatch '^[A-Za-z]:[\\/]')` refused as not an absolute local file
  path, the message naming the form with either separator and saying "The app refuses a relative
  or drive-relative path at startup, and these scripts read only the drive-letter form".

`Services/Storage/ConfigStorePath.cs` lines 38-44: the tripwire comment naming the module and the
test, and the module's two stricter cases. No other change; `dotnet build ExchangeAdminWeb.slnx -c
Release` (0 errors, the same 61 warnings as before) and `dotnet format ExchangeAdminWeb.slnx
--verify-no-changes --no-restore` (exit 0).

No caller changed. The same 5.1 probe as in Evidence, against the fixed module:

| Value | Module (fixed) |
| --- | --- |
| `D:/inetpub/ExchangeAdminWebShared/config/exchangeadmin.db` | accepts, returns `D:\inetpub\ExchangeAdminWebShared\config\exchangeadmin.db` |
| ` D:/x.db ` | accepts, returns `D:\x.db` |
| `D:\shared/config/exchangeadmin.db` | accepts, returns `D:\shared\config\exchangeadmin.db` |
| `<U+212A KELVIN SIGN>:\x.db` | refuses (drive form) |
| `\\server\share\...`, `//server/share/...`, `\/server/share/x.db`, `/\server\share\x.db` | refuses ("must be a local path, not a network share") |
| `D:exchangeadmin.db`, `config\exchangeadmin.db`, `\config\exchangeadmin.db`, `\??\D:\x.db`, `\??\UNC\server\share\x.db` | refuses ("must be an absolute local file path (a drive letter, a colon, then \ or / ...)") |

So the module and the app now agree on every row of the Evidence table except the four the app
lets through (`\/`, `/\`, `\??\D:\`, `\??\UNC\`), which the module refuses on purpose.

## Guard proof

| Test (`tests/ps/SqliteConfigBackup.Tests.ps1`, Context `cdt-17: ...`) | Pins |
| --- | --- |
| `accepts what the app accepts, refuses what it refuses, and reads back whatever the installer and the cutover write (cdt-17, cdt-30)` (rewritten) | Loads the built `ExchangeAdminWeb.dll` and calls the app's real `ConfigStorePath.Resolve` by reflection. Over nineteen values (drive forms with each separator, mixed, blank-padded; relative, drive-relative, a drive alone, rooted without a drive with each separator; `\\`, `//`, `\\?\`, `\\.\`; `1:\`, `DD:\`, `:\`, a bare name) the module's accept/refuse equals the app's; the Kelvin-sign drive is refused by both; `\/`, `/\`, `\??\D:\` and `\??\UNC\` are accepted by the app (asserted, so a fixed app makes the list stale loudly) and refused by the module; every value the installer's `Assert-LocalAbsoluteFilePath` or the cutover's `Assert-LocalAbsolutePath` accepts, the module accepts. It used to require the module to equal the two writers, which pinned the `X:\`-only rule this finding replaces |
| Context `cdt-30: ...`, `accepts <Value> (<Form>) from both readers and returns it with backslashes` (4 cases) | `D:/inetpub/...`, `D:\inetpub/...`, `D:\shared/config/...` and `  d:/x/exchangeadmin.db  ` each read back from `Get-ConfigStorePathSetting` and `Resolve-ConfigDbPath` as exactly the backslash spelling |
| `refuses <Form> from both readers, saying why and naming the value and the file` (11 cases) | `\\server\share`, `//server/share/x.db`, `\/server/share/x.db`, `/\server\share\x.db` say "not a network share"; `D:foo`, `D:`, `foo\bar`, `/foo/bar`, `\??\D:\x.db`, `\??\UNC\server\share\x.db` and the Kelvin-sign drive say "must be an absolute local file path"; each message contains the value as written, the `appsettings.json` path and "Nothing was changed" |
| `finds, and backs up with the real tool, the database a forward-slash key names` | A real database made by the built app's tool, named in `appsettings.json` with forward slashes: `Resolve-ConfigDbPath` returns exactly its backslash path, `Test-IsSqliteConfigDbPresent` sees it, `Backup-SqliteConfigDb` (the real `db backup`) writes a backup that passes `Test-SqliteConfigDbIntegrity` |
| cdt-17's five refusal cases, the trimmed absolute path and the reproduced current-directory case (unchanged) | Still green: relative, drive-relative, rooted without a drive and both UNC forms refused, naming the value and the file; `'  D:\shared\config\exchangeadmin.db  '` reads back trimmed; the relative key is refused instead of resolving against the current directory |

Mutations on `tools/SqliteConfigBackup.psm1` (fixed md5 `E83167818245F8BB07D01013439AF3A2`,
pre-comment; md5s of the content as committed, LF), each applied by a script that fails unless
every edit matches exactly once, run against `SqliteConfigBackup.Tests.ps1`,
`InstallerConfigBackup.Tests.ps1` and `DeployInvariants.Tests.ps1` (with the built app), then the
original bytes restored, touched, md5 re-confirmed, and the three rerun green (201 passed) each
time. Post-comment md5 `E98BAC57BB2742280AAF2DCB4DA380BE` (201 passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| V1 forward slashes refused again (`'^[A-Za-z]:\\'`) | `033B703DF3910C9C6E1129967313DBE2` | 4: the agreement test ("Expected $true, because the module and the app must make the same call on 'D:/shared/x.db', but got $false"), the forward-slash and blank-padded acceptance cases (each refused "must be an absolute local file path ... D:/inetpub/..."), and the real backup of a forward-slash key |
| V2 the drive test case-insensitive again (`-notmatch`) | `9BD7B669BFF488D67EFD6692E95B57E8` | 2: the agreement test ("Expected $false, because the module and the app must make the same call on '<U+212A>:\x.db', but got $true") and the Kelvin-sign refusal case ("Expected a value, because Get-ConfigStorePathSetting must refuse a non-ASCII drive letter ...") |
| V3 the normalisation dropped (`return $value`) | `39305F857A7B0CA1076E1C6F61642111` | 4: the forward-slash, mixed and blank-padded acceptance cases ("Expected: 'D:\inetpub\...' But was: 'D:/inetpub/...'") and the real backup case ("Expected: 'C:\Users\...\shared\exchangeadmin.db' But was: 'C:/Users/.../shared/exchangeadmin.db'") |
| V4 the network check narrowed to the app's two prefixes (`StartsWith('\\') -or StartsWith('//')`) | `E0F2B44703FB8218ADFBE0F28152138F` | 2: the two mixed-separator UNC refusal cases (refused, but as "must be an absolute local file path" - the refusal no longer says it is a network share) |
| V5 pre-fix shape (`2478ede`, byte-identical: the same md5 as that commit's file and cdt-17's post-comment md5) | `7E9CE5F00E597665947B03CD048F5414` | 8: the agreement test, the forward-slash, mixed and blank-padded acceptance cases, both mixed-separator UNC cases, the Kelvin-sign case, the real backup case |

V4 shows the network check is about the message, not the decision: every value it refuses would
also fail the drive test. It stays, because "not a network share" is the reason an operator needs
for those four spellings.

Not run: a deploy, promotion, cutover or installer run against a real forward-slash key (they need
elevation and IIS). Each reaches the key only through this reader (cdt-17 Evidence), which the
tests drive directly.

Found while fixing this, reported, not fixed (outside this batch: the app's C#, a versioned app
change): the app's own UNC refusal in `ConfigStorePath.Resolve` checks only `\\` and `//`, so it
accepts `\/server/share/...`, `/\server\share\...` and `\??\UNC\server\share\...` - network paths to
Windows (reproduced above with the built app's `Resolve`). Assumption, not tried: the app would then
open the database over the network share, which the check exists to prevent. The module refuses all
of them, so the scripts do not act on such a key.

## Correction: the agreement test loaded a .NET 10 assembly into pwsh (found after commit; fixed in its own commit)

The rewritten cdt-17 agreement test above loaded the built `ExchangeAdminWeb.dll` into the Pester
process and called `ConfigStorePath.Resolve` by reflection. That assembly targets .NET 10
(`System.Runtime 10.0.0.0`); the assemblies the suites already load in-process target .NET 8 or
.NET Standard 2.0 (`Microsoft.Data.Sqlite.dll`: `.NETCoreApp,Version=v8.0`; `SQLitePCLRaw.core.dll`:
`.NETStandard,Version=v2.0`), so they load in any pwsh 7.4 or later. Under a pwsh on .NET 8 or 9 the
type load fails, and CI's Pester job runs under whatever pwsh the runner image carries - so the test
could fail CI for a reason unrelated to the rule. It passed here only because this host's pwsh is
7.6.6 on .NET 10.0.12.

The test now pins the rule in the app's source and emulates it line for line: it asserts that
`Services/Storage/ConfigStorePath.cs` still contains `var value = configured.Trim();`, the `\\`/`//`
`StartsWith(..., StringComparison.Ordinal)` refusal, `if (!System.IO.Path.IsPathFullyQualified(value))`
and `return new Resolution(value, MustExist: true);`, with exactly two `throw new
InvalidOperationException(`; and computes the app's decision as trim, refuse an ordinal `\\` or `//`
prefix, then `[System.IO.Path]::IsPathFullyQualified`. Before switching, the emulation was checked
against the real `Resolve` (the built assembly, loaded locally): the same decision on all 24 values
of the test (the nineteen, the four stricter forms and the Kelvin-sign drive). The tripwire comments
in the module and in `ConfigStorePath.cs`, which said the test calls the built app's `Resolve`, now
say it pins the rule's lines.

md5s (LF) after the correction: `tools/SqliteConfigBackup.psm1` `C46340FB626DA6B36C11CE00ABFC6447`,
`Services/Storage/ConfigStorePath.cs` `42032B7F3D5AB9D1D8D5E93BCCDAA12F`,
`tests/ps/SqliteConfigBackup.Tests.ps1` `53BD77BFC019B30D9C1EAAC598E8FA2C`. `dotnet build
ExchangeAdminWeb.slnx -c Release` (0 errors, 61 warnings, as before) and `dotnet format
ExchangeAdminWeb.slnx --verify-no-changes --no-restore` (exit 0) after the comment change.

Mutations rerun against the corrected test, each against `SqliteConfigBackup.Tests.ps1`, restored,
touched, md5 re-confirmed, and rerun green (49 passed):

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| V1 forward slashes refused again | `8A44F0B9518CB54E1E6F75DB848BC4A7` | 4: the agreement test ("Expected $true, because the module and the app must make the same call on 'D:/shared/x.db', but got $false"), the forward-slash and blank-padded acceptance cases, the real backup case |
| V2 the drive test case-insensitive again | `F0642AA8F1F19B55CD467649DCBCAAC3` | 2: the agreement test ("... the same call on '<U+212A>:\x.db', but got $true") and the Kelvin-sign refusal case |
| V5 the module replaced by its `2478ede` file (byte-identical) | `7E9CE5F00E597665947B03CD048F5414` | 8, as before |
| T1 the app's rule changed in `ConfigStorePath.cs` (a third `StartsWith(@"\/", ...)` added to the UNC refusal, as a fix of its gap would) | `E1B8A9C2E5543AC3BCC1D179B3954227` (`ConfigStorePath.cs`) | 1: the agreement test ("Expected $true, because ConfigStorePath.Resolve's rule is what the module mirrors and this test emulates (if (value.StartsWith(@"\\", ...) || value.StartsWith("//", ...))); if it changed, change both with it") |

Also run before the correction, with the first version of the test: `dotnet test
ExchangeAdminWeb.slnx -c Release --no-build` - 4834 passed, 0 failed, 3 skipped (the three
`ADDirectorySearchServiceTests` that need Active Directory), after the first comment-only change to
`ConfigStorePath.cs`.
