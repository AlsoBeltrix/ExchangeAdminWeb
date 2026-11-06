# rev-d2: MFA Reset's double-click guard clears before the handler finishes, so a queued second click can reset again

**Severity**: MEDIUM - a second executable mutation (reset, audit row, notification). sweep-e item 42 guards the handler
with `if (isLoading) return;`, but `isLoading = false` is set before the admin notification is awaited; a second click queued
during that await is admitted, `listedUpn` is still set, the handler does not require `resetConfirmPending`, and
`ResetAllMethodsAsync(upn)` runs again.
**Status**: Admitted; awaiting fix on branch
**Lines** (master `dcf13a16`): `Components/Pages/MfaReset.razor:294`, `:483`, `:507-526`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review D, range `fe3b86b8..de53b714`, dispatched 2026-11-06 from master `dcf13a16`; report `.agents/review/rev-d.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

A dedicated reset-in-progress flag set before the first await and cleared in the outermost `finally` after the
notification; the guard tests it. `EntryBusyGuardTests` only presets the flag; add a test that suspends notification
delivery and invokes the handler twice. Check the other item-42 handlers for the same early release. ClickGate re-anchor if
lines move.
