# aud-a71: A cloud permission write that throws is not audited on the Mailbox Permissions and Calendar Permissions pages

**Severity**: HIGH - a privileged access mutation can be unaudited. On both pages, when the cloud
single-submit write throws after the step is `PermissionChangeFailure.TheWrite`, the aud-a53 catch says
"Whether the change was made could not be confirmed" and updates the progress and the result, but writes
no `Audit.LogMailboxPermission` / `LogCalendarPermission` row; a permission write that may have applied
leaves no audit evidence (Known Failure Class 1 on the audit side). `ExecuteOnPrem`'s catch (aud-a59) does
write the row.
**Status**: Complete; merged `6a971048`; codex perm-batch-8 raised one MEDIUM (admitted as aud-a73) and two LOWs (held in aud-a70)
**Branch**: `worktree-agent-ac701914283a170ea` (from master `15eb1449`)
**Commit**: plan `a8fdcbf5`; fix `bc1183ab`; the census follow-up `9d3697ee`
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
`Services/SelfGrantRefusalRecord.cs`, `ExchangeAdminWeb.Tests/PermissionPagesSelfGrantTests.cs`, this record; and, as the full suite showed (Guard proof),
`ExchangeAdminWeb.Tests/ValidatedWriteCallSiteTests.cs`.

Caller-visible change, by design: a single change whose write - or a step before or after it - throws leaves a Failed row
carrying the banner the operator saw; when that row cannot be written the banner says so.

## Fix

- `Components/Pages/MailboxPermissions.razor` 528-529 and `Components/Pages/CalendarPermissions.razor` 505-506, in place (808
  and 762 lines, no registry entry moved): the catch has `failure = PermissionChangeFailure.Banner(step, ex)`, logs the step as
  before, and writes the change's row through `SelfGrantRefusalRecord.Banner(failure, () => Audit.Log...(...),
  Logger, target, SelfGrantRefusalRecord.FailedSingleChangeRecord)` - Mailbox `"{Add|Remove}{permType}"` and the permission
  type rebuilt from the `fullAccess` and `sendAs` snapshots, Calendar `"SetCalendar"` / `"RemoveCalendar"` and the level
  (`isSet ? access.ToString() : null`); `success: false`; `errorDetail: failure`; `extra` the success row's,
  `ValidatedMailbox.WithWrittenIdentity(ProtectedPrincipalServicing.Extra(validation.ServicedNote), validation.Mailbox)`. The
  banner it returns, a local, is the result and the activity's completion text.
- `Services/SelfGrantRefusalRecord.cs`: `FailedSingleChangeRecord`, "the failed permission change".

Tests: `PermissionPagesSelfGrantTests` +6 and one strengthened. The drivers take the pool the page's own service borrows from;
`PoolWhoseWriteBorrowThrows` is a pool over the harness's stand-ins and settings whose first call (the location read) drains
it, through a call list that records into the harness's (`CallsThen`), and whose next connect throws the test's exception, so
the write's own borrow throws. `ACloudWriteThatThrows_IsRecordedAsFailed_WithTheBannersText` (each page: the banner in the
captured words, never the raw text; one Failed row with the action, the permission type or level, the banner as its error, the
target, the grantee and the written identity; the activity completed with the banner; the location read made, nothing
written; one warning), `ACloudWriteThatThrows_WhoseRowTheWriterDrops_SaysTheRowCouldNotBeWritten` (each page, the audit file
held: the banner and the sentence, the activity completed with the same, no row, one warning),
`ACloudWriteThatThrows_WhoseRecordThrows_SaysSo_AndNothingEscapesTheHandler` (each page, the writer missing: the banner and the
sentence; the step's warning and one naming the failed permission change, with the NullReferenceException).
`AThrowAfterTheWrite_IsNamedAsSuch_InTheCausesOwnWords_NotItsRawText` (aud-a53) now also pins the rows: the success row and a
failed row of the same action whose error is the after-the-write banner.

## Guard proof

Session-local driver `mutate.ps1` (session scratchpad, log `a71-mutations.log`), the aud-a60 protocol: the fix staged; per
mutant the MD5 of each file it touches, the edit (an exact-text replacement that must match exactly once), the MD5 again, a
Debug build of the test project with 0 errors required, `dotnet test --no-build` filtered to
`FullyQualifiedName~PermissionPagesSelfGrant|FullyQualifiedName~ClickGate|FullyQualifiedName~ProgressRegistry|FullyQualifiedName~PermissionValidator|FullyQualifiedName~MailboxPermission|FullyQualifiedName~CalendarPermission|FullyQualifiedName~ExchangeGroupMembers|FullyQualifiedName~BulkPermission|FullyQualifiedName~BulkAndOnPrem|FullyQualifiedName~AuditWriterThrowFree`
(781 tests), the failing names taken from the run, then the file restored from the git index (`git checkout --`), its write
time advanced so MSBuild rebuilds it, and its MD5 checked equal to the one before. Fixed-file MD5s:
`Components/Pages/MailboxPermissions.razor` `B97013967CCC1D91823B031C10A8FE71`, `Components/Pages/CalendarPermissions.razor`
`F4E90B08DE8782CA0E890BBE8157229B` (unmutated: `Services/SelfGrantRefusalRecord.cs` `219E6B641F0E28A6BF34308721D395A2`,
`ExchangeAdminWeb.Tests/PermissionPagesSelfGrantTests.cs` `44DF7D3A382EDD03C395B281B8C05F2B`). Every mutant built with 0
errors and every restore matched; each kept its page's line count, so ClickGate and ProgressRegistry ran under it and passed.
After the round, the restored tree rebuilt and the filter: 781/781 green.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 | the audit call removed from the Mailbox Permissions catch (the bare step banner again) | `MailboxPermissions.razor` `ABF38BFF1A0AE14E380B7168A15DA05B` | 4 of 781, each `(page: "Mailbox Permissions")`: `ACloudWriteThatThrows_IsRecordedAsFailed_WithTheBannersText` (no row), `ACloudWriteThatThrows_WhoseRowTheWriterDrops_SaysTheRowCouldNotBeWritten` and `ACloudWriteThatThrows_WhoseRecordThrows_SaysSo_AndNothingEscapesTheHandler` (the dropped row unsaid), `AThrowAfterTheWrite_IsNamedAsSuch_InTheCausesOwnWords_NotItsRawText` (one row, not two) |
| M1c | the same on Calendar Permissions | `CalendarPermissions.razor` `A8B29D32C0D40B683C3B37B623008B39` | 4 of 781: the same four tests' `(page: "Calendar Permissions")` rows |
| E1 | extra: the activity completed with the bare step banner (Mailbox Permissions) | `2D203BBD2A90A9C3ECB0E6E392DB5830` | 1 of 781: `ACloudWriteThatThrows_WhoseRowTheWriterDrops_SaysTheRowCouldNotBeWritten(page: "Mailbox Permissions")` |
| E2 | extra: `FailedChangeRecord` in place of the new kind (Mailbox Permissions) | `3C682CDCBF84018733DAC7171A74C886` | 1 of 781: `ACloudWriteThatThrows_WhoseRecordThrows_SaysSo_AndNothingEscapesTheHandler(page: "Mailbox Permissions")` - the warning names the on-premises change |

No separate pre-fix reproduction: M1 and M1c are each catch as it stood, at its call site.

Follow-up, found by the full suite on the fix commit `bc1183ab` (6,546 passed / 2 failed): `ValidatedWriteCallSiteTests.EveryPageWriteAudit_RecordsTheIdentityWritten`
counts each page's `ValidatedMailbox.WithWrittenIdentity(` sites - 2 on each permission page - and the failure row is a third,
deliberately (plan item 2). Its two rows now expect 3, with the reason in the test, in a second commit for this finding (no
amend). M1 and M1c were then run again with that test in the filter (800 tests), same mutant MD5s, every restore matching:
each fails 5 of 800 - the four named above and `EveryPageWriteAudit_RecordsTheIdentityWritten(page: "MailboxPermissions.razor",
expected: 3)` / `(page: "CalendarPermissions.razor", expected: 3)`. The restored tree: 800/800 green.

## Known gaps

- The tests reach the catch at the write through the one throw a cloud write has there, the pool's borrow; a delegate that
  threw past its own catches would reach the same catch and the same record.
- `ExecuteOnPrem`'s failure row (aud-a59) carries neither the serviced note nor the written identity in `extra`, which its
  own success rows carry (its `onPremExtra` is a local of the try): a candidate in the hand-back, not changed here.
- A throw before the write in `SubmitSingle` (the on-premises authorization check, before the confirmation) is now recorded as
  a failed change of the cloud action's name, as `ExecuteOnPrem` records its own; the location read's refusal and the
  "Unknown" location still return unrecorded, as before.
- Nothing ran against Exchange: the stand-in cmdlets behind a test pool, the harness's validator, a substitute progress
  service.

## Comments after the round

None for this record's files: its comments were written with the fix. The branch's comments commit `a9ce01f5` touches only
`Services/PermissionValidator.cs` (aud-a69, aud-a72).

## Gates - 2026-10-23 (branch tip `a9ce01f5`: the three fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: exit 0, 0 errors; 19 warnings - the baseline - none in a
  touched file (they are in GroupManagement, MessageTrace, RiskyUsers and SelfServiceGroups, Comms10kApprovedSetTests,
  AccountLockoutRemediationService, HeaderAnalysisService and MigrationReportExportProcessor).
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0, 6,569
  passed / 0 skipped / 0 failed, 6,569 total, 3m41s; nothing hung (the blame collector wrote no Sequence file). On the way:
  6,546 passed / 2 failed on `bc1183ab` (the census, `9d3697ee`), 6,560 / 0 on `828fc33a`.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0. `git add -A`
  then `tools/Test-AsciiOnly.ps1`: exit 0 ("ASCII lint passed"). The pages: 808 and 762 lines (`wc -l`).

No AD, Exchange Online, Graph, DHCP or IIS call: the directory is `SelfGrantDirectoryStandIn`, Exchange the harness's pool or a
test pool over the harness's stand-in cmdlets, or a stand-in resolver, the secret store a stand-in fetch. No coder self-review
verdict is claimed.

## Closeout

Fixed on branch `worktree-agent-ac701914283a170ea`, not merged, not pushed. Due at merge: the base app version - the shared
`SelfGrantRefusalRecord` (`FailedSingleChangeRecord`) - and the Mailbox Permissions and Calendar Permissions module versions:
what the single change's catch records and says.
