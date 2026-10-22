# aud-a2l: The on-prem room-list add reports success without reading the membership back

**Severity**: LOW - no wrong object is written, but a write that raised no error and changed nothing
(a replication-lagged domain controller, a directory that silently dropped the value, a
constraint the server did not report) is audited and announced as done. Known Failure Class 2:
success must be confirmed, not inferred from the absence of an error.
**Status**: Fixed on branch, not merged; independent review pending; dev acceptance owed with a2f/a2i/a2j/a2k
**Branch**: `worktree-agent-ae52370b2527142a4`
**Commit**: plan `b836c6d7`; fix, tests and comments in the commit after aud-a2k's `3341e112`
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

## Approach (as implemented)

- `AddToRoomListViaAdAsync` (`Services/ConferenceRoomService.cs`): the write is aud-a2j's, its
  `-Server` now held in `groupServer` (`ServerFromDn(groupDn)`). It runs in `try` / `catch (Exception
  ex) when (IsDuplicateMemberValue(ex))` / `finally`: the duplicate refusal is held in
  `refusedAsPresent`; any other thrown error passes the filter and reaches the method's existing catch
  ("Failed to add the room to the on-prem room list on the AD object: ...", logged) exactly as before; a
  non-thrown `HadErrors` returns its first error, as before; the `finally` clears the pipeline's commands
  and error stream for the read that follows.
- Then the read-back: `ReadOnPremListMembership(ps, groupGuid, groupDn, roomDn, ...)` - the aud-a2i
  helper, unchanged - with a throw caught as a read error. An error: `("Adding <room> to on-prem room
  list '<list>' could not be confirmed: reading its group <group DN> back from <server> failed: ...",
  null)`. Not a member: `("Adding <room> to on-prem room list '<list>' was not confirmed: its group <group
  DN>, read back from <server>, does not hold the room as a member[, though Active Directory refused the
  add as a value already there (<error>)].", null)`. Each logs a warning. A member: `(null, roomGuid)`,
  the duplicate refusal included. The row turns either failure into the existing partial ("Partial: room
  metadata was set, but adding the room to its room list failed: ...") with no `WrittenDirectoryObject`.
- `IsDuplicateMemberValue(Exception)` - the message of the exception and of every inner exception - and
  `IsDuplicateMemberValue(string?)`, internal static: case-insensitive "already a member", "already
  exists" or `ENTRY_EXISTS`.
- Harness: `DuplicateMemberValueError` (the Win32 1378 text), `SetAdGroupChangesNothing` and
  `AnotherAddLandsFirst`, passed to the directory runspace when it opens. The stand-in `Set-ADGroup` puts
  the `-Add` DN on `DirectoryGroupMembers` unless told to change nothing, refuses a DN already there
  (`Write-Error` then `return`, aud-a2h), and - told another add lands first - puts the DN there before
  recording the call, so `Failures['Set-ADGroup']` can still throw after it.
- Tests (`ConferenceRoomRoomListPreflightTests`): `TheOnPremAdd_IsConfirmedByReadingTheGroupsMemberAttributeBack`,
  `AnOnPremAddTheGroupDoesNotShowAfterwards_IsAFailedRow_NotAWrite`,
  `AnOnPremAddRefusedAsAValueAlreadyThere_IsSettledByTheReadBack` (the room there / not),
  `AnyOtherWriteError_StaysAFailure_EvenWithTheRoomThere`,
  `ADuplicateValueRefusal_IsRecognisedInEachFormItIsReported` (nine cases, each as a string and as an
  inner exception) and `TheHarness_SetADGroup_AddsTheMember_RefusesOneAlreadyThere_AndCanDropOrBeRaced`;
  (`ConferenceRoomDirectoryAuditTests`, page and bulk) `AnOnPremAddTheReadBackDoesNotShow_IsNotRecordedAsWritten`
  and `AnOnPremAddRefusedAsAValueAlreadyThere_IsRecordedAsWrittenOnlyWhenTheReadBackShowsTheRoom`.
  Existing, as planned: `TheGroupFoundInTheForest_IsReReadInItsOwnDomain_SoThePreflightAndTheAddResolveTheSameObject`
  counts three membership reads.

## Guard proof

| Test | What it pins |
| --- | --- |
| `TheOnPremAdd_IsConfirmedByReadingTheGroupsMemberAttributeBack` | the normal case: success, not partial, the room's objectGUID written; three membership reads, the third after `Set-ADGroup`, by the group's DN, `-Properties member`, `-Server` its domain |
| `AnOnPremAddTheGroupDoesNotShowAfterwards_IsAFailedRow_NotAWrite` | the finding: `Set-ADGroup` raises nothing and changes nothing - failed, partial, nothing written; the message names the group's DN and the server read (`fabrikam.test`, the group in the other domain) |
| `AnOnPremAddRefusedAsAValueAlreadyThere_IsSettledByTheReadBack` (there / not) | the race: refused as a value already there and read back either way (three reads); the room there - success, not partial, written; absent - failed, partial, not written |
| `AnyOtherWriteError_StaysAFailure_EvenWithTheRoomThere` | another error (a timeout) with another actor's add of the room landing first: failed, partial, not written, no read-back (two reads) |
| `ADuplicateValueRefusal_IsRecognisedInEachFormItIsReported` (nine) | the 1378, 8205 and ADSI texts, the LDAP diagnostic and PowerShell's `-ErrorAction Stop` wrapper are duplicates, as strings and as inner exceptions; access denied, the timeout, not found and "" are not |
| `TheHarness_SetADGroup_AddsTheMember_RefusesOneAlreadyThere_AndCanDropOrBeRaced` | the stand-in: the add puts the DN on the group; the same add again throws an exception `IsDuplicateMemberValue` recognizes (the one PowerShell throws, not a string); told to change nothing it changes nothing; told another add lands first, the DN is there and the write is refused |
| `ConferenceRoomDirectoryAuditTests.AnOnPremAddTheReadBackDoesNotShow_IsNotRecordedAsWritten` (page, bulk) | the audit: Failed, no `writtenDirectoryObject` |
| `ConferenceRoomDirectoryAuditTests.AnOnPremAddRefusedAsAValueAlreadyThere_IsRecordedAsWrittenOnlyWhenTheReadBackShowsTheRoom` (page, bulk x there / not) | the audit: Success and the room's objectGUID when the read-back shows the room; Failed and none when not |
| `TheGroupFoundInTheForest_IsReReadInItsOwnDomain_SoThePreflightAndTheAddResolveTheSameObject` (existing, count changed) | aud-a2j's order, with the read-back as the third membership read |

**Mutations** (`scratchpad/a2k-a2l-round/round.py`, log `round.log` beside it, 2026-10-22 09:17-09:20;
the a2k runner, same rules). M and revert rows: `Services/ConferenceRoomService.cs`, md5 before =
restored = `fe43e7cd98485e280a00aa317755dace`. H rows: `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`,
before = restored = `793d1d3b8f3c583e1c33a1f29e41d4ab`. Test names are in
`ConferenceRoomRoomListPreflightTests` unless qualified (`Audit.` is `ConferenceRoomDirectoryAuditTests`).

| # | Mutation | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- |
| revert | the service file replaced whole by HEAD's (`3341e112`, the aud-a2k commit); and, so the test file compiles against it, the two tests naming the new `IsDuplicateMemberValue` removed (`ConferenceRoomRoomListPreflightTests.cs` `9865bce2afcb656cb918b50c1e8fcf09` -> `44b9efc850ebdd0bb000020d8bcbb121`, restored) | `b2d329f1ff23e6d6e1388d1846cd3c11` | the seven behaviour tests | FAIL, 10 of 13 cases: no read-back (two membership reads, not three) in the normal, both race and the a2j order tests; the changes-nothing row a success, on both audit surfaces too; the race with the room there Failed on both surfaces. PASS, 3, as expected: the other-error test and the race with the room absent on both surfaces - outcomes the old code shares |
| M3 | the brief's: the read-back dropped - the write's own report decides, as before | `0fa68267f016bb3d0236d99d35576425` | the normal, changes-nothing and race tests; both `Audit.` tests | FAIL, 8 of 10: no read-back in the normal and both race cases; the changes-nothing row a success (and on both surfaces); the race with the room absent a success on both surfaces. PASS: the race with the room there on both surfaces - success without confirmation happens to be right there; the preflight-class race test fails it on the missing read |
| M4 | the brief's: the duplicate-value refusal a failure without a read-back (its catch removed, so it reaches the outer catch) | `f013615bbe89dcb0a44a2d25a963d81f` | the race test; the `Audit.` race test | FAIL, 4 of 6: both race cases (no read-back), and the room-there case Failed on both surfaces. PASS: the room-absent audit cases - failure is right there |
| M5 | every write error read back, the read-back deciding (the group modules' shape; the filter removed) | `01bec8e97ec8046166590085b4ebc0c8` | `AnyOtherWriteError_StaysAFailure_EvenWithTheRoomThere` | FAIL: a success - the timeout with the room present |
| H2 | harness: the stand-in applies the add even when told to change nothing | `c96452f1bf8d58ae64973f7eff796ab1` | the harness test; the changes-nothing test; `Audit.AnOnPremAddTheReadBackDoesNotShow_IsNotRecordedAsWritten` | FAIL, all four cases |
| H3 | harness: the stand-in no longer refuses a DN the group already holds | `363704cc2d585b46f3dfcfd37727f8cc` | the harness test; the race test | FAIL: the harness test (no exception). *Demonstration:* both race cases PASS - unrefused, the raced write just succeeds and the read-back finds the room, the wrong-reason pass only the harness test stops |

Every restored md5 equal to "before"; the restored tree rebuilt and the 22 named cases green.

**Comments after the round.** The round ran on the fix without its explanatory comments; they were
written afterwards, checked by `scratchpad/a2k-a2l-round/comment_only.py` (every changed line blank,
`//`, `///` or a PowerShell `#` line): service `fe43e7cd98485e280a00aa317755dace` ->
`2004aa1aafe648792b9e2a0fd0a7cdc4` (39 changed lines, 0 code); harness `793d1d3b8f3c583e1c33a1f29e41d4ab`
-> `2ef8ec07a3cecad18c19bd9791ddac85` (27, 0); `ConferenceRoomRoomListPreflightTests.cs`
`9865bce2afcb656cb918b50c1e8fcf09` -> `cfe9de3616a8e933cbf1e45c2eba613e` (27, 0);
`ConferenceRoomDirectoryAuditTests.cs` `1fe3aba802c1544a6391fddd3f3ca2da` ->
`2d7099d3eccfb0614c89ba2cf0febb9e` (6, 0).

## Known gaps

- *Assumption, the group modules' precedent, not probed:* the read-back, sent to the group's domain by
  its DNS name as the write was, is answered by a domain controller that has the write. One the write
  has not reached reports a write that landed as not confirmed - partial and loud; the re-run finds the
  room already a member. A false success would need that domain controller to hold the room while the
  write was dropped - a removal it has not yet seen; not guarded.
- *Assumption, documented error text, not probed:* which form the ActiveDirectory module reports a value
  already present in (`IsDuplicateMemberValue`'s comment). A form not matched - a localized message
  included - is reported as the failure the race always was.
- The race with the room there is recorded as written, by the folded-in rule below: the row's own write
  was refused, so `writtenDirectoryObject` there names the object whose membership the row asked for and
  confirmed, not one its write changed. aud-a2g's wording is "an AD write in the operation changed";
  left for the reviewer.
- Any other write error stays a failure even when the membership is in place (the brief); the re-run
  says already a member.
- Cost: one more `Get-ADGroup -Properties member` read for each on-prem add that writes - a ranged read
  of every member DN, large for a large group.
- Unchanged: the inherent two-system residual; a write that landed after the row was reported failed is
  found by the re-run.

**Operator-visible changes.**

- An on-prem add the directory did not keep is reported as failed (partial), naming the group and the
  server read; it used to be reported added and audited as written.
- An add refused because the room is already on the group - another actor's add in between - completes
  when the read-back shows the room; it used to be partial.
- A read-back that cannot be made is a failure, "could not be confirmed".
- Nothing else: on success the same writes, steps and messages.

**Dev acceptance owed** with aud-a2f, aud-a2i, aud-a2j and aud-a2k: the same Room Finder row as a2k's -
a synced room on a synced list, added on-prem, now confirmed by the read-back. Not run here.

**Page:** unchanged; no `ClickGateRegistry` entry moved.

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

Implemented as above: the refusal is held for the read-back and decided by it; "whatever
`Set-ADGroup` reported" is narrowed by the brief's rule, so no other write error is read back.

## Reviewer comments

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent on
`worktree-agent-ae52370b2527142a4`. Independent review pending.

## Closeout

Fixed on branch, not merged, not pushed: the plan `b836c6d7` (with aud-a2k's), aud-a2k's fix
`3341e112`, then this fix, tests, comments and record in one commit. A module-scoped behaviour change:
the Conference Rooms module version bump is due at merge (one bump covers a2k and a2l). No shared code
changed (`GroupManagementService` and the a2i helper are reused as they are) and the harness change is
test-only, so no base app bump from this finding.

Gates on this commit: the Conference Rooms, `GroupMemberListingTests` and `*WriteIdentityTests` classes
250 passed, 0 failed; `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` clean;
`git diff --check` clean; `tools/Test-AsciiOnly.ps1` after `git add` clean. The Release build and the
full suite on the branch tip: recorded below once run.
