# Message Analysis: separate the trace-search grant from header analysis

Status: **APPROVED by the owner 2026-09-22 and IN PROGRESS.** All seven open questions are
settled - see the Open questions section, where each records its ruling and where that ruling
lives. Revision 3, **codex consensus reached**, after three codex review rounds:
`unsound` with four findings, `unsound` with two, then `sound_with_changes` with one. All seven
were verified against the source and accepted, one of the round-2 pair with a scoped
implementation variance that is stated explicitly and that round 3 upheld; the changes are folded
in below and all three review records are the last three sections of this document. Codex
consensus was never owner approval; the owner's go came separately, on 2026-09-22, once every
question was settled. Queue item 4, in the owner's
words: "Break out permissions for message trace vs header analysis." Drafted 2026-09-18 against
`337e07b`, revised the same day against `155eaf7`, `c3b4239` and `5e69dd9`, reading the working tree (which carries another agent's in-flight
`ToggleDetail` change and a `MessageTrace` module version already at 1.4.2). Line numbers below
are as of that working tree; every claim also names the method or the exact string it rests on, so
a shifted line number does not invalidate it.

## Read this before approving: what happens on the first deploy

**Trace search stops working for every operator, including the owner, the moment the enforcement
slice is deployed, and stays broken until a group is stored against the new policy alias.** This
is not a risk, a corner case, or a misconfiguration. It is what the code does, on every path, for
every user, and it is unavoidable for any new granular permission in this app.

The chain, quoted:

1. `Modules/ModuleCatalog.cs:112-116` builds a granular policy out of two requirements, the
   parent's and its own:

   ```csharp
   options.AddPolicy(gp.PolicyAlias, policy => policy
       .RequireAuthenticatedUser()
       .AddRequirements(new GroupAuthorizationRequirement(mainAlias, dynamic: true))
       .AddRequirements(new GroupAuthorizationRequirement(gp.PolicyAlias, dynamic: true)));
   ```

2. Each requirement resolves its groups through `SectionAccessService.GetGroupsForSection`
   (`Services/SectionAccessService.cs:64-71`):

   ```csharp
   var (data, source) = ReadSectionAccess();
   if (source == SectionAccessSource.None)
       return IsFailClosed(section) ? Array.Empty<string>() : _allowedGroups;

   return data.TryGetValue(section, out var groups) ? groups : Array.Empty<string>();
   ```

   On this deployment the SQLite section-access store exists and is marked configured, so
   `ReadSectionAccess` returns through its `if (configured) return (data, SectionAccessSource.Fragment);`
   branch and **line 70 is the line that runs**. A brand-new alias is not a key in `data`, so the
   ternary takes its false arm and returns `Array.Empty<string>()`. Note what this means: the
   `FailClosed` flag is irrelevant to the outcome. Line 68, the only line `FailClosed` influences,
   is reached only when there is no section-access source at all. A new alias denies everyone
   whether or not it is marked fail-closed.

3. `Authorization/GroupAuthorizationHandler.cs:78-86` turns an empty group list into an explicit
   failure:

   ```csharp
   if (groups.Length == 0)
   {
       if (requirement.SectionName == "Application")
           _logger.LogError("Security:AllowedGroups is empty - denying all access until configured");
       else
           _logger.LogError("SectionAccess:{Section} has no groups configured - denying all access", requirement.SectionName);
       context.Fail(new AuthorizationFailureReason(this, $"No groups configured for {requirement.SectionName}. Contact your administrator."));
       return Task.CompletedTask;
   }
   ```

   The mechanism is `context.Fail(...)`, not a silently unsatisfied requirement.
   `AuthorizationHandlerContext.HasSucceeded` is `!HasFailed && no pending requirements`, so a
   `Fail()` call denies the policy outright and cannot be rescued by any other handler succeeding.

**The remedy is ordering, and it is the reason this plan has four slices rather than one.**
Slice 1 declares the alias and changes no behaviour: nothing consults the new policy, so nothing
can be denied by it, but the alias immediately appears in Module Config's Access tab and becomes
grantable. The owner configures the group. Only then do the remaining slices, which enforce, ship. Because
dev and prod share one config database (`.agents/repo-guidance.md` Architectural Invariant 2),
configuring the group on dev makes it live on prod at the same moment, so the grant is in place
before prod ever sees the enforcing code.

The alias cannot be granted before slice 1 is deployed. `GetConfigurablePolicyAliases`
(`Modules/ModuleCatalog.cs:42-56`) and the per-module Access tab (`Components/Pages/ModuleConfig.razor:910-912`)
both derive the list from the catalog in the running binary. There is no way to pre-seed a row for
an alias the deployed code does not declare.

**What that costs if the ordering is ignored is an outage window, not an unrecoverable state, and
the distinction matters for incident response.** Landing the declaration and the enforcement in
one deploy denies trace to everyone until an admin grants the group - but an admin can do exactly
that, in the app, with no rollback and no hand-editing of the database. `ModuleConfig.razor:875-882`
admits a caller who satisfies `AdminSettings` or is a module admin:

```csharp
var adminResult = await AuthorizationService.AuthorizeAsync(user, "AdminSettings");
isGlobalAdmin = adminResult.Succeeded;

if (!isGlobalAdmin && !ModuleAdmin.IsModuleAdmin(ModuleId, user))
{
    Navigation.NavigateTo("access-denied", forceLoad: true);
    return;
}
```

`AdminSettings` is a system module, and `ModuleCatalog.cs:88-96` registers it with a **static**
`GroupAuthorizationRequirement(adminGroups, alias)`. `GroupAuthorizationHandler:74-76` therefore
takes `requirement.AllowedGroups` for it and never consults the section-access store, so a missing
`MessageTraceSearch` row cannot lock a global admin out of the page that fixes it. The Access tab
builds its alias list from the running descriptor (`:910-912`) and saves through the ordinary path
(`:1197-1214`). One narrowing worth knowing during an incident: the section-access save handler
re-checks `AdminSettings` specifically and answers a module admin with "Only global admins can
change section access", so the repair needs a **global** admin, not a module admin.

So: deploy out of order and the cost is minutes of denied trace plus a global admin's attention.
The slice-1 -> configure -> enforce ordering below is the no-outage path and remains the plan of
record; it is not the only path back.

## Goal

Message Analysis is one page with two capabilities of very different sensitivity behind one policy.
Split them so that opening the module and analysing headers is one grant, and searching other
people's mail is a second, narrower one.

## The two halves, surveyed

`Components/Pages/MessageTrace.razor` answers two routes (`:1-2`, `/message-analysis` and the
legacy `/message-trace`), carries one page-level policy (`:7`,
`@attribute [Authorize(Policy = "MessageTrace")]`), re-checks that same policy in
`OnInitializedAsync` (`:659`, `AuthorizationService.AuthorizeAsync(user, "MessageTrace")`), and
renders two tabs selected by `activeTab` (`:600`, default `"headers"`).

**Header Analysis.** The operator pastes headers (`AnalyzePastedHeaders`) or uploads an `.eml`/`.msg`
(`AnalyzeUploadedFile`, `InputFile` at `:42`); both funnel into `AnalyzeHeadersAsync`, which calls
`HeaderAnalysisService`. That service's entire dependency set is `System.Text.RegularExpressions`,
`ExchangeAdminWeb.Models`, `MimeKit` and `MsgReader` (`Services/HeaderAnalysisService.cs:1-3`,
`:123`), plus a temp file it writes and cleans up itself (`:119`, `:178`). **It makes no Exchange,
Graph, LDAP or directory call of any kind.** It parses bytes the operator already possesses and
could paste into any online header analyser. Audits as `MessageHeaderAnalysis`.

**Trace Search.** `RunTrace` calls `RunRealtimeTrace`, which calls
`MsgTrace.GetMessageTraceAsync(...)` across Exchange Online and the on-premises transport logs for
**any sender or recipient the operator names** and returns senders, recipients, subjects, status,
server, message IDs and IP addresses. `ToggleDetail` pulls the per-hop delivery trail for one
message. `ExportCsv` writes the entire result set, not the rendered 50 (`:904-916` explains why).
`DownloadSelectedDetails` fetches detail for up to `LiveMax` messages and downloads it.
`EmailSelectedDetails` enqueues a background job that does the same for up to `EmailMax` and
publishes the result as a downloadable export. Audits as `MessageTrace`, `MessageTrace_Detail`,
`MessageTrace_DetailDownload`, `MessageTrace_DetailEmailJob`.

The module's own main-permission description already states the asymmetry
(`Modules/ModuleCatalog.cs:268-272`): "Open the module and trace any user's mail, reading senders,
recipients, subjects, headers and transport log detail."

## The split, and why this direction

**Main permission `MessageTrace` keeps the module and header analysis. A new fail-closed granular
gates trace search and everything derived from it.**

The direction is not a matter of taste and the code settles it. Header analysis reads nothing the
operator does not already hold; trace reads mail metadata for the entire tenant plus the on-prem
transport logs. Constitution "Authorization" (`docs/ProjectConstitution.md:30`) makes module
permissions the gate for that module, and the granular layering
(`docs/AdminModuleSpec.md:84-88`) requires a granular holder to also hold the parent grant, so
"trace implies module access" comes for free and "module access implies trace" is what we are
removing. The alternative shapes were considered and rejected:

- *Split the other way* (main = trace, granular = header analysis). Inverts the sensitivity
  ordering and would mean granting tenant-wide mail search in order to parse a pasted header.
- *Bare module-access main, two granulars.* Costs an extra alias and an extra deploy-time denial
  for no gain: the low half has no second tier to withhold, and `ServiceHealth`'s catalog test
  (`ExchangeAdminWeb.Tests/ModuleCatalogTests.cs:129-136`) records the house position that a
  module with nothing to withhold declares no granular.
- *Rename the main alias* to something honest like `MessageAnalysis`. **Do not.** The alias is the
  section-access row key (`ModuleCatalog.cs:105`, `SectionAccessService.GetGroupsForSection`);
  renaming it orphans the stored grant and locks every operator out of the whole module, which is
  strictly worse than the legibility problem it fixes. Accepted cost, mitigated by rewriting the
  descriptions (see below).

### What the proposed shape missed

**`Components/Pages/MessageTraceReports.razor` must move to the granular too, or the split leaks
the exact data it exists to protect.** That page is a separate component at
`/message-analysis/reports` carrying its own `@attribute [Authorize(Policy = "MessageTrace")]`
(`:4`) and its own `OnInitializedAsync` check on the same policy (`:126`). It calls
`Exports.GetExports()` (`:143`), which is
`_jobs.GetFinishedByType(MessageTraceDetailJobProcessor.ModuleName, MessageTraceDetailJobPayload.JobType, ListLimit)`
with **no filter on the submitting user** - the table renders `@item.SubmittedBy` per row (`:78`)
precisely because it is an all-operators listing - and `Download` (`:146`) hands any listed export
to any holder of the page's policy via `Exports.TryDownloadAsync(jobId, ticket)` (`:157`). Left on
the main permission, a header-analysis-only operator could open that URL and download every other
operator's full trace detail exports. Gating the page's own handlers is required by Constitution
`:29`, "UI hiding is not security. Direct URL access and direct event invocation must still be
denied" - and this is a direct URL, reachable without the Message Analysis page rendering a link.

## 1. Names

| | value |
| --- | --- |
| `Name` | `Search` |
| `PolicyAlias` | `MessageTraceSearch` |
| `FailClosed` | `true` |

`docs/AdminModuleSpec.md:106` gives the convention as "ParentId + PermissionName", worked as
`MailboxPermissionsOnPrem`, `MigrationCreate`; the live siblings are `BlockedSendersUnblock`,
`UndoAuditedActions`, `RiskyUsersRemediate`, `IntuneDevicesDelete`,
`AccountLockoutRemediationLogoff`. `MessageTrace` + `Search` gives `MessageTraceSearch`.
`MessageTraceTrace` also satisfies the convention and reads worse; see open question 2.

`FailClosed: true` matches every other Exchange-touching permission in the catalog and matches the
module's own main permission (`ModuleCatalog.cs:272`). As established above it changes nothing on
a configured store; it matters only on a server where section access has never been set at all,
where it is the difference between denying and falling through to the legacy app-wide
`Security:AllowedGroups`.

Both permission descriptions change in the same commit, because the current main description is
now false:

- Main `MessageTrace`: describe opening the module and analysing pasted or uploaded message
  headers locally, and state plainly that searching message traces needs the separate Search
  permission. Must still end with a period and must not restate the name or alias
  (`ModuleCatalogTests.Catalog_EveryPermissionCarriesAnOperatorFacingDescription`, `:471-503`).
- Granular `MessageTraceSearch`: describe searching and reading any user's mail metadata across
  Exchange Online and the on-premises transport logs, including per-message delivery trails and
  the detail exports.

## 2. The access consequence

Covered in full at the top of this document. Restated in one line so it cannot be read past: **a
new fail-closed granular that nobody has been granted denies everyone, and on this deployment it
does so regardless of the `FailClosed` flag, because `GetGroupsForSection` returns an empty array
for any alias absent from the configured store.** Configuring the group is step 1 of the manual
acceptance checklist and a precondition of deploying slice 2 or anything after it.

## 3. Enforcement points

UI state is not an enforcement point. The tab strip is `<button>` elements (`:29`, `:32`), so
`disabled` genuinely works on them - unlike Migration's anchors - but a disabled button still
leaves `RunTrace` and every other handler invokable over the circuit, so each one is gated in the
handler.

Resolve `canTrace` once in `OnInitializedAsync`, immediately after the existing main-policy check
at `:659`, and use it for rendering only:

```csharp
canTrace = (await AuthorizationService.AuthorizeAsync(user, "MessageTraceSearch")).Succeeded;
```

| # | point | file / anchor | change |
| --- | --- | --- | --- |
| 1 | page attribute | `MessageTrace.razor:7` | **unchanged.** Stays `[Authorize(Policy = "MessageTrace")]`. Moving it to the granular would deny header analysis to everyone without trace, which is the opposite of the ask. Pinned by a tripwire. |
| 2 | `OnInitializedAsync` | `:653-676` | keep the `MessageTrace` check verbatim; add the `canTrace` resolution after it. |
| 3 | `ShowTraceTab` | `:649` | becomes a guarded method: refuse to set `activeTab = "trace"` when the operator does not hold the grant. Currently an expression body; it becomes a block. |
| 4 | `UseHeaderTraceSuggestion` | `:863-881` | second entry into the trace tab (`activeTab = "trace"` at `:878`) and, with `runNow: true`, a second entry into `RunTrace`. Guard at the top and return. Its two buttons (`:56`, `:57`) also stop rendering. |
| 5 | `RunTrace` | `:770` | re-check `MessageTraceSearch` before `RunRealtimeTrace`. Covers the Search button (`:299`) and the Run Trace Now button (`:57`). |
| 6 | `RunRealtimeTrace` | `:803` | no separate gate. It is private, called only from `RunTrace:795`, and gating both would double the audit noise. Recorded here as a deliberate exemption, not an oversight. |
| 7 | `ToggleDetail` | `:959` | re-check before `MsgTrace.GetMessageDetailAsync`. **Placement constraint, see below.** |
| 8 | `ExportCsv` | `:916` | re-check. It exports the whole result set while the table renders at most `MaxRenderedResults` (50), so it is not merely a copy of what is on screen. |
| 9 | `DownloadSelectedDetails` | `:1063` | re-check before the `GetMessageDetailAsync` loop. |
| 10 | `EmailSelectedDetails` | `:1106` | re-check before `BulkJobs.Enqueue`, **and** attach `AuthSnapshotJson` to the enqueued job (point 16). The method is `private Task`, not `async`, so adding an await converts it to `private async Task`. |
| 11 | trace tab render | `:36` / `:264` | the trace body is the `else` arm of `@if (activeTab == "headers")`, so **any** `activeTab` value other than `"headers"` renders it. Change the condition to `@if (activeTab == "headers" \|\| !canTrace)` so a header-only operator lands on the headers branch whatever `activeTab` holds. One token, and it makes the render fail-safe against a future edit that lets `activeTab` slip. |
| 12 | reports page attribute | `MessageTraceReports.razor:4` | `[Authorize(Policy = "MessageTraceSearch")]`. |
| 13 | reports page init | `MessageTraceReports.razor:126` | `AuthorizeAsync(user, "MessageTraceSearch")`. |
| 14 | reports `Download` | `MessageTraceReports.razor:146` | re-check before `Exports.TryDownloadAsync`. |
| 15 | snapshot capture | `MessageTrace.razor` `OnInitializedAsync` | capture the authorization decision on the circuit, where the principal is live, for the off-circuit re-check at point 16. See "The background export job". |
| 16 | background job, per row | `Services/Jobs/MessageTraceDetailJobProcessor.cs` `ProcessRowAsync:76-85` | fail closed on a missing or stale snapshot **before** `_details.GetMessageDetailAsync`. This is the enforcement point a UI gate cannot reach: the job runs later, off-circuit, under the module's shared Exchange credential. |
| 17 | background job, completion | same file, `OnJobCompletedAsync:94-136` | **the point a row gate does not cover.** The same preflight, as the first statement, so no CSV is built, no file is saved and no mail is sent. Without it a fully denied job still publishes a downloadable export - see "Gating the rows is not enough". |
| 18 | export listing | `Services/MessageTraceExportListing.cs` `ClassifyState:258-278` | a denied job must classify as non-downloadable. New `Denied` state and marker; `CanDownload` (`:45`) then refuses it for free. |
| 19 | not gated | `OnRecipientInput`, `ToggleRowSelection`, `ToggleSelectAll`, `ClearSelection`, `TogglePasteInput`, `ClearHeaderAnalysis`, `ShowHeadersTab` | pure circuit-local UI state, no backend call. Named so the exemption is explicit. |

### Placement constraint on `ToggleDetail`

`ExchangeAdminWeb.Tests/ClickGateStuckFlagTests.cs:169-181`
(`ToggleDetail_RaisesDetailLoadingBeforeItsFirstAwait`) asserts that `detailLoading = true;`
precedes the method's first `await`, where "first await" is the first whole-word `await` match in
the comment-blanked body (`:255-259`). **Putting the authorization re-check at the top of the
method breaks that test.** Do not "fix" the test; it is load-bearing single-flight protection
landed days ago.

Put the re-check as the first statements *inside the existing `try`*, before
`MsgTrace.GetMessageDetailAsync`. Then `detailLoading = true;` still precedes the first await, and
the denial path can simply audit and `return`, because the existing token-guarded `finally`
(`:997-1014`) lowers the flag on the way out. That also keeps
`ToggleDetail_LowersDetailLoadingOnlyInsideTheFinally` (`:207-228`) satisfied, which fails on any
clear outside the `finally`.

The same care applies to `RunTrace`: `RunTrace_StillRescuesDetailLoadingAndSupersedesTheToken`
(`:231-244`) requires `detailLoading = false;` and `detailRequestToken++;` to survive in that
method. The re-check goes after the existing state resets at `:775-784` and before
`await RunRealtimeTrace()` at `:795`, inside the `try` whose `finally` lowers `isLoading`.

### Which rule requires the handler gates

Constitution `:28` ("every mutating operation must re-check authorization immediately before the
write") does not strictly bind here: a trace is a read. The binding rule is `:29`, "UI hiding is
not security. Direct URL access and direct event invocation must still be denied," plus
`docs/AdminModuleSpec.md:207-212`, which gives the granular re-check its canonical shape. State
that explicitly rather than overclaiming the mutation rule, so a later reader does not conclude
the gates were cargo-culted.

`BlockedSenders.razor:308-329` is the worked precedent for the handler gate itself. Note one place
this plan deliberately goes further than that precedent: the Unblock button there
(`BlockedSenders.razor:87`) is `disabled="@isLoading"` only and is rendered to operators who lack
`BlockedSendersUnblock`; the granular is enforced purely in the handler. That is correct but
unfriendly, and this page does better (see below) because it has an owner-facing rule of its own
about silent refusals.

### The background export job

**A UI gate cannot cover this, and leaving it open would make the plan's coverage claim false.**
`EmailSelectedDetails` enqueues a `BulkJob` that a worker thread runs later, with no principal on
the thread and the module's shared Exchange credential in hand.
`MessageTraceDetailJobProcessor.ProcessRowAsync` (`:76-85`) goes straight to the fetch:

```csharp
var payload = Deserialize(job);
var message = payload.Messages![rowIndex];
var target = string.IsNullOrWhiteSpace(message.MessageId) ? message.MessageTraceId : message.MessageId;

var detail = await _details.GetMessageDetailAsync(message, cancellationToken);
```

There is no authorization of any kind between the enqueue and that call, and
`MessageTrace.razor:1157-1170` builds the job without `AuthSnapshotJson`. Today that is not a leak,
because every `MessageTrace` holder can trace anyway. After the split it is one: the set of
operators entitled to read per-message delivery detail becomes strictly narrower than the set who
could submit a job before the split shipped.

**This is a settled pattern, not a new design, so it is closed here rather than raised as a
question.** The repo already carries every piece:

- `BulkJob.AuthSnapshotJson` (`Services/Jobs/BulkJobModels.cs:80`) is the persisted field.
- `JobAuthorizationSnapshot.Capture(user, section, allowedGroupsForSection)`
  (`Services/Jobs/JobAuthorizationSnapshot.cs:49-79`) records, on the circuit, which of the
  section's groups the submitter actually satisfied.
- `IsStillAuthorized` (`:87-93`) re-evaluates that captured decision against the section's
  **current** group set and returns `false` when nothing was captured or the authorizing group has
  since been removed.
- `ConferenceRoomBulkProcessor.cs:75-84` is the worked consumer:

  ```csharp
  var snapshot = JobAuthorizationSnapshot.FromJson(job.AuthSnapshotJson);
  var allowed = _sectionAccess.GetGroupsForSection(Section);
  if (snapshot is null || !snapshot.IsStillAuthorized(allowed))
  {
      Audit(job, actionAudit, target, success: false, ticket, "Authorization denied (captured snapshot lacks access).");
      return Failed(target, "Authorization denied.");
  }
  ```

  with `private const string Section = "ConferenceRooms";` at `:35` and the capture at
  `ConferenceRooms.razor:711-712`.

Applied here: `private const string Section = "MessageTraceSearch";` on the processor, and the same
`snapshot is null || !snapshot.IsStillAuthorized(allowed)` refusal; plus the matching
`JobAuthorizationSnapshot.Capture(user, "MessageTraceSearch", SectionAccess.GetGroupsForSection("MessageTraceSearch"))`
in `MessageTrace.razor`'s `OnInitializedAsync`, which requires injecting `SectionAccessService`
into the page as `ConferenceRooms.razor` does.

### Gating the rows is not enough: the completion path publishes a file on its own

**This is Revision 2's HIGH finding, and it is the most consequential thing in this document.** An
earlier draft put the refusal only in `ProcessRowAsync`. That produces a job in which every row is
denied, no Exchange call is made - **and a downloadable CSV of trace data is written anyway**. A
failed row is not a failed job in this runner, and the completion path never consults
authorization:

- `BulkJobService.ExecuteRowsAsync:464-466` ends
  `return cancelled ? (BulkJobStatus.Cancelled, "Cancelled by operator.") : (BulkJobStatus.Completed, null);`
  Per-row `Failed` outcomes are recorded (`:447-455`) and change nothing about the job status.
- `RunJobAsync:385-386` hands that status to `FinishAndNotify`, which persists it and then calls
  `NotifyCompletedInScope` -> `processor.OnJobCompletedAsync` (`:475-486`, `:510-524`).
- `MessageTraceDetailJobProcessor.OnJobCompletedAsync:104-110` **reconstructs a row from the
  payload whenever `_fetched` has none**:

  ```csharp
  details.Add(_fetched.TryGetValue(i, out var d)
      ? d
      : new MessageTraceDetail { Summary = messages[i], Error = "Not processed (job did not reach this message)." });
  ```

  and then calls `BuildCsv` (`:112`) and `SaveToLogPath` (`:117`) unconditionally.
- `MessageTraceDetailReport.BuildCsv:98-130` writes `Received`, `Backend`, `SenderAddress`,
  `RecipientAddress`, `Subject`, `Status`, `MessageId`, `MessageTraceId`, `Size`, `FromIP` and
  `ToIP` from `detail.Summary` **before** it reaches `FinalOutcome`/`OutcomeDetail` (`:124-126`),
  which are the only two columns the error touches. Eleven columns of trace data per message.
- `MessageTraceExportListing.ClassifyState:258-278` returns `NotProduced` only when the status is
  not `Completed`; a denied job is `Completed`, the file resolves, so the state is `Available`.
  `CanDownload => State == MessageTraceExportState.Available` (`:45`), and `TryDownloadAsync:182-196`
  hands back the bytes.
- `:131-132` also sends the "your export is ready" mail.

So the denial produced a file. This is Known Failure Class 2 (a loop whose per-item failures do not
fail the whole) and Class 3 (fail-closed authorization) in the same defect, and it is the shape
that ships looking correct: every row says Denied in the jobs panel while the export sits on the
reports page.

**Remedy: one preflight, consulted by both hooks.**

```csharp
private bool StillAuthorized(BulkJob job) =>
    JobAuthorizationSnapshot.FromJson(job.AuthSnapshotJson) is { } snapshot
    && snapshot.IsStillAuthorized(_sectionAccess.GetGroupsForSection(Section));
```

- `ProcessRowAsync` - first statements: refuse, audit, return a `Failed` row, before
  `_details.GetMessageDetailAsync`.
- `OnJobCompletedAsync` - **first statement, before `Deserialize`**: refuse, mark the job denied,
  audit, and `return`. No `BuildCsv`. No `SaveToLogPath`, so **no file is written anywhere**. No
  ready mail, and no failure mail either - `SendMessageTraceFailureAsync` says the export could not
  be *saved*, which is untrue here, and `ResolveRecipients` sends to operator-chosen addresses who
  may not hold the permission. Silence plus an audit row plus a visible Denied state on the reports
  page is the right notification, and it is a deliberate choice rather than an omission.
- `MessageTraceExportListing` - a `DeniedMarker` alongside `SaveFailedMarker` (`:101`), a new
  `MessageTraceExportState.Denied` member, a `ClassifyState` branch for it placed immediately after
  the `job.Status != Completed` check and **before** `SaveFailed`, and `Describe`/`ShortStatus`
  text (`:217-232`). Adding a member rather than reusing `Failed` is required by that file's own
  rule at `:253-257`: collapsing two causes lets one masquerade as the other. `CanDownload` needs
  no change - anything that is not `Available` is already refused.

**Two independent reasons a denied job is not downloadable, and neither depends on the other.**

**First, correct an error an earlier revision of this section carried.** `_jobs.AppendJobMessage`
is **not** fail-safe and does **not** swallow its errors. `BulkJobService.AppendJobMessage:236` is a
bare delegation - `=> _repository.AppendMessage(jobId, note)` - and `BulkJobRepository
.AppendMessage:221-237` opens a connection and runs the UPDATE with **no catch anywhere**. The
swallowing lives one level up, in the *caller*: `MarkSaveFailed:164-174` wraps its
`AppendJobMessage` call in its own try/catch and logs. The fail-safety is a property of that
wrapper, not of the API, and reading it as an API property is how this sentence went wrong.

**So the denial marker must be written through a `MarkDenied` helper mirroring
`MarkSaveFailed:164-174`, try/catch and all.** Implemented as a bare call instead, a repository
exception propagates out of `OnJobCompletedAsync` before the denial audit and the marker are
written. The exception itself is contained - `BulkJobService:515-523` swallows completion-hook
exceptions, and the terminal state is already committed - and because the marker write is ordered
*before* `SaveToLogPath`, no file is written and nothing becomes downloadable. But the denial audit
would be missing and the row would render `Expired` rather than `Denied`, so the durable record of
*why* it was refused is lost. That is the whole accountability mechanism for this feature.

With the helper in place: if the marker write fails, no file was written either, so
`_store.TryResolve` finds nothing and `ClassifyState` returns `Expired` - mislabelled, still not
downloadable. If a future edit re-introduced the file write, the marker still forces `Denied`.
Slice 4 carries a marker-write-failure test asserting no throw, no file, no mail and no
downloadable export.

**Scoped variation from the review's wording, stated rather than smuggled.** The review asked for
authorization denial to be "a job-level terminal outcome". It is job-level here - one preflight,
one job-level marker, one job-level report state - but the runner's own terminal *status* stays
`Completed`. It cannot be changed from the completion hook: by the time `OnJobCompletedAsync` runs,
`FinishAndNotify` has already persisted the terminal state through a compare-and-swap from a
non-terminal status, which `MarkSaveFailed:158-161` records as the reason it uses
`AppendJobMessage` rather than `TryFinish`. Making denial a first-class terminal status would mean
changing `BulkJobService` for every module - shared infrastructure, a base app version bump, and
outside this plan's scope. Recorded in Known gaps as the cleaner home if the owner ever wants it.

### What a refused job looks like end to end

Correcting this plan's earlier claim that refusing a snapshot-less job "needs no extra code". That
was true at the row level and **false at the job level**, which is exactly how the defect above got
past the first two drafts. The end-to-end behaviour after slice 4:

1. The runner starts the job normally; `CountRows` parses the payload and `TryStart` succeeds.
2. Every row is refused before any Exchange call. `_fetched` stays empty. Each refusal is audited.
3. `ExecuteRowsAsync` returns `Completed` - unchanged, and now harmless.
4. `OnJobCompletedAsync` refuses on the same preflight and returns immediately.
5. **No file is written. No mail is sent.** One job-level denial audit row is written.
6. The reports page lists the job with state **Denied**, `CanDownload` false, and
   `TryDownloadAsync` refuses with the Denied text; the refusal is still audited by the page.
7. The jobs panel shows the job as Completed with every row Failed. Cosmetically imperfect, see
   Known gaps.

Files written: **none**. That is the property the behavioural tests assert.

Honest scope, inherited from the mechanism and worth restating rather than overselling: this
re-checks the captured decision against the section's current group set. It does **not** detect
that the operator was removed from the group itself mid-flight - `JobAuthorizationSnapshot.cs:17-20`
says so, and that is parity with the live model, not a regression introduced here.

## 4. The UI for a header-only operator

**The Trace Search tab is NOT RENDERED for an operator without the granular permission.** Owner
ruling 2026-09-22, `.agents/decisions.md`; it overrules this section's original design, which
disabled the tab and named the missing permission on it. That argument rested on this page's
comment at `:397-399` about a disabled button stating its reason - a rule about a control refusing
a click, not about a capability never granted. `Components/Pages/Home.razor:53-65` is the closer
precedent and it hides: each module card sits inside an `AuthorizeView` on the module's alias.

Concretely: wrap the `<li>` at `:31-33` so it renders only when `canTrace`. No accompanying
explanatory line - hiding it and then describing it defeats the ruling. The headers tab, the
upload control, the paste box and the analysis output are untouched.

**The default tab already survives this.** `activeTab` initialises to `"headers"` (`:600`), so a
header-only operator lands on a tab that exists.

**But hiding the tab does not close the route to the trace panel, and this is the slice's real
work.** `:878` sets `activeTab = "trace"` programmatically from the header-analysis handoff, and
with `runNow` it calls `RunTrace()` immediately. So:

1. The handoff control that reaches `:878` is hidden on the same `canTrace` condition.
2. The method containing `:878` returns without switching tabs when `!canTrace`, so no future
   caller can re-open the panel by accident.
3. `RunTrace` refuses server-side regardless, per section 3. **Hiding is presentation; the gate is
   the enforcement point.** A reviewer should treat any argument of the form "the operator cannot
   reach it because the tab is hidden" as unsound.

## 5. Route compatibility

**Both routes stay on the same component with the same main policy, and they cannot diverge
without splitting the component.** `@attribute [Authorize]` in Blazor is component-level metadata,
not per-`@page` metadata; `MessageTrace.razor:1-2` declares two `@page` directives over one
component, so there is exactly one policy for both. Making `/message-trace` behave differently
would mean a second component, which buys nothing: the capability split is inside the page, not at
the URL.

`ExchangeAdminWeb.Tests/MessageTracePageRoutingTests.cs` pins the absence of the retired
historical-search path and needs no change for that purpose. It does gain one assertion, because
the routing file is where a future agent looking at "the page's routes and policy" will look: the
page must still declare both `@page` directives **and** exactly one
`[Authorize(Policy = "MessageTrace")]` attribute. That guards the specific regression of someone
"completing" the split by promoting the page attribute to `MessageTraceSearch`, which would deny
header analysis to everyone without trace and would leave every other test in this plan passing.

## 6. Config surface

**No new code. The alias appears automatically, and this is verified, not assumed.**

`Components/Pages/ModuleConfig.razor:910-912` builds the per-module Access tab's alias list
straight from the descriptor:

```csharp
policyAliases = new List<string> { module.MainPermission.PolicyAlias };
foreach (var gp in module.GranularPermissions)
    policyAliases.Add(gp.PolicyAlias);
```

The tab iterates that list (`:174`) and renders each alias with its `Description` via
`PermissionDescriptionFor` (`:187`, `:766-775`), which resolves granulars as well as mains.
`ModuleCatalog.GetConfigurablePolicyAliases()` (`:42-56`) also picks it up for the seeding path at
`ModuleConfig.razor:1201`, and the save path writes the whole map back through
`SectionAccess.SaveSectionAccess` so merging one module's grants cannot erase another's
(the comment at `ModuleConfig.razor:914-918` explains why that whole-map read-merge-write is
load-bearing).

The single precondition, already stated: the descriptor must be **deployed** before the alias is
grantable. That is what slice 1 is for.

## 7. Versioning

**Module-scoped. Bump `MessageTrace`'s `Version` in `Modules/ModuleCatalog.cs` by one MINOR above
whatever it holds at implementation time** - settled 2026-09-22, see open question 6 - so take
current, add one in the minor position and reset the patch position, rather than writing a
literal; another agent has been moving that same field. Add a dated comment above
the version in the catalog's house style explaining the split, as the `BlockedSenders` and
`GroupManagement` entries do.

**No base app version bump.** `docs/ProjectConstitution.md:112-113` draws the line at shared
infrastructure versus module-scoped behaviour. Everything this plan changes is module-scoped: the
descriptor entry, that module's two page components, and that module's tests. The policy is
generated by `ConfigureAuthorizationPolicies`, which is shared machinery - but it is not
*modified*; it already builds a two-requirement policy for any declared granular
(`ModuleCatalog.cs:108-116`), and adding a granular exercises that path rather than changing it.
`ExchangeAdminWeb.csproj` is not touched, so `BuildInfo` and the sidebar version are unaffected.
(The Migration button gate took the minor position for a comparable behavioural change. That
precedent is now the decision rather than an aside - see open question 6.)

## Slices

Four sessions, four commits, each with its own guard proof. **The owner's configuration step
between slice 1 and slice 2 is mandatory, not a convenience.**

### Why this order, and why no boundary leaks

The property to hold is not "each slice is independently landable" - an earlier draft claimed that
and it was false. It is: **no slice boundary opens an exposure that does not already exist at
HEAD, and the last slice closes the one that does.** Checked at each boundary:

| after | state | exposure vs HEAD |
| --- | --- | --- |
| 1 | alias declared, nothing consults it | none; nothing can be denied or permitted differently |
| 2 | jobs carry a snapshot nobody reads | none; strictly additive data |
| 3 | both pages enforce, together | none. This is the boundary the review caught: enforcing the main page without the reports page would shut the front door and leave `/message-analysis/reports` open, where `GetExports()` lists every operator's exports with no submitter filter. They land in one commit for that reason. |
| 4 | the queued job enforces too | none; the pre-split job exposure is closed |

The one exposure that persists between slices 1 and 4 - a queued job running without an
authorization re-check - is **today's behaviour at HEAD**, not something this plan creates. Slice 4
ends it.

Ordering the reports page first instead would also be leak-free (it over-restricts rather than
under-restricts during the gap), but it would break reports for anyone the owner has not yet
granted while trace still works, which is a worse operator experience for no safety gain. One
enforcement commit covering both pages is the choice.

### Slice 1 - declare the permission (inert)

1. `Modules/ModuleCatalog.cs`: add `GranularPermissions = [ new("Search", "MessageTraceSearch", "<description>", FailClosed: true) ]` to the `MessageTrace` descriptor (`:256-276`).
2. Rewrite the main permission's description so it no longer claims to grant trace.
3. Bump the module version (current + 1 in the MINOR position, patch reset to 0 - see section 7)
   and add the dated comment.
4. `ExchangeAdminWeb.Tests/ModuleCatalogTests.cs`:
   - new `Catalog_MessageTrace_HasFailClosedSearchGranular`, mirroring
     `Catalog_RiskyUsers_HasFailClosedRemediateGranular` (`:50-57`).
   - new `Catalog_MessageTrace_PolicyAliasesAreConfigurable`, mirroring `:59-65`.
   - `Catalog_GetConfigurablePolicyAliases_MatchesExpected` (`:262-312`): add
     `Assert.Contains("MessageTraceSearch", aliases)` and change the count assertion at `:311`
     from 41 to 42.
   - `Catalog_PolicyAliases_AreUnaffectedByOrdering` (`:413-459`): add `"MessageTraceSearch"` to
     the literal `expected` set at `:428`. This is an exact-set comparison and will fail
     otherwise.
5. Guard proof: delete the granular from the descriptor, confirm the two new tests and both
   catalog set/count tests fail, restore byte-identically.

**Nothing in this slice can deny anyone anything.** No component consults `MessageTraceSearch`
yet. Deploy it, then have the owner configure the section-access group (manual checklist step 1)
before starting slice 2.

### Slice 2 - capture the job authorization snapshot (inert)

6. `Components/Pages/MessageTrace.razor`: inject `SectionAccessService`; capture
   `authSnapshot = JobAuthorizationSnapshot.Capture(user, "MessageTraceSearch", SectionAccess.GetGroupsForSection("MessageTraceSearch"))`
   in `OnInitializedAsync`, following `ConferenceRooms.razor:711-712`; set
   `AuthSnapshotJson = authSnapshot?.ToJson()` on the `BulkJob` built in `EmailSelectedDetails`
   (`:1157-1170`).
7. A tripwire that the enqueue sets `AuthSnapshotJson`, and a test that `Capture` is called with
   the `MessageTraceSearch` section.
8. Guard proof: drop the `AuthSnapshotJson` assignment, confirm the tripwire fails, restore.

Nothing reads the field yet, so this changes no behaviour. It lands separately from slice 4 so
that by the time the processor starts refusing snapshot-less jobs, every job enqueued since this
deploy already has one - shrinking the set of jobs that get refused to those queued before this
slice rather than those queued before slice 4.

### Slice 3 - enforce, both pages, one commit

9. `Components/Pages/MessageTrace.razor`: `canTrace` field and resolution; the handler gates and
   the render condition from the table in section 3. Every denial path audits and returns without
   touching Exchange.
10. `Components/Pages/MessageTraceReports.razor`: attribute, `OnInitializedAsync` check and the
    `Download` re-check, all on `MessageTraceSearch`.
11. Tripwires in `ExchangeAdminWeb.Tests/PageAuthorizationRecheckTests.cs` and the `[Authorize]`
    pin in `ExchangeAdminWeb.Tests/MessageTracePageRoutingTests.cs` (section "Tests").
12. Guard proof, five mutations, each restored byte-identically and each expected to fail exactly
    its named assertion and nothing else:
    - **removed gate** - delete the whole re-check from `RunTrace`.
    - **removed gate** - delete the whole re-check from `MessageTraceReports.Download`.
    - **ignored result** - keep the `AuthorizeAsync` call in `ToggleDetail` but delete the
      `if (!auth.Succeeded) { ... return; }` branch. This is the mutation the review named: the
      earlier presence-only assertions would all have passed it.
    - **late check** - move `ExportCsv`'s gate below the CSV build loop. The earlier draft had no
      ordering assertion for this method at all, so this mutation would have passed.
    - **conditional-return fall-through** - rewrite one handler's deny block as
      `if (!auth.Succeeded) { if (someFlag) return; }`. Revision 2 showed the earlier assertions
      accepted this; assertion 3 must reject it.
    - **string-brace overcapture** - put a `}` inside a string literal in one deny block, for
      example an audit message containing `"denied {reason}"` written with a literal brace. A
      non-quote-aware extractor mis-scans this; the test-local quote-aware scanner must not, and
      the assertion must still pass on correct code. This mutation is a *negative* proof: the suite
      stays green.
    - **over-gating** - add the `MessageTraceSearch` check to `AnalyzeHeadersAsync`, which must
      fail `MessageTrace_HeaderAnalysisIsNotGatedOnTheSearchGranular`.
13. Separately confirm the existing `ClickGateStuckFlagTests` `ToggleDetail_*` and `RunTrace_*`
    assertions still pass with the gates in place - that is the collision this plan's placement
    rule exists to avoid, and it is worth proving rather than asserting.

**Both pages in one commit is deliberate and is the review's HIGH finding.** They are one fix -
one capability boundary - and splitting them puts a deploy between shutting the front door and
shutting the back one.

### Slice 4 - enforce in the background job processor

14. `Services/Jobs/MessageTraceDetailJobProcessor.cs`: inject `SectionAccessService`, add
    `private const string Section = "MessageTraceSearch";` and the single `StillAuthorized(job)`
    preflight, mirroring `ConferenceRoomBulkProcessor.cs:75-84`.
15. Call it from **both** hooks: `ProcessRowAsync`, before `_details.GetMessageDetailAsync` (audit,
    return a `Failed` row); and `OnJobCompletedAsync`, as its first statement, returning before
    `Deserialize`, `BuildCsv`, `SaveToLogPath` and every mail. One private method, two call sites -
    a second copy of the predicate is how the two hooks drift apart.
16. `Services/MessageTraceExportListing.cs`: `DeniedMarker`, a `MessageTraceExportState.Denied`
    member, the `ClassifyState` branch immediately after the `job.Status != Completed` check and
    before `SaveFailed`, and the `Describe`/`ShortStatus` text.
17. **Behavioural** tests, not tripwires: the processor is a plain class and is unit-testable with
    NSubstitute, as `ConferenceRoomBulkProcessorTests.cs:157` already does for the precedent. The
    completion-path tests assert **no file exists** afterwards, against a real temp directory, not
    merely that a substitute was not called.
18. Guard proof, four mutations, each restored byte-identically:
    - invert the fail-closed default: `snapshot is null ||` becomes `snapshot is not null &&`;
      the null-snapshot row test must fail.
    - delete the `OnJobCompletedAsync` preflight while keeping the row one. **This is the
      Revision 2 defect exactly**, and it must fail the no-file test. If it does not, the tests are
      testing the row gate twice and the whole slice is decorative.
    - delete the row preflight while keeping the completion one; the never-called-Exchange test
      must fail.
    - remove the `Denied` branch from `ClassifyState`; the non-downloadable listing test must fail.

This is the one slice whose gate is proven by behaviour rather than by string matching, because
the code under test is reachable without a rendered page. Mutation 2 is the reason the slice is
worth its session: it is the only check in this plan that would have caught the defect the second
review found.

## Tests

Two kinds, and the difference is worth stating because the review turned on it. The page gates are
**source-level tripwires**: there is no bUnit harness in this repo, so nothing can render either
page and these read the page as text. The limitation is inherited and is already recorded in
`docs/MigrationStaleReport-Plan.md` and `MessageTracePageRoutingTests.cs:9-22`. The background-job
gate is **behavioural**: the processor is an ordinary class with injectable dependencies, so that
one gate is executed rather than read.

**Use `ClickGateSource` for the scanning, not `PageAuthorizationRecheckTests.GetMethodBody`.** That
local helper's signature regex is `private\s+async\s+Task(<[^>]+>)?\s+{name}\s*\(`
(`PageAuthorizationRecheckTests.cs:133-134`), which cannot find `EmailSelectedDetails` (declared
`private Task`, `:1106`) before slice 3 converts it, cannot find `ShowTraceTab` (expression-bodied
`private void`, `:649`) at all, and does not blank comments - a scanner in this repo that does not
strip comments has already produced two false failures (`.agents/state.md`, Migration entry).
`ExchangeAdminWeb.Tests/ClickGateSource.cs` handles all three: `MethodBody` (`:181-183`) accepts
any accessibility, optional `async` and any return type, `MemberBody` (`:186-187`) handles
expression-bodied members, and `Text` is comment-blanked by `BlankComments` (`:54-64`). Either
extend the local helper to delegate to it or call `ClickGateSource` directly; do not add a third
scanner.

### Assert the denial branch, never the present call

The review's MEDIUM finding, and it is the `msr-1` lesson again: a tripwire that asserts
`AuthorizeAsync(... "MessageTraceSearch" ...)` appears in a method is satisfied by a handler that
awaits the check, throws the answer away and carries on. The earlier draft of this section did
exactly that, and for `ExportCsv` it did not even assert ordering, so a check placed after
`JS.InvokeVoidAsync("downloadFile", ...)` (`:956`) would have passed while the whole result set
shipped.

Revision 2 found that the first attempt at this was still too weak. Three assertions - assigned
call, a brace-balanced `if (!local.Succeeded)` block containing a `return`, and ordering before the
sink - are all satisfied by:

```csharp
if (!auth.Succeeded) { if (auditSucceeded) return; }
await MsgTrace.GetMessageDetailAsync(...);
```

The block contains a `return`; not every denied path takes it. So the assertion has to prove the
deny block **terminates unconditionally, and ends before the sink**, not that a `return` appears
somewhere inside it.

Every gated handler gets the same four assertions, expressed once as a shared helper and applied
per handler:

1. **the call** - the body matches `AuthorizeAsync\(\s*\w+(\.\w+)*\s*,\s*"MessageTraceSearch"\s*\)`
   and the result is assigned to a local; capture that local's name from the assignment rather
   than hard-coding it.
2. **the deny block** - the body contains `if (!<local>.Succeeded)` and the block it introduces is
   extracted by a **quote-aware** balanced scan that returns both offsets, not just the text.
3. **unconditional termination** - the block's **last statement** is `return;` or a `throw`, and no
   `if`, `switch`, `?:`, `&&`, `||` or `goto` appears between the block's opening brace and that
   terminator. A conditional return is not a gate.
4. **the ordering** - `index(call) < index(block start)` and `index(block end) < index(sink)`,
   where the sink is named per handler below. Asserting against the block *end* rather than its
   start is what stops a deny block that swallows the sink.

Assertions 3 and 4 are a canonical-shape pin rather than control-flow analysis: they refuse
anything that is not the plain guard this repo already writes (`BlockedSenders.razor:314-329`).
That is a deliberate trade - see "The robust option, costed" below.

| handler | protected sink asserted against |
| --- | --- |
| `RunTrace` | `RunRealtimeTrace()` |
| `ToggleDetail` | `GetMessageDetailAsync(` |
| `ExportCsv` | the **earlier** of `foreach (var msg in response.Results)` and `JS.InvokeVoidAsync(` - so a gate below the CSV build fails even though nothing has been handed to the browser yet |
| `DownloadSelectedDetails` | `GetMessageDetailAsync(` |
| `EmailSelectedDetails` | `BulkJobs.Enqueue(` |
| `MessageTraceReports.Download` | `TryDownloadAsync(` |

### External dependency: `ClickGateSource.ExtractBlock` is not quote-aware

**Do not fix this here.** `ExchangeAdminWeb.Tests/ClickGateSource.cs:213-232` counts braces with no
quote tracking:

```csharp
for (var i = open; i < source.Length; i++)
{
    if (source[i] == '{') depth++;
    else if (source[i] == '}' && --depth == 0)
        return source[open..(i + 1)];
}
```

A `{` or `}` inside a string literal or an interpolated string inside the deny block therefore
unbalances the count and the extractor over-captures into later code, where it can find a `return`
belonging to something else entirely. The asymmetry is visible in the same file: `Tags:94-128` **is**
quote-aware (`:107-118`) and its doc comment at `:88-93` explains exactly why it had to be;
`ExtractBlock` never got the same treatment. It also returns only the block text, not its end
offset, so assertion 4 cannot be written against it as it stands.

That file is committed infrastructure shared with the click-gating harness and is being edited by
another work stream, so this plan records the defect rather than repairing it. Until it is fixed,
the new tests use a small **test-local** quote-aware scanner returning `(start, end)`, declared in
the new test file and documented as temporary. When `ExtractBlock` is made quote-aware and returns
offsets, retire the local one in the same commit - this repo should not grow a third permanent
scanner.

### The robust option, costed

Assertions 3 and 4 pin a shape; they do not analyse control flow, and a handler written in some
other legitimate shape would fail them even though it is correct. The robust alternative is a
Roslyn assertion over the parsed method body: extract the `@code { ... }` block from the `.razor`
file, wrap it as a class body, parse with `Microsoft.CodeAnalysis.CSharp`, and assert that every
path from the `if (!auth.Succeeded)` condition reaches a `return`/`throw` without reaching the sink
invocation.

Cost: one new test-project package reference, one new technique nothing else in this repo uses, and
roughly one session to build and prove, plus ongoing maintenance of the `@code` extraction. Benefit:
real control-flow analysis for five page handlers - and none for the background job, which is
already proven behaviourally and is the more dangerous gate. Recommendation: ship the shape pin now
and treat Roslyn as a separate, separately-approved improvement to the shared harness, where it
would serve every page rather than this one. Open question 7.

### The tests

In `PageAuthorizationRecheckTests.cs`, under a new section header explaining that these gate a
READ behind a granular (Constitution `:29`, direct event invocation), not a write:

1. `MessageTrace_TraceHandlers_RefuseWhenTheSearchGranularIsDenied` - a `[Theory]` over the six
   handlers in the table above, applying all four assertions. This replaces both earlier forms; do
   not ship the presence-only version or the one that accepts a `return` anywhere in the block.
2. `MessageTrace_TabSwitchIsGated` - `ShowTraceTab` and `UseHeaderTraceSuggestion` both consult
   `canTrace` and return without setting `activeTab = "trace"` when it is false. Uses
   `MemberBody`/`MethodBody` as appropriate.
3. `MessageTrace_HeaderAnalysisIsNotGatedOnTheSearchGranular` - the counterweight, and the most
   important test here: `AnalyzePastedHeaders`, `AnalyzeUploadedFile` and `AnalyzeHeadersAsync`
   must **not** mention `MessageTraceSearch`. Without it, every other assertion in this file is
   satisfiable by gating the whole page, which is the failure this work exists to prevent.
4. `MessageTraceReports_PageIsGatedOnTheSearchGranular` - the page's `@attribute` and its
   `OnInitializedAsync` both name `MessageTraceSearch`, and neither names bare `"MessageTrace"`
   as the policy.
5. `MessageTrace_EmailSelectedDetails_AttachesTheAuthorizationSnapshot` (slice 2) - the `BulkJob`
   object initializer in `EmailSelectedDetails` sets `AuthSnapshotJson`, and `OnInitializedAsync`
   calls `JobAuthorizationSnapshot.Capture` with `"MessageTraceSearch"`. A job enqueued without a
   snapshot is refused by slice 4, so losing this line turns every export into a failure - the
   tripwire makes that a red suite instead of a support ticket.

In `MessageTracePageRoutingTests.cs`:

6. `Page_KeepsBothRoutesOnTheMainPolicy` - the source contains `@page "/message-analysis"`,
   `@page "/message-trace"` and exactly one `[Authorize(Policy = "MessageTrace")]`, and does
   **not** contain `[Authorize(Policy = "MessageTraceSearch")]`. The regression it refuses is
   promoting the page attribute to the granular, which locks header-only operators out of the
   module entirely and would leave tests 1-5 green.

New behavioural tests for slice 4, in a new `MessageTraceDetailJobAuthorizationTests.cs`, built
the way `ConferenceRoomBulkProcessorTests.cs:157` builds its snapshot:

7. `ProcessRow_RefusesWhenTheJobCarriesNoSnapshot` - `AuthSnapshotJson` null; assert the row comes
   back `Failed`, the denial is audited, and `GetMessageDetailAsync` was **never called** on the
   substitute. The never-called assertion is the real content; a `Failed` row alone would also be
   produced by a fetch that failed.
8. `ProcessRow_RefusesWhenTheCapturedGroupIsNoLongerGranted` - a valid snapshot whose
   `AuthorizedGroups` no longer intersects `GetGroupsForSection("MessageTraceSearch")`; same
   assertions.
9. `ProcessRow_ProceedsWhenTheSnapshotIsStillValid` - the counterweight, so 7 and 8 cannot be
   satisfied by a processor that refuses everything.

The completion path, which is where Revision 2's defect lived. These run against a **real temp
export directory** so "no file" is a filesystem fact, not a substitute's call count:

10. `Completion_WritesNoFileWhenTheJobCarriesNoSnapshot` - run `OnJobCompletedAsync` with a null
    `AuthSnapshotJson` and assert the export directory is **empty afterwards**, no
    `SendMessageTraceResultAsync` call, no `SendMessageTraceFailureAsync` call, and a denial audit
    row. Asserting on the directory rather than on a mock is deliberate: a mocked store would pass
    even if the processor wrote the file with `File.WriteAllText`, which is what `SaveToLogPath:190-196`
    actually does.
11. `Completion_WritesNoFileWhenTheCapturedGroupIsNoLongerGranted` - same, stale snapshot.
12. `Completion_StillProducesTheExportWhenTheSnapshotIsValid` - the counterweight: a file exists and
    the ready mail is sent.
13. `Completion_MarksTheJobDeniedSoTheReportIsNotDownloadable` - the denial marker is appended to
    the job record.

And in `MessageTraceExportListingTests.cs`:

14. `ClassifyState_ReturnsDeniedForAJobMarkedDenied`, and `CanDownload` is false for it.
15. `TryDownloadAsync_RefusesADeniedExport` - even given a job id, with a valid ticket, and even if
    a file were somehow present at the resolved path. Construct that case explicitly: write a file
    at the store path, mark the job denied, and assert the download is still refused. This is the
    defence-in-depth claim, and it is worth one test rather than a sentence.

These nine are stronger than anything else in this plan: they execute the gate rather than read it.
That is the whole reason the job slice is worth having rather than deferring - and tests 10 and 11
are the only checks anywhere in this document that would have caught the Revision 2 defect.

### The cheapest broken implementation that still passes each assertion

The discipline that would have caught both review rounds, written down so the next reader applies
it before shipping. For every assertion, what is the laziest wrong code that satisfies it, and what
covers that gap:

| assertion | cheapest thing that still passes | covered by |
| --- | --- | --- |
| 1 (the call) | a handler that awaits the check and ignores the answer | assertions 2-4 |
| 2 (deny block present) | `if (!auth.Succeeded) { _logger.LogWarning("denied"); }` - falls through | assertion 3 |
| 3 (unconditional terminator) | a correct guard that then leaks via a *second*, ungated path into the same sink elsewhere in the method | nothing here. Single-sink handlers only; a handler with two sinks needs its own assertion. Recorded in Known gaps. |
| 4 (ordering) | a guard whose block is correct but which sits after an *earlier* partial disclosure the sink list does not name | the per-handler sink list, which is hand-maintained. `ExportCsv` has two sinks for this reason; a new sink in a new handler is an unforced error. |
| 5 (tab switch gated) | `canTrace` consulted for rendering only, with `ShowTraceTab` still assigning | assert the assignment is inside the guarded branch, not merely that `canTrace` is mentioned |
| 6 (header analysis not gated) | deleting header analysis entirely | manual checklist item 4 |
| 7-9 (row gate) | a processor that refuses every row unconditionally | test 9, the counterweight |
| 10-13 (completion gate) | a processor that never writes a file at all | test 12, the counterweight |
| 14-15 (listing) | `CanDownload` hard-coded false | the existing `MessageTraceExportListingTests` Available cases |

Two of these have no automated cover and are listed in Known gaps rather than papered over. That is
the point of the table.

Behavioural coverage for the authorization decision itself already exists and is not duplicated
here: `GroupAuthorizationHandlerTests` covers the handler, `JobAuthorizationSnapshotTests` covers
`Capture`/`IsStillAuthorized`, and `ModuleCatalogTests` covers policy generation. The page-level
tripwires assert only wiring, the deny block's shape and ordering, which those cannot see.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`

No PowerShell changes, so PSScriptAnalyzer and Pester do not apply; say so rather than leaving it
unstated.

## Manual acceptance checklist

Needs a dev deploy. Nothing in this repo reaches the rendered page.

**Step 1 happens after slice 1 is deployed and before slice 2 is deployed. Do not reorder it.**

- [ ] **1. Configure the grant.** Open Module Config for Message Analysis, confirm a new
      `MessageTraceSearch` entry appears on the Access tab with its description, add the group(s)
      that should keep trace search, save, and confirm the grant reads back after a page reload.
      Until this is done, deploying slice 3 denies trace to everyone (recoverable in-app by a
      global admin, but avoidable entirely by doing this first). **Store a SID, not a group name:**
      `JobAuthorizationSnapshot.Capture` skips any non-SID value outright
      (`JobAuthorizationSnapshot.cs:68-69`), so a name-form grant would capture nothing and every
      background export would fail closed at slice 4. The startup SID migration
      (`Program.cs:292-294`) normally handles this; confirm it rather than assume it.
- [ ] 2. As an operator in both the main and the Search groups: the Trace Search tab is enabled,
      a search returns results, a row expands to its detail trail, Export CSV downloads, Download
      details downloads, and Export details as a job enqueues.
- [ ] 3. As that same operator: `/message-analysis/reports` lists exports and a download
      succeeds.
- [ ] 4. As an operator in the main group only (remove them from the Search group, or use a test
      account): the module still appears in the sidebar, the page still opens, the Header Analysis
      tab works end to end for both a pasted header block and an uploaded `.eml`, and the analysis
      output renders.
- [ ] 5. As that same operator: the Trace Search tab is visibly disabled, clicking it does
      nothing, and the page states which permission is missing.
- [ ] 6. As that same operator: navigating directly to `/message-analysis/reports` lands on
      access-denied, not on the export list.
- [ ] 7. As that same operator: navigating directly to `/message-trace` opens the same page in the
      same header-only state (the legacy route must not be a way around the split).
- [ ] 8. Remove an operator from the Search group while they have the trace tab open, then have
      them click Search and expand a detail row. Both must be refused rather than executed - this
      is what the handler re-checks exist for, and it is the only way to observe them.
- [ ] 9. Check the audit log: a denied trace attempt is recorded, and a successful header analysis
      is still recorded as `MessageHeaderAnalysis`.
- [ ] 10. After slice 4: submit a detail export as a fully entitled operator and confirm it still
      completes and appears on the reports page. This is the counterweight - a snapshot check that
      refuses everything would satisfy every other check on this list.
- [ ] 11. After slice 4: submit a detail export, then remove the Search grant from the section
      before the job runs (pause the runner or use a large selection to widen the window). The job
      must fail with an authorization denial rather than produce an export.
- [ ] 12. After slice 4: confirm any export job that was already queued before slice 2 shipped is
      refused. Expected and correct; listed so it is recognised as the designed behaviour and not
      reported as a regression. **What to look for, precisely** (this is the Revision 2 defect, and
      "the rows said Denied" is not enough): the reports page shows the job as **Denied**, the
      Download button is disabled, clicking through is refused, **no file exists** in the export
      directory for that job id, and no "your export is ready" mail arrived.
- [ ] 13. After slice 4: on the jobs panel, the denied job reads Completed with every row Failed.
      Confirm that is what is seen and accept it; the runner's terminal status is deliberately not
      changed by this plan (Known gaps).

## Known gaps

- **A denied job still reports `Completed` in the runner's own status model.** Authorization
  denial is not a first-class terminal status: `OnJobCompletedAsync` runs after `FinishAndNotify`
  has already committed the terminal state through a compare-and-swap
  (`MessageTraceDetailJobProcessor.cs:158-161` records why), so the module can mark and classify
  the job but not restate its status. Making denial a runner-level terminal outcome would change
  `BulkJobService` for every module - shared infrastructure, a base app version bump, and outside
  this plan. That is the cleaner home if the owner ever wants it; the jobs panel reading
  "Completed, all rows Failed" is the visible cost until then.
- **`ClickGateSource.ExtractBlock` is not quote-aware** (`ExchangeAdminWeb.Tests/ClickGateSource.cs:213-232`),
  while `Tags:94-128` in the same file is. Recorded here as a dependency for the coordinator to
  schedule, not fixed by this plan - that file is shared with the click-gating harness and is being
  edited by another work stream. The new tests carry a temporary test-local scanner instead; retire
  it when the shared one is fixed.
- **Assertion 3 cannot see a second, ungated path to the same sink** inside one handler, and
  assertion 4 only knows the sinks it is told about. Both are hand-maintained lists. Every handler
  gated here has exactly one sink today; a future handler with two needs its own assertion. Named
  in "The cheapest broken implementation" table rather than left implicit.
- **Mid-flight group-membership revocation is still undetected for a running job.** Slice 4
  re-checks the captured decision against the section's current group set, so removing the group
  from `MessageTraceSearch` stops the job; removing the *operator* from a group that is still
  granted does not. `JobAuthorizationSnapshot.cs:17-20` states this limit for the mechanism as a
  whole, and it is parity with the live one-check-per-loop model rather than something introduced
  here. Closing it would need a SAM-to-groups directory lookup the app deliberately does not have.
- **A name-form section-access grant silently disables background exports.**
  `JobAuthorizationSnapshot.Capture` skips non-SID values (`:68-69`), so a `MessageTraceSearch`
  row holding `DOMAIN\Group` captures an empty `AuthorizedGroups` and every job fails closed at
  slice 4 while the interactive page keeps working. Fail-closed and therefore safe, but confusing.
  Checklist step 1 calls it out.
- **The exports listing remains unscoped by submitter** even after slice 3. Every holder of
  `MessageTraceSearch` still sees and can download every other Search holder's exports. That is
  the current design and this plan does not change it; it only stops non-Search holders reaching
  it.
- **The main alias is now misleadingly named.** `MessageTrace` is the permission that does *not*
  grant trace. Renaming it would orphan the stored section-access row and lock everyone out of the
  module, so the descriptions carry the meaning instead. An admin reading only alias names in the
  Access tab could still get this wrong.
- **Tripwires are text analysis of C# inside `.razor` files.** They can be defeated by unusual
  formatting. They prove the call, the denial branch and the ordering; they do not prove the gate
  decides correctly. Only the slice 4 processor tests execute a gate.
- **No test in this repo can render either page**, so items 2-13 of the manual checklist are the
  only evidence that an operator experiences the split.

## Open questions

Each answerable in one line. **Questions 1 and 2 were ANSWERED by the owner on 2026-09-21;
the ruling is in `.agents/decisions.md`, "Owner answers on queue items 4, 5 and 8", which is
the canonical record. 3 to 7 remain open.**

1. **ANSWERED 2026-09-21: RE-GRANT DELIBERATELY - do not copy the existing group across.** Nobody
   can trace until the owner adds them to the new group, and that is an outage from the moment the
   enforcement slice deploys; the owner accepted that cost with it stated. The three-commit shape
   with a mandatory deploy boundary is therefore load-bearing, not ceremony. This plan seeds
   nothing in code either way, so no step below changes; checklist step 1 is now a deliberate
   re-grant rather than a copy.
   *(Original question: should the group(s) currently granted `MessageTrace` be copied onto
   `MessageTraceSearch` so today's operators keep trace search, or should trace be re-granted
   deliberately, group by group, as a fresh decision?)*
2. **ANSWERED 2026-09-21: `MessageTraceSearch`.** The same ruling names the alias in the owner's
   accepted text, which is the recommended option; `MessageTraceTrace` is dropped.
   *(Original question: alias name - `MessageTraceSearch` (recommended) or `MessageTraceTrace`?)*
3. **SETTLED 2026-09-22 as the coder's call, not the owner's: the reports route MOVES to the
   granular.** It lists trace detail exports, which is the half of this page the split exists to
   restrict; leaving it on the main permission would hand header-only operators the very data the
   granular protects. The Constitution's fail-closed rule and the purpose of the split decide this
   between them, so it was applied rather than asked.
   *(Original question: confirm `/message-analysis/reports` moves to the granular (recommended),
   rather than staying on the main permission.)*
4. **ANSWERED 2026-09-22: HIDE the tab**, overruling this plan's recommendation. Section 4 is
   rewritten to match and carries the consequence the disabled design did not have: the
   header-analysis handoff at `MessageTrace.razor:878` switches to the trace tab in code, so
   hiding the tab alone leaves that route open.
   *(Original question: confirm the Trace Search tab renders disabled with the reason stated
   (recommended) rather than being hidden.)*
5. **SETTLED 2026-09-22 as the coder's call: `ExportCsv` IS gated.** It writes the whole result
   set rather than the 50 rendered rows. The "they already ran the trace" argument fails under the
   ruling on question 4 and section 4's handoff finding - an operator can reach populated trace
   state without holding the permission, and an ungated export would then serve it.
   *(Original question: should `ExportCsv` be gated (recommended), or left ungated because the
   operator already ran the trace that produced it?)*
6. **SETTLED 2026-09-22 as the coder's call: MINOR, not the patch this plan was written with.**
   Section 7 is updated. The Constitution (`docs/ProjectConstitution.md:112-113`) says only that a
   module-scoped behavioural change bumps the module's version; it does not choose the position,
   so the tie-break is house precedent, and the Migration button gate took the minor position for
   a comparable behavioural change. A permission that can lock every operator out of half a page
   is not a patch.
   *(Original question: module version - patch (as written) or minor?)*
7. **SETTLED 2026-09-22 as the coder's call: ship the canonical-shape pin now.** The Roslyn
   control-flow assertion becomes a separate improvement to the shared harness, which is where it
   belongs - it is a property of the test harness every module uses, not of this module. Cost of
   the alternative is in "The robust option, costed". Note the related, already-recorded defect in
   `ClickGateSource.ExtractBlock`, which this work stream does not fix.
   *(Original question: ship the canonical-shape pin now and treat Roslyn as a separate
   improvement (recommended), or pay for Roslyn in this work stream?)*

Withdrawn in Revision 1: "is closing the background job's authorization gap in scope?" It is not a
judgment call. `AuthSnapshotJson`, `JobAuthorizationSnapshot` and the `ConferenceRoomBulkProcessor`
refusal already exist as the house pattern, and fail-closed is a Constitution rule, so applying
them is the work rather than a question. It is slice 4.

## Provenance

Owner queue item 4, verbatim: "Break out permissions for message trace vs header analysis."
Recorded as not-yet-started in `.agents/state.md` under "Queue items not yet started, in the
owner's own words". No decision in `.agents/decisions.md` bears on the split; the 2026-09-02
ruling on operator-facing permission descriptions
(`ModuleCatalogTests.Catalog_EveryPermissionCarriesAnOperatorFacingDescription`) governs the two
descriptions this plan rewrites.

## Revision 1 - codex review

Reviewed 2026-09-18, before any implementation, against the plan as committed in `155eaf7`.

Reviewer: codex / `@azure-openai-eus2-global/gpt-5.5-dzs` / xhigh / standard. Capability proof
passed. Verdict: **`unsound`**, four findings.

Every finding was checked against the source before being accepted; none was taken on the
reviewer's word. **All four were confirmed and all four are accepted.** Nothing was rejected.

| # | severity | finding | verified by | disposition |
| --- | --- | --- | --- | --- |
| 1 | HIGH | The plan identified the reports leak and then put main-page enforcement in one slice and reports enforcement in the next, calling them separately landable. Deploying the first alone shuts the front door and leaves `/message-analysis/reports` open. | `MessageTraceReports.razor:4`, `:126`, `:143`, `:146-157`; `MessageTraceExportListing.cs:119-123`; `BulkJobRepository.cs:373-388` - the SQL filters on `module_id` and `job_type` only, with no submitter predicate anywhere in the chain | Accepted. Both pages now land in **one** commit (slice 3). The "separately landable" claim is deleted, and the Slices section opens with a per-boundary exposure table stating the real property: no boundary opens an exposure that does not already exist at HEAD. |
| 2 | HIGH | The background export job was left as an open question while the plan claimed full enforcement coverage. A queued job runs later under the shared Exchange credential with no re-check. | `MessageTraceDetailJobProcessor.ProcessRowAsync:76-85` goes straight to `_details.GetMessageDetailAsync`; `MessageTrace.razor:1157-1170` enqueues with no `AuthSnapshotJson`; the precedent exists at `BulkJobModels.cs:80`, `JobAuthorizationSnapshot.cs:49-93`, `ConferenceRoomBulkProcessor.cs:35`, `:75-84`, `ConferenceRooms.razor:711-712` | Accepted, and closed rather than deferred. New section "The background export job"; new slice 2 (capture, inert) and slice 4 (refuse, with three behavioural tests). Open question 6 withdrawn with its reasoning. Jobs already queued carry no snapshot and are refused - `FromJson` returns null for absent JSON (`:98-110`) and the guard treats null as denied, so this needs no extra code; stated explicitly and added to the manual checklist as item 12. **Corrected by Revision 2:** "no extra code" was true at the row level and false at the job level - a denied job still published a file. See Revision 2 finding 1. |
| 3 | MEDIUM | The proposed tripwires asserted that `AuthorizeAsync` appears, so a handler that ignores `Succeeded` passes them all. `ExportCsv` was presence-only, so a check placed after `JS.InvokeVoidAsync` would have satisfied it. | the draft's own test list; `ExportCsv:916-956` with the download at `:956` and the build loop at `:923` | Accepted - this is the `msr-1` shape, tripwires the defective code also satisfies, and the repo has been bitten by it once. New subsection "Assert the denial branch, never the present call": three assertions per handler (call, `if (!local.Succeeded)` with a balanced-block `return`, ordering) against a named protected sink per handler, including two sinks for `ExportCsv`. Guard proof extended from three removed-gate mutations to five, adding an ignored-result mutation and a late-check mutation. |
| 4 | LOW | The recovery analysis was too pessimistic: it said a combined deploy leaves no way back without hand-editing the database. | `ModuleConfig.razor:875-882` admits `AdminSettings` or module admin; `ModuleCatalog.cs:88-96` registers `AdminSettings` with a **static** `GroupAuthorizationRequirement(adminGroups, alias)`, which `GroupAuthorizationHandler:74-76` reads from `requirement.AllowedGroups` without touching the section store; aliases come from the running descriptor at `:910-912`; the save is the ordinary path at `:1197-1214` | Accepted. Rewritten as an avoidable **outage window**. Added the narrowing the reviewer did not mention: the section-access save re-checks `AdminSettings` specifically, so the repair needs a global admin, not a module admin. The slice-1 -> configure -> enforce ordering stands as the no-outage path. |

Upheld by the review and therefore not re-argued anywhere above: the fail-closed chain
(`ModuleCatalog.cs:112-116` -> `SectionAccessService.cs:64-71` -> `GroupAuthorizationHandler.cs:78-86`),
the module-only version bump, and the `ToggleDetail` re-check placement against all four
`ClickGateStuckFlagTests` assertions from `1daf247`.

Findings 1 and 2 are the ones that mattered. Both are the same mistake in two places: naming an
exposure correctly and then filing it somewhere that does not close it - one into a later slice,
one into an open question.

## Revision 2 - codex review, round 2

Reviewed 2026-09-18 against the plan as committed in `c3b4239`. Same reviewer and settings.
Verdict: **`unsound`**, two findings. Both verified against source; **both accepted**, one with a
scoped implementation variance that is stated rather than smuggled. Nothing rejected.

Closed and not revisited: the page/reports window, the recovery-claim framing, the module-only
version bump, environment neutrality, the module permission contract.

**Finding 1 (HIGH) - a fully denied job still completed and published a downloadable CSV.**
Revision 1 put the refusal only in `ProcessRowAsync`. Verified line by line that this leaves the
export intact: `BulkJobService.cs:447-455` records a `Failed` row and `:464-466` returns
`Completed` regardless; `RunJobAsync:385-386` -> `FinishAndNotify:475-486` -> `NotifyCompletedInScope:510-524`
fires the completion hook anyway; `MessageTraceDetailJobProcessor.cs:104-110` substitutes a
payload-backed `MessageTraceDetail` for every row `_fetched` lacks, then `:112` builds the CSV,
`:117` saves it and `:131-132` mails "ready"; `MessageTraceDetailReport.cs:98-130` writes eleven
summary columns before the error columns at `:124-126`; and
`MessageTraceExportListing.ClassifyState:258-278` returns `Available` for a `Completed` job whose
file resolves, which `:45` and `:182-196` turn into a download. Known Failure Class 2 and Class 3
in one defect.

Accepted in full. New subsection "Gating the rows is not enough: the completion path publishes a
file on its own"; one shared `StillAuthorized(job)` preflight consulted by both hooks; the
completion hook returns before `Deserialize`, `BuildCsv`, `SaveToLogPath` and every mail, so **no
file is written**; a new `DeniedMarker` and `MessageTraceExportState.Denied` make the report
non-downloadable, with a new member rather than a reuse of `Failed` because that file's own rule at
`:253-257` forbids collapsing two causes. Enforcement table gains points 17 and 18. Slice 4 gains
the completion refusal, the listing change, four guard mutations (including deleting the completion
preflight while keeping the row one - this exact defect), and six new behavioural tests that assert
**no file exists** against a real temp directory rather than a mock's call count.

*Scoped variance, and the reason:* the review asked for denial to be "a job-level terminal
outcome". It is job-level here - one preflight, one marker, one report state - but the runner's
terminal *status* stays `Completed`. It cannot be changed from the completion hook, because
`FinishAndNotify` has already committed it through a compare-and-swap; the codebase records this at
`MessageTraceDetailJobProcessor.cs:158-161` as the reason `MarkSaveFailed` uses `AppendJobMessage`
rather than `TryFinish`. Making denial a first-class terminal status means editing `BulkJobService`
for every module, which is shared infrastructure and would invalidate the module-only version bump
this same review confirmed as correct. Recorded in Known gaps as the cleaner home if the owner
wants it.

Also accepted: the instruction to re-examine "refused with no extra code". It was true at the row
level and false at the job level - precisely how this got past two drafts. A new subsection, "What
a refused job looks like end to end", states the seven-step behaviour and the headline: **files
written, none.**

**Finding 2 (MEDIUM) - the denial-branch assertion still accepted a conditional return.** Verified:
Revision 1's three assertions are all satisfied by
`if (!auth.Succeeded) { if (auditSucceeded) return; }` followed by the sink. Verified the second
half too: `ClickGateSource.cs:213-232` counts braces with no quote tracking, so a brace inside a
string over-captures the block; `Tags:94-128` in the same file **is** quote-aware and `:88-93`
explains why, which makes the asymmetry an oversight rather than a decision. `ExtractBlock` also
returns only the block text, so an assertion on the block's *end* offset cannot be written against
it at all.

Accepted. Four assertions now, not three: assigned call; quote-aware balanced extraction returning
both offsets; **unconditional termination** (last statement is `return;`/`throw`, with no `if`,
`switch`, `?:`, `&&`, `||` or `goto` between the opening brace and it); and ordering against the
block **end**, not its start. Two guard mutations added - conditional-return fall-through, and a
string-brace overcapture that must leave the suite green.

Per instruction, `ExtractBlock` is **not** fixed here: recorded as an external dependency with file
and lines in Known gaps, with a temporary test-local quote-aware scanner in the meantime and an
explicit instruction to retire it when the shared one is repaired. The Roslyn option is costed in
"The robust option, costed" and raised as open question 7 rather than silently declined.

The lesson carried forward as a permanent section: "The cheapest broken implementation that still
passes each assertion", one row per assertion. Both review rounds found the same species of
mistake - an assertion satisfied by the defect it was written to catch - and that table is the
cheapest way to stop writing the third one.
## Revision 3 - codex round 3, CONSENSUS REACHED

Reviewer: codex / `@azure-openai-eus2-global/gpt-5.5-dzs` / xhigh / standard, 2026-09-18.
Capability proof passed. Verdict **sound_with_changes**, one LOW finding, now fixed above.

**Both round 2 findings confirmed closed.** The completion-hook gate before
`Deserialize`/`BuildCsv`/`SaveToLogPath` closes the CSV leak, and the `Denied` state keeps the
report non-downloadable. The four assertions were judged to meet the msr-1 standard for a source
scan, with behavioural tests on the job processor where that is practical.

**The scoped variance was upheld as sound engineering, not an under-fix.** `BulkJobService`
persists the terminal status before `OnJobCompletedAsync` runs, so making `Denied` a runner
terminal state would be a change to shared infrastructure and a base app version bump. Keeping the
runner status `Completed` while the denial is job-level in every effect that matters is the correct
trade, and the cosmetic inconsistency stays in Known gaps.

**The two admitted no-automation gaps were judged acceptable known gaps rather than blockers**,
because no current handler contains the extra sinks they describe and the plan names the
hand-maintained sink list as the risk. They stay recorded; they do not stop the work.

**The one finding (LOW)** was a false claim about existing code: the plan said `AppendJobMessage`
is fail-safe and swallows its errors. It is not - the swallowing is in `MarkSaveFailed`'s own
try/catch, and the API underneath runs uncaught SQL. Verified line by line before accepting.
Corrected above, with `MarkDenied` specified as a wrapper mirroring that precedent, because
otherwise a marker-write failure loses the denial audit - the accountability record this feature
exists to produce.

**This plan has reached codex consensus.** It stays `Status: Draft`: consensus is not approval, and
the owner still owns the seven open questions. Question 1 gates everything, because it decides
whether checklist step 1 copies the existing groups onto the new alias or re-grants deliberately,
and nothing can be implemented before slice 1 lands.
