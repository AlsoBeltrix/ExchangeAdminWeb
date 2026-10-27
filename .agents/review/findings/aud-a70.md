# aud-a70: Permission pages - the on-premises success records are unguarded, and the README and Admin Settings hint texts do not name the directory-read credential dependency

**Severity**: LOW - recorded under the 2026-10-23 stopping rule, not dispatched. (1) `ExecuteOnPrem`'s
success records (Mailbox 717-718 and 723-724; Calendar 665-668) sit in no guard; since aud-a28q only a
missing writer can throw there, and when it does the catch says "The change was written, but a step after
the write failed..." with `Success=false` and records a Failed row beside the success rows. (2) `README.md`
526 ("Self-grant Prevention") does not say group grants now need the protected-principal directory-read
credential (aud-a60). (3) `Components/Pages/AdminSettings.razor` 282 and 289 (the credential's placeholder
and hint: "Required when groups or OUs are configured" / "Read-only AD credential for group membership and
OU checks") should say it is also needed for Mailbox and Calendar Permissions grants to groups.
**Status**: 15 of 17 sub-items merged to master `ea565cbd` (head `d609001e`); codex perm-batch-11 raised kd-43 (against (12)/(14)) and kd-44 (against (15)), both MEDIUM, since merged; kd-44 was then reversed by owner ruling 2026-10-27 (`kd-44.md`), so (15) stands as it was written: administrators only. (13) fixed on `fix/kd44-a70-13` under the 2026-10-27 mandate (see "(13) - fixed 2026-10-27" below) and merged at `d5d4524e`, unreviewed until the single final codex-commercial review. (6) fixed 2026-10-27 on `fix/a70-6` under the owner's YES of 2026-10-26 (see "(6) - every grant refused without the directory-read credential" at the end of this record): verified on branch, gates green (7,530 tests), awaiting merge, unreviewed until the single final codex-commercial review; base app bump owed (`PermissionValidator`, `SelfGrantDirectory` are shared). (10) the coder's dispute upheld by the owner 2026-10-26, closed; (16) kept by design. One mid-branch commit `9b0aa71b` was red on the full suite and repaired by `dc3abcd7` (noted, not a defect on the merged head)
**Branch**: `fix/a70` (from master `eb017102`); (6) `fix/a70-6` (from master `7ef50c41`)
**Commit**: (1) `13909c2f`; (4) `83311a2b`; (5) `9b0aa71b` and its count-pin repair `dc3abcd7`; (9) `fe99aff4`; (17) `d0ed01eb`; (12), (14) `7576cea7`; (15) `0ef2ac77`; (7) `32d548ec`; (11), (8) `f374e13a`; (2), (3) `a4155f5c`; module versions and this record's close: the branch head. (6): `d7c9ec5c` (the shared check, Mailbox Permissions), `18d2467d` (Calendar Permissions), `36a9f2e9` (the docs), and the branch head (this record's gates)
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
say access changed, which nobody can say (kd-44 added a distinct, neutral notice to them; the owner's 2026-10-27 ruling
reversed it - administrators only, as here; `kd-44.md`). A change that wrote nothing (Failed) is unchanged. Through a helper rather than a
second `Email.SendAdminNotificationAsync(` in `SubmitSingle`: ProgressRegistry holds every such call ahead of the handler's
first `Complete`, which a call in the catch is not. After (12) and (14) no failure the services let out reaches
`ExecuteOnPrem`'s catch at the write step - the call there is kept for the shape, and is not proven by a test (Known gaps).

(7) `Services/PermissionValidator.cs` 745-752 (`EstablishOperator`, a sign-in carrying a UPN claim and no SID):
`SelfGrantDirectory.FindByUserPrincipalName` (`Services/SelfGrantDirectory.cs` 117-118) searched under the session's
identity, and none found is "no on-premises account" - `CompareInDirectoryAsync` then asks about no group (854). An account
that identity cannot see read as none. The lookup now runs under the directory-read credential, had once per run in
`ValidateSelfGrantAsync` (679) for such a sign-in only; without it the check refuses, naming the credential's problem ("the
signed-in operator '<x>' could not be looked up in the on-premises directory by their user principal name: <problem>"). The
SID lookup stays the session's: none or several there already refuse. No sign-in the app takes today carries a UPN claim
without a SID (Negotiate), so nothing a deployment does now changes.

(11) `Services/ExchangeGroupMembers.cs` 140-143: an empty dynamic membership throws as a membership that could not be read,
and the check's refusal reads like an outage. Exchange Online documents no signal that tells an empty calculated membership
from one not yet calculated: a group whose first calculation is still running "has no members", and
`Get-DynamicDistributionGroup`'s `CalculatedMembershipUpdateTime` says when the group was last refreshed, not whether it has
been calculated (Microsoft Learn, "Troubleshoot dynamic distribution group membership issues"; "Modern dynamic distribution
groups in Exchange Online" - read, not probed). So the record's "else document the refusal": the refusal stays, typed
(`ExchangeDynamicGroupEmptyException`, naming the group read empty, root or nested), the self-grant check says which group
and why (`PermissionValidator.CompareInExchangeGroupAsync`, beside aud-a61's bound refusal), and the README says so.
(8) A dynamic group's members are as of Exchange's last calculation: nothing in the app can see later - it does not
evaluate the filter, by aud-a72's design - so the README says so, with Exchange's documented refresh (about every 24 hours;
within about two hours of a filter change). No code change.

(2) `README.md` 526 (Self-grant Prevention) is one line and says nothing of what the check needs. It gains what the check
compares, that it needs the Directory Read Credential (Admin Settings, Protected principals), what is refused without it -
aud-a60's grants to a group the directory holds, aud-a69's to a grantee only the directory holds, (7)'s operator known by a
UPN alone - and what it costs when configured (one fetch per check, once per bulk file). (3)
`Components/Pages/AdminSettings.razor` 282 (the credential's placeholder) and 289 (its hint) named the protected-principal
rules only; they now say Mailbox and Calendar Permissions need it to grant to a group or to a grantee only the on-premises
directory holds, and that such a grant is refused without it. In place (1,436 lines). Neither text names an environment.

Not implemented, each with its reason:
- (6) An owner call (the record says so): whether to refuse every grant while the directory-read credential cannot be had.
  Nothing changed beyond fail-closed as it stands: without the credential, a grantee Exchange names is still looked up as
  the session (aud-a69's refusal-only evidence). Recommendation in the hand-back. The owner said yes on 2026-10-26; fixed
  2026-10-27 on `fix/a70-6` - see "(6) - every grant refused without the directory-read credential" at the end of this record.
- (10) Disputed - see Coder dispute.
- (13) An owner call when this was written: whether Undetermined counts as neither success nor failure in usage telemetry
  and the audit's trace operation. Since fixed as a bug under the owner's 2026-10-27 mandate, on `fix/kd44-a70-13` - see
  "(13) - fixed 2026-10-27" at the end of this record.
- (16) Kept by design: aud-a76's Plan (item 2) chose not to rest "nothing was written" on the pool's reading of the
  pre-flight error, so a Calendar removal retried after it, whose retry cannot connect, stays Undetermined - conservative,
  the operator rechecks. `PermissionWriteSubmission`'s remarks and `CalendarPermissionService` 88-89 say so already; nothing
  to change.

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

### (7) - commit `fix(aud-a70): (7) ...`

Filter `FullyQualifiedName~PermissionValidator|FullyQualifiedName~SelfGrant|FullyQualifiedName~ProtectedGroupMembership` (410
tests). Fixed-file MD5s: `Services/SelfGrantDirectory.cs` `DC8279156F7F6906F828B383588B2AD8`, `Services/PermissionValidator.cs`
`CB64D44A14286D7510153844ED15ED7A`. Every restore matched; reruns 410/410.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M7 | the UPN lookup under the session's identity again (`FindByUserPrincipalName` passing no credential) | `SelfGrantDirectory.cs` `D55EC14B56FFC7103A3E63F9DBE5B26B` | 2 of 410: both `AnOperatorKnownByTheirUpnClaim_HiddenFromTheSessionIdentity_IsFoundUnderTheDirectoryReadCredential` rows - the operator inside allowed; either way the lookup as the session |
| E7 | extra: no refusal without the credential (the lookup made with none) | `PermissionValidator.cs` `B7E0D2B4B04293BD13F27DED8A5193C2` | 5 of 410: every `AnOperatorKnownOnlyByTheirUpnClaim_WithoutTheDirectoryReadCredential_IsRefused_AsIncomplete` row |

M7 is the lookup as it stood at `eb017102`.

### (11) and (8) - commit `fix(aud-a70): (11), (8) ...`

Filter `FullyQualifiedName~PermissionValidator|FullyQualifiedName~ExchangeGroupMembers|FullyQualifiedName~SelfGrantDocumentation|FullyQualifiedName~ExchangeIdentityResolver`
(281 tests). Fixed-file MD5s: `Services/PermissionValidator.cs` `6FD31108171C506095E4CBBF7FB545FE`,
`Services/ExchangeGroupMembers.cs` `62EDB50DF299A38570812CE7A30BAC9F`, `README.md` `43B9F1FAFD9EEA639F21ED3A2EEB4A53`. Every
restore matched; reruns 281/281.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M11 | the validator's catch for the empty dynamic group removed (the general refusal again) | `PermissionValidator.cs` `BE3B955EC3C97072B3826BEC7FA58708` | 1 of 281: `ADynamicDistributionGroupWhoseMembersCannotBeRead_IsRefused_AsIncomplete(shape: "no member is reported")` |
| M11b | the read throws an untyped exception again | `ExchangeGroupMembers.cs` `06C89D236A50ECA0A14B5664D6A49D19` | 2 of 281: the same row, `ADynamicGroupThatReportsNoMember_ThrowsNamingIt_AtTheRootOrNested` |
| M8 | the README's dynamic-group sentence dropped (a source check) | `README.md` `47D5D22027E9F71F33D04CA74DCE69DB` | 1 of 281: `SelfGrantDocumentationTests.TheReadme_SaysADynamicGroupIsCheckedAsExchangeLastCalculatedIt_AndAnEmptyOneIsRefused` |

M11b is the throw as it stood at `eb017102`; M8 the README as it stood.

### (2) and (3) - commit `fix(aud-a70): (2), (3) ...`

Filter `FullyQualifiedName~SelfGrantDocumentation|FullyQualifiedName~SettingsStoreText|FullyQualifiedName~AdminPageDirtyState|FullyQualifiedName~ProgressRegistry|FullyQualifiedName~ClickGate`
(404 tests). Fixed-file MD5s: `README.md` `F91C4EC5AF40070345B4E8C1EBD887C3`, `Components/Pages/AdminSettings.razor`
`412A3EB35D9FC7593149D53CE9A28359`. Every restore matched; reruns 404/404.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M2 | the README's credential sentence dropped (a source check) | `README.md` `62F0A48A4A05120AF46672DECE797E6E` | 1 of 404: `SelfGrantDocumentationTests.TheReadme_SaysTheCheckNeedsTheDirectoryReadCredential_AndWhatItRefusesWithoutIt` |
| M3 | the Admin Settings hint back to its old text | `AdminSettings.razor` `1CB718A6C48F122E7715ADBFB211CCF9` | 1 of 404: `SelfGrantDocumentationTests.AdminSettings_SaysTheDirectoryReadCredentialIsWhatPermissionGrantsToGroupsNeed` |
| E3 | extra: the placeholder back to its old text | `56E2DF505FE07AFE2F8823DDFD44B98F` | 1 of 404: the same test |

M2, M3 and E3 are the texts as they stood at `eb017102`. The full suite before this commit found the placeholder is also how
`SettingsNoOpAndUnreadableSecretTests` finds the credential's input (2 tests); its locator constant follows the new text.

## Files changed

Production: `Components/Pages/MailboxPermissions.razor`, `Components/Pages/CalendarPermissions.razor` (both in place, 808 and
762 lines, no registry entry moved); `Components/Pages/AdminSettings.razor` (in place); `README.md`;
`Models/PermissionResult.cs`; `Services/SelfGrantRefusalRecord.cs`; `Services/PermissionChangeFailure.cs`;
`Services/PermissionChangeNotifier.cs`; `Services/ExchangeServiceBase.cs`; `Services/MailboxPermissionOutcome.cs`;
`Services/MailboxPermissionService.cs`; `Services/CalendarPermissionService.cs`; `Services/MigrationService.cs` (its two copies
carry the new field only); `Services/PermissionValidator.cs`; `Services/SelfGrantDirectory.cs`; `Services/ExchangeGroupMembers.cs`;
`Modules/ModuleCatalog.cs` (Mailbox Permissions 1.4.1 -> 1.5.0, Calendar Permissions 1.3.2 -> 1.4.0). Tests:
`PermissionPagesSelfGrantTests.cs`, `PermissionResultCopyTests.cs` (new), `ExchangeServiceBaseOnPremReadTests.cs`,
`ValidatedWriteCallSiteTests.cs`, `ExchangeWriteHarness.cs`, `PermissionWriteSubmissionTests.cs`,
`MailboxPermissionOutcomeTests.cs`, `PermissionValidatorSelfGrantTests.cs`, `ExchangeGroupMembersTests.cs`,
`SettingsNoOpAndUnreadableSecretTests.cs` (its locator), `SelfGrantDocumentationTests.cs` (new). This record. App-wide: yes (shared services and the model); the base app version is
not bumped here - the supervisor's.

## Coder dispute

(10) Not implemented. Acquiring the credential lazily for a person Exchange names means looking that grantee up under the
app pool's identity whenever the credential IS configured. For such a grantee the lookup's one job is to see objects the
typed name also names in the on-premises directory - a group Exchange Online does not hold, say - and the ones the app pool
cannot see are exactly the ones a session lookup misses. That is aud-a69's residual, (6), today confined to deployments
without the credential; a lazy acquisition would extend it to every deployment and rest the check's safety on the app
pool's visibility (Architectural Invariant 7; Known Failure Class 3). The code cannot know it does not need credentialed
visibility without having it. The cost stands as aud-a69 recorded it: one secret-store call per check, once per bulk file.
Caching the secret across checks would remove it, and is a credential change that needs a plan of its own (Constitution,
Planning Rules). Recommendation: decline (10), or route it with (6) to the owner.

## Known gaps

- (6) owner YES 2026-10-26, fixed 2026-10-27 on `fix/a70-6` (its own section at the end, with its own known gaps); (13) fixed
  2026-10-27 (its own section below); (16) kept by design (above).
- (14)'s reading of a failed command rests on the pool's own signals: an Exchange refusal whose words name a session or a
  connection is read as the session's (Undetermined - the conservative side); a session failure whose words name neither
  and is not a remoting transport failure is read as Exchange's answer (Failed, as before this fix). Labelled assumption, not
  probed: an on-premises session that breaks mid-command reaches Invoke-Command's error stream as a
  `PSRemotingTransportException` (the harness models it so).
- (14) on the bulk paths: the row's audit record is Undetermined and its message says not confirmed, but its CSV entry keeps
  the status FAILED and counts among the run's failures - the report's statuses are unchanged.
- (15): no user or owner notification for an unconfirmed change (they say access changed) - kd-44 added a distinct notice to
  them and the owner's 2026-10-27 ruling reversed it (`kd-44.md`): administrators only, as (15) left it. After (12) and (14) no failure the
  services let out reaches `ExecuteOnPrem`'s catch at the write step, so its Undetermined notification is kept for the shape
  and unproven by any test; `SubmitSingle`'s is proven (the Calendar removal whose retry cannot connect).
- (4): the servicer note rides the same dictionary as the identity written, which the tests pin; no serviced fixture drives
  a protected target through the on-premises catch.
- (7), (11): labelled assumptions as their sources record them - aud-a60's (the directory-read credential sees every account),
  and Microsoft's documentation for dynamic groups (read, not probed).
- (8): documentation only - the app does not evaluate a dynamic group's filter.
- Rows already written stay as written. Nothing ran against Exchange, a directory, Graph or a secret store: the harness's
  pools and stand-ins, its on-premises session through the services' own seams, `SelfGrantDirectoryStandIn` and a stand-in
  secret fetch.

## Gates - 2026-10-23 (branch head: the twelve commits above)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: exit 0, 0 errors ("error CS" 0 times); 19 warnings - the
  baseline - none in a touched file (AccountLockoutRemediationService, Comms10kApprovedSetTests, GroupManagement,
  HeaderAnalysisService, MessageTrace, MigrationReportExportProcessor, RiskyUsers, SelfServiceGroups).
- `dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4`: exit 0, 6,758 passed / 0 skipped / 0 failed, 6,758 total, 6m23s (Debug, as the gate is written).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `git add -A` then `pwsh -NoProfile -File tools/Test-AsciiOnly.ps1`: exit 0 ("ASCII lint passed"). The pages: 808 and 762 lines; Admin
  Settings 1,436.
- The full suite on each commit: (1) 6,701 and (4) 6,705, run just after each landed, green; (5) `9b0aa71b` red - 2 of
  6,713, a count pin - repaired by `dc3abcd7` (6,713/6,713; see (5)'s guard proof); from there each commit ran it before it
  landed, green, its count in its message.

No Exchange, Active Directory, Graph, DHCP, IIS or secret-store call: Exchange is the harness's pools over stand-in cmdlets,
on-premises Exchange its stand-in session through the services' own seams, the directory `SelfGrantDirectoryStandIn`, the
secret store a stand-in fetch. No coder self-review verdict is claimed.

Versions: Mailbox Permissions 1.4.1 -> 1.5.0 and Calendar Permissions 1.3.2 -> 1.4.0 (`Modules/ModuleCatalog.cs`), one
minor step each. App-wide: yes - `ExchangeServiceBase`, `PermissionResult`, `PermissionChangeFailure`,
`PermissionChangeNotifier`, `SelfGrantRefusalRecord`, `PermissionValidator`, `SelfGrantDirectory`, `ExchangeGroupMembers`,
`MailboxPermissionOutcome` are shared; the base app version is not bumped here (the supervisor's).

## Closeout

Fixed on branch `fix/a70`, not merged, not pushed - released by the owner's 2026-10-23 evening ruling (everything open is
fixed before any deploy). Owner calls (6) and (13) not implemented; (10) disputed; (16) kept by design. Pending: codex review
of the branch, then merge and branch deletion by the supervisor; the owner's calls on (6) and (13), and on (10)'s dispute.
`.agents/review/index.md`'s row is the supervisor's to move.

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

## Reviewer comments (supervisor, perm-batch-11)

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier=standard pair (grade fallback, owner 2026-08-03) [escalated: T1 *secret* test path]; codex-cli 0.163.0; base eb017102 head d609001e (merged ea565cbd); 2026-10-23 evening; capability proof succeeded (AGENTS.md read, git show --stat d609001e). Two MEDIUM candidates, admitted as kd-43 and kd-44. Codex confirmed clean: the UPN-only lookup, the dynamic-group refusal, the guarded success rows, the on-prem refusal and failure rows, Undetermined bulk filing, the completion guards and the banner collapse; MigrationService.cs is in the diff only for its result-copy helpers carrying UnconfirmedRights. Artefacts: `.agents/review/perm-batch-11.result.txt`, `.stream.log`, `prompt-perm-batch-11.md`.

## (13) - fixed 2026-10-27 (implementer, branch `fix/kd44-a70-13`, from master `3f7eaa07`)

Held as an owner call until the owner asked "how is ineligible a success" (`.agents/decisions.md` 2026-10-26, item 4) and
restated the mandate on 2026-10-27 (same file, the entry above it, item 0: find and fix bugs, remove or significantly change
no intended functionality). Under it this is a bug: `docs/UsageTelemetry-Plan.md` (the `AuditService` change, about line 343)
specified a usage action as successful when its result is `"Success"`, and the hook was built on the trace's older
`IsSuccessfulAuditResult`, which read every result but `"Failed"` as one.

**The fix.** `Services/AuditService.cs` `IsSuccessfulAuditResult` (597 at `3f7eaa07`) is true for `"Success"` alone,
case-insensitively as before; it is `internal` now so the tests read it. Its two callers in `WriteAuditEvent` are
unchanged: the trace's implicit operation (523-525) and the usage count (542-545). So Ineligible, Undetermined, Cancelled
(kd-57, the next commit), Failed and a missing result complete the implicit operation as not succeeded and count as an
action that did not succeed. One wording change rides with it: that operation's completion message said "Audit event
recorded failure" for every non-success, which would now call an ineligible check a failure. A Failed row keeps those words;
any other is "Audit event recorded result <result>" (`NotSucceededMessage`).

**Every result the app files** (the brief's enumeration; a grep for `"result"`, any case, through `Components`, `Services`,
`Modules` and `Models`, and every `filedAs:` argument). An audit row's result is set only in `AuditService`'s methods, or by
a caller through `extra` (merged last):
- `Success` - the success of every method: `LogMailboxPermission`, `LogCalendarPermission` and `LogMigrationAction` through
  `ResultText`; `LogMigrationBatch`, `LogModuleAction`, `LogLookupAction`, `LogMfaResetAction`, `LogConferenceRoomAction` and
  `LogADAttributeEdit` from their bool; `LogSettingsChange` always; `LogMigrationCheck` for the status Eligible.
- `Failed` - the failure of every method by its bool or `ResultText`; `LogSettingsChangeFailed` and
  `LogConnectionPoolDrainFailed` always.
- `Undetermined` - `ResultText` for `filedAs: AuditResult.Undetermined` (the permission pages and services through
  `PermissionChangeFailure.AuditResultOf` and `FiledAs`, Migration through its `neverSent` choice); `LogMigrationCheck` for any
  status but Eligible and Ineligible (aud-a28z part 5); and Blocked Senders' unblock whose outcome is unknown, through
  `extra["result"]` (kd-6, `FiledUndetermined`).
- `Ineligible` - `LogMigrationCheck` for the status Ineligible.
- Nothing else. No other caller puts a `"result"` key in `extra`; `ConferenceRoomBulkProcessor`'s `"Result"` is a line of an
  administrator mail, not an audit field. No module files a success under another word, so no spelling needed mapping.
- Not audit results, so not this predicate: the trace steps' own words (`Success`, `Failed`, `Blocked`, `Serviced`,
  `Skipped`, `Warning`). `OperationTraceService.Step` completes a standalone step's operation as succeeded for every step
  result but "Failed" (about line 128) - the same shape, in the diagnostic trace only, with no usage count; noted, not changed.
- The other readers of an audit result are unchanged: the Admin Event Log's filter and badge, `EventLogCsvFormatter`,
  `ADAttributeEditorUndoService` (its own category) and the writer's dropped-row report.

**aud-a28z part 5.** Its rule - an eligibility check is filed Success, Ineligible or Undetermined for what the check found -
does not depend on this predicate: it decides the row's result, which the predicate then reads. It is kept as it is
(`LogMigrationCheck` untouched). What part 5 also recorded - "the usage count's success flag is unchanged: any result but
'Failed' counts as a success, Ineligible and Undetermined alike" - was a statement that it left the predicate alone, and is
what this fix changes: an ineligible or undetermined check now counts as an action that did not succeed, and its implicit
operation completes as not succeeded, naming the result.

**Visible effect.** The Admin Event Log's Usage view counts, per module, the actions whose success flag is false in its
"Failed" column (`UsageEventRepository.ModuleSummary`, `success = 0`), so that column now also counts an ineligible or
undetermined eligibility check, a permission change whose outcome could not be confirmed, and (kd-57) a cancelled lookup -
the actions that did not succeed, as the plan defined them. The audit rows, their results and the event log's Result filter
are unchanged. Usage rows already written keep the flag they were written with.

**Tests.**
- `AuditServiceTests.OnlyTheResultSuccess_IsASuccessfulAuditResult` (9 rows): Success in three cases true; Failed,
  Ineligible, Undetermined, Cancelled, empty and null false.
- `AuditServiceTests.TheRowsImplicitOperation_SucceedsOnlyForARowFiledSuccess` (6 rows, the trace call site): a row of each
  result the app files, through the method that files it, and the implicit operation's completion - Success only for the
  Success row; Failed with "Audit event recorded failure" for the Failed row; Failed naming the result for the others.
- `UsageTelemetryServiceTests.AuditService_CountsOnlyARowFiledSuccess_AsASuccessfulAction` (the usage call site): seven rows,
  the success flags `true, false, false, false, false, false, true`.

### (13) - guard proof

Filter `FullyQualifiedName~AuditServiceTests|FullyQualifiedName~UsageTelemetryServiceTests|FullyQualifiedName~AuditWriterThrowFree|FullyQualifiedName~AuditExtraChannel|FullyQualifiedName~EventLogSectionsTarget`,
165 tests, 165/165 on the fixed tree. Fixed-file MD5: `Services/AuditService.cs` `D1DA67D0481CBF17D665EAE032E0B251`; every
restore matched it byte for byte.

| # | Mutation | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M13 | Ineligible counted as a success again (the brief's): `\|\| string.Equals(result, "Ineligible", ...)` | `476B24646AE1E70AC04DD89626C74A4A` | 3 of 165: `OnlyTheResultSuccess_IsASuccessfulAuditResult(result: "Ineligible", ...)`, `TheRowsImplicitOperation_SucceedsOnlyForARowFiledSuccess(filed: "an ineligible eligibility check", ...)`, `AuditService_CountsOnlyARowFiledSuccess_AsASuccessfulAction` |
| E13a | extra: the old predicate restored (every result but Failed) | `45B12F82303C409360F953DBCFE4BBEE` | 10 of 165: the predicate's Ineligible, Undetermined, Cancelled, empty and null rows; the trace test's four non-success, non-Failed rows; the usage test |
| E13b | extra: the completion message back to "Audit event recorded failure" for every non-success | `5470FAA3C11EDC0E0ACFE900DA514B46` | 4 of 165: the trace test's Ineligible, two Undetermined and Cancelled rows |
| E13c | extra: the usage call site alone on the old rule (inline) | `1D0A1BA5EE92EC14F3EFF878E4B59606` | 1 of 165: `AuditService_CountsOnlyARowFiledSuccess_AsASuccessfulAction` |
| E13d | extra: the trace call site alone on the old rule (inline) | `C34164F940121D47D46C43ECF2966992` | 4 of 165: the trace test's Ineligible, two Undetermined and Cancelled rows |

Every mutant failed on its own behaviour; E13c and E13d show each call site has a test of its own.

**Versions.** `AuditService` is shared infrastructure, so the base app version bump is owed by the Constitution's rule
(Deployment And Versioning) - `<VersionPrefix>` 2.35.0 in `ExchangeAdminWeb.csproj`, not bumped here (the brief: the
supervisor's). No module version: no module's own behaviour changed, only the shared count and trace.

**Gates on this commit's tree** (2026-10-27): `dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4` exit 0,
7,367 passed / 0 failed / 0 skipped; `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` exit 0;
`git diff --check HEAD` exit 0; `tools/Test-AsciiOnly.ps1` passed. The Release build is run on the branch's final tree.

## (6) - every grant refused without the directory-read credential (implementer, 2026-10-27, branch `fix/a70-6` from master `7ef50c41`)

**Ruling.** Owner, 2026-10-26: "yes" - every permission grant is refused while the directory-read credential cannot be
had, not only grants to groups (`.agents/decisions.md` 2026-10-26, item 3). Read against the 2026-10-27 mandate by the
owner: "the condition is to not change functionality for users. since it works currently, and will continue to work in the
same way with no change on the users' part, this satisfies the condition" (`.agents/decisions.md` 2026-10-27, item 2).

### The code as it stood (lines at `7ef50c41`)

- **Where the credential is had, and the gap.** `Services/PermissionValidator.cs` `CompareInDirectoryAsync`, 808-823:
  `var (credential, credentialProblem) = await run.DirectoryReadCredentialAsync(DirectoryReadCredentialAsync);` then
  `if (credential is null && grantee is null)` refuses (812, `SelfGrantCheckIncompleteBecause($"the on-premises directory
  could not resolve '{affectedUser}': {credentialProblem}")`) - only a grantee Exchange Online names nothing for. Otherwise
  `if (credential is null)` (815) logs "looks {Affected} up in the on-premises directory under the session's own identity"
  and the lookup is `credential is null ? directory.FindNamedByUnderSessionIdentity(affectedUser,
  grantee?.PrimarySmtpAddress) : directory.FindNamedBy(affectedUser, grantee?.PrimarySmtpAddress, credential)` (821-823;
  `Services/SelfGrantDirectory.cs` 140-141, `Search(..., credential: null)`). That is aud-a69's residual: a person Exchange
  names is compared by Exchange, and the directory objects the name also reaches are read as the app pool's identity, so one
  hidden from it goes uncompared. The other no-credential refusals sit downstream and fire only when a group turns up:
  875-879 (a group the lookup found: "whether you are a member of the group '<x>' could not be read: <problem>") and
  1003-1007 (`CompareInExchangeGroupAsync`, a synced group nested in a cloud-only one, the same words). A cloud-only group
  with no synced member reads no directory membership and is granted without the credential (869, `if (account is null ||
  groups.Count == 0) return null;`), its name looked up as the session.
- **Who asks.** Only the two permission modules, on three paths each, and only for a grant. Mailbox Permissions:
  `SubmitSingle` 428-431 (`// Validate self-grant (only for Add operations)`, `if (isAdd)`, then
  `Validator.ValidateSelfGrantAsync(validationAuthState.User, user)`), recorded at 443-446 as
  `Add{permType}_SelfGrantAttempt`, Failed, `errorDetail: selfGrantError`; `ExecuteOnPrem`'s re-check at 704 (`if (isAdd &&
  await Validator.ValidateSelfGrantAsync(onPremAuthState.User, user) is { } selfGrantError)`, recorded
  `Add{permType}_SelfGrantAttemptAtWrite`, its banner `SelfGrantRefusalRecord.AtWrite`); the bulk row,
  `Services/MailboxPermissionService.cs` 346-355 (`if (isAdd)`, `BulkAdd{permType}_SelfGrantAttempt`). Calendar Permissions:
  `SubmitSingle` 404-416 (`if (isSet)`, `SetCalendar_SelfGrantAttempt`), `ExecuteOnPrem` 658 (`isSet && ...`,
  `SetCalendar_SelfGrantAttemptAtWrite`), `Services/CalendarPermissionService.cs` 285-294 (`if (isSet)`,
  `BulkSetCalendar_SelfGrantAttempt`). Every one records the validator's refusal as its error, so the group case's audit shape
  is the shape every refusal of the check takes.
- **Cancellation.** The self-grant check's credential fetch takes no token: `DirectoryReadSecretFetch` (44) is
  `Func<int, Task<...>>`, and production's `FetchDirectoryReadSecretAsync` ends `return await
  store.GetCredentialsBySecretIdAsync(secretId);` (1149) - the default token, under which `DelineaService` answers a request
  that gave up with null (its `catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)`, 219, is
  never taken). kd-37/kd-59 threaded the operator's token to the on-premises WRITE's credential fetch only - Mailbox 707-709,
  Calendar 660-663 (`onPremActivity.CancellationToken`) - filed Cancelled by the catch at Mailbox 742 / Calendar 685
  (`PermissionChangeFailure.CancelledBeforeItWasSent`), and that fetch runs after the self-grant re-check. No operator cancel
  reaches the fetch this refusal rests on, on any of the three paths.

### Approach

1. **One refusal, in the shared check, where the credential is had.** `CompareInDirectoryAsync`: when the credential cannot
   be had - its setting unreadable, not configured, not retrieved, a blank account, a fetch that throws or answers with a
   cancellation - the grant is refused there, before any lookup, whatever the grantee: a group Exchange names in the group
   refusal's words, "whether you are a member of the group '<x>' could not be read: <problem>" (aud-a60's, 878 as it
   stood), and every other grantee in the lookup's, "the on-premises directory could not resolve '<x>': <problem>"
   (aud-a69's, 812). No new words: both go through `SelfGrantCheckIncompleteBecause` ("Access denied: The self-grant check
   could not be completed: ...; nothing was written. Contact your administrator."), and the problem texts name the setting
   (`DirectoryReadCredentialNotConfigured` ends "(Admin Settings, Protected principals)"). Everything before it is unchanged
   and runs first - the string match, the directory session, the operator's own lookup, Exchange's two lookups and its
   comparison - so a grant Exchange already names as the operator's is still refused as a self-grant, and a check that
   fails earlier still refuses with its own reason: with the check on, no grant passes without the credential.
2. **The session-identity lookup goes.** `FindNamedByUnderSessionIdentity` (its only caller was 822) is removed; the grantee
   is looked up under the credential alone. The downstream no-credential refusals (875-879, 1003-1007) can no longer be
   reached - the credential is had above them - and go, with the parameter that carried the problem to the nested one;
   `CompareInExchangeGroupAsync` takes the credential as non-null. The setting-unreadable log line (1117) says every grant
   is refused, not grants to groups.
3. **The pages and the bulk loops are not touched.** They hand the validator's refusal to the record they already write for
   the group case: single change `Add{rights}_SelfGrantAttempt` / `SetCalendar_SelfGrantAttempt`, the on-premises re-check
   `..._SelfGrantAttemptAtWrite` with its "made again right before the write" banner, the bulk row
   `BulkAdd{rights}_SelfGrantAttempt` / `BulkSetCalendar_SelfGrantAttempt` and a FAILED entry - Failed, the refusal as its
   error, nothing written, no notification (a refused grant sends none today). No line moves on either page, so
   ClickGateRegistry and ProgressRegistry are untouched.
4. **A removal is not affected.** The check is asked for a grant alone - each of the six call sites above sits behind
   `isAdd` / `isSet` - and a removal grants no one anything, so it needs no credential and goes ahead as before. Pinned by
   tests on each path. (A Calendar "set" is a grant here whatever its level, "None" included, as it always was.)
5. **Cancellation.** No operator cancel reaches this fetch (above), so a fetch that answers with a cancellation is a
   credential read that errored: refused, `DirectoryReadCredentialNotRetrieved`, fail closed. The operator's own cancel stays
   kd-59's: arriving while the check's credential is fetched, it is seen at the write's credential fetch, which runs next,
   and the change is filed Cancelled - not this refusal; if the check's fetch then fails for its own reason, that failure is
   the refusal (kd-59's rule, `AWriteThatFailedForItsOwnReason_AsTheOperatorCancelled_IsThatFailure`). Threading the token
   into the check is not done: it is the kd-58 class (cancellation reaching the shared reads), outside this ruling.
6. **Scope.** The ruling is the self-grant check's: with Prevent Self-Grant off no check runs, nothing needs the credential
   and nothing changes (`PermissionValidatorTests.ValidateSelfGrant_Disabled_AllowsSelfGrant`, whose validator has no
   credential, already pins it).
7. **Docs.** README Self-grant Prevention and the Admin Settings placeholder and hint (written by aud-a70 (2) and (3) - not
   (17), as the brief has it) say every grant needs the credential and a removal does not; environment-neutral.
8. **Versions.** Mailbox Permissions 1.6.3 -> 1.6.4, Calendar Permissions 1.4.3 -> 1.4.4, each noting aud-a70 (6) and the
   ruling. `PermissionValidator` and `SelfGrantDirectory` are shared: the base app version bump is owed, not made here.

Commits, one module per commit as the brief asks: the refusal lives in the shared check both modules ask, so the first
commit (Mailbox Permissions) carries the validator change with Mailbox Permissions' path tests and version - Calendar
Permissions' grants are refused from that commit on, through the same lines - and the second (Calendar Permissions) its path
tests and version. Each commit carries its own section of this record; the docs follow in a commit of their own, and the
branch-tip gates with the index row in the last.

Tests (none reaches a directory, Exchange or a secret store): `ExchangeWriteHarness.WithholdDirectoryReadCredential` -
absent (no Secret ID), errored (the fetch throws), cancelled (the fetch answers with a cancellation) - for the path tests.
`PermissionValidatorSelfGrantTests`: a grant to a person Exchange names, without the credential, refused and not looked up
(in place of `AGrantToAPerson_NeedsNoDirectoryReadCredential`, which pinned the gap); a cloud-only group without it, refused
and its members not read; "the fetch is cancelled" joins `CredentialsNotToBeHad`; each bulk loop refusing a person row and a
group row, the credential fetched once for the file; each bulk loop's removal written. `PermissionPagesSelfGrantTests`: each
page's `SubmitSingle` and `ExecuteOnPrem` refusing a person and a group per shape, recorded as the group case is; each page's
cloud and on-premises removal written. `PermissionPagesCancellationTests`: the operator's cancel during the check's
credential read filed Cancelled (kd-59); the check's fetch failing as the operator cancels, the refusal. Mutations: the
guard as it stood (the validator and the directory session at `7ef50c41`); the group branch's words dropped; the refusal
block removed; a removal put through the check.

### As implemented - the shared check, with Mailbox Permissions (lines at this commit)

- `Services/PermissionValidator.cs` `CompareInDirectoryAsync`: the credential had at 811 as before; `if (credential is
  null)` (812-822) refuses every grant there - `if (grantee is not null && IsGroupRecipient(grantee))` (814) in the group
  refusal's words (817), else in the lookup's (821) - one warning each, naming the problem. The lookup is
  `directory.FindNamedBy(affectedUser, grantee?.PrimarySmtpAddress, credential)` alone (827). The membership-time refusal
  that followed `if (account is null || groups.Count == 0)` is gone, its comment saying the credential was had above
  (876-878); `CompareInExchangeGroupAsync` takes `PSCredential credential` (930) and its synced-nested refusal is gone. The
  setting-unreadable log line says "every grant is refused until it can be" (1113). Remarks on `ValidateSelfGrantAsync`,
  `CompareInDirectoryAsync`, `CompareInExchangeGroupAsync` and `DirectoryReadCredentialAsync` say so.
- `Services/SelfGrantDirectory.cs`: `FindNamedByUnderSessionIdentity` removed; `FindNamedBy`'s summary says it is the only
  grantee lookup (129-130). No other reference to it in the code or the tests.
- `Modules/ModuleCatalog.cs`: Mailbox Permissions 1.6.3 -> 1.6.4 (184-188), naming aud-a70 (6) and the ruling.
- Pages and services untouched: `MailboxPermissions.razor` 817 lines, `CalendarPermissions.razor` 771, the same bytes as at
  `7ef50c41`, so no ClickGateRegistry or ProgressRegistry entry moved.
- Tests. `ExchangeWriteHarness`: `DirectoryReadCredentialsWithheld` and `WithholdDirectoryReadCredential(shape)` - "absent"
  clears the stored Secret ID now and for every validator made later; "errored" and "cancelled" make the stand-in fetch's
  answer a throw or a cancellation; each returns the problem the refusal names. `PermissionValidatorSelfGrantTests`:
  `AGrantToAPerson_WithoutTheDirectoryReadCredential_IsRefused_AsIncomplete_AndNotLookedUp` (6 shapes; replaces
  `AGrantToAPerson_NeedsNoDirectoryReadCredential`, which asserted the grant allowed and the session's lookup),
  `ACloudOnlyGroup_WithoutTheDirectoryReadCredential_IsRefused_AsIncomplete_AndItsMembersAreNotRead` (6),
  `CredentialsNotToBeHad` + "the fetch is cancelled" (a row more for the group, UPN-only and Exchange-does-not-name theories),
  `ABulkFile_WithoutTheDirectoryReadCredential_RefusesEveryGrantRow_RecordedAsTheChecksRefusal` (a person row and a group row:
  both FAILED, `BulkAddFullAccess_SelfGrantAttempt`, Failed, each refusal its error, nothing written, one fetch for the file)
  and `ABulkRemoval_WithoutTheDirectoryReadCredential_IsWritten_TheSelfGrantCheckNotAsked`, both over `LoopsWithoutTheCredential`
  (Mailbox Permissions' loop here). `PermissionPagesSelfGrantTests` (`Submit`, `SubmitMailboxGrant`, `SubmitCalendarGrant` and
  `ConfirmOnPrem` take `removal`): `ASingleGrant_WithoutTheDirectoryReadCredential_IsRefusedNamingIt_RecordedAsTheChecksRefusal`
  and `TheOnPremReCheck_WithoutTheDirectoryReadCredential_RefusesTheGrant_BeforeAnythingIsSent` (shape x person or group: the
  banner, `AddFullAccess_SelfGrantAttempt` / `..._SelfGrantAttemptAtWrite`, Failed, the refusal as its error, nothing written
  or sent), `ASingleRemoval_...` and `AnOnPremRemoval_WithoutTheDirectoryReadCredential_IsWritten_TheSelfGrantCheckNotAsked` (the
  removal written, no fetch, no directory call). `PermissionPagesCancellationTests`:
  `ACancelWhileTheSelfGrantCheckHasItsCredentialFetched_IsFiledCancelled_NotTheCredentialRefusal` and
  `TheSelfGrantChecksCredentialFailing_AsTheOperatorCancels_IsTheCredentialRefusal` (the check's fetch held until the operator
  cancels, then answering, or failing). Each page and loop theory runs over its own list, Mailbox Permissions alone in this
  commit. Fixture: `PermissionValidatorTests.ValidateSelfGrant_DifferentUser_ReturnsNull` granted to a person Exchange names
  with no credential configured, which this refuses: `CreateValidator(directoryReadCredential: true)` gives it one (the
  appsettings fallback Secret ID and a stand-in fetch). The full suite found no other.

### Guard proof - the shared check, with Mailbox Permissions

Driver `bin/a70-6-scratch/mutate.ps1` (git-ignored, in the worktree; log `a70-6-mutations.log`, one TRX per run). Per mutant:
each touched file's MD5 and line count; the edit - an exact-text replacement that must match once, or the whole file as at a
revision; the mutant MD5 and line count; a Debug build of the test project, 0 errors required; the filter run `--no-build`,
the failing names read from the TRX; the original bytes written back, the write time advanced, the MD5 checked equal. After
the round the restored tree was rebuilt and the filter run: 887/887. Filter
`FullyQualifiedName~PermissionValidator|FullyQualifiedName~PermissionPages|FullyQualifiedName~SelfGrant|FullyQualifiedName~MailboxPermission|FullyQualifiedName~CalendarPermission|FullyQualifiedName~ClickGate|FullyQualifiedName~ProgressRegistry|FullyQualifiedName~ValidatedWriteCallSite|FullyQualifiedName~PermissionWriteSubmission`
(887 tests). Fixed-file MD5s: `Services/PermissionValidator.cs` `DE2C5E015157CFB92F59414AA1825467` (1,426 lines),
`Services/SelfGrantDirectory.cs` `8EA1F48B4486FE08F361DADAAA68233E` (335), `Components/Pages/MailboxPermissions.razor`
`BE2CF82B7E739D284F5AFB547F75DB4E` (817, unchanged), `Services/MailboxPermissionService.cs` `6F19DED2F5BFF6DEFA49F99938EEAB22`
(608, unchanged). Every restore matched.

| # | Mutation | Mutant MD5 | Failed (of 887) |
|---|---|---|---|
| M6 (the record's) | the guard as it stood: `PermissionValidator.cs` and `SelfGrantDirectory.cs` as at `7ef50c41` | `6FD31108171C506095E4CBBF7FB545FE`, `DC8279156F7F6906F828B383588B2AD8` | 22: every person row on every path - `ASingleGrant_...` x3, `TheOnPremReCheck_...` x3, `ABulkFile_...` x3 (the person row written), `AGrantToAPerson_...` x6 (allowed) - `ACloudOnlyGroup_...` x6 (allowed once its members were read) and `TheSelfGrantChecksCredentialFailing_AsTheOperatorCancels_...` (allowed, so the write's fetch filed the cancel) |
| E6a | the group branch dropped: a group Exchange names refused in the lookup's words | `6B11D8D45F1A465786D88426603FC946` | 22: every group row - `ASingleGrant_...` x3, `TheOnPremReCheck_...` x3, `ABulkFile_...` x3, `ACloudOnlyGroup_...` x6, `AGroupGrant_WithoutTheDirectoryReadCredential_...` x6, `ASyncedGroupInACloudOnlyGroup_WithoutTheDirectoryReadCredential_...` |
| E6b | the refusal block removed (the missing credential handed to the lookup, `credential!`) | `3B9296F179873F76077931FA785C0366` | 41: every row above whose refusal names the problem, on every path, and the six `AGranteeExchangeDoesNotName_...` rows - each refused by the lookup's throw, the setting not named |
| E6d | a cancellation from the check's fetch let out (`catch (Exception ex) when (ex is not OperationCanceledException)`) | `3705F15DC75C685A83737A7684E6F9AA` | 10: every "cancelled" row - `ASingleGrant_...` x2 and `TheOnPremReCheck_...` x2 (person, group), `ABulkFile_...`, and the "the fetch is cancelled" rows of `AGrantToAPerson_...`, `ACloudOnlyGroup_...`, `AGroupGrant_...`, `AGranteeExchangeDoesNotName_...`, `AnOperatorKnownOnlyByTheirUpnClaim_...` |
| E6r1 | `SubmitSingle` asks the check of a removal (`if (isAdd)` -> `if (true)`, 429) | `08F1254961BE99CC57883094ED05A96D` (817 lines) | 3: `ASingleRemoval_...` x3 |
| E6r2 | the bulk loop asks it of a removal row (`MailboxPermissionService.cs` 346) | `642E0940C23131A01AC26FF17A7235CC` | 3: `ABulkRemoval_...` x3 |
| E6r3 | `ExecuteOnPrem` asks it of a removal (`isAdd &&` dropped, 704) | `67C65A5E930C7E2EE8E883280EEBE401` (817 lines) | 3: `AnOnPremRemoval_...` x3 |
| E6k | kd-59's cancel catch never taken (742, `when (false && ...)`) | `FA6B58532C43566A6035B450DED0D457` (817 lines) | 3: `ACancelWhileTheSelfGrantCheckHasItsCredentialFetched_...` and kd-59's own two `ACancelOfTheOnPremActivity_...` rows |

M6 is the finding's reproduction on this module's three paths: each person row it fails is a grant the code as it stood let
through without the credential. Its group rows pass under M6 - the group case was refused before, in the same words - which is
the "no new words" half of the Approach; E6a is what pins those words. The page mutants keep 817 lines, so ClickGateRegistry
and ProgressRegistry ran under them, green.

Full suite on this commit's tree (Debug, `dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4`): 7,504 passed / 0
failed / 0 skipped. `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`, `git diff --check HEAD` and
`tools/Test-AsciiOnly.ps1`: clean. Commit `d7c9ec5c`.

### As implemented - Calendar Permissions

- No production code of its own: its three paths ask the same check, changed in the commit above. `Modules/ModuleCatalog.cs`:
  Calendar Permissions 1.4.3 -> 1.4.4 (236-240), "as Mailbox Permissions 1.6.4", naming aud-a70 (6) and the ruling.
- Tests: Calendar Permissions joins each list the theories run over - `CredentialPages` in `PermissionPagesSelfGrantTests` and
  `PermissionPagesCancellationTests`, `CredentialLoops` in `PermissionValidatorSelfGrantTests` - 26 cases: `SubmitSingle` and
  `ExecuteOnPrem` refusing a person and a group per shape (`SetCalendar_SelfGrantAttempt`, `SetCalendar_SelfGrantAttemptAtWrite`),
  the bulk file (`BulkSetCalendar_SelfGrantAttempt`, one fetch), the cloud, on-premises and bulk removals written, and the two
  cancel cases.

### Guard proof - Calendar Permissions

The same driver, protocol and filter: 913 tests (887 and Calendar Permissions' 26). Fixed-file MD5s: the validator and the
directory session as above (`DE2C5E015157CFB92F59414AA1825467`, `8EA1F48B4486FE08F361DADAAA68233E`),
`Components/Pages/CalendarPermissions.razor` `25E8CB9039DA6B2DE0FF015AC6AD828C` (771, unchanged), `Services/CalendarPermissionService.cs`
`3B958EE6B26E5F2198BF30DFDDB19466` (543, unchanged). Every restore matched; the restored tree rebuilt and the filter: 913/913.

| # | Mutation | Mutant MD5 | Failed (of 913) - Calendar Permissions' rows among them |
|---|---|---|---|
| M6 again | the guard as it stood (both files as at `7ef50c41`) | as above | 32 - the 22 above and 10 of Calendar's: `ASingleGrant_...` x3, `TheOnPremReCheck_...` x3, `ABulkFile_...(calendar: True)` x3 (every person row), `TheSelfGrantChecksCredentialFailing_...(page: "Calendar Permissions")` |
| E6a again | the group branch dropped | as above | 31 - the 22 above and Calendar's 9 group rows: `ASingleGrant_...` x3, `TheOnPremReCheck_...` x3, `ABulkFile_...(calendar: True)` x3 |
| E6b again | the refusal block removed | as above | 57 - the 41 above and Calendar's 16: every single, on-premises and bulk row, and the failing-fetch cancel case |
| E6d again | a cancellation from the fetch let out | as above | 15 - the 10 above and Calendar's 5 "cancelled" rows |
| E6r4 | `SubmitSingle` asks the check of a removal (`if (isSet)` -> `if (true)`, 404) | `C18A13C71982A1C4BFACBE14CC316608` (771 lines) | 3: `ASingleRemoval_...(page: "Calendar Permissions")` x3 |
| E6r5 | the bulk loop asks it of a removal row (`CalendarPermissionService.cs` 285) | `D6D8562DE7999DD652E24DBEC997EE61` | 3: `ABulkRemoval_...(calendar: True)` x3 |
| E6r6 | `ExecuteOnPrem` asks it of a removal (`isSet &&` dropped, 658) | `9F529F1F198C66842B5BAFF7AC762816` (771 lines) | 3: `AnOnPremRemoval_...(page: "Calendar Permissions")` x3 |
| E6k2 | kd-59's cancel catch never taken (685) | `E4B9DCE560C54ED98A1088CFA17B329C` (771 lines) | 3: `ACancelWhileTheSelfGrantCheckHasItsCredentialFetched_...(page: "Calendar Permissions")` and kd-59's two Calendar rows |

Full suite on this commit's tree: 7,530 passed / 0 failed / 0 skipped. Format, whitespace and ASCII clean. Commit `18d2467d`.

### As implemented - the docs

- `README.md` 536-540, Self-grant Prevention: "While the check is on (Prevent Self-Grant, the default), every grant needs it:
  without it, every grant is refused as incomplete, naming the setting - to a person or to a group, whichever directory holds
  the grantee - and nothing is written. A removal is not checked and does not need it." It said a grant to a group the
  directory holds, or to a grantee only it holds, was refused, "and so is every grant by an operator whose sign-in names them
  by a user principal name alone; a grant to a person Exchange Online names still goes ahead" - the UPN-only clause goes, every
  grant being refused now. The cost sentence stays. In place, 1,264 lines.
- `Components/Pages/AdminSettings.razor` 282, the credential's placeholder: "Required for group and OU rules, and for every
  permission grant"; 289, its hint: "Read-only AD credential for group membership and OU checks. While Prevent Self-Grant is on,
  Mailbox Permissions and Calendar Permissions also need it for every grant: without it every grant is refused. A removal does
  not need it." In place, 1,465 lines; Admin Settings is not line-keyed (ClickGateRegistry lists it as not started).
- Neither text names an environment (Invariant 7): the setting by its label, the check by its module.
- Tests: `SelfGrantDocumentationTests.TheReadme_SaysTheCheckNeedsTheDirectoryReadCredential_AndWhatItRefusesWithoutIt` reads the
  new sentences and that "still goes ahead" is gone; `AdminSettings_SaysTheDirectoryReadCredentialIsWhatPermissionGrantsToGroupsNeed`
  is renamed `AdminSettings_SaysEveryPermissionGrantNeedsTheDirectoryReadCredential` and reads the new placeholder and hint, the
  old hint gone. `SettingsNoOpAndUnreadableSecretTests` finds the credential's input by its placeholder, so its locator
  constant follows the new text.

### Guard proof - the docs

The same driver; filter `FullyQualifiedName~SelfGrantDocumentation|FullyQualifiedName~SettingsNoOpAndUnreadableSecret|FullyQualifiedName~SettingsStoreText|FullyQualifiedName~AdminPageDirtyState|FullyQualifiedName~ProgressRegistry|FullyQualifiedName~ClickGate`
(419 tests). Fixed-file MD5s: `README.md` `FFB97C5ED345A08C969A75E9E7BC08A4`, `Components/Pages/AdminSettings.razor`
`F5A6704E45BBB2651BA473D23061D961`. Every restore matched; the restored tree rebuilt and the filter: 419/419.

| # | Mutation | Mutant MD5 | Failed (of 419) |
|---|---|---|---|
| M6d (the record's) | the README as it stood (`7ef50c41`) | `1CE94290C68E164AE285C962B24A01F1` | 1: `TheReadme_SaysTheCheckNeedsTheDirectoryReadCredential_AndWhatItRefusesWithoutIt` |
| M6e | the Admin Settings hint as it stood | `6767882B39203A2A9AB976180606D9C9` | 1: `AdminSettings_SaysEveryPermissionGrantNeedsTheDirectoryReadCredential` |
| E6f | the placeholder as it stood | `8CAE2C8561EDE426D004A2E45559D3B5` | 3: the same test, and `SettingsNoOpAndUnreadableSecretTests`' two that find the input by it |

Commit `36a9f2e9`.

### Known gaps - (6)

- **Deployment note, a labelled assumption (not verified - nothing here can read a deployment).** The owner's condition is met
  where the Directory Read Credential (Admin Settings, Protected principals) is configured and can be retrieved: grants go on
  as before. Where it is not, every Mailbox Permissions and Calendar Permissions grant is refused from this release on, naming
  the setting, while Prevent Self-Grant is on - before, only grants to groups and to grantees only the directory holds were.
  Check that setting on the instance before deploying.
- Without the credential a check still opens its directory session and asks Exchange its two lookups before it refuses - the
  order as it was, so a grant Exchange names as the operator's own is refused as a self-grant. A cost of the refused case only.
- No operator cancel reaches the check's credential fetch, as before: threading the activity's token into the check is the
  kd-58 class (cancellation reaching the shared reads), not done. A cancel during it is seen at the on-premises write's own
  credential fetch (kd-59); `SubmitSingle`'s cloud write and the bulk rows see no cancel at all (kd-58), unchanged.
- "Cancelled" goes through the test seam: production's fetch passes `DelineaService` no token, and it answers a request that
  gave up with null, which is `DirectoryReadCredentialNotRetrieved` - the same refusal. A fetch that throws is the same again.
- aud-a69's E4 site (the synced nested group's own no-credential refusal) is gone with the code it mutated;
  `ASyncedGroupInACloudOnlyGroup_WithoutTheDirectoryReadCredential_IsRefused_NamingIt_AndNotLookedUpAsTheSession` now holds the
  (6) refusal, in the same words, before the nested group is looked at.
- A Calendar "set" counts as a grant whatever its level, "None" included - unchanged; so a set to None now needs the credential.
- Rows already written stay as written. Nothing ran against Exchange, a directory, Graph or a secret store: the harness's pools
  and stand-ins, `SelfGrantDirectoryStandIn`, the stand-in secret fetch, `CancellableVaultCredentials` for the write's vault.

### Versions - (6)

Mailbox Permissions 1.6.3 -> 1.6.4 (`d7c9ec5c`), Calendar Permissions 1.4.3 -> 1.4.4 (`18d2467d`), each a patch step noting
aud-a70 (6) and the ruling. `PermissionValidator` and `SelfGrantDirectory` are shared infrastructure, so the base app version
bump is owed by the Constitution's rule (Deployment And Versioning) - `<VersionPrefix>` 2.35.0, not bumped here.

### Branch-tip gates - (6), 2026-10-27

On the branch's final code, `36a9f2e9` (the docs commit; the commit after it adds this record's gates and the index row only,
`.agents/` markdown), each gate run alone, nothing else building or testing on the host:

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: exit 0, 0 errors, 19 warnings - the baseline set
  (AccountLockoutRemediationService, Comms10kApprovedSetTests x2, GroupManagement x8, HeaderAnalysisService x3, MessageTrace 617
  CS0414, MigrationReportExportProcessor, RiskyUsers, SelfServiceGroups x2), none in a file this branch touched.
- `dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4` (Debug, as the gate is written): exit 0, 7,530 passed /
  0 failed / 0 skipped - 7,504 at `d7c9ec5c`, 7,530 at `18d2467d`.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0, and over the
  whole branch, `git diff --check 7ef50c41`: exit 0. `pwsh -NoProfile -File tools/Test-AsciiOnly.ps1`: passed.
- No PowerShell changed, so PSScriptAnalyzer and Pester were not run. Nothing was deployed; no manual check was run. No coder
  self-review verdict is claimed.

### Closeout - (6)

Fixed on `fix/a70-6`, not merged, not pushed. Pending: the supervisor's merge and branch deletion; the single final
codex-commercial review; the base app version bump at close; the deployment note above.
