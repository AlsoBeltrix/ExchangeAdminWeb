# aud-a2u: Conference Rooms - the directory-object refusals and the pre-directory refusals are not logged

**Severity**: LOW - observability only; no wrong write. (1) `ConfirmDirectoryObject`'s refusals (no
object, or another object, answering for the proven objectGUID) are returned and logged nowhere on
all four directory paths. (2) The refusals returned before the directory work starts (an unmappable
country, no list GUID, no sync state reported, no proven directory object) are not logged by the
service either. Only the row's audit carries them, so the application log shows no trace of why a
row was refused (the aud-a2t class, on the remaining refusal sites).
**Status**: Merged to master (`bf7ba5b4`); closed on guard proof; codex cr-batch-1 clean
**Branch**: `worktree-agent-addb7857655adf8dc`
**Commit**: `351f887b` (fix, tests, comments, record); plan `8233d1ff`; merged `bf7ba5b4`
**Reviewer**: implementer (surfaced by the aud-a2t agent, 2026-10-22, its two candidates)

## Evidence

At `932fd294`, `Services/ConferenceRoomService.cs`: the four callers of `ConfirmDirectoryObject`
turning its refusal into the row's; the early refusals in the row pipeline (`RunAsync` and the
preflights) before any runspace is opened.

## Approach (to be written by the implementing agent before code)

- Log each at Warning once, at the call site where the path turns the reason into the row's refusal
  (the aud-a2t placement), naming the room, the list where there is one, and the reason; row
  outcomes, messages, audit and notifications unchanged. Tests through `ExchangeWriteHarness` with
  the capturing logger: each refusal logs exactly one warning. Mutation at the call site: drop one
  log line per group.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("ALL the bugs includes ALL the bugs ... EVERYTHING", `.agents/decisions.md`
2026-10-21); the admitted approach above and the supervising session's brief. One finding, so one fix commit for its two
parts. Base `af01cd37`; the line numbers below are that tree's.

**Rule.** A refusal a Conference Rooms directory path returns without throwing - the room-object read's, and those the
path decides before its runspace - reaches the application log once, at Warning, naming the room, the list where there is
one, and the reason, at the call site where the path turns it into its refusal (aud-a2t's placement). Then it is returned
exactly as now.

1. **The room-object read's refusals - its four callers.** `ConfirmDirectoryObject` (`Services/ConferenceRoomService.cs`
   2130-2151) returns three: the read's error record (2138-2139), "could not be read (<n> found)" (2142-2143) and
   "answered <guid> with a different object" (2145-2147). Each caller logs whatever it returns, then returns as now:
   - `PreflightSyncedAttributesViaAdAsync` (1590-1594): `"Set-ADUser preflight for {Room} refused the row; nothing was
     written: {Refusal}"` - `roomEmail`, the refusal as returned (before " Nothing was written." is appended).
   - `SetSyncedAttributesViaAdAsync` (1682-1684): `"Set-ADUser City/State/Country write for {Room} refused before its
     write: {Refusal}"`. "Before its write", not "nothing was written": this runs after Set-Place, inside the row's
     partial (aud-a2r), as aud-a2t's add line says.
   - `PreflightRoomListAsync` (1851-1854): aud-a2t's preflight line, `"On-prem room-list preflight for {Room} on {List}
     refused the row; nothing was written: {Refusal}"`, `list.Name`.
   - `AddToRoomListViaAdAsync` (1943-1947): aud-a2t's add line, `"On-prem room-list add for {Room} on {List} refused
     before its write: {Refusal}"`, `roomListName`.
   - Not inside `ConfirmDirectoryObject`: a static reader shared by the four, it cannot say whether anything has been
     written - the reason aud-a2r gave its "Nothing was written." to the callers - the shape of the resolver and the
     membership read, whose refusals their callers log (aud-a2t). One line per call site covers all three of its
     refusals, the two the stand-ins cannot produce among them (an error record: they throw under `-ErrorAction Stop`; no
     object without a throw: the stand-in, like the real cmdlet, throws its not-found, which the step catch logs).
2. **The refusals decided before the directory work - where each path decides them.**
   - `PreflightSyncedAttributesViaAdAsync`: the unmappable country (`BuildSyncedUserAttributes`' `ArgumentException`,
     1556-1559) and no proven directory object (1564-1565) - the City/State/Country preflight's line above.
   - `PreflightRoomListAsync`: no list GUID (1804-1805) and no sync state reported (1807-1808) - `"Room-list preflight
     for {Room} on {List} refused the row; nothing was written: {Refusal}"`, not "On-prem": both refuse an existing list
     of either kind, before its sync state is known - and no proven directory object for a synced list (1820-1821),
     aud-a2t's on-prem preflight line. Each of the three refusals is built without its " Nothing was written.", logged,
     and returned with it appended: the line carries the refusal as aud-a2t's do, and the returned message is unchanged
     to the byte.
   - `AddToRoomListViaAdAsync`: no proven directory object (1912-1913) - aud-a2t's add line. Through the row only by a
     race (the preflight's membership read found the room on its synced list and the add's does not), so a test reaches
     it by calling the add directly (aud-a2p's seam).
   - Not logged here, and why:
     - the City/State/Country write's own unmappable country (1641-1644) and no proven object (1649-1650): unreachable.
       Its one caller (`SetRoomMetadataAndListAsync` 573) runs it only after the preflight passed (510-517) on the same
       `room` - a `ValidatedMailbox`, immutable - and the same fields, and `BuildSyncedUserAttributes` is pure; so the
       preflight has refused, and now logged, every row these could refuse. A line there is one no row and no test can
       reach.
     - a missing credential, on all four paths (1567-1569, 1652-1654, 1823-1825, 1915-1917): logged by the credential
       service - `ModuleCredentialService.GetCredentialsAsync` (19-35: corrupt config or no secret ID, at Error) and
       `DelineaService.GetCredentialsBySecretIdAsync`, whose every null return is logged. aud-a2t's note, verified.
     - a list the row creates coming back without a Guid (`AddToRoomListAsync` 714-715): a throw inside `RunAsync`,
       logged at Error by its catch (`ExchangeServiceBase.RunAsync` 96).
   - Unchanged: every row's outcome, message, steps, audit and notification; the paths' callers
     (`SetRoomMetadataAndListAsync`, `AddToRoomListAsync`), which put the refusal on the result and log nothing; the
     helpers and their refusals; every existing log line.

**Tests** (`ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs`, a new section at its end; through
`ExchangeWriteHarness` with the file's `CapturingLogger` (366-373); no directory, Exchange or network call; no harness
change).
- `TheObjectReadsRefusal_IsLoggedOnce_WhereThePathRefuses` (City/State/Country preflight and write, room list preflight
  and add), the whole row: the read that proves the room's object answered by another object - `ObjectReadAnswersAs`
  before Set-Place, `ObjectReadAnswersAsAfterSetPlace` after - set up as
  `TheObjectReadsRefusal_SaysNothingWasWritten_OnlyWhereNothingWas` (800-839) sets it up; the outcome as there; exactly
  one log entry carries "Active Directory answered <guid> with a different object (<guid>).", a Warning, equal to that
  path's line.
- `ARefusalBeforeTheDirectoryWork_IsLoggedOnce_NamingTheRoomTheListAndTheReason` (an unmappable country, "ZZ"; a
  cloud-only room with City/State/Country; a list Exchange reported with no GUID, `ListGuids`; a list whose sync state it
  did not report, `ListSyncState`; a cloud-only room on a synced list), the whole row: refused, not partial, nothing
  written, its one step; the refusal in words of its own; exactly one entry carries it - the message, less " Nothing was
  written." where the path appends it - a Warning, equal to the path's line.
- `TheOnPremAddsRefusalOfARoomWithNoDirectoryObject_IsLoggedOnce`: the add called directly (aud-a2p's seam), a cloud-only
  room, a synced list whose cloud add Exchange refuses as on-prem-mastered: failed, the step `Add to '<list>' (on-prem)`
  carrying the refusal, no `Set-ADGroup`, no credential asked for; exactly one entry carries it, a Warning, equal to the
  add's line.
- 10 new cases (4 + 5 + 1). No existing test or assertion changed.

**Mutations, at the call sites** (md5 before and after, the named failing tests, restored, md5 matched, touched,
rebuilt, green): M1a-d the brief's (1) - each of the four room-object-read refusal lines dropped in turn; M2a-f the
brief's (2) - each of the six pre-directory refusal lines dropped in turn; M3a-c a caller logging a refusal a second time
(`SetRoomMetadataAndListAsync` for the City/State/Country preflight's and for the write's, `AddToRoomListAsync` for the
on-prem add's); REV the service whole as at `af01cd37`, the new tests kept (they name nothing the fix adds). Comments
after the round, checked code-identical by a comment-only diff.

**Files.** `Services/ConferenceRoomService.cs`, `ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs`, this record.

## Approach (as implemented)

- `Services/ConferenceRoomService.cs`, ten lines, each `_logger.LogWarning(<the path's line>, roomEmail[, the list], <the
  refusal>)` just before the refusal's return, which is unchanged:
  - `PreflightSyncedAttributesViaAdAsync` (1549): the unmappable country, in the `ArgumentException` catch (1562); no
    proven object (1572, the refusal now built in a local first); the object read's refusal (1605) - `"Set-ADUser
    preflight for {Room} refused the row; nothing was written: {Refusal}"`.
  - `SetSyncedAttributesViaAdAsync` (1647): the object read's refusal (1702) - `"Set-ADUser City/State/Country write for
    {Room} refused before its write: {Refusal}"`. Its own unmappable-country and no-object refusals stay unlogged; a
    comment at its head says why (nothing reaches them).
  - `PreflightRoomListAsync` (1816): no list GUID (1831) and no sync state reported (1838) - `"Room-list preflight for
    {Room} on {List} refused the row; nothing was written: {Refusal}"`; no proven object for a synced list (1855) and the
    object read's refusal (1892) - aud-a2t's `"On-prem room-list preflight for {Room} on {List} refused the row; nothing
    was written: {Refusal}"`. The three pre-directory refusals are built without their " Nothing was written.", logged,
    and returned with it appended: each message the same to the byte.
  - `AddToRoomListViaAdAsync` (1950): no proven object (1958) and the object read's refusal (1995) - aud-a2t's `"On-prem
    room-list add for {Room} on {List} refused before its write: {Refusal}"`.
  - `ConfirmDirectoryObject`'s header says it logs nothing and who does. The helpers, their refusals, the paths' callers
    and every existing line are unchanged.
- `ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs` (1273-1426), no harness change: `RefusalLine(path,
  refusal)`, each path's line for the room as typed; `AssertLoggedOnce`, exactly one entry carrying the refusal, a Warning,
  equal to that line; `TheObjectReadsRefusal_IsLoggedOnce_WhereThePathRefuses` (4),
  `ARefusalBeforeTheDirectoryWork_IsLoggedOnce_NamingTheRoomTheListAndTheReason` (5),
  `TheOnPremAddsRefusalOfARoomWithNoDirectoryObject_IsLoggedOnce` (1).
- 10 new cases. No existing test or assertion changed; on the fix the 340 `ConferenceRoom*` cases pass, the 330 existing
  among them.

## Guard proof

| Test | What it pins |
| --- | --- |
| `TheObjectReadsRefusal_IsLoggedOnce_WhereThePathRefuses` (City/State/Country, preflight) | part 1 on the City/State/Country preflight: the object read answered by another object before Set-Place - refused, not partial, nothing written, the one failed step carrying "Active Directory answered <guid> with a different object (<guid>)."; exactly one log entry carries it, a Warning, equal to "Set-ADUser preflight for <room> refused the row; nothing was written: <refusal>" |
| the same (City/State/Country, write) | part 1 on the write: answered so after Set-Place - partial, no `Set-ADUser` made; one entry, "Set-ADUser City/State/Country write for <room> refused before its write: <refusal>" |
| the same (room list, preflight) | part 1 on the room-list preflight: refused, nothing written; one entry, "On-prem room-list preflight for <room> on <list> refused the row; nothing was written: <refusal>" |
| the same (room list, add) | part 1 on the on-prem add: partial, no `Set-ADGroup` made; one entry, "On-prem room-list add for <room> on <list> refused before its write: <refusal>" |
| `ARefusalBeforeTheDirectoryWork_IsLoggedOnce_NamingTheRoomTheListAndTheReason` (an unmappable country; no AD object for City/State/Country) | part 2 on the City/State/Country preflight, through the whole row: refused, not partial, nothing written, no credential asked for, the one step `Set-ADUser preflight (City/State/Country)` with the message as its `Error`, in the refusal's own words; one entry carries the message, a Warning, the preflight's line |
| the same (a list with no GUID; a list with no sync state) | part 2 on the room-list preflight: as above, the one step `Room list preflight`, the message ending " Nothing was written."; one entry carries the refusal before it, a Warning, "Room-list preflight for <room> on <list> refused the row; nothing was written: <refusal>" |
| the same (no AD object for a synced list) | the same, "On-prem room-list preflight for <room> on <list> refused the row; nothing was written: <refusal>" |
| `TheOnPremAddsRefusalOfARoomWithNoDirectoryObject_IsLoggedOnce` | part 2 on the on-prem add, called directly: failed, the step `Add to '<list>' (on-prem)` and the message "No on-premises Active Directory object was validated for '<room>' (a cloud-only room has none), so the room could not be added to the on-prem room list.", no `Set-ADGroup`, no credential asked for; one entry, the add's line |

**Mutations** (`scratchpad/a2u-round/round.ps1` - the a2t runner, its root pointed at this worktree - with `rows-a2u.ps1`;
log `round-a2u.log` beside them; 2026-10-22 16:37-16:46). Each row ran the 340 `ConferenceRoom*` cases, the test project
rebuilt each time, the failures read from its TRX. md5 before = restored: `Services/ConferenceRoomService.cs`
`707f4262d3002502d408680c9b4af1ab` (every row); the service was touched once mutated and once restored, so each build
compiled the file as it then was. Test names are in `ConferenceRoomWriteIdentityTests` unless qualified (`Preflight.` is
`ConferenceRoomRoomListPreflightTests`).

| # | Mutation | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- |
| M1a | the brief's (1), at the call site: the City/State/Country preflight's line for the object read's refusal dropped | `c55bbdde7de82cfb8fa5c2ba160474a1` | `TheObjectReadsRefusal_IsLoggedOnce_WhereThePathRefuses` (City/State/Country, preflight) | FAIL, 1 of 340: no entry carries the refusal |
| M1b | the same, the City/State/Country write's line | `326f30e9c2725eb7e4ee574e62258220` | the same (City/State/Country, write) | FAIL, 1 |
| M1c | the same, the room-list preflight's line | `d6d74625a89d7cde52c8a9a69fd25ffd` | the same (room list, preflight) | FAIL, 1 |
| M1d | the same, the on-prem add's line | `1d8b80a88f78e5e8240a35a7ebae1ebb` | the same (room list, add) | FAIL, 1: the only entry the cloud add's own Error ("Exchange operation failed: ..."), which carries no refusal |
| M2a | the brief's (2), at the call site: the City/State/Country preflight's line for the unmappable country dropped | `39adfa1949800cd0b0ef4a1331d9d464` | `ARefusalBeforeTheDirectoryWork_IsLoggedOnce_NamingTheRoomTheListAndTheReason` (an unmappable country) | FAIL, 1 |
| M2b | the same, its line for no proven object | `ff5faf467d72bf48b9c87e6be5ec4a03` | the same (no AD object for City/State/Country) | FAIL, 1 |
| M2c | the same, the room-list preflight's line for no list GUID | `55d5c1ec5821b6083bb30b6814d95131` | the same (a list with no GUID) | FAIL, 1 |
| M2d | the same, its line for no sync state reported | `1b5694d3ab2360aa5526edf9a2d14213` | the same (a list with no sync state) | FAIL, 1 |
| M2e | the same, its line for no proven object on a synced list | `39afa6e2a5dc8025bdb10037fd202b8b` | the same (no AD object for a synced list) | FAIL, 1 |
| M2f | the same, the on-prem add's line for no proven object | `4937085bac7538d9db89b45c9058d268` | `TheOnPremAddsRefusalOfARoomWithNoDirectoryObject_IsLoggedOnce` | FAIL, 1 |

Beyond the brief's two:

| # | Mutation | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- |
| M3a | the row logs the City/State/Country preflight's refusal a second time (a warning in `SetRoomMetadataAndListAsync`'s preflight refusal) | `bfa114f55adcc7f05abf7e30b926ab66` | `ARefusalBeforeTheDirectoryWork_...` (an unmappable country; no AD object for City/State/Country); `TheObjectReadsRefusal_...` (City/State/Country, preflight) | FAIL, 3: two entries carry the refusal |
| M3b | the row logs the City/State/Country write's refusal a second time (a warning in its partial) | `6d3b8418f70eb758d676395dc355870d` | `TheObjectReadsRefusal_...` (City/State/Country, write) | FAIL, 1 |
| M3c | the add's caller logs the on-prem add's refusal a second time (a warning in `AddToRoomListAsync`'s on-prem failure) | `896620005474cfc0a14eb60f0570288b` | `TheObjectReadsRefusal_...` (room list, add); `TheOnPremAddsRefusalOfARoomWithNoDirectoryObject_IsLoggedOnce`; `Preflight.ARefusalOfTheOnPremRoomListAdd_IsLoggedOnce_NamingTheRoomTheListAndTheReason` (all eight); `Preflight.ARefusalOfTheOnPremRoomListAdd_InTheWholeRow_IsLoggedOnce` | FAIL, 11: two entries carry the refusal |
| REV | the fix reverted: the service whole as at `af01cd37`, the new tests kept (they name nothing the fix adds) | `f6b0323d2c504c5d2b5651c7a3b4f80b` | the ten new cases | FAIL, 10 of 340: no entry carries the refusal |

Every restored md5 equal to "before"; after the last row the restored tree rebuilt and the 340 `ConferenceRoom*` cases green.

**Comments after the round.** The round ran on the fix with none of the service's new explanatory comments (the test
file's were written with the tests). They were written afterwards and checked by `scratchpad/a2u-round/comment_only.ps1`
(aud-a2q's checker: blank, `//`, `///` and PowerShell `#` lines dropped from both versions, the code lines identical in
order): service `707f4262d3002502d408680c9b4af1ab` -> `4a83690ac382f682e9c2eaf10e91cd60` (code lines 1816 = 1816; comment
and blank lines 974 -> 994). Unchanged since the round: `ConferenceRoomWriteIdentityTests.cs`
`85fdacc50f8a4b5a0f27b612e08d6a85`.

## Known gaps

- Two of the room-object read's three refusals are reached by no test: its error record and its empty answer ("could not
  be read (0 found)"). The stand-ins throw their errors under `-ErrorAction Stop` (as a cmdlet's own error is thrown under
  it, aud-a2o, measured), and the stand-in `Get-ADUser -Identity` throws its not-found, as the real cmdlet does (*its
  documented behaviour, not probed*) - a throw the step catch logs at Error. The line at each call site logs whatever the
  reader returns, so the "another object" cases pin the line those take too.
- The on-prem add's no-object refusal is met through the row only by a race - the room on its synced list as Exchange
  shows it at the preflight, gone by the add - so its case calls the add directly (aud-a2p's seam).
- The City/State/Country write's own unmappable-country and no-object refusals stay unlogged: nothing reaches them (the
  plan, part 2). Were the write ever called without the preflight they would be returned unlogged, as before; the comment
  at its head says so.
- A missing credential is logged by the credential services alone, and names the room only for a missing secret ID
  (`ModuleCredentialService`'s line carries the purpose, which names it): its corrupt-config line names the module, and
  `DelineaService`'s lines for a failed or incomplete secret name the secret - not the room or the row. Unchanged,
  outside this finding's list.
- The unmappable country's refusal is `ArgumentException.Message`, which carries .NET's " (Parameter 'countryOrRegion')"
  suffix - in the row's message, as before, and so in the line. Unchanged.
- Not changed, outside this record: `SetRoomMetadataAndListAsync`'s own early refusals - no validated room, an invalid
  timezone, a recipient that is not a room mailbox - are returned and logged nowhere by the service. Reported in the
  hand-back.

**Operator-visible changes.**

- A Room Finder row refused before any write - an unmappable country, a cloud-only room with City/State/Country, a room
  list Exchange reported with no GUID or without its sync state, a cloud-only room on a synced list, or the room's AD
  object answering as another object on either preflight - leaves one warning in the application log naming the room as
  typed, the list where there is one, and the refusal: "Set-ADUser preflight for <room> refused the row; nothing was
  written: <refusal>", "Room-list preflight for <room> on <list> refused the row; nothing was written: <refusal>", or
  aud-a2t's on-prem preflight line. The object answering as another object after Set-Place leaves "Set-ADUser
  City/State/Country write for <room> refused before its write: <refusal>" or aud-a2t's add line; so does the add's own
  no-object refusal. Before, only the row's result and its audit carried them.
- Nothing else: no outcome, message, write, audit or notification changes.

**Dev acceptance:** nothing new beyond the Room Finder check owed with aud-a2f..a2t: a clean row logs no refusal, and the
new lines follow refusals, which are not to be induced on dev. Not run here: no directory, Exchange or SMTP call was made,
and the owner's standing instruction is not to probe Active Directory.

## Reviewer comments

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent on `worktree-agent-addb7857655adf8dc`.
Independent review pending.

For the reviewer:

- One fix commit for the finding's two parts.
- Placement: aud-a2t's - the call site where the path turns the reason into its refusal - with the paths' callers
  (`SetRoomMetadataAndListAsync`, `AddToRoomListAsync`) logging nothing. Not inside `ConfirmDirectoryObject`: a static
  reader shared by four paths, it cannot say whether anything has been written. M3a-c pin the "once".
- Two lines in the room-list preflight: its no-GUID and no-sync-state refusals say "Room-list preflight", not aud-a2t's
  "On-prem room-list preflight" - they refuse a cloud list too, before the sync state is known; the no-object refusal,
  on a synced list only, takes aud-a2t's line.
- Not logged, deliberately: the City/State/Country write's own unmappable-country and no-object refusals (unreachable
  behind the preflight - the plan, part 2), a missing credential (the credential services log it), a list the row creates
  coming back without a Guid (`RunAsync` logs it).
- The room-list preflight's three pre-directory refusals are built in a local without their " Nothing was written." and
  returned with it appended, so the logged refusal has aud-a2t's shape; each returned message is the old literal to the
  byte, by construction, and the new cases pin each one's words.
- Tests in `ConferenceRoomWriteIdentityTests`: its `Finder` takes a country, and its
  `TheObjectReadsRefusal_SaysNothingWasWritten_OnlyWhereNothingWas` already drives all four paths. Its room is typed as an
  alias of another room ("boardroom"), so the lines are seen to name the room as the operator typed it, as every line in
  the service does.
- Beyond the brief's two mutation rows: each of the ten lines (M1a-d, M2a-f), the "once" (M3a-c) and a whole-file revert
  (REV).
- Outside the service and its tests: nothing.

## Closeout

Fixed on branch, not merged, not pushed: the plan `8233d1ff`, then this fix, tests, comments and record in one commit. A
module-scoped behaviour change (Conference Rooms: ten warning log lines): the Conference Rooms module version bump is due at
merge - not made here, `Modules/ModuleCatalog.cs` being outside this brief. No shared code changed, so no base app bump.

Gates, run on the final files this commit holds (the mutation round ran before the comments, which the comment-only check
shows changed no code line):

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings - the baseline count - none in a
  touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: 5581 passed, 0 failed, 3 skipped (the known AD
  skips, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`), 5584 total, 5 m 3 s; nothing hung, no Sequence
  file. This record's new cases are 10; the 316 `ClickGate*` cases are among those passed (and, run alone, 316 passed).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD`: clean.
- `tools/Test-AsciiOnly.ps1` after `git add -A`: clean.

PSScriptAnalyzer and Pester were not run: no `.ps1` or `.psm1` changed (the scratch runner and checker are not in the
repository).

Base `af01cd37`, which both remotes hold. The local master has since moved to `cef232a1` - five commits of the Migration
work (aud-a23g, and the admission of aud-a23h and aud-a23i) - touching none of this record's files; the totals above are
this branch's.
