# aud-a2k: The room-list's on-prem group is matched by display name, so a same-named unrelated group can be written

**Severity**: MEDIUM - a wrong-object write on the on-prem room-list path. The exact-one-match rule
narrows it, but one unrelated group that happens to carry the list's name as its displayName or
name, with the real synced group absent from the catalog result, is written in its place.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
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

## Guard proof

Pending.
