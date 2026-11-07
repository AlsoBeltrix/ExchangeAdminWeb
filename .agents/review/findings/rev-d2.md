# rev-d2: MFA Reset's double-click guard clears before the handler finishes, so a queued second click can reset again

**Severity**: MEDIUM - a second executable mutation (reset, audit row, notification). sweep-e item 42 guards the handler
with `if (isLoading) return;`, but `isLoading = false` is set before the admin notification is awaited; a second click queued
during that await is admitted, `listedUpn` is still set, the handler does not require `resetConfirmPending`, and
`ResetAllMethodsAsync(upn)` runs again.
**Status**: Fixed on branch; awaiting review
**Branch**: fix/rev-abc
**Commit**: e30c115a (and cd088bb6, a test-only warning fix)
**Lines** (master `dcf13a16`): `Components/Pages/MfaReset.razor:294`, `:483`, `:507-526`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review D, range `fe3b86b8..de53b714`, dispatched 2026-11-06 from master `dcf13a16`; report `.agents/review/rev-d.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

A dedicated reset-in-progress flag set before the first await and cleared in the outermost `finally` after the
notification; the guard tests it. `EntryBusyGuardTests` only presets the flag; add a test that suspends notification
delivery and invokes the handler twice. Check the other item-42 handlers for the same early release. ClickGate re-anchor if
lines move.

## As built (fix/rev-abc `e30c115a`, `cd088bb6`)

Verified real: `isLoading = false` is the first statement of the outermost finally, before the notification await; the
entry guard tested only it, and the handler does not require `resetConfirmPending`.
- `resetInProgress`: raised beside the entry guard (before the first await), lowered on the one exit above the try and at
  the very end of the outermost finally (after the notification and the progress completion, in its own finally). The guard
  tests `isLoading || resetInProgress`. `isLoading` still drops where it did.
- Every other item-42 handler checked: each lowers its flag after its last await (AdminBulkJobs x2, Calendar Permissions x2,
  Conference Rooms x2, DHCP x2, Group Management x2, M365 Groups, Mailbox Permissions x2, Named Locations). Intune Devices'
  and Risky Users' Confirm clear `actingDeviceId`/`actingUserId` before the notification, but the handler requires the staged
  `confirmAction`, which the first click consumed, so a queued second Confirm is a no-op (`EntryBusyGuardTests`). Unchanged.
- No ClickGate re-anchor: MfaReset.razor is not in the registry. MfaReset 1.6.3.

## Guard proof

| Test | Fails on the old code (guard reverted to `isLoading` only) |
| --- | --- |
| `MfaResetNotificationGuardTests.ASecondClickWhileTheNotificationSends_IsIgnored_AndTheResetPathRunsOnce` (notification held open, clicked twice) | yes |
| `EntryBusyGuardTests.ASecondClickWhileTheWriteRuns_ReturnsAtOnce_AndLeavesTheFlagUp(MfaReset, ExecuteReset, resetInProgress)` | yes |
