# aud-a59: Permission pages - the protected-target denial never says its audit row was not written, ExecuteOnPrem's catch shows a raw exception, a blank-Target bulk row is filed under the protected-target action, and the harness stub answers group members by default

**Severity**: LOW - the aud-a50 leftovers. (1) The protected-target denial's record ignores the aud-a28u
write outcome and a throw is only logged, so the banner never says the row was not written (in
`ExecuteOnPrem` the record is inside no try at all). (2) `ExecuteOnPrem`'s catch audits and shows the raw
`ex.Message`. (3) A bulk row with an empty Target is recorded as `Bulk*_ExcludedTarget`, the protected-target
action name (the aud-a50 part-3 class for the Target column). (4) Test hygiene: `ExchangeWriteHarness.ExchangeKnows`
leaves `ReadGroupMembersAsync` to NSubstitute's default empty list, so a future cloud-only group test that
forgets to set up members passes as allowed.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a50/a52 agent, 2026-10-22, candidates 1, 3, 4 and 5 of its hand-back)

## Evidence

At `81809833`: `Components/Pages/MailboxPermissions.razor` 409-425, 696-703, 744-747;
`Components/Pages/CalendarPermissions.razor` 387-402, 650-657, 687-690 (both line-keyed by
`ClickGateRegistry`, 808 and 762 lines, edit in place only); `Services/MailboxPermissionService.cs` 283-300;
`Services/CalendarPermissionService.cs` 164-181; `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs` 518-525.

## Approach (to be written by the implementing agent before code)

- The protected-target denial records through `SelfGrantRefusalRecord.Banner`'s shape (outcome-aware, guarded),
  on both paths.
- `ExecuteOnPrem`'s catch reports through the aud-a23i error-text helper, no raw message; the audit row
  carries the same text.
- A blank Target is an invalid row (`Bulk*_NoTarget`, "No target specified (the Target column is empty)")
  before any check.
- `ExchangeKnows` makes `ReadGroupMembersAsync` throw unless the test sets members, so an unset membership
  fails as incomplete.
- Tests through `PermissionPagesSelfGrantTests` and the bulk harness; mutations at the call site: the
  outcome ignored at the denial; raw `ex.Message` again; the blank-Target check removed; the stub default
  again.

## Guard proof

Pending.
