# aud-a2m: The Exchange side of the room list is re-resolved by its building-derived name, not by the object the preflight read

**Severity**: LOW - the Exchange Online membership read and add (`Get-DistributionGroupMember` /
`Add-DistributionGroupMember -Identity <name>`) resolve the list again by the name the Building
field derives, not by the `Guid` or `PrimarySmtpAddress` of the object `FindRoomList` already
read. Two Exchange recipients answering to one name would make the add ambiguous or land on the
other one; Exchange refuses an ambiguous identity, so the likely symptom is a failed row, not a
wrong write, but the aud-a2b/a2d binding rule (write to the object you validated, by an immutable
identity) does not yet cover the list itself.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
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

## Guard proof

Pending.
