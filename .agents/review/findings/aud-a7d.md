# aud-a7d: a Scheduled auto-reply tells the mailbox owner it was "enabled"

**Severity**: LOW - a misleading notification, not a wrong write.
**Status**: Fixed on master `9364341c` (patch applied by the supervisor); closed on guard proof; codex ops-batch-5 clean
**Branch**: `worktree-agent-aec54e09518f47fc1` - forked at `ca6575a2`, before the `aud-a7b` code it
fixes, so built and gated on a `git archive` export of master `1c6038d5` and delivered as a patch
for the supervisor to apply
**Commit**: `9364341c`
**Reviewer**: codex / gpt-5.5-dzs / xhigh, services-batch-2, 2026-10-21

## Evidence

Raised by `codex / gpt-5.5-dzs / xhigh` reviewing the services batch at `98ac93b`, finding 3.
Verdict text: `.agents/review/services-batch-2.result.txt` in the main checkout (machine-local).
Line numbers below are at `1c6038d5`; the page moved since `98ac93b` (codex's line 434), the call
did not.

`Components/Pages/OutOfOffice.razor`, `SetOof`, line 440:

```razor
                    await Email.SendOofNotificationAsync(
                        target, currentUser,
                        request.State == "Disabled" ? "disabled" : "enabled");
```

`Services/EmailService.cs`, `SendOofNotificationAsync`, line 380:

```csharp
    public async Task SendOofNotificationAsync(string targetEmail, string performedBy, string action)
    ...
        var isEnabled = action != "disabled";
        var headerText = isEnabled ? "Auto-Reply Enabled" : "Auto-Reply Disabled";
    ...
            <p>Your mailbox auto-reply (Out of Office) has been <strong>{h(action)}</strong> by an administrator.</p>
    ...
        await SendEmailAsync(targetEmail, $"Your Auto-Reply Has Been {(isEnabled ? "Enabled" : "Disabled")}", body);
```

The write (`OofService.SetOutOfOfficeAsync(request, ...)`), the audit row (`newState` =
`request.State`) and the admin notification (`SetOutOfOffice_{request.State}`) all carry
`Scheduled`; only the owner's notification collapses it to a two-way word, and names no window.
The method was also not `virtual`, unlike the other affected-user notifications, so the `aud-a7b`
harness's substitute could not see it - the gap the reviewer named ("add a test for the
affected-user notification, not only the admin notification").

## Predicted observable failure

An administrator schedules a mailbox's auto-reply for next week. The owner receives "Your
Auto-Reply Has Been Enabled ... has been enabled by an administrator" today, with no window, and
reasonably believes it is already answering their mail.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"). Module-scoped behaviour: Out of Office.

1. `EmailService.SendOofNotificationAsync(string targetEmail, string performedBy,
   OutOfOfficeRequest request)` replaces the `string action` form, and is `virtual` like the other
   affected-user notifications. The page was its only caller.
2. The template moves into `internal static BuildOofNotification(performedBy, request,
   changedAt)` - the `BuildMessageTraceReadyBody` precedent in the same file, "extracted from the
   send so it is assertable without SMTP" - with one arm per state. Enabled and Disabled render as
   before. Scheduled renders "Auto-Reply Scheduled", "has been scheduled", the subject "Your
   Auto-Reply Has Been Scheduled", and From and Until lines with both times in the template's own
   date format, HTML-encoded.
3. The page passes its captured `request` - `aud-a7b`'s rule, never a bound field. The
   ProgressRegistry closing pin `Email.SendOofNotificationAsync(` is unchanged.
4. ClickGate: `ClickGateRegistry` lists `OutOfOffice.razor` as `"tier 3, not approved"`, so the page
   carries no line-keyed entries and its line count may change. Confirmed by grep, and the
   `ClickGate*`, `ProgressRegistry*` and `GlobalProgressWiring*` tests pass after the edit.
5. Test in the `aud-a7b` harness, as behaviour: submit, hold inside the write, edit every bound
   field, release; capture the owner's notification from the substitute and render the request it
   was given through the service's own builder.

## Guard proof

Built as the Plan. Test in `ExchangeAdminWeb.Tests/OutOfOfficeRequestCaptureTests.cs`.

| Test | Pins |
| --- | --- |
| `TheMailboxOwnerIsToldWhatWasWritten_AScheduleWithItsWindow` (`Scheduled`, `Enabled`, `Disabled`) | held inside the write with every bound field edited (state to Enabled, new times, another mailbox); the owner's notification goes to the captured mailbox, from the operator, carrying the captured request; rendered by the service's builder, a Scheduled write names Scheduled in the subject, header and sentence and both captured times; Enabled still says enabled and Disabled disabled, neither with a window |

The pre-fix method was not virtual, so the pre-fix code cannot host this test; M1 is the pre-fix
behaviour expressed through the new signature.

Mutations at the call sites, by exact-text replacement asserted to occur once, md5 confirmed, the
`OutOfOffice` tests rebuilt and run, the file restored from a saved copy, touched, and the md5
re-confirmed equal to the pre-comment state. Pre-comment md5s: page
`db819642ee1ea33ee04beed816615257`, `Services/EmailService.cs` `482dcb2c64ab6ceb3c7385c898909fcf`.

| Mutation | File | md5 after mutation | Failed |
| --- | --- | --- | --- |
| M1: the call site collapses the state to the two-way word again (`request with { State = request.State == "Disabled" ? "Disabled" : "Enabled" }`) | page | `b77388f63800bf442396e6586176740e` | 1: `(Scheduled)`, `Expected: "Scheduled"` |
| M2: the builder's Scheduled arm removed, so Scheduled falls through to Enabled | EmailService | `ca29373a16ca276ecbb0b654652820b1` | 1: `(Scheduled)`, subject `Expected: "Your Auto-Reply Has Been Scheduled"` |
| M3: the window not rendered | EmailService | `76cbf7bf0e986dc6de72be0f93a39208` | 1: `(Scheduled)`, `Not found: "November 02, 2026 at 9:00 AM"` |
| M4: the call site rebuilds the request from the bound fields after the awaits | page | `31efb7b0a1cc457555558469022ba5d6` | 3: `(Scheduled)`, `(Disabled)` and the `aud-a7b` scan `SetOof_CapturesEveryBoundInputOnce_BeforeTheFirstAwait_AndNeverRereadsOne` |

Comments were written after the round; md5s are of LF bytes. Final: page
`39d50ad866b9d094782f87f7ebd4bf14`, `Services/EmailService.cs` `9f6031599165bf1cb090b1016b53fe4e`,
test file `b90e07d4de6f7e10ff59268866ced17f`.

## Gates

On the export of `1c6038d5` with `aud-a6e`, `aud-a38f` and this change applied:

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 61 warnings (61 before).
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: 4996 passed,
  3 skipped (the known AD skips), 0 failed (4993 with `aud-a6e`, +3 for the three cases); no hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check` and `tools/Test-AsciiOnly.ps1` need a repository, which the export is not.
  Their rules were applied to the patch and its files instead: no trailing whitespace, space
  before tab or blank line at end of file in what it adds, and no byte above 0x7F in any added
  line - the changed test file is in the lint's scope; `Services/EmailService.cs` is excluded by
  the lint and `.razor` is outside it, and their added lines are ASCII anyway. Owed in the
  repository at apply.
- The patch applies unchanged to master `96d9dc4f`: none of the three files moved since
  `1c6038d5`.

## Files changed

- `Services/EmailService.cs` - `SendOofNotificationAsync` takes the request and is virtual;
  `BuildOofNotification` (internal); one `using`.
- `Components/Pages/OutOfOffice.razor` - the owner's notification is handed the captured request.
- `ExchangeAdminWeb.Tests/OutOfOfficeRequestCaptureTests.cs` - the test, its capture helper, the
  class remark.

## Known gaps

- The send wrapper (`SendOofNotificationAsync`: the `NotifyUsersOnPermissionGrant` gate, the
  builder call, the send) is substituted in the harness, so its three lines run in no test; the
  builder and the call site are what the test drives. No email was sent and SMTP was not exercised.
- The window is shown as entered, with no time zone, as the page and the write take it. How
  Exchange interprets those times is not verified here.
- Module-scoped change: the Out of Office module version bump is the supervisor's
  (`Modules/ModuleCatalog.cs` is out of this branch's hands). `Services/EmailService.cs` is shared
  by location, but the only member changed is the Out of Office notification, whose sole caller is
  this page; read as module-scoped - the supervisor's call whether the base version also moves.

## Reviewer comments

`Reviewer: codex / gpt-5.5-dzs / xhigh, services-batch-2, 2026-10-21`, finding 3 (LOW). Raw
verdict `.agents/review/services-batch-2.result.txt` in the main checkout (machine-local). The
reviewer's suggested approach - pass the captured request and render Scheduled with its times, with
a test of the affected-user notification - is the one built.

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent. Independent
review of the fix pending.

## Closeout

Fixed on branch and delivered as a patch; not merged, not pushed. Out of Office module version
bump due at merge.
