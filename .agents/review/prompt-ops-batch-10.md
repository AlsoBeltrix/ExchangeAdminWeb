# Defect review request: unreadable migration rows listed, not dropped; Migration and Recipient Lookup times zoned and sorted as instants (aud-a23f, aud-a23e)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `038468fc` or later.
- The change is the branch merged at `90d57011`: `4ce34dd0` (plans), `73ba215d` (aud-a23f), `022014aa`
  (aud-a23e), forked from `9ef0a225`. Review `git diff 9ef0a225 022014aa`, and read the COMBINED
  `Models/ReportedCount.cs` (new), `Models/ReportedTime.cs` (`Order`, shared helpers), `Models/MigrationModels.cs`
  (`MigrationBatchList`, `UnreadableMigrationBatch`, `ReportedCount` fields, `AutoStart` as `bool?`,
  `CreatedDateTime` nullable), `Services/MigrationService.cs` (`GetMigrationBatchesAsync`),
  `Components/Pages/Migration.razor` (the warning, empty state, count cells, sorts, CSV),
  `Components/Pages/RecipientLookup.razor` (browser zone read, `ShowTime`) on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a23f.md`, `aud-a23e.md`; parents `aud-a23d.md`, `aud-a7i.md`,
  `aud-a7f.md`, `aud-a7e.md`. ALREADY admitted and out of scope: `aud-a23g` (a failed `Get-MigrationBatch`
  call shows as "No migration batches found"; a bad mailbox count fails the whole mailbox load; the
  report's "Taken" time and `ScheduledCompletionUtc` are still unzoned). Do not re-raise them. Repo
  rules: `AGENTS.md`; `.agents/repo-guidance.md` "Known Failure Classes" (1, 2) and the ClickGateRegistry
  rule; `docs/ProjectConstitution.md`.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 90d57011`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. a23f: `ReportedCount.Read` (an Int64 beyond Int32; negative; "12.0"
read as 12 - is a fraction-looking text a count); `AutoStart` null (any consumer treating null as
false); a row with no Identity listed as unreadable (can an action still be aimed at it; the CSV row
shape); the warning text and the empty-state text; the sorts via `Order`; the per-row catch now
listing (any exception type that should still abort the whole read - cancellation); the audit's
target count including unreadable rows. a23e: Migration in UTC labelled (the CSV format change -
anything in the repo parsing exported CSVs; the audit or notification that quotes a date); Recipient
Lookup's browser zone (the `OnAfterRenderAsync` exempt entry; prerender; the UTC fallback);
`ReportedTime.Order` (Kind-mixed comparison via `ToUniversalTime` on `Local` - on a server whose zone
differs from the values' origin; `Unspecified` grouped after instants; stability); `CreatedDateTime`
nullable - every consumer. `Migration.razor` at 4377 lines - confirm no keyed `ClickGateRegistry` line
moved (compare keyed lines with the hunks). Tests passing for the wrong reason: the stub runspaces;
`PSObject.AsPSObject` versus `new PSObject(x)`; render-tree assertions.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
