# Audit: AI slop and common LLM code defects, whole repo (2026-11-10)

Read-only audit of master at `4a5786d2`. No build, no test run, no browser. Every file:line below was opened on that tree.
Counts are from `git ls-files` plus grep/awk; where a count is a heuristic it says so. Nothing here is a fix or a plan.

Scope measured: app code (Services, Components, Modules, Models, Authorization, Middleware, Program.cs) 295 files,
115,645 lines. Tests 455 files, 164,010 lines. PowerShell (tools, deploy.ps1) 8,749 lines; Pester 12,497 lines.
Markdown 894 files, 12.8 MB (app code is 6.0 MB, test code 8.1 MB).

## 1. Verdict

The runtime code is not sloppy where it matters. Authorization, protection and config stores fail closed everywhere I
looked, async/threading hygiene is clean, there are no TODOs, no suppressed warnings, no swallowed-into-success catches.
I found no new confirmed behavioural defect; four possible ones are listed.
The slop is in what surrounds the code, and it is heavy:
1. Comments are a change log, not an explanation. 25% of app lines are comments; 2,889 app lines cite a finding ID
   (aud-, kd-, sweep item) that only `.agents/` can decode. 9 of the 17 comments that cite a file:line are stale.
2. Business logic lives in `.razor` pages (handlers of 250-360 lines doing authorization, protection, audit, mail).
   Nothing can execute a page, so 781 of 5,461 test methods (14%) string-match source text instead.
3. `ClickGateRegistry` pins 216 controls by line number. Production code is now written to fit it: whole methods on
   one 600-1,150 character line "so no line-keyed entry moves".
4. `ModuleCatalog.cs` is 52% comment; its per-version history is already wrong for 21 of 31 modules.
5. Process paperwork outweighs the product: 12.8 MB of markdown, 610 finding files, a 77 KB state file, 79 implemented
   plans (1.9 MB) still in `docs/`, and the two module contract docs last verified 2,721 commits ago.
Sound, do not spend money here: fail-closed stores, LDAP/OData escaping, secret handling, async void guards, interface
count (27, all test seams), test naming, ops scripts' error model, README config keys (34 checked, 0 missing).

## 2. Scorecard

| # | Category | Found by search | Opened and confirmed | DEFECT / DEBT / COSMETIC | Summary |
|---|---|---|---|---|---|
| 1 | History comments | 2,889 ID-bearing app lines in 185 files; 28,839 comment lines; 737 comment blocks of 10+ lines | 40 opened, 40 confirmed; 17 file:line citations all checked, 9 stale | 0 / 1 / 1 | Comments narrate findings and reviews; unreadable without `.agents/` |
| 2 | Defensive code | 947 catch sites (750 `catch (Exception`, 91 bare, 25 empty) | 45 opened: 13 "returns true" all fail-closed (false positives), 4 failure-hiding fallbacks, about 68 no-op wrappers | 0 / 3 / 0 (+2 possible defects) | No fail-open in security paths; audit/mail wrappers and nullable dependencies are noise |
| 3 | Over-engineering | 27 interfaces; 3 legacy-import copies | 27 checked: 26 are test seams or multi-implementation | 0 / 1 / 0 | Low. Not an over-abstracted codebase |
| 4 | Size and sprawl | 2,259 methods measured: 87 of 100+ lines, 18 of 200+ | top 30 listed, 6 opened | 0 / 1 / 0 | Pages are the god objects: 5 pages over 1,700 lines |
| 5 | Sibling inconsistency | 4 LDAP escapers, 3 OData escapers, about 79 result types, 12 runspace factories | escapers opened and compared (identical behaviour) | 0 / 1 / 1 | Beyond input audit Part B: duplicated helpers, one-off result shapes |
| 6 | Correctness smells | 7 async void, 9 .Wait, 2 GetResult, 115 Task.Run, 19 discards, 36 DateTime.Now, 0 TODO, 0 pragma, 3 null-forgiving | all async void, GetResult, discards, static state opened | 0 / 0 / 2 (+1 possible defect) | Clean. Three AD `-Filter` strings are the only open question |
| 7 | Hallucination residue | targeted checks only | 7 confirmed | 0 / 3 / 4 | Stale stamps and counts in docs, one false comment, one invented cmdlet name in a comment |
| 8 | Tests that cannot fail | 5,461 test methods; 781 source-text scans (heuristic); 146 literal-duplicate Facts in 65 groups | 8 files sampled | 0 / 1 / 0 | No vacuous asserts found; the problem is source-scanning as a stand-in for behaviour |
| 9 | Implementation pins | 216 line-keyed entries; 166 NonPublic reflection uses in 86 test files | registry, playbook, 6 distorted production sites opened | 0 / 1 / 0 | The line-number pin now shapes production code |
| 10 | PowerShell | 37 SilentlyContinue, 6 empty catch, 28 LASTEXITCODE checks, 15 duplicated function names | all 37 + 6 opened | 0 / 1 / 0 | Sound. Probes and cleanup only; helper duplication partly by design |
| 11 | Docs and process | 894 md files, 12.8 MB | counts measured; 4 contradictions opened | 0 / 1 / 0 | Yes, the paperwork is itself slop, by volume |
| 12 | User-facing text | 27 long literals, 94 title tips, 53 setting helps | 3 help misses, 4 internal-ID strings, 5 footers | 0 / 0 / 3 | Messages are specific and actionable; small residue |
| 13 | Version noise | 31 version comments, about 1,000 lines | all 31 compared to `Version` | 0 / 1 / 0 | 21 of 31 histories lack an entry for the shipped version |

"0 DEFECT" means none confirmed by reading. Nothing was executed.

## 3. Defects

### Confirmed

None new. Two from the input audit were re-read and stand (section 5): `Components/Pages/MfaReset.razor:53`
`placeholder="user@analog.com"` (names this environment in shipped UI, invariant 7), and `deploy.ps1:31`
`[string]$HybridEndpoint = "hybrid1",` (accepted, never read).

`.agents/state.md` (entry 2026-11-09 17:40) already records an unadmitted candidate: an Account Lockout execute
refused at the ticket gate "is still marked executed and the admin notification is still sent". Not re-verified here.

### Possible defects, not confirmed

P1. On-premises message trace silently narrows to one server when the server list cannot be read.
- `Services/MessageTraceService.cs:675` `ScriptBlock.Create("Get-TransportService -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name")`
- `:686-691` `catch { ps.Commands.Clear(); ps.Streams.Error.Clear(); return Array.Empty<string>(); }` (no log)
- `:570-572` `var servers = GetOnPremTransportServers(ps, session); if (servers.Length == 0) servers = [string.Empty];`
- Trigger: `Get-TransportService` fails or returns nothing. Effect: the tracking query runs against the session's
  default server only, and the result carries no warning, so the operator reads a partial trace as complete.
- Would confirm: on a multi-server on-prem organisation, deny the credential `Get-TransportService` and compare results.

P2. Three Active Directory `-Filter` strings are built by doubling quotes only.
- `Services/GroupManagementService.cs:126,135` `var escaped = searchTerm.Replace("'", "''");` then
  `.AddParameter("Filter", $"Name -like '*{escaped}*' -or SamAccountName -like '*{escaped}*' -or Mail -like '*{escaped}*'")`
- `Services/GroupManagementService.cs:1550-1552` `$"SamAccountName -eq '{escaped}' -or Name -eq '{escaped}' -or Mail -eq '{escaped}'"` (resolves the group a write targets)
- `Services/ConferenceRoomService.cs:2664-2666` `.AddParameter("Filter", $"mail -eq '{escaped}'")`
- The repo's own comment says this form is unsafe: `Services/Comms10kAddressResolver.cs:137-141` "The old code
  interpolated the address into `-Filter`, where PowerShell expands `$` as a variable and an address containing one
  silently queried something else". Every other directory lookup (about 45 sites) uses `-LDAPFilter` with an escaper.
- Trigger: a search term, alias or address containing `$` (or a backtick). Effect: a different object, or none, is
  matched. Would confirm: run `Get-ADGroup -Filter` through the SDK with such a value.

P3. The attribute legend read hides a database failure.
- `Services/Storage/AttributeEditorRepository.cs:183` `/// <summary>Reads the legend (fail-open: returns empty on any read failure).</summary>`
  and `:206-209` `catch { return new Dictionary<...>(StringComparer.OrdinalIgnoreCase); }` (no log).
- `Services/ADAttributeEditorService.cs:941-962`: `GetEffectiveChoices` then returns `attr.Choices ?? []` only.
- Trigger: the legend table cannot be read. Effect: an attribute's dropdown loses the legend's choices and every
  description, with no error and no log line. Would confirm: whether any attribute relies on legend-only choices.

P4. A directory search failure looks like "no match" in the pickers.
- `Services/ADDirectorySearchService.cs:86-87` `if (!IsAvailable) return [];`, `:93-94` throttle timeout `return [];`,
  `:114-117` `catch (Exception ex) { _logger.LogWarning(...); return []; }`
- `Components/Shared/ADIdentityAutocomplete.razor:148-155`: results empty, dropdown hidden.
- Effect: with AD unreachable the picker shows nothing, as it does for a name that does not exist. Logged, not shown.
  Low; listed because it is the "default value on error" shape the brief asks for.

## 4. Evidence by category

### 4.1 Comments that narrate history (DEBT)

Comment lines (line starts with `//`, `///`, `*`, `@*`, `#`, `<!--`) and lines carrying a finding ID
(`aud-|kd-|rev-|psl-|gmn-|...-NN`, "sweep item N", "finding N", "audit ANN"):

| Folder | Lines | Comment lines | Lines with a finding ID | Files with one |
|---|---|---|---|---|
| Services | 68,661 | 18,522 (27%) | 1,780 | 126 |
| Components | 38,150 | 6,619 (17%) | 790 | 36 |
| Modules | 2,060 | 1,070 (52%) | 151 | 1 |
| Models | 2,970 | 1,227 (41%) | 84 | 9 |
| Authorization | 2,905 | 1,146 (39%) | 70 | 11 |
| Middleware + Program.cs | 899 | 283 | 14 | 2 |
| ExchangeAdminWeb.Tests | 164,010 | 23,477 (14%) | 2,140 | 318 |
| tools + deploy.ps1 | 8,749 | 1,375 | 284 | 9 |
| tests/ps | 12,497 | 1,138 | 434 | 9 |

In app comments the ID families are: aud 1,509 occurrences, kd 712, rev 65, psl 60, gmn 41, then 30 smaller families.
332 app comment lines say "used to / previously / no longer / the fix / formerly / originally". 737 comment blocks
run 10+ lines, 171 run 20+, 20 run 40+. Worst files by ID lines: ConferenceRoomService.cs 236, ModuleCatalog.cs 151,
Migration.razor 148, ModuleConfig.razor 129, MigrationService.cs 106, ADAttributeEditorService.cs 106.

Can a reader without `.agents/` understand them? Partly. Most ID comments do state the reason in words after the ID,
and those survive. What does not survive: the ID itself, "the first fix", "review showed", "as it was", "kd-72's
shape", and every comparison to behaviour that no longer exists. The design-rationale blocks without IDs
(e.g. `Authorization/CircuitMembershipRevalidator.cs:43-105`, `Services/DefenderEndpointDeviceService.cs:427-483`)
are long but genuinely explain the code; they are not the problem.

Worst twenty:

1. `Modules/ModuleCatalog.cs:314-514` - 201 comment lines of version history above one `Version = "1.27.5",` line (see 4.13).
2. `Services/ConferenceRoomService.cs:2976-2987` - 12 lines above a one-line forwarder
   (`=> TicketPresence.Refusal(ticket);`), including "It lives here rather than on the page because the page is
   line-keyed by ClickGateRegistry and the fix had to leave its line count alone. Since kd-24 it is the shared ...".
3. `Services/MessageTraceExportListing.cs:269-274` - "The first fix excluded it, on the stated grounds that the startup
   orphan sweep flips jobs with no completion hook - and review showed that is simply FALSE: BulkJobService.cs:95 ...".
   A review argument left in the code; the cited file is now `Services/Jobs/BulkJobService.cs` and line 95 is `}`.
4. `Components/Pages/RiskyUsers.razor:953-958` - "ProtectedPrincipalService.MatchesIdentity compares EntraObjectId
   against every protected USER row (Services/ProtectedPrincipalService.cs:693-710) - verified, not assumed". Now
   false: lines 693-710 are cache code, `MatchesIdentity` is at `:2122` and its only caller is
   `CheckLegacyExclusions` (`:2461`).
5. `Services/DefenderEndpointDeviceService.cs:873-879` - "That method is GraphTokenClient.cs:109-126 and line 122 is
   `if (!response.IsSuccessStatusCode) return null;`". Lines 109-126 are now the URL guard.
6. `Components/Pages/ConferenceRooms.razor:837` - "kd-5: Cancel audited as Remove is, from the RENDERED row; kd-27: both
   re-check access first, the denial audited. One line each, so no line-keyed entry moves."
7. `Components/Pages/Migration.razor:1890` - a 200-character trailing comment on a two-statement line:
   "// aud-a23l: the cmdlet's own words, without PowerShell's ... prefix; aud-a23p: and the pane's, while this load still owns it".
8. `Services/DelegationReportService.cs:64-66` - "Review of A18. A row missing either field used to be dropped in
   silence ... the same lie this finding is about, one row at a time."
9. `Components/Pages/ConferenceRooms.razor:1300-1302` - "Sweep item 42 (kd-72's shape). ... The flag is raised before
   the first await and lowered in the finally, as it was."
10. `Services/IntuneDeviceService.cs:41-42` - "Copied in shape from MfaResetService.cs:20-37". The comment documents
    the copy (input audit B7) instead of removing it.
11. `Services/ProtectedPrincipalService.cs:215` - "IIdentityResolver is scoped (Program.cs:167, :173)". It is registered
    at `Program.cs:372`.
12. `Services/BlockedSenderProtectionGate.cs:48` - "(`requiresFullResolution`, `PermissionValidator.cs:116-118`)". That
    name is at `PermissionValidator.cs:368`.
13. `Services/Comms10kAddressResolver.cs:139-140` - "the lesson recorded at `SectionAccessGroupDirectory.cs:132`". Line
    132 of that file is blank.
14. `Components/Pages/MessageTrace.razor:668-669` - "Read it the same way Components/Pages/SelfServiceGroups.razor:325
    does." The PrimarySid read is at `:539`; `:325` is markup commentary.
15. `Services/MessageTraceExportListing.cs:97-98` - "(BulkJobService.cs:401, BulkJobRepository.TryFinish) ...
    (BulkJobService.cs:441)". `TryFinish` is called at `:218` and `:744`, the hook at `:823`.
16. `Services/AuditService.cs:545-547` - "aud-a28u. ... It read Success whatever became of the row, so after a drop the
    trace claimed an audit row the log did not have."
17. `Services/Jobs/BulkJobService.cs:120-124` - "kd-85. ... that read was outside any try: the first read the store
    failed ended the loop ... as the runner's tail has it (kd-84)".
18. `Services/ExchangeServiceBase.cs:476-480` - one paragraph citing aud-a23h, aud-a23g and kd-89 above an 8-line method.
19. `Services/ExoConnectionPool.cs:130` - "Audit finding aud-a6c. Dispose used to drain only what was idle at that instant ...".
20. `Services/ADAttributeEditorService.cs:2010-2012` - "aud-a2p's, a sibling because Conference Rooms' is private -
    since aud-a26l the value itself rather than its text ... *Assumption, not probed*".

File:line citations inside app comments: 17 found (`grep '\.(cs|razor|ps1):[0-9]+'` on comment lines), all 17 checked,
9 stale (items 3, 4, 5, 11, 12, 13, 14, 15 twice). The other 8 still point at the right lines.
Direction: keep the reason, delete the ID and the "used to"; never cite a line number in a comment.

### 4.2 Defensive code (DEBT; fail-open: none found)

Search: every `catch` in app code (947), then an awk pass over the 7 lines after each catch for `return true`,
`Success = true`, an empty collection, null/default, `return false`, or a throw.

Load-bearing, keep (opened, all correct): 13 "catch then true" hits are all fail-closed or deliberate.
`Services/ModuleEnablementService.cs:338-369`, `Services/ProtectedPrincipalService.cs:1796-1824` and
`Services/SectionAccessService.cs:435-464` return true meaning "corrupt, deny" with a logged reason.
`Services/ServiceNowService.cs:176-183` returns `IsValid = false` on any exception. `ExchangeServiceBase.ReadUnlessNotFound`
(`:479-489`) returns empty only `when (IsNotFound(ex, identity))`. Result-object catches in the services
(Dhcp, EmergencyDisable, MfaReset, AccountLockout, about 35 sites) return a failed result carrying the message.

Noise:
- Audit calls wrapped in try/catch although the writer is built not to throw. `Services/AuditService.cs:569-570`:
  "The writer never throws (aud-a28q) and says whether it wrote the row". The input audit counts 7 `SafeAudit` copies
  and about 52 inline `try { Audit. ... } catch` (B2). Four of those I opened swallow with no log at all, against the
  Constitution's "Log audit failures separately": `Components/Pages/AdminSettings.razor:952-953`
  (`try { Audit.LogSettingsChange(...); } catch { }`), `Components/Pages/M365GroupManagement.razor:429-430`,
  `Components/Pages/MessageTraceReports.razor:193-198` and `:204-208`. Unreachable today, so DEBT, not DEFECT; the
  real gap is the ignored return value, which B2 already covers.
- 9 admin-mail calls end in a bare `catch { }` on the same line (`ADAttributeEditor.razor:642`,
  `DhcpAuthorization.razor:369`, `:460`, `GroupManagement.razor:690`, `:710`, `:820`, `:835`, `NamedLocations.razor:448`,
  `:522`). `EmailService` already catches and logs its own send (`Services/EmailService.cs:1126-1128`).
- Production dependencies declared nullable to serve a test constructor, then null-checked at run time: 38 fields in
  16 files. Example `Services/IntuneDeviceService.cs:14-16` (`ModuleConfigService? _moduleConfig; DelineaService?
  _delineaService; IHttpClientFactory? _httpClientFactory;`) and `:46` `if (_moduleConfig == null || _delineaService
  == null || _httpClientFactory == null) return null;`. Same shape in CloudSignInService, DefenderEndpointDeviceService,
  RiskyUsersService, ServiceHealthService. DI always supplies them.
- Best-effort reads with an empty catch and no log: `Components/Pages/ConferenceRooms.razor:1095-1101` (old values for
  the audit row), `Components/Pages/AdminEventLog.razor:832-833` (undo availability), `Services/ExtendedLogService.cs:478-487`
  (log level reload), `Services/ConferenceRoomService.cs:1168-1177` (`catch { /* use raw value */ }`).

Failure-hiding fallbacks: P1, P3, P4 above, plus `Services/GroupManagementService.cs:128-132` ("Null catalog falls
back to the local domain - a degraded search beats a broken one"), which logs a warning but shows the operator nothing.
Direction: delete the wrappers the writer makes unreachable; make dependencies non-nullable.

### 4.3 Over-engineering (low)

27 interfaces in 217 service and authorization files. 26 have a test double or several implementations
(`IBulkJobProcessor` 4, `IVerifyStoreAdapter` 4). The one with neither, `IExoRecipientCommands`, is input audit A6.
No speculative generic layers found. What there is:
- Three hand-copies of the June JSON-to-SQLite import (`ImportLegacyIfPresent` in ModuleEnablementService.cs:326,
  and the same shape at ProtectedPrincipalService.cs:1785-1826 and SectionAccessService.cs:425-466), still run at every
  start. "legacy" appears 424 times in 42 app files. Whether any deployment still has those JSON files was not checked.
- Unused options and parameters: input audit A40-A44.
Direction: retire the legacy import through a plan once no instance carries the files.

### 4.4 Size and sprawl (DEBT)

Top 25 files by lines (tracked `.cs`, `.razor`, `.ps1`, `.psm1`):

| Lines | File | Lines | File |
|---|---|---|---|
| 5,406 | ExchangeAdminWeb.Tests/ClickGateRegistry.cs | 2,128 | Tests/PermissionPagesSelfGrantTests.cs |
| 4,474 | Components/Pages/Migration.razor | 2,070 | Tests/ConferenceRoomWriteIdentityTests.cs |
| 3,705 | tests/ps/DeployInvariants.Tests.ps1 | 2,034 | Components/Pages/AdminSettings.razor |
| 3,620 | Components/Pages/ModuleConfig.razor | 2,020 | Services/MigrationService.cs |
| 3,151 | Services/ConferenceRoomService.cs | 1,990 | Services/DefenderEndpointDeviceService.cs |
| 2,741 | Tests/MigrationStatusPageTests.cs | 1,937 | Modules/ModuleCatalog.cs |
| 2,620 | Tests/ClickGateTests.cs | 1,848 | Components/Pages/ConferenceRooms.razor |
| 2,522 | Services/ADAttributeEditorService.cs | 1,819 | Tests/CircuitRevalidationTests.cs |
| 2,497 | Services/ProtectedPrincipalService.cs | 1,761 | Tests/ConferenceRoomRoomListPreflightTests.cs |
| 2,455 | Tests/DefenderEndpointDeviceServiceTests.cs | 1,752 | Components/Pages/AdminEventLog.razor |
| 2,398 | Tests/ProgressRegistry.cs | 1,693 | Components/Pages/IntuneDevices.razor |
| 2,177 | tests/ps/AppPoolControl.Tests.ps1 | 1,681 | Tests/PermissionValidatorSelfGrantTests.cs |
| 2,176 | tests/ps/InstallerConfigBackup.Tests.ps1 | | |

Top 25 methods by lines (awk heuristic: members at class indentation, brace to brace; 2,259 measured, 87 of 100+
lines, 18 of 200+). Eleven of the 25 are page event handlers:

| Lines | Method |
|---|---|
| 362 | Components/Pages/IntuneDevices.razor:942 `ExecuteActionAsync` |
| 291 | Components/Pages/BlockedSenders.razor:310 `ConfirmUnblock` |
| 277 | Services/EmergencyDisableService.cs:172 `DisableAsync` |
| 276 | Components/Pages/CloudPasswordReset.razor:379 `ExecuteResetAsync` |
| 272 | Services/ConferenceRoomService.cs:1065 `SetRoomTypeAsync` |
| 267 | Services/RecipientLookupService.cs:22 `GetRecipientInfoAsync` |
| 267 | Services/ConferenceRoomService.cs:580 `SetRoomMetadataAndListAsync` |
| 266 | Components/Pages/MfaReset.razor:294 `ExecuteReset` |
| 266 | Components/Pages/AdminSettings.razor:1608 `SaveProtectedPrincipals` |
| 251 | Components/Pages/RiskyUsers.razor:826 `ExecuteActionAsync` |
| 250 | Components/Pages/ExchangeOnlineConfig.razor:378 `SaveExoConfig` |
| 237 | Services/MailboxPermissionService.cs:252 `ProcessMailboxPermissionsCsvAsync` |
| 236 | Components/Pages/OutOfOffice.razor:380 `SetOof` |
| 222 | Services/CalendarPermissionService.cs:310 `ProcessCalendarPermissionsCsvAsync` |
| 205 | Services/GroupManagementService.cs:390 `GetMembersAsync` |
| 204 | Components/Pages/AdminEventLog.razor:579 `LoadEvents` |
| 203 | Services/MigrationService.cs:69 `CheckEligibilityAsync` |
| 203 | Components/Pages/MailboxPermissions.razor:350 `SubmitSingle` |
| 199 | Services/ProtectedGroupMembership.cs:291 `Evaluate` |
| 199 | Services/ConferenceRoomService.cs:2259 `AddToRoomListViaAdAsync` |
| 197 | Services/PermissionValidator.cs:326 `ValidateTargetMailboxAsync` |
| 194 | Services/ADAttributeEditorService.cs:1367 `PerformSave` |
| 193 | Services/ADAttributeEditorUndoService.cs:166 `ExecuteUndoAsync` |
| 189 | Components/Pages/ModuleConfig.razor:1839 `SaveSectionAccessAsync` |
| 184 | Components/Pages/CalendarPermissions.razor:346 `SubmitSingle` |

`@code` blocks: Migration.razor 3,213 lines, ModuleConfig.razor 2,826, AdminSettings.razor 1,600, AdminEventLog.razor
1,288, ConferenceRooms.razor 1,218, IntuneDevices.razor 1,141. ModuleConfig.razor injects 16 services; four other
pages inject 14. The page handlers hold the ticket gate, re-authorization, the protected-principal check, the audit
row and the admin mail (input audit B1-B5), which is why nothing but source scans can test them (4.8).
Long parameter lists: `AuditService.LogMailboxPermission` 12 (`:88`), `LogCalendarPermission` 11, `LogMigrationBatch` 11,
`LogMfaResetAction` 11; `EmergencyDisableService` constructor 12; `DefenderDeviceListResult.CompleteRun` 15
(`Models/DefenderDeviceModels.cs:596`).
Direction: move handler bodies to services; the page keeps binding and display.

### 4.5 Inconsistency between siblings (beyond input audit Part B)

- Four LDAP filter escapers with identical behaviour: `Services/ADAttributeEditorService.cs:2002`,
  `Services/BitLockerLiveDirectorySearch.cs:421`, `Services/ProtectedPrincipalService.cs:2466`,
  `Services/SelfServiceGroups/AdOwnershipFilter.cs:23`. Three OData literal escapers:
  `DefenderEndpointDeviceService.cs:1754`, `IntuneDeviceService.cs:677-680`, `RiskyUsersService.cs:429`; a fourth inline at
  `M365GroupManagementService.cs:74`.
- Twelve private runspace factories ending in `return RunspaceFactory.CreateRunspace(iss);` (ADAttributeEditorService:987,
  ADDirectorySearchService:728 and :981, ADOrganizationalUnitService:46, Comms10kService:528, DirectorySessionPool:60,
  EmergencyDisableService:485, ExchangeServiceBase:210, ExoConnectionPool:186, GroupManagementService:1362,
  SelfServiceGroupService:1298, plus three `var runspace =` forms). Bodies not compared.
- About 79 `*Result` / `*Outcome` types. At least ten are the same "success flag plus message" under different names
  and member names (by declaration; class bodies not all opened): `PermissionResult`, `RoomOperationResult`,
  `DhcpOperationResult`, `MfaResetResult`, `NamedLocationResult`, `IntuneDeviceActionResult(bool Success, string Message,
  string? SafeError)`, `RiskyUserActionResult(string UserId, bool Success, string Message)`, `UndoResult(bool Success,
  string? Error, ...)`, `LockdownStampResult(bool Ok, ...)`, `VerifyActionResult(bool Succeeded, string Message, ...)`.
- Three autocomplete components each carry the same blur hack: `ADGroupAutocomplete.razor:152`,
  `ADIdentityAutocomplete.razor:291`, `RecipientAutocomplete.razor:263` (`_ = Task.Delay(200).ContinueWith(...)`).
- Good: async naming. 207 of 208 public Task-returning service methods end in `Async`. One sync method wears the
  suffix: `Services/Jobs/BulkJobService.cs:126` `public void InitializeAsync()` (COSMETIC).
Direction: one escaper per grammar; one operation-result type.

### 4.6 Correctness smells (clean)

| Smell | Found | Verdict |
|---|---|---|
| `async void` | 7 (AdminBulkJobs:237, Comms10k:1200, ConferenceRooms:817, GlobalProgress:208, :211, :215, InFlightWorkGuard:188) | All event handlers, all guarded by try/catch (GlobalProgress via `Refresh`, `:234-265`) |
| `.Result` on a task | 2 (`MembershipRevalidatingAuthenticationStateProvider.cs:133`, `:254`) | Both behind `IsCompletedSuccessfully` |
| `.Wait(` | 8 real | Semaphore waits with timeouts inside `Task.Run` bodies |
| `GetAwaiter().GetResult()` | 2 (`Jobs/BulkJobService.cs:823`, `PowerShellDirectoryCommands.cs:86`) | Background thread and stop path; inside try |
| `Task.Run` | 115 | Wrap blocking PowerShell SDK calls; correct for Blazor Server |
| Fire-and-forget | 19 discards | Continuations observe the fault or the body catches (`UsageTelemetryService.cs:223`, `BulkJobService.cs:462-467`) |
| Mutable static state | 4 (`ClientInfoService.cs:8`, `GroupManagementService.cs:182`, `HeaderAnalysisService.cs:16`, `MailboxGuidIdentityRefusal.cs:31`) | Benign caches and a startup-set logger |
| `DateTime.Now` | 36, against 114 `UtcNow` | File names, typed local times, Exchange "past time" idiom. No stored timestamp uses it |
| Culture-sensitive compare | 0 `ToLower()`/`ToUpper()`, 0 comparison-less `StartsWith` | Clean |
| String-built queries | 45 LDAP, 8 OData, 3 PowerShell command strings | All escaped (`MessageTraceService.cs:740` `PowerShellLiteral`); SQL is parameterised. Exception: P2 |
| `new HttpClient` | 0 | `IHttpClientFactory` throughout |
| TODO/FIXME/HACK, commented-out code | 0 markers | Clean |
| `#pragma warning disable`, `SuppressMessage` | 0 | Clean |
| Null-forgiving `!` | 3 dereferences, 25 `null!`/`default!` | `default!` are `[Inject]` properties |
| Undisposed | `JsonDocument.Parse` at `ServiceNowService.cs:116` | COSMETIC |

COSMETIC: mail bodies print server-local time with no zone (`Services/EmailService.cs:296`
`<strong>Date:</strong> {DateTime.Now:MMMM dd, yyyy 'at' h:mm tt}`, same at `:379`, `:449`, `:521`, `:681`).

### 4.7 Hallucination residue

Checked: 34 `Section:Key` config names in README against code (0 missing); backticked file names in README, the
Constitution, both module docs, repo-guidance and AGENTS.md (0 missing inside the repo; `Export-BitLockerKey.ps1` at
`README.md:632` is an external script); Graph permission names in code and README (all real); every cmdlet name passed
to `AddCommand` (all real). Found:

1. `Services/MigrationUserActionPlanner.cs:10` `/// <summary>Complete-MigrationUser against selected mailboxes that have
   finished syncing.</summary>`. No code calls a cmdlet of that name (`git grep Complete-MigrationUser` finds only this
   line), and to my knowledge Exchange Online has none. COSMETIC.
2. `Components/Pages/RiskyUsers.razor:953-958` states a check that the code no longer performs (4.1 item 4). DEBT: it
   tells the next editor that Entra-object-id protection is "genuinely effective here".
3. `docs/AdminModuleSpec.md:3-4` `Version: 2.0` / `Last verified against code: commit cd83823 (2026-06-25)`. That is
   2,721 commits ago. repo-guidance calls this doc "binding for `Modules/` and `Components/Pages/`". DEBT.
4. `docs/AdminModuleDeveloperGuide.md:3-5` `Host baseline: ExchangeAdminWeb 2.3.22`, last verified 2026-06-25. The app
   is 2.36.2 (`ExchangeAdminWeb.csproj:11`). DEBT.
5. `docs/GlobalProgressSystem-Plan.md:35` (Approved) "across all 36 modules". The catalog has 31
   (`grep -c 'Id = "' Modules/ModuleCatalog.cs`). COSMETIC.
6. `docs/ProjectConstitution.md` Verification Baseline lists `dotnet build ExchangeAdminWeb.csproj --no-restore` and
   `dotnet format ExchangeAdminWeb.csproj --verify-no-changes --no-restore`. repo-guidance and CI
   (`.github/workflows/ci.yml:17-21`) use `ExchangeAdminWeb.slnx` and say "Always target the `.slnx`". The highest
   authority names the weaker command. DEBT.
7. `Modules/ModuleCatalog.cs` version histories disagree with `Version` for 21 modules (4.13). COSMETIC.

### 4.8 Tests (DEBT in one shape; otherwise sound)

5,461 test methods (4,374 `[Fact]`, 1,087 `[Theory]` with 3,313 `[InlineData]`); state.md reports 8,904 executed cases.
0 `Assert.True(true)`, 0 `Skip =`, 0 test files without an assertion, 0 test names carrying a finding ID.
`.Received(` appears 79 times and `DidNotReceive` 65 across the suite, so "asserting the mock" is not a pattern here.
Literal-duplicate bodies that should be theories: 146 Facts in 65 groups (3%), worst
`ModuleCatalogTests.cs:44` (5 copies) and `AdSiteSubnetMapTests.cs:19` (4). COSMETIC.

Source-text scans: 781 of 5,461 test methods (14%) in 135 of 433 test files (awk heuristic: a test whose body, or a
helper it names, reads a `.razor`/`.cs`/`.ps1` file from the repo; 32 files are scans only). 8 files sampled.

Legitimate tripwires (a structural rule that only source can state): `WholeStoreWriterTripwireTests`,
`SharedTempDatabaseTripwireTests`, `RazorParseGateTests`, `AuditCategoryFilingTests`, `UsageTrackerWiringTests`,
`PageAuthorizationRecheckTests`, CSS guards. Roughly 100 tests.

Standing in for a behavioural test (the larger share): the page suites.
- `MigrationStatusPageTests.cs` 99 of 100 tests. Its own header (`:11-13`): "THESE ARE TRIPWIRES, NOT BEHAVIOURAL
  COVERAGE. The repo has no bUnit harness, so no test can render Migration.razor or exercise one of its handlers".
  Example `:47-50`: `Assert.Contains("private readonly HashSet<string> selectedBatches = new(StringComparer.OrdinalIgnoreCase);", page, ...)`.
- `IntuneDevicesPageTests.cs` 58 of 95, `RiskyUsersPageTests.cs` 37 of 54, `GroupBulkActionsWiringTests.cs` 29 of 29,
  `CloudPasswordResetWritePathTests.cs` 23 of 23, `GroupMemberNestingProtectionTests.cs` 22 of 43.
- `GroupManagerNotificationTests.cs:49-51` proves a notification rule by counting a regex:
  `Regex.Matches(page, @"if \(sendAdminEmail && result\.Success && result\.Changed\)")` then `Assert.Equal(2, gated.Count);`.
  Renaming a local fails it; changing what the branch does passes it.
- `EmergencyDisableServiceTests.cs:407` `Page_LockdownCheckbox_DefaultsUnchecked` is a page scan inside a service suite.
These tests are honest about what they are and several record the mutation that proved them. The cost is structural:
they exist because the logic is in the page (4.4).
Enormous test files: 13 over 1,300 lines (list in 4.4). Tests are 1.4x the app by lines.
Direction: logic to services, then replace the page scans with service tests.

### 4.9 Tests pinned to implementation detail (DEBT, and it has reached production code)

`ExchangeAdminWeb.Tests/ClickGateRegistry.cs`: 5,406 lines (1,950 comment), 216 entries keyed by page line number
(116 `DomSyncedControl`, 44 `AnnotatedControl`, 37 `ExemptControl`, 7 `KeyboardPath`, 6 `NonButtonTarget`,
6 `UngatedDomSyncedControl`), 12 pages, plus an `ExpectedLineCount` per page. Changed in 149 commits.
What it protects: every clickable control on a converted page is disabled while the page is busy, and a new control
cannot arrive ungated. That is a real class of defect here (double submit).
What it costs:
- Any line added to a page invalidates every entry below it. There is a playbook for the repair
  (`.agents/playbooks/clickgate-reanchor.md`: "the five-trap manual procedure", in `ProgressRegistry.cs`'s words).
  101 commit subjects since 2026-08-01 mention re-anchoring.
- Production code is being bent to avoid the repair. Quoted:
  - `Services/ConferenceRoomService.cs:2983-2984` "It lives here rather than on the page because the page is
    line-keyed by ClickGateRegistry and the fix had to leave its line count alone."
  - `Services/SelfGrantRefusalRecord.cs:12-13` "writes its refusal through this, on one line: the pages are line-keyed
    by ClickGateRegistry, and the one free line could not hold `SubmitSingle`'s try/catch".
  - `Components/Pages/ConferenceRooms.razor:837-838` "One line each, so no line-keyed entry moves." followed by
    `private async Task CancelJob(...) { if (!await JobControlAllowedAsync(...)) return; var cancelled = false; ...`
    - an authorization re-check, a cancel and an audit write as one line.
  - `Components/Pages/CalendarPermissions.razor:522` is 1,150 characters; `MailboxPermissions.razor:545` is 1,123;
    `Migration.razor:3651` is 637. Lines over 250 characters: Migration 35, CalendarPermissions 21,
    MailboxPermissions 21, GroupManagement 16, SelfServiceGroups 14.
  - `Services/MigrationService.cs:1106`, `Components/Pages/Migration.razor:1552`, `TrueLastLogon.razor:269` say the same.
- The newer `ProgressRegistry.cs` (2,398 lines) says why it did not copy the design (`:24-28`): "Entries are keyed by
  METHOD NAME, not by line number. ClickGateRegistry is line-keyed, which is why any page edit invalidates every entry
  below it".
Other pins: 166 `BindingFlags.NonPublic` uses in 86 test files (private member names), and the regex and exact-text
matches of 4.8. Each protects a page-level rule and breaks on a rename.
Direction: re-key ClickGateRegistry by control identity, as ProgressRegistry already is; then undo the one-liners.

### 4.10 PowerShell and ops (sound)

All 37 `-ErrorAction SilentlyContinue` uses opened: existence probes (`Get-PSDrive`, `Get-Website`, `Get-Member`),
cleanup of temp or staging paths, and optional globbing. None hides a result the script then relies on, with one
soft case: `tools/promote-dev-to-prod.ps1:82` sums file sizes for the free-space check and skips unreadable files, so
the estimate can be low. Empty catches: 5, all in the read-only discovery tool
`tools/Discover-GroupMembershipDelegation.ps1` (`:67`, `:70`, `:82`, `:126`, `:141`); the sixth hit
(`tools/AppPoolControl.psm1:9`) is the old defect quoted in a help block. `$LASTEXITCODE` is checked 28 times; I did
not match each native call to its check.
Duplicated helpers across scripts: `Write-Step` 6 definitions, `Write-Warn` 5, `Write-Plan`/`Write-Ok`/`Write-Fail` 4,
`Write-AppPoolSharingReport` 4 (deploy.ps1:293, Install:745, Move-ConfigDbToShared:249, promote:218; the deploy and
promote copies differ in 29 lines), `Test-IsAdministrator` 3, `Stop-AppPoolChecked`/`Start-AppPoolChecked` 2 each with
different parameters, `Invoke-RobocopyChecked` 2, `Write-DeploymentManifest` 2 (24 of 40 lines differ). The installer
must stand alone (architectural invariant 1), so its copies are by design; the deploy/promote/move copies are not.
Unread parameters: input audit A41, A42. Comment sprawl: 1,375 comment lines in 8,749, 284 with finding IDs.
`tests/ps` is 12,497 lines for 8,749 lines of script; `DeployInvariants.Tests.ps1` alone is 3,705.
Direction: move the shared console and app-pool helpers into the modules the three non-installer scripts already import.

### 4.11 Documentation and process residue (yes, it is slop by volume)

| Item | Size |
|---|---|
| Tracked markdown | 894 files, 12.8 MB |
| `docs/` | 130 files, 4.1 MB |
| `docs/*-Plan.md` | 106 plans, 2.85 MB |
| - Implemented | 79 plans, 1.86 MB |
| - Approved / In progress | 13 / 3 plans, 475 KB |
| - Draft | 7 plans, 131 KB |
| - Rejected, still present | 1 (`ServiceHealthPublicStatus-Plan.md`, 122 KB) |
| - Status line not one of the agreed words | 3 (ClickGatingAudit, DefenderEndpointDevices, MigrationInterfaceRedesign) |
| `.agents/` | 767 files, 8.8 MB |
| `.agents/review/` | 726 files, 7.7 MB, of which `findings/` 610 files, 6.9 MB |
| `.agents/state.md` | 936 lines, 77 KB; 204 lines over 1,000 characters |
| `.agents/decisions.md` | 3,117 lines, 213 KB, 112 entries |
| `.agents/token-log.md` | 495 lines, 510 KB |
| `README.md` | 1,719 lines, 126 KB |

- The `Status:` line is meant to be one word a reader can filter on. The longest is 931 characters; five exceed 280.
  `docs/ClickGatingAudit-Plan.md:3` begins "Slice 0, slice 1, tier 1 and the shared harness are Implemented
  (2026-09-19). TIERS 2, 3 AND 4 ARE NOW APPROVED ...".
- Implemented plans are history by the repo's own rule (repo-guidance, Authority Order 9) but sit beside the 16 live
  ones with nothing but that line to tell them apart. The largest "plan" in the folder is implemented
  (`DefenderEndpointDevices-Plan.md`, 159 KB).
- `state.md` is "Current work and blockers only" (`:3`) and the guidance target for the always-loaded prefix is
  "under 10K tokens". At 77 KB it is roughly twice that on its own.
- Can a new reader find current truth? The entry points are right (AGENTS.md is 59 lines, repo-guidance 251). After
  them the reader meets 213 KB of decisions with supersessions inline and a state file of dense paragraphs. The two
  documents that define a module are stamped as last verified in June (4.7 items 3, 4).
- Guidance that restates guidance: the three protected-principal exemptions are written out in full in the Constitution
  and again in repo-guidance (Known Failure Class 3); build/test/format commands appear in repo-guidance twice
  (Commands, Verification) and a third, different form in the Constitution (4.7 item 6).
- Activity since 2026-10-10: 1,576 commits. By subject prefix: fix 605, review 290, docs 187, merge 163, record 83,
  plan 65, feat 33, test 27, decision 25, state 17. 678 are paperwork. 389 commits landed on 2026-10-22 alone.
  Thirty-three features against 605 fixes and 290 review commits is the shape of a loop auditing its own output.
Direction: move Implemented and Rejected plans to `docs/history/`; archive closed findings; cut state.md to its rule.

### 4.12 User-facing text (mostly good)

Operator messages are specific and say what happened and what to do
(`Components/Pages/AdminSettings.razor:842` "Refusing to save: this page's toggles are not the stored module enablement
state (the store could not be read), so saving them could switch modules off. Nothing was changed. Reload the p...").
No hedging stacks, no "An error occurred" strings found. Residue:
- Finding IDs in text that leaves the code: `Authorization/CircuitRevocationGate.cs:112` exception message "... so it
  is not initialized (aud-a29c)."; `Services/MailboxGuidIdentityRefusal.cs:70` log template "... as not found (aud-a2d's
  identity form, sweep item 13): {Error}".
- Help the 2026-11-09 sweep missed (limit: field help one sentence). Of 53 setting helps, 3 have two sentences:
  `Modules/ModuleCatalog.cs:1100` FillMissingEmployeeId ("... emails them the password. Anyone who can reset passwords
  here can trigger that save."), `:1129` DefaultThrottleLimit ("Default WinRM fan-out throttle limit. Valid range:
  1-256."), `:1331` NotifySecurityTeam ("... comma- or semicolon-separated. The administrators ... are notified as
  well; blank notifies the administrators only"). No page note over 350 characters and no multi-sentence `title` tip
  found by search (94 `title=` attributes).
- Mail footers carry a template placeholder: `Services/EmailService.cs:306`, `:388`, `:459`, `:529`
  "(c) {DateTime.Now.Year} Your organization. All rights reserved." EmailService holds about 10 inline HTML documents
  (20 `<!DOCTYPE`/`<html` hits in 1,183 lines).
- About 46 page sites put raw `ex.Message` in front of the operator (ConferenceRooms 9, MessageTrace 5, M365 5,
  Comms10k 5, GroupManagement 4). The token and vault clients throw status codes only
  (`Services/GraphTokenClient.cs:295`, `Services/DelineaService.cs:422`), so no secret leaks; wording is whatever the
  backend said.
- Ticket wording and placeholders: input audit B1.

### 4.13 Version and changelog noise (DEBT)

`Modules/ModuleCatalog.cs` is 1,937 lines, 1,070 of them comments. Each descriptor carries a `// x.y.z:` history above
`Version`. Migration's is 201 lines and 45 entries (`:314-514`), ConferenceRooms' 94 lines and 29 entries, Comms10k 56,
SelfServiceGroups 59. Eight lines exceed 300 characters; the longest is 432. The file has 535 commits.
The history is already unreliable: for 21 of 31 modules the newest entry is older than the shipped version. Examples:
`:514-515` last entry "1.27.4: help sweep ..." then `Version = "1.27.5",`; `:813-814` last entry "2.16.3: sweep-e item
40 ..." then `Version = "2.16.6",`; `:1712-1713` "1.1.8" then `Version = "1.1.10",`; ConferenceRooms `:1237` "2.20.2"
then `"2.20.3"`. Cause: `663df997` "chore(ticket-sanitize): versions - base 2.36.2; 22 module patch bumps" bumped
without entries. The entries repeat commit messages and cite the same finding IDs as 4.1.
Other things that grow per slice: `.agents/token-log.md` (about 1 KB per line), `state.md`, one file per finding in
`.agents/review/findings/`, and ClickGateRegistry comments.
Direction: delete the histories; git log and a CHANGELOG own that.

## 5. Spot-check of the input audit

Seven rows of `.agents/review/shared-and-unwired-audit-2026-11-10.md` re-read against the files. All hold.

| Row | Claim | Result |
|---|---|---|
| A1 | `TicketNumberInput.razor` unreferenced; `:8` binds the parameter, `:76` IsNullOrWhiteSpace, `:88` calls ServiceNow, `:115` Dispose without `@implements` | Holds. `git grep "<TicketNumberInput"` finds only the audit file |
| A9 | `BulkJobService.IsStalled` (:397) has no caller | Holds. The file is `Services/Jobs/BulkJobService.cs`; the row omits the folder |
| A30 | `Comms10kService._config` only declared (:15) and assigned (:25) | Holds |
| A35 | `RiskyUsersService._logger` only declared (:15) and assigned (:23) | Holds |
| A41 | `deploy.ps1:31` `-HybridEndpoint` never read | Holds; one occurrence in the file |
| A45 | `@page "/message-trace"` (`MessageTrace.razor:2`) has no inbound link | Holds |
| Live bug 5 | `MfaReset.razor:53` `placeholder="user@analog.com"` | Holds |

For unwired code and cross-module copies, that audit is the reference; this one adds only 4.5.

## 6. Not checked

- Nothing was built, run or rendered. Every behavioural statement is from reading.
- Of 947 catch sites, about 45 were opened; the rest were classified by the awk pass only. The 750 `catch (Exception`
  sites were not individually judged for "logs and continues".
- The method-length and source-scan figures are awk heuristics (indentation based); nested classes and
  expression-bodied members are under-counted. The 781 figure could be off by tens either way.
- Tests: 8 of 433 files sampled for vacuity. Whether individual substitute-based tests assert only their own setup
  was not examined beyond the `.Received` count.
- Generic helpers with one caller, flags nobody sets, and DTO field-by-field duplication (category 3 and 4 detail).
- `$LASTEXITCODE` coverage per native call; parameter use in PowerShell beyond the input audit's AST pass.
- Help text inside components other than `title=` attributes and catalog setting help; permission descriptions.
- Docs: plans were classified by their `Status:` line only. README sections were not compared with `docs/` for
  duplicated prose. `docs/history/` and the 610 finding files were counted, not read.
- CSS, JS, `web.config`, `appsettings.json.sample` content.
- P1-P4 need a live directory or Exchange to confirm.
