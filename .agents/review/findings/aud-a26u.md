# aud-a26u: Attribute editor page - several-valued and binary values are shown as editable text, several binary values are refused under the single-valued wording, and a closed preview's progress bar lingers

**Severity**: LOW - the save is refused before any write in every case; the page misleads. (1)
`ADAttributeEditor.razor` renders a several-valued or binary attribute (named as such by the aud-a26r
lookup) in an editable text field; the operator can type into it and only learns at save time that the
editor writes text attributes only. (2) An attribute holding several binary values is refused with the
several-values wording rather than named as binary. (3) On the Admin Event Log page, a closed undo
preview's progress bar stays visible until its handler returns (cosmetic; aud-a26r cleared the loading
guard, not the bar).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28u/a26r agent, 2026-10-22, candidates 2, 3 and 4 of its hand-back)

## Evidence

At `172902c2`: `Components/Pages/ADAttributeEditor.razor` (the value fields; NotYetConverted for
`ClickGateRegistry`); `Services/ADAttributeEditorService.cs` `ReadDirectoryValue` and
`NotSingleTextRefusal` (the `byte[]` single case vs a collection of `byte[]`);
`Components/Pages/AdminEventLog.razor` `CloseUndoPanel` (the bar's visibility condition; NotYetConverted).

## Approach (to be written by the implementing agent before code)

- The page marks a several-valued or binary attribute read-only with the lookup's description in place of
  the input; the save path's refusal stays as the second line of defence.
- `NotSingleTextRefusal` names a collection whose elements are `byte[]` as binary.
- `CloseUndoPanel` hides the bar with the panel.
- Tests through `AttributeEditorHarness` and the page's reflection driver; mutations at the call site: the
  editable input again; the several-values wording for a binary collection; the bar left visible.

## Guard proof

Pending.
