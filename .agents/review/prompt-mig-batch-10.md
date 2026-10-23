# Defect review request: Recipient Lookup archive/wording/audit fixes and Migration bulk-label, denial and selection fixes (aud-a68 parts 1-7, kd-2)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `44fb74aa` or later.
- The change is the branch merged at `44fb74aa`: commits `3ba9dfa4`, `1963cb79`, `28256d97`, `37a2696b`, `6f0d6dfd`, `14bf6a4f`,
  `ffe2d99b`, `39a878cd`, `86953582`, forked from `eb017102`. Review `git diff eb017102 86953582 -- . ':!.agents'` and read
  the COMBINED `Services/RecipientLookupService.cs`, `Services/ExchangeServiceBase.cs` (new `MailboxSizes.ArchiveFound`),
  `Models/LookupModels.cs`, `Components/Pages/RecipientLookup.razor`, `Components/Pages/Migration.razor` (the bulk executors
  about 3600-3800, `AdoptSelectionAsOpenBatch`, `SyncOpenBatchFromUrl`, the row click and tick handlers; the page went
  4377 -> 4353 lines and `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` was re-anchored), `Services/MigrationService.cs`
  (`RecordDenial`), and the tests `RecipientLookupFailureTests.cs`, `RecipientLookupOnPremArchiveTests.cs`,
  `RecipientLookupReportedTimeTests.cs`, `MigrationBulkBatchActivityTests.cs`, `MigrationDenialTests.cs`,
  `MigrationMailboxSelectionTests.cs`, `MigrationStubExchange.cs`.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command per
  call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a68.md` (seven numbered parts; approach, mutations, known gaps), `kd-2.md`; shapes
  cited: `aud-a23i.md`, `aud-a23l.md`, `aud-a54.md`, `aud-a55.md`, `rc-1.md`..`rc-4.md`. ALREADY admitted and out of
  scope - do not re-raise: `kd-13` (a staged action survives a batch switch), `kd-14` (location and size catches swallow a
  cancellation / raw message), `kd-15` (bulk executors' outer catch raw message), `aud-a67` (7) (the 9 s wall-clock test
  bound). Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Known Failure Classes" (1, 2) and the ClickGateRegistry
  line-keying rule; `docs/ProjectConstitution.md` (audit on every action).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 86953582`. Report whether both succeeded.

## Mandate

Hunt for observable defects. Recipient Lookup: `ArchiveFound` semantics - the three states (no archive, archive present,
archive unreadable) for cloud and on-premises, and whether any combination still renders "Disabled" for a real archive or
hides the warning; the not-found mapping - which Exchange error shapes become "Recipient '<x>' not found." and whether a
transient or permission error can be misread as not-found (a false "not found" hides a real failure); the statistics
no-row warning - is a legitimately empty statistics result (a never-logged-on mailbox) now a false warning; the failed
lookup's audit row - written exactly once, Failed, and the activity completed with the same text, including when the
audit write itself throws (Known Failure Class 1); cancellation - which catches now let it through and whether a
cancelled lookup leaves the activity open. Migration: `RecordDenial` for the bulk executors - `mutationAttempted: false`
on every denial, the single-action and bulk rows now identical in shape, no double audit row; the activity label from the
action - every enum value mapped, no fallback to the old fixed label; kd-2 - the three routes (row click, tick boxes,
address route) clear the ticks, and `PruneSelection` / `TicksInThisBatch()` still agree with the rc-1..rc-3 confirm-bar
counting after the clear; the re-anchoring - `ExpectedLineCount` 4353 and every registered line for Migration.razor
still names the handler it claims (sample at least five entries against the file); and tests that pass for the wrong
reason (the `MigrationStubExchange` answering a shape production never produces; the "no archive" stand-in in
`RecipientLookupReportedTimeTests` asserting `Warnings` empty on a shape that would also be empty if the fix were absent).

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.