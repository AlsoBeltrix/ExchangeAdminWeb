# Risky Users Module (Microsoft Entra ID Protection)

Status: Draft, awaiting owner go. D1 (remediation phase in scope) and D2 (read alerting)
are OPEN and are named in `## Owner decisions`. Slices S1-S4 depend on neither and are
implementable behind a go on the read phase alone. S5-S7 are gated on D1. **D2 is a
pre-ship gate: S7 cannot close and no phase may be marked `Implemented` until D2 is
ruled and built.**

New module `RiskyUsers`. No base app version bump (Constitution, Deployment And
Versioning: adding a module is not a shared-infrastructure change) unless S4a is taken,
which changes `Services/GraphTokenClient.cs` and therefore does bump it.

## Purpose

Surface Microsoft Entra ID Protection risky users inside ExchangeAdminWeb so an operator
can triage them without an Entra portal sign-in, and (phase 2, gated on D1) act on the
risk state: dismiss, confirm safe, confirm compromised.

## External prerequisites

Both are outside the codebase and both are hard blockers. Do not begin S2 until each is
confirmed; a build that compiles against an unlicensed or unconsented tenant looks
correct and returns nothing.

1. **Microsoft Entra ID P2.** The `riskyUsers` API requires it -- Microsoft states this on
   both the resource page and every action page. Without P2 the endpoints return an error,
   not an empty list. Confirm the tenant licence before S2.
2. **A dedicated Entra app registration**, admin-consented, with application permissions:
   - `IdentityRiskyUser.Read.All` -- required for the read phase (S2-S4).
   - `IdentityRiskyUser.ReadWrite.All` -- required for the write phase (S5-S7) only.
     This is the least-privileged permission for all three actions; Microsoft lists no
     lower one.
   - `IdentityRiskEvent.Read.All` -- ONLY if risk detections are added later. Out of
     scope here (see `## Non-goals`).
   A matching Delinea Secret Server record must exist holding the three fields
   `Tenant ID`, `Application ID`, `Client Secret`, directly readable by the Delinea API
   bootstrap credential with no checkout or approval workflow (Constitution, Credential
   Isolation rule 5).

Reusing the `MfaReset` or `M365GroupManagement` app registration is permitted only if the
operator deliberately enters the same Secret ID (Spec, Credential Isolation rule 5), and
is discouraged here: those apps hold `UserAuthenticationMethod.ReadWrite.All` and
`Group.ReadWrite.All` respectively, and adding risk permissions to them widens two
unrelated blast radii.

## Graph surface

All endpoints are Graph **v1.0**. `Services/GraphTokenClient.cs:16` hardcodes
`https://graph.microsoft.com/v1.0` as its base, so no beta path and no client change is
needed for the endpoints themselves. Verified against Microsoft Learn 2026-08-12.

| Operation | Method and path | Permission | Success |
|---|---|---|---|
| List | `GET /identityProtection/riskyUsers` | `IdentityRiskyUser.Read.All` | 200 + `value[]` |
| Get one | `GET /identityProtection/riskyUsers/{id}` | `IdentityRiskyUser.Read.All` | 200 |
| History | `GET /identityProtection/riskyUsers/{id}/history` | `IdentityRiskyUser.Read.All` | 200 + `value[]` |
| Dismiss | `POST /identityProtection/riskyUsers/dismiss` | `IdentityRiskyUser.ReadWrite.All` | 204 |
| Confirm safe | `POST /identityProtection/riskyUsers/confirmSafe` | `IdentityRiskyUser.ReadWrite.All` | 204 |
| Confirm compromised | `POST /identityProtection/riskyUsers/confirmCompromised` | `IdentityRiskyUser.ReadWrite.All` | 204 |

`riskyUser` properties (all that this module reads): `id` (the Entra object id of the
user), `isDeleted`, `isProcessing`, `riskLastUpdatedDateTime`, `riskLevel`, `riskState`,
`riskDetail`, `userDisplayName`, `userPrincipalName`.

`riskLevel` values: `low`, `medium`, `high`, `hidden`, `none`, `unknownFutureValue`.
`riskState` values: `none`, `confirmedSafe`, `remediated`, `dismissed`, `atRisk`,
`confirmedCompromised`, `unknownFutureValue`.

The list endpoint supports `$filter` and `$select` only. `$top` maxes at 500. `$orderby`
and `$count` are NOT documented as supported -- do not emit them; sort client-side.

All three action bodies are `{ "userIds": [ ... ] }` and all three return `204 No Content`
with no per-user result. See S5 for why this module still calls them one user at a time.

## Owner decisions

### D1 -- OPEN. Is the remediation phase in scope now?

Read phase (S1-S4) lists and inspects risky users. Write phase (S5-S7) adds dismiss,
confirm safe, confirm compromised. The write phase needs the wider Graph consent, the
full ticket + confirmation + protected-principal + audit + notification stack, and is
where the real risk sits: dismissing risk on a genuinely compromised account is a
security regression that this app would be the instrument of.

Implement S1-S4 only until D1 is ruled. If D1 lands as read-only, mark S5-S7
`Deferred` in this file rather than deleting them, and drop
`IdentityRiskyUser.ReadWrite.All` from the app registration ask.

### D2 -- OPEN. Do reads on this module alert administrators?

Constitution, Notifications: "A read module classified as a security-response surface
must send an administrator alert", and whether a given read counts "is a deployment
classification, not an automatic property of touching directory data".
`.agents/decisions.md` 2026-06-30 classified every existing module's reads as
NON-alerting and deferred read-alerting work wholesale, on the reasoning that this app's
reads expose only what is already visible in AD or the address book.

That reasoning does not transfer. A risky-user list is Entra ID Protection output, not
address-book data, and this module is purpose-built for security response -- it is the
first module in the repo that meets the clause on its face.

Options, one line each:
- Alert on every list/history read. Honours the clause literally; a triage session that
  refreshes ten times sends ten alerts.
- Alert on nothing; audit only. Matches every existing module; leaves the clause
  unhonoured for the one module written for it.
- Alert once per operator session or per N minutes. Honours intent without the volume;
  needs a debounce that nothing in the repo currently has, so it is new shared code.

**D2 is a PRE-SHIP GATE, not an open question that ships unanswered.** During S1-S4
development, implement audit-only (`AuditService.LogModuleAction`) and do not wire
`EmailService` into the read path -- audit-only is the reversible default, since adding an
alert later is additive whereas an unwanted alert stream trains operators to ignore
notifications from this app. But the read phase MUST NOT be marked `Implemented`, and S7
MUST NOT close, until D2 is ruled and the ruled shape is implemented with AC17 and manual
check 7 satisfied.

The distinction matters because "audit-only is the interim default" and "audit-only is what
ships" are one line apart. This module is the first in the repo that meets the
security-response clause on its face; deploying it with the clause unhonoured and the
question still marked open is how a governance gap becomes permanent. If the owner rules
"alert on nothing", that is a ruling and the clause is honoured by an explicit
classification -- which is what the Constitution asks for. What is not permitted is
shipping with no ruling at all.

## Non-goals

- `riskDetections` (`GET /identityProtection/riskDetections`). Separate permission
  (`IdentityRiskEvent.Read.All`), separate resource, separate UI. Not in this plan.
- `riskyServicePrincipals`. Beta-only; `GraphTokenClient` is v1.0-pinned.
- Any Conditional Access, sign-in log, or user-remediation action (password reset,
  session revoke, account disable). `EmergencyDisable` owns that surface; this module
  must not grow a second path to it.
- Bulk/CSV input. Actions are per-row from the rendered table.
- A local store of risky users. Every page load queries Graph, matching
  `MigrationService.GetMigrationBatchesAsync`; there is no cache and therefore no
  staleness question.

## Design

### S1. Catalog descriptor (`Modules/ModuleCatalog.cs`)

Insert in `RegisterAll()` between `EmergencyDisable` (SortOrder 740) and `MfaReset` (750):

```csharp
new()
{
    Id = "RiskyUsers",
    DisplayName = "Risky Users",
    Description = "Review Microsoft Entra ID Protection risky users and their risk history.",
    Route = "risky-users",
    IconCss = "bi bi-person-fill-nav-menu",
    Category = "Identity & Access",
    SortOrder = 745,
    EnabledByDefault = false,
    IsSystemModule = false,
    Version = "1.0.0",
    MainPermission = new("Access", "RiskyUsers", FailClosed: true),
    GranularPermissions = [new("Remediate", "RiskyUsersRemediate", FailClosed: true)],
    ConfigFields = [
        new("GraphDelineaSecretId", "Graph App Delinea Secret ID", "Secret Server secret with fields: Tenant ID, Application ID, Client Secret (requires IdentityRiskyUser.Read.All, plus IdentityRiskyUser.ReadWrite.All for remediation). Requires Microsoft Entra ID P2."),
        new("MaxRows", "Max Rows", "Maximum risky users fetched per query (Graph caps at 500)", Required: false, DefaultValue: "500")
    ]
}
```

Notes binding on the implementer:

- `IconCss` reuses an EXISTING class. Only twelve `-nav-menu` icon classes are defined
  (`Components/Layout/NavMenu.razor.css`); a new class name renders a blank icon.
  Do not invent one.
- `EnabledByDefault = false` and both permissions `FailClosed: true` (Constitution:
  optional modules disabled by default; the data here is security-sensitive, so an
  unconfigured `section_access` store must deny, never fall back to the legacy app-wide
  `Security:AllowedGroups`).
- The granular alias is `RiskyUsersRemediate` -- parent Id + permission name, per the
  Spec naming table. Register it in S1 even if D1 defers the write phase: the alias is
  inert without a page control behind it, and adding it later is a second config change
  the operator has to make.
- **Adding the descriptor fails two existing tests until they are updated in the same
  commit.** `ExchangeAdminWeb.Tests/ModuleCatalogTests.cs:16` (module count 25 -> 26) and
  `:109` (alias count 34 -> 36; this module adds two aliases). See S4 for the detail and
  for why those assertions must not be weakened.
- If D1 defers remediation, keep the granular permission but say so in the
  `Description`, so the Module Config page does not present a grant with nothing behind it
  -- that is the exact shape flagged in `.agents/state.md` (servicer grant with no module
  access) and now badged by Module Config.

### S2. Service read path (`Services/RiskyUsersService.cs`)

Constructor mirrors `MfaResetService` exactly: `ILogger<RiskyUsersService>`,
`ModuleConfigService`, `DelineaService`, `IHttpClientFactory`.

**Registration belongs to THIS slice, not S1.** Add
`builder.Services.AddSingleton<RiskyUsersService>();` in `Program.cs` beside the other
Graph services (`Program.cs:113`, `:127`) in the same commit that introduces the type --
a registration committed ahead of the type it names does not compile. Singleton is
correct and matches both Graph services: it holds no per-request state, and the
`GraphTokenClient` is constructed per operation from the named client `"MicrosoftGraph"`
(`Program.cs:104`).

```csharp
private async Task<GraphTokenClient?> GetGraphClientAsync()   // copy of MfaResetService:20-36,
                                                              // reading module id "RiskyUsers"
public bool IsAvailable { get; }                              // GraphDelineaSecretId parses > 0
```

Public read surface:

```csharp
public async Task<RiskyUserPage> GetRiskyUsersAsync(RiskyUserFilter filter)
public async Task<IReadOnlyList<RiskyUserHistoryEntry>> GetHistoryAsync(string userId)
```

```csharp
public sealed record RiskyUserFilter(string? RiskLevel, string? RiskState, string? UpnContains);

public sealed record RiskyUserPage(
    IReadOnlyList<RiskyUser> Users,
    bool Truncated,          // Graph returned an @odata.nextLink this module did not follow
    int RequestedMax);

public sealed class RiskyUser
{
    public string Id { get; set; } = "";                 // Entra object id
    public string UserPrincipalName { get; set; } = "";
    public string UserDisplayName { get; set; } = "";
    public string RiskLevel { get; set; } = "";
    public string RiskState { get; set; } = "";
    public string RiskDetail { get; set; } = "";
    public DateTimeOffset? RiskLastUpdatedDateTime { get; set; }
    public bool IsProcessing { get; set; }
    public bool IsDeleted { get; set; }
}
```

**Five rules this service must hold. Each maps to a defect this repo has already shipped
once; the reference is given so the implementer can read the original rather than trust
this summary.**

1. **A failed request must never render as "no risky users."** Use
   `GetWithStatusAsync`, not `GetAsync`, and throw `InvalidOperationException` carrying
   `(int)status` when `doc == null`. `GetAsync` collapses failure and empty into one
   value; `Services/GraphTokenClient.cs:45-46` says so in the source. The same collapse
   turned a Graph 403 into a blanket-success MFA reset
   (`Services/MfaResetService.cs:54-57`) and produced `blr-2`.
   **403 is the expected shape on a tenant with no P2 or without consent.** Map it to a
   distinct operator-facing message ("Risky Users is not available for this tenant --
   verify Entra ID P2 licensing and the app registration's IdentityRiskyUser.Read.All
   consent"), not a generic failure, because it is the single most likely first-run
   outcome.

2. **Truncation must be visible.** `GraphTokenClient` appends a RELATIVE path to a
   hardcoded base URL; `@odata.nextLink` is ABSOLUTE, so it cannot be followed by the
   current client. Emit `$top` capped at 500 (`Math.Clamp(MaxRows, 1, 500)`), and if the
   response body contains `@odata.nextLink`, set `Truncated = true`. The page MUST render
   that (AC7). A silently truncated list of risky users is the same failure class as
   BitLocker's cap-before-match: "no more" and "I stopped looking" must not look alike.
   See S4a if following the link is wanted instead.

3. **Filtering is server-side where Graph supports it, client-side where it does not.**
   `riskLevel` and `riskState` go into `$filter` as
   `riskLevel eq 'high'` / `riskState eq 'atRisk'`, combined with `and`, URL-escaped via
   `Uri.EscapeDataString` on the whole filter expression (the
   `M365GroupManagementService.cs:74-76` shape). `UpnContains` is applied in C# after the
   fetch -- Graph's `$filter` on this resource does not document `contains()`, and a
   filter Graph rejects returns 400, which rule 1 then surfaces as a hard failure. Escape
   single quotes by doubling them before interpolation, as
   `M365GroupManagementService.cs:74` does.

4. **Unknown enum values must pass through, not be dropped.** Both enums include
   `unknownFutureValue`, and Microsoft adds members. Store `riskLevel`/`riskState` as
   plain strings and let the page render an unrecognised value with a neutral badge.
   Do NOT parse into a C# enum and do NOT filter the list against a hardcoded set of
   known values. An exhaustive-looking allowlist written from the values a developer has
   seen is a silent filter, not a safety rail -- that is the `CompletedWithErrors`
   failure from `docs/MigrationBatchSelection-Plan.md`, which hid batches from three
   separate controls on one page.

5. **Sort client-side, deterministically.** `$orderby` is not documented as supported.
   Default order: `riskLevel` descending by severity (`high`, `medium`, `low`, `hidden`,
   `none`, then anything unrecognised), then `RiskLastUpdatedDateTime` descending.

**Test seam.** `MfaResetService` builds its `GraphTokenClient` internally, which is why
`MfaResetServiceConfigTests` can only reach configuration. Do better here: add an
`internal` constructor taking `Func<Task<GraphTokenClient?>>` alongside the public DI
constructor, so tests can drive the full parse, filter and truncation logic against a
canned Graph response. The project already exposes internals to the test assembly and
already uses this seam shape in three services (see the servicer work in
`.agents/state.md`). The public constructor and DI registration must be unchanged by the
seam.

**The HTTP stub must be declared locally; it cannot be borrowed.**
`GraphTokenClientTests.StubHandler` is the right SHAPE to copy -- it serves a canned token
for `login.microsoftonline.com` and a settable response for every Graph call -- but it is
`private sealed` (`ExchangeAdminWeb.Tests/GraphTokenClientTests.cs:12`) and is therefore
not reachable from another test class. Referencing it does not compile.

Declare an equivalent private handler inside `RiskyUsersServiceTests`. Do NOT promote the
existing one to `internal` or hoist it into a shared helper as part of this work: that
edits a test file this module does not own, for a second consumer, and the duplication is
about fifteen lines. If a third consumer ever appears, extracting it is that change's job.

### S3. Read page (`Components/Pages/RiskyUsers.razor`)

`@page "/risky-users"`, `@attribute [Authorize(Policy = "RiskyUsers")]`,
`@rendermode InteractiveServer`. Structure and lifecycle copy
`Components/Pages/MfaReset.razor:1-16, 145-162`: `authChecked` spinner gate,
`OnInitializedAsync` re-checks the policy and navigates to `access-denied` on failure,
`ClientInfo` for the IP.

`<h1 class="mb-4">Risky Users<ModuleVersion /></h1>` -- required, and enforced by
`tools/validate-module-package.ps1`.

Guard on `!RiskyUsersService.IsAvailable` with the "not configured" alert, as
`MfaReset.razor:37-41`.

Controls: risk level select, risk state select, UPN contains text box, Refresh button.
Table columns: UPN, display name, risk level badge, risk state, risk detail, last
updated (local time), and flags for `isProcessing` / `isDeleted`. Row expander showing
`GetHistoryAsync` output.

**Four page rules, each from a defect that shipped in this repo:**

1. **Three states must be distinguishable: not searched, searched-and-empty, and
   failed.** Hold `hasQueried` separately from `results`, and CLEAR `hasQueried` at the
   start of every query, not only on the first. `blr-3` was exactly this: a second search
   emptied the results but left the searched flag set, so the page rendered a definitive
   "no recovery keys found" over emptied data for the seconds the query was in flight,
   on a recovery call.
2. **Show an in-flight indicator, and force a render before the work.** Set
   `isLoading = true` then `await Task.Yield()` before the Graph call, as
   `MfaReset.razor:174-178` does. `blr-4` was caused by fixing `blr-3` without this: the
   results area went blank and the page read as hung.
3. **Render the truncation flag from S2 rule 2** whenever `Truncated` is true, naming the
   cap: "Showing the first N; more exist. Narrow the filter."
4. **Audit every query** through
   `Audit.LogModuleAction(currentUser, clientIpAddress, "RiskyUsers_List", "RiskyUsers", target, success, errorDetail: ...)`
   where `target` is the filter expression, on BOTH the success and the failure path.
   Actions: `RiskyUsers_List`, `RiskyUsers_History`. No `EmailService` call on the read
   path until D2 is ruled.

### S4. Read-phase tests

New file `ExchangeAdminWeb.Tests/RiskyUsersServiceTests.cs`, behavioural, over the
internal seam and a locally declared HTTP stub:

- 403 / 404 / 429 / 500 each produce a FAILURE, never an empty success.
- 200 with `"value": []` produces an empty success -- the inverse of the above, and the
  test that proves the first is not simply "everything fails".
- 200 with `@odata.nextLink` present sets `Truncated = true`; absent sets it false.
- A `riskLevel` of `unknownFutureValue` and an undocumented literal both survive into
  the model unchanged and are not filtered out.
- `MaxRows` of 5000 clamps the emitted `$top` to 500; of 0 or unparseable falls back to
  500.
- `UpnContains` filters client-side and is NOT emitted into `$filter`.
- Single quotes in a filter value are doubled, not injected raw.
- The sort comparator orders `high` above `medium` above `low`, and places an
  unrecognised level last without throwing.

**Catalog tests belong to S1, not S4, because adding the descriptor BREAKS two existing
assertions and the S1 commit must be green.** `ModuleCatalogTests.cs:16` asserts
`Assert.Equal(25, _catalog.GetAll().Count)` and `:109` asserts
`Assert.Equal(34, aliases.Count)`. Adding this module with one granular permission makes
those 26 and 36. Update both in the S1 commit, with the comment on `:16` ("25 modules
(24 operational + 1 config-only)") corrected to match -- a count comment that disagrees
with its assertion is how the next person adds a module and trusts the wrong number.

Then add descriptor-specific tests in the same file, matching the existing pattern
(`Catalog_HasBlockedSendersModule`,
`Catalog_BlockedSenders_MainPermissionIsFailClosed`): the descriptor exists, both
permissions are `FailClosed`, the module is disabled by default, and both
`RiskyUsers` and `RiskyUsersRemediate` appear in the configurable alias list.

Note that these two count assertions are a deliberate tripwire, not incidental
brittleness: they force any module addition to be a conscious edit. Do not weaken them
into a range or a `>=`.

### S4a. OPTIONAL, and a base app version bump if taken -- nextLink paging

Teaching `GraphTokenClient` to follow `@odata.nextLink` (accept an absolute URL and skip
the base-URL prepend) is shared-infrastructure change: it touches a file used by
`MfaReset` and `M365GroupManagement`, so it bumps `<VersionPrefix>`, `AssemblyVersion`
and `FileVersion` in `ExchangeAdminWeb.csproj` and needs its own regression pass over
`GraphTokenClientTests`.

Do NOT take this slice as part of the module work. S2 rule 2 makes truncation honest and
visible, which is sufficient. Take S4a only if a real tenant is found to hold more than
500 risky users and the operators say the cap blocks them -- and then as its own plan,
because it is not a Risky Users change.

### S5. Write path -- GATED ON D1 (`Services/RiskyUsersService.cs`)

```csharp
public async Task<RiskyUserActionResult> ApplyActionAsync(string userId, RiskyUserAction action)
public sealed record RiskyUserActionResult(string UserId, bool Success, string Message);
public enum RiskyUserAction { Dismiss, ConfirmSafe, ConfirmCompromised }
```

**One user per HTTP call. This is the load-bearing decision of the write phase.**

All three endpoints accept `userIds` as a collection and return a single `204` for the
whole batch, with NO per-user body. Posting five ids and receiving one status code makes
it impossible to say which user was acted on -- and a partial rejection would be reported
as blanket success. That is Known Failure Class 2 (success aggregation) written into the
API itself. Calling once per user gives every row its own named outcome, matching the
Migration per-target pattern (`PartitionByProtectionAsync`) and the aggregating executor
in `docs/MigrationBatchSelection-Plan.md` slice 2: one audit event per target inside the
loop, per-item failures aggregated, one summary notification per run.

Use `PostNoContentAsync` (`Services/GraphTokenClient.cs:63`), which returns
`IsSuccessStatusCode`. Body: `new { userIds = new[] { userId } }`.

`isProcessing == true` means Entra is still working on that user's risk state. Surface
it; do not block on it. Do not invent a client-side eligibility allowlist over
`riskState` -- let Graph refuse and report the refusal as that row's own named failure.
This is D4 of `docs/MigrationBatchSelection-Plan.md` applied here: eligibility defined by
exclusion, so an unanticipated state defaults to visible rather than silently vanishing.

### S6. Write UI and gates -- GATED ON D1 (`Components/Pages/RiskyUsers.razor`)

Per-row action buttons, each requiring in order, and each failing closed:

1. **Granular authorization**, re-checked immediately before execution:
   `await AuthorizationService.AuthorizeAsync(user, "RiskyUsersRemediate")`. Not only at
   page load -- Spec, Page Authorization item 3.
2. **Ticket number**, mandatory, validated through
   `ServiceNow.ValidateTicketAsync` as `MfaReset.razor:241-248`. Render the confirm bar
   BENEATH the acting row, not above the table (`docs/MigrationBatchSelection-Plan.md`
   slice 3 -- a top-of-table confirm on row 47 puts the input off-screen while that row's
   buttons go disabled, which reads as the buttons breaking).
3. **Confirmation step** before the write, naming the user and the action.
4. **Protected-principal check.** See below; this is the part that needs care.
5. **Audit** via `LogModuleAction` on every path -- success, refusal, fail-closed,
   exception -- with the ticket, the target UPN, and the action.
6. **Admin notification** via `Email.SendAdminNotificationAsync` in a `finally`, wrapped
   in try/catch so a send failure cannot change the operation result
   (`MfaReset.razor:403-419`). Constitution, Notifications: every mutating action
   notifies administrators.

**The protected-principal check, and why it cannot be copied blind.**

Risky users are cloud identities. The repo's group, OU and SamAccountName-pattern
protection rules are all evaluated from an on-premises DN, so they can NEVER match a
cloud-only principal -- a structural limit stated in the Constitution, Protected
Principals, final bullet. A cloud-only principal is protected by the protected USER rows
(by address) or not at all.

`MfaReset` hit this exactly and its descriptor comment records the outcome: before
`1.1.0` an AD-only lookup reported every cloud-only user as "no AD object" and skipped
the check, leaving protection "close to inert" for a Graph module whose normal input is a
cloud identity.

So use the `MfaReset.razor:262-364` two-branch shape:

- `ProtectedPrincipalService.ResolveWithExchangeFallbackAsync(upn)`; `Unavailable` or
  `Ambiguous` REFUSES and audits (fail-closed outranks everything).
- Resolved -> `CheckAsync(resolved)`; `CheckFailed` refuses; `IsProtected` refuses unless
  a servicer note is returned.
- NOT resolved -> do NOT skip. Build a `ResolvedDirectoryPrincipal` from the raw identity
  and run `CheckAsync` against it, so the protected USER rows still get their chance to
  match an address.

**One improvement over the MfaReset shape, and it is the reason this must not be a
literal copy.** `MfaReset` only has a UPN, so it passes `EntraObjectId: null`. This
module has the Entra object id in hand -- `riskyUser.id` IS it. Populate
`EntraObjectId: user.Id` in the unresolved-branch `ResolvedDirectoryPrincipal`. Verify
before implementing whether `ProtectedPrincipalService.CheckAsync` actually consults
`EntraObjectId`; if it does not, populate the field anyway (it costs nothing and lands in
the audit trail) and record in this file that object-id-based protection matching does
not exist, rather than implying it works.

**Servicer override.** Honour `ProtectedServicer:RiskyUsers` through
`ProtectedPrincipalServicing.NoteFor(...)` / `.Extra(...)`, in BOTH branches --
`MfaReset.razor:216-220, 300, 351`. Both sites, or a cloud-only protected identity
becomes the one case a servicer cannot service, which is precisely this module's
population. The note travels in the audit event's `extra`, never `errorDetail`:
`LogModuleAction` writes `["error"] = success ? null : errorDetail`
(`Services/AuditService.cs:212`), so a note placed there is discarded on exactly the
success path that needs it. Never test the note with a bare `is null` inside a boolean --
that is `pps-3`, and a guard already forbids the shape across `Services/` and
`Components/Pages/`.

No `ProtectedServicer:RiskyUsers` row will exist in either config store on first deploy.
That is scope, not oversight -- record it, do not create it.

### S7. Docs and version -- GATED ON D1 for the remediation half

- `README.md`: new `### Risky Users (/risky-users)` section between
  `### Emergency Disable` and `### DHCP Authorization`, stating the P2 requirement, the
  Graph permissions, the truncation cap, and the two policy aliases.
- This file: `Status:` to `Implemented`, D1/D2 recorded with the owner's wording.
- `.agents/state.md`: move from queued to landed.
- `Modules/ModuleCatalog.cs`: `Version` stays `1.0.0` for the first shipped build. If the
  read phase ships and remediation lands later, remediation is `1.1.0` -- a behaviour
  change after the module first reached dev MUST get its own version, or two different
  builds share one version number. That has now happened twice in this repo (`2.5.1`,
  and Migration `1.6.0` carrying three later behaviour changes).
- `ExchangeAdminWeb.csproj`: UNCHANGED. Adding a module does not bump the base app
  version. Only S4a would.

## Slices

Commit one at a time; each is independently revertible and each closes its own paperwork.

| Slice | Content | Gate |
|---|---|---|
| S1 | Catalog descriptor + catalog test updates. NO `Program.cs` change | go on read phase |
| S2 | `RiskyUsersService` read path + models + internal seam + `Program.cs` DI | go on read phase |
| S3 | `RiskyUsers.razor` read UI | go on read phase |
| S4 | `RiskyUsersServiceTests` | go on read phase |
| S4a | `GraphTokenClient` nextLink paging | do not take; separate plan |
| S5 | Service write path, one call per user | D1 |
| S6 | Page write UI, ticket, protection, servicer, audit, notify | D1 |
| S7 | README, this file, `.agents/state.md`, module version | with the last slice taken |

S1 before S2 before S3. S4 may be written alongside S2. S5 must not begin before S3 is
green: the write UI attaches to rendered rows that do not exist until then.

**Every slice must build and test green on its own commit.** That is why the
`Program.cs` DI registration lives in S2 and not S1: registering
`AddSingleton<RiskyUsersService>()` before the type exists fails `dotnet build` at the
S1 commit, and a slice that does not build is not revertible in the way per-slice
commits exist to provide. The descriptor in S1 has no code dependency -- it is data in a
list -- so S1 stands alone. Verify this rather than assume it: build at the S1 commit
before starting S2.

## Acceptance criteria

Read phase:

- **AC1.** A Graph 403 renders an operator-facing message naming the P2 licence and the
  consent as the likely cause. It does NOT render as an empty result set.
- **AC2.** A Graph 200 with an empty `value` array renders "no risky users" and is
  distinguishable in the UI from AC1.
- **AC3.** Before any query has run, the page renders neither "no risky users" nor a
  result table. After a second query begins, the first query's verdict is no longer on
  screen.
- **AC4.** An in-flight query shows an indicator; the results region is never blank with
  no explanation.
- **AC5.** A `riskLevel` value not in the documented set renders in the table with a
  neutral badge and is not dropped from the list.
- **AC6.** `MaxRows` set to 5000 results in `$top=500` on the wire.
- **AC7.** A response carrying `@odata.nextLink` renders a visible truncation notice
  naming the cap.
- **AC8.** Every query writes an audit event on both the success and the failure path.
- **AC9.** With no `section_access` rows for `RiskyUsers`, the page denies -- it does not
  fall back to `Security:AllowedGroups`.
- **AC17.** GATED ON D2, and the read phase cannot ship without it. Whichever alerting
  shape D2 rules, a test asserts it: "alert on every read" asserts `EmailService` is
  called once per query and that a send failure does not change the rendered result;
  "alert on nothing" asserts `EmailService` is NOT reachable from the read path, so a
  later edit cannot add one silently; a debounced shape asserts both the first-send and
  the suppressed-resend.

Write phase (D1 only):

- **AC10.** Acting on three rows produces three separate Graph calls and three separately
  named per-row outcomes. A refusal on row 2 does not mark rows 1 and 3 failed, and does
  not mark row 2 succeeded.
- **AC11.** A protected cloud-only principal (protected by a USER row naming its address,
  with NO on-prem object) is REFUSED, and the refusal is audited. Prove non-vacuously:
  with the unresolved branch's `CheckAsync` call removed, this test must fail. If it
  still passes, the check is inert -- which is the exact `MfaReset` pre-`1.1.0` defect.
- **AC12.** A member of a group holding `ProtectedServicer:RiskyUsers` acts on that same
  principal, SUCCEEDS, and the audit event carries the note in `extra` -- not in
  `errorDetail`, and not absent.
- **AC13.** An operator with `RiskyUsers` but NOT `RiskyUsersRemediate` sees no action
  control AND is refused if the handler is reached, with the refusal audited.
- **AC14.** A missing or invalid ticket blocks the action before any Graph call.
- **AC15.** An `EmailService` throw does not change the reported result of a completed
  action.
- **AC16.** A user whose `riskState` is an undocumented value still renders its action
  buttons; Graph's refusal, if any, is reported as that row's own named failure.

## Verification

Per `.agents/repo-guidance.md`:

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx` -- the `.slnx`, never bare `dotnet test`, which
  resolves only the web csproj and silently runs zero tests
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`
- ASCII check over new `.cs` files -- CI fails on any non-ASCII in tracked `.cs`/`.ps1`,
  comments included

No PowerShell changes, so no PSScriptAnalyzer or Pester run is required.

**Non-vacuity is required, not optional.** For each new guard, revert the code it covers,
confirm ON DISK that the revert landed, watch the test fail, restore, confirm the restore
landed, watch it pass. Two traps this repo has hit and that will recur here:

- A replacement string that silently matches nothing manufactures a false PASS. Read the
  file back after the revert; do not trust the reverting script.
- `Copy-Item` restoring a backup carries the BACKUP's timestamp, so MSBuild judges the
  DLL up to date and keeps testing the mutant. Touch the file after any
  timestamp-preserving restore.

**No test in this repo renders a Razor page** -- there is no bUnit harness. Every AC
above that names page behaviour (AC1-AC5, AC7, AC13) is either a source-level tripwire or
a manual check, and must be labelled as whichever it is. Do not report a green suite as
evidence for them. Three of this repo's last four page defects lived in code whose commit
message claimed it worked, in a service that was itself correct.

Manual checks, to be run on dev and each recorded as run or not run:

1. Unconfigured module -> "not configured" alert, no Graph call attempted.
2. Configured against a tenant without P2 -> AC1's message, not an empty table.
3. A real query returning rows -> table populated, badges correct, history expander works.
4. A filter matching nothing -> AC2, distinguishable from check 2.
5. Two queries in succession -> AC3; the first verdict does not linger.
6. (D1) A per-row action end to end -> Graph accepts, audit event written with ticket,
   admin notification received.
7. (D2, required before the read phase ships) The ruled alerting behaviour observed on a
   real query -- an alert arrives, or demonstrably none is sent, matching the ruling.

## Versioning

- `Modules/ModuleCatalog.cs` -> `RiskyUsers` `Version = "1.0.0"` at first ship.
- Remediation landing after the read phase reaches dev -> `1.1.0`. Not optional: two
  builds sharing one version number is worse than a wrong number.
- `ExchangeAdminWeb.csproj` -> unchanged. Adding a module does not bump the base app
  version (Constitution, Deployment And Versioning; `.agents/decisions.md` 2026-07-21).
  S4a is the only slice here that would.

## Open questions

- **D1** -- is the remediation phase in scope now? Blocks S5-S7 only.
- **D2** -- do reads alert administrators? Does not block S1-S4, but IS a pre-ship gate:
  S7 cannot close and the read phase cannot be marked `Implemented` until it is ruled and
  the ruled shape is built. Audit-only is the development-time default, not the shipped
  answer.
- **Does `ProtectedPrincipalService.CheckAsync` consult `EntraObjectId`?** Not verified at
  the time of writing. Determine during S6 by reading the method, and record the answer
  here either way. If it does not, S6 still populates the field, and the gap is recorded
  rather than assumed closed.
