# aud-a38i: An IIS node nested inside the mirrored tree under the SAME pool passes the serving-pool check and is still mirrored over

**Severity**: MEDIUM - production-served files inside the publish tree can be purged or replaced
by `robocopy /MIR` while the promotion or deploy reports success. The same pool avoids the
live-file corruption class (aud-a38f), not the content loss.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
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

## Guard proof

Pending.
