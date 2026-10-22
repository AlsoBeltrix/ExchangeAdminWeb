# aud-a26m: A clear-only or empty-to-value attribute edit cannot be undone

**Severity**: MEDIUM - a successful directory mutation loses its recovery path. `AuditService` writes
`old_<attr>` / `new_<attr>` for each changed attribute, but `JsonlLogService` drops null-valued fields,
and `ADAttributeEditorUndoService.CanUndo` (around line 59) requires at least one `old_` key AND one
`new_` key. A save where every changed attribute has one side null - clearing `department`, or
setting a previously empty attribute - leaves a successful `ADAttributeEditor_Update` row with
`changedAttributes` but no Undo button, although the preview/execute code already treats a missing
side as null.
**Status**: Plan written; in progress
**Branch**: `worktree-agent-a6af37cca287ebc2c`
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from codex attr-batch-7 (`afa9dbf6`). One
commit `fix(ad-attribute-editor): ... (aud-a26m)`, second of three on branch `worktree-agent-a6af37cca287ebc2c`.
Behaviour: AD Attribute Editor (module version; the supervisor owns the bump). Evidence re-read at `afa9dbf6`:
`Services/ADAttributeEditorUndoService.cs` CanUndo 46-62 asks for any `old_` key AND any `new_` key in the
record (59-61); `Services/AuditService.cs` LogADAttributeEdit writes `old_<name>`/`new_<name>` per change
(354-358); `Services/JsonlLogService.cs` drops every null top-level field (68), so a cleared side (NewValue null)
or a side that was not set (OldValue null) is absent from the line; PreviewUndoAsync (119-120) and
ExecuteUndoAsync (255, 282) already read an absent side as null.

**Rule.** A successful `ADAttributeEditor_Update` naming changed attributes is undoable when every one of them
has at least one recorded side, `old_<attr>` or `new_<attr>`, present with a value; a key held null (a record
built by hand) counts as absent, as the JSONL writer drops it. The missing side is "not set", as the preview and
execute paths read it. An attribute with neither side has nothing to restore from or check against: not
undoable. Category, action and Success are checked first, unchanged, so a Failed record - the aud-a28c thrown
write with its pairs, the aud-a26j unconfirmed save, every refusal (aud-a28d, aud-a28m) - is never undoable, and
aud-a26c's rule that only a successful record marks an edit reversed is untouched. An empty `changedAttributes`
stays not undoable (aud-a26d's no-change record).

Tests (`ADAttributeEditorUndoRefusalTests`, through the harness; the records read back from the JSONL and turned
into the dictionary the page builds): (a) a clear-only edit (department cleared) - no `new_department`, CanUndo
true, ExecuteUndoAsync puts Finance back, confirmed, the undo record Success; (b) a set-from-empty edit
(department set on a user without one) - no `old_department`, CanUndo true, the undo clears it; (c) an attribute
with neither side, beside one with both - not undoable; the only side held null - not undoable; (d) a Failed
record carrying both sides (a refused write) - not undoable. Mutation at the call site: M3 both keys required
again - (a) and (b) fail.

Files: `Services/ADAttributeEditorUndoService.cs`, `ExchangeAdminWeb.Tests/ADAttributeEditorUndoRefusalTests.cs`,
this record.

## Guard proof

Pending.
