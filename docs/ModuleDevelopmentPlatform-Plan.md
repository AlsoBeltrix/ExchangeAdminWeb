# Module Development Platform Plan

Status: Draft revision 2 - full cutover proposed; implementation approval pending.
Owner: Michael
Revision: 2 (2026-09-30)
Assessment baseline: 552a6c00d4c29556aab4f113bb2ad70bac21f877

This document owns the implementation scope and acceptance criteria. Revision 2
replaces the two-pilot rollout. Implementation is one complete architecture change
covering the existing application and the module-authoring path. Intermediate
commits are implementation checkpoints, not a reason to defer the remaining modules.

## 1. Required outcome

The owner needs multiple agents to land module work this week, without worktrees,
branches, or competing writes to a central registry. The owner rejected both a
protracted pilot migration and a reduced change that merely splits registration.

Each module owns its definition, version, registrations, pages, domain services,
models, assets, tests, and work records. A normal module change stays in that
module's files. Adding another module requires no hand-edited application registry,
service list, routing list, test list, or solution entry. The application composes
included modules automatically and remains one deployable application.

All existing modules cross this boundary in this work. There is no supported
legacy module list at completion and no follow-on migration required to obtain the
benefit. Shared platform changes still have a shared owner; this design removes
that dependency from ordinary feature development, not from changes to the platform.

Success is demonstrated by two independent module changes, including a version
change and service/permission registration, building and testing concurrently with
disjoint authored file sets and appearing together in the assembled application.

## 2. Scope and preserved behavior

Include every descriptor present at the implementation baseline, configuration-only
and system modules included. Reconcile additions committed by active work before
moving their files; the baseline below is evidence, not a permanent module registry.

True Last Logon already has services and tests but no descriptor at the assessment
baseline. Move those implemented files to its module project and provide its
module-local integration path. Do not invent its unfinished page or enable it: its
approved feature plan still owns those changes. Its eventual definition must be
included automatically without modifying platform or host source.

The prior prohibition on Migration feature work remains. This revised scope proposes
an explicit, limited exception for moving its existing definition, code, assets,
registrations and tests, plus removing a reverse dependency from shared code. No
Migration fixes, UX changes, or reopening of its feature queue are included. Approval
of this revision must include that structural exception; until then those files
remain untouched. The same behavior-preserving rule applies to parked modules.

Preserve IDs, routes and aliases, permissions, enabled defaults, dependencies,
configuration keys, module versions, DI lifetimes, cache ownership, credential
isolation, ticket behavior, protection/servicer rules, audit and notification
semantics, job persistence, and startup cleanup ordering. Namespace changes are not
an objective: retain existing public type identities where practical to reduce
unnecessary edits and keep persisted job payloads compatible.

One shared/app version bump covers the infrastructure change. Do not bump every
module merely because its source moved. Subsequent feature behavior/version changes
are module-owned. Existing defects remain recorded; relocation does not certify or
repair them.

No new packaging/import system, runtime DLL-directory scan, deployment topology,
hot replacement, database/schema migration, Graph permissions, PAM implementation,
or UI redesign. Do not use this refactor to rewrite every page into a new workflow
framework or change Risky Users' outcomes/ticket policy. Reuse existing services and
security checks; extract only the boundaries needed for independent module work.

## 3. Source and dependency ownership

Proposed layout:

```text
ModulePlatform/
  Api/              module contracts, descriptors and genuinely shared data types
  Runtime/          existing shared integrations, policy engine, storage and jobs
  UI/               existing shared Razor components
  Composition/      compiler generator and its focused tests
  Testing/          common fixtures and parameterized contract/source guards
  DevHost/          selected-module development host
FeatureModules/
  <ModuleId>/
    src/            project, ModuleDefinition, pages, services, models, assets
    tests/          project, fixtures, source guards and click-gate declarations
    docs/           module behavior and development documentation
.agents/modules/
  <ModuleId>/       current work, task evidence and token log
```

The web executable owns bootstrap, authentication hosting, the application shell,
and top-level error/access-denied pages. Home/navigation consume the catalog.
Admin Settings owns generic module configuration UI; feature-specific setting
components are contributed by their owning module.

Use the current `net10.0-windows10.0.17763.0` target for runtime, feature and test
projects. Razor pages/components live in Razor Class Libraries. The compiler
extension targets the framework supported by the pinned SDK's analyzer host; pin
and test that compiler dependency rather than upgrading application packages.

Dependency rules:

- Api references no application implementation. Runtime depends on Api; UI may
  depend on Api and the public shared Runtime services it already consumes.
- Features may reference Api, Runtime and UI, never the web executable or another
  feature implementation. Shared libraries cannot reference a feature.
- Retain working shared implementations such as AuditService and the EXO pool in
  Runtime. Do not create a speculative interface for every existing class. Expose
  narrow replaceable integration contracts where independent tests/preview need
  them, preserving the real implementations and their authorization boundaries.
- A genuinely cross-feature contract belongs in Api, with implementation supplied
  by Runtime or the owning feature through an explicit extension contract. Do not
  move feature services wholesale into Runtime to make dependencies compile.
- Feature tests reference their own production project and Testing, not the host
  test assembly or other feature tests. Move reusable fixtures out of
  ExchangeAdminWeb.Tests before module tests need them. Grant InternalsVisibleTo
  to each owning test assembly where existing internal seams require it.
- The production graph excludes Testing, DevHost, fixtures and fake authentication.
  Architecture tests inspect evaluated project/assembly references, including
  transitive references, and reject forbidden dependency directions.

Root web compile/content globs exclude ModulePlatform, FeatureModules, build
artifacts and module tests before introducing the new trees. Preserve scoped CSS,
JavaScript/resource paths, namespaces, Razor imports and static-asset publishing.
The cloud-password word list remains embedded in its owning production assembly.

### 3.1 Initial ownership map

Rows group implementation work, not runtime registration. Derive the actual file
move manifest from references at the start; update it for concurrent feature work.
All listed modules get separate project/test directories, including grouped rows.

| Modules | Feature-owned code to move with the definition |
| --- | --- |
| ExchangeOnline | ExchangeOnlineConfig page and its configuration definition; shared EXO pool/credential infrastructure stays Runtime |
| MailboxPermissions, CalendarPermissions | respective pages/services and feature-only permission helpers/models |
| Migration | page/CSS, migration services/planners/models, report store, export processor/payload and tests; structural exception in section 2 |
| DelegationReport, RecipientLookup, OutOfOffice | respective pages/services and feature-only models; reusable recipient lookup contract stays shared where actually consumed |
| MessageTrace | MessageTrace and MessageTraceReports pages, trace service, reports/exports/window planner, header analysis, forensic models, export store/listing and job processor |
| BlockedSenders | page, service, protection gate and blocked-sender models |
| GroupManagement, M365GroupManagement, Comms10k | respective pages/services and feature-only helpers |
| SelfServiceGroups | page and Services/SelfServiceGroups tree |
| MfaReset, CloudPasswordReset | respective pages/services; password generation/resources follow their actual consumers |
| AccountLockoutRemediation | existing parked page/service/models/tests, preserving disabled state |
| ConferenceRooms | page/service/protection gate/models and bulk processor/payload/contracts |
| NamedLocations, EmergencyDisable, RiskyUsers | respective pages/services and feature-only models |
| DhcpAuthorization, BitLockerRecovery | respective pages/services; BitLocker directory-search seam and identifier helpers |
| DefenderEndpointDevices, IntuneDevices | respective pages/services/models; Defender named HTTP clients stay with its definition |
| ServiceHealth, LicensingUpdates | respective pages/services/assets, preserving existing singleton/cache and scoped service lifetimes |
| ADAttributeEditor | page, service, undo handler and custom settings contribution |
| AdminSettings | AdminSettings and generic ModuleConfig pages; shared config/auth/storage providers stay Runtime |
| AdminEventLog, AdminBulkJobs | respective pages and feature UI; shared audit/job engine stays Runtime |
| TrueLastLogon (in progress) | existing TrueLastLogon/CloudSignIn services, aggregators and tests; its feature plan supplies remaining UI/definition |

Resolve real coupling explicitly. For example,
`ExchangeServiceBase.CheckAdGroupMembership` currently takes
`MigrationEligibilityResult`. Move that Migration-specific method into the
Migration implementation while preserving its behavior; do not make Runtime depend
on Migration. Apply the same reference-based test to shared autocomplete components,
lookup models, permission outcomes and job payloads. Physical file names alone do
not establish ownership.

## 4. Automatic composition, with no replacement central list

### 4.1 Build inventory and generated references

The host uses one fixed project-reference convention:
`FeatureModules/*/src/*.csproj`. Each production module exposes a public definition
implementing `IAdminModule` and an assembly marker identifying that type. The marker
references the type; the descriptor remains the sole source of the module ID.

A small compiler generator reads the markers in the referenced assemblies and emits
the strongly typed module factories and assembly set into build output. It sorts
inputs consistently, diagnoses malformed/missing definitions and duplicate markers,
and emits direct references to each definition. It never depends on
`AppDomain.CurrentDomain.GetAssemblies()` or on application code having touched a
feature first. No generated inventory is checked in or edited by agents.

The build validates the evaluated feature project inventory against the marked
referenced assemblies. A production project in the module convention cannot silently
disappear because its marker is missing. Project names/assembly names follow one
validated convention; generation uses structured compiler/project metadata, not a
regex parser for C# files. Fixtures outside the feature tree are excluded explicitly.

The same composition mechanism serves production, integration tests and DevHost.
DevHost receives a selected module project at build time through a validated module
ID/path parameter, not a central switch statement. A module project awaiting its
feature definition, such as True Last Logon at this baseline, is explicitly marked
as a library-only work-in-progress in its own project; it is build/test discoverable
but contributes no route or enabled descriptor. Clearing that local marker requires
a valid definition and automatically enrolls it in application composition.

### 4.2 One complete catalog

`ModuleCatalog` becomes an immutable read model constructed from that complete
module set. Delete its hand-maintained descriptor list and the parameterless
constructor that implicitly builds a legacy catalog. Update all construction sites,
including tests; a compatibility type name must not conceal a different catalog.

Expose an aggregate route/alias/ID reader for navigation, version badges, usage
telemetry, authorization and configuration. Keep module-bound config/credential
access separate from that aggregate reader. Both legacy route names and newly added
routes resolve through the same catalog; there is no legacy-only view.

Before registering policies or serving requests, validate the complete definitions:
duplicate IDs/routes/aliases, missing/cyclic dependencies, supported contract
version, invalid capability contributions and conflicting service registrations.
Catalog construction performs no credential resolution or live backend calls.

Supply the generated assembly set to both Blazor routing surfaces: endpoint mapping
and the interactive Router. A route must work on direct navigation and in-app
navigation. Publish verification checks module static assets as well as assemblies.

### 4.3 Module-owned composition hooks

An IAdminModule definition owns these contributions through constrained registration
helpers. No feature-type lists remain in Program.cs or generic admin pages:

| Contribution | Required behavior |
| --- | --- |
| Services and named HTTP clients | preserve lifetimes/timeouts; diagnose name collisions; cannot replace platform authentication, authorization or credential services |
| Bulk processors | one AddJobProcessor call supplies both scoped DI and dispatch mapping; preserve runner concurrency, existing module IDs and payload compatibility |
| Startup maintenance | module-owned scoped hook for current retention/cleanup; generic dispatcher preserves ordering after store initialization and existing best-effort behavior; no new scheduled tasks |
| Undo | module registers its IUndoableModule handler; existing aggregate resolver remains generic |
| Settings and servicer UI | capability backed by the actual registered implementation, not an unchecked Boolean; custom settings component belongs to its feature |
| Metadata, policies and version | one descriptor in the module; navigation, policy construction, config UI, version badge and telemetry consume the composed result |

For example, MessageTraceExportStore and MigrationReportStore startup pruning move
out of Program.cs into their owning hooks. Defender and True Last Logon HTTP-client
setup lives beside the feature requiring its timeout and permitted endpoints.
Shared Graph transport does not acquire permissive URLs or shared credentials.

## 5. Tests, build isolation and developer support

Move feature-specific tests and click-gate declarations with their features. The
ClickGate runner/source parser remains reusable; each module owns its registered
controls, exclusions, explanations and fingerprints. Generic guards discover these
contributions and fail if a page has no declaration. Do not retain a central array
that each agent must edit. Preserve the existing conversion scope and evidence;
source relocation does not authorize converting parked pages or relaxing guards.

Inventory every source-layout dependency, including AuditExtraChannelTests,
PageHandlerHygieneTests, PageAuthorizationRecheckTests, servicer/protection tests,
ModuleCatalogTests, ClickGateSource/Tests/Registry, Get-ClickGateAudit.ps1 and
validate-module-package.ps1. Replace hardcoded Services/Components/Pages/Program.cs
assumptions at the same time their inputs move. A source-root resolver reports
missing expected files and empty coverage, rather than returning an empty success.
Parameterize local guards over the selected module; host integration guards check
completeness across the entire discovered module set.

Replace BulkJobProcessorWiringTests' assembly assumption and Program.cs regex with
behavioral tests that resolve each registered processor from a fresh scope and
compare its mapping, identity and DI registration. Preserve every existing processor
and prove the replacement catches a missing mapping/service registration.

Move coverage-path and report handling with the first affected code/test project,
not in a later cleanup phase. Security coverage includes relocated authorization,
section-access and protection code. Collect all reports from the current run;
merge hits by normalized source path and line without double-counting shared files.
Never pick the newest single module report. Require fresh reports for every expected
test project and prove failure on missing/empty/stale reports. Record comparable
before/after covered and total lines; do not lower the floor to hide a move.

Add one verification entry point, `tools/Invoke-Verification.ps1`, which inventories
host/platform/feature projects, generates a disposable solution with absolute project
paths, and runs the requested build/test/format/coverage operations. Every run builds
the inventory from source. A newly added module/test project needs no checked-in
solution edit. CI and deployment gates use this entry point; update guidance in the
same change so the old root-solution command cannot be mistaken for a complete gate.
The existing root solution may remain a convenience view, not the completion gate.

Provide `tools/Test-Module.ps1 -ModuleId <Id>` and a selected-module preview command.
Validate IDs/paths, derive all sources from the owning module, and give each invocation
its own ignored artifacts/results/storage directory. Use the SDK artifacts output
layout to separate intermediate and output files by project and invocation. Concurrent
checks must not overwrite shared bin/obj files or read another run's coverage. Limit
xUnit workers to the existing maximum of four per invoked suite; whole-repo verification
schedules module suites to avoid multiplying that worker load without a bound.

Testing supplies common fixtures, component-test helpers and actual platform policy
construction. The selected-module DevHost uses real policy/command boundaries with
explicit fake integrations and identities, disposable stores, unique ports, and no
installed configuration, production credentials or live external calls. Missing fake
capabilities refuse to start or report an explicit unavailable operation. No silent
fallback to the real Runtime adapter. Add the adapters/scenarios needed for each
module's supported preview, preserving existing test seams and service behavior.
Production publishing must exclude DevHost and all test/fake assemblies.

The developer guide and starter produce the same module layout, marker, registrations,
tests and documentation used by the converted modules. A starter must not copy host
internals, require a central source edit, or require the complete web app to compile
before its selected module can be tested.

## 6. Records and guidance must not become the next shared registry

Update the Constitution's module/version source-location wording, repo guidance,
module spec, authoring guide and validator with the actual cutover. Preserve their
security and behavior rules. Do not leave instructions directing the next agent to
put a descriptor or version back in ModuleCatalog.cs.

Module versions and feature documentation live with their definitions. Module work
records and token logs live under `.agents/modules/<ModuleId>/`. State.md remains
the sole entry point through a stable pointer to that convention; routine module
progress does not require rewriting a shared status table. The existing shared
token log becomes the platform-work log with a pointer to module logs. Update the
repo-owned logging guidance accordingly; do not edit toolkit-owned AGENTS/skills.
Repo-wide decisions and shared-platform work still use their existing central records.

The definition of ordinary independent work includes its required tests, version,
documentation and records, not just its production source. A feature needing a new
shared contract declares that platform change explicitly instead of quietly editing
another agent's shared implementation.

## 7. Execution as one complete cutover

Approval covers the complete scope below. No additional per-module pilot approval
or acceptance waiting period is part of this plan. Shared-foundation edits have one
owner; module moves can proceed concurrently after that boundary compiles. This is
code ownership during the transition, not a branch/worktree or locking scheme.

1. **Capture the current boundary and prepare the foundation.** Read current active
   work, enumerate all source/asset/test owners, and capture descriptor values,
   registrations/lifetimes, routes and representative security behavior. Preserve
   other agents' landed or uncommitted work. Extract Api/Runtime/UI/Testing, establish
   dependency guards and root exclusions, implement composition generation and the
   verification wrapper, and move affected coverage/scanner handling with the code.
2. **Convert the entire module set in parallel-capable batches.** Use section 3.1's
   ownership map to move each feature's full file set, definition, registrations,
   capabilities, tests and records. Shared-platform fixes return to the foundation
   owner; module batches do not independently rewrite Program.cs. Update source
   authority docs as the old registry is replaced. Intermediate commits may compose
   old and new source while extracting, but no migration adapter survives completion.
3. **Finish composition and prove concurrent development.** Remove remaining feature
   lists and legacy paths, validate every module, run independent/concurrent module
   checks and the complete application gates, and prove starter/discovery, preview,
   routing and publishing. Record remaining live/manual checks explicitly. The work
   is not complete with two modules converted or with a future-migration checklist.

Do not promise a calendar duration before running the checks. These are dependency
steps inside one requested refactor, not a multi-release roadmap. Feature delivery
can use completed module boundaries during the cutover; release readiness still
requires the complete checks below. Recheck the current source before every move so
an in-flight module addition is neither overwritten nor stranded on the old layout.

## 8. Completion criteria and proof

| Criterion | Required evidence |
| --- | --- |
| Entire application converted | every baseline descriptor appears exactly once with preserved behavior/config values; no hand-maintained legacy registry remains; in-progress library-only modules retain all implemented code/tests |
| No replacement central edit point | add two disposable module fixtures and change two real module versions/registrations using disjoint owned file sets; generated composition sees both; Program.cs, catalog implementation, routing, shared tests and checked-in solution require no feature edits |
| Independent compilation and tests | each production/test project builds without host/other-feature references; representative Graph, AD, EXO, job and system modules run locally; an unrelated broken fixture blocks the composed fixture host but not an independent real module |
| Actual concurrency | run two selected-module builds/tests simultaneously with distinct artifacts/storage/ports; demonstrate no output locking, stale report use or shared authored-file changes |
| Catalog and discovery parity | compare the cutover against captured module-local baselines; detect missing markers/definitions, duplicate IDs/routes/aliases and invalid dependencies; include an assembly never otherwise touched at startup; test both migrated routes and system/config-only routes |
| Registration completeness | resolve real registrations with test integrations, verify lifetimes and scoped processor construction, named clients, settings, undo and startup maintenance; missing either processor mapping or service fails |
| Security preserved | use actual policy construction/handlers to prove enabled/dependent/granular permissions, dynamic SID versus static AdminGroups rules, revocation-before-write, protected targets/servicers, module credential isolation and no backend write on denial; preserve Self-Service Groups' owner-ruled exception |
| UI and operation behavior preserved | representative rendered components and browser navigation cover version badges, telemetry attribution, direct routes, static assets, conflicting actions, error recovery, ticket behavior and known audit/notification ordering; retain existing module behavior tests |
| Guard and coverage completeness | each relocated source guard still detects its target defect; every test project produces a fresh report; security coverage spans all moved paths with comparable denominator and no floor reduction |
| Independent authoring support | starter module compiles/tests/previews through documented commands without host snippets; selected-module fakes cannot reach live integrations; feature checks do not require unrelated feature compilation |
| Whole app release integrity | full Release build, all discovered tests, format, ASCII, applicable Pester/PSScriptAnalyzer, coverage floor and git diff --check pass; publish includes module routes/resources and excludes fakes/tests |
| Durable ownership | each module's version, click-gate declarations, docs and work records are local; root state/guidance point to the new authority without recurring per-module edits |

For changed/new behavioral guards, temporarily remove the protected behavior in an
owned disposable fixture or narrowly scoped source edit, observe the intended test
failure, restore it, and rerun. Never revert entire shared files over concurrent work.
Use project inventory and test identities to reconcile moved tests, not just a total
pass count. Report skipped/unrun checks and manual/live validation separately.

## 9. Failure behavior, release and rollback

Malformed composition fails before requests/jobs are served and names the offending
module. Missing/unreadable permissions or required config continue to deny. Missing
preview fakes deny or stop preview startup; no real backend fallback is allowed.
Module compilation failure blocks the full app release, while another module's own
build/test loop remains usable. Existing best-effort cleanup and notification/audit
handling retain their behavior; no mutation is retried by this infrastructure.

This refactor has application-wide blast radius. Verification is a release gate, not
a claim that moving files cannot affect security, resource paths or persisted jobs.
No live deployment or push is included. Before an authorized deployment, retain the
previous published artifact; rollback restores that artifact and leaves the shared
configuration database, operational job data, reports and logs intact. No schema or
config-key migration is needed. Verify the previous artifact can still read queued
job payloads and existing module config; no delete/recreate workaround is acceptable.

## 10. Claude revision-1 findings and disposition

The original [Claude report](../.agents/review/module-development-platform-plan-r1.md)
is preserved with its provenance. It reviewed revision 1, not this revision.
No repeat review was requested or run.

| Finding | Revision-2 requirement |
| --- | --- |
| F1 catalog facade ambiguity | section 4.2 removes the implicit legacy constructor/list and requires one complete catalog everywhere |
| F2 source scanners lose moved pages | section 5 moves scanners and click-gate ownership with their inputs and requires discovery completeness |
| F3 coverage repaired too late | section 5 requires current-run report aggregation and scope repair with the first affected move |
| F4 route reader missing | section 4.2 separates aggregate route/alias reads from module-bound config/credential access |
| F5 job wiring guard breaks | sections 4.3 and 5 replace text/assembly assumptions with processor construction and mapping proof |
| F6 authoritative docs lag | sections 6 and 7 update source-location guidance during the cutover |
| F7 discovery unspecified | section 4.1 specifies generated static references from module-owned markers; an explicit hand-maintained host list is rejected because it recreates contention |

The report's optional TFM, internals/test-helper and contention-measurement concerns
are covered in sections 3, 5 and 8. These are plan dispositions, not claims of tested
repairs. The original review's implementation-dependent risks remain predictions
until implementation and guard proof exist.

## 11. Approval and implementation status

Pending: approval of this complete scope, including the structural-only Migration
exception in section 2. Approval of the old two-pilot draft is not being assumed.
No application changes have been made by drafting or reviewing this plan. After
approval, execute the whole cutover; do not stop at a pilot or solicit a new rollout
choice for every module. Deployment/push and any new external review remain separate
owner actions under existing policy.

Technical references for the proposed implementation: Microsoft's
[Razor Class Libraries](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/class-libraries?view=aspnetcore-10.0),
[Blazor assembly routing](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/routing?view=aspnetcore-10.0),
[SDK artifacts layout](https://learn.microsoft.com/en-us/dotnet/core/sdk/artifacts-output),
and the Roslyn team's
[incremental generator cookbook](https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.cookbook.md).
The specific composition and ownership rules above are this plan's design choices.
