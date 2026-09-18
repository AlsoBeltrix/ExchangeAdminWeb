# Message Analysis: Other Tenants / Domains We Own -- Feasibility

Status: **Draft.** Scoping and feasibility only. No code change is proposed for approval
yet; the owner's answers to the open questions in the last section decide whether any of
the slices below are ever drafted as work.

Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md` and
`.agents/repo-guidance.md`. On conflict the higher source wins.

Scope note: this plan does NOT touch Message Analysis permissions beyond the one
authorization question multi-tenancy forces (section 3). The permission split work is a
separate, concurrent work stream (`docs/MessageTracePermissionSplit-Plan.md`, Draft, queue
item 4). Where the two meet, that plan wins on permission shape; this one only states what
a tenant dimension would add to whatever shape it lands on.

Versioning note: another agent is concurrently editing `Modules/ModuleCatalog.cs`,
including the `MessageTrace` `Version` field. Every version statement below is therefore
expressed as **current + 1**, never as a literal. Read the live value from the catalog at
implementation time.

---

## 1. Which reading of "tenants/domains" applies

The owner's request reads "other tenants/domains we own" as one thing. It is two, and the
evidence separates them cleanly.

### Reading A -- additional accepted domains inside the SAME Exchange Online tenant

**Verdict: already works today. Zero code.**

Evidence. The cloud query is built in
`Services/MessageTraceService.cs:434-450` (`GetCloudMessageTraceAsync`):

```
ps.AddCommand("Get-MessageTraceV2")
  .AddParameter("StartDate", startDate)
  .AddParameter("EndDate", endDate)
  .AddParameter("ResultSize", 2000)
  .AddParameter("ErrorAction", "Stop");
```

followed by the optional `SenderAddress`, `RecipientAddress`, `MessageId`, `Subject` and
`SubjectFilterType` parameters at `:440-450`. There is **no domain parameter, no
organization parameter, and no accepted-domain filter anywhere in the command**. Nothing
in the query narrows the result to one domain.

The only thing that scopes the query is the session it runs on.
`Services/ExoConnectionPool.cs:440-445` establishes that session:

```
ps.AddCommand("Connect-ExchangeOnline")
  .AddParameter("AppId", appId)
  .AddParameter("CertificateThumbprint", cert.Thumbprint)
  .AddParameter("Organization", organization)
```

`Organization` names the tenant, not a domain. Exchange Online's message trace is
tenant-scoped, so a trace issued on that session already covers **every accepted domain in
that tenant**, with no configuration and no per-domain registration.

Consequence: if the owner's "other domains" are additional accepted domains on the
existing tenant -- a second brand, a merged-in company's mail domain, a vanity domain --
**this item closes with no work at all**. How to confirm it without touching production
is in section 9 (the one-search check).

Caveat, labelled as such: this is an inference from the absence of a scoping parameter
plus Microsoft's documented tenant-scoped trace behaviour. It is not a measurement against
a tenant with two accepted domains, because this is a documentation task and no tenant was
queried. Section 9's check is what converts it to a measurement.

### Reading B -- genuinely separate Microsoft 365 tenants

**Verdict: real work, with one structural blocker that is an owner decision, not an
engineering one.** Sections 2 through 8 are entirely about this reading.

Evidence that the app is single-tenant by construction, not by accident:

- `Program.cs:232` registers the pool as `builder.Services.AddSingleton<ExoConnectionPool>();`
  -- one pool instance for the whole process.
- `ExoConnectionPool` holds exactly one connection identity:
  `internal readonly record struct ExoConnectionConfig(string AppId, string Organization, string CertificateSubject)`
  (`Services/ExoConnectionPool.cs:53`), read from a single set of module-config keys
  (`ConfigModuleKey = "ExchangeOnline"`, `ConfigOrganizationKey = "Organization"`, lines
  57-60) by `GetExoConfig()` (lines 122-139).
- The pool does not merely tolerate one tenant -- it **actively destroys** any runspace
  connected under a different one. See section 6.

### If both readings are live

They can be. The owner may own domains that sit inside the existing tenant *and* a
separate tenant from an acquisition. If so: the in-tenant domains are done (Reading A,
nothing to do), the separate tenant is Reading B, and the document below applies only to
the latter. The two must not be conflated in the answer to open question 1, because the
work differs by everything.

---

## 2. What each additional tenant costs the owner operationally

This is the part to price before anything is built. **Correcting a likely assumption
first:** Exchange Online auth in this app does **not** go through Delinea. It uses a
certificate in the host's own certificate store plus an app registration ID, both named in
module config:

- `FindCertificate(certSubject)` (`Services/ExoConnectionPool.cs:507-530`) searches
  `StoreName.My` under `StoreLocation.LocalMachine` first, then `StoreLocation.CurrentUser`,
  for a certificate with a private key matching the configured subject DN, and throws if
  none is found (line 529).
- `MessageTrace`'s own `DelineaSecretId` config field
  (`Modules/ModuleCatalog.cs:274`) is for the **on-premises** Exchange credential only --
  `GetOnPremMessageTraceAsync` consumes it via
  `GetModuleCredentialsAsync("on-prem message tracking")`
  (`Services/MessageTraceService.cs:515`). It has nothing to do with the cloud half.

So the per-tenant bill is:

| Item | Per tenant | Notes |
|---|---|---|
| Azure AD app registration | 1 | Needs the Exchange.ManageAsApp application role, admin-consented in that tenant. |
| Certificate | 1 public key uploaded to that tenant's app registration | The matching private key must be installed in the **deploy host's** certificate store, because `FindCertificate` reads the local store. Both the dev and prod hosts, if both are to trace that tenant. |
| Admin consent | 1 | Granted by a Global Administrator of the *other* tenant. This is the item most likely to be slow, because it needs a person with rights in a tenant the owner may not administer day to day. |
| Exchange role assignment | 1 | The service principal needs a role permitting `Get-MessageTraceV2` and `Get-MessageTraceDetailV2` (View-Only Recipients or similar) in that tenant. |
| Module-config entry | 1 set of AppId + Organization + CertificateSubject | Where this lives is section 3's config-shape problem. |
| Delinea secret | **0 for the cloud half** | Only needed if that tenant also has an on-premises Exchange the owner wants traced, which section 4 argues against. |
| ExchangeOnlineManagement module version | shared | Already required at 3.7.0 or later for the V2 cmdlets (`Services/MessageTraceService.cs:493`); no per-tenant cost. |

The certificate row is the one that is easy to under-price. A single shared certificate
*can* be uploaded to several tenants' app registrations, which keeps the host-side
installation to one private key; but that makes one certificate's expiry or compromise a
simultaneous outage or incident across every tenant. Separate certificates per tenant cost
more host-side administration and give independent blast radius. This is a real fork and
it belongs in open question 4, not in a plan's assumptions.

---

## 3. The authorization question, and the blocker

**The question for the owner:** should an operator who can trace in the primary tenant
automatically be able to trace in every other configured tenant, or should access be
granted per tenant?

**"Every tenant" is free. "Per tenant" is the blocker.** Here is why, from the code.

Policies are registered **once, at startup, from a static list**.
`Program.cs:110-113`:

```
builder.Services.AddAuthorization(options =>
{
    catalog.ConfigureAuthorizationPolicies(options, allowedGroups, adminGroups);
});
```

`ConfigureAuthorizationPolicies` (`Modules/ModuleCatalog.cs:58-118`) iterates `_modules` --
the hardcoded `RegisterAll()` list beginning at line 120 -- and calls `options.AddPolicy`
for each module's `MainPermission.PolicyAlias` (line 103) and each entry in its
`GranularPermissions` (lines 108-116). `GranularPermissions` is
`IReadOnlyList<ModulePermission>` on the descriptor
(`Modules/AdminModuleDescriptor.cs:17`), populated by C# collection initialisers in the
catalog. The same static list drives what the admin UI can even offer to grant, via
`GetConfigurablePolicyAliases()` (`Modules/ModuleCatalog.cs:42-56`).

There is **no `IAuthorizationPolicyProvider` anywhere in the repository** (verified: zero
matches across all source). ASP.NET Core therefore uses
`DefaultAuthorizationPolicyProvider`, which can only return policies that were added at
startup. A policy alias that does not exist at startup cannot be authorized against later.

A tenant list, however, **must** be runtime configuration. Repo-guidance invariant 7
forbids a source file naming a domain, host or tenant as behaviour. So the set of tenants
is known only after the config store is read, which is after policy registration. The two
facts are in direct conflict:

> **Per-tenant authorization cannot be expressed in the current model.** The static
> catalog cannot name tenants (invariant 7), and the runtime tenant list cannot create
> policies (no dynamic policy provider).

Three ways out, each with a cost the owner must accept:

**(a) All-or-nothing.** The existing `MessageTrace` `Access` alias
(`Modules/ModuleCatalog.cs:268-272`, `FailClosed: true`) governs tracing in every
configured tenant. Cost: zero code. Consequence: anyone who can trace primary-tenant mail
can trace the acquired company's mail. Whether that is acceptable is a business judgement,
not an engineering one.

**(b) Fixed anonymous slots.** Add a small number of tenant-agnostic granular aliases --
`MessageTraceSecondaryTenant1`, `...2`, and so on -- to the static catalog, and have config
bind tenant N to slot N. This satisfies invariant 7 literally, because no source file names
a tenant; the slot is a position, not an identity. Cost: the alias set is capped at
whatever number is chosen, the section-access UI shows meaningless slot numbers that an
admin must cross-reference against the tenant config to grant correctly, and a
misconfigured binding grants access to the wrong tenant silently. This option is honest but
ugly, and the silent-mis-binding failure mode is a fail-open shape that the Constitution's
fail-closed rule would want guarded. It also inherits the standing cost of any new granular
alias in this app: a brand-new alias denies **everyone** from the moment it deploys until a
group is stored against it, because `SectionAccessService.GetGroupsForSection` returns
`Array.Empty<string>()` for a key absent from the configured store -- see the chain quoted
at `docs/MessageTracePermissionSplit-Plan.md` sections 1-2, which establishes it for queue
item 4 and applies unchanged here.

**(c) A dynamic authorization policy provider.** Implement `IAuthorizationPolicyProvider`
so `MessageTrace:<organization>` style policies are minted on demand from the configured
tenant list. Cost: this is new shared authorization infrastructure. Per
`docs/ProjectConstitution.md` "Planning Rules", authorization changes require a written
plan; per "Deployment And Versioning", shared infrastructure changes bump the base app
version. It also has to be fail-closed for an unknown alias, which is exactly the property
the current `FallbackPolicy` (`Modules/ModuleCatalog.cs:81-84`,
`.RequireAssertion(_ => false)`) exists to guarantee and which a careless provider would
undermine. This is the largest single piece of work in the whole item, and it is
disproportionate to "let us also trace the other tenant" unless per-tenant access is a hard
requirement.

**Recommendation:** (a), unless the owner states that an operator must be prevented from
tracing a specific tenant. If that requirement exists, it should be stated plainly, because
it roughly triples the size of this item.

### The config-shape problem, which bites regardless of the option chosen

A tenant list is a repeating structure: each tenant needs AppId + Organization +
CertificateSubject together. The config surface has no repeating shape.
`Modules/ModuleConfigField.cs:18-25` is a flat record:

```
public sealed record ModuleConfigField(
    string Key,
    string Label,
    string Description,
    bool Required = true,
    bool IsSecret = false,
    string DefaultValue = "",
    ConfigFieldType FieldType = ConfigFieldType.Text);
```

and `ConfigFieldType` offers only `Text`, `OU` and `Boolean` (same file, lines 1-16).
Storage underneath is a flat string-to-string map per module:
`ModuleConfigService.GetValue(string moduleId, string key)` returns
`config.TryGetValue(key, out var value) ? value : null`
(`Services/ModuleConfigService.cs:60-64`), backed by
`GetModuleConfig` returning `Dictionary<string, string>` (lines 66-69).

There is no list field type, and no way to express "N groups of three fields". The
repository's existing precedent for a list is a delimited `Text` field -- see
`OnPremTargetDatabases` ("Comma-separated target mailbox databases") and
`ExcludedADGroups` ("Comma-separated AD groups") at `Modules/ModuleCatalog.cs:233` and
`:236`. Applying that precedent to a tenant list means one `Text` field holding something
like `appid|org|certsubject;appid|org|certsubject`, hand-parsed, with no per-field
validation in the UI -- a materially worse editing experience than the existing Exchange
Online config page, which validates the GUID, the dot in the organization, and the `CN=`
prefix individually (`Components/Pages/ExchangeOnlineConfig.razor:269-288`).

The honest statement: **the config surface does not support a per-tenant repeating shape
today.** Either a delimited text field is accepted with its validation loss, or a new
repeating field type is added to the shared module-config surface -- which is again shared
infrastructure, with the base-version bump that implies.

Environment neutrality is satisfiable either way, and must be: the tenant values live in
the config store, read at runtime, and never appear as a constant in source. The repository
already has the right precedent for tenant identity as runtime data -- the Graph modules
carry the Tenant ID inside the Delinea secret's own fields and extract it at call time, e.g.
`IntuneDeviceService.ExtractGraphCredentials` reading `fields.GetValueOrDefault("Tenant ID")`
(`Services/IntuneDeviceService.cs:75-88`). A design that hardcodes even one tenant
identifier is out of bounds under invariant 7 and must be rejected at review.

---

## 4. What happens to the on-premises half

This is the sharpest correctness risk in the whole item, and it is not obvious from the UI.

A trace runs **both** backends, unconditionally and in parallel.
`Services/MessageTraceService.cs:21-23`:

```
var responses = await Task.WhenAll(
    RunMessageTraceBackendAsync(() => GetChunkedCloudMessageTraceAsync(...), "Exchange Online"),
    RunMessageTraceBackendAsync(() => GetOnPremMessageTraceAsync(...), "On-prem"));
```

and their rows are merged into one flat table at `:28-57`, distinguished only by a
`Backend` string on each row (`Models/LookupModels.cs:35`, set to `"ExchangeOnline"` at
`Services/MessageTraceService.cs:478` and `"OnPrem"` at `:601`).

The on-premises half has exactly one target, and it is not per-tenant. The constructor
takes it from host configuration:

```
: base(exoPool, delineaService, logger, config["OnPremExchange:ServerUri"] ?? "", ...)
```

(`Services/MessageTraceService.cs:13`), stored as `_onPremServerUri`
(`Services/ExchangeServiceBase.cs:15, 33`) and used to open a single PSSession in
`ConnectOnPrem` (`Services/ExchangeServiceBase.cs:336-341`). `appsettings.json` is not in
source control (`.gitignore` lists `appsettings.json` and `appsettings.Development.json`;
only `appsettings.json.sample` is tracked), so this value is host-supplied -- correctly
environment-neutral, and correctly singular. Within that one on-premises organization the
transport servers are themselves discovered at runtime via `Get-TransportService`
(`Services/MessageTraceService.cs:645`), never named -- the existing code already honours
invariant 7 here.

**The failure mode if multi-tenant is added without touching this:** an operator selects
the second tenant, the cloud half correctly queries tenant B, and the on-premises half --
which has no notion of tenants -- queries the **primary organization's** transport logs
anyway and merges those rows into the result. The operator sees a single table that mixes
tenant B's cloud mail with tenant A's on-premises mail, labelled only `OnPrem` in a Backend
column. That is not an empty result or an error; it is a wrong answer that looks right, and
it is precisely Known Failure Class #2 (success aggregation presenting a partial or
contaminated result as a complete one) applied across an organizational boundary.

**Required behaviour, non-negotiable if Reading B proceeds:** for any tenant other than the
one the host's on-premises Exchange belongs to, the on-premises backend must be **skipped by
construction**, and the page must say so. Not "return no rows" -- skipped, with a visible
statement that the on-premises half was not searched for this tenant, so an operator does
not read the absence of on-premises rows as evidence that no on-premises mail exists.

The existing code already has the right vocabulary for this: `MessageTraceResponse.Warnings`
plus the `FailedBackends` / `IsPartial` distinction (`Models/LookupModels.cs:78-91`) exists
specifically so the page can distinguish "a backend failed" from "a backend returned
nothing". A third state -- "a backend was not applicable to this tenant" -- is a natural
extension of it and must not be collapsed into either of the existing two.

Note also the safety argument this forces. "The second tenant has no on-premises Exchange"
is an environment fact. Under invariant 7 a safety argument may not rest on this
environment's shape. The skip must therefore be driven by configuration -- which tenant, if
any, the configured `OnPremExchange:ServerUri` belongs to -- and must **fail closed**
(skip the on-premises half) when that association is absent or ambiguous, rather than
assuming the host's on-premises org matches whichever tenant is selected.

---

## 5. Audit

Every trace already audits. The cloud/on-prem summary path writes one record on success and
one on failure (`Components/Pages/MessageTrace.razor:817` and `:822`), and there are seven
`LogLookupAction` call sites on the page in total, covering the summary trace, header
analysis, per-message detail, the detail CSV download and the detail email job
(`Components/Pages/MessageTrace.razor:817, 822, 850, 855, 983, 991, 1092, 1097, 1174, 1179`).

**None of them records which tenant was searched, and none can today** -- there is no tenant
to record. With more than one tenant configured, every one of these audit rows becomes
ambiguous: `"action": "MessageTrace", "target": "<someone@example>"` no longer identifies
what was actually searched. A compliance question of the form "who looked at the acquired
company's mail?" becomes unanswerable from the audit log. Under the Constitution's
"Auditing And Tracing" rule that entries must include actor, IP, action, target and result,
an ambiguous target is a defect the moment a second tenant exists.

**Where it goes: the `extra` dictionary.** `AuditService.LogLookupAction` already accepts
one and merges it:

```
public void LogLookupAction(
    string performedBy,
    string ipAddress,
    string action,
    string target,
    bool success,
    string? errorDetail = null,
    string ticketNumber = "",
    Dictionary<string, object?>? extra = null)
```

(`Services/AuditService.cs:227-235`), with `MergeExtra(evt, extra);` at `:250`, after the
computed fields are set at `:237-248`. The ordering is deliberate and is itself guarded:
`ExchangeAdminWeb.Tests/AuditExtraChannelTests.cs` asserts both that
`LogLookupAction` still accepts `extra` (the required-methods list at
`ExchangeAdminWeb.Tests/AuditExtraChannelTests.cs:58-73`) and that every accepting method
actually merges it -- a guard written because `LogLookupAction` specifically was once left
without the merge (that file's remarks, lines 19-21).

So the change is mechanical and needs no audit-infrastructure work: pass
`extra: new Dictionary<string, object?> { ["tenant"] = <selected organization> }` at each of
the seven call sites. Two constraints on the value:

1. It must be the organization string read from runtime config for the selected tenant --
   never a literal, never a display name baked into source (invariant 7).
2. It must be written on the failure path as well as the success path. A denied or failed
   trace against the wrong tenant is exactly the event a compliance query wants to find, and
   the failure call sites (`:822`, `:855`, `:991`, `:1097`, `:1179`) are separate calls that
   would each need the argument. Adding it to the success path only would produce an audit
   trail that is complete for successes and silent for failures, which is worse than
   uniform ambiguity because it looks complete.

The background export job audits under its own action
(`Services/Jobs/MessageTraceDetailJobProcessor.cs:43`,
`AuditAction = "MessageTrace_DetailExport"`) and would need the same treatment; see
section 6's note on the job path.

---

## 6. The connection pool: can it hold two tenants at once?

**No. It is not merely single-tenant -- it actively destroys cross-tenant connections.**

The pool records the identity every pooled runspace connected under:

```
private ExoConnectionConfig? _connectedUnder;
```

(`Services/ExoConnectionPool.cs:81`). Every borrow re-reads config and compares:

```
DrainIfConnectionConfigChanged(current);
```

(`Services/ExoConnectionPool.cs:177`, inside `BorrowAsync`, immediately after the slot is
acquired). That method, at `:377-399`, reads in full-effect:

```
if (_connectedUnder is null || _connectedUnder.Value == current)
{
    _connectedUnder = current;
    return false;
}

previous = _connectedUnder.Value;
_connectedUnder = current;
...
DrainPoolCore();
return true;
```

and `DrainPoolCore()` (`:401-416`) increments `_configGeneration` and destroys every idle
runspace. Because `ExoConnectionConfig` is a record struct over
`(AppId, Organization, CertificateSubject)` (`:53`), **any change to `Organization` compares
unequal and triggers the drain.** The generation bump additionally causes every
still-borrowed runspace to be destroyed rather than pooled when it comes back, via the
first branch of `Return` (`:218-224`):

```
if (runspace.ConfigGeneration != Interlocked.Read(ref _configGeneration))
{
    DestroyRunspace(runspace);
    ...
}
```

The mechanism is `DestroyRunspace` (`:470-485`), which issues `Disconnect-ExchangeOnline`
and disposes the runspace.

This behaviour is correct and deliberate for its purpose -- it exists so that an Exchange
Online config change made by the *other* instance on the shared config database cannot leave
a runspace connected to the old tenant (the `scdi-1` review finding cited at `:78-81` and
`:369-376`). It is a safety property, not an oversight, and it is the exact property that
makes tenant switching structurally impossible: alternating traces between two tenants would
drain and fully re-establish the pool on **every single borrow**. `Connect-ExchangeOnline`
is explicitly described as costing "multiple seconds" (`:196-200`), and the chunked cloud
path issues up to nine sequential borrows for a 90-day search (`:346-356` of
`Services/MessageTraceService.cs`). The result would not be a slow feature; it would be a
feature that thrashes the shared pool for every other EXO module in the app.

**What multi-tenant actually requires: a keyed pool.** The single
`ConcurrentBag<PooledRunspace> _available` (`:62`) and single `SemaphoreSlim _slots` with
five permits (`:101`) become per-tenant, or a shared slot budget partitioned across tenants;
`_connectedUnder` becomes a per-key value; and `DrainIfConnectionConfigChanged` becomes a
per-key comparison so that reconfiguring tenant B does not drain tenant A's connections. The
callers change too: `RunWithRetryAsync` (`:254-259`) and the base-class helpers
`RunAsync` / `RunPooledQueryAsync` (`Services/ExchangeServiceBase.cs:47, 110`) currently
carry no tenant argument, and every EXO-backed service in the app flows through them.

That last point is the size of it: **`ExoConnectionPool` is shared infrastructure used by
every Exchange Online module**, so keying it is not a Message Analysis change. Per the
Constitution's "Deployment And Versioning", it bumps the base app version, and per
"Planning Rules" ("Exchange service or shared infrastructure refactors") it needs its own
written plan, separate from this one.

**Testability, stated precisely.** The prompt for this item said the pool is sealed and
cannot be unit-hosted; the accurate version is narrower and more useful. `ExoConnectionPool`
is `public sealed class` (`:55`) and so cannot be mocked by subclassing, but it **does**
carry a deliberate test seam: an internal constructor taking
`Func<ExoConnectionConfig, long, PooledRunspace>? connect` that "replaces the live
`Connect-ExchangeOnline` step" (`:88-93`), plus internal accessors `ConfigGeneration` (`:106`)
and `AvailableCount` (`:109`). Keying logic -- which key a borrow resolves to, that
reconfiguring one tenant drains only that tenant's runspaces, that slots are conserved
across keys -- is therefore genuinely unit-testable through that seam, and any plan should
promise those tests. What is **not** testable is the live path: an actual
`Connect-ExchangeOnline` against a second real tenant, and the services layered on the pool,
which the codebase repeatedly notes "cannot be unit-hosted"
(`Services/ExoConnectionPool.cs:291-293`, `Services/MessageTraceService.cs:112-116`). Those
are manual-acceptance items only (section 9).

**Background job path.** The detail export runs off the browser circuit through
`MessageTraceDetailJobProcessor`, resolved per job from a fresh DI scope
(`Program.cs:195-196` registering `IMessageTraceDetailSource` against the scoped
`MessageTraceService`; `Services/Jobs/MessageTraceDetailJobProcessor.cs:26-31`). A queued job
carries a serialized payload and runs later, with no circuit and no UI selection state. If a
tenant selector exists, **the selected tenant must be persisted into the job payload**, or a
job queued against tenant B will execute against whatever the default is -- silently
producing an export full of empty details. This is the same wrong-tenant failure mode as
section 7, arriving by email hours later, where it is least likely to be noticed.

---

## 7. What the UI becomes

A tenant selector on the Message Analysis search form, alongside the existing sender /
recipient / date / subject / message-ID inputs, feeding `RunRealtimeTrace`
(`Components/Pages/MessageTrace.razor:803-824`).

**The default is the decision that matters.** The current page has no selector and traces
the one configured tenant; whatever a selector defaults to becomes the behaviour every
existing operator gets without changing anything. Defaulting to the primary tenant preserves
today's behaviour exactly and is the conservative choice.

**The failure mode to call out explicitly.** A trace run against the wrong tenant does not
error. `Get-MessageTraceV2` on a tenant that never handled the message returns zero rows,
the merge produces an empty `Results` list, and the page renders what an operator reads as
"no mail found". The distinction between *this mail does not exist* and *you searched the
wrong organization* is invisible. Worse, the existing empty-result path is
deliberately quiet: `merged.Error` is only set when there are warnings to report
(`Services/MessageTraceService.cs:59-60`), so a clean, successful, empty, wrong-tenant trace
produces no error, no warning and no banner at all.

Three mitigations, all cheap, and a plan should commit to all three:

1. The selector is **always visible**, never collapsed behind an "advanced" toggle, and
   never a setting remembered from a previous session -- so the operator sees which tenant
   is selected at the moment they read the result.
2. The empty-result message **names the tenant searched**: not "No messages found" but "No
   messages found in <organization> between <start> and <end>". The organization string
   comes from runtime config, not a literal.
3. The result header states the tenant for a non-empty result too, so an operator scanning a
   populated table cannot mistake it for a different tenant's.

Beyond the search form, the tenant belongs on each result row and in the export. The result
model has no such field today -- `MessageTraceResult` (`Models/LookupModels.cs:23-38`)
carries `Backend`, `Server`, `MessageTraceId` and the rest, but nothing identifying an
organization -- so adding one is a model change that flows into the detail view, the CSV
built by `MessageTraceDetailReport.BuildCsv` (called at
`Components/Pages/MessageTrace.razor:1086`) and the emailed export.

**Out of scope, deliberately:** searching several tenants at once and merging the results.
It compounds the section 4 contamination risk across every configured tenant instead of one,
multiplies pool pressure by the tenant count, and makes the truncation and per-window
failure semantics (`Services/MessageTraceService.cs:358-407`) considerably harder to state
honestly. One tenant per search.

---

## 8. Scope, non-goals, and slices

### In scope (only if the owner confirms Reading B)

- A runtime-configured set of Exchange Online tenants, read from the config store.
- A keyed connection pool, as its own prerequisite plan.
- A tenant selector on Message Analysis, defaulting to the primary tenant.
- On-premises backend skipped-by-construction, and visibly so, for non-primary tenants.
- Tenant recorded in every Message Analysis audit row, success and failure alike.
- Tenant persisted into the detail-export job payload.

### Non-goals

- Multi-tenant support for any module other than Message Analysis. The keyed pool makes it
  *possible*; extending it is separate work per module, each with its own authorization and
  audit questions.
- Cross-tenant search in one query (section 7).
- Any on-premises Exchange in a second tenant's environment (section 4).
- Per-tenant permissions, unless the owner answers open question 3 in a way that requires
  them.
- Retiring or changing the existing single-tenant Exchange Online config page beyond what
  the tenant list requires.

### Slices, if the verdict is favourable

Each slice is one commit with its record, per `AGENTS.md`. Slice 0 is a gate, not work.

- **Slice 0 (no code): confirm Reading A first.** Run the one-search check in section 9
  against an accepted domain that is already in the tenant. If the trace returns that
  domain's mail, and every domain the owner meant is in that tenant, **stop -- the item is
  closed** and slices 1 onward are never drafted.
- **Slice 1: keyed connection pool.** Its own plan, its own approval, base app version bump.
  Behaviour-neutral for a single-tenant configuration -- one configured tenant must produce
  byte-identical pool behaviour to today, proven by the existing pool tests continuing to
  pass unchanged.
- **Slice 2: tenant configuration.** The runtime tenant list, in whichever config shape the
  owner accepts from section 3. Includes the association that says which tenant, if any, the
  host's on-premises Exchange belongs to -- fail-closed when absent.
- **Slice 3: service plumbing.** `MessageTraceService` takes a tenant; the on-premises
  backend is skipped by construction for non-primary tenants; the response carries the new
  "not applicable to this tenant" state distinctly from failed and from empty.
- **Slice 4: UI and audit.** Selector, defaults, tenant-named empty-result and header text,
  tenant on the result row and in the CSV, tenant in all seven `LogLookupAction` calls, and
  tenant persisted into the export job payload.
- **Slice 5 (only if open question 3 requires it): per-tenant authorization.** Its own plan.
  Whichever of section 3's options (b) or (c) the owner chose.

---

## 9. Verification

### The Reading A check (slice 0, manual, read-only)

On the existing Message Analysis page, with no code change: run a trace with a recipient
address in a **second accepted domain of the current tenant** and a date range known to
contain mail. If rows come back, Reading A is confirmed as a measurement rather than an
inference, and the in-tenant half of the owner's request needs nothing. This reads mail
metadata the operator is already authorized to read and writes the ordinary audit record;
it changes nothing.

### Automated, for any slice that lands code

Per `.agents/repo-guidance.md`:

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`

New tests are required for new or rewritten services. Each new guard must be proven
non-vacuous: revert the fix, watch the test fail, restore it.

What can be covered automatically: keyed-pool key resolution, per-key drain isolation and
slot conservation, through the internal `connect` seam (`Services/ExoConnectionPool.cs:88-93`);
the tenant-to-on-premises association and its fail-closed behaviour, as a pure function; the
audit `extra` payload shape, alongside the existing `AuditExtraChannelTests`; the
"not applicable" response state in the merge.

What cannot: anything requiring a live `Connect-ExchangeOnline`, and the page itself. There
is no bUnit harness in this repository -- `Program.cs:200-202` notes page logic is
deliberately pushed into testable services "(the repo has no bUnit harness)".

### Manual acceptance checklist

Nothing below is automatable; all of it must be run and reported, or explicitly reported as
not run.

1. With exactly one tenant configured, Message Analysis behaves identically to today:
   selector shows the one tenant, cloud and on-premises both searched, results merged as
   before.
2. Second tenant configured. Trace a message known to exist in tenant B. Rows return, and
   the header names tenant B.
3. Same trace, tenant A selected. **Zero rows, and the empty-result text names tenant A** --
   the wrong-tenant failure mode is visibly distinguishable from "no mail".
4. Tenant B selected: the on-premises backend is reported as not searched, in words, and no
   on-premises row from tenant A's organization appears in the table.
5. Tenant A selected: the on-premises backend IS searched, and on-premises rows appear as
   before.
6. Audit rows for runs 2 through 5 each carry the correct tenant, in
   `logs/` via the Admin Event Log page.
7. A **failed** trace in tenant B (for example, with tenant B's certificate deliberately
   absent from the host store) produces an audit row that still carries tenant B, and an
   error naming the tenant.
8. Detail export queued against tenant B: the emailed export contains tenant B's details,
   and its audit row names tenant B.
9. Alternating traces between tenants do not degrade other Exchange Online modules --
   open Mailbox Permissions or Recipient Lookup immediately after and confirm normal
   response time (the pool-thrash regression from section 6).
10. Reconfiguring tenant B's connection settings does not disturb an in-flight or subsequent
    tenant A operation.

### Versioning

- Message Analysis behaviour changes (selector, on-premises skip, audit tenant, export
  payload): module-scoped. Bump `MessageTrace`'s `Version` in `Modules/ModuleCatalog.cs` to
  **current + 1**. Read the live value at implementation time; another agent is editing that
  field concurrently, which is why no literal appears anywhere in this document.
- The keyed connection pool: shared infrastructure. Bumps the base app version
  (`<VersionPrefix>`, `AssemblyVersion` and `FileVersion` in `ExchangeAdminWeb.csproj`) to
  **current + 1**, per `docs/ProjectConstitution.md` "Deployment And Versioning"
  ("Shared infrastructure changes bump the base app version").
- A dynamic authorization policy provider, if option (c) is ever chosen: also shared
  infrastructure, also a base app version bump.
- Both rules fire independently. A slice touching both layers gets both bumps.

---

## 10. Open questions for the owner

Each is answerable in one line. Questions 1 and 2 gate everything else.

1. **Which domains or tenants do you actually mean?** Name them, or say "the ones on the
   existing tenant".
2. **Are they accepted domains on the tenant we already connect to, or separate Microsoft
   365 tenants?** If the former, this item is closed with no work (section 1, Reading A).
3. **Should an operator who can trace today be able to trace in every configured tenant, or
   must access be per tenant?** "Every tenant" is free; "per tenant" is the blocker in
   section 3 and roughly triples the item.
4. **One certificate shared across all tenants' app registrations, or one per tenant?**
   Shared is less host administration and a single point of simultaneous failure; separate is
   more administration and independent blast radius.
5. **Does any additional tenant have its own on-premises Exchange you would want traced?**
   The plan assumes not; if yes, section 4's skip becomes a second on-premises endpoint and
   a second Delinea secret instead.
6. **Can you obtain Global Administrator consent in the other tenant, and roughly how long
   does that take?** This is the item most likely to stall implementation after the code is
   done.
7. **For the tenant config shape: accept a delimited text field with weaker validation, or
   fund a repeating field type on the shared module-config surface?** (Section 3.)
8. **Confirm one tenant per search, not a merged cross-tenant search** (section 7's
   non-goal), or say you want the merged view and accept the contamination and truncation
   consequences.
