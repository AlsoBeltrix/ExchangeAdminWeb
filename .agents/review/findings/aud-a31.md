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

## Independent review, 2026-10-19 (codex-commercial / gpt-6-astra / max) - CHANGES REQUIRED

Three product defects and a weakened guard. All four correct, all four repaired.

### 1. Removing the only selected batch invalidated nothing - FIXED

A successful bulk action deselects every accepted batch and then reloads. If the accepted
batch was the **only** selected one, `AdoptSelectionAsOpenBatch` set `expandedBatch` to null,
which the helper turned into `""`, which the store refuses - so nothing was swept and another
circuit could still reopen the previous migration's report.

**A regression this fix introduced.** The old blanket `DeleteAll()` happened to cover it.

Repaired at the source of truth rather than downstream: the accepted batch names are known in
the bulk-action handler, so each is discarded there, by name, immediately after it is
deselected.

### 2. ExecuteUserAction could invalidate a different batch than it acted on - FIXED

`expandedBatch` was read for invalidation **after** authorization, the action and the
notification had all awaited. A URL navigation or selection change in that window moves it, so
an action on A could finish by discarding B's reports and leaving A's stale.

**This is the aud-a7 class again, inside the fix for a different finding.** The null check
proved only that some batch was open, not that it was still this one. The batch is now
captured before any await and used for both the invalidation and the reload.

### 3. DeleteForBatch did not normalise the way the file key does - FIXED

`FileNameFor` trims and lowercases, so a report written under `" Wave1 "` is readable as
`"Wave1"` - but `Write` stores the original string, and the comparison was untrimmed, so
`DeleteForBatch("Wave1")` left it behind and `TryRead` kept serving it until expiry. R24b
through the back door. Both sides are trimmed now.

### 4. The revised R24b guards accepted a declaration where a call was required - FIXED

The reviewer demonstrated the bypass rather than asserting it: replacing the call in
`ReplaceBatchUsers` with an unused local function

```csharp
void InvalidateStoredReports(string? ignored) { }
```

satisfied the relaxed prefix scan, and the argument scan then skipped it as "the declaration".
The original `InvalidateStoredReports();` predicate would have rejected it. **So in loosening
the guards to accept an argument I loosened them to accept a page with the call deleted.**

A declaration is now identified by a return type immediately before the name, its parameter
list is pinned exactly, and there must be exactly one of them and at least seven call sites.

Note the original `MigrationStatusPageTests` guards are still satisfied by that local function;
the new guard is what rejects it. Those were not re-tightened - recorded rather than hidden.

## Repair guard proof

| Mutation | Result |
| --- | --- |
| drop the `Trim()` from `DeleteForBatch` | FAIL - `DeleteForBatch_MatchesAReportWrittenUnderAPaddedName` |
| delete the per-accepted-batch discard loop | FAIL - `EveryAcceptedBatchInABulkActionHasItsReportsDiscarded` |
| replace the `ReplaceBatchUsers` call with the reviewer's local function | FAIL - `EveryInvalidationNamesTheBatchItIsInvalidating` |

Three mutations, one failing test each, in a single build. Both files restored byte-identical
(md5 verified), touched, rebuilt.

Two existing guards pinned `if (result.Success && expandedBatch != null)` and
`await LoadMailboxesFor(expandedBatch);` and were repointed at the captured local.

`ClickGateRegistry`'s `ExpectedLineCount` for this page went 4382 -> 4401. The four
line-anchored `ExemptControl` entries are all at or below line 1540 and the additions are
below 3368, so none moved - checked, not assumed.

Gate: suite 4318 passed / 3 known AD skips / 0 failed; format and `git diff --check` clean.
Migration 1.23.1 -> 1.23.2.
