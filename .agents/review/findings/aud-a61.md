# aud-a61: Self-grant check - bulk grants re-read a large cloud group once per row, the 50-group nesting bound refuses valid grants, and the write-time refusal does not say the earlier check passed

**Severity**: MEDIUM (part 1), LOW (parts 2 and 3). (1) Each bulk row calls `ValidateSelfGrantAsync`, which
calls `ReadGroupMembersAsync`, which runs `Get-UnifiedGroupLinks` / `Get-DistributionGroupMember -ResultSize
Unlimited`; a 200-row upload granting one large cloud group does 200 full traversals and courts throttling.
(2) `ExchangeGroupMembers` throws past 50 nested cloud groups, so a grant to a group with more than 50 nested
groups that does not contain the operator is refused as incomplete. (3) The aud-a50 write-time re-check
refusal shows the generic refusal and records the same `*_SelfGrantAttempt` action as the pre-confirmation
check, so the operator cannot tell that the first check passed and the state changed in between.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (perm-batch-6, 2026-10-22, candidates 3, 4 and 5)

## Evidence

At `ea5e1a72`: `Services/MailboxPermissionService.cs` around 301 and `Services/CalendarPermissionService.cs`
(the per-row call); `Services/ExchangeGroupMembers.cs` around 71 (the bound); `Components/Pages/MailboxPermissions.razor`
704 and `Components/Pages/CalendarPermissions.razor` 658 (the write-time re-check; both pages line-keyed by
`ClickGateRegistry`, 808 and 762 lines, edit in place only).

## Approach (to be written by the implementing agent before code)

- An operation-scoped membership cache (keyed by operator and grantee group, holding results AND failures)
  handed to the validator by the bulk loops for the run (the aud-a23r `DirectoryLookupRun` shape); the
  single-grant path uses a fresh one.
- The nesting bound is a named constant reported specifically ("more than N nested groups; the check was
  not completed") and raised to a value that fits real tenants (document the choice); a traversal that is
  cut off is still a refusal, never an allow.
- The write-time refusal appends "the check before confirmation had passed; the state changed" and records
  a distinct audit action (`*_SelfGrantAttemptAtWrite`); same-line edits only.
- Tests through the bulk harness (one traversal for N rows), `ExchangeGroupMembersTests` (the bound text),
  `PermissionPagesSelfGrantTests` (the write-time wording and action). Mutations at the call site: the cache
  bypassed; the generic bound text again; the shared action again.

## Guard proof

Pending.
