# aud-a75: ExecuteOnPrem's catch files a write-step failure as Failed under the "could not be confirmed" banner

**Severity**: MEDIUM - the aud-a73 defect on the on-premises path. `ExecuteOnPrem`'s catch (Mailbox
744-747, Calendar 687-690) records its failure row with `success: false`, so a throw at the write step
(reachable: `AddMailboxPermissionsOnPremAsync` calls `ConnectOnPrem` inside a try with only a `finally`; the
Calendar on-prem set and remove have no catch) is filed Failed while the banner says the outcome could not
be confirmed; the Undetermined filter does not find it.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (MEDIUM); folded into the aud-a73 agent's branch
**Branch**: `worktree-agent-a22f2baf02d6abcdd` (from master `066ea1a2`, after aud-a73's `ff0b9ded`)
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 (`.agents/decisions.md` 2026-10-21), within the 2026-10-23 stopping rule (MEDIUM); folded onto the
aud-a73 branch by the coordinator, the record taken from master `1c0e9b6b`. Branch `worktree-agent-a22f2baf02d6abcdd`
(from master `066ea1a2`; aud-a73 at `ff0b9ded`): this record alone, then one fix commit
`fix(permission-pages): ... (aud-a75)`; the pages edited in place (808 and 762 lines, no registry entry moved). Behaviour:
the two pages' `ExecuteOnPrem` catch (both module versions); nothing shared changes - `filedAs` and `AuditResultOf` are
aud-a73's.

Evidence re-read at `ff0b9ded`:
- `Components/Pages/MailboxPermissions.razor` 744-747: the catch's banner is `PermissionChangeFailure.Banner(step, ex)` and
  its row `Audit.LogMailboxPermission(currentUser, clientIpAddress, action, target, user, permType, false, ticket,
  errorDetail: failure)` inside `SelfGrantRefusalRecord.Banner(..., FailedChangeRecord)`; `Components/Pages/CalendarPermissions.razor`
  687-690 the same through `LogCalendarPermission(..., action, target, user, level, false, ticket, errorDetail: failure)`.
- `step` is `TheWrite` from Mailbox 706 and Calendar 659 until the write returns (710, 664). A throw there is reached:
  `MailboxPermissionService.AddMailboxPermissionsOnPremAsync` and `RemoveMailboxPermissionsOnPremAsync` call `ConnectOnPrem`
  in a try with only a `finally` (each right's own failure is a result, `TryRightOnPrem`); `CalendarPermissionService`'s
  on-premises set and remove have no catch, so a connect that fails and a write that fails both throw.

1. **The catch files by its step.** In place, on the record's last line (Mailbox 747, Calendar 690):
   `errorDetail: failure, filedAs: PermissionChangeFailure.AuditResultOf(step))` - Undetermined at the write, Failed before
   and after it. The banner, the action, `success: false` and the `FailedChangeRecord` kind stay.

Tests, `PermissionPagesSelfGrantTests`:
- The on-premises services the tests use (`OnPremMailboxService`, `OnPremCalendarService`) take an optional exception
  their `ConnectOnPrem` throws, and `ConfirmOnPrem` passes one a test gives.
- New `AnOnPremWriteThatThrows_IsRecordedAsUndetermined_WithTheBannersText` (each page): the write's connect throws an
  exception whose raw text differs from its captured PowerShell error - the write-step banner in the captured words, never
  the raw text; one row, `OnPrem_AddFullAccess` / `OnPrem_SetCalendar`, Undetermined, its error the banner; one warning
  with the exception; nothing sent to on-premises Exchange (no Invoke-Command), nothing written.
- aud-a59's `AnOnPremStepThatThrowsBeforeTheWrite_IsNamed_InTheCausesOwnWords_AndItsRecordCarriesTheSameText` (each page)
  is the before-the-write row, Failed - unchanged, and the extra mutation below shows it bites at the new call site.

Mutations at the call site (md5, mutate, rebuild, filtered tests, failing names, restore from git, touch, rebuild, md5
equal, green): M1 the Mailbox Permissions `ExecuteOnPrem` catch's `filedAs` removed (the bare `false` again); M1c the same
on Calendar Permissions. Extra E1: `filedAs: AuditResult.Undetermined` at the Mailbox call, every step Undetermined.

Files: `Components/Pages/MailboxPermissions.razor`, `Components/Pages/CalendarPermissions.razor` (in place),
`ExchangeAdminWeb.Tests/PermissionPagesSelfGrantTests.cs`, this record.

Caller-visible change, by design: an on-premises change whose write threw is filed with result "Undetermined", as its
banner says, and the Admin Event Log's Undetermined filter finds it; a throw before or after the write stays Failed.

## Fix

- `Components/Pages/MailboxPermissions.razor` 747 and `Components/Pages/CalendarPermissions.razor` 690, in place (808 and
  762 lines, no registry entry moved): `ExecuteOnPrem`'s catch's record gains
  `filedAs: PermissionChangeFailure.AuditResultOf(step)` after `errorDetail: failure`, and a line-end comment naming
  aud-a75. The banner, the action, `success: false` and the `FailedChangeRecord` kind are as aud-a59 left them.

Tests: `PermissionPagesSelfGrantTests` +2. `OnPremMailboxService` and `OnPremCalendarService` take an optional exception
their `ConnectOnPrem` throws (`connectFailure`), which `ConfirmOnPrem` passes as `onPremConnect`.
`AnOnPremWriteThatThrows_IsRecordedAsUndetermined_WithTheBannersText` (each page): the write's connect throws an exception
whose raw text differs from its captured PowerShell error - the write-step banner in the captured words, never the raw
text; one row, `OnPrem_AddFullAccess` / `OnPrem_SetCalendar`, Undetermined, its error the banner; one warning with the
exception; no Invoke-Command, nothing written. aud-a59's `AnOnPremStepThatThrowsBeforeTheWrite_...` (each page) stays the
before-the-write row, Failed.

## Guard proof

Session-local driver `a75-mutate.ps1` (session scratchpad, log `a75-mutations.log`), aud-a73's driver and protocol with
these mutants: the fix staged; per mutant the MD5 of the file, the exact-text edit (it must match exactly once), the MD5 and
the line count again, a Debug build of the test project with 0 errors required, `dotnet test --no-build` over aud-a73's
filter (892 tests now), the failing names taken from the run, then the file restored from the git index, its write time
advanced so MSBuild rebuilds it, and its MD5 checked equal to the one before. Fixed-file MD5s:
`Components/Pages/MailboxPermissions.razor` `DD546DD91928FE5CA9071297AD5E1D79`, `Components/Pages/CalendarPermissions.razor`
`AD5E95BC7153F5C66675B69286E5748B` (unmutated: `ExchangeAdminWeb.Tests/PermissionPagesSelfGrantTests.cs`
`70BB53BE6A822079636C315048EF7588`). Every mutant built with 0 errors, kept its page's line count (808, 762; ClickGate and
ProgressRegistry ran under it and passed), and every restore matched. After the round, the restored tree rebuilt with 0
errors and the filter: 892/892 green.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 | the Mailbox Permissions `ExecuteOnPrem` catch's `filedAs` removed (the bare `false` again) | `MailboxPermissions.razor` `70EABEB64700388711B948EACECA40D4` | 1 of 892: `AnOnPremWriteThatThrows_IsRecordedAsUndetermined_WithTheBannersText(page: "Mailbox Permissions")` - the row filed Failed |
| M1c | the same on Calendar Permissions | `CalendarPermissions.razor` `AB74DCD5328F76076CB49565B1D0AB81` | 1 of 892: the same test's `(page: "Calendar Permissions")` row |
| E1 | extra: `filedAs: AuditResult.Undetermined` at the Mailbox Permissions call (every step Undetermined) | `MailboxPermissions.razor` `8E58AA41098F8B4979DB29ABF7B892ED` | 1 of 892: `AnOnPremStepThatThrowsBeforeTheWrite_IsNamed_InTheCausesOwnWords_AndItsRecordCarriesTheSameText(page: "Mailbox Permissions")` - the before-the-write row filed Undetermined |

No separate pre-fix reproduction: M1 and M1c are each catch's record as it stood at `ff0b9ded`, byte for byte but for the
line-end comment, so each failure is also the finding's reproduction.

## Known gaps

- `CalendarPermissionService`'s on-premises set and remove let a write Exchange refused out as an exception, so such a
  write reaches this catch at the write step and is now filed Undetermined under "could not be confirmed" - conservative,
  but Exchange did answer. The Mailbox on-premises write reports a refused right as a result (`TryRightOnPrem`). Recorded in
  the aud-a73 hand-back as a LOW candidate; not changed here.
- The stage names the step, not what threw in it (aud-a53): a connect that failed before anything reached Exchange is
  Undetermined too, as its banner says.
- Nothing ran against Exchange: the harness's on-premises stand-in through the services' own seam, or a connect that
  throws the test's exception; the harness's validator and directory stand-in.
