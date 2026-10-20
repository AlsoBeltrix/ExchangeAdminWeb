# aud-a23: a partial permission change told the affected user nothing

**Severity**: HIGH. Access really changed and the person it changed for was not told. The
owner's standing ruling is that mailbox and calendar permission changes must notify the
affected user; a partial change is the case where that silently did not happen.
**Status**: Verified, fixed and independently reviewed
**Branch**: -- (direct-to-main)
**Commit**: `67c0c65`

## Evidence

Finding 23 of the 2026-10-16 audit, verified against the code.

`Components/Pages/MailboxPermissions.razor`, the single-mailbox cloud path in `SubmitSingle`:

```csharp
if (opResult.Success)
{
    var isGrant = isAdd;
    await Email.SendUserNotificationAsync(user, target, currentUser, permType, isGrant);
    await Email.SendOwnerNotificationAsync(target, user, currentUser, permType, isGrant);
}
```

`AddMailboxPermissionsAsync` applies FullAccess and SendAs in separate try/catch blocks and
aggregates through `MailboxPermissionOutcome.ForGrant`, which returns **`Success = false`**
when some rights landed and others did not - deliberately, so an operator is not told a
half-applied operation worked.

The consequence at the notification gate was not deliberate. FullAccess granted, SendAs
failed: the trustee can open the mailbox, the mailbox owner has a new delegate, and neither
is sent anything.

Two further details make it worse than a single missed mail:

- `permType` is the **requested** set, not the applied set. Had the gate been loosened
  naively, the notice would have claimed FullAccess+SendAs when only FullAccess existed.
- **The on-premises path in the same file already handles this**, at what is now line 731,
  by splitting `opResult.Detail` and notifying when anything succeeded. Only the cloud path
  was missing it, which is why this reads as an oversight rather than a policy.

## Predicted observable failure

An operator grants FullAccess and SendAs to a delegate. SendAs fails on a throttling error.
The page reports a partial failure to the operator, who retries later or escalates. The
delegate is never told they already have FullAccess, and the mailbox owner is never told
someone gained access to their mailbox - which is the notification that exists for the
owner's benefit, not the operator's.

## Approach

**A first-class `AppliedRights` on `PermissionResult`**, populated by
`MailboxPermissionOutcome` on every path where something landed - partial and full, grant and
revoke, cloud and on-premises. The notification gate becomes "did anything actually apply?"
rather than "did everything succeed?", and the notice names the applied rights.

It is a field rather than a parse of `Detail`, and that distinction is the whole reason this
needed more than a one-line change. `Detail` is overloaded: on a partial result it holds the
applied rights, but on a **successful cloud grant it holds the OWA link**. Copying the
on-premises path's `Detail.Split(',')` into the cloud path would have put a URL in the
permission-type line of a user's mail. The on-premises copy is safe only because it never
sees a cloud result.

`WithServicedNote` rebuilds the record field by field, so the new field is carried there too -
otherwise a servicer override would drop it, and the protected-target case is exactly when
the notification matters most.

**The page edit keeps the file at exactly 808 lines**, so no `ClickGateRegistry` line key
moves and no re-anchoring is needed. Verified by counting before and after.

## Files changed

- `Models/PermissionResult.cs` - the field, and carrying it through `WithServicedNote`
- `Services/MailboxPermissionOutcome.cs` - populated on all four landing paths
- `Components/Pages/MailboxPermissions.razor` - the gate (same line count)
- `ExchangeAdminWeb.Tests/MailboxPartialNotificationTests.cs`
- `ExchangeAdminWeb.csproj` - 2.30.0 -> 2.31.0. `PermissionResult` is a shared model.
- `Modules/ModuleCatalog.cs` - MailboxPermissions 1.2.4 -> 1.3.0

## Guard proof

The aggregation is driven for real: `MailboxPermissionOutcome` is the seam that exists so
these decisions are provable without a tenant. That the page reads the field is a source
scan, because the handler around it needs a live Exchange connection.

| Test | What it pins |
| --- | --- |
| `APartialGrant_NamesTheRightThatLanded` | the finding - FullAccess applied, SendAs failed |
| `APartialRevoke_NamesTheRightThatWasRemoved` | the same for revoke, with the failure first |
| `AFullGrant_NamesEveryRight` / `AFullRevoke_NamesEveryRight` (cloud and on-prem) | the fix did not only special-case partials |
| `ATotalFailure_AppliesNothing` | fail closed - nothing landed, nobody is told |
| `ASuccessfulCloudGrant_KeepsTheLinkOutOfTheAppliedRights` | the reason this is a field and not a `Detail` parse |
| `AServicedNote_DoesNotDropTheAppliedRights` | `WithServicedNote` rebuilds field by field |
| `ThePageNotifiesOnWhatApplied_NotOnOverallSuccess` | the wiring, and that the notice carries `notifyPerm` |

The last one matters most: a fix that populated the field and left the gate alone would pass
every other test here and change nothing for the person it is for.

| Mutation | Result |
| --- | --- |
| `AppliedRights` dropped from the partial grant | FAIL - 2 tests |
| page gate reverted to `if (opResult.Success)` | FAIL - `ThePageNotifiesOnWhatApplied_...` |
| `AppliedRights` dropped from `WithServicedNote` | FAIL - `AServicedNote_DoesNotDropTheAppliedRights` |

All three sources restored byte-identical (md5 verified), touched, rebuilt.

Gate: Release 0 errors; suite 4423 passed / 3 known AD skips / 0 failed; format and
`git diff --check` clean.

## Known gaps

- **No Exchange was reached.** Nothing here proves the handler executes the gate at runtime,
  only that the source reads the field and that the field is correct.
- **The on-premises path still parses `Detail`.** It is correct today, because it only ever
  receives on-premises results, which carry no OWA link. It is now the only consumer of that
  convention and should move to `AppliedRights`; left alone because A23 names the cloud path.
  Filed as `aud-a23b`.
- **The bulk path is untouched** - that is `aud-a24`, next in this wave.
- The admin notification still reports plain success or failure on the cloud path, where the
  on-premises path marks partials with an `_PARTIAL` action suffix. Not in this finding's
  scope, and the audit row does record the applied rights.
- `CalendarPermissions.razor` was not examined for the same shape beyond what `aud-a24`
  covers.

## Closeout

Fixed on `master` as `67c0c65`.

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

- The page scan passed if the applied-rights block were nested back under
  `if (opResult.Success)`, which re-breaks the partial case while every asserted string
  remains. The guard now requires no `opResult.Success` between taking the applied set and
  sending.

One MEDIUM runtime defect was found elsewhere in the batch - bulk calendar removals
notified with the operator's word ("Remove") rather than the access ("Calendar Access") -
and is fixed in the same follow-up. See `aud-a24`.
