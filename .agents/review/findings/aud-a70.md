# aud-a70: Permission pages - the on-premises success records are unguarded, and the README and Admin Settings hint texts do not name the directory-read credential dependency

**Severity**: LOW - recorded under the 2026-10-23 stopping rule, not dispatched. (1) `ExecuteOnPrem`'s
success records (Mailbox 717-718 and 723-724; Calendar 665-668) sit in no guard; since aud-a28q only a
missing writer can throw there, and when it does the catch says "The change was written, but a step after
the write failed..." with `Success=false` and records a Failed row beside the success rows. (2) `README.md`
526 ("Self-grant Prevention") does not say group grants now need the protected-principal directory-read
credential (aud-a60). (3) `Components/Pages/AdminSettings.razor` 282 and 289 (the credential's placeholder
and hint: "Required when groups or OUs are configured" / "Read-only AD credential for group membership and
OU checks") should say it is also needed for Mailbox and Calendar Permissions grants to groups.
**Status**: In progress - released by the owner's 2026-10-23 evening ruling (everything open is fixed before any deploy); implemented one sub-item per commit
**Branch**: `fix/a70` (from master `eb017102`)
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a60/a61/a53/a59/psl-9 agent, 2026-10-23, candidates 2-4 and its follow-up)

## Evidence

At `c89f639c`: `Components/Pages/MailboxPermissions.razor` 717-724; `Components/Pages/CalendarPermissions.razor`
665-668 (both line-keyed, edit in place); `README.md` 526; `Components/Pages/AdminSettings.razor` 282, 289.

## Approach (to be written by the implementing agent before code)

- The success records go through the aud-a53 guard (the result stands; a dropped row is said).
- The README section and the two hint texts name the credential dependency and what is refused without it.
- Mutations at the call site: one guard removed; the README sentence dropped (a source check).

### Per sub-item (the implementing agent, 2026-10-23; lines at `eb017102`, re-located there)

The two pages stay line-keyed (ClickGateRegistry, ProgressRegistry): every page edit below is in place, on the lines named,
and each page keeps 808 and 762 lines unless a sub-item says otherwise.

(1) `Components/Pages/MailboxPermissions.razor` 717-718 and 723-724 (`ExecuteOnPrem`'s FullAccess and SendAs rows) and
`Components/Pages/CalendarPermissions.razor` 665-668 (its row) call the writer in no guard, and drop its answer. Each row now
goes through new `SelfGrantRefusalRecord.Recorded(write, logger, target, record)` - aud-a53's guard as one helper: a throw is
logged ("Failed to write audit for the on-premises permission change on <target>; the change stands") and never reaches the
catch, so the change's result stands - and its answer is kept (`rowsWritten`, declared on Mailbox 711; `rowWritten`, Calendar
665). The result is published through new `SelfGrantRefusalRecord.Said(result, written)` (Mailbox 740, Calendar 683): as it
is when every row was written, else with "The audit row could not be written." after its message and its outcome unchanged,
through new `PermissionResult.WithMessage` (every other field carried; `PermissionResultCopyTests` holds each one).
`SubmitSingle`'s success record (Mailbox 501-506, Calendar 477-481) already sat in aud-a53's try/catch but dropped the
writer's answer, so a dropped row was said nowhere: it takes the same helper, with the log text it always had ("the
permission change"), and its result the same `Said` (Mailbox 523, Calendar 500) - the Approach's "a dropped row is said" on
both handlers, so their success records read alike.

(4) `Components/Pages/MailboxPermissions.razor` 745-747 and `Components/Pages/CalendarPermissions.razor` 688-690 (`ExecuteOnPrem`'s
catch row) carry no `extra`, where the success rows carry the identity written and the servicer note
(`ValidatedMailbox.WithWrittenIdentity(ProtectedPrincipalServicing.Extra(onPremValidation.ServicedNote), onPremValidation.Mailbox)`,
Mailbox 705, Calendar 668) - but that dictionary is built inside the try, out of the catch's reach. It is now declared
beside `outcome` above the try (`onPremExtra`, Mailbox 676, Calendar 630), set right after the write-time re-check
(Mailbox 705; Calendar 659, ahead of the step's move to the write), read by the success rows as before (Calendar 668 now
names it), and handed to the catch's row as its `extra`. A throw before the re-check leaves it null: that row names no
identity, there being none validated yet.

(5) `SubmitSingle`'s refusals once the target is validated return with no row: `Components/Pages/MailboxPermissions.razor`
461 (the location read failing), 464 (the location "Unknown"), 474 (on-premises, the operator not permitted); Calendar 431,
434, 444. Each now goes through a local `Refused(refusal)` declared on the step's own line (Mailbox 458, Calendar 428): the
change's own row - its action (`Add<rights>` / `Remove<rights>`, `SetCalendar` / `RemoveCalendar`), Failed, the refusal as
its error, the identity it was bound to and the servicer note - through `SelfGrantRefusalRecord.Banner`, so a row not
written is said, as the catch records a throw before the write. `ExecuteOnPrem`'s own authorization refusal (Mailbox 684,
Calendar 638) is the same refusal on the on-premises write and returned the same way; it is recorded too, under the
handler's `OnPrem_` action (no identity: it comes before the re-check). Every one of these texts now ends "Nothing was
written." (the two page texts gain it; `ExchangeServiceBase.LocationReadFailure`, used by these two pages alone, said
"Nothing was changed." and now says the same as the rest). Not in this sub-item: the ticket and re-authorization refusals
above the validation (candidates in the hand-back). `ExchangeServiceBaseOnPremReadTests`' line pin follows the new call
shape (`PermissionResult.Fail(Refused(...))`).

(9) `Components/Pages/MailboxPermissions.razor` 529 and `Components/Pages/CalendarPermissions.razor` 506: `SubmitSingle`'s catch
ends in `activity.Complete(false, failed)`, unguarded, after the result and the row are set; a completion that throws there
leaves the handler - and, with no ErrorBoundary in this app, the circuit. It now sits in its own guard ("The progress of
the change on <target> could not be completed; the result stands"). `ExecuteOnPrem`'s `finally` completes its activity the
same way (Mailbox 752, Calendar 695), above the staged target's reset, which such a throw skipped: the same guard there. In
place. `ProcessBulk`'s `finally` has the same shape and is outside this sub-item (a candidate in the hand-back).

(17) `Services/PermissionChangeFailure.cs` 38 (`Banner`): the cause is trimmed only. The pool's "stopped" refusal is an
`ObjectDisposedException` (`Services/ExoConnectionPool.cs` 270, 308, 358, 369), whose message puts "Object name: '...'" on a
line of its own, so the banner and the row's error it becomes carried a line break. The cause's line breaks and runs of
white space now read as one space (`OneLine`), so every banner is one line whatever its cause. The pool's own message is
unchanged: other modules show it, and they are not this finding's.

(12) and (14), one cluster: both turn on telling Exchange's answer from a session that failed once the command was sent.
At `eb017102`: `Services/CalendarPermissionService.cs` 368 and 419 - the on-premises set's and removal's one `Invoke(ps)` -
let every failure out as an exception after the aud-a76 mark, so a write Exchange refused was filed Undetermined, "could
not be confirmed" (12). `Services/MailboxPermissionService.cs` `TryRight` (163-174) and `TryRightOnPrem` (185-198) file every
right's failure Failed, and the Calendar cloud writes (40-73, 91-104, through `ExchangeServiceBase.RunAsync` 49-107) make every
failure a failed result: a right whose command was sent and whose session then dropped is filed Failed, though its outcome
is unknown (14).
- One reading of a failed command, new `ExchangeServiceBase.OutcomeUnknown(ex, tracker)`: the failure is its session's - what
  the pool reads as a dead session (`IsConnectionError`, which the command's own invoke records in its tracker before it
  clears the error stream), or a remoting transport failure by its type (new `IsTransportFailure`, which
  `ClassifyConnectionError` now reads too: an on-premises transport failure carries none of the words `IsConnectionError`
  reads) - and not the pre-flight error that proves no cmdlet ran. Anything else is Exchange's answer.
- The record's "sent" flag: `RightOutcome.Unconfirmed` (factory `Unanswered`) and `PermissionResult.UnconfirmedRights` (with
  `Unconfirmed`): sent, and its session failed before Exchange answered. `TryRight` and `TryRightOnPrem` give each right its
  own tracker - the cloud one carried to the operation's, so the pool still discards a poisoned connection - and file a
  session failure `Unanswered`; `MailboxPermissionOutcome` says those rights not confirmed ("Whether SendAs was granted on
  <target> could not be confirmed: <the session's words>. Check the permissions before trying again."), beside what took and
  what Exchange refused. The Calendar cloud writes keep the attempt's tracker and, once marked, make a session failure's result
  unconfirmed. The on-premises Calendar writes catch their invoke: Exchange's answer is a failed result - "Failed on <target>
  calendar (on-premises): <its words>.", as `TryRightOnPrem` makes one (12) - and a session failure an unconfirmed one.
- The pages and the bulk loops file it: each change's own record takes `filedAs: PermissionChangeFailure.FiledAs(result[,
  right])` - Undetermined for an unconfirmed right, per right on Mailbox Permissions' on-premises rows - on `SubmitSingle`
  (Mailbox 505, Calendar 480), `ExecuteOnPrem` (Mailbox 717-718 and 723-724, Calendar 665-668) and the bulk rows
  (`MailboxPermissionService` 371-375, `CalendarPermissionService` 250-254). The banner is the result's own text.
- Every copy of a result carries the new field: `PermissionResult.WithServicedNote` and `WithMessage`, and
  `MigrationService.WithProtectionOutcome` and `WithDetail`, which `MigrationResultRewrapTests` holds to every field (no
  Migration behaviour changes: no migration result is ever unconfirmed).
aud-a76's `AnOnPremCalendarWriteThatFailsOnceSent_GoesOutAsItIs` and `AnOnPremWriteThatThrowsOnceSent_IsRecordedAsUndetermined_WithTheBannersText`
asserted the throw (12) removes; they are retargeted to Exchange's refusal - a failed result, a Failed row - and the
session's failure has tests of its own. Test seam: `ExchangeWriteHarness.OnPremTransportFailures` - the stand-in
Invoke-Command runs the script, then writes a `PSRemotingTransportException` as a session that broke before its answer does.

(15) An Undetermined single change notified no one: `SubmitSingle`'s catch (Mailbox 528-529, Calendar 505-506) and
`ExecuteOnPrem`'s (Mailbox 744-747, Calendar 687-690) send nothing. Each catch now calls new
`PermissionChangeNotifier.NotifyAdminIfUnconfirmedAsync(filed, ...)`: when its row is filed Undetermined, the administrator
notification - its action marked `_UNCONFIRMED`, not a success, the banner as its detail; a throw logged, the result
standing. A result left unconfirmed through (14) reaches the success path's administrator notification already: its action
takes the same mark there (Mailbox 510 and 729, Calendar 485 and 671). No user or owner notification in either case: those
say access changed, which nobody can say. A change that wrote nothing (Failed) is unchanged. Through a helper rather than a
second `Email.SendAdminNotificationAsync(` in `SubmitSingle`: ProgressRegistry holds every such call ahead of the handler's
first `Complete`, which a call in the catch is not. After (12) and (14) no failure the services let out reaches
`ExecuteOnPrem`'s catch at the write step - the call there is kept for the shape, and is not proven by a test (Known gaps).

## Guard proof

Protocol, every round: session-local driver `mutate.py` (session scratchpad, log `a70-mutations.log`, TRX per run). Per
mutant: the MD5 of each file it touches; the edit - an exact-text replacement that must match exactly once, reverting the
fix at its call site; the MD5 and the line count again (each page mutant kept 808 / 762 lines, so ClickGate and
ProgressRegistry ran under it); a Debug build of the test project with 0 errors required; `dotnet test --no-build` over the
round's filter, the failing names taken from the TRX; then the original bytes restored, the write time advanced, the MD5
checked equal, the test project rebuilt with 0 errors and the filter run again green.

### (1) - commit `fix(aud-a70): (1) ...`

Filter `FullyQualifiedName~PermissionPagesSelfGrant|FullyQualifiedName~ClickGate|FullyQualifiedName~ProgressRegistry|FullyQualifiedName~PermissionResultCopy`
(440 tests; E1b over the first and the last, 89). Fixed-file MD5s: `Components/Pages/MailboxPermissions.razor`
`2A05C357D741C6AEF7B355D647413409`, `Components/Pages/CalendarPermissions.razor` `1F8C348AB4808F05ACF0920730B7B1E8`,
`Models/PermissionResult.cs` `95BE8569E6CCAF980D0206F64BE191E0`. Every restore matched and its rerun was green (440/440, 89/89).

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 | Mailbox `ExecuteOnPrem`'s FullAccess row back to the bare writer call (no guard, its answer dropped) | `AEF17122B11D920055B30B41BEDD468A` | 2 of 440: `AnOnPremChange_WhoseRecordThrows_StandsAsWritten_AndSaysTheRowCouldNotBeWritten(page: "Mailbox Permissions")` (the written change shown as failed), `AnOnPremChange_WhoseRowTheWriterDrops_StandsAsWritten_AndSaysTheRowCouldNotBeWritten(page: "Mailbox Permissions")` (the dropped row not said) |
| M1c | the same on Calendar Permissions (the row and its `Said`) | `C3A5C24C76088380EBE06AAEA4ECE258` | 2 of 440: the same two tests' `(page: "Calendar Permissions")` rows |
| E1 | extra: Mailbox `SubmitSingle` publishes its result without `Said` | `73EA2D2705F7E03CEDD70543F815E8D0` | 2 of 440: `AWrittenChange_WhenItsRecordThrows_StandsAsWritten(page: "Mailbox Permissions")`, `AWrittenChange_WhoseRowTheWriterDrops_StandsAsWritten_AndSaysTheRowCouldNotBeWritten(page: "Mailbox Permissions")` |
| E1b | extra: `PermissionResult.WithMessage` drops `AppliedRights` | `PermissionResult.cs` `CBA687A42DCB02F6F99ADE33414F6F84` | 1 of 89: `PermissionResultCopyTests.WithMessage_CarriesEveryFieldButTheMessage` |

No separate pre-fix reproduction: M1 and M1c are each row as it stood at `eb017102`, so each failure is also the finding's
reproduction on that page.

### (4) - commit `fix(aud-a70): (4) ...`

Filter `FullyQualifiedName~PermissionPagesSelfGrant|FullyQualifiedName~ClickGate|FullyQualifiedName~ProgressRegistry` (443
tests). Fixed-file MD5s: `Components/Pages/MailboxPermissions.razor` `4673304C9F774E409822258A01F49BD6`,
`Components/Pages/CalendarPermissions.razor` `8FED9365B2A44F94C02A0B997C4D7077`. Every restore matched; reruns 443/443.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M4 | Mailbox `ExecuteOnPrem`'s catch row without `extra: onPremExtra` | `14A0E700BB6DF7F51B65688995BFB013` | 1 of 443: `AnOnPremChangeThatFails_IsRecordedWithTheIdentityItWasBoundTo(page: "Mailbox Permissions")` |
| M4c | the same on Calendar Permissions | `E0364FE5DED57D99C8E40CFD6AABC9CA` | 1 of 443: the same test's `(page: "Calendar Permissions")` row |

M4 and M4c are the catch rows as they stood at `eb017102`. `AnOnPremChangeThatFailsBeforeItsReCheck_IsRecordedWithNoIdentity`
pins the other side: a throw before the re-check names nothing.

### (5) - commit `fix(aud-a70): (5) ...`

Filter `FullyQualifiedName~PermissionPagesSelfGrant|FullyQualifiedName~ClickGate|FullyQualifiedName~ProgressRegistry|FullyQualifiedName~ExchangeServiceBaseOnPremRead`
(493 tests). Fixed-file MD5s: `Components/Pages/MailboxPermissions.razor` `33C8FCA760B856876DCDF51A3E905C3E`,
`Components/Pages/CalendarPermissions.razor` `A0F05BE65F6856332283EA647F1454DD`. Every restore matched; reruns 493/493.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M5 | Mailbox: the location read's refusal not recorded (`Refused(` removed at 461) | `79B70D84C97C63B7A0AB41951E254D08` | 2 of 493: `ARefusalBeforeTheWrite_IsRecordedAsFailed_NothingWasWritten(page: "Mailbox Permissions", refusal: "the location cannot be read")` (no row), `ExchangeServiceBaseOnPremReadTests.APermissionsPage_RefusesALocationReadThatFailed_BeforeAnyWrite(page: "MailboxPermissions.razor", ...)` (the line pin) |
| M5c | Calendar: the "Unknown" location's refusal not recorded (434) | `EB1BEFCF943C61654DEE5BB132F12DDE` | 1 of 493: `ARefusalBeforeTheWrite_...(page: "Calendar Permissions", refusal: "no mailbox anywhere")` |
| E5 | extra, Mailbox: the on-premises "not permitted" refusal not recorded (474) | `D46BBDF8FB00FD36E2F2E498023904B0` | 1 of 493: `ARefusalBeforeTheWrite_...(page: "Mailbox Permissions", refusal: "on-premises not permitted")` |
| E5b | extra, Mailbox: `ExecuteOnPrem`'s authorization refusal not recorded (684) | `446CCB5A34F558B6CC1C729D672E4806` | 1 of 493: `TheOnPremWrite_WhenNotAuthorized_IsRecordedAsFailed_NothingWasWritten(page: "Mailbox Permissions")` |

Each mutant is its refusal as it stood at `eb017102` but for the "Nothing was written." its text now ends with.

Repair: the (5) commit `9b0aa71b` ran red in the full suite - 2 of 6,713, `ValidatedWriteCallSiteTests.EveryPageWriteAudit_RecordsTheIdentityWritten`
(both pages), a count pin of `ValidatedMailbox.WithWrittenIdentity(` calls per page (3) that the refusal row's own call makes
4. The round's filter had not included that class. The follow-up commit raises the pin to 4 and names the new row; no
production change. From here every commit runs the full suite before it lands.

### (9) - commit `fix(aud-a70): (9) ...`

Filter `FullyQualifiedName~PermissionPagesSelfGrant|FullyQualifiedName~ClickGate|FullyQualifiedName~ProgressRegistry` (455
tests). Fixed-file MD5s: `Components/Pages/MailboxPermissions.razor` `632CF15C0209D2C29A912E9BE096B33B`,
`Components/Pages/CalendarPermissions.razor` `405930F4FA2E3AB8F21D94339D205D19`. Every restore matched; reruns 455/455.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M9 | Mailbox `SubmitSingle`'s catch: the completion's guard removed | `0DB19052F5B0DED8C2232EA7FD7738B6` | 1 of 455: `ASingleChange_WhoseProgressCannotComplete_EndsWithItsBanner_AndNothingEscapesTheHandler(page: "Mailbox Permissions")` - the throw left the handler |
| M9c | the same on Calendar Permissions | `DCE47A8478B5FACBDB34687316D7C25C` | 1 of 455: the same test's `(page: "Calendar Permissions")` row |
| E9 | extra, Mailbox `ExecuteOnPrem`'s `finally`: the completion's guard removed | `E76F62E5B88817231BECA6CCAAE15B29` | 1 of 455: `AnOnPremChange_WhoseProgressCannotComplete_EndsWithItsResult_AndNothingEscapesTheHandler(page: "Mailbox Permissions")` |
| E9c | the same on Calendar Permissions | `43AA1656013D615884DE385C876FB2A8` | 1 of 455: the same test's `(page: "Calendar Permissions")` row |

Each mutant is its completion call as it stood at `eb017102`.

### (17) - commit `fix(aud-a70): (17) ...`

Filter `FullyQualifiedName~PermissionPagesSelfGrant|FullyQualifiedName~PermissionWriteSubmission` (119 tests). Fixed-file MD5:
`Services/PermissionChangeFailure.cs` `8EC30F93A434695D37990019FC0FB59C`. The restore matched; rerun 119/119.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M17 | `Banner`'s cause trimmed only again (`OneLine` removed) | `A9B8DCD1F1195D08A053D6007AB65CB6` | 3 of 119: `TheStepBanner_IsOneLine_WhateverLinesItsCauseSpans`, `ACloudWriteWhoseBorrowIsRefused_IsRecordedAsFailed_NothingWasWritten(page: "Mailbox Permissions", refusal: "pool stopped")` and its `(page: "Calendar Permissions", ...)` row - the line break in the banner and the row |

M17 is the cause's line as it stood at `eb017102`.

### (12) and (14) - commit `fix(aud-a70): (12), (14) ...`

Filter `FullyQualifiedName~PermissionPagesSelfGrant|FullyQualifiedName~PermissionWriteSubmission|FullyQualifiedName~MailboxPermissionOutcome|FullyQualifiedName~ClickGate|FullyQualifiedName~ProgressRegistry`
(508 tests). Fixed-file MD5s: `Services/MailboxPermissionService.cs` `074F0F0302E52F19983767DC39334286`,
`Services/CalendarPermissionService.cs` `BB04EA3613D8625E0AD69C2E696D0DCD`, `Services/ExchangeServiceBase.cs`
`C38AAB8164170255FE3B9B83E7A85D69`, `Components/Pages/MailboxPermissions.razor` `E2AEC0C06175EE28E63CA82E75230037`
(unmutated: `Components/Pages/CalendarPermissions.razor` `3527941B4E6873F8183ABE0B7D8B4563`, `Services/MailboxPermissionOutcome.cs`
`218A026E6222EAAE960110D3AE4E6EEE`, `Models/PermissionResult.cs` `31291F11CF3ED6DA2D52ECA7414469CE`,
`Services/PermissionChangeFailure.cs` `698E2F8B08211DA8DFAB2018782076E2`). Every restore matched; reruns 508/508.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M12 | the on-premises Calendar set's invoke let out again (its catch removed) | `CalendarPermissionService.cs` `14590BD914AACBC44FA8C2D922C81B2A` | 4 of 508: `AnOnPremCalendarWrite_ExchangeRefuses_IsAFailedResult_NotAThrow(write: "SetCalendarPermissionOnPrem", ...)`, `AnOnPremCalendarWrite_WhoseSessionBreaksOnceSent_IsUnconfirmed(write: "SetCalendarPermissionOnPrem", ...)`, `AnOnPremCalendarWriteExchangeRefuses_IsItsOwnFailedRow_AsMailboxPermissionsIs`, `AnOnPremWriteWhoseSessionBreaksOnceSent_IsRecordedAsUndetermined(page: "Calendar Permissions")` |
| M14a | `TryRight` files every failure Failed again | `MailboxPermissionService.cs` `DD12EF0BB493DFEB0675AF7D556888E1` | 4 of 508: both `ACloudMailboxRight_WhoseSessionFailsOnceSent_IsUnconfirmed_AndTheRightThatTookStands` rows, `ACloudWriteWhoseSessionFailsOnceSent_IsRecordedAsUndetermined(page: "Mailbox Permissions")`, `ABulkRow_WhoseWriteIsUnconfirmed_IsRecordedAsUndetermined(mailbox: True)` |
| M14b | `TryRightOnPrem` files every failure Failed again | `F0879D40B15C472F895F7B56CC3A4E08` | 3 of 508: both `AnOnPremMailboxRight_WhoseSessionBreaksOnceSent_IsUnconfirmed` rows, `AnOnPremWriteWhoseSessionBreaksOnceSent_IsRecordedAsUndetermined(page: "Mailbox Permissions")` |
| M14c | the Calendar cloud set returns RunAsync's result as it is | `CalendarPermissionService.cs` `A97AC3EBB259095EE9FF6BDB73DF5B6C` | 3 of 508: `ACloudCalendarWrite_WhoseSessionFailsOnceSent_IsUnconfirmed(write: "SetCalendarPermission")`, `ACloudWriteWhoseSessionFailsOnceSent_IsRecordedAsUndetermined(page: "Calendar Permissions")`, `ABulkRow_WhoseWriteIsUnconfirmed_IsRecordedAsUndetermined(mailbox: False)` |
| M14d | Mailbox `SubmitSingle`'s row without `filedAs` | `MailboxPermissions.razor` `25FF16452F40559879E000625DDB8196` | 1 of 508: `ACloudWriteWhoseSessionFailsOnceSent_IsRecordedAsUndetermined(page: "Mailbox Permissions")` - filed Failed |
| E14e | extra: `OutcomeUnknown` ignores the pre-flight error | `ExchangeServiceBase.cs` `F68A708F180F18717F9292622DF22BDB` | 1 of 508: `ACloudMailboxRight_ExchangeRefuses_OrThatMeetsThePreFlightError_IsFailed_AsBefore(failure: "You must call Connect-ExchangeOnline ...")` |
| E14f | extra: `ClassifyConnectionError` reads no transport failure | `7171E3FBFB49927A966C30DD2EE0E61F` | 6 of 508: both `AnOnPremMailboxRight_WhoseSessionBreaksOnceSent_...`, both `AnOnPremCalendarWrite_WhoseSessionBreaksOnceSent_...` rows, both `AnOnPremWriteWhoseSessionBreaksOnceSent_IsRecordedAsUndetermined` rows |
| E14g | extra: the Mailbox bulk row without `filedAs` | `MailboxPermissionService.cs` `27230E71FAA95BC9377382498D76C0CD` | 1 of 508: `ABulkRow_WhoseWriteIsUnconfirmed_IsRecordedAsUndetermined(mailbox: True)` |

M12, M14a, M14b and M14c are each write as it stood at `eb017102` (M12 the set's one invoke; the removal's catch has the same
shape, driven by the removal rows of the same tests); M14d the row as it stood.

### (15) - commit `fix(aud-a70): (15) ...`

Filter `FullyQualifiedName~PermissionPagesSelfGrant|FullyQualifiedName~ClickGate|FullyQualifiedName~ProgressRegistry|FullyQualifiedName~PermissionWriteSubmission`
(496 tests). Fixed-file MD5s: `Components/Pages/MailboxPermissions.razor` `F82E851C85151B71A618C2B0213E7052`,
`Components/Pages/CalendarPermissions.razor` `F4B8A74937A5AB9FE5D833483F568E66`, `Services/PermissionChangeNotifier.cs`
`DB2A761821841D6A08B7DE5F3EFD9602`. Every restore matched; reruns 496/496.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M15 | Calendar `SubmitSingle`'s catch without the Undetermined notification | `CalendarPermissions.razor` `62BE66B5C6736302FFEEBA5822EEF7BB` | 1 of 496: `ASingleChangeItsCatchFilesUndetermined_TellsTheAdministrators_AndNoOneElse` - no one told |
| M15b | Mailbox `SubmitSingle`'s administrator notification without the unconfirmed mark | `MailboxPermissions.razor` `4E9F45DEBCB94F6DF31165B916E82E3B` | 1 of 496: `AWriteWhoseOutcomeIsUnknown_TellsTheAdministratorsItIsUnconfirmed(page: "Mailbox Permissions")` |
| E15 | extra: the helper's Undetermined gate removed | `PermissionChangeNotifier.cs` `1A5C0C211CAE795DF034C8195308CA74` | 3 of 496: `TheUnconfirmedNotification_IsSentOnlyForAnUndeterminedChange_AndAThrowIsLogged`, both `AChangeThatWroteNothing_SendsNoUnconfirmedNotification` rows |
| E15b | extra: Mailbox `ExecuteOnPrem`'s administrator notification without the mark | `MailboxPermissions.razor` `A687578320491DC3E96324BC73AD9D30` | 1 of 496: `AnOnPremWriteWhoseOutcomeIsUnknown_TellsTheAdministratorsItIsUnconfirmed(page: "Mailbox Permissions")` |

M15 is the catch as it stood at `eb017102`; M15b and E15b the notifications' actions as they stood. `ExecuteOnPrem`'s catch
call has no mutant: no failure reaches it there (Known gaps).

## Also held (aud-a71/a69/a72 agent, 2026-10-23) - LOW

(4) `ExecuteOnPrem`'s failure row carries neither the servicer note nor the written identity (Mailbox 745-747,
Calendar 688-690). (5) `SubmitSingle` refusals before the write still return with no audit row: the
location-read failure (Mailbox 461 / Calendar 431), the "Unknown" location, and the on-prem authorization
refusal. (6) aud-a69 residual: without the directory-read credential, a grantee Exchange names is looked up
as the app pool; a name Exchange Online and the directory resolve to different objects, hidden from the app
pool, on the on-prem write path, would not be compared - refusing every grant without the credential would
close it (an owner call). (7) The operator's own lookups still run as the app pool; with a UPN-only sign-in
(which Negotiate never produces) a hidden account reads as "no account" and no group is checked. (8) A
dynamic group's members are as of Exchange's last calculation. (9) Latent: an `activity.Complete` that
throws inside `SubmitSingle`'s catch still escapes the handler (the result and the audit row are set first).

## Also held (codex, perm-batch-8, 2026-10-23) - LOW

(10) `Services/PermissionValidator.cs` 793 and `Services/SelfGrantRun.cs` 45: with the directory-read credential
configured, every single grant to a person Exchange names fetches the secret before the code knows it needs
credentialed visibility; acquire it lazily for directory-only grantees, group evaluation and synced nested
groups. (11) `Services/ExchangeGroupMembers.cs` 140: a dynamic distribution group whose filter currently
matches nobody returns zero rows and is refused as incomplete, so an empty dynamic group can never be granted
while empty; distinguish "not calculated/unavailable" from an empty calculated membership if Exchange offers a
signal, else document the refusal.

## Also held (aud-a73 agent, 2026-10-23) - LOW

(12) `SetCalendarPermissionOnPremAsync` and `RemoveCalendarPermissionOnPremAsync` let a cmdlet refusal out as an
exception rather than a failed result, so a write Exchange refused reads "could not be confirmed"; the Mailbox
on-prem write reports a refusal through `TryRightOnPrem` - do the same. (13) `IsSuccessfulAuditResult` treats
any result but "Failed" as a success, so an Undetermined permission row (aud-a73) counts as a success in usage
telemetry and completes the audit's trace operation as succeeded (the aud-a28z part-5 rule kept for
eligibility checks; `docs/UsageTelemetry-Plan.md` 343 specified `result == "Success"`) - an owner call whether
Undetermined counts as neither.

## Also held (aud-a76 agent, 2026-10-23) - LOW

(14) A right whose command was sent and whose session then failed mid-command is filed Failed through the
`TryRight`/`TryRightOnPrem` results (Mailbox) and the `RunAsync` result (Calendar cloud) though its outcome is
unknown; only Calendar on-premises files it Undetermined. The agent graded it MEDIUM by aud-a73's measure;
the supervisor holds it LOW: the trigger is a session drop in the middle of one cmdlet, Failed is the
conservative reading that sends the operator to recheck, and the audit text still carries the cause. Fix
shape: the per-right results carry a "sent" flag the pages file as Undetermined. (15) An Undetermined
(possibly applied) single change sends no administrator, user or owner notification; the Constitution asks
for an administrator notification on every mutating action. (16) A Calendar removal retried after a
pre-flight error, whose retry cannot connect, is filed Undetermined though the pool treats that error as proof
nothing ran (conservative, by design). (17) The pool's "stopped" refusal message contains a line break, so
the banner and the audit row do too.
