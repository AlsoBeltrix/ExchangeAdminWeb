# aud-a38j: The cutover inherits the nested-node refusal although it never mirrors, and the plan lines still describe the old rule

**Severity**: LOW - two over-refusals and one stale description. `Move-ConfigDbToShared.ps1`
only stops pools and copies databases; it calls `Assert-AppPoolServesPath` (aud-a38g) and since
aud-a38i refuses any IIS node nested inside either instance's folder, although it would not
replace or purge those nodes' files. Deploy and promote also refuse nested nodes that sit inside
the `/XD` excluded folders (`logs`, `config`), which the mirror leaves alone. And the plan-mode
lines and comments in promote and the cutover ("refuse unless IIS serves that folder from this
pool and no other") describe aud-a38f's rule, not aud-a38i's; tests pin that text.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a38i/a7e agent, 2026-10-22, candidates 3 and 4 of its hand-back)

## Evidence

At the aud-a38i branch tip `f6b86a46`, `tools/AppPoolControl.psm1`, `Assert-AppPoolServesPath`:
one rule for all five callers (promote `:240`, deploy `:616` and `:929`, installer `:1090`, cutover
`:231`); no parameter distinguishes a caller that mirrors from one that does not, nor the mirror's
excluded subfolders.

## Approach (to be written by the implementing agent before code)

- Give `Assert-AppPoolServesPath` an explicit, mandatory choice for the nested-node rule - a
  parameter such as `-NestedNodes Refuse|Allow` with NO default, so every caller states what it
  does: deploy, promote and the installer pass `Refuse` (they mirror); the cutover passes `Allow`
  with a comment saying it copies databases only. The aud-a38f other-pool refusal and the
  "something must be AT the folder" rule apply to every caller regardless.
- Optionally `-ExcludedSubfolders` naming the mirror's `/XD` folders so nodes strictly inside them
  are not refused; take it only if the robocopy exclusion list is already a single shared value
  the module can be handed (one truth, pointers over copies); otherwise leave the over-refusal and
  record why.
- Update the plan-mode lines and comments in promote and the cutover to describe the rule each
  caller applies; update the Pester pins that hold that text.
- Pester: the cutover passes with a nested node under another pool? NO - the other-pool rule still
  refuses; the cutover passes with a nested node under the same pool; deploy and promote refuse it;
  a caller omitting the parameter fails binding. Mutation at the call site: the cutover passes
  `Refuse`; the parameter given a default. 5.1 `ParseFile` zero errors on every changed script and
  the module; the installer must never contain the string `deploy.ps1`; `Import-Module Pester
  -RequiredVersion 6.2.0` before the analyzer.

## Guard proof

Pending.
