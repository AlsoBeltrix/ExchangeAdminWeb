# aud-a7b: Out of Office captures only the mailbox; its other inputs are still live reads

**Severity**: MEDIUM - the mailbox can no longer change under an operation, but the state,
messages, schedule and ticket still can. An auto-reply can be written with a body or schedule
the operator had already moved on from, and the audit row can name a different ticket than the
one the change was made under.
**Status**: Merged to master; codex group review pending
**Branch**: `worktree-agent-a6325a13692998ce6`
**Commit**: `419ad2a` (merged)

## Evidence

Raised by codex-commercial (gpt-6-astra, max) reviewing the `aud-a7` fix, as a limit of that
fix rather than a defect in it.

`aud-a7` captured `var target = setEmail.Trim()` and uses it everywhere. The other inputs were
not captured: `Components/Pages/OutOfOffice.razor` reads `oofState`, the message fields, the
schedule and `ticketNumber.Trim()` at the write, and reads state and ticket **again** at the
audit call after further awaits.

Narrower than `aud-a7` was: the re-entry guard stops the same operator starting a second run,
so this needs the bound fields to change while one run is in flight. A two-way binding updating
from a component event during the awaited write is the realistic route.

## Predicted observable failure

An operator submits an auto-reply, edits the message or the end date while the Exchange write
is in flight, and the audit row records a ticket or state that does not match what was sent -
or the notification email describes a schedule that was never applied.

## Approach - NOT BUILT

Capture all of them next to `target`, as one snapshot taken before the first await, and use the
locals everywhere below. The shape already exists in this method; it was simply not extended
past the mailbox.

This is also where a behavioural harness belongs. The `aud-a7` guards are source scans, and
review proved concretely what that costs: they passed while the page had a stuck busy flag that
made the form unusable until reload. Comms-10k's reflection harness drives a real page
instance; extending it here would let both the capture and the flag be tested as behaviour
rather than as text.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"). Module-scoped: Out of Office only;
the base app is untouched.

1. **One immutable request.** `Models/LookupModels.cs`, beside `OutOfOfficeResult`:
   `public sealed record OutOfOfficeRequest(string Mailbox, string State, string? InternalMessage,
   string? ExternalMessage, DateTime? StartTime, DateTime? EndTime, string Ticket)` with
   `static FromForm(...)`. It trims the mailbox and the ticket, keeps the messages only when the
   state is not Disabled and the schedule only when it is Scheduled - exactly the expressions the
   page evaluated at the write, moved to one place and evaluated once.
2. **The service takes the request.** `OutOfOfficeService.SetOutOfOfficeAsync(OutOfOfficeRequest)`
   replaces the positional overload; the page was its only caller, so no path reaches the write
   except through a captured request.
3. **`SetOof` captures once, before the first await** - the Comms-10k C7 shape: re-entry guard,
   flag raise, then `var request = OutOfOfficeRequest.FromForm(...)`, and nothing after it reads a
   bound field. Schedule validation, the progress label and steps, both denial audits, the write,
   the success audit and its `newState`, both notifications and the exception-path audit all read
   `request`. `var target = request.Mailbox` keeps the A7 name.
4. **Tests - behaviour, not text.** A harness drives a real `OutOfOffice` page instance through
   reflection (Comms-10k's idiom) over a real `OutOfOfficeService` on a real pool whose connect
   seam returns an in-process runspace with stub `Get-`/`Set-MailboxAutoReplyConfiguration`
   functions recording their bound parameters; a real validator with nothing protected; a real
   `AuditService` over a temp JSONL root, read back; a substituted `EmailService` whose virtual
   admin notification is captured. Pause at the first await, inside the previous-state read and
   inside the write; change all seven bound fields at the pause; assert the cmdlet's parameters,
   the audit row and the admin notification all carry the request built before the first await.
   The denial path likewise (ticket changed during the pause; the denial row keeps the captured
   one), plus the flag the record asks for as behaviour: a refusal leaves the form usable, and a
   second submit while one is in flight writes nothing. The A7 source scan is generalised from the
   mailbox to all seven fields.
5. **ClickGate**: the page is tier 3 / not approved, so it carries no line-keyed entries. Checked
   by running the ClickGate tests rather than assumed.

## Guard proof

Built as the Plan. `OutOfOfficeRequestCaptureTests` is the behavioural harness;
`OutOfOfficeTargetCaptureTests` keeps the A7 scans, with the target scan generalised.

| Test | Pins |
| --- | --- |
| `AnEditDuringTheOperation_ChangesNeitherWhatIsWrittenNorWhatIsRecorded` (auth, read, write) | paused at the first await, inside the previous-state read and inside the write; all seven bound fields changed at the pause; `Set-MailboxAutoReplyConfiguration` was bound with the original mailbox, state, both messages and schedule; the `OutOfOffice_Set` row has the original target, ticket and `newState`; the admin notification has the original action, target and ticket |
| `ARefusal_RecordsTheCapturedTicket_AndLeavesTheFormUsable` | authorization re-check denies after an edit; nothing written; the `OutOfOffice_Denied` row keeps the captured target and ticket; `isSetting` is clear afterwards |
| `ASecondSubmitWhileOneIsInFlight_WritesNothing` | the A7 re-entry guard as behaviour: a second submit during the write returns at once and writes nothing; one write, one row, flag clear |
| `TheRequestKeepsOnlyWhatItsStateWrites` (Enabled, Scheduled, Disabled) | `FromForm` trims, keeps messages unless Disabled, keeps the schedule only when Scheduled |
| `SetOof_CapturesEveryBoundInputOnce_BeforeTheFirstAwait_AndNeverRereadsOne` (scan, replaces `SetOof_CapturesTheTarget_AndNeverRereadsTheEditableField`) | all seven fields appear in the capture, the capture precedes the first await, none appears after it; covers the protected-principal refusal and exception paths the harness does not drive |

Mutations at the call site by the scripted driver (apply, md5, rebuild, run the named tests,
restore, touch, md5 re-checked equal every time, final rebuild). Page pre-comment md5
`3825C9D76ED0665AB23765E583D1DCD6`; `Models/LookupModels.cs` pre-comment md5
`75D945FBCBBFC768E13FB80F3691DC41`.

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| M1 the write rebuilds its request from the live fields | `B65EDA4E6161C9195412BAA4C42614BD` | `AnEdit...(auth)`, `AnEdit...(read)`, the generalised scan. `(write)` passes, correctly: its parameters were bound before the pause |
| M2 the success audit reads `ticketNumber.Trim()` | `F3A6D791F94F6A36C9423815FEEBC8FF` | all three `AnEdit...` cases |
| M3 the success audit's `newState` reads `oofState` | `ED9F93DBDE4FBA7505F64154F9CF67AA` | all three `AnEdit...` cases |
| M4 the admin notification reads `oofState` and `ticketNumber` | `7E715B66DF6651FDE2745DB1185CCA30` | all three `AnEdit...` cases |
| M5 the re-check denial audit reads `ticketNumber.Trim()` | `65A837BE81AB52E3B7CE7FD72A0777E1` | `ARefusal_RecordsTheCapturedTicket_AndLeavesTheFormUsable` |
| M6 the re-entry guard removed | `3C468678085D3E103C979E3631192842` | `ASecondSubmitWhileOneIsInFlight_WritesNothing` |
| M7 the `finally` no longer clears `isSetting` | `F118D21BDE412343AF52394EB8BE1B1B` | `ASecondSubmit...`, `ARefusal...` |
| M8 (model) a Disabled request keeps its messages | `A13A23CBBE5603B0DD4AC7858172E9B7` | `TheRequestKeepsOnlyWhatItsStateWrites(Disabled)` |
| M9 (model) a non-Scheduled request keeps its schedule | `E120CAB251F3C7E3834F28BC71CD8047` | `TheRequestKeeps...(Enabled)`, `(Disabled)` |
| M10 (model) the ticket is not trimmed | `7BB1C32AC9BE079DCD15DF35F6309D7E` | all three `TheRequestKeeps...`, all three `AnEdit...` |

M1 to M4 are the defect's own shapes - each re-reads a bound field after the first await at one
call site. Comments were written after the round.

Gate at the committed state: Release build 0 errors / 61 warnings (unchanged); `dotnet test
ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` 4507 passed / 3 known AD skips /
0 failed (4499 before, +8: one A7 scan replaced by its generalisation), no hang; `dotnet format
--verify-no-changes` clean; `git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` clean after
staging.

ClickGate: `ClickGate*` tests green after the page edit; the page has no line-keyed entries, as
the registry's `"tier 3, not approved"` says. `ProgressRegistry*` and `GlobalProgressWiring*` green
(the registry names `OofService.SetOutOfOfficeAsync(`, still the call text).

## Files changed

- `Models/LookupModels.cs` (`OutOfOfficeRequest`)
- `Services/OutOfOfficeService.cs` (takes the request)
- `Components/Pages/OutOfOffice.razor` (`SetOof` captures once and reads only the request)
- `ExchangeAdminWeb.Tests/OutOfOfficeRequestCaptureTests.cs` (new)
- `ExchangeAdminWeb.Tests/OutOfOfficeTargetCaptureTests.cs` (scan generalised, remark updated)

## Known gaps

- The harness does not drive the protected-principal refusal (it needs a configured protected
  principal and its resolution) or the exception path; the generalised scan covers both as text.
- `servicedNote` is still a field the run assigns and later reads. No form control binds it and
  the re-entry guard keeps a second run out, so an edit cannot move it; left alone as out of scope.
- Module-scoped change: the Out of Office module version bump is the supervisor's
  (`Modules/ModuleCatalog.cs` is out of this branch's hands). The base app is untouched.

## Reviewer comments

Source: codex-commercial / `gpt-6-astra` / max, 2026-10-19. Flagged as an existing limitation,
distinct from the stuck flag that review also found.

## Closeout

Fixed on `worktree-agent-a6325a13692998ce6`, awaiting merge and the Out of Office module version
bump. Not pushed. Independent review pending.
