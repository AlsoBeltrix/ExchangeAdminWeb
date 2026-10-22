# aud-a23i: Migration - an identity-less mailbox row is a targetable "Unknown", a throwing row loses the list, failed loads report progress as success, two generic error texts, and unzoned export file names

**Severity**: LOW. (1) `GetMigrationBatchUsersAsync`: a mailbox row with no Identity becomes
"Unknown", which an action can target; a row whose getter throws loses the whole mailbox list (the
aud-a23f shape on the mailbox list). (2) `LoadBatchList` and every load on the Migration page
complete their progress activity as `true` on failure, so the status bar reports success after a
failed load. (3) `ExchangeServiceBase.Invoke` fails with "An unknown error occurred." whenever
`HadErrors` is set with an empty error stream, which a nested suppressed error causes (not seen
live). (4) Under `-ErrorAction Stop` the error stream is empty, so app-wide `RunAsync` failure
messages carry the "The running command stopped because..." prefix instead of the cmdlet's own
message (aud-a23g unwrapped it for one path only). (5) The Migration export file names use the
server's clock with no zone.
**Status**: Merged `ab5361c8`; closed on guard proof; codex review pending
**Branch**: `worktree-agent-a3e674616251be4de` (forked at `34c7ccd4`)
**Commit**: `24c8957e` (fix, tests, record); plan `4cf6b629`; comments `033a1a54` (shared with aud-a23h)
**Reviewer**: implementer (surfaced by the aud-a23g agent, 2026-10-22, candidates 3 to 7 of its hand-back)

## Evidence

At `cafef918`: `Services/MigrationService.cs` `GetMigrationBatchUsersAsync` (identity default,
per-row throw); `Components/Pages/Migration.razor` `LoadBatchList` and the other loads' progress
completion; `Services/ExchangeServiceBase.cs` `Invoke` (the empty-stream branch) and `RunAsync`'s
failure message; the export file-name composition.

## Approach (to be written by the implementing agent before code)

- Mailbox rows: the aud-a23f shape - an identity-less row is listed as unreadable (not targetable);
  a throwing row is listed with its identity as far as read and the reason; the rest of the list
  loads.
- Progress: a failed load completes its activity as not succeeded with the failure text (the
  aud-a26h drain precedent); every load on the page.
- `ExchangeServiceBase`: when `HadErrors` is set with an empty stream, report that shape honestly
  ("a nested command reported an error it did not surface") rather than "unknown"; unwrap
  `ActionPreferenceStopException.ErrorRecord` in `RunAsync`'s failure path once, for every caller (the
  aud-a23g part-1 unwrapping moves into the base).
- Export file names in UTC with a `Z` suffix.
- Tests: identity-less and throwing mailbox rows; a failed load's activity outcome; the base class
  message shapes; a file name. Mutations at the call site per part. `Migration.razor` line-keyed
  entries: edit in place or re-anchor per the playbook.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `cef232a1`. One commit `fix(migration,shared): ...
(aud-a23i)`, after aud-a23h on the same branch. Behaviour: Migration (parts 1, 2, 5) and the base app (parts 3, 4,
`Services/ExchangeServiceBase.cs`: every `ExchangeServiceBase` subclass's `Invoke` failure text, and every `RunAsync`
caller's failure message - Migration, Blocked Senders, Calendar Permissions, Conference Rooms, Out of Office). Bumps
are the supervisor's. Written before the code; line numbers at `34c7ccd4`; the page in place, 4377 lines, no
registered line or snippet among the edits.

1. **Mailbox rows, the aud-a23f shape.**
   - `Models/MigrationModels.cs`: new `UnreadableMigrationMailbox(string? Identity, string Reason)` (`Shown`;
     `Warning` - "N mailbox(es) could not be read: <name>: <reason>; ...") and `MigrationMailboxList(Mailboxes,
     Unreadable)`, positional, so the page's one-line hand-off to `ReplaceBatchUsers` holds.
   - `Services/MigrationService.cs` `GetMigrationBatchUsersAsync` (763-813) returns it. Per row, Identity first: absent
     or blank - unreadable, "a row with no Identity" (it was a mailbox "Unknown", 782, which an action could target); a
     row that throws while read - unreadable with its identity as far as read (or "a row whose Identity could not be
     read: <message>") and the message, `when (!IsCancellation(ex))`; the rest load. A failed query still throws.
   - Page: 1639 (blank) the `unreadableMailboxes` field; `ReplaceBatchUsers` (3962) takes the list and deconstructs it
     on a line freed by rewrapping its comment (3964-3973, ten lines to nine) - `batchUsers = users;` (3978) stays as
     written, pinned by `MigrationStatusPageTests` - and assigns `unreadableMailboxes` at 3980 (blank); the two failure
     calls (1885, 1948) pass an empty list. 1026 (blank): the warning, `alert-warning`, `role="alert"`, when the list
     is loaded and any row was unreadable. 1049, 1054: neither "N pending. Batch not started." nor "This batch
     contains no mailboxes." when rows were unreadable; 1060 then reads "No mailbox in this batch could be read; the
     warning above names each one." Unreadable rows are not in `batchUsers`, so no tick box, planner or action
     reaches them.
2. **Progress: every load completes its activity with its real outcome** - not succeeded, with the failure text (the
   aud-a26h drain precedent; from a local where an await sits between the failure and the `finally`):
   `LoadBatchList` (2304, from `batchListFailure`, which both paths write with no await before the `finally`);
   `LoadMailboxesFor` (1878 local; 1883 `catch (Exception ex)`; 1886-1887 become `mailboxLoadFailed |= ...` and the
   failure text; 1893); `SyncOpenBatchFromUrl` (1938, 1946, 1949-1950, 1956, the same); `CheckSingleEligibility`
   (1973 local; 1977 an Undetermined result; 2006 the catch; 2013); `CheckBulkEligibility` (2114; 2123 any
   undetermined row - "N of M eligibility checks could not be determined"; 2150 the catch; 2157); `SearchUser` (3844;
   3869 and 3896, the two "Search failed" results - "no match" and "several match" are answers; 3905);
   `FetchUserReport` (4040; 4064-4065, the failure recorded before the superseded-load return; 4078). The comment at
   1555-1562 says so, rewritten in its eight lines. The writes still complete true (ENDED): not this finding.
   `ActivityProgressService.Finish` discards both today; the handle is still the module's statement of how it ended.
3. **`ExchangeServiceBase.Invoke`** (227-237): `HadErrors` with an empty stream fails with new `internal const
   ErrorNotSurfaced` - the command flagged an error but surfaced none, which an error suppressed by `-ErrorAction
   SilentlyContinue` or `Ignore` (on a command it ran, or on itself) causes - not "An unknown error occurred.". Still a
   failure. The text names neither a connection nor a session, so `IsConnectionError` (message-based) still reads it
   as no connection fault.
4. **`RunAsync`'s failure message** (74): through new `internal static ResolveFailure(Exception)` beside
   `ResolvePsErrors` (194): an `ActionPreferenceStopException` with a record - the exception, or one level in, which is
   `Invoke`'s wrapper - gives the record's own text, any captured records as the detail; otherwise `ResolvePsErrors`,
   as before. One place, every caller. `MigrationService.FailureReason` (750-751) goes; `GetMigrationBatchesAsync`
   (729) and aud-a23h's callers use `ResolveFailure(ex).primary`.
5. **Export file names in UTC, with `Z`** (`yyyyMMdd_HHmmss'Z'`, invariant culture): the batch CSV (4238), the report
   text (4314: the report's UTC fetch time, no `ToLocalTime()`), the report zip (2995).

**Tests** (fakes only):
- `MigrationBatchReadTests`, extended: a mailbox row with no Identity and a blank one beside readable rows - not
  listed, unreadable, no "Unknown" mailbox; a row whose Status getter throws, and one whose Identity getter throws -
  unreadable with identity and reason, the rest load; through the page's `LoadMailboxesFor`, the warning, and no row
  for them in `batchUsers`; only unreadable rows - "No mailbox in this batch could be read", not "contains no
  mailboxes"; a cancellation while reading a mailbox row escapes. `GetMigrationBatchUsersAsync`'s callers in
  `MigrationBatchReadTests`, `MigrationMailboxLoadTests` and `MigrationReportedTimeTests` take `.Mailboxes`,
  expectations unchanged.
- new `MigrationLoadProgressTests`: a recording `IActivityProgress` whose first Complete wins, as the real one's: each
  load above, failing - not succeeded, with the failure text; succeeding - succeeded.
- `ExchangeServiceBaseInvokeTests`: a nested `-ErrorAction SilentlyContinue` error - `ErrorNotSurfaced`.
- new `ExchangeServiceBaseRunFailureTests`, the real pool over stub runspaces: outside Migration,
  `BlockedSenderService.UnblockSenderAsync` over a `Remove-BlockedSenderAddress` that writes an error under Stop - the
  message is the error's own text, no "The running command stopped"; one that only suppresses a nested error -
  `ErrorNotSurfaced`; inside, `MigrationService.StopMigrationUserAsync` the same. aud-a23g's `AFailedBatchQuery...`
  stays green.
- new `MigrationExportFileNameTests`, a recording `IJSRuntime`: each name `..._\d{8}_\d{6}Z.<ext>`, the stamp inside
  the run's UTC window (this host is Eastern, so the server's clock is hours off); the report text's stamp is its fetch
  time exactly.

**Mutations at the call site** (md5 before/after, failing tests named, restored, md5 equal, touched, rebuilt, green):
I3 (the brief's (3)) `?? "Unknown"` identity again; I4 (the brief's (4)) `LoadBatchList` completes true again; I5 (the
brief's (5)) `RunAsync` back to `ResolvePsErrors`. Extras: I3b the per-row catch gone; I3c the warning line blank
again; I3d the unreadable empty-state text dropped; I4b `LoadMailboxesFor` true again; I4c `CheckSingleEligibility`
true again; I5b `ResolveFailure` without its Stop branch; I6 "An unknown error occurred." again; I7 the CSV name on the
server's clock again; I7b the report text's name `ToLocalTime()` again.

**Gates** as aud-a23h. Comments written after the mutation round.

## Guard proof

Built as the Plan, with one correction the tests forced, below. Line numbers are at the fix commit's bytes, which carry
no new code comments (the comments commit follows and moves the `.cs` lines; the page's comments are trailing, so its
lines stay). `Components/Pages/Migration.razor` is 4377 lines before and after: `git diff --numstat` 48/48, every
changed region the same length on both sides (git shows 4063-4065 as +1 at 4064 and -1 at 4065: the failure text took
the blank line and the superseded-load return moved up one); none of the edited lines is registered.

**Found by the tests, fixed before the round.** The Plan's `ResolveFailure` looked one level in for any wrapper. The
approval's own composed failures (aud-a23h) wrap `InvokeOptional`'s original exception, so that reading took the inner
error's words and dropped the composed message: aud-a23h's `AnExistenceReadThatFails_ChangesNothing` (written error)
failed, "the account lacks the Mail Recipients role" where it expected "Could not tell whether ...". It now looks
through a wrapper only when the wrapper restates the exception it holds - what `Invoke`'s does under Stop, with nothing
on the error stream - so a wrapper that says more keeps its words. New tests pin both readings; I5c below.

- `Services/ExchangeServiceBase.cs` (base app):
  - `ErrorNotSurfaced` (173); `Invoke`'s `HadErrors` branch (251) fails with it where it said "An unknown error
    occurred.".
  - `ResolveFailure` (209): an `ActionPreferenceStopException` - the exception, or the one a restating wrapper holds -
    gives its record's own text, any captured records as the detail; otherwise `ResolvePsErrors`, as before.
  - `RunAsync` (74): `ResolveFailure(ex)` where it took `ResolvePsErrors(ex)`; the stream fallback below it unchanged.
- `Services/MigrationService.cs`: `FailureReason` gone; its five callers (150, 740, 985, 1063, 1082) take
  `ResolveFailure(ex).primary`. `GetMigrationBatchUsersAsync` (809-) returns `MigrationMailboxList`; per row (829-869)
  the Identity first, absent or blank - unreadable, "a row with no Identity" (836); a row that throws - unreadable with
  its identity, or "a row whose Identity could not be read: ...", `when (!IsCancellation(ex))` (861); the rest listed
  (871). A failed query still throws.
- `Models/MigrationModels.cs`: `UnreadableMigrationMailbox` (239: `Shown`, `Warning`), `MigrationMailboxList` (247).
- `Components/Pages/Migration.razor`, in place: 1026 the warning; 1049, 1054 neither "not started" nor "contains no
  mailboxes" with unreadable rows; 1060 "No mailbox in this batch could be read; the warning above names each one.";
  1555-1562 the progress comment; 1639 `unreadableMailboxes`; `LoadMailboxesFor` 1878, 1883, 1885-1887, 1893;
  `SyncOpenBatchFromUrl` 1938, 1946, 1948-1950, 1956; `CheckSingleEligibility` 1973, 1977, 2006, 2013;
  `CheckBulkEligibility` 2114, 2123, 2150, 2157; `LoadBatchList` 2304; the zip's name 2995; `SearchUser` 3844, 3869,
  3896, 3905; `ReplaceBatchUsers` 3962, 3972-3973, 3980 (`batchUsers = users;` at 3978 as written); `FetchUserReport`
  4040, 4064, 4078; the CSV's name 4238; the report's name 4314.

New tests (34 cases): `MigrationLoadProgressTests` (7 tests, 17 cases), `MigrationExportFileNameTests` (3),
`ExchangeServiceBaseRunFailureTests` (5); `MigrationBatchReadTests` +4 (6 cases), `ExchangeServiceBaseInvokeTests` +2,
`MigrationEligibilityAndApproveTests` +1. `MigrationStubExchange` gains `Pool()`, `Trace`, a `Remove-BlockedSenderAddress`
stub, the `ReadBackRefused` and `NestedSuppressedError` switches, a recording progress channel and a recording JS
runtime. `GetMigrationBatchUsersAsync`'s callers in `MigrationBatchReadTests`, `MigrationMailboxLoadTests` and
`MigrationReportedTimeTests` take `.Mailboxes`, expectations unchanged.

| Test | Pins |
| --- | --- |
| `MigrationBatchReadTests.AMailboxRowWithNoIdentity_IsUnreadable_NotAMailboxNamedUnknown` | **part 1, the brief's**: an absent and a blank Identity beside a readable row - one mailbox listed, two unreadable "a row with no Identity"; through the page's `LoadMailboxesFor`, "2 mailboxes could not be read: ...", only the readable row in `batchUsers`, no "Unknown" on the page |
| `AMailboxRowThatThrowsWhileBeingRead_IsListedWithItsReason_AndTheRestLoad` | a row whose Status getter throws: its identity and the message; one whose Identity getter throws: "a row whose Identity could not be read: ..."; the rows around them listed |
| `ABatchWhoseEveryMailboxRowIsUnreadable_SaysNoneCouldBeRead_NotThatItIsEmpty` (a count of none; pending, not started) | "No mailbox in this batch could be read; the warning above names each one.", neither "This batch contains no mailboxes." nor "Batch not started" |
| `ACancellationWhileReadingAMailboxRow_EscapesTheLoad` (`OperationCanceledException`, `PipelineStoppedException`) | the load throws with the cancellation in its chain - not an unreadable row |
| `MigrationLoadProgressTests.LoadBatchList_CompletesWithItsOutcome` (fails, reads) | **part 2, the brief's**: (false, "The migration batches could not be read: <message>"), (true, null) |
| `LoadMailboxesFor_...`, `SyncOpenBatchFromUrl_...` (fails, reads) | (false, "Unable to load mailboxes in <batch>: ...<message>"), (true, null); the URL entry through a test renderer that reports itself interactive |
| `CheckSingleEligibility_...` (undetermined, eligible, ineligible, throws before the service returns) | not succeeded, with the reason, only when no answer was reached; an ineligible answer is a check that succeeded |
| `CheckBulkEligibility_...` (one row undetermined, every row answered, the file cannot be read) | (false, "1 of 2 eligibility checks could not be determined"), (true, null), (false, "The bulk eligibility check failed: ...") |
| `SearchUser_...` (fails, finds nothing) | a search that could not run failed; "no batch or user found" is an answer |
| `FetchUserReport_...` (fails, fetches) | (false, "The report for <address> could not be fetched: ..."), (true, null) |
| `ExchangeServiceBaseInvokeTests.Invoke_AnErrorThatWasSuppressed_IsReportedAsSuch_NotAsUnknown` | **part 3**: a nested `-ErrorAction SilentlyContinue` error - `ErrorNotSurfaced`; commands and stream cleared |
| `ResolveFailure_GivesAStoppedErrorsOwnWords_ThroughInvokesWrapper_NotThroughOneThatSaysMore` | **part 4** over real `Invoke` output: the Stop exception and `Invoke`'s wrapper give the error's own words; a wrapper that says more keeps its own |
| `ExchangeServiceBaseRunFailureTests.OutsideMigration_AnErrorTheCmdletWroteUnderStop_IsReportedInItsOwnWords` | **part 4 outside Migration**: `BlockedSenderService.UnblockSenderAsync` - the message is exactly the error's text |
| `OutsideMigration_AThrownError_IsReportedAsBefore`, `ACleanWrite_StillSucceeds` | controls: a thrown error's message unchanged; a clean write still succeeds |
| `OutsideMigration_AnErrorThatWasSuppressed_IsReportedAsSuch_NotAsUnknown` | part 3 through `RunAsync`: `ErrorNotSurfaced` |
| `InsideMigration_AnErrorTheCmdletWroteUnderStop_IsReportedInItsOwnWords` | `StopMigrationUserAsync`: the error's text, no Stop prefix |
| `MigrationEligibilityAndApproveTests.AReadBackThatFails_CouldNotBeConfirmed_InTheApprovalsOwnWords` | the correction: the approval's "could not be confirmed: reading it back failed: <message>" survives `RunAsync`; aud-a23h's `AnExistenceReadThatFails_ChangesNothing` is the test that caught it |
| `MigrationExportFileNameTests.TheBatchListCsv_IsNamedInUtc_AndSaysSo`, `TheReportZip_IsNamedInUtc_AndSaysSo` | **part 5**: `Migration_yyyyMMdd_HHmmssZ.csv`, `MigrationReports_Wave_12_yyyyMMdd_HHmmssZ.zip`, the stamp inside the run's UTC window (this host is Eastern) |
| `TheOpenReport_IsNamedForWhenItWasTaken_InUtc` | `MigrationReport_alice_contoso.test_20260102_030405Z.txt` for a report taken at 2026-01-02 03:04:05 UTC |

**Against the unfixed code: not runnable as a whole** - the service's return type and the base class's members changed,
so the tests cannot compile against aud-a23h's commit. Each behaviour was put back at its own call site instead.

Mutations at the call site, by the same driver and protocol as aud-a23h's (exact-text replacement asserted to match
once, md5 recorded, Release build, the focused tests run with failures read from the TRX, the file restored from its
saved bytes, touched, md5 re-confirmed equal, rebuilt, the focused tests re-run green - after every mutation), over the
1056 tests of `Migration*|ClickGate*|ProgressRegistry*|ExchangeServiceBase*|AuditService*|GlobalProgressWiring*|BlockedSender*`.
Pre-comment bytes (LF): service `023f4339cf53e4918d1ab58bfc9be9c0`, base `37bfb69e452942bffdda99a8c2661662`, page
`44301bebd87172b553139795adedcfe6`.

| Mutation | File | md5 after | Failed (of 1056) | Restored, rerun |
| --- | --- | --- | --- | --- |
| I3 (the brief's (3)): `?? "Unknown"` on the mailbox row's Identity again | service | `029b819d381d393c22b80efb99ceb9e4` | 3: `AMailboxRowWithNoIdentity...`, `ABatchWhoseEveryMailboxRowIsUnreadable...` (both) - a mailbox "Unknown" again | equal; 1056/0 |
| I4 (the brief's (4)): `LoadBatchList` completes true on failure again | page | `c6d36216920d3863352cb054762cc3fc` | 1: `LoadBatchList_CompletesWithItsOutcome` (fails) | equal; 1056/0 |
| I5 (the brief's (5)): `RunAsync` back to `ResolvePsErrors` - no unwrapping in the base | base | `569315c94944f6b126b323d087d98d7a` | 2: `OutsideMigration_AnErrorTheCmdletWroteUnderStop...` (Blocked Senders), `InsideMigration_...` (Migration) - the Stop prefix again | equal; 1056/0 |
| I3b: the per-row catch never catches (`when (ex is null)`) | service | `a0036356f106905c7b2f32070c618a72` | 1: `AMailboxRowThatThrowsWhileBeingRead...` - the list lost again | equal; 1056/0 |
| I3c: the warning line blank again | page | `ccd8ab139aa6190a0ddbb7742411c272` | 3: `AMailboxRowWithNoIdentity...`, `ABatchWhoseEveryMailboxRowIsUnreadable...` (both) | equal; 1056/0 |
| I3d: "contains no mailboxes" without its unreadable-rows guard | page | `11f3b349edf342fd8498f55556c24e31` | 1: `ABatchWhoseEveryMailboxRowIsUnreadable...` (a count of none) | equal; 1056/0 |
| I4b: `LoadMailboxesFor` completes true | page | `b16f45a43b309e7b7251e9e197d116f2` | 1: `LoadMailboxesFor_...` (fails) | equal; 1056/0 |
| I4c: `CheckSingleEligibility` completes true | page | `2ce75e548cb0e9d46a1c79a2a00a8f59` | 2: `CheckSingleEligibility_...` (undetermined, throws) | equal; 1056/0 |
| I4d: `SearchUser` completes true | page | `cd4ec016fb3a86e061a546301f7c0a86` | 1: `SearchUser_...` (fails) | equal; 1056/0 |
| I4e: `FetchUserReport` completes true | page | `48d55fe9e6eb999ecf9ff34f32c23c63` | 1: `FetchUserReport_...` (fails) | equal; 1056/0 |
| I4f: `CheckBulkEligibility` completes true | page | `97488d45431f73855b3e75886e6d09ee` | 2: `CheckBulkEligibility_...` (undetermined row, file unreadable) | equal; 1056/0 |
| I4g: `SyncOpenBatchFromUrl` completes true | page | `0199df6dcbfa56d2269472cdce931441` | 1: `SyncOpenBatchFromUrl_...` (fails) | equal; 1056/0 |
| I5b: `ResolveFailure` without its Stop branch | base | `e7729274f64f297e326e90015fe29c2a` | 15: both `...UnderStop...` callers, the `ResolveFailure` unit test, aud-a23g's `AFailedBatchQuery_IsShownAsAFailure_NeverAsNoBatches` (written error) - its unwrapping now lives here - and every aud-a23h reason taken from a written error | equal; 1056/0 |
| I5c: `ResolveFailure` sees through any wrapper (the Plan's first reading) | base | `fa0058c5bfd3981e992b72c733032b16` | 3: the `ResolveFailure` unit test, `AnExistenceReadThatFails_ChangesNothing` (written error), `AReadBackThatFails...` - a composed message lost | equal; 1056/0 |
| I6: "An unknown error occurred." again | base | `2b0868be033e5a76efdd3f622c81918d` | 2: `Invoke_AnErrorThatWasSuppressed...`, `OutsideMigration_AnErrorThatWasSuppressed...` | equal; 1056/0 |
| I7: the CSV named on the server's clock again | page | `c9469bdce212733c672a5f7c81721655` | 1: `TheBatchListCsv_IsNamedInUtc_AndSaysSo` | equal; 1056/0 |
| I7b: the report text named `ToLocalTime()` again | page | `80e6caeb2205676d0f462e3139993765` | 1: `TheOpenReport_IsNamedForWhenItWasTaken_InUtc` | equal; 1056/0 |
| I7c: the zip named on the server's clock again | page | `db9b7e401d9769b2725bb6981e058012` | 1: `TheReportZip_IsNamedInUtc_AndSaysSo` | equal; 1056/0 |

I3, I4 and I5 are the brief's three. md5s (LF) of the other files at the fix commit: `Models/MigrationModels.cs`
`ab9865e3cb5f4b101c47b445e2d06da1`; tests `MigrationLoadProgressTests.cs` `9185fbef38824ca271184bfb66f88d0a` (the round
ran on `88fe97456aab4b68c9297df9f0a5acd1`; the format gate then asked for its test renderer to sit inside `#pragma warning
disable BL0006`, as the other test renderers do - no test changed),
`MigrationExportFileNameTests.cs` `434bf6a35569af4bed917fed40e440b7`, `ExchangeServiceBaseRunFailureTests.cs`
`dc3b7287ff9951b6d29f6140be4d8587`, `ExchangeServiceBaseInvokeTests.cs` `f2cea6690a6e8ae8330f52af8c73d823`,
`MigrationBatchReadTests.cs` `916e7605da2489b66f8560309b2471bb`, `MigrationMailboxLoadTests.cs`
`e4d4cdd0a538fcbd159abbd560d01b70`, `MigrationReportedTimeTests.cs` `c00a9ea3cfa15f44a298c275147e5f54`,
`MigrationStubExchange.cs` `6bf1c2c22a09a0dcb467cf4825097d42`, `MigrationEligibilityAndApproveTests.cs`
`c6f85d82eb2481033304fac1270dfa45`.

**Pins.** `ClickGateRegistry`: 4377 lines before and after; no registered line among the edits (the registered lines
run to 1540; 1026, 1049, 1054 and 1060 are not among them) and no registered snippet changed; `ExpectedLineCount`
untouched - the `ClickGate*` tests pass. `ProgressRegistry`: no handler, lifecycle method or `Complete(` added - each
load's `Complete(` is the one it had, in its `finally`, now passing the outcome - so every Reported entry still pins its
call inside its window. `MigrationStatusPageTests` pass unchanged: `batchUsers = users;` is still the one row
assignment, behind the generation guard and the report close; both fetches are still handed straight to
`ReplaceBatchUsers(generation, await ...)`; the report's error write still sits behind its generation check.

## Gates (the fix commit's tree, on `5b5936eb`)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors.
- `dotnet test ExchangeAdminWeb.slnx -c Release --no-build --blame-hang --blame-hang-timeout 10m`: 5658 passed, 3 skipped
  (the known `ADDirectorySearchServiceTests ...WhenAdUnavailable` cases), 0 failed - 5661 in all, 34 of them new; 5 m 4 s,
  no hang. On aud-a23h's commit: 5624 / 3 / 0, 5627.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0, once the test renderer sat inside its
  `BL0006` pragma (the first run reported the warning). `git diff --check HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` after
  `git add -A`: exit 0.
- No PowerShell source changed, so PSScriptAnalyzer and Pester were not run.

## Known gaps

- **The outcome is shown nowhere today.** `ActivityProgressService.Finish` discards the flag and the message (the frame
  is live only, owner ruling 2026-10-02); what changed is the page's own statement of how each load ended, read here
  through a recording channel. The page's writes still complete true (ENDED) - not this finding.
- **The Stop prefix is gone from `RunAsync`'s message only**, as briefed. Callers that format `ex.Message` themselves
  still carry it under Stop: `FindMigrationUserBatchAsync` ("Search failed: The running command stopped ..."),
  `GetMigrationUserReportAsync`, and the page's mailbox-load and report catches (activity text only). Candidate.
- **Behaviour outside Migration (base app):** every `RunAsync` caller's failure message for an error a cmdlet wrote
  under Stop is now that error's text - Blocked Senders, Calendar Permissions, Conference Rooms and Out of Office as well
  as Migration - and every `Invoke` caller's suppressed-error failure reads `ErrorNotSurfaced`. A message a caller
  composed around the exception is kept.
- `ErrorNotSurfaced` names the shape this host's SDK showed (a suppressed error sets `HadErrors` and writes no record),
  not every possible cause; it is still a failure, as before.
- **A mailbox row that throws is the backstop, not the path**: a deserialized row has no property that can throw. The
  identity-less row is the case a module could plausibly return.
- **The export names change form** - `Migration_<UTC>Z.csv`, `MigrationReports_<batch>_<UTC>Z.zip`,
  `MigrationReport_<address>_<UTC>Z.txt` - so a folder sorted by the old local-time names sees a jump of the UTC offset.
- No circuit or browser ran: the page's handlers and compiled render tree were driven directly, the URL entry through a
  test renderer that reports itself interactive.

## Comments (`033a1a54`)

Written after both mutation rounds, at the call sites they proved load-bearing; comments only - every changed `.cs`
line a comment or blank, and each of the page's 17 changed lines the line it was plus a trailing comment, so its 4377
lines and every registered line key hold. Final md5s (LF) at the branch tip: `Services/MigrationService.cs`
`1899365d604775f7672321f08c7f32ee`, `Services/ExchangeServiceBase.cs` `e06caa1c89eacfdbbd0bc548329f6671`,
`Components/Pages/Migration.razor` `5bcc6493e840f35baec9c4ee39eb5e24`, `Models/Enums.cs`
`f1b3966c785807b77bf1d5cfb777c6a2`, `Models/MigrationModels.cs` `a78d64044b4415fa1b779d7ded4b68fb`; the tests are as
their fix commits left them. Focused rerun after the comments: 1056 / 0.

## Branch-tip gates (`033a1a54`: both fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings - the count at `34c7ccd4`.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): 5658 passed, 3 skipped (the known `ADDirectorySearchServiceTests ...WhenAdUnavailable` cases), 0 failed - 5661 in all, 66 of them new across both findings; 5 m 22 s, no hang. Baseline at `34c7ccd4`: 5592 / 3 / 0, 5595.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

No live Exchange, Active Directory, Graph, DHCP or IIS call was made: every Exchange cmdlet was a stub in an in-process
runspace, and no excluded-group setting was configured, so the eligibility check's Active Directory step never ran. No
PowerShell source changed (PSScriptAnalyzer and Pester not run). No coder self-review verdict is claimed. Version bumps
are the supervisor's: Migration for both findings, and the base app for aud-a23i's `ExchangeServiceBase` change.

## Closeout

Fixed on branch `worktree-agent-a3e674616251be4de`; not merged, not pushed.
