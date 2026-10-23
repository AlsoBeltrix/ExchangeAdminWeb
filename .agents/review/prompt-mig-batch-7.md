# Defect review request: Migration Approve reads the batch state and treats an accepted completion as success (aud-a23n); a failed excluded-group check is Undetermined with resolved-DN matching (aud-a23j); audit and notification failures never replace a verdict, writes complete with their outcome (aud-a23m); a failed report or size read is shown as such (aud-a23k); the cmdlet's own words and "could not be verified" (aud-a23l parts 1-2)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `3a948129` or later.
- The change is the branch merged at `09d3bacb`: `f62462b9` (plans), `2249d828` (aud-a23n), `5cf71c1e` (aud-a23j),
  `7e2ad045` (aud-a23m), `e455a37f` (aud-a23k), `26237a55` (aud-a23l), `97ca9448` (comments only), `ebf55bf7` (record
  SHAs), forked from `b16898dc`. Review `git diff b16898dc ebf55bf7`, and read the COMBINED `Services/MigrationService.cs`
  (`ApproveMigrationAsync` - the pre-read states and the post-completion Detail; `CheckExcludedGroups` and
  `CreateDirectoryRunspace`; `CheckMigrationEligibilityAsync` ToOnPrem markers; `GetMigrationUserReportAsync`;
  `ResolveFailure` callers; `ProtectionGate.IsProtected` and the four-list partition), `Components/Pages/Migration.razor`
  (`CheckSingleEligibility` guards; the six writes' `Complete(...)`; `FetchUserReport` and the report dialog; the size
  display), `Models/MigrationModels.cs` (`MigrationUserReport`, the size markers),
  `Services/Jobs/MigrationReportExportProcessor.cs`, and the tests `MigrationStubExchange.cs` (the AD stand-in),
  `MigrationExcludedGroupTests.cs`, `MigrationVerdictAndWriteOutcomeTests.cs`, `MigrationReportAndSizeReadTests.cs`,
  `MigrationApproveConfirmationTests.cs` on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a23n.md`, `aud-a23j.md`, `aud-a23m.md`, `aud-a23k.md`, `aud-a23l.md`; parents
  `aud-a23h.md`, `aud-a23i.md`, `aud-a23g.md`. ALREADY admitted and out of scope: `aud-a23o` (the base's on-prem size
  read `?? 0` and archive catch; `HasCloudMailboxAsync` under Ignore and `OnPremRecipientExists` under SilentlyContinue;
  the dead `CheckAdGroupMembership`; the ToCloud size failure filed as Ineligible with a guessed cause), `aud-a23p`
  (protected-principal wording over unverified exclusions at the partial-exclusion sites; the ExcludedADGroups
  description; the load banner without a reason), `aud-a23q` (an authorization denial on a single action or either
  create shows nothing and writes no audit row), `aud-a28z` part 5 (`LogMigrationCheck` filing Undetermined as
  Ineligible; the event log filter option). Do not re-raise them. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md`
  "Known Failure Classes" (1, 2, 3) and Architectural Invariant 7; `docs/ProjectConstitution.md` ("OU and group checks
  must be based on resolved directory objects, not substring matches"; the audit and notification rules).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 09d3bacb`. Report whether both succeeded.

## Mandate

Hunt for observable defects. a23n: the refused-state list (Completed, CompletedWithErrors, Completing, Failed,
Removing, Stopped, Stopping, no status) - a state that should refuse and is not listed (Corrupted, Synced with a
pending completion already requested, Syncing - which of these should the three steps act on); "an unlisted status
proceeds as before" - is that fail-open for a state the cmdlet reports that the agent did not foresee; the
post-completion Detail when the read-back FAILS - is the operation still Success (correct) and does the Detail say
the state was not confirmed. a23j: `Get-ADGroup -Identity <value>` for each configured value on every eligibility
check (N groups x M users - a cost, and a transient failure makes every check Undetermined; is the resolution
cached per check run); the user lookup by UPN in the host's own domain with no `-Server` - a user in another domain
of the forest reads Undetermined (visible) - does the ToCloud path reach that user at all; `memberOf` is direct
membership only - a nested exclusion is missed (named in the record as not this finding - confirm it is a stated
gap, not a silent one); the `Invoke` under Stop with `HadErrors` - the psl-7 import helper is used, or is an
import's error-stream record taken for a later read's error. a23m: the six writes' local outcome - any path where
the outcome is set Success before the write's result is known; the create handlers' notification guards - does a
notification failure now go unreported anywhere (the Constitution asks that it be logged and surfaced, not
hidden). a23k: `MigrationUserReport.Failed` with a blank reason given one - from where; the export processor's
Failed row - does the zip still get produced with a gap and is the gap named; the size markers - any consumer
summing or comparing a marked size (Known Failure Class 2). a23l: the four-list partition - any caller that still
reads the old three lists and so drops the unverified list silently; `IsProtected` default false - a caller that
treats false as "verified unprotected". Tests passing for the wrong reason: the AD stand-in's stub cmdlets vs the
real module's parameter binding (`-Filter`, `-Properties memberOf`, `-Identity`); the page's reflection driver.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
