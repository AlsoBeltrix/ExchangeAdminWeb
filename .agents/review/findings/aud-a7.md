# aud-a7: Out of Office checks one mailbox and can write another

**Severity**: HIGH - a protected mailbox's auto-reply can be changed by a non-servicer, and
the audit record names the mailbox that passed rather than the one written.
**Status**: Verified
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Finding 7 of the 2026-10-16 repository audit, **independently reached twice more**: by the
codex-commercial review of `aud-a2` (finding 2), and by the same reviewer's note that "Mailbox
and Calendar capture their targets before awaits" - this page did not. Three sources, one
defect.

`Components/Pages/OutOfOffice.razor.SetOof` read the editable field fresh at every step:

```csharp
var protectedValidation = await Validator.ValidateTargetMailboxAsync(
    setEmail.Trim(), oofAuthState.User, ServicerModuleId);
...
isSetting = true;      // only AFTER the awaited check
...
setResult = await OofService.SetOutOfOfficeAsync(setEmail.Trim(), ...);
```

`isSetting` is what disables the mailbox box, and it went up **after** the awaited
protected-principal resolution - which reaches Exchange and is documented in this codebase at
ten to fifteen seconds. The control stayed enabled for that entire window, and Blazor permits
re-entry across an incomplete await.

So: submit an unprotected mailbox, change the address while resolution is in flight, and the
write lands on whatever is in the box. Only the first address was ever checked.

**`aud-a2` widened the window rather than creating it.** Always resolving means an
address-only protected configuration now also makes that Exchange round trip, where before it
compared a string and returned immediately.

## Predicted observable failure

A non-servicer enables an auto-reply on a protected executive's mailbox by submitting their
own address and switching it during the pause. The audit row records the address that passed
the check, so the trail names the wrong mailbox.

## Approach

The Comms-10k C7 shape, reused rather than reinvented:

- **Refuse re-entry as the first statement** and raise `isSetting` immediately, both before
  any await. The schedule validation that used to run first now sits after them and lowers the
  flag on its early return.
- **Capture `var target = setEmail.Trim()` once** and use it everywhere - the write, the three
  audit paths, the progress label, the previous-state read and the notification.

Both halves are needed and they do different jobs. The guard closes the window; the capture
means that even if something reopens it, this run acts on the address it validated. A guard
alone would leave the defect one `StateHasChanged` away from returning.

## Files changed

- `Components/Pages/OutOfOffice.razor`
- `ExchangeAdminWeb.Tests/OutOfOfficeTargetCaptureTests.cs`

## Guard proof

| Test | What it pins |
| --- | --- |
| `SetOof_RefusesReentry_AndRaisesTheBusyFlag_BeforeAnyAwait` | the guard exists, the flag follows it, and both precede the first `await` |
| `SetOof_CapturesTheTarget_AndNeverRereadsTheEditableField` | `setEmail` appears nowhere after the capture |

Mutations, both run this session:

| Mutation | Result |
| --- | --- |
| delete the re-entry guard and the flag raise | FAIL (`RefusesReentry`) |
| restore `setEmail.Trim()` at the write itself | FAIL (`CapturesTheTarget`) |

The second is the defect exactly. Page restored byte-identical (md5 verified) and touched.

Both read `ProgressScan.CodeView`, which blanks comments - the comments in this method mention
`setEmail` by name, and reviewing `aud-a3` showed raw-text tripwires passing against
commented-out code.

Out of Office is `tier 3, not approved` in `ClickGateRegistry`, so it carries no line-keyed
entries and the added lines needed no re-anchor. Verified rather than assumed.

## Coder dispute

None.

## Known gaps

**These are source scans, not behaviour.** Comms-10k's equivalent guards run against a real
page instance through a reflection harness, but that harness is built for that page; standing
one up for this method would have been larger than the fix. So nothing here proves the control
is actually disabled in a browser - only that the code that disables it runs before the await.

The same pattern should be audited across the other pages that await before raising their busy
flag. `aud-a2`'s review confirmed Mailbox Permissions and Calendar Permissions capture their
targets correctly; the rest of the module set has not been checked.

## Reviewer comments

Source: the 2026-10-16 audit, finding 7; independently raised by codex-commercial /
`gpt-6-astra` / max while reviewing `efa08c5`. Verified by the implementer before changing
anything. Independent review of the fix pending.

## Closeout

Fixed directly on `master`. Not pushed.

## Independent review, 2026-10-19 (codex-commercial / gpt-6-astra / max) - CHANGES REQUIRED

The reviewer confirmed the target capture is complete - "validation, previous-state lookup,
mutation, audit and notification all use `target`; the re-entry guard precedes every await" -
and then found a regression this fix introduced.

**The form locked permanently after a refusal.** `isSetting = true` was raised before the
awaited protected-principal check, which was the point of the fix, but the refusal's `return`
sat **above** the `try` whose `finally` clears the flag. So the mailbox box and the submit
button stayed disabled and every further click returned at the re-entry guard. The operator had
to reload the page.

It is not limited to deliberately protected accounts: `ValidateTargetMailboxAsync` returns the
same denial for a mailbox that does not exist or that it could not resolve, so an ordinary typo
locked the form.

Two paths, not one - anything thrown by `GetAuthenticationStateAsync` or
`ValidateTargetMailboxAsync` stranded the flag the same way. Both are new; before this fix the
flag went up after that region.

**Repair.** The `try` now opens immediately after `Progress.Begin`, so the target check, the
refusal and anything either throws all pass through the existing `finally`. There is still
exactly one `Complete` in source order, so `ClickGateRegistry` condition 3 reads what it read
before; the refusal now completes the activity as the non-success it is, carrying its reason,
rather than being ended silently by the `using`. The stale comment asserting the opposite was
removed.

Guard: `SetOof_LeavesNoWayOutBetweenRaisingTheBusyFlagAndTheTryThatClearsIt` walks every
`return` between the flag raise and the `try` and requires each to clear the flag first. The
schedule-validation return does, and is allowed; any new one that does not fails.

Mutation: deleting `isSetting = false;` from the schedule-validation return fails it with "A
return between raising isSetting and the try must clear the flag first". Page restored
byte-identical (md5 `1e5675ba71baa12a1a37006ba63183c4`), touched, rebuilt.

Gate: 4299 passed / 3 known AD skips / 0 failed; format and `git diff --check` clean. Module
version 1.1.1 -> 1.1.2; `da2c917` omitted its bump and nothing has been deployed, so this one
covers both.

**The reviewer's other point stands and is NOT fixed here.** These guards remain source scans.
They would have passed with the stuck flag in place, and only caught it once a scan was written
specifically for it. A behavioural harness for this page is still absent, and that limit is
real: the page captures only the mailbox, while action, messages, schedule and ticket are still
live field reads after awaits. That is pre-existing, outside A7's scope, and recorded as
`aud-a7b`.
