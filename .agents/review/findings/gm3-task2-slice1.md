# gm3-task2-slice1: list-time member-write eligibility (GM-3 task 2, slice 1)

**Severity**: n/a — slice-landing review (not a reviewer-raised defect)
**Status**: Verified (accepted, no material issue)
**Branch**: none (committed directly to master per repo policy)
**Commit**: `1a0cf58dd31fa3bd77e648a603ecdd435c6cb3c6`

## Scope
Whole-slice review of `git diff 296c3e6..1a0cf58`:
- `Services/SelfServiceGroups/GroupMembershipAce.cs` — new pure ACE classifier.
- `Services/SelfServiceGroups/SelfServiceGroupService.cs` — list-time DACL read wiring.
- `ExchangeAdminWeb.Tests/GroupMembershipAceTests.cs` — 8 classifier tests.

## Intent reviewed against (plan §6.3)
1. Rights-bit classification, never ObjectType-name (Self-on-member trap).
2. Fail-closed exclusion on unreadable DACL / Deny wins.
3. Credential isolation (named AD drive bound to the module credential, not process identity).
4. Ownership is not authorization (CanManageMembers true only on DACL pass).

## Guard proof
- `GroupMembershipAceTests` (8 tests). Reverting `ConveysMemberWrite` rights-bit logic to
  a naive GUID-only check fails 5 tests incl. `Self_on_member_attribute_does_not_convey_member_write`;
  restoring passes all 8. Verified locally (coder) AND independently in the reviewer's own
  worktree (guard_confirmed: true).

## Reviewer comments
`Reviewer: codex / gpt-5.5-dzs / xhigh / standard` — codex-cli 0.145.0, transport cli.
- reviewed_sha: `1a0cf58dd31fa3bd77e648a603ecdd435c6cb3c6`
- base_sha: `296c3e6dcee409dba8e749011309a7629c694f08`
- guard_confirmed: true
- verdict: **accepted**
- timestamp: 2026-07-24T17:47Z
- comments: (none — no material issue found)

Note on dispatch grammar: owner ruled "use codex at its default, do not specify a model
or effort" (2026-07-24). Codex's own configured default (gpt-5.5-dzs / xhigh) was used as
dispatched; no `-m`/effort flag was passed. This satisfies the playbook's tier intent
(a standard, owner-directed dispatch) via the owner's explicit instruction rather than a
machine-local cache entry.
