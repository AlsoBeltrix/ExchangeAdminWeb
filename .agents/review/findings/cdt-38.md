# cdt-38: the cutover's -SharedDbPath check keeps its own copy of the old writers' rule

**Severity**: LOW. `tools/Move-ConfigDbToShared.ps1` validates `-SharedDbPath` with a private copy
of the rule the installer dropped in `cdt-37` - a drive letter, a colon and a BACKSLASH, matched
case-insensitively. So a forward-slash path the app opens and the read side reads back is refused,
a mixed-separator UNC path is refused for the wrong reason, and a Kelvin-sign "drive letter" the
app refuses is accepted - under PowerShell 7 the plan then names it as the `ConfigStore:Path` to
write into both instances. Same class as `cdt-37`; admitted with it.
**Status**: Fixed on branch; awaiting merge
**Branch**: -- (delivered as a patch against `master` `1c6038d` with `aud-a38g`'s patch applied
first, from the worktree `agent-af538bba9cd55306a`; the coordinator applies and commits it)
**Commit**: `<pending>`
**Reviewer**: implementer (cdt-37 agent)

## Evidence

`tools/Move-ConfigDbToShared.ps1` at `1c6038d`:

```powershell
113: function Assert-LocalAbsolutePath {
114:     param([string]$Path, [string]$Name)
115:
116:     if ([string]::IsNullOrWhiteSpace($Path)) { Write-Fail "$Name is required." }
117:     if ($Path.StartsWith('\\') -or $Path.StartsWith('//')) {
118:         Write-Fail "$Name must be a local path, not a network share: $Path. SQLite locking is not reliable over a share and both instances run on this server."
119:     }
...
123:     if ($Path -notmatch '^[A-Za-z]:\\') {
124:         Write-Fail "$Name must be an absolute local path (drive letter, colon, backslash - for example D:\inetpub\ExchangeAdminWebShared\config\exchangeadmin.db): $Path"
125:     }
126: }
...
576: Assert-LocalAbsolutePath -Path $SharedDbPath -Name 'SharedDbPath'
```

The value is then used as given: compared with each instance's configured path, created by the
VACUUM INTO, and written into both `appsettings.json` files as `ConfigStore:Path` (step 6).

The rule the app and the read side share since `cdt-35`/`cdt-37` is `Get-ConfigStorePathForm` in
`tools/SqliteConfigBackup.psm1` (224-246, exported at 421, the module already imported at line 84
of the cutover): any two leading separators are a network share; otherwise only an ASCII letter, a
colon and then `\` or `/`, matched case-SENSITIVELY, is a local path. The installer calls it
(`tools/Install-ExchangeAdminWeb.ps1:241`) and then normalises with `GetFullPath` (748). The
module's own comment still records the cutover as the one writer with its own copy:

```powershell
239:    # GetFullPath). Of the two writers, the installer's -ConfigStorePath check is this function
240:    # (cdt-37); the cutover's -SharedDbPath check accepts only X:\, which this reads back.
```

**Mechanism**: `Write-Fail` is `throw`, at script level under `$ErrorActionPreference = "Stop"` -
a terminating throw before anything is read or changed; and for the Kelvin sign, no error at all.

Reproduced at `1c6038d`, plan mode, fake dev and prod folders built as the suite builds them:

```
pwsh 7.6.6
=== forward slashes : -SharedDbPath C:/Users/.../cdt38probe_.../Shared/config/exchangeadmin.db
  refused: SharedDbPath must be an absolute local path (drive letter, colon, backslash - ...): C:/Users/...
=== UNC, mixed separators (\/) : -SharedDbPath \/server/share/exchangeadmin.db
  refused: SharedDbPath must be an absolute local path (drive letter, colon, backslash - ...): \/server/share/exchangeadmin.db
=== Kelvin-sign drive (U+212A) : -SharedDbPath K:\shared\exchangeadmin.db
  accepted: the run went on to its plan
    PLAN Set ConfigStore:Path = K:\shared\exchangeadmin.db in C:\Users\...\Dev\appsettings.json (...)
    PLAN Set ConfigStore:Path = K:\shared\exchangeadmin.db in C:\Users\...\Prod\appsettings.json (...)
Windows PowerShell 5.1.20348
  the first two: the same refusals
=== Kelvin-sign drive (U+212A)
  refused: SharedDbPath (K:\shared\exchangeadmin.db) is inside DevPublishPath (...\Dev). A publish folder is mirrored by deploys ...
```

(The `K` above is U+212A KELVIN SIGN; this record is ASCII, so it is written as a plain K.) Under
5.1 the check passes it too, and A39's containment check then refuses it for a reason that is not
true: .NET Framework's `GetFullPath` throws on it, and `Test-PathIsInside` treats an unparseable
path as "not provably outside".

## Predicted observable failure

An operator gives `-SharedDbPath D:/inetpub/ExchangeAdminWebShared/config/exchangeadmin.db`: the
cutover refuses a path both instances would open, and says it wants a backslash. One giving
`\/server/share/exchangeadmin.db` is told the path is not absolute rather than that a share is
not allowed. A Kelvin-sign letter (pasted from somewhere that folded case) is planned into both
`appsettings.json` files under PowerShell 7; with `-Apply` the VACUUM INTO cannot create the file,
so the run fails at step 4 having stopped and restarted both pools for nothing.

## Plan (approved by the owner's EVERYTHING go)

Owner, 2026-10-21: "fix all the bugs that you know about and any more you find while doing
so" and "... EVERYTHING." (`.agents/decisions.md`, 2026-10-21 "Every open review follow-up
is approved for implementation"). The `cdt-37` agent recorded this as a follow-up; the supervisor
admitted it with "the cutover calls Get-ConfigStorePathForm and refuses anything but Local, writes
the normalised backslash form, and holds no rule of its own". Written before the code.

1. **No copy of the rule.** `Assert-LocalAbsolutePath` keeps its blank check and its two
   messages, and decides with `$form = Get-ConfigStorePathForm -Value $Path`: `NetworkShare` is
   the network-share refusal; anything else that is not `Local` is the absolute-path refusal, its
   wording now "a drive letter, a colon, then \ or /" (the installer's). Fail closed: an
   unexpected form is a refusal. The module is already imported at the top level (line 84),
   before the one call.
2. **One spelling.** The statement after the call is
   `$SharedDbPath = [System.IO.Path]::GetFullPath($SharedDbPath)`, the installer's idiom: a
   `Local` value is fully qualified, so this consults no current directory, and it turns `/` into
   `\` under .NET Framework and .NET alike. The banner, the comparisons with each instance's
   configured path, the containment check, the VACUUM INTO, the `appsettings.json` writes and the
   restore commands all use that value, so plan mode prints what `-Apply` writes.
3. **The module's comment** that names the cutover as the writer with its own copy (239-240), and
   the agreement test's comment on the writers' own drive test
   (`tests/ps/SqliteConfigBackup.Tests.ps1:298`), say what is now true. Comments only; the
   pinned TRIPWIRE wording is not touched.
4. Read by Windows PowerShell 5.1: pure ASCII, no PS7-only syntax, parsed under 5.1. No version
   bump: ops-script-only behaviour (the `cdt-37` precedent).
5. **Guards** in `tests/ps/MoveConfigDbToShared.Tests.ps1`, a `cdt-38` Describe: the real script
   in plan mode given a forward-slash, a mixed and a lower-case-drive `-SharedDbPath` goes on to
   plan, its banner and both `ConfigStore:Path` plan lines carry the backslash spelling, and the
   given spelling appears nowhere; UNC in all four two-separator spellings is refused as a network
   share, and relative, drive-relative, driveless rooted, `\??\UNC\` and Kelvin-sign values as not
   absolute, before anything is created; lifted, the check accepts exactly what
   `Get-ConfigStorePathForm` calls `Local` on a list of values; AST - the function calls the module
   once and holds no `-match`/`-like` operator and no `StartsWith`/`IsPathRooted`-style call of its
   own, the import precedes the call, and the next statement is the `GetFullPath`.

Mutations planned, at the call site: the old regex restored beside the module's answer; the
normalisation dropped; the module's rule re-implemented locally (same behaviour, so only the AST
pin can see it); the `1c6038d` function restored whole.

## Approach

As planned. `tools/Move-ConfigDbToShared.ps1` after the fix (on top of `aud-a38g`):

- Lines 116-136: `Assert-LocalAbsolutePath` - a comment saying why it holds no rule, the blank
  check, `$form = Get-ConfigStorePathForm -Value $Path` (129), the network-share refusal for
  `NetworkShare` (130-132), and for anything else that is not `Local` (133-135) "`$Name` must be an
  absolute local path (a drive letter, a colon, then \ or / - for example D:\...): `$Path`".
- Line 86: the module import, unchanged, before the one call.
- Line 607: the call, unchanged; lines 608-611 a comment; line 612
  `$SharedDbPath = [System.IO.Path]::GetFullPath($SharedDbPath)`.

`tools/SqliteConfigBackup.psm1`: the help of `Get-ConfigStorePathForm` (219-223) and its comment
(239-241) name both writers - the installer's `-ConfigStorePath` (`cdt-37`) and the cutover's
`-SharedDbPath` (`cdt-38`) - in place of "the cutover's -SharedDbPath check accepts only X:\".
`tests/ps/SqliteConfigBackup.Tests.ps1:298-299`: the agreement test's comment no longer says the
writers have a drive test of their own. Comments only in both; the pinned TRIPWIRE wording and the
rule's lines are untouched.

The fixed script under both shells, the same probe as in Evidence:

```
pwsh 7.6.6 and Windows PowerShell 5.1.20348 alike
=== forward slashes : -SharedDbPath C:/Users/.../cdt38probe_.../Shared/config/exchangeadmin.db
  accepted: the run went on to its plan
    Shared DB : C:\Users\...\cdt38probe_...\Shared\config\exchangeadmin.db
    PLAN Set ConfigStore:Path = C:\Users\...\Shared\config\exchangeadmin.db in C:\Users\...\Dev\appsettings.json (...)
    PLAN Set ConfigStore:Path = C:\Users\...\Shared\config\exchangeadmin.db in C:\Users\...\Prod\appsettings.json (...)
=== UNC, mixed separators (\/) : -SharedDbPath \/server/share/exchangeadmin.db
  refused: SharedDbPath must be a local path, not a network share: \/server/share/exchangeadmin.db. ...
=== Kelvin-sign drive (U+212A) : -SharedDbPath K:\shared\exchangeadmin.db
  refused: SharedDbPath must be an absolute local path (a drive letter, a colon, then \ or / - ...): K:\shared\exchangeadmin.db
```

## Files changed

- `tools/Move-ConfigDbToShared.ps1` - `Assert-LocalAbsolutePath`, the normalisation after its call.
- `tools/SqliteConfigBackup.psm1` - comments only.
- `tests/ps/MoveConfigDbToShared.Tests.ps1` - the `cdt-38` Describe (15 tests).
- `tests/ps/SqliteConfigBackup.Tests.ps1` - one comment.
- This record.

## Guard proof

| Test (`tests/ps/MoveConfigDbToShared.Tests.ps1`, `cdt-38: ...`) | Pins |
| --- | --- |
| `accepts a <Form> -SharedDbPath, and plans and reports the backslash spelling` (forward-slash, mixed-separator, lower-case-drive forward-slash) | **the finding**: the real script in plan mode goes on to its plan ("Plan only - nothing was changed"); the banner says `Shared DB : <the backslash spelling>`; exactly two `PLAN Set ConfigStore:Path = <the backslash spelling> in` lines; the given spelling appears nowhere in the output; no shared file |
| `refuses <Value> as a network share, before anything` (`\\`, `//`, `\/`, `/\`) | each refused with "SharedDbPath must be a local path, not a network share: `<value>`."; no backup root created |
| `refuses a <Form> -SharedDbPath as not an absolute local path, naming it, before anything` (relative, drive-relative, driveless rooted with either separator, `\??\UNC\`, Kelvin-sign drive) | each refused with the new wording, naming the value; the Kelvin sign refused at all (the old check passed it); no backup root created |
| `makes the module's call on every value: it accepts exactly what Get-ConfigStorePathForm calls Local` | the lifted check, the module imported, on 22 values: accepted if and only if the module says `Local` |
| `holds no rule of its own, asks the module after importing it, and normalises the path next (AST)` | one `Get-ConfigStorePathForm -Value $Path` in the function; no `-match`/`-like` family operator and no `StartsWith`, `EndsWith`, `IsPathRooted`, `IsPathFullyQualified` or `GetPathRoot` call in it; the import is a top-level statement before the call; the statement after the call is exactly `$SharedDbPath = [System.IO.Path]::GetFullPath($SharedDbPath)` |

Unchanged tests that still hold it: the plan-mode block's UNC, relative and drive-relative
refusals (`'*network share*'`, `'*absolute local path*'`), the A39 containment tests, and
`SqliteConfigBackup.Tests.ps1`'s agreement test, which lifts this function and requires whatever
it accepts to read back.

Mutations on `tools/Move-ConfigDbToShared.ps1`, fixed md5 `F9267226BAD2B0E89F2F3EB9A484E126`
(pre-comment, LF form, with `aud-a38g` applied). Each was applied by exact-text edits asserted
to match once, confirmed by md5, run in a fresh pwsh against `MoveConfigDbToShared.Tests.ps1`,
`DeployInvariants.Tests.ps1` and `SqliteConfigBackup.Tests.ps1` (248 tests), then the fixed bytes
restored, touched and md5-confirmed. The three environmental `DeployInvariants` failures
(`aud-a38g`'s Gates) showed in every run, as on the unchanged export, and are not counted below.

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| N1: the old regex restored beside the module's answer - `if ($form -ne 'Local' -or $Path -notmatch '^[A-Za-z]:\\')` | `3C367F25F267E0D2D6DC39E5631ED4B2` | 4: the forward-slash and lower-case-drive acceptance cases (refused with the absolute-path message), the call-on-every-value test ("must make the module's call on 'D:/shared/x.db'"), the AST test ("no -match or -like of its own, but got 1") |
| N2: the normalisation dropped - `$SharedDbPath = $SharedDbPath` | `555258877B3E915CCB12AB57D8380296` | 4: all three acceptance cases ("the banner reports the one spelling"), the AST test (the next statement) |
| N3: the module's rule re-implemented locally, same behaviour - `$form = if ($Path -match '^[\\/][\\/]') { 'NetworkShare' } elseif ($Path -cnotmatch '^[A-Za-z]:[\\/]') { 'NotAbsolute' } else { 'Local' }` | `63F3CCE4278611B688870E2207E172C3` | 1: the AST test ("Expected 1, but got 0" module calls) - the one guard that can see a copy that behaves the same |
| N4: the `1c6038d` function restored whole | `03F5F5143FC019212563FD06DA771071` | 12: the forward-slash and lower-case-drive acceptance cases; the `\/` and `/\` shares (refused as "absolute local path"); all six not-absolute cases (the old wording, and for the Kelvin sign "no exception was thrown"); the call-on-every-value test; the AST test |

Post-comment md5s (LF form): `tools/Move-ConfigDbToShared.ps1` `64C897414AE05D661DE00FAB6A398160`,
`tests/ps/MoveConfigDbToShared.Tests.ps1` `9A47CB884E75BF9E1624C703EA8C106F`,
`tools/SqliteConfigBackup.psm1` `D205F8E47ED639D2BB9B29496DF6692E`,
`tests/ps/SqliteConfigBackup.Tests.ps1` `C9C7EC837EAC3451173D6F2F0CCEA2F0`.

## Gates

On the same export of `1c6038d` with `aud-a38g` and this change applied, one pwsh 7.6.6 session,
Pester 6.2.0 imported before the analyzer, on the final (commented) content:

- `Invoke-ScriptAnalyzer -Path . -Recurse`: 0 errors. One finding added, Information:
  `PSAvoidUsingPositionalParameters` on the `Join-Path` of the new Describe's
  `SqliteConfigBackup.psm1` import (the file's existing idiom). The script 39 before and after,
  the module 3 and 3, `SqliteConfigBackup.Tests.ps1` 2 and 2.
- `Invoke-Pester tests/ps`: 438 passed, 3 failed, 0 skipped, 0 not run (441: 426 after
  `aud-a38g`, plus the 15 new). The 3 are the environmental `DeployInvariants` failures recorded
  on `aud-a38g` (an archive export is not a git repository); identical on the unchanged export.
- Windows PowerShell 5.1.20348.5756 `Parser::ParseFile`: `tools/Move-ConfigDbToShared.ps1`,
  `tests/ps/MoveConfigDbToShared.Tests.ps1`, `tools/SqliteConfigBackup.psm1`,
  `tests/ps/SqliteConfigBackup.Tests.ps1` - 0 errors each. The fixed script also ran under 5.1
  (the probe above).
- ASCII: `tools/Test-AsciiOnly.ps1`'s rule applied without git to every `.cs`, `.ps1` and `.psm1`
  of the export outside `bin\`/`obj\` (486 files): passed.
- `git diff --check`: not runnable outside a repository; the patch's added lines checked against
  the default rules (blank-at-eol, space-before-tab, blank-at-eof): clean.
- No .NET run for its own sake - no C# changed. `ConfigStorePathTests` pins the module's rule
  lines and its TRIPWIRE phrase, none of which this touches.

## Known gaps

- **No `-Apply` run.** Every run here is plan mode. What `-Apply` writes is the variable the plan
  line prints: step 6 is `Set-ConfigStorePathInAppsettings -Value $SharedDbPath`.
- **A dotted spelling of an already-shared path is no longer recognised on a re-run.**
  `GetFullPath` folds `.` and `..`, so if a run before this change wrote such a spelling into both
  `appsettings.json` files, re-running with it compares the folded path with the stored one, misses
  "already shared", and refuses on "A file already exists at ...". Fail-safe, and only for a
  `-SharedDbPath` with dot segments; the default has none.
- **Under Windows PowerShell 5.1 a path with characters .NET Framework calls illegal** (for
  example `|`) now ends the run at the `GetFullPath` with .NET's own message instead of A39's
  untrue "inside DevPublishPath". The installer behaves the same (`cdt-37`). Nothing has been
  changed by then either way.
