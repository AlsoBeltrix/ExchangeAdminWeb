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

## Folded in: the duplicate-value race (codex, a2j review, 2026-10-22)

codex's review of aud-a2j (`prompt-a2j.md`) raised one LOW candidate that belongs to this record
rather than its own: when the a2i membership read says "not a member" and another actor adds the
same room DN before `Set-ADGroup -Add @{member=<room DN>}` runs, Active Directory raises an
"attribute or value already exists" error for the linked-attribute add, and the code treats every
`Set-ADGroup` error as a failed row (around `Services/ConferenceRoomService.cs:1705`). The row
is then reported PARTIAL after the Exchange metadata writes even though the membership is present;
a re-run says "already a member". The read-back this record adds is the answer to both: after the
write, whatever `Set-ADGroup` reported, re-read `member` on the group's own domain; present means
success (a duplicate-value error included), absent means a failed row. Keep every other write
error a failure. Test: a stand-in whose `Set-ADGroup` raises the duplicate-value error while the
read-back shows the room present (success, audited as written); the same error with the room
absent (failure).
