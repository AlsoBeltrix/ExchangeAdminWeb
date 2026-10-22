# aud-a28o: The event log's undo can be re-entered or re-targeted mid-run, its service's BeginOperation sits outside the try, and an unconfirmed save records no pre-write value

**Severity**: LOW. (1) `ShowUndoPreview` is not refused while an undo runs; the table's Undo buttons
behind the backdrop are reachable by keyboard, so a second preview mid-run replaces the panel and
shows the first undo's outcome on it. (2) `ExecuteUndo` has no re-entry guard beyond the disabled
Confirm button (the ClickGate class; the page's conversion is tier 4, not approved): a double-click
before the re-render can start two undos. (3) `ADAttributeEditorUndoService.ExecuteUndoAsync` calls
`BeginOperation` before the try and outside its catch; not known to throw, but if it did there would
be no record. (4) An unconfirmed attribute save's record (aud-a26j) has no pre-write value and its
trace step lists no changes, so a reviewer cannot see what the editor tried to set.
**Status**: Plan written; in progress
**Branch**: `worktree-agent-a6af37cca287ebc2c`
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the a28m/a26j hand-back (`a8ae8dd9`). One
commit `fix(event-log,ad-attribute-editor): ... (aud-a28o)`, third of three on branch
`worktree-agent-a6af37cca287ebc2c`. Behaviour: Admin Event Log (the two guards) and AD Attribute Editor (the
undo's trace inside its try; the unconfirmed record's attempted values) - both module versions due; the
supervisor owns the bumps. Evidence re-read at `afa9dbf6`: `Components/Pages/AdminEventLog.razor` ShowUndoPreview
910-948 replaces undoTargetEvent, undoPreview and undoTicket (912-917) with no undoExecuting check, and the
table's Undo button (279) stays keyboard-reachable behind the backdrop; ExecuteUndo 967-1086 checks the target,
the preview and the ticket (975) and raises undoExecuting only at 983, so two calls before the re-render both
pass; Confirm is disabled while undoExecuting (429). `Services/ADAttributeEditorUndoService.cs` ExecuteUndoAsync:
`using var scope = _operationTrace.BeginOperation(...)` (194-201) sits before the try (203), outside the catch
that writes the failure record (328-345). `Services/ADAttributeEditorService.cs` PerformSave records an
unconfirmed save as `LogAudit(target, [], ...)` (1466): no changes in the record nor in the AttributeWriteCompleted
trace step (1821).

1. **Page guards.** `if (undoExecuting) return;` first in ShowUndoPreview and first in ExecuteUndo. No record: a
   second click is not a second attempt, and the running undo records its own outcome. Not a ClickGate
   conversion - the page stays NotYetConverted ("tier 4, not approved") with no ActionsDisabled and no line-keyed
   entry, so nothing re-anchors. ProgressRegistry: both guards return before `Progress.Begin`, so the pinned
   calls (`handler.PreviewUndoAsync`, `handler.ExecuteUndoAsync`) stay inside their windows.
2. **BeginOperation inside the try.** `OperationTraceService.OperationScope? scope = null;` before the try, the
   operation begun as the try's first statement, the catch completing it when there is one (`scope?.Complete`),
   a finally disposing it (the using's disposal, same order). A throw there reaches the existing catch: one failed
   `ADAttributeEditor_Undo` record, the exception-path notice, a failed result. Seam: the call goes through
   `internal virtual BeginUndoOperation(...)` (the editor service's CreateDirectoryRunspace shape) so a test can
   make it throw; production begins exactly the operation it begins now.
3. **The unconfirmed save names what it tried.** LogAudit gains an optional `attempted`: for each change the
   record carries `attempted_<name>` = `{ old, proposed }` - a nested object, whose nulls the writer keeps (it
   drops top-level nulls only; checked on this SDK) - and the trace step `attemptedChanges` (name, old,
   proposed); `changedAttributes` and the `old_`/`new_` pairs stay empty, so the record is not undoable (Failed,
   and no changed attributes). PerformSave passes it for both unconfirmed outcomes (not confirmed; could not be
   confirmed).
4. Comments after the mutation round, in their own commit.

Tests: EventLogUndoRefusalTests (the aud-a28l/a28m pattern) - (a) a preview requested while an undo runs returns
at once, no preview is built, the panel keeps the event, preview and ticket the undo was confirmed for, and the
undo's outcome is shown and recorded once; (b) a second ExecuteUndo while one runs completes at once with no
record of its own; when the first ends there is one record. ADAttributeEditorUndoRefusalTests - (c) a
BeginOperation that throws: nothing escapes, one failed `ADAttributeEditor_Undo` record carrying the error and no
`refusedAt`, nothing written, no directory session, one failure notice. ADAttributeEditorSaveTests - (d) the
unconfirmed saves' records carry `attempted_title` (old Analyst, proposed Lead Analyst) and their trace steps list
it; a clear's proposed is null. Mutations at the call site: M4 ExecuteUndo's guard dropped - (b) fails; M5
BeginOperation back before the try - (c) fails; M6 the attempted values dropped - (d) fails; extra E1
ShowUndoPreview's guard dropped - (a) fails.

Files: `Components/Pages/AdminEventLog.razor`, `Services/ADAttributeEditorUndoService.cs`,
`Services/ADAttributeEditorService.cs`, `ExchangeAdminWeb.Tests/EventLogUndoRefusalTests.cs`,
`ExchangeAdminWeb.Tests/ADAttributeEditorUndoRefusalTests.cs`, `ExchangeAdminWeb.Tests/ADAttributeEditorSaveTests.cs`,
`ExchangeAdminWeb.Tests/AttributeEditorHarness.cs` (the seam's subclass, a trace reader), this record.

## Guard proof

Pending.
