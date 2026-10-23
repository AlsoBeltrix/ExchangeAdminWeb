# aud-a76: A permission write that fails before the permission command was submitted is filed Undetermined, as if its outcome were unknown

**Severity**: MEDIUM - audit semantics and operator triage. Since aud-a73/a75 the pages set the step to
`TheWrite` before calling the service, so a failure that happens BEFORE the permission cmdlet runs - the
cloud pool refusing the borrow (Exchange Online disabled, config unreadable, pool busy or stopped, the
connect failing) or the on-premises `ConnectOnPrem` failing before any `Invoke-Command` - is filed
`Undetermined` with "Whether the change was made could not be confirmed", though nothing was submitted and
nothing could have applied. The Admin Event Log then hides a definite no-write failure from the Failed
filter and sends the operator into needless reconciliation. The aud-a75 test even asserts no
`Invoke-Command` while expecting Undetermined.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (MEDIUM); not started
**Branch**: -
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (perm-batch-9, 2026-10-23)

## Evidence

At `8c6b908c`: `Components/Pages/MailboxPermissions.razor` 486 and 706, `Components/Pages/CalendarPermissions.razor`
459 and 659 (the step set to `TheWrite` before the service call); `Services/MailboxPermissionService.cs` and
`Services/CalendarPermissionService.cs` (the borrow / `ConnectOnPrem` before the cmdlet; `TryRight` /
`TryRightOnPrem`); `ExchangeAdminWeb.Tests/PermissionPagesSelfGrantTests.cs` (`AnOnPremWriteThatThrows_...`
asserting no `Invoke-Command` with Undetermined).

## Approach (to be written by the implementing agent before code)

- The services distinguish a pre-command failure from a submitted-write failure: a borrow or connect that
  fails before the permission cmdlet is reached throws (or returns) a typed pre-write failure
  (`PermissionWriteNotSubmittedException` or a result flag), and the pages file it `Failed` with "Nothing
  was written: <cause>"; `Undetermined` is reserved for a failure after the cmdlet was submitted and the
  outcome is unknown. Where the service already returns a failed result for a refused right
  (`TryRight`/`TryRightOnPrem`), nothing changes.
- Tests: Exchange Online disabled, config unreadable, pool busy, on-prem connect failing - each Failed with
  "Nothing was written" on both pages; a throw from the cmdlet itself still Undetermined. The aud-a75
  on-prem test is retargeted to a cmdlet-level throw for its Undetermined case. Mutation at the call site:
  the pre-write failure filed through `AuditResultOf(TheWrite)` again.

## Guard proof

Pending.
