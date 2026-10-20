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

## A14 and A15 built, 2026-10-19 - the group is now complete

### A15 - Licensing Updates

`Components/Pages/LicensingUpdates.razor`. Cancel calls `ResetForm`, which nulls
`previewResult` and clears `ticketNumber` - and the button was **not disabled while the apply
ran**. A click during the write made `previewResult.LicenseType` throw, and where it threw is
the whole finding: **inside the notification's own `try`**, whose `catch` logged "email
notification failed" and carried on.

So the licences were changed, and the admin notification the Constitution requires was
dropped with a log line. `ticketNumber` would have been empty by then too, and `applyResult`
could be nulled between its assignment and its use.

Fixed on both sides. The preview and the ticket are captured before the first await and used
throughout, including in the notification; the apply result is held in a local and only
mirrored to the field. And Cancel is now `disabled="@isProcessing"`, so the operator is not
offered a button that abandons a running write.

### A14 - AD Attribute Editor

`Components/Pages/ADAttributeEditor.razor`. `ConfirmSave` already captured a display label
before its await - the comment there says why - but **not the principal**. The write itself
used `lookupResult!.Principal!`, and so did the success message and both notifications, all
after awaits. A search completing in that window nulls `lookupResult` and replaces it with a
different user, so the save could be aimed at B while the operator was reviewing A.

The principal is captured alongside the label now, and refuses if there is none. The applied
values are written back into the form only when `ReferenceEquals(lookupResult?.Principal,
target)` still holds - populating B's form with A's new values is the half of this an
operator would act on without noticing.

### Guard proof for both

| Test | What it pins |
| --- | --- |
| `LicensingApply_UsesWhatItCapturedBeforeTheWrite` | capture precedes the first await, and nothing after it re-reads the three mutable fields |
| `LicensingCancel_IsNotClickableDuringAnApply` | the disabled attribute |
| `AttributeSave_WritesToThePrincipalItCaptured_NotTheCurrentLookup` | same, plus the one permitted later mention is the identity re-check |

The "nothing after" assertions slice from the **first await**, not from the capture. Slicing
from the capture failed immediately, because the capture line itself necessarily reads the
field - the question is only whether anything does so once the handler can be interrupted.

| Mutation | Result |
| --- | --- |
| notification reads `previewResult.LicenseType` again | FAIL - `LicensingApply_...` |
| save passes `lookupResult!.Principal!` again | FAIL - `AttributeSave_...` |

Both files restored byte-identical (md5 verified), touched, rebuilt.

Gate: suite 4352 passed / 3 known AD skips / 0 failed; format and `git diff --check` clean.
`LicensingUpdates` 1.1.3 -> 1.1.4, `ADAttributeEditor` 1.4.3 -> 1.4.4.

### Still not covered

Behavioural coverage for all four. These remain source scans, and for A15 in particular the
interesting property - that the notification still goes out when Cancel is clicked mid-apply -
is exactly what a scan cannot show.

## Independent review, 2026-10-19 (codex-commercial / gpt-6-astra / max) - CHANGES REQUIRED

The licensing notification fix was accepted as sound against Cancel, and the ordinary MFA
wrong-user path as fixed. Four findings against A14 and A16, all repaired.

### A16 - value equality is not record identity - FIXED

The re-sync gate compared the bound *string*. Two records holding the same countries are
indistinguishable to that, and `ToggleCode` changes only the internal set without touching
`Value` - so with A and B both holding `US`, ticking `CA` on A without pressing Apply and then
opening B skipped the sync entirely and Apply emitted A's unfinished `CA, US` onto B.

**That is half the original defect still live, and a value comparison cannot close it.** The
parent now keys the picker on the record being edited, so Blazor builds a fresh component per
record. That is record identity rather than a guess at it.

### A14 - `ReferenceEquals` was a regression I introduced - FIXED

Every successful lookup constructs a **new** principal instance, and search stays enabled
during a save because its controls check `isLoading`, not `isSaving`. So re-searching the
*same* account during a save made the write-back check false: the save succeeded and the page
went on showing the old values while reporting success.

Now compared by `ObjectGuid`. The question is "is the panel showing this account", and only
the immutable id answers it.

### A14 - the proposed changes were still mutable across the await - FIXED

Capturing the principal was not enough. A concurrent search clears `pendingChanges`, and
`proposedValues` was built from that live collection *after* the yield - so a search landing
in the window turned a real save into a successful "updated 0 attributes", and emptied the
list the notification names. **The principal fix turned what had been a null-lookup failure
into a silent no-op**, which is worse. The changes are captured alongside the principal now.

### A14 - the exception notification still named the live lookup - FIXED

The outer catch used `lookupResult?.Principal?.UserPrincipalName ?? searchIdentity`, so a save
on A that threw while a search displayed B emailed a failure naming B. It uses the captured
target. The reviewer also noted the source guard missed this because it prohibited two exact
spellings and not the nullable form - a fair hit on the guard, not just the code.

### Repair guards

| Test | What it pins |
| --- | --- |
| `CountryPicker_IsKeyedToTheRecordBeingEdited` | the parent key |
| `AttributeSave_ComparesTheShownAccountByIdNotByReference` | no `ReferenceEquals`, GUID compared |
| `AttributeSave_CapturesTheChangesItIsApplying` | capture before the await, no live reads after |
| `AttributeSave_NamesTheCapturedTargetEvenWhenItThrows` | the nullable spelling is refused too |

`ClickGateRegistry` re-anchored for `NamedLocations` via the playbook's diff map: 599 -> 604
lines, six entries moved, one unmapped because it is the line that was edited.

Gate: suite 4362 passed / 3 known AD skips / 0 failed. `NamedLocations` 1.3.3,
`ADAttributeEditor` 1.5.1, `MessageTrace` 1.5.9.
