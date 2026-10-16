# Repository audit - 2026-10-16

Saved at the owner's request from the completed read-only audit report delivered in chat.

- Audited local `master`: `34bac0872e0131ebeef595217590b03beeb3eee4`.
- Both remote `master` refs at audit time: `eb0d19122f4921eca66e4300efe0605403214e23`.
- Six agents reviewed security, directory operations, cloud/mail, UI/data handling, jobs/storage, and deployment.
- Source links and line numbers refer to the audited revision.

**Eight high-priority findings and 32 medium-priority findings. No files changed during the audit.** All 855 tracked files retained identical fingerprints; HEAD remained unchanged, the working tree stayed clean, and `git diff --check` passed. This report and its state pointer were saved afterward at the owner's request.

The before/after aggregate tracked-file SHA-256 was `fa2cb3b3480a855646616f7979c168544ad69a3ab1654c11e11907d7277c7e00`. The manifest used `git ls-files` order, with each relative path, a NUL, the lowercase SHA-256 of its contents, and a newline; the aggregate hashes the UTF-8 manifest.

Findings are based on source tracing and relevant API documentation. Tests, builds, browser workflows, and live infrastructure were not exercised. Fixes remain unstarted; this report does not authorize implementation.

These eight findings deserve priority:

1. **Delegated administration can authorize the wrong domain's group.** A grant for `FOREIGN\Ops` also checks bare `Ops`, potentially accepting a member of `HOME\Ops`. The group picker also discards domain information. This can expose module configuration and the AD attribute allowlist. Store and compare validated group SIDs. [Code: ModuleAdminService.cs:31](../Services/ModuleAdminService.cs#L31). [Windows role matching](https://learn.microsoft.com/en-us/dotnet/api/system.security.principal.windowsprincipal.isinrole?view=net-10.0).

2. **Mailbox aliases can bypass protected-user rules.** With an address-only protection configuration, the validator skips identity resolution. A secondary alias can therefore pass while its primary address is protected, allowing Mailbox, Calendar, or Out of Office changes by a non-servicer. Check and write the same resolved identity. [Code: PermissionValidator.cs:163](../Services/PermissionValidator.cs#L163).

3. **Foreign-domain protected membership is checked in the wrong domain.** The transitive membership query omits `Server` and `SearchBase`. A foreign object can produce a clean "no match" in the host domain and be treated as unprotected by Group Management. Resolve authoritative directory scope and fail closed when visibility is uncertain. [Code: ProtectedPrincipalService.cs:904](../Services/ProtectedPrincipalService.cs#L904). [AD query defaults](https://learn.microsoft.com/en-us/powershell/module/activedirectory/get-adobject?view=windowsserver2025-ps).

4. **Self-Service Groups ignores explicit denies applying through group membership.** An explicit group-level deny followed by a direct user allow is accepted because the checker discards every trustee except the caller's individual SID. The privileged module credential then performs the write. Evaluate applicable token-group denies and ACE ordering. [Code: SelfServiceGroupService.cs:1359](../Services/SelfServiceGroups/SelfServiceGroupService.cs#L1359). [Windows access checking](https://learn.microsoft.com/en-us/windows/win32/secauthz/how-dacls-control-access-to-an-object).

5. **Lockout remediation can log off the wrong account.** When enabled, it guards a directory identity but dispatches by bare session username. Guarding `A\alex` can therefore log off `B\alex` or a local `alex`, potentially bypassing protection and losing unsaved work. Verify the actual session owner's domain/SID before logoff. [Code: AccountLockoutRemediationService.cs:727](../Services/AccountLockoutRemediationService.cs#L727).

6. **Concurrent Exchange borrows can use the previous organization's connection.** The pool publishes its new configuration marker before incrementing its retirement generation. A second borrower can read the new configuration yet receive an old connection and execute against the previous organization. Configuration identity and generation need one consistent transition. [Code: ExoConnectionPool.cs:389](../Services/ExoConnectionPool.cs#L389).

7. **Out of Office can write a different mailbox from the one checked.** The mailbox remains editable during awaited protection validation. Changing A to protected B during that wait makes the later write read B, although only A passed protection. Capture the request and acquire the busy guard before awaiting. [Code: OutOfOffice.razor:301](../Components/Pages/OutOfOffice.razor#L301).

8. **Conference Rooms jobs continue after the module is disabled.** Each row checks current section access, but not ConferenceRooms enablement. With ExchangeOnline still enabled, queued jobs and subsequent rows continue changing rooms. Recheck effective module enablement before dispatch. [Code: ConferenceRoomBulkProcessor.cs:78](../Services/Jobs/ConferenceRoomBulkProcessor.cs#L78).

The following findings are medium priority:

| # | Finding and consequence | Evidence |
|---|---|---|
| 9 | **Undo can modify a replacement user.** It resolves the old audit UPN without the original object GUID. Reassigned UPNs with matching attribute values pass conflict checks. | [ADAttributeEditorUndoService.cs:177](../Services/ADAttributeEditorUndoService.cs#L177) |
| 10 | **M365 Add uses UPN/email where an object ID is required.** Normal UI input becomes an invalid `directoryObjects/{UPN}` reference. | [M365GroupManagementService.cs:262](../Services/M365GroupManagementService.cs#L262); [Graph API](https://learn.microsoft.com/en-us/graph/api/group-post-members?view=graph-rest-1.0) |
| 11 | **MFA phone deletion ordering is ineffective.** Every phone maps to `Phone`, so primary mobile may be deleted before alternate mobile and remain behind after rejection. | [MfaResetService.cs:140](../Services/MfaResetService.cs#L140); [Graph API](https://learn.microsoft.com/en-us/graph/api/phoneauthenticationmethod-delete?view=graph-rest-1.0) |
| 12 | **MFA reset loses completed work when a later deletion throws.** The page substitutes a generic failure with default counts, omitting known successful deletions. | [MfaResetService.cs:114](../Services/MfaResetService.cs#L114) |
| 13 | **MFA results follow the editable UPN instead of the listed user.** Changing A to B relabels A's methods and can audit A's method types for B's reset. | [MfaReset.razor:50](../Components/Pages/MfaReset.razor#L50) |
| 14 | **Searching during an attribute save corrupts attribution.** Save(A) can finish while Search(B) replaces page state; the message/email names B and A's values populate B's display. | [ADAttributeEditor.razor:543](../Components/Pages/ADAttributeEditor.razor#L543) |
| 15 | **Licensing Cancel clears state while Apply continues.** Completion dereferences the cleared preview, suppressing the required notification after the backend mutation. | [LicensingUpdates.razor:84](../Components/Pages/LicensingUpdates.razor#L84) |
| 16 | **Named Location editing retains another location's countries.** The picker initializes once; switching A to B and applying can save A's country selection into B. | [CountryCodePicker.razor:47](../Components/Shared/CountryCodePicker.razor#L47) |
| 17 | **Failed collection reads appear complete.** M365 members/owners can become empty or truncated after HTTP failure; Named Locations silently returns partial continuation results. | [M365GroupManagementService.cs:298](../Services/M365GroupManagementService.cs#L298); [NamedLocationsService.cs:59](../Services/NamedLocationsService.cs#L59) |
| 18 | **Failed calendar delegation reads display "No calendar delegates."** The exception is consumed and the overall report is recorded as successful. | [DelegationReportService.cs:72](../Services/DelegationReportService.cs#L72) |
| 19 | **Mixed lockout batches drop refused users from final results.** Allowed users execute, but protected/unresolved refusals disappear from totals and the summary. | [AccountLockoutRemediationService.cs:366](../Services/AccountLockoutRemediationService.cs#L366) |
| 20 | **Event-log truncation can hide newer audits.** The FIFO limit applies before timestamps are merged, allowing older diagnostics to evict newer audit events. | [AdminEventLog.razor:559](../Components/Pages/AdminEventLog.razor#L559) |
| 21 | **ARC assertions can mask direct authentication failures.** Both populate one first-result-wins object, so an earlier ARC pass can suppress a later direct fail. | [HeaderAnalysisService.cs:562](../Services/HeaderAnalysisService.cs#L562); [RFC 8617](https://www.rfc-editor.org/info/rfc8617/) |
| 22 | **Parsed Microsoft spam scores miss their findings.** Expanded dictionary keys never match the threshold code's `SCL`/`BCL` checks. | [HeaderAnalysisService.cs:418](../Services/HeaderAnalysisService.cs#L418) |
| 23 | **Partial cloud mailbox changes suppress affected-user notices.** One permission can change successfully, but notifications require overall success. | [MailboxPermissions.razor:514](../Components/Pages/MailboxPermissions.razor#L514) |
| 24 | **Bulk mailbox/calendar changes omit affected-user notices.** Successful rows receive audit records and an admin summary, but no affected-user notification. | [MailboxPermissions.razor:602](../Components/Pages/MailboxPermissions.razor#L602); [CalendarPermissions.razor:559](../Components/Pages/CalendarPermissions.razor#L559) |
| 25 | **Administrative security-group changes omit affected-user notices.** Single and bulk Group Management paths send only administrator notifications. | [GroupManagement.razor:651](../Components/Pages/GroupManagement.razor#L651) |
| 26 | **Attribute Undo sends no administrator notification.** Its execution bypasses the forward-edit page that owns notification dispatch. | [ADAttributeEditorUndoService.cs:251](../Services/ADAttributeEditorUndoService.cs#L251) |
| 27 | **Emergency Disable ignores the configured security recipient.** It sends to the unrelated global admin address, while the UI can still say the security team was notified. | [EmergencyDisableService.cs:1060](../Services/EmergencyDisableService.cs#L1060) |
| 28 | **Selected rejected mutations lack durable attempt audits.** Emergency Disable and Attribute Editor return from protection/preflight refusals before recording the attempted mutation. | [EmergencyDisableService.cs:157](../Services/EmergencyDisableService.cs#L157); [ADAttributeEditorService.cs:534](../Services/ADAttributeEditorService.cs#L534) |
| 29 | **Revoked Bulk Jobs access remains usable through an open page.** Removing the operator's configured SID does not prevent subsequent cross-module cancellation/deletion. | [AdminBulkJobs.razor:294](../Components/Pages/AdminBulkJobs.razor#L294) |
| 30 | **Enabled ServiceNow validation accepts query injection.** Input containing `^OR` can match an unrelated active ticket; the returned number is never compared. Deployment activation was not checked. | [ServiceNowService.cs:69](../Services/ServiceNowService.cs#L69); [Encoded-query operators](https://servicenow.github.io/sdk/4.8.0/guides/encoded-query-guide) |
| 31 | **Refreshing one migration batch deletes everyone's stored reports.** Circuit-local invalidation calls `DeleteAll` on the instance-wide store, including unrelated completed exports. | [Migration.razor:3953](../Components/Pages/Migration.razor#L3953) |
| 32 | **Cancelled/interrupted exports are emailed as ready but unavailable.** Completion creates the file, while listing refuses every non-Completed job. | [MessageTraceDetailJobProcessor.cs:101](../Services/Jobs/MessageTraceDetailJobProcessor.cs#L101); [MessageTraceExportListing.cs:263](../Services/MessageTraceExportListing.cs#L263) |
| 33 | **Concurrent shared-database migrations can abort startup.** Both instances read the old version before locking; the second repeats an already-applied `ALTER TABLE`. | [ConfigStoreMigrator.cs:200](../Services/Storage/ConfigStoreMigrator.cs#L200) |
| 34 | **Installer updates replace an existing service account when omitted.** The pool changes to ApplicationPoolIdentity, potentially losing credential-store, certificate, directory, or database access. | [Install-ExchangeAdminWeb.ps1:598](../tools/Install-ExchangeAdminWeb.ps1#L598) |
| 35 | **Deployment continues after pool shutdown fails.** Exceptions are swallowed before mirroring, permitting partial replacement of a live application. | [deploy.ps1:496](../deploy.ps1#L496); [Install-ExchangeAdminWeb.ps1:710](../tools/Install-ExchangeAdminWeb.ps1#L710) |
| 36 | **Generic installer upgrades omit the verified database backup.** Updated binaries can start migrations without the required recovery snapshot. | [Install-ExchangeAdminWeb.ps1:698](../tools/Install-ExchangeAdminWeb.ps1#L698) |
| 37 | **Recovery instructions describe the retired storage arrangement.** Shared deployments are directed toward local paths, single-pool shutdown, database recreation, and name-based authorization rows. | [ConfigDbOperations.md:3](ConfigDbOperations.md#L3) |
| 38 | **Promotion restart failure bypasses rollback.** Restart occurs in `finally`, outside the rollback catch; new files can remain deployed with the pool stopped. | [promote-dev-to-prod.ps1:384](../tools/promote-dev-to-prod.ps1#L384) |
| 39 | **Cutover accepts a database path that deployment can delete.** A shared DB inside a publish tree passes validation but is eligible for removal by later mirroring. | [Move-ConfigDbToShared.ps1:351](../tools/Move-ConfigDbToShared.ps1#L351) |
| 40 | **Cutover can falsely fail on an idle IIS application.** It waits for startup logs without warming the app; pool AlwaysRunning alone does not ensure application initialization. | [Move-ConfigDbToShared.ps1:515](../tools/Move-ConfigDbToShared.ps1#L515); [IIS initialization](https://learn.microsoft.com/en-us/iis/configuration/system.webserver/applicationinitialization/) |

Additional lower-priority findings and improvements:

- **Promotion provenance checks fail open:** missing commit IDs or Git failures can still produce a "matches source HEAD" claim. [deploy-pipeline.ps1:124](../tools/deploy-pipeline.ps1#L124).
- **Backup `-WhatIf` creates a directory** before `ShouldProcess`. [SqliteConfigBackup.psm1:202](../tools/SqliteConfigBackup.psm1#L202).
- **Graph bootstrap handles partial provisioning poorly:** failure during the second registration can strand the first and suppress its one-time secret output. [New-ExchangeAdminWebGraphApps.ps1:101](../tools/New-ExchangeAdminWebGraphApps.ps1#L101).
- **Header-processing bounds are incomplete:** capped raw headers still feed uncapped derived routing collections. Large-input runtime impact remains unmeasured.
- **Tests need more composed workflows:** paused operations, target changes, cross-circuit activity, partial failures, and deployment failures. Several existing tests validate individual pieces whose combined behavior is wrong.
- **Operational hardening:** narrow publish-folder Modify permissions, require SQLite-dependent CI tests to run, and exercise compatible scripts under Windows PowerShell 5.1.

Existing issues remain separate from these new findings:

- [Licensing diagnostics: psl-2](../.agents/review/findings/psl-2.md).
- [DHCP diagnostics: psl-3](../.agents/review/findings/psl-3.md).
- [EXO exception classification: psl-4](../.agents/review/findings/psl-4.md).
- [Comms count wording: cdr-followup-1](../.agents/review/findings/cdr-followup-1.md).
- [Event Log CSV formula-injection gap: csv-1](../.agents/review/findings/csv-1.md).

The broad navigation-cancellation promise also exceeds currently implemented cancellation.

Unconfirmed follow-ups include protection rules retaining stale group DNs, legacy name-valued servicer grants, and Emergency Disable's cloud UPN binding. Dependency review did **not** establish an exploitable package vulnerability; resolved transitive packages and deployed runtime versions remain unverified.

This was a broad static review across the repository, with deeper tracing of privileged and failure paths. **Fixes remain unstarted. Recommended next step: draft a remediation plan beginning with findings 1-8.**
