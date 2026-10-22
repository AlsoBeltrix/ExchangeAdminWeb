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

## Guard proof

Pending.
