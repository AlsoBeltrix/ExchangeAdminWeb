# aud-a28o: The event log's undo can be re-entered or re-targeted mid-run, its service's BeginOperation sits outside the try, and an unconfirmed save records no pre-write value

**Severity**: LOW. (1) `ShowUndoPreview` is not refused while an undo runs; the table's Undo buttons
behind the backdrop are reachable by keyboard, so a second preview mid-run replaces the panel and
shows the first undo's outcome on it. (2) `ExecuteUndo` has no re-entry guard beyond the disabled
Confirm button (the ClickGate class; the page's conversion is tier 4, not approved): a double-click
before the re-render can start two undos. (3) `ADAttributeEditorUndoService.ExecuteUndoAsync` calls
`BeginOperation` before the try and outside its catch; not known to throw, but if it did there would
be no record. (4) An unconfirmed attribute save's record (aud-a26j) has no pre-write value and its
trace step lists no changes, so a reviewer cannot see what the editor tried to set.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28m/a26j agent, 2026-10-22, candidates 1 to 4 of its hand-back)

## Evidence

At `6008a986`: `Components/Pages/AdminEventLog.razor` `ShowUndoPreview`, `ExecuteUndo` (no
`undoExecuting` re-entry check at the top), `Services/ADAttributeEditorUndoService.cs`
`ExecuteUndoAsync` (`BeginOperation` placement), `Services/ADAttributeEditorService.cs` `ConfirmWrite`'s
failed record (`LogAudit(target, [], ...)`).

## Approach (to be written by the implementing agent before code)

- `ShowUndoPreview` and `ExecuteUndo` return at once while `undoExecuting` (re-entry guard at the top
  of the handler, not only the disabled button); this is a guard, not a ClickGate conversion - the
  page stays `NotYetConverted` and gains no `ActionsDisabled`.
- Move `BeginOperation` inside the try so a throw there is recorded by the existing catch.
- The unconfirmed save's record carries the attempted changes as `attempted_<name>` (old and
  proposed) in `extra`, not as old/new pairs (so `CanUndo` stays false), and the trace step lists them.
- Tests in the `EventLogUndoRefusalTests` pattern: a preview requested mid-run is refused; a second
  `ExecuteUndo` mid-run returns without a second record; a throwing `BeginOperation` leaves one failure
  record; the unconfirmed save's record carries the attempted values. Mutations at the call site:
  drop each guard; move `BeginOperation` back out; drop the attempted values.

## Guard proof

Pending.
