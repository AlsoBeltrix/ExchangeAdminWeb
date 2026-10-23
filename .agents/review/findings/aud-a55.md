# aud-a55: Migration - approving a mailbox that is not in its own batch writes without reading any status, the bulk executors' "no list loaded" guards show nothing, and a cloud archive read with no rows reads as no archive

**Severity**: MEDIUM (part 1), LOW (parts 2 and 3). (1) `ApproveMigrationUserAsync` for a mailbox in a
batch of many (no batch named for it) runs `Set-MigrationUser -CompleteAfter`, `Set-MoveRequest` and
`Resume-MoveRequest` without reading the mailbox's or its batch's status; only the UI planner (Approve
offered on NeedsApproval) gates it, so a stale page or a second operator can approve a mailbox whose state
no longer allows it (the aud-a23r class for the many-user case). (2) `Migration.razor`'s bulk executors
return silently on "no list loaded" guards. (3) `MigrationService.GetCloudMailboxSizeAsync` reads an archive
read that returns no rows and no error as no archive (minor: no decision reads the cloud size).
**Status**: Merged `671d9bc9`; closed on guard proof; codex review pending
**Branch**: `worktree-agent-ae9cc385925087b25` (forked at `a7300dee`)
**Commit**: `d2bcd9c2` (fix, tests, record); plan `3113f6f0`; comments `2e2218e4` (shared with aud-a54)
**Reviewer**: implementer (surfaced by the aud-a23r/o/q/p agent, 2026-10-22, candidates 3, 4 and 5 of its hand-back)

## Evidence

At `d690a6c1`: `Services/MigrationService.cs` `ApproveMigrationUserAsync` from around line 1308 (the writes at
around 1387), `GetCloudMailboxSizeAsync` (the archive read's no-row case); `Components/Pages/Migration.razor`
around 3545 and 3713 (the guard returns) - line-keyed by `ClickGateRegistry`, 4377 lines, edit in place only;
`Services/MigrationBatchActionPlanner.cs` (what Approve requires of a migration user's status).

## Approach (to be written by the implementing agent before code)

- The many-user approval reads the migration user's status (`Get-MigrationUser`) before any write and
  refuses, naming the status and that nothing was changed, unless the planner's Approve rule accepts it (the
  planner is the one owner; call it); a failed read refuses (the aud-a23h shape).
- The two guard returns set the banner and the aud-a23m local ("no mailbox list is loaded; nothing was done")
  through the aud-a23q refusal helpers; not audited (nothing attempted).
- The archive read with no rows and no error is "could not be read: Exchange returned no statistics row"
  (the aud-a23k marker), not no archive.
- Tests through `MigrationStubExchange`; mutations at the call site: the status read removed; one guard
  return silent again; no-rows as no-archive again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `59061f01`. One commit `fix(migration): ... (aud-a55)`
on branch `worktree-agent-ae9cc385925087b25` (forked at `a7300dee`), before aud-a54. Module-scoped: Migration (the bump is
the supervisor's; `Modules/ModuleCatalog.cs`, `ExchangeAdminWeb.csproj` not edited). `Services/ExchangeServiceBase.cs` is
read and its helpers called, not changed. `Components/Pages/Migration.razor` in place: 4377 lines before and after, two
lines each replaced by one line, neither registered (the registered lines run to 1540). Line numbers at `a7300dee`.

**Naming drift, settled by reading the files.** The record's Evidence names `MigrationBatchActionPlanner` as "what Approve
requires of a migration user's status"; that planner has no Approve action (Delete, RemoveCompleted, Resume, Complete,
Stop, Schedule). The rule the finding means - "Approve offered on NeedsApproval" - is
`MigrationUserActionPlanner.Applies(MigrationUserAction.Approve, status)` (`Services/MigrationUserActionPlanner.cs` 94-95,
exactly NeedsApproval, trimmed, ignoring case), the one the page's per-row Approve button and the mailbox bar call. That
is the planner called here; its rule is not copied.

1. **Part 1, the many-user approval reads the migration user's status before any write**
   (`Services/MigrationService.cs` `ApproveMigrationUserAsync`, 1308-1407). After the existence read (1322-1340) answers
   that the mailbox has no batch of its own (`hasOwnBatch` false, 1345), and before the first `Set-MigrationUser` (1354),
   new `RefuseUnlessAwaitingApproval` reads `Get-MigrationUser -Identity <address> -ErrorAction Stop` through the base's
   `ReadUnlessNotFound` and throws, before any write:
   - a read that fails: "Could not read the migration status of <address>: <reason>. Nothing was changed." (the
     existence read's shape, 1330-1334; the reason in the cmdlet's own words, `ResolveFailure`);
   - none - Exchange's not-found, by structure: "<address> was not approved: Exchange has no migration user <address>, so
     there is nothing to approve. Nothing was changed.";
   - more than one: "Exchange returned N migration users for <address>. Nothing was changed.";
   - no status (absent or blank): "<address> was not approved: its migration user reports no status, so whether it is
     awaiting approval is not known. Nothing was changed.";
   - a status the planner's Approve rule does not accept: "<address> was not approved: its migration status is <Status> -
     not a status the Approve action approves a mailbox from. Nothing was changed."

   Otherwise the writes run as today. `RunAsync` returns a refusal as the row's failure, so both callers' existing audits
   (`ExecuteUserAction`, `ExecuteBulkMailboxAction`) record it Failed with its text, and the bulk run puts it on the row.
   Unchanged: a mailbox in a batch of its own (its batch's status gates it, aud-a23r; no user read is added there), the
   writes and their order, the Detail. The status is read as the page's list reads it
   (`GetMigrationBatchUsersAsync` 1182: `Properties["Status"]`, `ToString()`).
   **Assumptions, labelled (no live call):** `Get-MigrationUser -Identity` reports the Status value the batch's mailbox
   list reads from `Get-MigrationUser -BatchId`, so a mailbox the page offers Approve on (NeedsApproval) reads
   NeedsApproval here; and Exchange Online's not-found for `Get-MigrationUser -Identity` is one of the shapes `IsNotFound`
   reads (aud-a23h's assumption, same cmdlet). If either is wrong, an approval the page offers is refused, naming what
   Exchange said - visible and fail closed, never a write from a state nobody checked.
2. **Part 2, the bulk executors' "no list loaded" guards say so** (`Components/Pages/Migration.razor` 3545
   `ExecuteBulkBatchAction`, 3713 `ExecuteBulkMailboxAction`). Each `if (... == null) return;` becomes one statement on its
   line: `if (MigrationService.RefusesWithNoListLoaded(<list>, "<name>", out var <refusal>)) { batchActionResult =
   PermissionResult.Fail(bulkFailure = <refusal>); return; }` - the banner and aud-a23m's local (`bulkFailure`, which the
   existing `finally` hands to `activity.Complete`) set to the refusal. New `MigrationService.RefusesWithNoListLoaded`
   (internal static, the aud-a23q refusal helpers' shape: `bool`, the text as an `out`, `NotNullWhen`): "No migration batch
   list is loaded; nothing was done." / "No mailbox list is loaded; nothing was done." Not audited: nothing was attempted
   (the executors' own "nothing to do" rule). Before: no banner, and the activity ended "Nothing was done." with nothing on
   screen to say why.
3. **Part 3, a cloud archive read with no row is a marker, not no archive** (`GetCloudMailboxSizeAsync`, 1597-1612). The
   archive read keeps its invoke (`InvokeOptional`, which `ReadUnlessNotFound` ran it through) and its catch for any other
   failure; Exchange's not-found, by structure (`IsNotFound`, caught first), stays no archive (logged, as the on-premises
   read logs it); a read that answers with no row is `ReadSize`'s marker "Exchange returned no archive statistics" (the
   aud-a23k marker already passed there, and what aud-a23o's on-premises read reports for the same answer) - where
   `if (archive.Count > 0)` made it no archive, because `ReadUnlessNotFound` returns no rows for both. The record's
   "Exchange returned no statistics row" is a paraphrase; the code's existing words are kept. No decision reads the cloud
   size (aud-a23k): the page shows "could not be read (Exchange returned no archive statistics)".

**Tests** (fakes only; `MigrationStubExchange`'s `Get-MigrationUser -Identity` already answers `MigrationUsers`):
- `MigrationApproveConfirmationTests`, a new section for part 1:
  - `AMailboxInABatchOfMany_WhoseStatusMovedOn_IsRefusedBeforeAnyWrite` (Synced, Completed, Completing, Failed, Stopped,
    " stopped "): **the finding** - the stale page's case: refused naming the status, "Nothing was changed.", the two reads
    the only calls. Before: every mailbox write ran.
  - `AMailboxInABatchOfMany_IsApprovedExactlyWhenTheApproveActionWould` (a list of statuses, NeedsApproval, " needsapproval
    " and "NEEDSAPPROVAL" among them): one owner - the mailbox steps exactly when `MigrationUserActionPlanner.Applies(Approve,
    status)` is true, the refusal and only the two reads otherwise.
  - `AMigrationUserThatReportsNoStatus_IsRefused_AndNothingIsWritten` (absent, empty, blank).
  - `AStatusReadThatFails_ChangesNothing` (thrown, written).
  - `NoMigrationUser_IsRefused_AndNothingIsWritten` (Exchange's not-found by category, type and identifier).
  - `SeveralMigrationUsers_AreRefused_AndNothingIsWritten`.
  - `AStaleBulkApproval_IsRefusedOnItsRow_AndAuditedFailed`: through `ExecuteBulkMailboxAction`, the loaded list says
    NeedsApproval and Exchange now says Synced - the row's outcome is the refusal, one `ApproveMigrationUser` audit row,
    Failed, the refusal as its error, the banner a failure, only the two reads.
  - `ARefusalThroughExecuteUserAction_IsOneFailedAuditRow`.
- `MigrationEligibilityAndApproveTests.AMailboxNotInABatchOfItsOwn_IsApprovedWithoutTouchingABatch_AndSaysSo` (aud-a23h)
  gains a migration user at NeedsApproval and the new read in its call list (`ApprovalSteps(ownBatch: false)`); the
  own-batch call lists are unchanged (no user read there) and so are their tests.
- `MigrationDenialTests`, part 2: `ABulkExecutorWithNoListLoaded_SaysSo_AndNothingRuns` (ExecuteBulkBatchAction,
  ExecuteBulkMailboxAction) - the banner's text, rendered on the status tab; the activity (false, the text); no audit row;
  the action never called.
- `MigrationReportAndSizeReadTests`, part 3: `AnArchiveReadWithNoRowAndNoError_IsMarked_NotNoArchive` (the marker; the
  mailbox read as 1.5; the page "could not be read (Exchange returned no archive statistics)"), and
  `NoArchive_IsNoArchive_ByEachNotFoundShape` (category, type, identifier) beside the existing `NoArchive_IsNoArchive`.

**Mutations at the call site** (md5 before/mutated/after, failing tests named, restored from git, touched, rebuilt, md5
equal, filtered tests green): A1 (the record's) the status read removed - the many-user path writes without
`RefuseUnlessAwaitingApproval`; A2 (the record's) one guard return silent again - `ExecuteBulkMailboxAction`'s 3713; A3
(the record's) no rows as no archive again. Extras: A1b the planner's rule copied as a refuse list (Completed, Failed,
Stopped) instead of calling the planner; A1c a status read that fails let through to the writes; A2b
`ExecuteBulkBatchAction`'s 3545 silent again.

**Gates** as the brief (`ClickGate*`/`ProgressRegistry*` green, `wc -l` 4377); comments after the mutation round, in their
own commit.

## Guard proof

Built as the Plan. Line numbers are at the fix commit's bytes, which carry no new code comments (they come in their own
commit after the last finding's round). `Components/Pages/Migration.razor` is 4377 lines before and after: `git diff
--numstat` 2/2, each changed line replaced in place (3545, 3713); neither is registered (the registered lines run to 1540),
no registered snippet changed, no handler, lifecycle method or `Complete(` added.

- `Services/MigrationService.cs`:
  - Part 1: `ApproveMigrationUserAsync` (1314-): a mailbox with no batch of its own goes through new
    `RefuseUnlessAwaitingApproval` (called at 1358, defined at 1472) before the first `Set-MigrationUser`:
    `Get-MigrationUser -Identity <address> -ErrorAction Stop` through `ReadUnlessNotFound`; a read that fails, none,
    several, no status, and a status `MigrationUserActionPlanner.Applies(MigrationUserAction.Approve, status)` (1507) does
    not accept each throw their refusal, ending "Nothing was changed.". A mailbox in a batch of its own is unchanged
    (`RefuseUnlessApprovable`, aud-a23r; no user read added there).
  - Part 2: `RefusesWithNoListLoaded` (1016): "No <list> list is loaded; nothing was done.", a `bool` with the text as an
    `out`, `NotNullWhen` both ways (the aud-a23q helpers' shape).
  - Part 3: `GetCloudMailboxSizeAsync` (1629-): the archive read is `ReadSize(InvokeOptional(ps, tracker), "Exchange returned
    no archive statistics")` (1655); Exchange's not-found, by structure, caught first (1657, `IsNotFound`) is no archive,
    logged; any other failure as before. `ReadUnlessNotFound` gave no rows for both, and `if (archive.Count > 0)` made both
    no archive.
- `Components/Pages/Migration.razor` 3545 (`ExecuteBulkBatchAction`) and 3713 (`ExecuteBulkMailboxAction`): `if
  (MigrationService.RefusesWithNoListLoaded(<list>, "<name>", out var <refusal>)) { batchActionResult =
  PermissionResult.Fail(bulkFailure = <refusal>); return; }` - the banner and aud-a23m's local, which the existing `finally`
  hands to the activity. Not audited.

`ExchangeAdminWeb.Tests/MigrationApproveConfirmationTests.cs`, part 1 - 8 new tests, 36 cases:

| Test | Pins |
| --- | --- |
| `AMailboxInABatchOfMany_WhoseStatusMovedOn_IsRefusedBeforeAnyWrite` (Synced, Completed, Completing, Failed, Stopped, " stopped ") | **the finding**: each asserted first to be a status the Approve action does not accept; refused naming it, "Nothing was changed.", the two reads the only calls. Before: `Set-MigrationUser` twice, `Set-MoveRequest` and `Resume-MoveRequest` ran |
| `AMailboxInABatchOfMany_IsApprovedExactlyWhenTheApproveActionWould` (19 statuses) | one owner: the mailbox steps exactly when `MigrationUserActionPlanner.Applies(Approve, ...)` is true (NeedsApproval, " needsapproval ", "NEEDSAPPROVAL"), the refusal and the two reads otherwise |
| `AMigrationUserThatReportsNoStatus_IsRefused_AndNothingIsWritten` (absent, empty, blank) | "its migration user reports no status, so whether it is awaiting approval is not known" |
| `AStatusReadThatFails_ChangesNothing` (thrown, written) | "Could not read the migration status of <address>: <reason>. Nothing was changed." - the cmdlet's own words |
| `NoMigrationUser_IsRefused_AndNothingIsWritten` (category, type, identifier) | Exchange's not-found by structure: "Exchange has no migration user <address>, so there is nothing to approve" |
| `SeveralMigrationUsers_AreRefused_AndNothingIsWritten` | "Exchange returned 2 migration users for <address>" |
| `ARefusalThroughExecuteUserAction_IsOneFailedAuditRow` | the page's single action: one `ApproveMigrationUser` row, Failed, the refusal as its error |
| `AStaleBulkApproval_IsRefusedOnItsRow_AndAuditedFailed` | the stale page through the mailbox bar: planned from a list that says NeedsApproval while Exchange says Synced - the refusal on the row, one Failed row, the banner "1 refused", only the two reads |

`MigrationEligibilityAndApproveTests.AMailboxNotInABatchOfItsOwn_IsApprovedWithoutTouchingABatch_AndSaysSo` (aud-a23h,
changed): a migration user at NeedsApproval, and the call list gains `Get-MigrationUser <address>` after the existence read.
The own-batch call lists, and their tests, are unchanged.

`MigrationDenialTests.ABulkExecutorWithNoListLoaded_SaysSo_AndNothingRuns` (part 2, new; ExecuteBulkBatchAction,
ExecuteBulkMailboxAction): the banner's text, rendered on the status tab; the activity (false, the text); no audit row; the
action never called. Before: no banner, the activity "Nothing was done.".

`MigrationReportAndSizeReadTests`, part 3 - 2 new tests, 4 cases: `AnArchiveReadWithNoRowAndNoError_IsMarked_NotNoArchive`
(the marker "Exchange returned no archive statistics", the mailbox read as 1.5, the page "could not be read (Exchange
returned no archive statistics)"; before: no archive, no marker) and `NoArchive_IsNoArchive_ByEachNotFoundShape`
(category, type, identifier: no marker, 0).

**Against the unfixed code**: A1, A2, A2b and A3 below put back exactly the unfixed behaviour at each call site (no status
read; each guard's silent return; no rows read as no archive), and each fails the tests that pin it.

Mutations at the call site, each by exact-text replacement asserted to match once (session scratchpad `work/mutate.sh`,
`work/round-a55.sh`, the texts in `work/muts/`): the fix staged; md5 recorded; `dotnet build ExchangeAdminWeb.slnx -c
Release`; the 1934 tests of `ExchangeAdminWeb.Tests.Migration*|ClickGate*|ProgressRegistry*|ExchangeServiceBase*|AuditService*|GlobalProgressWiring*|BlockedSender*|RecipientLookup*|CalendarPermission*|MailboxPermission*|OutOfOffice*|ConferenceRoom*|ValidatedWriteCallSite*|RazorParseGate*|LookupValidation*|DelegationReport*|MessageTrace*`
run with a TRX logger (failures read from it); the file restored from git's index, touched so MSBuild rebuilds, md5
re-checked equal, rebuilt, the 1934 re-run green - after every mutation. Bytes (LF) before every mutation and after every
restore: service `0e427c3a117fc50c49f0d17774d82cee`, page `9183c6a5862d5b0b99cf97670aef19b3`.

| Mutation | File | md5 mutated | Failed (of 1934) | Restored, rerun |
| --- | --- | --- | --- | --- |
| A1 (the record's): the status read removed - the many-user path writes with no `RefuseUnlessAwaitingApproval` | service | `416e8e50975f478838ca87c2ba8d8c49` | 37: all 36 aud-a55 cases of `MigrationApproveConfirmationTests` (the refused rows ran the writes; the approved rows lack the read) and `AMailboxNotInABatchOfItsOwn...` | equal; 1934/0 |
| A2 (the record's): `ExecuteBulkMailboxAction`'s guard silent again (3713) | page | `4649c33e423028bac35454bac8c10b90` | 1: `ABulkExecutorWithNoListLoaded...` (ExecuteBulkMailboxAction) | equal; 1934/0 |
| A3 (the record's): no rows as no archive again | service | `7dcd2227ea9a7dd444a4cd019e9eb389` | 1: `AnArchiveReadWithNoRowAndNoError...` | equal; 1934/0 |
| A1b: the planner's rule copied as a refuse list (Completed, Failed, Stopped) | service | `edccef45476266b8592b362e1338b299` | 17: the one-owner theory for the 13 statuses the list does not name, `...WhoseStatusMovedOn...` (Synced, Completing), `ARefusalThroughExecuteUserAction...`, `AStaleBulkApproval...` | equal; 1934/0 |
| A1c: a status read that fails let through to the writes | service | `75122f5aed6e39ce23e4c3b1f1cdf5a7` | 2: `AStatusReadThatFails...` (thrown, written) | equal; 1934/0 |
| A2b: `ExecuteBulkBatchAction`'s guard silent again (3545) | page | `d3aa1861c34dbfae4975f35d8b8a07e3` | 1: `ABulkExecutorWithNoListLoaded...` (ExecuteBulkBatchAction) | equal; 1934/0 |

md5s (LF) of the tests at the fix commit: `MigrationApproveConfirmationTests.cs` `b66d827cda6991fa859f21db168608aa`,
`MigrationDenialTests.cs` `b61e17a8585f6f114f21265934bcdfae`, `MigrationEligibilityAndApproveTests.cs`
`8a85290073d68a964a058fa8afc418e0`, `MigrationReportAndSizeReadTests.cs` `c55cdec08db2296d4ea68a0d69e97f24`.

On the fix commit's tree: `dotnet build ExchangeAdminWeb.slnx -c Release` 0 errors, no warning outside the baseline's 19
(none in a touched file); the 1934 focused tests 1934/0 (`ClickGate*` and `ProgressRegistry*` among them); `dotnet format
ExchangeAdminWeb.slnx --verify-no-changes --no-restore` exit 0; `git diff --cached --check` exit 0; `tools/Test-AsciiOnly.ps1`
exit 0; `wc -l Components/Pages/Migration.razor` 4377. No Exchange was asked anything: every cmdlet was a stub in an
in-process runspace. The full suite runs at the branch tip.

## Known gaps

- **Assumptions, labelled (no live call):** `Get-MigrationUser -Identity` reports the Status value the batch's mailbox list
  reads through `-BatchId`; Exchange Online's not-found for `Get-MigrationUser -Identity` is one of the shapes `IsNotFound`
  reads (aud-a23h's). If either is wrong, an approval the page offers is refused, naming what Exchange said - visible and
  fail closed; one dev approval settles both.
- A mailbox in a batch of its own still reads only its batch's status (aud-a23r's allow list, Synced), not its own: an
  approval there completes a Synced batch, which is what an approval asks for. Outside this finding, which is the
  many-user case.
- No decision reads the cloud size (aud-a23k): part 3 changes what the page shows, nothing it allows.
- No circuit or browser ran: the page's handlers and compiled render tree were driven directly.

## Comments and branch-tip gates (shared by aud-a55 and aud-a54)

The canonical copy for both records; aud-a54's points here.

**Comments (`2e2218e4`).** Written after both mutation rounds, at the call sites the rounds proved load-bearing; comments
only. Checked by `check-comment-only.sh` with `code-only.pl` (session scratchpad `work/`): HEAD's bytes and the tree's are
each reduced to their code - comment-only lines dropped (`//`, `///`, a one-line `@* ... *@`), the trailing comments this
commit adds cut off (`// aud-a55...`), blank lines dropped - and the reductions are identical for all five files. The checker
was shown to bite: against the plan commit `3113f6f0`, before any code, it reports every file the fixes changed as
different. In the three `.cs` files every changed line is an added comment line (service +18, Recipient Lookup +10, models
+2); `Components/Pages/Migration.razor`'s two changed lines (3545, 3713) are each the line it was plus a trailing comment, so
it stays 4377 lines and every registered line key holds; `Components/Pages/RecipientLookup.razor` gains one `@* ... *@` line
(232 to 233; not line-keyed). Reduced-code md5s, unchanged by the commit: `Services/MigrationService.cs`
`d4e2f3f6c46240d612f9dc79351b7429`, `Components/Pages/Migration.razor` `2f842d18eeb867da859c4d710f4d201d`,
`Services/RecipientLookupService.cs` `63f6705da70d8234e4448666ae5aee90`, `Models/LookupModels.cs`
`e6b4b903f1dcaa2e950be0c945b92495`, `Components/Pages/RecipientLookup.razor` `037c2a929e89a9492827649812b93fb3`. Final file
md5s (LF) at the tip: `Services/MigrationService.cs` `b93a26aeda6957f63112fec50a8beee3`, `Components/Pages/Migration.razor`
`57d3ab57c485e7f01751f8782c30ba7f`, `Services/RecipientLookupService.cs` `d63fafcf4733b020319f79336ee8783d`,
`Models/LookupModels.cs` `902f5691d23783b0f4ef3cabfb1a6b76`, `Components/Pages/RecipientLookup.razor`
`45c8ccf3760248e74997d4fe4f020708`; the tests are as their fix commits left them. Focused rerun after the comments: 1946/0.

**Branch-tip gates (`2e2218e4`, both fixes and the comments; the records-only commit after it changes no code):**

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 19 warnings - the same 19 (file, line, column,
  id) as the baseline build of `a7300dee`; none in a file this branch touched.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): Passed 6361,
  Skipped 3 (the known `ADDirectorySearchServiceTests ...WhenAdUnavailable_ReturnsEmpty` cases), Failed 0, Total 6364;
  6 m 43 s; "All tests finished running, Sequence file will not be generated." - no hang. This branch adds 54 cases (42 for
  aud-a55, 12 for aud-a54); the suite at `a7300dee` was not run here, so 6310 there is derived, not measured.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0. `Components/Pages/Migration.razor`: 4377 lines.

No live Exchange, Active Directory, Graph, DHCP or IIS call was made: every Exchange Online cmdlet was a stub in an
in-process runspace and every on-premises one a stub defined through the services' own `ConnectOnPrem` seam. No PowerShell
source changed, so PSScriptAnalyzer and Pester were not run. No coder self-review verdict is claimed. Version bumps are the
supervisor's: the Migration module (aud-a55) and the Recipient Lookup module (aud-a54); no base-app change
(`Services/ExchangeServiceBase.cs` was read, not changed).

## Closeout

Fixed on branch `worktree-agent-ae9cc385925087b25` (forked at `a7300dee`): plan `3113f6f0`, fix `d2bcd9c2` (code, tests,
record), comments `2e2218e4` (shared with aud-a54); not merged, not pushed. This record was taken from `a7300dee`
(master's copy), so at merge it is master's text plus this branch's Plan, Guard proof, Known gaps, the shared section and
this closeout - take the branch's copy.
