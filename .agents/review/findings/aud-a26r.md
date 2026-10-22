# aud-a26r: Attribute editor - the lookup still reads values with ToString, a binary attribute gets a misleading refusal, and a closed loading preview blocks the next one

**Severity**: LOW. (1) The editor's lookup still reads attribute values with `ToString`, so a
collection-valued attribute shows its type name in the form and in the undo conflict check (aud-a26o
fixed the pre-write re-read and the preview, not the lookup). (2) A binary (byte-array) attribute is
refused as "holds N values", where N is the byte count: the outcome is safe, the message misleads.
(3) After a still-loading preview panel is closed, a new preview is refused until the first one
returns (the aud-a26o loading guard outlives the panel).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the psl-7/a28q/a26o agent, 2026-10-22, candidates 4, 6 and 7 of its hand-back)

## Evidence

At `5e2b80f3`: `Services/ADAttributeEditorService.cs` `PerformLookup` (value reads), the several-values
refusal in the pre-write re-read (`ReadDirectoryValue` on a `byte[]`), the undo conflict check's read;
`Components/Pages/AdminEventLog.razor` `ShowUndoPreview` / `CloseUndoPanel` (`undoLoading` not cleared by
a close).

## Approach (to be written by the implementing agent before code)

- The lookup and the undo conflict check read through `ReadDirectoryValue` (one-element collection as its
  element; several values and binary values named as such).
- A `byte[]` is "binary (N bytes); the attribute editor writes text attributes only", not "holds N values".
- Closing the panel while a preview loads marks the pending preview stale (aud-a26o already drops its
  result) and clears the loading guard so the next preview can start; the dropped result still never lands.
- Tests through `AttributeEditorHarness` and `EventLogUndoRefusalTests`; mutations at the call site:
  `ToString` again in the lookup; the "holds N values" wording for a `byte[]`; the guard left set on close.

## Guard proof

Pending.
