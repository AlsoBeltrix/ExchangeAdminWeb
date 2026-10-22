# aud-a38i: An IIS node nested inside the mirrored tree under the SAME pool passes the serving-pool check and is still mirrored over

**Severity**: MEDIUM - production-served files inside the publish tree can be purged or replaced
by `robocopy /MIR` while the promotion or deploy reports success. The same pool avoids the
live-file corruption class (aud-a38f), not the content loss.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; in progress
**Branch**: `worktree-agent-ab28c8810a2e397d6` (forked at `1049f839`)
**Commit**: -
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

Pending.
