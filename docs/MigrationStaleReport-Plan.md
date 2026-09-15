# Migration Stale Open Report Plan

Status: Draft - not approved. No implementation authorized.

Base app version at drafting: 2.21.0
Migration module version at drafting: 1.8.0
Repository base: `6ccdf9d`

Owner-reported issue 3 of the 2026-09-15 queue, recorded in `.agents/state.md`.

Review: `codereview codex (@azure-openai-eus2-global/gpt-5.5-dzs @ xhigh, standard)
over 6ccdf9d..d48f750`: three candidate findings, all verified against the code at
the pinned head and all admitted. Recorded under Review findings below; the plan
body is NOT yet revised to absorb them, pending the owner's remedy ruling.
Harness codex-cli 0.154.0 (cache recorded 0.153.2; cached flags still valid).
Capability proof: reviewer read `Migration.razor:771` and ran `git log --oneline -3`,
both succeeded.

## Goal

The Mailbox Migrations status page must never display a migration report that was
not just fetched. Today it can show a previous migration's report for a user whose
batch was deleted and recreated under the same name, and it can show an arbitrarily
old snapshot for a migration that is still running.

## Reported symptom

With a migration report open on the module's status page, deleting and recreating
the batch under the same name from elsewhere (another session, or PowerShell) and
then refreshing the page leaves the operator looking at the previous migration's
report. Closing the report and reopening it shows the correct one.

## Diagnosis

Established by reading `Components/Pages/Migration.razor` at `6ccdf9d`. This
supersedes the working hypothesis recorded in `.agents/state.md`, which guessed
that the open report was cached by batch name.

The open report is three page fields declared at `Migration.razor:870-872`:

```csharp
private string? loadingReport;   // email whose report is being fetched
private string? reportUser;      // email whose report is currently shown
private string? userReport;      // the fetched report TEXT
```

`userReport` holds a one-time snapshot string. The only writer is `LoadUserReport`
(`:1760-1787`), which calls `MigrationSvc.GetMigrationUserReportAsync(email)` once
and stores the rendered text. The only clears are the explicit Close button
(`:778`) and the same-email toggle at the top of `LoadUserReport` (`:1762-1766`).

Nothing else invalidates either field:

- `LoadMigrationStatus` (`:1179-1202`) resets `expandedBatch = null` and
  `batchUsers = null` but leaves `reportUser` and `userReport` untouched.
- `RefreshBatchUsers` (`:1330-1346`) re-fetches the user list and touches neither.
- `ToggleBatchDetails` (`:1301-1328`) collapses and expands batches and touches
  neither.
- The two search paths (`:1705-1748`) set `expandedBatch` and reload `batchUsers`
  and touch neither.

The render guard is `reportUser == user.EmailAddress && userReport != null`
(`:771`), keyed on the email address alone. So after a refresh the panel is hidden
only because the batch collapsed; when the operator re-expands a batch containing
the same address, the guard matches and the panel re-renders the previously
fetched text with no fetch. Closing and reopening works because Close nulls both
fields and reopening calls the fetch, which is exactly the reported workaround.

Two separable defects follow:

1. **No re-fetch.** The report text is never refreshed while the panel is open.
   This is not limited to the recreate case: for a live migration the open report
   goes stale as the migration progresses, and the operator has no way to tell.
2. **Identity too weak.** The panel's gate is an email address, which survives
   batch deletion and recreation.

Note for the record: keying the report on batch NAME would not fix the reported
symptom, because the repro reuses the same name. `MigrationUserInfo`
(`Models/MigrationModels.cs:73-81`) carries no batch identity and no migration
GUID, so no stronger identity is available to the page without a service change.

## Owner decision required

One decision, and implementation should not start before it is ruled.

**Which remedy?**

- **Option A - clear the open report whenever the user list is reloaded.**
  `reportUser` and `userReport` are nulled in `LoadMigrationStatus`,
  `RefreshBatchUsers`, `ToggleBatchDetails` and the two search paths, alongside
  the existing `batchUsers = null`. After any refresh the operator sees the
  Report button again and clicks once to get a freshly fetched report.
  Cost: no extra Exchange calls. Consequence: an open report closes on refresh,
  which is one extra click.
- **Option B - re-fetch the open report whenever the user list is reloaded.**
  Same call sites, but each re-runs `GetMigrationUserReportAsync` for the open
  address if that address is still present in the reloaded list, and closes the
  panel if it is not. Cost: one extra `Get-MigrationUserStatistics -IncludeReport`
  round trip per refresh while a report is open; move reports can be large.
  Consequence: the panel stays open and stays current.

**Recommendation: Option A.** It is the smaller change, it closes both defects
by the strongest available rule (never render text that was not just fetched),
and it adds no per-refresh Exchange load. Option B is strictly more work and more
cost for the convenience of the panel staying open; it can be layered later if
the owner finds the extra click worse than the extra call.

The steps below are written for Option A. If the owner rules Option B, this plan
is revised before implementation rather than stretched to cover it.

## Scope

In scope:

- `Components/Pages/Migration.razor` - clearing the two report fields at every
  site that reloads or discards `batchUsers`.
- `ExchangeAdminWeb.Tests/MigrationStatusPageTests.cs` - a source-level tripwire.
- `Modules/ModuleCatalog.cs` - Migration module version bump.

Out of scope:

- Any change to `Services/MigrationService.cs` or `GetMigrationUserReportAsync`.
  The service is correct; it fetches when asked. The defect is entirely in the
  page's retention of what it fetched.
- Auto-refresh of the batch list, polling, or any change to refresh cadence.
- Option B's re-fetch behavior, unless the owner rules for it.
- The base app version. This is a module-scoped behavior change, so under
  `docs/ProjectConstitution.md` Deployment And Versioning only the module version
  bumps.

## Steps

1. Extract the clear into one private method on the page, so the five call sites
   cannot drift apart:

   ```csharp
   // The open report is a snapshot fetched once. Any reload of batchUsers can change
   // which migration a row refers to - a batch deleted and recreated under the same
   // name is a different migration with the same key - so the snapshot must not
   // survive the reload. Never render report text that was not just fetched.
   private void CloseUserReport()
   {
       reportUser = null;
       userReport = null;
   }
   ```

2. Call `CloseUserReport()` from every site that reloads or discards `batchUsers`:
   `LoadMigrationStatus` (`:1183`), `ToggleBatchDetails` (both the collapse branch
   at `:1306` and the expand branch at `:1314`), `RefreshBatchUsers` (`:1332`),
   and the two search paths (`:1713`, `:1744`).

3. Have the existing Close button call `CloseUserReport()` rather than its inline
   lambda (`:778`), so one definition owns the clear.

4. Add a tripwire to `ExchangeAdminWeb.Tests/MigrationStatusPageTests.cs` in that
   file's established style: anchored to the specific method bodies, not the file
   as a whole. Assert that each of the five reload sites contains a
   `CloseUserReport()` call. Follow the file's own warning - a guard a broken page
   satisfies is worse than no guard - so anchor each assertion inside the method
   body it covers, using the same helper pattern the existing tests use.

5. Bump the Migration module `Version` in `Modules/ModuleCatalog.cs` from `1.8.0`
   to `1.8.1` with a one-line comment naming this plan, matching the existing
   comment convention on that field.

## Why there is no behavioural test

The repo has no bUnit harness, so no test can render `Migration.razor` or invoke
one of its handlers. `MigrationStatusPageTests.cs` states this explicitly and
exists for exactly this gap. The guard in step 4 is a source-level tripwire, not
behavioural coverage, and must be described as such in the commit message.

## Guard proof

Prove the tripwire bites before claiming completion: delete one
`CloseUserReport()` call, run `dotnet test ExchangeAdminWeb.slnx`, confirm the new
test fails and names the site, restore it, confirm the suite passes. Restoring by
file copy keeps the old mtime and MSBuild will skip the rebuild - touch the file
after restoring.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`

No PowerShell is touched, so PSScriptAnalyzer and Pester are not required by this
change.

## Manual acceptance checklist

Requires a dev deploy. Not satisfiable by automation.

1. Open Mailbox Migrations, go to Status, expand a batch, open a user's Report.
   Confirm the report renders.
2. With the report open, click Refresh batch users. Confirm the report panel
   closes and the Report button returns.
3. Reopen the report. Confirm the content is current.
4. Recreate the reported defect: with a report open, delete and recreate the batch
   under the same name from PowerShell, reload the status list, re-expand the
   batch, and confirm no report panel is showing for that user until it is
   explicitly reopened - and that reopening shows the new migration's report.
5. Confirm the Close button still closes the panel.

## Review findings

All three were independently verified against the code at `d48f750` before being
admitted. None is a defect in shipped code - no code has shipped - so each is a
required revision to this plan rather than a `.agents/review/` finding record.

**MSR-A - the plan's call-site list is not exhaustive.** `ExecuteUserAction`
reloads the user list on its success path at `Migration.razor:1495-1496`
(`if (result.Success && expandedBatch != null) batchUsers = await
MigrationSvc.GetMigrationBatchUsersAsync(expandedBatch);`) and the Steps section
omits it. Trigger: open a report, then run Complete / Approve / Pause / Resume /
Clear on that user. The list refreshes and the report panel keeps the pre-action
text. This is the reported defect class surviving in a first-party workflow, so
the step-2 list must grow to six sites.

**MSR-B - Option A does not invalidate an in-flight report fetch.**
`LoadUserReport` sets `loadingReport`, awaits `GetMigrationUserReportAsync`
(`:1769-1776`), then assigns `reportUser`/`userReport`. Nothing disables the
refresh controls while that fetch is outstanding: the batch-users refresh button
gates on `loadingBatchUsers != null` (`:620-622`), not on `loadingReport`. Trigger:
open a report, then refresh before the fetch returns. `CloseUserReport()` clears
the fields and the late continuation repopulates them, restoring the stale panel.
Report fetches use `Get-MigrationUserStatistics -IncludeReport`
(`Services/MigrationService.cs:872-875`) and can be slow, so the window is real.
Remedy: capture a monotonic report generation in `LoadUserReport` and assign only
if it is still current; `CloseUserReport()` increments it.

**MSR-C - the proposed tripwire can pass with a broken branch.** Step 4 anchors
per method body, but two of the sites have two independent stale-producing
branches each: `ToggleBatchDetails` (`:1304-1314`) and the search paths
(`:1709-1717`, `:1741-1747`). A single `CloseUserReport()` anywhere in the method
satisfies a body-anchored assertion while the other branch stays unguarded - the
exact failure `MigrationStatusPageTests.cs:18-21` warns about. Anchor each
assertion to its branch, not its method.

Reviewer judgments confirmed without change: the cited lines and quoted
declarations are accurate; the batch-name-keying claim is correct because the
repro reuses the name; the module-only version bump is correct.

## Pending decisions

- The remedy choice above (Option A or Option B). Nothing else is open.
  MSR-A and MSR-C apply to either option. MSR-B applies to either option and is
  slightly larger under Option B, which keeps a fetch outstanding on every
  refresh rather than only on operator action.
