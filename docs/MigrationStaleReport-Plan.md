# Migration Stale Open Report Plan

Status: Implemented 2026-09-17. Implementation reviewed 2026-09-17; the one MEDIUM finding it raised (msr-1) was fixed the same day. The manual acceptance checklist below is outstanding.
Remedy ruled by the owner 2026-09-15: Option A, clear the open report on reload.
Plan approved by the owner 2026-09-17; steps 1-6 landed in the commit that carries
this status change.

Base app version at drafting: 2.21.0 (unchanged - module-scoped change)
Migration module version: 1.8.0 at drafting, 1.8.1 as shipped
Repository base: `6ccdf9d`

Owner-reported issue 3 of the 2026-09-15 queue, recorded in `.agents/state.md`.

Review: `codereview codex (@azure-openai-eus2-global/gpt-5.5-dzs @ xhigh, standard)
over 6ccdf9d..d48f750`: three candidate findings, all verified against the code at
the pinned head and all admitted. MSR-A, MSR-B and MSR-C are absorbed into the
Steps below; the findings themselves are retained under Review findings as the
record of why those steps look the way they do.
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

Nothing else invalidates either field. Seven assignment points reload or discard
`batchUsers` and leave the report standing:

| # | Method | Line | What it does |
|---|--------|------|--------------|
| 1 | `LoadMigrationStatus` | `:1183-1184` | resets `expandedBatch` and `batchUsers` |
| 2 | `ToggleBatchDetails` (collapse) | `:1306-1307` | clears both |
| 3 | `ToggleBatchDetails` (expand) | `:1314` | clears then reloads |
| 4 | `ExecuteUserAction` (success) | `:1495-1496` | reloads the expanded batch |
| 5 | `RefreshBatchUsers` | `:1332-1336` | reloads |
| 6 | `SearchUser` (batch match) | `:1711-1716` | reloads |
| 7 | `SearchUser` (user match) | `:1742-1747` | reloads |

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

## Remedy, as ruled

The owner ruled Option A on 2026-09-15: **clear the open report whenever the user
list is reloaded or discarded.** After any refresh the operator sees the Report
button again and clicks once to get a freshly fetched report. No extra Exchange
calls; the cost is one extra click.

The rejected alternative, recorded so it is not re-litigated: Option B would have
re-fetched the open report on every reload, keeping the panel live at the cost of
one extra `Get-MigrationUserStatistics -IncludeReport` round trip per refresh.
It can be layered later if the extra click proves worse than the extra call.

The governing rule the implementation must satisfy: **never render report text
that was not just fetched.**

## Scope

In scope:

- `Components/Pages/Migration.razor` - a single clear/invalidate helper, called at
  all seven assignment points, plus generation-guarding of the async fetch.
- `ExchangeAdminWeb.Tests/MigrationStatusPageTests.cs` - branch-anchored
  source-level tripwires.
- `Modules/ModuleCatalog.cs` - Migration module version bump.

Out of scope:

- Any change to `Services/MigrationService.cs` or `GetMigrationUserReportAsync`.
  The service is correct; it fetches when asked. The defect is entirely in the
  page's retention of what it fetched.
- Auto-refresh of the batch list, polling, or any change to refresh cadence.
- Option B's re-fetch behavior.
- Disabling the refresh controls while a report is loading. The generation guard
  in step 2 makes that unnecessary, and gating more buttons is a UX change the
  owner has not asked for.
- The base app version. This is a module-scoped behavior change, so under
  `docs/ProjectConstitution.md` Deployment And Versioning (lines 112-115) only the
  module version bumps.

## Steps

1. Add the generation counter and one clear helper, so the seven call sites cannot
   drift apart:

   ```csharp
   // Bumped on every close. An in-flight LoadUserReport captures the value it
   // started under and drops its result if the generation has moved on, so a slow
   // fetch cannot repopulate a panel that a reload just cleared.
   private int reportGeneration;

   // The open report is a snapshot fetched once. Any reload of batchUsers can change
   // which migration a row refers to - a batch deleted and recreated under the same
   // name is a different migration with the same key - so the snapshot must not
   // survive the reload. Never render report text that was not just fetched.
   private void CloseUserReport()
   {
       reportGeneration++;
       reportUser = null;
       userReport = null;
       loadingReport = null;
   }
   ```

2. Rewrite `LoadUserReport` (`:1760-1787`) to capture and check the generation:

   ```csharp
   private async Task LoadUserReport(string emailAddress)
   {
       // Same row clicked twice: close it.
       if (reportUser == emailAddress)
       {
           CloseUserReport();
           return;
       }

       CloseUserReport();
       var generation = reportGeneration;
       loadingReport = emailAddress;

       try
       {
           var text = await MigrationSvc.GetMigrationUserReportAsync(emailAddress);
           if (generation != reportGeneration) return;
           userReport = text;
           reportUser = emailAddress;
       }
       catch (Exception ex)
       {
           if (generation != reportGeneration) return;
           userReport = $"Error: {ex.Message}";
           reportUser = emailAddress;
       }
       finally
       {
           if (generation == reportGeneration)
               loadingReport = null;
       }
   }
   ```

   A Blazor Server circuit runs its handlers on a single synchronization context,
   so the counter needs no lock; the check and the assignment cannot interleave.
   The superseded continuation deliberately leaves `loadingReport` alone: the
   `CloseUserReport()` that superseded it already nulled it, and a late write
   would resurrect a spinner for a fetch nobody is waiting on.

3. Call `CloseUserReport()` at all seven assignment points from the Diagnosis
   table: `LoadMigrationStatus` (`:1183`), both `ToggleBatchDetails` branches
   (`:1306`, `:1314`), `ExecuteUserAction`'s success reload (`:1495`),
   `RefreshBatchUsers` (`:1332`), and both `SearchUser` paths (`:1711`, `:1742`).
   Place each call adjacent to that site's own `batchUsers` assignment so the
   pairing is visible at the point of change.

4. Have the Close button call `CloseUserReport()` rather than its inline lambda
   (`:778`), so one definition owns the clear.

5. Add tripwires to `ExchangeAdminWeb.Tests/MigrationStatusPageTests.cs` in that
   file's established style. **Anchor every assertion to its own branch, not to
   the enclosing method body** - `ToggleBatchDetails` and `SearchUser` each have
   two independent stale-producing branches, and a body-anchored assertion passes
   when only one of them is guarded. Assert:
   - each of the seven sites pairs its `batchUsers` assignment with a
     `CloseUserReport()` call, matched within that branch's own text;
   - `LoadUserReport` captures a generation and checks it before assigning
     `userReport`, so the race guard cannot be dropped silently;
   - `CloseUserReport` increments `reportGeneration`.

   Follow the file's own warning at `MigrationStatusPageTests.cs:18-21`: a guard a
   broken page satisfies is worse than no guard, because it reads as coverage.

6. Bump the Migration module `Version` in `Modules/ModuleCatalog.cs:208` from
   `1.8.0` to `1.8.1` with a one-line comment naming this plan, matching the
   existing comment convention on that field.

## Why there is no behavioural test

The repo has no bUnit harness, so no test can render `Migration.razor` or invoke
one of its handlers. `MigrationStatusPageTests.cs` states this explicitly and
exists for exactly this gap. The guards in step 5 are source-level tripwires, not
behavioural coverage, and must be described as such in the commit message.

## Guard proof

Prove the tripwires bite before claiming completion. For each of the three
assertion groups in step 5, break exactly that thing, run
`dotnet test ExchangeAdminWeb.slnx`, confirm the matching test fails and names the
site, then restore:

1. Delete one `CloseUserReport()` call - and specifically delete it from one
   branch of `ToggleBatchDetails` while leaving the other, which is the case a
   body-anchored guard would miss.
2. Remove the generation check before the `userReport` assignment.
3. Remove the `reportGeneration++` from `CloseUserReport`.

Restoring by file copy keeps the old mtime and MSBuild will skip the rebuild -
touch the file after restoring, or the next run tests the mutated binary.

**Result, run 2026-09-17.** All three mutations bit, each naming the intended site,
and the restored file was verified byte-identical to the original:

| Mutation | Test that failed |
|----------|------------------|
| 1. `CloseUserReport()` removed from the `ToggleBatchDetails` COLLAPSE branch only | `EveryDiscardOfBatchUsersAlsoClosesTheOpenReport` and `BothBranchesOfToggleBatchDetailsCloseTheReport` |
| 2. generation check removed before the success-path `userReport` write | `NoReportTextIsWrittenByASupersededFetch` |
| 3. `reportGeneration++` removed from `CloseUserReport` | `ClosingTheReportClearsEveryFieldAndBumpsTheGeneration` |

Mutation 1 is the one that matters most: the other `ToggleBatchDetails` branch was
left guarded, and both tripwires still failed, which is the branch-anchoring
property MSR-C asked for.

## Verification

Run 2026-09-17, all green:

- `dotnet build ExchangeAdminWeb.slnx -c Release` - build succeeded, 0 errors.
  The one CS8604 warning is pre-existing in `AccountLockoutRemediationService.cs`
  and untouched by this change.
- `dotnet test ExchangeAdminWeb.slnx` - 2494 passed, 0 failed, 3 skipped.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` - exit 0.
- `git diff --check HEAD` - exit 0.

No PowerShell is touched, so PSScriptAnalyzer and Pester are not required by this
change and were not run.

## Manual acceptance checklist

Requires a dev deploy. Not satisfiable by automation.

1. Open Mailbox Migrations, go to Status, expand a batch, open a user's Report.
   Confirm the report renders.
2. With the report open, click Refresh batch users. Confirm the report panel
   closes and the Report button returns.
3. Reopen the report. Confirm the content is current.
4. With a report open, run a per-user action (Pause or Resume on a safe target).
   Confirm the panel closes rather than showing the pre-action text. This is the
   MSR-A path.
5. Reproduce the reported defect: with a report open, delete and recreate the
   batch under the same name from PowerShell, reload the status list, re-expand
   the batch, and confirm no report panel is showing for that user until it is
   explicitly reopened - and that reopening shows the new migration's report.
6. MSR-B race, best effort: click Report on a user with a large move report and,
   while the spinner is still showing, click Refresh batch users. Confirm no
   report panel appears when the fetch lands. If the fetch is too fast to
   interleave, record the check as not exercised rather than as passed.
7. Confirm the Close button still closes the panel.

## Review findings

All three were independently verified against the code at `d48f750` before being
admitted, and all three are absorbed into the Steps above. None is a defect in
shipped code - no code has shipped - so each was a required revision to this plan
rather than a `.agents/review/` finding record.

**MSR-A - the plan's call-site list was not exhaustive.** `ExecuteUserAction`
reloads the user list on its success path at `Migration.razor:1495-1496` and the
first draft omitted it. Trigger: open a report, then run Complete / Approve /
Pause / Resume / Clear on that user. The list refreshes and the report panel keeps
the pre-action text. Absorbed: the site list is now seven, enumerated in the
Diagnosis table and re-used by steps 3 and 5.

**MSR-B - clearing the fields did not invalidate an in-flight fetch.**
`LoadUserReport` sets `loadingReport`, awaits `GetMigrationUserReportAsync`
(`:1769-1776`), then assigns `reportUser`/`userReport`. Nothing disables the
refresh controls while that fetch is outstanding: the batch-users refresh button
gates on `loadingBatchUsers != null` (`:620-622`), not on `loadingReport`. Trigger:
open a report, then refresh before the fetch returns. The clear empties the fields
and the late continuation repopulates them, restoring the stale panel. Report
fetches use `Get-MigrationUserStatistics -IncludeReport`
(`Services/MigrationService.cs:872-875`) and can be slow, so the window is real.
Absorbed as the generation counter in steps 1 and 2.

**MSR-C - the proposed tripwire could pass with a broken branch.** The first draft
anchored per method body, but `ToggleBatchDetails` (`:1304-1314`) and `SearchUser`
(`:1709-1717`, `:1741-1747`) each have two independent stale-producing branches. A
single `CloseUserReport()` anywhere in the method satisfies a body-anchored
assertion while the other branch stays unguarded - the exact failure
`MigrationStatusPageTests.cs:18-21` warns about. Absorbed as the branch-anchoring
requirement in step 5 and the targeted mutation in guard proof 1.

Reviewer judgments confirmed without change: the cited lines and quoted
declarations are accurate; the batch-name-keying claim is correct because the
repro reuses the name; the module-only version bump is correct.

## Pending decisions

None. The remedy is ruled (Option A), the review findings are absorbed, and the
owner approved implementation on 2026-09-17.

## Implementation review

`codereview codex (@azure-openai-eus2-global/gpt-5.5-dzs @ xhigh, standard) over
1250220..3e4f13d: findings (1)`. Dispatched 2026-09-17T13:42Z, codex-cli 0.154.0,
read-only, capability proof passed - the reviewer quoted `Migration.razor:778`
verbatim and ran `git log --oneline -3`, echoing all three real subjects.

The reviewer was asked directly about five things. Four came back clean and stand
as reviewed: there is no eighth path that reloads or discards `batchUsers` without
clearing the report, the generation guard is correct on all three continuation
paths of `LoadUserReport`, no field combination was found that renders a
half-populated panel, and the module-only version bump to 1.8.1 is right.

The fifth produced **msr-1** (MEDIUM, admitted, `.agents/review/findings/msr-1.md`):
the ten tripwires assert that `CloseUserReport()` precedes the refetch, and that is
exactly what `RefreshBatchUsers` does - it bumps the generation *before* its await
and never nulls `batchUsers`, so the old rows and their **Report** button
(`Migration.razor:737-739`, gated on neither `loadingBatchUsers` nor
`actionInProgress`) stay live for the whole reload. A report opened inside that
window captures the post-bump generation and writes itself over rows that have
since been replaced. `ExecuteUserAction`'s success reload has the same shape and a
narrower window. The finding was confirmed line by line before intake, not accepted
on the reviewer's word.

### msr-1 fixed

Fixed on the owner's go, 2026-09-17. The remedy is structural rather than a patch at
the two offending call sites: one private helper, `ReplaceBatchUsers`, now owns every
replacement of the rendered rows and closes the report itself, and the seven
assignment sites hand it the awaited fetch directly. The close therefore lands on the
same side of the await as the new rows, so a report opened mid-reload is discarded by
a generation bump that happens after it was started. Discards (`batchUsers = null;`)
are unchanged - those paths stop rendering the rows, and their existing guard already
covers them.

Three tripwires were added. The first two are ordinary ordering assertions on the
helper; the third is the one that matters, asserting per occurrence that no code path
anywhere in the page assigns `batchUsers` outside the helper, so an eighth reload path
added later cannot reintroduce the hole. The guard proof reverted each of the three
mutations in turn - and the first mutation IS the pre-fix code, which the original ten
tripwires passed.

The alternative considered and rejected was gating the **Report** button on
`loadingBatchUsers != null`, matching the refresh button beside it. It is one
attribute, but it blocks a read-only diagnostic the operator may legitimately want
during a reload, and it leaves the underlying invariant unenforced.

## Outstanding

- The manual acceptance checklist above. It needs a dev deploy and is the only
  evidence that reaches the rendered page; nothing in this repo can render
  `Migration.razor`.
