# aud-a2j: The room-list AD group lookup searches only the credential's default domain

**Severity**: MEDIUM - fails closed, so no wrong write, but on a multi-domain forest the on-prem
room-list add refuses every valid synced list whose group lives in another domain, and the logic
rests on the forest's shape (Architectural Invariant 7).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; plan recorded, in progress
**Branch**: `worktree-agent-a0b52fcc43c6766da`
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (batch-6, `prompt-batch-6.md`, 2026-10-22)

## Evidence

At `bc24d7d1`, `Services/ConferenceRoomService.cs`, `ResolveAdGroupGuid` (shared by the a2f
room-list preflight and the on-prem add, and the source of the DN that a2i's membership read
uses):

```
1748        ps.AddCommand("Get-ADGroup")
1749          .AddParameter("Filter", $"mail -eq '{escaped}' -or displayName -eq '{escaped}' -or name -eq '{escaped}'")
1750          .AddParameter("Credential", credential)
1751          .AddParameter("ErrorAction", "Stop");
```

No `-Server`. Without it `Get-ADGroup -Filter` is answered by a domain controller of the
credential's own domain and returns only that domain's groups. A room list synced from a group
in another domain of the forest comes back as "No on-prem AD group found", the preflight refuses
the row before any write, and the room cannot be added to a valid list.

`GroupManagementService` already solved this for its group search: it resolves the forest global
catalog once (`ResolveSearchGlobalCatalog`, `Get-ADForest ... GlobalCatalogs`, "host:3268"),
searches there, refuses ambiguity, then re-reads the chosen object by DN against
`ServerFromDn(dn)` before any write. Its comments record the incident in which a bare `":3268"`
was accepted by `Get-ADGroup` and quietly served from the local domain.

## Approach (to be written by the implementing agent before code)

- Search via the forest global catalog as `GroupManagementService` does (reuse or lift its
  resolver; the GC endpoint must be a real host, never a bare `:3268`; a failed GC resolution is
  a refusal, not a fallback to the default domain).
- Keep the exact-one-match rule; a GC search widens the match set, so ambiguity across domains
  must still refuse.
- Re-read the chosen group by DN against `ServerFromDn(groupDn)` and carry that GUID and DN into
  the a2i membership read and the add, so the preflight and the add resolve the same object.
- Tests: a group in a second domain found via the GC; the default-domain-only read would have
  missed it (mutation at the call site: drop `-Server`); cross-domain ambiguity refuses; GC
  resolution failure refuses before any write. No live AD.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21); the
admitted approach above and the supervising session's brief.

**Rule.** The room list's on-prem group is looked for in the whole forest, must be exactly one
object there, is proven in its own domain, and every later read and the write go to that domain.
A forest whose global catalog cannot be named refuses the row before any write; nothing falls back
to one domain.

- **The catalog.** `ResolveForestGlobalCatalog`, lifted, not shared: the two resolvers it copies
  are private instance members, and `GroupManagementService.ResolveSearchGlobalCatalog` caches in a
  static and falls back to the local domain, neither of which this path may do
  (`PowerShellProtectedGroupDirectory.GlobalCatalog` is the fail-closed shape copied). AddScript
  `param($c) (Get-ADForest -Credential $c -ErrorAction Stop).GlobalCatalogs | Select-Object -First 1`
  (the measured idiom); the endpoint from a pure `GlobalCatalogEndpoint(host)` - `host.Trim():3268`,
  null for a blank host, so never a bare `:3268`. A throw, an error or no host is an error. Not
  cached: each preflight and add opens its own runspace.
- **`ResolveAdGroupGuid`**, shared by the preflight and the add, signature unchanged: no catalog -
  refuse. The search, filter unchanged, gains `-Server <catalog>`. None - not found; more than one -
  refuse (now across domains); no ObjectGUID - refuse. Then the re-read, `Get-ADGroup -Identity <the
  row's DN> -Server ServerFromDn(dn)`: a DN naming no domain, an answer other than exactly one
  object carrying the row's objectGUID, or one without a DN - refuse. It returns the re-read's
  objectGUID and DN, which the a2i membership read and the add take, so the preflight and the add
  resolve the same object the same way.
- **The add writes the group's `member` attribute**: `Set-ADGroup -Identity <groupGuid> -Add
  GroupManagementService.BuildMemberAttributeWrite(<room DN>) -Server ServerFromDn(groupDn)`.
  Necessary, not incidental. `Add-ADGroupMember` without `-Server` cannot find a group outside the
  default domain, and routed to the group's domain it resolves `-Members` there as well, where the
  room is not - the protection gate proves the room's object in the default domain
  (`ProtectedPrincipalService.FindMailboxGuidHoldersAsync`). That is the live dev failure of
  2026-09-03 (`docs/GroupMemberNesting-Plan.md`, Revision 2026-09-03) and why both group modules
  write this way. Left as it was, the preflight would pass a group in another domain and the add
  would fail after Set-Place - the partial aud-a2f exists to prevent. The room's DN is the one
  `ConfirmDirectoryObject` read by its proven objectGUID, the DN the a2i read compares. The same
  write-members right as before. `written` unchanged (the room's objectGUID, aud-a2g); the trace
  command and the credential-purpose labels follow the cmdlet.
- **Preflight:** a group error now ends " Nothing was written.", as its other refusals do (the
  re-read's refusals land there).
- **Unchanged:** the room-object read, the a2i membership helper, the cloud path, every Exchange
  write, the step names and the write order.

**Tests** (`ExchangeWriteHarness` as a two-domain forest: `contoso.test`, the default, and
`fabrikam.test`, the a2i fixture's other domain; groups placed per domain. The stand-in `Get-ADGroup`
reaches domains by `-Server`: none, or a bare `:port`, the default domain only - the real cmdlet's
answer to `:3268`; the catalog endpoint every domain; a domain's DNS name that domain; anything else
is unreachable. New stand-ins `Get-ADForest` and `Set-ADGroup`; the stand-in `Add-ADGroupMember`
resolves the group and each member in the domain it reaches, as the real cmdlet does;
`DirectoryGroupMemberReadBack` answers the member read only):

- a group in the other domain is found through the catalog; the row succeeds, written on that
  domain with the room's DN;
- the harness: a search with no `-Server`, or with `:3268`, misses that group; the catalog finds
  both domains' groups; a domain's name finds only its own;
- a name matching one group in each domain - refused before any write, not partial;
- no catalog (Get-ADForest fails; reports none; reports a blank host), the group in the default
  domain - refused before any write, and no `Get-ADGroup` issued at all;
- each phase re-reads the group it found by its DN on `fabrikam.test`, between its search and its
  membership read - the preflight's before Set-Place, the add's after - and the write names the
  objectGUID both re-reads answered;
- the group's own domain answers its DN as another object - refused before any write;
- the harness: the stand-in `Add-ADGroupMember` refuses a member from another domain.
- Existing tests, fixtures and assertions only: `Add-ADGroupMember` becomes `Set-ADGroup` (member =
  the room's DN) in `ConferenceRoomRoomListPreflightTests`, `ConferenceRoomDirectoryAuditTests` and
  `ConferenceRoomWriteIdentityTests`; the a2i membership-read selector takes reads of the `member`
  attribute; a2i's "the group answers as another object" uses the member-read knob, so its read-back
  check stays reachable behind the new re-read.

**Mutations, at the call sites.** The brief's four: `-Server` dropped from the catalog search; the
search's `-Server` a bare `":3268"`; a catalog failure falling back to the search without `-Server`;
the re-read removed, the catalog row carried. Then: the re-read's objectGUID check dropped; the
blank-host guard dropped; the write's `-Server` dropped; the old `Add-ADGroupMember` write restored,
routed. Harness: no `-Server` reaching every domain; a bare port reaching every domain;
`Add-ADGroupMember` resolving members anywhere. Each with md5 before and after, the named failing
test, the restore md5-checked and touched, rebuilt and green. Comments after the round.

**Files.** `Services/ConferenceRoomService.cs`, `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`,
`ExchangeAdminWeb.Tests/ConferenceRoomRoomListPreflightTests.cs`,
`ExchangeAdminWeb.Tests/ConferenceRoomDirectoryAuditTests.cs`,
`ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs`, this record.
`GroupManagementService.ServerFromDn` and `BuildMemberAttributeWrite` are reused as they are (already
`internal static`); nothing outside Conference Rooms changes. Not touched, per the brief:
`docs/ConferenceRooms-OnPremRoomListAdd-Plan.md` step 3 still names `Get-ADGroup -Filter` and
`Add-ADGroupMember` - an amendment pointer to this record is owed. No page change.

## Guard proof

Pending.

## Known gaps

Dev acceptance owed with a2f/a2i: a room list whose group is in a second domain.
