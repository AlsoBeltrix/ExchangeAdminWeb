# aud-a71: A cloud permission write that throws is not audited on the Mailbox Permissions and Calendar Permissions pages

**Severity**: HIGH - a privileged access mutation can be unaudited. On both pages, when the cloud
single-submit write throws after the step is `PermissionChangeFailure.TheWrite`, the aud-a53 catch says
"Whether the change was made could not be confirmed" and updates the progress and the result, but writes
no `Audit.LogMailboxPermission` / `LogCalendarPermission` row; a permission write that may have applied
leaves no audit evidence (Known Failure Class 1 on the audit side). `ExecuteOnPrem`'s catch (aud-a59) does
write the row.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (HIGH); folded into the aud-a69 agent's scope
**Branch**: -
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (perm-batch-7, 2026-10-23, candidate 1)

## Evidence

At `105a129f`: `Components/Pages/MailboxPermissions.razor` around 486 and `Components/Pages/CalendarPermissions.razor`
around 459 (the `SubmitSingle` catch after `step = TheWrite`); the `ExecuteOnPrem` catch the aud-a59 fix gave the
row-writing shape (Mailbox around 744, Calendar around 687).

## Approach (to be written by the implementing agent before code)

- The `SubmitSingle` catch mirrors `ExecuteOnPrem`'s: compute the failure banner (`PermissionChangeFailure.Banner`),
  write a Failed permission audit row carrying that same text (success false, the step and the cause), through
  `SelfGrantRefusalRecord.Banner(..., FailedChangeRecord)` so a dropped row is said and nothing escapes; the
  row's result states "could not be confirmed" for the write step so a reviewer knows Exchange may have
  applied it. Same-line edits where possible; the pages are line-keyed (808 and 762).
- Tests through `PermissionPagesSelfGrantTests`: a cloud write that throws yields one Failed row with the
  banner text, on both pages; a dropped row is said. Mutation at the call site: the audit call removed from
  the catch.

## Guard proof

Pending.
