# aud-a2h: The room-list membership reads ask Exchange for its default page, not the whole list

**Severity**: MEDIUM - no wrong object is written, but a legitimate Room Finder row is refused before
any write (synced list, Active Directory unprovable) or comes back partial after Set-Place and the
timezone committed (cloud list).
**Status**: Fixed on branch; awaiting merge
**Branch**: `worktree-agent-a24bdc2542a9c48b7`
**Commit**: `<pending>`
**Reviewer**: codex / gpt-5.5-dzs / xhigh, batch-5, 2026-10-21

## Evidence

Raised by the codex batch-5 review of aud-a2f (verdict `.agents/review/batch-5.result.txt` in the
main checkout, the one candidate finding). At `7d5b0961`, `Services/ConferenceRoomService.cs`.

The add's already-a-member read, in `AddToRoomListAsync`:

```
639            // Check if already a member
640            ps.AddCommand("Get-DistributionGroupMember")
641              .AddParameter("Identity", roomListName)
642              .AddParameter("ErrorAction", "SilentlyContinue");
643            var members = InvokeOptional(ps, tracker);
644            var alreadyMember = IsListMember(members, roomPrimary);
646            if (!alreadyMember)
649                ps.AddCommand("Add-DistributionGroupMember")
```

and the preflight's, added by the aud-a2f repair:

```
740    private Task<bool> IsOnRoomListAsync(string roomListName, string roomPrimary) =>
743            ps.AddCommand("Get-DistributionGroupMember")
744              .AddParameter("Identity", roomListName)
745              .AddParameter("ErrorAction", "SilentlyContinue");
746            return IsListMember(InvokeOptional(ps, tracker), roomPrimary);
```

which decides, for a list synchronized from on-premises, whether the on-prem add needs proving at all:

```
1568        if (await IsOnRoomListAsync(list.Name, roomPrimary))
1569            return null;
1571        if (room.DirectoryObjectGuid is not { } roomGuid)
```

Neither passes `-ResultSize`. Exchange Online's `Get-DistributionGroupMember` returns at most
`ResultSize` members and defaults to 1000; "Unlimited" returns them all (the cmdlet reference;
*assumption, documented behaviour, not probed live*). The repository's own complete-membership read
of the same cmdlet, the protected-principal group expansion, asks for everything -
`Services/PermissionValidator.cs`:

```
778                ps.AddCommand("Get-DistributionGroupMember")
779                  .AddParameter("Identity", identity)
780                  .AddParameter("ResultSize", "Unlimited")
781                  .AddParameter("ErrorAction", "Stop");
```

The tests could not see the gap: the harness stand-in (`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`)
answers every read with every member, whatever it was asked, and does not even declare the parameter:

```
404        function global:Get-DistributionGroupMember {
405            [CmdletBinding()] param($Identity)
406            Write-StandInCall 'Get-DistributionGroupMember' $PSBoundParameters
407            foreach ($member in $global:ListMembers) { [pscustomobject]@{ PrimarySmtpAddress = $member } }
```

## Predicted observable failure

A room already on a room list of more than 1000 members, past Exchange's first page:

- **Synced list, Active Directory unprovable** (AD credential unset, the room's AD object unreadable,
  the list's on-prem group missing or ambiguous): the preflight's read misses the room, the row takes
  the on-prem proof the aud-a2f repair exempts members from, and is refused before Set-Place - a
  re-run that changes Building/Capacity only and would have touched nothing on-prem.
- **Cloud list**: the preflight passes (a cloud list needs no proof), Set-Place and the timezone pair
  commit, the add's read misses the room, `Add-DistributionGroupMember` is sent for a member Exchange
  already holds and refused, and the row reports "Partial: room metadata was set, but adding the room
  to its room list failed" - on every re-run.

(A synced list with Active Directory provable still ends right - the cloud add is refused as
on-prem-mastered and the on-prem add's own `Get-ADGroupMember` finds the member - so that shape only
does extra work.)

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21); codex's
better approach, verbatim: "add `ResultSize Unlimited` to both `Get-DistributionGroupMember` calls in
the room-list preflight/add path, and make the harness fail or truncate when the parameter is absent
so the already-member tests do not pass for the wrong reason."

**Rule.** A membership read that decides whether a room is already on its list reads the whole list.

- **Both reads** pass `.AddParameter("ResultSize", "Unlimited")` between `Identity` and `ErrorAction`
  - the protected-principal idiom above, verbatim. Nothing else changes: `-ErrorAction
  SilentlyContinue` stays (a failed read still reads as "not a member" - the stricter path in the
  preflight, the old path in the add), and `IsListMember` stays the one membership test.
- **The harness stand-in** declares `-ResultSize` and answers as Exchange would, except that a read
  without the parameter is **refused** - an error and no members - instead of answered short, so no
  test can find a member through a read the real cmdlet would have cut at 1000: a number returns that
  many, "Unlimited" all of them. The refusal is `Write-Error` then `return`, not `throw`: a `throw`
  inside an advanced function called with `-ErrorAction SilentlyContinue` - how both reads are called -
  is suppressed and the function carries on (probed in pwsh 7.6.6: the statement after the `throw`
  ran), so a refusal that relies on `throw` to stop the function does not stop it under the very
  ErrorAction these reads use.
- **Tests** (`ConferenceRoomRoomListPreflightTests`):
  - a synced list one member longer than Exchange's default page, the room last, no AD credential,
    typed by another string: succeeds, writes exactly Set-Place and the timezone pair, asks for no
    credential, and both reads - the preflight's and the add's - bound `ResultSize` "Unlimited";
  - a cloud list of the same size, the room last, Exchange refusing a duplicate add: succeeds, not
    partial, no `Add-DistributionGroupMember`, its one read bound "Unlimited";
  - the harness itself: a read without `-ResultSize` is refused under `-ErrorAction SilentlyContinue`
    (no members) and `Stop` (throws); 1000 returns 1000; "Unlimited" returns the whole list.
  - The existing already-member tests (`ASyncedRoom_AlreadyOnItsSyncedRoomList_NeedsNothingOnPrem`,
    `ConferenceRoomWriteIdentityTests.ARoomAlreadyOnTheList_ByItsValidatedAddress_IsNotAddedAgain`)
    now depend on the parameter too - the refusal is for every list size.
- **Mutations, at the call sites:** the parameter dropped from the add's read; from the preflight's;
  "Unlimited" replaced by 1000 at each; in the harness, the refusal removed and the numeric page
  ignored. A named test fails for each.
- **Audit** every other `Get-DistributionGroupMember` / `Get-*Member` read in this service and the
  Comms-10k resolver for the same gap: fix in this service, report the rest as candidates.

**Files.** `Services/ConferenceRoomService.cs`, `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`,
`ExchangeAdminWeb.Tests/ConferenceRoomRoomListPreflightTests.cs`, this record. No page change.

## Approach (as implemented)

- `AddToRoomListAsync` (`Services/ConferenceRoomService.cs:643-646`) and `IsOnRoomListAsync`
  (`:749-752`) each gained `.AddParameter("ResultSize", "Unlimited")` between `Identity` and
  `ErrorAction`, the protected-principal idiom verbatim. Nothing else in the service changed but two
  comments.
- `ExchangeWriteHarness`: the stand-in `Get-DistributionGroupMember` (`:425-440`) declares
  `-ResultSize`; without it, `Write-Error` and `return` - no members; a number, the first N of
  `ListMembers`; "Unlimited", all. `ExchangeDefaultResultSize` (1000) sizes the test lists.
  `OpenExchangeRunspace()` opens the Exchange stand-ins, which the pool's connect seam now uses too,
  so a test can drive a stand-in directly. The `Failures` doc now says it cannot fail a stand-in
  called with `-ErrorAction SilentlyContinue` (the same suppressed `throw`; probed for the nested
  `Write-StandInCall` throw as well). Every current `Failures` entry targets a write called with
  `Stop`, so no existing test was relying on it.
- `ConferenceRoomRoomListPreflightTests`: the three tests of the plan, `FillListPastTheDefaultPage`
  (1000 fillers, then the room) and `AssertEveryMembershipReadAskedForTheWholeList` (the count of
  reads, each bound "Unlimited").

## Guard proof

| Test | What it pins |
| --- | --- |
| `ConferenceRoomRoomListPreflightTests.ASyncedRoom_PastTheDefaultPageOfItsSyncedRoomList_IsSeenByThePreflightAndTheAdd` | codex's synced scenario: a synced list of 1001, the room last, no AD credential, typed by another string, Exchange refusing the cloud add - succeeds, writes exactly Set-Place and the timezone pair, asks for no credential, and both reads (preflight, add) bound `ResultSize` "Unlimited" |
| `ARoom_PastTheDefaultPageOfItsCloudRoomList_IsNotAddedAgain` | codex's cloud scenario: a cloud list of 1001, the room last, Exchange refusing a duplicate add - succeeds, not partial, no `Add-DistributionGroupMember`, the "Already in" step, its one read bound "Unlimited" |
| `TheHarness_RefusesAMembershipReadThatDoesNotAskForTheWholeList` | the stand-in: no `-ResultSize` - no members under SilentlyContinue, no members and one error naming `-ResultSize` under Continue, a throw under Stop; 1000 returns 1000; "Unlimited" all 1001, the room last |
| `ASyncedRoom_AlreadyOnItsSyncedRoomList_NeedsNothingOnPrem` (aud-a2f repair; unchanged) | now also needs the preflight's read to pass the parameter - the refusal applies at every list size |
| `ConferenceRoomWriteIdentityTests.ARoomAlreadyOnTheList_ByItsValidatedAddress_IsNotAddedAgain` (unchanged) | now also needs the add's read to pass the parameter |

**Revert the fix.** `Services/ConferenceRoomService.cs` replaced whole by HEAD's
(`104e8b5b60546277c57d77aed26e681a`, the file the aud-a2f repair committed), the harness and tests
as fixed: 4 of the 5 named tests FAIL, each with codex's predicted outcome - the cloud test partial
("The recipient ... is already a member of the group ..."), the synced test refused before any write
("On-prem AD credential is not configured ..."), the repair test refused the same way, the identity
test sending an add. The harness test passes (it does not touch the service). Restored to
`a8456d8b0505f0c9d20ee7dac2c8fda8`, touched.

**Mutations** (`scratchpad/a2h/round_a2h.ps1` in the session scratchpad, log `round.log` beside
it; each edit applied only if its old text occurs exactly once and the md5 changes; test project
rebuilt; the named tests run with a TRX logger; the file restored from the saved bytes and touched;
md5 re-checked). M rows: `Services/ConferenceRoomService.cs`, md5 before = restored =
`a8456d8b0505f0c9d20ee7dac2c8fda8`. H rows: `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`, md5
before = restored = `3bd7bb4e4714190a9742978d9a3cc699`. Test names are in
`ConferenceRoomRoomListPreflightTests` unless qualified.

| # | Mutation | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- |
| M1 | the add's read without `-ResultSize` | `7c9601dfcfa2a5b7a041576516f1a6d0` | `ARoom_PastTheDefaultPageOfItsCloudRoomList_IsNotAddedAgain` | FAIL (partial, "already a member"); also FAIL: the synced test (partial - the add fell back on-prem with no credential) and `ConferenceRoomWriteIdentityTests.ARoomAlreadyOnTheList_ByItsValidatedAddress_IsNotAddedAgain` |
| M2 | the preflight's read without `-ResultSize` | `708025fb17f74d613988ecfd2f83281a` | `ASyncedRoom_PastTheDefaultPageOfItsSyncedRoomList_IsSeenByThePreflightAndTheAdd` | FAIL (refused before any write); also FAIL: `ASyncedRoom_AlreadyOnItsSyncedRoomList_NeedsNothingOnPrem` |
| M3 | the add's read capped at 1000 ("Unlimited" -> 1000) | `bd884452d30985dbf3883dc3e14e189c` | `ARoom_PastTheDefaultPageOfItsCloudRoomList_IsNotAddedAgain` | FAIL; the synced test also FAIL |
| M4 | the preflight's read capped at 1000 | `5ef5ec501aeb7ff9ab2012d370ca74f0` | `ASyncedRoom_PastTheDefaultPageOfItsSyncedRoomList_IsSeenByThePreflightAndTheAdd` | FAIL |
| H1 | the stand-in answers a read without `-ResultSize` in full (the old stand-in) | `5326f17d751514d7f02040684718657f` | `TheHarness_RefusesAMembershipReadThatDoesNotAskForTheWholeList` | FAIL |
| H2 | the stand-in ignores a numeric `-ResultSize` | `51089ea7de45f8114abd32e70eea9b7d` | same | FAIL |
| H1+M1 | *demonstration, not a guard:* the old stand-in AND the add's read without the parameter (harness `5326f17d...`, service `7c9601df...`) | - | `ConferenceRoomWriteIdentityTests.ARoomAlreadyOnTheList_ByItsValidatedAddress_IsNotAddedAgain` | PASSES - the wrong-reason pass the refusal exists to stop. The cloud test still FAILS, but only on its `ResultSize` pin (`Assert.All`); its behaviour assertions pass. The harness test FAILS. |

Every restored md5 equal to "before"; final rebuild and the five named tests green.

**Comments after the round.** The round ran on the fix without its comments; the comments were
written afterwards, a comment-only diff (`scratchpad/a2h/comment_only.ps1`: every changed line in
`git diff --no-index --unified=0` between the round copy and the file is blank or a comment):
service `a8456d8b0505f0c9d20ee7dac2c8fda8` -> `cd1eeb1a7632a75c2f14e7c214ac975f` (7 changed lines,
0 code), harness `3bd7bb4e4714190a9742978d9a3cc699` -> `ba48316712050073621a7eb34aca948d` (20, 0
code), tests unchanged (`46ac77c9d7b575b68598d5cd84c4c228`). The gates below ran on the final files.

## Other membership reads audited

| Read | Where | This gap? | State |
| --- | --- | --- | --- |
| `Get-DistributionGroupMember`, the add's already-member test | `ConferenceRoomService.cs:643-646` | yes | fixed |
| `Get-DistributionGroupMember`, the preflight's | `ConferenceRoomService.cs:749-752` | yes | fixed |
| `Get-ADGroupMember -Identity <group objectGUID>`, the on-prem add's idempotency check | `ConferenceRoomService.cs:1666-1669` | no - the AD cmdlet has no result-size parameter and no default page; it returns every direct member or fails, and with `-ErrorAction Stop` a failure is surfaced, never read as "not a member" | **candidate, different shape, not fixed** (below) |
| `Get-DistributionGroupMember`, protected-principal expansion | `PermissionValidator.cs:778-781` | no - the idiom; already "Unlimited" | none |
| Comms-10k: `Comms10kMemberResolver` | `Services/Comms10kMemberResolver.cs` | no - pure (batching, filters, rows, RID); it issues no read | none |
| Comms-10k reads behind it: `Get-ADGroup -Properties member,SID` (`Comms10kService.cs:115`, read-back `:648`); `Get-ADObject -LDAPFilter (primaryGroupID=...)` (`:145`, `:667`); `Get-ADObject` DN and address batches (`:201`, `:343`, `:687`) | `Services/Comms10kService.cs` | no - neither cmdlet of this finding is called; no result cap is passed anywhere (deliberately, `:302-306`); an AD search without `-ResultSetSize` returns every match, and the linked `member` attribute comes back by range retrieval, uncapped (the module's own record, `:111-113`; not re-probed) | none |

**Candidate (not admitted, not fixed): the on-prem add's `Get-ADGroupMember`.** It is the last
`Get-ADGroupMember` read in the code; every other one was replaced because the cmdlet is bound by
the ADWS `MaxGroupOrMemberEntries` cap (default 5,000) and faults on a member it cannot resolve in
another domain (`Comms10kService.cs:39-42`, `ModuleCatalog.cs:590` and `:677-681`,
`GroupMemberListingTests`). Here either would fail `AddToRoomListViaAdAsync` after Set-Place, the
timezone and the refused cloud add - partial, loud, on every re-run - and the preflight does not read
it. Shape: the group's `member` attribute or the room object's `memberOf`, as elsewhere in the repo.
Suggested severity LOW: no wrong object is written, and a room list that large, or with a member
the read cannot resolve, is unusual.

## Known gaps

- *Assumption, documented behaviour, not probed live:* Exchange Online's `Get-DistributionGroupMember`
  returns 1000 members by default and accepts the string "Unlimited" - the value the protected-principal
  expansion already sends (`PermissionValidator.cs:780`).
- Unchanged and out of scope: both reads keep `-ErrorAction SilentlyContinue`, so a read that fails
  reads as "not a member" - in the preflight the stricter path (the on-prem proof is demanded), in the
  add the add is sent and, for a member, refused: partial, as before. A whole-list read is larger and
  slower, so on a very large list a failed read is somewhat likelier; it surfaces the same way.
- Cost: a synced list is now read whole twice (preflight, add), a cloud list once. Correctness was
  the brief.
- The harness refusal covers only `Get-DistributionGroupMember`; no other stand-in returns a list a
  real cmdlet would page.

**Operator-visible changes.** For a room already on a room list longer than 1000 members, past
Exchange's first 1000: on a synced list with Active Directory unprovable the row is written (Set-Place,
the timezone) instead of refused; on a cloud list it succeeds instead of reporting partial. Nothing
else.

**Dev acceptance:** not run, and only a room list of more than 1000 members can show the change; the
unit proof above is the evidence. aud-a2f's dev acceptance stands.

**Page:** unchanged; no `ClickGateRegistry` entry moved.

## Reviewer comments

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent on
`worktree-agent-a24bdc2542a9c48b7`. Independent review pending.

## Closeout

Fixed on branch, not merged, not pushed. Module-scoped behaviour change: the Conference Rooms module
version bump is due at merge; the harness change is test-only and no shared code changed, so no base
app bump from this finding.

Gates, on the final files: `dotnet build ExchangeAdminWeb.slnx -c Release` 0 errors, 61 warnings,
none in a touched file; `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`
4979 passed, 0 failed, 3 skipped (the known AD skips), 4982 total, nothing hung; `dotnet format
ExchangeAdminWeb.slnx --verify-no-changes --no-restore` clean; `git diff --check HEAD` clean;
`tools/Test-AsciiOnly.ps1` clean after `git add`. PSScriptAnalyzer and Pester not run: no `.ps1` or
`.psm1` changed.
