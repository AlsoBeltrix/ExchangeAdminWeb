# aud-a2u: Conference Rooms - the directory-object refusals and the pre-directory refusals are not logged

**Severity**: LOW - observability only; no wrong write. (1) `ConfirmDirectoryObject`'s refusals (no
object, or another object, answering for the proven objectGUID) are returned and logged nowhere on
all four directory paths. (2) The refusals returned before the directory work starts (an unmappable
country, no list GUID, no sync state reported, no proven directory object) are not logged by the
service either. Only the row's audit carries them, so the application log shows no trace of why a
row was refused (the aud-a2t class, on the remaining refusal sites).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; plan written, in progress
**Branch**: `worktree-agent-addb7857655adf8dc`
**Commit**: -
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

## Guard proof

Pending.
