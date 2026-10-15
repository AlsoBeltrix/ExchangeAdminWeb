# PowerShell command replay audit - 2026-10-15

Native `/root/audit_powershell_replay`, `gpt-6-astra` at `max`, performed a read-only
audit after the owner supplied an interim Claude review suggesting the C9 pattern in
roughly eighteen other services. Evidence is anchored to committed
`b936c604482447c36f93010b6fae135dd71d6df9`, before the concurrently implemented C4.
No files, tests, builds, hooks or external systems were changed or exercised.

## Result and scope

One shared command-replay defect was confirmed by control flow: ExchangeServiceBase's
InvokeOptional can retain a failed command, and ConferenceRoomService continues using that
same PowerShell instance. `findings/psl-1.md` owns that finding and
`docs/PowerShellCommandLifecycle-Plan.md` owns the proposed repair, pending approval.

The lexical pattern alone is not a defect: a throw can end the owning using scope, a caller
can clear before continuing, or the next operation can create a fresh PowerShell object.
Reusing a runspace does not by itself reuse a PowerShell command queue. The original claim
that Comms-10k is the only hardened module is not supported by this audit.

## Immutable-head inventory

127 direct SDK invocation expressions in 22 production C# files: 126 Invoke and one
InvokeAsync. Counts exclude comments and calls to shared wrappers. Twenty files contain
Commands.Clear; that is not a defect count. The seven originally named services total 71
direct sites. Emergency Disable has 12, not 13; Group Management has 23, including five
protected by finally, two of which are its mutations.

| File under Services | Sites | Command-replay disposition |
|---|---:|---|
| ExchangeServiceBase.cs | 8 | InvokeOptional is unsafe; other helpers clear failures, reset before retry, or end ownership. |
| ConferenceRoomService.cs | 8 | Direct AD calls dispose on failure; confirmed EXO replay uses InvokeOptional. |
| EmergencyDisableService.cs | 12 | Failure ends the owning using lifetime; StampPreviousOu's catch is followed by caller return. |
| GroupManagementService.cs | 23 | Writes clear commands/errors in finally before reconciliation; caught fallbacks clear commands. |
| ProtectedPrincipalService.cs | 7 | Group/transitive catches clear commands/errors; other failures end ownership. |
| ADAttributeEditorService.cs | 8 | Search-base catch is followed by clear; failed save returns and disposes. |
| LicensingUpdatesService.cs | 5 | Per-row catches clear commands. Separate diagnostic issue: psl-2. |
| DhcpAuthorizationService.cs | 8 | Discovery catches clear commands; writes return/dispose. Separate diagnostic issue: psl-3. |
| SelfServiceGroups/SelfServiceGroupService.cs | 20 | Write/read-back/member-detail cleanup protected; other failures clear or end ownership. |
| ADDirectorySearchService.cs | 7 | Fresh PowerShell object per request/probe. |
| ADEmployeeIdLookup.cs | 2 | Fresh PowerShell object per domain/probe. |
| ADOrganizationalUnitService.cs | 2 | Failure ends ownership. |
| AccountLockoutRemediationService.cs | 3 | Failed object not reused; failed factory does not return its object. |
| BitLockerLiveDirectorySearch.cs | 1 | Separate PowerShell objects for individual queries. |
| CalendarPermissionService.cs | 2 | Cleanup clears commands before session removal, then disposes. |
| MailboxPermissionService.cs | 2 | Same cleanup ownership; normal operations use the correct shared Invoke. |
| Comms10kService.cs | 1 | C9 helper clears commands in finally, clears thrown-call errors and rethrows. |
| ExoConnectionPool.cs | 3 | Creation failure disposes; return/borrow clears; destruction clears before disconnect. |
| IExoRecipientCommands.cs | 1 | Adapter clears commands in finally; no production construction reference found. |
| PermissionValidator.cs | 2 | Failures discard/return through pool cleanup; expected absence clears commands. |
| PowerShellDirectoryCommands.cs | 1 | Fresh PowerShell object per call. |
| TrueLastLogonService.cs | 1 | Single async invocation followed by disposal. |

## Shared helper callers

InvokeOptional has twenty operational call sites across five files. Conference Rooms has
eight, including the two confirmed continuation paths in psl-1. Migration has six; its three
optional mutations at lines 806/812/829 abort on throw and pool return/discard clears state.
Message Trace has three; query catches clear both collections, and session cleanup clears
commands before cleanup. Shared mailbox-location checks have two; failures reach pool/session
cleanup. Recipient Lookup has one; outer catch returns and pool return clears state.

Line references belong to the immutable head above, not a moving working tree.

## Other diagnostics and limits

Licensing and DHCP retain error records on confirmed continuation paths; psl-2 and psl-3
record the narrower diagnostic impacts. They are not additional command-replay defects and
are not included in the proposed replay fix. Group Management's global-catalog catch and
Self-Service Groups' ACL catch also retain errors, but no incorrect downstream outcome was
established for those residues; do not promote them to confirmed defects without evidence.

This is source-level evidence, not an executed regression or live-backend test. Generated and
remote script retries were not analyzed. The audit establishes current C# command ownership
and continuation paths; it does not assert that the entire application is hardened.
