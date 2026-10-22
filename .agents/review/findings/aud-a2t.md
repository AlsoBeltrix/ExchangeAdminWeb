# aud-a2t: Conference Rooms - the credential step has no test, and the room-list resolver's refusals are not logged

**Severity**: LOW - a test gap and an observability gap; no wrong write. (1) On the City/State/Country
paths the step "preparing the module's AD credential" is named (aud-a2q/a2r) but no test reaches it;
the room-list paths reach theirs only through a test seam, because the PAM provider refuses an
incomplete secret before the service sees it. (2) The refusals the room-list resolver and membership
read return without throwing (catalog unresolved, no group or several, group answers as another
object) are logged nowhere by the service on either path; only the row's audit carries the message,
so an operator reading the application log sees no trace of why a row was refused.
**Status**: Fixed on branch, not merged; guard proof below; independent review pending
**Branch**: `worktree-agent-a3acf365c1c335b8c`
**Commit**: the fix commit after plan `95bfa5ca` (fix, tests, comments, record)
**Reviewer**: implementer (surfaced by the aud-a2s agent, 2026-10-22, its two candidates)

## Evidence

At `3ff4a6d4`, `Services/ConferenceRoomService.cs`: `ResolveAdGroupGuid`, `ResolveForestGlobalCatalog`,
`ReadOnPremListMembership` return refusals without a log line; the City/State/Country credential step
(`CreateAdCredential`) has no harness knob reaching it.

## Approach (to be written by the implementing agent before code)

- Log each returned refusal at Warning with the room, the list and the reason, once, at the point it
  is decided (not again at the caller); the audit is unchanged.
- Reach the credential step on the City/State/Country paths through the aud-a2s `IncompleteCredentialFor`
  seam and pin its step name; label it as not reachable live for the same reason aud-a2s recorded.
- Tests: each refusal logs one warning naming the reason; the credential step is named on both
  City/State/Country paths. Mutations at the call site: drop one refusal's log line; collapse the
  credential step name.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("ALL the bugs includes ALL the bugs ... EVERYTHING", `.agents/decisions.md`
2026-10-21); the admitted approach above and the supervising session's brief. One finding, so one fix commit for its two
parts. Base `53efd9d1`; the line numbers below are that tree's.

**Rule.** (1) Every step the City/State/Country paths name is reached by a test. (2) A refusal the room list's resolver or
membership read returns without throwing reaches the application log - once, at Warning, naming the room, the list and the
reason - on the preflight and on the add.

1. **The credential step on both City/State/Country paths - test-only.** `ConferenceRoomWriteIdentityTests.Service`
   (`ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs` 49-64) gains aud-a2s's seam as
   `ConferenceRoomRoomListPreflightTests.Service` has it (66-81): `IncompleteCredentialFor` - for a purpose containing that
   text, a credential with no password, so `CreateAdCredential` (`Services/ConferenceRoomService.cs` 2606-2616) throws in
   the step "preparing the module's AD credential" (the preflight 1587-1588, the write 1674-1675). Each step theory gains
   a `credential` case: `AFailureAtEachStepOfTheCityStateAndCountryPreflight_NamesThatStep` (678-715; the purpose "Set-ADUser
   preflight", 1567) and `AFailureAtEachStepOfTheCityStateAndCountryWrite_NamesThatStep` (472-527; "Set-ADUser synced
   attributes", 1652). *Not reachable live*, for aud-a2s's reason: the PAM provider returns no credential at all for a
   secret missing its username or password (`DelineaService.GetCredentialsBySecretIdAsync` 199-205), which both paths
   refuse before the runspace as "not configured" (1568-1569, 1653-1654). With the username and password it does return,
   `CreateAdCredential` can throw only for a password past `SecureString`'s capacity of 65,536 characters (measured here,
   pwsh 7.6.6 in-process: `AppendChar` throws "Capacity exceeds maximum capacity" at the 65,537th) - no plausible secret.
   So the two cases pin the step's name, not a failure seen live. No service change for this part.

2. **The refusals logged.** The resolver (`ResolveAdGroupGuid` 2276-2313, with `ReadAdGroupInItsDomain` 2322-2345 and the
   catalog through `ResolveForestGlobalCatalog` 2357-2377) and the membership read (`ReadOnPremListMembership` 2399-2423)
   return every refusal they make - no address; the catalog unresolved; the search's error record; no group, or several;
   no ObjectGUID; a DN naming no domain; the re-read's error record; the group's own domain answering as another object or
   with no DN; the room read with no DN; the membership read's error record; the group answering it as another object -
   and no line of the service logs any of them. Each is logged where the path decides that it refuses the row, once, at
   Warning, then returned exactly as now:
   - `PreflightRoomListAsync` (1796-1877), after the resolver (1858-1860) and after the membership read (1868-1869):
     `"On-prem room-list preflight for {Room} on {List} refused the row; nothing was written: {Refusal}"` - `roomEmail`,
     `list.Name`, the refusal as returned (before " Nothing was written." is appended).
   - `AddToRoomListViaAdAsync` (1895-2064), after the resolver (1934-1936) and after the membership read (1945-1947):
     `"On-prem room-list add for {Room} on {List} refused before its write: {Refusal}"` - `roomEmail`, `roomListName`, the
     refusal. "Before its write", not "nothing was written": this runs after Set-Place, inside the row's partial (aud-a2r).
   - *Where, and why not in the helpers.* They are static readers that return a reason, shared by both paths: the shape of
     `ReadObjectThrough`, whose reason the City/State/Country preflight logs at Warning as it refuses (aud-a2q, 1619-1623).
     The membership read is also the add's read-back (2017), whose outcome the add already logs at Warning with the
     write's error and the server (aud-a2l, aud-a2n; 2024-2039): a line in the helper would log that one twice. Only the
     path knows whether anything has been written, which its line says, as the step catches' lines do (aud-a2s). And one
     line at each of the four call sites covers every refusal its helper returns - the error-record and no-ObjectGUID ones
     too, which the stand-ins cannot reach (they throw under `-ErrorAction Stop`; a stand-in group always has an
     objectGUID) - so no refusal is left to a line no test reaches. Not again by the paths' callers
     (`SetRoomMetadataAndListAsync`, `AddToRoomListAsync`), which put the refusal on the result.
   - Unchanged: every row's outcome, message, steps, audit and notification; the read-back and its log lines; the step
     catches and every throw (named by them, aud-a2s); the City/State/Country paths' code.

**Tests** (through `ExchangeWriteHarness`; no directory, Exchange or network call; no harness change).
- `ConferenceRoomWriteIdentityTests`, part 1 - each theory's `credential` case, asserted as its other steps are. The
  preflight: refused, not partial, nothing written, the one step `Set-ADUser preflight (City/State/Country)`, the message
  naming "preparing the module's AD credential failed: " with the text the throw carries, the objectGUID, ending "Nothing
  was written.", an Error logged naming the step. The write: failed, partial, nothing written, the step
  `Set-ADUser (City/State/Country)` failed, its message opening "City/State/Country were not written on the AD object
  <guid>: " and naming the step, not in the write's words, no log line naming Set-ADUser, no `Set-ADUser` made, the
  timezone not run.
- `ConferenceRoomRoomListPreflightTests`, part 2 - a synced list the room is not on in Exchange, a capturing logger; the
  eight refusals the stand-ins can reach: no address (`ListAddresses`), no catalog (`GlobalCatalogs` cleared), no group,
  two groups, a group DN naming no domain (`DirectoryGroupDomains`), the group's own domain answering as another object
  (`DirectoryGroupReadBack`), the room read with no DN, the membership read answering as another object
  (`DirectoryGroupMemberReadBack`).
  - `ARefusalOfTheRoomListPreflight_IsLoggedOnce_NamingTheRoomTheListAndTheReason` (the eight), the whole row through
    `SetRoomMetadataAndListAsync`: refused, not partial, nothing written, the refusal in the case's own words; exactly one
    log entry carries the refusal (the message less " Nothing was written."), a Warning, equal to the preflight's line for
    this room, this list and that refusal.
  - `ARefusalOfTheOnPremRoomListAdd_IsLoggedOnce_NamingTheRoomTheListAndTheReason` (the eight), the add called directly
    through aud-a2p's seam - no preflight, so the add's own resolver and membership read meet each: failed, the step
    `Add to '<list>' (on-prem)` carrying the refusal in the case's words, no `Set-ADGroup`; exactly one entry carries it, a
    Warning, equal to the add's line.
  - `ARefusalOfTheOnPremRoomListAdd_InTheWholeRow_IsLoggedOnce`: the catalog failing only after Set-Place
    (`FailuresAfterSetPlace['Get-ADForest']`), through `SetRoomMetadataAndListAsync` - the partial, and exactly one entry
    carrying the add's refusal: the row adds none.

**Mutations, at the call sites** (md5 before and after, the named failing tests, restored, md5 matched, touched, rebuilt,
green): M1a-d the brief's (1) - each of the four refusal log lines dropped in turn; M2a-b the brief's (2) - the credential
step's name collapsed into the import's (its assignment removed), on the preflight, then on the write; M3a-b the row's caller
logging the refusal a second time - `SetRoomMetadataAndListAsync` for the preflight's, `AddToRoomListAsync` for the add's;
REV the service whole as at `53efd9d1`, the new tests kept (they name nothing the fix adds). Comments after the round,
checked code-identical by a comment-only diff.

**Files.** `Services/ConferenceRoomService.cs`, `ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs`,
`ExchangeAdminWeb.Tests/ConferenceRoomRoomListPreflightTests.cs`, this record.

## Approach (as implemented)

- `Services/ConferenceRoomService.cs`. `PreflightRoomListAsync`: after the resolver's refusal (1867) and after the membership
  read's (1883 - an `if` and two returns in place of the ternary), `_logger.LogWarning("On-prem room-list preflight for
  {Room} on {List} refused the row; nothing was written: {Refusal}", roomEmail, list.Name, <the refusal>)`, then the refusal
  returned as before with " Nothing was written.". `AddToRoomListViaAdAsync`: after each of the same two (1954, 1970),
  `_logger.LogWarning("On-prem room-list add for {Room} on {List} refused before its write: {Refusal}", roomEmail,
  roomListName, <the refusal>)`, then returned as before. The helpers, their refusals, the read-back and every caller are
  unchanged; the two helpers' headers say they log nothing and who does (2302, 2425-2427).
- `ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs`: the `Service` takes aud-a2s's `IncompleteCredentialFor` (57);
  `InjectedText(failing, injected)` (389), private static: the text injected, or, for the credential, the
  `NullReferenceException` message that building a credential with no password throws. The two City/State/Country step
  theories (491, 700) gain `credential`: each now builds its service before its switch, so the case can set the seam - the
  preflight for the purpose "Set-ADUser preflight", the write for "Set-ADUser synced attributes" - and asserts it as its
  other steps, the write's case among the steps before the write.
- `ExchangeAdminWeb.Tests/ConferenceRoomRoomListPreflightTests.cs` (1629-1759): `SetUpRefusal` and `RefusalWords` - the eight
  refusals the stand-ins can produce, and words of each refusal's own, so a case is known to have met the refusal it names;
  `ARefusalOfTheRoomListPreflight_IsLoggedOnce_NamingTheRoomTheListAndTheReason` (8),
  `ARefusalOfTheOnPremRoomListAdd_IsLoggedOnce_NamingTheRoomTheListAndTheReason` (8) and
  `ARefusalOfTheOnPremRoomListAdd_InTheWholeRow_IsLoggedOnce` (1). No harness change.
- 19 new cases (2 + 17). No existing assertion changed: the two step theories' existing cases run as before, on the same
  service, built a line earlier. On the fix the 330 `ConferenceRoom*` cases pass, the 311 existing among them.

## Guard proof

| Test | What it pins |
| --- | --- |
| `ConferenceRoomWriteIdentityTests.AFailureAtEachStepOfTheCityStateAndCountryPreflight_NamesThatStep` (credential) | part 1 on the preflight: the credential step reached - through the seam, not live - refused, not partial, `Writes` empty, the one step `Set-ADUser preflight (City/State/Country)` with the message as its `Error`; the message "... preparing the module's AD credential failed: <the throw's text> ..." with the objectGUID, ending "Nothing was written."; an Error logged naming the step |
| `ConferenceRoomWriteIdentityTests.AFailureAtEachStepOfTheCityStateAndCountryWrite_NamesThatStep` (credential) | part 1 on the write: failed, partial, nothing written, no detail, the step `Set-ADUser (City/State/Country)` failed; "City/State/Country were not written on the AD object <guid>: preparing the module's AD credential failed: <the throw's text>", not "Failed to write", no log line naming Set-ADUser, no `Set-ADUser` made, the timezone not run |
| `ARefusalOfTheRoomListPreflight_IsLoggedOnce_NamingTheRoomTheListAndTheReason` (no address, no catalog, no group, two groups, a group DN naming no domain, the group's own domain answering as another object, the room read with no DN, the membership read answering as another object) | part 2 on the preflight, through the whole row: refused, not partial, nothing written, the one step `Room list preflight`, the refusal in its own words; exactly one log entry carries it, a Warning, equal to "On-prem room-list preflight for <room> on <list> refused the row; nothing was written: <refusal>" - so the row logs it no second time |
| `ARefusalOfTheOnPremRoomListAdd_IsLoggedOnce_NamingTheRoomTheListAndTheReason` (the same eight) | part 2 on the add, called directly so its own resolver and membership read meet each: failed, nothing written, the step `Add to '<list>' (on-prem)` failed carrying the refusal in its own words, no `Set-ADGroup`; exactly one entry carries it, a Warning, equal to "On-prem room-list add for <room> on <list> refused before its write: <refusal>" |
| `ARefusalOfTheOnPremRoomListAdd_InTheWholeRow_IsLoggedOnce` | the add's refusal through the whole row (the catalog failing only after Set-Place): the partial, the refusal in the step and the message; one entry carries it, the add's Warning - the row, which wraps it in its partial, adds none |

**Mutations** (`scratchpad/a2t-round/round.ps1`, the a2s runner on this worktree, with `rows-a2t.ps1`; log `round-a2t.log`
beside them; 2026-10-22 15:52-15:59). Each row ran the 330 `ConferenceRoom*` cases, the test project rebuilt each time, the
failures read from its TRX. md5 before = restored: `Services/ConferenceRoomService.cs` `f7e08ae0503288f6d2a1dcd247b4f347` (every
row); the service was touched once mutated and once restored, so each build compiled the file as it then was. Test names are in
`ConferenceRoomRoomListPreflightTests` unless qualified (`Identity.` is `ConferenceRoomWriteIdentityTests`).

| # | Mutation | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- |
| M1a | the brief's (1), at the call site: the preflight's log line for the resolver's refusal dropped | `46b5b6e3e72ed64090f7c2ee8aa97711` | `ARefusalOfTheRoomListPreflight_...` (no address, no catalog, no group, two groups, a group DN naming no domain, the group's own domain answering as another object) | FAIL, 6 of 330: no entry carries the refusal. PASS: the two membership cases |
| M1b | the same, the preflight's line for the membership read's refusal | `4deb363c39d3ee3e767c63c4aec68fe0` | `ARefusalOfTheRoomListPreflight_...` (the room read with no DN, the membership read answering as another object) | FAIL, 2 |
| M1c | the same, the add's line for the resolver's refusal | `880298819f328a8484315f7552d7320b` | `ARefusalOfTheOnPremRoomListAdd_...` (the six resolver cases); `ARefusalOfTheOnPremRoomListAdd_InTheWholeRow_IsLoggedOnce` | FAIL, 7: the only entry the cloud add's own Error ("Exchange operation failed: ..."), which carries no refusal |
| M1d | the same, the add's line for the membership read's refusal | `25550781c85723543dc37ef713aa3653` | `ARefusalOfTheOnPremRoomListAdd_...` (the two membership cases) | FAIL, 2 |
| M2a | the brief's (2), at the call site: the City/State/Country preflight's credential step collapsed into the import's (its `step` assignment removed) | `e754dc58499002422ceec841d0bce987` | `Identity.AFailureAtEachStepOfTheCityStateAndCountryPreflight_NamesThatStep` (credential) | FAIL, 1: "preparing the module's AD credential failed: " not found - told as the import |
| M2b | the same on the write | `187e753b9c08ec66c75eab7ff53ea336` | `Identity.AFailureAtEachStepOfTheCityStateAndCountryWrite_NamesThatStep` (credential) | FAIL, 1: the same |

Beyond the brief's two:

| # | Mutation | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- |
| M3a | the row logs the preflight's refusal a second time (a warning in `SetRoomMetadataAndListAsync`'s preflight refusal) | `8443b16496951cc65eb5a788a83d4477` | `ARefusalOfTheRoomListPreflight_...` (all eight) | FAIL, 8: two entries carry the refusal |
| M3b | the add's caller logs the add's refusal a second time (a warning in `AddToRoomListAsync`'s on-prem failure) | `b5f9b6e96764b5f4306f743b7f0e77db` | `ARefusalOfTheOnPremRoomListAdd_...` (all eight); `ARefusalOfTheOnPremRoomListAdd_InTheWholeRow_IsLoggedOnce` | FAIL, 9: two entries carry the refusal |
| REV | the fix reverted: the service whole as at `53efd9d1`, the new tests kept (they name nothing the fix adds) | `52fdc56d7abb05fe8c0a67863c5fde69` | the three refusal tests | FAIL, 17 of 330. PASS: the two credential cases - part 1 closes a test gap, the step already named |

Every restored md5 equal to "before"; after the last row the restored tree rebuilt and the 330 `ConferenceRoom*` cases green.

**Comments after the round.** The round ran on the fix with none of the service's new explanatory comments (the test files'
were written with the tests). They were written afterwards and checked by `scratchpad/a2t-round/comment_only.ps1` (aud-a2q's
checker: blank, `//`, `///` and PowerShell `#` lines dropped from both versions, the code lines identical in order): service
`f7e08ae0503288f6d2a1dcd247b4f347` -> `f6b0323d2c504c5d2b5651c7a3b4f80b` (code lines 1783 = 1783; comment and blank lines 957 ->
974); `ConferenceRoomWriteIdentityTests.cs` `57104c12ee27b23de46ac4ae979b860d` -> `eaf52f657cadb98a2c7f4939122cdf76` (973 = 973;
298 -> 299: one phrase of a comment written with the tests, in each theory, reworded). Unchanged since the round:
`ConferenceRoomRoomListPreflightTests.cs` `8f68b7ca054f2e9c3690228d61562f0e`.

## Known gaps

- The credential step, on all four directory paths, is reached only through the test seam - aud-a2s's, now on the
  City/State/Country paths too. Not reachable live: the PAM provider returns no credential at all for a secret missing its
  username or password (`DelineaService.GetCredentialsBySecretIdAsync`), which the paths refuse before the runspace as not
  configured; with the username and password it does return, `CreateAdCredential` throws only for a password past
  `SecureString`'s capacity of 65,536 characters (measured: pwsh 7.6.6, in-process). The two cases pin the step's name, not a
  failure seen live.
- Four refusals the helpers return are reached by no test: the resolver's no-ObjectGUID refusal and the error-record refusals of
  the search, the re-read and the membership read. A stand-in group always has an objectGUID, and the stand-ins throw their
  errors under `-ErrorAction Stop`, as a cmdlet's own error is thrown under it (aud-a2o, measured) - so those branches can see
  only a record written past the preference. The line at each call site logs whatever its helper returns, so the tested cases pin the
  line those take too.
- Through the row, the add meets only a refusal whose cause appears after the preflight (pinned with the catalog, failing only
  after Set-Place): the preflight meets every other one first, on the same directory. The add's other refusals are reached by
  calling the add directly (aud-a2p's seam).
- "A group DN naming no domain" is a stand-in shape: every DN a global catalog returns for an object in a domain naming context
  names its domain (aud-a2q's assumption, carried, not probed). Its cases pin that `ReadAdGroupInItsDomain`'s refusal reaches the
  line.
- The catalog's own error reaches the log as text inside the refusal: `ResolveForestGlobalCatalog` catches it, so no exception or
  stack is logged with it.
- The read-back's refusals stay logged by the add's could-not-be-confirmed line (aud-a2l, aud-a2n), unchanged.
- Not changed, outside this record: the room-object read's refusals (`ConfirmDirectoryObject`: none, or another object,
  answering) are returned and logged nowhere on all four directory paths; nor are the refusals the preflights and the writes
  return before their runspace - an unmappable country, no list GUID, no sync state reported, no proven directory object (a
  missing credential is logged by the credential service). Reported in the hand-back.

**Operator-visible changes.**

- A Room Finder row refused by the room-list preflight for a reason the resolver or the membership read returns - the list's
  address missing, the forest's catalog unresolved, no group carrying the address or several, a group DN naming no domain, the
  group answering as another object, the room's object read with no DN - leaves one warning in the application log: "On-prem
  room-list preflight for <room> on <list> refused the row; nothing was written: <the refusal>". The same refusals met by the
  on-prem add after Set-Place: "On-prem room-list add for <room> on <list> refused before its write: <the refusal>". Before,
  only the row's result and its audit carried them.
- Nothing else: no outcome, message, write, audit or notification changes; the City/State/Country part is test-only.

**Dev acceptance:** nothing new beyond the Room Finder check owed with aud-a2f..a2s: a clean row logs no refusal, and the new
lines follow refusals, which are not to be induced on dev. Not run here: no directory, Exchange or SMTP call was made, and the
owner's standing instruction is not to probe Active Directory.

## Reviewer comments

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent on `worktree-agent-a3acf365c1c335b8c`.
Independent review pending.

For the reviewer:

- One fix commit for the finding's two parts.
- Where the refusals are logged. The brief's "where it is decided ... not again at the caller" is read as the point where a path
  turns the reason its helper returned into the row's refusal - the four call sites - with the paths' callers
  (`SetRoomMetadataAndListAsync`, `AddToRoomListAsync`) logging nothing. Not inside the helpers: they are static readers that
  return a reason, the shape of `ReadObjectThrough`, whose refusal its caller logs (aud-a2q); the membership read is also the
  add's read-back, already logged with the write's outcome (aud-a2n), so a line in it would log that twice; only the path knows
  whether anything was written; and one line per call site covers the refusals no stand-in reaches. M3a/M3b pin the "once".
- The seam is copied into `ConferenceRoomWriteIdentityTests.Service`, not shared: each test class keeps its own `Service` and
  `CapturingLogger` (aud-a2s's precedent).
- Beyond the brief's mutation rows: all four log lines (M1a-d), the credential step on both paths (M2a-b), the "once" (M3a-b) and
  a whole-file revert (REV).
- Outside the service and its tests: nothing.

## Closeout

Fixed on branch, not merged, not pushed: the plan `95bfa5ca`, then this fix, tests, comments and record in one commit. A
module-scoped behaviour change (Conference Rooms: four warning log lines): the Conference Rooms module version bump is due at
merge - not made here, `Modules/ModuleCatalog.cs` being outside this brief. No shared code changed, so no base app bump.

Gates, run on the final files this commit holds (the mutation round ran before the comments, which the comment-only check shows
changed no code line):

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings - the baseline count - none in a
  touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: 5545 passed, 0 failed, 3 skipped (the known AD
  skips, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`), 5548 total, 5 m 26 s; nothing hung, no Sequence
  file. This record's new cases are 19; the 316 `ClickGate*` cases are among those passed (and, run alone, 316 passed).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD`: clean.
- `tools/Test-AsciiOnly.ps1` after `git add -A`: clean.

PSScriptAnalyzer and Pester were not run: no `.ps1` or `.psm1` changed (the scratch runner and checker are not in the
repository).

Base `53efd9d1`. The local master has since moved to `e9a59ed4` - eleven commits of the attribute editor, event log and a
decision - touching none of this record's files; the totals above are this branch's.
