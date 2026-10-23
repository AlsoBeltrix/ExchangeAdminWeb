# aud-a50: Permission pages - the on-prem write is not re-checked for self-grant right before it writes; the stored PreventSelfGrant warning floods; blank-User grant rows are misrecorded; the pages cannot learn whether the refusal's audit row was written; no service wording for an unreadable enablement store

**Severity**: LOW/MEDIUM - the aud-a43/a44 leftovers. (1) `ExecuteOnPrem` in both pages re-runs only
`ValidateTargetMailboxAsync` immediately before the on-prem write; the self-grant check ran in `SubmitSingle`
before the operator's confirmation pause and is not repeated right before the write (a write-time re-check
is the aud-a2l/a26l pattern). (2) `PermissionValidator` warns on every validation for a STORED
`PreventSelfGrant` value that is neither true nor false (the aud-a43 once-guard covers only appsettings).
(3) The bulk loops have no blank-User check before the self-grant check, so a grant row with an empty User is
refused as "Exchange Online could not resolve ''" and recorded under `*_SelfGrantAttempt` rather than as an
invalid row. (4) `AuditService.LogMailboxPermission` / `LogCalendarPermission` return void, so the pages
cannot learn the aud-a28u write outcome; the banner says "The audit row could not be written." only when the
call throws. (5) `SettingsStoreText` has no `ServiceEnablement(bool)`; the pool rebuilds the service wording
by replacing the page clause.
**Status**: Fixed on branch, closed on guard proof; not merged, not pushed; codex review pending
**Branch**: `worktree-agent-a80f49ad9ee618412` (from master `132d6e97`)
**Commit**: plan `1d7346ca`; part 1 `fcac67d7`; part 3 `29d9b16f`; part 2 `b1ec0b5d`; part 4 `43a82568` (after aud-a52's `f7921ce8`); part 5 `0e00ab23`; comments `dcfec67c`
**Reviewer**: implementer (surfaced by the aud-a43/a44 agent, 2026-10-22, candidates 1-3 and its two "Left for a follow-up" items)

## Evidence

At `fce22146`: `Components/Pages/MailboxPermissions.razor` 693-695 and 708-709, 443-446;
`Components/Pages/CalendarPermissions.razor` 647-649 and 661-663, 413-416 (both line-keyed by
`ClickGateRegistry`, 808 and 762 lines, edit in place only); `Services/PermissionValidator.cs` around 116;
`Services/MailboxPermissionService.cs` 274-277 and `Services/CalendarPermissionService.cs` 162-165;
`Services/AuditService.cs` 47-109 and 463-517 (`WriteAuditEvent` computes the outcome at 488);
`Services/SettingsStoreText.cs` 30-32 and 43.

## Approach (to be written by the implementing agent before code)

- `ExecuteOnPrem` re-runs the self-grant check immediately before the write (same principal, same grantee);
  a refusal there is the same refusal record and banner as in `SubmitSingle`; nothing written.
- The stored-value warning is once per process per observed value, like the appsettings one.
- The bulk loops refuse a blank User as an invalid row before any check, recorded as such.
- The two audit methods return the `JsonlWriteOutcome` `WriteAuditEvent` already computes; the pages add
  "The audit row could not be written." when the outcome is not `Written`, not only on a throw.
- `SettingsStoreText.ServiceEnablement(bool)` beside `ServiceModuleConfig`; the pool uses it.
- Tests through `PermissionPagesSelfGrantTests`, the bulk loops' harness, `AuditWriterThrowFreeTests` and
  `StoreTextSurfaceTests`; mutations at the call site: the re-check removed; the stored-value once-guard
  removed; the blank-User check removed; the outcome ignored on one page; the pool's replace again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21). Branch
`worktree-agent-a80f49ad9ee618412` from master `132d6e97`. One commit per part, in the order 1, 3, 2, 4, 5; aud-a52
(folded into this agent's scope by the supervisor; its own record, plan and commit) lands between part 2 and part 4 so
the validator work is contiguous; then one comments-only commit after every mutation round. Behaviour: the shared
`PermissionValidator`, `AuditService`, `ExoConnectionPool` and `SettingsStoreText` (base app version) and what the Mailbox
Permissions and Calendar Permissions pages and bulk loops refuse and say (both module versions); the bumps are the
supervisor's at merge (`ExchangeAdminWeb.csproj` and `Modules/ModuleCatalog.cs` are outside this brief).

Evidence re-read at `132d6e97`:
- `MailboxPermissions.razor` `ExecuteOnPrem` 693-703 re-validates the target against `onPremAuthState.User`, then writes
  at 707-709; no self-grant check. `SubmitSingle` 429-457 runs it before staging the on-premises write (468-484) and the
  operator's confirmation, an unbounded pause. `CalendarPermissions.razor` the same: 647-657, then the write at 659-663;
  `SubmitSingle` 404-427. Lines free for an in-place statement: Mailbox 704 and 706, Calendar 658 (blank).
- `PermissionValidator.GetPreventSelfGrant` 116: a stored value that is neither true nor false is named on every check;
  `PreventSelfGrantFromAppsettings` 132-134 carries aud-a43's once-guard (`AppsettingsValuesNamed`).
- `MailboxPermissionService` 218-297 / `CalendarPermissionService` 133-185: nothing checks `row.User` before the target
  check (252 / 140) and the self-grant check (277 / 165), which puts '' to Exchange and records `*_SelfGrantAttempt`.
- `AuditService.LogMailboxPermission` 47-78 and `LogCalendarPermission` 80-109 return void; `WriteAuditEvent` 466-520
  computes the outcome at 491. Two source guards hold its shape: `UsageTelemetryServiceTests.AuditService_HookIsLastInWriteAuditEvent`
  (another agent's file, not edited) finds `private void WriteAuditEvent(` and requires the telemetry call to stay its
  last statement; `AuditExtraChannelTests` reads only `public void Log...` methods and looks for `WriteAuditEvent(evt);`.
- `SettingsStoreText.Enablement` 30-32 and `ServiceModuleConfig` 43; `ExoConnectionPool.UnreadableEnablement` 223-226
  rebuilds the service wording by replacing `DatabaseRemedy` in the page text.

1. **Part 1 - the on-premises write re-checks the grantee.** `ExecuteOnPrem`, Add (Mailbox) / Set (Calendar) only, after
   the target re-check and before the write: `Validator.ValidateSelfGrantAsync(onPremAuthState.User, user)` - the token
   the target re-check was just decided against, and the staged grantee. A refusal writes the record `SubmitSingle`
   writes (`Add{permType}_SelfGrantAttempt` / `SetCalendar_SelfGrantAttempt`, the validator's refusal as `errorDetail`; the
   on-premises path carries no auto-mapping), shows the banner `SubmitSingle` shows (the refusal, and "The audit row could
   not be written." when its record fails), sets `result` and `outcome`, and returns: nothing written. In place, one
   statement: Mailbox 704, Calendar 658; 808 and 762 lines kept, no registry entry moves. The record-and-banner step is
   new `Services/SelfGrantRefusalRecord.cs` (`Banner(refusal, write, logger, target)`, `SubmitSingle`'s catch as a
   method): one free line cannot hold `SubmitSingle`'s try/catch, which stays as it is.
2. **Part 3 - a blank User is an invalid row.** First in each bulk row, Add and Remove alike (the `_NoPermissions`
   shape): `string.IsNullOrWhiteSpace(row.User)` records `Bulk{Add|Remove}_NoUser` / `Bulk{Set|Remove}Calendar_NoUser`
   with "No user specified (the User column is empty)", adds a FAILED entry "No user specified", and continues - before
   the target check, the self-grant check and the write.
3. **Part 2 - the stored value is named once.** `GetPreventSelfGrant`: a static `StoredValuesNamed` beside
   `AppsettingsValuesNamed`; the warning once per process per value; every check still refuses on it.
4. **Part 4 - the pages learn the write's outcome.** `AuditService`: the shared writer becomes
   `private void WriteAuditEvent(Dictionary<string, object?> evt, out JsonlWriteOutcome outcome)`, its body unchanged but
   for the outcome being its out parameter - so `private void WriteAuditEvent(` and the telemetry call as its last
   statement both stand - and `private void WriteAuditEvent(Dictionary<string, object?> evt) => WriteAuditEvent(evt, out _);`
   follows it, so no other method changes. `LogMailboxPermission` and `LogCalendarPermission` return `JsonlWriteOutcome`
   (`WriteAuditEvent(evt, out var outcome); return outcome;`); a caller that ignores it compiles and behaves as before.
   The pages, in place: `SubmitSingle`'s record becomes `if (Audit.Log...(...) != JsonlWriteOutcome.Written) selfGrantError
   += " The audit row could not be written.";` inside its existing try, whose catch still covers a throw;
   `SelfGrantRefusalRecord.Banner` takes `Func<JsonlWriteOutcome>` and adds the sentence when the outcome is not
   `Written`, so `ExecuteOnPrem`'s line does not change. `AuditExtraChannelTests`' source readers accept the return type
   and the out form (still: every method taking `extra` merges it before it writes).
5. **Part 5 - the service wording for an unreadable enablement.** `SettingsStoreText.ServiceEnablement(bool
   legacyImportFailed)` beside `ServiceModuleConfig`: the legacy-file text as `Enablement` gives it (it has no page
   clause), else the table sentence with `ServiceDatabaseRemedy`; the table sentence one private constant both forms use.
   `ExoConnectionPool.UnreadableEnablement => SettingsStoreText.ServiceEnablement(_enablement.LegacyImportFailed)`. The
   text the pool says is unchanged.

Tests:
- Part 1, `PermissionPagesSelfGrantTests`: each page's `ExecuteOnPrem` driven through its compiled code (services and
  the staged fields set by reflection, as aud-a43's driver does `SubmitSingle`) over an on-premises service whose
  credential and session are the harness's (the `*WriteIdentityTests` shape). A staged grant to a grantee Exchange names
  as the operator is refused right before the write: the banner is the refusal, one `*_SelfGrantAttempt` record carries
  it, no on-premises command runs. The control, a grantee who is someone else: the write runs and its record is a
  success. A refusal whose record throws: the banner says so, one warning. Unit cases for `SelfGrantRefusalRecord`.
  Updated, not weakened: `PermissionValidatorSelfGrantTests`' two source checks expect two record sites and two check
  sites on each page, every record's `errorDetail` the refusal it was handed, every check the token its page decided the
  target against (`validationAuthState.User` in `SubmitSingle`, `onPremAuthState.User` in `ExecuteOnPrem`).
- Part 3, `PermissionValidatorSelfGrantTests` (the bulk loops over the harness): an empty and a whitespace User, Add and
  Remove, both loops - one `*_NoUser` record, a FAILED entry, Exchange never asked, no directory session, nothing written
  or connected; a valid row after it is still processed.
- Part 2, `PermissionValidatorConfigReadTests`: a stored value of the test's own, two validations and a second validator
  over it - one warning, every check refused; another value is named in its turn.
- Part 4, `AuditWriterThrowFreeTests`: both methods return `Written`, `NotSerializable` (an unwritable value in `extra`)
  and `FileWriteFailed` (the file held). `PermissionPagesSelfGrantTests`: each page's `SubmitSingle` and `ExecuteOnPrem`
  with the audit file held, so the writer drops the row and nothing throws - the banner is the refusal and the sentence,
  and the page logs no exception.
- Part 5, `StoreTextSurfaceTests`: the service enablement text is the page text without its reload clause, and the
  legacy text is one text in both. `ExoConnectionPoolConfigReadTests`: the borrow's refusal is exactly the prefix and
  `ServiceEnablement(legacy)` for both stores, and a code-view guard that `UnreadableEnablement` calls `ServiceEnablement`
  and replaces nothing - the replace says the same words today, so only the guard can tell the two apart.

Mutations at the call site (md5 before, mutate, rebuild, filtered tests, failing names recorded, restore from git, touch,
rebuild, md5 equal, filtered tests green): M1 the Mailbox Permissions `ExecuteOnPrem` re-check removed, M1c the Calendar
Permissions one; M3 the Mailbox Permissions blank-User check removed, M3c the Calendar one; M2 the stored-value once-guard
removed; M4 the outcome ignored on one page (Mailbox Permissions `SubmitSingle`'s `if` back to the bare call), extra E4
`SelfGrantRefusalRecord` ignoring the outcome; M5 the pool's replace again, extra E5 `ServiceEnablement(false)` on the page
remedy.

Files: `Components/Pages/MailboxPermissions.razor` and `Components/Pages/CalendarPermissions.razor` (in place),
`Services/SelfGrantRefusalRecord.cs` (new), `Services/PermissionValidator.cs`, `Services/MailboxPermissionService.cs`,
`Services/CalendarPermissionService.cs`, `Services/AuditService.cs` (the two methods and `WriteAuditEvent` only),
`Services/SettingsStoreText.cs`, `Services/ExoConnectionPool.cs`, the tests named, this record.

## Fix

### Part 1 - the on-premises write re-checks the grantee right before it writes

- `Components/Pages/MailboxPermissions.razor` 704 and `Components/Pages/CalendarPermissions.razor` 658, each a blank line
  before (808 and 762 lines kept, no registry entry moved): `if (isAdd && await Validator.ValidateSelfGrantAsync(
  onPremAuthState.User, user) is { } selfGrantError) { result = outcome = PermissionResult.Fail(SelfGrantRefusalRecord.Banner(
  selfGrantError, () => Audit.LogMailboxPermission(currentUser, clientIpAddress, $"Add{permType}_SelfGrantAttempt", target,
  user, permType, false, ticket, errorDetail: selfGrantError), Logger, target)); return; }`, and its Calendar twin on
  `isSet` with `"SetCalendar_SelfGrantAttempt"` and `level` (the access right `SubmitSingle` records). After the target
  re-check and before the write; the handler's finally still lowers `isLoading`, completes the activity from `outcome`
  and clears the staged target.
- New `Services/SelfGrantRefusalRecord.cs`: `Banner(refusal, write, logger, target)` writes the record; a write that
  throws is logged as `SubmitSingle`'s catch logs it, and the banner is the refusal and `AuditRowNotWritten`, "The audit
  row could not be written.".
- Tests: `PermissionPagesSelfGrantTests` +8 - `TheOnPremWrite_ReChecksTheGrantee_RightBeforeItWrites_AndRefusesASelfGrant`,
  `TheOnPremWrite_ToSomeoneElse_StillWrites` and `TheOnPremRefusal_WhoseRecordCannotBeWritten_IsTheValidatorsRefusal_AndSaysSo`
  for each page (`ExecuteOnPrem` driven by reflection over the staged grant, the harness's on-premises stand-ins behind a
  service whose credential and session are the harness's), and `TheRefusalsBanner_IsTheRefusal_WhenItsRecordIsWritten`,
  `TheRefusalsBanner_WhenItsRecordThrows_SaysTheRowCouldNotBeWritten_AndLogsTheException`. `PermissionValidatorSelfGrantTests`'
  `EverySelfGrantRecord_CarriesTheValidatorsOwnRefusal` and `EverySelfGrantCheck_IsHandedTheSignedInPrincipal_NeverTheLogonName`
  read every site, not a single one: two per page, each record's `errorDetail` `selfGrantError`, the checks handed
  `validationAuthState.User` and then `onPremAuthState.User`.

Caller-visible change, by design: a staged on-premises Mailbox Permissions or Calendar Permissions grant that the
self-grant check refuses when the operator presses Continue - say they joined the granted group during the pause - is
refused then with the validator's refusal, recorded as `SubmitSingle` records it, and nothing is written on-premises.
Every on-premises grant pays one more self-grant check (a directory session and the Exchange lookups) just before it writes.

### Part 3 - a bulk row that names no user is an invalid row

- `Services/MailboxPermissionService.cs` `ProcessMailboxPermissionsCsvAsync` and `Services/CalendarPermissionService.cs`
  `ProcessCalendarPermissionsCsvAsync`, first in each row's try: `string.IsNullOrWhiteSpace(row.User)` records
  `Bulk{Add|Remove}_NoUser` (permission "Mailbox") / `Bulk{Set|Remove}Calendar_NoUser` (no access right), Failed, with
  `MailboxPermissionService.BulkRowNamesNoUser` - "No user specified (the User column is empty)" - as its error; adds that
  error and a FAILED entry "No user specified"; continues. Before the rights are parsed, the target check, the self-grant
  check and the write, for grants and revocations alike.
- Tests: `PermissionValidatorSelfGrantTests` +12 - `ABulkRowThatNamesNoUser_IsAnInvalidRow_RefusedBeforeAnyCheck` (both
  loops x grant and revoke x an empty and a whitespace User: one `*_NoUser` record, the FAILED entry and error, Exchange
  never asked, no directory session, nothing written or connected) and `ARowAfterOneThatNamesNoUser_IsStillProcessed`
  (both loops x grant and revoke: the next row written).

Caller-visible change, by design: a bulk row whose User is empty or blank is reported "No user specified" and audited as
`*_NoUser`; a grant row used to be refused by the self-grant check after it asked Exchange and the directory about '' and
was audited as `*_SelfGrantAttempt`, and a revocation row went to the target check and the write.

### Part 2 - a stored PreventSelfGrant value that is neither true nor false is named once

- `Services/PermissionValidator.cs`: a static `StoredValuesNamed` (`ConcurrentDictionary<string, byte>`, ordinal) beside
  aud-a43's `AppsettingsValuesNamed`; `GetPreventSelfGrant` writes its warning only when the value is added to it. The
  refusal is unchanged: every check over such a value refuses.
- Tests: `PermissionValidatorConfigReadTests.AStoredValueThatIsNeitherTrueNorFalse_IsNamedOnce_NotOnEveryValidation` - a
  value of the test's own, two validations and a second validator: one warning naming it, every check refused; a second
  value stored, then two validations: named once in its turn.

Caller-visible change, by design: a stored `PreventSelfGrant` that is neither true nor false is named in the log once per
process for each value, not once per self-grant check (per bulk row); self-grants stay refused.

### Part 4 - the pages learn whether the refusal's row was written

aud-a52 landed between part 2 and this part (`f7921ce8`, its own record).

- `Services/AuditService.cs`: the shared writer is `private void WriteAuditEvent(Dictionary<string, object?> evt, out
  JsonlWriteOutcome outcome)` - its body as it was, the local `outcome` now the out parameter, so the telemetry call is
  still its last statement and `UsageTelemetryServiceTests.AuditService_HookIsLastInWriteAuditEvent` (another agent's
  file, not edited) holds - and `private void WriteAuditEvent(Dictionary<string, object?> evt) => WriteAuditEvent(evt, out _);`
  after it, which every other method still calls, unchanged. `LogMailboxPermission` and `LogCalendarPermission` return
  `JsonlWriteOutcome` (`WriteAuditEvent(evt, out var outcome); return outcome;`); every other caller ignores it as before.
- `Components/Pages/MailboxPermissions.razor` 443-451 and `Components/Pages/CalendarPermissions.razor` 413-421, in place
  (808 and 762 lines): `SubmitSingle`'s self-grant record is `if (Audit.Log...(...) != JsonlWriteOutcome.Written)
  selfGrantError += " " + SelfGrantRefusalRecord.AuditRowNotWritten;` inside the existing try, and the catch's sentence is
  that constant too, so the two sites and the helper say one sentence.
- `Services/SelfGrantRefusalRecord.cs`: `Banner` takes `Func<JsonlWriteOutcome>`; the refusal alone when the outcome is
  `Written`, the sentence added otherwise (the writer has logged its own error), the catch as before. `ExecuteOnPrem`'s
  lines (part 1) are unchanged: their lambdas now return the outcome.
- `ExchangeAdminWeb.Tests/AuditExtraChannelTests.cs`: its source readers take a `JsonlWriteOutcome` return as well as
  `void`, and the write as `WriteAuditEvent(evt` - so they still find, and check, the two methods.
- Tests: `AuditWriterThrowFreeTests.ThePermissionRecords_HandBackWhetherTheirRowWasWritten` (6: both methods x written,
  an unserialisable value in `extra`, the file held); `PermissionPagesSelfGrantTests` +6 -
  `TheSubmitSingleBanner_WhenTheWriterDropsTheRefusalsRow_SaysTheRowCouldNotBeWritten` and
  `TheOnPremBanner_WhenTheWriterDropsTheRefusalsRow_SaysTheRowCouldNotBeWritten` for each page (the audit file held, so the
  writer drops the row and nothing throws: the refusal and the sentence, no page warning, no row, nothing written) and
  `TheRefusalsBanner_WhenTheWriterDropsItsRecord_SaysTheRowCouldNotBeWritten` (2); the helper's written case returns
  `Written`.

Caller-visible change, by design: when the writer drops the self-grant refusal's audit row - it no longer throws - the
Mailbox Permissions and Calendar Permissions banner, single or on-premises, is the refusal followed by "The audit row could
not be written.", as it already was when the record threw. `AuditService.LogMailboxPermission` and `LogCalendarPermission`
return the writer's outcome.

### Part 5 - the service wording for an unreadable enablement store

- `Services/SettingsStoreText.cs`: the legacy-file sentence and the table sentence are private constants
  (`EnablementLegacyUnimported`, `EnablementTableUnreadable`); `Enablement(bool)` is built on them, its text byte for byte
  as it was; new `ServiceEnablement(bool legacyImportFailed)` beside `ServiceModuleConfig` - the legacy text as it is (it
  names no page), else the table sentence and `ServiceDatabaseRemedy`.
- `Services/ExoConnectionPool.cs`: `UnreadableEnablement => SettingsStoreText.ServiceEnablement(_enablement.LegacyImportFailed)`;
  the refusal and its log line say what they said.
- Tests: `StoreTextSurfaceTests` +2 - `TheEnablementPageText_IsAsItWas` and
  `TheServiceEnablementText_IsThePageTextWithoutItsReloadClause_AndTheLegacyTextIsOneText`;
  `ExoConnectionPoolConfigReadTests`: `AnEnablementThatCannotBeRead_RefusesTheBorrow_SayingSo_NotThatTheModuleIsDisabled`
  (both rows) also requires exactly the prefix and `ServiceEnablement(legacyFile)`, and new
  `TheUnreadableEnablement_IsTheStoreTextsServiceWording_NotThePageTextRewritten` (code view: the pool calls
  `ServiceEnablement`, and names neither `Enablement(` nor `DatabaseRemedy`).

Caller-visible change: none in text. The service wording now comes from the store text, where a change to the page text
can no longer leave the page's reload clause in a service's refusal.

## Guard proof

Protocol (session-local driver `mutate.ps1`, session scratchpad `a50`, log `a50-mutations.log`): the fix staged; per
mutant: the MD5 of each file it touches, one exact-text replacement that must match exactly once, the MD5 again, a Debug
build of the test project with 0 errors required, `dotnet test --no-build` filtered as each part names, the failing names
taken from the run, then each file restored from the git index (`git checkout --`), its write time advanced so MSBuild
rebuilds it, and its MD5 checked equal to the one before. Every mutant below built with 0 errors and every restore
matched. No separate pre-fix reproduction where a mutant is the code as it stood at `132d6e97`: it is that code at its
call site, and its MD5 shows it.

### Part 1

Filter `FullyQualifiedName~PermissionPagesSelfGrant|FullyQualifiedName~PermissionValidatorSelfGrant|FullyQualifiedName~ClickGate|FullyQualifiedName~ProgressRegistry`
(422 tests). Fixed-file MD5s: `Components/Pages/MailboxPermissions.razor` `9A9D9BB401D5B555940BAEDC658937F7`,
`Components/Pages/CalendarPermissions.razor` `B8EE0434FCF41E8123712EB2D6069EEC`, `Services/SelfGrantRefusalRecord.cs`
`0BA2B0792BF57E331294CBC5FB88FD67`. After the round, the restored tree rebuilt and the filter: 422/422 green.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 | Mailbox Permissions `ExecuteOnPrem`: line 704 blank again - the page as it stood | `7B2C771C3B9ACD5E398F18F7B20E4060` (aud-a44's recorded MD5 of the page at `132d6e97`) | 4 of 422: `TheOnPremWrite_ReChecksTheGrantee_RightBeforeItWrites_AndRefusesASelfGrant(page: "Mailbox Permissions")` and `TheOnPremRefusal_WhoseRecordCannotBeWritten_IsTheValidatorsRefusal_AndSaysSo(page: "Mailbox Permissions")` (the staged self-grant written on-premises), and both source checks' Mailbox Permissions rows |
| M1c | Calendar Permissions `ExecuteOnPrem`: line 658 blank again - the page as it stood | `1F953D7A79E5775A508B8B4C506140B9` (aud-a44's recorded MD5 of the page at `132d6e97`) | 4 of 422: the same two page tests' `(page: "Calendar Permissions")` rows and both source checks' Calendar Permissions rows |
| E1 | extra, `SelfGrantRefusalRecord.Banner` without its catch | `D0232B6656BAB02CFEEAA03C2A45E0BB` | 3 of 422: both `TheOnPremRefusal_WhoseRecordCannotBeWritten_IsTheValidatorsRefusal_AndSaysSo` rows and `TheRefusalsBanner_WhenItsRecordThrows_SaysTheRowCouldNotBeWritten_AndLogsTheException` |

Each page mutant kept its page's line count, so the ClickGate and ProgressRegistry suites ran under it and passed.

### Part 3

Filter `FullyQualifiedName~PermissionValidatorSelfGrant|FullyQualifiedName~MailboxPermission|FullyQualifiedName~CalendarPermission|FullyQualifiedName~BulkPermission|FullyQualifiedName~PermissionPagesSelfGrant`
(146 tests). Fixed-file MD5s: `Services/MailboxPermissionService.cs` `9BBAE48216BFA259B38BCD8D7028B94B`,
`Services/CalendarPermissionService.cs` `CA0F76AB9ACEC0173AF911B6C7AA75AA`. After the round, the restored tree rebuilt and
the filter: 146/146 green.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M3 | Mailbox Permissions bulk loop: the blank-User check removed (the row as it stood; the unused constant left) | `EEE3F76921F4B18017F40F7988FD144F` | 6 of 146: the four `ABulkRowThatNamesNoUser_IsAnInvalidRow_RefusedBeforeAnyCheck(calendar: False, ...)` rows and both `ARowAfterOneThatNamesNoUser_IsStillProcessed(calendar: False, ...)` rows - no `_NoUser` record |
| M3c | Calendar Permissions bulk loop: the blank-User check removed | `F88F9402D9DCE8C57E3FED7CB55BF040` | 6 of 146: the same rows with `calendar: True` |

### Part 2

Filter `FullyQualifiedName~PermissionValidator` (144 tests). Fixed-file MD5: `Services/PermissionValidator.cs`
`63FEFF64CC30B21F8CDA8A8E9017D36F`. After the round, the restored tree rebuilt and the filter: 144/144 green.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M2 | `GetPreventSelfGrant`: the once-guard removed - the warning on every check, as it stood (the unused set left) | `2831442629024F24B566E369185BB699` | 1 of 144: `PermissionValidatorConfigReadTests.AStoredValueThatIsNeitherTrueNorFalse_IsNamedOnce_NotOnEveryValidation` |

### Part 4

Filter `FullyQualifiedName~PermissionPagesSelfGrant|FullyQualifiedName~AuditWriterThrowFree|FullyQualifiedName~AuditExtraChannel|FullyQualifiedName~AuditService|FullyQualifiedName~UsageTelemetryService|FullyQualifiedName~PermissionValidatorSelfGrant|FullyQualifiedName~ClickGate|FullyQualifiedName~ProgressRegistry|FullyQualifiedName~MailboxPermission|FullyQualifiedName~CalendarPermission|FullyQualifiedName~AuditCategoryFiling|FullyQualifiedName~RefusalAudit`
(644 tests). Fixed-file MD5s: `Components/Pages/MailboxPermissions.razor` `1030EE8C8462E8EDBD6FDB796E1AE9EB`,
`Components/Pages/CalendarPermissions.razor` `821DBD99BEB92F2238D691142DA872C5`, `Services/SelfGrantRefusalRecord.cs`
`D0DBE61EFF35F08464886B0D03743342`, `Services/AuditService.cs` `70533F198EDE2E764C988367AA45D2D2`. After the round, the
restored tree rebuilt and the filter: 644/644 green; the pages 808 and 762 lines throughout.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M4 | the outcome ignored on one page: Mailbox Permissions `SubmitSingle`'s record back to the bare call | `764888FF58D4D218A53C5F8F5CEED9F8` | 1 of 644: `TheSubmitSingleBanner_WhenTheWriterDropsTheRefusalsRow_SaysTheRowCouldNotBeWritten(page: "Mailbox Permissions")` - the refusal alone |
| M4c | extra: the same on Calendar Permissions | `DE2A5765AF8E23B25CCD10F4D6066C42` | 1 of 644: the same test's `(page: "Calendar Permissions")` row |
| E4 | extra: `SelfGrantRefusalRecord.Banner` ignoring the outcome | `C926C52E3238A0438658AE762961A677` | 4 of 644: both `TheOnPremBanner_WhenTheWriterDropsTheRefusalsRow_SaysTheRowCouldNotBeWritten` rows and both `TheRefusalsBanner_WhenTheWriterDropsItsRecord_SaysTheRowCouldNotBeWritten` rows |
| E4b | extra: `AuditService.LogMailboxPermission` handing back `Written` whatever the writer said | `7AFA6DC1CFB0DC54FFD53CF9792987ED` | 4 of 644: the two dropped `ThePermissionRecords_HandBackWhetherTheirRowWasWritten(record: "mailbox", ...)` rows and both pages-tests' Mailbox Permissions rows (`SubmitSingle`, `ExecuteOnPrem`) |

### Part 5

Filter `FullyQualifiedName~StoreTextSurface|FullyQualifiedName~ExoConnectionPool|FullyQualifiedName~ExoConnection|FullyQualifiedName~ModuleEnablement|FullyQualifiedName~PermissionValidator|FullyQualifiedName~ExchangeIdentityResolver|FullyQualifiedName~Settings`
(480 tests). Fixed-file MD5s: `Services/ExoConnectionPool.cs` `C36F10D0332C53ECC9A38B9D36696C4F`,
`Services/SettingsStoreText.cs` `ACD8B9295C4A8078B0BD1A4A43F7ECE1`. After the round, the restored tree rebuilt and the
filter: 480/480 green.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M5 | the pool's replace again: `UnreadableEnablement` rebuilt from the page text, as it stood | `905BACF98F5C2BD4843FD5A06653153E` (the file at `132d6e97`, byte for byte) | 1 of 480: `ExoConnectionPoolConfigReadTests.TheUnreadableEnablement_IsTheStoreTextsServiceWording_NotThePageTextRewritten` - the replace says the same words today, so only the code-view guard can see it, as the plan says |
| E5 | extra: `ServiceEnablement(false)` on the page remedy | `B7564C5AB62012F50D997961A6DF3F03` | 2 of 480: `StoreTextSurfaceTests.TheServiceEnablementText_IsThePageTextWithoutItsReloadClause_AndTheLegacyTextIsOneText` and `AnEnablementThatCannotBeRead_RefusesTheBorrow_SayingSo_NotThatTheModuleIsDisabled(legacyFile: False)` |

## Comments after the round

Written after every mutation round (aud-a50's five parts and aud-a52), in their own commit `dcfec67c`, checked by
`codelines.ps1` (session scratchpad `a50`) over the staged change: every line it adds or removes in the twelve code files
is a whole-line `//` or `///` comment or blank (170 lines), and each file's code lines - blanks and whole-line comments
stripped - hash the same before and after; an untouched file fails the check (control `Services/ProtectedGroupMembership.cs`:
"no change"). This record's files, with their code-line MD5s equal before and after: `Services/SelfGrantRefusalRecord.cs`
(17 lines) `BF24F34F693586F0B8F3CCB82F35402D`, `Services/MailboxPermissionService.cs` (7) `CF134B435A3B3616341747F9CC117E12`,
`Services/CalendarPermissionService.cs` (2) `4BC60A2570107085EFF3006DE731B593`, `Services/PermissionValidator.cs` (58, with
aud-a52's) `20B028FDC08C8FB53E3F75C6EC19B474`, `Services/AuditService.cs` (9: `WriteAuditEvent` and the two methods only)
`BEAD7308691CC5AC49D94EB334021A91`, `Services/SettingsStoreText.cs` (6) `1481699D1DDC2CE4042951DA1EFA384B`,
`Services/ExoConnectionPool.cs` (5: the follow-up note aud-a44 left, rewritten) `638690CBDE29FBE020B38C6CBFC94C95`. The
pages carry no comment change.

## Gates - 2026-10-22 (branch tip `dcfec67c`: every fix and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: exit 0, 0 errors; 19 warnings - the baseline, 19 at
  `132d6e97` - none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0, 6,195
  passed / 3 skipped (the three known AD skips, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`) / 0
  failed, 6,198 total, 5m39s; nothing hung, no Sequence file. Over aud-a52's commit, before parts 4 and 5: 6,180 / 3 / 0.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0. The pages: 808 and 762 lines.

No AD, Exchange Online, Graph, DHCP or IIS call: the directory is `SelfGrantDirectoryStandIn`, Exchange the harness's pool
over stand-in cmdlets or a stand-in resolver, the on-premises session the harness's `DefineOnPremSession`. No coder
self-review verdict is claimed.

## Closeout

Fixed on branch `worktree-agent-a80f49ad9ee618412`, not merged, not pushed. Due at merge: the base app version - the shared
`PermissionValidator`, `AuditService`, `ExoConnectionPool` and `SettingsStoreText`, and the new `SelfGrantRefusalRecord` - and
the Mailbox Permissions and Calendar Permissions module versions: the on-premises re-check, the banner that says when a
refusal's row was dropped, and the bulk loops' `*_NoUser` rows. The bumps are the supervisor's: `ExchangeAdminWeb.csproj` and
`Modules/ModuleCatalog.cs` were outside this brief. No file owned by another agent was edited; `UsageTelemetryServiceTests`
(another agent's) shaped part 4's out parameter and passes unedited.

## Known gaps

- Part 1: the on-premises re-check runs for a grant only (Add, Set), as `SubmitSingle`'s check does; its record carries no
  auto-mapping, which the on-premises grant does not do, and is otherwise `SubmitSingle`'s.
- Parts 1 and 4: `SubmitSingle`'s try/catch and `SelfGrantRefusalRecord.Banner` say the same thing through one constant in
  two code shapes - the pages are line-keyed, and one free line could not hold the try/catch. The page tests pin both.
- Part 2: the once-guard keys on the stored value as stored (ordinal), as aud-a43's appsettings guard does: "Yes" and "yes"
  are named once each.
- Part 3: a row with an empty Target is not refused as an invalid row - outside this finding (a candidate in the hand-back).
- Part 4: only the self-grant refusal's banner learns the writer's outcome; the protected-target denial's banner on both
  pages does not (a candidate in the hand-back).
- Nothing ran against Exchange, a directory or an on-premises session: the harness's stand-ins and the self-grant forest
  stand in for them.
