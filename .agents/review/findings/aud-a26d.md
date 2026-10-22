# aud-a26d: A no-op save sends an administrator notice but writes no audit row

**Severity**: LOW - nothing is mutated, but administrators receive an `ADAttributeEditor_Update`
notice that no audit event can be reconciled to.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
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

## Guard proof

Pending.
