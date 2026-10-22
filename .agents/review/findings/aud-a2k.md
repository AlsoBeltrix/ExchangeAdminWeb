# aud-a2k: The room-list's on-prem group is matched by display name, so a same-named unrelated group can be written

**Severity**: MEDIUM - a wrong-object write on the on-prem room-list path. The exact-one-match rule
narrows it, but one unrelated group that happens to carry the list's name as its displayName or
name, with the real synced group absent from the catalog result, is written in its place.
**Status**: Fixed on branch, not merged; independent review pending; dev acceptance owed with a2f/a2i/a2j
**Branch**: `worktree-agent-ae52370b2527142a4`
**Commit**: `3341e112` (fix, tests, comments); plan `b836c6d7`
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

## Approach (as implemented)

- `RoomListTarget` gained `PrimarySmtpAddress` (`string?`). `FindRoomList` fills it through
  `ReadPrimarySmtpAddress` - `PrimarySmtpAddress` of the canonical or legacy object it already read for
  `IsDirSynced`, as Exchange returned it; null for a list that does not exist. No new Exchange call.
- `ResolveAdGroupGuid(ps, roomListName, listAddress, credential)`: `listAddress?.Trim() ?? ""`, and an
  empty result refuses first - before `ResolveForestGlobalCatalog`, so a list with no address asks the
  forest nothing ("Exchange reported no primary SMTP address for room list '<list>', so its on-prem group
  cannot be identified: the group is found by that address alone, never by its name. Check the list in
  Exchange Online. ..."). The search filter is `mail -eq '<address, single quotes doubled>'` alone. None:
  "No on-prem AD group in the forest has mail '<address>', the primary SMTP address Exchange holds for
  room list '<list>'. Check that the list's group in on-premises Active Directory carries exactly that
  address in its mail attribute. ..."; several: "Multiple on-prem AD groups (<n>) in the forest have mail
  '<address>' ... Check which of them is the list's group; only it may carry that address. ...". The
  ObjectGUID check, the re-read in the group's own domain, the a2i membership read and the write are
  aud-a2j's, untouched.
- Callers: the preflight passes `list.PrimarySmtpAddress`; `AddToRoomListViaAdAsync` takes `listAddress`,
  passed from the `RoomListTarget` `AddToRoomListAsync` holds - the object the preflight decided on. Both
  refusal paths are as before: the preflight appends " Nothing was written."; the add (after the writes)
  is partial.
- Harness (`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`): `DefaultListAddress`
  (`list@contoso.test`, the old hard-coded address), `ListAddresses`, `DirectoryGroupAttributes` and
  `SetDirectoryGroupAttributes(objectGuid, mail, displayName, name)`. PowerShell: `Get-DistributionGroup`
  reports the per-list address (null omits the property); `Get-StandInGroupAttribute`,
  `Read-StandInFilter` (clauses `<attribute> -eq '<value>'` joined by `-or`, `''` unescaped; any other
  shape `$null`) and `Test-StandInGroupMatches` (any clause, `-ieq`, a missing attribute as `''`); the
  `Get-ADGroup` search returns only the reached groups the filter matches and refuses an unreadable filter
  (`Write-Error` then `return`, aud-a2h). By default every group carries the default address, so every
  test written before this one finds the groups it found before.
- Tests (`ConferenceRoomRoomListPreflightTests`): the five planned, as
  `TheListsGroup_IsTheOneCarryingItsAddress_NotAGroupThatSharesItsName`,
  `AGroupThatOnlySharesTheListsName_IsNeverWritten`,
  `ASyncedListWhoseGroupCarriesAnotherAddress_IsRefused_NamingTheListAndTheAddressSearched`,
  `ARoomListWithNoPrimaryAddress_IsRefusedBeforeAnyGroupIsLookedUp` (absent, empty, blank) and
  `TheHarness_AGroupSearchMatchesOnlyTheAttributesItsFilterNames`. Existing, as planned: the a2j harness
  search (`SearchGroups`) searches by the default address, or a filter given; the a2j test
  `ARoomListNameMatchingAGroupInEachDomain_IsRefusedBeforeAnyWrite` is now
  `ARoomListAddressMatchingAGroupInEachDomain_IsRefusedBeforeAnyWrite` (body unchanged; aud-a2j's guard
  proof names it by its old name).

## Guard proof

| Test | What it pins |
| --- | --- |
| `TheListsGroup_IsTheOneCarryingItsAddress_NotAGroupThatSharesItsName` | the synced group (mail = the list's address) and an unrelated group elsewhere in the forest, both with the list's name as displayName and name: the row succeeds, not partial; the one `Set-ADGroup` names the synced group's objectGUID; both searches (preflight, add) bind `Filter` exactly `mail -eq '<address>'` |
| `AGroupThatOnlySharesTheListsName_IsNeverWritten` | the finding: the forest's only group with the list's name carries another address - refused, not partial, "Nothing was written.", no write, the group never read by identity |
| `ASyncedListWhoseGroupCarriesAnotherAddress_IsRefused_NamingTheListAndTheAddressSearched` | the list's own group by every name, its mail another address: refused, not partial, no write; the message names the list and the address searched |
| `ARoomListWithNoPrimaryAddress_IsRefusedBeforeAnyGroupIsLookedUp` (absent, empty, blank) | no address: refused, not partial, "Nothing was written.", no write, no `Get-ADGroup` at all - with a mail-less group of the list's name present for a blank search to find |
| `TheHarness_AGroupSearchMatchesOnlyTheAttributesItsFilterNames` | the stand-in: an address finds only its carrier (quotes doubled, any case); a list's name never matches `mail`; names match through `displayName`/`name`; any `-or` clause matches; a missing attribute reads as empty; an unreadable filter is an error |

**Mutations** (`scratchpad/a2k-a2l-round/round.py` in the session scratchpad, log `round.log` beside it,
2026-10-22 09:03-09:05). Each edit was applied only if its old text occurred exactly once and the md5
changed; `dotnet test` on the test project (build included) ran the named tests with a TRX logger; the
file was restored from the saved bytes and touched (`os.utime`) and the md5 re-checked. M and revert
rows: `Services/ConferenceRoomService.cs`, md5 before = restored = `cf07a632c6f0202d80a965fa9bc89ff2`. H
row: `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`, before = restored =
`2df4be0842d06c742e95dcdc0972b57d`. Test names are in `ConferenceRoomRoomListPreflightTests`.

| # | Mutation | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- |
| revert | the service file replaced whole by HEAD's (`b836c6d7`, the file master `1049f839` holds), the rest of the tree as fixed | `4de21d801a49265f0868fa299d3f5d9d` | the five above | FAIL, 6 of 7 cases: the synced-plus-namesake row refused ("Multiple on-prem AD groups (2) match room list ...", by name); the namesake-only, other-address and all three no-address rows written. PASS: the harness test, which tests the stand-in only |
| M1 | the brief's: the `displayName`/`name` clauses restored beside the address (the list's name, escaped as before) | `35f8657350b0ccec1e964ad3a528a19f` | `TheListsGroup_IsTheOneCarryingItsAddress_NotAGroupThatSharesItsName`; `AGroupThatOnlySharesTheListsName_IsNeverWritten`; `ASyncedListWhoseGroupCarriesAnotherAddress_IsRefused_NamingTheListAndTheAddressSearched` | FAIL, all three: the first refused as ambiguous (two groups match), the other two written to the group carrying the name |
| M2 | the brief's: the no-address refusal dropped | `33b2475d825cfbf1312ce1dabd48d86b` | `ARoomListWithNoPrimaryAddress_IsRefusedBeforeAnyGroupIsLookedUp` | FAIL, all three cases: `mail -eq ''` sent to the catalog, and the mail-less namesake written |
| H1 | harness: the stand-in search ignores its filter, every reached group matching (the pre-a2k stand-in) | `63db0b490a4116d55bb97a0addc16b0d` | `TheHarness_AGroupSearchMatchesOnlyTheAttributesItsFilterNames`; `TheListsGroup_...`; `AGroupThatOnlySharesTheListsName_IsNeverWritten` | FAIL, all three - the a2k rows rest on the stand-in evaluating what the service asks |

Every restored md5 equal to "before"; the restored tree rebuilt and the 7 named cases green.

**Comments after the round.** The round ran on the fix without its explanatory comments; they were
written afterwards. `scratchpad/a2k-a2l-round/comment_only.py` over the round copies checks that every
changed line is blank, `//`, `///` or a PowerShell `#` line: service `cf07a632c6f0202d80a965fa9bc89ff2`
-> `b2d329f1ff23e6d6e1388d1846cd3c11` (48 changed lines, 0 code); harness
`2df4be0842d06c742e95dcdc0972b57d` -> `fe59038a4bca79da8a4db6825c708d04` (33, 0);
`ConferenceRoomRoomListPreflightTests.cs` `2006d1c36be32890b12dab5b0f44c1c9` ->
`bf26e82d225ae93b9389e74af09a3698` (44, 0).

## Known gaps

- *Assumption, Active Directory's schema default, not probed:* `mail` is in the global catalog's
  partial attribute set, so the catalog search can match it. Were it not, every search would find
  nothing and every such row would be refused before any write - fail closed.
- *Assumption, documented sync behaviour, not probed:* for a list synchronized from on-premises, the
  on-prem group's `mail` is the primary SMTP address Exchange Online holds for the list. Where they
  differ - the group's `mail` not its primary `SMTP:` proxy address, or a change not yet synchronized -
  the row is refused naming the address, and the operator corrects the data; nothing is guessed.
- The address is a locator, not an immutable link. Exchange exposes no attribute tying the list to its
  on-prem object (the admitted approach), so while an address that has just moved from one group to
  another is waiting on sync, the address can name a group other than the one the list is synchronized
  from. Two groups carrying it at once refuse as ambiguous.
- A list synchronized from another forest has no group with its address in this one: refused before
  any write, as before, now with the address named.
- Unchanged and out of scope: the list itself is still found in Exchange by its building-derived name
  (`Get-DistributionGroup -Identity`), and aud-a2f's gap that an ambiguous name reads as "not found".

**Operator-visible changes** (all in the refusing direction, all before any write, except the second):

- A synced list whose only on-prem group with its name carries another address, or none, is refused;
  that group used to be written.
- A synced list whose group carries every name of the list but another `mail` is refused, the message
  naming the list and the address searched; it used to be found by name and written.
- A list whose name a second group also carries, but whose address only its own group does, now
  completes on that group; it used to be refused as ambiguous.
- A list Exchange reports with no primary SMTP address is refused; the forest is not asked.
- The group refusals name the address searched. Every other row: the same writes, steps and messages.

**Dev acceptance owed** with aud-a2f, aud-a2i and aud-a2j: Room Finder for a synced room on an existing
synced list - the on-prem add finds the list's group by its address and adds the room; the ~30-minute
sync note. Not run here: no live directory or Exchange call was made.

**Page:** unchanged; no `ClickGateRegistry` entry moved.

## Reviewer comments

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent on
`worktree-agent-ae52370b2527142a4`. Independent review pending.

## Closeout

Fixed on branch, not merged, not pushed: the plan `b836c6d7`, then the fix, tests, comments and this
record in one commit. A module-scoped behaviour change: the Conference Rooms module version bump is due
at merge. No shared code changed and the harness change is test-only, so no base app bump from this
finding.

Gates on this commit: the Conference Rooms, `GroupMemberListingTests` and `*WriteIdentityTests` classes
(every user of the harness) 229 passed, 0 failed; `dotnet format ExchangeAdminWeb.slnx
--verify-no-changes --no-restore` clean; `git diff --check` clean; `tools/Test-AsciiOnly.ps1` after
`git add` clean. The Release build and the full suite ran on the branch tip, after aud-a2l: see its
Closeout (0 errors; 5108 passed, 0 failed, 3 skipped).
