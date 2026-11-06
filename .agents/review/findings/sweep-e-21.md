# sweep-e-21: bulk permission runs mailed the trustee and the owner once per applied row, and "granted" for a no-op (sweep item 21)

**Severity**: MEDIUM - `.agents/review/live-risk-sweep-2026-10-30.md` item 21: `MailboxPermissionService.cs:420-421` and
`CalendarPermissionService.cs:427` called `PermissionChangeNotifier.NotifyAffectedAsync` inside the CSV loop, so a 200-row
file could send 400 mails one after another; Exchange's warning-only no-ops counted as applied
(`MailboxPermissionService.cs:198-199`), so people were mailed "granted" for nothing.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/sweep-e` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-e`, from master `54dcfb40`)
**Commit**: `488cea40` (`fix(sweep-e, item 21): ...`) | **Reviewer**: implementer (candidate)

## Evidence

- At `54dcfb40`: each applied row called `NotifyAffectedAsync`, which sent `SendUserNotificationAsync` and
  `SendOwnerNotificationAsync` before the next row ran. `TryRight` returned `RightOutcome.Ok` for any right whose command
  raised no error; nothing read the warning stream (no `Streams.Warning` anywhere in `Services/`).

## Approach (as built) - the 2026-11-03 ruling's shape

- **One mail per affected person, after the loop.** `PermissionChangeDigest` (in `PermissionChangeNotifier.cs`) records
  each row's changed rights as the row completes and, after the loop, sends each trustee one mail listing every mailbox
  or calendar the run changed for them and each owner one listing every user whose access to their mailbox or calendar
  changed. Grouped case-insensitively, in first-seen order. A person with a single change gets the existing single-row
  mail, word for word. New `EmailService.SendUserDigestNotificationAsync` / `SendOwnerDigestNotificationAsync`
  (virtual, gated by `NotifyUsersOnPermissionGrant` like the others, HTML-encoded). Every send is in its own try: one
  failing does not suppress another and none changes a row's outcome. `NotifyAffectedAsync` is removed (no other caller).
- **No mail, and "No change", for a no-op.** The warning stream is cleared before each cloud right and read after it
  (`ExchangeNoChangeWarning`): "access control entry ... already present", "... isn't/is not present", "doesn't/does not
  exist", and Set-MailboxFolderPermission's "no settings of ... have been modified". Such a right is a success marked
  `RightOutcome.Unchanged`; the result carries `UnchangedRights` beside `AppliedRights` (and `ChangedRights`, the
  difference). The bulk loops record only changed rights; a row that changed nothing says "No change: ... already had
  ... (Exchange reported nothing to grant)", a partly-unchanged row names the unchanged rights. The calendar Set reads
  the warning for its own Set only - an Add after a refused Set is a change.
- **Single-row paths unchanged.** `AppliedRights` keeps every applied right, so the pages' single-change notifications,
  banners and audit rows read exactly as before. `UnchangedRights` is carried by every `PermissionResult` copy
  (`WithMessage`, `WithServicedNote`, the new `WithUnchangedRights`, Migration's two re-wraps).
- Help: the two pages' notification note (line 22, same line), README Email Notifications. MailboxPermissions `1.6.11`,
  CalendarPermissions `1.4.11`. Base app stays `2.36.0` (EmailService is shared; this cycle's base bump already absorbs it).

## Where the code goes beside the fix shape

- No-op detection is on the cloud paths only: the bulk loops call only the cloud writes. The on-prem writes and the
  calendar removal are untouched (a calendar removal of an absent entry is an Exchange error, not a warning).
- The audit rows are unchanged: a no-op row is still `Success`; only its bulk-table message and the mail differ.

## Assumptions not verified live

- The wording of Exchange's no-change warnings (recalled, not probed against a tenant) and that the EXO V3 runspace
  delivers them on `PowerShell.Streams.Warning`. A wording or stream miss reads as a change, which is what the app did
  before: the cost is only the mail that was always sent.

## Guard proof

| Mutation | Failing tests |
|---|---|
| M1 the digest sends one mail per change (no grouping) | `BulkPermissionNotificationTests.AFailedNoticeStillSendsTheOthers_AndNothingThrows`, `ATrusteeGrantedSeveralMailboxesGetsOneMailListingEveryOne`, `AnOwnerWithSeveralChangesGetsOneMailListingEveryUser`; `BulkPermissionDigestRunTests.AMailboxRun_SendsTheOwnerOneMailListingEveryTrustee`, `ACalendarRun_...` |
| M2 `TryRight` ignores the warning | `ARightExchangeAnswersAsAlreadyPresent_IsAnUnchangedSuccess`; `AMailboxRowExchangeAnswersAsAlreadyInPlace_MailsNobody_AndSaysNoChange` |
| M3 the calendar Set ignores the warning | `BulkPermissionDigestRunTests.ACalendarRun_SendsTheOwnerOneMail_AndACalendarSetExchangeAnswersAsUnchangedMailsNobody` |
| M4 the warning stream is not cleared before a right | `AWarningLeftFromAnEarlierCommand_DoesNotMakeTheRightUnchanged` |
| M5 the mailbox loop records applied, not changed, rights | `TheBulkLoopsRecordEachRowAndSendOncePerPersonAfterTheLoop(Mailbox)`; `AMailboxRowExchangeAnswersAsAlreadyInPlace_...` |
| M6 the row message ignores the no-op | `AMailboxRowExchangeAnswersAsAlreadyInPlace_...` |
| M7 the mailbox loop sends inside the loop | `TheBulkLoopsRecordEachRowAndSendOncePerPersonAfterTheLoop(Mailbox)`; `AMailboxRun_SendsTheOwnerOneMailListingEveryTrustee` |

The end-to-end tests drive both CSV processors against the aud-a2d stand-ins; the harness gained `Warnings` (a stand-in
writes the text as a warning and carries on). `PermissionResultCopyTests` holds `WithUnchangedRights` to the copy rule.

## Gates

Run once for the branch at its tip; see the sweep-e hand-back. Not run: a live bulk run.

## Deploy-time checks

- Bulk-grant FullAccess on a test mailbox to a user who already has it: the row should read "No change" and no mail
  should arrive. If it reads "granted" and mails, the warning wording or stream assumption is wrong (harmless; record it).
- A 3-row CSV for one trustee on three mailboxes: the trustee receives one mail listing all three.
