# aud-a2s: The room-list preflight's and add's outer catches name no step but the import, and the add's catch blames Set-ADGroup for any step

**Severity**: LOW - wording in the log and the row message; no wrong write. The aud-a2q/a2r
outer-catch class on the two room-list paths: `PreflightRoomListAsync`'s catch and
`AddToRoomListViaAdAsync`'s catch name only the import step; the add's catch logs
"Set-ADGroup ... failed" and reports a write failure when the runspace, credential, catalog
resolution, re-read, membership read or read-back failed instead.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a2r agent, 2026-10-22, candidate 1 of its hand-back)

## Evidence

At `027c3309`, `Services/ConferenceRoomService.cs`: the two room-list paths' outer catches, compared
with the City/State/Country paths' step tracking added by aud-a2q (write) and aud-a2r (preflight).

## Approach (to be written by the implementing agent before code)

- Apply the same step tracking to both room-list paths: runspace, import, credential, catalog,
  group search, re-read, membership read, write, read-back; the catch names the step in the log line
  and in the row message; the write's own failure keeps its text. Tests through `ExchangeWriteHarness`:
  a failure injected at each step names that step on both paths. Mutation at the call site: collapse
  the step naming on each path.

## Guard proof

Pending.
