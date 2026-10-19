# aud-a31: one operator reloading a batch deletes every operator's stored migration reports

**Severity**: HIGH - instance-wide data loss triggered by an ordinary, frequent action. Not a
race and not an edge case: it happens every time anyone expands a batch.
**Status**: Verified and FIXED - store side `7316dd9`, page side `6a26f21`; review pending
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

## Built, 2026-10-19

Two commits, store side first so it could land independently: `7316dd9` adds
`DeleteForBatch`, `6a26f21` scopes the page's seven call sites to it.

**The page edit changed no line counts, and that was deliberate.** Every call site went from
`InvalidateStoredReports();` to `InvalidateStoredReports(<batch>);` in place, and the body from
`DeleteAll()` to `DeleteForBatch(batchName ?? "")` in place. 4,382 lines before and after, so
the three `ClickGateRegistry` entries on this page needed no re-anchoring - confirmed by the
registry tests passing unchanged, not assumed. The re-anchor playbook this record said would be
needed was avoided rather than run.

The batch passed at each site, as built:

| Site | Argument |
| --- | --- |
| `:1849` | `open` |
| `:1917` | `requested` |
| `:3128` | `batchName` |
| `:3409` | `expandedBatch`, null-checked by the enclosing `if` |
| `:3863` | `matchedBatch.BatchName` |
| `:3894` | `searchResult.BatchId` |
| `:3982` | `expandedBatch` - the generation guard immediately above has just proved the open batch unchanged, which is what makes it the right one |

**The existing R24b guards had to change, and that is the part worth reading.** Nine
assertions pinned the literal `"InvalidateStoredReports();"`. They now pin
`"InvalidateStoredReports("`, so they still assert that every discard path invalidates - their
actual purpose - without pinning an argument list. The one asserting
`ReportStore.DeleteAll()` now requires `DeleteForBatch(` and explicitly refuses `DeleteAll(`.

## Guard proof

Store side, against a real temporary directory: `DeleteForBatch_LeavesEveryOtherBatchAlone`,
`..._MatchesTheBatchNameCaseInsensitively`, `..._RefusesABlankNameRatherThanSweepingEverything`
(two cases).

Page side, source scans - and that limit is real, because the page has no harness here:
`ThePageNeverDiscardsEveryOperatorsReports` and
`EveryInvalidationNamesTheBatchItIsInvalidating`. Both read `ProgressScan.CodeView`, because
the comments around this method name `DeleteAll` and would satisfy a raw-text scan.

| Mutation | Result |
| --- | --- |
| `DeleteForBatch` widened back to `SweepInternal(_ => true)` | FAIL - `DeleteForBatch_LeavesEveryOtherBatchAlone`, alone |
| page body back to `ReportStore.DeleteAll()` | FAIL - `ThePageNeverDiscardsEveryOperatorsReports` |
| a call site changed to `InvalidateStoredReports(null)` | FAIL - `EveryInvalidationNamesTheBatchItIsInvalidating` |

The third matters most: a null argument compiles, sweeps nothing, and would leave exactly the
stale reports R24b exists to remove - silently, with every other guard still green.

Both files restored byte-identical after mutation (md5 verified), touched, and rebuilt.

Gate: Release 0 errors; suite 4316 passed / 3 known AD skips / 0 failed; format and
`git diff --check` clean. Base 2.27.10 -> 2.27.11, Migration 1.23.0 -> 1.23.1.

## Known gaps

- The page guards are source scans. The store behaviour is real, but nothing here drives the
  seven call sites through a rendered component.
- Two operators on the same batch still clear each other's reports. That is intended, not
  overlooked.
- Nothing has run against a real instance with two concurrent operators, which is the exact
  scenario the finding describes.

## Closeout

Fixed on `master`, pushed. Independent review pending.
