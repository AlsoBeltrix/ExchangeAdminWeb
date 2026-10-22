# aud-a2k: The room-list's on-prem group is matched by display name, so a same-named unrelated group can be written

**Severity**: MEDIUM - a wrong-object write on the on-prem room-list path. The exact-one-match rule
narrows it, but one unrelated group that happens to carry the list's name as its displayName or
name, with the real synced group absent from the catalog result, is written in its place.
**Status**: In progress - plan recorded; approved under the owner's 2026-10-21 "EVERYTHING" go
**Branch**: `worktree-agent-ae52370b2527142a4`
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a2j agent, 2026-10-22, candidate 2 of its hand-back)

## Evidence

At the aud-a2j branch tip `62b4a39f` (and before it, since aud-a2f), `Services/ConferenceRoomService.cs`,
`ResolveAdGroupGuid` builds its filter from the room list's NAME as Exchange reports it:

```
mail -eq '<list name>' -or displayName -eq '<list name>' -or name -eq '<list name>'
```

The `mail` clause compares an address attribute with a display name, so it can never match; the
effective match is `displayName`/`name`. The repo's rule since aud-a2b/a2c is that Exchange and AD
objects are bound by an immutable identity or, failing that, by the address Exchange holds for the
object, never by a human-readable name (`.agents/review/findings/aud-a2c.md`, aud-a2d). The room
list is an Exchange distribution group: Exchange knows its `PrimarySmtpAddress` and, for a
dir-synced list, the on-prem `mail` attribute carries that same address.

## Approach (to be written by the implementing agent before code)

- Read the room list from Exchange first (`Get-DistributionGroup -Identity <list> -ResultSize
  Unlimited` is not needed; one object) and take its `PrimarySmtpAddress` and `IsDirSynced`.
  Resolve the on-prem group through the forest global catalog (aud-a2j) by `mail -eq
  '<PrimarySmtpAddress>'` and nothing else; refuse zero or multiple matches. Where the on-prem
  group holds the list's `ExchangeGuid`-equivalent (`msExchMailboxGuid` is for mailboxes; for
  groups there is no equivalent stable attribute Exchange exposes), the address is the binding.
- Keep the re-read by DN on the group's own domain and the member-attribute write (aud-a2j),
  and the preflight-before-any-write rule (aud-a2f).
- Tests through `ExchangeWriteHarness`: a same-named unrelated group plus the real synced group
  (address match wins, name match would have refused as ambiguous or chosen wrong); only a
  same-named group with a different address (refused, nothing written); a synced list whose
  on-prem `mail` differs from its `PrimarySmtpAddress` (refused with a message naming both, no
  guess). Mutation at the call site: restore the name clauses.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21); the
admitted approach above and the supervising session's brief. The owner's rule applies to every
refusal here: "the onus is on the user to provide accurate data ... We do not guess"
(`.agents/decisions.md` 2026-10-15).

**Rule.** The room list's on-prem group is the one object in the forest whose `mail` is the primary
SMTP address Exchange holds for the list - never a name. No address, no match or more than one match
refuses before any write, naming the list and the address searched; the operator is not asked to
pick.

- **The address.** `FindRoomList` takes `PrimarySmtpAddress` from the `Get-DistributionGroup` object
  it already reads for `IsDirSynced` (aud-a2f) - the same read, no second one. `RoomListTarget`
  carries it (`PrimarySmtpAddress`, `string?`; null for a list that does not exist or reports none).
- **`ResolveAdGroupGuid(ps, roomListName, listAddress, credential)`**, still shared by the preflight
  and the add: the address trimmed; blank refuses before the catalog is resolved or anything is
  searched. The catalog search's filter is `mail -eq '<address>'` alone (single quotes doubled, as
  now); the `displayName`/`name` clauses go. None: refused, naming the list and the address searched,
  and what to check (that address on the group's `mail` in on-prem Active Directory). More than one:
  refused, naming the same. Everything after the match is aud-a2j's, unchanged: exactly one, an
  ObjectGUID, the re-read by DN on the group's own domain, the a2i membership read and the
  `Set-ADGroup` write.
- `AddToRoomListViaAdAsync` gets the address from the `RoomListTarget` `AddToRoomListAsync` already
  holds. The preflight still ends every group refusal " Nothing was written."
- **Unchanged:** the room-object read, every Exchange write, the write order, step names, the cloud
  path, the CSV preview's lookup.

"Naming both" in the approach above is read as the list and the address searched: the group's own
`mail` could be named only by finding the group by its name, the binding this finding removes.

**Tests** (`ExchangeWriteHarness`). The stand-in `Get-DistributionGroup` reports a per-list
`PrimarySmtpAddress` (`ListAddresses`; `list@contoso.test` by default; null leaves it out). Stand-in
groups carry `mail`, `displayName` and `name` (`DirectoryGroupAttributes`; by default `mail` that
default address and the CN as both names), and the stand-in `Get-ADGroup` search evaluates its filter
- clauses `<attribute> -eq '<value>'` joined by `-or`, case-insensitive, an absent attribute as empty -
instead of answering every group whatever the filter; a filter it cannot read is an error. In
`ConferenceRoomRoomListPreflightTests`:
- a same-named unrelated group and the real synced group both present: the address picks the synced
  one, the row succeeds and the write names its objectGUID; every search's filter is the address
  alone (with the name clauses both match and the row is refused);
- only a same-named group carrying another address: refused, not partial, nothing written, the group
  never re-read;
- a synced list whose group's `mail` differs from the list's `PrimarySmtpAddress`: refused, the
  message naming the list and the address searched, nothing written;
- a list with no primary address (absent, empty, blank), a mail-less group with the list's name
  present: refused, not partial, nothing written, no `Get-ADGroup` call at all;
- the harness: a search matches only the attributes its filter names.
- Existing fixtures only: the a2j harness search test searches by the default address; the a2j
  cross-domain ambiguity test is renamed for what it now matches, the address.

**Mutations, at the call site:** (1) the `displayName`/`name` clauses restored; (2) the no-address
refusal dropped. Each: md5 before and after, the named failing test, restored, md5 matched, touched,
rebuilt, green. Comments after the round.

**Files.** `Services/ConferenceRoomService.cs`, `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`,
`ExchangeAdminWeb.Tests/ConferenceRoomRoomListPreflightTests.cs`, this record. Not touched, per the
brief: `docs/ConferenceRooms-OnPremRoomListAdd-Plan.md` step 3 still says "by mail/displayName"; the
supervisor amends its pointer.

## Guard proof

Pending.
