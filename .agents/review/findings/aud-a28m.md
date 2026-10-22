# aud-a28m: The event log's undo has two more unrecorded exits, a Cancel mid-undo makes the catch throw, and the repository's allowlist writer is still public

**Severity**: MEDIUM for the undo exits (the aud-a28g/a28l class on the authorization-adjacent undo
path), LOW for the rest. (1) `AdminEventLog.razor` `ExecuteUndo`'s "No undo handler available"
return and its catch (an exception thrown before `ExecuteUndoAsync`) write no record. (2) The undo
panel's Cancel/close buttons stay active during an undo; closing mid-run nulls `undoTargetEvent`,
and the catch's `LogError(..., undoTargetEvent.OperationId)` then throws out of the handler - the
operator sees a circuit error instead of the undo's outcome. (3) `AttributeEditorRepository.SaveAllowlist`
is still public with only test callers; the aud-a28l tripwire refuses a production caller, but the
surface stays.
**Status**: Plan written; in progress
**Branch**: `worktree-agent-a762d0851374626dc`
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the a28l/a26g/a26i hand-back
(`8f3aa99d`). One commit `fix(event-log): ... (aud-a28m)`, first of two on this branch. Behaviour: Admin
Event Log (the page); the AD Attribute Editor's repository changes visibility only. The supervisor owns the
version bumps. Evidence re-read at `762c774c`, `Components/Pages/AdminEventLog.razor`: ExecuteUndo 957-1054 -
the no-handler return 975-979 sets undoError and returns with no record; the catch 1043-1047 logs
`undoTargetEvent.OperationId`; after its awaits the handler reads undoTargetEvent (998, 1002), undoTicket (998,
1018) and undoPreview (1023-1024) again. The panel's three close paths - the backdrop 341, the X 345, Cancel
422 - call CloseUndoPanel 948-955, which nulls undoPreview and undoTargetEvent and clears undoTicket without
looking at undoExecuting. `Services/Storage/AttributeEditorRepository.cs` SaveAllowlist 56-71, public: no
production caller (the aud-a28l source tripwire); test callers AttributeEditorHarness 59,
ADAttributeEditorPatternTests 29, ADAttributeEditorServiceTests 40 and 479, AllowlistBannerTests 56 and 81.

1. **Both exits are recorded.** New page helper `LogUndoNotRun(target, userName, clientIp, ticket, refusedAt,
   error)`: `Audit.LogModuleAction(userName, clientIp, "AdminEventLog_Undo", "AdminEventLog", <the event's
   FullTarget, or "(none)">, false, ticket, error, extra)` with `originalOperationId` (the event's
   OperationId, null when blank), `mutationAttempted: false` and, when given, `refusedAt` - inside a catch that
   logs an audit failure, so it never throws. Filed under the event log's own catalog id because there may be
   no module to file it under; the category filter (58-76) gains `AdminEventLog` so the rows can be filtered
   to. `@inject AuditService Audit` (the ADAttributeEditor and AdminSettings precedent).
   - No handler: `LogUndoNotRun(..., "NoHandler", undoError)` directly above the return. So that it names the
     operator, the auth-state read and `userName` move above FindHandler (same values; FindHandler is pure).
   - The catch: `LogUndoNotRun(..., userName ?? "unknown", ..., refusedAt: null, undoError)` only while the
     module has not been called - a local `moduleCalled`, set on the line before `handler.ExecuteUndoAsync`.
     Exactly one record per attempt: ExecuteUndoAsync records every exit it reaches, a throw included
     (aud-a28d; its own catch), and an exception after it - the trailing LoadEvents - follows a success the
     module has already recorded. `mutationAttempted: false` because nothing was handed to the module; no
     `refusedAt`, because it is a failure, not a refusal. `error` is what the operator is shown.
   Failed, so neither marks the edit reversed (aud-a26c), and no module claims category AdminEventLog, so
   neither offers an Undo of its own. Not announced: the page announces nothing (aud-a28d's known gap).
2. **The handler acts on what it was confirmed for, and the panel stays open while it runs.** At the top of
   ExecuteUndo, before any await: `target = undoTargetEvent`, `preview = undoPreview`, `ticket =
   undoTicket.Trim()` and `clientIp = ClientInfo.IpAddress` into locals; the guard tests them, and every later
   read - FindHandler, LogUndoRefused, the fields copy, ExecuteUndoAsync, the reversed-set add, the catch's log
   line and record - uses them. The ticket is captured for the reason the event is: the box stays editable
   while the undo runs, and the old close cleared it. CloseUndoPanel returns at once while `undoExecuting`
   (the backdrop cannot be disabled, and a click the browser sent before the re-render still arrives); the X
   and Cancel buttons render `disabled="@undoExecuting"`. undoExecuting is lowered in ExecuteUndo's finally,
   so the panel closes again once the undo ends. Not a ClickGate conversion: the page stays NotYetConverted
   and gains no ActionsDisabled.
3. **SaveAllowlist is internal**: the tests' seed through InternalsVisibleTo, with no production caller. The
   tests stay on it as the internal seam; the aud-a28l tripwires are unchanged.
4. **Registries.** ClickGateRegistry: AdminEventLog.razor is NotYetConverted ("tier 4, not approved") with no
   line-keyed entry - confirmed at `762c774c`; nothing re-anchors. ProgressRegistry: ExecuteUndo's pin
   (`handler.ExecuteUndoAsync`) unchanged and no Complete added before it; CloseUndoPanel stays synchronous;
   LogUndoNotRun is not a handler.

Tests, through the aud-a28l EventLogUndoRefusalTests pattern (its authorization substitute can also hold a
policy's answer or fault it; the page gets the harness's `Audit`): (a) no handler (an empty UndoRegistry) -
one record: AdminEventLog_Undo, Failed, `refusedAt` NoHandler, `mutationAttempted` false, `error` the message
shown, the event's `originalOperationId` and target, the operator's user, ip and ticket; no directory
session, nothing written, no notice, the edit not reversed; (b) an exception before ExecuteUndoAsync - the
page re-check or the level check faulted - one failure record (no `refusedAt`, `mutationAttempted` false,
`error` the message shown) and nothing else; (c) Cancel raced in - the page re-check held, CloseUndoPanel
called, the check then faulted - the panel not closed, one failure record, the outcome shown, no exception
escapes, and Cancel closes the panel once the undo has ended; (d) the panel's fields cleared mid-run as the
old Cancel cleared them - no exception escapes and the record names the event and ticket the undo was
confirmed for; (e) the X and Cancel buttons carry `disabled="@undoExecuting"`; (f)
AllowlistConcurrentSaveTests: the repository's SaveAllowlist is internal, not public. Conditions asserted,
not wording.

Mutations at the call site (md5 before and after, the named failing test, restored, md5 matched, touched,
rebuilt, green): M1 the no-handler record dropped; M2 the catch's record dropped; M3 the catch's log line
reading `undoTargetEvent.OperationId` again, as at `762c774c`; extras E1 CloseUndoPanel's undoExecuting guard
dropped, E2 SaveAllowlist public again.

Files: `Components/Pages/AdminEventLog.razor`, `Services/Storage/AttributeEditorRepository.cs`,
`ExchangeAdminWeb.Tests/EventLogUndoRefusalTests.cs`, `ExchangeAdminWeb.Tests/AllowlistConcurrentSaveTests.cs`,
this record.

## Guard proof

Pending.
