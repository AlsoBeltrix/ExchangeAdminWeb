# Audit: code written but not wired in, and behaviour copied across modules (2026-11-10)

Read-only audit of master at `ee1e7a18`. Every line reference below was read and grepped on that tree. No build or test was run.
Module list: `Modules/ModuleCatalog.cs`, 31 descriptors (`grep -c 'Id = "' Modules/ModuleCatalog.cs`).

## Summary

- Part A: 46 unwired items. 0 to wire in, 46 to remove. Nothing unwired turned out to be a better shared piece. The better
  shared pieces that exist (`ITicketValidator`, `ProtectedPrincipalServicing.ForWriteTargetAsync`, `SelfGrantRefusalRecord.RefusalExtra`,
  `PermissionChangeNotifier`, `ActiveDirectoryModuleImport`, `ModuleCredentialService`, `BulkCsvRowLimit`) are wired, but only
  partly adopted. That is Part B.
- Of the 46: 3 components or policies, 3 types, 3 members nothing calls, 19 members only tests call, 1 group of 5 deliberate
  test-seeding seams, 10 injected-but-unused dependencies, 3 config or script parameters, 2 descriptor fields, 1 route, 1 tool doc.
- Part B: 17 copied patterns. 6 high risk (ticket gate, audit refusal shapes, cloud-user protection check, run-time job
  authorization, admin notifications, operation-trace scopes), 7 medium, 4 low.
- 8 possible live bugs, listed separately at the end. None was fixed.

## Part A - written but not wired in

How it was checked: Razor components by `<Name`, `typeof(Name)` and `Name.` across `*.razor`/`*.cs`. Types by declaration
name in production files other than the declaring file. Public and internal methods by whole-word count with comment lines
removed: a name counts as unwired when its only production occurrences are its declarations. DI by registration and
consumer. Injected fields by `_field` count (declaration plus assignment only). Config keys by string literal. PowerShell
through the PS AST (functions and parameters). Routes by `@page` against `href`/`NavigateTo`/catalog `Route`. "Tests:" gives
the whole-word count of hits in `ExchangeAdminWeb.Tests`.

| # | Item | Added | Evidence nothing uses it | Why it was left | Recommendation |
|---|---|---|---|---|---|
| 1 | `Components/Shared/TicketNumberInput.razor` | 7fa4dc96 2026-05-04 | `git grep "<TicketNumberInput"` finds nothing, in code or in history (`git log -S"<TicketNumberInput" -- '*.razor'` is empty). Tests: 0 | Written in the 2026-05-04 bulk commit. Every page then wrote its own box. Recorded as "never-referenced" in decisions 2026-08-31 (`.agents/decisions.md:2100`). Left alone by ticket-sanitize (`.agents/review/findings/ticket-sanitize.md:49`) and noted in `.agents/state.md:35` and `:789` | REMOVE, and replace it with a new shared ticket component (Part B1). It is not worth wiring. It calls `ServiceNow.ValidateTicketAsync` directly (:88). It judges a blank ticket with `IsNullOrWhiteSpace` (:76), not `TicketPresence`. `@bind="TicketNumber"` (:8) binds the parameter itself, so `OnTicketNumberChanged` (:98) is never called and `TicketNumberChanged` never fires. It defines `Dispose()` (:115) without `@implements IDisposable` |
| 2 | `Components/AuthorizationCheck.razor` | 7fa4dc96 2026-05-04 | No `<AuthorizationCheck` anywhere. Tests: 0 | Superseded by `@attribute [Authorize(Policy = ...)]` on every page and `AuthorizeRouteView` (`Components/Routes.razor:6`). It checks the legacy `"GroupPolicy"` (:25) | REMOVE |
| 3 | The `"GroupPolicy"` authorization policy (`Modules/ModuleCatalog.cs:63-67`) | 7fa4dc96 2026-05-04 (moved into the catalog by 475799d6) | Its only consumer is #2 (`git grep '"GroupPolicy"'`: AuthorizationCheck.razor:25, ModuleCatalog.cs:67, ModuleCatalogTests.cs:532) | The catalog's own comment (:72-74) calls it "the legacy app-wide AllowedGroups gate the Constitution removed" | REMOVE with #2, and drop it from `ModuleCatalogTests.cs:532` |
| 4 | `PermissionType`, `PermissionAction` enums (`Models/Enums.cs:3`, `:5`) | abae350e 2026-04-30 (initial commit) | Each name appears only at its declaration. `git log -S"PermissionType."` finds no use, ever | Dead since the initial commit | REMOVE |
| 5 | `RoomTypeDefinition`, `CalendarPermissionEntry` (`Models/ConferenceRoomModels.cs:65-88`) | 131bb59d 2026-06-05 | `RoomTypeDefinition` appears only at its declaration. `CalendarPermissionEntry` is used only by it. `git log -S"RoomTypeDefinition"` shows only the adding commit | Added with Conference Rooms v2.0 and never used. Not verified where room types are defined instead | REMOVE |
| 6 | `IExoRecipientCommands`, `PooledExoCommands` (`Services/IExoRecipientCommands.cs`) | 81fd069c 2026-08-06 | Referenced only inside its own file. Tests: 0 | The commit body says it was written for "the paused PermissionValidator test slice", committed so that `stash@{0}` ("wip: permissionvalidator seam", still present) stays buildable | REMOVE together with the stash. Wire it in only if the owner resumes that slice |
| 7 | `NamedLocationsService.GetByIdAsync` (:97) | f56bdf78 2026-05-22 | No caller, ever (`git log -S` shows only the adding commit). Tests: 0 | Written with the module and never used | REMOVE |
| 8 | `BulkJobProcessorRegistry.IsRegistered` (:26) | 971555ff 2026-07-02 | No caller, ever. Tests: 0 | Never used | REMOVE |
| 9 | `BulkJobService.IsStalled` (:397) | 971555ff 2026-07-02 | No caller, ever. Tests: 0. Callers use `DisplayStatus` | Never used | REMOVE |
| 10 | `AuthorizationGroupInventory.GetConsultedSids` (:129) | cb3be915 2026-10-21 | Prod: only the declaration. Tests: 1 | Its caller was removed the same day in 80eacbf8 (aud-a29d) | REMOVE |
| 11 | `BulkJobService.GetRecentJobsBySubmitter` (:364) | a6e49ff5 2026-10-01 | Prod: only the declaration. Tests: 1 | f048ca60 2026-10-02 took out "the finished-job announcement fed by GetRecentJobsBySubmitter" (commit body) and left the method | REMOVE |
| 12 | `ProtectedPrincipalService.MatchesDnToProtectedGroup` (:2402, internal) | a2263413 2026-05-29 | Prod: only the declaration. Tests: 1 | Last caller removed in fe96bc89 2026-06-01. It matches a protected group by name or CN (:2396-2401). Protection now matches by SID | REMOVE. Revived, it would be the wrong rule |
| 13 | `ProtectedPrincipalService.ResolveDirectoryPrincipalAsync` (:828) | a2263413 2026-05-29 | Prod: only the declaration (EmergencyDisable.razor:356 names it in a comment). Tests: 1 | Its own doc says "Legacy wrapper - returns null for both NotFound and Unavailable" (:825-827). The last callers moved in fb5aa0c0 2026-08-06, whose body says it collapsed "no such user" into "the directory did not answer" | REMOVE |
| 14 | `ADDirectorySearchService.SearchUsers` (:58) | 43faba10 2026-06-03 | No production caller, ever. Tests: 1 | A one-line wrapper of `Search(term, "User", ...)`. The pickers call other entry points | REMOVE |
| 15 | `MigrationReportStore.DeleteAll` (:216) | 3f35cab4 2026-09-28 | Prod: only the declaration. Tests guard that the page never calls it | Replaced by `DeleteForBatch` in 6a26f215 2026-10-19 because it wiped every operator's reports (audit A31, the doc at :208-215) | REMOVE |
| 16 | `ModuleAdminService.GetUnusableGrants` (:74) | 5059c6fa 2026-10-19 | Prod: only the declaration. Tests: 2 | Callers moved to `TryGetUnusableGrants` (1f47879a, 5ff82c67, 2026-11-03). It is now a throwing wrapper of that method | REMOVE |
| 17 | `ModuleAdminService.IsModuleAdminForAny` (:113) | cdf18751 2026-05-22 | Prod: only the declaration. Tests: 2 | Its last caller was removed by f93c4b0e the same day | REMOVE |
| 18 | `AdminPageDirtyState.ClearSection` (:98) | 857be64c 2026-08-04 | Prod: only the declaration. Tests: 4 | Its last caller was removed in bcef5fec 2026-10-23 | REMOVE |
| 19 | `ModuleAdminRepository.GetForModule` (:17) | 5f6bb401 2026-06-18 | No production caller, ever. Tests: 4 | Written with the SQLite migration and never used | REMOVE |
| 20 | `SectionAccessService.GetSectionAccess` (:180) | 385a4ef4 2026-05-12 | Prod: only the declaration. Tests: 5 | Production reads `TryGetSectionAccess` (:197). The last caller was removed in b2afe0e2 2026-10-22 | REMOVE. Point the tests at `TryGetSectionAccess` |
| 21 | `LicensingUpdatesService.ParseCsv` (:484, internal) | a574ed60 2026-05-29 | Prod: only the declaration. Tests: 5 (`LicensingUpdatesServiceTests.cs:148-204`) | Production calls `ParseCsvAsync` (:97, :509). The two bodies differ only in `csv.Read()` and `await csv.ReadAsync()`. The tests therefore test a copy that production does not run | REMOVE. Move the tests onto `ParseCsvAsync` |
| 22 | `AttributeEditorRepository.TryReadAllowlist` (:30) | 8a8d708e 2026-06-18 | Prod: only the declaration. Tests: 5 | Its caller was removed by ff6823b8 2026-10-22 (aud-a28i: the save decides inside its own write) | REMOVE |
| 23 | `ADAttributeEditorService.PreviewAsync` (:1027) | 75579543 2026-05-29 | No production caller, ever (`git log -S` shows only the adding commit). Tests: 6 | Never wired into the page. It was still maintained afterwards: aud-a26o reworked its credential path (:1037-1038) | REMOVE. If the owner wants a pre-save preview, that is a feature for a plan |
| 24 | `AuditFirstMerge.Merge` (:44) | 2026-10-19 | Prod: only the declaration. Tests: 7. Production calls `FitDiagnostics` (AdminEventLog.razor:749) | Replaced in 7e95e630 and 30e893bd 2026-10-19. `Merge` is separate logic that production does not run | REMOVE |
| 25 | `PermissionValidator.IsUserExcludedAsync` (:189) | abae350e 2026-04-30 | Prod: only the declaration. Tests: 9 | Production moved to `ValidateTargetMailboxAsync` in 1ad872f3 2026-10-21 (aud-a2b: "check the mailbox Exchange will write, not an AD namesake"). It wraps `CheckExclusionAsync` | REMOVE. Point the tests at the production entry |
| 26 | `ADAttributeEditorService.IsAllowlistCorrupt` (:250) | a4686c81 2026-06-17 | Prod: only the declaration. Tests: 10 | Its own doc says "The allowlist save no longer calls this" (:248). The caller was removed in 07c366ef 2026-10-22 | REMOVE |
| 27 | `AdSiteSubnetMap.FindSite` (:64) | c6b90b41 2026-10-13 | Prod: only the declaration. Tests: 15 | Production uses `Prepare` and `FindSiteCancellable` (DefenderEndpointDeviceService.cs:1372, :1395) since a4c01925 2026-10-16. `FindSite` delegates to them (:70) | REMOVE. Point the tests at `FindSiteCancellable` |
| 28 | `IActivityHandle.AsProgress` (`Services/Progress/ActivityProgress.cs:137`, impl :424) | 2026-10-01 (global progress S1) | Prod: interface plus implementation, no caller. Tests: 4 (one direct, three fakes) | Plumbing for services to report progress. No caller was ever written, and `docs/GlobalProgressSystem-Plan.md` does not name it | REMOVE |
| 29 | Test-seeding seams: `SectionAccessService.SaveSectionAccess` (:285), `ProtectedPrincipalService.SaveConfig` (:1612), `ModuleConfigService.SaveModuleConfig` (:98), `ModuleAdminService.SetModuleAdmins` (:149), `AttributeEditorRepository.SaveAllowlist` (:64) | 2026-05 and 2026-06 | Prod: only the declarations. Tests: 10 to 54 each | Deliberate. Each doc says "Internal rather than removed because the suite seeds ... through it" (e.g. SectionAccessService.cs:280-284), and WholeStoreWriterTripwireTests refuses a production caller | REMOVE from the production classes and seed through a test-only helper. Low priority, because the tripwire already guards them |
| 30 | `Comms10kService._config` (:15, :25) | 0a7dc4b5 2026-05-21 | Only declared and assigned | Left after a refactor | REMOVE |
| 31 | `EmergencyDisableService._env` (:125, :157) | 3660cb2b 2026-05-29 | Only declared and assigned | Never used | REMOVE |
| 32 | `GroupManagementService._moduleConfig` (:10, :27) | 7ba76a9b 2026-05-21 | Only declared and assigned | Left after the Graph-client refactor | REMOVE |
| 33 | `M365GroupManagementService._audit`, `_operationTrace` (:12-13, :33-34) | cd42847b 2026-06-01 | Only declared and assigned | The page writes the audit instead (`M365GroupManagement.razor`, 12 inline `try { Audit.`) | REMOVE. Re-add only if Part B2/B6 moves audit and trace into the service |
| 34 | `ProtectedPrincipalService._env` (:208, :358) | 9163b812 2026-05-29 | Only declared and assigned | Never used | REMOVE |
| 35 | `RiskyUsersService._logger` (:15, :23) | e003af96 2026-09-01 | Only declared and assigned | Never used | REMOVE |
| 36 | `ServiceHealthService._logger` (:16, :33) | e86156f7 2026-09-08 | Only declared and assigned | Never used | REMOVE |
| 37 | `ADAttributeEditor.razor:11` `@inject OperationTraceService OperationTrace` | 75579543 2026-05-29 | `OperationTrace` occurs once in the file | Never used | REMOVE |
| 38 | `AdminEventLog.razor:9` `@inject IConfiguration Config` | f00a1a64 2026-05-15 | `Config` occurs once in the file | Never used | REMOVE |
| 39 | `Home.razor:8-9` `@inject IAuthorizationService AuthorizationService`, `@inject NavigationManager Navigation` | 7fa4dc96 2026-05-04 | Each occurs once in the file | Never used | REMOVE |
| 40 | Legacy appsettings fallbacks with no sample entry and no README line: `Migration:HybridEndpoint`, `:CloudTargetDeliveryDomain`, `:OnPremTargetDeliveryDomain`, `:CloudQuotaGB`, `:ExcludedADGroups`, `:OnPremTargetDatabases` (`MigrationService.cs:24-47`, read only when no Migration module config exists); `Security:PreventSelfGrant` (`PermissionValidator.cs:162`); `Security:ProtectedPrincipalDirectoryReadSecretId` (`ProtectedPrincipalService.cs:1830`) | 2026-05 | `appsettings.json.sample` has none of them; `grep -c` in README.md = 0 for each | cde84d9a 2026-05-22 removed them from the README and generated config but kept the reads. `docs/RetireExcludedUsersAppsettingsFallback-Plan.md` (Implemented) retired the sibling fallback the same way | REMOVE, through a retirement like that plan's. Not verified: whether any deployment still relies on them |
| 41 | `deploy.ps1:31` parameter `-HybridEndpoint` (default `"hybrid1"`) | 385a4ef4 2026-05-12 | The PS AST shows its only variable reference is the declaration | cde84d9a 2026-05-22 removed its only use (`HybridEndpoint = $HybridEndpoint` in the generated config) and kept the parameter | REMOVE. Also listed as a live bug: it is accepted and silently ignored |
| 42 | Unread function parameters: `Prompt-Value -Name` (`deploy.ps1:126`), `Read-Value -Name` (`tools/Install-ExchangeAdminWeb.ps1:149`) | 2026-05 | PS AST: the body never reads `$Name` | Callers pass it; the body ignores it | REMOVE |
| 43 | `ModulePermission.Name` (`Modules/ModulePermission.cs:32`) | 2026-05-20 | No production read of a permission's `.Name` (`git grep "MainPermission\.Name\|gp\.Name"` finds nothing). Read only by `ModuleCatalogTests.cs:96-98` | Its own doc (:9-14) says it "is NOT a label" and that `DisplayName` is shown instead | REMOVE. Low value: it is a positional argument in every permission declaration |
| 44 | `ConfigFieldType.AdUser` (`Modules/ModuleConfigField.cs:12`) | 131bb59d 2026-06-05 | Not declared by any catalog field and not read by any code (`git grep "AdUser\b"` finds only the enum) | Never used | REMOVE |
| 45 | Route `@page "/message-trace"` (`Components/Pages/MessageTrace.razor:2`) | e62ae736 2026-05-05 | No `href`, `NavigateTo` or catalog route uses it. The catalog and nav use `/message-analysis` | Kept when the page moved to `/message-analysis` in 4c2ff836 2026-05-28. No record says it is kept for bookmarks | REMOVE, or record it as a deliberate alias. Owner call |
| 46 | `tools/test-plan-bulk-csv.md` | 7d38e9c7 2026-05-26 | Referenced by no file | A manual performance test plan for a pre-pool design | REMOVE, or move it to `docs/history` |

Dormant rather than unwired, so not counted. `ModuleConfigField.IsSecret` is read (ModuleConfig.razor:479, :2239), but no catalog field
sets it (`grep -c IsSecret Modules/ModuleCatalog.cs` = 0; `.agents/review/findings/aud-a26n.md:63`). `ServiceNowService` is
wired but dormant by deployment setting (`ServiceNow:Enabled`). The favicon scripts in `tools/` are run by hand by design.

DI: every registration in `Program.cs:47-386` has a consumer (constructor, `@inject`, `GetRequiredService`, or
`IEnumerable<IUndoableModule>`). Static assets in `wwwroot` are all referenced from `Components/App.razor`. All catalog config
keys are read by a string literal in production code. All PowerShell functions in `tools/` and `deploy.ps1` are called.

### Approved or In-progress plans whose work is not built or half built (list only)

From each plan's `Status:` line:
- `docs/ClickGatingAudit-Plan.md`: tiers 2, 3 and 4 approved 2026-10-12, "NOT STARTED".
- `docs/Comms10kBulkResolveScale-Plan.md`: S3 and S4 "not started".
- `docs/VerbositySweep-Plan.md`: approved at full scope 2026-10-12, "Not started".
- `docs/AdminUIRedesign-Plan.md`: In progress; slices 4-5 remain.
- `docs/MessageTracePermissionSplit-Plan.md`: approved and in progress.
- `docs/TrueLastLogon-Plan.md`: "NOT DONE - the mandatory live check has not been run".
- `docs/MigrationRemoveCompleted-Plan.md`: "manual acceptance OUTSTANDING".
- `docs/TacticalAdminAccounts-Plan.md` and `docs/TestSuiteRemediation-Plan.md`: In progress.
- `docs/BlockedSendersLoadTiming-Plan.md`, `docs/Comms10kReplaceUx-Plan.md`, `docs/ConferenceRooms-OnPremRoomListAdd-Plan.md`: completion unverified
  (already in `.agents/state.md:871-877`).
- Decision 2026-08-31 (`.agents/decisions.md:2077-2127`): every module with a ticket field was to get a `ValidateTickets` switch
  through `ITicketValidator`, and "rewiring the eight existing call sites" was deferred to "the same future sweep". Today 2 modules
  have the switch (`ModuleCatalog.cs:1096` CloudPasswordReset, `:1519` BitLockerRecovery); 20 pages still call ServiceNow
  directly (B1).
- Not verified one way or the other: `docs/ThemeSupport-Plan.md`, `docs/SelfServiceGroupManagement-Plan.md` and
  `docs/SelfServiceGroupsMemberListingAndPicker-Plan.md` read "Approved". The first SelfService plan says its task set is COMPLETE.

## Part B - the same behaviour copied across modules

Ranked by risk. "Sites" counts call sites in production code.

### B1 (high) - The ticket box and its gate

23 pages in 22 modules use `TicketPresence`. The rule itself (`TicketPresence.Sanitize` / `IsPresentAsTyped` / `Problem`) is shared and
used everywhere, which is good. Everything around it is copied:

- **The check behind the button: five shapes.**
  - (a) `TicketPresence.Refusal(ticket) ?? await ServiceNow.ValidateTicketAsync(ticket)`: ADAttributeEditor:561,
    AdminEventLog:1189, BlockedSenders:384, CalendarPermissions:379 and :569, DhcpAuthorization:309, GroupManagement:607, :747,
    :1003 and :1157, LicensingUpdates:382, MailboxPermissions:399 and :608, MfaReset:355, Migration:3365, OutOfOffice:449.
  - (b) The same through an alias, `ConferenceRoomService.RequireTicket` (ConferenceRoomService.cs:2988): ConferenceRooms:1070,
    :1337, :1467 and :1682.
  - (c) `TicketPresence.Describe(ticket, "A ticket number is required.")`, then ServiceNow: DhcpAuthorization:398,
    IntuneDevices:1041, M365GroupManagement:878, NamedLocations:638, RiskyUsers:888. M365's and NamedLocations'
    `TicketRefusalAsync` and `AuditTicketRefusal` are verbatim copies (M365GroupManagement.razor:876-895,
    NamedLocations.razor:636-655).
  - (d) The per-module policy `ITicketValidator` (TicketValidationService.cs:65): BitLockerRecoveryService and
    CloudPasswordResetService only.
  - (e) A presence-only gate in the service: AccountLockoutRemediationService.cs:324, EmergencyDisableService.cs:190,
    MessageTraceExportListing.cs:175, MigrationService.cs:1172 and :1181.
  - Comms10k makes the ticket optional and validates only a non-empty one (Comms10k.razor:740-746).
- **Refusal wording: six variants.**
  - "Ticket number is required" (`TicketPresence.RequiredMessage`, via `Refusal`).
  - "A ticket number is required." (TicketValidationService.cs:82, the `Describe` callers, MigrationService.cs:1172).
  - "A ticket number is required for deauthorization. Enter one in the Authorize form." (DhcpAuthorization.razor:398).
  - "Ticket Number is required for logoff execution." (AccountLockoutRemediationService.cs:326).
  - "Ticket number is required for emergency disable operations." (EmergencyDisableService.cs:192).
  - `MessageTraceExportListing.TicketRequiredMessage` (:104).
- **Binding.** Most boxes bind `@bind:event="oninput"`. Four bind on change while a button is enabled by the ticket:
  AdminEventLog:436 (button :456), Migration:63 (:67) and :237 (:241), NamedLocations:187 (:206). See live bug 3.
- **Placeholders: seven variants.** "INC/REQ number", "INC0001234 or REQ0005678", "INC0000000", "Ticket #", "Ticket # (required)",
  "Required", "optional" (grep `placeholder=` on the ticket inputs).
- **Is the refusal audited?** With `refusedAt`: BlockedSenders:393, LicensingUpdates:390, MfaReset:361, OutOfOffice:458, M365:894,
  NamedLocations:654, MigrationService:1145. Without refusal fields: IntuneDevices:985-989 and RiskyUsers:842-846 (`Refuse` ->
  `LogModuleAction ... errorDetail` only), GroupManagement:611 and :751, DhcpAuthorization:313. Not at all: Comms10k:744-746.
- **Shared pieces.** `TicketPresence` is fully adopted. `ITicketValidator` is in 2 of 22 modules. `TicketNumberInput` is in none (A1).
- **Shape.** One `<TicketField>` component (box, `oninput`, one placeholder, and an `IsPresent` the button binds to). One gate in the
  service that sanitizes, judges, returns the refusal text and writes the one refusal audit row. Every page and every service gate
  calls these two. An owner decision comes first. Decision 2026-08-31 ("service now integration IS going to be required", a
  per-module switch) and the owner's 2026-11-09 words ("there is no connection to SNow, and won't be in the foreseeable future")
  pull in opposite directions, and the later entry does not supersede the earlier one. Whether the shared gate keeps the
  per-module ServiceNow switch, or drops ServiceNow from the 20 pages, is the owner's call.

### B2 (high) - Audit rows: refusal shape, result values, and what happens when the row is not written

- **Refusals are filed in six shapes.** README.md:1417-1440 already documents them. Item 44 in `.agents/state.md:122`/`:237`
  (`.agents/review/findings/sweep-e-44.md`) is the open owner question, and everything in this section bears on it.
  - A shared builder exists: `SelfGrantRefusalRecord.RefusalExtra(step)` (SelfGrantRefusalRecord.cs:72-73). Only
    MailboxPermissions (:373, :585) and CalendarPermissions (:358, :551) use it.
  - 18 other sites build the same `{ ["refusedAt"] = ..., ["mutationAttempted"] = false }` by hand: AdminBulkJobs:506,
    AdminEventLog:1301, BlockedSenders:393, ConferenceRoomService.cs:2970, DhcpAuthorization:313, :403 and :411,
    GroupManagement:611, :751 and :950, LicensingUpdates:390, M365:894, MfaReset:361, NamedLocations:654, OutOfOffice:458,
    ADAttributeEditorService.cs:587 and :2300, ADAttributeEditorUndoService.cs:476, EmergencyDisableService.cs:1095,
    MigrationService.cs:1145, SettingsSaveAudit.cs:43.
  - The same page can mix shapes. MfaReset files its ticket refusal with refusal fields (:361) and its authorization refusal
    without them (:370-371).
- **Undetermined.** Only `LogMailboxPermission` (AuditService.cs:88), `LogCalendarPermission` (:126) and `LogMigrationAction` (:229)
  take `filedAs` and can file Undetermined. `LogModuleAction` (:258) has 82 production callers and can file only Success or
  Failed (:272).
- **A row that was not written.** Mailbox and Calendar check the `JsonlWriteOutcome` and tell the operator
  (`SelfGrantRefusalRecord.Recorded`, :98). Every other caller ignores it.
- **Exception guards.** Seven identical `SafeAudit` copies that differ only in the log message: BitLockerRecovery:557,
  Comms10k:549, DefenderEndpointDevices:1126, IntuneDevices:880, RiskyUsers:782, TrueLastLogon:565,
  Comms10kReplaceProcessor.cs:344. About 52 inline `try { Audit. ... } catch` lines: GroupManagement 11, M365 12,
  DhcpAuthorization 7, NamedLocations 7, SelfServiceGroups 7, ConferenceRooms 3, Migration 2, and others.
- **Where the audit is written.** On the page for most modules. In the service for AccountLockout, EmergencyDisable,
  ADAttributeEditor and Licensing. M365's service injects `AuditService` and never uses it (A33).
- **Shape.** One module-operation recorder, owned by the service: `Succeeded`, `Failed`, `Undetermined`, and
  `Refused(step, reason)`, each writing the one agreed shape and returning whether the row was written. Pages stop writing audit
  rows. The refusal shape waits on item 44.

### B3 (high) - The protected-principal check for a cloud (Graph) user

- **Four hand-written copies** of "resolve with Exchange fallback -> refuse Unavailable/Ambiguous -> CheckAsync -> if
  unresolved, build a stand-in principal and check again -> servicer note or refuse": MfaReset.razor:387-475,
  RiskyUsers.razor:914-990, IntuneDevices.razor:1090-1160, CloudPasswordReset.razor:679-731.
- **They differ.**
  - The stand-in's `EntraObjectId`: MfaReset passes `null` (:450). RiskyUsers (:967), Intune (:1139) and CloudPasswordReset
    (:703) pass the real id. CloudPasswordReset.razor:676-677 says: "Do not copy the null."
  - Ambiguity wording: four variants (MfaReset:391, RiskyUsers:918, IntuneDevices:1094, CloudPasswordReset:690).
  - CloudPasswordReset folds both branches into one and catches exceptions (:721-728). The other three run two branches and
    have no catch around the check.
- **Wrappers.** `ServicerNoteFor` is copied verbatim four times: CloudPasswordReset:363, IntuneDevices:736, MfaReset:288,
  RiskyUsers:557.
- **Where the check runs.** In the page only for MfaReset, RiskyUsers, Intune and CloudPasswordReset, so any non-page caller of
  those services skips it. In the service for GroupManagement, M365, Migration, Licensing, AccountLockout, SelfServiceGroups,
  BlockedSenders (`BlockedSenderProtectionGate`), and both for EmergencyDisable and ADAttributeEditor.
- **Shared pieces.** `ProtectedPrincipalServicing.NoteFor` is used everywhere (18 sites). The shared write-target gate
  `ForWriteTargetAsync` (ProtectedPrincipalServicing.cs:89) is used by GroupManagementService only. M365's Update and Delete call
  no check; that is already recorded, with no work approved (`.agents/state.md:878-882`).
- **Exemptions.** The three scoped exemptions (Self-Service Groups targets, Comms-10k, Conference Rooms) are untouched by this
  and must stay so.
- **Shape.** One `CheckCloudUserAsync(upn, entraObjectId, actingUser, moduleId)` on the protection service, returning allow,
  refuse(reason) or serviced(note). It is called from each module's service before the write, and the four page copies go.

### B4 (high) - Authorization of a queued job when it runs

- **The contract.** `docs/BulkJobRunner-Plan.md:289-303` and `:347-349`: a job carries a claims snapshot taken at submit and is
  re-checked per row; disabled modules fail closed.
  - `Comms10kReplaceProcessor.cs:221-229` and `ConferenceRoomBulkProcessor.cs:98` and `:125` do this.
  - `MessageTraceDetailJobProcessor.cs` and `MigrationReportExportProcessor.cs` do neither. Grepping them for
    `authori|enabl|revok` finds nothing, and the enqueuers (MessageTrace.razor:1346, Migration.razor:2972) set no
    `AuthSnapshotJson`, which only Comms10k.razor:858 and ConferenceRooms.razor:1767 set.
- **Who sees and cancels jobs.**
  - Comms10k is owner-scoped (`GetOwnedJob`, `CancelOwnedJob`; BulkJobService.cs:270-282).
  - Conference Rooms is module-scoped: any authorized Conference Rooms operator may cancel anyone's job (ConferenceRooms.razor:801,
    :838-839).
  - Admin Bulk Jobs sees all.
  - Not verified whether the Conference Rooms scope is deliberate.
- **Shape.** The runner (`BulkJobService`) checks the snapshot and the module's enablement before every `ProcessRowAsync`, so no
  processor can leave it out. Each enqueuer must supply a snapshot.

### B5 (high) - Admin and user notifications

- **Two `SendAdminNotificationAsync` overloads.** There are 57 call sites.
  - The legacy one (EmailService.cs:82) has fixed rows "Target Mailbox / Affected User / Permission Type". Its header is chosen by
    `action.Contains("Migration")` (:98-101). It is still used by MailboxPermissions, CalendarPermissions, PermissionChangeNotifier,
    Migration (8 sites) and OutOfOffice.
  - Migration and OutOfOffice put non-permission values under those headings. Migration.razor:2007-2008 puts the eligibility
    status under "Permission Type"; :3403-3404 passes "N/A" and "Migration"; OutOfOffice.razor:565 passes "N/A" and "OutOfOffice".
  - Every other module uses the details-dictionary overload (:146).
- **Who sends.** The page, for most modules. The service for AccountLockout (:1186), ADAttributeEditor (service :2439 and :2457,
  plus the page at :642 on an exception) and both job processors.
- **Is a refusal notified?** It differs. IntuneDevices' gates "send no administrator notification" (comment at :1055-1057).
  M365 and NamedLocations say "No administrator email" for a ticket refusal (M365GroupManagement.razor:887-889). Account
  Lockout sends one for a refusal at its ticket gate (`.agents/review/findings/rev-j1.md`, "Pre-existing").
- **User and owner mail for a permission change.** Bulk runs go through `PermissionChangeNotifier` (:104, :120). Single changes
  call `SendUserNotificationAsync` and `SendOwnerNotificationAsync` inline (MailboxPermissions:524-525 and :757-758,
  CalendarPermissions:500-502 and :700-701). The shared piece is half adopted.
- **Self-Service Groups** passes ticket `""` (:773, :904, :1016, :1155), so the subject reads "Ticket #" with nothing after it.
- **NotifySecurityTeam** is one module's key (EmergencyDisable), read by the service (:1158) and by the page (:292). No drift
  across modules.
- **Shape.** One admin notice built from the same operation record as the audit row (B2), with one rule for whether refusals
  notify. Retire the legacy overload. Single permission changes go through `PermissionChangeNotifier`.

### B6 (high) - Operation trace scopes

- `docs/AdminModuleSpec.md:250` says a module that needs a transcript "should begin an OperationTraceService scope before the first
  backend call".
- Explicit scopes exist at 8 sites in 6 modules: BlockedSenders.razor:459, ConferenceRooms.razor:1108 and :1499,
  ConferenceRoomBulkProcessor.cs:150, ADAttributeEditorUndoService.cs:368, AccountLockoutRemediationService.cs:52, :196 and :301,
  EmergencyDisableService.cs:180, LicensingUpdatesService.cs:146.
- Every other writing module gets only the implicit scope that `AuditService.WriteAuditEvent` opens at the audit write
  (AuditService.cs:527-538). Backend steps that ran before it are standalone records, not part of the operation.
- Two injections of the trace service are unused (A33, A37).
- **Shape.** The B2 recorder opens the operation scope when the operation begins, not when it is audited.

### B7 (medium) - Graph client and secret setup

- **Ten private `GetGraphClientAsync` copies, plus GraphOnPremisesSidSource.cs:232.** MfaResetService:20, NamedLocationsService:20,
  M365GroupManagementService:40, RiskyUsersService:41, ServiceHealthService:50, IntuneDeviceService:44,
  EmergencyDisableService:450, CloudPasswordResetService:296, CloudSignInService (:269 fetch), DefenderEndpointDeviceService
  (:371 fetch).
- **What they share.** The same steps: `int.TryParse` the module's `GraphDelineaSecretId`, `GetSecretFieldsAsync`, read
  "Tenant ID"/"Application ID"/"Client Secret", `new GraphTokenClient`.
- **How they differ.**
  - A missing secret returns null in MfaReset, RiskyUsers, Intune and EmergencyDisable, but throws with module wording in
    NamedLocations, M365 and ServiceHealth. ServiceHealth returns null for missing dependencies and throws for missing config
    (:52-57).
  - Intune alone has `ExtractGraphCredentials`.
  - Each parses the Secret ID with `int.TryParse`, which accepts what the app's Secret ID rule `ProtectedPrincipalService.IsSecretId`
    (:1869; aud-a26s: digits only) refuses.
  - Most re-read the key a second time for an "is configured" check (e.g. MfaResetService:43, RiskyUsersService:67).
- **The AD side** has the shared `ModuleCredentialService` (11 services use it). Graph has no equivalent.
- **Shape.** `ModuleCredentialService.GetGraphClientAsync(moduleId)` with one Secret ID rule and one failure result.

### B8 (medium) - Building the PSCredential from a vault secret

- 14 copies of `fullUsername = username.Contains('\\') || username.Contains('@') ? username : $"{domain}\\{username}"` plus the
  SecureString loop: ADAttributeEditorService:2498, AccountLockoutRemediationService:1095, Comms10kService:815,
  ConferenceRoomService:3031, DhcpAuthorizationService:261, EmergencyDisableService:1331, ExchangeServiceBase:694,
  GroupManagementService:1569, LicensingUpdatesService:548, PermissionValidator:1270, ProtectedPrincipalService:2486,
  SelfServiceGroupService:1611.
- BitLocker's `BuildCredentialUserName` (BitLockerLiveDirectorySearch.cs:409-418) differs: it also returns the bare username when
  the domain is blank. The other copies would build `"\user"` (live bug 4).
- **Shape.** One `VaultCredential.ToPSCredential(username, password, domain)` in `ModuleCredentialService`.

### B9 (medium) - Importing the ActiveDirectory module

- The shared `ActiveDirectoryModuleImport.Import` (it clears commands and the error stream) is used by 11 files.
- Inline copies remain: Comms10kService (:100, :192, :326, :469, :805), DhcpAuthorizationService (:36, :90, :159),
  LicensingUpdatesService (:229, :322), SelfServiceGroupService (:982, :1141). SelfServiceGroups uses both forms.
- The inline copies clear only `Commands`. The shared piece's remarks (ActiveDirectoryModuleImport.cs) say psl-8 audited those
  copies and left them, because no later check reads their stream. They are not a defect today, only the copy the 2026-11-10
  ruling forbids.
- **Shape.** Every import goes through the shared piece. Runspace-plus-import setup could follow as one helper.

### B10 (medium) - Bulk upload: limits, CSV reading, results

- **Row cap.** Only MailboxPermissions and CalendarPermissions have one (`BulkCsvRowLimit`, 200; MailboxPermissionService:269-273,
  CalendarPermissionService:327-331). No row cap was found for Migration, LicensingUpdates, ConferenceRooms, GroupManagement or
  SelfServiceGroups bulk lists (grep for `MaxRows|RowLimit|row limit` finds nothing else).
- **Upload size caps, inline.** 5 MB (Migration:2155), 10 MB (CalendarPermissions:576, LicensingUpdates:327,
  MailboxPermissions:615), 16 MB (Comms10k:306, ConferenceRooms:1180 and :1558), 50 MB (MessageTrace:907,
  HeaderAnalysisService:9).
- **CsvReader setup: six copies.** Comms10k.razor:617, CalendarPermissionService:312, ConferenceRoomService:1364,
  LicensingUpdatesService:536, MailboxPermissionService:254, MigrationService:632. Licensing alone sets
  `HasHeaderRecord = false` and `BadDataFound = null`.
- **Already shared.** `CsvExport` (7 pages) and `BulkIdentityList` (5 files).
- **Shape.** One bulk-upload reader taking a per-module row cap and size cap from the descriptor, plus the existing exporter.

### B11 (medium) - Module config defaults written twice

- The catalog's `DefaultValue` is only shown in the editor (ModuleConfig.razor:468, :536, :550, :567). Each service holds its own
  literal default:
  - `HybridEndpoint` "hybrid1": ModuleCatalog.cs:531, MigrationService.cs:34, and again in deploy.ps1:31.
  - `CloudQuotaGB` 99: :536 and MigrationService.cs:37.
  - `AllowedLicenseTypes`: :1722 and LicensingUpdatesService.cs:83.
  - `DefaultThrottleLimit` 32 and `MaxSweepTargets` 10000: :1129-1130 and AccountLockoutRemediationService.cs:285 and :1130.
  - `MaxTotalRows` 10000: :1422 and RiskyUsersService.cs:407.
- Only Defender pins the catalog default to the service constant (ModuleCatalogTests.cs:204-205).
- `docs/ProdReadinessReview-2026-06-12.md:334` already suggested dropping the "hybrid1" default.
- **Shape.** `ModuleConfigService` returns the descriptor's default when a value is unset, and services stop carrying literals.

### B12 (medium) - Typed date and time, and its zone

- Migration reads a typed schedule time in the server's zone (Migration.razor:3200-3214, which shows the resolved UTC before the
  operator commits).
- OutOfOffice reads the browser's zone and refuses a schedule when the zone cannot be resolved (OutOfOffice.razor:274-305).
- RecipientLookup reads the browser's zone and falls back to UTC (RecipientLookup.razor:225-255).
- ConferenceRooms uses `TimezonePicker`.
- That is three conventions for "a time the operator typed". Migration's one schedules a cutover.
- **Shape.** One zone service (browser zone, resolved once per circuit, with one failure rule) used by every page that takes or
  shows a time.

### B13 (medium) - Looking up a Graph user by UPN

- Five separate `/users/{upn}` reads, each with its own escaping and error handling: CloudPasswordResetService:372,
  EmergencyDisableService:532, M365GroupManagementService:313, MfaResetService:52, RiskyUsersService:219.
- **Shape.** One Graph user resolver, beside B7's client.

### B14 (low) - Confirm before write

- About 15 pages hold their own confirm state: AccountLockoutRemediation, BlockedSenders, CloudPasswordReset, Comms10k,
  DhcpAuthorization, EmergencyDisable, IntuneDevices, M365, MfaReset, Migration, NamedLocations, RiskyUsers, and others.
- The two typed confirmations disagree. Account Lockout requires exactly "LOG OFF", ordinal and untrimmed (:216, :223). Intune
  compares the trimmed device name, ignoring case (:710).
- **Shape.** One confirm-bar component with one typed-confirmation rule.

### B15 (low) - Result banners, errors and "No change"

- Every page hand-codes Bootstrap `alert` banners. There is no shared result component.
- The shared "no change" warning (`ExchangeNoChangeWarning`) is used by Mailbox and Calendar only. ADAttributeEditorService keeps
  its own wording (:2385).

### B16 (low) - Identity entry

- `RecipientAutocomplete` is used on 4 pages and `ADIdentityAutocomplete` on 4. About 12 pages take an identity in a raw text box
  with differing placeholders and accepted forms: ADAttributeEditor:54, CloudPasswordReset:48, DelegationReport:30,
  EmergencyDisable:53, M365:128 and :170, MfaReset:53, RecipientLookup:31, RiskyUsers:67, TrueLastLogon:57, Migration:52, and
  others.

### B17 (low) - Verify metadata held beside the catalog

- `ModuleConfigVerifyAdapter.Settings` (:106-121) repeats each verified setting's kind and list separator, which the catalog
  already declares (`FieldType`, `ListSeparator`).
- A test pins the two together (ModuleConfigVerifyEngineTests.cs:241-259), so this is a second copy that is guarded, not drift.

## Possible live bugs (found, not fixed)

1. The Message Trace detail-email job and the Migration report-export job run with no run-time authorization or enablement
   check (B4). A job queued before the module was disabled, or before the operator's access was removed, still runs, and the
   Message Trace one mails results. Not verified how long a job can wait in the queue in practice.
2. `deploy.ps1 -HybridEndpoint` is accepted and silently ignored (A41). An operator who passes it gets nothing.
3. Four ticket boxes bind on change while a button is enabled by the ticket (B1): AdminEventLog:436/:456, Migration:63/:67 and
   :237/:241, NamedLocations:187/:206. The button stays disabled until the box loses focus, so the first click can land on a
   disabled button. Not verified in a browser.
4. 13 of 14 PSCredential builders produce `"\user"` when the vault secret's domain field is blank (B8). BitLocker's does not. Not
   verified what PowerShell or AD do with that name.
5. `Components/Pages/MfaReset.razor:53` has `placeholder="user@analog.com"`. That names this environment's domain in shipped UI.
   Invariant 7 exempts only test fixtures and comments.
6. `LicensingUpdatesService` tests exercise `ParseCsv`, a copy that production does not run (A21). Production's `ParseCsvAsync` has
   no direct test, as far as `git grep ParseCsvAsync ExchangeAdminWeb.Tests` shows.
7. Admin mail for Migration and Out of Office shows non-permission values under "Target Mailbox / Affected User / Permission
   Type" (B5). It is misleading, not unsafe.
8. MfaReset's directory-unresolved check passes `EntraObjectId: null` (MfaReset.razor:450), where the sibling pages pass the id. The
   only matcher that reads `EntraObjectId` is the legacy-exclusion one (ProtectedPrincipalService.cs:2122-2131, :2456-2462), so the
   impact is limited to legacy exclusions. CloudPasswordReset.razor:676-677 documents why MfaReset cannot pass the id.

## Not checked

- **Method scan.** It matches by name, so a member that shares a name with another member that is used (an overload, or the same
  name on two classes) can hide. Properties, public fields, constants and enum members were not scanned, except
  `ConfigFieldType`. Private members of Razor `@code` blocks were not scanned.
- **CSS.** Classes in `wwwroot/app.css` and the `.razor.css` files were not checked for use.
- **Logging.** Message shape, levels, structured fields and the `Logger` versus `_logger?` nullable pattern were not compared
  across modules.
- **Busy flags and click guards.** Names and placement (`IsBusy`, `isLoading`, `busy`, ...) and their match with
  `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` were not compared.
- **Plans.** Completion of each Approved plan was taken from its own `Status:` line. No plan was verified line by line against
  the code.
- **Policy names.** Whether page-level `AuthorizeAsync(..., "<alias>")` literals are pinned to catalog aliases by a test was not
  verified. An unregistered policy name throws rather than permits.
- **Section access and enablement.** These are enforced centrally (`GroupAuthorizationHandler.cs:58`). Per-page enablement
  re-checks were not compared beyond B4.
- **Undo.** Only `ADAttributeEditorUndoService` implements `IUndoableModule`. No other undo path exists to compare.
- **Not run.** No browser, build or test run, so nothing behavioural was observed. Every claim above comes from reading files and
  grepping.
