# aud-a71: A cloud permission write that throws is not audited on the Mailbox Permissions and Calendar Permissions pages

**Severity**: HIGH - a privileged access mutation can be unaudited. On both pages, when the cloud
single-submit write throws after the step is `PermissionChangeFailure.TheWrite`, the aud-a53 catch says
"Whether the change was made could not be confirmed" and updates the progress and the result, but writes
no `Audit.LogMailboxPermission` / `LogCalendarPermission` row; a permission write that may have applied
leaves no audit evidence (Known Failure Class 1 on the audit side). `ExecuteOnPrem`'s catch (aud-a59) does
write the row.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (HIGH); folded into the aud-a69 agent's scope; planned
**Branch**: `worktree-agent-ac701914283a170ea` (from master `15eb1449`)
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 (`.agents/decisions.md` 2026-10-21), within the 2026-10-23 stopping rule (HIGH). Folded into the
aud-a69 agent by the coordinator on 2026-10-23, the record taken from master `3a65ad62`. Branch
`worktree-agent-ac701914283a170ea` from master `15eb1449`: this record alone, then one fix commit
`fix(permission-pages): ... (aud-a71)`, first of three (aud-a69, then aud-a72, follow, each with its own plan commit);
the pages edited in place (808 and 762 lines, no registry entry moved); comments, if any, in one comments-only commit
after the three mutation rounds. Behaviour: the two pages' `SubmitSingle` catch (both module versions) and one new
constant in the shared `SelfGrantRefusalRecord` (base app version); the bumps are the supervisor's.

Evidence re-read at `15eb1449`:
- `MailboxPermissions.razor` `SubmitSingle`: `step` is `TheWrite` at 486 and the write is 487-489; the catch 526-530 logs
  the step, completes the activity and shows `PermissionChangeFailure.Banner(step, ex)`, and writes no row.
  `CalendarPermissions.razor` the same: `TheWrite` at 459, the write 460-471, the catch 503-507.
- What throws there is the pool: `MailboxPermissionService.AddMailboxPermissionsAsync` catches every per-right failure
  (`TryRight`) and `ExchangeServiceBase.RunAsync` everything inside its delegate, but `ExoConnectionPool.RunWithRetryCoreAsync`
  borrows outside its try, and `BorrowAsync` rethrows what refused or failed the borrow (the module off, the settings
  unreadable, the pool busy or stopped, the connect failing) as it was thrown. Such a throw reaches the catch with
  "Whether the change was made could not be confirmed" and no record.
- `ExecuteOnPrem`'s catch (Mailbox 742-748, Calendar 685-691, aud-a59) is the shape: the step banner, the step logged,
  and the result `SelfGrantRefusalRecord.Banner(failure, () => Audit.Log...(..., false, ticket, errorDetail: failure),
  Logger, target, FailedChangeRecord)` - a dropped or throwing row said, nothing escaping the handler. It records at every
  step, the one before the write included.

1. **The catch records, as `ExecuteOnPrem`'s does.** In place, on the catch's own two lines (Mailbox 528-529, Calendar
   505-506): `failure` is `PermissionChangeFailure.Banner(step, ex)`; the step is logged as now; the row is the success
   row's own - Mailbox `Audit.LogMailboxPermission(currentUser, clientIpAddress, "{Add|Remove}{permType}", target, user,
   permType, false, ticket, errorDetail: failure, extra: ...)`, Calendar `Audit.LogCalendarPermission(currentUser,
   clientIpAddress, "SetCalendar" | "RemoveCalendar", target, user, <the level, or none for a removal>, false, ticket,
   errorDetail: failure, extra: ...)` - with `success: false` and the step banner as its error, so at the write step the
   row says "Whether the change was made could not be confirmed"; written through `SelfGrantRefusalRecord.Banner(failure,
   ..., Logger, target, FailedSingleChangeRecord)`, whose banner is the result and the activity's completion text. The
   banner is a local: no handler reads `result` back (the ClickGateRegistry exemption of the banner's dismiss). The
   permission type and level come from the handler's entry snapshots (`isAdd`, `fullAccess`, `sendAs`; `isSet`, `access`),
   the try's own locals being out of scope in its catch. At every step, as `ExecuteOnPrem`: before the write the row says
   nothing was written; after it, beside the success row, that the change was written and a later step failed.
2. **The failure row carries what the success row would.** `extra` is the success row's -
   `ValidatedMailbox.WithWrittenIdentity(ProtectedPrincipalServicing.Extra(validation.ServicedNote), validation.Mailbox)`:
   the identity the write was bound to and, after a servicer override, the note (pgwt-7, `PermissionResult.WithServicedNote`:
   every outcome after an override reaches the audit with it - a write that failed or may have applied included).
3. **The log line names the record truly.** New `SelfGrantRefusalRecord.FailedSingleChangeRecord`, "the failed permission
   change": `FailedChangeRecord`'s words, "the failed on-premises change", would misname a cloud change in the warning a
   throwing row is logged with. `ExecuteOnPrem` keeps `FailedChangeRecord`.

Tests, `PermissionPagesSelfGrantTests`, each page (a Full Access or Reviewer grant to someone other than the operator): the
page's service over a test pool whose first call - the location read - drains it, and whose next connect throws an exception
of the test's own, its raw text unlike its captured PowerShell error; so the write's own borrow throws, as a refused borrow or
a lost connection does.
- `ACloudWriteThatThrows_IsRecordedAsFailed_WithTheBannersText`: the banner "Whether the change was made could not be
  confirmed: the write failed: <the captured error>. Check the permissions before trying again.", never the raw text; one row
  - `AddFullAccess` / `SetCalendar`, Failed, its error the banner, the written identity; the activity completed with the
  banner; nothing written; one warning, the step's, with the exception.
- `ACloudWriteThatThrows_WhoseRowTheWriterDrops_SaysTheRowCouldNotBeWritten`: the audit file held - the banner and "The audit
  row could not be written.", the activity completed with the same text, no row, one warning.
- `ACloudWriteThatThrows_WhoseRecordThrows_SaysSo_AndNothingEscapesTheHandler`: the writer missing - the same banner; two
  warnings, the second naming the failed permission change, with the record's exception.
- aud-a53's `AThrowAfterTheWrite_IsNamedAsSuch_InTheCausesOwnWords_NotItsRawText` gains its rows: the success row, and a
  failed row whose error is the after-the-write banner.

Mutations at the call site (md5, mutate, rebuild, filtered tests, failing names, restore from git, touch, rebuild, md5 equal,
green): M1 the audit call removed from the Mailbox Permissions catch (the bare step banner again); M1c the same on Calendar
Permissions. Extras: E1 the activity completed with the bare step banner (Mailbox Permissions); E2 `FailedChangeRecord` in
place of the new kind (Mailbox Permissions).

Files: `Components/Pages/MailboxPermissions.razor`, `Components/Pages/CalendarPermissions.razor` (in place),
`Services/SelfGrantRefusalRecord.cs`, `ExchangeAdminWeb.Tests/PermissionPagesSelfGrantTests.cs`, this record.

Caller-visible change, by design: a single change whose write - or a step before or after it - throws leaves a Failed row
carrying the banner the operator saw; when that row cannot be written the banner says so.

## Guard proof

Pending.
