# aud-a2q: Conference Rooms - the cloud no-op builds an "Added" message, the attribute write's outer catch blames Set-ADUser for any step, and a commit-then-throw leaves a partial row

**Severity**: MEDIUM for the folded-in preflight part (an avoidable partial after Set-Place); the three original parts are LOW - no wrong write. (1) The cloud already-a-member path in `AddToRoomListAsync`
(step "Already in '<list>'") still builds "Added to room list '<list>'.", an unused message claiming
an add that never happened (the on-prem twin was fixed in aud-a2p). (2) `SetSyncedAttributesViaAdAsync`'s
outer catch logs "Set-ADUser synced-attribute write failed" and reports "Failed to write
City/State/Country" for failures of the surrounding steps too (runspace open, module import, the
confirming read), the log-line class aud-a2n fixed for the group add. (3) A `Set-ADUser` that
commits and then throws (a timeout) still leaves a partial row though the values are in place;
aud-a2n reconciles the group write by read-back after any error, aud-a2p's read-back runs only after
a clean return.
**Status**: Merged to master (`f4de7909`); closed on guard proof; codex clean (one LOW folded into aud-a2r); dev acceptance owed
**Branch**: `worktree-agent-a69863ec13d81b5ba`
**Commit**: `df084c68` (fix, tests, comments, record); plan `a37754c4`; merged `f4de7909`
**Reviewer**: implementer (surfaced by the aud-a2p agent, 2026-10-22, its three candidates)

## Evidence

At `28ffde25`, `Services/ConferenceRoomService.cs`: `AddToRoomListAsync` cloud branch's no-op
message; `SetSyncedAttributesViaAdAsync` outer catch and its message; the write's `try/finally`
(aud-a2p) with the read-back after it.

## Approach (to be written by the implementing agent before code)

- Cloud no-op: build the "already in" message (or remove the unused one) with a test pinning that the
  cloud no-op's result message does not claim an add.
- Outer catch: name the step that failed (runspace, import, object read, write, read-back) in the log
  and the row message; the write's own failure keeps its text.
- Commit-then-throw: hold the write error (aud-a2o/a2n try/catch/finally shape), read the attributes
  back, and when they match report success with a "confirmed after a write error" detail and no
  `writtenDirectoryObject` (aud-a2g; the aud-a2n `DirectoryMembershipConfirmed` precedent, a sibling
  flag for attributes), keeping the error text in the step detail; a read-back that misses stays a
  failed row carrying the write error.
- Tests through `ExchangeWriteHarness`: the cloud no-op message; a failure injected at each
  surrounding step names that step; a `Set-ADUser` that applies then throws is settled by the
  read-back with no written object recorded. Mutations at the call site: restore the "Added"
  message; collapse the step naming; drop the read-back after a thrown error.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("ALL the bugs includes ALL the bugs ... EVERYTHING", `.agents/decisions.md`
2026-10-21); the admitted approach above, the folded-in MEDIUM part at the end of this record and the supervising
session's brief. One finding, so one fix commit for all four parts, the MEDIUM part first in it.

**Rule.** The City/State/Country write goes only through the server its preflight proved, before `Set-Place`, that the
room's object answers through; whatever the write raises, the attributes read back decide the row: as written is a
success, the object named written only when the write raised nothing (aud-a2g); not as written, or unreadable, a failed
row carrying the write's error. A failure names the step it came from. No message claims a write that did not happen.

1. **Routed preflight (MEDIUM).** `PreflightSyncedAttributesViaAdAsync` returns `(error, server)`. Its object read becomes
   `ConfirmDirectoryObject`'s DN overload (unrouted, as now: it finds the object in the module's default domain and its
   DN); then `server = GroupManagementService.ServerFromDn(<that DN>)`, the derivation the write makes today.
   - A server: `ReadObjectThrough(ps, objectGuid, server, credential)`, new, private static - `Get-ADUser -Identity
     <objectGUID> -Server <server> -Credential -ErrorAction Stop`, an error record failing it as in the file's other reads,
     exactly one object with that objectGUID required (`"<n> object(s) answered, ObjectGUID <guid or none>"` otherwise), a
     throw caught by the caller. Any failure refuses before `Set-Place`: "City/State/Country could not be written for
     '<room>': its AD object <guid> is written through <server>, the domain its distinguished name names, and could not be
     read there: <why>. Nothing was written." - the existing preflight refusal (one failed `Set-ADUser preflight
     (City/State/Country)` step, not partial, nothing written); a warning logged. No other server is tried: no guess.
     Otherwise `(null, server)`.
   - No server (no DN, or one with no DC components): the explicit fallback - `(null, null)` and a warning that the write
     goes to the default domain controller, the unrouted route the read above has just proved; no second read.
   - `SetRoomMetadataAndListAsync` hands that server to `SetSyncedAttributesViaAdAsync(..., server)`, which no longer
     derives one: its confirming read is `ConfirmDirectoryObject` without the DN (aud-a2d's read, unrouted as now), and
     `Set-ADUser` and the read-back take `-Server <the proven server>` - none for the fallback, "the default domain
     controller" in the messages, as now.
2. **Cloud no-op message.** `AddToRoomListAsync`'s cloud path keeps whether its membership read found the room already
   there (the lambda's `alreadyMember`, hoisted). Success: that no-op is `BuildRoomListAlreadyInMessage(list)`, internal
   static, pure - "Already in room list '<list>': nothing was written.", the on-prem twin's wording (aud-a2p) - and only an
   add `BuildRoomListAddedMessage(list, viaOnPrem: false)`. The caller replaces a success's message, so it is shown
   nowhere today; pinned by a direct call through aud-a2p's `internal` seam.
3. **Step naming.** `SetSyncedAttributesViaAdAsync` keeps `step` - "opening the directory runspace", "importing the
   ActiveDirectory module", "preparing the module's AD credential", "reading the AD object <guid>", "Set-ADUser" - set
   before each; the outer catch logs "City/State/Country for {Room} were not written on the AD object {Object}: {Step}
   failed" with the exception and returns "City/State/Country were not written on the AD object <guid>: <step> failed:
   <error>". With (4) only the steps before the write's `Invoke` reach it, so "were not written" holds. The write and the
   read-back settle their own errors and name themselves: the write's failure keeps its text, "Failed to write
   City/State/Country on the AD object: <error>", followed by what the read-back found (4); the read-back's is aud-a2p's
   "... could not be confirmed: reading l, st, co back from <server> failed: <error>"; each logged naming its step.
4. **Commit-then-throw.** The write in `try { ps.Invoke(); } catch (Exception ex) { writeError = ex; } finally { the
   commands and the error stream cleared }` - aud-a2o's shape, aud-a2n's every-error-read-back rule - then
   `ReadBackSyncedAttributes` (a throw caught as its error) whatever the write did. Returns `(error, written, confirmed)`:
   - no write error: unchanged - all as written `(null, objectGuid, null)`; otherwise aud-a2p's not-confirmed and
     could-not-be-confirmed failures;
   - a write error, all as written: `(null, null, <detail>)`, the detail `AttributesConfirmedAfterWriteError` (internal
     const, "City/State/Country confirmed in place after Set-ADUser reported an error") + " (<error>): the AD object
     <guid>, read back from <server>, holds them as written. No directory object is recorded as written: whether this
     row's write put them there is not known."; a warning logged;
   - a write error, not as written: "Failed to write City/State/Country on the AD object: <error> Read back from <server>
     afterwards, <each attribute, aud-a2p's wording>."; the read-back failing: "Failed to write City/State/Country on the
     AD object: <error> Reading l, st, co back from <server> afterwards failed too: <read error>"; logged as an error with
     the write's exception. The row: the existing partial.
   - The confirmed row: `RoomOperationResult.DirectoryAttributesConfirmed`, new - the sibling of aud-a2n's
     `DirectoryMembershipConfirmed` - holds the detail; `WrittenDirectoryObject` stays null for it; the step `Set-ADUser
     (City/State/Country)` is successful with the detail as its `Error` (the page shows a step's `Error`); later steps run
     as after any success; the final success message says City/State/Country "read back as set on-prem after Set-ADUser
     reported an error" in place of "were written on-prem" (the admin notification carries it). Bulk: the row message is
     `r.Message`, then the attributes detail, then the membership detail, each when set (aud-a2o's rule). Audit:
     `AttributesConfirmedAuditKey` (`attributesConfirmedPresent`) and `WithAttributesConfirmed(extra, detail)`, the
     siblings of aud-a2n's, called by the page's `AuditFinderAction` (lines 1079, 1720 and 1722 edited in place: 1758
     lines, no `ClickGateRegistry` entry moves) and the processor's `Audit`.
5. Comments after the mutation round, checked code-identical by a comment-only diff.

**Tests** (through `ExchangeWriteHarness`; no directory, Exchange or network call).
- Harness, test-only and additive: `DirectoryServerFailures` (server -> message) fails the stand-in `Get-ADUser` and
  `Set-ADUser` sent `-Server` to that server, and a `-Server` the stand-in forest does not have (`Get-StandInReach`) is
  "Unable to contact" for them too; without `-Server` they reach the default domain as now. `SetAdUserFailsAfterApplying`
  (a message or an exception) applies the `-Replace`, then throws. Once `Set-Place` has been called -
  so the preflight's own reads are untouched - `DirectoryRunspaceFailureAfterSetPlace` fails `OpenDirectoryRunspace`,
  `ImportModuleFailureAfterSetPlace` the stand-in `Import-Module`, `ObjectReadFailureAfterSetPlace` a `Get-ADUser
  -Identity` without `-Properties` (the write's confirming read), and `ObjectDnAfterSetPlace` changes the DN that read
  answers. A harness test pins each.
- `ConferenceRoomWriteIdentityTests` (a capturing logger on its service):
  - the folded-in scenario: the default domain reachable with no `-Server`, `-Server contoso.test` failing - refused, not
    partial, `Writes` empty, one step, the message naming `contoso.test`, the room, its objectGUID and the error, ending
    "Nothing was written.";
  - the normal case: before `Set-Place` an unrouted read, then the read by the objectGUID through `contoso.test`; after it
    `Set-ADUser` and the read-back through `contoso.test`;
  - carried, not derived twice: with `ObjectDnAfterSetPlace` naming the other domain, the write and the read-back still go
    to `contoso.test`, the server the preflight proved;
  - a DN naming no domain: one object read before `Set-Place`, the write and the read-back without `-Server`, success;
  - a failure at each step (runspace, import, object read, write, read-back): failed, partial, nothing written, the step's
    `Error`, the message and the logged error naming that step and carrying the injected text; the three before the write
    not in the write's words, the write's in them;
  - applied, then thrown: success, not partial, nothing written, `DirectoryAttributesConfirmed` opening
    `AttributesConfirmedAfterWriteError` with the error's text, the same as the step's `Error`, the read-back made, the
    message not "were written";
  - thrown without applying, an older City there: failed, partial, nothing written, no detail, the write's error and the
    read-back's finding, the read-back made, the timezone not run; and with the read-back failing too - both errors.
- `ConferenceRoomRoomListPreflightTests`: the cloud add's own message, by a direct call - already a member: no
  `Add-DistributionGroupMember`, the already-in message, not the add's; added: the add's; pure - the already-in message
  names the list and claims no add.
- `ConferenceRoomDirectoryAuditTests`, page and bulk: applied, then thrown - Success, no `writtenDirectoryObject`,
  `attributesConfirmedPresent` opening `AttributesConfirmedAfterWriteError` with the error, and (bulk) the row's message
  carrying it; `CityWritten_...` and `ASetADUserThatFails_...` also pin no `attributesConfirmedPresent`.
- `ConferenceRoomBulkProcessorTests` (the fake ops): a result with `DirectoryAttributesConfirmed`, and with both details -
  the row message carries each after the result's.

**Mutations, at the call sites** (md5 before and after, the named failing tests, restored, md5 matched, touched, rebuilt,
green): M1 the brief's - the routed preflight read dropped, the derived server returned unproven; M2 the brief's - the
cloud no-op given "Added to room list"; M3 the brief's - the outer catch's step naming collapsed to its old log line and
message; M4 the brief's - no read-back after a thrown write error, the old failure returned; M5 the write deriving its
server from its own read's DN again; M6 the confirmed path reporting the object written; harness rows for the knobs the
tests rest on.

**Files.** `Services/ConferenceRoomService.cs`, `Models/ConferenceRoomModels.cs`, `Services/Jobs/ConferenceRoomBulkProcessor.cs`,
`Components/Pages/ConferenceRooms.razor` (three lines in place), `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`,
`ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs`, `ExchangeAdminWeb.Tests/ConferenceRoomRoomListPreflightTests.cs`,
`ExchangeAdminWeb.Tests/ConferenceRoomDirectoryAuditTests.cs`, `ExchangeAdminWeb.Tests/ConferenceRoomBulkProcessorTests.cs`,
this record.

## Approach (as implemented)

- `PreflightSyncedAttributesViaAdAsync` (`Services/ConferenceRoomService.cs`) returns `(error, server)`; every early return
  is `(<its message as before>, null)`, the no-op `(null, null)`. Its object read is `ConfirmDirectoryObject(..., out
  roomDn)` (unrouted, as before); then `server = GroupManagementService.ServerFromDn(roomDn)`:
  - null (no DN, or no DC components): a warning ("... was read with no distinguished name naming its domain (<dn or
    none>), so City/State/Country go to the default domain controller, the route that read took") and `(null, null)`;
  - otherwise `ReadObjectThrough(ps, objectGuid, server, credential)` (private static: `Get-ADUser -Identity <objectGUID>
    -Server <server> -Credential -ErrorAction Stop`; an error record returns its first error; anything but exactly one
    object whose objectGUID is the one asked for returns "<n> object(s) answered, ObjectGUID <guid or none>"), a throw
    caught as its error. An error: a warning and `("City/State/Country could not be written for '<room>': its AD object
    <guid> is written through <server>, the domain its distinguished name names, and could not be read there: <why, ended
    as a sentence> Nothing was written.", null)`; none: `(null, server)`. The caller's refusal is unchanged in shape: not
    partial, one failed step `Set-ADUser preflight (City/State/Country)`, nothing written.
- `SetRoomMetadataAndListAsync` passes that server to `SetSyncedAttributesViaAdAsync(..., server)` and takes back `(error,
  written, confirmed)`: `WrittenDirectoryObject = written`, `DirectoryAttributesConfirmed = confirmed`; the successful
  step `Set-ADUser (City/State/Country)` carries `Error = confirmed`; the final success message with City/State/Country
  given reads "... City/State/Country read back as set on-prem after Set-ADUser reported an error, and will appear in
  Exchange/Room Finder after the next directory sync (typically ~30 min)." when `confirmed` is set, as before otherwise.
- `SetSyncedAttributesViaAdAsync` derives no server: `serverName` is the handed one or "the default domain controller".
  Its confirming read is `ConfirmDirectoryObject` without the DN (unrouted, as before). `step` is set before each step -
  "opening the directory runspace", "importing the ActiveDirectory module", "preparing the module's AD credential",
  "reading the AD object", "Set-ADUser" - and the outer catch logs `"City/State/Country for {Room} were not written on
  the AD object {Object}: {Step} failed"` (with the exception) and returns "City/State/Country were not written on the AD
  object <guid>: <step> failed: <error>". The write: `try { ps.Invoke(); } catch (Exception ex) { writeError = ex; }
  finally { ps.Commands.Clear(); ps.Streams.Error.Clear(); }`, then `ReadBackSyncedAttributes` (unchanged, a throw caught
  as its error) whatever the write did:
  - no write error: aud-a2p's three outcomes, unchanged;
  - a write error, the read-back failing: `LogError(writeError, ...)` and "Failed to write City/State/Country on the AD
    object: <error, ended as a sentence> Reading l, st, co back from <server> afterwards failed too: <read error>";
  - a write error, not as written: `LogError(writeError, ...)` and "Failed to write City/State/Country on the AD object:
    <error, ended as a sentence> Read back from <server> afterwards, <aud-a2p's per-attribute text joined by "; ">.";
  - a write error, all as written: a warning and `(null, null, <detail>)`, the detail
    `$"{AttributesConfirmedAfterWriteError} ({error}): the AD object <guid>, read back from <server>, holds them as
    written. No directory object is recorded as written: whether this row's write put them there is not known."`.
- `ReadObjectThrough` and `Sentence` (private static: the text trimmed, a "." added unless it ends with one) are new;
  `AttributesConfirmedAfterWriteError` (internal const, "City/State/Country confirmed in place after Set-ADUser reported
  an error"), `AttributesConfirmedAuditKey` (public const, `attributesConfirmedPresent`) and
  `WithAttributesConfirmed(extra, detail)` (public static: the key added only for a detail, `extra` created when null)
  are new, beside aud-a2n's.
- `AddToRoomListAsync`, the cloud path: `alreadyInList`, set from the membership read's `alreadyMember` inside the
  lambda; success is `alreadyInList ? BuildRoomListAlreadyInMessage(list) : BuildRoomListAddedMessage(list, viaOnPrem:
  false)`. `BuildRoomListAlreadyInMessage` (internal static, pure): "Already in room list '<list>': nothing was written.".
- `Models/ConferenceRoomModels.cs`: `RoomOperationResult.DirectoryAttributesConfirmed` (string?).
- `Services/Jobs/ConferenceRoomBulkProcessor.cs`: the row's `Message` is `string.Join(" ", [r.Message,
  r.DirectoryAttributesConfirmed, r.DirectoryMembershipConfirmed].OfType<string>())`; `Audit` takes
  `attributesConfirmed` and wraps its `extra` in `WithAttributesConfirmed`.
- `Components/Pages/ConferenceRooms.razor`: `AuditFinderAction` takes `attributesConfirmed` (line 1720) and wraps its
  `extra` in `WithAttributesConfirmed` (1722); the Finder handler passes `r.DirectoryAttributesConfirmed` (1079). Three
  lines edited in place; 1758 lines before and after; no `ClickGateRegistry` entry moved.
- Harness (`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`), test-only and additive, every knob off by default:
  `DirectoryServerFailures` (server -> message), `SetAdUserFailsAfterApplying`, `DirectoryRunspaceFailureAfterSetPlace`,
  `ImportModuleFailureAfterSetPlace`, `ObjectReadFailureAfterSetPlace`, `ObjectDnAfterSetPlace`. The stand-in
  `Get-ADUser` and `Set-ADUser` record the call, then fail a `-Server` that `Get-StandInServerFailure` finds unusable -
  named in `DirectoryServerFailures` (its message), or one `Get-StandInReach` does not reach ("Unable to contact the
  server '<server>'.") - Write-Error then return; no `-Server` is unaffected. `Set-ADUser` throws
  `SetAdUserFailsAfterApplying` after applying its `-Replace`. Once a `Set-Place` call is recorded
  (`Test-StandInAfterSetPlace`): `OpenDirectoryRunspace` throws `DirectoryRunspaceFailureAfterSetPlace`, the stand-in
  `Import-Module` throws `ImportModuleFailureAfterSetPlace`, a `Get-ADUser -Identity` without `-Properties` throws
  `ObjectReadFailureAfterSetPlace`, and `Get-ADUser` answers `ObjectDnAfterSetPlace` as the DistinguishedName.
- Tests: new - `ConferenceRoomWriteIdentityTests.TheCityStateAndCountryPreflight_RefusesBeforeAnyWrite_WhenTheObjectCannotBeReadThroughTheServerTheWriteUses`,
  `TheCityStateAndCountryPreflight_ReadsTheObjectThroughTheServerTheWriteUses_AndTheWriteGoesThere`,
  `TheWrite_GoesToTheServerThePreflightProved_NotOneItDerivesAgain`,
  `ARoomObjectWhoseDnNamesNoDomain_IsWrittenOnTheRouteThePreflightRead` (no DN / no DC components),
  `AFailureAtEachStepOfTheCityStateAndCountryWrite_NamesThatStep` (runspace, import, object read, write, read-back),
  `ASetADUserThatAppliedThenFailed_IsASuccess_WithTheErrorInTheDetail_NothingRecordedAsWritten`,
  `ASetADUserThatFailedWithoutApplying_StaysAFailedRow_CarryingTheWriteError` (the read-back answering / failing) and
  `TheHarness_ADUserStandIns_RefuseAServerTheyCannotUse_AndFailAfterSetPlace_OrAfterApplying` (its service takes a
  capturing logger); `ConferenceRoomRoomListPreflightTests.TheCloudAddsOwnMessage_ClaimsAnAddOnlyWhenItAddedTheRoom`
  (already there / added) and `TheCloudAddsAlreadyInMessage_NamesTheList_AndClaimsNoAdd`;
  `ConferenceRoomDirectoryAuditTests.ASetADUserThatAppliedThenFailed_IsASuccess_RecordedAsConfirmed_NotAsWritten` (page,
  bulk); `ConferenceRoomBulkProcessorTests.ARowsMessage_CarriesCityStateAndCountryTheReadBackConfirmed_WhenTheResultHasThem`
  (alone / with a membership). Changed - `ConferenceRoomDirectoryAuditTests.CityWritten_TheAuditNamesTheObjectWritten` and
  `ASetADUserThatFails_IsNotRecordedAsWritten` (page, bulk) also pin no `attributesConfirmedPresent`. 21 new cases; no
  existing assertion changed or removed - every existing Conference Rooms test passed on the fix before the new ones
  were added (253 of 253).

## Guard proof

| Test | What it pins |
| --- | --- |
| `TheCityStateAndCountryPreflight_RefusesBeforeAnyWrite_WhenTheObjectCannotBeReadThroughTheServerTheWriteUses` | the folded-in MEDIUM: the default domain answering a read with no `-Server`, `-Server contoso.test` failing - refused, not partial, nothing written (`Writes` empty, `l` never set), one step, `Set-ADUser preflight (City/State/Country)`, its message naming `contoso.test`, the room as typed, its objectGUID and the error and ending "Nothing was written."; the two reads made, unrouted then through `contoso.test` |
| `TheCityStateAndCountryPreflight_ReadsTheObjectThroughTheServerTheWriteUses_AndTheWriteGoesThere` | the normal case: before `Set-Place` two reads by the objectGUID without `-Properties` - unrouted, then through `contoso.test` - and no write; after it `Set-ADUser` and its read-back through `contoso.test`; success, the object written |
| `TheWrite_GoesToTheServerThePreflightProved_NotOneItDerivesAgain` | carried, not derived twice: the object answering a `fabrikam.test` DN after `Set-Place`, the write and the read-back still go to `contoso.test` |
| `ARoomObjectWhoseDnNamesNoDomain_IsWrittenOnTheRouteThePreflightRead` (no DN / no DC components) | the explicit fallback: one read before `Set-Place`, unrouted; the write and the read-back unrouted; success; a warning naming the default domain controller |
| `AFailureAtEachStepOfTheCityStateAndCountryWrite_NamesThatStep` (runspace, import, object read, write, read-back) | part 3: each a failed, partial row, nothing written, no detail, the step failed carrying the injected text, its message and a logged warning or error naming that step; the three before the write "City/State/Country were not written on the AD object <guid>: <step> failed: ...", in no words of the write's, no log line naming Set-ADUser, no `Set-ADUser` made; the write's own "Failed to write City/State/Country on the AD object: ..."; the read-back's "reading l, st, co back from contoso.test failed"; the timezone not run |
| `ASetADUserThatAppliedThenFailed_IsASuccess_WithTheErrorInTheDetail_NothingRecordedAsWritten` | part 4: applied, then a timeout - success, not partial, nothing written, `DirectoryAttributesConfirmed` opening `AttributesConfirmedAfterWriteError` and carrying the timeout, the objectGUID and the server, the same text as the successful step's `Error`; the read-back made; the values in place; the message without "were written"; the timezone run |
| `ASetADUserThatFailedWithoutApplying_StaysAFailedRow_CarryingTheWriteError` (read-back answering / failing) | part 4's other half: the timeout before anything is applied, an older City there - failed, partial, nothing written, no detail, "Failed to write City/State/Country on the AD object: <timeout>" then the read-back's finding ("Read back from contoso.test afterwards, l (City) is 'Cork', not 'Dublin' as written.") or its failure ("Reading l, st, co back from contoso.test afterwards failed too: ..."); the read-back made; the timezone not run |
| `TheHarness_ADUserStandIns_RefuseAServerTheyCannotUse_AndFailAfterSetPlace_OrAfterApplying` | the stand-ins, so the tests above cannot pass for the wrong reason: `DirectoryServerFailures` fails `Get-ADUser` and `Set-ADUser` through that server with its message, nothing applied, and not a read with no `-Server` or through the other domain; an unknown server "Unable to contact"; `SetAdUserFailsAfterApplying` applies, then throws; the import, object-read and DN knobs change nothing before a `Set-Place` and each its thing after one (the DN on a `-Properties` read too); the runspace knob only after one |
| `ConferenceRoomRoomListPreflightTests.TheCloudAddsOwnMessage_ClaimsAnAddOnlyWhenItAddedTheRoom` (already there / added) | part 1, by a direct call: the no-op - no `Add-DistributionGroupMember`, its `Already in '<list>'` step, the already-in message, not the add's; the add - the add's |
| `ConferenceRoomRoomListPreflightTests.TheCloudAddsAlreadyInMessage_NamesTheList_AndClaimsNoAdd` | the message, pure: naming the list, no "Added to", not the on-prem no-op's |
| `ConferenceRoomDirectoryAuditTests.ASetADUserThatAppliedThenFailed_IsASuccess_RecordedAsConfirmed_NotAsWritten` (page, bulk) | the audit, both surfaces: Success, no `writtenDirectoryObject`, `attributesConfirmedPresent` opening `AttributesConfirmedAfterWriteError` with the timeout; the bulk row a Success whose message carries it |
| `ConferenceRoomDirectoryAuditTests.CityWritten_TheAuditNamesTheObjectWritten`, `ASetADUserThatFails_IsNotRecordedAsWritten` (page, bulk; changed) | no `attributesConfirmedPresent` for a clean write, nor for a failed one |
| `ConferenceRoomBulkProcessorTests.ARowsMessage_CarriesCityStateAndCountryTheReadBackConfirmed_WhenTheResultHasThem` (alone / with a membership) | the bulk message: the result's, then the attributes detail, then the membership detail |

**Mutations** (`scratchpad/a2q-round/round.ps1` with `rows-a2q.ps1`, log `round-a2q.log` beside them, 2026-10-22
13:07-13:14). Each row ran the 274 `ConferenceRoom*` cases (`FullyQualifiedName~ConferenceRoom`), the test project rebuilt each
time, the failures read from its TRX. md5 before = restored: `Services/ConferenceRoomService.cs` `57b6b4c8889d8b3ce4b52eb7c1766a30`
(M rows); `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs` `cfb781ba46dd0ecf06274fa63adda5fd` (H rows); both for D1. Each
restored file was touched, the next row rebuilding from it. Test names are in `ConferenceRoomWriteIdentityTests` unless
qualified (`Audit.` is `ConferenceRoomDirectoryAuditTests`, `Preflight.` `ConferenceRoomRoomListPreflightTests`).

| # | Mutation | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- |
| M1 | the brief's, at the call site: the routed preflight read dropped (`routeError = null`) - the derived server returned unproven | `9439989a5b2ae9b35f3520fb8b25a1ba` | `TheCityStateAndCountryPreflight_RefusesBeforeAnyWrite_...`; `TheCityStateAndCountryPreflight_ReadsTheObjectThrough...` | FAIL, 2 of 274: the unusable-server row passes the preflight and writes; one read before `Set-Place`, not two |
| M2 | the brief's, at the call site: the cloud success `BuildRoomListAddedMessage(list, viaOnPrem: false)` for the no-op too | `8241a71ff43f7d3375393fa9700ea5b0` | `Preflight.TheCloudAddsOwnMessage_...` (already there) | FAIL, 1: "Added to room list ..." for the no-op |
| M3 | the brief's, at the call site: the outer catch's log line and message as at `657a11be` ("Set-ADUser synced-attribute write failed for {Room}", "Failed to write City/State/Country on the AD object: <error>") | `cc87e4ec1b827e2a93173847263fea9d` | `AFailureAtEachStepOfTheCityStateAndCountryWrite_NamesThatStep` (runspace, import, object read) | FAIL, 3: no step named, the write's words for steps before it. PASS: the write and read-back rows, which do not reach the catch |
| M4 | the brief's, at the call site: no read-back after a thrown write error - "Failed to write City/State/Country on the AD object: <error>" returned at once, the new log line kept | `3f6838a6ef54acca8e2910dabaf46eb2` | `ASetADUserThatAppliedThenFailed_...`; `ASetADUserThatFailedWithoutApplying_...` (both); `Audit.ASetADUserThatAppliedThenFailed_...` (page, bulk) | FAIL, 5: the applied-then-failed row partial, in the service and on both audit surfaces; no read-back after the write in the failed-without-applying rows. PASS: the step test's write row - the write's own text unchanged |
| M5 | the write derives its server from its own read's DN again (as at `657a11be`), not the preflight's | `d804254cee792b36748464052aa2074c` | `TheWrite_GoesToTheServerThePreflightProved_...` | FAIL, 1: the write and the read-back sent to `fabrikam.test` |
| M6 | the confirmed path reports the object written (`(null, objectGuid, confirmed)`) | `92045f19997badf4aabbdd974154d174` | `ASetADUserThatAppliedThenFailed_...`; `Audit.ASetADUserThatAppliedThenFailed_...` (page, bulk) | FAIL, 3: `WrittenDirectoryObject` the objectGUID; `writtenDirectoryObject` on both surfaces |
| H1 | harness: `SetAdUserFailsAfterApplying` thrown before the `-Replace` is applied | `6447de72fb604ab3ab19bb2318af62b0` | the harness test; `ASetADUserThatAppliedThenFailed_...`; `Audit.ASetADUserThatAppliedThenFailed_...` (page, bulk) | FAIL, 4: nothing applied, so the read-back misses and each row is partial |
| H2 | harness: `ObjectReadFailureAfterSetPlace` without its after-`Set-Place` gate | `30e3177448ab325fba91382fb15fd3b4` | the harness test; `AFailureAtEachStep...` (object read) | FAIL, 2: the preflight's own read fails - refused, not partial |
| H3 | harness: `DirectoryServerFailures` ignored | `0d60b87d6e951d381555209271a48026` | the harness test; `TheCityStateAndCountryPreflight_RefusesBeforeAnyWrite_...` | FAIL, 2: the routed read answers, so the row is written |
| H4 | harness: `ObjectDnAfterSetPlace` ignored | `2839a1548d29cbeaf9aff27253bb22b2` | the harness test | FAIL, 1 |
| D1 | *demonstration:* M5 with H4 | service `d804254cee792b36748464052aa2074c`, harness `2839a1548d29cbeaf9aff27253bb22b2` | - | 1 failed, the harness test only: `TheWrite_GoesToTheServerThePreflightProved_...` PASSES with M5 when the DN knob does nothing - the wrong-reason pass only the harness test stops |

Every restored md5 equal to "before"; after the last row the restored tree rebuilt and the 274 `ConferenceRoom*` cases green.
No revert row: the tests name members this fix adds (`DirectoryAttributesConfirmed`, `AttributesConfirmedAfterWriteError`,
`AttributesConfirmedAuditKey`, `BuildRoomListAlreadyInMessage`, the harness knobs), so the service as at `657a11be` does not
compile against them; M1 to M4 are the four parts of that revert, each at its call site.

**Comments after the round.** The round ran on the fix with aud-a2p's now-wrong comments on the write (its server derived
there; a thrown error not read back) removed and none of the new explanatory comments; they were written afterwards and
checked by `scratchpad/a2q-round/comment_only.ps1` (blank, `//`, `///` and PowerShell `#` lines dropped from both versions,
the code lines identical in order): service `57b6b4c8889d8b3ce4b52eb7c1766a30` -> `fb3f4f168cfbcd81ffb0bbb445fb7b5a` (code
lines 1745 = 1745; comment and blank lines 810 -> 890); harness `cfb781ba46dd0ecf06274fa63adda5fd` ->
`d1e367fd4a4edeab47b2f128162bc74e` (653 = 653; 370 -> 410); `Services/Jobs/ConferenceRoomBulkProcessor.cs`
`d8b858143fa9d3cf6c08a37ca4a4dd34` -> `94418e8edca84a1a90976cce06ce7162` (174 = 174; 93 -> 95); `Models/ConferenceRoomModels.cs`
`eba529610148fbdf00469acc23d9beea` -> `d706172546d1c1199458f79917b1cb1a` (158 = 158; 56 -> 63). Unchanged since the round: the
four test files, whose comments were written with the tests (`ConferenceRoomWriteIdentityTests.cs`
`eaa7b1a7730f8046d9ea9a5c5373d5dd`, `ConferenceRoomDirectoryAuditTests.cs` `b719f3e918cd9dbd2ccd8eb0ab588ba2`,
`ConferenceRoomRoomListPreflightTests.cs` `5e66c8b9116dd5be5a262297ba276e96`, `ConferenceRoomBulkProcessorTests.cs`
`2cab41f14c48abffae416eeacbdbd55d`), and the page.

**Page:** three lines edited in place (1079, 1720, 1722); 1758 lines before and after; md5 `6607b944fae10efefca43e99674e19b3`
-> `6e585a343e4594619ea1e34298ae7466`; no `ClickGateRegistry` entry moved (its `ExpectedLineCount` 1758 holds).

## Known gaps

- *Assumption, aud-a2l's and aud-a2p's, carried, not probed:* a domain's DNS name in `-Server` reaches a domain controller
  holding the write; one DC is not pinned. The preflight's routed read proves that the object answers through that name,
  under the module credential, before `Set-Place` - not that the controller answering the write a moment later is the
  same one. One unreachable for the write after a good preflight read is the inherent two-system residual the
  preflight's header names: a partial, now settled by the read-back.
- The routed read proves the object readable there, not writable: a credential that may read the room but not write it
  passes the preflight and fails the write after `Set-Place` - the accepted residual, as before (the preflight never
  proved write permission, and a write cannot be tried without writing).
- *Documented AD module default, not probed (aud-a2p's):* the unrouted read finds the object only in the module's default
  domain, which its DN then names, so the routed read is normally the same domain by its DNS name. The case the fix
  closes is that name being unusable from this host while the default locator works.
- The explicit fallback - a DN naming no domain - is not expected live (every object DN in a domain naming context carries
  its DC components; `ServerFromDn` returns null only for none or a malformed one). It is logged and taken on the route
  the preflight read, never guessed.
- A write error with the values already in place before it - a re-run after an earlier run wrote them, or another writer -
  reads as confirmed after a write error: a success naming no written object, the error in the detail. That is aud-a2n's
  "whether this row's add put it there is not known", by the same rule; the desired values are in place and the audit
  says it is not known who put them there.
- *Assumption, aud-a2o's measurement, carried:* under `-ErrorAction Stop` the real `Set-ADUser`'s own error is thrown, not
  left as a record, so the write's error is only the thrown one; a record left past the preference is cleared unread by
  the `finally` and the read-back decides (aud-a2p's test pins that path).
- Not changed, in the same class, outside this record: the preflight's own outer catch still says "Failed to validate the
  AD object before writing: <error>" for any of its steps - unspecific, but true (nothing was written); and the
  `Import-Module` `HadErrors` branches (preflight, write, room-list paths) return the first error record with no step
  named - unreachable for the cmdlet's own error under `-ErrorAction Stop` (aud-a2o). Reported in the hand-back.
- Cost: one more `Get-ADUser` for each row that writes City/State/Country (the routed preflight read).
- The cloud add's messages are pinned by a direct call through aud-a2p's `internal` seam; nothing shows a success's today.

**Operator-visible changes.**

- A row writing City/State/Country for a room whose domain this host cannot use by its DNS name - or not under the module
  credential - is refused before anything is written, its one step naming the server, the room and the error; it used to
  come back partial after `Set-Place`. A warning log line goes with it.
- A `Set-ADUser` that committed and then reported an error is a success: the step `Set-ADUser (City/State/Country)` shows
  the detail ("City/State/Country confirmed in place after Set-ADUser reported an error (<error>): ... whether this row's
  write put them there is not known."), a bulk row's message carries it, the audit records it as
  `attributesConfirmedPresent` and names no `writtenDirectoryObject`, the success message (and so the admin notification)
  says "read back as set on-prem after Set-ADUser reported an error", and a warning is logged. It used to be a partial row.
- A `Set-ADUser` that failed without applying is the partial it was, its message "Failed to write City/State/Country on the
  AD object: <error>" now followed by what the read-back found, or that it failed too.
- A failure opening the directory runspace, importing the module, preparing the credential or reading the object - after
  `Set-Place` - says "City/State/Country were not written on the AD object <guid>: <step> failed: <error>" and logs the
  step; it used to say "Failed to write City/State/Country ..." and log "Set-ADUser synced-attribute write failed".
- The cloud add's no-op builds "Already in room list '<list>': nothing was written." (shown nowhere today).
- Nothing else: a clean write reads, writes and audits as before, with one more read before `Set-Place`.

**Dev acceptance owed** with aud-a2f, a2i, a2j, a2k, a2l, a2m, a2n, a2o and a2p: a Room Finder row with City/State/Country on
a synced room - expected a success, no preflight warning in the log (the routed read answered), the audit naming the
room's objectGUID. Not run here: no directory or Exchange call was made, and the owner's standing instruction is not to
probe Active Directory.

## Reviewer comments

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent on `worktree-agent-a69863ec13d81b5ba`.
Independent review pending.

For the reviewer:

- One fix commit for the four parts: one finding, this record's; the MEDIUM part first in the plan and the code.
- One deviation from the plan's wording, none from its scope: the object-read step is "reading the AD object", not
  "reading the AD object <guid>" - the outer catch's message names the object already.
- The routed preflight read is its own helper, `ReadObjectThrough`, not `ConfirmDirectoryObject` given a server: its failure
  is reported inside the preflight's refusal, which names the server, so it returns a bare reason rather than a row message
  of its own ending "Nothing was written.".
- The write's confirming read stays unrouted - aud-a2d's read, as it was - and its DN is no longer consulted: the preflight
  has proved both routes the write takes (its first read the default one, its second the routed one), and the write's
  server is the preflight's. Routing that read too was not needed by the finding. M5 and D1 show the carried server is
  pinned, and by what.
- "The write's own failure keeps its text" is read as: a failed write's message still opens "Failed to write
  City/State/Country on the AD object: <error>", with the read-back's finding after it.
- The outer catch's "were not written" holds because only the steps before the write's `Invoke` reach it: the write's
  error is held, the read-back's caught, and what follows both is string building and logging.
- Commit-then-throw follows aud-a2n: the read-back decides, a success after an error names no written object (aud-a2g),
  and the detail is on the step, in the bulk row's message and in the audit `extra` under a sibling key. Beyond the brief's
  list, the row's final success message no longer says "were written on-prem" in that case - part 1's class, a message
  claiming a write not known to have happened - and the admin notification carries that message.
- Outside the service and its tests: the model gains one property; the bulk processor and the page pass it to the audit
  (three page lines in place); the harness change is test-only and additive - six knobs, off by default - and its
  `Get-ADUser` and `Set-ADUser` now refuse a `-Server` the stand-in forest does not have, which no existing test passes.

## Closeout

Fixed on branch, not merged, not pushed: the plan `a37754c4`, then this fix, tests, comments and record in one commit. A
module-scoped behaviour change (the Conference Rooms City/State/Country preflight and write, the cloud add's message, the new
audit key): the Conference Rooms module version bump is due at merge - not made here, `Modules/ModuleCatalog.cs` being outside
this brief. No shared code changed (`GroupManagementService.ServerFromDn` reused as it is; the model, the bulk processor and
the page are the module's; the harness test-only), so no base app bump.

Gates, run on the final files this commit holds (the mutation round ran before the comments, which the comment-only check
shows changed no code line):

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings - the baseline count - none in a
  touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: 5320 passed, 0 failed, 3 skipped (the known AD
  skips, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`), 5323 total, 5 m 28 s; nothing hung, no Sequence
  file. This record's new cases are 21; the 316 `ClickGate*` cases are among those passed (and, run alone, 316 passed).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD`: clean.
- `tools/Test-AsciiOnly.ps1` after `git add -A`: clean.

PSScriptAnalyzer and Pester were not run: no `.ps1` or `.psm1` changed (the harness's stand-ins are PowerShell inside a C#
string, run by the tests).

## Folded in: the preflight does not prove the `-Server` the write uses (codex, a2o-a2p review, 2026-10-22) - MEDIUM

`Services/ConferenceRoomService.cs` around 1550 / 1608 / 1617 at `28ffde25`:
`PreflightSyncedAttributesViaAdAsync` reads the object with `ConfirmDirectoryObject` and no `-Server`;
the write (aud-a2p) then derives `ServerFromDn(roomDn)` and routes `Set-ADUser` there. If the
unrouted read succeeds but the DN-derived domain endpoint is unreachable from this host or unusable
under the module credential, the preflight passes, `Set-Place` commits, and the routed write fails:
an avoidable partial after the preflight that exists to prevent exactly that (aud-a2f). Fix: in the
preflight, after reading the object and its DN, derive the same server and perform an exact-object
read (by objectGUID) through it before returning success; if the DN has no domain components, make
the fallback explicit and preflight the same unrouted route, or refuse before `Set-Place`. Test: a
harness domain reachable without `-Server` but not by its DNS name makes the preflight refuse before
any Exchange write. Mutation at the call site: drop the routed preflight read. This part is MEDIUM;
the record's other three parts stay LOW.
