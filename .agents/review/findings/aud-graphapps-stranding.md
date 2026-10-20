# aud-graphapps-stranding: a half-finished run lost a secret that cannot be reissued

**Severity**: HIGH for the blast radius, LOW for frequency. The script is run once per
environment, and when it goes wrong it leaves a real app registration in the tenant whose
client secret nobody ever saw and nobody can retrieve.
**Status**: Verified, fixed, independently reviewed, review findings remediated
**Branch**: -- (direct-to-main)
**Commit**: `aeab7ec`

## Evidence

From the lower-priority Wave 4 list of the 2026-10-16 audit:
*"`tools/New-ExchangeAdminWebGraphApps.ps1:101` - a failure during the second registration
strands the first and suppresses its one-time secret output."* Verified against the code.

The script creates two registrations in a loop. Each iteration creates the application, its
service principal, grants admin consent for every role, and adds a client secret. The
credentials go into `$results`. Then, **after the loop**:

```powershell
Write-Host "Copy each row into its Delinea secret now - the Client Secret is not retrievable later."
$results | Format-List
```

`$ErrorActionPreference = "Stop"`, so anything that fails in iteration two propagates out of
the loop and that summary never runs. Registration one exists in the tenant, consented, with
a secret that was generated, never displayed, and **cannot be read back from Entra**. The
operator's only remedy is to find the orphan and delete it - and nothing told them it is
there.

The script's own line says the secret is not retrievable later. It then buffered it until
after a loop that can fail.

**The likeliest trigger is in the same file.** The Graph role lookup ran *inside* the
creation loop:

```powershell
if (-not $role) { throw "Graph application permission '$roleName' not found." }
```

The `Roles` lists are hand-edited literals at the top of the script. A typo or a renamed
Graph permission in the second registration's list throws at exactly the point that strands
the first.

There was already a preflight for name collisions - *"Preflight BOTH names before creating
anything, so an existing registration refuses the whole run and never leaves the tenant
half-configured"* - so the shape was understood. Roles just were not covered by it.

## Predicted observable failure

An operator adds a permission to the Intune registration's list and mistypes it. The run
creates `ExchangeAdminWeb-RiskyUsers` completely, then throws on the unknown permission. The
console shows a red error. There is no summary, no client ID, no secret. In the tenant there
is now a consented application with `IdentityRiskyUser.ReadWrite.All` and a live credential
that nobody holds.

## Approach

**Three changes, smallest first.**

1. **Preflight every role for every registration**, in the same place and for the same
   reason as the existing name-collision check. The most likely mid-run failure now happens
   before anything is created.
2. **Print each registration the moment it exists**, not only in the summary. This is the
   actual fix: an unrecoverable value must not be buffered across an operation that can
   fail. The summary stays, because reading one list at the end is what an operator wants
   when it all works.
3. **Wrap the loop so a failure reports what was created.** The names, client IDs and
   secrets of completed registrations are printed with an explicit instruction to record or
   delete them, and the error is rethrown so a partial run still fails.

## Files changed

- `tools/New-ExchangeAdminWebGraphApps.ps1`
- `tests/ps/DeployInvariants.Tests.ps1`

No version bump; ops-script-only.

## Guard proof

Source guards. The script needs a live `Connect-MgGraph` and write access to a tenant;
nothing here can be driven. The guards are written as **ordering and structure**, not as
message text, because a string assertion passes while the code around it is dead - the
mistake made on `aud-a38` and caught there by a mutation.

| Test | What it pins |
| --- | --- |
| `prints each registration as it is created, not only in the final summary` | **the finding** - by source index: the per-item print is after the loop head and before the summary |
| `resolves every Graph role before creating anything` | the preflight precedes `New-MgApplication` |
| `looks up AppRoles in exactly one place` | the preflight and the loop cannot drift apart |
| `names what was created when it fails partway` | the catch reports the orphans |
| `rethrows after reporting, so a partial run is not reported as success` | the catch does not swallow |
| `still refuses before creating anything when a name already exists` | the pre-existing preflight survived |
| `supports -PlanOnly` | unchanged |
| `parses without syntax errors` | the file still loads |

| Mutation | Result |
| --- | --- |
| per-registration print removed (back to summary only) | FAIL - the ordering guard |
| role lookup moved back inside the creation loop | FAIL - the single-lookup guard |
| `throw` removed from the catch | FAIL - the rethrow guard |

Each confirmed applied before its run; source restored byte-identical (md5).

Gate: ASCII lint passed; `Invoke-Pester tests/ps` 236 passed / 0 failed; PSScriptAnalyzer 0
errors; the script parses clean. No .NET run - no C# or Razor changed.

## Known gaps

- **Nothing was run against a tenant.** No `Connect-MgGraph`, no registration created, no
  failure injected. That `$ErrorActionPreference = "Stop"` makes a Graph cmdlet failure
  terminating, and that a terminating error skips the post-loop summary, are PowerShell
  semantics read from the code rather than demonstrated.
- **No rollback.** A stranded registration is reported, not deleted. Deleting one
  automatically on failure is a destructive act against a tenant and a bigger decision than
  this finding carries - the operator is told precisely what exists instead.
- **Failures between creating the application and adding its secret still strand silently.**
  If `New-MgServicePrincipal` or the consent grant throws, the application exists but has no
  `$record` yet, so the catch cannot name it. Narrowing that window would mean recording the
  app id before the secret exists and printing a partial row; not done, and worth a second
  opinion.
- **The secret is printed to the console**, as it already was. Anything capturing that
  session's output captures the credential. Unchanged by this fix, and inherent to a script
  whose job is to hand a human a one-time secret.

## Closeout

Fixed directly on `master`. Independent review pending - and currently impossible: both
review harnesses are returning 401 (`.agents/state.md`). The third *Known gap* - the window
between creating an application and recording its secret - is the part most likely to be
judged insufficient.

## Independent review - `gemini-4-argon-high`, 2026-10-20

Reviewed as batch 3 of the backlog. Prompt kept at `.agents/review/prompt-backlog-3.md`.

**Any earlier claim in this record that review was impossible was wrong** - the probes had
been invoked incorrectly. `.agents/state.md` carries that correction.

**One MEDIUM defect, and a false claim of mine.** The record said that if
`New-MgApplication` succeeded and a later call threw, "the catch block and the existing
name-collision preflight together tell the operator what is there". **That was false.** The
catch inspects `$results`, which was only appended AFTER the secret existed - so a failure
in `New-MgServicePrincipal`, the consent grant or `Add-MgApplicationPassword` stranded a real
application and printed "No registrations were created."

The application is now recorded in `$results` the moment it exists, with its secret field
marked as never issued, and the completed row replaces it once the secret is in hand. An
Application Administrator without Privileged Role Administrator - who gets a 403 on the
consent grant - now sees exactly what was left behind.

Remediation landed in the follow-up commit, every fix mutation-proven.
