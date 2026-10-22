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
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; plan written, in progress
**Branch**: `worktree-agent-a3708734379b4119d`
**Commit**: -
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

## Guard proof

Pending.
