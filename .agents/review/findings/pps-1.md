# pps-1: Mailbox/Calendar bulk CSV cannot service, and the on-prem branch writes without a fresh check

**Severity**: HIGH -- an authorised servicer is refused on the bulk path (the capability silently
does nothing where it is most useful), and the on-prem path performs a write whose protection
decision is stale and whose override is absent from the audit record.
**Status**: Fixed
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

(a) `ClaimsPrincipal?` and an explicit module id thread through
`ProcessMailboxPermissionsCsvAsync` and `ProcessCalendarPermissionsCsvAsync` to the servicing
overload. Each row's serviced note joins that row's existing audit call via `extra`, never
`errorDetail`. Both services gained their own `ServicerModuleId` constant matching the page's --
the validator serves three modules, so a borrowed id would cross-authorise.

(b) `ExecuteOnPrem` re-validates immediately before the write on BOTH pages, and carries the
resulting note into the audit. Calendar had the identical defect and is fixed in the same change;
the finding as written named only Mailbox, and Calendar was found by checking rather than assuming
the pair diverged.

Re-validating is correct independent of servicing: a confirmation dialog is an unbounded pause, so
the pre-prompt verdict may be arbitrarily stale, and the Constitution requires the check
immediately before the write.

## Files changed

- `Services/MailboxPermissionService.cs`, `Services/CalendarPermissionService.cs` -- bulk entry
  points take the principal; loops use the servicing overload; per-row audit carries the note.
- `Components/Pages/MailboxPermissions.razor`, `Components/Pages/CalendarPermissions.razor` --
  bulk passes the re-authorised principal; `ExecuteOnPrem` re-validates and audits the note.
- `ExchangeAdminWeb.Tests/BulkAndOnPremServicingTests.cs` -- new.

## Guard proof

Reverting both halves fails **3 of 10**: the Mailbox bulk loop, the Calendar on-prem check, and the
Calendar on-prem audit. Reverts confirmed applied at both files before trusting the verdict, and
confirmed removed after.

Mixed strength, stated rather than glossed: the bulk entry-point guard is reflection over the
compiled signature (a missing principal parameter means the method CANNOT service, whatever its
body does); the loop and on-prem guards are source-level, because the loop can carry a principal
and still call the non-servicing overload, and `ExecuteOnPrem` is a `.razor` handler no test can
render. The on-prem guards are anchored INSIDE the `ExecuteOnPrem` body -- a file-wide match would
have been satisfied by the single-submit handler's own validation while the on-prem write stayed
unchecked, which is exactly the state being fixed.

One guard was rewritten mid-work: the first cut matched exact source formatting and failed on
whitespace, which is a guard a reformat breaks and a real regression could slip past. Now matched
on the call's arguments.

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
