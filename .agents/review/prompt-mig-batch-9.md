# Defect review request: approving a mailbox in a batch of many reads its migration status first, the bulk no-list guards say why, a no-row archive read is marked (aud-a55); Recipient Lookup reads the mailbox and archive under Stop (aud-a54)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `be41a71f` or later.
- The change is the branch merged at `671d9bc9`: `3113f6f0` (plans), `d2bcd9c2` (aud-a55), `4e2c4e79` (aud-a54),
  `2e2218e4` (comments only), `b54f0c24` (record SHAs), forked from `a7300dee`. Review `git diff a7300dee b54f0c24`,
  and read the COMBINED `Services/MigrationService.cs` (`ApproveMigrationUserAsync`, new `RefuseUnlessAwaitingApproval`
  through `MigrationUserActionPlanner.Applies(Approve, ...)`, `RefusesWithNoListLoaded`, `GetCloudMailboxSizeAsync`'s
  archive branch), `Services/MigrationUserActionPlanner.cs` (the Approve rule: exactly NeedsApproval),
  `Components/Pages/Migration.razor` 3545 and 3713, `Services/RecipientLookupService.cs` (the `Get-Mailbox` read through
  `ReadUnlessNotFound`; the archive read; `MailboxUnreadable`, `ArchiveUnreadable`), `Models/LookupModels.cs`,
  `Components/Pages/RecipientLookup.razor` (the Archive and Forwarding rows), and the tests
  `MigrationApproveConfirmationTests.cs`, `MigrationDenialTests.cs`, `RecipientLookupCloudReadTests.cs` on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a55.md` (its "naming drift" note: the record named `MigrationBatchActionPlanner`,
  the rule lives in `MigrationUserActionPlanner`), `aud-a54.md`; parents `aud-a23r.md`, `aud-a23o.md`, `aud-a23q.md`,
  `aud-a23k.md`, `aud-a23n.md`, `aud-a23h.md`. ALREADY admitted and out of scope: `aud-a68` (Recipient Lookup never shows
  an on-premises archive; the statistics and recipient-error wordings carry the Stop prefix; a thrown lookup is not
  audited; the bulk batch activity label). Do not re-raise them. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md`
  "Known Failure Classes" (1, 2, 3); `docs/ProjectConstitution.md`.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 671d9bc9`. Report whether both succeeded.

## Mandate

Hunt for observable defects. a55: the many-user approval's status read `Get-MigrationUser -Identity` before the writes
- a status that is NeedsApproval at the read and changes before the first write (the aud-a23r window, shorter now):
acceptable, but is the Detail honest about what was read; the Approve rule "exactly NeedsApproval" - a migration user
whose status is `NeedsApproval` in a casing or with whitespace Exchange emits (the planner trims and ignores case - fine);
the refusal texts reaching both callers' audits as Failed - the bulk run's row; the own-batch path unchanged (aud-a23r
gates it) - confirm no user whose batch is named for them now skips BOTH gates; `RefusesWithNoListLoaded` not audited -
right by the "nothing attempted" rule; the archive no-row marker - any consumer summing a marked size (Known Failure
Class 2) in the bulk table totals. a54: `Get-Mailbox` under Stop through `ReadUnlessNotFound` - for a recipient that
is a mail contact or a mail user (no mailbox by nature), Exchange's `Get-Mailbox` returns not-found: "not a mailbox"
as before - confirm the not-found shape for that case is the one `IsNotFound` reads, else every contact lookup now
warns "could not be read" (a functional regression on the lookup page); `MailboxUnreadable` set -> "nothing more of
the mailbox is read" - the location and on-premises steps still run: does the page then show an on-premises size
beside a cloud "could not be read" (confusing but honest - name it only if misleading); the Forwarding row "Could not
be read" when the MAILBOX read failed - correct; the archive no-row -> warning: the `RecipientLookupReportedTimeTests`
stand-in answers no row - the agent says those tests assert times and no Error; confirm they do not now show a warning
that a real mailbox would not. Tests passing for the wrong reason: the stub `Get-MigrationUser -Identity` answering the
same `MigrationUsers` table as `-BatchId` (the labelled assumption); the Recipient Lookup stand-in's not-found shapes.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
