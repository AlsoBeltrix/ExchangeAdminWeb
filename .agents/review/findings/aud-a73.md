# aud-a73: A cloud permission write whose outcome is unknown is filed as Failed, so the event log's Undetermined filter cannot find it

**Severity**: MEDIUM - audit result bucket misleading for privileged-access reconciliation. The aud-a71 row
for a write that threw at `PermissionChangeFailure.TheWrite` carries the banner "Whether the change was made
could not be confirmed..." but is written with `success: false`, so `AuditService.LogMailboxPermission` /
`LogCalendarPermission` file it as `result: "Failed"`; the Admin Event Log's Undetermined filter (aud-a28z
part 5) does not find it, and a reviewer reconciling Exchange may treat a possibly-applied change as a
failed one.
**Status**: Merged `39afd9a1`; closed on guard proof; codex review pending
**Branch**: `worktree-agent-a22f2baf02d6abcdd` (from master `066ea1a2`)
**Commit**: plan `f224dd22`; fix `75355a7b`
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 (`.agents/decisions.md` 2026-10-21), within the 2026-10-23 stopping rule (MEDIUM). Branch
`worktree-agent-a22f2baf02d6abcdd` from master `066ea1a2`: this record alone, then one fix commit
`fix(permission-pages,audit): ... (aud-a73)`; the pages edited in place (808 and 762 lines, no registry entry moved);
comments with the fix, or after the mutation round in a comments-only commit. Behaviour: the two pages' `SubmitSingle`
catch (both module versions) and the shared audit service, a new shared result type and `PermissionChangeFailure` (base
app version); the bumps are the supervisor's.

Evidence re-read at `066ea1a2`:
- `Components/Pages/MailboxPermissions.razor` 528-529 and `Components/Pages/CalendarPermissions.razor` 505-506: the catch
  writes the change's row through `SelfGrantRefusalRecord.Banner(failure, () => Audit.Log...(..., false, ticket,
  errorDetail: failure, extra: ...), Logger, target, FailedSingleChangeRecord)` at every step; at
  `PermissionChangeFailure.TheWrite` the banner, and so the row's error, is "Whether the change was made could not be
  confirmed: ...".
- `Services/AuditService.cs` `LogMailboxPermission` (51-83) and `LogCalendarPermission` (86-116) file
  `["result"] = success ? "Success" : "Failed"` and `["error"] = success ? null : errorDetail`. `extra`, merged last,
  could already put any text under "result"; that is a free-text override of a computed field at two call sites, not the
  explicit result the Approach names.
- `LogMigrationCheck` (118-146) files a three-way result since aud-a28z part 5; the Admin Event Log's Result filter
  offers Undetermined (`AdminEventLog.razor` 96) and matches a row's result exactly (996), and its badge shows any result
  but Success and Failed as a warning (284). Nothing to change there.
- The other readers of a row's result: `ADAttributeEditorUndoService.CanUndo` (its own category only), the writer's
  dropped-row report (the text), and `WriteAuditEvent`'s `IsSuccessfulAuditResult` - any result but "Failed" is a success
  for the implicit trace operation's completion and for the usage count.
- `AuditWriterThrowFreeTests` pins 13 public `AuditService` methods (15 taking a dictionary) and builds an argument for
  each parameter type it knows; `AuditExtraChannelTests` reads a method by the first match of its signature, so an
  overload of the same name would be read in its place. Hence an optional parameter, not an overload.

1. **An explicit result.** New `Services/AuditResult.cs`: `public enum AuditResult { Success, Failed, Undetermined }`, each
   name the text a row carries. `LogMailboxPermission` and `LogCalendarPermission` take a last optional
   `AuditResult? filedAs = null`: the row's result is `filedAs` when given, else Success or Failed from `success` as now
   (one private helper beside `IsSuccessfulAuditResult`), and its error is kept unless the row is filed Success. Every
   caller that gives no `filedAs` - all but the two below - writes the row it wrote before.
2. **The step says the result.** `PermissionChangeFailure.AuditResultOf(step)`: Undetermined at `TheWrite`, whose banner
   says whether the change was made could not be confirmed; Failed before the write (nothing was written) and after it
   (the written change has its own row; this one says a later step failed).
3. **The pages pass it.** In place, inside the catch's audit call on its own line (Mailbox 529, Calendar 506):
   `filedAs: PermissionChangeFailure.AuditResultOf(step)`. `success: false`, the banner as the error, the written identity
   and the servicer note in `extra`, and the record's guard stay as they are.

Not in scope, both candidates in the hand-back: `ExecuteOnPrem`'s catch (aud-a59; Mailbox 744-747, Calendar 687-690)
files its own write-step throw the same way, Failed under the "could not be confirmed" banner - this record names
`SubmitSingle`'s catch, whose written identity and servicer note the Approach keeps. And `IsSuccessfulAuditResult` stays:
an Undetermined row counts as a non-failure in the usage count and completes the audit's implicit trace operation as
succeeded, as an undetermined eligibility check has since aud-a28z part 5, where the Failed row counted as a failure.

Tests:
- `PermissionPagesSelfGrantTests`. aud-a71's `ACloudWriteThatThrows_IsRecordedAsFailed_WithTheBannersText` becomes
  `ACloudWriteThatThrows_IsRecordedAsUndetermined_WithTheBannersText`: the one row's result Undetermined, the rest as it
  was (each page). New `ACloudChangeThatThrowsBeforeTheWrite_IsRecordedAsFailed` (each page): a target Exchange Online
  does not hold (a test pool whose Get-Mailbox answers ObjectNotFound, as Exchange does) and on-premises Exchange does (the
  harness's stand-in through the page service's on-premises seam, a server configured), whose on-premises permission
  check throws - the one step before the write that can throw: the before-the-write banner, one Failed row with that text,
  nothing written, nothing staged for confirmation. aud-a53's `AThrowAfterTheWrite_IsNamedAsSuch_...` keeps its Failed row
  for the step after the write. New `TheStepsAuditResult_IsUndetermined_OnlyAtTheWrite` (each stage).
- `AuditServiceTests`: each method given `filedAs: Undetermined` files that result and keeps its error, whatever its bool.
- `EventLogSectionsTargetTests`: each method's Undetermined row, parsed by the page, is found by its Result filter under
  Undetermined and not under Failed.
- `AuditWriterThrowFreeTests`: the argument builder knows the new parameter type (it passes Undetermined), so the
  tripwire calls the two methods with it; its counts stand.

Mutations at the call site (md5, mutate, rebuild, filtered tests, failing names, restore from git, touch, rebuild, md5
equal, green): M1 `success: false` again at the write step - the Mailbox Permissions catch's `filedAs` argument removed;
M1c the same on Calendar Permissions. Extras: E1 `AuditResultOf` Undetermined at every step; E2 `LogMailboxPermission`
files from its bool alone (`filedAs` ignored); E3 its error dropped by the bool again (`success ? null : errorDetail`).

Files: `Services/AuditResult.cs` (new), `Services/AuditService.cs`, `Services/PermissionChangeFailure.cs`,
`Components/Pages/MailboxPermissions.razor`, `Components/Pages/CalendarPermissions.razor` (in place),
`ExchangeAdminWeb.Tests/PermissionPagesSelfGrantTests.cs`, `ExchangeAdminWeb.Tests/AuditServiceTests.cs`,
`ExchangeAdminWeb.Tests/EventLogSectionsTargetTests.cs`, `ExchangeAdminWeb.Tests/AuditWriterThrowFreeTests.cs`, this
record.

Caller-visible change, by design: a single permission change whose write threw is filed with result "Undetermined", its
error still the "could not be confirmed" banner, shown with the warning badge and found by the Admin Event Log's
Undetermined filter; a throw before or after the write is filed Failed as before.

## Fix

- New `Services/AuditResult.cs`: `public enum AuditResult { Success, Failed, Undetermined }`, each name the text a row
  carries.
- `Services/AuditService.cs`: `LogMailboxPermission` and `LogCalendarPermission` take a last optional
  `AuditResult? filedAs = null`. The row's result is the new private `ResultText(success, filedAs)` - the explicit result
  when one is given, else Success or Failed from the bool as before (a value outside the enum is no explicit result) - and
  its error is kept unless the row is filed Success (`result == "Success" ? null : errorDetail`). Nothing else in the file
  changes: `WriteAuditEvent`, its out parameter and its last-statement telemetry call stand.
- `Services/PermissionChangeFailure.cs`: `AuditResultOf(step)`, Undetermined at `TheWrite`, Failed at the other two stages.
- `Components/Pages/MailboxPermissions.razor` 529 and `Components/Pages/CalendarPermissions.razor` 506, in place (808 and
  762 lines, no registry entry moved): the catch's audit call gains `filedAs: PermissionChangeFailure.AuditResultOf(step)`
  after its `extra`, and the line-end comment above it (528, 505) names aud-a73. `success: false`, `errorDetail: failure`,
  the `extra` and the record's guard are as aud-a71 left them.

Tests: 11 new cases and one test renamed.
- `PermissionPagesSelfGrantTests` (+5). `ACloudWriteThatThrows_IsRecordedAsFailed_WithTheBannersText` is renamed
  `ACloudWriteThatThrows_IsRecordedAsUndetermined_WithTheBannersText`, its row's result Undetermined (each page).
  `TheStepsAuditResult_IsUndetermined_OnlyAtTheWrite` (each stage). `ASingleChangeThatThrowsBeforeTheWrite_IsRecordedAsFailed`
  (each page) - "single change", not the plan's "cloud change": the throw is in `SubmitSingle`'s on-premises branch. Its
  driver, `SubmitToAMailboxOnlyOnPremisesHolds`, runs the page over `OnPremMailboxService` / `OnPremCalendarService` (which
  now take an optional pool and settings), a pool whose Get-Mailbox writes an ObjectNotFound error as Exchange does
  (`PoolWhereExchangeOnlineHoldsNoMailbox`, the harness's stand-ins otherwise), and settings naming an on-premises server
  (`WithAnOnPremServer`), so the location read asks on-premises, where the harness's stand-in holds the mailbox; then the
  on-premises permission check throws the test's exception. aud-a53's `AThrowAfterTheWrite_IsNamedAsSuch_...` is unchanged:
  its second row stays Failed.
- `AuditServiceTests` (+4): `APermissionRow_FiledUndetermined_CarriesThatResult_AndKeepsItsError_WhateverItsBool` (each
  method, its bool false and true).
- `EventLogSectionsTargetTests` (+2): `APermissionChangeThatCouldNotBeConfirmed_IsFiledUndetermined_AndTheResultFilterFindsItThere`
  (each method): parsed by the page's `ParseAuditLine`, found under Undetermined and under All, not under Failed.
- `AuditWriterThrowFreeTests`: the argument builder hands an `AuditResult?` parameter Undetermined; its counts (13 methods,
  15 taking a dictionary) stand.

## Guard proof

Session-local driver `a73-mutate.ps1` (session scratchpad, log `a73-mutations.log`), the aud-a60 protocol: the fix staged;
per mutant the MD5 of the file it touches, the edit (an exact-text replacement that must match exactly once), the MD5 and
the line count again, a Debug build of the test project with 0 errors required, `dotnet test --no-build` filtered to
`FullyQualifiedName~PermissionPagesSelfGrant|FullyQualifiedName~ClickGate|FullyQualifiedName~ProgressRegistry|FullyQualifiedName~PermissionValidator|FullyQualifiedName~MailboxPermission|FullyQualifiedName~CalendarPermission|FullyQualifiedName~ExchangeGroupMembers|FullyQualifiedName~BulkPermission|FullyQualifiedName~BulkAndOnPrem|FullyQualifiedName~AuditWriterThrowFree|FullyQualifiedName~AuditServiceTests|FullyQualifiedName~EventLogSectionsTarget|FullyQualifiedName~AuditExtraChannel|FullyQualifiedName~UsageTelemetryService|FullyQualifiedName~ValidatedWriteCallSite`
(890 tests), the failing names taken from the run, then the file restored from the git index (`git checkout --`), its
write time advanced so MSBuild rebuilds it, and its MD5 checked equal to the one before. Fixed-file MD5s:
`Components/Pages/MailboxPermissions.razor` `A9B58BB864502CB360F0F5AC83A06574`, `Components/Pages/CalendarPermissions.razor`
`2B328E4103A0DB7108A93CAB5BE668C2`, `Services/PermissionChangeFailure.cs` `1CC6067A547F661042F320E0BAB9D057`,
`Services/AuditService.cs` `73875020D8E994D960EF964EB141EF6D` (unmutated: `Services/AuditResult.cs`
`FFCE002953763765BAEBE7D523FBC362`, `ExchangeAdminWeb.Tests/PermissionPagesSelfGrantTests.cs`
`0FF13DCAF11C43FF4792F4B623C28C0A`, `ExchangeAdminWeb.Tests/AuditServiceTests.cs` `9C2687BF1721108D4C84FF460DBD0DF8`,
`ExchangeAdminWeb.Tests/EventLogSectionsTargetTests.cs` `0ABF05B3F096F974B990461AE3DB6B90`,
`ExchangeAdminWeb.Tests/AuditWriterThrowFreeTests.cs` `096DEF87D29A31F9F3E6C313F41465EA`). Every mutant built with 0
errors and every restore matched; each page mutant kept its page's line count (808, 762), so ClickGate and
ProgressRegistry ran under it and passed. After the round, the restored tree rebuilt with 0 errors and the filter: 890/890
green.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 | `success: false` again at the write step: the Mailbox Permissions catch's `filedAs` argument removed | `MailboxPermissions.razor` `B189445F2662B7DD36F09CEFF53F9A13` | 1 of 890: `ACloudWriteThatThrows_IsRecordedAsUndetermined_WithTheBannersText(page: "Mailbox Permissions")` - the row filed Failed |
| M1c | the same on Calendar Permissions | `CalendarPermissions.razor` `422BD93513078C42FC0D4405D4205CDB` | 1 of 890: the same test's `(page: "Calendar Permissions")` row |
| E1 | extra: `AuditResultOf` Undetermined at every step | `PermissionChangeFailure.cs` `A9E41C8A6FFD652F6C5D2EA046BF63D9` | 6 of 890: `TheStepsAuditResult_IsUndetermined_OnlyAtTheWrite` (the before and after rows); `AThrowAfterTheWrite_IsNamedAsSuch_InTheCausesOwnWords_NotItsRawText` (each page: no Failed row beside the success row); `ASingleChangeThatThrowsBeforeTheWrite_IsRecordedAsFailed` (each page) |
| E2 | extra: `LogMailboxPermission` files from its bool alone (`filedAs` ignored) | `AuditService.cs` `80004EF88E94D5469C314E0D56191BEB` | 4 of 890: `APermissionChangeThatCouldNotBeConfirmed_IsFiledUndetermined_AndTheResultFilterFindsItThere(category: "MailboxPermission")`; `APermissionRow_FiledUndetermined_CarriesThatResult_AndKeepsItsError_WhateverItsBool(category: "MailboxPermission", ...)`, both bools; `ACloudWriteThatThrows_IsRecordedAsUndetermined_WithTheBannersText(page: "Mailbox Permissions")` |
| E3 | extra: `LogMailboxPermission`'s error dropped by its bool again (`success ? null : errorDetail`) | `AuditService.cs` `935999DD9C074C2FD61B520EE54E581E` | 1 of 890: `APermissionRow_FiledUndetermined_CarriesThatResult_AndKeepsItsError_WhateverItsBool(category: "MailboxPermission", success: True)` |

No separate pre-fix reproduction: M1 and M1c are each catch's audit call as it stood at `066ea1a2`, byte for byte (only
the line-end comment above it is the fix's), so each failure is also the finding's reproduction.

## Known gaps

- `ExecuteOnPrem`'s catch (aud-a59; Mailbox 744-747, Calendar 687-690) files a throw at its own write step Failed under
  the same "could not be confirmed" banner - out of this record's scope (Plan), a candidate in the hand-back. It is reached:
  `MailboxPermissionService.AddMailboxPermissionsOnPremAsync`'s connect sits in a try with only a `finally`, and
  `CalendarPermissionService.SetCalendarPermissionOnPremAsync` lets every failure of its write out. Admitted as aud-a75 and
  fixed on this branch (plan `b8bad419`, fix `73af9fc8`).
- `IsSuccessfulAuditResult` reads any result but "Failed" as a success, so an Undetermined row counts as a non-failure in
  the usage count and completes the audit's implicit trace operation as succeeded (the rule aud-a28z part 5 kept for an
  undetermined eligibility check); filed Failed, this row counted as a failure there. The audit row, its trace step's
  `auditResult` and the event log say Undetermined. A candidate in the hand-back.
- Rows already written stay as written: a write that threw, logged before this build, reads "Failed"; its error, "Whether
  the change was made could not be confirmed...", is the way to tell it apart.
- The stage names the step, not what threw in it (aud-a53): at the write, the row is Undetermined whatever threw there -
  a borrow the pool refused before anything reached Exchange included, which the handler cannot tell apart, as its banner
  cannot.
- Nothing ran against Exchange: the stand-in cmdlets behind the harness's pool or a test pool, the harness's on-premises
  stand-in through the services' own seam, the harness's validator, a substitute progress service.

## Comments after the round

None: the comments were written with the fix (`75355a7b`), and every mutant ran with them in place, so there is no
comments-only commit to check.

## Gates - 2026-10-23 (branch tip `75355a7b`: the plan and the fix)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: exit 0, 0 errors; 19 warnings - the baseline - none in
  a touched file (they are in GroupManagement, MessageTrace, RiskyUsers and SelfServiceGroups, Comms10kApprovedSetTests,
  AccountLockoutRemediationService, HeaderAnalysisService and MigrationReportExportProcessor).
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0, 6,644
  passed / 0 skipped / 0 failed, 6,644 total, 3m54s; nothing hung (the blame collector wrote no Sequence file).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0. `git add -A`
  then `tools/Test-AsciiOnly.ps1`: exit 0 ("ASCII lint passed"). The pages: 808 and 762 lines (`wc -l`).

No AD, Exchange Online, Graph, DHCP or IIS call: Exchange is the harness's pool or a test pool over the harness's stand-in
cmdlets, on-premises Exchange the harness's stand-in defined in the services' own runspace, the directory
`SelfGrantDirectoryStandIn`, the secret store a stand-in fetch. No coder self-review verdict is claimed.

## Closeout

Fixed on branch `worktree-agent-a22f2baf02d6abcdd`, not merged, not pushed. Due at merge: the base app version - the shared
`AuditService` (the two permission methods' `filedAs`), the new `AuditResult` and `PermissionChangeFailure.AuditResultOf` -
and the Mailbox Permissions and Calendar Permissions module versions: what a single change's failure row is filed as.
