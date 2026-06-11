Build a new ExchangeAdminWeb admin module.

Module request from Michael: $ARGUMENTS

## Phase 0 — Read before anything else
1. docs/AdminModuleDeveloperGuide.md — in full. It is the binding contract.
2. docs/AdminModuleSpec.md — check its version header against the csproj;
   flag drift rather than silently trusting it.
3. Modules/AdminModuleDescriptor.cs, ModulePermission.cs, ModuleConfigField.cs —
   the actual shapes override the guide if they differ; report any difference.
4. ONE reference module nearest in backend type to this request, read end to
   end (.razor + Service): Graph-backed → NamedLocations; Exchange-backed →
   CalendarPermissions; AD-backed → ADAttributeEditor.

Standing rule (guide, Host API Boundary): if an API you need is neither
documented nor found in host source, STOP and report the missing contract.
Never invent a stub, a fake host, or an always-allow policy.

## Phase 1 — Plan, then STOP
Create docs/<ModuleName>-Plan.md from docs/templates/Plan-Template.md.
- §1 Goal: Michael's request verbatim.
- §3/§4 proposals must be module-flavored: enablement default, fail-closed
  permissions, ticket requirement, protected-principal applicability, config
  fields and the Delinea secret template, audit actions emitted.
- §6 must include the complete draft descriptor and name the reference module.
Present §1–§5 plus the descriptor to Michael and STOP. No implementation
until the plan Status is Approved.

## Phase 2 — Build, tests first
1. Write the guide's minimum catalog tests, plus its security tests if the
   module mutates anything, BEFORE the page and service.
2. Then: descriptor in ModuleCatalog.RegisterAll(), service, page, DI
   registration in Program.cs.
3. Required patterns are verbatim, not inspirational: the page auth block,
   re-check immediately before every write, the 9-step protected-principal
   write sequence, audit and trace rules.

## Phase 3 — Gates (all must pass; paste outputs)
dotnet build ExchangeAdminWeb.csproj
dotnet test ExchangeAdminWeb.Tests/ExchangeAdminWeb.Tests.csproj
dotnet format ExchangeAdminWeb.csproj --verify-no-changes
git diff --check HEAD
(Isolated package authoring only: tools/validate-module-package.ps1 first.)

## Phase 4 — Self-review and hand-off
1. Walk the guide's Review Checklist item by item. Evidence standard applies:
   every "pass" cites file:line; no recalled code.
2. Fill plan §9 (traceability) and §10 (review log).
3. Report the Definition Of Done as a split list:
   - Satisfied by this session, each with evidence.
   - Remaining for Michael: dev deployment, realistic operator-workflow
     validation, Delinea secret creation, section access group configuration.
