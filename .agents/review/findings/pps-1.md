# pps-1: Mailbox/Calendar bulk CSV cannot service, and the on-prem branch writes without a fresh check

**Severity**: HIGH -- an authorised servicer is refused on the bulk path (the capability silently
does nothing where it is most useful), and the on-prem path performs a write whose protection
decision is stale and whose override is absent from the audit record.
**Status**: Verified
**Branch**: -- (default-branch mode)
**Commit**: `6f7f2ac`

## Evidence

Two distinct defects in the same pair of modules, both in code the `e1547e7` commit message
claims honours servicing.

**(a) Bulk CSV uses the non-servicing overload.**
`Services/MailboxPermissionService.cs:222`:

```csharp
var validationError = await validator.ValidateTargetMailboxAsync(row.Target);
```

That is the back-compat overload, which takes no `ClaimsPrincipal` and by construction never
services -- it was kept precisely so a caller that supplies no principal cannot accidentally
allow. `ProcessMailboxPermissionsCsvAsync` (`:164`) never receives a principal to pass, so the
page cannot supply one either (`Components/Pages/MailboxPermissions.razor:509`). Calendar's bulk
path has the same shape.

**(b) The on-prem confirmation branch re-enters without re-validating.**
`Components/Pages/MailboxPermissions.razor:540` `ExecuteOnPrem` runs after the operator confirms a
dialog. It re-checks AUTHORIZATION (`:560-562`) but performs no protected-principal check and
carries no serviced note into its audit call. The protection decision was made in the earlier
handler, before the confirmation prompt.

## Predicted observable failure

(a) A member of the servicer group uploads a CSV containing a protected mailbox. The row is
refused with "protected principal" while the same operator, doing the same thing one row at a
time through the single-target form, is allowed. The bulk path is the one an exec support team
would actually use for a batch of moves.

(b) An on-prem grant against a protected mailbox writes with a protection verdict computed before
the confirmation dialog, and the audit row for that write contains no record that an override was
exercised. If the operator sat on the dialog, the verdict is arbitrarily old.

## What

These are two faces of one mistake: servicing was implemented on the paths that were easy to see
(the single-target handler) and not on the paths that fork away from it. The Constitution's rule
is the check runs *immediately before the write* -- (b) violates that directly, and it violated it
before this work stream too, but this work stream is what made the missing note observable.

(a) is not merely a missing feature. The opt-in list in `ModuleConfig.razor` now advertises a
servicer grant for MailboxPermissions and CalendarPermissions, so an admin can configure a group
and reasonably expect it to work everywhere in the module. A grant that works on one form and not
the other is worse than one that does not exist, because nothing tells the operator which is which.

## Approach

Not yet implemented -- recorded first, per the one-finding-per-commit rule.

(a) Thread `ClaimsPrincipal?` and the module id through
`ProcessMailboxPermissionsCsvAsync` / the Calendar equivalent to the servicing overload, and
carry each row's serviced note into that row's audit call. The per-row audit already exists;
the note joins its `extra`, never `errorDetail`.

(b) Re-run the protection validation inside `ExecuteOnPrem` immediately before the write, and
carry the resulting note into the audit call. Re-validating is correct regardless of servicing:
a confirmation dialog is an unbounded pause.

## Files changed

None yet.

## Guard proof

Pending. Both need a test that fails with the current code: for (a), an authorised servicer's
bulk row against a protected target must be applied rather than refused; for (b), the on-prem
execute path must consult protection at execute time (source-level tripwire -- no bUnit harness
exists, so no test can render the page).

## Coder dispute (if any)

None. Both verified by reading the current code, not inferred from the review summary.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh`
Range: `b351005..HEAD` (10 commits, 52 files), single consolidated pass as the owner requested.

**The pass did not deliver its final consolidated report**: the codex auth token was revoked
mid-run (`refresh_token_invalidated`, 56 occurrences in the transcript) and the run ended after
its analysis phase. The findings above were recovered from the reasoning trace it did emit, and
then verified independently against the current code before being recorded. The severity and the
proposed fixes are mine, not the reviewer's -- it never got to state either.

Its analysis phase confirmed clean: DI lifetimes (no captive dependency; servicer and its
section-access dependency are both singleton), the ASCII scan, the opt-in list against real gates
for all 15 modules, Migration's per-target notes surviving the audit-failure rewrap, Self-Service
not letting the grant bypass ownership, Conference Rooms' deliberate null-principal bulk refusal,
and Licensing's request-thread decision.
