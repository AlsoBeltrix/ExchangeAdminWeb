# Message Analysis: separate the trace-search grant from header analysis

Status: Draft. Queue item 4, in the owner's words: "Break out permissions for message trace vs
header analysis." Drafted 2026-09-18 against `337e07b`, reading the working tree (which carries
another agent's in-flight `ToggleDetail` change and a `MessageTrace` module version already at
1.4.2). Line numbers below are as of that working tree; every claim also names the method or the
exact string it rests on, so a shifted line number does not invalidate it.

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

**The remedy is ordering, and it is the reason this plan has three slices rather than one.**
Slice 1 declares the alias and changes no behaviour: nothing consults the new policy, so nothing
can be denied by it, but the alias immediately appears in Module Config's Access tab and becomes
grantable. The owner configures the group. Only then does slice 2, which enforces, ship. Because
dev and prod share one config database (`.agents/repo-guidance.md` Architectural Invariant 2),
configuring the group on dev makes it live on prod at the same moment, so the grant is in place
before prod ever sees the enforcing code.

The alias cannot be granted before slice 1 is deployed. `GetConfigurablePolicyAliases`
(`Modules/ModuleCatalog.cs:42-56`) and the per-module Access tab (`Components/Pages/ModuleConfig.razor:910-912`)
both derive the list from the catalog in the running binary. There is no way to pre-seed a row for
an alias the deployed code does not declare. Landing slices 1 and 2 in one deploy would therefore
produce a window in which trace is denied to everyone and nobody can turn it back on without
touching the database by hand.

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
acceptance checklist and a precondition of deploying slice 2.

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
| 10 | `EmailSelectedDetails` | `:1106` | re-check before `BulkJobs.Enqueue`. This is the only authorization gate the background job ever gets; see Known gaps. The method is `private Task`, not `async`, so adding an await converts it to `private async Task`. |
| 11 | trace tab render | `:36` / `:264` | the trace body is the `else` arm of `@if (activeTab == "headers")`, so **any** `activeTab` value other than `"headers"` renders it. Change the condition to `@if (activeTab == "headers" || !canTrace)` so a header-only operator lands on the headers branch whatever `activeTab` holds. One token, and it makes the render fail-safe against a future edit that lets `activeTab` slip. |
| 12 | reports page attribute | `MessageTraceReports.razor:4` | `[Authorize(Policy = "MessageTraceSearch")]`. |
| 13 | reports page init | `MessageTraceReports.razor:126` | `AuthorizeAsync(user, "MessageTraceSearch")`. |
| 14 | reports `Download` | `MessageTraceReports.razor:146` | re-check before `Exports.TryDownloadAsync`. |
| 15 | not gated | `OnRecipientInput`, `ToggleRowSelection`, `ToggleSelectAll`, `ClearSelection`, `TogglePasteInput`, `ClearHeaderAnalysis`, `ShowHeadersTab` | pure circuit-local UI state, no backend call. Named so the exemption is explicit. |

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

## 4. The UI for a header-only operator

**The Trace Search tab renders, disabled, with the reason on the control.** Not hidden.

Justification comes from this page's own recorded rule, at `:397-399`:

```
// Why the live button is unavailable, on the CONTROL. A disabled button
// that states no reason is what produced "the Download details button
// doesn't click" - it was refusing correctly and silently.
```

An operator who cannot find the Trace Search tab files the same support ticket as an operator who
cannot click it, except the hidden version gives the service desk nothing to go on. The disabled
version names the missing permission, so the operator can ask for it by name. The tab strip here
is `<button type="button">` (`:29`, `:32`), so `disabled` is honoured, unlike Migration's anchors.

Concretely: `disabled="@(!canTrace)"` on `:32` plus a `title` naming the permission, and a short
`form-text` line under the tab strip when `!canTrace` explaining that trace search requires the
Message Analysis Search permission. No other layout change. The headers tab, the upload control,
the paste box and the analysis output are untouched.

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

**Module-scoped. Bump `MessageTrace`'s `Version` in `Modules/ModuleCatalog.cs` by one patch above
whatever it holds at implementation time** - a concurrent agent is moving that same field, so take
current and add one in the patch position rather than writing a literal. Add a dated comment above
the version in the catalog's house style explaining the split, as the `BlockedSenders` and
`GroupManagement` entries do.

**No base app version bump.** `docs/ProjectConstitution.md:112-113` draws the line at shared
infrastructure versus module-scoped behaviour. Everything this plan changes is module-scoped: the
descriptor entry, that module's two page components, and that module's tests. The policy is
generated by `ConfigureAuthorizationPolicies`, which is shared machinery - but it is not
*modified*; it already builds a two-requirement policy for any declared granular
(`ModuleCatalog.cs:108-116`), and adding a granular exercises that path rather than changing it.
`ExchangeAdminWeb.csproj` is not touched, so `BuildInfo` and the sidebar version are unaffected.
(The Migration button gate took the minor position for a comparable behavioural change; if the
owner reads a permission split the same way, this is a minor rather than a patch. Stated once, not
raised as a fork.)

## Slices

Three sessions, three commits, each with its own guard proof. **The deploy boundary between slice
1 and slice 2 is mandatory, not a convenience.**

### Slice 1 - declare the permission (inert)

1. `Modules/ModuleCatalog.cs`: add `GranularPermissions = [ new("Search", "MessageTraceSearch", "<description>", FailClosed: true) ]` to the `MessageTrace` descriptor (`:256-276`).
2. Rewrite the main permission's description so it no longer claims to grant trace.
3. Bump the module version (current + 1 patch) and add the dated comment.
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
yet. Deploy it, then have the owner configure the section-access group before starting slice 2.

### Slice 2 - enforce on the Message Analysis page

6. `Components/Pages/MessageTrace.razor`: `canTrace` field and resolution; the eight handler
   gates and the render condition from the table in section 3. Every denial path audits and
   returns without touching Exchange.
7. Tripwires in `ExchangeAdminWeb.Tests/PageAuthorizationRecheckTests.cs` (section "Tests").
8. The `[Authorize]` pin in `ExchangeAdminWeb.Tests/MessageTracePageRoutingTests.cs`.
9. Guard proof: remove the gate from `RunTrace`, then from `ToggleDetail`, then from
   `EmailSelectedDetails`, confirming each fails exactly its named tripwire and nothing else;
   restore byte-identically each time. Separately confirm the existing
   `ClickGateStuckFlagTests` `ToggleDetail_*` and `RunTrace_*` assertions still pass with the
   gates in place - that is the collision this plan's placement rule exists to avoid, and it is
   worth proving rather than asserting.

### Slice 3 - enforce on the Downloadable Reports sub-page

10. `Components/Pages/MessageTraceReports.razor`: attribute, `OnInitializedAsync` check and the
    `Download` re-check on `MessageTraceSearch`.
11. Its tripwires.
12. Guard proof: revert the `Download` re-check, confirm failure, restore.

Slice 3 is separable from slice 2 and could land first; it is listed second only because the main
page is the owner's stated ask. It must not be dropped - see "What the proposed shape missed".

## Tests

Source-level tripwires. There is no bUnit harness in this repo, so nothing can render either page;
these read the page as text. The limitation is inherited and is already recorded in
`docs/MigrationStaleReport-Plan.md` and `MessageTracePageRoutingTests.cs:9-22`.

**Use `ClickGateSource` for the scanning, not `PageAuthorizationRecheckTests.GetMethodBody`.** That
local helper's signature regex is `private\s+async\s+Task(<[^>]+>)?\s+{name}\s*\(`
(`PageAuthorizationRecheckTests.cs:133-134`), which cannot find `EmailSelectedDetails` (declared
`private Task`, `:1106`) before slice 2 converts it, cannot find `ShowTraceTab` (expression-bodied
`private void`, `:649`) at all, and does not blank comments - a scanner in this repo that does not
strip comments has already produced two false failures (`.agents/state.md`, Migration entry).
`ExchangeAdminWeb.Tests/ClickGateSource.cs` handles all three: `MethodBody` (`:181-183`) accepts
any accessibility, optional `async` and any return type, `MemberBody` (`:186-187`) handles
expression-bodied members, and `Text` is comment-blanked by `BlankComments` (`:54-64`). Either
extend the local helper to delegate to it or call `ClickGateSource` directly; do not add a third
scanner.

In `PageAuthorizationRecheckTests.cs`, under a new section header explaining that these gate a
READ behind a granular (Constitution `:29`, direct event invocation), not a write:

1. `MessageTrace_TraceHandlers_RecheckTheSearchGranular` - a `[Theory]` over `RunTrace`,
   `ToggleDetail`, `ExportCsv`, `DownloadSelectedDetails`, `EmailSelectedDetails`, asserting each
   body contains `AuthorizeAsync(` with `"MessageTraceSearch"`.
2. `MessageTrace_TraceHandlers_CheckBeforeTheyReachExchange` - for `RunTrace`, `ToggleDetail` and
   `DownloadSelectedDetails`, assert the index of the `MessageTraceSearch` check is less than the
   index of the backend call (`RunRealtimeTrace()`, `GetMessageDetailAsync(`). Ordering is the
   guarantee; a check after the read protects nothing. Same shape as
   `BlockedSenders_ConfirmUnblock_ChecksTheTargetBeforeTheWrite` (`:62-88`).
3. `MessageTrace_EmailSelectedDetails_ChecksBeforeEnqueue` - the check precedes
   `BulkJobs.Enqueue(`. Separate from 2 because the "backend call" here is a queue write, and this
   is the job's only gate.
4. `MessageTrace_TabSwitchIsGated` - `ShowTraceTab` and `UseHeaderTraceSuggestion` both consult
   `canTrace`. Uses `MemberBody`/`MethodBody` as appropriate.
5. `MessageTrace_HeaderAnalysisIsNotGatedOnTheSearchGranular` - the counterweight, and the most
   important test here: `AnalyzePastedHeaders`, `AnalyzeUploadedFile` and `AnalyzeHeadersAsync`
   must **not** mention `MessageTraceSearch`. Without it, every other assertion in this file is
   satisfiable by gating the whole page, which is the failure this work exists to prevent.
6. `MessageTraceReports_Download_RechecksTheSearchGranular` - the re-check precedes
   `TryDownloadAsync(`.
7. `MessageTraceReports_PageIsGatedOnTheSearchGranular` - the page's `@attribute` and its
   `OnInitializedAsync` both name `MessageTraceSearch`, and neither names bare `"MessageTrace"`
   as the policy.

In `MessageTracePageRoutingTests.cs`:

8. `Page_KeepsBothRoutesOnTheMainPolicy` - the source contains `@page "/message-analysis"`,
   `@page "/message-trace"` and exactly one `[Authorize(Policy = "MessageTrace")]`, and does
   **not** contain `[Authorize(Policy = "MessageTraceSearch")]`. The regression it refuses is
   promoting the page attribute to the granular, which locks header-only operators out of the
   module entirely and would leave tests 1-7 green.

Behavioural coverage for the authorization decision itself already exists and is not duplicated
here: `GroupAuthorizationHandlerTests` covers the handler, and `ModuleCatalogTests` covers policy
generation. These tripwires assert only wiring and ordering, which those cannot see.

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
      Until this is done, deploying slice 2 denies trace to everyone.
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

## Known gaps

- **The background detail-export job has no off-circuit authorization re-check, before or after
  this change.** `Services/Jobs/MessageTraceDetailJobProcessor.cs` does not capture or consult a
  `JobAuthorizationSnapshot`; the only consumer of that mechanism in the app is
  `Components/Pages/ConferenceRooms.razor:711` and `Services/Jobs/ConferenceRoomBulkProcessor.cs:78`.
  So the gate on `EmailSelectedDetails` is the only authorization the job ever sees: a job
  submitted a second before the operator's grant is revoked still runs with the module's shared
  Exchange credential. This is pre-existing and not caused by the split, but the split makes it
  more visible, because "holds trace search" is now a narrower set than "can open the module". See
  open question 6.
- **The exports listing remains unscoped by submitter** even after slice 3. Every holder of
  `MessageTraceSearch` still sees and can download every other Search holder's exports. That is
  the current design and this plan does not change it; it only stops non-Search holders reaching
  it.
- **The main alias is now misleadingly named.** `MessageTrace` is the permission that does *not*
  grant trace. Renaming it would orphan the stored section-access row and lock everyone out of the
  module, so the descriptions carry the meaning instead. An admin reading only alias names in the
  Access tab could still get this wrong.
- **Tripwires are text analysis of C# inside `.razor` files.** They can be defeated by unusual
  formatting. They prove wiring and ordering, not that the gate decides correctly.
- **No test in this repo can render either page**, so items 2-9 of the manual checklist are the
  only evidence that an operator experiences the split.

## Open questions

Each answerable in one line.

1. Should the group(s) currently granted `MessageTrace` be copied onto `MessageTraceSearch` so
   today's operators keep trace search, or should trace be re-granted deliberately, group by
   group, as a fresh decision? (This plan assumes the owner decides at checklist step 1 and does
   not seed anything in code.)
2. Alias name: `MessageTraceSearch` (recommended) or `MessageTraceTrace`?
3. Confirm `/message-analysis/reports` moves to the granular (recommended - it exposes every
   operator's trace detail exports), rather than staying on the main permission.
4. Confirm the Trace Search tab renders disabled with the reason stated (recommended) rather than
   being hidden.
5. Should `ExportCsv` be gated (recommended - it exports the whole result set, not the 50 rendered
   rows), or left ungated because the operator already ran the trace that produced it?
6. Is closing the background job's missing off-circuit authorization re-check in scope for this
   work stream, or a separate item?
7. Module version: patch (as written) or minor, given the Migration button gate took a minor for a
   comparable behavioural change?

## Provenance

Owner queue item 4, verbatim: "Break out permissions for message trace vs header analysis."
Recorded as not-yet-started in `.agents/state.md` under "Queue items not yet started, in the
owner's own words". No decision in `.agents/decisions.md` bears on the split; the 2026-09-02
ruling on operator-facing permission descriptions
(`ModuleCatalogTests.Catalog_EveryPermissionCarriesAnOperatorFacingDescription`) governs the two
descriptions this plan rewrites.
