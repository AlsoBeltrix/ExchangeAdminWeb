# aud-a38c: the promotion stops the pool it is told to, not the pool that serves prod

**Severity**: MEDIUM. The same failure the independent review of `aud-a35` rated HIGH in
`deploy.ps1` - a confirmed stop of the WRONG pool, then `robocopy /MIR` over a live
application. Narrower here: after `aud-a38b` a mistyped pool name is refused (an absent prod
pool is an error), so only a correctly spelled name of a different, existing pool gets
through.
**Status**: Merged to master; codex services-batch-2 raised a follow-on, admitted as aud-a38f (nested IIS apps inside the mirrored tree) - fix in progress
**Branch**: `worktree-agent-a4a7066146993eb49`
**Commit**: `bafac35` (merged)
**Reviewer**: implementer (found while fixing `aud-a38b`; the supervisor asked for the check)

## Evidence

`deploy.ps1` gained this before both of its stops in the remediation of the `aud-a35` review
(update path line 521 at `0e18583`; fresh install line 732):

```powershell
$boundPool = try { (Get-WebApplication -Name $AppAlias -Site $ParentSite -ErrorAction Stop).applicationPool } catch { $null }
if ($boundPool -and $boundPool -ne $AppPoolName) {
    Write-Fail "IIS application $ParentSite/$AppAlias runs under app pool '$boundPool', not '$AppPoolName'. ..."
}
```

`tools/promote-dev-to-prod.ps1` has no equivalent. At `25e7187` (`aud-a38b`) its stop step
warns about bulk jobs and then calls `Stop-AppPoolVerified -Name $Name`, where `$Name` is
`-ProdAppPoolName` - a parameter defaulting to `ExchangeAdminWeb`, and passed by
`tools/deploy-pipeline.ps1` from its own `-ProdAppPool`. Nothing in the file asks IIS which
pool serves `$prod`: no `Get-WebApplication`, no `Get-Website`, no `applicationPool` read.

## Predicted observable failure

An operator runs `deploy-pipeline.ps1 -Prod -ProdAppPool ExchangeAdminWebDev` (the dev pool,
by slip), or prod's IIS application was moved to a new pool while the old pool of the old
name still exists. The promotion stops the named pool, `Stop-AppPoolVerified` truthfully
confirms that pool Stopped, and `robocopy /MIR` replaces the binaries of prod, which is still
serving under its real pool. Some files are locked and skipped, some replaced; prod runs a
mixture of two builds, and the promotion reports success. In the first example dev is taken
down as well.

## Plan (approved by the owner's EVERYTHING go)

Owner, 2026-10-21: "fix all the bugs that you know about and any more you find while doing
so" and "... EVERYTHING." (`.agents/decisions.md` 2026-10-21, second entry). The supervisor's
brief for `aud-a38b` named this check and asked for it as its own finding if it was missing.
Written before the code.

1. **`tools/AppPoolControl.psm1` gains `Assert-AppPoolServesPath -Name -PhysicalPath`**, with
   the module's injection idiom: `-ApplicationsProbe` lists every IIS site root and
   application as `Location`, `ApplicationPool`, `PhysicalPath`; the default reads
   `Get-Website` and `Get-WebApplication -Site`. It selects those whose files are at the
   folder about to be replaced and:
   - throws when any of them runs under a different pool, naming where and which pool;
   - throws when none of them does - **fail closed**: if nothing in IIS can be shown to serve
     the folder, stopping the named pool cannot be shown to stop what runs from it. The
     message lists what IIS reported, so a path spelled differently or a probe that read
     the wrong property is diagnosable from the refusal alone;
   - throws when IIS cannot be read;
   - otherwise returns what serves the folder.
   Paths compare after expanding environment variables, normalising to a full path and
   dropping trailing separators, ordinal and case-insensitive - and by equality, never by
   prefix: `...\ExchangeAdminWebDev` starts with `...\ExchangeAdminWeb`. Pool names compare
   ordinal, case-insensitive. The function names nothing; folder and pool come from the
   caller (Architectural Invariant 7).
2. **The promotion calls it in its stop step**, after the jobs warning and before
   `Stop-AppPoolVerified` - the order `deploy.ps1` uses. `Stop-AppPoolChecked` gains
   `-PublishPath`; the call site passes `$prod`. The dry run prints the check as a plan line
   and touches no IIS.
3. **Why the folder and not a site/alias pair as in `deploy.ps1`.** The promotion has no
   `-ParentSite` or `-AppAlias`, and adding them would add a site-name default this script does
   not have today and a second way to name what `-ProdPath` already names. The mirror replaces
   a folder, so "which pool serves this folder" is the question the safety argument needs
   answered.
4. **Fail closed where `deploy.ps1` does not.** `deploy.ps1`'s check passes when the lookup
   fails (`catch { $null }`, then `if ($boundPool -and ...)`). This one refuses on an
   unreadable or empty answer. `deploy.ps1` is not changed here (out of scope; another agent
   is working that file).
5. Guards in `tests/ps/AppPoolControl.Tests.ps1`: behavioural tests of the new function
   through `-ApplicationsProbe`; for the promotion, the lifted stop step from `aud-a38b`'s
   tests driven with the probe injected, plus AST guards on the call's arguments, its order
   before the stop, and `-PublishPath $prod` at the call site.
6. No version bump: ops-script-only, the `aud-a34`/`aud-a35`/`aud-a36`/`aud-a38` precedent.

Implemented as planned. One detail settled during implementation: the default probe's
property names were checked against WebAdministration's own type data on this host
(`iisprovider.types.ps1xml`), not taken from memory - `ConfigurationElement#site#application`
defines `PhysicalPath` as a script property reading the root virtual directory's
`physicalPath`, and lists `Path` and `ApplicationPool` among its display properties.

## Files changed

- `tools/AppPoolControl.psm1` - `Assert-AppPoolServesPath` (exported) and the private
  `ConvertTo-ComparablePhysicalPath`.
- `tools/promote-dev-to-prod.ps1` - `Stop-AppPoolChecked -PublishPath`, the check before the
  verified stop, its plan line, `-PublishPath $prod` at the call site, comments.
- `tests/ps/AppPoolControl.Tests.ps1` - the `Assert-AppPoolServesPath` block; the
  `aud-a38c` context inside the `aud-a38b` block; the `aud-a38b` stop-step tests now pass
  `-PublishPath` and inject what IIS serves (by default, the prod pool alone).

## Guard proof

| Test (`tests/ps/AppPoolControl.Tests.ps1`) | Pins |
| --- | --- |
| `Assert-AppPoolServesPath` / `passes, and returns what serves the folder, when the named pool alone serves it` | the ordinary path; a site root elsewhere is ignored |
| `refuses when the folder is served by a different pool, and says which and where` | **the finding**, at the function |
| `refuses when any one of several applications on the folder runs under another pool` | all must match, not any |
| `recognises the same folder however IIS spells it` | case, forward slashes, trailing separator, dot segment, environment variable |
| `does not mistake a sibling folder that shares a prefix for the folder` | equality, not prefix (refusing direction) |
| `fails closed when nothing in IIS serves the folder, and lists what IIS reported` | no fall-through on an empty match; also the accepting direction of the prefix trap |
| `fails closed when IIS reports nothing at all` | no fall-through on an empty answer |
| `fails closed when IIS cannot be read, and says why` | unreadable is not confirmed |
| `compares pool names without regard to case, as deploy.ps1's check does` | parity with `deploy.ps1`'s `-ne` |
| `aud-a38c` / `refuses a prod folder served by another pool, before stopping anything` | **the finding**, through the real stop step: refused, and neither the pool probe nor the stop ran |
| `asks IIS after the jobs warning and before the stop` | order: jobs, binding, stop |
| `in a dry run prints the check and reads nothing from IIS` | plan mode (Architectural Invariant 4) |
| `checks the folder the mirror replaces, inside the stop step, before the stop` | AST: one call, `-Name $Name -PhysicalPath $PublishPath`, inside `Stop-AppPoolChecked`, ending before `Stop-AppPoolVerified`; the call site passes `-PublishPath $prod` |

Mutations, fixed md5s (pre-comment) `tools/promote-dev-to-prod.ps1`
`C75694E18DF19640A90C4A33AA81FCC2`, `tools/AppPoolControl.psm1`
`F2878B38E904398DF2F0CCB3D4B59FF4`. Each applied by exact-text replacement asserted to match
once, confirmed by md5, run against `AppPoolControl.Tests.ps1` and
`DeployInvariants.Tests.ps1`, then restored byte-identical, touched, md5 re-confirmed and the
two suites re-run green (144 passed, 0 failed). md5s are of the CRLF working-tree bytes.

| Mutation | File | md5 after mutation | Failed |
| --- | --- | --- | --- |
| C1: the check removed from the stop step (the pre-fix shape) | promote | `0AD0E95526CDDCAF8859EE8A4FDF9A09` | 3: `refuses a prod folder served by another pool`; `asks IIS after the jobs warning`; `checks the folder the mirror replaces` |
| C2: folders compared by prefix (`StartsWith`) | module | `AF8B4ACAFD91C7FC661688D3E30B59DF` | 2: `does not mistake a sibling folder`; `fails closed when nothing in IIS serves the folder` |
| C3: an empty match returns instead of refusing (`deploy.ps1`'s leniency) | module | `FCC637CA82FD34611A026FA38878CA85` | 2: `fails closed when nothing in IIS serves the folder`; `fails closed when IIS reports nothing at all` |
| C4, present but dead: the wrong-pool refusal downgraded to `Write-Warning` | module | `D123B3C11508A6AED7A13D822B4C6DA9` | 3: both module wrong-pool tests and the promotion's `refuses a prod folder served by another pool` |
| C5: the check moved after the stop - it still refuses, but only once the wrong pool has stopped | promote | `79F821B150D22308BEE69A00A3938357` | 3: `refuses a prod folder ...` ("exists probe" was found); `asks IIS after the jobs warning`; `checks the folder ...` (order) |

Post-comment md5s: promote `265D6B30D85568C2DED25DDE2D933515`, module
`55D74BC1EEFE58BFAFB041261C92F66C`.

The new function was also exercised under Windows PowerShell 5.1.20348 - the module imports,
exports four functions with the helper private, and a forward-slashed, lower-case,
trailing-separator spelling of the folder matches under .NET Framework's `GetFullPath`.

## Gates

One pwsh 7.6.6 session, Pester 6.2.0 imported before the analyzer, on the final code:

- `Invoke-ScriptAnalyzer -Path . -Recurse`: 0 errors; 298 warnings, unchanged from
  `aud-a38b` (one new Information-level item).
- `Invoke-Pester tests/ps`: 295 passed, 0 failed, 0 skipped (282 after `aud-a38b`; 13 new).
- Windows PowerShell 5.1.20348 `Parser::ParseFile`: `tools/promote-dev-to-prod.ps1`,
  `tools/AppPoolControl.psm1`, `deploy.ps1`, `tools/Install-ExchangeAdminWeb.ps1` - 0 errors.
- `tools/Test-AsciiOnly.ps1` after `git add`: passed. `git diff --check HEAD`: clean.
- No .NET run for its own sake - no C# or Razor changed.

## Known gaps

- **The default probe has never run against real IIS.** IIS is not readable without
  elevation on this host, and no promotion was run. Its property names come from the
  module's type data (above), not from an observed object. If one is wrong, the module's
  strict mode turns the read into a throw and the promotion refuses with "Cannot read the
  IIS sites and applications ...: The property ... cannot be found" - it cannot pass. A
  refusal there would block promotions until the probe is fixed, which is the price of
  failing closed. **Worth a one-line manual check on the deploy host, elevated, before the
  next real promotion**: `Import-Module WebAdministration; Import-Module .\tools\AppPoolControl.psm1;
  Assert-AppPoolServesPath -Name ExchangeAdminWeb -PhysicalPath D:\inetpub\ExchangeAdminWeb`
  (read-only; it stops nothing). Not run.
- **Only a site's root and its applications' root virtual directories are considered.** A
  non-root virtual directory of some other application pointing into prod's folder, or a
  folder IIS reaches through a junction under a different spelling, is not matched. The
  first would be missed; the second fails closed with the IIS spelling in the message.
- **`deploy.ps1`'s own check still passes when its lookup fails** (`catch { $null }`, then
  `if ($boundPool -and ...)`), and on its update path an `'Absent'` pool is then accepted.
  Not changed here - that file is another agent's - and reported to the supervisor as a
  candidate finding.
