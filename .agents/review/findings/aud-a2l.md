# aud-a2l: The on-prem room-list add reports success without reading the membership back

**Severity**: LOW - no wrong object is written, but a write that raised no error and changed nothing
(a replication-lagged domain controller, a directory that silently dropped the value, a
constraint the server did not report) is audited and announced as done. Known Failure Class 2:
success must be confirmed, not inferred from the absence of an error.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a2j agent, 2026-10-22, candidate 3 of its hand-back)

## Evidence

At the aud-a2j branch tip `62b4a39f`, `Services/ConferenceRoomService.cs`,
`AddToRoomListViaAdAsync`: the member-attribute write (`Set-ADGroup` on the group's own domain) is
followed by no read of the group's `member` attribute; `ps.HadErrors == false` is taken as
success. Both group modules do it the other way: `GroupManagementService` and
`SelfServiceGroupService` re-read membership after a write on the same `-Server` and report a
failure when the member is absent.

## Approach (to be written by the implementing agent before code)

- After the write, re-read `member` through the aud-a2i read (`Get-ADGroup -Properties member
  -Server <ServerFromDn(groupDn)>`), compare by DN (the aud-a2i rule), and treat absence as a
  failed row with a message naming the group and the server that was asked. No retry loop; the
  operator re-runs.
- The audit row records the write as failed when the read-back misses, with `writtenDirectoryObject`
  omitted (aud-a2g rule: name only what a write produced).
- Tests through `ExchangeWriteHarness`: a stand-in whose `Set-ADGroup` succeeds but whose
  subsequent member read lacks the room (failed row, audit says failed); the normal case (read-back
  present, success). Mutation at the call site: drop the read-back.

## Guard proof

Pending.
