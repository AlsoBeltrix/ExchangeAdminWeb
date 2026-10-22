# aud-a38f: the serving-pool check matches exact roots only, but the mirror replaces the whole tree

**Severity**: MEDIUM - the same live-file promotion corruption `aud-a38c` closed, still open for
an IIS application or virtual directory rooted inside the prod folder and served by another pool.
**Status**: Fixed on master `c3c4b685` (patch applied by the supervisor); closed on guard proof; codex ops-batch-5 clean. Known gap for the owner: a nested IIS node under the SAME pool passes the check, yet robocopy /MIR still replaces or purges its files
**Branch**: `worktree-agent-aec54e09518f47fc1` - forked at `ca6575a2`, before the `aud-a38c` code it
fixes, so built and gated on a `git archive` export of master `1c6038d5` and delivered as a patch
for the supervisor to apply
**Commit**: `c3c4b685`
**Reviewer**: codex / gpt-5.5-dzs / xhigh, services-batch-2, 2026-10-21

(Not the cutover finding first committed under this id in `3dd3ca5`: that one is `aud-a38h`, renamed
because codex assigned this id here. `tests/ps/MoveConfigDbToShared.Tests.ps1` still labels its
`Describe` with the old id.)

## Evidence

Raised by `codex / gpt-5.5-dzs / xhigh` reviewing the services batch at `98ac93b`, finding 2.
Verdict text: `.agents/review/services-batch-2.result.txt` in the main checkout (machine-local).

`tools/AppPoolControl.psm1`, `Assert-AppPoolServesPath`, at `1c6038d5` (unchanged since `98ac93b`):

```powershell
    $serving = @($applications | Where-Object {                                    # line 331
            [string]::Equals((ConvertTo-ComparablePhysicalPath $_.PhysicalPath), $target, [System.StringComparison]::OrdinalIgnoreCase)
        })
    ...
    $elsewhere = @($serving | Where-Object { -not [string]::Equals([string]$_.ApplicationPool, $Name, ...) })
```

Only entries whose folder EQUALS the target are ever compared with the named pool, and the
default probe lists site roots and applications only - no virtual directories. Then
`tools/promote-dev-to-prod.ps1` mirrors the whole tree (line 382):

```powershell
    Invoke-RobocopyChecked -Description "Promoting dev binaries to prod" -RobocopyArgs @(
        $dev, $prod,
        '/MIR',
```

So with `D:\sites\App` served by `ProdPool` and an application or virtual directory at
`D:\sites\App\Admin` served by `OtherPool`, the check passes, the promotion stops `ProdPool`, and
`/MIR` rewrites a tree `OtherPool` is still serving from.

## Predicted observable failure

A nested application (or a virtual directory of some other application) lives under prod's
folder in its own pool. Promotion stops prod's pool, confirms the stop truthfully, and mirrors:
the nested application's files are replaced under it while it serves - or, since `/MIR` purges
what dev's output lacks, deleted outright. The promotion reports success.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"). Ops-script-only: no version bump (the
`aud-a34`/`a35`/`a36`/`a38`/`a38c` precedent). `tools/AppPoolControl.psm1` and its tests only;
the promotion's call site is unchanged.

1. **Tree-aware, boundary-matched.** A private `Test-PhysicalPathInside -Path -Root` over the
   comparable forms: equal, or `Root` followed by a directory separator - so `...\AppDev` and
   `...\App.old` stay siblings of `...\App`. Every entry inside the tree must run under the named
   pool, or the check refuses naming each offender's location, pool and folder.
2. **"Something serves the folder" still means something AT it.** The existing fail-closed rule
   is kept on the equal entries, not the inside ones: a parent folder named by mistake would have
   its other children replaced by `/MIR`, so a nested match alone is not a pass. The unreadable-IIS
   refusal is unchanged.
3. **The default probe reads virtual directories.** For every application, the site's root one
   included, `Get-WebConfiguration -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter "<ItemXPath>/virtualDirectory"`
   - the XPath form WebAdministration's own type data uses for an application's `PhysicalPath`
   (`iisprovider.types.ps1xml` on this host: `$this.ItemXPath + "/virtualDirectory[@path='/']/@physicalPath"`).
   Each non-root virtual directory is emitted under the pool of the application that owns it. The
   root application's XPath is the site's `ItemXPath` plus `/application[@path='/']`, the one piece
   not taken verbatim from the type data. Every IIS application has a root virtual directory, so a
   listing without `/` is a read that missed: the probe throws, and the throw is the existing
   unreadable-IIS refusal - a wrong XPath refuses promotions, it cannot pass one.
4. **Message** keeps the strings the existing tests pin (`... runs under '<pool>'`, `Stopping
   '<pool>' would leave that application running`) and adds the folder:
   `Files at or under '<target>' are served by an app pool other than '<name>': <location> runs
   under '<pool>', with its files at '<folder>'`.
5. Returns every entry at or inside the folder; the promotion discards the value (`$null = ...`).
6. Tests in `tests/ps/AppPoolControl.Tests.ps1`: through `-ApplicationsProbe` for the matching
   rules, and through the default probe with stand-in WebAdministration commands defined in the
   module's own scope (`InModuleScope`), where the probe resolves them first.

## Guard proof

Built as the Plan. Run against the unfixed module first: the six tests that need the fix failed
as predicted - the nested application under another pool threw nothing - and the two
over-matching guards passed.

| Test (`tests/ps/AppPoolControl.Tests.ps1`, `Assert-AppPoolServesPath`) | Pins |
| --- | --- |
| `aud-a38f ...` / `refuses when an application nested inside the folder runs under another pool, and names it` | **the finding**: refused, naming `Web/App/Admin`, `OtherPool` and its folder |
| `passes when everything inside the folder runs under the named pool, and returns all of it` | nested under the same pool passes; a site root ABOVE the folder is ignored; case and slash spelling of a nested folder |
| `does not count a folder that only shares the prefix as inside the folder` | `...\AppDev`, `...\AppDev\Admin`, `...\App.old` under other pools pass - boundary, not prefix |
| `counts a virtual directory inside the folder, under the pool of whatever owns it` | a virtual directory entry inside the tree is held to the named pool |
| `still refuses when something inside the folder is served but nothing at the folder itself` | the fail-closed rule stays on the folder itself (parent folder named by mistake) |
| `aud-a38f: the default probe ...` / `finds a virtual directory of the site root pointing inside the folder, and refuses its pool` | the default probe reads the root application's virtual directories and attributes them to the site's pool |
| `reads an application's own virtual directories under that application's pool` | an application's own virtual directory is read and attributed to that application's pool |
| `refuses when an application's virtual directories cannot be shown to have been read` | a listing without a root virtual directory refuses, as unreadable IIS |
| (existing) `does not mistake a sibling folder that shares a prefix for the folder` | now also pins the boundary in the tree match |

Mutations at the call site, `tools/AppPoolControl.psm1` at its pre-comment state (md5
`77fda2ceb98204d9ebcb26091f18c319`). Each applied by exact-text replacement asserted to occur
once, md5 confirmed, `AppPoolControl.Tests.ps1` run, the file restored from a saved copy, touched
and the md5 re-confirmed equal to the pre-comment state. Re-run against the final test file
(md5 `c766ba6b283f9001c8958c8626ebcbd7`; one helper renamed after the first round, for the
analyzer) with the same results.

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| N1: the tree match reverted to equality (the pre-fix rule) | `c72cad6d1ab3aa467650f9fe96b09abd` | 5: **the nested case does not refuse**; `passes when everything inside ...`; `counts a virtual directory inside ...`; both default-probe virtual-directory reads |
| N2: the boundary dropped - a bare prefix test | `7df971b548ee422aa37d793f5fe96f6b` | 2: `does not mistake a sibling folder ...`; `does not count a folder that only shares the prefix ...` |
| N3: the nothing-serving check counts nested entries | `b5822138dd5364bddf6bc88961787b54` | 1: `still refuses when something inside the folder is served but nothing at the folder itself` |
| N4: the default probe reads no virtual directories | `e5d23e4556d323ec81fd0815aaa5fa0d` | 3: all three default-probe tests |
| N5: virtual directories attributed to the site's pool, not their application's | `51cfdcb2f2191749689089efb267f130` | 1: `reads an application's own virtual directories under that application's pool` |
| N6: the root-virtual-directory guard removed | `5302478059caa3eb97a05c3dd2763b59` | 1: `refuses when an application's virtual directories cannot be shown to have been read` |

md5s are of the CRLF working-tree bytes (`.ps1` is `eol=crlf`; `.psm1` checks out native). The
pre-fix module's md5 is `55d74bc1eefe58bfafb041261c92f66c`, the post-comment md5 `aud-a38c`
records, so the export's bytes are a checkout's. Comments were written after the round; the final
module is `073bbcc514b681862c568c567b759b4a`. The patch itself is LF-normalised, the form
`git diff` emits and `git apply` matches.

Under Windows PowerShell 5.1.20348 the module imports, exports its four functions with the helper
private, passes the mixed-spelling nested same-pool case and refuses the nested other-pool case.

## Gates

On the export of `1c6038d5` with `aud-a6e` and this change applied; one pwsh 7 session, Pester
6.2.0 imported before the analyzer:

- `Invoke-ScriptAnalyzer -Path . -Recurse`: 0 errors; 333 warnings and 124 information items,
  the same as `1c6038d5` in the same export. The two changed files carry the same items as before.
- `Invoke-Pester tests/ps`: 424 passed, 3 failed, 0 skipped (`1c6038d5` in the same export: 416,
  3, 0; +8 for these tests). The 3 failures are environmental, identical before and after: each
  runs `git -C $RepoRoot ls-files` or `rev-parse HEAD` against the export, which is not a
  repository (exit 128) - `DeployInvariants` `cdt-10`, `cdt-14` and the `aud-sqlite3-path` scan.
  Owed in the repository at apply.
- Windows PowerShell 5.1.20348 `Parser::ParseFile`: `tools/AppPoolControl.psm1`,
  `tests/ps/AppPoolControl.Tests.ps1`, `tools/promote-dev-to-prod.ps1`, `deploy.ps1`,
  `tools/Install-ExchangeAdminWeb.ps1` - 0 errors each.
- `git diff --check` and `tools/Test-AsciiOnly.ps1` need a repository, which the export is not.
  Their rules were applied to the patch and the changed files instead: no trailing whitespace,
  space before tab or blank line at end of file in what it adds, and no byte above 0x7F in either
  changed file. Owed in the repository at apply.
- No .NET run for its own sake - no C# or Razor changed.

## Files changed

- `tools/AppPoolControl.psm1` - `Test-PhysicalPathInside` (private); the tree match; the default
  probe's virtual directories and its missed-read guard; the message; comments.
- `tests/ps/AppPoolControl.Tests.ps1` - the two `aud-a38f` contexts; one block comment.

## Known gaps

- **The default probe has never run against real IIS**, as with `aud-a38c`: IIS is not readable
  without elevation here and no promotion was run. The virtual-directory read uses the type data's
  own XPath form; the root application's XPath is constructed. A wrong guess refuses (missing
  property under strict mode, or no root virtual directory), it cannot pass. Worth the same
  one-line elevated check on the deploy host before the next real promotion, in Windows PowerShell
  5.1: `Import-Module WebAdministration; Import-Module .\tools\AppPoolControl.psm1;
  Assert-AppPoolServesPath -Name <prod pool> -PhysicalPath <prod folder>` (read-only; stops
  nothing). Not run.
- **A nested node under the SAME pool passes, and `/MIR` still replaces or purges it.** The check
  answers "is everything served from this tree stopped by stopping this pool?", which is the
  finding; it does not ask whether a nested application's content should be mirrored over at all.
  Refusing every nested node would be a different rule - the supervisor's or owner's call.
- A folder IIS reaches through a junction or a different volume spelling is not matched; as in
  `aud-a38c`, the root case fails closed with the IIS spelling in the message, a nested one would
  be missed.

## Reviewer comments

`Reviewer: codex / gpt-5.5-dzs / xhigh, services-batch-2, 2026-10-21`, finding 2 (MEDIUM). Raw
verdict `.agents/review/services-batch-2.result.txt` in the main checkout (machine-local). The
reviewer's "include the containing site/app that serves the target" is read as the entry AT the
folder, which stays required; an application rooted ABOVE the folder is not served from the tree
and is ignored, as before.

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent. Independent
review of the fix pending.

## Closeout

Fixed on branch and delivered as a patch; not merged, not pushed. No version bump (ops scripts).
