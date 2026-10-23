# aud-a73: A cloud permission write whose outcome is unknown is filed as Failed, so the event log's Undetermined filter cannot find it

**Severity**: MEDIUM - audit result bucket misleading for privileged-access reconciliation. The aud-a71 row
for a write that threw at `PermissionChangeFailure.TheWrite` carries the banner "Whether the change was made
could not be confirmed..." but is written with `success: false`, so `AuditService.LogMailboxPermission` /
`LogCalendarPermission` file it as `result: "Failed"`; the Admin Event Log's Undetermined filter (aud-a28z
part 5) does not find it, and a reviewer reconciling Exchange may treat a possibly-applied change as a
failed one.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (MEDIUM); not started
**Branch**: -
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (perm-batch-8, 2026-10-23, candidate 1)

## Evidence

At `c78de758`: `Components/Pages/MailboxPermissions.razor` around 529 and `Components/Pages/CalendarPermissions.razor`
around 506 (the catch passing `success: false`); `Services/AuditService.cs` around 71 (`LogMailboxPermission`'s
result from the bool) and `LogCalendarPermission`; `LogMigrationCheck`'s three-way result (aud-a28z part 5)
and the event log's Result filter.

## Approach (to be written by the implementing agent before code)

- The two permission audit methods accept an explicit result (`Success`, `Failed`, `Undetermined`) beside
  the bool they take today (an overload or an optional parameter, so no other caller changes); the pages
  pass `Undetermined` for the write step's "could not be confirmed" row and `Failed` for the steps before
  and after the write; the banner text, written identity, servicer note and error detail stay.
- Tests through `PermissionPagesSelfGrantTests` (the write-step row's result is Undetermined; a
  before-the-write row is Failed) and the event log filter test gains the permission row; mutation at the
  call site: `success: false` again at the write step.

## Guard proof

Pending.
