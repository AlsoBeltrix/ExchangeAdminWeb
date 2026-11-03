# ppi-s4: Conference Rooms runs no protected-principal check; its gate becomes a room binder (slice S4)

**Severity**: HIGH - `ConferenceRoomProtectionGate` ran the protected-principal check, and the servicer grant, on every room
mailbox Conference Rooms wrote, against the owner's ruling of 2026-11-03 (`.agents/decisions.md`, "Conference Rooms runs no
protected-principal check at all"): "protected principals does not apply to conference rooms." A room the rules named was
refused, and a bulk row - which has no operator - could never be serviced. Plan `docs/ProtectedPrincipalIdentifiers-Plan.md`
(Approved 2026-11-03), sections D.2, D.3, H, slice S4.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/ppi-s4` (worktree `D:\source\ExchangeAdminWeb-wt\ppi-s4`, from master `3b7a946e`)
**Commit**: see `git log master..fix/ppi-s4` (binder commit, governance-docs commit) | **Reviewer**: implementer (candidate)

## Evidence

- Plan D.1 (at `c94b443e`; unchanged through `3b7a946e`): `ConferenceRoomProtectionGate.EvaluateAsync` -> `BeginCheck` ->
  `ResolveExchangeTargetAsync` -> `CheckAsync` -> `ProtectedPrincipalServicerService.Evaluate` -> `ValidatedMailbox.From`.
- Decision 2026-11-03 supersedes item 2 of the 2026-10-26/27 entry (the kept room-mailbox gate).

## Approach (as built)

- **Shared binder exposed without protection semantics.** `ProtectedPrincipalService.BindSyncedRecipientAsync(identity,
  recipient)` (public, virtual): one `DirectoryReadOperation.Begin()` (one directory-read credential fetch, no timing line,
  no `BeginCheck`), AD's reading of the typed string (`ResolveWithStatusAsync`; Ambiguous/Unavailable refuse), then the
  private `BindSyncedAsync` - C.2 steps 3-6 and the aud-a2b agreement - which `ResolveExchangeTargetAsync`'s synced branch
  now calls too. `ResolveExchangeTargetAsync` is otherwise unchanged for its other callers. A recipient not synced binds
  nothing (Unavailable).
- **`ConferenceRoomBinder`** (`Services/ConferenceRoomBinder.cs`, scoped; replaces `ConferenceRoomProtectionGate.cs`):
  `BindThenRunAsync(identity, onRefused, onBound)`, check-then-run. One `IIdentityResolver.ResolveRecipientAsync` (injected);
  throws -> "Exchange Online could not be asked. Nothing was written."; null -> "Room '{x}' not found."; RecipientTypeDetails
  not RoomMailbox / RemoteRoomMailbox -> `ValidateRoomRecipientAsync`'s wording; no ExchangeGuid -> the existing no-room
  refusal; cloud-only -> `ValidatedMailbox.From(recipient)` (no objectGUID); synced -> `BindSyncedRecipientAsync`, Ambiguous ->
  `AmbiguousMessage` (moved, neutral), anything else unbound -> "The room's Active Directory account could not be identified
  ...". A bound principal whose mailbox GUID differs from Exchange's is refused as no room. Depends on `IIdentityResolver`,
  `ProtectedPrincipalService` and a logger only: no `CheckAsync`, `BeginCheck`, servicer, rules or `ClaimsPrincipal`.
- **Removed with the gate.** `ProtectionDenial` / `ProtectionOutcome` (nothing else used them; replaced by
  `RoomBindingRefusal`); the page's `CurrentUserAsync()` and its `user` argument at both call sites; `ServicedExtra` and the
  `protectedPrincipalServiced` audit extra (both audit helpers lose `servicedAuditDetail`); the bulk processor's servicer
  comment and null-user pass; `"ConferenceRooms"` from `ModulesWithProtectedPrincipalServicing`. `Program.cs` registers
  `ConferenceRoomBinder`.
- **Dead servicer config.** `ProtectedPrincipalServicerService.RetiredModuleIds` (`ConferenceRooms`) and
  `ReportRetiredGrants()`, called once at startup after the section-access SID migration: one Warning per retired module
  with groups stored under `ProtectedServicer:<id>`. The row is left in the store; Module Configuration no longer shows it
  and its save writes only edited keys.
- **Comments.** `ConferenceRoomService.cs` aud-a2c comment (the AD-step preflight) and four "protection gate validated"
  remarks rewritten to the binder; page and processor comments likewise; `ProgressRegistry` anchors follow
  `RoomBinder.BindThenRunAsync(` and its prose drops `CurrentUserAsync`.
- **Help (Known Failure Class 5).** No operator-visible page text mentioned protection (grep). README: the servicing section
  points at the code's list and says Conference Rooms is not among it (it said "Blocked Senders alone"); the Conference
  Rooms section gains a "No protected-principal check" bullet.
- **Governance.** Constitution third scoped-exception bullet and repo-guidance Known Failure Class 3 third bullet rewritten
  to the 2026-11-03 ruling.
- **Versions.** Conference Rooms 2.19.12 -> 2.20.0 with a note. Base 2.36.0 unchanged (undeployed).
- **ClickGate.** No `ClickGateRegistry` entry for the page sits below the edits (all are in markup above line 1000); only
  `ExpectedLineCount` moved, 1863 -> 1838.

## Where the code goes past or beside the plan's text

- Cloud-only rooms are bound with no Active Directory read: the old gate (through `ResolveExchangeTargetAsync`) refused a
  cloud-only room when AD held an object under the same string. That was protection of the checked object; no write is
  affected, since the AD steps refuse without an objectGUID. Plan D.2 step 4 says "exactly as today" of the
  `ValidatedMailbox`; the refusal is not carried.
- A recipient Exchange does not know is "Room '{x}' not found." even when AD knows the string (the old gate's no-room text).
- `ConferenceRoomAmbiguityMessageTests`: the cloud-only-groups collision no longer exists (a group is not a room), so that
  case is replaced by two objects carrying the room's SID; the Unavailable case now expects the binder's neutral text.
- `ConferenceRoomBulkProcessorTests`' two protected-room tests now assert the row IS applied with no check; the
  Unavailable/Ambiguous theory is renamed `ARoomWhoseAccountCannotBeBound_IsRefused_NotApplied`.
- `ConferenceRoomWriteIdentityTests` needed no change (it never built the gate).

## Guard proof

Each reverted, the named test failed, restored, the suite passed:

| Revert | Failing test |
|---|---|
| M1 the shared binder binds by address | `ASyncedRoom_IsBoundBySid_AndTheWriteIsHandedThatAccountsObjectGuid` (+5) |
| M2 the binder runs the old check and refuses a protected room | `AProtectedRoom_IsWritten_TheBinderNeverConsultsTheRules`, `NoProtectionEntryPoint_IsEverReached_EvenForARoomTheRulesProtect`, `Finder_/Type_AProtectedRoom_IsApplied_WithNoUser_AndNoCheckRuns`, `ConferenceRoomBinder_RunsNoProtectionCheck` |
| M3 room-type check removed | `ARecipientThatIsNotARoom_IsRefused_InTheReadBackChecksWords_NothingWritten` |
| M4 unknown-recipient refusal removed | `AnUnknownAddress_IsRefused_NothingWritten`, `ARowNeitherDirectoryKnows_HasNoRoomToBindTo_IsDeniedAndNotApplied` |
| M5 Exchange failure not caught | `AnExchangeFailure_IsRefused_NothingWritten` |
| M6 binder begins a protected-principal check | `TheBinding_FetchesTheCredentialOnce_AndBeginsNoProtectionCheck`, `ConferenceRoomBinder_RunsNoProtectionCheck` |
| M7 shared binder opens no operation (two fetches) | `TheBinding_FetchesTheCredentialOnce_AndBeginsNoProtectionCheck` |
| M8 cloud-only room sent to the SID binder | `ACloudOnlyRoom_IsBound_WithNoDirectoryObject_AndNoDirectoryRead` |
| M9 retired grant not logged | `AConferenceRoomsServicerGroupInTheStore_IsLoggedOnce_AndLeftInTheStore` |
| M10 startup call removed | `Startup_ReportsTheRetiredGrants_Once` |
| M11 ConferenceRooms back on the servicing list | `ModuleConfiguration_NoLongerOffersConferenceRoomsServicing` |
| M12 no-mailbox-GUID refusal removed | `ARoomWithNoMailboxGuid_IsRefused_AsNoRoom_NothingWritten` |

## Gates (worktree, 2026-11-03)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: succeeded, 0 errors, 17 warnings (none in a changed file).
- `dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4`: 8395 passed, 0 failed, 0 skipped.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0.
- `git diff --check HEAD`: clean. `tools/Test-AsciiOnly.ps1`: passed.
- Not run: any live Graph, Exchange or directory read; the page was not rendered in a browser; startup was not run to see
  the retired-grant line.

## Left for later slices

Module-admin grants (S5); records, plan status and state (S6). Dev: a room in the protection rules is now written.
