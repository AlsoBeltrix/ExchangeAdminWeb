# kd-7: M365 Group Management validates no ticket; the same shape on Named Locations delete and twice on Conference Rooms

**Severity**: MEDIUM - mutating actions without the ticket validation the Constitution and the other modules apply. `M365GroupManagement` validates no ticket on its writes; `NamedLocations` delete and two Conference Rooms paths share the shape. Conference Rooms and Named Locations were not re-verified at the last state pass; the implementing agent verifies each before writing.
**Status**: Merged to master `881b8d5b`; codex defect review ticket-batch-1 in flight
**Branch**: `fix/kd7-kd8` (worktree `D:\source\ExchangeAdminWeb-wt\kd7-kd8`, from master `3e43b35b`)
**Commit**: `cd25a924` (M365 Group Management), `11408fae` (Named Locations), the Conference Rooms commit after it on `fix/kd7-kd8`
**Reviewer**: implementer (recorded in `.agents/state.md` "Known defects"; M365 re-verified 2026-10-23 as of `d881ea58`)

## Evidence

`Components/Pages/M365GroupManagement.razor` and `Services/M365GroupManagementService.cs`; `Components/Pages/NamedLocations.razor` (delete); `Components/Pages/ConferenceRooms.razor` (two paths - locate). Trigger: a write with an empty or malformed ticket. Predicted failure: the write proceeds and the audit row carries no valid ticket.

## Approach

Re-read at `3e43b35b`; every line below is that commit's.

### Verification of the record's claims

Where "the shape" comes from: the tier-1 click-gating log for M365GroupManagement (`8b3bfd64`, `.agents/token-log.md`
2026-09-19 page 7) - "nothing in this module validates a ticket against ServiceNow and no handler re-checks one, so the
four markup clauses are the ONLY enforcement of the ticket requirement - the NamedLocations 191 / IntuneDevices 403
shape". The shape is a precondition enforced only by a button's `disabled` expression, which is one round trip stale and
is bypassed by a double dispatch or a programmatic click.

One fact decides all three pages: `Services/ServiceNowService.cs:57-65` returns `IsValid = true` for EVERY ticket, a blank
one included, while `ServiceNow:Enabled` is false (the deployment today: no ServiceNow API access,
`.agents/decisions.md` 2026-08-31), and its own blank check (`:67`) sits below that early return. So
`ServiceNow.ValidateTicketAsync` alone - the inline shape eight pages use - does not enforce presence on a dormant
deployment. RiskyUsers (`RiskyUsers.razor:870`, `:876`) and IntuneDevices (`IntuneDevices.razor:1026`, `:1032`) check presence first and then validate; that is the
shape used here.

1. **M365 Group Management - true.** No handler checks the ticket and the page injects no `ServiceNowService`
   (`M365GroupManagement.razor:5-12`). `SaveGroup` (`:502-579`) captures the ticket at `:516`, re-checks authorization
   at `:533-540` and writes at `:546-548`; `DeleteGroup` (`:581-647`) captures at `:587`, re-authorizes at `:601-608`,
   deletes at `:613`; `RunMemberOpAsync` (`:685-773`, the pipeline `AddMember` `:652`, `AddOwner` `:660`,
   `RemoveMember` `:668` and `RemoveOwner` `:675` share) captures at `:697`, re-authorizes at `:710-718` and writes at
   `:720`. The only enforcement is the markup at `:130`, `:149`, `:172`, `:191` (`memberTicketNumber`) and `:291`,
   `:301` (`formTicketNumber`). Every audit row and admin email carries whatever was in the box, blank included.
2. **Named Locations delete - true, and the record understates it.** `DeleteLocation` (`NamedLocations.razor:442-506`)
   captures the ticket at `:453`, re-authorizes at `:467-474` and deletes at `:476` with no ticket check; the Confirm
   Delete clause at `:196` is the only enforcement (`ClickGateRegistry` says so: "DeleteLocation does not re-check the
   ticket"). `SaveLocation` (`:341-440`, captures `:359`, re-authorizes `:379-386`, writes `:404-410`) has the same
   shape - its Save clause at `:206` is equally the only enforcement ("SaveLocation re-checks neither") - so both writes
   are fixed. The page injects no `ServiceNowService` (`:5-13`).
3. **Conference Rooms, "two paths validate no ticket" - false as written.** All four writes validate:
   `SetupSingleRoom` `ConferenceRooms.razor:1033-1039`, `ApplyFinderCsv` `:1277-1283`, `SetSingleRoomType`
   `:1383-1389`, `ApplyTypeCsv` `:1587-1593` each call `ServiceNow.ValidateTicketAsync(ticket)` and refuse with an
   audited Failed row. The "twice" in the source note is the page's two room-address clauses (`ClickGateRegistry`,
   ConferenceRooms entry: "Two of the six clauses are the SOLE enforcement of their condition - neither SetupSingleRoom
   nor SetSingleRoomType checks its room address anywhere"), and that sub-claim is false as a write defect: an empty
   address reaches `ConferenceRoomProtectionGate.GuardThenRunAsync` (`Services/ConferenceRoomProtectionGate.cs:103-130`),
   which refuses a recipient that does not resolve to a room mailbox (NotFound refused since aud-a2d) and the page
   audits the denial. Declined; no change. **But** the dormant pass-through above means all FOUR ticket checks admit
   a blank ticket today, so the predicted failure is real there too: presence is enforced only by the markup at `:126`,
   `:159`, `:313` and `:340`, and `ClickGateRegistry`'s "ServiceNow validation would reject an empty ticket" holds only
   on a deployment with the integration enabled. The fix is presence on those four lines, in place.

### What changes - M365 Group Management (commit 1)

- `[Inject] private ServiceNowService ServiceNow` beside `Progress` (`:317`), below every registered line.
- Two helpers at the end of `@code`: `TicketRefusalAsync(ticket)` - presence first ("A ticket number is required."),
  then `ServiceNow.ValidateTicketAsync`, which refuses a malformed or unknown ticket wherever the integration is enabled
  ("Ticket validation failed: ..."); null when the ticket passes - and `AuditTicketRefusal(action, target, ticket,
  refusal)` - one Failed `LogModuleAction` row with the reason in `error` and, in the aud-a28 refusal convention,
  `refusedAt = "TicketValidation"` and `mutationAttempted = false`.
- In `SaveGroup`, `DeleteGroup` and `RunMemberOpAsync`, directly below the authorization re-check and above the write:
  on a refusal the banner and the activity's local get it, the row is written, and the handler returns. Inside the try,
  so the finally lowers the flag; inside the activity window. No administrator email: like the authorization denial
  beside it, nothing reached Graph. The protection gap the owner excluded (`UpdateGroupAsync` / `DeleteGroupAsync` call
  no protection check) is untouched and does not interact: the gate sits above the service call.
- Line keys: every registered line is in markup at 300 or earlier and every edit is in `@code` below 313, so only
  `ExpectedLineCount` moves (re-anchor playbook: a diff map, then the ClickGate tests). The registry's M365 remarks and
  `RendersOnlyWhen` notes that call the markup clauses the only enforcement are corrected; the clauses stay registered.
  `ProgressRegistry` names `TicketRefusalAsync(` as the opening call of the three entries.
- Guard: `M365GroupManagementTicketGateTests` drives the compiled handlers by reflection (the kd-4 / AttributeEditor
  pattern) over a real `AuditService`, a substituted `EmailService` and an unconfigured service, so a write that is
  reached fails into the catch, which audits it without `mutationAttempted` and emails it.
- Mutation: one handler's gate removed; the presence half of `TicketRefusalAsync` removed.
- Module `M365GroupManagement` 1.6.1 -> 1.7.0 in `Modules/ModuleCatalog.cs`; no base app bump.

### What changes - Named Locations (commit 2)

The M365 shape, on `NamedLocations.razor` at `cd25a924` (lines unchanged from `3e43b35b`):

- `[Inject] private ServiceNowService ServiceNow` beside `Progress` (`:222`).
- `TicketRefusalAsync` and `AuditTicketRefusal` above `ParseLines` (`:600`), category `NamedLocations`.
- `SaveLocation`: the gate below the authorization re-check (`:379-386`) and above the empty-values check (`:388`) and
  the write (`:404-410`). `DeleteLocation`: below the re-check (`:467-474`), above the delete (`:476`).
- Line keys: every registered line is in markup at 205 or earlier and every edit is in `@code` below 218, so only
  `ExpectedLineCount` moves. The Confirm Delete (196) and Save (205) `RendersOnlyWhen` notes that say the handlers
  re-check nothing are corrected. `ProgressRegistry` names `TicketRefusalAsync(` first on both entries.
- Guard: `NamedLocationsTicketGateTests` (create, update, delete) over a real `NamedLocationsService` with no Graph
  secret. Mutation: `DeleteLocation`'s gate removed; the presence half removed.
- Module `NamedLocations` 1.3.3 -> 1.4.0; README states the requirement.

### What changes - Conference Rooms (commit 3)

In place, so `ConferenceRooms.razor` keeps its 1758 lines and no `ClickGateRegistry` key or count moves (another
worktree is editing this page); lines are those of `3e43b35b`, unchanged at `11408fae`:

- The four ticket checks (`:1033`, `:1277`, `:1383`, `:1587`) become
  `ConferenceRoomService.RequireTicket(ticket) ?? await ServiceNow.ValidateTicketAsync(ticket)`: a blank ticket gets the
  refusal `ServiceNowService` itself gives one when enabled ("Ticket number is required"), so it flows into the existing
  refusal branch - banner "Ticket validation failed: ...", audited, return - and ServiceNow is asked only about a ticket
  that is present. Each check stays above the authorization re-check, where it was; the
  `ServiceNow.ValidateTicketAsync(` text `ProgressRegistry` pins on these handlers stays in each body.
- The four refusal audit calls (`:1037`, `:1281`, `:1387`, `:1591`) pass `refusedAt: TicketRefusalStep`, and
  `AuditFinderAction` / `AuditTypeAction` (`:1718-1731`) gain that optional parameter on their existing signature and
  body lines, merging it through `ConferenceRoomService.WithRefusal`: the row gains `refusedAt` "TicketValidation" and
  `mutationAttempted` false, for the existing invalid-ticket refusal as well as the new blank one.
- `Services/ConferenceRoomService.cs`, beside `WithAttributesConfirmed`: `TicketRefusalStep`, `WithRefusal` and
  `RequireTicket` - there rather than on the page because the page's line count had to stay put.
- Guard: `ConferenceRoomTicketPresenceTests` drives the four handlers with a substitute authorization service that
  is never called when the ticket refuses and refuses when it is. Mutation: `RequireTicket` answering null for a blank
  ticket; one site's check reverted to the bare ServiceNow call.
- Module `ConferenceRooms` 2.7.0 -> 2.8.0 (the version this branch saw; another worktree may bump it as well).

## Files changed

Commit 1, M365 Group Management:

- `Components/Pages/M365GroupManagement.razor` - `[Inject] ServiceNowService ServiceNow`; `TicketRefusalAsync` and
  `AuditTicketRefusal` at the end of `@code`; the gate in `SaveGroup`, `DeleteGroup` and `RunMemberOpAsync`, each
  directly below its authorization re-check. 781 -> 842 lines, every added line below 313.
- `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` - M365 `ExpectedLineCount` 781 -> 842 (diff map: 24 line keys, none
  moved, none unmapped); the entry's falsification-6 remarks, the `memberTicketNumber` / `formTicketNumber`
  ExcludedField reasons, the AnnotatedControls preamble, the `RendersOnlyWhen` notes on 129, 149, 191 and 291 and the
  `SaveGroup` ticket PostAwaitLiveRead note said no handler checks a ticket; they now say the handlers do and the
  clauses stay as the click gate's precondition. No clause, key or registration changed.
- `ExchangeAdminWeb.Tests/ProgressRegistry.cs` - `TicketRefusalAsync(` named first on the `SaveGroup`, `DeleteGroup`
  and `RunMemberOpAsync` entries.
- `ExchangeAdminWeb.Tests/M365GroupManagementTicketGateTests.cs` - new.
- `Modules/ModuleCatalog.cs` - `M365GroupManagement` 1.6.1 -> 1.7.0.
- `README.md` - the M365 Group Management section states the ticket requirement.

Commit 2, Named Locations:

- `Components/Pages/NamedLocations.razor` - `[Inject] ServiceNowService ServiceNow`; `TicketRefusalAsync` and
  `AuditTicketRefusal` above `ParseLines`; the gate in `SaveLocation` and `DeleteLocation`, each directly below its
  authorization re-check. 604 -> 657 lines, every added line below 218.
- `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` - NamedLocations `ExpectedLineCount` 604 -> 657 (diff map: 14 line keys,
  none moved, none unmapped); the Confirm Delete (196) and Save (205) `RendersOnlyWhen` notes corrected.
- `ExchangeAdminWeb.Tests/ProgressRegistry.cs` - `TicketRefusalAsync(` named first on `SaveLocation` and `DeleteLocation`.
- `ExchangeAdminWeb.Tests/NamedLocationsTicketGateTests.cs` - new.
- `Modules/ModuleCatalog.cs` - `NamedLocations` 1.3.3 -> 1.4.0.
- `README.md` - the Named Locations section states the ticket requirement.

Commit 3, Conference Rooms:

- `Components/Pages/ConferenceRooms.razor` - in place, 1758 lines before and after (diff map: 37 line keys, none moved):
  the four ticket-check lines, the four refusal audit lines, and the signature and body lines of `AuditFinderAction` and
  `AuditTypeAction`. Twelve lines in all; `ClickGateRegistry` and `ProgressRegistry` untouched.
- `Services/ConferenceRoomService.cs` - `TicketRefusalStep`, `WithRefusal`, `RequireTicket` after
  `WithAttributesConfirmed`.
- `ExchangeAdminWeb.Tests/ConferenceRoomTicketPresenceTests.cs` - new.
- `Modules/ModuleCatalog.cs` - `ConferenceRooms` 2.7.0 -> 2.8.0, with a version note.

## Guard proof

### M365 Group Management

`M365GroupManagementTicketGateTests` (35 cases) drives the seven write entry points - Create and Update
(`SaveGroup`), `DeleteGroup`, and `AddMember`, `AddOwner`, `RemoveMember`, `RemoveOwner` through `RunMemberOpAsync` -
by reflection, over a real `AuditService` on a temp log root, a substituted `EmailService`, granting authorization,
and a real `M365GroupManagementService` with no Graph and no directory-read secret, so a write that is reached fails
without leaving the process and is audited without `mutationAttempted` and emailed.

- `AWriteWithNoTicket_IsRefusedAndAudited_AndNeverReachesTheService` (7 writes x `""`, `"   "`, ServiceNow dormant):
  one Failed row with the action, target and "A ticket number is required.", `refusedAt` "TicketValidation",
  `mutationAttempted` false and no ticket; no admin email; the banner is the refusal.
- `AWriteWithATicketServiceNowRejects_IsRefusedAndAudited_AndNeverReachesTheService` (7; enabled, a stub answering "not
  found"): the same row with "Ticket validation failed: Ticket INC0042 not found in ServiceNow"; one query.
- `AMalformedTicket_IsRefusedWithoutAQuery_WhereServiceNowIsEnabled` (7): "Ticket validation failed: Ticket number must
  be letters and digits only."; no query.
- `AWriteWithATicket_ReachesTheService` (7, the control): the write is attempted - one Failed row with the service's
  own error and no refusal fields, and one admin email.

| Mutation | Result |
| --- | --- |
| M1: `DeleteGroup`'s gate removed (md5 `4c55d015` -> `156ee1b9`) | FAIL, 4 of 35 - every Delete refusal case: `Expected: "A ticket number is required." Actual: "M365 Group Management module is not configured..."` (the delete reached the service) |
| M2: the presence half of `TicketRefusalAsync` removed (md5 -> `a3660e13`) | FAIL, 14 of 35 - every blank-ticket case: the dormant client passed it, and the write reached the service ("not configured" x6, "Protection check unavailable" x8) |

After each, the page was restored byte-exact from the fixed copy with a fresh mtime (md5 `4c55d015` again), rebuilt
and rerun: 35 passed; with the ClickGate and ProgressRegistry groups, 386 passed.

### Named Locations

`NamedLocationsTicketGateTests` (15 cases): the same four tests over create, update and delete, behind a real
`NamedLocationsService` with no Graph secret, so a reached write throws "not configured" into the catch, which audits
it without `mutationAttempted` and emails it. Blank (dormant) -> "A ticket number is required."; rejected by an
enabled stub -> "Ticket validation failed: Ticket INC0042 not found in ServiceNow", one query; malformed -> the pattern
refusal, no query; the control reaches the service and emails once.

| Mutation | Result |
| --- | --- |
| M1: `DeleteLocation`'s gate removed (md5 `175da707` -> `14f37495`) | FAIL, 4 of 15 - every Delete refusal case: `Actual: "Named Locations module is not configured..."` |
| M2: the presence half of `TicketRefusalAsync` removed (md5 -> `5e76f3a9`) | FAIL, 6 of 15 - every blank-ticket case reached the service |

Restored byte-exact with a fresh mtime (md5 `175da707`), rebuilt, rerun: 15 passed; with the ClickGate and
ProgressRegistry groups, 369 passed.

### Conference Rooms

`ConferenceRoomTicketPresenceTests` (17 cases) drives `SetupSingleRoom`, `ApplyFinderCsv`, `SetSingleRoomType` and
`ApplyTypeCsv` with only what runs up to the ticket check set, and a substitute authorization service - the next step -
that refuses.

- `AWriteWithNoTicket_IsRefusedAndAudited_BeforeAnythingElseRuns` (4 x `""`, `"   "`, ServiceNow dormant): one Failed
  row - `ConferenceRooms_SetMetadata` or `ConferenceRooms_SetType`, the room or "(bulk)", "Ticket validation failed:
  Ticket number is required", `refusedAt` "TicketValidation", `mutationAttempted` false, no ticket - the banner says the
  same, and the authorization service was never called.
- `AWriteWithATicketServiceNowRejects_IsRefusedWithTheSameAuditedDenial` (4; enabled stub, "not found"): the existing
  invalid-ticket refusal, now carrying the same two fields; one query; authorization never called.
- `AWriteWithATicket_GoesOnToTheAuthorizationCheck` (4, the control): authorization called once, the banner is its
  "Authorization denied.", and no row carries `refusedAt`.
- `RequireTicket_RefusesOnlyABlankTicket`.

| Mutation | Result |
| --- | --- |
| M1: `RequireTicket` answers null for every ticket (service md5 `97c5f9a4` -> `3b5b422a`) | FAIL, 9 of 17 - every blank-ticket case (`Assert.Single() Failure: The collection was empty`: the blank ticket passed the dormant client and reached the authorization check) and the unit case |
| M2: `SetSingleRoomType`'s check reverted to the bare ServiceNow call (page md5 `902ca738` -> `ba52464f`) | FAIL, 2 of 17 - that handler's two blank-ticket cases |
| M3: `ApplyFinderCsv`'s refusal row without `refusedAt` (page md5 -> `7c620744`) | FAIL, 3 of 17 - that handler's blank and rejected cases |

Each restored byte-exact with a fresh mtime (service `97c5f9a4`, page `902ca738`), rebuilt, rerun: 17 passed; with the
ClickGate and ProgressRegistry groups, 368 passed.

## Coder dispute

None.

## Known gaps

- The refusal sends no administrator email, as the authorization denial beside it does not: nothing reached Graph.
  Recorded so a reviewer does not take it for an omission (Constitution, Notifications: mutating actions notify; a
  refused attempt is audited, as here).
- The same dormant pass-through leaves the inline-ServiceNow pages outside this record with markup-only presence:
  `MailboxPermissions.razor:394` / `:587`, `CalendarPermissions.razor:374` / `:548`, `GroupManagement.razor:573` /
  `:702` / `:933` / `:1078`, `DhcpAuthorization.razor:302` (its deauthorize at `:385` does check), `MfaReset.razor:325` call
  `ServiceNow.ValidateTicketAsync` with no presence check first, so on a dormant deployment a blank ticket that bypasses
  the button reaches the write (`ClickGateRegistry` already calls the Mailbox Permissions clauses "the only
  enforcement"). Reported to the supervisor as a candidate, not fixed here. Comms-10k is exempt: its ticket is optional
  by design.
- `ClickGateRegistry`'s ConferenceRooms notes on 125 and 158 ("ServiceNow validation would reject an empty ticket",
  "re-checked by ServiceNow validation") were true only with the integration enabled; they are true now through
  `RequireTicket`. Left as written so the entry another worktree is editing is not touched.
- The pages' three helper shapes differ for a reason: M365 and Named Locations got page-local helpers below their
  registered lines; Conference Rooms had to keep its line count, so its helpers are statics in its service. No shared
  helper, because a new shared one would be the base-version change this stream rules out.
- No live check: no Graph, Exchange or ServiceNow call was made; rendering is not exercised (no bUnit harness).

## Reviewer comments

None yet (codex review pending, dispatched by the supervising session).

## Closeout

Pending: merge into master, codex review, branch deletion.
