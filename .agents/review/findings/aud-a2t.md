# aud-a2t: Conference Rooms - the credential step has no test, and the room-list resolver's refusals are not logged

**Severity**: LOW - a test gap and an observability gap; no wrong write. (1) On the City/State/Country
paths the step "preparing the module's AD credential" is named (aud-a2q/a2r) but no test reaches it;
the room-list paths reach theirs only through a test seam, because the PAM provider refuses an
incomplete secret before the service sees it. (2) The refusals the room-list resolver and membership
read return without throwing (catalog unresolved, no group or several, group answers as another
object) are logged nowhere by the service on either path; only the row's audit carries the message,
so an operator reading the application log sees no trace of why a row was refused.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; plan written, in progress
**Branch**: `worktree-agent-a3acf365c1c335b8c`
**Commit**: -
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

## Guard proof

Pending.
