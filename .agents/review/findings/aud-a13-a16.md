# aud-a13 / aud-a16: an action must act on the subject it was prepared for

**Severity**: HIGH for A13 - an MFA reset aimed at the account the operator reviewed could
land on a different one. MEDIUM for A16 - one record's country list saved onto another.
**Status**: Verified and fixed; A14 and A15 of the same group still open
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Findings 13 and 16 of the 2026-10-16 audit. The plan groups A13-A16 and says to fix them
"with one shared discipline, not five bespoke patches". The discipline, already established
by `aud-a7` and Comms-10k C7: **bind the action to the subject it was prepared for, and never
re-read editable state to decide what to act on.**

### A13 - MFA Reset

`Components/Pages/MfaReset.razor`. `ListMethods` correctly captured the UPN before its await,
but nothing else did:

- both headings rendered `@userPrincipalName`, the live box, over results belonging to
  whoever was listed;
- `ExecuteReset` re-read `userPrincipalName.Trim()`.

So: list A, edit the box to B, click Reset. The confirmation names B, the method list on
screen is still A's, the deletion targets B, and `methodTypesBeforeReset` - taken from A's
list - is what the audit records against B.

### A16 - the country picker

`Components/Shared/CountryCodePicker.razor` synced its selection from `Value` behind a
one-shot `_initialized` latch, set the first time `Value` arrived non-empty. The component is
reused across records (`NamedLocations.razor:172` binds it to the edit form), so opening
location A and then location B left A's selection in place, and Apply wrote A's countries
onto B.

## Approach

**A13** adds `listedUpn`, set when a listing succeeds and cleared when a new one starts. Both
headings render it, and `ExecuteReset` uses it. With nothing listed it **refuses** rather than
falling back to the box - the fallback is the defect, and a default here would reinstate it
quietly.

**A16** replaces the latch with `_syncedFrom`, the value last read from. A re-sync happens
when the bound value actually *changes*, which is the condition that was wanted all along.
`Apply` and `ClearAll` record what they emit before emitting it, so the parameter set that
echoes the component's own output is not mistaken for the record changing underneath it.

## Files changed

- `Components/Pages/MfaReset.razor`
- `Components/Shared/CountryCodePicker.razor`
- `ExchangeAdminWeb.Tests/StaleTargetDisciplineTests.cs`

## Guard proof

**Source scans, and that is a real limit.** Neither page has a harness. After the `aud-a31`
review showed scans of this shape passing against broken code, each of these checks the order
or the absence of a named expression inside a bounded method body, not that a string appears
somewhere in the file.

| Test | What it pins |
| --- | --- |
| `MfaReset_ActsOnTheUserWhoseMethodsAreListed_NotTheEditableBox` | `userPrincipalName` appears nowhere in `ExecuteReset` |
| `MfaReset_RefusesWhenNothingIsListed` | the refusal returns before any await |
| `MfaReset_NamesTheListedUserOnScreen_NotTheBox` | both headings |
| `CountryPicker_ResyncsWhenItsBoundValueChanges` | the latch is gone and the gate is a value comparison |
| `CountryPicker_DoesNotTreatItsOwnOutputAsARecordChange` | both emitters record before they emit |

| Mutation | Result |
| --- | --- |
| `ExecuteReset` reads `userPrincipalName.Trim()` again | FAIL - `MfaReset_ActsOnTheUser...` |
| `Apply` emits without recording | FAIL - `CountryPicker_DoesNotTreatItsOwnOutput...` |

Both files restored byte-identical (md5 verified), touched, rebuilt.

Gate: Release 0 errors; suite 4348 passed / 3 known AD skips / 0 failed; format and
`git diff --check` clean. `MfaReset` 1.2.3 -> 1.2.4, `NamedLocations` 1.3.1 -> 1.3.2 (it hosts
the picker).

## Known gaps

- **A14 (`ADAttributeEditor`) and A15 (`LicensingUpdates`) are NOT fixed.** They belong to this
  group and to this discipline; they are simply not done. A15 is the more serious of the two -
  Cancel clears state while Apply continues, and the required notification is suppressed
  *after* the backend mutation.
- No behavioural coverage for either fix. The country picker in particular would be well
  served by a component harness, since its bug is entirely about parameter lifecycle.
- A13 refuses when nothing is listed, but does not stop an operator editing the box after
  listing - it just ignores what they typed. The heading tells them which account is in
  scope; nothing prevents the confusion, it only prevents acting on it.

## Closeout

Fixed directly on `master`. A14 and A15 outstanding. Independent review pending.
