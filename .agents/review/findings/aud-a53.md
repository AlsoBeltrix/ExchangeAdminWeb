# aud-a53: Permission pages - the SubmitSingle catch reports any throw as "Operation may have succeeded but audit logging failed", and the notifications are not separately guarded

**Severity**: LOW - latent (EmailService catches per-recipient send failures), but the shape is the aud-a23m
defect on the permission pages. In `MailboxPermissions.razor` and `CalendarPermissions.razor` the
`SubmitSingle` catch reports every throw in its try as "Operation may have succeeded but audit logging
failed"; the admin notifications inside that try are not guarded on their own, so a throw there would
replace a successful write's result with that text.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
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

## Guard proof

Pending.
