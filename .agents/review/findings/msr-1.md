# msr-1: A report opened DURING a row reload survives that reload

**Severity**: MEDIUM - reintroduces the reported stale-report symptom through a narrower
door. The panel the fix was written to close can still be left open over rows that were
replaced underneath it, so the operator can read a report that pre-dates the reload.
**Status**: Open
**Branch**: - (direct-to-main when authorized)
**Commit**: -

## Evidence

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
Dispatched over `1250220..3e4f13d`, 2026-09-17T13:42Z, codex-cli 0.154.0.
Capability proof passed (quoted `Migration.razor:778` verbatim, ran `git log --oneline -3`
and echoed all three real subjects). Verdict: findings (1).

Confirmed against the source before intake, not accepted on the reviewer's word.

`Components/Pages/Migration.razor:737-739` - the per-user **Report** button is disabled
only by `@(loadingReport == e)`. The refresh button immediately beside it at `:750` is
disabled by `@(loadingBatchUsers != null)`, and every action button above it at `:706-727`
by `@(actionInProgress != null || ...)`. The Report button is gated by neither, so it stays
live while a row reload is in flight.

`RefreshBatchUsers` at `:1337-1344` is the clear case:

```
loadingBatchUsers = batchName;
CloseUserReport();              // bumps reportGeneration
try { batchUsers = await MigrationSvc.GetMigrationBatchUsersAsync(batchName); }
```

It never nulls `batchUsers`, so the old rows keep rendering for the whole duration of that
Exchange Online call, with the Report button enabled.

Triggering sequence:
1. Operator clicks the refresh arrow on a user row. `CloseUserReport()` bumps the
   generation to G, and the method awaits.
2. Still inside that await, the operator clicks **Report** on a row. `LoadUserReport`
   captures `generation = G` - the post-bump value - and awaits its own slower
   `Get-MigrationUserStatistics -IncludeReport`.
3. The reload returns and assigns a fresh `batchUsers`. It does **not** bump the
   generation again.
4. The report fetch returns, finds `generation == reportGeneration`, and writes
   `userReport`.

`ExecuteUserAction`'s success reload at `:1503-1506` has the same shape and the same hole,
though its window is narrower: the long part of that method is the action itself, which
runs before `CloseUserReport()`, and a report landing during that earlier stretch is
correctly discarded by the subsequent bump.

The other five call sites are not affected: they either null `batchUsers` or change
`expandedBatch`, so the rows carrying the Report button stop rendering during the await.

## Predicted observable failure

A report panel stays open across a row reload, describing the state the row held before
the reload rather than after it. The rendered header and body still agree on the email
address, so nothing looks wrong - which is precisely the failure mode of the original
defect. It self-heals only if the user vanishes from the reloaded list, because the panel's
guard at `:771` lives inside the `batchUsers` loop.

## Approach

Not yet authorized; no code written. The reviewer's suggestion is to await into a local,
call `CloseUserReport()`, then assign `batchUsers` - so any report opened during the
reload is dropped by the generation bump that follows it. The alternative is to disable
the Report button while `loadingBatchUsers != null`, matching the refresh button beside it.
The first is the stronger guarantee; the second is one attribute. Choosing between them is
a plan question, not an intake one.

## Files changed

None yet. Expected: `Components/Pages/Migration.razor`,
`ExchangeAdminWeb.Tests/MigrationStatusPageTests.cs`, `Modules/ModuleCatalog.cs`,
`docs/MigrationStaleReport-Plan.md`.

## Guard proof

Not yet run. Note that the existing ten tripwires cannot catch this: they assert that
`CloseUserReport()` precedes the refetch, which is exactly what the defective code already
does. A tripwire for this finding has to assert ordering *after* the await, not before it.

## Coder dispute

None. The mechanism was checked line by line and holds.

## Known gaps

The window requires the operator to click Report during a reload that is already running.
How likely that is in practice is unmeasured; the manual acceptance checklist on
`docs/MigrationStaleReport-Plan.md` is owner-deferred, so no observation exists either way.
That uncertainty is why this is MEDIUM and not HIGH.

## Reviewer comments

Round 1 (change review, 2026-09-17): findings (1), this one. No other defect reported.
The reviewer was asked directly about an eighth reload path, the generation guard on all
three continuation paths of `LoadUserReport`, inconsistent field combinations, the
tripwires' weakness, and the module-only version bump. It returned nothing on the last
four - the generation guard, the four-field consistency, and the 1.8.1-without-base-bump
call all stood.

## Closeout

Pending. Needs an owner go before any code.
