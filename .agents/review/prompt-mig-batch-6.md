# Defect review request: Migration eligibility fails closed as Undetermined (aud-a23h); Approve reads before writing and names the failing step, unreadable mailbox rows, loads complete with their outcome, the Exchange base reports a cmdlet's own error under Stop (aud-a23i)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `fffdc451` or later.
- The change is the branch merged at `ab5361c8`: `4cf6b629` (plans), `5b5936eb` (aud-a23h), `24c8957e` (aud-a23i),
  `033a1a54` (comments only), `66ac2430` (record SHAs), forked from `34c7ccd4`. Review `git diff 34c7ccd4 66ac2430`,
  and read the COMBINED `Services/MigrationService.cs` (`CheckSingleEligibility`, the not-found rule, the
  `Get-MigrationUser` eligibility read, `ApproveMigrationAsync` and the three batch steps, `FindMigrationUserBatchAsync`,
  the mailbox list read and `UnreadableMigrationMailbox`, `ResolveFailure`), `Services/ExchangeServiceBase.cs`
  (`RunAsync`, `Invoke`, the new constant and helper), `Models/Enums.cs` (`MigrationStatus.Undetermined`),
  `Models/MigrationModels.cs`, `Components/Pages/Migration.razor` (the eligibility display, the bulk check counts, the
  Approve handler, `LoadBatchList` and the other loads' `Complete(...)` calls) and the new test stand-in
  `ExchangeAdminWeb.Tests/MigrationStubExchange.cs` on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a23h.md`, `aud-a23i.md`; parents `aud-a23g.md`, `aud-a23f.md`, `aud-a23e.md`,
  `aud-a23d.md`, `aud-a23c.md`. ALREADY admitted and out of scope: `aud-a23j` (a failed excluded-group check leaves
  the user Eligible; the group match is a DN substring), `aud-a23k` (a failed report fetch is served as the report;
  a failed cloud mailbox size read shows 0 GB), `aud-a23l` (the "running command stopped" prefix at sites that
  format `ex.Message` themselves; "is a protected principal" when the check failed; Undetermined filed under
  "Ineligible"), `aud-a23m` (the `CheckSingleEligibility` catch covers the audit and notification; the page's write
  actions complete their activity true on failure). Do not re-raise them. Repo rules: `AGENTS.md`;
  `.agents/repo-guidance.md` "Known Failure Classes" (1, 2, 3); `docs/ProjectConstitution.md`.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat ab5361c8`. Report whether both succeeded.

## Mandate

Hunt for observable defects. a23h: the not-found rule (category ObjectNotFound, or a `*NotFoundException` in the
reason or error-ID; text never read; `CommandNotFoundException` never counts) - name any Exchange Online not-found
shape it misses or any failure shape it wrongly treats as not-found (a management-role or throttling error whose
error ID contains "NotFound"); the Undetermined status blocks creation in BOTH the UI and the handler (find the
handler path and confirm no third path creates a batch); the bulk check counting an Undetermined row apart. a23i:
Approve reads the batch first - what if the read succeeds but the batch is in a state the three steps cannot act
on (already completed, syncing, failed); the confirm-by-read-back relies on `CompleteAfter` staying set - what
does the code do when it is not set after a successful `Complete-MigrationBatch` (does it report a false failure
after a real success, Known Failure Class 1); the step-naming on failure (the steps "already applied" list -
accurate on every ordering); `ResolveFailure` looking through a wrapper only when the wrapper restates the inner
message - a wrapper that prepends text; `UnreadableMigrationMailbox` - does any consumer of the list (export,
counts, the page's totals) still count or drop them silently (Known Failure Class 2); the base `RunAsync` change -
any caller outside Migration (Blocked Senders, Calendar Permissions, Conference Rooms, Out of Office) whose
behaviour changed beyond the message text; `Invoke` reporting a suppressed error with no record. Tests passing for
the wrong reason: `MigrationStubExchange`'s stub cmdlets vs the real cmdlets' error shapes; the `ProgressRegistry`
pins.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
