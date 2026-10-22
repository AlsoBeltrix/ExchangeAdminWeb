# aud-a26o: Attribute editor - a preview race can target the wrong event, ComputeChanges is ordinal, a JSON-null side reads as the text "null", the preview path is unrouted and unseamed, and the pre-write re-read mishandles multi-valued attributes

**Severity**: LOW. (1) `ShowUndoPreview` is not guarded while a preview is still loading; two previews
requested back to back can finish out of order, leaving one event's preview on the panel against
another event as the target - `ExecuteUndo` would undo the target event while the in-session
"reversed" marker uses the other preview's operation id. (2) `ComputeChanges` compares ordinal, so an
edit that differs from the current value only in form ("TRUE" against "True") is written and recorded
as a change whose old and new values read the same. (3) `ADAttributeEditorUndoService.GetString`
returns the text "null" for a JSON-null value; a hand-made record with a JSON-null side would make
the undo write that string (latent: the writer drops nulls). (4) The preview's `ReReadCurrentValues`
builds its own runspace outside the test seam and stays unrouted, so it cannot be tested through the
harness and may ask a different DC than the save (aud-a26l routed the save chain). (5) `PerformSave`'s
pre-write re-read uses `.Value?.ToString()`, so a collection-valued attribute reads as its type name
while the read-back takes its element; v1 excludes multi-valued attributes but the allowlist does not
enforce that.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a26l/a26m/a28o agent, 2026-10-22, candidates 1, 2, 3, 5 and 6 of its hand-back)

## Evidence

At `fee8a58b`: `Components/Pages/AdminEventLog.razor` `ShowUndoPreview` (no loading guard; a
completed later preview overwrites an earlier one's target); `Services/ADAttributeEditorService.cs`
`ComputeChanges` (ordinal), `PerformSave` pre-write re-read (`.Value?.ToString()`); `Services/ADAttributeEditorUndoService.cs`
`GetString` ("null"), `ReReadCurrentValues` (own runspace, no `-Server`).

## Approach (to be written by the implementing agent before code)

- Preview: a loading guard (`undoPreviewLoading`), and the preview result applied only if the target
  event is still the one it was requested for (compare the captured event id).
- `ComputeChanges` compares with the aud-a26l syntax rules so a form-only difference is not a change
  (and the audit does not record a non-change).
- `GetString` returns null for a JSON-null; a record with a null side reads as "not set".
- The preview re-read goes through the editor service's runspace seam and the aud-a26l routing
  (`ServerFromDn` of the event's target DN), so it can be tested and asks the same domain as the save.
- The pre-write re-read reads a one-element collection as its element and refuses a multi-valued
  attribute with a message naming it (v1 scope), or the allowlist save refuses adding one - say which.
- Tests through `AttributeEditorHarness` and `EventLogUndoRefusalTests`; mutations at the call site for
  each part.

## Guard proof

Pending.
