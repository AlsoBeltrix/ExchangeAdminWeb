# aud-a2y: Conference Rooms and credential wording leftovers - the Type path's room read that throws is not logged, "Check the DelineaSecretId configuration" at seven sites in other modules, and two refusals outside the service lack "Nothing was written."

**Severity**: LOW - recorded under the 2026-10-23 stopping rule, not dispatched. (1)
`ConferenceRoomService.SetRoomTypeAsync`: when the Type path's room read throws, the service neither
catches nor logs it (aud-a2w part 2's gap on the Type path). (2) "Check the DelineaSecretId configuration"
is shown whatever the actual reason at six `SelfServiceGroupService` sites and one `GroupManagementService`
site - the aud-a2x defect in other modules; they can now call `ModuleCredentialService.LookupCredentialsAsync`.
(3) Refusals outside the service do not end "Nothing was written.": `ConferenceRoomBulkProcessor` "Invalid
type" and `ConferenceRooms.razor` "Invalid room type" (line-keyed page).
**Status**: Closed - merged to master `07f2d371`; codex cr-batch-8 clean. Complete once fix/psl10-a2y is deleted
**Branch**: `fix/psl10-a2y` (worktree `D:\source\ExchangeAdminWeb-wt\psl10-a2y`, forked from master at `3e43b35b`)
**Commit**: (1) `3f641ce4`, (2) `2de45069`, (3) the commit that adds this line (SHA in the implementer's hand-back)
**Reviewer**: implementer (surfaced by the psl-8/a2w/a2x agent, 2026-10-23, candidates 1, 7 and 8 of its hand-back)

## Evidence

At `9293b77a`: `Services/ConferenceRoomService.cs` 931; `Services/SelfServiceGroups/SelfServiceGroupService.cs`
88, 165, 242, 468, 738, 791; `Services/GroupManagementService.cs` 107; `Services/Jobs/ConferenceRoomBulkProcessor.cs`
205; `Components/Pages/ConferenceRooms.razor` 1400 (line-keyed by `ClickGateRegistry`, 1758 lines, edit in
place only).

## Approach (to be written by the implementing agent before code)

- The Type path's room read gets the aud-a2w Warning-and-rethrow.
- The seven credential refusals choose their clause from `LookupCredentialsAsync`'s failure (the aud-a2x
  shape).
- The two refusals end " Nothing was written." (the page edit in place).
- Mutations at the call site: the catch removed; one site's single text again; one suffix dropped.

### As written before each item's code (branch `fix/psl10-a2y`, forked from master `3e43b35b`)

Line numbers are the base's unless an item says otherwise. One item per commit, in order.

**(1) The Type path's room read.** `Services/ConferenceRoomService.cs` `SetRoomTypeAsync` (1066), its read of the
validated room at 1099 - `var (_, recipientError) = await ValidateRoomRecipientAsync(room, roomEmail);`, in no try - runs in
a try whose catch logs `_logger.LogWarning(ex, "Room Type apply for {Room}: reading the room failed; nothing was written:
{Error}", roomEmail, ex.Message)` and rethrows with `throw;`: the Finder row's read since aud-a2w (624-633), word for word but
the path's name. It is the path's first call: the no-room and time-zone refusals before it read nothing, and nothing is written
before it. The outcome is the same throw, so the page's catch (`Components/Pages/ConferenceRooms.razor`
1436-1441, untouched) and the bulk row's (`Services/Jobs/ConferenceRoomBulkProcessor.cs` `ProcessRowAsync`, untouched) audit
and notify as before; neither writes a log line, and the pool rethrows without one, so this is the one line. Test in
`ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs` beside the Finder row's (`TheRowsRoomRead_ThatThrows_...`),
through the same `Service` seam and `CapturingLogger`. Mutation: the catch removed (the fix reverted). Conference Rooms
2.7.0 -> 2.8.0, the module's one minor step for this branch (kd-5 and item (3) land under it).

**(2) The seven credential refusals.** Each site asked `ModuleCredentialService.GetCredentialsAsync`, which returns null
alike for an unreadable module config, no secret ID and a configured ID Delinea answered with nothing, and refused with
"AD credentials unavailable. Check the DelineaSecretId configuration for <module>." whatever the reason:
`Services/SelfServiceGroups/SelfServiceGroupService.cs` 86-88 (the owned-groups list, a throw), 163-165 (the single-group
search, a throw), 240-242 (the member listing, a throw), 466-468 (the typed membership change, `PermissionResult.Fail`),
736-738 (the listed-member removal, `PermissionResult.Fail`), 789-791 (the bulk resolution, every line `NotAttempted`,
through the `GetCredentialsAsync` test seam at 835-836); `Services/GroupManagementService.cs` 105-107 (the group search, a
throw). The change, aud-a2x's shape:
- `GroupManagementService.CredentialUnavailableRefusal(moduleId, failure)`, new, internal static, pure: `SecretIdNotSet`
  (and `None`, which no lookup returns beside no credential) today's text to the byte; `NotReturned` "AD credentials
  unavailable. The DelineaSecretId for <module> is set, but Delinea returned no credential for it (the application log has
  its reason)."; `ConfigUnreadable` "AD credentials unavailable. The <module> module configuration could not be read, so
  neither could its DelineaSecretId (the application log records it)." Each keeps "AD credentials unavailable." first, as
  callers and tests read it. In Group Management's file because Self-Service Groups already takes that service's statics
  (`NotAttempted`, `ServerFromDn`); not in `ModuleCredentialService`, which every module shares.
- `GroupManagementService.LookupCredentialsAsync(purpose)`, new, internal virtual beside `GetCredentialsAsync` (which stays
  for the six other paths and the four test fixtures that override it): the credential service's `LookupCredentialsAsync`.
  The search takes its credential from it and refuses with the helper's text.
- `SelfServiceGroupService`: the seam `GetCredentialsAsync(purpose)` becomes `LookupCredentialsAsync(purpose)` (internal
  virtual, the credential service's lookup), and all six sites take their credential through it - five asked the
  credential service directly - each refusing, in its own shape, with the helper's text for "SelfServiceGroups". The
  pinned call texts (`creds.Value` passed on) are unchanged. `SelfServiceBulkResolveTests`' fixture overrides the renamed
  seam (no credential reads as no secret ID set, as before).
- Tests, new `ExchangeAdminWeb.Tests/GroupCredentialRefusalTests.cs`: the seven sites x (no secret ID; an ID Delinea returns
  none for; an unreadable module configuration), each through a real `ModuleCredentialService` (aud-a2x's
  `OfflineCredentials`: no bootstrap credential, any request counted and failed) - refused in the site's shape, in that
  reason's words, "Check the DelineaSecretId configuration" only for no ID, no request sent; and the helper's four answers.
  Mutations at the call sites: each site's refusal built from `SecretIdNotSet` instead of its lookup's failure, in turn
  ("one site's single text again"); the helper answering today's text whatever the failure.
- Self-Service Groups 1.11.3 -> 1.12.0, Group Management 2.12.1 -> 2.13.0.

**(3) The two refusals outside the service.** Both refuse a type that does not parse before the service is called, so
nothing is written:
- `Services/Jobs/ConferenceRoomBulkProcessor.cs` 208, `ApplyTypeRowAsync`: `Message = $"Invalid type: {row.Type}"` becomes
  `$"Invalid type: {row.Type}. Nothing was written."`. The row's audit and its row message carry it, as before.
- `Components/Pages/ConferenceRooms.razor` 1400, `SetSingleRoomType`: `Message = $"Invalid room type: {typeName}"` becomes
  `$"Invalid room type: {typeName}. Nothing was written."` - the one line edited in place, so the page keeps its 1758 lines and
  no `ClickGateRegistry` or `ProgressRegistry` entry moves.
- Tests: `ConferenceRoomBulkProcessorTests` - a Type row whose type does not parse is refused, failed, with that message in
  its outcome and its audit row, and the service never called; a new `ConferenceRoomPageTests` drives the real page's
  `SetSingleRoomType` (its services set through reflection, the `ConferenceRoomDirectoryAuditTests` idiom) with a type that
  does not parse: the banner's message, nothing sent to Exchange. Mutation: each suffix dropped in turn.

## Guard proof

Each row: the file's md5 with the fix, the mutation applied (the fix reverted to the commit before it, or one exact
replacement at the call site), the projects rebuilt, the filter run, the failures read from the TRX; then the fixed bytes
copied back with a fresh mtime, the md5 checked equal, rebuilt, the filter run again. Scripts and logs in the session
scratchpad (`round/prove.sh`, `round/<row>.log`); no directory, Exchange or network call in any of them.

**(1) The Type path's room read** - `ConferenceRoomWriteIdentityTests.TheTypePathsRoomRead_ThatThrows_IsLoggedOnce_AndStillThrows`
(another mailbox; no mailbox): the read still throws with Exchange's message ("... to a different mailbox ...", "...
couldn't be found ..."); nothing written; no credential asked for; exactly one log entry carries the message, a Warning,
equal to "Room Type apply for <room>: reading the room failed; nothing was written: <message>", with the thrown exception.

| # | Mutation | md5 fixed = restored | md5 mutated | Filter `ConferenceRoomWriteIdentityTests` (101) | Restored |
| --- | --- | --- | --- | --- | --- |
| 1 | the fix reverted - the read in no try, as at `3e43b35b` (the brief's "the catch removed") | `e79e2bf881012163891c3274223095d6` | `3ab67aa6b221ae3c912a8427adf6a422` | FAIL, 2 of 101: both cases, "Assert.Single() Failure: The collection did not contain any matching items" - no log entry carries the message; the throw and its message as before | PASS, 101 of 101 |

**(2) The seven credential refusals** - `GroupCredentialRefusalTests` (new):
`ARefusalForWantOfTheCredential_SaysWhyThereIsNone` (the seven paths x no secret ID, an ID Delinea returns none for, an
unreadable module configuration - 21 cases, each through a real `ModuleCredentialService` over a real module config store):
refused in the path's own shape (the three reads throw; the two membership writes fail with no change; the bulk resolution
marks every line not attempted, one reason), the refusal that reason's words exactly, "AD credentials unavailable." first,
"Check the DelineaSecretId configuration" in it only for no secret ID, no request sent to Delinea;
`TheSharedRefusal_ChoosesItsWordsFromTheFailure` (4): the helper's answer for each failure, `None` read as no secret ID.
`SelfServiceBulkResolveTests` (its fixture on the renamed seam) unchanged in every assertion. No whole-file revert row: the
tests name members this item adds (`CredentialUnavailableRefusal`, the `LookupCredentialsAsync` seams), so the files as at
`3f641ce4` do not compile against them; the rows below are that revert's parts, each at its call site. Filter
`GroupCredentialRefusalTests|SelfServiceBulkResolveTests` (34 cases); md5 fixed = restored: `Services/GroupManagementService.cs`
`1cb895e73202900a048e349fe0628899`, `Services/SelfServiceGroups/SelfServiceGroupService.cs` `db8f6dedde8dbe03de0e1b491aeff63d`.

| # | Mutation (call site) | md5 mutated | Failing (of 34) | Restored |
| --- | --- | --- | --- | --- |
| M1 | the shared helper answering today's text whatever the failure | `e461873ed5d2ba0dc42ef66c6ad18151` | 16: every path's ID-set and unreadable cases (14), the helper's `NotReturned` and `ConfigUnreadable` (2); no secret ID passes, its words being today's | PASS 34 |
| M2a | the owned-groups list's refusal built from `SecretIdNotSet`, not its lookup's failure (the brief's "one site's single text again") | `c169c9e98c02a4302d1df6a0b51e6293` | 2: that path, ID set and unreadable | PASS 34 |
| M2b | the same, the single-group search | `cf67e23221e06910127d84a388bdba3e` | 2: that path's two | PASS 34 |
| M2c | the same, the member listing | `0f3bdec7652d528f169ca4b6adf82691` | 2: that path's two | PASS 34 |
| M2d | the same, the typed membership change | `5686c5d21d2cbf26d5d21ecdf4b661d7` | 2: that path's two | PASS 34 |
| M2e | the same, the listed-member removal | `07258d9e2c92a7f1af144f1793663e14` | 2: that path's two | PASS 34 |
| M2f | the same, the bulk member resolution | `6ff5e4871b44fd3f376dbda68a05faa7` | 2: that path's two | PASS 34 |
| M2g | the same, Group Management's group search | `b36aeeba6417fa293a35c2e936f79b73` | 2: that path's two | PASS 34 |
| M3a | Self-Service Groups' seam dropping the reason on the way - the old lookup's credential, read as no secret ID | `23808f6b64b547a7aac40b2b3408f5b4` | 12: the six Self-Service paths' ID-set and unreadable cases | PASS 34 |
| M3b | the same, Group Management's seam | `69a323bdfec9bc4ed448c3790f6cc046` | 2: the group search's two | PASS 34 |

Before the round, on the fix: the related suites (`GroupCredentialRefusalTests`, `SelfService*`, `GroupManagement*`,
`GroupMember*`, `GroupBulk*`, `DirectoryModuleImportTests`, `ModuleCredential*`, `ResourceDomainDenies*`,
`ProtectedGroupWriteTarget*`, `GroupSearchForestScope*`, `ModuleCatalog*`, `AttributeEditor*`) 768 of 768.

**(3) The two refusals outside the service** - `ConferenceRoomBulkProcessorTests.ATypeRowWhoseTypeDoesNotParse_IsRefused_SayingNothingWasWritten`
(the row failed, its message "Invalid type: Boardroom. Nothing was written.", the service never called, one audit row
carrying that message as its error); `ConferenceRoomPageTests.ARoomTypeThatDoesNotParse_IsRefused_SayingNothingWasWritten`
(the real page's `SetSingleRoomType`, the ticket and authorization passed: the banner "Invalid room type: Boardroom.
Nothing was written.", no Exchange call at all). Filter `ConferenceRoomBulkProcessorTests|ConferenceRoomPageTests` (26).

| # | Mutation | md5 fixed = restored | md5 mutated | Failing (of 26) | Restored |
| --- | --- | --- | --- | --- | --- |
| 3a | the bulk suffix dropped - `ConferenceRoomBulkProcessor.cs` as at `2de45069` | `903cb55fea2b9a5de11ed22b54a2bcd0` | `18d6b408727b1a1b1b5c6f8f98ad2015` | 1: the bulk case, "Expected ... Boardroom. Nothing was written. / Actual ... Boardroom" | PASS 26 |
| 3b | the page suffix dropped - `ConferenceRooms.razor` as at `2de45069` | `dca79be9386db21fff416b44672a0b94` | `0ff410bff9f6dc1c0f27672d0595b2c2` | 1: the page case, the same difference | PASS 26 |

On the fix, before the round: `ConferenceRoom*`, `ClickGate*`, `ProgressRegistry*`, `ModuleCatalog*`, `ValidatedWriteCallSite*`
829 of 829; the page has 1758 lines, as before.

## Files changed

Snapshot before repair: `3e43b35b`.

- (1) `Services/ConferenceRoomService.cs` (`SetRoomTypeAsync`'s room read); `ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs`
  (the new theory); `Modules/ModuleCatalog.cs` (Conference Rooms 2.8.0).
- (2) `Services/GroupManagementService.cs` (`CredentialUnavailableRefusal`, `LookupCredentialsAsync`, the search);
  `Services/SelfServiceGroups/SelfServiceGroupService.cs` (the six sites, the seam renamed); `ExchangeAdminWeb.Tests/GroupCredentialRefusalTests.cs`
  (new); `ExchangeAdminWeb.Tests/SelfServiceBulkResolveTests.cs` (its fixture on the renamed seam); `Modules/ModuleCatalog.cs`
  (Self-Service Groups 1.12.0, Group Management 2.13.0).
- (3) `Services/Jobs/ConferenceRoomBulkProcessor.cs`; `Components/Pages/ConferenceRooms.razor` (line 1400, in place);
  `ExchangeAdminWeb.Tests/ConferenceRoomBulkProcessorTests.cs`; `ExchangeAdminWeb.Tests/ConferenceRoomPageTests.cs` (new);
  `Modules/ModuleCatalog.cs` (the 2.8.0 note).
- This record.

**Operator-visible changes.** A Room Type apply whose room read throws leaves one warning in the application log, "Room Type
apply for <room>: reading the room failed; nothing was written: <error>", with the exception; the outcome, audit and
notification as before. A Self-Service Groups or Group Management refusal for want of the module's AD credential keeps
"AD credentials unavailable." and says, when the secret ID is set, that Delinea returned no credential for it, or, when the
module's configuration is unreadable, that the ID could not be read - only for no ID does it still say "Check the
DelineaSecretId configuration for <module>."; the audit rows and banners that carry it change with it. The page's and a bulk
row's refusal of a type that does not parse end " Nothing was written." Nothing else.

## Coder dispute

None.

## Known gaps

- (1) The room read inside `RunAsync` (Step 1, `ReadValidatedMailbox`) is not this item's: it runs after the refusals,
  inside the operation runner, whose own failure handling records it as the step; unchanged.
- (2) Only the seven sites that said "Check the DelineaSecretId configuration" changed. Group Management's other six
  credential refusals say "AD credentials unavailable." with no instruction at all, so none misdirects; unchanged, as are
  other modules' own words (Emergency Disable, Comms-10k, the attribute editor, DHCP's "Enterprise Admin credentials
  unavailable from Delinea.").
- (2) The categories are aud-a2x's and as coarse: "Delinea returned no credential" covers a missing bootstrap credential,
  an HTTP failure, a secret missing a field and an exception; `DelineaService` logs which, and the text points there.
- (3) The page's invalid-type refusal is reachable only through a crafted request (the select offers the six types); it
  writes no audit row - nor did it before - which this item, about its words, does not change (a candidate in the hand-back).
- Source-level and stand-in driven: no directory, Exchange, Delinea or network call was made.

## Closeout

Fixed on branch `fix/psl10-a2y`, not merged, not pushed: (1) `3f641ce4`, (2) `2de45069`, (3) the commit that adds this
section (its SHA in the implementer's hand-back). Module bumps on this branch: Conference Rooms 2.7.0 -> 2.8.0, Self-Service
Groups 1.11.3 -> 1.12.0, Group Management 2.12.1 -> 2.13.0. No shared code changed by this record; no base app bump from it.
Pending: the merge and codex review.

## Reviewer comments (supervisor, cr-batch-8)

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base 3e43b35b head f7b794cb (merged 07f2d371); 2026-10-23 evening; capability proof succeeded (AGENTS.md read, git show --stat f7b794cb). Two LOW candidates across the range, admitted as kd-32 (against kd-5) and psl-11 (against psl-10 (4)). Codex confirmed: the seven credential refusals use the same LookupCredentialsAsync failure shape; the DHCP 2>$null still lets -ErrorAction Stop terminate and the file is ASCII; the psl-10 stream clears run in finally on exception paths. Artefacts: `.agents/review/cr-batch-8.result.txt`, `.stream.log`, `prompt-cr-batch-8.md`.
