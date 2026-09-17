# msr-1: A report opened DURING a row reload survives that reload

**Severity**: MEDIUM - reintroduces the reported stale-report symptom through a narrower
door. The panel the fix was written to close can still be left open over rows that were
replaced underneath it, so the operator can read a report that pre-dates the reload.
**Status**: Fixed and guard-proved 2026-09-17
**Branch**: - (direct to master)
**Commit**: 2b93fdc

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

The owner's go was "fix it", with no option named, so the choice was the coder's.

Taken: the structural form of the reviewer's suggestion. Rather than reordering the two
offending call sites, one private helper now owns every replacement of the rendered rows
and closes the report itself:

```
private void ReplaceBatchUsers(List<MigrationUserInfo> users)
{
    CloseUserReport();
    batchUsers = users;
}
```

All seven assignment sites hand it the awaited fetch directly -
`ReplaceBatchUsers(await MigrationSvc.GetMigrationBatchUsersAsync(batchName));` - which is
what puts the close on the same side of the await as the new rows. The five discards
(`batchUsers = null;`) are untouched: those paths stop rendering the rows, and the existing
discard guard already covers them. The pre-await `CloseUserReport()` calls stay as well, so
the panel still clears the moment the operator asks for a reload.

Rejected: gating the **Report** button on `loadingBatchUsers != null`, matching the refresh
button beside it. One attribute, but it blocks a read-only diagnostic the operator may
legitimately want mid-reload, and it leaves the invariant unenforced - the next reload path
added to the page would still be free to get it wrong.

## Files changed

- `Components/Pages/Migration.razor` - the helper, and seven assignment sites routed
  through it.
- `ExchangeAdminWeb.Tests/MigrationStatusPageTests.cs` - three tripwires.
- `Modules/ModuleCatalog.cs` - Migration module 1.8.1 -> 1.8.2. No base app bump; the
  change is module-scoped.
- `docs/MigrationStaleReport-Plan.md` - an `msr-1 fixed` subsection and a shortened
  Outstanding.

## Guard proof

The intake note said a tripwire for this finding has to assert ordering *after* the await,
not before it. With the helper in place that ordering is structural, so the load-bearing
assertion is a negative one instead, anchored per occurrence:
`NoCodePathAssignsBatchUsersWithoutClosingTheReport` walks every `batchUsers =` in the page
and requires each to be either `null;` or the single `users;` inside the helper. An eighth
reload path cannot slip past it. `TheRowReplacementHelperClosesTheReportBeforeAssigning` and
`EveryRowRefetchIsHandedStraightToTheHelper` pin the helper's internals and the call shape.

Three mutations, each applied, run, and restored byte-identically
(`--filter FullyQualifiedName~MigrationStatusPageTests`, 52 tests):

| mutation | result |
| --- | --- |
| M1 `RefreshBatchUsers` assigns `batchUsers` directly again | 2 failed, 50 passed |
| M2 the helper assigns before closing | 1 failed, 51 passed |
| M3 the post-action reload assigns directly again | 2 failed, 50 passed |

M1 is exactly the pre-fix code. The ten tripwires that shipped with `3e4f13d` all passed it;
only the three new ones failed. That is the finding itself, restated as evidence.

Full verification at the fix commit: `dotnet build ExchangeAdminWeb.slnx -c Release` clean,
`dotnet test ExchangeAdminWeb.slnx` 2497 passed / 3 skipped / 0 failed,
`dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` clean,
`git diff --check HEAD` clean.

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

Closed 2026-09-17 on the coder-side guard proof, per the repo rule that only CRITICAL
findings take a reviewer verification round. Landed in `2b93fdc`, direct to `master`, not
pushed.

The residual risk named under Known gaps is unchanged and is now moot for this mechanism:
the window still exists in the sense that the operator can click **Report** during a
reload, but the report that opens is discarded rather than rendered over stale rows.
