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
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; plan written, in progress
**Branch**: `worktree-agent-a69863ec13d81b5ba`
**Commit**: -
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

## Guard proof

Pending.

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
