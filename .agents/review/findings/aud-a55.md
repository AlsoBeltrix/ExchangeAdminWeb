# aud-a55: Migration - approving a mailbox that is not in its own batch writes without reading any status, the bulk executors' "no list loaded" guards show nothing, and a cloud archive read with no rows reads as no archive

**Severity**: MEDIUM (part 1), LOW (parts 2 and 3). (1) `ApproveMigrationUserAsync` for a mailbox in a
batch of many (no batch named for it) runs `Set-MigrationUser -CompleteAfter`, `Set-MoveRequest` and
`Resume-MoveRequest` without reading the mailbox's or its batch's status; only the UI planner (Approve
offered on NeedsApproval) gates it, so a stale page or a second operator can approve a mailbox whose state
no longer allows it (the aud-a23r class for the many-user case). (2) `Migration.razor`'s bulk executors
return silently on "no list loaded" guards. (3) `MigrationService.GetCloudMailboxSizeAsync` reads an archive
read that returns no rows and no error as no archive (minor: no decision reads the cloud size).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
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

Pending.
