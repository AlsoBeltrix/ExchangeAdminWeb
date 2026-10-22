# aud-a26m: A clear-only or empty-to-value attribute edit cannot be undone

**Severity**: MEDIUM - a successful directory mutation loses its recovery path. `AuditService` writes
`old_<attr>` / `new_<attr>` for each changed attribute, but `JsonlLogService` drops null-valued fields,
and `ADAttributeEditorUndoService.CanUndo` (around line 59) requires at least one `old_` key AND one
`new_` key. A save where every changed attribute has one side null - clearing `department`, or
setting a previously empty attribute - leaves a successful `ADAttributeEditor_Update` row with
`changedAttributes` but no Undo button, although the preview/execute code already treats a missing
side as null.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (attr-batch-7, `prompt-attr-batch-7.md`, 2026-10-22)

## Evidence

At `6008a986`: `Services/ADAttributeEditorUndoService.cs` `CanUndo`; `Services/JsonlLogService.cs`
(null fields omitted on write); `Services/AuditService.cs` `LogADAttributeEdit` (`old_`/`new_` pairs).

## Approach (to be written by the implementing agent before code)

- `CanUndo`: a successful `ADAttributeEditor_Update` with non-empty `changedAttributes` is undoable
  when, for each changed attribute, at least one of `old_<attr>` or `new_<attr>` is present; a
  missing side means null/empty, exactly as the preview and execute paths read it. A record with
  neither side for some attribute stays not undoable (nothing to restore from).
- Keep aud-a26c (`ReversedOperations` requires Success) and aud-a28d/a28m (refusals are Failed
  records) untouched - a Failed record never becomes undoable.
- Tests through the undo tests and `AttributeEditorHarness`: a clear-only edit is undoable and its
  undo restores the old value; a set-from-empty edit is undoable and its undo clears; a record with
  no side for an attribute is not undoable; a Failed record is not undoable. Mutation at the call
  site: require both keys again.

## Guard proof

Pending.
