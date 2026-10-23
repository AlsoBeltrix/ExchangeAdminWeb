# aud-a53: Permission pages - the SubmitSingle catch reports any throw as "Operation may have succeeded but audit logging failed", and the notifications are not separately guarded

**Severity**: LOW - latent (EmailService catches per-recipient send failures), but the shape is the aud-a23m
defect on the permission pages. In `MailboxPermissions.razor` and `CalendarPermissions.razor` the
`SubmitSingle` catch reports every throw in its try as "Operation may have succeeded but audit logging
failed"; the admin notifications inside that try are not guarded on their own, so a throw there would
replace a successful write's result with that text.
**Status**: In progress; plan below, approved under the owner's 2026-10-21 "EVERYTHING" go
**Branch**: `worktree-agent-a83ff1596fd6eee17` (from master `a7300dee`)
**Commit**: -
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

## Guard proof

Pending.

## Also noted (aud-a50/a52 agent, 2026-10-22)

The same catch's wording is wrong wherever it runs now: since aud-a28q the audit writers do not throw, so
"Operation may have succeeded but audit logging failed: {ex.Message}" is reached by other throws (a location
probe before anything was written, for one) and shows raw exception text. The fix names the step that failed
and uses the aud-a23i error-text helper, no raw message.
