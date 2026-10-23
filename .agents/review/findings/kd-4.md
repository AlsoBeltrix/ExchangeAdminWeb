# kd-4: Intune Devices - the wipe's typed device-name confirmation is enforced only in the button's disabled expression

**Severity**: MEDIUM - a destructive action's confirmation lives only in the UI. `Components/Pages/IntuneDevices.razor` `WipeNameConfirmed` (about 404) disables the button; the wipe handler never re-reads the confirmation, so a stale render, a double-dispatch or a programmatic click wipes without the typed name matching.
**Status**: Verified on branch; awaiting merge and codex review
**Branch**: `fix/kd-misc` (worktree `D:\source\ExchangeAdminWeb-wt\kd-misc`, from master `eb017102`)
**Commit**: -
**Reviewer**: implementer (recorded in `.agents/state.md` "Known defects", re-verified 2026-10-23 as of `d881ea58`)

## Evidence

As above. Trigger: the handler runs while the typed name no longer matches (device changed under the dialog, or the click raced the re-render). Predicted failure: a wipe of a device whose name the operator did not type.

## Approach

Re-read at `eb017102`; every line below is that commit's.

- `Components/Pages/IntuneDevices.razor:401-404` - the confirm button,
  `@onclick="() => ExecuteActionAsync(device, confirmAction.Value)"` with
  `disabled="@(ActionsDisabled || string.IsNullOrWhiteSpace(actionTicket) || !WipeNameConfirmed(device, confirmAction.Value))"`.
- `:707-709` - `WipeNameConfirmed(IntuneDevice device, IntuneDeviceAction action) => action != IntuneDeviceAction.Wipe
  || string.Equals(wipeConfirmName.Trim(), DeviceLabel(device), StringComparison.OrdinalIgnoreCase);` - named nowhere
  but that attribute, so the button's markup was the only check.
- `:941-1152` - `ExecuteActionAsync` binds its inputs before its first await (`:945-969`, the first await `:982`),
  re-checks the granular policy (`:1013-1021`), the ticket's presence and validity (`:1026-1038`), sets
  `notifyAdmins = true` (`:1040`), runs the protected-principal check, and writes at `:1152`
  (`PerformActionAsync(action, deviceId, wipeOptions, entraDeviceId)`) - it never reads `wipeConfirmName`.
  `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` (the IntuneDevices entry's remarks) recorded exactly this gap and
  the shape its fix should take.

What changes, in that recorded shape:

1. **Read with the other inputs.** `var typedNameMatches = WipeNameConfirmed(device, action);` joins the bindings
   above the first await, so it is the name as typed when the click was made, checked against the `device` this call
   acts on (`deviceId = device.Id`, `deviceName = DeviceLabel(device)`). `WipeNameConfirmed` answers true for every
   action but a wipe, so nothing else gains a step.
2. **Refused with the other pre-gates.** Between the ticket gates and `notifyAdmins = true`: `if (!typedNameMatches)`
   -> `Refuse("The typed device name does not match this device, so it was not wiped. ...", "Typed device name does
   not match the device - wipe not confirmed")`; `return;`. `Refuse` audits as it refuses (one Failed
   `IntuneDevices_Wipe` row with the ticket and the target) and sets the device's on-screen verdict; like the
   authorization and ticket gates it never reached the device, so no administrator mail. The write stays unreachable.
3. **Line keys.** The page is line-keyed by `ClickGateRegistry`; every registered line on it is at line 523 or earlier and
   the edit is in `@code` below 700, so only `ExpectedLineCount` moves (per `.agents/playbooks/clickgate-reanchor.md`,
   verified by the ClickGate tests). `ProgressRegistry` keys this page by method and call fragment; none changes.
   The registry's IntuneDevices remarks, its Confirm entry's `RendersOnlyWhen` and the `PreserveClauses` doc stated
   the gap as current; they are updated to say the handler re-checks the name, and the markup clause stays registered
   as the button's precondition.

Declared files: `Components/Pages/IntuneDevices.razor`, `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` (the
IntuneDevices entry's line count and prose; the `PreserveClauses` doc; one "is" -> "was" in DhcpAuthorization's
entry, which compares itself to this clause), `ExchangeAdminWeb.Tests/IntuneDevicesPageTests.cs` (the early-exit
census goes 9 -> 10; a capture-ordering tripwire), `ExchangeAdminWeb.Tests/IntuneDevicesWipeNameRecheckTests.cs`
(new), this record. The module version bump is the branch's version commit.

Mutation: the handler check removed.

## Files changed

- `Components/Pages/IntuneDevices.razor` - `ExecuteActionAsync` binds `typedNameMatches` above its first await and
  refuses on it between the ticket gates and `notifyAdmins = true` (gate "2b"); the `WipeNameConfirmed` summary says
  it is read twice. 1668 -> 1685 lines, every added line below 700.
- `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` - IntuneDevices `ExpectedLineCount` 1668 -> 1685 (no registered line
  moved: all are at line 523 or earlier); the entry's first two remarks paragraphs, its "no snapshot" paragraph, the Confirm
  control's `RendersOnlyWhen`, the `PreserveClauses` doc (which now defines "the IntuneDevices 403 shape" as the
  pre-kd-4 state the other entries name), and DhcpAuthorization's "the way IntuneDevices 403 is" -> "was".
- `ExchangeAdminWeb.Tests/IntuneDevicesPageTests.cs` - the early-exit census 9 -> 10 with the new exit named;
  `IntuneDevices_ExecuteAction_ReadsTheTypedNameBeforeItsFirstAwait_AndRefusesBeforeTheWrite` (code view).
- `ExchangeAdminWeb.Tests/IntuneDevicesWipeNameRecheckTests.cs` - new.
- This record.

## Guard proof

`IntuneDevicesWipeNameRecheckTests` drives the compiled page's `ExecuteActionAsync` by reflection (the
AttributeEditor and Comms10k page-handler pattern) after `BeginAction` opened the confirm bar, with a real
`AuditService` over a temp log root, a dormant `ServiceNowService`, granting authorization, and an
`IntuneDeviceService` whose Graph client factory counts the write's attempts and returns no client. The device has
no primary user, so nothing but the typed name stands between the confirm and the write.

- `AWipeWhoseTypedNameDoesNotMatchTheDevice_IsRefusedAndAudited_AndNeverReachesGraph` (another device's name, one
  keystroke short, nothing typed): zero Graph attempts; one Failed `IntuneDevices_Wipe` row with the target, the
  ticket and "Typed device name does not match"; the device's on-screen verdict is a failure that says so.
- `AWipeWhoseTypedNameMatches_ReachesTheWrite` (the control: any case, surrounding spaces): one Graph attempt.
- `ARetireAsksForNoTypedName`: one Graph attempt with nothing typed.

Mutation (the handler check removed: `if (!typedNameMatches)` -> `if (false)`), applied by exact replacement, md5
`b273d65c` -> `550d3bb6`, built, run against `FullyQualifiedName~IntuneDevices` (230 tests): **4 failed** - the three
mismatch rows (`Expected: 0, Actual: 1` Graph attempts: the wipe was sent with the wrong name) and the ordering
tripwire. Restored byte-exact (md5 `b273d65c` again) with a fresh mtime, rebuilt, rerun: 230 passed.

Re-anchor: `FullyQualifiedName~ClickGate` with the Intune, progress and wiring filters, 623 passed, before the
mutation round.

## Coder dispute

None.

## Known gaps

- The residual double-dispatch on Confirm (the registry's remarks, "a finding, not a fix") is not closed by this: a
  second dispatch that reaches the handler before the first's `finally` resets the confirm options still reads a
  matching name. It stays the owner's decision recorded there (a handler guard swallows legitimate clicks).
- The refusal uses the method's existing `Refuse` shape (`LogModuleAction`, Failed, the detail in `error`); like the
  other nine refusals there it carries no `refusedAt` / `mutationAttempted` fields.
- No live check: no Graph call was made; rendering is not exercised (no bUnit harness, `.agents/decisions.md`
  2026-10-01).

## Reviewer comments

None yet (codex review pending, dispatched by the supervising session).

## Closeout

Pending: merge into master, codex review, branch deletion.
