# aud-a2j: The room-list AD group lookup searches only the credential's default domain

**Severity**: MEDIUM - fails closed, so no wrong write, but on a multi-domain forest the on-prem
room-list add refuses every valid synced list whose group lives in another domain, and the logic
rests on the forest's shape (Architectural Invariant 7).
**Status**: Merged to master (`04779a54`); closed on guard proof; codex review pending (write form `Set-ADGroup` is the focus); dev acceptance owed
**Branch**: `worktree-agent-a0b52fcc43c6766da` (merged, deleted)
**Commit**: `7d863547` (fix and tests); plan `618f33c9`; comments `62b4a39f`; merged `04779a54`
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

## Approach (as implemented)

- `ResolveForestGlobalCatalog` (`Services/ConferenceRoomService.cs`): the AddScript above, under the
  module credential; a throw, an error-stream entry or no usable host returns null and the reason.
  The endpoint comes from `GlobalCatalogEndpoint(host)`, internal static and pure: `host.Trim():3268`,
  null for a null or blank host. Resolved per call, never cached.
- `ResolveAdGroupGuid`: the catalog first - none refuses ("The forest's global catalog could not be
  resolved (...), so the on-prem group for room list '<list>' cannot be looked up across the forest.
  Refusing rather than searching one domain."). The search, filter unchanged, gains
  `-Server <catalog>`; its refusals are unchanged (none; several; no ObjectGUID). The one match goes
  to `ReadAdGroupInItsDomain`.
- `ReadAdGroupInItsDomain`: `Get-ADGroup -Identity <the row's DN> -Server
  GroupManagementService.ServerFromDn(dn) -Credential ... -ErrorAction Stop`. It refuses a DN that
  names no domain, and any answer that is not exactly one object carrying the row's objectGUID and a
  DN. It returns that read's objectGUID and DN - what the a2i membership read and the write take.
- `PreflightRoomListAsync`: a group error is returned with " Nothing was written." appended; the
  credential-purpose label is "on-prem room-list membership preflight for <room>".
- `AddToRoomListViaAdAsync`: the write is `Set-ADGroup -Identity <groupGuid> -Add
  GroupManagementService.BuildMemberAttributeWrite(<roomDn>) -Server ServerFromDn(groupDn) -Credential
  ... -ErrorAction Stop`, the room's DN being the one `ConfirmDirectoryObject` read for its proven
  objectGUID (never null there: the membership read just before refuses a room without one). Its
  fallback error text names the room and the list; the label is "on-prem room-list membership add for
  <room>"; the `AddToRoomListOnPrem` trace step's command is `Set-ADGroup`. It still returns
  `(null, roomGuid)` for a write and `(null, null)` for the already-a-member no-op (aud-a2g).
- Harness (`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`): `OtherDirectoryDomainDn`
  (`DC=fabrikam,DC=test`), `DirectoryDomains`, `DirectoryGroupDomains` (objectGUID to domain DN; the
  default domain otherwise), `DirectoryGroupDnFormat` now `CN=Group {0},OU=Groups,{1}` with a
  `DirectoryGroupDn(guid, domainDn)` overload, `GlobalCatalogHost` / `GlobalCatalogEndpoint`
  (`gc01.contoso.test:3268`) / `GlobalCatalogs`, `DirectoryGroupMemberReadBack`, and `AddedMember(call)`
  (the DN a `Set-ADGroup -Add` call adds). PowerShell: `Get-StandInReach` maps a call's `-Server` to
  what it reaches - none or a leading `:` the default domain, `<catalog>:3268` the forest, a domain's
  DNS name that domain, anything else nothing ("Unable to contact the server"). `Get-ADGroup`
  searches and reads only the groups its call reaches; a read asking for `member` takes
  `DirectoryGroupMemberReadBack` before `DirectoryGroupReadBack`. New `Get-ADForest` (reports
  `GlobalCatalogs`; `Failures['Get-ADForest']` fails it) and `Set-ADGroup` (its group must be reached;
  a write to the catalog is refused). `Add-ADGroupMember` now declares `-Server` and resolves its group
  and every member in the one domain it reaches. Every refusal is `Write-Error` then `return` (aud-a2h).
- Tests (`ConferenceRoomRoomListPreflightTests`): the seven planned, as
  `ARoomListGroupInAnotherDomain_IsFoundThroughTheForestGlobalCatalog`,
  `TheHarness_AnswersAGroupSearchOnlyFromTheDomainsItsServerReaches`,
  `ARoomListNameMatchingAGroupInEachDomain_IsRefusedBeforeAnyWrite`,
  `AForestWhoseGlobalCatalogCannotBeResolved_IsRefusedBeforeAnyWrite` (three cases),
  `TheGroupFoundInTheForest_IsReReadInItsOwnDomain_SoThePreflightAndTheAddResolveTheSameObject`,
  `AGroupItsOwnDomainHoldsAsAnotherObject_IsRefusedBeforeAnyWrite` and
  `TheHarness_AWriteFindsItsGroupAndMembersOnlyInTheDomainItReaches`, plus the pure
  `AGlobalCatalogEndpoint_IsAHostAndItsPort_NeverABarePort` (five cases). Existing tests changed as
  planned, and `ConferenceRoomWriteIdentityTests.TheOnPremRoomListFallback_AddsTheProvenDirectoryObject`
  also pins every `Get-ADUser` to the proven objectGUID, so the DN written can only be that object's.

## Guard proof

| Test | What it pins |
| --- | --- |
| `ConferenceRoomRoomListPreflightTests.ARoomListGroupInAnotherDomain_IsFoundThroughTheForestGlobalCatalog` | the finding: a synced list whose group is in `fabrikam.test`, the room's object in `contoso.test`. The row succeeds, not partial, through the on-prem add; both searches (preflight, add) bound `-Server gc01.contoso.test:3268`; the one write is `Set-ADGroup -Identity <the group's objectGUID> -Server fabrikam.test` adding the room's DN; no `Add-ADGroupMember` |
| `TheHarness_AnswersAGroupSearchOnlyFromTheDomainsItsServerReaches` | the default-domain shape, in the stand-in: a search with no `-Server`, or with `:3268`, returns only the `contoso.test` group - a default-domain search would have missed the other; the catalog endpoint returns both; `fabrikam.test` only its own; an unknown server is an error |
| `ARoomListNameMatchingAGroupInEachDomain_IsRefusedBeforeAnyWrite` | cross-domain ambiguity: a matching group in each domain - refused, not partial, "Nothing was written.", no write; the one search was the catalog's, and nothing was read by identity |
| `AForestWhoseGlobalCatalogCannotBeResolved_IsRefusedBeforeAnyWrite` (Get-ADForest fails; reports no catalog; reports a blank host) | no catalog, the group in the default domain, where a one-domain search would find it: refused, not partial, "Nothing was written.", no write, one `Get-ADForest`, no `Get-ADGroup` at all |
| `AGlobalCatalogEndpoint_IsAHostAndItsPort_NeverABarePort` (five cases) | a null or blank host gives no endpoint, never `:3268`; a host, trimmed, gives `host:3268` |
| `TheGroupFoundInTheForest_IsReReadInItsOwnDomain_SoThePreflightAndTheAddResolveTheSameObject` | in each phase the catalog search, then a re-read of the found DN on `fabrikam.test` (not the member read), then the membership read of that DN on `fabrikam.test`; the preflight's reads all before Set-Place, the add's after; the write names the objectGUID the re-reads answered, on `fabrikam.test` |
| `AGroupItsOwnDomainHoldsAsAnotherObject_IsRefusedBeforeAnyWrite` | the group's own domain answers its DN with another objectGUID: refused at the re-read - not partial, no write, no membership read |
| `TheHarness_AWriteFindsItsGroupAndMembersOnlyInTheDomainItReaches` | the stand-ins: `Add-ADGroupMember` accepts a member of the domain it reaches and refuses one from another (the 2026-09-03 failure), and without `-Server` cannot find the other domain's group; `Set-ADGroup` finds that group only when routed to its domain |
| existing tests, changed as planned | `AProvableRow_ForASyncedRoomList_WritesInTheSameOrderAsBefore` (the same order, `Set-ADGroup` last); a2i's already-member, cap and same-name tests (the write is `Set-ADGroup`, member = the room's DN); `ConferenceRoomDirectoryAuditTests` (the audit still names the room's objectGUID as written); `ConferenceRoomWriteIdentityTests.TheOnPremRoomListFallback_AddsTheProvenDirectoryObject` |

**Mutations** (`scratchpad/a2j-gc/round_a2j.py` in the session scratchpad, log `round.log` beside
it, 2026-10-22 07:58-08:09; the revert row by `round_revert.py`, the demonstrations by
`round_demo.py`, the a2i re-proof by `round_a2i_m10.py`, same runner). Each edit was applied only if
its old text occurred exactly once and the md5 changed; `dotnet test` on the test project (build
included) ran the named tests with a TRX logger; the file was restored from the saved bytes and
touched (`os.utime`), and the md5 re-checked. M rows: `Services/ConferenceRoomService.cs`, md5
before = restored = `5c25ef5ee32634edcf593a2782725a96`. H rows:
`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`, before = restored =
`b3b055903db1f4b43749eb542e85d91f`. Test names are in `ConferenceRoomRoomListPreflightTests`.

| # | Mutation | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- |
| revert | the service file replaced whole by HEAD's (the file aud-a2i committed), and the pure endpoint test - the one test naming a new production member, so it cannot compile against that file - removed from the test file (`467180b750a8a7b934442c24d5fee93d` -> `da9c87d8779c6068c3d62d2512ff5bbf`, restored) | `090908dc7b22a90c4ecb2114d8464875` | the five behaviour tests | FAIL, all seven cases: the other-domain group "No on-prem AD group found for room list 'Building 1 Conference Rooms'." (the finding's predicted failure) in two; the two-domain name written to the default-domain group; all three no-catalog rows written; the other-object case refused for the old not-found reason. A first attempt with the test file as fixed could not compile (CS0117), hence the second file |
| M1 | `-Server` dropped from the catalog search | `3ca91cb4925b820ab268ae43fecc017d` | `ARoomListGroupInAnotherDomain_IsFoundThroughTheForestGlobalCatalog`; `ARoomListNameMatchingAGroupInEachDomain_IsRefusedBeforeAnyWrite` | FAIL both: "No on-prem AD group found ... Nothing was written."; the two-domain name written |
| M2 | the search's `-Server` a bare `":3268"` | `f8201a480c2ab596806f6298173bf232` | the same two | FAIL both, the same outcomes - the stand-in answers `:3268` from the default domain, as the real cmdlet does |
| M3 | a catalog failure falls back to the search without `-Server` | `dcea3b356b913c1c901d2b51588ec24a` | `AForestWhoseGlobalCatalogCannotBeResolved_IsRefusedBeforeAnyWrite` | FAIL, all three: written |
| M4 | the re-read removed, the catalog row carried | `eb0412e7c2df45f51467c0422fa4451e` | `TheGroupFoundInTheForest_IsReReadInItsOwnDomain_SoThePreflightAndTheAddResolveTheSameObject` | FAIL (0 re-reads of 2); also `AGroupItsOwnDomainHoldsAsAnotherObject_IsRefusedBeforeAnyWrite` - still refused, but by the a2i read's own check, after a membership read |
| M5 | the re-read's objectGUID check dropped | `955cc921f091092b97bd0fedbf4fcca9` | `AGroupItsOwnDomainHoldsAsAnotherObject_IsRefusedBeforeAnyWrite` | FAIL: partial - the other object's GUID carried through every read and refused only by the write, after Set-Place |
| M6 | the blank-host guard dropped (`host is null ? ...`) | `8699d4270fddb78e970979b5cbee106a` | `AForestWhoseGlobalCatalogCannotBeResolved_IsRefusedBeforeAnyWrite`; `AGlobalCatalogEndpoint_IsAHostAndItsPort_NeverABarePort` | FAIL: the blank-host row written through `:3268`; the endpoint `":3268"` for `""` and `"   "`. The other cases PASS - not this guard's |
| M7 | the write's `-Server` dropped | `3c3f96e289ecc9d2c5110daf13b0931d` | `ARoomListGroupInAnotherDomain_IsFoundThroughTheForestGlobalCatalog` | FAIL: partial, "Cannot find an object with identity" - the group sought in the default domain |
| M8 | the old write restored, routed: `Add-ADGroupMember -Identity <groupGuid> -Members <roomGuid> -Server <group's domain>` | `907ab298ea15d33d3c0c810cf668ccc3` | the same | FAIL: partial, "Cannot find an object with identity" - the room sought in the group's domain, the 2026-09-03 failure |
| H1 | the stand-in: no `-Server` reaches every domain | `d1775108848d87e793e53256da6d90a3` | `TheHarness_AnswersAGroupSearchOnlyFromTheDomainsItsServerReaches` | FAIL |
| H2 | the stand-in: a bare port reaches every domain | `35242a89543d458fe07cbb3b1cbefa0a` | the same | FAIL |
| H3 | the stand-in `Add-ADGroupMember` resolves members anywhere | `0a46fe9ae1ed54e2eefbbf7a5810fc6a` | `TheHarness_AWriteFindsItsGroupAndMembersOnlyInTheDomainItReaches` | FAIL |
| H4 | the stand-in `Set-ADGroup` finds its group in any domain | `a1558d26f3de9e8e476dd9b34a9da722` | the same | FAIL |
| H1+M1 | *demonstration, not a guard:* H1 and M1 together | - | the M1 pair | the behaviour assertions PASS - the wrong-reason pass the stand-in's scoping exists to stop; each FAILS only on its `-Server` pin |
| H3+M8 | *demonstration:* H3 and M8 together | - | `ARoomListGroupInAnotherDomain_IsFoundThroughTheForestGlobalCatalog` | the behaviour assertions PASS; it FAILS only on its `Set-ADGroup` pin |
| A10 | aud-a2i's M10 again, behind the new re-read: the membership read's objectGUID check dropped | `35d203214c31120aefd73cf8a38de877` | `AnOnPremGroupWhoseMembershipCannotBeRead_IsRefusedBeforeAnyWrite` | FAIL, "the group answers as another object" (now the member-read knob); the other two PASS, as in aud-a2i - the a2i guard is still reachable |
| A2 | aud-a2i's M2 again: the preflight's membership read deleted | `8b4a8a25f0a1a475745829655c93db8b` | the same | FAIL, "another object" and "no DN"; PASS, "the membership read fails" - refused at the re-read, which comes first (aud-a2i recorded all three failing) |

Every restored md5 equal to "before"; the restored tree rebuilt, and the 197 Conference Rooms and
`GroupMemberListingTests` cases green.

**aud-a2i's guard proof, as it now stands.** Its "the membership read fails" case
(`Failures['Get-ADGroup -Identity']`) now trips at the re-read: still refused before any write, but no
longer a proof of the membership read's own failure path, so aud-a2i's M2 now rests on the other two
cases (A2), as its M3 always did. Its "the group answers as another object" case uses
`DirectoryGroupMemberReadBack`, so the re-read passes and the membership read's check is what refuses
(A10).

**Comments after the round.** The round ran on `7d863547`, the fix without its explanatory comments;
they were written afterwards. `scratchpad/a2j-gc/comment_only.py` over `git diff --unified=0
7d863547` checks that every changed line is blank, `//` or `///`, or a PowerShell `#` line in the
harness's stand-in script: service `5c25ef5ee32634edcf593a2782725a96` ->
`4de21d801a49265f0868fa299d3f5d9d` (62 changed lines, 0 code); harness
`b3b055903db1f4b43749eb542e85d91f` -> `9b0d8a85354f0bc58bec945f2e56a981` (63, 0);
`ConferenceRoomRoomListPreflightTests.cs` `467180b750a8a7b934442c24d5fee93d` ->
`64424c1b7036e60a6fcf650d401ad595` (55, 0); `ConferenceRoomWriteIdentityTests.cs`
`e22bdba27c0559f47e42f7fb8089faed` -> `bc554509f28d023e73d8ae0c40ed26ac` (2, 0);
`ConferenceRoomDirectoryAuditTests.cs` unchanged (`ca06a2e86982470f795c1242d7148237`). The gates in
the Closeout ran on the final files.

## Known gaps

- *Assumption, the repo's measured idiom, not re-probed (no directory call was made):* a
  `Get-ADGroup` search against `<host>:3268` with no `-SearchBase` covers every domain of the forest.
  `ADDirectorySearchService.ResolveGlobalCatalog` records the measurement (one search, 18 + 7
  matches across two domains whose DNs are not nested), and `PowerShellProtectedGroupDirectory`'s
  protected-group reads rest on the same shape. Were it wrong, a group outside the part covered would
  be "not found" - refused before any write, as before - and a same-named group there would not count
  against the exactly-one rule, which is the one-domain shape this replaced, narrowed rather than
  gone.
- *Assumption, Active Directory's documented scope rules:* a room-list group can hold a member from
  another domain - a universal group can, as a mail-enabled distribution group is; a global group
  cannot. Such a refusal comes from the write, after Set-Place: partial and loud, never a wrong
  write. Not pre-empted by a local rule, the Group Management posture (AD's own refusal surfaces).
- *Assumption:* the module credential can read the forest (`Get-ADForest`) and its global catalog.
  If it cannot, every row needing the on-prem add is refused before any write, where the old code
  searched one domain - the brief's rule, operator-visible.
- The write-members right is now needed on the room-list group in whatever domain holds it, not only
  in the default one; no read can prove it (the plan's own "VERIFY IN DEV" item, per domain now). A
  missing right is partial after the writes, as before.
- Unchanged and out of scope: the room's own object is read in the default domain
  (`ConfirmDirectoryObject`, no `-Server`), where the protection gate proved it
  (`ProtectedPrincipalService.FindMailboxGuidHoldersAsync`, whose documented scope is the default
  domain; a holder elsewhere is "Unavailable" and refused at the gate). So the cross-domain case this
  serves is a room in the default domain and its list's group anywhere in the forest.
- The inherent two-system residual is unchanged in kind: the add repeats the preflight's reads after
  the writes and can still fail there - a group renamed, moved or deleted in between, the catalog or
  a DC unreachable - partial, as before.
- Cost: a row that takes the on-prem add now makes two `Get-ADForest` reads and two more
  `Get-ADGroup` reads (the re-reads), one each in the preflight and the add.

**Operator-visible changes.**

- A room list synchronized from on-premises whose group lives in another domain of the forest now
  takes the room on-prem, written on that domain. It used to be refused before any write as "No
  on-prem AD group found".
- A list name matching a group in more than one domain is refused before any write ("Multiple on-prem
  AD groups ... match"); before, only the default domain's group was seen and written.
- A forest whose global catalog cannot be resolved refuses such a row before any write, with the
  reason; before, the default domain was searched.
- The room-list preflight's group refusals now end "Nothing was written.", as its other refusals do.
- Nothing else: the same writes in the same order for every other row, the same step names and
  messages, the same audit (the room's objectGUID as the object written).

**Dev acceptance owed** with aud-a2f and aud-a2i: Room Finder for a synced room whose Building's room
list is synchronized from a group in a second domain of the forest - the room added on-prem on that
domain, the ~30-minute sync note, the membership visible there at once. And aud-a2f's check (1), a
synced room on a synced list in the default domain, now exercises the catalog search and the
`Set-ADGroup` write on the common path.

**Page:** unchanged; no `ClickGateRegistry` entry moved.

## Reviewer comments

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent on
`worktree-agent-a0b52fcc43c6766da`. Independent review pending.

For the reviewer, one change beyond the brief's list: the on-prem write's form, `Add-ADGroupMember`
to `Set-ADGroup -Add @{ member = ... }` (the Plan's third bullet carries the reason). It changes the
write on a path whose dev acceptance is still owed (aud-a2f's check (1)); the write-members right it
needs is the same.

## Closeout

Fixed on branch, not merged, not pushed: the plan `618f33c9`, the fix and tests `7d863547`, then the
comments and this closeout. A module-scoped behaviour change: the Conference Rooms module version
bump is due at merge. No shared code changed (`GroupManagementService` is reused, not modified) and the
harness change is test-only, so no base app bump from this finding.

Gates, run on the final files:

- `dotnet build ExchangeAdminWeb.slnx -c Release` (`--no-incremental`): 0 errors, 61 warnings - the
  baseline count at `68c97ede` - none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: 5005 passed, 0 failed,
  3 skipped (the known AD skips), 5008 total, 6 m 47 s; nothing hung, no Sequence file generated.
  That is aud-a2i's 4994 plus the 14 new cases here.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD`: clean.
- `tools/Test-AsciiOnly.ps1` after `git add`: clean.

PSScriptAnalyzer and Pester were not run: no `.ps1` or `.psm1` changed.
