# Production Readiness Review — 2026-06-12

Status: Reference (findings register)
Produced by: multi-agent review (13 dimensions, 94 agents); every finding of severity
medium or higher was independently re-verified by an adversarial agent that re-read the
cited code. 6 findings were refuted at that stage and are listed at the end. One further
finding was refuted after the fact by the owner + direct code read (see Corrections).
Verified against: commit 0021502, working tree clean.
Remediation plan: docs/ProdReadiness-Plan.md

## Corrections (post-review)

- **REFUTED after publication:** "Orphaned CredentialManagerService bypasses the
  Delinea-only credential rule (dead code) — delete it." Wrong: `Services/DelineaService.cs:36`
  calls `CredentialManagerService.ReadCredential(_credentialTarget)` (default target
  `Delinea_Client`) to bootstrap the Secret Server connection, exactly as documented in
  `docs/AdminModuleSpec.md:135`. The class is live, load-bearing infrastructure. Do NOT
  delete it. (The separate finding that its bare `catch { return (null, null); }` hides
  the cause of bootstrap failures still stands.)

## Owner decisions affecting findings

- On-prem Exchange (hybrid) is being decommissioned this year (confirmed by Michael
  2026-06-12); all conference rooms are cloud mailboxes. The ConferenceRooms
  `OnPremDelineaSecretId` dead-key finding is therefore resolved by removing/retiring
  the on-prem path rather than wiring the key (plan AC14).


## Confirmed — HIGH (21)

### [audit] ADAttributeEditor: terminating Set-ADUser failures are never audited

`Services/ADAttributeEditorService.cs:595`

PerformSave only audits failure when ps.HadErrors is true after Invoke(). But the Set-ADUser pipeline is built with -ErrorAction Stop, so in the PowerShell SDK most real failures (access denied, constraint violation, DC unreachable, object deleted mid-flight) surface as a thrown exception from ps.Invoke(), not as HadErrors. There is no try/catch in PerformSave or in SaveAsync (lines 397-404 contain only try/finally for throttle release), so the exception propagates to the page, where the catch in ADAttributeEditor.razor (lines 482-487) sets operationResult and sends an email but writes NO audit event. Net effect: the most common real-world failure mode of an AD attribute write leaves zero audit trail, violating the Constitution rule that every failed user-facing mutation writes an audit event.

**Suggested fix:** Wrap the PerformSave body (or the Task.Run call in SaveAsync) in try/catch, call LogAudit(target, changes, performedBy, ip, ticket, false, ex.Message) on exception, then return a failed AttributeSaveResult instead of letting the exception escape unaudited.

### [audit] Migration ClearCompletedBatches audits only successful removals; failed removals and auth denials are unaudited, and an audit failure flips the result to failed

`Components/Pages/Migration.razor:1254`

In ClearCompletedBatches, the loop over batches calls Audit.LogMigrationAction only inside the result.Success branch (line 1245, hardcoded success=true). The else branch records the failure in the local errors list but never writes an audit event, so failed batch removals (a failed user-facing mutation) have no audit trail. Additionally: (a) the authorization re-check at line 1220 silently returns with no denial audit, unlike GroupManagement/NamedLocations/DHCP which audit auth denials; (b) when a batch IS removed but the audit write throws, the catch at line 1249 adds it to errors, so errors.Count != 0 and the overall batchActionResult reports failure for an operation whose mutations all completed — brushing against the Constitution rule that an audit failure must not make a completed operation look failed.

**Suggested fix:** Add Audit.LogMigrationAction(currentUser, clientIpAddress, "RemoveMigrationBatch", batch.BatchName, false, ticket, result.Message) in the else branch; audit the authorization denial like other modules; and report audit-write failures as a warning detail rather than counting them in the failure aggregate.

### [auth] MailboxPermissions cloud and bulk mutations skip pre-write authorization re-check

`Components/Pages/MailboxPermissions.razor:392`

The Constitution requires 'Every mutating operation must re-check authorization immediately before the write.' MailboxPermissions only checks the 'MailboxPermissions' policy in OnInitializedAsync (line 248). The on-prem path re-checks 'MailboxPermissionsOnPrem' (lines 372, 515), but the cloud single-grant path (SubmitSingle, line 288) calls AddMailboxPermissionsAsync/RemoveMailboxPermissionsAsync with no re-check, and ProcessBulk (line 442) calls ProcessMailboxPermissionsCsvAsync with no re-check. On a long-lived Blazor Server circuit, a user whose section-access group is removed, or whose module is disabled by an admin, can keep granting FullAccess/SendAs until they reload the page. Other modules (ConferenceRooms ReauthorizeAsync, TestAccountPool RecheckAuthorization, Migration, Comms10k, etc.) all implement the pre-write pattern — this is a cross-session/cross-model inconsistency on the highest-privilege Exchange module.

**Suggested fix:** Add a ReauthorizeAsync helper (matching ConferenceRooms.razor line 1019) that checks the 'MailboxPermissions' policy, and call it at the top of SubmitSingle and ProcessBulk before any service mutation.

### [auth] CalendarPermissions cloud and bulk mutations skip pre-write authorization re-check

`Components/Pages/CalendarPermissions.razor:383`

Same gap as MailboxPermissions, evidently copied from the same template: the page checks 'CalendarPermissions' only at OnInitializedAsync (line 244). The on-prem path re-checks 'CalendarPermissionsOnPrem' (lines 359, 516) before writing, but the cloud path in SubmitSingle (line 283) and the bulk CSV path in ProcessBulk (line 437) mutate calendar permissions without re-checking authorization immediately before the write, violating the Constitution's pre-write re-check rule. A user removed from the section group mid-session can keep setting/removing calendar permissions over the existing circuit.

**Suggested fix:** Re-check the 'CalendarPermissions' policy at the start of SubmitSingle and ProcessBulk, mirroring the on-prem branch and the ReauthorizeAsync pattern used by newer modules.

### [auth] AdminEventLog ExecuteUndo never re-checks UndoAuditedActions before performing the undo write

`Components/Pages/AdminEventLog.razor:789`

Undo is a privileged directory mutation (it reverses AD attribute writes via handler.ExecuteUndoAsync). The 'UndoAuditedActions' fail-closed granular permission is evaluated once at render (line 390) into the hasUndoPermission field, which only hides UI elements (lines 124, 175, 562). ExecuteUndo itself performs no AuthorizeAsync against 'UndoAuditedActions' — it only re-derives ADAttributeEditorLevelN for the ADAttributeEditor handler (line 817), and even that check returns maxLevel=0 rather than aborting. The Constitution states 'UI hiding is not security. Direct URL access and direct event invocation must still be denied' and requires a pre-write re-check. A user whose undo permission is revoked mid-session (or whose EventLog access is removed) can still execute undos on the live circuit; contrast with ADAttributeEditor.ConfirmSave (line 410) which correctly re-checks before saving.

**Suggested fix:** At the top of ExecuteUndo, re-check both 'EventLog' and 'UndoAuditedActions' policies and abort with an error message on failure, before calling UndoRegistry/ExecuteUndoAsync.

### [auth] Legacy mutating Exchange modules are not FailClosed and silently fall back to global AllowedGroups when section access is lost

`Modules/ModuleCatalog.cs:130`

SectionAccessService.GetGroupsForSection (lines 58-65) returns the global Security:AllowedGroups for any section not in the fail-closed set when no section-access source exists (fragment file absent AND legacy appsettings absent). Every newer module is marked FailClosed:true, but the original Exchange modules are not: MailboxPermissions main (line 130), CalendarPermissions main (line 150), Migration main + MigrationCreate + MigrationManage (lines 169-170), DelegationReport (194), RecipientLookup (227), OutOfOffice (245). So if config/sectionaccess.json is ever lost, every user in the base AllowedGroups gains FullAccess/SendAs granting, migration batch creation, and migration management. This is not hypothetical: commit 0021502 ('Fix robocopy /XD so deploys stop purging runtime config') shows deploys have purged the config directory before. The inconsistency is stark — read-only MessageTrace is FailClosed:true (line 209) while write-capable MailboxPermissions is not — a clear cross-session/cross-model drift between the legacy modules and the FailClosed convention all later modules adopted.

**Suggested fix:** Mark all mutating permissions FailClosed:true (at minimum MailboxPermissions, CalendarPermissions, Migration main, MigrationCreate, MigrationManage, OutOfOffice). If the AllowedGroups fallback must remain for upgrade compatibility, restrict it to genuinely read-only modules and document the exception in AdminModuleSpec.md.

### [blazor] Audit IP attribution via static username-keyed cache with 1-hour TTL returns Unknown or another session's IP

`Services/ClientInfoService.cs:33`

In Blazor Server, the circuit's scoped ClientInfoService is a different instance from the one ClientInfoMiddleware populates during the HTTP request, so the scoped IpAddress property is always 'Unknown' inside a circuit; every page actually depends on the static fallback cache keyed by username. Two failure modes: (1) entries expire after 1 hour and SignalR WebSocket circuits send no further HTTP requests, so pages that look up the IP at mutation time (AdminSettings.razor:334, ModuleConfig.razor:540/586/647/693/840, ExchangeOnlineConfig.razor:216/301) write audit records with IP 'Unknown' for any admin whose tab has been open more than an hour — normal use; (2) the cache is last-write-wins per username, so if the same account is signed in from two locations, audit records for session A carry session B's IP, which is forensically misleading. The Constitution's AUDIT invariant requires actor IP on every mutation. There is also a cross-session inconsistency: some pages capture IP once in OnInitializedAsync (ADAttributeEditor.razor:262-264) while others read it per action, so the two bugs bite different pages differently.

**Suggested fix:** Capture the client IP per circuit, not per username: implement a CircuitHandler that reads IHttpContextAccessor (valid during circuit establishment) or pass the IP from the initial request into circuit-scoped state once, with no TTL. At minimum, capture IP once at OnInitializedAsync everywhere (while the connect-request cache entry is fresh) instead of at mutation time, and remove the 1-hour expiry for the audit path.

### [catalog] ConferenceRooms on-prem secret field is never read; credential path reads a different key

`Modules/ModuleCatalog.cs:332`

The Conference Rooms catalog defines the on-prem Exchange secret under key 'OnPremDelineaSecretId', and that is the only field the Module Config UI renders. But the only credential retrieval path is ConferenceRoomService.SetRemoteMailboxAsync -> ExchangeServiceBase.GetModuleCredentialsAsync -> ModuleCredentialService.GetCredentialsAsync, which hardcodes key 'DelineaSecretId' (ModuleCredentialService.cs:27: `var secretIdValue = _moduleConfig.GetValue(moduleId, "DelineaSecretId");`). ModuleConfigService.GetValue is a plain dictionary lookup with no aliasing, and a repo-wide grep shows 'OnPremDelineaSecretId' is read nowhere. Mechanism: GetValue returns null -> int.TryParse fails -> GetCredentialsAsync returns null -> ConferenceRoomService.cs:1064-1071 records a failed step 'On-prem credentials unavailable from Delinea'. So an admin who fills in the documented field gets a working-looking save, but every Set-RemoteMailbox (including the required=true case where the cloud write was rejected for on-prem-mastered rooms) fails. The field works only if someone hand-edits the module config JSON to add an undocumented 'DelineaSecretId' key.

**Suggested fix:** Either rename the catalog field to 'DelineaSecretId' (with a one-time config-key migration for any existing 'OnPremDelineaSecretId' values), or have ConferenceRoomService resolve the secret from 'OnPremDelineaSecretId' explicitly instead of using the generic ModuleCredentialService path. Add a test asserting the key the service reads exists in the module's ConfigFields.

### [ci-release] CI test step runs zero tests and passes green (no solution file; dotnet resolves only the web csproj)

`.github/workflows/ci.yml:19`

There is no .sln/.slnx in the repo (verified: `git ls-files` shows none). Both CI commands run from the repo root, where the only project file is ExchangeAdminWeb.csproj — the web app, not the test project. `dotnet build -c Release` therefore builds only the app and never compiles ExchangeAdminWeb.Tests (the root csproj even excludes it via `<DefaultItemExcludes>$(DefaultItemExcludes);ExchangeAdminWeb.Tests\**</DefaultItemExcludes>`, ExchangeAdminWeb.csproj line 7). `dotnet test -c Release --no-build` then resolves to the same non-test web project; empirically verified on .NET SDK 10.0.300 with an identical nested layout: the command produces zero output and exits 0 without running any tests (dotnet test does not recurse into subdirectories; it silently skips non-test projects). Net effect: the entire xUnit suite (25+ test files including SectionAccessServiceTests, ProtectedPrincipalServiceTests, PermissionValidatorTests) is never built or executed, test-project compile breaks are invisible, the TestResults directory is never created (upload-artifact@v4 defaults to if-no-files-found: warn, so even that step stays green), and the very first run of this never-observed workflow will report a fully green release gate having tested nothing. This is the success-aggregation failure class applied to CI itself.

**Suggested fix:** Either add a solution file and use `dotnet build ExchangeAdminWeb.slnx -c Release` / `dotnet test ExchangeAdminWeb.slnx -c Release --no-build`, or target the projects explicitly: `dotnet build ExchangeAdminWeb.csproj -c Release && dotnet build ExchangeAdminWeb.Tests/ExchangeAdminWeb.Tests.csproj -c Release` then `dotnet test ExchangeAdminWeb.Tests/ExchangeAdminWeb.Tests.csproj -c Release --no-build ...`. Also set `if-no-files-found: error` on the upload-artifact step so a missing TestResults directory can never pass silently. After fixing, verify the first run shows the actual test count.

### [ci-release] deploy-pipeline.ps1 reports 'Dev deployment complete' when deploy.ps1 fails with exit 1

`tools/deploy-pipeline.ps1:80`

deploy-pipeline.ps1 invokes deploy.ps1 with the call operator and then applies the robocopy failure convention (`-ge 8`) to a PowerShell child script. deploy.ps1's failure mechanism is `exit 1` (its Write-Fail at deploy.ps1 line 39 is `Write-Host ...; exit 1`, not a throw). `exit 1` in a child .ps1 invoked with `&` ends only the child and sets the caller's $LASTEXITCODE to 1 — it is not a terminating error, so $ErrorActionPreference='Stop' does not catch it. Since 1 < 8, the check never fires and the pipeline falls through to `Write-Ok "Dev deployment complete at $DevPath"` and instructs the operator to promote to prod. Concrete trigger paths: `dotnet publish` failure (deploy.ps1 line 361 `Write-Fail "dotnet publish failed (exit $LASTEXITCODE)"`), missing ServiceAccount/password in -NonInteractive mode (lines 212/222 — and deploy-pipeline always passes -NonInteractive), and install validation errors (line 177). This is the Known Failure Class 2 pattern (blanket success despite failure), and deploy.ps1's exit-based Write-Fail also violates AGENTS.md Architectural Invariant 5 ('failures go through Write-Fail (throw)'). The -Prod branch (line 159) has the same dead `-ge 8` check, but promote-dev-to-prod.ps1 fails via throw, which propagates correctly, so only the -Dev path actually masks failures.

**Suggested fix:** Change the checks to `if ($LASTEXITCODE -ne 0)` in both the -Dev (line 80) and -Prod (line 159) branches (the robocopy 0-7 convention belongs inside the scripts that call robocopy, not to script-to-script invocation). Better: change deploy.ps1's Write-Fail to `throw $m` per Invariant 5 so failures propagate as terminating errors regardless of the caller's exit-code handling.

### [config] ConferenceRooms on-prem credential key OnPremDelineaSecretId is never read by any code

`Modules/ModuleCatalog.cs:332`

The ConferenceRooms admin config page exposes the field key 'OnPremDelineaSecretId' (and config/module-config-ConferenceRooms.example.json line 2 documents it as the optional on-prem secret key), but the only code path that fetches on-prem credentials for this module is ExchangeServiceBase.GetModuleCredentialsAsync -> ModuleCredentialService.GetCredentialsAsync, which reads the literal key 'DelineaSecretId' (ModuleCredentialService.cs:27). A repo-wide grep for OnPremDelineaSecretId matches only the catalog entry and the example file — nothing reads it. Every other module's catalog uses the key 'DelineaSecretId' (e.g. ModuleCatalog.cs:133,153,176). Consequence: an admin who fills in the on-prem secret via the UI or the documented schema gets a config value that is dead; ConferenceRoomService.SetRemoteMailboxAsync (ConferenceRoomService.cs:1063-1071) always reports 'On-prem credentials unavailable from Delinea', and for on-prem-mastered rooms (required=true path) the room-type operation fails even though the admin configured everything the UI asked for.

**Suggested fix:** Rename the ConferenceRooms ConfigField key to 'DelineaSecretId' (matching every other module and ModuleCredentialService), or add an optional perKey parameter to GetCredentialsAsync and pass 'OnPremDelineaSecretId' from ExchangeServiceBase for this module. Update the example-file comment to the chosen key and add a test that the catalog ConfigField key matches the key ModuleCredentialService reads.

### [config] ProtectedPrincipalService silently loses exclusions when MailboxPermissions module config is corrupt (fail-open)

`Services/ProtectedPrincipalService.cs:346`

GetLegacyExclusions feeds protected-principal matching (line 112-113: if (legacyExclusions.Length > 0) CheckLegacyExclusions(...)). It reads ModuleConfigService.GetValue("MailboxPermissions", "ExcludedUsers") with no IsModuleCorrupt check. ModuleConfigService.ReadModuleConfig swallows parse failures via catch (Exception ex) and returns an empty dictionary (ModuleConfigService.cs:137-141), so a corrupt module-config-MailboxPermissions.json makes GetValue return null, and GetLegacyExclusions falls through to the legacy appsettings Security:ExcludedUsers (typically absent after migration) and returns an empty array. Result: users who should be protected from privileged mutations silently lose protection — the opposite of the Constitution's 'protected principal checks fail closed when config data unavailable' and a violation of 'legacy appsettings fallback only when module config is absent (not when corrupt)'. PermissionValidator guards the exact same data correctly (PermissionValidator.cs:251 blocks all operations when HasModuleConfigFile && IsModuleCorrupt), proving this is a cross-session inconsistency between the two services.

**Suggested fix:** In LoadEffectiveConfig/GetLegacyExclusions, check _moduleConfig.IsModuleCorrupt("MailboxPermissions") first and return the corrupt-config error tuple (same pattern as lines 149/163), mirroring PermissionValidator's fail-closed handling. Add a test: corrupt MailboxPermissions config file -> CheckTargetAsync returns Failed, not NotProtected.

### [consistency] Graph HTTP failure collapses to null; MFA reset reports blanket success

`Services/MfaResetService.cs:79`

GraphTokenClient.GetAsync swallows every non-success HTTP status by returning null (no throw, no status surfaced). Callers handle that null divergently: MfaResetService.GetUserMethodsAsync treats null as an empty method list, and ResetAllMethodsAsync then maps an empty list to Success=true. Mechanism: HTTP failure -> null JsonDocument -> empty List<AuthMethod> -> early-return success. A Graph 403 (missing UserAuthenticationMethod.ReadWrite.All), throttling 429, transient 5xx, or a typo'd/nonexistent UPN (404) makes the operator-facing result 'Success: No MFA methods to remove' when nothing was checked or removed. This is the repo's known failure class 2 (blanket success). NamedLocationsService.GetAllAsync has the same null-collapse on the first page (silently returns an empty location list), while M365GroupManagementService at least logs a warning — three different treatments of the same client null.

**Suggested fix:** Make GraphTokenClient.GetAsync either throw on non-success or return a result carrying the status code; in MfaResetService distinguish 'user has no methods' (HTTP 200, empty value array) from 'request failed' and return Success=false with the status. In NamedLocationsService throw or surface an error when the FIRST page fails instead of returning an empty list.

### [creds] ConferenceRooms on-prem credential config key never read by credential lookup

`Services/ModuleCredentialService.cs:27`

The ConferenceRooms catalog entry exposes the on-prem Exchange credential as config key 'OnPremDelineaSecretId' (the only key the Module Config UI will save for this purpose), but the runtime credential path reads a different key. ConferenceRoomService passes moduleId 'ConferenceRooms' to ExchangeServiceBase, and SetRemoteMailboxAsync (Services/ConferenceRoomService.cs:1063, 'var creds = await GetModuleCredentialsAsync("Set-RemoteMailbox");') routes through ModuleCredentialService.GetCredentialsAsync, which does a plain dictionary lookup of key 'DelineaSecretId' (ModuleConfigService.GetValue is 'config.TryGetValue(key, out var value) ? value : null' with no aliasing). int.TryParse on null fails, the method logs 'DelineaSecretId is not configured' and returns null, and the room operation step fails with 'On-prem credentials unavailable from Delinea'. Result: configuring the documented On-Prem Exchange Delinea Secret ID field has no effect, and on-prem-mastered rooms (where the cloud CustomAttribute9/MailTip write is rejected and the on-prem write is required) always fail. This is a cross-session catalog/service key drift: TestAccountPool correctly pairs its catalog key 'OnPremExchangeDelineaSecretId' with GetConfig("OnPremExchangeDelineaSecretId") (TestAccountPoolService.cs:900), but ConferenceRooms does not.

**Suggested fix:** Either rename the ConferenceRooms catalog config field to 'DelineaSecretId' (matching every other on-prem module) or make ConferenceRoomService fetch via a key-specific path (e.g. an overload GetCredentialsAsync(moduleId, key, purpose) reading 'OnPremDelineaSecretId'). Add a test that every catalog-declared *DelineaSecretId key is actually read by its module's service.

### [failure-classes] Failed AD attribute saves write no audit event (failure-audit branch is dead code)

`Services/ADAttributeEditorService.cs:601`

PerformSave issues Set-ADUser with -ErrorAction Stop, which makes every cmdlet error terminating, so ps.Invoke() at line 601 throws CmdletInvocationException. There is no try/catch in PerformSave, so the 'if (ps.HadErrors)' branch at line 604 — the ONLY place a failed write is audited via LogAudit(..., false, errMsg) — is unreachable for any real Set-ADUser failure. The exception propagates through SaveAsync's Task.Run to the page, whose catch (Components/Pages/ADAttributeEditor.razor:482-487) only sets operationResult and sends email — it never writes an audit event. The earlier failure returns in PerformSave (re-read empty at 564, GUID mismatch at 569, search-base move at 573) also return without auditing. Net effect: a failed user-facing mutation produces NO audit record, violating the Constitution's 'every successful or failed user-facing mutation writes an audit event'. The comment in ADAttributeEditorUndoService.cs:227 ('The SaveAsync already audits failure internally') is a stale claim that this dead branch works.

**Suggested fix:** Wrap the Set-ADUser invoke in try/catch inside PerformSave; in the catch, call LogAudit(target, changes, ..., false, ex.Message) before rethrowing or returning a failed AttributeSaveResult. Also audit the re-read-empty / GUID-mismatch / search-base failure returns. Fix the stale comment in ADAttributeEditorUndoService.cs:227.

### [failure-classes] SetRoomTypeAsync 'Remove existing permissions' swallows per-permission failures and always records Success

`Services/ConferenceRoomService.cs:545`

Step 3 of SetRoomTypeAsync (mandatory for CEO rooms: 'CEO type always removes all existing calendar permissions') runs Get-MailboxFolderPermission and each Remove-MailboxFolderPermission with -ErrorAction SilentlyContinue via InvokeOptional. SilentlyContinue suppresses the non-terminating error, and InvokeOptional (ExchangeServiceBase.cs:268-276) clears ps.Streams.Error without inspecting it, so a failed removal — or a failed enumeration that returns zero permissions to remove — leaves no trace. The step is then unconditionally recorded as Success=true, so the final failedSteps aggregation (lines 667-674) cannot catch it and the operation reports overall success. Security impact: converting a room to CEO can report 'Configured as CEO' while previous Editor permission holders silently retain access to the CEO calendar.

**Suggested fix:** Use InvokeBestEffort (which returns captured errors) instead of InvokeOptional for the enumeration and each removal; record the step as Success=false with the collected errors when any removal fails or the permission read errors, so the failedSteps aggregation marks the operation partially failed.

### [ps-scripts] deploy-pipeline treats deploy.ps1 exit code 1 as success (robocopy threshold misapplied to a PS script)

`tools/deploy-pipeline.ps1:80`

deploy-pipeline.ps1 invokes deploy.ps1 via call operator and then checks `$LASTEXITCODE -ge 8` — robocopy semantics applied to a PowerShell script. deploy.ps1 signals every non-throw failure via `exit 1` (its Write-Fail), e.g. dotnet publish failure (deploy.ps1:361), fresh-install validation errors (deploy.ps1:177), missing ServiceAccount in NonInteractive mode (deploy.ps1:212). `exit 1` from a child script sets $LASTEXITCODE=1 in the caller, which passes the `-ge 8` test, so the pipeline prints 'OK Dev deployment complete at $DevPath' after a failed deploy. This is the repo's known failure class 2 (blanket success despite failure). The same wrong threshold appears at line 159 after promote-dev-to-prod.ps1 (currently dead code because promote uses `throw`, which propagates under $ErrorActionPreference=Stop, but it will mask failures if promote ever exits non-zero). Note also a successful deploy.ps1 leaves $LASTEXITCODE at the last native call's code (robocopy 1-3), so the check distinguishes nothing meaningful.

**Suggested fix:** Check `$LASTEXITCODE -ne 0` after invoking deploy.ps1 (and promote), or better: make deploy.ps1's Write-Fail throw so failures propagate naturally under $ErrorActionPreference=Stop, and drop the exit-code check for script invocations entirely.

### [ps-scripts] No -PlanOnly/dry-run path through deploy.ps1, and deploy-pipeline -Prod hardcodes Apply, foreclosing promote's dry-run

`tools/deploy-pipeline.ps1:149`

AGENTS.md Architectural Invariant 4: 'Every ops-script step must support -PlanOnly (via Invoke-PlanOrAction / Write-Plan)', and ProjectConstitution.md line 89: 'Deployment scripts must support dry-run behavior for destructive promotion steps' (line 206: 'Do not change production deployment behavior without dry-run support'). deploy.ps1 has no PlanOnly parameter at all — it mutates app pool identity/password, ACLs, IIS auth, robocopy-mirrors the publish folder, and rewrites appsettings.json with no preview mode. deploy-pipeline.ps1, the documented promotion workflow ('run -Dev first ... then run -Prod'), hardcodes `Apply = $true` and `IUnderstandThisOverwritesProd = $true`, so an operator following the documented path can never see promote-dev-to-prod.ps1's dry-run output before prod is overwritten; the explicit consent switch is also auto-asserted, defeating its purpose. promote-dev-to-prod.ps1 itself implements dry-run correctly (default is DRY RUN; every mutating helper checks $Apply), but only when invoked directly.

**Suggested fix:** Add a -PlanOnly switch to deploy-pipeline.ps1 that omits Apply/IUnderstandThisOverwritesProd when promoting (letting promote's native dry-run run), and retrofit deploy.ps1 with the Invoke-PlanOrAction pattern already proven in tools/Install-ExchangeAdminWeb.ps1.

### [test-gaps] dotnet test and CI never execute the test suite (no solution file)

`.github/workflows/ci.yml:19`

There is no .sln/.slnx anywhere in the repo (verified via git ls-files and root listing). `dotnet build`/`dotnet test` with no path argument resolve to the single project file in the current directory, which at repo root is ExchangeAdminWeb.csproj — the app, not the test project. The app csproj explicitly excludes the tests from compilation and does not reference ExchangeAdminWeb.Tests.csproj, so CI's Build step never compiles the test project, and the Test step (`--no-build`) runs vstest against the app assembly, which contains zero tests — it either errors out or reports nothing ran; in no case do the 25 test files execute. The same applies to the documented local command `dotnet test` (AGENTS.md line 62) when run from repo root. Despite commit 00054a8 'Wire up CI workflow so it actually runs' and .agents/state.md noting CI is 'not yet observed running', the entire xUnit suite is unenforced — every other coverage finding is moot until this is fixed.

**Suggested fix:** Add a solution file (`dotnet new sln && dotnet sln add ExchangeAdminWeb.csproj ExchangeAdminWeb.Tests/ExchangeAdminWeb.Tests.csproj`) or change CI and AGENTS.md to `dotnet test ExchangeAdminWeb.Tests/ExchangeAdminWeb.Tests.csproj -c Release`. Then verify the first CI run shows a nonzero test count, and consider `--fail-on-no-tests` style guards.

### [test-gaps] tests/ps/ does not exist — zero Pester coverage for 2,439 lines of ops PowerShell

`AGENTS.md:107`

The repo rule mandates Pester coverage in tests/ps/ for .ps1 logic, but the directory does not exist (verified: `find . -name '*.Tests.ps1'` returns nothing; `ls tests` → no such dir). The uncovered scripts are exactly the highest-blast-radius ops code: deploy.ps1 (643 lines), tools/Install-ExchangeAdminWeb.ps1 (675), tools/promote-dev-to-prod.ps1 (542), tools/validate-module-package.ps1 (341), tools/deploy-pipeline.ps1 (167), tools/test-delinea.ps1 (71). The most recent commit (0021502, 'Fix robocopy /XD so deploys stop purging runtime config') fixed a deploy bug that destroyed runtime config — a Constitution invariant — and shipped with no regression test, so the exact incident class can recur silently. The CI Pester step deliberately no-ops when the directory is missing, so absence is never flagged.

**Suggested fix:** Create tests/ps/ with Pester tests for the invariants that already bit: robocopy exclusion list contains appsettings*.json/config/logs, $LASTEXITCODE>=8 handling, -PlanOnly produces no side effects, and Install-ExchangeAdminWeb.ps1 contains no ADI-specific strings. Make the CI Pester step fail (not no-op) once the directory exists.

### [test-gaps] GroupAuthorizationHandler — the sole authorization gate — has zero tests

`Authorization/GroupAuthorizationHandler.cs:48`

Every privileged page's policy funnels through GroupAuthorizationHandler.HandleRequirementAsync, which implements the Constitution's fail-closed rules: deny on empty group config, deny when a module is disabled, DOMAIN\group normalization, and case-insensitive role matching. No test file references it (grep across ExchangeAdminWeb.Tests/*.cs finds nothing). This is pure, dependency-injectable logic (SectionAccessService, ModuleCatalog, ModuleEnablementService are all constructible or substitutable — SectionAccessServiceTests already builds the real SectionAccessService against temp files), so the gap is not a tooling limitation. A regression here (e.g. someone inverting the empty-groups branch, or the `Split('\\')[1]` normalization matching the wrong group) is an authorization bypass with no failing test.

**Suggested fix:** Add GroupAuthorizationHandlerTests covering: empty groups → Fail; dynamic resolution with disabled module → Fail; DOMAIN\group claim normalization; case-insensitive match → Succeed; non-member → Fail. Use ClaimsPrincipal with role claims and the real SectionAccessService over a temp config dir, as SectionAccessServiceTests already demonstrates.


## Confirmed — MEDIUM (53)

### [audit] EmergencyDisable, Comms10k, and LicensingUpdates write audit events under wrong categories via borrowed audit methods

`Services/EmergencyDisableService.cs:562`

Three modules borrow category-specific AuditService methods instead of the generic LogModuleAction, so their audit events are misfiled. EmergencyDisableService audits emergency account disables via LogMigrationAction, which hardcodes ["category"] = "MigrationAction" (AuditService.cs:146). Comms10k.razor does the same for 10k-group membership replacement (lines 311-374). LicensingUpdatesService audits AD attribute WRITES via LogLookupAction, which hardcodes ["category"] = "Lookup" (AuditService.cs:204) and stuffs status/old/new values into the target string. Consequence: in AdminEventLog, a security-critical EmergencyDisable appears as a migration action, and license mutations appear as read-only lookups; a compliance query for mutations by category misses them entirely. This is classic multi-session drift — newer modules reused whichever audit method an earlier model wrote instead of the generic one.

**Suggested fix:** Switch all three call sites to AuditService.LogModuleAction with categories "EmergencyDisable", "Comms10k", and "LicensingUpdates" (passing old/new values via the extra dictionary), and add those categories to the AdminEventLog filter dropdown. Document in the developer guide that LogMigrationAction/LogLookupAction are reserved for their namesake modules.

### [audit] EmergencyDisable early failure paths (credentials, pre-state read, snapshot persist) produce no audit event

`Services/EmergencyDisableService.cs:125`

The single _audit call in EmergencyDisableService is LogAudit at step 8 (line 259), reached only after the mutation steps execute. Every earlier failure return — AD creds unavailable, Graph creds unavailable, ReadAdEnabledState/ReadEntraEnabledState exception, PersistSnapshot exception — writes an operation trace step and opScope.Complete(false, msg) but never an audit event. A failed attempt to emergency-disable an account is exactly the kind of failed security-critical mutation the Constitution requires in the audit log, and trace events are a separate stream that the event log only shows when the operator toggles 'Show diagnostics'. Other modules (MfaReset.razor, ConferenceRooms.razor) audit pre-mutation failures such as ticket-validation and protection-check denials, so this is also drift.

**Suggested fix:** Call LogAudit (with success=false and the failing step as errorDetail) on each early-return failure path before returning, so every failed EmergencyDisable attempt lands in the audit stream, not just the trace stream.

### [audit] Settings/config mutations can only audit success: LogSettingsChange hardcodes result=Success and save failures are never audited

`Services/AuditService.cs:333`

AuditService.LogSettingsChange has no success/error parameters and hardcodes ["result"] = "Success". All AdminSettings-category mutations (section access, module enablement, module config, ExchangeOnline config, protected principals, allowlist) route through it, and every caller invokes it only after the save call returns; the surrounding catch blocks set a status message but write no audit event. A failed attempt to change section access groups or module config — a privileged mutation per the Constitution — therefore leaves no audit record, and the API makes it impossible to record one.

**Suggested fix:** Add success/errorDetail parameters to LogSettingsChange (defaulting to success for compatibility), and call it with success=false from the catch blocks in AdminSettings.razor, ModuleConfig.razor, and ExchangeOnlineConfig.razor save handlers.

### [audit] Audit IP fidelity: static per-username cache with 1-hour TTL records ip="Unknown" for long-lived circuits and the wrong IP for concurrent sessions

`Services/ClientInfoService.cs:33`

Pages resolve the audit IP once at circuit init via ClientInfo.IpAddress (usually "Unknown" in a Blazor circuit scope, since the middleware populated a different request-scoped instance) falling back to the static GetIpForUser cache. That cache entry expires after 1 hour and is only refreshed by a new HTTP request (page reload / SignalR reconnect). An operator who keeps a circuit open past 1 hour and then opens a module page gets clientIpAddress="Unknown" stamped into every audit event, defeating the Constitution requirement that audit entries carry the actor's IP. Separately, the cache is keyed by username only, so two concurrent sessions for the same account from different IPs overwrite each other and audits can attribute actions to the wrong IP.

**Suggested fix:** Capture the IP per circuit at circuit creation (e.g., a CircuitHandler reading HttpContext during the initial request, or IHttpContextAccessor at first render) and store it on the circuit-scoped service without a TTL, instead of a static username-keyed cache; alternatively refresh the cache timestamp from SignalR keep-alive requests.

### [audit] JsonlLogService serializes the event outside its try block — the one place an audit write can still throw into callers

`Services/JsonlLogService.cs:68`

WriteToFile is designed to swallow audit-write failures (catch at line 82 logs and returns), which is what protects the Constitution rule that an audit failure must not fail a completed operation. But JsonSerializer.Serialize runs at line 69, BEFORE the try block at line 72. If any caller ever passes a value System.Text.Json cannot serialize (e.g., a PSObject or other exotic object via the extra/oldValues/newValues dictionaries that several call sites accept), the exception escapes WriteToFile, propagates through AuditService.WriteAuditEvent into the mutating caller — and many call sites do not wrap their audit calls (see the idiom-drift finding). Today all observed call sites pass primitives, so this is latent, but it is the single structural hole in the otherwise fail-silent audit pipeline.

**Suggested fix:** Move the Serialize call inside the try block so serialization failures are swallowed and logged exactly like I/O failures, making AuditService.Log* genuinely non-throwing by contract.

### [audit] Two competing audit idioms: unwrapped audit calls inside operation try blocks can make a completed mutation look failed and write a false Failed audit

`Components/Pages/MfaReset.razor:292`

The codebase has two idioms: one set of modules wraps every Audit.Log* call in try/catch (GroupManagement.razor, NamedLocations.razor, M365GroupManagement.razor, ConferenceRooms.razor, OutOfOffice.razor, TestAccountPoolService.LogAudit, LicensingUpdatesService), while another calls audit bare inside the operation's try (MfaReset.razor, MailboxPermissions.razor, CalendarPermissions.razor, MailboxPermissionService bulk loop, ADAttributeEditorService.LogAudit, EmergencyDisableService.LogAudit). In MfaReset.ExecuteReset, the success-path audit at line 292 sits inside the same try as the mutation: if Audit.LogMfaResetAction throws (see the serialization hole in JsonlLogService), control jumps to the catch at line 300, which sets result = Success=false AND writes a second audit event recording the completed reset as Failed — directly violating 'audit failure must not make a completed operation look failed' and corrupting the audit record. In MailboxPermissionService.ProcessMailboxPermissionsCsvAsync, the per-row catch itself calls audit.LogMailboxPermission unwrapped (line 229), so one audit throw aborts the whole CSV run mid-loop.

**Suggested fix:** Standardize on one idiom: make AuditService.Log* contractually non-throwing (fix the JsonlLogService serialize placement and wrap WriteAuditEvent in a top-level catch), then remove the per-call-site wrapping requirement; or at minimum move success-path audit calls out of the mutation try block in MfaReset.razor, MailboxPermissions.razor, and CalendarPermissions.razor.

### [auth] BuildFailClosedSet swallows all exceptions, degrading fail-closed permissions to fail-open

`Services/SectionAccessService.cs:54`

BuildFailClosedSet constructs its own ModuleCatalog (instead of the DI singleton) inside a try block with an empty 'catch { }' (swallowed catch). If catalog construction ever throws here, the fail-closed set is silently empty, so GetGroupsForSection treats every FailClosed permission as eligible for the AllowedGroups fallback when no section-access source exists — the exact fail-open behavior the FailClosed flag exists to prevent. Today Program.cs line 36 ('var catalog = new ModuleCatalog();') would crash first, making this latent, but any refactor of catalog validation or construction order (or unit tests instantiating SectionAccessService alone) re-exposes it. Duplicating the catalog also risks divergence if the catalog ever becomes configurable.

**Suggested fix:** Inject the singleton ModuleCatalog into SectionAccessService and remove the try/catch entirely so a broken catalog fails startup loudly instead of silently disabling fail-closed enforcement.

### [auth] Latent global AllowedGroups base gate: FallbackPolicy = GroupPolicy plus unused AuthorizationCheck.razor

`Modules/ModuleCatalog.cs:59`

The Constitution says 'Do not reintroduce a global AllowedGroups base gate', and AdminModuleSpec.md line 56 says 'there is no separate base gate'. Yet ConfigureAuthorizationPolicies still builds a GroupPolicy from Security:AllowedGroups and installs it as the global FallbackPolicy. It is currently inert for pages because MapRazorComponents(...).RequireAuthorization() (Program.cs line 135) plus per-page [Authorize] attributes supply authorize metadata, so the fallback never evaluates — but any future endpoint mapped without metadata (health check, minimal API, download endpoint) silently becomes AllowedGroups-gated, resurrecting the base gate. Components/AuthorizationCheck.razor is a completely unused component (no references anywhere) that authorizes against 'GroupPolicy'; a future session could wrap a page in it believing it provides module gating when it only checks the deprecated base group.

**Suggested fix:** Delete AuthorizationCheck.razor, and replace the AllowedGroups FallbackPolicy with a RequireAuthenticatedUser-only fallback (or keep a deny-by-default fallback) so the deprecated base gate cannot silently re-attach to future endpoints. Record the decision in .agents/decisions.md.

### [auth] Section access is cached forever in-process while module enablement is re-read per call

`Services/SectionAccessService.cs:86`

ReadSectionAccess caches the parsed sectionaccess.json on first read and only invalidates on an in-process SaveSectionAccess (line 166). ModuleEnablementService, by contrast, re-reads modules-enabled.json on every IsModuleEnabled call. Operationally this means an emergency revocation done by editing config/sectionaccess.json directly on the server (the natural ops move for a file-based config, and what tools/promote-dev-to-prod.ps1 merges at line 474) has no effect until the app pool restarts — a removed group keeps its access indefinitely on a running instance. The promote script does restart the pool, but manual or scripted edits outside that path silently do nothing. The asymmetry between the two sibling config services looks like two different sessions' designs.

**Suggested fix:** Invalidate the cache on file change (FileSystemWatcher or last-write-time check per read, as enablement effectively does), or document loudly that section-access edits require an app restart.

### [blazor] Connect-ExchangeOnline runs synchronously on the circuit dispatcher inside BorrowAsync, freezing the user's UI

`Services/ExoConnectionPool.cs:120`

BorrowAsync is awaited from circuit event handlers (ExchangeServiceBase.cs:43 'var pooled = await _exoPool.BorrowAsync();' and RecipientAutocomplete.razor 'await ExoPool.BorrowAsync()'). When a pool slot is free, '_slots.WaitAsync' completes synchronously and execution stays on the renderer synchronization context; there is no ConfigureAwait(false) and no Task.Run around connection creation. CreateConnected then performs runspace.Open() plus two synchronous ps.Invoke() calls including Connect-ExchangeOnline (typically 5-15 seconds) directly on the circuit dispatcher thread, blocking ALL UI events and renders for that user's circuit for the duration. DestroyRunspace (a synchronous Disconnect-ExchangeOnline ps.Invoke) similarly runs inline on the dispatcher in the stale/expired branch of BorrowAsync (line 117) and in Return (line 134). This is per-circuit blocking, not a deadlock, but it makes pages appear hung whenever a cold connection is established.

**Suggested fix:** Wrap CreateConnected (and DestroyRunspace on the borrow/return paths) in await Task.Run(...), or add ConfigureAwait(false) throughout the pool so continuations leave the renderer context. The existing Task.Run in ExchangeServiceBase.RunAsync covers only the operation, not connection establishment.

### [blazor] Fire-and-forget Enter-key handlers never re-render: results invisible, spinner stuck

`Components/Pages/MfaReset.razor:165`

HandleKeyDown is a synchronous void handler that discards the task from ListMethods. Blazor only calls StateHasChanged automatically when the event handler's own task completes; a discarded task's completion triggers no render. So pressing Enter renders once with isLoading=true (input disabled, spinner shown via the button's disabled binding), then when ListMethods finishes and sets methods/result/isLoading=false, no re-render occurs — the page stays stuck showing the loading state until the user clicks something else. Exceptions on the discarded task are caught inside ListMethods, so no circuit crash, but the result is invisible. Identical pattern in GroupManagement.razor:194-197 ('if (e.Key == "Enter" ...) _ = Search();'). The button paths (@onclick="ListMethods") are unaffected because they return the Task to Blazor.

**Suggested fix:** Make the handlers 'private Task HandleKeyDown(KeyboardEventArgs e) => (e.Key == "Enter" && ...) ? ListMethods() : Task.CompletedTask;' so Blazor awaits the task and re-renders on completion, or add 'await InvokeAsync(StateHasChanged)' / call StateHasChanged in the finally block of ListMethods/Search.

### [blazor] Autocomplete keystrokes borrow the shared 5-slot EXO mutation pool with 2-minute waits

`Components/Shared/RecipientAutocomplete.razor:104`

Every debounced keystroke (3+ chars, 300 ms) in RecipientAutocomplete borrows a connection from the singleton ExoConnectionPool — the same 5-slot pool used by all mutation services app-wide (MailboxPermissions, CalendarPermissions, PermissionValidator's protected-principal expansion, etc.). ExoConnectionPool.cs:50 caps it at 'new SemaphoreSlim(5, 5)' and BorrowAsync waits up to 2 minutes (:94). A few users typing in autocomplete fields can hold all slots (each Get-Recipient round trip plus possible cold Connect-ExchangeOnline), starving actual admin operations across all users — user A's typing degrades user B's mutations. The component also re-implements pool borrow/return/discard and connection-error string matching inline in UI code instead of going through ExchangeServiceBase, a cross-session duplication that will drift.

**Suggested fix:** Give autocomplete a short borrow timeout (e.g. 2-5 s, returning empty results on timeout), or a dedicated single read-only connection/slot, and route the EXO call through a shared service method instead of inline pool management in a .razor file. Cancel the in-flight search when a newer debounce fires.

### [blazor] All users' AD autocomplete searches serialize through one global lock with fresh runspace + Import-Module per query

`Services/ADDirectorySearchService.cs:80`

ADDirectorySearchService is a singleton whose Search path takes a SemaphoreSlim(1,1) — one AD search at a time for the entire server — and then ExecuteSearch creates a brand-new runspace and runs Import-Module ActiveDirectory on every call (seconds of overhead per keystroke search). With several admins typing in ADIdentityAutocomplete/ADGroupAutocomplete simultaneously, searches queue behind each other; the synchronous '_runspaceLock.Wait(TimeSpan.FromSeconds(30))' blocks a ThreadPool thread (called via Task.Run from the components) for up to 30 seconds and then silently returns an empty list, which the UI cannot distinguish from 'no matches'. User A's typing directly degrades user B's experience.

**Suggested fix:** Replace the PowerShell runspace with System.DirectoryServices.Protocols or DirectorySearcher (cheap per-call, no module import, naturally concurrent), or pool a persistent runspace instead of rebuilding one per query; raise the semaphore to a small N and drop the wait to a few seconds so stale searches fail fast.

### [catalog] PermissionValidator reads 'ExcludedUsers' key the catalog never defines

`Services/PermissionValidator.cs:44`

PermissionValidator and ProtectedPrincipalService both read module-config key 'ExcludedUsers' from the MailboxPermissions module, but the MailboxPermissions descriptor (ModuleCatalog.cs:132-135) defines only 'DelineaSecretId' and 'PreventSelfGrant'. ModuleConfig.razor renders only catalog-declared ConfigFields, so this security-relevant setting (users excluded from permission grants / treated as protected) cannot be seen or set from the UI; it is reachable only via legacy appsettings 'Security:ExcludedUsers' (PermissionValidator.cs:50) or hand-editing the module config JSON. The Constitution requires module-specific settings to live in per-module config with appsettings only as upgrade fallback, yet here the module-config form of the setting is invisible — a classic cross-session inconsistency where one model added the module-config read and no one added the catalog field.

**Suggested fix:** Add an 'ExcludedUsers' ModuleConfigField (Required: false) to the MailboxPermissions descriptor so it is visible and editable in the config UI, or remove the module-config read and document appsettings as the single source.

### [catalog] Graph modules fall back to 'DelineaSecretId' for Graph app secrets, against the credential naming rule

`Services/MfaResetService.cs:22`

The Constitution mandates 'Use GraphDelineaSecretId for Graph app secrets' and 'Use DelineaSecretId for [AD/on-prem credentials]'. MfaResetService, NamedLocationsService, and M365GroupManagementService all fall back to reading key 'DelineaSecretId' for the Graph client when 'GraphDelineaSecretId' is unset — a key none of their catalog descriptors define (ModuleCatalog.cs:313-315, 362-364, 278-280 list only GraphDelineaSecretId). EmergencyDisableService (line 271) correctly reads only GraphDelineaSecretId even though that module legitimately has both keys. The fallback is harmless today only because these three modules have no AD secret; if a maintainer later adds an AD 'DelineaSecretId' field to any of them (as EmergencyDisable has), the AD secret ID would silently be sent to Delinea and used as the Graph app credential. This is a cross-model inconsistency: one pattern with fallback, one without.

**Suggested fix:** Delete the '?? GetValue(moduleId, "DelineaSecretId")' fallback in all three Graph services (matching EmergencyDisableService). If the fallback exists for upgrade compatibility from an old key name, replace it with a one-time config migration instead of a permanent runtime fallback.

### [catalog] Environment-specific (ADI/analog.com) strings hardcoded in catalog, UI, and room-type logic

`Modules/ModuleCatalog.cs:404`

Confirmed: ModuleCatalog.cs:404 still carries the analog.com UPN-suffix example in the Test Account Pool descriptor. Additional environment-specific values remain in shipped code (not tests): (1) Migration's HybridEndpoint DefaultValue 'hybrid1' (ModuleCatalog.cs:172) plus a silent code fallback to 'hybrid1' in MigrationService.cs:26 — a wrong-but-plausible endpoint name in any other environment; (2) MfaReset.razor:48 input placeholder 'user@analog.com'; (3) ModuleConfig.razor:170 instructs admins to use the 'ADI - Azure App Registration' Delinea template by name; (4) Conference Rooms bakes the ADI site code 'ADGT' into both the catalog schema (ADGTAdminsGroup/ADGTContactEmail, ModuleCatalog.cs:340,346) and hard dispatch logic in ConferenceRoomService.cs:1157 (`RoomType.Restricted when site == "ADGT"`). These contradict the install script's environment-neutral goal and the recent 'extract ADI defaults to config' direction.

**Suggested fix:** Replace analog.com examples with contoso-style placeholders, drop the 'hybrid1' DefaultValue and code fallback (fail closed or leave blank), genericize the Delinea template hint, and make the ADGT site-specific room behavior config-driven (e.g. a configured site code) rather than a hardcoded string.

### [ci-release] Fresh install prompts for and validates CloudTargetDomain/OnPremTargetDomain/HybridEndpoint, then silently discards them

`deploy.ps1:149`

On a fresh production install, deploy.ps1 requires the operator to supply CloudTargetDomain and OnPremTargetDomain (prompted at lines 145/149, hard-validated at lines 165/171 — install aborts via Write-Fail if they fail validation) and accepts a HybridEndpoint parameter (line 26). None of these three values appear anywhere in the generated appsettings.json (the config hashtable at lines 540-593 contains only ExchangeOnline, OnPremExchange.ServerUri, Delinea, Audit, OperationTrace, Email, Security, ServiceNow, Application, AllowedHosts). Worse, no code in the entire repo reads keys named CloudTargetDomain or OnPremTargetDomain at all (verified by repo-wide grep over .cs/.razor/.json — zero hits), and HybridEndpoint is read only as module config `Migration:HybridEndpoint` with a built-in default (Services/MigrationService.cs line 26). So the first production install (a) can fail validation on values the app never uses, and (b) silently produces a deployment whose Migration-related configuration differs from dev, with the operator believing they configured it. This looks like cross-session drift: one model added install prompts for config keys a later session removed from the app.

**Suggested fix:** Remove the CloudTargetDomain, OnPremTargetDomain, and HybridEndpoint parameters, prompts, and validation from deploy.ps1 (Migration settings are module config now), or — if these are meant to seed Migration module config — write them into the generated config under the keys MigrationService actually reads. Either way, stop hard-failing install on values that have no effect.

### [ci-release] AGENTS.md still claims ci.yml is misplaced in the repo root and that no CI exists

`AGENTS.md:119`

Commit 00054a8 ('Wire up CI workflow so it actually runs') moved the workflow to .github/workflows/ci.yml and fixed the branch trigger to master — .agents/state.md line 10 records exactly this. But AGENTS.md (authority level 3, the agent behavioral contract) still asserts the workflow is misplaced in the repo root. This is the Known Failure Class 3 pattern (stale references: docs claiming things the code no longer does), and it is a direct conflict between two authority sources that AGENTS.md itself says must be flagged. The accompanying claim 'do not assume any check runs automatically on push or PR' is about to flip from wrong-pessimistic to wrong-optimistic: once the workflow runs, maintainers will see green CI, yet per the vacuous-test finding it validates only that the web project compiles. Update both together.

**Suggested fix:** Rewrite the AGENTS.md Verification bullet: CI exists at .github/workflows/ci.yml but has never been observed running; keep treating verification as local-only until a green run with a non-zero test count is observed (the test-count caveat matters because of the vacuous dotnet test resolution bug).

### [ci-release] deploy-pipeline.ps1 -Prod hardcodes Apply and the overwrite confirmation, with no PlanOnly path

`tools/deploy-pipeline.ps1:149`

promote-dev-to-prod.ps1 was designed dry-run-by-default with an explicit `-IUnderstandThisOverwritesProd` confirmation gate (promote-dev-to-prod.ps1 lines 399-400: `if ($Apply -and -not $IUnderstandThisOverwritesProd) { throw ... }`). deploy-pipeline.ps1 -Prod neutralizes both protections by unconditionally passing `Apply = $true` and `IUnderstandThisOverwritesProd = $true`, and offers no -PlanOnly/-WhatIf switch of its own (param block lines 29-45 has none); deploy.ps1 likewise has no plan mode. AGENTS.md Architectural Invariant 4 states 'Every ops-script step must support -PlanOnly (via Invoke-PlanOrAction / Write-Plan)'. As written, the documented release entry point (`.\deploy-pipeline.ps1 -Prod`) goes straight to overwriting the prod publish folder with no way to preview what will change, while the underlying script's safety design implies that preview was a requirement.

**Suggested fix:** Add a `-PlanOnly` switch to deploy-pipeline.ps1 that forwards `Apply = $false` to the promote script (it already prints a full dry-run plan), and document `deploy-pipeline.ps1 -Prod -PlanOnly` as the required first step before a real promotion. Consider requiring the operator to pass the confirmation switch through rather than hardcoding it.

### [config] Legacy config migration writes per-module config files non-atomically and never repairs a truncated result

`Services/ModuleConfigService.cs:177`

EnsureLegacyMigrated writes each new per-module config with a direct File.WriteAllText to the live path — the only non-temp-file config write in the repo — violating the Constitution rule that config writes are atomic (temp file then replace; SaveModuleConfig at lines 93-108 does it correctly). Failure mechanism: a crash/IOException mid-write leaves a truncated module-config-<Id>.json; because the catch (Exception) at line 185-188 swallows the error, _legacyMigrated is still set true, and on the next run the skip-existing guard at lines 169-170 ('if (File.Exists(perModulePath)) continue;') sees the truncated file and never rewrites it. The corrupt file then persists permanently, and (per finding above) corrupt config is partly silent downstream.

**Suggested fix:** Route the migration write through the same temp-file + File.Replace/Move pattern used by SaveModuleConfig (or simply call SaveModuleConfig from the migration loop), so a crash leaves either no file or a complete one.

### [config] ReadModuleConfig swallows corruption and returns empty config — fail-closed is opt-in per caller

`Services/ModuleConfigService.cs:137`

ReadModuleConfig catches all exceptions and returns an empty dictionary, making a corrupt file indistinguishable from an absent one at the GetValue/GetModuleConfig API surface. The Constitution requires 'corrupt config fails closed (never silent fallback to defaults)', but here fail-closed only happens if each caller separately remembers to call IsModuleCorrupt — and grep shows only 7 of ~25 call sites do (ExoConnectionPool, ModuleCredentialService, MigrationService, MigrationTargetDatabaseSelector, Comms10kService, PermissionValidator, ExchangeOnlineConfig.razor). Concrete silent-default victims beyond the ProtectedPrincipalService finding: LicensingUpdatesService.GetAllowedLicenseTypes falls back to a hardcoded license list on corrupt config (LicensingUpdatesService.cs:75-77), PermissionValidator.GetPreventSelfGrant falls back to appsettings (PermissionValidator.cs:56-60), and ModuleEnablementService's migration falls back to appsettings ExchangeOnline:AppId (ModuleEnablementService.cs:152-153) — each silently treating corrupt as absent.

**Suggested fix:** Make corruption first-class in the API: have GetValue/GetModuleConfig throw a ModuleConfigCorruptException (or return a result type) when the file exists but fails to parse, and let the handful of callers that genuinely want absent-equals-empty handle it explicitly. That converts every current and future caller to fail-closed by default instead of by convention.

### [config] ModuleConfig admin page renders corrupt config as blank and overwrites it on save with empty audit 'previous'

`Components/Pages/ModuleConfig.razor:482`

LoadState calls ModuleConfigSvc.GetModuleConfig(module.Id) with no IsModuleCorrupt check; on a corrupt file the swallowing reader (ModuleConfigService.cs:137-141) returns an empty dict, so the admin sees blank fields with no corruption warning. If they hit Save, SaveConfigAsync overwrites the corrupt file with whatever is on screen — silently destroying the possibly-recoverable original values — and the audit diff at line 530 reads 'previous' through the same swallowing reader, so the audit event records the previous values as empty rather than what was actually lost. ExchangeOnlineConfig.razor:176 ('if (ModuleConfigSvc.HasModuleConfigFile(...) && ModuleConfigSvc.IsModuleCorrupt(...))') shows the intended guard exists elsewhere but was not applied to the generic module config page — a cross-session drift.

**Suggested fix:** In LoadState, check HasModuleConfigFile && IsModuleCorrupt and show a blocking 'config file is corrupt — fix or delete config/module-config-<Id>.json' banner (as ExchangeOnlineConfig does), refusing to render blank fields or accept a save until the operator acknowledges.

### [config] Module config key lookup is case-sensitive when the file exists but case-insensitive when absent

`Services/ModuleConfigService.cs:134`

ReadModuleConfig returns the raw JsonSerializer.Deserialize<Dictionary<string,string>> result, which uses the default case-sensitive ordinal comparer, while both the absent-file path (line 129) and the corrupt path (line 140) return dictionaries built with StringComparer.OrdinalIgnoreCase — and the sibling ModuleAdminService deliberately rewraps its deserialized dict with OrdinalIgnoreCase (ModuleAdminService.cs:103-105), showing the intended semantics. Consequence: a hand-edited config file (a workflow the example file explicitly invites: 'Copy to module-config-ConferenceRooms.json ... replace placeholders') whose key casing differs from the catalog key (e.g. 'defaultArbiterGroup') is silently ignored by GetValue, and ConferenceRooms' preflight will report the group as missing even though the operator can see it in the file.

**Suggested fix:** Wrap the deserialized dictionary: return new Dictionary<string,string>(deserialized, StringComparer.OrdinalIgnoreCase) — matching ModuleAdminService and the fallback paths. Add a unit test reading a file with mixed-case keys.

### [config] Module config saves are whole-file last-write-wins across concurrent admin sessions

`Components/Pages/ModuleConfig.razor:531`

The admin page snapshots the entire module config at page load (line 482) and SaveConfigAsync writes that whole dictionary back (line 531). ModuleConfigService._writeLock only serializes the file replacement itself, not the read-modify-write cycle, so two admin sessions editing different keys of the same module silently lose each other's changes (last write wins) with a misleading audit trail (the second save's 'previous' is read after the first save, so the first admin's change appears in 'previous' and its disappearance is attributed to nobody). The same unguarded read-modify-write exists in ProtectedPrincipalService.SaveDirectoryReadSecretId (ProtectedPrincipalService.cs:339-341), which can race with a concurrent ModuleConfig page save for the same module. Torn file writes are prevented (temp + File.Replace), but lost updates are not.

**Suggested fix:** Add optimistic concurrency: have GetModuleConfig return a version token (file last-write time or content hash) and have SaveModuleConfig reject the write when the token no longer matches, surfacing 'config changed by another session — reload' in the UI. Alternatively expose an atomic UpdateModuleConfig(moduleId, Action<Dictionary<string,string>>) that performs read-modify-write inside _writeLock and migrate SaveDirectoryReadSecretId to it.

### [consistency] GetGraphClientAsync duplicated 5x with divergent secret-key fallback and error idioms

`Services/MfaResetService.cs:22`

Five private copies of GetGraphClientAsync build a GraphTokenClient from Delinea fields (MfaResetService:20, NamedLocationsService:20, M365GroupManagementService:30, EmergencyDisableService:269, TestAccountPoolService:1165). They drift in three ways. (1) MfaReset, NamedLocations, and M365GroupManagement fall back to the module's 'DelineaSecretId' key when 'GraphDelineaSecretId' is absent — the Constitution reserves DelineaSecretId for AD/on-prem username/password secrets, and ModuleCatalog.cs declares only GraphDelineaSecretId for these three modules, so the fallback reads an undeclared key and, if an admin hand-adds an AD secret id under that key, fetches an AD password secret for Graph use. EmergencyDisable and TestAccountPool (which DO have a real AD DelineaSecretId alongside GraphDelineaSecretId) correctly omit the fallback — the copies that kept the fallback are the ones where it is wrong-by-Constitution. (2) Error idiom drift: MfaReset/EmergencyDisable/TestAccountPool return null on misconfiguration; NamedLocations/M365GroupManagement throw InvalidOperationException with operator guidance. (3) None of the five checks ModuleConfigService.IsModuleCorrupt before GetValue, unlike the canonical ModuleCredentialService.GetCredentialsAsync (line 21), so a corrupt module config is reported as 'module is not configured' instead of 'config is corrupt'.

**Suggested fix:** Extract one shared factory (e.g. ModuleGraphCredentialService.GetGraphClientAsync(moduleId)) that reads only GraphDelineaSecretId, checks IsModuleCorrupt first, and has a single agreed failure idiom; delete the five copies and the DelineaSecretId fallback.

### [consistency] TestAccountPool reimplements on-prem Exchange connect without the base retry loop

`Services/TestAccountPoolService.cs:1363`

ExchangeServiceBase.ConnectOnPrem wraps New-PSSession in a 3-attempt retry with escalating backoff ('catch (Exception ex) when (attempt < maxRetries) ... Thread.Sleep(2000 * attempt)') because on-prem WinRM session creation is flaky. TestAccountPoolService duplicated the same connect sequence as a private static ConnectOnPremExchange (same New-PSSessionOption, same Kerberos New-PSSession, same onpremSession variable) but with zero retries — a single transient failure throws immediately via ThrowIfHadErrors/InvalidOperationException and fails the mailbox-enable step of account provisioning. It also re-duplicates RemoveOnPremSession as RemoveOnPremExchangeSession. This is the exact 'one copy got the hardening, the other did not' pattern: the base class gained retries and trace steps; the copy never did.

**Suggested fix:** Reuse ExchangeServiceBase.ConnectOnPrem (extract it to a shared helper if TestAccountPoolService should not inherit the base), or copy the retry/backoff loop into ConnectOnPremExchange.

### [consistency] Legacy module-config migration writes per-module files non-atomically

`Services/ModuleConfigService.cs:177`

The Constitution requires config writes to be atomic (temp file then replace), and SaveModuleConfig in the same class follows that pattern (temp + File.Replace/File.Move). EnsureLegacyMigrated, however, writes each migrated per-module config with a direct File.WriteAllText to the final path. A crash/app-pool recycle mid-write leaves a truncated module-config-<id>.json, which IsModuleCorrupt then reports as corrupt — and because corrupt configs correctly fail closed, one interrupted startup migration can lock out credential retrieval for that module until manual repair. Two authors implemented 'write module config' two different ways inside one file.

**Suggested fix:** Route the migration writes through the same temp-then-replace code path as SaveModuleConfig (extract a private WriteAtomic(path, json) helper used by both).

### [consistency] Migration AD-group exclusion uses substring match on DNs and ambient credentials

`Services/ExchangeServiceBase.cs:630`

CheckAdGroupMembership (shared base helper, called from MigrationService.cs:147 for ExcludedADGroups eligibility checks) decides group membership by case-insensitive SUBSTRING match of the configured group name against each memberOf distinguished name. A configured exclusion like 'Sales' matches every group whose full DN contains 'Sales' anywhere — including the OU path (CN=AnyGroup,OU=Sales,...) — producing false 'Ineligible' verdicts. The Constitution's principal-matching rule (OU/group checks use resolved directory objects, not substring matches) is followed elsewhere (ProtectedPrincipalService resolves DNs and escapes LDAP filters; GroupManagementService resolves group DNs before mutation) but not in this older helper. Additionally, this helper runs Get-ADUser with no -Credential parameter (app-pool identity), unlike every other AD read in the repo which uses module Delinea credentials — credential-sourcing drift between sessions/models.

**Suggested fix:** Resolve each excluded group to its DN once (Get-ADGroup) and compare full DNs with string.Equals(..., OrdinalIgnoreCase), or parse the CN component before comparing; pass the Migration module's Delinea credential like the other AD operations do.

### [creds] SMTP sends credentials and live test-account passwords over cleartext when SmtpUseSsl=false

`Services/EmailService.cs:424`

SendEmailOrThrowAsync maps SmtpUseSsl=false to SecureSocketOptions.None, which never negotiates STARTTLS even when the server offers it. The shipped sample default is 'SmtpPort: 25, SmtpUseSsl: false' (appsettings.json.sample:25-28). Two secrets then transit in cleartext: (1) if SmtpUsername is set, 'await client.AuthenticateAsync(_smtpUser, _smtpPass)' performs SASL auth on the unencrypted connection; (2) SendTestAccountPasswordAsync (line 358) puts the newly reset, live AD test-account password in the HTML body ('<div class="password">{h(password)}</div>', line 389) and sends it over that same channel. The Constitution forbids leaking passwords; transmitting a working AD password over unencrypted SMTP on the wire is the network equivalent.

**Suggested fix:** Use SecureSocketOptions.StartTlsWhenAvailable as the non-SSL default (MailKit will opportunistically encrypt), and refuse to call AuthenticateAsync or send SendTestAccountPasswordAsync mail when the connection ends up unencrypted (client.IsSecure == false), failing the checkout instead (the existing email-failure cleanup path at TestAccountPoolService.cs:363-371 already disables and resets the account).

### [creds] SMTP and ServiceNow passwords stored as plaintext in global appsettings.json instead of Delinea

`Services/ServiceNowService.cs:21`

Constitution Credential Isolation: 'Privileged credentials must come from Delinea Secret Server unless the operation is explicitly read-only and approved for ambient Windows identity.' Two credentials bypass this: ServiceNow Basic-auth password ('_password = config["ServiceNow:Password"] ?? ""', then base64'd into a default Authorization header) and the SMTP submission password (EmailService.cs:29: '_smtpPass = config["Email:SmtpPassword"] ?? ""'). Both live in plaintext in the global appsettings.json (sample lines 26-27 'SmtpUsername'/'SmtpPassword'), which deploy scripts deliberately preserve on the server, so the plaintext persists across releases. Neither uses ambient Windows identity; SMTP submission is a write operation (it sends mail as the org, including the test-account password emails). The Delinea bootstrap credential itself correctly uses Windows Credential Manager (CredentialManagerService), so a precedent for non-appsettings storage already exists.

**Suggested fix:** Move both passwords behind Delinea secret IDs (e.g. Email:SmtpDelineaSecretId, ServiceNow:DelineaSecretId) or Windows Credential Manager targets like the Delinea bootstrap credential, and document them as explicitly named shared infrastructure credentials per the Constitution. If plaintext appsettings storage is an accepted trade-off, record that decision in .agents/decisions.md.

### [docs-drift] AdminModuleSpec.md version header is 7 minor versions stale and its descriptor example omits five current fields

`docs/AdminModuleSpec.md:3`

AGENTS.md authority item 6 explicitly requires checking the spec's version header against the csproj version and flagging drift. The spec claims it is based on v1.5.4 while the app is at 2.3.5 (~8 modules and many security mechanisms newer than the spec baseline). Concretely, the spec's binding AdminModuleDescriptor example (lines 14-28) omits five fields that exist in Modules/AdminModuleDescriptor.cs today: Category, Version, DependsOn, IsConfigOnly, and ConfigFields. The Version field matters most: the Constitution's versioning rule says module-scoped behavior changes must bump the module's Version in ModuleCatalog.cs, but a developer following the spec verbatim would create a descriptor with no Version at all (it silently defaults to "1.0.0"). The spec also never mentions ConfigFields, yet the spec's own Credential Isolation section tells authors to 'Declare a DelineaSecretId config field in the module descriptor' with no field shown in the descriptor contract.

**Suggested fix:** Re-verify the spec against the 2.3.5 codebase, add the missing descriptor fields (especially Version, with a pointer to the Constitution's module-version bump rule, and ConfigFields), and update the header to the current app version. Checked clean: the spec's ModulePermission record signature, the EventLog fail-closed claim, icon naming, and the config file paths all still match code exactly.

### [docs-drift] AGENTS.md still instructs agents that there is no working CI and ci.yml is misplaced in the repo root

`AGENTS.md:117`

The Verification section of AGENTS.md (authority level 3, read every session) asserts CI does not exist. In reality commit 00054a8 ('Wire up CI workflow so it actually runs') moved the workflow to .github/workflows/ci.yml with the correct master trigger, there is no ci.yml in the repo root anymore, and `gh run list` shows the workflow has already executed successfully twice on master pushes (runs 27414996270 and 27414062844, both 'completed success' on 2026-06-12). Every future agent session will be told to ignore CI signals and treat verification as local-only, and the parenthetical points readers at a repo-root file that no longer exists. AGENTS.md says doc/code conflict is a first-class defect; this one sits in the behavioral contract itself.

**Suggested fix:** Rewrite the AGENTS.md Verification bullet: CI exists at .github/workflows/ci.yml, runs build+test+PSScriptAnalyzer+Pester on push/PR to master, and has been observed succeeding; local verification remains required before claiming completion but agents may now treat CI as an additional signal.

### [docs-drift] .agents/repo-map.json 'ci' entry says NONE ACTIVE, .github/workflows does not exist, and trigger is branches:[main] — all three claims now false

`.agents/repo-map.json:31`

AGENTS.md directs agents to 'use the repo's automated verification recorded in .agents/repo-map.json', making this the canonical machine-readable verification map. Its ci field makes three factual claims that are each contradicted by the current tree: the workflow is at .github/workflows/ci.yml (that directory exists), the trigger is branches: [master], and the workflow has run twice with success. This is the same staleness as the AGENTS.md finding but in a second canonical file, so fixing only one leaves a documented-conflict pair that the repo's own rules treat as a defect.

**Suggested fix:** Update the ci entry to describe the active workflow (.github/workflows/ci.yml, master push + PR triggers, build-test and powershell jobs) in the same commit that fixes AGENTS.md, so the two canonical sources cannot disagree.

### [docs-drift] .agents/state.md confirmed stale: describes landed/pushed work as uncommitted/unpushed, omits commit 0021502, and says CI has never been observed running

`.agents/state.md:9`

Confirming the known staleness plus one additional item. (a) Known: lines 9-12 list 00054a8/54c07da/c8ed096 as 'local, ahead of origin/master, not pushed' — git status shows 'master...origin/master' with zero ahead commits; everything is pushed. (b) Known: lines 13-19 describe the Conference Rooms config extraction as 'Uncommitted work in tree... awaiting owner's commit decision' — it landed as e1dbac1 and the tree is clean; the code claims themselves verified true (FindMissingRequiredGroups exists at Services/ConferenceRoomService.cs:162, catalog module Version = "2.0.4" at Modules/ModuleCatalog.cs:328, config/module-config-ConferenceRooms.example.json committed). (c) Known: the file predates 0021502 (robocopy /XD fix), which appears nowhere. (d) Additional staleness found: lines 33-34 and 46-47 say 'First push will confirm it actually executes — until a run is observed, continue to treat verification as local-only' — two successful CI runs have since been observed on master. Since state.md is the designated 'first place to read for current repo state', a fresh session would re-litigate a commit decision that was already made and might try to re-create work that already landed.

**Suggested fix:** Run the repo's `handoff` process: rewrite Now/Findings/Verification to reflect that all five recent commits are pushed, the config extraction landed as e1dbac1, 0021502 fixed the robocopy /XD exclusions, and CI has been observed succeeding. Keep the still-valid note about the ModuleCatalog.cs Test Account Pool analog.com example (verified still present at ModuleCatalog.cs:404).

### [docs-drift] README documents a 'Workspace' Conference Rooms booking template that does not exist anywhere in code, and omits four real room types

`README.md:139`

The README (which AGENTS.md calls 'the full behavior reference') says the Conference Rooms module offers exactly three booking policy templates: Standard, Workspace, and Restricted. The actual RoomType enum is Standard, Video, Restricted, Exception, CEO, Executive — 'Workspace' appears in zero .cs/.razor/.json files in the repo (grep count 0), while four shipped types (Video, Exception, CEO, Executive — including the high-impact CEO type that force-removes existing calendar permissions per ConferenceRoomService.cs:529 'if (removeExistingPermissions || roomType == RoomType.CEO)') are undocumented. This reads like a hallucinated or pre-rewrite feature list from an earlier AI session that was never reconciled with the implemented module. An operator reading the README would look for a hot-desk mode that does not exist and would be unaware of the CEO/Executive lockdown behaviors.

**Suggested fix:** Replace the template list with the six real room types and a one-line description of each (especially noting that CEO forces removal of existing calendar permissions). Also worth a line on the Building-based room list grouping and the fail-closed required-groups preflight added in 2.0.3/2.0.4.

### [docs-drift] Three plan documents have no Status: header, so the repo's own plan-lifecycle rule cannot classify them; two describe work that is already implemented

`docs/ADAttributeEditor-Plan.md:1`

AGENTS.md authority item 8 says: 'Check the Status: header; only Approved or In progress plans represent current intent. Implemented/Superseded are history.' Grepping all docs/*-Plan.md headers shows four plans carry a Status header (all 'Implemented') but three carry none: ADAttributeEditor-Plan.md, AdminModuleModularization-Plan.md, and FutureModules-Plan.md. The first two describe work that demonstrably shipped — the AD Attribute Editor module exists (README.md:116 '### AD Attribute Editor (/ad-attribute-editor)' and a catalog entry) and the descriptor architecture the modularization plan proposes is the current architecture (Modules/AdminModuleDescriptor.cs, ModuleCatalog.cs with 21 modules). Without an Implemented/Superseded marker, the documented convention forces an agent to either treat finished plans as live intent or guess — exactly the cross-session ambiguity the convention exists to prevent. FutureModules-Plan.md is genuinely forward-looking but is equally unclassifiable.

**Suggested fix:** Add Status headers: ADAttributeEditor-Plan.md and AdminModuleModularization-Plan.md as 'Implemented' (with version landed), FutureModules-Plan.md as whatever lifecycle state fits ('Approved' backlog or a new 'Roadmap' status documented in AGENTS.md).

### [docs-drift] README installation instructions point all users at ADI-specific deploy.ps1 and never mention the generic installer tools/Install-ExchangeAdminWeb.ps1

`README.md:304`

AGENTS.md Architectural Invariant 1 establishes a deliberate split: 'tools/Install-ExchangeAdminWeb.ps1 is environment-neutral and standalone' for generic installs, while 'Dev deploy: ./deploy.ps1 (ADI-specific)'. The README's Installation section ('### 4. Deploy to IIS') tells every reader — including a new operator at a different site — to run .\deploy.ps1, and grep confirms 'Install-ExchangeAdminWeb' appears nowhere in the 631-line README. deploy.ps1 carries ADI-specific assumptions (and the promote/dev tooling around it defaults to paths like D:\inetpub\ExchangeAdminWebDev), so a non-ADI operator following the README verbatim runs the wrong, environment-coupled script while the purpose-built neutral installer goes undiscovered. This is a doc-doc-code conflict between the two install paths the repo intentionally maintains.

**Suggested fix:** Rework README §4 to present tools/Install-ExchangeAdminWeb.ps1 as the supported installation path for new environments (it supports -PlanOnly), and reposition deploy.ps1 as the maintainer's dev-instance script.

### [failure-classes] AllowedLicenseTypes silently falls back to hardcoded defaults when module config is corrupt

`Services/LicensingUpdatesService.cs:76`

GetAllowedLicenseTypes treats a null/blank GetValue result as 'use the built-in default list E5,EOP2+SOP2,F3,F3+EOP1'. Because ModuleConfigService.ReadModuleConfig returns an empty dictionary for a corrupt config file (swallowed catch at ModuleConfigService.cs:137-141), a corrupt module-config-LicensingUpdates.json silently re-enables the default license set — including values an admin may have deliberately removed — and both PreviewCsvAsync and the UI dropdown will accept and WRITE them to extensionAttribute11. The Constitution requires corrupt config to fail closed, never silently fall back to defaults. Comms10kService (lines 25-28) shows the intended pattern (explicit IsModuleCorrupt check), making this a cross-model inconsistency.

**Suggested fix:** Check _moduleConfig.IsModuleCorrupt("LicensingUpdates") in GetAllowedLicenseTypes and return an empty array (or surface an error) so Preview/Apply fail closed; keep the hardcoded default only for the genuinely-absent-config case if that is the documented intent.

### [failure-classes] Get-ADGroupMember on the ~10k-member target group will hit the ADWS 5000-object default limit

`Services/Comms10kService.cs:64`

Both GetMembersAsync (line 64) and ExecuteReplaceAsync's pre-count (line 198) enumerate the target group with Get-ADGroupMember. The module's namesake group holds ~10,000 members, but Active Directory Web Services rejects Get-ADGroupMember for groups larger than MaxGroupOrMemberEntries (default 5000) with a terminating 'The size limit for this request was exceeded' error unless every DC's ADWS config has been raised. In ExecuteReplaceAsync the pre-count call sits OUTSIDE the try/catch (lines 198-204; only the Set-ADGroup at 206-220 is guarded), so this failure surfaces as a raw exception to the page rather than a clean result. TestAccountPoolService.IsCurrentPoolMember (TestAccountPoolService.cs:1349) has the same exposure for large pool groups.

**Suggested fix:** Enumerate membership via `Get-ADGroup -Properties member` (single attribute read, no 5000 cap) or an LDAP ranged-retrieval query instead of Get-ADGroupMember; move the pre-count inside the try/catch so an enumeration failure returns a structured Comms10kUpdateResult instead of an unhandled exception.

### [failure-classes] Cloud bulk mailbox-permission rows lose partial-success information (FullAccess granted, SendAs failed reports plain FAILED)

`Services/MailboxPermissionService.cs:38`

AddMailboxPermissionsAsync runs Add-MailboxPermission (FullAccess) and Add-RecipientPermission (SendAs) sequentially inside one RunAsync lambda. If FullAccess succeeds and SendAs then throws, RunAsync's catch converts the whole operation to a single failure — the already-applied FullAccess grant is reported nowhere. The bulk CSV loop (ProcessMailboxPermissionsCsvAsync line 197) then audits the row as Failed and the CSV report shows Status=FAILED, so the audit trail understates access that was actually granted, and an operator 'retry' re-runs both grants. The on-prem variants in the SAME file were written to handle exactly this (per-right try/catch with an explicit 'Partial: granted X ... Failed: Y' result at lines 318-319), making the cloud path a cross-session inconsistency.

**Suggested fix:** Mirror the on-prem pattern in the cloud Add/Remove methods: wrap each right in its own try/catch, collect successes/failures, and return a 'Partial' message when mixed — so the bulk audit row and CSV report record which rights actually landed.

### [protected] GroupManagement protected-principal check is UI-only and skipped for non-UPN identities

`Components/Pages/GroupManagement.razor:269`

The protected-principal enforcement for adding/removing AD group members lives entirely in the Blazor page and is gated on the member string containing '@'. GroupManagementService.AddMemberAsync / RemoveMemberAsync (the actual mutating methods that call Add-ADGroupMember / Remove-ADGroupMember) contain NO protection check at all. This is exactly the Constitution's 'UI hiding is not security / re-check immediately before the write' violation: any future or alternate caller of the service bypasses protection entirely, and even from the page, a protected principal supplied by sAMAccountName or DOMAIN\user (no '@') skips the entire ResolveWithStatusAsync/CheckAsync block. The same fail-open gate is repeated for RemoveMember at line 352 (`member.Contains('@')`). Unlike ADAttributeEditorService.SaveAsync and TestAccountPoolService, which call CheckAsync inside the service before mutating, GroupManagementService was never given an in-service check — a classic cross-model gap where one model hardened its own services and the group service never got it.

**Suggested fix:** Move the protected-principal resolution+CheckAsync into GroupManagementService.AddMemberAsync/RemoveMemberAsync (fail closed when resolution is Unavailable/Ambiguous), independent of any '@' heuristic, so the check is enforced immediately before the AD write regardless of caller or identity format.

### [protected] Dead, parallel group-matching helpers (MatchesDnToProtectedGroup/ExtractCnFromDn) are tested but unused; the real transitive path is untested

`Services/ProtectedPrincipalService.cs:607`

MatchesDnToProtectedGroup and ExtractCnFromDn are internal static helpers with no production caller — the live group check (CheckTransitiveGroupMembership) instead uses the LDAP_MATCHING_RULE_IN_CHAIN OID (1.2.840.113556.1.4.1941) and never invokes these helpers. They are a leftover from an earlier non-transitive name-matching implementation that a later session replaced but did not delete. Worse, ProtectedPrincipalServiceTests covers these dead helpers (MatchesDnToProtectedGroup_MatchesCorrectly, ExtractCnFromDn_ExtractsCorrectly) while the actual transitive membership code has zero unit coverage, giving false confidence that group protection is verified. This is the 'stale references / docs-and-code disagree' failure class manifested as stale code + misdirected tests.

**Suggested fix:** Delete the unused helpers and their tests, or repurpose them; add coverage that exercises the actual transitive path (e.g., the expansionHadErrors fail-closed branch) so group protection is genuinely guarded.

### [ps-scripts] icacls native exit codes never checked; ACL failures reported as success

`deploy.ps1:290`

Invariant: every native exe call must check $LASTEXITCODE. All icacls invocations in deploy.ps1 (lines 275, 290, 298, 306) and Install-ExchangeAdminWeb.ps1 (lines 190, 569) pipe output to Out-Null and never inspect $LASTEXITCODE; each is immediately followed by a Write-Success/Write-Ok. Mechanism: icacls failure (bad/unresolvable account name, access denied) returns a non-zero exit code and prints to stdout/stderr, both discarded — the script proceeds and reports 'OK Audit log folder ACL set'. Consequence: the app pool identity may lack write access to the audit log folder, app logs folder, or config folder, so at runtime audit writes and module-config writes fail — undermining the Constitution's audit invariant — while the deploy transcript shows all green.

**Suggested fix:** After each icacls call: `if ($LASTEXITCODE -ne 0) { throw "icacls failed (exit $LASTEXITCODE) granting $ServiceAccount on $path" }` (or route through a checked helper like Invoke-RobocopyChecked's pattern).

### [ps-scripts] deploy.ps1 upgrade path rewrites appsettings.json in place — non-atomic, no validation re-parse

`deploy.ps1:439`

Constitution: config writes must be atomic (temp file then replace). The upgrade-path config reconciliation rewrites the live prod appsettings.json directly with Set-Content and never re-parses the result before the app pool restarts. A crash/kill mid-write leaves truncated JSON; corrupt root config means the app fails to start (and there is no validation step that would catch a ConvertTo-Json -Depth 10 truncation of unexpectedly deep config — PS stringifies beyond -Depth instead of erroring). This is also a cross-session inconsistency: promote-dev-to-prod.ps1 (lines 352-356) and Install-ExchangeAdminWeb.ps1 (lines 207-208) both write to a temp file, re-parse it as validation, then Move-Item over the target — deploy.ps1 predates that pattern and was never updated. A timestamped backup is taken earlier (line 376), which mitigates but does not prevent the broken-state window.

**Suggested fix:** Reuse the temp-write + re-parse + Move-Item pattern from promote-dev-to-prod.ps1's Set-AppsettingsPathBase, and use -Depth 20 to match the other scripts.

### [ps-scripts] Promotion failure message claims rollback succeeded even when rollback failed

`tools/promote-dev-to-prod.ps1:531`

When promotion fails, the catch block attempts a rollback robocopy. If that rollback itself fails (exit >= 8 or throws), the failure is reported only via Write-Host (non-terminating, not captured in any state variable). Control then falls through to the unconditional `throw "Promotion failed and was rolled back. Prod has been restored from backup."` — asserting prod was restored when it may be in the explicitly-warned 'inconsistent state'. deploy-pipeline.ps1 surfaces only that throw message to the operator. The same message also fires for dry-run failures (e.g. a merge-preview throw) where the 'No backup available for automatic rollback' branch ran and nothing was rolled back. Mechanism: rollback outcome is tracked nowhere; only $promotionFailed is tracked.

**Suggested fix:** Track a $rollbackSucceeded flag set only on rollback robocopy exit < 8; throw distinct messages: 'rolled back successfully' vs 'ROLLBACK FAILED - prod inconsistent, restore manually from <backup>' vs (dry-run) 'dry run failed, no changes made'.

### [ps-scripts] deploy.ps1 Write-Fail exits instead of throwing — violates the repo error model and diverges from the installer

`deploy.ps1:39`

AGENTS.md invariant 5: 'PowerShell error model: $ErrorActionPreference = "Stop"; failures go through Write-Fail (throw)'. deploy.ps1's Write-Fail does `exit 1` (a process/script exit, not an exception), while Install-ExchangeAdminWeb.ps1's Write-Fail throws. The two definitions were clearly written in different sessions/models. Consequence beyond style: `exit` is invisible to a caller's try/catch and to $ErrorActionPreference=Stop — callers must remember to check $LASTEXITCODE with the right threshold, which is exactly what deploy-pipeline.ps1 got wrong (see the exit-code-masking finding). A throw-based Write-Fail would have propagated and made that bug impossible.

**Suggested fix:** Change deploy.ps1's Write-Fail to `Write-Host ...; throw $m` (matching the installer), then verify deploy-pipeline still reports failures correctly.

### [ps-scripts] test-delinea.ps1 prints raw Delinea auth response bodies and takes the password as a plain-string parameter

`tools/test-delinea.ps1:46`

ProjectConstitution.md line 42: 'Never log secret values, OAuth response bodies, bearer tokens, passwords, certificate private-key details, or raw Delinea auth responses.' This diagnostic script echoes the raw HTTP response body from the Delinea oauth2/token endpoint (and the secrets API) to the console on failure — exactly the 'raw Delinea auth responses' the Constitution forbids, and console output is routinely captured into transcripts/CI logs. Additional gotchas in the same file: `[string]$Password` accepts the Delinea password as a plain command-line argument (lands in PSReadLine history and process command line); the SecureString prompt is immediately converted to a plaintext variable; the default ServerUrl hardcodes the internal ADI endpoint `secretserver.ad.analog.com` in a generic tools/ script; and the script never sets `$ErrorActionPreference = "Stop"` (invariant 1) — it relies on per-call -ErrorAction Stop only.

**Suggested fix:** Print only the HTTP status code and a sanitized error reason (never $_.ErrorDetails.Message from the token endpoint); change -Password to [securestring]; drop the ADI default URL (make it mandatory or blank); add $ErrorActionPreference = "Stop".

### [ps-scripts] Zero Pester coverage for 2,400+ lines of ops-script logic despite repo rule requiring it

`tools/promote-dev-to-prod.ps1:194`

AGENTS.md Working Rules: 'New `.ps1` logic requires Pester coverage in `tests/ps/`.' The tests/ps directory (and tests/ entirely) does not exist, yet the ops scripts contain genuinely testable, high-consequence pure logic: promote's Merge-Object / Compare-JsonKeys / Format-ValueForDiff (JSON dev-wins merge that rewrites prod config), Assert-DevProdPaths / Assert-SafeDeploymentPath guards, the installer's Normalize-Groups and seed generators. A merge bug here silently corrupts prod module config — exactly the class of regression the recent robocopy /XD incident showed these scripts are prone to. Several of the bugs found in this review (exit-code masking, rollback misreporting) would be caught by trivial Pester tests.

**Suggested fix:** Create tests/ps/ with Pester tests for Merge-Object/Compare-JsonKeys (dev-wins, prod-only-key preservation, nested objects, arrays), Assert-DevProdPaths, Normalize-Groups, and a mocked-$LASTEXITCODE test for the pipeline's failure detection.

### [test-gaps] Credential chain (DelineaService, ModuleCredentialService, GraphTokenClient, CredentialManagerService) has zero direct tests

`Services/DelineaService.cs:113`

The entire privileged-credential path is untested. DelineaService (316 lines) contains unit-testable logic with security consequences: secret-field extraction in GetCredentialsBySecretIdAsync (case-insensitive 'Password'/'Username' field matching, missing-field fail paths), OAuth token caching/expiry under _tokenLock, and GetOAuthErrorCode — the function that decides what part of a Delinea auth error is safe to surface (the Constitution forbids logging raw Delinea auth responses; a regression here leaks secrets into logs with no failing test). ModuleCredentialService implements per-module credential isolation (DelineaSecretId vs GraphDelineaSecretId — a CREDS invariant) and GraphTokenClient handles OAuth token bodies. These types appear in tests only as pass-through constructor dependencies (ADAttributeEditorServiceTests:60, LicensingUpdatesServiceTests:46), never as the subject under test.

**Suggested fix:** Test DelineaService against a stub HttpMessageHandler: field-name matching, missing username/password → null + error path, token expiry refresh, and assert GetOAuthErrorCode never returns the raw body. Test ModuleCredentialService resolves the correct per-module secret ID and fails closed when unconfigured.

### [test-gaps] ModuleConfigService untested: atomic-write, corruption, and legacy-migration logic unguarded (migration write is also non-atomic)

`Services/ModuleConfigService.cs:177`

ModuleConfigService implements three Constitution-critical behaviors — atomic temp-then-replace saves (SaveModuleConfig), corrupt-config detection (IsModuleCorrupt), and one-time legacy migration (EnsureLegacyMigrated) — with zero tests, even though the directly analogous file-backed SectionAccessService has a thorough 10-test suite proving this is cheap to test here. The untested migration path already contains a latent violation: it writes per-module config files with a bare File.WriteAllText (no temp+replace), contradicting the CONFIG invariant 'config writes must be atomic', and ReadModuleConfig silently returns an empty dictionary on corrupt JSON (swallowed catch), which is only fail-closed if every consumer preflights IsModuleCorrupt — an assumption no test verifies.

**Suggested fix:** Add ModuleConfigServiceTests (temp-dir pattern from SectionAccessServiceTests): save/read round-trip, no .tmp residue, corrupt file → IsModuleCorrupt true and GetValue null, legacy migration creates per-module files and backs up the legacy file, existing per-module file not overwritten by migration. Fix the migration write to use the same temp+replace path.

### [test-gaps] All EXO/Graph mutating services have zero direct test coverage, including the bulk-CSV aggregation loop

`Services/MailboxPermissionService.cs:93`

None of the services that mutate live tenant/directory state have a dedicated test file: MailboxPermissionService (418 lines), CalendarPermissionService (296), GroupManagementService (296), M365GroupManagementService (234), MfaResetService (183), NamedLocationsService (252), DhcpAuthorizationService (243), Comms10kService (273), OutOfOfficeService (77). Most exposed is ProcessMailboxPermissionsCsvAsync — a 160-line per-row loop over up to 200 mutations that aggregates successCount/errors/entries and emits per-row audit events. This is exactly Known Failure Class #2 (success aggregation) from past incidents, yet only the passive result model is tested (BulkOperationResultTests); no test drives the loop to verify mixed success/failure rows produce correct counts, continue-on-error behavior, or per-row audit emission. Shared plumbing (ExchangeServiceBase.Invoke, PermissionValidator, ProtectedPrincipalService) is tested, but the per-service orchestration that decides what gets written and what gets reported is not.

**Suggested fix:** Refactor ProcessMailboxPermissionsCsvAsync to take a mutation delegate (or extract the per-row pipeline) so the loop can be tested without EXO; assert mixed-result aggregation, the 201-row limit message, ParseBool failure handling, self-grant block, and that each row produces exactly one audit call. Prioritize the same pattern for GroupManagementService and MfaResetService.

### [test-gaps] ADAttributeEditorUndoService (AD-writing undo path) and UndoRegistry untested

`Services/ADAttributeEditorUndoService.cs:124`

The undo service re-writes previously captured attribute values back into Active Directory based on parsed audit-event payloads (CanUndo/PreviewUndoAsync/ExecuteUndoAsync, 339 lines). The forward path is well tested (ADAttributeEditorServiceTests, 21 facts) but the reverse path — which is more dangerous, because a parsing bug writes stale or wrong values into AD while claiming to 'restore' — has no tests, and neither does UndoRegistry. The audit-event parsing in CanUndo/PreviewUndoAsync is pure dictionary logic and trivially unit-testable.

**Suggested fix:** Add tests for CanUndo/PreviewUndoAsync against representative audit-event dictionaries (valid, missing old-value, malformed, already-undone) and for UndoRegistry module resolution. Gate ExecuteUndoAsync's pre-write checks (re-read before write, protected-principal block) the same way EmergencyDisableServiceTests gates its step ordering.

### [test-gaps] MigrationService move-request orchestration untested (only peripheral fragments covered)

`Services/MigrationService.cs:246`

MigrationService is 750 lines orchestrating mailbox migrations. Existing tests cover only satellites: MatchMigrationUserTests (user matching), MigrationUserSearchResultTests (a result record), MigrationTargetDatabaseSelectorTests (a separate service). The core flows — CheckMigrationEligibilityAsync, CheckBulkMigrationEligibilityAsync (another bulk CSV loop, Known Failure Class #2), and CreateMigrationBatchAsync (the actual mutation, with autoStart/autoComplete flags) — have no coverage. A regression in eligibility logic silently migrates ineligible mailboxes or batches the wrong users.

**Suggested fix:** Extract eligibility-decision logic into testable pure functions (input: mailbox/user facts; output: eligible + reason) and test the bulk CSV loop's aggregation the same way as recommended for MailboxPermissionService.


## Confirmed — LOW (1)

### [auth] ExchangeOnlineConfig page policy contradicts the catalog; the registered 'ExchangeOnline' policy is dead and module-admin delegation links break

`Components/Pages/ExchangeOnlineConfig.razor:4`

The catalog declares the ExchangeOnline module's permission as alias 'ExchangeOnline' (ModuleCatalog.cs line 110) and ConfigureAuthorizationPolicies registers a dynamic policy for it, but the page at that module's route is gated by 'AdminSettings' instead. The 'ExchangeOnline' policy is therefore registered but enforced nowhere ('ModuleCatalog is the source of truth for ... permissions' per the Constitution). Concrete breakage: NavMenu shows config-only modules to delegated module admins (line 71 links to cfgModule.Route for IsConfigOnly modules) and ModuleConfig.razor redirects config-only modules to module.Route — so a delegated ExchangeOnline module admin who is not in AdminGroups gets a nav link that always lands on access-denied. Gating EXO connection config behind AdminSettings is defensible, but then the catalog entry and dead policy should say so.

**Suggested fix:** Either gate the page with the catalog alias ('ExchangeOnline') plus the existing in-code AdminSettings re-checks, or change the catalog so config-only system-config modules are explicitly AdminSettings-gated and stop registering the unused dynamic 'ExchangeOnline' policy; also exclude IsConfigOnly modules from delegated-admin nav.


## Low / unverified (29)

These were not adversarially re-verified (low severity). Treat as leads.

### [auth] ModuleConfig page has no catalog-backed policy; gating lives only in component code

`Components/Pages/ModuleConfig.razor`

The Constitution requires 'Every page with privileged functionality must have a catalog-backed policy', but /module-config/{ModuleId} — which edits per-module config including Delinea secret IDs — carries only a bare [Authorize] (default policy: any authenticated Windows user). The compensating controls are real: OnParametersSetAsync checks AdminSettings-or-IsModuleAdmin and navigates to access-denied before authChecked allows rendering, and SaveConfigAsync re-checks the same condition before writing (line 522), while enablement/section-access/module-admin saves require AdminSettings (lines 569, 620, 680). The design constraint (AdminSettings OR delegated module admin cannot be expressed as one static catalog policy) explains the deviation, but it is undocumented, so a reviewer auditing pages against the 'catalog-backed policy' rule will flag it every time, and future pages may copy bare [Authorize] without copying the in-code checks.

**Suggested fix:** Register a dedicated 'ModuleConfigAccess' policy whose handler encodes AdminSettings-or-any-module-admin, apply it via [Authorize(Policy=...)], and keep the per-module IsModuleAdmin(ModuleId) check in code; at minimum document the deviation in docs/AdminModuleSpec.md.

### [creds] CredentialManagerService swallows all vault read errors, hiding bootstrap failure cause

`Services/CredentialManagerService.cs`

ReadCredential wraps the entire PasswordVault read in a blanket 'catch { return (null, null); }' (swallowed catch, no logging, no exception type filter). A real vault failure (vault service down, access denied under the app-pool identity, corrupted store) is indistinguishable from 'credential not provisioned'. DelineaService then logs the misleading 'Delinea credentials not found in Credential Manager (target: ...)' (DelineaService.cs:120), sending an operator hunting for a missing credential that actually exists. Since this is the single bootstrap credential gating every Delinea-backed module, accurate failure diagnosis matters for production triage.

**Suggested fix:** Catch only the expected not-found case (FindAllByResource throws a COMException with HRESULT 0x80070490 'Element not found' when no credential matches) and let or log other exceptions distinctly, e.g. return a third state or accept an ILogger and log the exception type (never the password) so 'not configured' and 'vault unreadable' are distinguishable.

### [creds] GraphTokenClient built per operation defeats its token cache and multiplies secret fetches

`Services/MfaResetService.cs`

GraphTokenClient carries a token cache ('_tokenLock', '_accessToken', '_tokenExpiry' with a 5-minute early-refresh window at GraphTokenClient.cs:107), but every Graph-using service constructs a fresh client per operation via GetGraphClientAsync (MfaResetService.cs:36, NamedLocationsService.cs:37, M365GroupManagementService.cs:48, EmergencyDisableService.cs:285, TestAccountPoolService.cs:1181), so the cache never gets a second use. Net effect: every single Graph operation performs one Delinea secret retrieval (pulling the client secret over the wire and into a new managed string) plus one client_credentials token request. Beyond latency, this needlessly multiplies the number of plaintext client-secret copies on the heap and the volume of secret traffic. MfaResetService is worst: GetUserMethodsAsync and each delete call independently construct a client.

**Suggested fix:** Cache the GraphTokenClient per module (e.g. lazy field invalidated when the module's GraphDelineaSecretId config changes, mirroring ExoConnectionPool's config-generation pattern) so the token cache is effective and the client secret is fetched from Delinea once per token lifetime instead of once per operation.

### [audit] AdminEventLog category filter dropdown is stale — TestAccountPool, MessageTrace, and the misfiled module categories are missing

`Components/Pages/AdminEventLog.razor`

The hardcoded category <option> list omits categories that the code actually writes: TestAccountPoolService writes category "TestAccountPool" (LogModuleAction with category: ModuleId), and there is no option for EmergencyDisable, LicensingUpdates, or Comms10k (today hidden inside MigrationAction/Lookup per the misfiling finding — once that is fixed, the dropdown must gain them). Operators can still see these events under "All" but cannot filter to them, and an operator filtering "MigrationAction" today silently gets EmergencyDisable and Comms10k events mixed in. This is known failure class 3 (stale references) applied to UI: the dropdown reflects the module set of an earlier session.

**Suggested fix:** Build the category list dynamically from the distinct categories present in the loaded events (or from ModuleCatalog), instead of a hardcoded option list that must be manually maintained per module.

### [audit] M365GroupManagementService injects AuditService but never uses it

`Services/M365GroupManagementService.cs`

The service declares and assigns a private AuditService _audit field but contains no _audit.Log* call anywhere; all M365 group auditing happens in M365GroupManagement.razor. A dead dependency like this is a multi-session drift artifact and a trap: a future maintainer (or model) may assume the service audits internally and remove the page-level audit calls, silently dropping the module's audit trail.

**Suggested fix:** Remove the unused AuditService injection from M365GroupManagementService (or move the page's audit calls into the service where the mutations occur, matching the TestAccountPool/LicensingUpdates pattern).

### [protected] Mutating GroupManagementService AD writes resolve member by UPN/email only, so the member bound for Remove can be empty and unchecked

`Services/GroupManagementService.cs`

RemoveMemberAsync resolves the member via `UserPrincipalName -eq` / `EmailAddress -eq` only. Members surfaced by GetMembersAsync populate Email from EmailAddress and leave it blank for principals without a mail attribute (nested groups, mail-less service accounts). For such members the page passes an empty string, the '@' protection gate at GroupManagement.razor:352 is skipped, and RemoveMemberAsync then throws 'not found' — so the operation fails rather than silently mutating, but the protected-principal check is still bypassed and the failure mode is incidental rather than designed. Pairs with the headline GroupManagement finding.

**Suggested fix:** Resolve members for protection checks by DN/objectGUID (already available in the AD member list) rather than relying on a mail attribute, and run the protection check unconditionally in the service.

### [consistency] Module config key lookup comparer differs between loaded and fallback dictionaries

`Services/ModuleConfigService.cs`

ReadModuleConfig deliberately returns 'new(StringComparer.OrdinalIgnoreCase)' for the missing-file and corrupt-file paths, signalling intent that config keys be case-insensitive. But the success path returns the raw JsonSerializer.Deserialize<Dictionary<string,string>> result, which uses the default ordinal case-SENSITIVE comparer. So GetValue(module, "DelineaSecretId") is case-insensitive only when the file does not exist (when it cannot matter) and case-sensitive whenever the file exists. A hand-edited or externally generated config with 'delineaSecretId' silently reads as 'not configured' — for credential keys that means the module reports unconfigured with no corruption warning.

**Suggested fix:** Wrap the deserialized dictionary: 'return new Dictionary<string,string>(parsed, StringComparer.OrdinalIgnoreCase);' so all three paths share one comparer.

### [consistency] PSCredential factory duplicated in 8 services with naming drift and a divergent base copy

`Services/TestAccountPoolService.cs`

An identical 6-line CreateCredential(username, password, domain) helper is copy-pasted into 8 services (ADAttributeEditorService:766, Comms10kService:235, DhcpAuthorizationService:223, LicensingUpdatesService:424, GroupManagementService:264, TestAccountPoolService:1550, ProtectedPrincipalService:689, and EmergencyDisableService:647 under the different name CreatePSCredential), while ExchangeServiceBase.ConnectOnPrem inlines a ninth variant that additionally calls securePassword.MakeReadOnly(). TestAccountPoolService.ResetPassword builds a tenth ad-hoc SecureString inline. The copies are behaviorally equivalent today, but this is the highest-count cross-session duplication in the repo: any future fix (e.g. UPN-vs-domain composition for a new domain format, or zeroing the password) must land in 10 places, and the CreatePSCredential rename plus the base's MakeReadOnly already show the copies drifting.

**Suggested fix:** Create one internal static PsCredentialFactory.Create(username, password, domain) (with MakeReadOnly) and replace all 10 occurrences.

### [consistency] Two parallel JSONL log writers with copy-pasted rotation/size logic

`Services/ExtendedLogService.cs`

JsonlLogService and ExtendedLogService independently implement the same rotation machinery: GetMaxFileBytes (identical except for the 'Audit:' vs 'ExtendedLog:' config prefix), GetWritableLogPath (identical size check), RotateLogFiles (identical delete-oldest/shift/move sequence), and the shared-read FileStream idiom — but with different serializer options objects (JsonlLogService sets PropertyNamingPolicy=CamelCase; ExtendedLogService relies on already-lowercase anonymous-type members) and different write paths (synchronous lock vs bounded Channel). Behavior is currently consistent, but a rotation bugfix (e.g. the delete-vs-overwrite race under FileShare.Delete readers) applied to one will silently miss the other — classic cross-model duplication.

**Suggested fix:** Extract a RotatingJsonlWriter(folder, filenameFunc, maxBytes, maxFiles) used by both services so rotation, size limits, and serializer options live in one place.

### [consistency] Local-time vs UTC drift in user-facing timestamps and migration scheduling

`Services/MigrationService.cs`

Audit and trace events consistently use DateTime.UtcNow ISO-8601 ('ts' in AuditService/OperationTraceService/ExtendedLogService), but operational code mixes in DateTime.Now with unspecified Kind: MigrationService passes 'DateTime.Now.AddHours(-1)' as Set-MigrationBatch -CompleteAfter and names batches by local date; JsonlLogService/ExtendedLogService pick the log FILE by local date while writing UTC 'ts' inside, so events written 20:00-24:00 local (EDT) carry a UTC ts dated the next day relative to their filename — an AdminEventLog date query by filename can miss or mislabel entries near midnight. Each usage is individually defensible, but the mixture is cross-session drift and makes time-window correlation (audit vs migration vs extended log) error-prone.

**Suggested fix:** Document one rule (UTC for stored/compared timestamps, local only for display/file naming) in AGENTS.md or the Constitution, and convert MigrationService scheduling values to DateTime.UtcNow with explicit Kind.

### [consistency] Three error idioms inside one service (throw vs result-object) in Comms10k

`Services/Comms10kService.cs`

Within the single Comms10kService, the three public operations use different failure contracts written in different sessions: GetMembersAsync throws InvalidOperationException for 'not configured' and 'credentials unavailable'; ResolveEmailsAsync returns Comms10kResolveResult{Success=false} for the same two conditions; ExecuteReplaceAsync returns Comms10kUpdateResult{Success=false}. The Razor caller must remember which method throws and which returns. This mirrors the wider repo split (PermissionResult-returning Exchange services vs throwing Graph services vs null-returning MfaReset) but is the clearest single-file example and the cheapest to normalize before more modules copy it as a template.

**Suggested fix:** Pick one contract per service (result objects are the dominant repo pattern for mutations) and convert GetMembersAsync's config/credential failures to a result type; record the chosen convention in docs/AdminModuleDeveloperGuide.md.

### [ps-scripts] Installer hardcodes duplicate copies of ModuleCatalog policy aliases and module IDs

`tools/Install-ExchangeAdminWeb.ps1`

Get-ConfigurablePolicyAliases (28 aliases) and New-ModuleEnablementSeed (20 module IDs) are hand-maintained mirrors of Modules/ModuleCatalog.cs. I verified they are currently in sync (every alias including MailboxPermissionsOnPrem, MigrationCreate/Manage, ADAttributeEditorLevel1-3, EventLog/UndoAuditedActions exists in the catalog, and every non-system module ID is seeded). But nothing enforces the sync: the next module added by a different session/model will be silently missing from the seeded sectionaccess.json and modules-enabled.json. Fail-closed permissions make the security outcome safe (the new module is denied/absent), but fresh installs will mysteriously lack the module until someone hand-edits config — a classic cross-session drift trap in an AI-built repo.

**Suggested fix:** Add an xUnit test in ExchangeAdminWeb.Tests that parses the installer script's two lists and asserts they equal ModuleCatalog's GetConfigurablePolicyAliases() and non-system module IDs — or generate the seeds at install time from a `dotnet run`-emitted catalog dump.

### [ps-scripts] Installer creates config/appsettings and IIS app before the build succeeds (side-effect ordering)

`tools/Install-ExchangeAdminWeb.ps1`

Known Failure Class 1 (side-effect ordering): on a fresh install, appsettings.json, all five config seed fragments, ACLs, the app pool, and the IIS web application (with anonymous auth disabled) are all created before `dotnet publish` runs (line 617) and before any binary reaches the publish folder. If the build fails (Write-Fail throws at line 622), the machine is left with a live IIS application pointing at a folder containing config but no app — broken 502s for anyone hitting the URL, and deploy.ps1's mode detection (`$isUpgrade = (Test-Path $configPath)`) would now classify this never-deployed instance as an UPGRADE. Re-running the installer heals it (Write-AppSettings/Write-JsonFileIfMissing preserve existing files), so impact is low, but ordering build-first would make the failure path side-effect-free.

**Suggested fix:** Move the dotnet publish step to the top of the mutation sequence (it writes only to the staging folder), so a failed build leaves no IIS/config/ACL state behind.

### [catalog] ConferenceRooms 'leave blank' fields are marked Required, producing a false 'not configured' warning

`Modules/ModuleCatalog.cs`

ModuleConfigField defaults Required to true (ModuleConfigField.cs:15). Several ConferenceRooms fields whose descriptions explicitly permit blank values omit Required: false — OnPremDelineaSecretId ('Leave blank if cloud-only', line 332), RestrictedMailTip ('Leave blank for built-in default.', line 342), ExecMailTip (line 343). Consequence: ModuleConfigService.IsModuleConfigured (lines 82-86: `.Where(f => f.Required).All(... !string.IsNullOrWhiteSpace(val))`) returns false for a legitimately configured cloud-only deployment, so ModuleConfig.razor:161-165 shows 'This module has required fields that are not yet configured' and renders red required asterisks (ModuleConfig.razor:180) on fields the descriptions say may be blank. Operators get a contradictory signal between the field description and the required marker/warning.

**Suggested fix:** Add Required: false to OnPremDelineaSecretId, RestrictedMailTip, and ExecMailTip (and audit the other ConferenceRooms group/contact fields against the fail-closed preflight list, which already treats only the eight RequiredGroupConfigKeys as mandatory).

### [catalog] Catalog Validate() lets a system module silently shadow a non-system module's policy alias

`Modules/ModuleCatalog.cs`

The duplicate-alias guard exempts system modules: `if (!policyAliases.Add(m.MainPermission.PolicyAlias) && !m.IsSystemModule) throw`. If a future system module reuses an alias already taken by a non-system module, construction succeeds. Then in ConfigureAuthorizationPolicies system modules are registered first (line 63) and the non-system loop skips already-registered aliases (line 76: `if (registered.Add(mainAlias))`), so the non-system module's section-access-driven dynamic policy is silently replaced by an AdminGroups-gated static policy. The failure direction is stricter (admin-only), so it is not an escalation, but it is a silent behavior change with no validation error — exactly the kind of cross-session trap this catalog's Validate() exists to prevent. Today only AdminSettings is a system module, so the hole is latent.

**Suggested fix:** Make the duplicate-alias check unconditional (remove the `&& !m.IsSystemModule` exemption); nothing in the current catalog relies on alias sharing.

### [catalog] ExchangeOnline MainPermission registers a permanently unsatisfiable dynamic policy

`Modules/ModuleCatalog.cs`

The config-only ExchangeOnline module declares MainPermission ('Access','ExchangeOnline'), and ConfigureAuthorizationPolicies registers it as a dynamic section-access policy because the non-system loop (line 73) does not exclude IsConfigOnly modules. But GetConfigurablePolicyAliases (line 40) excludes config-only modules, so Admin Settings can never assign groups to section 'ExchangeOnline'; GroupAuthorizationHandler then always denies (groups.Length == 0 -> context.Fail). The actual page is intentionally gated by AdminSettings instead (ExchangeOnlineConfig.razor:4, asserted by ModuleCatalogTests.Catalog_ConfigOnlyModules_UseAdminSettingsPolicy). Fail direction is closed, so this is not a vulnerability — but the descriptor advertises a permission that does not exist in practice, and the dead policy invites a future page to use it and be mysteriously denied.

**Suggested fix:** Skip IsConfigOnly modules in the dynamic-policy registration loop (or register their alias as an AdminGroups-backed policy matching actual page behavior), and add a comment on the descriptor that config-only modules are governed by the AdminSettings policy.

### [catalog] FallbackPolicy reintroduces an AllowedGroups gate that is inert and misleading

`Modules/ModuleCatalog.cs`

The Constitution says 'Do not reintroduce a global AllowedGroups base gate', yet ConfigureAuthorizationPolicies still builds a policy from Security:AllowedGroups and installs it as options.FallbackPolicy. Mechanism check: the fallback applies only to endpoints with no authorization metadata; the single mapped endpoint (Program.cs:133-135 MapRazorComponents(...).RequireAuthorization()) carries metadata, and Blazor page-level [Authorize(Policy=...)] is enforced by AuthorizeRouteView, so the fallback gates nothing in practice — module access correctly does not stack an AllowedGroups requirement. The residue is misleading in two ways: Program.cs:30 still reads Security:AllowedGroups as if load-bearing, and GroupAuthorizationHandler logs 'Security:AllowedGroups is empty — denying all access until configured' (line 77) for a section that never executes, which will send a future operator chasing a non-existent gate.

**Suggested fix:** Either remove the GroupPolicy/FallbackPolicy and the Security:AllowedGroups read entirely, or record in docs that AllowedGroups exists solely as a defense-in-depth fallback for future non-Blazor endpoints and is intentionally not a module gate.

### [config] SectionAccessService swallows catalog failure, collapsing the fail-closed set to empty (fail-open direction)

`Services/SectionAccessService.cs`

BuildFailClosedSet wraps catalog enumeration in a bare 'catch { }'. If ModuleCatalog construction or enumeration ever threw here, _failClosedSections would be empty, and GetGroupsForSection (line 62) would then return _allowedGroups for every section when no section-access source exists — i.e. the failure direction is open, contrary to the fail-closed design. Practically this is near-unreachable today because Program.cs:36 constructs ModuleCatalog first and would crash startup, but the swallowed catch is exactly the pattern the Constitution's never-do list targets and will bite if the catalog ever gains config/IO-dependent initialization. Note the rest of this service is correct: corrupt or section-missing fragment files fail closed (lines 106-117), and legacy appsettings fallback happens only when the fragment file is absent (File.Exists guard at line 97).

**Suggested fix:** Remove the try/catch (let construction fail loudly at startup) or, if a guard is wanted, set a _catalogLoadFailed flag that makes GetGroupsForSection return Array.Empty<string>() for all sections — failing closed instead of open.

### [config] Section access cache never expires — external edits to sectionaccess.json require an app restart

`Services/SectionAccessService.cs`

ReadSectionAccess caches the parsed fragment indefinitely (_cache is only cleared by SaveSectionAccess in the same process, line 166). If sectionaccess.json is edited out-of-band — hand-fixed after a corruption incident, or updated by the dev-to-prod config promotion flow — the singleton service keeps serving the stale (possibly fail-closed-empty) data until the app restarts, with no log hint. By contrast, module config files are re-read on every access. This is a behavior asymmetry an operator fixing a corrupt fragment will trip over: the file is fixed but access stays denied.

**Suggested fix:** Add a short TTL (e.g. 30-60s) or a FileSystemWatcher/last-write-time check to invalidate _cache when the fragment changes on disk, and document that fixing a corrupt fragment takes effect within that window.

### [docs-drift] README 'Project Structure' omits the Modules/ directory and module catalog — the architecture the whole app is built on

`README.md`

The Development > Project Structure tree predates the modularization: it shows no Modules/ folder (home of ModuleCatalog.cs, AdminModuleDescriptor.cs, ModulePermission.cs — the 'descriptor-based architecture' both AGENTS.md and the README intro lead with), and its Pages example names only the three original pages despite ~21 module pages existing. The Services list also omits the shared infrastructure newer docs treat as canonical (ModuleConfigService, ModuleCredentialService, ProtectedPrincipalService, DelineaService, ConferenceRoomService, etc.). Harmless for operators but actively misleading for a new developer orienting via the README, and a classic cross-session artifact: the intro paragraph was updated to '21 modules' while this tree never was.

**Suggested fix:** Refresh the tree: add Modules/ (catalog + descriptor contract), note Components/Pages holds one page per module, and either list the current key services or replace the per-file list with a pointer to docs/AdminModuleDeveloperGuide.md.

### [docs-drift] AdminModuleDeveloperGuide 'Last verified' stamp is five releases behind current code

`docs/AdminModuleDeveloperGuide.md`

The guide self-declares its verification baseline as host 2.3.0 at commit 6e2fbb6 (2026-06-05); the app is now 2.3.5 with seven commits since, including module-architecture-adjacent changes (Conference Rooms config extraction with fail-closed required-groups preflight, CI wiring, robocopy /XD deploy fix). Spot-checks of the guide against current code came back clean — the preserved-config list matches tools/promote-dev-to-prod.ps1:474-478 exactly, tools/validate-module-package.ps1 and deploy-pipeline.ps1 exist as described — so this is honest-but-stale metadata rather than wrong content. Worth a re-verify pass and stamp bump before the production release so the stamp stays trustworthy, since this guide is the document new module authors are told to follow.

**Suggested fix:** Re-verify the guide against 2.3.5 (the Conference Rooms fail-closed required-groups pattern is a good candidate to cite as the worked example of 'missing/corrupt config fails closed') and update the Host baseline / Last verified header.

### [blazor] ExoConnectionPool.CleanupIdle empties the bag during sweep, racing concurrent borrows into expensive reconnects

`Services/ExoConnectionPool.cs`

The 5-minute cleanup timer drains every pooled runspace out of the ConcurrentBag into a local snapshot, then re-adds the non-idle ones. During that window a concurrent BorrowAsync's '_available.TryTake' finds the bag empty and falls through to CreateConnected, paying a full multi-second Connect-ExchangeOnline even though a warm connection existed and is about to be re-added (the re-added one then sits idle, so the pool can transiently hold more live EXO sessions than the 5 borrow slots imply). Also, DestroyRunspace's synchronous Disconnect-ExchangeOnline runs on the timer thread per idle item — fine, but the whole sweep is unsynchronized with borrows. Not a correctness bug (generation checks keep stale config out), just wasted tenant connections and avoidable user-visible latency spikes aligned with the 5-minute timer.

**Suggested fix:** Iterate without draining: TryTake one item at a time and immediately re-add keepers, or guard the sweep and borrow's TryTake with a small lock so borrows never observe a fully-drained bag while warm connections exist.

### [blazor] TicketNumberInput is dead code with a never-invoked Dispose and broken two-way binding if ever revived

`Components/Shared/TicketNumberInput.razor`

TicketNumberInput is referenced by no page (repo-wide grep finds only its own file). It carries three latent bugs that will bite whoever wires it up later: (1) it defines 'public void Dispose()' but the file has no '@implements IDisposable' directive (it starts with only '@inject ServiceNowService ServiceNow'), so Blazor would never call Dispose and the validation timer would leak per circuit; (2) 'OnTicketNumberChanged' (the debounced-validation path that creates validationTimer) is never referenced — the input uses '@bind="TicketNumber"' which writes the parameter property directly; (3) consequently the 'TicketNumberChanged' EventCallback is never invoked, so a parent using '@bind-TicketNumber' would never receive typed values and any parent re-render would reset the field. Classic cross-session AI artifact: a component half-migrated between two binding styles, then abandoned.

**Suggested fix:** Delete the component, or fix it before reuse: add '@implements IDisposable', replace '@bind="TicketNumber"' with explicit value/@oninput wiring through OnTicketNumberChanged so TicketNumberChanged actually fires, and keep local state in a private field rather than writing the [Parameter] property.

### [ci-release] Failed upgrade leaves staging folder containing the dev appsettings.json on the prod server

`deploy.ps1`

In the upgrade path, `dotnet publish` (line 360) copies the local working tree's appsettings.json — gitignored precisely because it holds real environment values — into `$PublishPath.staging.$timestamp`. The staging cleanup at line 469 sits after the try/finally block, so if the deploy throws inside the try (e.g. line 394 `throw "robocopy failed with exit code $LASTEXITCODE"`), the finally restarts the app pool, the exception then propagates and terminates the script under $ErrorActionPreference='Stop', and line 469 is never reached. Failed upgrades therefore accumulate `ExchangeAdminWeb.staging.<timestamp>` folders (each with a live-config appsettings.json) next to the prod site. This is the side-effect-ordering failure class (cleanup unreachable on the failure path). The robocopy /XF exclusion only protects the destination copy step; the staging artifact itself persists.

**Suggested fix:** Move `Remove-Item $StagingPath` into the finally block (or a trap/outer finally) so staging is removed on both success and failure, and/or publish with an explicit item exclusion so appsettings*.json never enters staging at all (e.g. set `<CopyToPublishDirectory>Never</CopyToPublishDirectory>` for appsettings files or delete them from staging immediately after publish).

### [ci-release] Pester CI step will fail once tests/ps exists: Install-Module without -SkipPublisherCheck

`.github/workflows/ci.yml`

The Pester step is currently dead code (tests/ps does not exist in the repo, so the `Test-Path` guard takes the else branch and prints a message). The day Pester tests are added — which AGENTS.md requires for any new .ps1 logic — the step activates and will likely fail before running a single test: on GitHub Windows runners, the inbox Pester 3.4.0 under C:\Program Files\WindowsPowerShell\Modules (visible to pwsh via PSModulePath) makes `Install-Module Pester -Force` error with the well-known Authenticode publisher-check conflict ('...has authenticode signature different from previously-installed module...') unless `-SkipPublisherCheck` is supplied. Since the whole workflow has never been observed running, this latent failure will surface at the worst time: when someone first lands PowerShell test coverage.

**Suggested fix:** Change to `Install-Module Pester -Force -Scope CurrentUser -MinimumVersion 5.5 -SkipPublisherCheck`. Optionally guard with a check for an already-adequate installed Pester 5.x (runner images often preinstall it) to skip the install entirely.

### [ci-release] CI omits the dotnet format check that AGENTS.md lists as standard verification; PSScriptAnalyzer warnings pass silently

`.github/workflows/ci.yml`

AGENTS.md's Verification section names `dotnet format ExchangeAdminWeb.csproj --verify-no-changes --no-restore` as part of the standard verification set, but the workflow has no format step, so formatting drift between sessions/models (a stated concern for this AI-built codebase) is unenforced. Separately, the PSScriptAnalyzer step prints warnings but fails only on Severity 'Error' (`if ($results | Where-Object Severity -eq 'Error')`), while the repo's documented lint command is unfiltered `Invoke-ScriptAnalyzer -Path . -Recurse`; warnings will scroll past unread in CI logs forever. These are gaps, not bugs — the steps that do exist are otherwise sound (windows-latest is correct for net10.0-windows10.0.17763.0, setup-dotnet '10.0.x' is a valid pin, and the master branch trigger matches the repo's default branch).

**Suggested fix:** Add a step `dotnet format ExchangeAdminWeb.csproj --verify-no-changes` to the build-test job (after restore/build). Decide a warning policy for PSScriptAnalyzer: either fail on warnings too, or record in AGENTS.md that CI intentionally gates only on Error severity so the two sources stop disagreeing.

### [test-gaps] AGENTS.md still claims ci.yml is misplaced in repo root (stale-reference drift)

`AGENTS.md`

AGENTS.md's Verification section states there is no working CI because ci.yml sits in the repo root, but commit 00054a8 moved it to .github/workflows/ci.yml (confirmed on disk and in .agents/state.md, which records the move). This is Known Failure Class #3 (stale references): a future agent following AGENTS.md will wrongly conclude no CI exists. Ironically the deeper claim is still accidentally true — CI exists but its test step runs zero tests (see the no-solution-file finding) — which makes the stale wording actively misleading about *why* CI cannot be trusted.

**Suggested fix:** Update AGENTS.md to: CI exists at .github/workflows/ci.yml but must not be trusted until a run is observed executing a nonzero test count (and fix the dotnet test target so it does).

### [test-gaps] No vacuous tests found in sampled suite (positive note) — but no enforcement that new services get tests

`ExchangeAdminWeb.Tests/EmergencyDisableServiceTests.cs`

Sampled four test files for tautology (SectionAccessServiceTests, ExchangeServiceBaseInvokeTests, AuditServiceTests, EmergencyDisableServiceTests): all are substantive — they exercise real file I/O, real in-process PowerShell runspaces, and real service graphs with only loggers/HTTP substituted, and they assert ordering and fail-closed behavior (e.g. corrupt protected-principal config blocks before credential lookup). The gap is purely structural: roughly 30 of 46 services have no test file, and nothing (CI, analyzer, convention check) flags a new Service shipped without one, so the AGENTS.md rule is enforced only by reviewer memory.

**Suggested fix:** Once dotnet test actually runs in CI, add a coverage floor (coverlet is already referenced in the test csproj but never invoked with a threshold) or a simple convention test that asserts every catalog module's primary service type has at least one corresponding *Tests class.


## Refuted during verification (6)

Claims that did not survive adversarial re-reading of the code; recorded so they are not re-reported.

- **[creds] Graph modules fall back from GraphDelineaSecretId to DelineaSecretId, conflating credential purposes** — The quoted code is accurate — MfaResetService.cs:22/43, M365GroupManagementService.cs:32-33/55-56, and NamedLocationsService.cs:22/44 all contain the `GetValue(..., "GraphDelineaSecretId") ?? GetValue(..., "DelineaSecretId")` fallback, EmergencyDisableService.cs:271 reads only `GraphDelineaSecretId`, and the catalog entries match the finding's description. But the conclusion (Constitution violation / credential-purpose conflation) is refuted by documented intent in the repo and its history.

1) 

- **[protected] Transitive group-membership check fails OPEN when target DN cannot be resolved** — The quoted code is accurate (Services/ProtectedPrincipalService.cs:512-513 `if (string.IsNullOrEmpty(targetDn)) return (matches, false);`), but the claimed failure mechanism — fail-OPEN when lookup data is unavailable, contradicting the Constitution — is refuted by the actual control flow. (1) Genuine lookup unavailability cannot reach line 512: the sam lookup (lines 503-507) uses `-ErrorAction Stop` and is OUTSIDE the per-group try/catch, so DC-down/bad-credential errors throw out of CheckTrans

- **[failure-classes] AD Attribute Editor OU search-base boundary fails OPEN when module config is corrupt** — The quoted code is accurate in isolation — ADAttributeEditorService.cs:747 `if (searchBases.Length == 0 || string.IsNullOrEmpty(dn)) return true;` fails open, and ModuleConfigService.ReadModuleConfig:137-141 swallows parse errors returning an empty dictionary. However, the claimed end-to-end mechanism (corrupt config -> boundary silently disappears -> out-of-scope users editable) is unreachable. All three public entry points fetch credentials first from the SAME config file via ModuleCredentialS

- **[failure-classes] Comms10k member/resolve lookups conflate AD query failure with 'not found' and export blank emails** — The quoted code is accurate (Comms10kService.cs:81-90 does use `.AddParameter("ErrorAction", "SilentlyContinue")` with `var email = user?.Properties["EmailAddress"]?.Value?.ToString() ?? ""`, and lines 153-156 put empty filter results into `skipped`), but the claimed failure mechanism does not survive a control-flow trace. `-ErrorAction SilentlyContinue` only suppresses NON-terminating errors. The failure modes the finding cites — transient lookup failure, directory/server errors — are pipeline-

- **[consistency] Pooled EXO runspace returned with uncleared error stream in PermissionValidator** — The quoted code exists exactly as cited: Services/PermissionValidator.cs:358-363 reads "catch (Exception ex) when (ex.Message.Contains(\"couldn't be found\")) { ps.Commands.Clear(); ... _exoPool.Return(pooled); return members; }" with no ps.Streams.Error.Clear(). But the claimed mechanism — stale error records surviving on the runspace and polluting the next borrower's SnapshotErrorMessages / IsConnectionError scans in ExchangeServiceBase — is impossible, because the pool sanitizes streams on BO

- **[blazor] async void Timer callbacks in shared autocomplete components can crash the whole process** — The quoted code exists exactly as cited: ADIdentityAutocomplete.razor:87-90 has `_debounceTimer = new System.Threading.Timer(async _ => { await InvokeAsync(() => PerformSearch(newValue.Trim())); }, null, 300, Timeout.Infinite);`, Dispose() at :208-212 sets `_disposed` without the lambda checking it, and the same pattern appears in RecipientAutocomplete.razor:84-87, ADGroupAutocomplete.razor:80-83, and TicketNumberInput.razor:109-112. The async-void-on-Timer premise is also correct in the abstrac


## Dimension summaries

### auth
Authorization is largely well-built: all 21 operational pages declare catalog-matching [Authorize(Policy=...)] attributes enforced both at the SSR endpoint (MapRazorComponents().RequireAuthorization()) and via AuthorizeRouteView; granular policies correctly compose main+granular requirements; disabled parent modules cascade-disable dependents at the policy-handler level (GroupAuthorizationHandler lines 55-65 calling ModuleEnablementService.IsModuleEnabled which walks DependsOn), so direct-URL access to a disabled module's page is denied at runtime, not just hidden in nav; corrupt sectionaccess.json and corrupt modules-enabled.json both fail closed; and most mutating handlers (Migration, ConferenceRooms, TestAccountPool, Comms10k, MfaReset, EmergencyDisable, Dhcp, NamedLocations, M365, GroupManagement, LicensingUpdates, OutOfOffice, ADAttributeEditor, AdminSettings, ModuleConfig) re-check authorization immediately before writing. The serious gaps are concentrated in the oldest code: MailboxPermissions and CalendarPermissions cloud/bulk paths and AdminEventLog's undo execute mutations with only render-time checks (Constitution pre-write re-check violation, classic cross-model inconsistency), and the legacy Exchange modules' permissions are not FailClosed, so loss of config/sectionaccess.json (which has actually happened per commit 0021502) silently opens FullAccess granting and migration creation to every base AllowedGroups user. Secondary drift: a dead 'ExchangeOnline' policy contradicting the page's AdminSettings gate, a latent AllowedGroups FallbackPolicy plus unused AuthorizationCheck.razor, a swallowed catch that can empty the fail-closed set, and a never-invalidated section-access cache.

### creds
Credential isolation is largely sound: every Graph/AD/on-prem module resolves its own module-scoped Delinea secret ID (verified all 11 GetValue("<module>", "...DelineaSecretId") sites and all GetCredentialsBySecretIdAsync/GetSecretFieldsAsync callers), the shared protected-principal directory-read credential is explicitly named per the Constitution, Delinea auth failures surface only the parsed OAuth error code and HTTP status (never raw response bodies — GetOAuthErrorCode extracts only the "error" field), OperationTraceService records only exception type names and masks password/secret/token detail keys, AD password writes use SecureString via AddParameter, and the test-account password generator uses RandomNumberGenerator. The two significant problems found are a catalog/service config-key mismatch that makes ConferenceRooms' documented on-prem credential field dead (on-prem-mastered room writes fail), and a Graph→AD secret-key fallback in three modules that contradicts the Constitution's key-purpose separation and is inconsistent with EmergencyDisable. Secondary hygiene gaps: cleartext SMTP can carry SMTP auth and live test-account passwords, and SMTP/ServiceNow passwords live in plaintext appsettings rather than Delinea/Credential Manager.

### audit
Audited the full pipeline (AuditService → JsonlLogService → OperationTraceService, ClientInfo middleware/service, AdminEventLog) and sampled every listed mutating service/page for success+failure audit coverage, field fidelity, audit-failure isolation, and idiom consistency. The architecture is fundamentally sound — central JSONL audit with operation-ID correlation, per-row audits in bulk CSV loops (MailboxPermission, CalendarPermission, ConferenceRooms, TestAccountPool, LicensingUpdates), protection/auth/ticket denials audited in most modules, and JsonlLogService swallowing I/O failures so audit writes rarely fail an operation; MfaResetService aggregation, OutOfOffice, GroupManagement, DHCP, NamedLocations, and the ADAttributeEditor undo path checked clean. The significant problems are cross-model drift and missing failure-path audits: three modules misfile events under borrowed categories (EmergencyDisable and Comms10k as MigrationAction, LicensingUpdates mutations as Lookup), terminating Set-ADUser failures in ADAttributeEditor and failed Migration batch removals leave no audit record, EmergencyDisable pre-mutation failures are trace-only, settings mutations structurally cannot audit failure, and the audit IP comes from a static 1-hour-TTL cache that degrades to "Unknown". One latent isolation hole (JSON serialization outside the swallow-try) combined with unwrapped audit calls in MfaReset/MailboxPermissions could make a completed operation look failed.

### protected
I traced ProtectedPrincipalService and every listed mutating consumer. The service itself fails closed correctly on corrupt/missing config (LoadEffectiveConfig sets _configCorrupt and returns Failed), on missing/failed directory-read credentials (CheckGroupMembershipAsync returns checkFailed), and on group-expansion errors (expansionHadErrors && no matches => fail closed); group protection is genuinely transitive via the LDAP_MATCHING_RULE_IN_CHAIN OID; OU/search-base checks use comma-anchored DN suffix matches (not arbitrary substrings); and ADAttributeEditorService, ADAttributeEditorUndoService, TestAccountPoolService, EmergencyDisableService, MfaReset/EmergencyDisable/MailboxPermissions/OutOfOffice all re-check protection (or its PermissionValidator equivalent) and bind/re-read by GUID before write. The notable gaps: GroupManagementService's mutating methods have no in-service protection check at all — enforcement lives only in the Blazor page and is gated on the member string containing '@', a fail-open UI-only design that violates the 'recheck before the write / UI hiding is not security' invariant; the transitive check fails open when a target DN cannot be resolved; and an earlier name-matching implementation left dead helpers that are unit-tested while the real transitive path is not.

### failure-classes
Swept all emphasized bulk/multi-item services (Comms10kService, ConferenceRoomService + its CSV paths and the ConferenceRooms.razor bulk loops, LicensingUpdatesService, MigrationService, TestAccountPoolService + CleanupWorker, GroupManagementService, ADAttributeEditorService + UndoService, MailboxPermission/CalendarPermission bulk CSV, BulkOperationResult) plus the shared plumbing they depend on (ExchangeServiceBase Invoke/InvokeOptional/InvokeBestEffort, AuditService, JsonlLogService, ModuleConfigService, OperationTraceService). Found clean: success aggregation in the ConferenceRooms bulk loops (per-row results, X/N succeeded UI), the mailbox/calendar bulk CSV loops, TestAccountPool create/cleanup (per-item aggregation with compensating cleanup and audit), EmergencyDisableService (snapshot-before-mutation, all-steps-must-succeed aggregation), LicensingUpdates apply (re-read + GUID bind, per-row audit with throw-safe audit wrapper), JsonlLogService (audit write failures are swallowed internally so they cannot make completed operations look failed), and atomic temp-then-replace config writes. The seven findings are concentrated in the known failure classes: a dead failure-audit branch in ADAttributeEditor (failed Set-ADUser writes no audit at all), silently-swallowed permission removals reported as Success in CEO/restricted room conversion, fail-open OU scoping on corrupt ADAttributeEditor config, a silent corrupt-config default fallback in LicensingUpdates, error/not-found conflation feeding Comms10k's destructive full-membership replace, the ADWS 5000-member Get-ADGroupMember ceiling on the 10k group, and lost partial-success detail in cloud bulk mailbox-permission rows.

### consistency
Reviewed all 46 files in Services/ plus Models/, Modules/ModuleCatalog.cs, and the shared base class, specifically hunting cross-session/cross-model duplication drift. The shared ExchangeServiceBase is in good shape post-c8ed096, audit timestamps are uniformly UtcNow, atomic temp-then-replace writes are used consistently for runtime config saves, LDAP escaping is properly shared (ADDirectorySearchService reuses ProtectedPrincipalService.EscapeLdapFilter), and PermissionValidator/ProtectedPrincipalService fail closed correctly. The real drift clusters are: (1) a null-swallowing GraphTokenClient whose five copy-pasted per-service factories diverge in secret-key fallback and error idiom — including an MFA reset path that reports blanket success when Graph calls fail (the worst single finding); (2) shared-base hardening (pooled-runspace error clearing, on-prem connect retries) that did not propagate to hand-rolled copies in PermissionValidator and TestAccountPoolService; (3) a 10-way duplicated PSCredential factory and twin JSONL rotation engines that will absorb future fixes unevenly. One Constitution-level gap: the legacy config migration writes module config non-atomically, and the old migration-eligibility group check still uses substring DN matching with ambient credentials.

### ps-scripts
Reviewed all six ops scripts (tests/ps/ does not exist) plus cross-checks against Program.cs, ModuleCatalog.cs, and the Constitution. The recent robocopy /XD fix is verified correct in all three /MIR call sites (bare 'logs'/'config' names, /XF appsettings*.json, exit >= 8 checked via throw), promote-dev-to-prod.ps1 is the strongest script (real dry-run, atomic temp+validate+move config writes, path-safety guards, backup+rollback), and the installer's hardcoded module/alias seeds currently match the catalog exactly. The main problems cluster in the oldest script and its wrapper: deploy-pipeline.ps1 applies robocopy's >=8 exit threshold to deploy.ps1 (whose Write-Fail does `exit 1`), so failed deployments print 'Dev deployment complete'; deploy.ps1 has no plan mode and the pipeline hardcodes Apply+IUnderstandThisOverwritesProd, foreclosing the Constitution-required dry-run on the documented prod-promotion path; icacls exit codes are unchecked everywhere; and the upgrade-path appsettings rewrite is non-atomic, unlike its two sibling scripts.

### catalog
Module catalog coherence is largely solid: all 21 catalog routes have matching @page routes and matching catalog-backed [Authorize] policies (verified directly and enforced by ModuleCatalogTests, including the intentional AdminSettings gate on the config-only ExchangeOnline page and the legacy /message-trace alias), the DependsOn cascade is correctly enforced at both nav (ModuleEnablementService recursion) and authorization (GroupAuthorizationHandler disabled-module deny) layers, granular permissions are all consumed with pre-write re-checks, and every DI-consumed service is registered in Program.cs. The standout real bug is Conference Rooms: the catalog/UI field OnPremDelineaSecretId is never read because the credential path hardcodes DelineaSecretId, so the on-prem Set-RemoteMailbox path cannot ever obtain credentials configured through the UI. Secondary issues are config-key drift (hidden ExcludedUsers key, Graph-secret fallback to DelineaSecretId in three modules), confirmed analog.com/ADI/ADGT/hybrid1 environment hardcodes in the catalog, UI, and room-type logic, and a few low-severity latent traps (system-module alias shadowing, dead PasswordVault helper, inert AllowedGroups fallback policy).

### config
Config handling is largely well-built: SaveModuleConfig, ModuleAdminService, ModuleEnablementService, SectionAccessService, and EmergencyDisable snapshots all use the temp-file + File.Replace atomic pattern (two even re-validate the temp file before swap); ModuleEnablementService fails fully closed on a corrupt enablement file (all modules disabled); SectionAccessService fails closed on corrupt/section-missing fragments and only falls back to legacy appsettings when the fragment file is absent; and the ConferenceRooms example JSON's keys exactly match the catalog ConfigFields and the keys ConferenceRoomService.Cfg reads, with the FindMissingRequiredGroups preflight failing room-type operations closed when groups are blank. The serious problems are (1) the ConferenceRooms catalog/example expose 'OnPremDelineaSecretId' while ModuleCredentialService only ever reads 'DelineaSecretId', so on-prem Set-RemoteMailbox credentials can never be configured for that module, and (2) ModuleConfigService's reader swallows corruption into an empty dictionary, making fail-closed opt-in per caller — ProtectedPrincipalService's legacy-exclusion path consequently fails open on a corrupt MailboxPermissions config (where PermissionValidator handles the identical data correctly), and the generic ModuleConfig admin page will render a corrupt file as blank and overwrite it on save. Lesser issues: the one non-atomic config write (legacy migration, which also never repairs a truncated output), a case-sensitivity inconsistency in deserialized config keys, and whole-file last-write-wins between concurrent admin sessions.

### docs-drift
Docs-drift review of the release-bound docs set. The worst drift clusters in agent guidance: AGENTS.md and .agents/repo-map.json both still assert there is no working CI with ci.yml misplaced in the repo root, while .github/workflows/ci.yml exists, triggers on master, and has two observed successful runs; .agents/state.md staleness is confirmed (landed/pushed work described as uncommitted/unpushed, missing commit 0021502, CI described as never-run) plus docs/AdminModuleSpec.md's header still claims a v1.5.4 baseline against app 2.3.5 with a descriptor example missing five current fields including the Constitution-mandated Version. README drift includes a nonexistent 'Workspace' Conference Rooms booking template (real enum: Standard/Video/Restricted/Exception/CEO/Executive), install instructions that route everyone to the ADI-specific deploy.ps1 with no mention of the generic installer, and a pre-modularization project-structure tree; three plan docs lack the Status header AGENTS.md's lifecycle rule depends on. Verified clean: all Implemented Conference Rooms plans match code (module 2.0.4 in catalog, FindMissingRequiredGroups present, example config committed), the spec's ModulePermission record/EventLog/icon/config-path claims match code exactly, the developer guide's preserved-config list matches promote-dev-to-prod.ps1 verbatim, deploy robocopy exclusions match the documented /XF appsettings*.json + /XD logs,config invariant, and decisions.md is current.

### blazor
Blazor Server hygiene is better than typical AI-built apps: DI lifetimes are largely sound (no captive scoped-in-singleton dependencies found across all 20 singleton services; singleton module services hold no per-user mutable state; the background worker correctly creates scopes), pool borrow/return paths in ExchangeServiceBase and PermissionValidator release exactly once via try/finally, JsonlLogService and ModuleConfigService writes are locked and atomic, JS interop occurs only in event handlers (never during prerender), and pages use Task-returning handlers almost everywhere. The standout risk is audit-record integrity: client IP flows through a static username-keyed cache with a 1-hour TTL and last-write-wins semantics, so long-lived circuits log 'Unknown' and concurrent sessions of one account can cross-attribute IPs. Secondary issues are process-stability (async void timer callbacks in the shared autocompletes), per-circuit UI freezes from synchronous Connect-ExchangeOnline on the dispatcher, two fire-and-forget Enter handlers that never re-render, and cross-user contention where autocomplete typing consumes the shared 5-slot EXO pool and a global 1-at-a-time AD search lock.

### ci-release
CI and release readiness has one headline defect on each side. The never-run CI workflow is structurally green-but-vacuous: with no solution file, both `dotnet build` and `dotnet test` at the repo root resolve only to ExchangeAdminWeb.csproj, so the entire test suite is never compiled or executed (empirically reproduced on SDK 10.0.300: zero output, exit 0); runner OS, SDK pin, and master trigger are otherwise correct. On the release path, deploy-pipeline.ps1 applies the robocopy `-ge 8` convention to deploy.ps1, whose Write-Fail is `exit 1`, so a failed dev deploy prints "Dev deployment complete"; the -Prod path also hardcodes away the promote script's dry-run and overwrite-confirmation safeguards, and fresh installs prompt/validate config values (CloudTargetDomain, OnPremTargetDomain) that no code in the repo reads. Checked clean: csproj version triple (2.3.5 / 2.3.5.0 / 2.3.5.0) is consistent with nullable enabled and no dev-only Release leakage; appsettings.json.sample and launchSettings.json contain only placeholders (no real hostnames/secrets); .gitignore correctly excludes appsettings*.json and logs; robocopy exclusions for appsettings*.json/config/logs are present at both deploy.ps1 call sites; all test-stack NuGet versions (xunit.v3 3.2.2, MS.NET.Test.Sdk 18.5.1, coverlet.collector 10.0.0) exist in the local restore cache; promote-dev-to-prod.ps1 fails via throw with proper robocopy exit-code handling and a real dry-run mode.

### test-gaps
The ~25 existing test files are genuinely good — sampled tests (SectionAccessService, ExchangeServiceBase.Invoke, AuditService, EmergencyDisable) are substantive, assert fail-closed ordering, and are not vacuous. But the harness around them is broken: there is no solution file, so both the documented `dotnet test` command and the new CI workflow resolve to the app csproj (which excludes the tests) and never execute the suite, and tests/ps/ does not exist, leaving all 2,439 lines of deploy/install/promote PowerShell with zero Pester coverage despite the repo rule and a fresh deploy-bug fix. Coverage itself is bimodal: roughly 30 of 46 services are untested, including the single authorization gate (GroupAuthorizationHandler), the entire Delinea/Graph credential chain, ModuleConfigService (whose untested legacy-migration path also writes config non-atomically), and every EXO/Graph mutating service including bulk-CSV aggregation loops that match the repo's documented success-aggregation failure class.
