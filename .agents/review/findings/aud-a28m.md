# aud-a28m: The event log's undo has two more unrecorded exits, a Cancel mid-undo makes the catch throw, and the repository's allowlist writer is still public

**Severity**: MEDIUM for the undo exits (the aud-a28g/a28l class on the authorization-adjacent undo
path), LOW for the rest. (1) `AdminEventLog.razor` `ExecuteUndo`'s "No undo handler available"
return and its catch (an exception thrown before `ExecuteUndoAsync`) write no record. (2) The undo
panel's Cancel/close buttons stay active during an undo; closing mid-run nulls `undoTargetEvent`,
and the catch's `LogError(..., undoTargetEvent.OperationId)` then throws out of the handler - the
operator sees a circuit error instead of the undo's outcome. (3) `AttributeEditorRepository.SaveAllowlist`
is still public with only test callers; the aud-a28l tripwire refuses a production caller, but the
surface stays.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28l/a26g/a26i agent, 2026-10-22, candidates 1 to 4 of its hand-back)

## Evidence

At `f0cccea8`, `Components/Pages/AdminEventLog.razor` `ExecuteUndo`: the no-handler return; the
catch's log line reading `undoTargetEvent.OperationId`; the panel's Cancel/close handlers not gated
on an undo in progress. `Services/Storage/AttributeEditorRepository.cs` `SaveAllowlist` (public).

## Approach (to be written by the implementing agent before code)

- Record both exits: the no-handler return through the page's own refusal helper (no module to ask,
  so a generic settings/undo refusal record via `AuditService`, `refusedAt: NoHandler`,
  `mutationAttempted: false`, `originalOperationId` the event's); the catch through a failure record
  with the exception message. Exactly one record per attempt.
- Capture the target event into a local at the top of the handler and use the local everywhere
  (including the catch); disable Cancel/close while an undo runs. A Cancel during an undo can no
  longer null what the catch reads.
- Make the repository writer internal (or private to the audited path), moving tests to the audited
  save or to an internal seam; keep the tripwire.
- Tests through the aud-a28l `EventLogUndoRefusalTests` pattern: no handler leaves one refusal
  record; an exception before `ExecuteUndoAsync` leaves one failure record; a Cancel raced into the
  handler leaves the outcome recorded and no exception escapes; the repository writer is not public.
  `ClickGateRegistry`: the page is `NotYetConverted`; confirm. Mutations at the call site: drop the
  no-handler record; drop the catch record; read the field again in the catch.

## Guard proof

Pending.
