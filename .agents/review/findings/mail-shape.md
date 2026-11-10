# mail-shape: Migration and Out of Office administrators' mail used the mailbox-permission template

**Severity**: LOW - the mail was sent, to the right people, with the right facts; they sat under the wrong labels.
A migration batch was a "Target Mailbox", a count of users an "Affected User", the batch options a "Permission
Type", and where there was nothing to say the rows read "N/A" and "Migration". Out of Office mail was headed
"Permission Change Notification".
**Status**: Fixed on branch; awaiting merge
**Branch**: `fix/p0-c-phase0-lows` (worktree `D:\source\ExchangeAdminWeb-wt\p0-c`, from master f151f527)
**Commit**: 9e422005 (the nine mails, tests, README), c71ddc30 (versions) |
**Implementer**: Claude (supervised agent)
**Reviewer**: Fable 5.2 audit 2026-11-10, confirmed by Gemini 4 Argon (High) through agy; orchestrator triage
**Plan**: `docs/SharedPiecesAndSlop-Plan.md` slice 0.7 (defect #6)

## Evidence (master f151f527)

- `Services/EmailService.cs:82-137`: the nine-argument `SendAdminNotificationAsync(performedBy, ip, action,
  targetMailbox, affectedUser, permissionType, success, ticket, error)` writes the rows Target Mailbox, Affected User
  and Permission Type, under the heading "Migration Notification" when the action contains "Migration" and
  "Permission Change Notification" otherwise.
- `Components/Pages/Migration.razor`: eight calls to it (`:2007`, `:2106`, `:2171`, `:2273`, `:3411`, `:3477`,
  `:3761`, `:3894`). `Components/Pages/OutOfOffice.razor:562`: one.

## Fix

All nine now call the existing row-shaped `SendAdminNotificationAsync(performedBy, ip, action, success, ticket,
details, error)` - the one Account Lockout, Named Locations and others already use. No new template, no new method.
Same recipients (`Email:AdminNotificationEmail`, split the same way), same conditions for sending, same action name,
success flag, ticket and error text. Each call keeps its line count, so no line moved in either page and the
click-gate registry needed no re-anchoring.

What changes for every one of the nine, because it is the row-shaped notification's own form:

| | Before | After |
|---|---|---|
| Subject | `[Exchange Admin] <action> - SUCCESS/FAILED - Ticket #<ticket>` | `[Admin] <action> - SUCCESS/FAILED - Ticket #<ticket>` |
| Heading | "Migration Notification" (Migration), "Permission Change Notification" (Out of Office) | the action name |
| Fixed rows | Ticket Number, Action, Status ... Performed By, IP Address, Timestamp (UTC), Error (when there is one) | the same, in the same places |

The rows between Status and Performed By, per mail:

| Mail (action) | Before: Target Mailbox / Affected User / Permission Type | After |
|---|---|---|
| Single check (`CheckMigrationEligibility_<direction>`) | the address / N/A / the verdict | Mailbox: the address; Eligibility: the verdict |
| Single create (`CreateMigrationBatch_<direction>`) | the batch name / the address / `AutoStart:.. AutoComplete:..` plus any exclusion note | Batch: the name; Mailbox: the address; Options: the same text |
| Bulk check (`BulkMigrationEligibilityCheck_<direction>`) | "Bulk Operation" / the CSV file name / `n/m eligible` | CSV File: the name; Eligible: `n/m eligible` |
| Bulk create (`CreateMigrationBatch_<direction>`) | the batch name / `n users` / options plus any exclusion note | Batch: the name; Mailboxes: `n users`; Options: the same text |
| One batch's action (`Migration_<action>`) | the batch name / N/A / "Migration" | Batch: the name |
| One mailbox's action (`Migration_<action>`) | the address / N/A / "Migration" | Mailbox: the address |
| Bulk batch action (for example `Migration_StopSelected`) | `n batch(es)` / N/A / "Migration" | Succeeded: `n batch(es)` |
| Bulk mailbox action (for example `Migration_PauseMailboxes`) | `n mailbox(es)` / N/A / "Migration" | Succeeded: `n mailbox(es)` |
| Out of Office (`SetOutOfOffice_<state>`, `DisableOutOfOffice`) | the mailbox / N/A / "OutOfOffice" | Mailbox: the mailbox |

Dropped: the fillers "N/A", "Migration", "OutOfOffice" and "Bulk Operation". They named no fact.

**For the owner to know**: a mail rule that files these by the `[Exchange Admin]` subject prefix will stop matching
them. The prefix belongs to the template; keeping the old one would have meant a new template or a change to the
shared one, which the plan rules out here.

- README: one bullet under Email Notifications.
- App code: +103 / -103 lines (`git diff --numstat 9e422005~1 9e422005 -- Services Components`). 18 lines are the
  nine calls; the rest are the comment lines of the nine handlers that carried finding IDs (below).

## Tests

- `MigrationAdminMailShapeTests` (new, 8): one per Migration mail, each through the page's own handler over
  `MigrationStubExchange`, asserting the action, success, ticket, error and the exact rows in order.
- `MigrationStubExchange.RecordingEmail` now records the row-shaped notification only (its `Sent` and `Notes` are
  kept, derived from it), so a page that sent the old shape records nothing and every existing test that reads `Sent`
  fails.
- `OutOfOfficeRequestCaptureTests.AnEditDuringTheOperation_...` (changed, 3 cases): its three assertions on the
  notification's arguments now read the new argument order and assert the one row, "Mailbox", and no error.

No test deleted. The mail's HTML is produced by the existing method and is not re-tested here.

## Guard proof (reverted, targeted run of the Migration and Out of Office classes, 952 tests, restored)

| Reverted | Failed | Tests that failed |
|---|---|---|
| The Migration page as before the fix | 22 | all 8 `MigrationAdminMailShapeTests`; `MigrationSubmittedValueTests` (8), `MigrationVerdictAndWriteOutcomeTests` (4), `MigrationExclusionWordingTests` (2) - the existing tests that read the recorded mail |
| The Out of Office page as before the fix | 3 | `OutOfOfficeRequestCaptureTests.AnEditDuringTheOperation_ChangesNeitherWhatIsWrittenNorWhatIsRecorded` (auth, write, read) |

Restored: 952 of 952 pass.

## Left alone, and why

- `EmailService`'s nine-argument overload still has its "Migration Notification" heading branch, which nothing now
  reaches. The overload and its remaining callers go in plan slice 3.5; removing three lines of shared code here
  would cost a base version bump for dead code that is already scheduled out.
- The Options row still carries the exclusion note (" | EXCLUDED (not migrated): ...") after the two options, as the
  Permission Type row did: same text, so nothing that reads it changes. Splitting it is a wording change.
- "n users" and "n batch(es)" keep their wording.
- The mailbox owner's Out of Office mail (`SendOofNotificationAsync`) is a different template and is untouched.
- Comments: finding IDs were taken out of the nine handlers touched (`CheckSingleEligibility`,
  `CreateSingleMigrationBatch`, `CheckBulkEligibility`, `CreateMigrationBatch`, `ExecuteBatchAction`,
  `ExecuteUserAction`, `ExecuteBulkBatchAction`, `ExecuteBulkMailboxAction`, `SetOof`) and nowhere else, line for
  line, with the plainest "it used to" clauses. Some of those trailing comments still describe a past failure in
  the past tense, and a few now say "the same" of a neighbour; rewriting them properly is a comment pass over eight
  long handlers and belongs to the slop phase, not to a mail fix.
- Versions: Migration 1.27.8, Out of Office 1.1.8. No base bump: no shared code changed.
