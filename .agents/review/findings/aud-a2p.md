# aud-a2p: The on-prem City/State/Country write is not confirmed by a read-back, keeps a dead HadErrors branch, and an unused success message claims a write

**Severity**: LOW - no wrong object is written. `SetSyncedAttributesViaAdAsync` (the Room Finder
City/State/Country `Set-ADUser` on a synced room) treats "no error" as done with no read-back, the
aud-a2l shape Known Failure Class 2 forbids; it also keeps an error-without-a-throw (`HadErrors`)
branch that cannot fire under `-ErrorAction Stop` (it fails closed, so the harm is only dead code).
Separately, `AddToRoomListAsync` still builds "Membership was written on-prem ..." for the no-op and
the confirmed-present rows; the caller replaces success messages today, so it is unused, but wrong if
ever shown.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a2o agent, 2026-10-22, candidates 1 and 2 of its hand-back)

## Evidence

At `15de2fcf`, `Services/ConferenceRoomService.cs`: `SetSyncedAttributesViaAdAsync` (the `Set-ADUser`
write by the room's objectGUID on its own domain, aud-a2d; no `Get-ADUser` read of the written
attributes after it; `if (ps.HadErrors)` after an `-ErrorAction Stop` invoke); `AddToRoomListAsync`,
`BuildRoomListAddedMessage(viaOnPrem: true)` on the no-op and confirmed-present paths.

## Approach (to be written by the implementing agent before code)

- After `Set-ADUser`, read the three attributes back (`Get-ADUser -Identity <objectGUID> -Properties
  l,st,co -Server <same DC>`) and compare to what was written (ordinal, trimmed as the write trims);
  a mismatch or a failed read is a failed step naming the attribute and the server; the audit's
  `writtenDirectoryObject` (aud-a2g) is recorded only when the read-back matches. Same shape as
  aud-a2l for the room list.
- Remove the dead `HadErrors` branch the way aud-a2o did for the group write (try/catch/finally; the
  read-back decides), with the comment saying why.
- The no-op and confirmed-present paths build the message that describes them (no "was written"
  claim); or remove the unused message if the caller never shows it, with a test pinning that the
  no-op's result message does not claim a write.
- Tests through `ExchangeWriteHarness`: a `Set-ADUser` that applies (read-back matches, success,
  written object recorded); one that raises no error and changes nothing (failed step, nothing
  recorded as written); a read-back that fails (failed step); the dead branch gone (a stand-in
  writing a non-terminating error is not taken for the write's error). Mutations at the call site:
  drop the read-back; restore the `HadErrors` branch; restore the "was written" message on the no-op.

## Guard proof

Pending.
