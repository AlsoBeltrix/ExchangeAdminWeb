# cpr-8: A transport failure on the PATCH was audited as "no change was made"

**Severity**: HIGH - the write may have applied. Reporting it as a refusal tells the operator and
Splunk that nothing happened, when the account may be sitting with a password nobody knows.
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: the commit that completes this record

## Evidence

`Services/GraphTokenClient.cs:143` calls `HttpClient.SendAsync` with no try/catch around it, so a
timeout or a dropped connection throws out of `PatchWithStatusAsync`. In `3b29e5f` that exception
reached the page's outer catch, which set a generic "The reset failed" message and audited
`passwordDelivery = "NotAttempted"` with `GraphReadFailed`.

Graph may have received and applied the request before the response was lost. Nothing in that path
distinguishes "the request never arrived" from "the response never came back".

## Predicted observable failure

The PATCH reaches Graph and changes the password. The connection then drops before the 204 is
read. The owner is not emailed, the operator is told the reset failed, and Splunk records
`NotAttempted`. The account now has a password that nobody - not the owner, not the operator, not
the record - knows, and every signal says no write was attempted, so nobody goes looking.

## Approach

A third outcome. `CloudPasswordResetRefusal.WriteIndeterminate` and a `passwordDelivery` value of
`Indeterminate`, produced by catching around the PATCH specifically rather than at the top of the
handler. The operator is told the change was sent, no response came back, the password has been
discarded and not delivered, and the account must be checked before retrying. It logs at
**Critical** for the same reason the post-write send failure does.

**It is not a refusal and is not recorded as one.** The distinction the code now carries is
"refused before the write" versus "issued, outcome unknown", and collapsing the second into the
first is the defect.

## Files changed

- `Services/CloudPasswordResetService.cs` - the enum member and the try/catch around the PATCH.
- `Components/Pages/CloudPasswordReset.razor` - the indeterminate branch.
- `docs/CloudPasswordReset-Plan.md` - `passwordDelivery`'s value list and the refusal list.
- `ExchangeAdminWeb.Tests/CloudPasswordResetWritePathTests.cs` - two guards.

## Guard proof

Mutation: change the indeterminate branch to audit `delivery: "NotAttempted"` and
`The_indeterminate_outcome_is_never_audited_as_NotAttempted` fails. A mutation removing the
try/catch entirely was attempted first and is NOT counted - it did not compile, and a
non-compiling mutation proves nothing about a test.

## Coder dispute

None.

## Known gaps

The outcome is inferred from an exception, not from asking Graph what the account's state now is.
A follow-up read could narrow it - the password's `lastPasswordChangeDateTime` would say whether
the write landed - but that is a second network call on a path where the network has just failed,
and a failed confirmation read would leave the same ambiguity one layer deeper. Recorded as a
deliberate limit rather than an oversight.

## Reviewer comments

Round 1, Change review over `3b29e5f^..3b29e5f`:
Reviewer: codex / `@azure-openai-eus2-global/gpt-5.5-dzs` / xhigh / standard, 2026-09-23.
Capability proof passed. Verdict **unsound**, two HIGH and two MEDIUM, no CRITICAL.
It cleared the happy path explicitly: the password stays out of the audit, the administrator
notification and the log text, and the explicit send-failure branch does not reveal it.

A HIGH closes on the coder-side guard proof (`.agents/decisions.md` 2026-08-31).

## Closeout

Fix and this record land with cpr-9, cpr-10 and cpr-11 in one commit.
