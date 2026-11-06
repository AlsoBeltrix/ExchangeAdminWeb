# sweep-e-41: six ticket gates were markup-only or IsNullOrWhiteSpace, and none asked ServiceNow (sweep item 41)

**Severity**: MEDIUM - `.agents/review/live-risk-sweep-2026-10-30.md` item 41: no server-side check on
`ADAttributeEditor.razor` (the service has no ticket step), `BlockedSenders.razor`, `LicensingUpdates.razor` (with
`LicensingUpdatesService.cs:136`), `OutOfOffice.razor`; still `IsNullOrWhiteSpace` on `AdminEventLog.razor` (the undo's
only gate, its refusal unrecorded) and `Migration.razor` (the confirm bar, the only gate for every batch and mailbox
action); none validated against ServiceNow when enabled.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/sweep-e` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-e`, from master `54dcfb40`)
**Commit**: `c7479ba0` (`fix(sweep-e, item 41): ...`) | **Reviewer**: implementer (candidate)

## Approach (as built) - the kd-7/18/24/45/48 shape

Each handler runs `TicketPresence.Refusal(t) ?? await ServiceNow.ValidateTicketAsync(t)` before its authorization
re-check and before anything it reads or writes, and records a refusal in that page's own convention with `refusedAt`
`TicketValidation` and `mutationAttempted` `false`:

| Page | Where | Refusal row |
|---|---|---|
| AD Attribute Editor | `ConfirmSave`, first in the try | `EditorService.LogSaveRefused` - `ADAttributeEdit_Refused` |
| Blocked Senders | `ConfirmUnblock`, first in the preflight try (a throw there is the existing fail-closed preflight refusal) | `UnblockSender_Denied` + refusal fields |
| Licensing Updates | `RunApply`, before the authorization re-check and the progress frame | `LicensingUpdates_Update`, target `(bulk) N rows` |
| Out of Office | `SetOof`, first in the try (the finally completes the activity) | `OutOfOffice_Denied` + refusal fields |
| Event Log undo | `ExecuteUndo`, after the handler lookup, before authorization; the blank-ticket silent return is gone | the module's own refused-undo record (`handler.LogUndoRefused`) |
| Migration | each of the four executors (`ExecuteBatchAction`, `ExecuteUserAction`, `ExecuteBulkBatchAction`, `ExecuteBulkMailboxAction`) via `TicketRefusalAsync` | `{action}_Denied` via new `MigrationService.RecordTicketRefusal` (targets for a bulk run) |

- Migration's `ConfirmPendingAction` no longer judges the ticket (its return was unrecorded); Confirm and Enter are enabled
  only for a present ticket (`IsPresentAsTyped`), so a blank one reaches an executor only by a raced click, and is refused
  and recorded there.
- `ServiceNowService` is an `[Inject]` property in each page's `@code`, not an `@inject` directive, so no markup line
  moves (Migration's line count only). The page test harnesses set a dormant ServiceNow (`TestServiceNow`).
- Help: README "Ticket numbers". Covered by the item-40 module bumps (comments extended).

## Where the code goes beside the fix shape

- None. Out of scope, recorded: Account Lockout Remediation's service still checks presence with
  `string.IsNullOrWhiteSpace` and asks no ServiceNow; it was not in item 41's list.

## Guard proof

| Mutation | Failing tests |
|---|---|
| U the ticket check removed from AD Attribute Editor | `AttributeEditorPageSaveAuditTests.ASaveWithNoUsableTicket_...` (3), `ASaveWithATicketServiceNowRejects_...` |
| U ... Blocked Senders | `BlockedSendersSubmittedUnblockTests.AnUnblockWithNoUsableTicket_...` (3), `AnUnblockWithATicketServiceNowRejects_...` |
| U ... Licensing Updates | `LicensingUpdatesApplyBusyGuardTests.AnApplyWithNoUsableTicket_...` (3), `AnApplyWithATicketServiceNowRejects_...` |
| U ... Out of Office | `OutOfOfficeRequestCaptureTests.ASetWithNoUsableTicket_...` (3), `ASetWithATicketServiceNowRejects_...` |
| U ... Event Log undo | `EventLogUndoRefusalTests.AnUndoWithNoUsableTicket_...` (3), `AnUndoWithATicketServiceNowRejects_...` |
| U ... Migration's executors | `MigrationDenialTests.AnActionWithNoUsableTicket_...` (12), `AnActionWithATicketServiceNowRejects_...` (4), `AConfirmWithABlankTicket_ReachesTheActionsRecordedRefusal_NotASilentReturn` |
| U7 Blocked Senders presence only, no ServiceNow | `AnUnblockWithATicketServiceNowRejects_...` |
| U8 Migration's confirm returns on a blank ticket again | `AConfirmWithABlankTicket_...`; `TicketPresenceTests.EveryTicketGatedControl_...` |

## Gates

Run once for the branch at its tip; see the sweep-e hand-back. Not run: any live write; ServiceNow is stubbed.

## Deploy-time checks

- None beyond item 40's: with ServiceNow dormant (both instances today) behaviour differs only for a blank or invisible
  ticket that reaches a handler, which the buttons already prevent.
