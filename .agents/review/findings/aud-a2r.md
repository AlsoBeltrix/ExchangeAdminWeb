# aud-a2r: Conference Rooms - the preflight's outer catch names no step, Import-Module HadErrors branches are dead, a refusal says "Nothing was written" inside a partial, and the directory stand-ins do not route by domain

**Severity**: LOW - wording, dead code and a test gap; no wrong write. (1) `PreflightSyncedAttributesViaAdAsync`'s
outer catch reports "Failed to validate the AD object before writing: <error>" for any step with no
step named (the aud-a2q outer-catch class, on the preflight). (2) The `Import-Module` `HadErrors`
branches on the preflight, write and room-list paths return the first error record with no step
named; under `-ErrorAction Stop` they are effectively dead (the aud-a2o/a26g pattern). (3)
`ConfirmDirectoryObject`'s refusals end "Nothing was written." even when called from the write after
`Set-Place` committed, which is false inside the partial message. (4) Test gap: the harness stand-in
`Get-ADUser` and `Set-ADUser` answer on any server they can reach, so a wrong `-Server` is caught only
by pinning the parameter value, not by behaviour.
**Status**: Fixed on branch, not merged; guard proof below; independent review pending; dev acceptance owed
**Branch**: `worktree-agent-ae910e9c92bf1c2aa`
**Commit**: the fix commit after plan `17db13ed` (fix, tests, comments, record)
**Reviewer**: implementer (surfaced by the aud-a2q agent, 2026-10-22, its four candidates)

## Evidence

At the aud-a2q branch tip `df084c68`, `Services/ConferenceRoomService.cs`: the preflight's outer catch;
the `Import-Module` invocations followed by `if (ps.HadErrors)`; `ConfirmDirectoryObject`'s message
suffix; `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs` user stand-ins (compare the group stand-in
`Get-ADGroup`, which routes by `-Server` since aud-a2j).

## Approach (to be written by the implementing agent before code)

- Name the step in the preflight's catch as aud-a2q did for the write's catch.
- Remove the dead `Import-Module` `HadErrors` branches (try/catch/finally; the thrown error is the
  step's error, named), as aud-a2o/a26g did; or pin them with a non-terminating stand-in if a reason
  to keep them is found - say which.
- `ConfirmDirectoryObject` takes the suffix from its caller (the preflight appends "Nothing was
  written."; the write appends nothing), so the message is true where it appears.
- Harness: `Get-ADUser`/`Set-ADUser` stand-ins route by `-Server` as `Get-ADGroup` does (an object
  answers only on its own domain's name, the catalog, or no `-Server` for the default domain), with
  a harness self-test; re-run the aud-a2p/a2q routing tests to confirm they now fail on behaviour, not
  only on the pinned value.
- Mutations at the call site: collapse the preflight step naming; restore one `HadErrors` branch;
  restore the fixed suffix; make the stand-ins answer on any server again (harness self-test fails).

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("ALL the bugs includes ALL the bugs ... EVERYTHING", `.agents/decisions.md`
2026-10-21); the admitted approach above, the folded-in part at the end of this record and the supervising session's
brief. One finding, so one fix commit for its five parts. Base `d4c588b8`; the line numbers below are that tree's.

**Rule.** A failure names the step it came from; an import's error is the one it throws; a message says nothing was
written only where nothing was; a wrong `-Server` in a test fails by what the stand-in directory does with it, not only
by the value pinned; and the admin notification says what the row's audit says when a read-back confirmed a directory
write after an error.

1. **Preflight step naming.** `PreflightSyncedAttributesViaAdAsync` (`Services/ConferenceRoomService.cs` 1548-1625)
   keeps `step`, as the write does since aud-a2q - "opening the directory runspace", "importing the ActiveDirectory
   module", "preparing the module's AD credential", "reading the AD object" - set before each. Its outer catch
   (1619-1623: log "Set-ADUser preflight failed for {Room}", message "Failed to validate the AD object before writing:
   <error>") logs "Set-ADUser preflight for {Room} on the AD object {Object}: {Step} failed; nothing was written" with the
   exception and returns "City/State/Country could not be validated before writing on the AD object <guid>: <step>
   failed: <error, ended as a sentence> Nothing was written." The routed read (aud-a2q's `ReadObjectThrough`) keeps its
   own catch and refusal, which name the server; nothing after it reaches the outer catch.
2. **Import errors: the `HadErrors` branches removed, not pinned** (1578-1582, 1665-1669, 1831-1835, 1900-1904). This
   matches every other directory import in the app - `GroupManagementService` (119, 295, 403, 684),
   `ADAttributeEditorService`, `Comms10kService` - none of which checks `HadErrors` after it, and aud-a2o/a26g's
   treatment of the writes. Why dead, measured (`scratchpad/a2r-round/probe-import.ps1`, pwsh 7.6.6, an in-process
   runspace, no directory): a real `Import-Module` of a module that does not exist, under `-ErrorAction Stop`, throws
   `ActionPreferenceStopException` from `Invoke` and leaves 0 records, so the check after `Invoke` is never reached for
   its own error; without Stop it does not throw and leaves 1. The import is each runspace's first command, so no
   earlier record exists either. Kept, the branch could fire only on an error written past the preference - a module's
   load code with its own `-ErrorAction Continue` (*assumption, not probed: the ActiveDirectory module's import writes
   none*) - and would refuse a row whose module loaded, in no step's name. A new private static
   `ImportActiveDirectoryModule(ps)`: the import, `-ErrorAction Stop`, in `try { Invoke } finally { ps.Commands.Clear();
   ps.Streams.Error.Clear(); }` - the clear keeps a record the import left from being taken by the next read's own error
   check (`ConfirmDirectoryObject`'s `HadErrors`, psl-2) for that read's error. The thrown error is the import step's,
   named: the City/State/Country preflight and write by their outer catches (`step`); the room-list preflight and add,
   which keep no `step`, by a catch around the import - "Failed to validate the on-prem room list '<list>' before
   writing: importing the ActiveDirectory module failed: <error, ended as a sentence> Nothing was written." and "Failed
   to add the room to the on-prem room list '<list>': importing the ActiveDirectory module failed: <error>", each logged
   naming the step.
3. **The suffix from the caller.** `ConfirmDirectoryObject` (2051-2077) returns its three refusals without "Nothing was
   written." - the local shape of `ResolveAdGroupGuid` and `ReadOnPremListMembership`, whose callers add it. The two
   preflights, before any write, append it (`Sentence(<refusal>) + " Nothing was written."`; 1585-1587, 1841-1843); the
   City/State/Country write (1677-1680) and the room-list add (1910-1912), both after `Set-Place`, append nothing, so
   their partial ("Partial: ... were set in Exchange Online, but ... failed: <refusal> Re-run this row ...") no longer
   says nothing was written.
4. **Harness routing.** The stand-in `Get-ADUser` and `Set-ADUser` (`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`
   854-882) route by `-Server` through aud-a2j's `Get-StandInReach` and `Test-StandInReaches`, as `Get-ADGroup` does. An
   object lives in the forest domain its DistinguishedName ends with, or in the default domain when the DN names none
   of them (no DN, or no DC components - aud-a2q's fallback fixtures). It is found through that domain's DNS name, the
   catalog endpoint (a read: a `Set-ADUser` sent there is refused "The server is unwilling to process the request.", as
   the stand-in `Set-ADGroup` refuses one), or - default domain only - no `-Server` or a bare `:port`. Anywhere else:
   "Cannot find an object with identity", thrown by `Get-ADUser -Identity` and written then returned by `Set-ADUser`,
   as each one's not-found already is. `DirectoryServerFailures` and an unknown server are refused first, as now. The
   one existing assertion that pinned the old behaviour (aud-a2q's harness test: the default domain's room answering
   through `fabrikam.test`) becomes an object of `fabrikam.test` answering there while `contoso.test` fails - the
   failure still scoped to the server it names.
5. **Notification.** `Components/Pages/ConferenceRooms.razor`, three lines edited in place (1758 before and after; no
   `ClickGateRegistry` entry moves): the Finder handler's call (1080) passes `r.DirectoryAttributesConfirmed` and
   `r.DirectoryMembershipConfirmed`; `NotifyRoomAdminAsync` (1665) takes them as optional parameters; its details (1670)
   are `ConferenceRoomService.NotificationDetails(target, attributesConfirmed, membershipConfirmed)` - new, public
   static, pure: `Room`, then each detail present on a row of its own, `AttributesConfirmedNotificationRow`
   ("City/State/Country") and `MembershipConfirmedNotificationRow` ("Room list membership"), new public consts. The error
   field is untouched (`success ? null : errorDetail`). The rows follow the audit's rule - the page's `AuditFinderAction`
   records both details whatever the outcome - so a partial whose City/State/Country were confirmed before a later step
   failed carries the row beside its error. A clean success's notification, and a clean failure's, is `Room` only, as
   now. The other callers (Type, the catch) pass nothing. The bulk completion notice is a counts summary whose rows
   already carry the details (aud-a2o, aud-a2q): unchanged.
6. Comments after the mutation round, checked code-identical by a comment-only diff; aud-a2q's comment that "the admin
   notification carries this message" (652-653), untrue - the notification drops a success's message - corrected
   there.

**Tests** (through `ExchangeWriteHarness`; no directory, Exchange or network call).
- Harness, test-only and additive, every knob off by default: `DirectoryRunspaceFailure` and `ImportModuleFailure`
  (at any time - the preflight's steps, before `Set-Place`); `ImportModuleReportsErrorWithoutThrowing` (a message the
  stand-in `Import-Module` - now an advanced function, so `-ErrorAction Stop` acts on it as on the real cmdlet - writes
  with its own `-ErrorAction Continue`, then carries on); `ObjectReadAnswersAs` and `ObjectReadAnswersAsAfterSetPlace`
  (the objectGUID a `Get-ADUser -Identity` without `-Properties` - the read that proves the object - answers with, at any
  time or only after a `Set-Place`). The routing of part 4.
- `ConferenceRoomWriteIdentityTests`:
  - each preflight step failing (runspace, import, object read through `Failures['Get-ADUser']`): refused, not partial,
    `Writes` empty, the one step `Set-ADUser preflight (City/State/Country)` failed with the message as its `Error`,
    naming the step, carrying the injected text, ending "Nothing was written."; a logged error naming the step;
  - the import failing on the room-list preflight and on the add: the step named in the message and the log; refused
    before any write, or partial;
  - an error the import reports without a throw, on all four directory paths in one row (City/State/Country and a synced
    list): success, not partial, the room's object written, both directory writes made, the text in no message or step;
  - `ConfirmDirectoryObject` refusing (another object answering) on each of the four paths: a preflight refused, ending
    "Nothing was written."; a write partial, the refusal in its step and the message, "Nothing was written" nowhere;
  - harness: the routing self-test - an object of each domain answering and written only where part 4 says, the catalog
    answering a read and refusing a write, no DN or no DC components the default domain; each knob above pinned; and a
    real `Import-Module` of a module that does not exist, in a default runspace, under `-ErrorAction Stop`, throwing and
    leaving no record - the measurement of part 2 on this SDK (7.6.1).
- `ConferenceRoomDirectoryAuditTests`, the page, its email substitute kept: the admin notification of a clean success
  (both directory writes clean) - `Room` only, no error; City/State/Country confirmed after an error - `Room` and the
  row equal to the audit's `attributesConfirmedPresent`, no error; membership confirmed - its row equal to
  `membershipConfirmedPresent`; both; a partial whose City/State/Country were confirmed before the timezone failed - the
  row beside its error.

**Routing re-run.** The aud-a2p routing mutations (its M5, the read-back without `-Server`; its M6, the write without it)
and aud-a2q's (its M1, the routed preflight read dropped; its M5, the write deriving its server from its own read's DN)
re-applied at their call sites on this tree; each reported by the assertion that now fails - behaviour (the row's outcome)
or only the `-Server` pin.

**Mutations, at the call sites** (md5 before and after, the named failing tests, restored, md5 matched, touched, rebuilt,
green): M1 the preflight's step naming collapsed to its old catch; M2a-d one `HadErrors` branch restored, at each of the
four imports in turn; M2e the import's `finally` without its error-stream clear; M3 the write's caller restoring the fixed
suffix (M3b the room-list add's; M3c the preflights' suffix dropped); M4 the stand-ins answering on any server again; M5
the notification's details without the confirmed rows; harness rows for the knobs the tests rest on.

**Files.** `Services/ConferenceRoomService.cs`, `Components/Pages/ConferenceRooms.razor` (three lines in place),
`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`, `ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs`,
`ExchangeAdminWeb.Tests/ConferenceRoomDirectoryAuditTests.cs`, this record.

## Approach (as implemented)

- `PreflightSyncedAttributesViaAdAsync` (`Services/ConferenceRoomService.cs`): `step` - "opening the directory runspace",
  "importing the ActiveDirectory module", "preparing the module's AD credential", "reading the AD object" - set before
  each; the outer catch logs `"Set-ADUser preflight for {Room} on the AD object {Object}: {Step} failed; nothing was
  written"` with the exception and returns "City/State/Country could not be validated before writing on the AD object
  <guid>: <step> failed: <error, ended as a sentence> Nothing was written."; the caller's refusal is unchanged in shape
  (not partial, one failed step `Set-ADUser preflight (City/State/Country)`, nothing written). Its `ConfirmDirectoryObject`
  refusal is returned as `Sentence(<refusal>) + " Nothing was written."`.
- `ImportActiveDirectoryModule(ps)`, new, private static: `Import-Module -Name ActiveDirectory -ErrorAction Stop` in
  `try { ps.Invoke(); } finally { ps.Commands.Clear(); ps.Streams.Error.Clear(); }`. The four directory paths call it in
  place of their import and its `if (ps.HadErrors) return (FirstError(ps) ?? "Failed to load the ActiveDirectory
  module.", ...)`: the City/State/Country preflight and write under their `step`; the room-list preflight and add inside
  a catch that logs the step and returns "Failed to validate the on-prem room list '<list>' before writing: importing the
  ActiveDirectory module failed: <error, ended as a sentence> Nothing was written." and "Failed to add the room to the
  on-prem room list '<list>': importing the ActiveDirectory module failed: <error>".
- `ConfirmDirectoryObject`: its two refusals end at their own sentence - "The AD object validated for '<room>' (<guid>)
  could not be read (<n> found)." and "Active Directory answered <guid> with a different object (<guid, or no
  ObjectGUID>)." - with no "Nothing was written."; its error-record branch is unchanged. The room-list preflight appends
  " Nothing was written." as the City/State/Country preflight does; the City/State/Country write and the room-list add
  return the refusal as it is, inside their partial.
- `NotificationDetails(room, attributesConfirmed, membershipConfirmed)`, new, public static, pure: `Room`, then
  `AttributesConfirmedNotificationRow` ("City/State/Country") and `MembershipConfirmedNotificationRow` ("Room list
  membership"), new public consts, each only when its detail is given.
- `Components/Pages/ConferenceRooms.razor`, three lines edited in place: the Finder handler's notification passes
  `r.DirectoryAttributesConfirmed` and `r.DirectoryMembershipConfirmed` (1080); `NotifyRoomAdminAsync` takes them as
  optional parameters (1665); its details are `ConferenceRoomService.NotificationDetails(target, attributesConfirmed,
  membershipConfirmed)` (1671 - the plan's 1670 is the line before it). The error argument is untouched.
- Harness (`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`), test-only and additive, every knob off by default:
  `DirectoryRunspaceFailure` (`OpenDirectoryRunspace` throws it at any time), `ImportModuleFailure`,
  `ImportModuleReportsErrorWithoutThrowing`, `ObjectReadAnswersAs`, `ObjectReadAnswersAsAfterSetPlace`. The stand-in
  `Import-Module` is an advanced function, `[CmdletBinding()] param($Name)`: it declared `$ErrorAction` as a plain
  parameter, so the caller's `-ErrorAction Stop` did not act on it. `ImportModuleFailure` is `Write-Error` then return -
  thrown only by the caller's Stop, as the real cmdlet reports a module it cannot load;
  `ImportModuleReportsErrorWithoutThrowing` is `Write-Error -ErrorAction Continue`, then carries on. Routing:
  `Get-StandInObjectDomain` (the forest domain
  a `DirectoryObjects` entry's DN ends with, else the default one) and `Test-StandInObjectReached` (aud-a2j's
  `Test-StandInReaches` of the call's `Get-StandInReach`, for that domain). `Get-ADUser -Identity` throws "Cannot find an
  object with identity" for an object the call does not reach, as for one it does not hold; `Set-ADUser` refuses one
  (Write-Error then return), and refuses the catalog ("The server is unwilling to process the request."); both after
  `Failures`, `DirectoryServerFailures` and an unknown server, as before. A `Get-ADUser -Identity` without `-Properties`
  answers `ObjectReadAnswersAs` - or, once a `Set-Place` is recorded, `ObjectReadAnswersAsAfterSetPlace` - as its
  ObjectGUID.
- Tests: new - `ConferenceRoomWriteIdentityTests.AFailureAtEachStepOfTheCityStateAndCountryPreflight_NamesThatStep`
  (runspace, import, object read), `AFailedImportOnTheOnPremRoomListPaths_NamesThatStep` (preflight, add),
  `AnErrorTheImportReportsWithoutThrowing_IsNotTakenForAnyStepsError`,
  `TheObjectReadsRefusal_SaysNothingWasWritten_OnlyWhereNothingWas` (City/State/Country and room list, each preflight and
  write), `TheHarness_ADUserStandIns_FindAnObjectOnlyInADomainTheirServerReaches`,
  `TheHarness_ImportAndObjectReadKnobs_DoWhatTheTestsAboveRestOn`,
  `TheRealImportModule_ThrowsItsOwnErrorUnderStop_AndLeavesNoRecord`;
  `ConferenceRoomDirectoryAuditTests.TheAdminNotification_CarriesWhatTheReadBackConfirmedAfterAnError_OnARowOfItsOwn`
  (clean, attributes, membership, both, partial). 18 new cases. Changed - aud-a2q's
  `TheHarness_ADUserStandIns_RefuseAServerTheyCannotUse_AndFailAfterSetPlace_OrAfterApplying`: its read "to the forest's
  other domain, unaffected" is of an object of that domain (`OtherDomainObject`), no longer the default domain's room,
  which the routing now refuses there - the one existing assertion this record changes; `ConferenceRoomDirectoryAuditTests`
  keeps the page's email substitute. On the fix the 292 `ConferenceRoom*` cases passed, the 274 existing among them.

## Guard proof

| Test | What it pins |
| --- | --- |
| `AFailureAtEachStepOfTheCityStateAndCountryPreflight_NamesThatStep` (runspace, import, object read) | part 1: each preflight step failing before `Set-Place` - refused, not partial, `Writes` empty, the one step `Set-ADUser preflight (City/State/Country)` with the message as its `Error`; the message names the step ("<step> failed: "), carries the injected text and the objectGUID and ends "Nothing was written."; a logged error names the step |
| `AFailedImportOnTheOnPremRoomListPaths_NamesThatStep` (preflight, add) | part 2's naming where no `step` is kept: the import's thrown error told as "importing the ActiveDirectory module failed: <error>", naming the list, logged so; the preflight refused, not partial, nothing written, ending "Nothing was written."; the add partial after `Set-Place`, without it; no `Set-ADGroup` |
| `AnErrorTheImportReportsWithoutThrowing_IsNotTakenForAnyStepsError` | part 2, the chosen treatment: an error each import reports without a throw, on the four directory paths of one row (the City/State/Country preflight and write, the synced list's preflight and add) - success, not partial, the room's object written, one `Set-ADUser` and one `Set-ADGroup`, the text in no message or step |
| `TheObjectReadsRefusal_SaysNothingWasWritten_OnlyWhereNothingWas` (City/State/Country and room list; preflight and write) | part 3: `ConfirmDirectoryObject`'s "answered ... with a different object" on each path - a preflight refused, not partial, nothing written, ending "<refusal> Nothing was written."; a write after `Set-Place` partial, the refusal in the failed step and the message, "Nothing was written" nowhere, the write not made |
| `TheHarness_ADUserStandIns_FindAnObjectOnlyInADomainTheirServerReaches` | part 4: the default domain's room - the object read and the read-back alike - found with no `-Server`, by its DNS name, through the catalog and a bare port, not through `fabrikam.test`; `fabrikam.test`'s object through its name and the catalog only; written only where found, a write through the other domain or the catalog ("unwilling to process") applying nothing; an object with no DN or none naming a domain in the default domain |
| `TheHarness_ImportAndObjectReadKnobs_DoWhatTheTestsAboveRestOn` | the knobs, so the tests above cannot pass for the wrong reason: under Stop the import's reported error not thrown, `HadErrors`, one record; its failure thrown leaving no record, and without Stop a record, not thrown; the runspace knob; `ObjectReadAnswersAs` on the object read and not the read-back, its twin only after a `Set-Place` |
| `TheRealImportModule_ThrowsItsOwnErrorUnderStop_AndLeavesNoRecord` | the measurement part 2 rests on, on the tests' SDK (7.6.1; the probe was pwsh 7.6.6): the real `Import-Module`'s own error thrown under Stop, no record left; without Stop a record, not thrown |
| `ConferenceRoomDirectoryAuditTests.TheAdminNotification_CarriesWhatTheReadBackConfirmedAfterAnError_OnARowOfItsOwn` (clean, attributes, membership, both, partial) | part 5, the real page: clean (both directory writes clean) - the rows are `Room` alone, no error; attributes, membership, both - `Room` and each confirmed row, in that order, equal to the audit's `attributesConfirmedPresent` / `membershipConfirmedPresent`, success, no error; partial (City/State/Country confirmed, then the timezone failed) - the row beside the error ("Partial: ..."), which does not carry the detail |
| `TheHarness_ADUserStandIns_RefuseAServerTheyCannotUse_AndFailAfterSetPlace_OrAfterApplying` (aud-a2q's; one assertion changed) | its read "to the forest's other domain, unaffected" by `DirectoryServerFailures[contoso.test]` is now of an object of that domain, found there |

**Mutations** (`scratchpad/a2r-round/round.ps1`, the a2q runner extended to back up a file once per row and log each
failure's first assertion text, with `rows-a2r.ps1`; log `round-a2r.log` beside them; 2026-10-22 14:12-14:28). Each row ran
the 292 `ConferenceRoom*` cases, the test project rebuilt each time, the failures read from its TRX. md5 before = restored:
`Services/ConferenceRoomService.cs` `84e4ec5e19ef03457fe897ffb6c104b7` (M, R rows); `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`
`67806bcff5d569c011a81d26d8586db8` (M4, H rows); `Components/Pages/ConferenceRooms.razor` `9d485ecad3ce6c868c5ac707aa06a07d`
(M5); both of the first two for D. Each restored file was touched, the next row rebuilding from it. Test names are in
`ConferenceRoomWriteIdentityTests` unless qualified (`Audit.` is `ConferenceRoomDirectoryAuditTests`).

| # | Mutation | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- |
| M1 | the brief's (1), at the call site: the preflight's catch as at `d4c588b8` (log "Set-ADUser preflight failed for {Room}", message "Failed to validate the AD object before writing: <error>") | `ebb0fc1b7a4ac0b23bc43b5897d234cd` | `AFailureAtEachStepOfTheCityStateAndCountryPreflight_NamesThatStep` (runspace, import, object read) | FAIL, 3 of 292: no step named, no "Nothing was written." |
| M2a | the brief's (2), at the call site: the City/State/Country preflight's import as at `d4c588b8`, its `HadErrors` branch restored | `9fc342fe9c7310fa715b822b7fa5ae66` | `AnErrorTheImportReportsWithoutThrowing_IsNotTakenForAnyStepsError` | FAIL, 1: the reported text taken for the import's error, the row refused |
| M2b | the same at the City/State/Country write's import | `805f8b507e02007881d29c19a8fdd351` | the same | FAIL, 1: partial after `Set-Place` |
| M2c | the same at the room-list preflight's import | `bb171b9782210520cfc8c0d9c9fc50bc` | the same; `AFailedImportOnTheOnPremRoomListPaths_NamesThatStep` (preflight) | FAIL, 2: refused with the reported text; the thrown import error unnamed |
| M2d | the same at the room-list add's import | `9b315213878664c361481c976c9e94c5` | the same; `AFailedImportOnTheOnPremRoomListPaths_NamesThatStep` (add) | FAIL, 2 |
| M2e | the import's `finally` without its error-stream clear (the commands still cleared) | `6cf3f78482bb75f602f3be5ce3192cd3` | `AnErrorTheImportReportsWithoutThrowing_...` | FAIL, 1: the record the import left fails the object read's own error check, which returns the import's text as the read's (psl-2) |
| M2f | `-ErrorAction Stop` dropped from the import | `b5888b257524ed103149e74359f7e78b` | `AFailureAtEachStepOfTheCityStateAndCountryPreflight_NamesThatStep` (import); `AFailedImportOnTheOnPremRoomListPaths_NamesThatStep` (preflight) | FAIL, 2: the module's failure a record, cleared unread, and the row goes on past it. PASS: the add case and aud-a2q's write-import case, whose knob (`ImportModuleFailureAfterSetPlace`) throws whatever the preference |
| M3 | the brief's (3): the fixed suffix restored in `ConfirmDirectoryObject`'s two refusals, as at `d4c588b8`, the callers as fixed | `5a305cbc669e44baddee0ce9bbc1b899` | `TheObjectReadsRefusal_SaysNothingWasWritten_OnlyWhereNothingWas` (all four) | FAIL, 4: each write's partial says "Nothing was written."; each preflight says it twice |
| M3b | (3) at the City/State/Country write's call site: the preflight's suffix appended after `Set-Place` | `0429fb1a0c5884196f4e857b3eabb4d3` | the same (City/State/Country, write) | FAIL, 1 |
| M3c | (3) at the room-list add's call site: the same | `0b0413a123e5ad978439da768aae4c87` | the same (room list, add) | FAIL, 1 |
| M3d | (3) at the two preflights' call sites: their suffix dropped | `65cd906b747781e7db16ef37ea498553` | the same (both preflights) | FAIL, 2 |
| M4 | the brief's (4): the stand-in `Get-ADUser` and `Set-ADUser` answer on any server again (the routing and the catalog refusal removed) | harness `11f3d35eccc7e9a97bebf8a73cf8e43f` | `TheHarness_ADUserStandIns_FindAnObjectOnlyInADomainTheirServerReaches` | FAIL, 1: "No exception was thrown" - the default domain's room found through `fabrikam.test` |
| M5 | the brief's (5), at the call site: the Finder handler's notification without the confirmed details (line 1080 as at `d4c588b8`) | page `507c63461806b4b4e262593a7ef7ef46` | `Audit.TheAdminNotification_...` (attributes, membership, both, partial) | FAIL, 4: the room alone. PASS: clean - its notification unchanged |
| H1 | harness: `ImportModuleReportsErrorWithoutThrowing` ignored | `2de06fbe2ec4842a1a4d8ec22223985d` | the knobs self-test | FAIL, 1: `HadErrors` false. *Demonstration:* `AnErrorTheImportReportsWithoutThrowing_...` PASSES - nothing reported, nothing pinned - the wrong-reason pass only the self-test stops |
| H2 | harness: `ObjectReadAnswersAs` and its twin ignored | `dc3326e2519244e5711811b4a3aea0ca` | the knobs self-test; `TheObjectReadsRefusal_...` (all four) | FAIL, 5: no refusal, each row a success |
| H3 | harness: `ImportModuleFailure` ignored | `e32043bcb49faa8b50d4093c34cade09` | the knobs self-test; `AFailureAtEach...Preflight_...` (import); `AFailedImport..._...` (preflight) | FAIL, 3 |
| H4 | harness: `DirectoryRunspaceFailure` ignored | `499cd06ae6a3a2c849b4cf04aa2c0935` | the knobs self-test; `AFailureAtEach...Preflight_...` (runspace) | FAIL, 2 |

**Routing re-run** (the same round). The aud-a2p and aud-a2q routing mutations re-applied at their call sites on this
tree, each read by the first assertion its failing tests now fail.

| # | Mutation | md5 mutated | Failing tests: first failed assertion | Fails on |
| --- | --- | --- | --- | --- |
| R1 | aud-a2p's M6: the write without its `-Server` | `2e01c7077a2527520bd921766ecb19a1` | `TheCityStateAndCountryWrite_IsConfirmedByReadingItsAttributesBack_FromTheServerItWroteTo`, `TheCityStateAndCountryPreflight_ReadsTheObjectThroughTheServerTheWriteUses_AndTheWriteGoesThere`, `TheWrite_GoesToTheServerThePreflightProved_NotOneItDerivesAgain`: "Expected: contoso.test Actual: null" | the pin only; each row succeeds |
| R2 | aud-a2p's M5: the read-back without the write's `-Server` | `f3c9b6b0aaf2ab0fad7c6bfeb6c06ec2` | the same three, the same assertion on the read-back | the pin only |
| R3 | aud-a2q's M5: the write derives its server from its own read's DN again | `7f521cdb4ee57353da5e99226bb840b6` | `TheWrite_GoesToTheServerThePreflightProved_NotOneItDerivesAgain`: `Assert.True(result.Success)` - "Partial: ... City/State/Country failed: Failed to write City/State/Country on the AD object: ..." | behaviour: the write sent to `fabrikam.test` finds no object there. At aud-a2q it failed on the pin only |
| R4 | aud-a2q's M1: the routed preflight read dropped | `98b8b605743c9c406e7dd0bfb27ae782` | `TheCityStateAndCountryPreflight_RefusesBeforeAnyWrite_...`: `Assert.False(result.Success)`; `..._ReadsTheObjectThrough...`: "Expected: 2 Actual: 1" reads before `Set-Place` | behaviour (through `DirectoryServerFailures`, as at aud-a2q), and a count |
| D | *demonstration:* R3 with M4 | service `7f521cdb4ee57353da5e99226bb840b6`, harness `11f3d35eccc7e9a97bebf8a73cf8e43f` | the routing self-test; `TheWrite_GoesToTheServerThePreflightProved_...`: "Expected: contoso.test Actual: fabrikam.test" | the pin only: without the routing R3 is aud-a2q's pin-only failure again - the behaviour failure is the routing's doing |

So the aud-a2q write-routing test now fails on behaviour; the aud-a2p ones cannot, by construction rather than by a gap
in the harness: no `-Server` reaches the default domain - in the stand-in as with the real module (*documented module
default, not probed*) - and the room the unrouted preflight read can find lives there, so a write or read-back that drops
its `-Server` reaches the same object. Only the pin tells them apart, and it stays.

Every restored md5 equal to "before"; after the last row the restored tree rebuilt and the 292 `ConferenceRoom*` cases
green. In the log: R2's first attempt stopped before mutating - its old text also matched the membership read's routing
lines - the file restored to "before" unchanged; one resume ran no row (`-Only` reached the runner as one string, now
split); R2 onward then ran with its text anchored. No revert row: the tests name members this fix adds
(`NotificationDetails`'s row constants, the harness knobs), so the service as at `d4c588b8` does not compile against them;
M1, M2a-d, M3, M4 and M5 are the five parts of that revert, each at its call site.

**Comments after the round.** The round ran on the fix with none of the new explanatory comments; the one harness comment
the routing made false ("otherwise do not route by it ... the tests pin what is passed") was cut to a clause before it.
They were written afterwards and checked by `scratchpad/a2r-round/comment_only.ps1` (aud-a2q's checker: blank, `//`, `///`
and PowerShell `#` lines dropped from both versions, the code lines identical in order): service
`84e4ec5e19ef03457fe897ffb6c104b7` -> `bf67aebfd2dbb1048ff08f92a39a0d6d` (code lines 1773 = 1773; comment and blank lines
894 -> 945); harness `67806bcff5d569c011a81d26d8586db8` -> `a69238529c47d6098dd88cf75f68de8a` (679 = 679; 431 -> 444). Among
them aud-a2q's untrue "the admin notification carries this message", corrected. Unchanged since the round: the page and the
two test files, whose comments were written with the tests (`ConferenceRoomWriteIdentityTests.cs`
`9026f1b7261ac598622c8a29f3339a31`, `ConferenceRoomDirectoryAuditTests.cs` `e83b31dc7afc0ce8757948f1edd1b34c`).

**Page:** three lines edited in place (1080, 1665, 1671); 1758 lines before and after; md5 `6e585a343e4594619ea1e34298ae7466`
-> `9d485ecad3ce6c868c5ac707aa06a07d`; no `ClickGateRegistry` entry moved (its `ExpectedLineCount` 1758 holds).

## Known gaps

- *Assumption, not probed:* the real ActiveDirectory module's import writes no error past `-ErrorAction Stop`. Were one
  written it is now cleared unread and the row goes on; a module that did not in fact load fails at the next step (its
  cmdlet not found), named as that step - the object read - not as the import. Fail-closed either way.
- Measured by the probe, harmless here: `PowerShell.HadErrors` reads true after an `Invoke` that threw, with no record in
  the stream. Nothing here reads it after a caught throw without a fresh `Invoke`.
- The stand-in routing models the documented module default (*not probed*): no `-Server` reaches the module's default
  domain only. A room of another domain is refused by the preflight's first, unrouted read - aud-a2p's and aud-a2q's known
  gap, unchanged - and the stand-ins now refuse it the same way.
- The stand-ins take a bare `:3268` for the default domain for a write too (`Set-ADGroup`'s rule since aud-a2j); a real
  write through the catalog port would be refused. No code sends one.
- Not changed, in the same class, outside this record: the room-list paths' outer catches name no step for anything but
  the import - the runspace, the room's object read, the group's resolution and re-read, the membership read: "Failed to
  validate the on-prem room list '<list>' before writing: <error> Nothing was written." and "Failed to add the room to the
  on-prem room list on the AD object: <error>", the add logging "On-prem room-list member add (Set-ADGroup on the group's
  member attribute) failed" for any of them - aud-a2q's outer-catch class on these paths. Reported in the hand-back.
- The notification rows carry the detail's full text - the error, the object or group, the server - as the step and the
  audit do: long.
- The bulk completion notice stays a counts summary; a bulk row's message carries the details (aud-a2o, aud-a2q).

**Operator-visible changes.**

- A City/State/Country preflight step failing - the runspace, the module import, the credential, the object read - says
  "City/State/Country could not be validated before writing on the AD object <guid>: <step> failed: <error> Nothing was
  written." and logs the step; it said "Failed to validate the AD object before writing: <error>".
- The module import failing on the room-list preflight or add says "... importing the ActiveDirectory module failed:
  <error>" and is logged so; the add's log no longer names Set-ADGroup for it.
- The object read's refusal after `Set-Place` - in the City/State/Country write or the room-list add - no longer says
  "Nothing was written." inside the partial; a preflight's still does, its error-record form now included.
- An error the module import reports without throwing no longer fails the row - no outcome the real cmdlet is known to
  produce changes.
- The admin notification of a Room Finder apply whose City/State/Country or room-list membership the read-back confirmed
  after the write reported an error has a "City/State/Country" or "Room list membership" row carrying the detail the audit
  records, beside the error for a partial. A clean apply's notification is as before.
- Nothing else.

**Dev acceptance owed** with aud-a2f, a2i, a2j, a2k, a2l, a2m, a2n, a2o, a2p and a2q: a Room Finder row with
City/State/Country on a synced room, on a synced list - expected a success, its admin notification with the `Room` row only.
Not run here: no directory, Exchange or SMTP call was made, and the owner's standing instruction is not to probe Active
Directory.

## Reviewer comments

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent on `worktree-agent-ae910e9c92bf1c2aa`.
Independent review pending.

For the reviewer:

- One fix commit for the five parts: one finding, this record's.
- The dead branches are removed, not pinned: every other directory import in the app has none, and the probe and
  `TheRealImportModule_...` measure why it was dead. `ImportActiveDirectoryModule` is one helper, not four inline blocks:
  one place for that why. The brief's "try/catch/finally" is the helper's try/finally with the caller's catch, which names
  the step - the `step` catch on the City/State/Country paths, a catch of its own on the room-list paths, which keep no
  `step`.
- `ImportModuleFailure` is non-terminating, the real cmdlet's shape, so dropping `-ErrorAction Stop` from the import bites
  (M2f); aud-a2q's `ImportModuleFailureAfterSetPlace` throws, so its import test does not see the preference.
- `ConfirmDirectoryObject`'s suffix comes from the caller by the callers appending it, `ResolveAdGroupGuid`'s shape, not by a
  parameter.
- Beyond the brief's "success notification": the rows follow the audit's rule and are present whatever the outcome, so a
  partial whose City/State/Country were confirmed carries the row beside its error - pinned by the partial case.
- One deviation from the plan's wording, none from its scope: the details line is 1671, not 1670.
- One existing assertion changed - aud-a2q's harness read through the other domain - because the routing it predates now
  refuses that read; the stand-in `Import-Module` became an advanced function. Every other existing Conference Rooms test
  passed unchanged.
- Outside the service and its tests: the page (three lines in place) and the harness (test-only, additive: five knobs off by
  default, and the routing).

## Closeout

Fixed on branch, not merged, not pushed: the plan `17db13ed`, then this fix, tests, comments and record in one commit. A
module-scoped behaviour change (Conference Rooms: the preflight's message, the import's handling, the refusal's wording, the
admin notification's rows): the Conference Rooms module version bump is due at merge - not made here,
`Modules/ModuleCatalog.cs` being outside this brief. No shared code changed (`EmailService` is unchanged, the page and the
service are the module's, the harness test-only), so no base app bump.

Gates, run on the final files this commit holds (the mutation round ran before the comments, which the comment-only check
shows changed no code line):

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings - the baseline count - none in a
  touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: 5384 passed, 0 failed, 3 skipped (the known AD
  skips, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`), 5387 total, 5 m 15 s; nothing hung, no Sequence
  file. This record's new cases are 18; the 316 `ClickGate*` cases are among those passed (and, run alone, 316 passed).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD`: clean.
- `tools/Test-AsciiOnly.ps1` after `git add -A`: clean.

PSScriptAnalyzer and Pester were not run: no `.ps1` or `.psm1` changed (the harness's stand-ins are PowerShell inside a C#
string, run by the tests; the scratch runner and probe are not in the repository).

Base `d4c588b8`. The remote master has since moved to `cc4f9c5a` - nine commits of the settings work (aud-a28j, aud-a28k,
aud-a26h) - touching none of this record's files; the totals above are this branch's.

## Folded in: the success-after-error detail is dropped from the admin notification (codex, a2q review, 2026-10-22)

`Components/Pages/ConferenceRooms.razor` around line 1669: `NotifyRoomAdminAsync` passes
`success ? null : errorDetail` to `EmailService`, so when `Set-ADUser` applied City/State/Country, then
threw, and the read-back confirmed the values (aud-a2q), the page, result and audit say so
(`attributesConfirmedPresent`, no `writtenDirectoryObject`) but the admin notification is a generic
SUCCESS email with the room only. Carry the `DirectoryAttributesConfirmed` (and the aud-a2n
`DirectoryMembershipConfirmed`) detail into the success notification as its own line, not through the
error field; the page has line-keyed `ClickGateRegistry` entries, so edit in place. Test: the
confirmed-after-error case's notification names the condition; a clean success's does not. Mutation
at the call site: drop the detail from the notification.
