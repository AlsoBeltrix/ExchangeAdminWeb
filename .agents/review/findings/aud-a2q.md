# aud-a2q: Conference Rooms - the cloud no-op builds an "Added" message, the attribute write's outer catch blames Set-ADUser for any step, and a commit-then-throw leaves a partial row

**Severity**: LOW - no wrong write. (1) The cloud already-a-member path in `AddToRoomListAsync`
(step "Already in '<list>'") still builds "Added to room list '<list>'.", an unused message claiming
an add that never happened (the on-prem twin was fixed in aud-a2p). (2) `SetSyncedAttributesViaAdAsync`'s
outer catch logs "Set-ADUser synced-attribute write failed" and reports "Failed to write
City/State/Country" for failures of the surrounding steps too (runspace open, module import, the
confirming read), the log-line class aud-a2n fixed for the group add. (3) A `Set-ADUser` that
commits and then throws (a timeout) still leaves a partial row though the values are in place;
aud-a2n reconciles the group write by read-back after any error, aud-a2p's read-back runs only after
a clean return.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a2p agent, 2026-10-22, its three candidates)

## Evidence

At `28ffde25`, `Services/ConferenceRoomService.cs`: `AddToRoomListAsync` cloud branch's no-op
message; `SetSyncedAttributesViaAdAsync` outer catch and its message; the write's `try/finally`
(aud-a2p) with the read-back after it.

## Approach (to be written by the implementing agent before code)

- Cloud no-op: build the "already in" message (or remove the unused one) with a test pinning that the
  cloud no-op's result message does not claim an add.
- Outer catch: name the step that failed (runspace, import, object read, write, read-back) in the log
  and the row message; the write's own failure keeps its text.
- Commit-then-throw: hold the write error (aud-a2o/a2n try/catch/finally shape), read the attributes
  back, and when they match report success with a "confirmed after a write error" detail and no
  `writtenDirectoryObject` (aud-a2g; the aud-a2n `DirectoryMembershipConfirmed` precedent, a sibling
  flag for attributes), keeping the error text in the step detail; a read-back that misses stays a
  failed row carrying the write error.
- Tests through `ExchangeWriteHarness`: the cloud no-op message; a failure injected at each
  surrounding step names that step; a `Set-ADUser` that applies then throws is settled by the
  read-back with no written object recorded. Mutations at the call site: restore the "Added"
  message; collapse the step naming; drop the read-back after a thrown error.

## Guard proof

Pending.
