# aud-a26l: The attribute editor's read-back compares strictly, so a value the directory stores in its own form fails a legitimate save

**Severity**: MEDIUM - a functional regression introduced by aud-a26j: the read-back compares the
value read against the value written with strict ordinal equality, nothing trimmed. Active Directory
stores several syntaxes in its own form - a DN-valued attribute (`manager`, `assistant`) comes back
in the directory's canonical case and spacing, a Boolean comes back as `True`/`False`, an integer
without leading zeros, some strings with the directory's own trimming. For those attributes every
legitimate save now ends as a failed `ADAttributeEditor_Update` naming the value read and the value
written, offers no Undo, and announces a failure to administrators, although the write landed. Loud,
never a false success, but it blocks real edits until fixed.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started - blocks the deploy of the attribute editor until landed
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28m/a26j agent, 2026-10-22, candidate 5 of its hand-back, recorded by it as a known gap)

## Evidence

At `6008a986`, `Services/ADAttributeEditorService.cs` `ConfirmWrite` / `UnconfirmedWrites`: ordinal
comparison of `NewValue` against the value read; "" equals absent; a one-element collection reads as
its element. The allowlist's `EditableAttribute.Type` names the attribute's kind (text, choice,
boolean, DN-valued if present); the comparison does not consult it.

## Approach (to be written by the implementing agent before code)

- Compare by the attribute's syntax, not by bytes: DN-valued attributes compare DN-wise
  (case-insensitive, whitespace-normalised around RDN separators, as `GroupManagementService` and
  the aud-a2i membership read compare DNs); Booleans parse both sides; integers parse both sides;
  plain strings compare ordinal after the directory's documented trimming of leading/trailing
  whitespace (label the trimming an assumption and keep it minimal). Where the allowlist's `Type` does
  not say, fall back to: equal ordinal, or equal after case-insensitive comparison when the attribute
  is known DN-valued by name - never a loose "contains".
- When the read differs only by form, the save is CONFIRMED and the record carries the value READ as
  the new value (what the directory holds), with no failure. A genuinely different or missing value
  still fails as aud-a26j does.
- Tests through `AttributeEditorHarness` with `SetStoresAs`: a manager DN stored in canonical case
  confirms; a Boolean written `true` stored `True` confirms with `True` recorded; a value stored with
  different content still fails; the undo path round-trips the normalised value. Mutation at the
  call site: compare ordinal again (the DN and Boolean cases fail).
- No live AD; the directory's normalisation rules are assumptions labelled in the record, with a
  dev acceptance item: edit a manager and a Boolean attribute on a test user and confirm the save is
  recorded as a success with the directory's form.

## Guard proof

Pending.

## Folded in: the read-back can be answered by a different, stale DC (codex, attr-batch-7, 2026-10-22) - LOW

`Services/ADAttributeEditorService.cs` `ConfirmWrite` around line 1526: `Set-ADUser` is sent with no
`-Server`, and the immediate `Get-ADUser -Identity <objectGUID>` has none either, so in a multi-DC
domain a DC that has not replicated the write can answer and the correct save is reported Failed
with no Undo and a failure notice. Choose a writable DC once for the target object's domain
(`ServerFromDn` of the re-read DN, the aud-a2p/a2q routing; or the DC the pre-write re-read answered
from, read off `Get-ADUser`'s `-Server`-less result if the module exposes it) and pass that exact
`-Server` to the pre-write re-read, `Set-ADUser` and the read-back; label the DC-affinity assumption.
The not-confirmed message also says the value may not have replicated yet and names the server
asked. Test: a harness whose read-back server differs from the write server fails before the fix
and confirms after. Mutation at the call site: drop `-Server` from the read-back.
