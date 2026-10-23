# aud-a59: Permission pages - the protected-target denial never says its audit row was not written, ExecuteOnPrem's catch shows a raw exception, a blank-Target bulk row is filed under the protected-target action, and the harness stub answers group members by default

**Severity**: LOW - the aud-a50 leftovers. (1) The protected-target denial's record ignores the aud-a28u
write outcome and a throw is only logged, so the banner never says the row was not written (in
`ExecuteOnPrem` the record is inside no try at all). (2) `ExecuteOnPrem`'s catch audits and shows the raw
`ex.Message`. (3) A bulk row with an empty Target is recorded as `Bulk*_ExcludedTarget`, the protected-target
action name (the aud-a50 part-3 class for the Target column). (4) Test hygiene: `ExchangeWriteHarness.ExchangeKnows`
leaves `ReadGroupMembersAsync` to NSubstitute's default empty list, so a future cloud-only group test that
forgets to set up members passes as allowed.
**Status**: Merged `c89f639c`; closed on guard proof; codex review pending
**Branch**: `worktree-agent-a83ff1596fd6eee17` (from master `a7300dee`)
**Commit**: plan `fa1ed15c`; fix `7328135e` (after aud-a53's `4580c404`)
**Reviewer**: implementer (surfaced by the aud-a50/a52 agent, 2026-10-22, candidates 1, 3, 4 and 5 of its hand-back)

## Evidence

At `81809833`: `Components/Pages/MailboxPermissions.razor` 409-425, 696-703, 744-747;
`Components/Pages/CalendarPermissions.razor` 387-402, 650-657, 687-690 (both line-keyed by
`ClickGateRegistry`, 808 and 762 lines, edit in place only); `Services/MailboxPermissionService.cs` 283-300;
`Services/CalendarPermissionService.cs` 164-181; `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs` 518-525.

## Approach (to be written by the implementing agent before code)

- The protected-target denial records through `SelfGrantRefusalRecord.Banner`'s shape (outcome-aware, guarded),
  on both paths.
- `ExecuteOnPrem`'s catch reports through the aud-a23i error-text helper, no raw message; the audit row
  carries the same text.
- A blank Target is an invalid row (`Bulk*_NoTarget`, "No target specified (the Target column is empty)")
  before any check.
- `ExchangeKnows` makes `ReadGroupMembersAsync` throw unless the test sets members, so an unset membership
  fails as incomplete.
- Tests through `PermissionPagesSelfGrantTests` and the bulk harness; mutations at the call site: the
  outcome ignored at the denial; raw `ex.Message` again; the blank-Target check removed; the stub default
  again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 (`.agents/decisions.md` 2026-10-21). Branch `worktree-agent-a83ff1596fd6eee17` from master `a7300dee`: one
fix commit `fix(permission-pages,permission-bulk): ... (aud-a59)`, fourth of four, after aud-a53 (whose step helper its
`ExecuteOnPrem` catch reuses); the pages edited in place (808 and 762 lines); comments in the branch's comments-only commit.
Behaviour: the two pages and the two bulk loops (both module versions) and `SelfGrantRefusalRecord` (base app version).

Evidence re-read at `a7300dee`:
- `MailboxPermissions.razor` `SubmitSingle` 409-426: the protected-target denial's record (417-419) ignores the writer's
  outcome inside a try whose catch only logs, so the banner never says the row was not written; `ExecuteOnPrem` 696-703 records
  its denial (699-701) inside no try of its own - the handler's catch then records again and shows the raw text.
  `CalendarPermissions.razor` 387-402 (record 393-395) and 650-657 (record 653-655) the same.
- `ExecuteOnPrem`'s catch, Mailbox 742-748 and Calendar 685-691: the raw `ex.Message` in the record and in the banner, whatever
  step threw, and the record itself unguarded.
- `MailboxPermissionService` 226-247 and `CalendarPermissionService` 135-157: aud-a50's blank-User row is refused first; a
  blank Target goes on to `ValidateTargetMailboxAsync("")` and is recorded as `Bulk*_ExcludedTarget`, the protected-target
  action.
- `ExchangeWriteHarness.ExchangeKnows` 518-525 stubs `ResolveRecipientAsync` only, so `ReadGroupMembersAsync` answers
  NSubstitute's empty list: a cloud-only group test that forgets its members passes as allowed.

1. **The denial's record is outcome-aware and guarded, on both paths.** `SubmitSingle`, in place: the record's outcome is read
   (`!= JsonlWriteOutcome.Written` adds `SelfGrantRefusalRecord.AuditRowNotWritten` to the banner) and its catch logs and says
   the same - the shape `SubmitSingle`'s self-grant branch has since aud-a50. `ExecuteOnPrem`, on the record's own four lines:
   `SelfGrantRefusalRecord.Banner(denial, () => Audit.Log...(...), Logger, target, SelfGrantRefusalRecord.DenialRecord)` - the
   one-line helper aud-a50 wrote for this handler, given the record's kind so its log line names a protected-target denial.
2. **`ExecuteOnPrem`'s catch names its step and reports no raw text.** The aud-a53 step local in place (before the write, the
   write, after it); the catch logs the exception against the target and the step, the banner and the record's `errorDetail`
   are one text, `PermissionChangeFailure.Banner(step, ex)` (the aud-a23i helper inside it), and the record goes through
   `SelfGrantRefusalRecord.Banner(..., FailedChangeRecord)`, so a record that cannot be written is said once and nothing escapes
   the handler.
3. **A blank Target is an invalid row.** First in each bulk row, before aud-a50's blank-User check, grants and revocations
   alike: `string.IsNullOrWhiteSpace(row.Target)` records `Bulk{Add|Remove}_NoTarget` (permission "Mailbox") /
   `Bulk{Set|Remove}Calendar_NoTarget` with `MailboxPermissionService.BulkRowNamesNoTarget`, "No target specified (the Target
   column is empty)", adds that error and a FAILED entry "No target specified", and continues - before the target check, the
   self-grant check and the write.
4. **The harness resolver answers no membership it was not given.** `ExchangeKnows` stubs `ReadGroupMembersAsync` for every
   group to throw ("stand-in: no members were set for '<group>'"); a test that sets members overrides it for its group.

Tests. `PermissionPagesSelfGrantTests`: each page's `SubmitSingle` and `ExecuteOnPrem` denying a target Exchange holds no
mailbox for, with the audit file held (the writer drops the row) and with the writer missing (the record throws) - the denial
followed by "The audit row could not be written.", one warning for the throw, nothing escaping the handler; `ExecuteOnPrem`
over a step before the write that throws (the on-premises permission check) - the before-the-write text in the record and the
banner, the cause in its own words, never the raw message. `PermissionValidatorSelfGrantTests` (the bulk loops over the
harness): an empty and a whitespace Target, both loops, grant and revoke - one `*_NoTarget` record, the FAILED entry and error,
Exchange never asked, no directory session, nothing written; a valid row after it still processed; the harness resolver's
`ReadGroupMembersAsync` throwing for a group it was not given, and a cloud-only group grantee whose members no test set
refused as incomplete.

Mutations at the call site: M1 the outcome ignored at the denial - Mailbox `SubmitSingle`'s record back to the bare call (M1b
`ExecuteOnPrem`'s back to the bare record); M2 the raw `ex.Message` again in the Mailbox `ExecuteOnPrem` catch; M3 the Mailbox
loop's blank-Target check removed (M3c the Calendar loop's); M4 the stub default again (`ExchangeKnows` without its
`ReadGroupMembersAsync` stub).

Files: `Components/Pages/MailboxPermissions.razor`, `Components/Pages/CalendarPermissions.razor` (in place),
`Services/SelfGrantRefusalRecord.cs`, `Services/MailboxPermissionService.cs`, `Services/CalendarPermissionService.cs`,
`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`, `ExchangeAdminWeb.Tests/PermissionPagesSelfGrantTests.cs`,
`ExchangeAdminWeb.Tests/PermissionValidatorSelfGrantTests.cs`, this record.

Caller-visible change, by design: a protected-target denial whose row was not written says so; an `ExecuteOnPrem` failure says
which step failed in the command's own words, and its record carries that text; a bulk row with an empty Target is reported
"No target specified" and audited as `*_NoTarget`.

## Fix

- `Services/SelfGrantRefusalRecord.cs`: `Banner` takes the record's kind (`SelfGrantRecord`, the default, "the self-grant
  refusal"; `DenialRecord`, "protected-target denial"; `FailedChangeRecord`, "the failed on-premises change") and logs a write
  that throws as "Failed to write audit for {Record} on {Target}" - the same words as before for a self-grant.
- `Components/Pages/MailboxPermissions.razor` and `Components/Pages/CalendarPermissions.razor`, in place (808 and 762 lines):
  `SubmitSingle`'s denial reads the record's outcome (Mailbox 417-419, Calendar 393-395) and its catch says the same (Mailbox
  423, Calendar 399): the denial followed by `AuditRowNotWritten`. `ExecuteOnPrem`'s denial is `SelfGrantRefusalRecord.Banner(denial,
  ..., DenialRecord)` on the record's own four lines (Mailbox 698-701, Calendar 652-655). `ExecuteOnPrem` has the aud-a53 step
  local (Mailbox 677, 706, 710; Calendar 631, 659, 664) and its catch (Mailbox 744-747, Calendar 687-690) logs the exception with
  the step, and banners and records one text - `PermissionChangeFailure.Banner(step, ex)` - through `Banner(..., FailedChangeRecord)`.
- `Services/MailboxPermissionService.cs` and `Services/CalendarPermissionService.cs`: first in each row, `string.IsNullOrWhiteSpace(row.Target)`
  records `Bulk{Add|Remove}_NoTarget` ("Mailbox") / `Bulk{Set|Remove}Calendar_NoTarget` with new
  `MailboxPermissionService.BulkRowNamesNoTarget`, "No target specified (the Target column is empty)", adds that error and a
  FAILED entry "No target specified", and continues.
- `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs` `ExchangeKnows`: `ReadGroupMembersAsync` throws for every group unless a test
  sets its members.

Tests: `PermissionPagesSelfGrantTests` +10 (the driver stages the target a test gives and can make the on-premises permission
check throw) - `AProtectedTargetDenial_WhoseRowTheWriterDrops_SaysTheRowCouldNotBeWritten` and
`AProtectedTargetDenial_WhoseRecordThrows_SaysSo_AndNothingEscapesTheHandler` (each page x `SubmitSingle` and `ExecuteOnPrem`,
a target Exchange holds no mailbox for: the denial and the sentence; for the throw one warning naming the protected-target
denial and nothing escaping), `AnOnPremStepThatThrowsBeforeTheWrite_IsNamed_InTheCausesOwnWords_AndItsRecordCarriesTheSameText`
(each page: the before-the-write text in the banner and the record, the cause the captured PowerShell error, never the raw
message, one warning). `PermissionValidatorSelfGrantTests` +14 - `ABulkRowThatNamesNoTarget_IsAnInvalidRow_RefusedBeforeAnyCheck`
(both loops x grant and revoke x an empty and a blank Target: one `*_NoTarget` record, the FAILED entry and error, Exchange never
asked, no directory session, nothing written or connected), `ARowAfterOneThatNamesNoTarget_IsStillProcessed` (both loops x grant
and revoke), `TheHarnessResolver_ReadsNoMembershipItWasNotGiven`, `ACloudOnlyGroupWhoseMembersNoTestSet_IsRefused_AsIncomplete`.

## Guard proof

The aud-a60 driver and protocol (that record's Guard proof; log `a59-mutations.log`). Filter
`FullyQualifiedName~PermissionPagesSelfGrant|FullyQualifiedName~ClickGate|FullyQualifiedName~ProgressRegistry|FullyQualifiedName~PermissionValidator|FullyQualifiedName~MailboxPermission|FullyQualifiedName~CalendarPermission|FullyQualifiedName~ExchangeGroupMembers|FullyQualifiedName~BulkPermission|FullyQualifiedName~BulkAndOnPrem`
(699 tests). Fixed-file MD5s: `Components/Pages/MailboxPermissions.razor` `DCA0EBFF4102013AE512C95C1C0E5E18`,
`Services/MailboxPermissionService.cs` `DDE69B719CC9D7D5BFBA9225ABDFCCCD`, `Services/CalendarPermissionService.cs`
`2A958AB0E30C24F0B0A2687385CB513E`, `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs` `EEAD20EB29ACFC0D07207F4C40E0856E` (unmutated: `Components/Pages/CalendarPermissions.razor` `D63372D1DD7E892E00FE70FCEECA2E67`, `Services/SelfGrantRefusalRecord.cs` `0B071FEFBAECE408CEB290BD36F21D11`). Every
mutant built with 0 errors and every restore matched; each page mutant kept its page's line count, so ClickGate and
ProgressRegistry ran under it and passed. After the round, the restored tree rebuilt and the filter: 699/699 green.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 | the outcome ignored at the denial: Mailbox Permissions `SubmitSingle`'s record back to the bare call | `MailboxPermissions.razor` `AA22206EA96415FE8678D977C2D278C8` | 1 of 699: `AProtectedTargetDenial_WhoseRowTheWriterDrops_SaysTheRowCouldNotBeWritten(page: "Mailbox Permissions", onPrem: False)` - the denial alone |
| M1b | the same at `ExecuteOnPrem`: the record back to the bare call outside any guard | `D808382A99F60094C74F0BBB67453BE7` | 2 of 699: both `AProtectedTargetDenial_...(page: "Mailbox Permissions", onPrem: True)` rows - the dropped row unsaid; the throw reached the catch, whose banner then named a failed step |
| M2 | the raw `ex.Message` again in the Mailbox Permissions `ExecuteOnPrem` catch | `DD033C0E681D36AF0ADB1106C553B69D` | 1 of 699: `AnOnPremStepThatThrowsBeforeTheWrite_IsNamed_InTheCausesOwnWords_AndItsRecordCarriesTheSameText(page: "Mailbox Permissions")` |
| M3 | the Mailbox Permissions loop's blank-Target check removed | `MailboxPermissionService.cs` `868BF929D53DC199338BB88BC680356E` | 6 of 699: the four `ABulkRowThatNamesNoTarget_IsAnInvalidRow_RefusedBeforeAnyCheck(calendar: False, ...)` rows and both `ARowAfterOneThatNamesNoTarget_IsStillProcessed(calendar: False, ...)` rows - recorded as `Bulk*_ExcludedTarget` |
| M3c | the Calendar Permissions loop's | `CalendarPermissionService.cs` `6006CF56404A0E1D50A1F585BC5817BF` (the file as aud-a61 left it, byte for byte) | 6 of 699: the same tests' `calendar: True` rows |
| M4 | the stub default again: `ExchangeKnows` without its `ReadGroupMembersAsync` stub | `ExchangeWriteHarness.cs` `464FA635ED4642517DEB789FB7CA7333` | 2 of 699: `TheHarnessResolver_ReadsNoMembershipItWasNotGiven`, `ACloudOnlyGroupWhoseMembersNoTestSet_IsRefused_AsIncomplete` - allowed on NSubstitute's empty answer |

No separate pre-fix reproduction: each mutant is the code as it stood at its call site (M3c byte for byte).

## Known gaps

- `ExecuteOnPrem`'s success records stay outside a guard of their own (aud-a53's known gap): a throw there - since aud-a28q
  only a missing writer - reaches this catch, which says the change was written and a later step failed, and records a failure
  beside the success rows.
- A blank Target and a blank User on one row is reported as the Target's (it is checked first).

## Comments after the round

None: this record's comments were written with the fix, and the comments commit `1c982906` touches none of its files.

## Gates - 2026-10-23 (branch tip `1c982906`: the five fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: exit 0, 0 errors; 19 warnings - the baseline, 19 at
  `a7300dee`, the same nineteen - none in a touched file. NuGet audit on (NU1902/3/4 errors): none raised.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0, 6,387 passed / 3 skipped (the
  three known AD skips, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`) / 0 failed, 6,390 total, 6m55s;
  nothing hung (the blame folder empty, no Sequence file). After aud-a60 alone: 6,333 / 3 / 0.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `git add -A` then `tools/Test-AsciiOnly.ps1`: exit 0 ("ASCII lint passed"). The pages: 808 and 762 lines.

No AD, Exchange Online, Graph, DHCP or IIS call: the directory is `SelfGrantDirectoryStandIn`, Exchange the harness's pool over
stand-in cmdlets or a stand-in resolver, the on-premises session the harness's `DefineOnPremSession`, the secret store a
stand-in fetch, the pool's connect step a local runspace of stand-in functions. No coder self-review verdict is claimed.

## Closeout

Fixed on branch `worktree-agent-a83ff1596fd6eee17`, not merged, not pushed. Due at merge: the base app version - the shared
`SelfGrantRefusalRecord` - and the Mailbox Permissions and Calendar Permissions module versions: the denial banner, the
on-premises catch's text and record, and the bulk loops' `*_NoTarget` rows.
