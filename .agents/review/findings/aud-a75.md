# aud-a75: ExecuteOnPrem's catch files a write-step failure as Failed under the "could not be confirmed" banner

**Severity**: MEDIUM - the aud-a73 defect on the on-premises path. `ExecuteOnPrem`'s catch (Mailbox
744-747, Calendar 687-690) records its failure row with `success: false`, so a throw at the write step
(reachable: `AddMailboxPermissionsOnPremAsync` calls `ConnectOnPrem` inside a try with only a `finally`; the
Calendar on-prem set and remove have no catch) is filed Failed while the banner says the outcome could not
be confirmed; the Undetermined filter does not find it.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (MEDIUM); folded into the aud-a73 agent's branch
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a73 agent, 2026-10-23, candidate 1 of its hand-back)

## Evidence

At `698881b8` plus the aud-a73 branch: `Components/Pages/MailboxPermissions.razor` 747 and
`Components/Pages/CalendarPermissions.razor` 690 (the `Audit.Log...(... false, ticket, errorDetail: failure)`
call inside `SelfGrantRefusalRecord.Banner`); `PermissionChangeFailure.AuditResultOf(step)` (aud-a73).

## Approach (to be written by the implementing agent before code)

- On the two lines, in place: pass `filedAs: PermissionChangeFailure.AuditResultOf(step)` so the write step
  files Undetermined and the other steps Failed; no lines added (808 and 762).
- Tests through `PermissionPagesSelfGrantTests`: an on-prem write that throws yields an Undetermined row; a
  throw before the write a Failed row. Mutation at the call site: `filedAs` removed on one page.

## Guard proof

Pending.
