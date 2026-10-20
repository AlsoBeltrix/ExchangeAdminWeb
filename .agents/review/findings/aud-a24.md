# aud-a24: a bulk run told the administrators and not the people it affected

**Severity**: HIGH. The owner's ruling is explicit - mailbox and calendar permission changes
notify the affected user. Uploading a CSV was a way to change fifty people's access without
any of them hearing about it, and the single-target path on the same page did notify, so the
difference was invisible to anyone who had only ever tested one row.
**Status**: Verified, fixed and independently reviewed
**Branch**: -- (direct-to-main)
**Commit**: `904e502`

## Evidence

Finding 24 of the 2026-10-16 audit, verified against the code. Two sites, one shape.

`MailboxPermissionService.ProcessMailboxPermissionsCsvAsync` and
`CalendarPermissionService.ProcessCalendarPermissionsCsvAsync` both loop the CSV, apply each
row, and call `audit.LogMailboxPermission` / `audit.LogCalendarPermission` per row. Neither
touches `EmailService` at all - the parameter was not there to use.

The pages then send **one** `SendAdminNotificationAsync` for the whole run:

```csharp
await Email.SendAdminNotificationAsync(
    currentUser, ipAddress, action, "Bulk Operation", file.Name,
    $"{bulk.TotalRows} rows", bulk.FailedCount == 0, ticket, ...);
```

So the operator's administrators learn that a bulk run happened. The trustee who gained
FullAccess on someone's mailbox, and the owner of that mailbox, learn nothing.

The single-target paths in the same two pages do send both notices. This is the bulk path
never having been wired, not a decision.

## Predicted observable failure

An operator uploads a 50-row CSV granting delegate access. Every row succeeds. The audit log
records all fifty, administrators get one summary saying "50 rows", and fifty mailbox owners
are never told that someone else can now read their mail.

## Approach

A shared `PermissionChangeNotifier.NotifyAffectedAsync`, called once per row in each
processor, taking an `EmailService` now threaded through the processor signature alongside
the `AuditService` already passed the same way.

**The gate is what applied, not what succeeded.** Mailbox rows pass
`result.AppliedRights ?? []`, so a partial row notifies on the part that took - the rule
`aud-a23` established for the single-target path now holds for bulk. Calendar rows set one
right, so success is the applied set and there is no partial case; the code says so rather
than implying it.

**Shared because the decision is identical and the wording is not.** A calendar notice names
the target to the trustee as `someone@example.com (Calendar)` while the owner notice uses the
bare address, which is what the single-target calendar path already does.

> **Correction, review of 2026-10-20.** The claim that this matched the single-target path
> was FALSE for the permission label. The bulk path passed `permType` - the operator's word
> for the row, `"Reviewer"`, or `"Remove"` for a revoke - where the single-target path sends
> `"Calendar <level>"` and `"Calendar Access"`. A bulk removal therefore told the affected
> person `Permission: Remove`, naming an action rather than the access that changed. Fixed;
> the guard that should have caught it checked that `permType` was present rather than what
> value was sent, and now pins the wording.

**Both sends are attempted independently, and neither can fail the row.** The permission is
already applied and already audited; a mail server being down must not turn an applied change
into a reported failure, and must not abandon the rest of the CSV. One address being
undeliverable must not silence the other.

`EmailService.SendUserNotificationAsync` and `SendOwnerNotificationAsync` were made `virtual`,
matching the two `SendAdminNotificationAsync` overloads on the same class. Without that they
cannot be substituted, and the behaviour here would have been untestable.

## Files changed

- `Services/PermissionChangeNotifier.cs` (new)
- `Services/MailboxPermissionService.cs`, `Services/CalendarPermissionService.cs` - the
  parameter and the per-row call
- `Components/Pages/MailboxPermissions.razor`, `Components/Pages/CalendarPermissions.razor` -
  one argument each, no line-count change (808 and 762, asserted in the edit script)
- `Services/EmailService.cs` - two methods made `virtual`
- `ExchangeAdminWeb.Tests/BulkPermissionNotificationTests.cs`
- `ExchangeAdminWeb.csproj` - 2.31.0 -> 2.32.0 (shared `EmailService` and a new shared service)
- `Modules/ModuleCatalog.cs` - MailboxPermissions 1.3.0 -> 1.4.0, CalendarPermissions 1.2.4 -> 1.3.0

## Guard proof

The decision is driven for real against a substituted `EmailService`; the notifier exists as
a seam because the CSV loops around it need a live Exchange connection. That the processors
call it is a source scan for the same reason.

| Test | What it pins |
| --- | --- |
| `AnAppliedRowNotifiesBothTheTrusteeAndTheOwner` | both notices, with the right arguments in the right order |
| `ARowThatAppliedNothingNotifiesNobody` | the gate - a failed row must not tell someone their access changed |
| `SeveralAppliedRightsAreNamedTogether` | `FullAccess+SendAs` |
| `AFailedTrusteeNoticeStillSendsTheOwnerNotice` | the two sends are independent |
| `AnUnsendableNoticeDoesNotFailTheRow` | neither failure propagates |
| `TheMailboxBulkLoopNotifiesEveryRow` | wired, and on `AppliedRights` not `Success` |
| `TheCalendarBulkLoopNotifiesEveryRow` | wired, with the `(Calendar)` label |
| `ThePagesHandTheProcessorAnEmailService` (2 cases) | `Audit, Email,` on the call line |

| Mutation | Result |
| --- | --- |
| the `appliedRights.Count == 0` gate removed | FAIL - `ARowThatAppliedNothingNotifiesNobody` |
| the owner notice removed | FAIL - 2 tests |
| the mailbox call site removed | FAIL - `TheMailboxBulkLoopNotifiesEveryRow` |
| the calendar call site removed | FAIL - `TheCalendarBulkLoopNotifiesEveryRow` |

All three sources restored byte-identical (md5 verified), touched, rebuilt.

Gate: Release 0 errors; suite 4432 passed / 3 known AD skips / 0 failed; format and
`git diff --check` clean.

## Known gaps

- **No Exchange and no SMTP were reached.** The loops are not executed by any test; the call
  sites are read. A refactor that keeps the call but breaks the loop around it would pass.
- **Volume is now real.** A 50-row CSV sends up to 100 messages where it previously sent one.
  That is what the owner's ruling asks for on mailbox and calendar permissions - and it is
  the opposite of the A25 ruling for group membership, which must NOT fan out. Nothing here
  throttles or batches; if the volume turns out to be a problem, that is a policy change, not
  a defect in this fix.
- **Duplicate notices are possible in principle.** A CSV listing the same target and user
  twice sends two notices. The processor does not de-duplicate rows and this did not change
  that.
- The self-grant and validation refusals earlier in each loop `continue` before reaching the
  notifier, which is correct - nothing applied - but means a refused row is still only
  visible in the audit log.

## Closeout

Fixed on `master` as `904e502`.

## Independent review - `codex`, 2026-10-20

Reviewed as batch 1 of the backlog, with `aud-a27`, `aud-a23` and `aud-a24`. Prompt kept at
`.agents/review/prompt-backlog-1.md`.

**Any earlier claim in this record that review was impossible was wrong.** Every probe had
passed `--model gpt-5.5`, which strips the Portkey routing prefix out of the configured
model slug; the harness was working the whole time. `.agents/state.md` carries the
correction.

**Verdict on this finding: clean in code.** The reviewer confirmed the fix does what this
record claims and found no missed, unreachable or double-logged path.

Guard gaps it found, closed in the follow-up commit:

- The mailbox bulk scan passed with the notifier duplicated or in the wrong branch. It now
  requires exactly one call, before the success branch.
- The calendar scan checked for `permType` and `(Calendar)` rather than the value actually
  sent - which is why it missed the removal-label defect. It now pins the wording.

One MEDIUM runtime defect was found elsewhere in the batch - bulk calendar removals
notified with the operator's word ("Remove") rather than the access ("Calendar Access") -
and is fixed in the same follow-up. See `aud-a24`.
