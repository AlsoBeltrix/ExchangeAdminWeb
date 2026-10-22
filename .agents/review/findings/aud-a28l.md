# aud-a28l: The event log's undo authorization refusal writes no record, and the public SaveAllowlist has no caller, no version check and no record

**Severity**: MEDIUM - (1) `AdminEventLog.razor` `ExecuteUndo` refuses "Authorization denied." and
writes no audit record: an attempt on the authorization-adjacent undo path that leaves no trace (the
aud-a28g class; aud-a28d audited the undo SERVICE's refusals, not the page's). (2) After aud-a28i,
`ADAttributeEditorService.SaveAllowlist` has no production caller; it still writes the allowlist
with no version check and no audit record, so any future caller bypasses both guards.
**Status**: Plan written; in progress
**Branch**: `worktree-agent-ad10b5ab24c8ce164`
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28g/h/a26f/a28i agent, 2026-10-22, candidates 6 and 10 of its hand-back)

## Evidence

At `f1b4692d`: `Components/Pages/AdminEventLog.razor` `ExecuteUndo`, the authorization refusal
return; `Services/ADAttributeEditorService.cs` `SaveAllowlist` (public; `git grep` shows tests as
its only callers).

## Approach (to be written by the implementing agent before code)

- Undo page refusal: record it through the aud-a28d undo-record shape (`refusedAt: Authorization`,
  `mutationAttempted: false`, `originalOperationId` set so aud-a26c's rule still does not mark the
  edit Reversed because the record is not a success) via a non-throwing helper on the undo service;
  exactly one record per attempt. `ClickGateRegistry`: check `AdminEventLog.razor`'s status; re-anchor
  per the playbook if it has line-keyed entries.
- `SaveAllowlist`: make it private (or remove it) so the only write path is `SaveAllowlistAndAudit`;
  move the tests that used it to the audited path; a tripwire test asserts no public allowlist write
  bypasses the version check.
- Tests: an unauthorised undo leaves exactly one refusal record and the edit keeps its Undo button;
  the reflection tripwire. Mutations at the call site: drop the undo refusal record; make
  `SaveAllowlist` public again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the a28g/h/a26f/a28i
hand-back (`374718b3`). One commit `fix(event-log,ad-attribute-editor): an undo the event log refuses
for authorization is audited, and the allowlist's unversioned public save is gone (aud-a28l)`, first
of three on this branch. Behaviour: Admin Event Log (the page) and AD Attribute Editor (its undo and
editor services); the supervisor owns the version bumps. Evidence re-read at `374718b3`:
`Components/Pages/AdminEventLog.razor` ExecuteUndo 957-1050 - the handler is found at 974, and the
re-check 987-993 sets `undoError = "Authorization denied."` and returns with no record;
`Services/ADAttributeEditorService.cs` SaveAllowlist 407-426 - public, called by no production code
(the Module Configuration save goes through SaveAllowlistAndAudit) and by five tests
(ADAttributeEditorServiceTests 451, 806, 834; ADAttributeEditorPatternTests 110, 124).

1. **The undo refusal is recorded.** `IUndoableModule` gains `LogUndoRefused(auditEvent,
   performedBy, ip, ticket, refusedAt, reason)`: an undo its caller refused before calling
   ExecuteUndoAsync, recorded as the module's own refused-undo record, never thrown. On the
   interface because the page holds the handler only as one (Program.cs 362 registers the undo
   service only as `IUndoableModule`), the record's shape is the module's, and a later undoable
   module must then write one rather than nothing. `ADAttributeEditorUndoService.LogUndoRefused` is
   the existing `LogUndoAudit` with `refusedAt` - aud-a28d's record: `ADAttributeEditor_Undo`,
   Failed, the reason as `error`, `originalOperationId` the event's `operationId`,
   `changedAttributes` and the reversed old/new pairs the event names, `refusedAt`,
   `mutationAttempted: false`, target `(none)` when the event names none - inside a catch that logs
   an audit failure. Page: the refusal calls `handler.LogUndoRefused(undoTargetEvent.RawFields,
   userName, clientIp, undoTicket.Trim(), "Authorization", undoError)` directly above its return;
   `userName` and `clientIp` are read before the re-check rather than after it (same values).
   Exactly one record per attempt: the refusal returns before ExecuteUndoAsync, which records
   everything it reaches. Failed, so aud-a26c's rule leaves the edit's Undo button. Not announced:
   the page never announced this refusal (aud-a28d's known gap stands).
2. **SaveAllowlist is removed**, not made private: a private method with no caller is dead code.
   SaveAllowlistAndAudit is the service's only allowlist writer; aud-a28e's uncompilable-pattern
   throw goes with SaveAllowlist, the audited PatternCheck refusal being the check that runs. The
   tests move to the audited path: ADAttributeEditorServiceTests' cache-invalidation and
   persist-and-read-back tests save through SaveAllowlistAndAudit with the version their load
   returned; its denylisted-row test plants the row through the repository - the audited save
   refuses one (AllowlistSaveAuditTests), so only an out-of-band write can store it - and still
   asserts GetAllowlist strips it; ADAttributeEditorPatternTests' two admin-side tests save through
   SaveAllowlistAndAudit, refused PatternCheck with the store unchanged, and the valid pattern
   stored. The pattern-wording comment that names SaveAllowlist's throw is corrected.
3. **Tripwire**, in AllowlistConcurrentSaveTests (the version check's home): (a) reflection - every
   non-private method of ADAttributeEditorService with a parameter that is a collection of
   `EditableAttribute` or `AttributeRow` is SaveAllowlistAndAudit, and it takes `loadedVersion`;
   (b) source - in `Services/`, `Components/`, `Modules/` and `Program.cs` the store's whole-table
   writers (`.SaveAllowlist(`, `.ReplaceAllowlist(`) are called only from SaveAllowlistAndAudit, so
   nothing bypasses it through the repository either (a DI singleton, Program.cs 80).
4. **Registries.** ClickGateRegistry: AdminEventLog.razor is NotYetConverted ("tier 4, not
   approved"), with no line-keyed entry and no ExpectedLineCount - confirmed at `374718b3`; nothing
   re-anchors. ProgressRegistry: ExecuteUndo's pin (`handler.ExecuteUndoAsync`) unchanged; no handler
   added.

Tests. New `ExchangeAdminWeb.Tests/EventLogUndoRefusalTests.cs` drives ExecuteUndo itself: the
compiled page, its injected services set by reflection, on a do-nothing renderer (it calls
StateHasChanged), the real undo service over AttributeEditorHarness behind a real UndoRegistry, a
substituted authorization service. Either policy denied (EventLog, UndoAuditedActions) -> exactly one
record, the refusal above, its `error` what the page showed, the operator's user, ip and ticket; no
directory session, nothing written, no notice; the edit not marked Reversed - not in the page's
in-session set, and not by the rule over the records the log now holds (ReversedOperations.From, as
LoadEvents applies it). `LogUndoRefused` with no audit writer logs and does not throw. The tripwire
above. Conditions asserted, not wording.

Mutations at the call site (md5 before and after, the named failing test, restored, md5 matched,
touched, rebuilt, green): M1 the page's `handler.LogUndoRefused(...)` deleted; M2 SaveAllowlist
restored, public, as at `374718b3`; extra E1 the helper's catch removed.

Files: `Services/IUndoableModule.cs`, `Services/ADAttributeEditorUndoService.cs`,
`Services/ADAttributeEditorService.cs`, `Components/Pages/AdminEventLog.razor`,
`ExchangeAdminWeb.Tests/EventLogUndoRefusalTests.cs` (new), `AllowlistConcurrentSaveTests.cs`,
`ADAttributeEditorServiceTests.cs`, `ADAttributeEditorPatternTests.cs`, this record.

## Guard proof

Pending.
