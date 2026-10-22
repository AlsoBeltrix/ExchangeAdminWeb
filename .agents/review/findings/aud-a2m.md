# aud-a2m: The Exchange side of the room list is re-resolved by its building-derived name, not by the object the preflight read

**Severity**: LOW - the Exchange Online membership read and add (`Get-DistributionGroupMember` /
`Add-DistributionGroupMember -Identity <name>`) resolve the list again by the name the Building
field derives, not by the `Guid` or `PrimarySmtpAddress` of the object `FindRoomList` already
read. Two Exchange recipients answering to one name would make the add ambiguous or land on the
other one; Exchange refuses an ambiguous identity, so the likely symptom is a failed row, not a
wrong write, but the aud-a2b/a2d binding rule (write to the object you validated, by an immutable
identity) does not yet cover the list itself.
**Status**: Merged to master (`5c116ce4`); closed on guard proof; codex clean; dev acceptance owed
**Branch**: `worktree-agent-a76a90b485c4fa8ce`
**Commit**: `8441f609` (fix, tests, comments, record); plan `bd961140`; merged `5c116ce4`
**Reviewer**: implementer (surfaced by the aud-a2k/a2l agent, 2026-10-22, candidate 2 of its hand-back)

## Evidence

At `28263a6c`, `Services/ConferenceRoomService.cs`: `FindRoomList` reads the list with
`Get-DistributionGroup` and returns a `RoomListTarget` carrying `IsDirSynced` and, since aud-a2k,
`PrimarySmtpAddress`; the cloud-path membership read and `Add-DistributionGroupMember` then pass
the list NAME as `-Identity`.

## Approach (to be written by the implementing agent before code)

- `RoomListTarget` carries the list's `Guid` (Exchange's immutable id for the group) from the same
  `Get-DistributionGroup` read; every later Exchange call on the list (`Get-DistributionGroupMember
  -ResultSize Unlimited`, `Add-DistributionGroupMember`) passes that Guid as `-Identity`. One read,
  one identity, as aud-a2d did for mailboxes.
- Tests through `ExchangeWriteHarness`: the stand-ins record the `-Identity` each call received; the
  cloud add and the membership read use the Guid of the object `FindRoomList` returned; a second
  recipient sharing the name is never touched. Mutation at the call site: pass the name again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21); the
admitted approach above and the supervising session's brief. The owner's rule applies to the
refusal: "We do not guess" (`.agents/decisions.md` 2026-10-15).

**Rule.** Once the room list has been read, every Exchange call on it names it by the `Guid` of the
object that read returned, never by the name the Building derives. An existing list Exchange
reports with no usable `Guid` is refused before any write.

- **The Guid.** `FindRoomList` takes `Guid` from the `Get-DistributionGroup` object it already reads
  for `IsDirSynced` (aud-a2f) and `PrimarySmtpAddress` (aud-a2k) - the same read, no second one.
  `RoomListTarget` carries it (`Guid`, `string?`, "D" form): null for a list that does not exist, and
  for a property that is absent, not a GUID, or all zeros.
- **The refusal.** `PreflightRoomListAsync`, first for any existing list, cloud or synced: no Guid
  refuses, naming the list, ending " Nothing was written." Every later refusal is as before.
- **The calls.** The preflight's already-a-member read (`IsOnRoomListAsync`), the add's membership
  read (`Get-DistributionGroupMember -ResultSize Unlimited`) and `Add-DistributionGroupMember` pass
  the Guid as `-Identity`. `-Member` stays the validated room's GUID (aud-a2d).
- **A list the row creates.** A Building with no list yet: `New-DistributionGroup` creates it
  cloud-side, as now, and the read and the add after it name the Guid of the object the creation
  returned - "never the name" covers this list too, and the object the write produced is the one to
  name. None returned: the row fails after the create (partial, like any failure at that step), and
  no add is sent by name; a re-run finds the list by its name and reads its Guid. *Assumption,
  documented cmdlet behaviour, not probed:* `New-DistributionGroup` outputs the group it created.
- **Unchanged:** the preview's lookup (`ResolveRoomListAsync` reads the same object and writes
  nothing), the on-prem group found by the list's address (aud-a2k), the step names, the messages
  and the write order.

*Assumption, the aud-a2c/a2d precedent for mailboxes, not probed:* Exchange Online accepts a group's
`Guid` as `-Identity` on `Get-DistributionGroupMember` and `Add-DistributionGroupMember`. Were it
wrong, every cloud add would fail "couldn't be found" - partial and loud, never a wrong write.

**Tests** (`ExchangeWriteHarness`). The stand-in `Get-DistributionGroup` reports a per-list `Guid`
(`ListGuids`; `DefaultListGuid` unless a test says otherwise; null leaves the property out; a value
that is not a GUID is reported as given) and reports a list in `MissingLists` as not found;
`New-DistributionGroup` returns the group it made (`CreatedListGuid`; null, no `Guid`);
`Get-DistributionGroupMember` and `Add-DistributionGroupMember` refuse as ambiguous an `-Identity`
that a second recipient also answers to (`ListNamesakes`), as Exchange refuses an ambiguous identity.
In `ConferenceRoomWriteIdentityTests`:
- a cloud list whose name a second recipient also answers to: the row succeeds, not partial; the
  membership read and the add name the list's Guid; no call names the list's name or the namesake;
- a synced list: the preflight's read, the add's read and the refused cloud add name the Guid, and the
  on-prem add completes;
- a list Exchange reports with no Guid (absent, all zeros, not a GUID): refused, not partial, "Nothing
  was written.", no write and no membership read;
- a Building with no list: created, and the read and the add name the created list's Guid; the
  creation returns none - failed, partial, no `Add-DistributionGroupMember`;
- the harness: a namesake's name is refused as ambiguous, the list's Guid answered.

**Mutation, at the call site:** (1) the list's name as `-Identity` again - the preflight's read, the
add's read and the add. md5 before and after, the named failing tests, restored, md5 matched,
touched, rebuilt, green. Comments after the round.

**Files.** `Services/ConferenceRoomService.cs`, `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`,
`ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs`, this record; an existing fixture in the
other Conference Rooms test classes only if the Guid changes what it pins.

## Approach (as implemented)

- `RoomListTarget` gained `Guid` (`string?`). `FindRoomList` fills it through `ReadListGuid`: the
  `Guid` property of the canonical or legacy object it already read for `IsDirSynced` and
  `PrimarySmtpAddress` - a `System.Guid`, wrapped or not, or a string that parses as one - in "D"
  form; null when absent, not a GUID or all zeros, and for a list that does not exist. No new Exchange
  call.
- `PreflightRoomListAsync`: for an existing list, a null Guid refuses before anything else is asked:
  "Exchange reported no GUID for room list '<list>', so the list cannot be named by an immutable
  identity, and the room is not added to it by its name. Check the list in Exchange Online. Nothing was
  written." The already-a-member read is `IsOnRoomListAsync(<the Guid>, roomPrimary)`.
- `AddToRoomListAsync`: `listGuid` is the target's Guid or, for a list the row creates, the Guid of the
  one object `New-DistributionGroup` returned (`ReadListGuid` again). Null - possible only for a
  creation that returned none, since the preflight refuses an existing list without one - throws "Exchange
  returned no GUID for room list '<list>', so the room was not added to it: a list is named only by the
  GUID Exchange reported for it, never by its name.", which `RunAsync` reports as the failed add, so the
  row is partial after the create, as any failure there is. The membership read
  (`Get-DistributionGroupMember -ResultSize Unlimited`) and `Add-DistributionGroupMember` pass `listGuid`
  as `-Identity`; `-Member` is unchanged (the validated room's GUID).
- Harness (`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`): `DefaultListGuid`, `ListGuids`,
  `MissingLists`, `DefaultCreatedListGuid` / `CreatedListGuid` and `ListNamesakes`. PowerShell:
  `ConvertTo-StandInGuid` (a GUID typed, anything else as given); `Get-DistributionGroup` reports `Guid`
  and does not find a list in `MissingLists` (`Write-Error` then `return`); `New-DistributionGroup`
  returns the group it made; `Test-StandInAmbiguousList`, with which `Get-DistributionGroupMember` and
  `Add-DistributionGroupMember` refuse a name a second recipient also answers to ("... matches multiple
  entries.", `Write-Error` then `return`), after recording the call and after any `Failures` entry
  throws. Every other identity is answered as before, so every earlier test reads and adds as it did.
- Tests (`ConferenceRoomWriteIdentityTests`): the five planned, as
  `TheCloudAddAndItsMembershipRead_NameTheListByTheGuidItWasReadWith`,
  `ASyncedList_IsReadAndRefusedTheCloudAddByItsGuid_ThenAddedOnPrem`,
  `ARoomListExchangeReportsWithNoGuid_IsRefusedBeforeAnyWrite` (absent, all zeros, not a GUID),
  `AListTheRowCreates_IsNamedByTheGuidTheCreationReturned`, `AListCreatedWithoutAGuid_IsNotAddedToByItsName`
  and `TheHarness_RefusesAListNameASecondRecipientAnswersTo_AndAnswersTheListsGuid`. No existing test
  changed: none pinned the list's `-Identity`.

## Guard proof

| Test (`ConferenceRoomWriteIdentityTests`) | What it pins |
| --- | --- |
| `TheCloudAddAndItsMembershipRead_NameTheListByTheGuidItWasReadWith` | the finding: a cloud list whose name a second recipient also answers to by the time of the add. The row succeeds, not partial; the one read is by the name; the add's membership read and `Add-DistributionGroupMember` (in that order, nothing else) name the Guid the read returned; the namesake's Guid appears in no call |
| `ASyncedList_IsReadAndRefusedTheCloudAddByItsGuid_ThenAddedOnPrem` | a synced list: the preflight's membership read, the add's and the refused cloud add - three calls - all name the Guid; the on-prem add (one `Set-ADGroup`) completes the row |
| `ARoomListExchangeReportsWithNoGuid_IsRefusedBeforeAnyWrite` (absent, all zeros, not a GUID) | refused, not partial, the message naming the list and ending "Nothing was written.", no write, no membership read or add - on a cloud list, which needs no other preflight |
| `AListTheRowCreates_IsNamedByTheGuidTheCreationReturned` | no list under either name: `New-DistributionGroup` named the list, and the read and the add after it name the Guid the creation returned (a namesake present, so the name would be ambiguous) |
| `AListCreatedWithoutAGuid_IsNotAddedToByItsName` | the creation returned no Guid: failed, partial, one `New-DistributionGroup`, no membership read or add |
| `TheHarness_RefusesAListNameASecondRecipientAnswersTo_AndAnswersTheListsGuid` | the stand-ins: `Get-DistributionGroup` reports the test's Guid or the default, and does not find a missing list; the member cmdlets refuse a name with a namesake ("matches multiple entries") and answer the list's Guid; `New-DistributionGroup` returns the group it made |

**Mutations** (`scratchpad/a2m-a2n-round/round.ps1` with `rows-a2m.ps1`, log `round-a2m.log` beside them,
2026-10-22 10:11-10:14). Each edit was applied only if its old text occurred exactly once and the md5
changed; `dotnet test` on the test project (build included) ran `ConferenceRoomWriteIdentityTests` with a
TRX logger; the file was restored from the saved bytes, touched, and the md5 re-checked. M and revert
rows: `Services/ConferenceRoomService.cs`, md5 before = restored = `89b0932fc662866e9f2ddac872ba738e`. H row:
`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`, before = restored = `6e856e5066eaa524b7b8e4f1e4f27309`.

| # | Mutation | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- |
| revert | the service file replaced whole by HEAD's (`bd961140`, the file `e560de5e` holds) | `2004aa1aafe648792b9e2a0fd0a7cdc4` | the seven a2m behaviour cases | FAIL, 7 of 7: the cloud and created rows partial ("... 'Building 1 Conference Rooms' matches multiple entries."); the synced row's three calls by name; the three no-Guid rows and the created-without-Guid row written. PASS: the harness test, which tests the stand-ins only |
| M1 | the brief's: the list's name as `-Identity` again - the preflight's read, the add's read and the add | `b178c64c532abdda12e0feeb37e3c336` | cloud, synced, created | FAIL, all three: cloud and created partial (ambiguous); synced, 3 of 3 calls by name. PASS, as expected: the no-Guid rows (refused first) and created-without-Guid (no call is made) |
| M1a | the name again at `Add-DistributionGroupMember` only | `e953cc10557f4b3b772c3c2b3ad64a91` | cloud, synced, created | FAIL, all three: cloud and created partial; synced, the add by name |
| M1b | the name again at the add's membership read only | `3aa68ac12347101ff4bff0e54a03f99f` | cloud, synced, created | FAIL, all three, on the pin: the read made by the name. The outcome alone would not show it - the ambiguous read runs under SilentlyContinue and returns no members, and the add by Guid then completes |
| M1c | the name again at the preflight's membership read only | `ba6ba06c0852f4d634bb839a6b12cfcf` | synced | FAIL: the preflight's read by name (1 of 3) |
| M1r | the preflight's no-Guid refusal dropped | `2e8d500af2bd6ac544cb16329beb30b0` | the no-Guid three | FAIL, all three: partial - Set-Place written, then the add refused for want of a Guid |
| H1 | harness: the member cmdlets answer a name a second recipient also answers to | `55b01c1596cd9b121055d961caab874d` | the harness test | FAIL: no exception for the ambiguous name |

Every restored md5 equal to "before"; the restored tree rebuilt and the 208 Conference Rooms cases green.

**Comments after the round.** The round ran on the fix without its explanatory comments; they were
written afterwards and checked by `scratchpad/a2m-a2n-round/comment_only.ps1` (every blank, `//`, `///` or
PowerShell `#` line dropped from both versions, the remaining code lines identical in order): service
`89b0932fc662866e9f2ddac872ba738e` -> `6a88f144bc5f2926df82fdd8dc2a6af6` (code lines 1498 = 1498; comment and
blank lines 669 -> 687); `ConferenceRoomWriteIdentityTests.cs` `01170141f4b02d9f722be6bc1f491325` ->
`03dee7d3ee804c89c4caaaf13adcbba0` (269 = 269; 82 -> 98); the harness unchanged (its doc comments were written
with the stand-ins, before the round).

## Known gaps

- *Assumption, the aud-a2c/a2d precedent, not probed:* Exchange Online takes a group's `Guid` as
  `-Identity` on `Get-DistributionGroupMember` and `Add-DistributionGroupMember`. Were it wrong, every cloud
  add would fail "couldn't be found" - partial and loud, never a wrong write.
- *Assumption, documented cmdlet output, not probed:* `Get-DistributionGroup` reports the list's `Guid`, and
  `New-DistributionGroup` returns the group it created with its `Guid`. Were the first wrong, every row with
  an existing list would be refused before any write ("Exchange reported no GUID ..."); the second, every
  row that creates a list would be partial after the create, the re-run completing it. Both fail closed.
- Unchanged and out of scope (aud-a2f's, recorded again in aud-a2k): the read itself is by the
  Building-derived name, and a name answering to more than one group reads as "not found", so the row then
  tries to create a list of that name.
- The preview's lookup (`ResolveRoomListAsync`) reads the same object and carries the Guid unused; it writes
  nothing.

**Operator-visible changes.**

- An existing room list Exchange reports without a usable Guid is refused before any write, naming the list.
- A list the row creates whose creation returns no Guid: the row is partial (the list made, the room not on
  it); a re-run completes it.
- Nothing else: on success the same writes, steps and messages.

**Dev acceptance owed:** Room Finder for a room on a cloud list (the read and the add by Guid), on a synced
list (the preflight's and the add's reads and the refused cloud add by Guid, then the on-prem add), and for
a Building with no list yet (created, then added to by the Guid the creation returned). Not run here: no
Exchange or directory call was made.

**Page:** unchanged; no `ClickGateRegistry` entry moved.

## Reviewer comments

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent on
`worktree-agent-a76a90b485c4fa8ce`. Independent review pending.

For the reviewer, one extension of the brief's wording: "every later Exchange call ... passes that Guid"
is applied to a list the row creates, by the Guid `New-DistributionGroup` returned - there is no read Guid
for it, and the name is what the finding removes.

## Closeout

Fixed on branch, not merged, not pushed: the plan `bd961140` (with aud-a2n's), then this fix, tests,
comments and record in one commit. A module-scoped behaviour change: the Conference Rooms module version
bump is due at merge. No shared code changed and the harness change is test-only, so no base app bump from
this finding.

Gates on this commit: every class that uses the harness (`ConferenceRoom*`, `*WriteIdentityTests`) and
`GroupMemberListingTests`, 258 passed, 0 failed; `dotnet format ExchangeAdminWeb.slnx --verify-no-changes
--no-restore` clean; `git diff --check` clean; `tools/Test-AsciiOnly.ps1` after `git add` clean. The Release
build and the full suite run on the branch tip, after aud-a2n: see its Closeout.
