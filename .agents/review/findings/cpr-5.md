# cpr-5: A failed SMTP disconnect reported an accepted password email as undelivered

**Severity**: HIGH - this caller has already changed the password by the time it asks whether the
mail went. A false negative makes it discard a credential the owner has already received and
audit a delivery failure that did not happen.
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: the commit that completes this record

## Evidence

`Services/EmailService.cs`, `SendEmailOrThrowAsync` as of `e1b786a`:

```csharp
await client.SendAsync(message);
await client.DisconnectAsync(true);
```

`DisconnectAsync(true)` sends QUIT and waits for the server. It runs AFTER the message has been
accepted and can throw on a dropped connection. The method has no try/catch, so that throw
propagates to `SendCloudPasswordResetAsync`, whose catch returns `false`.

## Predicted observable failure

The SMTP server accepts the owner's mail, containing a live password for an admin account. The
connection then drops during QUIT. `SendCloudPasswordResetAsync` returns false. Per the plan's
post-write rule the caller discards the password, tells the operator the password was changed but
could not be delivered, and audits `CloudPasswordReset_DeliveryFailed`.

The owner has the working password in their inbox. The operator believes nobody does and runs the
reset again, invalidating the password that was delivered. The audit record says a delivery failed
that succeeded.

## Approach

Wrap the disconnect in its own try/catch, log a warning, and let the method return normally. The
handoff is complete at `SendAsync`; everything after it is cleanup, and a failure in cleanup is
not a failure to send.

**Fixed in the shared helper rather than only on this path**, deliberately. No caller of
`SendEmailOrThrowAsync` wants a successful send reported as a failure because a socket closed
badly, and the plan's own definition of delivery is "accepted by the mail server". Scoping the fix
to the password path would have left the same false negative for every other caller while
implying the others were somehow fine with it.

## Files changed

- `Services/EmailService.cs` - the disconnect try/catch in `SendEmailOrThrowAsync`.
- `ExchangeAdminWeb.Tests/CloudPasswordResetEmailTests.cs` - `EmailSendHandoffTests`.

## Guard proof

`EmailSendHandoffTests`, two source-text assertions: the disconnect sits inside a try that opens
after the send, and the "sent" log line follows the send rather than the disconnect. Mutation:
remove the try/catch and `Disconnect_failures_after_a_successful_send_are_absorbed` fails.

Source-text rather than behavioural because `SendEmailOrThrowAsync` is private and needs a mail
server. This is the shape `DeployInvariantsTests` uses for the same reason, and it is stated as a
limitation rather than passed off as equivalent: it proves the try exists, not that MailKit throws
where we think it does.

## Coder dispute

None. The finding and its recommendation were both correct; the only change is that the fix went
into the shared helper rather than being special-cased for this caller.

## Known gaps

No test drives a real disconnect failure. Doing so needs an SMTP server that accepts a message and
then drops the connection, which is a test-infrastructure work stream rather than a slice of this
module.

## Reviewer comments

Round 1, Change review over `e1b786a^..e1b786a`:
Reviewer: codex / `@azure-openai-eus2-global/gpt-5.5-dzs` / xhigh / standard, 2026-09-23.
Capability proof passed. Verdict **unsound**, one HIGH, no CRITICAL.
It cleared the rest of the slice explicitly: the password is not logged by the new helper, the
HTML encoding and the conditional wording are correct, cpr-4 is closed by the three-state sync
read, and the base app version bump is complete across all three fields.

Per `.agents/decisions.md` 2026-08-31, a HIGH closes on the coder-side guard proof above.

## Closeout

Fix, tests and this record land with the descriptor-and-page slice.
