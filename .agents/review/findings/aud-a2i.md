# aud-a2i: The on-prem room-list add's already-member check is the last Get-ADGroupMember read, and the preflight does not make it

**Severity**: LOW - no wrong object is written, but on a room list whose on-prem group the cmdlet
cannot read, every row that takes the on-prem add comes back partial after Set-Place and the timezone
committed, on every re-run.
**Status**: Merged to master; closed on coder-side guard proof (LOW); dev acceptance owed with a2f
**Branch**: `worktree-agent-a69c682cecca186cc`
**Commit**: `294e9d4` (merged)
**Reviewer**: implementer (surfaced by the aud-a2h agent)

## Evidence

Surfaced by the aud-a2h agent's audit of every membership read (`aud-a2h.md`, "Other membership reads
audited", the candidate paragraph) and admitted by the supervising session (`7e50b717`). At
`7e50b717`, `Services/ConferenceRoomService.cs`, the on-prem add's idempotency check in
`AddToRoomListViaAdAsync`:

```
1664                // Idempotency: skip the add if the room is already a member (matches the cloud
1665                // path's already-member no-op so a re-run is safe).
1666                ps.AddCommand("Get-ADGroupMember")
1667                  .AddParameter("Identity", groupGuid)
1668                  .AddParameter("Credential", credential)
1669                  .AddParameter("ErrorAction", "Stop");
1670                var members = ps.Invoke();
1671                if (ps.HadErrors)
1672                    return (FirstError(ps) ?? $"Failed to read membership of on-prem room list '{roomListName}'.", null);
```

and its failure lands after the writes - the add's catch:

```
1692            catch (Exception ex)
1695                return ($"Failed to add the room to the on-prem room list on the AD object: {ex.Message}", null);
```

which `SetRoomMetadataAndListAsync` reports as a partial, Set-Place and the timezone committed:

```
589                result.Success = false;
590                result.Partial = true;
591                result.Message = $"Partial: room metadata was set, but adding the room to its room list failed: {listResult.Message} Re-run this row after fixing the cause.";
```

The aud-a2f preflight, which exists so that what the on-prem add needs is proven before any write,
reads the room object and the group and stops there - the membership read is never made before the
writes:

```
1601                // The two objects the on-prem add names - the room's proven object and the list's
1602                // group - each read here exactly as the add will read them, without writing.
1603                var roomError = ConfirmDirectoryObject(ps, roomGuid, roomEmail, credential);
1607                var (groupError, _) = ResolveAdGroupGuid(ps, list.Name, credential);
1608                return groupError;
```

`Get-ADGroupMember` resolves every member server-side, and the repository has already moved every
other membership read off it for exactly the two ways that fails - `Services/Comms10kService.cs`:

```
39    /// Download CSV simply threw on the real group.</b> It called <c>Get-ADGroupMember</c>, which
40    /// expands every member into a full object and is bound by the ADWS
41    /// <c>MaxGroupOrMemberEntries</c> cap (default 5,000) - so it raised past that size, which is
111        // Get-ADGroup -Properties member, NOT Get-ADGroupMember: the linked attribute comes back
112        // by range retrieval and is not subject to the ADWS MaxGroupOrMemberEntries cap, which is
```

and `Services/GroupManagementService.cs`:

```
414            // The member list comes from the group's own linked attribute, NOT Get-ADGroupMember:
415            // that cmdlet makes ADWS resolve every member server-side and faults the WHOLE read
416            // ("An operations error occurred", GetADGroupMemberFault) when a member belongs to
417            // another domain in the forest and cannot be chased under this credential - one
423            ps.AddCommand("Get-ADGroup")
424              .AddParameter("Identity", resolvedDn)
425              .AddParameter("Properties", new[] { "member", "mail" })
430            var memberReadServer = ServerFromDn(resolvedDn);
431            if (memberReadServer is not null)
432                ps.AddParameter("Server", memberReadServer);
```

The harness could not show it: the stand-in `Get-ADGroupMember` (`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs:498-502`)
answers every group with every configured member, whatever its size or wherever its members live.

## Predicted observable failure

Room Finder for a synced room whose Building's room list is synchronized from on-premises, the room
not on Exchange's copy of the list (a first add, or a re-run inside the ~30-minute sync window after
an on-prem add), and the list's on-prem group either past the ADWS cap (more than 5,000 members by
default) or holding a member in another domain that ADWS cannot chase under the module credential:
the preflight passes (it reads only the room object and the group), Set-Place, City/State/Country and
the timezone pair commit, the cloud add is refused as on-prem-mastered, the fallback's
`Get-ADGroupMember` fails ("The size limit for this request was exceeded" / "An operations error
occurred"), and the row reports "Partial: room metadata was set, but adding the room to its room list
failed: ..." - on every re-run, and the room is never added. A room already on Exchange's copy of the
list is unaffected (the preflight and the add find it there and never reach the fallback).

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21); the
admitted approach, from the supervising session's brief: "replace the read with the repo's
established idiom (read the group's `member` attribute by the group's DN against the domain that
holds the group, compare by the room's proven objectGUID/DN, never by name), and move the
already-member decision into the a2f preflight so the on-prem add path decides before any write; the
add itself then needs no membership read, or re-checks with the same idiom. Keep the per-row outcome
shape."

**Rule.** Whether the on-prem add will write is settled before any write, from a read that neither
the group's size nor where its members live can refuse.

- **The read - one helper, `ReadOnPremListMembership`, called by the preflight and by the add** (the
  aud-a2f shape: the preflight reads what the add reads, the same way). `Get-ADGroup -Identity <the
  group's DN> -Properties member -Credential <module credential> -ErrorAction Stop`, with `-Server`
  the domain the group's DN names (`GroupManagementService.ServerFromDn`; omitted only for a DN that
  names none, exactly as `GroupManagementService.GetMembersAsync` does). The group's own linked
  attribute: it resolves no member, so no member in another domain can fault it, and it is not bound
  by `MaxGroupOrMemberEntries` (`Comms10kService.cs:111-113`; *assumption, documented behaviour and the
  repo's own record, not re-probed*). The answer must be exactly one object carrying the objectGUID
  `ResolveAdGroupGuid` resolved; anything else is an error.
- **The comparison.** The room is a member when the group's member DNs
  (`GroupManagementService.MemberDnsOf`) include the distinguished name of the room's AD object as
  `ConfirmDirectoryObject` read it - the object proven by objectGUID (aud-a2d) - compared
  ordinal-ignore-case, as DNs are compared elsewhere in the repo. Never a name, an address or a UPN.
  A room object read without a DN is an error, never "not a member".
- `ConfirmDirectoryObject` reports the DN it read (an `out` overload; the City/State/Country callers
  keep the existing signature, unchanged); `ResolveAdGroupGuid` reports the group's DN beside its
  objectGUID.
- **The preflight** (`PreflightRoomListAsync` - a synced list the room is not on in Exchange, the room
  object and the group proven): reads the membership. A read that fails refuses the row before any
  write - `Success = false`, `Partial = false`, the one failed "Room list preflight" step, "Nothing was
  written." Member or not, it passes: either way the add can complete.
- **The add** (`AddToRoomListViaAdAsync`): the `Get-ADGroupMember` read is replaced by the same helper,
  after the add's own re-read of the room object and the group, as now. A member: `(null, null)`, the
  no-op (aud-a2g's "nothing written" unchanged); not: `Add-ADGroupMember` by both objectGUIDs, as now.
  It re-checks rather than carrying the preflight's answer across the writes: the add already re-reads
  both objects before writing (aud-a2f's accepted residual), and a stale "already a member" carried
  forward would report success, silently, for a room taken off the group in between; a re-read adds
  it, or fails loudly.
- **Unchanged:** every write, their order, every step name and outcome message, the cloud path,
  `IsOnRoomListAsync` and the Exchange-side membership test.

**Tests** (`ExchangeWriteHarness` idiom):

- *Harness.* The stand-in `Get-ADGroup` reports each group's DN on the search, and answers a read by
  `-Identity` (DN or objectGUID) with that group and, when `-Properties` asks for it, its `member`
  attribute - `DirectoryGroupMembers`, which now holds DNs (it held objectGUIDs, for the old stand-in).
  `Failures["Get-ADGroup -Identity"]` fails that read alone; `DirectoryGroupReadBack` makes it answer
  as another object. The stand-in `Get-ADGroupMember` refuses as the real cmdlet does - more than
  `MaxGroupOrMemberEntries` (5,000) members, or a member outside the stand-in domain - and otherwise
  answers each member's objectGUID, so a restored call is refused for the reasons the real one is.
- `ConferenceRoomRoomListPreflightTests`:
  - a room already in its on-prem group by the `member` attribute (its DN as read, and in another case),
    not yet on Exchange's copy of the synced list, Exchange refusing the cloud add: succeeds, not partial,
    no `Add-ADGroupMember`, no `Get-ADGroupMember`; two membership reads, the first before Set-Place and
    the second after, each bound to the group's DN, `-Properties member`, `-Server` its domain;
  - a group `Get-ADGroupMember` would refuse - over the cap; a member in another domain - with the room
    already a member and not: succeeds, not partial, no `Get-ADGroupMember`, `Add-ADGroupMember` exactly
    when the room was not a member;
  - another object with the room's name in the group (same CN, another DN): the room is added;
  - a membership that cannot be read - the read fails; the group answers as another object; the room's
    object has no DN: refused before any write, not partial, the message naming the list;
  - the harness itself: the stand-in `Get-ADGroupMember` answers a small group in its domain and refuses
    a group over the cap and a member in another domain.
- `GroupMemberListingTests`: no `.cs` or `.razor` under `Services/` or `Components/`, `//` comments
  stripped, names `Get-ADGroupMember` (a tripwire; non-vacuous - the scan includes
  `ConferenceRoomService.cs`).
- `ConferenceRoomDirectoryAuditTests.ARoomAlreadyOnItsOnPremList_IsNotRecordedAsWritten`: fixture only -
  the room is a member by its DN on the `member` attribute; assertions unchanged.

**Mutations, at the call sites:** the service file reverted to `7e50b717`; the old `Get-ADGroupMember`
read restored in the add (the tripwire and the cap test must fail); the preflight's membership read
deleted; its returned error ignored; the add's re-check deleted (always add); the helper answering
"member" for every room; the comparison by the member's name; case-sensitive; `-Server` not passed;
`-Properties member` not asked for; the read-back objectGUID check dropped; the room-DN check dropped;
in the harness, each `Get-ADGroupMember` refusal removed. A named test fails for each.

**Files.** `Services/ConferenceRoomService.cs`, `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`,
`ExchangeAdminWeb.Tests/ConferenceRoomRoomListPreflightTests.cs`,
`ExchangeAdminWeb.Tests/ConferenceRoomDirectoryAuditTests.cs` (fixture),
`ExchangeAdminWeb.Tests/GroupMemberListingTests.cs` (tripwire), an amendment pointer in
`docs/ConferenceRooms-OnPremRoomListAdd-Plan.md` (its idempotency step), an admission pointer in
`aud-a2h.md`'s candidate paragraph, this record. No page change.

## Approach (as implemented)

- `ReadOnPremListMembership` (`Services/ConferenceRoomService.cs:1781`): refuses a room DN that is
  missing or blank; reads `Get-ADGroup -Identity <group DN> -Properties member -Credential ...
  -ErrorAction Stop`, adding `-Server` from `GroupManagementService.ServerFromDn(groupDn)` when the DN
  names a domain; requires exactly one object back whose objectGUID is the one `ResolveAdGroupGuid`
  resolved; then answers `GroupManagementService.MemberDnsOf(group).Contains(roomDn,
  StringComparer.OrdinalIgnoreCase)`. Both group helpers are reused, not copied. A terminating read
  error propagates to the caller's existing catch, which refuses (preflight) or reports the add failed
  (add); `HadErrors` is checked too, as every helper in the file does. A group with no DN needs no
  check of its own: `-Identity $null` is refused by the cmdlet, so it fails closed through that catch.
- `ConfirmDirectoryObject` gained an `out string? distinguishedName` overload, which the original
  signature now calls with `out _` (the City/State/Country callers are unchanged); the DN is reported
  only once the object has been confirmed. `ResolveAdGroupGuid` returns
  `(error, objectGuid, distinguishedName)`; its four refusals are unchanged.
- Preflight (`:1617`): after the group resolves, the membership is read; an error is returned with
  " Nothing was written." appended - the existing "Room list preflight" refusal shape. Member or not,
  it passes.
- Add (`:1680`): the `Get-ADGroupMember` block is replaced by the helper call; an error returns
  `(error, null)` - the add's existing failure shape, partial at the row - a member `(null, null)`, and
  otherwise `Add-ADGroupMember` by both objectGUIDs, unchanged.
- Harness (`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`): `DefaultDirectoryGroupGuid`,
  `DirectoryDomainDn`, `DirectoryGroupDnFormat` and `DirectoryGroupDn(...)`, `DirectoryGroupReadBack`,
  `AdwsMaxGroupOrMemberEntries` (5000), passed into the directory runspace. The stand-in `Get-ADGroup`
  (`:536`) and `Get-ADGroupMember` (`:559`) behave as planned, every refusal written as `Write-Error`
  then `return` (aud-a2h's finding: a `throw` is suppressed under `-ErrorAction SilentlyContinue`).
- Tests: the five in `ConferenceRoomRoomListPreflightTests` (`:344`, `:373`, `:406`, `:423`, `:447`,
  eleven cases) and the tripwire `GroupMemberListingTests.NoProductionCode_CallsGetAdGroupMember`
  (`:221`). One deliberate split: the cap test asserts the row's outcome only, while the "no
  `Get-ADGroupMember` call" pin is the tripwire's and the already-member test's. That way the cap test
  fails under the restored read only because the stand-in refuses, as the real cmdlet would - the
  H1+H2+M1 row below shows the difference.

## Guard proof

| Test | What it pins |
| --- | --- |
| `ConferenceRoomRoomListPreflightTests.ARoomAlreadyInItsOnPremGroup_ByTheMemberAttribute_IsNotAddedAgain` (DN as read; DN in another case) | the brief's already-member case: the room's DN on the group's `member` attribute, not on Exchange's copy of the synced list, the cloud add refused. Succeeds, not partial, no `Add-ADGroupMember`, no `Get-ADGroupMember`; exactly two membership reads, one before Set-Place (the preflight) and one after (the add), each bound `-Identity` the group's DN, `-Server` `contoso.test`, `-Properties` including `member` |
| `AGroupGetAdGroupMemberWouldRefuse_NoLongerAffectsTheRow` (over the cap / a member in another domain, x the room a member / not) | the brief's cap test, and the finding's second shape: a 5,001-member group, or one with a member in another domain - both refused by the stand-in `Get-ADGroupMember` as by the real one - and the row succeeds, not partial; `Add-ADGroupMember` (the proven objectGUID) exactly when the room was not a member |
| `AnotherObjectWithTheRoomsName_InItsOnPremGroup_IsNotTheRoom` | never by name: a member with the room's CN in another OU is another object, and the room is added |
| `AnOnPremGroupWhoseMembershipCannotBeRead_IsRefusedBeforeAnyWrite` (the read fails / the group answers as another object / the room's object has no DN) | decided before any write: each refuses the row, not partial, the message naming the list and ending "Nothing was written.", no write at all |
| `TheHarness_GetAdGroupMember_RefusesAGroupOverTheCapAndAMemberInAnotherDomain` | the stand-in: a small group in its domain is answered (the member's objectGUID resolved); a member in another domain - "An operations error occurred"; 5,001 members - "The size limit for this request was exceeded" |
| `GroupMemberListingTests.NoProductionCode_CallsGetAdGroupMember` | the brief's tripwire: no `.cs` or `.razor` under `Services/` or `Components/`, `//` comments stripped, names `Get-ADGroupMember` (case-insensitive); the scan must include `ConferenceRoomService.cs` |
| `ConferenceRoomDirectoryAuditTests.ARoomAlreadyOnItsOnPremList_IsNotRecordedAsWritten` (fixture only) | aud-a2g's already-member audit, now reached through the `member` attribute |
| `ConferenceRoomRoomListPreflightTests.AProvableRow_ForASyncedRoomList_WritesInTheSameOrderAsBefore` (unchanged) | the writes and their order, unchanged |

**Mutations** (`scratchpad/a2i/round_a2i.ps1` in the session scratchpad, log `round.log` beside it,
2026-10-21 22:47-22:51). For each row, the edit was applied only if its old text occurred exactly once
and the md5 changed; the test project was rebuilt; the named tests ran with a TRX logger; and the file
was restored from the saved bytes and touched, with the md5 re-checked. M and revert rows:
`Services/ConferenceRoomService.cs`, md5 before = restored = `ca64304c36aae0c271431f86afc5bef0`. H rows:
`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`, md5 before = restored =
`b48d7c020585164c2a4da2aae935ad12`. Test names are in `ConferenceRoomRoomListPreflightTests` unless
qualified.

| # | Mutation | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- |
| revert | the service file replaced whole by HEAD's (`7e50b717`), the rest of the tree as fixed | `cd1eeb1a7632a75c2f14e7c214ac975f` | the eight above | FAIL, 10 cases: all 4 cap cases (partial: "Failed to add the room to the on-prem room list on the AD object: The running command stopped ..." - the predicted failure), all 3 refusal cases (written instead of refused), both already-member cases (`Get-ADGroupMember` called), the tripwire. PASS, 5 cases, as expected: the name test, the harness test, both audit surfaces, the order test - each tests a behaviour the fix keeps or the harness only |
| M1 | the brief's mutation: the old `Get-ADGroupMember` read restored in the add, in place of the re-check | `e2d19102f9d257d9b85983fe46c93703` | the tripwire; the cap test | FAIL: the tripwire (`Services\ConferenceRoomService.cs`); all 4 cap cases, partial. Also FAIL: both already-member cases |
| M2 | the preflight's membership read deleted | `3bad92dbfefc61eeb5689c8855d30f31` | `AnOnPremGroupWhoseMembershipCannotBeRead_IsRefusedBeforeAnyWrite` | FAIL, all 3 (partial after the writes); also both already-member cases (one read, not two) |
| M3 | the preflight ignores the helper's returned error (`return null;`) | `a8370ec86afc00c516ac0768a45fc03b` | same | FAIL: "another object", "no DN". PASS: "the read fails" - a terminating error never reaches the returned value; the preflight's own catch refuses it (M2 removes the read and fails it) |
| M4 | the add's re-check deleted - it always adds | `2f0d779514bd2b557fdf9ab9603391ff` | `ARoomAlreadyInItsOnPremGroup_ByTheMemberAttribute_IsNotAddedAgain` | FAIL, both; also `ConferenceRoomDirectoryAuditTests.ARoomAlreadyOnItsOnPremList_IsNotRecordedAsWritten`, both surfaces |
| M5 | the helper answers "a member" for every room | `3e22e976050f048bd02c85350be2e4b0` | `AnotherObjectWithTheRoomsName_InItsOnPremGroup_IsNotTheRoom` | FAIL; also the two not-a-member cap cases and `AProvableRow_ForASyncedRoomList_WritesInTheSameOrderAsBefore` (no `Add-ADGroupMember`) |
| M6 | the comparison by the member's CN (`DisplayNameFromDn`) instead of the proven DN | `807b721e85094090542b31f0a59704d0` | same | FAIL |
| M7 | the comparison case-sensitive (`StringComparer.Ordinal`) | `dcbcedbe37cb98ab4d7ce736269ad4e8` | `ARoomAlreadyInItsOnPremGroup_ByTheMemberAttribute_IsNotAddedAgain` | FAIL, the "another case" case (the add sent); the as-read case passes |
| M8 | `-Server` not passed | `d3500a824fa0566fc67c77c737e4770d` | same | FAIL, both (the `-Server` pin) |
| M9 | `-Properties member` not asked for | `34897fa59e62108908cefeaa7176419f` | same | FAIL, both (no attribute, so "not a member", and the add sent) |
| M10 | the read-back objectGUID check dropped | `0598fdc66b8b7c247b2ce394bd15022b` | `AnOnPremGroupWhoseMembershipCannotBeRead_IsRefusedBeforeAnyWrite` | FAIL, "another object"; the other two pass (not this guard's) |
| M11 | the room-DN check dropped | `555e324ac95efca18c826f071940f89c` | same | FAIL, "no DN"; the other two pass (not this guard's) |
| H1 | the stand-in `Get-ADGroupMember` no longer refuses a group over the cap | `fafd70d2d61d0d70d904eedf7356fcc5` | `TheHarness_GetAdGroupMember_RefusesAGroupOverTheCapAndAMemberInAnotherDomain` | FAIL |
| H2 | the stand-in no longer refuses a member in another domain | `63401e69e5ca93bd46a707af47508772` | same | FAIL |
| H1+H2+M1 | *demonstration, not a guard:* both stand-in refusals removed (harness `c3abb719cf50f2f9fdf7bfe518f67f1f`) AND the old read restored (service `e2d19102...`, as M1) | - | the cap test | PASSES, all 4 - the wrong-reason pass the stand-in's refusals exist to stop. The tripwire, both already-member cases and the harness test FAIL |

Every restored md5 equal to "before"; the final rebuild on the restored tree and all 15 named cases
green.

**Comments after the round.** The round ran on the fix without its explanatory comments, which were
written afterwards. `scratchpad/a2i/comment_only_a2i.ps1` checks the result: every changed line in
`git diff --no-index --unified=0` between the round copy and the final file is blank, `//`, `///` or a
PowerShell `#` line. Service `ca64304c36aae0c271431f86afc5bef0` -> `090908dc7b22a90c4ecb2114d8464875`
(37 changed lines, 0 code); harness `b48d7c020585164c2a4da2aae935ad12` ->
`416a5966353f50fa7ec82e2eace6726a` (32, 0); `ConferenceRoomRoomListPreflightTests.cs`
`63ada29ca3565a814a0e172ee15ab0cf` -> `6efbdc4c1800bdf7c3950bcf82f9fc8c` (26, 0);
`GroupMemberListingTests.cs` `765d48c4d360a601db4d6420dc29b65d` -> `a6160f2a43b017578fceb969ca0ce521`
(8, 0); `ConferenceRoomDirectoryAuditTests.cs` `d86d27c67b65de93c42365d23a012d60` ->
`cd04368441935a30798782bccab6b00c` (3, 0). The gates below ran on the final files.

## Known gaps

- *Assumption, documented behaviour and the repo's own record, not re-probed live:*
  `Get-ADGroup -Properties member` returns every member of a group past 5,000 (the ActiveDirectory
  module retrieves the linked attribute by range) and resolves none, so neither the ADWS cap nor a
  member in another domain can fail it. `Comms10kService.cs:111-113` records the first for the
  10k-member group, and `GroupManagementService.cs:414-419` the second from dev (2026-08-28). No
  directory call was made here.
- *Assumption, AD's documented behaviour, not probed:* the `member` attribute does not carry
  primary-group membership (lst-2's lesson). A room whose primary group was its room list's group
  would read "not a member", so `Add-ADGroupMember` would be sent, and AD refuses to add an account to
  its primary group ("already a member"). The row would come back partial - loud, as before this fix,
  never a false success or a wrong write. A primary group must be a security group, so this needs a
  security-enabled room-list group made a room account's primary group; no union was added for it.
- The inherent two-system residual is unchanged in kind. The add re-reads the membership after the
  writes and can still fail there - a DC unreachable, the credential revoked, the group renamed in
  between - partial, as before. What can no longer fail it is the group's size or where its members
  live, and the preflight has just made the same read.
- The preflight's Exchange-side short cut (aud-a2f repair, aud-a2h) is unchanged: a room Exchange
  already lists needs no AD read, and none is made.
- Pre-existing and out of scope: `ResolveAdGroupGuid` searches the host's default domain only, so a
  room list whose on-prem group lives in another domain of the forest is still "No on-prem AD group
  found", refused before any write since aud-a2f. The new read routes by the group's DN, so it would
  serve such a group if that search ever widened.
- Cost: a row that takes the on-prem add now reads the group's `member` attribute twice (preflight and
  add) instead of `Get-ADGroupMember` once; for a group of thousands, two ranged reads of DNs.
  Correctness was the brief.

**Operator-visible changes.**

- On a room list synchronized from on-premises whose on-prem group has more than 5,000 members, or a
  member in another domain that ADWS cannot chase, a row that takes the on-prem add now completes:
  added, or left alone when already a member. It used to be partial on every re-run.
- A room-list membership that cannot be read is now refused before Set-Place with "... Nothing was
  written." It used to surface as a partial after the writes.
- Nothing else: the same writes, steps and messages for every other row.

**Dev acceptance:** not run. Only a synced room list whose on-prem group is past the ADWS cap, or has
a member in another domain, can show the change, and the unit proof above is the evidence. aud-a2f's
owed dev check (1), a synced room on an existing synced list via the on-prem add, now exercises the new
read on the common path. Any small synced list will do for it.

**Page:** unchanged; no `ClickGateRegistry` entry moved.

## Reviewer comments

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent on
`worktree-agent-a69c682cecca186cc`. Independent review pending.

## Closeout

Fixed on branch, not merged, not pushed. This is a module-scoped behaviour change: the Conference Rooms
module version bump is due at merge. The harness and test changes are test-only and no shared code
changed, so this finding needs no base app bump. (`GroupManagementService` is reused, not modified.)

Gates, run on the final files:

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors and 61 warnings - the baseline count at
  `7e50b717` - none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 3m`: 4991 passed, 0 failed, 3
  skipped (the known AD skips), 4994 total, 5 m 25 s. Nothing hung, and no Sequence file was generated.
  The 3m timeout is the supervising session's and is stricter than the brief's 10m.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD` and `git diff --check --cached`: clean.
- `tools/Test-AsciiOnly.ps1` after `git add`: clean.

PSScriptAnalyzer and Pester were not run: no `.ps1` or `.psm1` changed.

The supervising session reported a hung run at 22:51. It was the round's own final test run
(22:51:40-22:51:48), which completed. `dotnet test ExchangeAdminWeb.slnx --filter
"FullyQualifiedName~ConferenceRoomRoomListPreflightTests" --blame-hang --blame-hang-timeout 2m` then
passed 25 of 25 in 3 s.
