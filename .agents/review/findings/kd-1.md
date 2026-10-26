# kd-1: Message Trace detail exports are listed and downloadable by any policy holder, not only their submitter

**Severity**: MEDIUM - an authorization gap. `Services/MessageTraceExportListing.cs` `GetExports` lists every operator's finished exports and `TryDownloadAsync` checks the ticket and the export's state but never the submitter, so any operator holding the Message Trace policy can download any other operator's detail export (message metadata for addresses they never searched).
**Status**: Declined by owner ruling 2026-10-26 ("1. no") - the fix reversed D2 and D4 of docs/MessageTraceDownloadLink-Plan.md, which stand. The fix commit `c139a31b` was merged with its branch at `5ea424d5` and reverted in the next commit; the Message Analysis 1.8.0 bump it carried is left as the catalog note now documents kd-45 at that number. Any policy holder may list and download any operator's detail export, by design.
**Branch**: `fix/kd-misc` (worktree `D:\source\ExchangeAdminWeb-wt\kd-misc`, from master `eb017102`)
**Commit**: fix `c139a31b`; module version in `d79e1b79`
**Reviewer**: implementer (recorded in `.agents/state.md` "Known defects", re-verified 2026-10-23 as of `d881ea58`)

## Evidence

At `4498da49`: `Services/MessageTraceExportListing.cs` `GetExports` (about line 119) and `TryDownloadAsync` (about 173). Trigger: operator B opens the exports list after operator A's export finished. Predicted failure: B sees and downloads A's export; the audit row, if any, names B as a download of a file B did not request.

## Approach

Re-read at `eb017102` (the branch base); every line below is that commit's.

- `Services/MessageTraceExportListing.cs:119-122` lists every operator's exports:
  `public IReadOnlyList<MessageTraceExportListItem> GetExports()` /
  `var jobs = _jobs.GetFinishedByType(MessageTraceDetailJobProcessor.ModuleName, MessageTraceDetailJobPayload.JobType, ListLimit);`
  - no viewer, no submitter predicate.
- `:148-158` `Resolve(string jobId)` filters by module and job type only (`job.ModuleId`, `job.JobType`).
- `:173-183` `TryDownloadAsync(string jobId, string? ticket)`: the ticket presence check (175-176),
  `var item = Resolve(jobId);` (178), `if (!item.CanDownload)` (182), then the read at 190 - nothing compares
  `job.SubmittedBy` with the caller.
- `Components/Pages/MessageTraceReports.razor:141` `items = Exports.GetExports();` (Refresh), `:158`
  `var result = await Exports.TryDownloadAsync(jobId, ticket);`, `:170` the relist on failure, and `:191-192`
  `Audit.LogLookupAction(currentUser, clientIpAddress, AuditAction, $"job {jobId}", result.Succeeded, result.Error, ticket);`
  - the row names the downloader and the job, never whose export it was.

What changes:

1. **Exports carry their submitter, and the listing is scoped to it.** `BulkJob.SubmittedBy` is already the
   submitter (`Components/Pages/MessageTrace.razor:1284`, `SubmittedBy = currentUser`, the operator's
   `Identity.Name`). `GetExports()` becomes `GetExportsAsync(ClaimsPrincipal viewer)`: the viewer's own exports
   through the jobs store's existing ownership scope - `BulkJobService.GetFinishedOwnedJobs`
   (`Services/Jobs/BulkJobService.cs:200-201`), filtered in SQL before the ordering and the limit
   (`Services/Jobs/BulkJobRepository.cs:354` `OwnerScope`, OrdinalIgnoreCase, blank owner matches nothing), read
   in pages because that scope clamps each call to 100 rows (`:421`) and `ListLimit` is 200. A viewer with no
   authenticated name owns nothing.
2. **One existing admin alias widens it; none is invented and none renamed.** `AdminSettings`, the system
   module's alias (`Modules/ModuleCatalog.cs:1380-1397`), whose policy answers to `Security:AdminGroups`
   (`:88-96`, `GroupAuthorizationRequirement(adminGroups, alias)`) and which this app already reads as "global
   admin" (`Components/Pages/ModuleConfig.razor:1074-1075`, `isGlobalAdmin`). An administrator can grant
   themselves any section's access, so seeing every export gives them nothing they could not take.
   `MessageTrace` and `MessageTraceSearch` are untouched (`.agents/state.md` standing instruction); the inert
   `MessageTraceSearch` is not consulted (its enforcement is `docs/MessageTracePermissionSplit-Plan.md` slices
   2-4, outside this stream). The policy is evaluated through `IAuthorizationService`, now injected into the
   listing; a check that throws is no grant (fail closed, Known Failure Class 3) and the viewer keeps their own.
3. **A non-submitter download is refused with an audited denial.** `TryDownloadAsync(jobId, ticket, viewer)`
   keeps the ticket check first (the F2 ordering), finds the job (module and type as `Resolve` does), and then -
   before the export's state is classified or its file touched - refuses anyone who is neither its submitter nor
   an `AdminSettings` holder: no bytes, no row metadata, the message "That export belongs to another operator...",
   and `AuditExtra` `{ refusedAt: "Ownership", mutationAttempted: false, exportSubmittedBy: <submitter> }` (the
   aud-a28 refusal convention). The page passes `result.AuditExtra` to its one audit row, so the denial is a
   Failed `MessageTrace_ExportDownload` row that says nothing was attempted and whose export it was. An
   administrator's download of another operator's export carries `exportSubmittedBy` and `grantedBy:
   "AdminSettings"` on its row, which answers the record's attribution half.
4. **The page and the export-ready email say who can download.** The page asks for the viewer's principal
   fresh at each list and each download (`AuthStateProvider`), so a grant removed while it is open stops
   widening the list. The intro states the rule; the email (`Services/EmailService.cs:918-920`, "Downloading
   requires your Windows sign-in and Message Analysis access") now says only the requester or an application
   administrator can download it.

Declared files: `Services/MessageTraceExportListing.cs`, `Components/Pages/MessageTraceReports.razor`,
`Services/EmailService.cs`, `ExchangeAdminWeb.Tests/MessageTraceExportListingTests.cs`,
`ExchangeAdminWeb.Tests/MessageTraceDetailJobProcessorTests.cs` (the listing's constructor),
`ExchangeAdminWeb.Tests/ProgressRegistry.cs`
(one prose reference to the renamed `GetExports`), this record. The module version bump is the branch's version
commit.

Mutation: the submitter comparison removed (every viewer treated as the submitter).

## Files changed

- `Services/MessageTraceExportListing.cs` - `GetExports()` became `GetExportsAsync(ClaimsPrincipal viewer)`
  (own exports through `GetFinishedOwnedJobs`, read in pages to `ListLimit`; every operator's for an
  `AdminSettings` holder); `TryDownloadAsync(jobId, ticket, viewer)` with the ownership refusal; new
  `EveryExportPolicy`, `NotYourExportMessage`, `RefusedAtOwnership`, `IsSubmitter`, `SeesEveryExportAsync`
  (fail closed), `OwnFinishedExports`, `FindExport`; `MessageTraceExportDownloadResult.AuditExtra`; the
  constructor takes `IAuthorizationService`; `Resolve` is now internal (it checks no ownership).
- `Components/Pages/MessageTraceReports.razor` - `Refresh` is async and lists for the current principal;
  `Download` passes the principal and the result's `AuditExtra` to its one audit row; the intro and the
  empty-list text say who sees what. Not line-keyed (`ClickGateRegistry.NotYetConverted`, tier 4).
- `Services/EmailService.cs` - the export-ready mail's download sentence.
- `ExchangeAdminWeb.Tests/MessageTraceExportListingTests.cs` - every existing test kept, now as the submitter;
  the kd-1 section below; the page tests live here too (they share the jobs fixture), so no new file.
- `ExchangeAdminWeb.Tests/MessageTraceDetailJobProcessorTests.cs` - the listing's new constructor argument.
- `ExchangeAdminWeb.Tests/ProgressRegistry.cs` - one prose mention of `Exports.GetExports(`.
- This record.

## Guard proof

New tests in `MessageTraceExportListingTests`: `GetExports_ShowsAnOperatorOnlyTheExportsTheySubmitted`,
`GetExports_ComparesTheSubmitterIgnoringCase_AsTheJobsStoreDoes`,
`GetExports_AnApplicationAdministratorSeesEveryOperatorsExports`,
`GetExports_AnAdministratorCheckThatThrows_WidensNothing`, `GetExports_AViewerWithNoAuthenticatedName_OwnsNothing`,
`GetExports_OtherOperatorsExportsCannotCrowdOutTheViewersOwn` (one own export behind 200 newer foreign ones),
`GetExports_ReadsTheViewersOwnExportsPastOnePageOfTheOwnershipScope` (150 own),
`Download_OfAnotherOperatorsExport_IsRefusedBeforeTheFileIsRead_AndSaysNothingWasAttempted` (read spy at 0, no row,
the three audit fields), `Download_ByAnAdministrator_OfAnotherOperatorsExport_Succeeds_AndSaysWhoseItWas`,
`Download_WhenTheAdministratorCheckThrows_ANonSubmitterIsRefused`,
`Download_ByTheSubmitter_DoesNotDependOnTheAdministratorCheck`, and three that drive the compiled page's handlers
by reflection with a real `AuditService` over the fixture's log root:
`Page_ARefusedDownloadOfAnotherOperatorsExport_IsOneAuditedDenial_AndSendsNothing` (one Failed
`MessageTrace_ExportDownload` row with `refusedAt` Ownership, `mutationAttempted` false, `exportSubmittedBy`, the
ticket and the target; no `downloadFile` interop), `Page_TheSubmittersDownload_SendsTheFile_AndItsRowIsAnOrdinarySuccess`,
`Page_TheListShowsTheViewersOwnExports`.

Mutations, each applied by exact single-occurrence replacement, confirmed applied by md5, built, run against
`FullyQualifiedName~MessageTraceExportListingTests` (37 tests), then restored byte-exact (md5 equal to the
pre-mutation file) with a fresh mtime, rebuilt and rerun:

| # | mutation | with it | restored |
| --- | --- | --- | --- |
| M1 | the submitter comparison removed: `if (!IsSubmitter(job, viewer))` -> `if (!true)` | 4 failed: the refusal, the throwing-admin-check refusal, the admin download's attribution, the page denial row | 37 passed |
| M2 | the listing's owner scope removed: `: OwnFinishedExports(viewer);` -> `: _jobs.GetFinishedByType(...)` | 5 failed: own-only, throwing admin check, no-name viewer, crowd-out, the page list | 37 passed |
| M3 | the page drops the extra: `..., ticket, result.AuditExtra);` -> `..., ticket);` | 1 failed: the page denial row | 37 passed |
| M4 | one page of the ownership scope only: `while (jobs.Count < ListLimit)` -> `while (jobs.Count == 0)` | 1 failed: the 150-export read | 37 passed |

Full suite at `c139a31b`: 6706 passed / 1 failed - `SqliteBusyWaitTests.AWriteThatMeetsAHeldLock_FailsBusy_AfterTheDocumentedWait`
("the write waited 9.93 s: more than one busy wait of 5 s"), a timing test in the config store this commit does not
touch, run while several other agents' suites loaded the host; rerun alone it passed (5 of 5). Every later commit's
full suite passed with it included.

## Coder dispute

Not a dispute of the defect as admitted, but the fix reverses two earlier owner rulings, and the owner should
confirm before merge:

- `docs/MessageTraceDownloadLink-Plan.md:94-101`, **D2 (RULED 2026-07-29)**: "message trace isn't privileged and
  anyone who can log in and is able to access the message analysis module should be able to download it" -
  "**No ownership check**: an operator with module access may download any export, not only their own."
- `:142-156`, **D4 (RULED)**: the operator may send the export-ready mail to any address ("the data stays behind the
  login gate, which is the point of the redesign"). Under this fix a recipient who is neither the requester nor an
  application administrator follows the link and sees nothing to download; the mail now says so.

The admission rests on `.agents/decisions.md` 2026-10-23 (evening), which reads the former "Known defects" list as
inside "all remaining issues"; that list did not cite D2. Under the repo's authority order the decisions entry
outranks an Implemented plan, which is why this was built rather than held. If the owner keeps D2, drop this
finding's commit (it touches nothing else) and close kd-1 as by design; if he confirms, D2 and D4 need a
supersession note (a `decision` entry), which this branch does not write.

## Known gaps

- The widening alias is `AdminSettings` only. A delegated module admin of Message Analysis (`ModuleAdminService`) is
  not an alias and does not widen; `MessageTraceSearch` is inert and deliberately not consulted. Either could be the
  owner's preferred widening instead; neither is invented here.
- The ticket-blank refusal and the other existing failure rows are unchanged (no `refusedAt`); only the new
  ownership denial carries the refusal convention.
- No live check: nothing was deployed or run against a real tenant; the principal shape (`Identity.Name` equal to
  the enqueue-time `SubmittedBy`) is the one `Components/Pages/MessageTrace.razor:657` and `:1284` use, read, not
  observed under Negotiate.

## Reviewer comments

None yet (codex review pending, dispatched by the supervising session).

## Closeout

Pending: merge into master, codex review, branch deletion. Branch-tip gates at `d79e1b79` (the branch's last code
commit): Release build 0 errors / 19 warnings (all pre-existing, none in a changed file);
`dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4` 6720 passed / 0 failed / 0 skipped;
`dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`, `git diff --check HEAD` and
`tools/Test-AsciiOnly.ps1` clean. PSScriptAnalyzer and Pester not run: no PowerShell file changed.
