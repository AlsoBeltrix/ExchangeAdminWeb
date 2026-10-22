# aud-a26d: A no-op save sends an administrator notice but writes no audit row

**Severity**: LOW - nothing is mutated, but administrators receive an `ADAttributeEditor_Update`
notice that no audit event can be reconciled to.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; in progress
**Branch**: `worktree-agent-a4f347c5f99ae060f`
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (attr-batch-1, `prompt-attr-batch-1.md`, 2026-10-22)

## Evidence

At `8586b047`, `Services/ADAttributeEditorService.cs`, `PerformSave`:

```
929            var changes = ComputeChanges(proposedValues, currentValues, allowlist);
930            if (changes.Count == 0)
931                return new(true, "No changes to apply.", changes);
```

That return is before `LogAudit`. Since aud-a26b, `SaveAsync` announces whatever it returns, so a
no-op save sends the administrators an update notice whose `Changed` row reads "(none - the
directory already held the requested values)" while the Admin Event Log has no row for it.

## Approach (to be written by the implementing agent before code)

Pick one and say why in the record: write a successful no-op audit row (empty
`changedAttributes`, `mutationAttempted: false`) so the notice reconciles, or stop announcing
no-op saves. The recommended choice is the audit row: the Constitution's rule is that every
attempt leaves a durable record, and the aud-a26b design is that every outcome is announced
once; both stay true. Tests through `AttributeEditorHarness`: a no-op save leaves exactly one
audit row and one notice, and the row and the notice name the same operation. Mutation: remove
the no-op audit call.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted by codex attr-batch-1. One commit
`fix(ad-attribute-editor): a save that finds nothing to change is audited, so its notice has a row
(aud-a26d)`, after aud-a28e on the same branch. Module behaviour: AD Attribute Editor (the
supervisor owns the version bump). Evidence re-read at `a64416fa`: PerformSave 929-931, as quoted.

**Choice: the audit row.** Every attempt leaves a durable record (Constitution, Auditing) and
SaveAsync announces every result it returns, once (aud-a26b); the row keeps both true. Not
announcing no-ops would bring back a special case in the dispatch aud-a26b made unconditional, and
would still leave the attempt itself unrecorded.

Scope: the `changes.Count == 0` branch writes one record through `AuditService.LogADAttributeEdit`
before it returns - `ADAttributeEditor_Update`, Success, empty `changedAttributes`, no old_/new_
pairs, `mutationAttempted: false` (aud-a28's field: nothing was sent to the directory) and a
`detail` saying no change was needed because the directory already held the requested values. It
carries the target GUID and the servicer note the success record carries. A small helper of its
own rather than LogAudit, so the operation trace does not log `AttributeWriteCompleted` for a write
that never happened. The returned result is unchanged. Undo: `CanUndo` needs an old_/new_ pair, so
the row offers no Undo button; an undo whose save finds nothing left to change gets the same two
rows a real undo gets (this one and the undo record).

Tests, in `ADAttributeEditorSaveTests`, through `AttributeEditorHarness`: a no-op save -> nothing
written, exactly one audit record and exactly one notice, the two naming the same operation (action,
target, ticket, success), the record saying no mutation was attempted; a real change made after it
still leaves the normal row (old_/new_ pairs, no `mutationAttempted`). Mutation: the no-op audit
call deleted.

## Guard proof

Pending.
