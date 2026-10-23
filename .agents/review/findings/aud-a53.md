# aud-a53: Permission pages - the SubmitSingle catch reports any throw as "Operation may have succeeded but audit logging failed", and the notifications are not separately guarded

**Severity**: LOW - latent (EmailService catches per-recipient send failures), but the shape is the aud-a23m
defect on the permission pages. In `MailboxPermissions.razor` and `CalendarPermissions.razor` the
`SubmitSingle` catch reports every throw in its try as "Operation may have succeeded but audit logging
failed"; the admin notifications inside that try are not guarded on their own, so a throw there would
replace a successful write's result with that text.
**Status**: Fixed on branch, not merged, not pushed; approved under the owner's 2026-10-21 "EVERYTHING" go
**Branch**: `worktree-agent-a83ff1596fd6eee17` (from master `a7300dee`)
**Commit**: plan `fa1ed15c`; fix `4580c404` (after aud-a61's `d945d8c7`)
**Reviewer**: implementer (surfaced by the aud-a23r/o/q/p agent, 2026-10-22, candidate 1 of its hand-back)

## Evidence

At `d690a6c1`: `Components/Pages/CalendarPermissions.razor` 506 (the catch), 484/495/497 (notifications);
`Components/Pages/MailboxPermissions.razor` 529 (the catch), 509/519 (notifications) - both line-keyed by
`ClickGateRegistry` (808 and 762 lines), edit in place only.

## Approach (to be written by the implementing agent before code)

- Each notification call sits in its own guard (logged, the result stands - the aud-a23m shape); the catch's
  text is reserved for the audit write and says which step failed otherwise.
- Tests through `PermissionPagesSelfGrantTests`' page driver with a throwing notification stand-in;
  mutation at the call site: one notification guard removed.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 (`.agents/decisions.md` 2026-10-21). Branch `worktree-agent-a83ff1596fd6eee17` from master `a7300dee`: one
fix commit `fix(permission-pages): ... (aud-a53)`, third of four, after aud-a61; the pages edited in place (808 and 762 lines,
no registry entry moved); comments in the branch's comments-only commit. Behaviour: the two pages (both module versions) and
one new shared helper (base app version).

Evidence re-read at `a7300dee`: `MailboxPermissions.razor` `SubmitSingle` 459-530: the location read has its own refusal
(461, aud-a23o); the inner try then holds the on-premises permission check (470-471), the write (487-489), the success record
(501-506), the admin notification (509-511) and the user and owner notifications (519-520), none guarded; its catch (526-530)
says "Operation may have succeeded but audit logging failed: {ex.Message}" for every one of them - a throw before the write as
well as after it, the raw text, and a completed change shown as failed. `CalendarPermissions.razor` 429-507 the same (write
460-471, record 477-481, notifications 484-487, 495, 497, catch 503-507). `ExecuteOnPrem` sends the same three notifications
unguarded (Mailbox 728-730 and 736-737; Calendar 670-673 and 679-680) inside a try whose catch records the change as failed.

1. **Each notification in its own guard**, on both handlers of both pages - the record's "each notification call", and
   `ExecuteOnPrem`'s sit in the same unguarded shape: a throw is logged against the target and the change's result stands (the
   aud-a23m shape; the bulk loops' `PermissionChangeNotifier`). In place, on the call's own lines.
2. **The success record in its own guard** (`SubmitSingle`, both pages): a throw is logged and the change's result stands - the
   Constitution's "audit failure must not make an already-completed operation appear to have failed; log audit failures
   separately", and aud-a23m's own step for Migration. This is how "the catch's text is reserved for the audit write" is met:
   the audit write no longer reaches a catch at all, so no catch says "audit logging failed" for a throw that was not the
   audit's, and an audit write that throws no longer turns a completed change into a failure.
3. **The catch names its step.** A local `step` is set in place before each stage - before the write, the write, after the
   write - and the catch logs the exception against the target and the step and says, through new
   `Services/PermissionChangeFailure.Banner(step, ex)` and the aud-a23i helper (`ExchangeServiceBase.ResolveFailure(ex).primary`,
   the cmdlet's own words; never `ex.Message` as such): before the write "The change was not made: a step before the write
   failed: <cause>. Nothing was written."; the write "Whether the change was made could not be confirmed: the write failed:
   <cause>. Check the permissions before trying again."; after it "The change was written, but a step after the write failed:
   <cause>. Check the permissions to confirm the result." The activity completes with the same text.

Tests, through `PermissionPagesSelfGrantTests`' driver over a grant to someone other than the operator, which writes through
the harness: each notification throwing in turn, on each page - the change's own result, one warning, the other two still
sent; the success record throwing (the audit writer missing) - the change's own result and one warning; a throw after the
write (the activity's completion throwing, the one throw left there) - the after-the-write text in the cause's own words, not
its raw message; `ExecuteOnPrem`'s notifications throwing on each page - the on-premises write's result stands;
`PermissionChangeFailure.Banner` for each step and the aud-a23i unwrapping.

Mutations at the call site: M1 the Mailbox Permissions `SubmitSingle` admin notification's guard removed (M1c Calendar's).
Extras: E1 the catch's old raw text back (Mailbox); E2 the success record's guard removed (Mailbox); E3 the Mailbox
`ExecuteOnPrem` admin notification's guard removed.

Files: `Components/Pages/MailboxPermissions.razor`, `Components/Pages/CalendarPermissions.razor` (in place),
`Services/PermissionChangeFailure.cs` (new), `ExchangeAdminWeb.Tests/PermissionPagesSelfGrantTests.cs`, this record.

Caller-visible change, by design: a notification or a success record that throws no longer replaces a written change's result
with a failure - it is logged, and the change's result is shown. A throw inside the change says which step it came from and
whether anything could have been written, in the failing command's own words.

## Fix

- New `Services/PermissionChangeFailure.cs`: the three stages (`BeforeTheWrite` "a step before the write", `TheWrite` "the
  write", `AfterTheWrite` "a step after the write") and `Banner(step, ex)`, whose cause is
  `ExchangeServiceBase.ResolveFailure(ex).primary` (aud-a23i): "The change was not made: a step before the write failed:
  <cause>. Nothing was written."; "Whether the change was made could not be confirmed: the write failed: <cause>. Check the
  permissions before trying again."; "The change was written, but a step after the write failed: <cause>. Check the
  permissions to confirm the result."
- `Components/Pages/MailboxPermissions.razor` and `Components/Pages/CalendarPermissions.razor`, in place (808 and 762 lines, no
  registry entry moved): `SubmitSingle` declares `step` on the free line above its inner try and moves it on the free lines
  around the write (Mailbox 458, 486, 490; Calendar 428, 459, 472); the success record (Mailbox 501-506, Calendar 477-481)
  and each notification (Mailbox 509-511, 519, 520; Calendar 484-487, 495, 497) sit in their own `try { ... } catch`,
  logged against the target, the result standing; the catch (Mailbox 528-529, Calendar 505-506) logs the exception with the
  step and completes the activity and the banner with `PermissionChangeFailure.Banner(step, ex)`. `ExecuteOnPrem`'s three
  notifications the same (Mailbox 728-730, 736, 737; Calendar 670-673, 679, 680).

Tests: `PermissionPagesSelfGrantTests` +19 (its driver takes the grantee, the target, the mail and the progress service a test
gives) - `AWrittenChange_WhenANotificationThrows_StandsAsWritten_AndTheOtherNotificationsAreStillSent` (each page x the
administrator, user and owner notification: the result a success, all three sent, one warning, the record a success),
`AWrittenChange_WhenItsRecordThrows_StandsAsWritten` (each page, the writer missing: a success, every notification sent, one
warning, the write made), `AThrowAfterTheWrite_IsNamedAsSuch_InTheCausesOwnWords_NotItsRawText` (each page, the activity's
completion throwing an exception whose raw message differs from its captured PowerShell error: the after-the-write banner in
the captured words, the activity completed with it, one warning with the exception),
`AnOnPremChange_WhenANotificationThrows_StandsAsWritten` (each page x each notification), and
`TheStepBanner_SaysWhatCouldHaveBeenWritten_InTheCausesOwnWords` (each stage, the captured words and a plain message).

## Guard proof

The aud-a60 driver and protocol (that record's Guard proof; log `a53-mutations.log`). Filter
`FullyQualifiedName~PermissionPagesSelfGrant|FullyQualifiedName~ClickGate|FullyQualifiedName~ProgressRegistry|FullyQualifiedName~PermissionValidatorSelfGrant|FullyQualifiedName~MailboxPermission|FullyQualifiedName~CalendarPermission`
(549 tests). Fixed-file MD5s: `Components/Pages/MailboxPermissions.razor` `C7D94BAD4AEAD5110C440C318051DB6C`,
`Components/Pages/CalendarPermissions.razor` `24A0A94ED8D70EF0F8787340CA3823A0`. Every mutant built with 0 errors and every
restore matched; each kept its page's line count, so ClickGate and ProgressRegistry ran under it and passed. After the round,
the restored tree rebuilt and the filter: 549/549 green.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 | the Mailbox Permissions `SubmitSingle` admin notification's guard removed | `E2C96BD62201ED52E335736B6274E5C4` | 1 of 549: `AWrittenChange_WhenANotificationThrows_StandsAsWritten_AndTheOtherNotificationsAreStillSent(page: "Mailbox Permissions", which: "admin")` - the written change shown as failed |
| M1c | the same on Calendar Permissions | `6243224B48F72B8FB3219C924949B62A` | 1 of 549: the same test's `(page: "Calendar Permissions", which: "admin")` row |
| E1 | extra: the catch's old raw text back (Mailbox Permissions) | `79465F23D461DC3E3F80B1134336CA36` | 1 of 549: `AThrowAfterTheWrite_IsNamedAsSuch_InTheCausesOwnWords_NotItsRawText(page: "Mailbox Permissions")` |
| E2 | extra: the success record's guard removed (Mailbox Permissions) | `5611E048435E8CBE95ED38BA392C90F3` | 1 of 549: `AWrittenChange_WhenItsRecordThrows_StandsAsWritten(page: "Mailbox Permissions")` |
| E3 | extra: the Mailbox Permissions `ExecuteOnPrem` admin notification's guard removed | `F31DDBDC6E5B63F473B97508F62DE569` | 1 of 549: `AnOnPremChange_WhenANotificationThrows_StandsAsWritten(page: "Mailbox Permissions", which: "admin")` |

No separate pre-fix reproduction: M1 and E1-E3 are the handlers as they stood, each at its call site.

## Known gaps

- `ExecuteOnPrem`'s own success records (Mailbox Permissions 717-724, Calendar Permissions 665-668) are not in a guard of
  their own; since aud-a28q the writers do not throw, and a throw there reaches the catch aud-a59 gives its step text. A
  candidate in the hand-back.
- The stage names the step, not the command: a throw after the write says "a step after the write" and the command's own
  words, not which of the steps.

## Also noted (aud-a50/a52 agent, 2026-10-22)

The same catch's wording is wrong wherever it runs now: since aud-a28q the audit writers do not throw, so
"Operation may have succeeded but audit logging failed: {ex.Message}" is reached by other throws (a location
probe before anything was written, for one) and shows raw exception text. The fix names the step that failed
and uses the aud-a23i error-text helper, no raw message.

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

Fixed on branch `worktree-agent-a83ff1596fd6eee17`, not merged, not pushed. Due at merge: the base app version - the new shared
`PermissionChangeFailure` - and the Mailbox Permissions and Calendar Permissions module versions: what the single-change and
on-premises handlers show when a notification, the record or a step throws.
