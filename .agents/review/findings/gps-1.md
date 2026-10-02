# gps-1: Background navigation prompt promises a status-bar completion notice that no longer exists

**Severity**: LOW - misleading guidance only; no data loss and no incorrect write. It sends the
operator to watch a surface that cannot report the event, so a finished job can go unnoticed.
**Status**: Verified
**Branch**: - (direct-to-main)
**Commit**: see Closeout

## Evidence
`Components/Shared/InFlightWorkGuard.razor:122` (pre-fix). `BackgroundMessage` returned
"... The status bar will tell you when it is done, and the results are on the Bulk Jobs page."
Commit `f048ca6` in the same reviewed range removed the frame's finished-job announcement, so
`GlobalProgress.razor` now reads only `GetActiveJobsBySubmitter`.

Trigger: an operator navigates internally while only a background job is running, accepts the
informational prompt, then waits on another page for the promised completion indication.

Predicted observable failure: when the job reaches a terminal state its line simply disappears
and the frame returns to Idle. Nothing announces success or failure, so the operator waits on a
surface that will never speak.

## Approach
Reworded to match the live-only contract: the bar shows the job WHILE it runs, and the result is
on the Bulk Jobs page when it is done. No promise the frame cannot keep.

## Files changed
- `Components/Shared/InFlightWorkGuard.razor`
- `ExchangeAdminWeb.Tests/InFlightWorkGuardTests.cs`

## Guard proof
`InFlightWorkGuardTests.TheBackgroundPromptDoesNotPromiseAStatusBarCompletionNotice` asserts the
stripped source contains neither "tell you when it is done" nor "status bar will tell", and does
contain "Bulk Jobs page". Probed by restoring the original wording: the test FAILED at the
assertion; restored (file touched so MSBuild rebuilds), 7/7 pass.

## Coder dispute
None. Verified against the code before accepting: the old text was present at the cited line and
the finished-job path was genuinely removed in the same range.

## Known gaps
None.

## Reviewer comments
Round 1 - Change review, candidate finding 1 of 2.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
  harness: codex-cli 0.159.0 (cache recorded 0.154.0; validated in this dispatch)
  base ad3c1e958b29c2eabd43822e1eac3242c86610a7
  head e5f9e5ce38fce7ac35f0e439b7554fbe5b530658
  capability proof: read `.agents/repo-guidance.md` OK; ran `git log --oneline ad3c1e9..e5f9e5c` OK
No reviewer verification round: repo policy makes those CRITICAL-only and owner-gated
(`.agents/decisions.md` 2026-08-31). LOW closes on the coder-side guard proof above.

## Closeout
Fixed on master. Raw reviewer output: `.agents/review/gps-sweep.result.json`.
