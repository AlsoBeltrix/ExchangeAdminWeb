# cdt-35: the app's ConfigStore:Path rule lets a network share through in spellings the ops scripts refuse

**Severity**: MEDIUM. `ConfigStorePath.Resolve` (`Services/Storage/ConfigStorePath.cs`) refuses a
network path only when the value starts `\\` or `//`. Windows reads ANY two leading separators as a
UNC path, so `\/server/share/...` and `/\server\share\...` pass, and so does
`\??\UNC\server\share\...`, the NT namespace's spelling of a share (all three are "fully
qualified"); `\??\D:\...` passes too. Each is accepted at startup and handed to the connection
factory as the shared config database, which the shared-config-DB invariant forbids (decision
2026-09-04; `docs/SharedConfigDb-Plan.md` section 1: "A network path is refused (section 3, AC1)" -
SQLite's WAL locking is not reliable over a share). The ops scripts' copy of the rule
(`tools/SqliteConfigBackup.psm1`, cdt-30) already refuses all four, so for those spellings the app
starts on a database every deploy script refuses to back up. The app also keeps a `D:/...` value
as spelled, while the scripts back up and compare `D:\...`. Found by the cdt-30 implementer while
aligning the module with the app (cdt-30's "Found while fixing this"), admitted by the supervisor.
**Status**: Fixed on branch; awaiting merge
**Branch**: `worktree-agent-ae79adccaaf3eeb47` (worktree `agent-ae79adccaaf3eeb47`)
**Commit**: `<pending>`
**Reviewer**: implementer (cdt-30 agent, verified against the real Resolve)

## Evidence

`Services/Storage/ConfigStorePath.cs` at `0d040ed`:

```
36:         var value = configured.Trim();
...
46:         if (value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal))
47:         {
48:             throw new InvalidOperationException(
49:                 $"{Key} is a network (UNC) path: '{value}'. The shared config database must be a local " +
...
57:         if (!System.IO.Path.IsPathFullyQualified(value))
58:         {
59:             throw new InvalidOperationException(
60:                 $"{Key} must be an absolute local file path (for example D:\\inetpub\\ExchangeAdminWebShared\\config\\exchangeadmin.db); got '{value}'.");
...
63:         return new Resolution(value, MustExist: true);
```

`Program.cs:56-70` passes `configDb.Path` straight to `new SqliteConnectionFactory(configDbPath,
configDb.MustExist)`; nothing else checks it.

**Mechanism: no error at all** - the value is returned as a valid local path. Reproduced with the
app's REAL `Resolve` (the `0d040ed` Release build of `ExchangeAdminWeb.dll`, loaded and called by
reflection under PowerShell 7.6.6 on .NET 10.0.12), beside the module's reader
(`Get-ConfigStorePathSetting`) on a temporary `appsettings.json` holding each value:

| Value | App (`Resolve`) | Module |
| --- | --- | --- |
| `\/server/share/x.db` | **accepts**, returns `\/server/share/x.db` | refuses ("must be a local path, not a network share") |
| `/\server\share\x.db` | **accepts**, returns `/\server\share\x.db` | refuses ("not a network share") |
| `\??\UNC\server\share\x.db` | **accepts**, returns it | refuses ("must be an absolute local file path") |
| `\??\D:\x.db` | **accepts**, returns it | refuses ("must be an absolute local file path") |
| `D:/x/y.db` | accepts, returns **`D:/x/y.db`** | accepts, returns `D:\x\y.db` |
| `\\server\share\x.db`, `//server/share/x.db`, `\\?\D:\x`, `\\?\UNC\server\share\x.db`, `\\.\D:\x.db` | refuses ("is a network (UNC) path") | refuses ("not a network share") |
| `D:foo`, `foo\bar`, `/foo/bar`, `<U+212A KELVIN SIGN>:\x.db` | refuses ("must be an absolute local file path") | refuses (same) |
| `D:\x\y.db` | accepts | accepts |

How Windows reads the first three (`[System.IO.Path]::GetFullPath` and `IsPathFullyQualified` under
.NET 10; the first two give the same `\\server\share\x.db` under Windows PowerShell 5.1's .NET
Framework):

```
\/server/share/x.db          GetFullPath -> \\server\share\x.db   IsPathFullyQualified=True
/\server\share\x.db          GetFullPath -> \\server\share\x.db   IsPathFullyQualified=True
\??\UNC\server\share\x.db    GetFullPath -> \??\UNC\server\share\x.db   IsPathFullyQualified=True
```

`\??\UNC\` is the NT object namespace's name for the network redirector, so that value names
`\\server\share\x.db` too. Assumption, not tried (no share on this host, and nothing here runs
elevated): the factory's `File.Exists` and SQLite's open would then reach the database over SMB,
which the refusal exists to prevent.

## Plan (approved by the owner's EVERYTHING go)

`ConfigStorePath.Resolve` takes the module's rule exactly (`tools/SqliteConfigBackup.psm1`,
`Assert-ConfigStorePathValue`, cdt-30), so the app and every ops script make the same call on every
value:

1. The trim, unchanged.
2. A value whose first two characters are both separators, `\` or `/` in any mix, is refused with
   the existing network message ("is a network (UNC) path ... SQLite's locking is not reliable over
   a network share"): `\\`, `//`, `\/`, `/\`, and with them `\\?\` and `\\.\` device paths.
3. Then a value is accepted only if it is an ASCII letter (`char.IsAsciiLetter`, the module's
   case-sensitive `[A-Za-z]`), a colon, then `\` or `/`. Everything else is refused with the
   existing "must be an absolute local file path" message, whose example now states the form:
   relative, drive-relative (`D:foo`), a drive alone, rooted without a drive, `\??\...` (so
   `\??\UNC\...`), a non-ASCII drive "letter". `Path.IsPathFullyQualified` goes: it is what let
   `\??\` through, and every value that passes the drive test is fully qualified anyway.
4. An accepted value comes back with `/` turned into `\` (the module's `Replace('/', '\')`), so the
   app opens the spelling the scripts back up and compare.
5. Fail-closed as before: every refusal is the same `InvalidOperationException` naming the key and
   the value, which `Program.cs:63-67` logs as fatal and rethrows - the app does not start.
6. Tripwire comments: `ConfigStorePath.cs` names the module as the identical copy and the test in
   each suite that pins the other side; the module's comment drops its "stricter than the app"
   paragraph (there is no difference left) and names the C# test that pins its lines.

Assumptions, recorded:

- A drive letter mapped to a network share (SMB global mapping, `net use` in the pool's session)
  still passes, in the app and in the scripts alike: the rule is textual. Already recorded as a
  known limit of the guard (`.agents/decisions.md`, "2026-09-21 - Queue 6", item 4: "SMB global
  mapping presents the share as a drive letter, so the guard accepts it"); not changed here.
- `\\?\D:\...` and `\\.\...` are local device forms, refused with the network message as the module
  refuses them; no tool writes either. The message is the module's, kept identical on purpose.
- Base-app behaviour change (the app's startup rule): it needs the base app version bump. Not made
  here (the consolidated bump is state item 9); reported for it.

Constraints: `ConfigStorePath.cs` stays ASCII, LF and `dotnet format`-clean; the module change is
a comment only, still pure ASCII and Windows PowerShell 5.1-parseable.

Files: `Services/Storage/ConfigStorePath.cs`, `ExchangeAdminWeb.Tests/ConfigStorePathTests.cs`,
`tools/SqliteConfigBackup.psm1` (comment only), `tests/ps/SqliteConfigBackup.Tests.ps1` (cdt-30's
agreement test pins the app's new lines and emulates the new rule; its list of the four values the
app used to accept joins the plain agreement; two test-case labels that said "the app accepts it"
corrected), this record.

Guards: xUnit - a theory over the four spellings plus `\\server\share`, `//server/share`, `D:foo`,
`foo\bar` and `\\?\D:\x` (and `\\.\`, `\\?\UNC\`, `/foo/bar`, `D:`, the Kelvin-sign drive), each
refused naming the key, the value and the reason; `D:\x\y.db`, `D:/x/y.db`, mixed and blank-padded
each accepted as exactly `D:\x\y.db`; a source tripwire that the module still holds its rule lines
and its comment naming `ConfigStorePath.cs`; and a cross-check theory that runs the module's two
pinned patterns (PowerShell `-match` case-insensitive, `-cnotmatch` case-sensitive, after its trim)
against `Resolve` over one list of values - same decision, same reason, same returned spelling.
Pester - cdt-30's agreement test pins the new C# lines and agrees on every value with no exception
list. Mutations: `\/server/share` accepted again; the normalisation dropped; the ASCII-letter test
widened to any letter; the `0d040ed` file restored; the module's rule changed (the C# suite's
tripwire on it).

## Approach

As planned. `Services/Storage/ConfigStorePath.cs` after the fix:

- Lines 14-20: the class summary states the one accepted form and why every other is refused.
- Line 39: `var value = configured.Trim();` (unchanged).
- Lines 41-49: the tripwire comment, naming the module as the identical copy (by file, not by
  function, so it stays true wherever in the module the rule lives) and the test in each suite
  that pins the other side.
- Lines 50-55: `if (value.Length >= 2 && IsSeparator(value[0]) && IsSeparator(value[1]))` throws
  the existing network message.
- Lines 57-65: `if (!IsDriveLetterPath(value))` throws "must be an absolute local file path (a drive
  letter, a colon, then \ or /, for example D:\...); got '<value>'".
- Line 67: `return new Resolution(value.Replace('/', '\\'), MustExist: true);`
- Lines 70-73: `IsSeparator` (`\` or `/`) and `IsDriveLetterPath` (length 3 or more,
  `char.IsAsciiLetter(value[0])`, `value[1] == ':'`, `IsSeparator(value[2])`).

`tools/SqliteConfigBackup.psm1` lines 196-213: comment only - the "stricter than the app" paragraph
replaced by the rule both copies now share and the test in each suite that pins the other side.
No code line in the module changed.

The same probe as in Evidence, against the fixed build: `\/server/share/x.db` and
`/\server\share\x.db` refused as network paths; `\??\D:\x.db` and `\??\UNC\server\share\x.db`
refused as not absolute local file paths; `D:/x/y.db` and `D:\x\y.db` both returned as `D:\x\y.db`;
every other row as before. The app and the module now make the same call, with the same reason and
the same returned spelling, on every row.

## Guard proof

| Test | Pins |
| --- | --- |
| `ConfigStorePathTests.Resolve_RefusesEveryFormButADriveLetterPath` (14 cases) | `\/server/share`, `/\server\share`, `\\server\share`, `//server/share`, `\\?\D:\x`, `\\?\UNC\...`, `\\.\D:\...` throw "is a network (UNC) path"; `\??\D:\...`, `\??\UNC\...`, `D:foo`, `foo\bar`, `/foo/bar`, `D:` and the Kelvin-sign drive throw "must be an absolute local file path"; each message names `ConfigStore:Path` and the value |
| `Resolve_DriveLetterPath_WithEitherSeparator_ReturnsOneSpelling` (4 cases) | `D:\x\y.db`, `D:/x/y.db`, `D:\x/y.db` and `  D:/x/y.db  ` each resolve to exactly `D:\x\y.db`, must-exist |
| `ModuleRule_IsPinnedInItsSource_AndNamesThisMethod` | `tools/SqliteConfigBackup.psm1` still contains its trim, `$Value -match '^[\\/][\\/]'`, `$Value -cnotmatch '^[A-Za-z]:[\\/]'`, `return $value.Replace('/', '\')` and its tripwire naming `Services/Storage/ConfigStorePath.cs (Resolve)` |
| `Resolve_MakesTheModulesCall_OnEveryValue` (24 cases) | Over the agreement list the Pester suite uses (drive forms with each separator, mixed, blank-padded; relative, drive-relative, a drive alone, rooted without a drive; `\\`, `//`, `\\?\`, `\\.\`; `1:\`, `DD:\`, `:\`, a bare name; the four former gaps; the Kelvin-sign drive), `Resolve` accepts exactly what the module's two pinned patterns accept (applied as PowerShell does: `-match` case-insensitive, `-cnotmatch` case-sensitive, after the trim), refuses with the same reason, and returns the module's spelling |
| `tests/ps/SqliteConfigBackup.Tests.ps1`, `accepts what the app accepts, refuses what it refuses, ... (cdt-17, cdt-30, cdt-35)` (rewritten) | Pins `Resolve`'s new lines in its source (the trim, the two-separator test, the drive test, the normalising return, `IsSeparator`, `IsDriveLetterPath`, exactly two throws), emulates them, and requires the module to make the same call AND return the same spelling on all 24 values; the four the app used to accept are no longer an exception list |
| Existing `ConfigStorePathTests` (blank, absolute, UNC, relative, drive-relative, must-exist, factory) and the cdt-17/cdt-30 Pester contexts | Unchanged and green; two cdt-30 case labels that said "the app accepts it" now say it did until cdt-35 |

Mutations, each applied by a script that fails unless every edit matches exactly once; md5s are of
the LF content (as committed). Fixed `ConfigStorePath.cs` md5 `53E7AEE8D3E3F7D8B0436A49E888110F`
(pre-comment); module md5 `C46340FB626DA6B36C11CE00ABFC6447` (cdt-30's, unchanged before the
comment pass). After each: the original bytes restored, the file touched (so MSBuild rebuilds it),
md5 re-confirmed, and the next run rebuilt from it.

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| M1 `\/server/share` accepted again: the network test narrowed to `StartsWith(@"\\")` / `StartsWith("//")` and the drive test widened with `&& !System.IO.Path.IsPathFullyQualified(value)` | `F77AA12E179170F1FE7905B49BD90455` | xUnit 8: the refusal theory and the cross-check theory for `\/server/...`, `/\server\...`, `\??\D:\...` and `\??\UNC\...` ("Assert.Throws() Failure: No exception was thrown"). Pester 1: the agreement test ("Expected $true, because ConfigStorePath.Resolve's rule is what the module mirrors and this test emulates (if (value.Length >= 2 && IsSeparator(value[0]) && IsSeparator(value[1]))); if it changed, change both with it") |
| M2 the network test narrowed to `\\` and `//` only, drive test kept | `3D7784021D921250445CD04D642B6DCB` | xUnit 4: both theories for `\/server/...` and `/\server\...` ("Assert.Contains() Failure: Sub-string not found" - still refused, but no longer as a network path) |
| M3 the normalisation dropped (`return new Resolution(value, MustExist: true);`) | `1D9FF725D5AEEFB3FDA6CBAA43549E68` | xUnit 7: the accepted theory for `D:/x/y.db`, `D:\x/y.db`, `  D:/x/y.db  ` and the cross-check for `D:/shared/x.db`, `D:\shared/x.db`, ` D:/x.db `, `z:/a` ("Assert.Equal() Failure: Strings differ") |
| M4 `char.IsAsciiLetter` widened to `char.IsLetter` | `3C8D190913F12B446AE802A4F6690AC3` | xUnit 2: the Kelvin-sign case in both theories ("No exception was thrown") |
| M5 the `0d040ed` file restored (byte-identical: cdt-30's recorded md5 of this file) | `42032B7F3D5AB9D1D8D5E93BCCDAA12F` | xUnit 15: the four spellings in both theories (8), the three forward-slash accepted cases, and the four forward-slash cross-check cases |
| M6 the module's drive test changed back to `'^[A-Za-z]:\\'` (the C# suite's tripwire on the module; cdt-30's V1, the same md5) | `8A44F0B9518CB54E1E6F75DB848BC4A7` (module) | xUnit 1: `ModuleRule_IsPinnedInItsSource_AndNamesThisMethod` ("tools/SqliteConfigBackup.psm1 no longer contains `$Value -cnotmatch '^[A-Za-z]:[\\/]'`. It is the ops scripts' copy of ConfigStorePath.Resolve's rule: change Resolve, ModuleDecision here and the module together."). Pester 4: the agreement test ("... the same call on 'D:/shared/x.db'"), two forward-slash acceptance cases, the real forward-slash backup case |

M2 shows the two-separator test is about the reason, not the decision: every value it refuses
would also fail the drive test. It stays, because "a network (UNC) path" is what an operator needs
to hear for those spellings, and it is the module's split.

Comment pass after the round (the tripwire and summary comments in `ConfigStorePath.cs`, the
module's comment), then the guards rerun green: `ConfigStorePathTests` 57 passed,
`SqliteConfigBackup.Tests.ps1` 49 passed. Post-comment md5s (LF, as committed in `c73e51c`):
`ConfigStorePath.cs` `CBAC08792F931B75FE8905F88714D6B9` (corrected in a follow-up commit: the record
first carried `FDC3A5F491157686B1C90426CBA13D9A`, taken before the tripwire comment's last wording
change - it names the module by file rather than by function - which the gates below ran after),
`tools/SqliteConfigBackup.psm1`
`4412D7BFC9A5F153EAB9E3456C5893F0`, `ExchangeAdminWeb.Tests/ConfigStorePathTests.cs`
`83E4A0EC357D106C96B852C510D746C3`, `tests/ps/SqliteConfigBackup.Tests.ps1`
`B410AFC85E661DAB4225266F2B44C676`.

Gates, on the content as committed: `dotnet build ExchangeAdminWeb.slnx -c Release` 0 errors (61
warnings on a full rebuild, the same 61 as before the change, none in these files); `dotnet test
ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` 4951 passed, 0 failed, 3 skipped (the
three `ADDirectorySearchServiceTests` that need Active Directory), 4954 total; `dotnet format
ExchangeAdminWeb.slnx --verify-no-changes --no-restore` exit 0; `git diff --check HEAD` clean;
`./tools/Test-AsciiOnly.ps1` after `git add` passed. Because the module and a Pester file changed
too, in one pwsh session with Pester 6.2.0 imported first: `Invoke-ScriptAnalyzer -Path . -Recurse`
0 errors (333 warnings repo-wide; none new in the touched files); `Invoke-Pester tests/ps` 400
passed, 0 failed, 0 skipped; Windows PowerShell 5.1.20348 `ParseFile` of
`tools/SqliteConfigBackup.psm1` 0 errors.

Not run: an app start against a real network share in any of the refused spellings (no share here,
nothing elevated); the startup path is `Program.cs:56-67`, which calls this method and treats its
throw as fatal, unchanged.
