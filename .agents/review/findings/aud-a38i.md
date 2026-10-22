# aud-a38i: An IIS node nested inside the mirrored tree under the SAME pool passes the serving-pool check and is still mirrored over

**Severity**: MEDIUM - production-served files inside the publish tree can be purged or replaced
by `robocopy /MIR` while the promotion or deploy reports success. The same pool avoids the
live-file corruption class (aud-a38f), not the content loss.
**Status**: Merged to master (`5e9b2c79`); closed on guard proof; codex ops-batch-6 clean
**Branch**: `worktree-agent-ab28c8810a2e397d6` (forked at `1049f839`)
**Commit**: `f6b86a46` (merged `5e9b2c79`)
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (ops-batch-5, `prompt-ops-batch-5.md`, 2026-10-22); first recorded as the known gap in `aud-a38f.md` by its implementer

## Evidence

At `c3c4b685`, `tools/AppPoolControl.psm1`, `Assert-AppPoolServesPath` (around line 380): every
site, application and virtual directory at or inside the folder must run under the named pool,
and a nested node under ANOTHER pool is refused. A nested node under the SAME pool passes. The
caller then stops that one pool and runs `robocopy /MIR` over the whole folder, which deletes or
replaces the nested node's files - they are not in the staging publish.

The aud-a38f record and the index row carry this as an owner gap; the owner's standing go covers
every open review row, and the reviewer judged refusal the right rule.

## Approach (to be written by the implementing agent before code)

- After proving something is served AT the folder, refuse ANY IIS node whose physical path is
  strictly inside the folder (directory-boundary match as in aud-a38f), whatever its pool. The
  refusal names each nested node's location, pool and folder and says the mirror would replace or
  purge it. No allow-list or override in this change; if the owner wants one it is a separate
  decision.
- Keep every aud-a38f rule (boundary match, vdir read, missed-read guard, unreadable IIS refuses).
- Pester: a nested application under the same pool refuses; a nested vdir under the same pool
  refuses; the existing same-pool-at-root case still passes; the aud-a38f other-pool cases still
  refuse. Mutation at the call site: restore the same-pool exemption. Windows PowerShell 5.1
  `ParseFile` zero errors on the module, promote, `deploy.ps1`, the installer and
  `Move-ConfigDbToShared.ps1`; `Import-Module Pester -RequiredVersion 6.2.0` before the analyzer.
- The installer and `deploy.ps1` call the same assertion, so they inherit the rule; say so in the
  record and confirm no caller filters the result.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); this record was admitted under it.
Ops-script-only: no version bump (the `aud-a34`/`a35`/`a36`/`a38`/`a38c`/`a38f` precedent).
Written before the code.

1. **`tools/AppPoolControl.psm1`, `Assert-AppPoolServesPath`.** The other-pool check narrows to
   the entries AT the folder (the `aud-a38c` rule). A new check refuses every entry strictly
   inside the folder - `$inside` less the entries equal to the target, so the `aud-a38f` boundary
   match (`Test-PhysicalPathInside`) still decides "inside" - whatever its pool. Order: IIS
   unreadable; nothing at the folder; something at the folder under another pool; anything rooted
   inside it.
2. **Message.** Each nested node as `<location> runs under '<pool>', with its files at '<folder>'`
   (the `aud-a38f` item form its tests pin), then: the mirror replaces the folder with the publish
   output and purges what that output lacks, so it would replace or purge those files whatever
   pool serves them; move the node out of the folder or remove it from IIS. No allow-list, no
   override parameter.
3. **Every `aud-a38f` rule kept**: boundary match, virtual-directory read, missed-read guard (no
   root virtual directory), unreadable IIS refuses, nothing at the folder refuses. A pass returns
   the entries at the folder (nothing can be inside it on a pass).
4. **Callers unchanged; they inherit.** `tools/promote-dev-to-prod.ps1` (`Stop-AppPoolChecked`),
   `deploy.ps1` (both branches), `tools/Install-ExchangeAdminWeb.ps1` (its confirm step) and
   `tools/Move-ConfigDbToShared.ps1` (`Assert-PoolServesInstance`). Verify by AST that each call is
   `$null = Assert-AppPoolServesPath -Name ... -PhysicalPath ...` - the result discarded, never
   filtered - and that no catch swallows the throw (the cutover's wrapper rethrows through
   `Write-Fail`). Record the result.
5. **Tests**, `tests/ps/AppPoolControl.Tests.ps1`, a new `aud-a38i` context: a nested application
   under the same pool refuses (THE FINDING; nested folder spelled in mixed case and slashes, a
   site root above the folder ignored); a nested virtual directory under the same pool refuses;
   nested nodes under the same and another pool are all named in one refusal; through the default
   probe, an application's own virtual directory inside the folder, under that application's pool,
   refuses. Two `aud-a38f` tests pinned the same-pool pass and change: `passes when everything
   inside the folder ...` gives way to the finding test; the default-probe attribution test keeps
   its purpose with the application's own virtual directory AT the folder (passes only if
   attributed to the application's pool). The same-pool-at-root pass (`passes, and returns what
   serves the folder ...`) and every `aud-a38f` other-pool refusal are unchanged.
6. **Mutation at the call site**: the nested check regains the same-pool exemption (the `aud-a38f`
   rule). Expected: the same-pool tests fail; the other-pool tests still pass.
7. Pure ASCII, nothing PS7-only; Windows PowerShell 5.1 `ParseFile` on the module and the four
   callers; `Import-Module Pester -RequiredVersion 6.2.0` before the analyzer.

## Guard proof

Built as the Plan. `tools/AppPoolControl.psm1`, `Assert-AppPoolServesPath`: the other-pool check
reads `$serving` (the entries at the folder) instead of `$inside`; a new check refuses
`$nested` - `$inside` less the entries equal to the target - with no pool filter. Order: unreadable
IIS, nothing at the folder, the folder's own nodes under another pool, anything nested. A pass
returns `$serving`. Every `aud-a38f` rule is untouched (boundary match, virtual-directory read,
missed-read guard).

**Callers - they inherit, none filters.** Checked by AST over the four scripts
(`Parser::ParseFile`, every `CommandAst` named `Assert-AppPoolServesPath`):

| Call site | Statement | Catch whose try body holds it |
| --- | --- | --- |
| `tools/promote-dev-to-prod.ps1:240` (`Stop-AppPoolChecked`) | `$null = Assert-AppPoolServesPath -Name $Name -PhysicalPath $PublishPath` | none |
| `deploy.ps1:616` (upgrade) | `$null = Assert-AppPoolServesPath -Name $AppPoolName -PhysicalPath $PublishPath` | none |
| `deploy.ps1:929` (`-Force`) | same | none |
| `tools/Install-ExchangeAdminWeb.ps1:1090` (its confirm step) | same | none |
| `tools/Move-ConfigDbToShared.ps1:231` (`Assert-PoolServesInstance`) | `$null = Assert-AppPoolServesPath -Name $Name -PhysicalPath $PublishPath` | one: `Write-Fail "... $($_.Exception.Message) ... Nothing has been stopped or copied."` - rethrows (Write-Fail throws) |

Each discards the result (`$null =`, one-element pipeline) and none filters it, so each inherits the
refusal unchanged; the existing AST guards (`DeployInvariants` cdt-28, `InstallerConfigBackup`
cdt-32, `aud-a38c`, `aud-a38g`) still pin the call shapes and pass.

| Test (`tests/ps/AppPoolControl.Tests.ps1`, `Assert-AppPoolServesPath`) | Pins |
| --- | --- |
| `aud-a38i ...` / `refuses an application nested inside the folder under the same pool, and says the mirror would replace or purge it` | **the finding**: same pool, nested folder spelled `d:/SITES/app/admin/`, a site root above the folder ignored - refused, naming location, pool and folder |
| `refuses a virtual directory nested inside the folder under the same pool` | a virtual directory entry is held to the same rule |
| `names every node inside the folder in one refusal, whatever pool each runs under` | a same-pool and an other-pool nested node in one message |
| `aud-a38f: the default probe ...` / `aud-a38i: refuses an application's own virtual directory inside the folder, though it runs under the named pool` | the rule through the default probe's virtual-directory read |
| (changed) `reads an application's own virtual directories under that application's pool` | the virtual directory now points AT the folder, so the test still passes only when it is attributed to the application's pool (`aud-a38f` N5) and still needs the read (N4) |
| (removed) `passes when everything inside the folder runs under the named pool, and returns all of it` | pinned the behaviour this finding reverses; replaced by the finding test (same fixture) |
| (unchanged) `passes, and returns what serves the folder, when the named pool alone serves it` | the same-pool-at-root pass |
| (unchanged) the `aud-a38f` other-pool refusals, sibling-prefix and nothing-at-the-folder tests | still refuse / still pass |

Against the unfixed module (`git checkout HEAD`, md5 `073bbcc514b681862c568c567b759b4a`): 71 tests,
4 failed - the four new ones; the three same-pool ones with "no exception was thrown", the combined
one because the old message names only the other-pool node. Restored from a saved copy, touched,
md5 re-confirmed.

Mutations at the call site, each by exact-text replacement asserted to match once, md5 recorded,
`AppPoolControl.Tests.ps1` run in a fresh pwsh (Pester 6.2.0), the module restored from a saved
copy, touched, md5 re-confirmed equal, and the file re-run green (71/71). First run on the
pre-comment module (md5 `0c14e4efd0641bff173c61ec95543963`), re-run on the final module
(`16a3880cf505713bd59ad2998ccfe826`) and final test file (`14e9f318a14c7e9c79a689c8e0d13632`) with
the same results; the md5s below are the re-run's.

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| I1: the nested check regains the same-pool exemption (`-and` the pool is not the named one) - the `aud-a38f` rule | `4165398d30e8b2e0af4f0b45acbda2ad` | 4: both same-pool tests and the default-probe one with "no exception was thrown"; the combined one names only `Web/media`. Every other-pool test passes |
| I2: the other-pool check widened back to the whole tree (`$inside`) | `6c285188b308b85bacd9227cdc80a2e9` | 1: `names every node inside the folder in one refusal ...` - the operator would be told to "name the pool that serves them", which cannot help |

md5s are of the CRLF working-tree bytes (`.psm1` is `text=auto`, `autocrlf` on; `.ps1` is
`eol=crlf`). Comments and help text were written after the round.

## Gates

One pwsh 7.6.6 session, `Import-Module Pester -RequiredVersion 6.2.0` before `PSScriptAnalyzer`
1.25.0:

- `Invoke-ScriptAnalyzer -Severity Error`, one `-Path` per file: `tools/AppPoolControl.psm1` 0,
  `tests/ps/AppPoolControl.Tests.ps1` 0.
- `Invoke-Pester tests/ps`: 452 passed, 0 failed, 0 skipped, 0 not run.
- Windows PowerShell 5.1.20348.5756 `Parser::ParseFile`: `tools/AppPoolControl.psm1`,
  `tools/promote-dev-to-prod.ps1`, `deploy.ps1`, `tools/Install-ExchangeAdminWeb.ps1`,
  `tools/Move-ConfigDbToShared.ps1`, `tests/ps/AppPoolControl.Tests.ps1` - 0 errors each. Under 5.1
  the module imports, exports its four functions (helpers private), refuses the nested same-pool
  case and passes a mixed-spelling at-the-folder case.
- The installer contains no `deploy.ps1` string (0 occurrences).
- `git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` passed after `git add -A`.

## Files changed

- `tools/AppPoolControl.psm1` - the narrowed other-pool check, the nested check, help text, comments.
- `tests/ps/AppPoolControl.Tests.ps1` - the `aud-a38i` context (3 tests), one default-probe test,
  one `aud-a38f` test removed and one re-pointed, comments.
- This record.

## Known gaps

- **The default probe has never run against real IIS** (`aud-a38c`/`aud-a38f`, unchanged). Not run.
- **Over-refusal, by design.** A node inside a folder the mirrors exclude (`/XD logs config` in
  `deploy.ps1`, the promotion and the installer) is refused though `/MIR` would leave it alone; and
  the cutover, which mirrors nothing, inherits the refusal too - where the deploy and the promotion
  of the same folder already refuse. No allow-list or override; one would be an owner decision.
- The callers' plan lines and comments ("refuse unless IIS serves that folder from this pool and no
  other") stay true but do not mention the nested rule. Unchanged: the plan text is pinned by the
  `aud-a38c` and `aud-a38g` tests, and the callers' code is unchanged.
- A folder IIS reaches through a junction or another volume spelling is still not matched
  (`aud-a38c`/`aud-a38f`).

## Closeout

Fixed on branch; not merged, not pushed. No version bump (ops scripts).
