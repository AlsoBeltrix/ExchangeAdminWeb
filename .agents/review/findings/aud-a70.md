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
