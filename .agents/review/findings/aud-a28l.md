# aud-a28l: The event log's undo authorization refusal writes no record, and the public SaveAllowlist has no caller, no version check and no record

**Severity**: MEDIUM - (1) `AdminEventLog.razor` `ExecuteUndo` refuses "Authorization denied." and
writes no audit record: an attempt on the authorization-adjacent undo path that leaves no trace (the
aud-a28g class; aud-a28d audited the undo SERVICE's refusals, not the page's). (2) After aud-a28i,
`ADAttributeEditorService.SaveAllowlist` has no production caller; it still writes the allowlist
with no version check and no audit record, so any future caller bypasses both guards.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
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

## Guard proof

Pending.
