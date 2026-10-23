# aud-a44: The pool's borrow reads enablement through the fail-open IsModuleEnabled, and the pages' self-grant audit-failure banner says "self-grant" for an incomplete check and shows a raw exception

**Severity**: LOW. (1) `ExoConnectionPool.BorrowAsync` reads enablement through `IsModuleEnabled`, so an
unreadable enablement store refuses with "Exchange Online module is not enabled..." - fail-closed but
misleading (aud-a28t added `TryGetModuleEnabled` for exactly this). (2) In `MailboxPermissions.razor` and
`CalendarPermissions.razor`, when the self-grant audit write throws, the banner reads "Self-grant blocked,
but audit logging failed: {ex.Message}": it says self-grant for a check that could not be completed
(aud-a28y) and shows a raw exception message; since aud-a28q the writers do not throw, so the branch is
nearly dead, but the text is wrong where it can run.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28y/a28x/a26t agent, 2026-10-22, candidates 5 and 6 of its hand-back)

## Evidence

At `448b02c8`: `Services/ExoConnectionPool.cs` around 249; `Components/Pages/MailboxPermissions.razor` 450
and `Components/Pages/CalendarPermissions.razor` 420 (both line-keyed by `ClickGateRegistry`, 808 and 762
lines, edit in place only).

## Approach (to be written by the implementing agent before code)

- `BorrowAsync` reads enablement through `TryGetModuleEnabled`; unreadable refuses with "the Exchange
  Online enablement could not be read: <rows and remedy>" (the aud-a28x message shape), disabled keeps its
  text.
- The banner uses the validator's own refusal text and "the audit row could not be written" without the
  raw message (the aud-a28u outcome tells it whether the row was written).
- Tests through the pool's config-read tests and the pages' reflection driver; mutations at the call site:
  `IsModuleEnabled` again; the raw message again.

## Guard proof

Pending.
