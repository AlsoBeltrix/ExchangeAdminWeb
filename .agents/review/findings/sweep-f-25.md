# sweep-f-25: cloud calendar grants fell back from Set to Add only on one wording (sweep item 25)

**Severity**: MEDIUM - `.agents/review/live-risk-sweep-2026-10-30.md` item 25: kd-43's `NamesNoPermissionEntry`
(`CalendarPermissionService.cs`) followed a refused `Set-MailboxFolderPermission` with `Add-MailboxFolderPermission` only when the
error said "no existing permission entry" (or its FQEID named `UserNotFoundInPermissionEntry`); every first-time delegate
depended on Exchange's wording. Any other Set refusal came back as its bare error text.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/sweep-f` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-f`, from master `624ff16e`)
**Commit**: `7b6e01ad` (`fix(sweep-f, item 25): ...`) | **Reviewer**: implementer (candidate)

## Approach (as built)

- **The error class, not a sentence.** `NamesNoPermissionEntry` walks the chain (depth 16) and matches any of
  `UserNotFoundInPermissionEntry`, "no existing permission entry", "no permission entry" (case-insensitive) in: the exception's
  message and its type name (its own or a wrapped one); every captured error (`PsErrorsDataKey`); the error record's text (its
  ErrorDetails when it carries any), its fully qualified error id, its exception's type name, and that exception's own chain.
  The EXO V3 module's REST errors carry `|<exception type>|<message>` in their text, which the type-name mark matches.
- **Handled shapes, stated**: Exchange's sentence; the V3 `|...UserNotFoundInPermissionEntryException|...` prefix with any
  message; a rewording that drops "existing"; the identifier alone in an FQEID; the type on an error record's exception; a
  wrapped exception of that type. **Never matched**: Add's opposite "An existing permission entry was found for user"
  (`UserAlreadyExistsInPermissionEntryException`), or any other refusal.
- **Not "fall back on any failure".** Any other Set refusal is reported as the Set's: "Set-MailboxFolderPermission failed for
  <user> on <folder>: <Exchange's words>. No entry was added: Exchange did not answer that the user holds none." An Add Exchange
  refuses after "no entry" is said as the Add's. A session that broke once sent is unchanged (unconfirmed, no Add).
- README Calendar Permissions; CalendarPermissions `1.4.13`.

## Where the code goes beside the fix shape

- "Error category" was considered and not used: no category is specific to this refusal (ObjectNotFound covers a missing
  mailbox or user too), so matching on it would add Adds after the wrong refusals.
- Two record checks first written (category reason, ErrorDetails) were removed: the guard proof showed each redundant (the
  record's text is its ErrorDetails; the reason is the exception's type name, already read).
- The on-premises path uses the same matcher (item 23); its failure messages are unchanged.

## Assumptions not verified live

- The wordings and the type name are recalled, not captured from this tenant; a shape none of the marks names reads as "the
  Set's own refusal", which is loud (a named failure, no Add) - never a hidden error.

## Guard proof (`PermissionWriteSubmissionTests`)

| Mutation | Failing tests |
|---|---|
| M25a one wording only | `ACloudCalendarSet_AnsweredNoEntryInAnotherShape_AddsOne` (2), `OnlyExchangesNoEntryAnswer_NamesNoPermissionEntry` (2 new rows), `TheNoEntryClass_IsReadFromTheErrorRecord_...` |
| M25b no exception type name | `TheNoEntryClass_IsReadFromTheErrorRecord_...` |
| M25c the Set's refusal back to its bare text | `ACloudCalendarSet_ThatExchangeRefusesOtherwise_IsFailed_AndNoAddIsSent` |
| M25d Add on any Set failure | `ACloudCalendarSet_ThatExchangeRefusesOtherwise_IsFailed_AndNoAddIsSent` |
| M25g the Add's refusal back to its bare text | `ACloudCalendarAdd_ExchangeRefusesAfterTheSetsNoEntry_IsSaidAsTheAddsRefusal` |
| M25h no FQEID | `TheNoEntryClass_IsReadFromTheErrorRecord_...` |
| M25i no record exception | `TheNoEntryClass_IsReadFromTheErrorRecord_...` |
| M25j no record text | `TheNoEntryClass_IsReadFromTheErrorRecord_...` |

## Gates

Run once for the branch at its tip; see the sweep-f hand-back. Not run: a live grant.

## Deploy-time checks

- The sweep's: grant Reviewer on a test cloud mailbox's calendar to a user who has no entry - it should succeed (Set refused
  "no entry", then Add). Then grant to a user Exchange cannot resolve: the message should begin "Set-MailboxFolderPermission
  failed for" and say no entry was added.
