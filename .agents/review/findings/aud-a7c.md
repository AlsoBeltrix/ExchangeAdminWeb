# aud-a7c: Out of Office's status check audits whatever the mailbox box says after the read

**Severity**: LOW - read path only. The lookup audit row (who read which mailbox's auto-reply) can
name a mailbox other than the one that was read. Nothing is written. The box is disabled while
the read runs (`isChecking`), so it needs the same route `aud-a7b` names: a bound value updated
from a component event while the await is in flight.
**Status**: Closed - codex group review (services-batch-2, 2026-10-21) raised no defect in this change
**Branch**: `worktree-agent-a6325a13692998ce6`
**Commit**: `b8dd5d7` (merged)
**Reviewer**: implementer (found while fixing `aud-a7b`; same class)

## Evidence

`Components/Pages/OutOfOffice.razor`, `CheckStatus`. It reads `checkEmail.Trim()` for the progress
label and for the read, then, AFTER `await OofService.GetOutOfOfficeAsync(...)`, reads
`checkEmail.Trim()` again as the target of `Audit.LogLookupAction(..., "CheckOutOfOffice", ...)`,
and its catch builds the error result from `checkEmail`. The same post-await live read `aud-a7b`
removed from `SetOof`, on the read path of the same page.

## Predicted observable failure

An operator checks one mailbox's auto-reply, the box changes while the read is in flight, and the
audit trail records a lookup of the second mailbox - which was never read - and no lookup of the
first, which was.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"). Module-scoped: Out of Office.

1. Capture `var mailbox = checkEmail.Trim();` before the first await, and use it for the label, the
   read, the audit and the error result. Nothing after it reads `checkEmail`.
2. No re-entry guard. Adding one is ClickGate conversion, and the registry holds this page at
   "tier 3, not approved" - that needs an owner go of its own. The capture is this finding's class.
3. Tests: the `aud-a7b` harness, paused inside the read, with `checkEmail` changed at the pause -
   the `CheckOutOfOffice` row must name the mailbox that was read. A scan pins the capture on the
   paths the harness does not drive (the exception path).

## Guard proof

Built as the Plan. The harness's `Get-MailboxAutoReplyConfiguration` stub now also records the
identity it was asked to read.

| Test | Pins |
| --- | --- |
| `OutOfOfficeRequestCaptureTests.AStatusCheck_AuditsTheMailboxItRead_NotWhatTheBoxSaysAfterwards` | paused inside the read, `checkEmail` changed; the read was of the original mailbox, the `CheckOutOfOffice` row names it, `isChecking` clear |
| `OutOfOfficeTargetCaptureTests.CheckStatus_CapturesTheMailboxBeforeTheRead_AndNeverRereadsTheBox` (scan) | the capture precedes the first await and `checkEmail` appears nowhere after it, the exception path included |

Mutations at the call site by the scripted driver (apply, md5, rebuild, run the named tests,
restore, touch, md5 re-checked equal, final rebuild). Page pre-comment md5
`851419257CFF7ADE4B80AC14BC75EBC0`.

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| M1 the lookup audit reads `checkEmail.Trim()` after the read (the original line) | `6F60E7314D517CD36F845FA077B83DA7` | `AStatusCheck_AuditsTheMailboxItRead_...`, `CheckStatus_CapturesTheMailbox...` |
| M2 the error result reads `checkEmail` (the original line) | `6E4D1819EC71EB7F7256ACA547F09377` | `CheckStatus_CapturesTheMailbox...` |

M2 is caught by the scan only: the harness cannot reach the page's catch without a borrow that
fails synchronously, which never pauses. Comment written after the round.

Gate at the committed state: Release build 0 errors / 61 warnings (unchanged); `dotnet test
ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` 4510 passed / 3 known AD skips /
0 failed (4508 before, +2), no hang; `dotnet format --verify-no-changes` clean;
`git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` clean after staging. The ClickGate and
ProgressRegistry suites ran inside it, green: the page still has no line-keyed entries, and
`CheckStatus` still names `OofService.GetOutOfOfficeAsync`.

## Files changed

- `Components/Pages/OutOfOffice.razor` (`CheckStatus`)
- `ExchangeAdminWeb.Tests/OutOfOfficeRequestCaptureTests.cs` (the test; Get stub records its identity)
- `ExchangeAdminWeb.Tests/OutOfOfficeTargetCaptureTests.cs` (the scan)

## Known gaps

- No re-entry guard on `CheckStatus`, deliberately (Plan, item 2).
- Module-scoped change: the Out of Office module version bump is the supervisor's.

## Reviewer comments

Raised by the implementer while fixing `aud-a7b`. No independent review yet.

## Closeout

Fixed on `worktree-agent-a6325a13692998ce6`, awaiting merge and the Out of Office module version
bump. Not pushed. Independent review pending.
