# aud-a38j: The cutover inherits the nested-node refusal although it never mirrors, and the plan lines still describe the old rule

**Severity**: LOW - two over-refusals and one stale description. `Move-ConfigDbToShared.ps1`
only stops pools and copies databases; it calls `Assert-AppPoolServesPath` (aud-a38g) and since
aud-a38i refuses any IIS node nested inside either instance's folder, although it would not
replace or purge those nodes' files. Deploy and promote also refuse nested nodes that sit inside
the `/XD` excluded folders (`logs`, `config`), which the mirror leaves alone. And the plan-mode
lines and comments in promote and the cutover ("refuse unless IIS serves that folder from this
pool and no other") describe aud-a38f's rule, not aud-a38i's; tests pin that text.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; plan written, in progress
**Branch**: `worktree-agent-aeb07a6371bf7b965` (forked at `36fb150d`)
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); this record was admitted under it.
Ops-script-only: no version bump (the `aud-a34`/`a35`/`a36`/`a38`/`a38c`/`a38f`/`a38i` precedent).
Written before the code.

1. **`tools/AppPoolControl.psm1`, `Assert-AppPoolServesPath -NestedNodes Refuse|Allow`.** Mandatory,
   `ValidateSet('Refuse', 'Allow')`, no default: a caller that omits it fails binding before IIS is
   read, so every caller states the rule it needs.
   - `Refuse` - the `aud-a38i` rule, unchanged: the folder's own nodes run under the named pool, and
     any node rooted strictly inside the folder is refused whatever its pool.
   - `Allow` - the `aud-a38f` rule: every node at or inside the folder runs under the named pool, so
     a nested node under the named pool passes and one under another pool is refused.
   - Both: unreadable IIS refuses; nothing AT the folder refuses; the folder's own nodes under
     another pool refuse. Every `aud-a38f` mechanism is kept (boundary match, virtual-directory read,
     missed-read guard). A pass returns what IIS serves at or inside the folder (under `Refuse` that
     can only be what is at it). Under `Allow` the other-pool refusal says "at or under" and drops
     the mirror clause ("while its files are replaced"); under `Refuse` every message is unchanged.
2. **Callers.** `deploy.ps1` (upgrade `:616`, `-Force` `:929`), `tools/promote-dev-to-prod.ps1`
   (`:240`, `Stop-AppPoolChecked`) and `tools/Install-ExchangeAdminWeb.ps1` (`:1090`) pass
   `-NestedNodes Refuse`: each then mirrors the folder with `robocopy /MIR`.
   `tools/Move-ConfigDbToShared.ps1` (`:231`, `Assert-PoolServesInstance`) passes
   `-NestedNodes Allow`, with a comment saying why: it stops pools, copies databases (backups out to
   `-BackupRoot`, dev's database to the shared path, which must lie outside both folders) and
   rewrites each instance's `appsettings.json`, and never mirrors - nothing of a nested node is
   replaced or purged, and stopping the named pool stops a same-pool node with it. A nested node
   under another pool keeps a worker serving from inside the instance's folder while its database
   is copied, so it is still refused. Every call stays `$null = ...`, inside no catch that swallows.
3. **Text.** The plan-mode lines and comments in promote and the cutover, the installer's step
   description (same `aud-a38f` wording, "and no other"), and the call-site comments in `deploy.ps1`
   and the installer describe the rule each caller applies. Pure ASCII, nothing PS7-only
   (Invariant 6).
4. **`-ExcludedSubfolders`: not taken.** The mirror's exclusion list is not one shared value: it is
   a literal in each robocopy argument list - `deploy.ps1` twice, the promotion's mirror and its
   rollback, the installer - and each copy is pinned by its own test. Handing the module the list
   would make a sixth copy, which is what the condition (one truth, pointers over copies) rules
   out. The over-refusal of a node inside an excluded folder stays, recorded under Known gaps.
5. **Tests.**
   - `tests/ps/AppPoolControl.Tests.ps1`: every existing call passes `-NestedNodes Refuse` (they pin
     the mirroring callers' rule). A new `aud-a38j` context: omitting the parameter fails binding,
     in a hostless runspace (where a missing mandatory parameter is an error, not a prompt), and the
     probe is never asked; the declaration is mandatory, exactly the two values, no default (AST);
     `Allow` passes an application and a virtual directory nested under the named pool and returns
     them; `Allow` still refuses a nested node under another pool, a folder another pool serves,
     nothing at the folder, and unreadable IIS; through the default probe, `Allow` passes an
     application's own virtual directory inside the folder. The promotion's lifted stop step, over
     the real module, refuses a same-pool nested node before anything is stopped. The plan-line pin
     becomes exact; the call-text pin includes `-NestedNodes Refuse`.
   - `tests/ps/DeployInvariants.Tests.ps1`: the cdt-28 call pin includes `-NestedNodes Refuse`; its
     stub records the value it is handed; a new test runs each branch's real region over the real
     module: a same-pool nested node is refused before the stop.
   - `tests/ps/InstallerConfigBackup.Tests.ps1`: the cdt-32 step-text and call pins; the stub records
     the value.
   - `tests/ps/MoveConfigDbToShared.Tests.ps1`: the call pin (`-NestedNodes Allow`) and the plan-line
     pins; the lifted wrapper over the real module passes a same-pool nested node and still refuses
     an other-pool one.
6. **Mutations at the call site**, each with md5 before and after, the failing test named, restore,
   md5 re-confirmed, touch, rerun green: the cutover passes `Refuse`; the parameter is given a
   default (made optional with one, and - inert at run time - added while kept mandatory). Also: the
   module ignores `-NestedNodes` (either way); `Allow` narrowed to the folder's own nodes; promote
   and deploy pass `Allow`; the cutover's plan line reverted.
7. **Gates**: Pester 6.2.0 imported before PSScriptAnalyzer; `-Severity Error` per changed file;
   `Invoke-Pester tests/ps`; Windows PowerShell 5.1 `ParseFile` on the module and the five calling
   scripts; the installer never contains `deploy.ps1`; `git diff --check HEAD`; ASCII lint.

## Guard proof

Pending.
