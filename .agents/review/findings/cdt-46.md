# cdt-46: ConvertTo-ComparablePhysicalPath does not resolve 8.3 short names or junctions, so the same folder can read as a move

**Severity**: LOW - fails closed. IIS may store `physicalPath` as an 8.3 spelling (`D:\INETPU~1\EXCHAN~1`) or a junction path while `-PublishPath` names the same directory in long or final form; `tools/AppPoolControl.psm1` about 268 expands environment variables, normalises slashes, dot segments and trailing separators but not short names or final paths, so the cdt-36 guard (and `Assert-AppPoolServesPath`, which shares the helper) treats the same folder as a move and demands `-AllowRepoint` with a misleading "MOVES it" warning.
**Status**: Verified on branch; awaiting merge and codex review - admitted 2026-10-23 evening from codex ops-batch-12 on cdt-36; inside the released scope
**Branch**: `fix/cdt36` (worktree `D:\source\ExchangeAdminWeb-wt\cdt36`), after the cdt-44 commit `349253f7`
**Commit**: the `fix(cdt-46)` commit on `fix/cdt36` that carries this record (not merged)
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base eb017102 head 493c270d (merged 72430407); 2026-10-23T19:06-19:13Z; capability proof: AGENTS.md read and git show --stat 493c270d both succeeded

## Evidence

Codex ops-batch-12 finding 3 at `72430407`.

**Implementer's correction, probed while fixing.** The 8.3 half does not reproduce for a folder that
exists. `[System.IO.Path]::GetFullPath` expands an existing short name itself, under Windows
PowerShell 5.1 (.NET Framework 4.8) and PowerShell 7.6 (.NET 10) alike. A scratch probe on a
temporary folder gave its long spelling from `...\CDT46G~1\REALFO~1` and from
`...\CDT46G~1\REALFO~1\INNERF~1` in both, and `C:\PROGRA~1` gave `C:\Program Files`. Only a spelling
whose tail does not exist yet keeps short components: .NET Framework expands just part of
`<short>\not-there-yet`. The junction half reproduces fully: `GetFullPath` leaves a junction as
written, so a junction and its target compare as two folders. The fix below covers both. The 8.3
test is a regression pin (the comparison must go on taking the short name) rather than proof of the
new code; the junction tests carry the proof.

## Approach

Written before the code; copied from master's record and filled in here. Line numbers are
`tools/AppPoolControl.psm1` at `349253f7`.

**Mechanism.** Every folder comparison in the module goes through the lexical normaliser, and since
cdt-44 deploy.ps1's check does too, through `Test-SamePhysicalFolder`:

```
266:     if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
267:     $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim())
...
270:         $full = [System.IO.Path]::GetFullPath($expanded)
...
274:     return $full.TrimEnd('\', '/')
...
401:     $target = ConvertTo-ComparablePhysicalPath $PhysicalPath
...
409:     $inside = @($applications | Where-Object { Test-PhysicalPathInside (ConvertTo-ComparablePhysicalPath $_.PhysicalPath) $target })
410:     $serving = @($inside | Where-Object {
411:             [string]::Equals((ConvertTo-ComparablePhysicalPath $_.PhysicalPath), $target, [System.StringComparison]::OrdinalIgnoreCase)
...
460:         $nested = @($inside | Where-Object { -not [string]::Equals((ConvertTo-ComparablePhysicalPath $_.PhysicalPath), $target, ...) })
```

`GetFullPath` leaves an 8.3 component (`INETPU~1`) and a junction as written, so one folder under
two spellings compares as two. In deploy.ps1's check that is a refusal, or with `-AllowRepoint` a
"MOVES it" warning for a folder that does not move. In `Assert-AppPoolServesPath` it is worse than
the finding says. A node IIS spells another way is not "at" the folder, so an upgrade whose IIS
path is an 8.3 spelling is refused only there, before the stop, after the pool step and the grants.
If deploy.ps1's early check learned the spellings alone, that late refusal would be a side-effect-
ordering failure of its own. And a node serving the folder's files under another pool, through a
junction, is not seen at all, so the mirror replaces files it serves.

**Change** (all in the module, so every caller has it - "keep the helper shared"):

1. A private `Resolve-FinalPhysicalPath`: the final path of a folder that exists, as the filesystem
   resolves it (`GetFinalPathNameByHandle`, through a small C# type compiled with `Add-Type` on first
   use). That means long names for 8.3 components, junctions and symbolic links followed, and the
   case on disk. It returns `$null` when the path cannot be opened: it does not exist, access is
   denied, or the type cannot be compiled. The comparison then falls back to the lexical form and
   fails closed, as today. ASCII C#, compiled the same way under Windows PowerShell 5.1 and
   PowerShell 7, and checked in both by a scratch prototype first.
2. `Test-SamePhysicalFolder` (279-293): lexically equal as before, or both resolve and their final
   paths are equal. A spelling that does not resolve is not taken as the same folder.
3. `Assert-AppPoolServesPath` (401-460): the folder's final path beside its lexical form. A node is at
   the folder when either form is equal, and inside it when either form is inside. The union never
   counts fewer nodes than the lexical comparison alone. So a node at a short or junction spelling
   is held to the named pool. A node rooted inside the folder through a junction that leads
   elsewhere is still nested, because the lexical form says so. And a node whose files reach into
   the folder through a junction elsewhere is newly nested.
4. `ConvertTo-ComparablePhysicalPath` stays lexical: it is what the hints print, and what
   containment starts from.
5. deploy.ps1's `Assert-ApplicationTargetUnchanged`: when the folders still differ and a spelling does
   not exist on disk, the refusal says so after the differences. "A spelling that cannot be resolved
   on disk is not taken as the same folder: '<spelling>'." The existing wording stays as it is, so
   the cdt-36/42 pins hold.

Not changed: `tools/Install-ExchangeAdminWeb.ps1`'s own cdt-15 comparison
(`ConvertTo-InstallerComparablePath`), which stays lexical and fails closed early. Its
`Assert-AppPoolServesPath` call (cdt-32) gets the module's new rule.

Constraints: Invariant 6 (the module is read by Windows PowerShell 5.1: pure ASCII, no PS7-only
syntax); the C# compiles under the 5.1 CodeDom compiler (C# 5: no interpolation, no expression
bodies). No version bump.

Guards:

- `tests/ps/AppPoolControl.Tests.ps1`, a new Describe with real fixtures: a temporary folder with a
  long name; a junction to it (`mklink /J`); an 8.3 spelling - the fixture's own when the volume
  generates short names, else `$env:ProgramFiles`' (`C:\PROGRA~1` on most hosts). The 8.3 test is
  skipped, with a reason, only when neither has a short name. Note that CI treats a skip as a
  failure. Cases: `Test-SamePhysicalFolder` takes the short spelling and the junction for the
  folder; it does not take a sibling, or a spelling that does not exist; two missing spellings still
  compare lexically. `Assert-AppPoolServesPath`: a node at the junction or short spelling under the
  named pool serves the folder; under another pool it is refused. A node rooted inside through a
  junction leading out is still refused as nested. A node whose final path is inside the folder is
  refused as nested.
- `tests/ps/DeployInvariants.Tests.ps1`: deploy.ps1's check, lifted, with the fixtures. The same folder
  through a junction or an 8.3 spelling is deployed in place, with no "MOVES it". A run folder that
  does not exist is refused with the note naming it.

Mutations: the canonicalisation removed (`Resolve-FinalPhysicalPath` returns `$null`); containment by
the final path alone (the lexical half dropped); `Assert-AppPoolServesPath` back to the lexical form
only; `Test-SamePhysicalFolder` without the final comparison; the note removed.

Files: `tools/AppPoolControl.psm1`, `deploy.ps1`, `tests/ps/AppPoolControl.Tests.ps1`,
`tests/ps/DeployInvariants.Tests.ps1`, this record.

**After the fix**, as built: `tools/AppPoolControl.psm1` 279-319 (the C# source), 321-342
(`Resolve-FinalPhysicalPath`), 344-364 (`Test-SamePhysicalFolder` and its export), 472-508 and 557
(`Assert-AppPoolServesPath`: `$targetFinal` at 478, the two predicates, `$inside` at 508, the nested
set at 557). `deploy.ps1` 232-242 (the note).

## Files changed

Before the repair at `349253f7` (blob; md5 of the committed LF content), and after:

- `tools/AppPoolControl.psm1` - `d271b95a00ab`, `407F15DB6D68E6FD33DB915C69CBC03D`; after `C77C62268160179B6A60851BDA055140`
- `deploy.ps1` - `35f5b7531cc3`, `AEED2CD068888AB6F67948793743C3AF`; after `B0306FB2BE46EBF0715ACC7098323B81`
- `tests/ps/AppPoolControl.Tests.ps1` - `bd8b5263a126`, `342786E8FBB17FE5B04443C736A204D8`; after `F56D475C6BB7ACFFC6E9BE1025C813CA`
- `tests/ps/DeployInvariants.Tests.ps1` - `4ed29bd97d33`, `9863A1640638E51F32FEA0F3D5C5B74B`; after `F2F7ECAC228B1849A11B678E059B7184`
- this record - copied from master's (`fe83c3b2`) and filled in

No version bump.

## Guard proof

Tests:

| Test | Pins |
| --- | --- |
| `tests/ps/AppPoolControl.Tests.ps1`, Describe `cdt-46: one folder however the filesystem spells it - an 8.3 short name, a junction` (1205): `takes an 8.3 short spelling for the folder it names` | Against the fixture folder's own short name (generated on this host's temp volume; else `$env:ProgramFiles`'; skipped with a reason only if neither has one): `Test-SamePhysicalFolder` takes the short and the long spelling for one folder, both ways round, and `Assert-AppPoolServesPath` counts a node IIS spells with the short name as serving the folder. A regression pin: `GetFullPath` already expands an existing short name (Evidence) |
| the same Describe: `takes a junction for the folder it points at, but not a sibling, and not a spelling that does not exist` | A junction (`mklink /J`) and its target are one folder, with a trailing separator and in another case too; a sibling whose name starts with the folder's is not; a spelling that does not exist is not taken for an existing folder; two spellings that do not exist still compare lexically |
| the same Describe: `holds a node IIS spells through a junction to the named pool (Assert-AppPoolServesPath)` | A node at the junction under the named pool serves the folder (1 node returned). With the folder served directly and a node at the junction under `OtherPool`, the refusal "served by an app pool other than 'ProdPool': Other/Copy runs under 'OtherPool'" |
| the same Describe: `keeps a node rooted inside the folder nested though its junction leads out, and counts one whose files reach in through a junction` | A node at `<folder>\outlink`, a junction leading out of the folder, is refused as nested ("IIS serves content from inside ... Web/App/out"). A node at `<root>\inlink`, a junction leading into `<folder>\reports`, is refused as nested ("... Other/Reports") |
| `tests/ps/DeployInvariants.Tests.ps1`: `deploys in place when IIS spells the folder through a junction, and names a spelling it cannot resolve on disk (cdt-46, real files)` (1353) | deploy.ps1's check, lifted, with the real module: IIS at the junction, the run at the folder - plain, with `-AllowRepoint`, and as an upgrade - is deployed in place, and nothing prints "MOVES it". A run folder that does not exist is refused, the refusal reading "... (this run: -PublishPath '<missing>'). A spelling that cannot be resolved on disk is not taken as the same folder: '<missing>'. -Force and -ConfirmFreshInstall ...". Two existing, different folders are refused as before, with no note |

Mutations on the fixed files (md5s above), each applied by a script that fails unless every edit
matches exactly once and the mutant passes a sanity check of its own shape, run against
`DeployInvariants.Tests.ps1` and `AppPoolControl.Tests.ps1` together; then the original bytes restored,
the mtimes refreshed, the md5s re-confirmed and both suites re-run green each time (215 passed):

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| R0 the whole fix reverted: the module and `deploy.ps1` as at `349253f7` (byte-identical: that commit's md5s) | module `407F15DB6D68E6FD33DB915C69CBC03D`; `AEED2CD068888AB6F67948793743C3AF` | 4: the deploy test (the junction refused), the junction `Test-SamePhysicalFolder` test, the junction-serving test ("No IIS site or application has its files at ..."), and the nested test (the in-link node not refused) |
| C1 the canonicalisation removed (`Resolve-FinalPhysicalPath` returns `$null`) - the record's mutation | module `58E16C845E3116051FA1E9E20291D874` | the same 4 |
| C2 containment by the final path alone (the lexical half of `$isInFolder` dropped) | module `D36E504AAACF7ED3E2E31D1CA9EDE6DC` | 30: the nested test (the out-link node not refused), and every existing `Assert-AppPoolServesPath`, promotion and deploy-region test that uses folders which do not exist on disk, where nothing could be found at the folder any more |
| C3 `Assert-AppPoolServesPath` back to the lexical form only (`$targetFinal = $null`) | module `E1D5BD67D2D33D2D25E66AED4A279FF1` | 2: the junction-serving test and the nested test (the in-link node) |
| C4 `Test-SamePhysicalFolder` without the final comparison | module `AE57066347A4C95E289E90AD9B569A44` | 2: the deploy test (the junction refused) and the junction `Test-SamePhysicalFolder` test |
| C5 the spelling note removed (deploy.ps1) | `16C53D290CF9C43D38F46957C245584D` | 1: the deploy test (the refusal without the note) |

A first version of the 8.3 test was wrong and is recorded so it is not re-learned. It asserted that
the short and long spellings differ lexically ("so only the final path can match them"), and it
failed on the fixed tree, because `GetFullPath` had already expanded the short name. That is how the
Evidence correction was found. The assertion was removed, and the test now pins the behaviour.

Windows PowerShell 5.1.20348.5756: `deploy.ps1` and the module parse with no errors, and a scratch
harness importing the module against a temporary folder and three junctions gave the Pester results.
The first call compiled the type in 404 ms. `Test-SamePhysicalFolder` took the junction and
`C:\PROGRA~1`, and refused a missing spelling. `Assert-AppPoolServesPath` served the folder through
the junction, and refused the other pool through the junction and both nested junction cases.

Gates on the final tree: `Invoke-Pester tests/ps` (Pester 6.2.0, pwsh 7) 491 passed, 0 failed, 0
skipped; `Invoke-ScriptAnalyzer -Path . -Recurse -Severity Error,Warning` (1.25.0) 0 errors, 317
warnings, none new; `git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` passed; all four
PowerShell files stay CRLF.

Not run: a deploy or promotion against IIS paths spelled through junctions (needs IIS).

## Coder dispute

None. The finding's 8.3 half is corrected in Evidence, not disputed: the fix covers it either way.

## Known gaps

- CI treats a skipped test as a failure. The 8.3 test skips only when neither the temp volume nor
  `$env:ProgramFiles` has a short name. On such a runner the powershell job would fail on that skip,
  and the message would say why. **Resolved by cdt-50** (`fix/cdt36`, after `e999ddda`): the test no
  longer skips. It can also set its fixture's short name with `fsutil`, and it always asserts the
  synthetic case. It is renamed "takes an 8.3 short spelling for the folder it names, and never a
  short-looking spelling that is not on disk (cdt-50: no skip path)". `TestSuiteIntegrity` now refuses
  any skip path in the suite. See `.agents/review/findings/cdt-50.md`.
- `tools/Install-ExchangeAdminWeb.ps1` keeps its own lexical cdt-15 comparison, so a junction-spelled
  IIS path is refused there early (fail-closed). Its later `Assert-AppPoolServesPath` call has the new
  rule.
- `Add-Type` compiles the helper type on first use: about 0.4 s under 5.1 and 0.9 s under 7, once per
  process. Where compiling is blocked (constrained language mode, no compiler), `Resolve-FinalPhysicalPath`
  returns `$null` and every comparison falls back to the lexical form, failing closed as before.
- `Assert-AppPoolServesPath` now opens each IIS node's folder to resolve it. A node whose folder is
  unreadable to the deploying account resolves to nothing and is compared lexically.

## Reviewer comments

None yet.

## Closeout

Pending: commit on `fix/cdt36`, codex review, merge into `master`, branch deletion.
