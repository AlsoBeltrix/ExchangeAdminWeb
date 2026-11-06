# sweep-e-42: write handlers that are their own click target ran a second time on a stale second click (sweep item 42)

**Severity**: MEDIUM - `.agents/review/live-risk-sweep-2026-10-30.md` item 42: no entry busy guard on Mailbox and
Calendar Permissions (single and bulk), Conference Rooms (both CSV applies), M365 Groups save, Named Locations save,
DHCP authorize and remove, AD Group Management add and remove, MFA reset, Admin Bulk Jobs cancel and remove; on Intune
Devices and Risky Users the stale second Confirm threw and ended the circuit. kd-72 and kd-74 had fixed five pages.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/sweep-e` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-e`, from master `54dcfb40`)
**Commit**: `6e3fb26c` (`fix(sweep-e, item 42): ...`) | **Reviewer**: implementer (candidate)

## Approach (as built) - kd-72's shape

- `if (<own flag>) return;` as each handler's first statement, on the flag the handler itself raises before its first
  await and lowers in its finally (both verified for every handler): `isLoading` (Mailbox/Calendar Permissions
  `SubmitSingle`/`ProcessBulk`, AD Group Management `AddMember`/`RemoveMember`, MFA Reset `ExecuteReset`),
  `isCsvProcessing` (Conference Rooms `ApplyFinderCsv`/`ApplyTypeCsv`), `isOperating` (M365 `SaveGroup`, Named Locations
  `SaveLocation`, DHCP `AuthorizeServer`/`RemoveServer`). The handler's own flag rather than the page's wider `IsBusy`,
  so a click is not swallowed because an unrelated read is running. None of these handlers is a registered
  ForbiddenGuardSite and none is called from another handler.
- Admin Bulk Jobs had no flag: `isActing` wraps `CancelJob`/`RemoveJob` (the bodies move to `CancelJobCore`/`RemoveJobCore`
  unchanged) and disables both row buttons (`disabled="@isActing"`, same lines).
- Intune Devices and Risky Users: the Confirm lambda passed `confirmAction.Value`, read at the click after the first
  Confirm had cleared it, so the second threw. It now passes `confirmAction`, and `ExecuteActionAsync` takes the nullable
  and returns at once when nothing is staged or when `actingDeviceId`/`actingUserId` is set. It names the handler's own
  flag, not `ActionsDisabled`, so the registry's recorded owner decision about clicks during a detail read stays open
  (registry note updated; ExactlyOneGuard stays empty).
- Tripwires that named the old signatures or counted early exits were updated: `ProtectionCheckCallSiteTests`
  signatures, `AdminBulkJobsRevocationTests` now reads the `*Core` methods, and the Intune/Risky early-exit census starts
  after the entry guard, which it asserts is the first statement above the first await. ClickGate line counts and the
  Intune Confirm snippet updated.
- No help change: the pages behave as their help already says. AdminBulkJobs `1.2.3`; the other pages are covered by
  this branch's earlier bumps.

## Guard proof (`EntryBusyGuardTests`, a bare page with the flag raised: guarded completes at once, unguarded yields or faults)

| Mutation | Failing tests |
|---|---|
| G each of the 13 `if (<flag>) return;` guards removed, one at a time | that handler's `ASecondClickWhileTheWriteRuns_ReturnsAtOnce_AndLeavesTheFlagUp` case (13 of 13 caught) |
| J1/J2 Admin Bulk Jobs guard removed (cancel, remove) | that handler's case |
| I1 Intune / Risky acting check removed | `ASecondConfirmWhileAnActionRuns_IsIgnored` (each page) |
| I2 Intune / Risky null Confirm dereferenced again | `AStaleConfirmWithNothingStaged_IsANoOp_NotAThrow`, `ASecondConfirmWhileAnActionRuns_IsIgnored` (each page) |
| I3 the Confirm lambda reads `.Value` again | Intune: `ClickGateTests.AnnotatedControlsKeepTheirNonBusyClauses`; both: `TheConfirmButton_HandsOverWhatIsStaged_NeverReadsItsValueAtTheClick` |

`TheJobButtons_AreDisabledWhileACancelOrRemovalRuns` pins the two new disabled attributes.

## Gates

Run once for the branch at its tip; see the sweep-e hand-back. Not run: a browser double-click on a deployed page.
