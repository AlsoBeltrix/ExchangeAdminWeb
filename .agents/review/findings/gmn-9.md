# gmn-9: Audit records discard the immutable identity used for the write

**Severity**: MEDIUM - two same-named objects from different domains produce
indistinguishable audit records, and failed attempts cannot be tied to the selected
GUID/DN.
**Status**: In progress
**Branch**: `-` (default-branch mode)
**Commit**: the commit that adds this record

Reviewer-raised (generation pass over `3f2ab21..45b95e9`).

## Evidence
`Components/Pages/GroupManagement.razor` - the add path audits only `newMember` (a SAM by
default) while the service writes the held DN; the remove path includes
`memberObjectGuid` only on the normal-result branch - ticket-denial, auth-denial, and
exception audits omit it.

## Predicted observable failure
Adding either of two same-SAM groups from different forest domains yields identical
success audits though different objects were written; a failed removal of a mail-less
member leaves no record of which object was targeted.

## What
The immutable identity travelled to the service but not into every audit branch.

## Approach
The snapshots gmn-7 introduced carry the selected DN (add) and the GUID + DN (remove)
into EVERY audit branch - ticket denial, authorization denial, success/failure, and
exception - as `memberDn` / `memberObjectGuid` extras; the display identity stays as the
human-readable `member` field.

## Files changed
- `Components/Pages/GroupManagement.razor` - audit extras on all branches of AddMember and
  RemoveMember
- `ExchangeAdminWeb.Tests/GroupMemberNestingProtectionTests.cs` - per-branch audit-extra
  tripwires

## Guard proof
Removing the DN extra from any audited branch makes the new tripwire FAIL; restoring makes
it PASS.

## Coder dispute (if any)
None - admitted as written.

## Known gaps
Self-service's list-removal audits already carry `memberObjectGuid` on every branch (S4);
only the admin page had the gap.

## Reviewer comments
Reviewer: codex / gpt-5.6-sol / xhigh / standard (inline, session-only)
codex-cli 0.147.0, reviewed 45b95e901189addc4e60df403f019362b8089619, base
3f2ab2191399e07de02e3c71cdb1724423df4e07, capability_ok true, verdict findings (4 of 4),
2026-08-27 UTC. Verification round pending.
