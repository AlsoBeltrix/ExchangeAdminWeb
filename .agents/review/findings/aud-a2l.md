# aud-a2l: The on-prem room-list add reports success without reading the membership back

**Severity**: LOW - no wrong object is written, but a write that raised no error and changed nothing
(a replication-lagged domain controller, a directory that silently dropped the value, a
constraint the server did not report) is audited and announced as done. Known Failure Class 2:
success must be confirmed, not inferred from the absence of an error.
**Status**: In progress - plan recorded; approved under the owner's 2026-10-21 "EVERYTHING" go
**Branch**: `worktree-agent-ae52370b2527142a4`
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21); the
admitted approach above, the folded-in race below and the supervising session's brief.

**Rule.** The on-prem add succeeds only when the group's `member` attribute, read back from the
group's own domain after the write, holds the room's DN (Known Failure Class 2).

- **The read-back.** After `Set-ADGroup -Add @{ member = <room DN> }` (aud-a2j),
  `ReadOnPremListMembership` - aud-a2i's read, the helper the preflight and the pre-write check call:
  `Get-ADGroup -Identity <group DN> -Properties member -Server ServerFromDn(groupDn)`, the answer's
  objectGUID checked, DNs compared ordinal-ignore-case. Present: `(null, roomGuid)`, written
  (aud-a2g). Absent: `(<message naming the room, the list, the group's DN and the server read>,
  null)` - the existing failed shape, so the row is partial after the Exchange writes and the audit
  carries no `writtenDirectoryObject`. A read-back that cannot be made fails the same way, as "could
  not be confirmed".
- **The race (folded in).** A write refused because the value is already there - recognized by
  `IsDuplicateMemberValue` (internal static) over the error's message chain: "already a member",
  "already exists", `ENTRY_EXISTS`, the forms Active Directory reports it in (*assumption*,
  documented error text, not probed) - is read back like a write that reported nothing: present is
  success, audited as written (the folded-in rule below), absent a failure. Every other write error
  stays the failure it was, with no read-back, the same message and log.
- No retry loop; the operator re-runs. **Unchanged:** the preflight, the pre-write membership check,
  the cloud path, every Exchange write, the step names and the success message.

**Tests** (`ExchangeWriteHarness`). The stand-in `Set-ADGroup` applies `-Add member` to
`DirectoryGroupMembers` and refuses a DN already there with Active Directory's duplicate-value error
(`DuplicateMemberValueError`); `SetAdGroupChangesNothing` makes it report success and change nothing;
`AnotherAddLandsFirst` puts the DN on the group just before the call - another actor's add. In
`ConferenceRoomRoomListPreflightTests`:
- the normal case: success, written, and a membership read after the write, of the group's DN on its
  domain;
- `Set-ADGroup` reports success and changes nothing: failed, partial, the message naming the group
  and the server, nothing recorded as written;
- the race: refused as a value already present with the room there - success, written; the same
  refusal with the room absent - failed, not written;
- another write error with the room present (another actor's add, then the write times out): failed,
  no read-back;
- the duplicate-value forms recognized, other errors not (pure).
In `ConferenceRoomDirectoryAuditTests`, page and bulk: changes nothing - Failed, no
`writtenDirectoryObject`; refused as present - Success and the room's objectGUID when the read-back
shows the room, Failed and none when it does not. Existing:
`TheGroupFoundInTheForest_IsReReadInItsOwnDomain_SoThePreflightAndTheAddResolveTheSameObject` counts
three membership reads, not two.

**Mutations, at the call site:** (3) the read-back dropped - success on no error, as before; (4) the
duplicate-value error a failure without a read-back; and (5) every write error read back, the group
modules' shape - against the brief's rule that other errors stay failures. Each: md5 before and
after, the named failing test, restored, md5 matched, touched, rebuilt, green. Comments after the
round.

**Files.** `Services/ConferenceRoomService.cs`, `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`,
`ExchangeAdminWeb.Tests/ConferenceRoomRoomListPreflightTests.cs`,
`ExchangeAdminWeb.Tests/ConferenceRoomDirectoryAuditTests.cs`, this record.

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
