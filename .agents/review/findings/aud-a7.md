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
