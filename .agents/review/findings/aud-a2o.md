# aud-a2o: Conference Rooms on-prem add - bulk row message omits the confirmed-membership detail, the no-op reuses the add's step label, and an unreachable error branch has no test

**Severity**: LOW - operability and dead code, no wrong write. (1) When a row succeeds only because
the read-back confirmed the membership (aud-a2n), the page's step and the audit `extra` carry the
detail but a bulk job row's message does not, so a bulk operator cannot tell a confirmed-present
row from an applied write. (2) The on-prem already-a-member no-op uses the same step label as a
real add, "Add to '<list>' (on-prem)", so the step list reads as if a write happened. (3) The
write's error-without-a-throw (`HadErrors`) branch cannot be reached under `-ErrorAction Stop`
and has no test; dead code that a later edit could make live and wrong.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a2m/a2n agent, 2026-10-22, candidates 3 to 5 of its hand-back)

## Evidence

At `5c116ce4`, `Services/ConferenceRoomService.cs` `AddToRoomListViaAdAsync` and its callers;
`Services/Jobs/ConferenceRoomBulkProcessor.cs` row message composition;
`Models/ConferenceRoomModels.cs` `DirectoryMembershipConfirmed`.

## Approach (to be written by the implementing agent before code)

- Bulk row message: when `DirectoryMembershipConfirmed` is set, the row message says the membership
  was confirmed present rather than written (same wording the page step uses).
- Step label: the no-op path reports a distinct step label, for example "Already on '<list>'
  (on-prem)", so the step list says what happened; audit unchanged (aud-a2g).
- Dead branch: either remove the `HadErrors` branch with a comment saying why `-ErrorAction Stop`
  makes it unreachable, or keep it and pin it with a test through a stand-in that writes a
  non-terminating error; pick the one that matches `GroupManagementService`'s handling and say so.
- Tests through `ExchangeWriteHarness` and the bulk processor tests: a confirmed-present bulk row's
  message; the no-op step label; the chosen treatment of the dead branch. Mutations at the call
  site: drop the detail from the bulk message; reuse the add's label.

## Guard proof

Pending.
