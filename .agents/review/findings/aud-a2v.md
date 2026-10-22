# aud-a2v: Conference Rooms - the row's own early refusals are not logged, a credential failure names no room, an operator message carries a .NET parameter suffix, and refusal wording is inconsistent

**Severity**: LOW - observability and wording; no wrong write. (1) `SetRoomMetadataAndListAsync`'s own
early refusals (no validated room, an invalid time zone, a recipient that is not a room mailbox) are
returned and logged nowhere by the service (the aud-a2t/a2u class on the row's entry). (2) When the
credential lookup fails inside Delinea (an incomplete secret or an HTTP error), the log never names
the room or row; only `ModuleCredentialService`'s missing-secret-ID line carries the purpose. (3) The
unmappable-country message shown to operators ends with .NET's " (Parameter 'countryOrRegion')"
suffix (`ArgumentException.Message`). (4) The City/State/Country preflight's early refusals do not end
"Nothing was written." while the room-list preflight's do; both are true, the operator sees two
styles.
**Status**: Merged `6ac190f7`; closed on guard proof; codex review pending
**Branch**: `worktree-agent-a3708734379b4119d`
**Commit**: `1d6d2c2d` (plan), `937c2599` (fix, tests, comments, record); merged `6ac190f7`
**Reviewer**: implementer (surfaced by the aud-a2u agent, 2026-10-22, its four candidates)

## Evidence

At `bf7ba5b4`, `Services/ConferenceRoomService.cs`: `SetRoomMetadataAndListAsync`'s early returns;
the credential step's catch (what it logs); `BuildSyncedUserAttributes`' country mapping throw and
the message that reaches the row; the two preflights' refusal suffixes.

## Approach (to be written by the implementing agent before code)

- Log the row's early refusals once at Warning naming the room and the reason (aud-a2u placement).
- The credential step's failure log names the room (and the list where there is one) beside the
  purpose; the Delinea-side log stays as it is.
- The country mapping throws with a message that has no parameter suffix (or the caller takes
  `ArgumentException.Message` without it); the operator sees the country text and that it could not
  be mapped.
- One suffix rule for both preflights: every refusal decided before any write ends "Nothing was
  written."; the tests pin the City/State/Country preflight's early refusals end so.
- Tests through `ExchangeWriteHarness` with the capturing logger; mutations at the call site per part.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("ALL the bugs includes ALL the bugs ... EVERYTHING", `.agents/decisions.md`
2026-10-21); the admitted approach above and the supervising session's brief. One finding, so one fix commit for its four
parts. Base `dc0555e2`, which both remotes hold; the line numbers below are that tree's (`Services/ConferenceRoomService.cs`
unless named).

**Rule.** (1) A refusal the Room Finder row returns before its preflights reaches the application log once, at Warning, naming
the room and the reason, where the row refuses (aud-a2t's placement). (2) A credential lookup that returns none, on each of the
four directory paths, is logged once, at Warning, where that path refuses: the path's own refusal line, naming the room, the
list where there is one, and the purpose the credential service was asked with. (3) The country mapping's refusals carry no
.NET parameter suffix. (4) Every refusal either preflight decides before any write ends "Nothing was written.". Otherwise every
outcome, message, step, audit and notification is unchanged.

1. **The row's own refusals.** `SetRoomMetadataAndListAsync` (470): no validated room (477-482, `ValidatedMailbox.Refusal`), an
   invalid time zone (484-493), the recipient refused (495-501: not a room mailbox, or not found). Each sets its message as now,
   then `_logger.LogWarning("Room Finder apply for {Room} refused the row; nothing was written: {Refusal}", roomEmail,
   result.Message)`, then returns. True of all three: nothing has been read but the room, or written.
   - "Not found" (499, and `ValidateRoomRecipientAsync` 454-455) shares the third line and is reached by no test:
     `GetRoomInfoAsync(ValidatedMailbox)` (90-96) returns a room or throws (`ReadValidatedMailbox`), so `info` is never null -
     defensive, as written.
   - Not in `ValidateRoomRecipientAsync` (451-459): it is shared with `SetRoomTypeAsync`, and the row is where the reason
     becomes the refusal. `SetRoomTypeAsync`'s own identical three (869-892) are outside this finding: reported in the hand-back.
2. **The credential lookups.** Each path's purpose is hoisted into a local `purpose` (the same text as now) and passed to
   `GetModuleCredentialsAsync`; on none, the path's line, then the refusal returned:
   - `PreflightSyncedAttributesViaAdAsync` (1576-1578): `"Set-ADUser preflight for {Room} refused the row; nothing was written:
     no AD credential was returned for {Purpose}: {Refusal}"`, the refusal without its new " Nothing was written." (4).
   - `SetSyncedAttributesViaAdAsync` (1668-1670): `"Set-ADUser City/State/Country write for {Room} refused before its write: no
     AD credential was returned for {Purpose}: {Refusal}"`; returned as now (after Set-Place, inside the partial).
   - `PreflightRoomListAsync` (1859-1861): `"On-prem room-list preflight for {Room} on {List} refused the row; nothing was
     written: no AD credential was returned for {Purpose}: {Refusal}"`, `list.Name`; the refusal built without its " Nothing
     was written." and returned with it appended - the message the same to the byte.
   - `AddToRoomListViaAdAsync` (1962-1964): `"On-prem room-list add for {Room} on {List} refused before its write: no AD
     credential was returned for {Purpose}: {Refusal}"`, `roomListName`; returned as now.
   - Why the purpose: `ModuleCredentialService`'s missing-secret-ID line logs it, and `DelineaService`'s lines name only the
     secret, so the purpose ties the room and the list to whatever the credential services logged. Their lines stay as they are;
     so do `ModuleCredentialService`, `DelineaService` and `ExchangeServiceBase.GetModuleCredentialsAsync`. The refusal texts
     stay too ("not configured", though a Delinea failure also gives none: the message is the operator's, outside this finding;
     reported in the hand-back).
   - Reachable live: a preflight's on any lookup returning none (no secret ID, corrupt config, the PAM provider failing or
     returning an incomplete secret); a write's when its own lookup returns none after the preflight's returned one, and the
     add's also when its preflight asked for none (the room already on the list in Exchange). In the tests, a seam.
3. **The country message.** `BuildCountryAttributes` (165-193) throws `new ArgumentException(<message>)` at 173-176 and 187-189,
   without `nameof(countryOrRegion)`, so `Message` - what the row shows and the line logs - ends with the mapping's own text, not
   " (Parameter 'countryOrRegion')". The type is unchanged (`ConferenceRoomSyncedAttributeTests` asserts `ArgumentException`
   exactly); nothing reads `ParamName` (searched). The second throw is reachable: `RegionInfo` does not know Western Sahara (EH,
   in the table) on this machine's runtime (.NET 10.0.12, the OS's ICU; measured with a scratch probe over the table's 249
   codes, EH the only one) - so the comment calling it "extremely unlikely" is corrected after the round.
4. **One suffix rule.** `PreflightSyncedAttributesViaAdAsync`'s three refusals decided before its runspace - the unmappable
   country (1556-1564), no proven object (1569-1574), no credential (1576-1578) - are each built without " Nothing was
   written.", logged as now (the country's as `Sentence(ex.Message)`), and returned with it appended, as the room-list
   preflight's are. The room-list preflight's every refusal already ends so (1832, 1839, 1856, 1861, and its runspace's);
   verified, unchanged. The writes' refusals come after Set-Place and keep their wording (aud-a2r). The row's own three (part 1)
   keep theirs: outside the two preflights, the time zone and recipient messages shared with `SetRoomTypeAsync`.

**Tests** (`ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs`, a new section at its end, through `ExchangeWriteHarness`
with the file's `CapturingLogger`; no directory, Exchange or network call; no harness change). Its `Service` (49-70) gains
`NoCredentialFor` - for a purpose containing that text no credential at all, as the credential services return none - and
records each purpose asked; `RefusalLine` (1282-1290) gains the row's line.
- `TheRowsOwnRefusal_IsLoggedOnce_NamingTheRoomAndTheReason` (no validated room; an invalid time zone; a recipient that is not
  a room mailbox): refused, not partial, no step, nothing written, no credential asked for, the message as before to the byte;
  exactly one entry carries it, a Warning, equal to the row's line.
- `ACredentialTheLookupDoesNotReturn_IsLoggedOnce_NamingTheRoomTheListAndThePurpose` (the City/State/Country preflight and
  write, the room list's on-prem preflight and add): each path's outcome and message (the preflights' ending "Nothing was
  written.", the writes' inside the partial without it); the purpose asked of the lookup; exactly one entry carries "no AD
  credential was returned for <that purpose>: <refusal>", a Warning, equal to the path's line.
- `TheUnmappableCountrysMessage_CarriesNoParameterSuffix` (the whole row, "ZZ"): the message exactly the mapping's text then
  " Nothing was written.", no "(Parameter"; one entry, the preflight's line with the text.
- `TheCountryMappingsOtherRefusal_CarriesNoParameterSuffix` (EH, a direct call): where the runtime's `RegionInfo` cannot name
  EH, the `ArgumentException`'s message exactly "Country 'EH' has no display name on this system; cannot set the 'co'
  attribute."; where it can, EH maps - the case says which it met.
- `EveryRefusalEitherPreflightDecidesBeforeTheDirectoryWork_EndsNothingWasWritten` (City/State/Country: an unmappable country,
  no AD object, no credential; room list: no GUID, no sync state, no AD object for a synced list, no credential): refused, not
  partial, nothing written, the one step with the message as its `Error`, the message a full sentence then exactly one "
  Nothing was written.".
- Changed: `ARefusalBeforeTheDirectoryWork_IsLoggedOnce_NamingTheRoomTheListAndTheReason` (1340-1399): its two
  City/State/Country cases ("append nothing", 1378-1384) now end " Nothing was written." and log the refusal less it, as its
  other three - part (4) changes that assertion by design. No other existing test or assertion changes.
- 16 new cases (3 + 4 + 1 + 1 + 7).

**Mutations, at the call sites** (aud-a2u's runner on this worktree; md5 before and after, the named failing tests, restored,
md5 matched, touched, rebuilt, green): M1a-c the brief's (1) - each of the row's three lines dropped in turn; M2a-d each of the
four credential lines dropped; M3a the brief's (2) - the parameter suffix restored on the first country throw; M3b on the
second; M4a-c the brief's (3) - the suffix dropped from each City/State/Country early refusal in turn; REV the service whole
as at `dc0555e2`, the new tests kept (they name nothing the fix adds to it). Comments after the round, checked code-identical
by aud-a2q's comment-only checker.

**Files.** `Services/ConferenceRoomService.cs`, `ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs`, this record.

## Approach (as implemented)

- `Services/ConferenceRoomService.cs` (the fix's line numbers):
  - `BuildCountryAttributes` (165): both throws (176-178, 190-191) are `new ArgumentException(<message>)`, no parameter name.
    A comment says why (172-174); the second's "extremely unlikely" is corrected, EH measured (187-189).
  - `SetRoomMetadataAndListAsync` (472): after each of its three refusals sets its message, `_logger.LogWarning("Room Finder
    apply for {Room} refused the row; nothing was written: {Refusal}", roomEmail, result.Message)` (487, 498, 510); the
    messages unchanged.
  - `PreflightSyncedAttributesViaAdAsync` (1560): the unmappable country's refusal is `Sentence(ex.Message)` (1578), no proven
    object's as before; each logged as before and returned with " Nothing was written." (1580, 1590). The credential lookup
    takes a local `purpose` (1597); on none it logs `"Set-ADUser preflight for {Room} refused the row; nothing was written: no
    AD credential was returned for {Purpose}: {Refusal}"` (1602) and returns the refusal with " Nothing was written." (1603).
  - `SetSyncedAttributesViaAdAsync` (1673), `PreflightRoomListAsync` (1850), `AddToRoomListViaAdAsync` (1991): the same for
    each credential lookup (`purpose` 1697, 1895, 2006; the line 1702, 1900, 2011), each in its path's words; the room-list
    preflight's refusal built without its " Nothing was written." and returned with it. Every message but the City/State/Country
    preflight's three (part 4) and the country's (part 3) is the same to the byte.
  - Nothing else: the helpers, the step catches, every other line and message.
- `ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs`, no harness change: the `Service` takes `NoCredentialFor` (60)
  and records `CredentialPurposes` (62); `RefusalLine` gains the row's line (1293); `ARefusalBeforeTheDirectoryWork_...`
  (1356) strips " Nothing was written." from all five cases (1387-1390); the new section (1434-1641) -
  `TheRowsOwnRefusal_IsLoggedOnce_NamingTheRoomAndTheReason` (3),
  `ACredentialTheLookupDoesNotReturn_IsLoggedOnce_NamingTheRoomTheListAndThePurpose` (4),
  `TheUnmappableCountrysMessage_CarriesNoParameterSuffix` (1), `TheCountryMappingsOtherRefusal_CarriesNoParameterSuffix` (1),
  `EveryRefusalEitherPreflightDecidesBeforeTheDirectoryWork_EndsNothingWasWritten` (7).
- 16 new cases. One existing assertion changed, by part (4) as the plan says: the a2u theory's two City/State/Country cases. On
  the fix the 356 `ConferenceRoom*` cases pass, the 340 existing among them.

## Guard proof

| Test | What it pins |
| --- | --- |
| `TheRowsOwnRefusal_IsLoggedOnce_NamingTheRoomAndTheReason` (no validated room, an invalid time zone, a recipient that is not a room mailbox) | part 1: the row refused with every field set - not partial, no step, `Writes` empty, no credential asked for - its message the old one to the byte (`ValidatedMailbox.Refusal`, "Invalid timezone: 'Mars Standard Time'.", "'<room>' is a EquipmentMailbox, not a room mailbox. ..."); exactly one log entry carries it, a Warning, equal to "Room Finder apply for <room> refused the row; nothing was written: <refusal>" |
| `ACredentialTheLookupDoesNotReturn_IsLoggedOnce_NamingTheRoomTheListAndThePurpose` (City/State/Country preflight, City/State/Country write, on-prem room-list preflight, on-prem room-list add) | part 2: the lookup returning none for that path's purpose only; the preflights refused with nothing written, the message the refusal then " Nothing was written."; the writes partial after Set-Place, no `Set-ADUser` / `Set-ADGroup`, the step's `Error` the refusal, "Nothing was written" nowhere; the last purpose asked the path's; exactly one entry carries "no AD credential was returned for <purpose>: <refusal>", a Warning, the path's line - and it is the only entry carrying the refusal |
| `TheUnmappableCountrysMessage_CarriesNoParameterSuffix` | part 3 through the row: "ZZ" refused, nothing written, the message exactly the mapping's words then " Nothing was written.", no "(Parameter"; one entry, the preflight's line, the mapping's words |
| `TheCountryMappingsOtherRefusal_CarriesNoParameterSuffix` | part 3 on the second throw: where `RegionInfo` cannot name EH - this machine, as M3b shows - the `ArgumentException`'s message exactly "Country 'EH' has no display name on this system; cannot set the 'co' attribute."; where it can, EH maps to ("EH", its name, 732) |
| `EveryRefusalEitherPreflightDecidesBeforeTheDirectoryWork_EndsNothingWasWritten` (City/State/Country: an unmappable country, no AD object, no credential; room list: no GUID, no sync state, no AD object, no credential) | part 4: each refused, not partial, nothing written, its one step with the message as its `Error`, the message a full sentence then exactly one " Nothing was written." |
| `ARefusalBeforeTheDirectoryWork_IsLoggedOnce_NamingTheRoomTheListAndTheReason` (aud-a2u's; its two City/State/Country cases changed) | part 4 on aud-a2u's cases: all five now end " Nothing was written."; each line carries the refusal before it |

**Mutations** (`scratchpad/a2v-round/round.ps1` - aud-a2u's runner, its root pointed at this worktree - with `rows-a2v.ps1`;
log `round-a2v.log` beside them; 2026-10-22 17:25-17:37). Each row ran the 356 `ConferenceRoom*` cases, the test project rebuilt
each time, the failures read from its TRX. md5 before = restored: `Services/ConferenceRoomService.cs`
`cd41f30561245e675d132b1aa48631b2` (every row); the service was touched once mutated and once restored, so each build compiled
the file as it then was. Test names are in `ConferenceRoomWriteIdentityTests`.

| # | Mutation | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- |
| M1a | the brief's (1), at the call site: the row's line for no validated room dropped | `b1a52a17349d43faf3abc4a28f80c03f` | `TheRowsOwnRefusal_IsLoggedOnce_NamingTheRoomAndTheReason` (no validated room) | FAIL, 1 of 356: no entry carries the refusal |
| M1b | the same, its line for an invalid time zone | `c693b41b8087444e2fa2dd7f738006b9` | the same (an invalid time zone) | FAIL, 1 |
| M1c | the same, its line for the recipient refused | `e3370dd3ebcfd94956f411f1c8833b82` | the same (a recipient that is not a room mailbox) | FAIL, 1 |
| M3a | the brief's (2), at the call site: `nameof(countryOrRegion)` restored on the country mapping's first throw | `c1b58e7a655c218daae9830401364250` | `TheUnmappableCountrysMessage_CarriesNoParameterSuffix` | FAIL, 1: "(Parameter" found in the message |
| M4a | the brief's (3), at the call site: " Nothing was written." dropped from the City/State/Country preflight's unmappable-country refusal | `95136ab8f7e062febe53fcc6e17185be` | `EveryRefusalEitherPreflight...` (City/State/Country, an unmappable country); `ARefusalBeforeTheDirectoryWork_...` (an unmappable country); `TheUnmappableCountrysMessage_CarriesNoParameterSuffix` | FAIL, 3 |
| M4b | the same, its no-proven-object refusal | `d71716077ee9f1244e1f0f2b8d1eebf8` | `EveryRefusalEitherPreflight...` (City/State/Country, no AD object); `ARefusalBeforeTheDirectoryWork_...` (no AD object for City/State/Country) | FAIL, 2 |
| M4c | the same, its no-credential refusal | `c9942203c5bee38c0c6486be1c6d842e` | `EveryRefusalEitherPreflight...` (City/State/Country, no credential); `ACredentialTheLookupDoesNotReturn_...` (City/State/Country preflight) | FAIL, 2 |

Beyond the brief's three:

| # | Mutation | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- |
| M2a | the City/State/Country preflight's credential line dropped | `610c6b7b1426681486acc28334b5b44e` | `ACredentialTheLookupDoesNotReturn_IsLoggedOnce_NamingTheRoomTheListAndThePurpose` (City/State/Country preflight) | FAIL, 1: no entry carries the line's reason |
| M2b | the same, the City/State/Country write's | `ecc446ccddc208ddc6141e13ab7b5d94` | the same (City/State/Country write) | FAIL, 1 |
| M2c | the same, the on-prem room-list preflight's | `ebd27372eff515fd6f77ebe5e355a300` | the same (on-prem room-list preflight) | FAIL, 1 |
| M2d | the same, the on-prem room-list add's | `8d14f73cbeb5c5d6568df5c06e84eff7` | the same (on-prem room-list add) | FAIL, 1: the only entry the cloud add's own Error ("Exchange operation failed: ..."), which carries no refusal |
| M3b | `nameof(countryOrRegion)` restored on the second throw (no display name) | `11699a0ab33427dc2cefb1ec532887ba` | `TheCountryMappingsOtherRefusal_CarriesNoParameterSuffix` | FAIL, 1: the message ended " (Parameter 'countryOrRegion')" - so the case took its throw branch on this machine |
| M5a | a second line: `ValidateRoomRecipientAsync` logs its not-a-room refusal too | `ded0f5bb918b9a40049322a7be868a56` | `TheRowsOwnRefusal_...` (a recipient that is not a room mailbox) | FAIL, 1: two entries carry the refusal |
| M5b | a second line: the row logs the City/State/Country write's refusal again in its partial | `8ae6e5ae8077fc35a68a72e1df3f0480` | `ACredentialTheLookupDoesNotReturn_...` (City/State/Country write); aud-a2u's `TheObjectReadsRefusal_IsLoggedOnce_WhereThePathRefuses` (City/State/Country, write) | FAIL, 2: two entries carry the refusal |
| REV | the fix reverted: the service whole as at `dc0555e2`, the new tests kept (they name nothing the fix adds to it) | `4a83690ac382f682e9c2eaf10e91cd60` | the row's three, the credential's four, both country cases, `EveryRefusalEitherPreflight...` (its three City/State/Country cases), `ARefusalBeforeTheDirectoryWork_...` (its two) | FAIL, 14 of 356. PASS: `EveryRefusalEitherPreflight...`'s four room-list cases - that preflight already ended so |

Every restored md5 equal to "before"; after the last row the restored tree rebuilt and the 356 `ConferenceRoom*` cases green.

**Comments after the round.** The round ran on the fix with none of the service's new explanatory comments (the test file's were
written with the tests). They were written afterwards and checked by `scratchpad/a2v-round/comment_only.ps1` (aud-a2q's checker:
blank, `//`, `///` and PowerShell `#` lines dropped from both versions, the code lines identical in order): service
`cd41f30561245e675d132b1aa48631b2` -> `233ab9c3a6dac32dc0ef87de2b6c412e` (code lines 1838 = 1838; comment and blank lines 994 ->
1021). Unchanged since the round: `ConferenceRoomWriteIdentityTests.cs` `3fc844df307edb1032bec703ae13e154`.

## Known gaps

- The recipient refusal's "not found" is reached by no test: `GetRoomInfoAsync(ValidatedMailbox)` returns a room or throws
  (`ReadValidatedMailbox`), so `info` is never null. The row's third line logs whichever refusal it gets.
- The country mapping's second refusal depends on the runtime: this machine's (.NET 10.0.12 over the operating system's ICU)
  cannot name EH, measured by a scratch probe over the table and by M3b. Where a runtime can, the case pins EH's mapping instead.
  CI's runner was not measured.
- The two writes' credential refusals are reached in the tests only through the seam. Live they need a second lookup returning
  none after the preflight's returned one - or, for the add, the preflight having asked for none (the room on the list in
  Exchange then) - and are not induced.
- The four credential refusals still read "On-prem AD credential is not configured ... (set the AD Delinea Secret ID in Module
  Config)" when the secret ID is set and the PAM lookup failed: the operator's message is outside this finding. The line now says
  "no AD credential was returned for <purpose>", beside the credential services' own lines, which say why.
- For a missing secret ID two lines carry the purpose: `ModuleCredentialService`'s Error and the path's Warning - two layers, by
  design; the "once" is the service's own.
- Not changed, outside this record: `SetRoomTypeAsync`'s identical three refusals (and its unconfigured-groups refusal) are
  returned and logged nowhere by the service; the row's own time-zone and recipient messages do not end "Nothing was written."
  (shared with `SetRoomTypeAsync`); a throw from the row's room read (`ReadValidatedMailbox`) leaves the service and reaches the
  page's catch, which audits and notifies but logs nothing. Reported in the hand-back.

**Operator-visible changes.**

- A Room Finder row refused before its preflights - no validated room, an invalid time zone, a recipient that is not a room
  mailbox - leaves one warning in the application log: "Room Finder apply for <room> refused the row; nothing was written:
  <refusal>". Before, only the row's result and its audit carried it.
- A row refused because the module's AD credential lookup returned none leaves one warning on that path naming the room, the list
  where there is one, and the purpose the lookup was asked with, e.g. "Set-ADUser preflight for <room> refused the row; nothing
  was written: no AD credential was returned for Set-ADUser preflight for <room>: <refusal>". Before, only the credential services'
  lines, naming the secret or the module.
- The unmappable country's message ends with the mapping's own words - no " (Parameter 'countryOrRegion')"; so does "Country
  '<code>' has no display name on this system ..." (EH on this machine).
- The City/State/Country preflight's three refusals before its runspace - an unmappable country, a cloud-only room with
  City/State/Country, no AD credential - end " Nothing was written.", as the room-list preflight's do. The row's message carries
  it, so its audit and admin notification do too.
- Nothing else: no outcome, step, write, audit field or notification changes.

**Dev acceptance:** nothing new beyond the Room Finder check owed with aud-a2f..a2u: a clean row logs no refusal and its messages
are unchanged, and the new lines and words follow refusals, which are not to be induced on dev. Not run here: no directory,
Exchange or SMTP call was made, and the owner's standing instruction is not to probe Active Directory.

## Reviewer comments

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent on `worktree-agent-a3708734379b4119d`.
Independent review pending.

For the reviewer:

- One fix commit for the finding's four parts.
- Part 1's placement: at the row's three returns (aud-a2t's), not in `ValidateRoomRecipientAsync`, which `SetRoomTypeAsync`
  shares; M5a pins the "once" against a line there.
- Part 2's line is the path's own refusal line (aud-a2t/a2u), its reason "no AD credential was returned for <purpose>: <refusal>":
  the purpose is the text `ModuleCredentialService`'s missing-secret-ID line names, so it ties the room and the list to the
  credential services' lines. The credential services are unchanged, as the approach says.
- Part 3 drops the parameter name rather than stripping the suffix at the callers: the suffix is the runtime's resource string,
  which a caller would have to match; nothing reads `ParamName`, and the type stays `ArgumentException`
  (`ConferenceRoomSyncedAttributeTests` asserts it exactly).
- Part 4 builds the City/State/Country preflight's three refusals as aud-a2u built the room-list preflight's: without the suffix,
  logged, returned with it - so the lines carry the refusal, and the logged country text drops the parameter suffix too.
- The EH case is conditional on the runtime, by measurement; M3b shows its throw branch is the one taken here.
- One existing assertion changed, by design: aud-a2u's "append nothing" for its two City/State/Country cases.
- Beyond the brief's three mutation rows: all three row lines (M1a-c), all four credential lines (M2a-d), both throws (M3a-b), all
  three suffixes (M4a-c), the "once" (M5a-b) and a whole-file revert (REV).
- Outside the service and its tests: nothing.

## Closeout

Fixed on branch, not merged, not pushed: the plan `1d6d2c2d`, then this fix, tests, comments and record in one commit. A
module-scoped behaviour change (Conference Rooms: seven new warning lines, and the words of the City/State/Country preflight's
three early refusals and of the country mapping's two): the Conference Rooms module version bump is due at merge - not made here,
`Modules/ModuleCatalog.cs` being outside this brief. No shared code changed, so no base app bump.

Gates, run on the final files this commit holds (the mutation round ran before the comments, which the comment-only check shows
changed no code line):

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings - the baseline count - none in a
  touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: 5644 passed, 0 failed, 3 skipped (the known AD
  skips, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`), 5647 total, 5 m 19 s; nothing hung, no Sequence
  file. This record's new cases are 16; the 316 `ClickGate*` cases are among those passed (and, run alone, 316 passed).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD`: clean.
- `tools/Test-AsciiOnly.ps1` after `git add -A`: clean.

PSScriptAnalyzer and Pester were not run: no `.ps1` or `.psm1` changed (the scratch runner, rows, probe and checker are not in
the repository).

Base `dc0555e2`, which both remotes held at the start (`git ls-remote`). The local master has since moved to `c3bc0c24` - twenty
commits of other work streams - touching none of this record's files; the totals above are this branch's.
