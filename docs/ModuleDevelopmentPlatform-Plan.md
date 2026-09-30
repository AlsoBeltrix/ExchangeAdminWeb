# Module Development Platform Plan

Status: Draft - awaiting owner approval; implementation has not started.
Owner: Michael
Revision: 1 (2026-09-30)
Assessment baseline: `5bb19ad70fd2d293b5d7a5565d9a122303dc5aa1`

Sections 1-5 contain the owner's goal and proposed scope, acceptance criteria,
failure behavior, and rollback. The proposals are not approved requirements yet.
This document is the canonical scope and implementation checklist for this work.

## 1. Goal

Owner's original request:

> I need a way for multiple agents to work on this app simultaneously without stepping on each other's work. assess and recommend a solution.

Owner's clarification:

> I need to understand what about the CODE and the way the app functions would need to change to enable more rapid development of modules. no git tricks.

Make a module an independently buildable feature with its own registration,
pages, workflows, models, styles, and tests. Give module authors a small, stable
host API and working development/test support so that normal feature work does
not require changes to shared application behavior. Prove the approach with
Service Health and Risky Users before extending the migration. Keep the deployed
product as one application with its existing operational security model.

The desired authoring loop is: define a module, implement its domain operations,
compose its UI, and run its own tests. Navigation, permission enforcement,
configuration access, credential resolution, auditing, and common interaction
behavior come from tested platform components.

## 2. Non-goals and scope boundaries

- No worktrees, new branches, shared-checkout locking system, or agent orchestrator.
  This is a source architecture and developer feedback plan.
- No runtime module upload, ZIP import, arbitrary assembly loading, separate module
  deployment, hot replacement, microservices, or new package distribution service.
  The 2026-07-22 module packaging/import deferment in
  [decisions](../.agents/decisions.md) remains in force. The current request
  authorizes drafting development boundaries, not reopening that distribution work.
- No wholesale module migration. Only Service Health and Risky Users become feature
  projects in this plan. Other modules remain supported through a legacy adapter.
- No Migration module work, True Last Logon implementation, or change to the active
  queue's priority. Those scopes remain owned by [state](../.agents/state.md).
- No new Service Health public-status feature. Its existing separate draft remains
  separate. Preserve the implemented tenant-status and load-feedback behavior.
- No new Graph permissions, PAM provider, credential sharing, ticket policy,
  ServiceNow behavior, protected-principal exception, or authorization rule.
- No database split, configuration schema migration, or change to the shared
  dev/prod configuration database. Development fixtures use disposable data.
- No universal form/table generator, application-wide UI redesign, or replacement
  of all source-based tests. Extract only components exercised by the pilots.
- No automatic approval, deployment, live directory/tenant mutation, or reviewer
  dispatch. The existing authorities and verification gates still apply.

## 3. Acceptance criteria - proposed for owner approval

- **AC1 - Independent builds.** Each pilot's production project and tests build and
  run against the module API, shared UI/runtime as needed, and test support without
  referencing the web executable or another feature module. A deliberately broken
  unrelated fixture module does not break the selected module's build/test loop.
  The assembled application still requires all included modules to build.
- **AC2 - One module definition.** Each pilot owns its descriptor, version, service
  registrations, optional implemented capabilities, and tests. Host composition
  generates the catalog and service/job registration from those definitions.
  Ordinary pilot edits require no changes to `Program.cs`, the legacy catalog
  list, shared settings markup, or a central list of module-specific tests.
- **AC3 - Validated composition.** Duplicate IDs, routes, policy aliases, invalid
  dependencies, incompatible contract versions, and invalid job registrations are
  rejected before requests or jobs are served. All existing modules remain present
  exactly once. A job registration constructs its processor through a fresh scope
  and cannot register only the dispatch mapping or only the DI service.
- **AC4 - Real shared contracts.** Pilot production code consumes public module API
  types rather than host internals or local host stubs. Shared adapters use the
  existing config/PAM/Graph implementations. A module resolves only its configured
  credential, with no ambient or other-module fallback. Existing Graph continuation
  URL restrictions remain enforced.
- **AC5 - Authorization equivalence.** Direct routes, page entry, and operation
  execution retain the existing enabled-module and permission checks. Missing or
  unreadable grants deny. Tests cover permission revocation and disablement after
  page load, main plus granular permissions, SID-based dynamic groups, and the
  distinct AdminGroups system-module rule. Test hosting exercises the same policy
  construction and handlers used by production.
- **AC6 - Testable operations.** Pilot workflows can run through application
  services without a Razor page. Risky Users' protected write entry point enforces
  its existing ticket, target-protection, and servicer rules, and uses trusted
  actor/IP context. Audit and notification failures do not rewrite the backend
  outcome. Results distinguish a refusal before dispatch, a confirmed operation
  failure/success, and an indeterminate dispatched write; there is no automatic
  mutation retry. This last distinction is an explicit improvement in the pilot,
  not a claim that its current boolean result already provides it.
- **AC7 - Usable shared UI.** The pilots use small shared header/version and
  operation-state components, plus confirmation/ticket and result primitives where
  applicable. Stable row identity, conflicting-action prevention, stale-response
  handling, and recovery after error/cancellation have behavioral coverage. Domain
  presentation remains module-owned; existing routes and operator actions survive.
- **AC8 - Safe independent preview.** A development host loads a selected module
  with fake backends, test identities, and disposable storage. It exercises real
  platform authorization and command behavior, makes no live external calls, and
  does not read installed configuration or real secrets. Production publishing
  excludes the development host, fakes, fixtures, and test authentication.
- **AC9 - Pilot parity.** Service Health retains its cache, explicit refresh,
  sanitized incident content, audited reads, and visible load/error behavior.
  Risky Users retains its complete-results/ceiling distinction, direct lookup,
  labels, permissions, cloud-only protection checks, and per-target outcomes.
  Reads remain audited without alert email. Existing config keys, IDs, aliases,
  audit action names, and enabled defaults remain compatible.
- **AC10 - Effective tests.** New module tests cover workflows and rendered
  components. Browser tests cover actual DOM/event behavior. Host tests cover the
  assembled catalog, real adapters, routes, and published assets. Source guards
  are removed only when an equivalent or stronger behavioral test demonstrably
  catches their failure. No existing coverage floor or test discovery is weakened.
- **AC11 - Repeatable authoring.** A starter creates a module definition, page,
  workflow, test project, and fake-backend examples without production host stubs.
  A disposable third module proves the instructions and composition mechanism.
  New module setup may add a mechanical solution/project entry; ordinary feature
  changes stay within that module. No application-code snippets are pasted into
  `Program.cs` or `ModuleCatalog.cs`.
- **AC12 - Compatible release and rollback.** The full solution, format, script,
  and coverage gates cover all new projects. One publish contains the app and
  production module assets. No runtime data/config migration is required. Module
  and base-app versions follow existing rules, with the module definition becoming
  the canonical version source for each migrated module.

## 4. Failure behavior - proposed for owner approval

| Failure | Operator/developer sees | Resulting state |
| --- | --- | --- |
| Selected module does not compile | Error from that module's build | Its preview is unavailable; unrelated module projects can still build. Full app release is blocked. |
| Composition has duplicate identity/route/policy, missing dependency, invalid registration, or unsupported API version | Actionable startup/test error naming the module and violated contract | Host does not serve a partially validated security configuration. Existing deployed app is unaffected until an authorized deployment. |
| Module config or credential cannot be read | Module unavailable or operation refused, with sanitized explanation | No privileged fallback; no write dispatched. A shared authorization/config-store failure keeps existing fail-closed behavior. |
| Permission revoked, module disabled, ticket refused, or protection check unavailable | Explicit refusal attached to the requested target | Backend mutation count is zero; audit follows the existing policy. |
| Backend read fails | Error or explicitly incomplete result, according to the module's existing rules | Failure is not rendered as empty data, healthy status, or an authoritative negative result. |
| Mutation fails before dispatch / has known result / loses its response after dispatch | Distinct refused, failed/succeeded, or outcome-unknown result | No speculative retry; caller is not told that a possibly applied write never happened. |
| Audit or notification fails after execution | Existing logging/warning path records the secondary failure | Backend result remains intact; no repeat write and no secret-bearing error payload. |
| Duplicate click, obsolete response, navigation, or cancellation | Consistent busy/result state; obsolete response does not overwrite current state | Operation context remains bound to the original target and actor. Cancellation is not proof that a dispatched write was undone. |
| Preview is given real config, credentials, or a live transport | Development startup/test refusal | No access to installed dev/prod data or external systems. |
| Module test/project is missing from solution or published assets are missing | Composition/publish verification fails | Release blocked; no silent reduction in verification. |

## 5. Rollback and blast radius - proposed for owner approval

The platform affects composition, authorization wiring, routing, and shared
services, so its blast radius includes the whole host even though only two modules
are migrated. Module boundaries are maintainability and compilation boundaries;
they are not a security sandbox or process-failure boundary inside the trusted app.

Land the foundation and each pilot as separate verified slices. Keep legacy
registration available for unmigrated modules. Do not combine a pilot migration
with unrelated fixes, new permissions, a schema change, or another module's cleanup.

Rollback is the previous verified complete application artifact through the existing
authorized deployment/rollback procedure. Do not restore an older configuration
database: configuration identity and storage remain compatible. Never load old and
new implementations of the same pilot simultaneously. Source rollback restores a
slice together with its project references, registration, and test relocation;
reversing individual files across that boundary is not a valid rollback.

Production deployment and live write acceptance are separate, named approvals.
The existing dev/prod shared database must not be used for development fixtures.

## 6. Design sketch

### 6.1 Evidence and existing foundations

The following were inspected at the assessment baseline. They identify the reason
for each change, not a request to repair unrelated behavior.

| Evidence | Architectural implication |
| --- | --- |
| `Modules/ModuleCatalog.cs:120`, `RegisterAll()` | Every descriptor/version shares one source list; keep the catalog as an aggregate and move pilot definitions beside their code. |
| `Program.cs:95`, processor map; `:163` onward, module service registrations | Composition currently has several separately maintained integration points. |
| `ExchangeAdminWeb.Tests/BulkJobProcessorWiringTests.cs:8` | Existing test explicitly documents the two-registration failure class. Build one registration primitive that supplies both. |
| `Services/Jobs/IBulkJobProcessor.cs` | Reuse the existing module/runner separation. Do not replace the runner, FIFO policy, job database, or startup reconciliation. |
| `Services/RiskyUsersService.cs:41`, `Services/ServiceHealthService.cs:50` | Both modules assemble Graph credentials/client access from concrete host services. Replace duplicated bootstrap with module-bound adapters. |
| `Components/Pages/RiskyUsers.razor:800` | Write orchestration lives in a component. Its denial branches call `Refuse(...)` and `return`; notification exceptions are caught in `finally`. Preserve those policy outcomes when extracting the workflow. |
| `Components/Pages/ModuleConfig.razor:693` | Servicer configuration uses an explicit list because metadata alone cannot prove enforcement. Replace only the pilot entry with a tested implemented-capability registration. |
| `Components/Shared/ModuleVersion.razor`, `TicketNumberInput.razor` | Existing useful UI components inject concrete host services. Adapt them to public contracts rather than creating divergent copies. |
| `Authorization/GroupAuthorizationHandler.cs` | The requirement and handler share this file. Dynamic groups use SIDs; static AdminGroups and Windows-principal matching have different behavior. Preserve both during extraction. |
| `ExchangeAdminWeb.Tests/RiskyUsersPageTests.cs:16`, `ClickGateRegistry.cs:4716` | Source assertions explicitly lack behavioral rendering proof; registry entries include source-position fingerprints. |
| `Components/Routes.razor:4`, `Program.cs:415` | Current routing targets the host assembly. Module assemblies must be registered for direct requests and interactive navigation. |
| `ExchangeAdminWeb.csproj`, `ExchangeAdminWeb.Tests/ExchangeAdminWeb.Tests.csproj` | The current test project references the web executable; a separate module test cannot inherit that dependency. Root recursive source globs must exclude new project trees. |

The module guide/spec still carry a 2.0 document version and June verification
headers; the guide's stated host baseline is 2.3.22, while the assessed project is
2.24.0. Treat that freshness gap explicitly. Current code and the Constitution
govern the migration; refresh the relevant authoring documentation in S7.

### 6.2 Project and dependency boundaries

Proposed source layout (new paths are proposals, not existing artifacts):

```text
ModulePlatform/
  Api/           descriptors, contracts, shared operation/identity DTOs
  Runtime/       composition validation, authorization and operation machinery
  UI/            Razor class library of shared components
  Testing/       fake adapters, fixtures and common contract test support
  DevHost/       selected-module preview, development/test use only
FeatureModules/
  ServiceHealth/
    src/         Razor class library: definition, pages, workflows, models, CSS
    tests/       module tests referencing its src project and test support
  RiskyUsers/
    src/
    tests/
```

The existing web project remains the production host. It references Runtime/UI
and the compiled production modules. Modules reference Api/UI and their own
necessary libraries; they cannot reference the web executable, host `Services`,
or another feature implementation. Runtime and UI reference Api, never a feature.
Testing and DevHost have no incoming dependency from a production project.

Use normal project references and Razor Class Libraries. Add explicit exclusions
for `ModulePlatform/**` and `FeatureModules/**` to the root web project's default
compile/content items so source and test files are not compiled or published twice.
Keep each module's test sources outside its production project directory.

The host can include production projects by the fixed
`FeatureModules/*/src/*.csproj` convention; discovery inspects only compiled,
referenced module assemblies. It must not search arbitrary DLL directories or
accidentally discover test fixtures. Module test projects must also be present in
the solution; a completeness test checks the convention against solution entries.
No extra registry of each module's individual test methods is introduced.

Razor library support and both routing surfaces are documented by Microsoft:
[RCLs](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/class-libraries?view=aspnetcore-10.0)
and [routing across assemblies](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/routing?view=aspnetcore-10.0#route-to-components-from-multiple-assemblies).

### 6.3 Definitions and composition

Introduce a small `IAdminModule` contract: a descriptor and a registration method
using a constrained module registration builder. The descriptor remains the source
of ID, route, display information, version, dependency, permissions, and config
fields. Registration binds that identity to services and implemented capabilities.
Do not duplicate the module ID in independent job/settings registries.

Build and validate the complete legacy-plus-new descriptor set before generating
authorization policies. Preserve current alphabetical ordering and validation of
duplicate IDs/routes/aliases and dependency cycles. Add contract-version and
registration validation. Discovery must not construct backend services or contact
PAM, Graph, AD, or Exchange merely to enumerate metadata.

Keep the existing `ModuleCatalog` entry point as a compatibility facade for legacy
consumers/tests. Remove only the two migrated descriptor entries from its legacy
list; all other descriptor values remain unchanged. The aggregate catalog remains
the source for enumeration, navigation, authorization, and admin configuration.

A single `AddJobProcessor<T>()` registration must bind the module ID, register the
scoped processor, and supply the runner mapping. Reuse `IBulkJobProcessor` and its
existing payload/outcome contracts, moving pure shared types to Api where needed.
Exercise this extension through synthetic test processors; neither pilot acquires
new background work. Existing processors can use the same helper through legacy
composition without changing their execution behavior.

Expose optional settings through registered, executable capabilities. For Risky
Users, the servicer settings entry requires the actual protection implementation
and passing denial/override tests. Do not replace the present list with an
unchecked `SupportsServicing = true` flag. Other legacy settings special cases,
including AD Attribute Editor, remain in the legacy path.

### 6.4 Public API and adapters

Define only contracts required by the pilots and existing job seam:

- Read-only catalog/config access bound to a module identity.
- Graph client access bound to that module's configured secret.
- Trusted operation context: authenticated principal, actor, IP, target, ticket,
  correlation ID, and cancellation. Capture immutable operation values before
  awaiting work; do not read another circuit's later mutable UI state.
- Authorization/config readers and protected-target/servicer evaluation.
- Audit, operation trace, notification, and ticket validation adapters.
- Explicit read/operation outcomes and the existing job extension contracts.

Existing host implementations stay authoritative: SQLite repositories,
`ModuleConfigService`, `ModuleCredentialService`, `DelineaService`,
`GraphTokenClient`, protected-principal services, audit/email, and the job runner.
Thin host adapters implement the new contracts. Production modules receive
module-bound Graph access, not PAM secret material or a method that selects an
arbitrary other module's credential. No duplicate token, retry, or URL validation
implementation is added.

Extract reusable authorization construction/handler behavior into Runtime using
narrow catalog, enablement, and section-access readers. Host adapters delegate to
the existing stores. Preserve the SID/name distinctions and shared membership
checker used by live and job authorization. The preview uses fixture readers with
that same engine, never an always-allow replacement.

Preserve dependency lifetimes explicitly. Service Health's shared cache remains
singleton; circuit identity and operation context remain scoped. Singletons must
not capture scoped actor services. Job processors retain their per-job scope.

### 6.5 Workflows and common execution

Keep module domain logic in module-owned handlers. A small shared executor owns
trusted context, the enforced authorization call, audit/trace sequencing, and
secondary-failure handling. Module registrations supply named action policies and
the applicable ticket/protection/notification adapters. A page calls a typed
operation entry point; it cannot obtain the raw mutation handler and skip gates.
This is a trusted code contract enforced by composition and tests, not an in-process
security sandbox.

For Risky Users, preserve the current action order and semantics: bind the immutable
target/action/ticket; check permission and ticket; resolve/check the actual target
(including cloud-only user rows); apply the module's servicer rule; revalidate
authorization at the write boundary; dispatch the single Graph action; record its
outcome and required notifications. Failed or ambiguous protection resolution
refuses. No unrelated group-protection exception is imported from another module.

The current Risky Users ticket path checks nonblank input and calls
`ServiceNow.ValidateTicketAsync`. Adapt that existing behavior. Do not silently
replace it with the newer `ITicketValidator` policy and introduce/change a
`ValidateTickets` switch. New ServiceNow functionality is outside this plan.

Carry backend outcome separately from audit/notification delivery. Do not classify
an exception after dispatch as proof that nothing changed. The module backend
adapter supplies dispatch/result knowledge; the executor cannot infer it from a
generic thrown exception. Preserve audit names and the servicer note on success.
Reads retain their existing audit-only policy.

### 6.6 UI and test/development support

Adapt the existing version/header and ticket primitives to Api contracts. Extract
operation-state, confirmation, and result components only as the pilots need them.
Keep module-specific columns, incident formatting, and remediation wording local.
Use stable identity for rendered rows; keep selection/paging behavior only where
the pilot already offers it. Do not add bulk actions to demonstrate a component.

Introduce rendered component tests with a pinned bUnit version compatible with the
repo's .NET/xUnit setup, and browser tests for DOM/event behavior. Microsoft's
[Blazor testing guidance](https://learn.microsoft.com/en-us/aspnet/core/blazor/test?view=aspnetcore-10.0)
distinguishes component testing from behavior requiring a real browser. Retain
existing guard coverage until replacements fail for the corresponding defects.

DevHost references a selected module project supplied at build time, plus Runtime,
UI, and Testing; it does not reference the production host or all feature modules.
Default fixture scenarios include successful/empty/error/partial reads, missing
config, denied/revoked permissions, protected targets, failed/unknown writes, and
notification failure. Use test identities with meaningful group claims, fake
transports that reject unexpected requests, a temporary content root, disposable
SQLite files, and local audit/notification sinks. No inherited real appsettings,
user secrets, environment-supplied credentials, PAM adapter, or live network path.
Choose a local port through launch arguments so previews can coexist.

The starter is a local source template, not a downloadable runtime module package.
It emits real public API calls and tests; test doubles live only in test projects.
Update the legacy package validator/documentation to distinguish existing snippet
contributions from the new first-party module-project path. Do not add import or
installation behavior to the validator.

### 6.7 Pilot choice and compatibility

- **Service Health** proves independent reads, Graph/config adapters, cache lifetime,
  sanitized HTML, CSS assets, routing, and the existing deferred initial load.
- **Risky Users** proves granular authorization, mandatory ticket behavior,
  cloud-only protection, servicer configuration, audit/notification sequencing,
  typed write outcomes, pagination, and direct lookup.

The pair intentionally shares Graph access, avoiding a simultaneous rewrite of
the Exchange/AD infrastructure. EXO/AD modules and their inherited service base can
adopt later under a separate scope. This pilot does not establish that every
current module can be moved without additional contracts.

Preserve the landed behavior in `ServiceHealth-Plan.md`,
`ServiceHealthLoadFeedback-Plan.md`, `RiskyUsersModule-Plan.md`, and
`RiskyUsersCompleteResults-Plan.md`, plus current code changes newer than those
plans. Do not treat historical versions/verification claims as fresh measurements.

## 7. Task breakdown

Each slice includes its tests and required durable records. Dependencies are
explicit; the platform foundation is established before independent module work.

| Slice | Work and exit evidence | Depends on | ACs |
| --- | --- | --- | --- |
| S0 | Re-read the baseline and active scopes; record pilot IDs/routes/config keys/audit names, current behavior tests, and build/test timings. Add characterization tests only where observable behavior lacks coverage. Identify exact source guards and publish assets that must move. | Owner approval | 5, 9, 10, 12 |
| S1 | Create Api/Runtime/UI project boundaries and root item exclusions. Introduce public descriptor/read/context contracts and thin host adapters; extract shared authorization machinery without changing its rules. Prove legacy host behavior and dependency direction. | S0 | 1, 4, 5, 12 |
| S2 | Add module definition discovery, legacy catalog facade, composition validation, module-owned registration, and atomic job registration helper. Wire both Blazor routing surfaces. Test synthetic modules, policy collisions, DI lifetimes, processor construction, and reference/solution discovery. | S1 | 2, 3, 5, 10, 12 |
| S3 | Build Testing/DevHost, the shared Graph adapter, common operation execution, and initial UI primitives. Use synthetic operations to prove denial-before-write, protected-target/servicer gates, trusted context, outcome handling, and complete offline behavior. Do not wait for a pilot UI to test the security machinery. | S1-S2 | 4-8, 10 |
| S4 | Move Service Health into its module/test projects; replace concrete host dependencies with adapters. Preserve singleton cache, sanitation, load feedback, public route, and assets. Retain host integration checks while moving domain/component tests locally. | S3 | 1, 2, 4, 5, 7-10, 12 |
| S5 | Move Risky Users into its module/test projects; extract the protected command workflow and replace its shared settings-list entry with the implemented capability. Preserve current read/write policies and UI semantics; add explicit unknown-write outcome. | S3 | 1, 2, 4-10, 12 |
| S6 | Verify both pilots together with legacy modules; run publish/layout/static-asset checks and full solution gates. Make test discovery and security coverage aggregation account for all projects. Demonstrate independent builds with an unrelated broken fixture. | S4-S5 | 1-10, 12 |
| S7 | Add the starter and prove a disposable third module. Update the module spec, guide, validator as necessary, and repo-specific test/version/source-location guidance. Record measured feedback results and remaining authorized manual acceptance. | S6 | 10-12 |

S4 and S5 have no module-to-module implementation dependency once S3's contracts
are stable. They can be implemented independently within their directories. A new
shared API requirement is a platform change with its own tests, not permission to
reach into another module or alter shared behavior opportunistically.

S7 documentation changes include the Constitution's source-location wording only
where needed to point to module-owned definitions; they must not weaken its rules.
Repo guidance must recognize colocated module tests while retaining the full
solution gate. Toolkit-owned AGENTS/skills/playbooks are outside this work.

## 8. Test plan and verification

### 8.1 Acceptance evidence

| AC | Required proof |
| --- | --- |
| 1 | Build and test each pilot project directly with no host/other-feature reference in its evaluated dependency graph. In an excluded disposable fixture, introduce a compile failure in module B and show A remains buildable; B and a composition including B must fail. |
| 2 | Edit a pilot descriptor/version and operation inside its directory; aggregate catalog, page version, and behavior reflect it without editing host lists. Scan registrations to prove the old implementation is not also included. |
| 3 | Negative composition cases for duplicate identities/routes/aliases, missing/cyclic dependency, unsupported contract version, mismatched/duplicate job processor, and missing DI dependency. Resolve scoped processors and prove both registration paths are supplied together. |
| 4 | Adapter tests for module A vs B credentials/config, missing/corrupt settings, PAM failure, secret-field failure, and existing allowed/rejected Graph continuation URLs. Transport assertions prove refusal before prohibited network access; logs carry no fixture secret. |
| 5 | Exercise actual authorization policies/handlers: direct navigation, main/granular grants, absent/unreadable grants, dynamic SID matching, static system groups, disabled dependencies, and revocation while an operation waits before its write. Assert zero backend writes on denial. |
| 6 | Call the workflow without a page. Cover missing/invalid ticket, unreadable protection, cloud-only protected user by address/object ID, ambiguous resolution, servicer refusal/allow with audit note, success/failure/unknown write, and failing audit/email. Assert exact write count and original target/actor/IP, including overlapping sessions. |
| 7 | Render and interact with shared components: row reorder/filter/page changes, conflicting clicks, ticket input/Enter, confirmation, stale response, failed load, cancellation, and busy-state recovery. Real-browser tests verify native checkbox/input state and JS interactions where present. |
| 8 | Preview every fixture scenario offline. Sentinel real-config/secret values and disallowed endpoints must never be consumed. Inspect production dependency closure/publish output for absence of fakes, test auth, fixtures, and DevHost. |
| 9 | Port existing pilot service tests; add rendered behavior equivalence for caching/refresh, sanitized content, load feedback, complete/partial/error/empty distinctions, lookup, labels, and per-row outcomes. Preserve policies, config keys, audit naming and read notification policy. |
| 10 | Full solution discovers every module test project. Replace each source guard only after its replacement fails under a targeted defect probe. Keep assembly/publish/routing and real-adapter integration tests in the host suite. Coverage includes moved security code. |
| 11 | Generate a temporary third module, compile it, render it in DevHost, discover it in a test composition, and run its tests using only the documented API. Verify no pasted host snippets, production stubs, or edits to another module are required. |
| 12 | Full Release build/test/format and applicable script/coverage gates; publish to a disposable local destination and exercise routes/assets. Compare config compatibility and document the previous-artifact rollback procedure without touching a live installation. |

Prove new behavioral tests bite: temporarily remove or invert the relevant fix/guard,
observe the intended failure, restore it, and rerun. Use owned fixture inputs or an
explicitly scoped local edit; do not restore whole shared files over another task's
work. This proof changes no approval or cleanup policy. Preserve guards until their
replacement is demonstrated, including ClickGate re-anchoring when still applicable.

### 8.2 Commands and required checks

Fast module checks supplement the repo's completion gates; they do not replace them:

```powershell
dotnet build FeatureModules/ServiceHealth/src/ExchangeAdminWeb.Modules.ServiceHealth.csproj -c Release
dotnet test FeatureModules/ServiceHealth/tests/ExchangeAdminWeb.Modules.ServiceHealth.Tests.csproj -- xUnit.MaxParallelThreads=4
dotnet build FeatureModules/RiskyUsers/src/ExchangeAdminWeb.Modules.RiskyUsers.csproj -c Release
dotnet test FeatureModules/RiskyUsers/tests/ExchangeAdminWeb.Modules.RiskyUsers.Tests.csproj -- xUnit.MaxParallelThreads=4
dotnet build ExchangeAdminWeb.slnx -c Release
dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4
dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore
git diff --check HEAD
```

The future project names above are fixed by this plan unless revised explicitly.
Run the existing coverage collector/floor and ASCII checks with all affected
projects. If PowerShell changes, run PSScriptAnalyzer and Pester with corresponding
guard proof. Update CI's solution/test discovery and coverage handling if the new
project layout requires it; do not lower a floor to accommodate moved code.
Pin compatible component/browser test dependencies and record their exact commands
with the implementation; run those tests in CI, not only on one workstation.

Before any slice claims completion, run the applicable full repo gates. Use the
repo's current Windows target. Keep the existing maximum-four xUnit worker
guidance for the full suite; don't change runtime job concurrency as a development
optimization.

Local publish verification uses a disposable destination, not the IIS deployment
pipeline. Manual live tenant/domain checks and deployment need their existing named
authority. Unrun operational checks remain listed as pending rather than inferred
from simulated tests.

### 8.3 Measuring whether this improves development

Record baseline and post-pilot timings for the same machine/configuration and
separate cold/warm runs: selected-module build, selected-module tests, preview
startup, and full integration gates. Also record the files required for one
representative module change and for creating the third module, together with any
host API change that was necessary. No speedup percentage is assumed.

The structural success criterion is AC1/AC2/AC11: independent module feedback and
module-local ordinary edits. A test filter on the original monolithic test project
does not demonstrate independent compilation. Broader migration needs separate
approval informed by these results.

### 8.4 Implementation and acceptance status

| Item | Status |
| --- | --- |
| Owner approval of this plan | Pending |
| S0-S7 implementation | Not started |
| Module/host automated verification | Not run for this plan; no implementation exists |
| Independent reviewer | Not requested or dispatched |
| Live acceptance / deployment | Not authorized by the drafting request |

Track implementation as Draft -> Approved -> In progress -> Verified (pending any
named integration/operational step) -> Implemented. Verification is not completion
while required acceptance remains outstanding. This plan creates no branch or
branch-closeout work. Existing git/push authority still governs ordinary records.

## 9. Traceability check

Pending plan iteration; no completed review or approval is implied.

## 10. Review log and pending owner decision

- 2026-09-30: Revision 1 drafted from the owner-requested code architecture
  assessment. No implementation, self-review, or independent review was performed.
- **Pending decision:** approve the bounded S0-S7 scope with Service Health and
  Risky Users as the pilots. The consequence is an initial shared-platform
  refactor, including authorization adapters and new test infrastructure, followed
  by two module migrations. Recommendation: approve this bounded pilot before
  considering additional modules; keep runtime packaging/import deferred.
- Drafting verification is documentation-only. Implementation build/test results
  must be recorded by slice as the work actually lands.
