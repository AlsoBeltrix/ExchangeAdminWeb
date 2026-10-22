# aud-a28p: The protected-principal check reads the legacy ExcludedUsers list fail-open after its corruption probe

**Severity**: MEDIUM - a fail-open read on the protection gate (Known Failure Class 3: an
authorization store must deny when a read fails). `ProtectedPrincipalService.GetLegacyExclusions`
reads the Mailbox Permissions module configuration's `ExcludedUsers` list after the corruption probe
has passed; a read failure between the probe and this read returns an empty list, so the principals
that list protects are un-protected for that check and a write against them goes through.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28n/a26k agent, 2026-10-22, candidate 1 of its hand-back)

## Evidence

At `3ef12e60`, `Services/ProtectedPrincipalService.cs` `GetLegacyExclusions` (the `ReadModuleConfig`
fail-open read, `ModuleConfigService.ReadModuleConfig` around 166-178, after `IsModuleCorrupt`).
Compare the fail-closed shape every other read on this gate uses since aud-a3b/a3c.

## Approach (to be written by the implementing agent before code)

- Read the list through a `Try...` read that distinguishes unreadable from empty (aud-a26k's
  `TryGetModuleConfig`), and treat unreadable as a failed check: the protection gate refuses the
  write (audited `refusedAt: ProtectionRead`, `mutationAttempted: false`), never as "no exclusions".
  Make the probe and the read one read where possible (one fresh read serving both, aud-a28f/a28i
  shape) so there is no window between them.
- Tests through `ProtectedGroupMembershipTests` / the protected-principal tests: a store that fails
  between the probe and the list read refuses the check; an empty list with a healthy store still
  passes; a corrupt store still refuses. Mutation at the call site: read fail-open again.

## Guard proof

Pending.
