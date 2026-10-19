# aud-a31: one operator reloading a batch deletes every operator's stored migration reports

**Severity**: HIGH - instance-wide data loss triggered by an ordinary, frequent action. Not a
race and not an edge case: it happens every time anyone expands a batch.
**Status**: Open - VERIFIED, fix designed, not built
**Branch**: -
**Commit**: -

## Evidence

Finding 31 of the 2026-10-16 audit, verified against the code.

`Components/Pages/Migration.razor:3953`, inside `InvalidateStoredReports()`:

```csharp
ReportStore.DeleteAll();
```

and `Services/MigrationReportStore.cs:211`:

```csharp
public void DeleteAll() => SweepInternal(_ => true);
```

`SweepInternal` enumerates `Directory.EnumerateFiles(DirectoryPath, "*.json")` and deletes
every file the predicate accepts (`:312`). **The store is a single shared directory on disk**,
keyed by a hash of batch name plus email address (`PathFor`, `:102`) - there is nothing in the
key, the path or the predicate that is per-operator, per-circuit or per-session.

So the deletion is instance-wide. The method's own comment calls the scope "every stored
report" and is accurate; what is wrong is calling it from a circuit-local event.

**It is not rare.** `InvalidateStoredReports()` runs on seven paths, including simply expanding
a batch (`:1849`, `:1917`). Any operator opening any batch wipes the reports every other
operator has fetched, on an app where both dev and prod share one instance's storage.

## Predicted observable failure

Two operators work migrations at once. The first fetches reports for a dozen mailboxes, ready
to export. The second expands any batch - theirs, or an unrelated one. The first operator's
export now produces nothing, with no error: `BuildExport` simply holds none of them and
returns null.

## Approach - DESIGNED, NOT BUILT

The invalidation is correct in intent and wrong in scope. R24b exists because a batch removed
and recreated under the same name kept serving the previous migration's report - that is about
**one batch**, so the sweep should be about one batch.

`MigrationReport` already carries `BatchName`, and `SweepInternal` already takes a predicate
over the deserialized report, so the store side is one method:

```csharp
public int DeleteForBatch(string batchName) =>
    SweepInternal(r => string.Equals(r.BatchName, batchName, StringComparison.OrdinalIgnoreCase));
```

Two operators on the *same* batch still affect each other, and that is correct: the rows that
batch's reports describe really were replaced.

**The page side is the careful part, and is why this is written down rather than rushed.**
`InvalidateStoredReports()` has seven call sites and the batch in scope differs at each:

| Site | Batch in scope |
| --- | --- |
| `:1849` | `open`, assigned immediately above |
| `:1917` | `requested`, assigned immediately above |
| `:3128` | the `batchName` parameter of `RefreshBatchUsers` |
| `:3409` | `expandedBatch`, already null-checked by the enclosing `if` |
| `:3863` | `matchedBatch.BatchName` |
| `:3894` | `searchResult.BatchId` |
| `:3982` | the batch whose users were just loaded - **confirm which local holds it before editing** |

Give the method a required `string batchName` parameter rather than defaulting to
`expandedBatch`. A default would silently delete nothing wherever `expandedBatch` is null,
which would reintroduce the stale-report bug R24b was written to prevent - the failure mode
would be invisible and the guard would still pass.

## Guard the fix needs

- A store-level test: two batches with reports, `DeleteForBatch` on one, assert the other's
  survive. Mutating it back to `DeleteAll()` must fail that test.
- A page-level check that no call site passes a null or empty batch name, since that would
  sweep nothing and re-create R24b silently.

## Before editing the page

`Components/Pages/Migration.razor` is ~4,000 lines and **`ClickGateRegistry` entries are keyed
by line number**. Adding a parameter and seven arguments changes line counts. Re-anchor with
`.agents/playbooks/clickgate-reanchor.md`; do not hand-compute an offset. Check
`ProgressRegistry` the same way.

## Closeout

Not started. Verified and designed only; it is the first slice of Wave 2 and should be a fresh
session's work, because the page edit plus re-anchoring is the bulk of it.
