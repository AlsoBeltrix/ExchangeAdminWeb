# sweep-f-23: on-premises calendar grants sent a try/catch script the no-language Exchange endpoint refuses, filed "could not be confirmed" (sweep item 23)

**Severity**: MEDIUM, pre-existing shape - `.agents/review/live-risk-sweep-2026-10-30.md` item 23: `CalendarPermissionService.cs`
sent `try { Set-MailboxFolderPermission } catch { Add-MailboxFolderPermission }` through `Invoke-Command -Session` to the
Microsoft.Exchange endpoint, which is in no-language mode; the refusal's words contain "runspace", so
`ExoConnectionPool.IsConnectionError` classed it a dead session and the grant was filed unknown with an `_UNCONFIRMED` admin mail.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/sweep-f` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-f`, from master `624ff16e`)
**Commit**: `64c0dff4` (`fix(sweep-f, item 23): ...`), and `bf725472` (the GUID-binding tripwire that followed) | **Reviewer**: implementer (candidate)

## Evidence

- At `624ff16e` `SetCalendarPermissionOnPremAsync` built one script block with a `param` block, an expandable string
  (`"$Identity`:\Calendar"`) and a try/catch. Measured here: `[scriptblock]::Create(<that script>).GetPowerShell(...)` throws
  "Only a script block that contains exactly one pipeline or command can be converted". Invoke-Command sends a block as a
  command only when that conversion succeeds; otherwise as script text, which a no-language endpoint refuses.
- The removal named `"\Calendar"` - wrong for a mailbox whose calendar has a localized name - although the cloud path already
  read the folder (`GetCalendarFolderName`).

## Approach (as built)

- **The folder from the mailbox, on-premises.** `OnPremCalendarFolder`: `Get-MailboxFolderStatistics -Identity <GUID>
  -FolderScope Calendar | Select-Object -First 1 FolderPath`, one pipeline through `AddOnPremRead` (made protected), composed by
  `CalendarFolderIdentity.Build` as the cloud path does. A read that fails, or finds no folder, returns a failed result saying
  "Nothing was written"; a cancel passes through (before the mark: not sent).
- **One command per Invoke-Command.** `OnPremSetScript`, `OnPremAddScript`, `OnPremRemoveScript` (consts), each
  `param($Folder, ...) <one cmdlet> -Identity $Folder ...`, the identity passed whole. Set first; Add as a second command only
  on Exchange's "no entry" answer (`NamesNoPermissionEntry`, item 25 makes it robust), and never after a Set whose session broke
  once sent; any other refusal is that failure (`OnPremSetFailed`, aud-a70 (12)/(14) unchanged). Remove reads the folder too.
- **Classifier.** `ExoConnectionPool.IsLanguageModeRefusal`: "no-language mode", "syntax is not supported by this runspace" or
  identifier `ScriptsNotAllowed`, anywhere in the chain, is excluded from `IsConnectionError` - nothing ran and the session is
  sound, so it is neither a dead session nor a pool discard.
- **Harness.** The on-prem `Invoke-Command` stand-in now does what the endpoint does: a block that does not convert with
  `GetPowerShell` is refused with the no-language error, running none of it. Every other on-prem script in the repo converts.
- README on-premises section; CalendarPermissions `1.4.12`.

## Where the code goes beside the fix shape

- The removal was not named by the item; it carried the same hard-coded `\Calendar` and is fixed in the same commit.
- The tripwire `MailboxGuidIdentityTests` counted the old ArgumentList form; it now counts the folder reads (`bf725472`, a
  follow-up the item-23 filtered run missed and item 25's caught).

## Assumptions not verified live

- That the Microsoft.Exchange endpoint is in no-language mode (the repo's `MailboxPermissionService` comment relies on the same).
- The refusal's wording ("The syntax is not supported by this runspace. This can occur if the runspace is in no-language
  mode.", `ScriptsNotAllowed`) is PowerShell's as recalled, not captured from this endpoint.
- That on-premises `Get-MailboxFolderStatistics -FolderScope Calendar` reports `FolderPath` (`/Calendar` or `\Calendar`; both
  compose).

## Guard proof

| Mutation | Failing tests |
|---|---|
| M1 the Set script back to try/catch | 20, among them `AnOnPremSet_SendsTheValidatedMailbox_AndWritesItsCalendar`, `AnOnPremSetWithNoEntryToSet_AddsInACommandOfItsOwn_OnTheSameFolder`, `EachOnPremWriteScript_ConvertsToOneCommand_...`, the page and cancellation suites' Calendar cases |
| M2 the classifier exclusion removed | `ANoLanguageRefusal_IsNotADeadSession_SoTheWriteIsNotFiledUnconfirmed` |
| M3 Add on any Set failure | `AnOnPremSetRefusedForAnotherReason_IsThatFailure_NoAdd_NotUnconfirmed` |
| M4 the folder path ignored (`\Calendar`) | `AnOnPremWriteOnALocalizedCalendar_WritesThatFolder_NotBackslashCalendar` (set, remove) |
| M5 a failed folder read writes anyway | `AnOnPremFolderReadThatFails_WritesNothing_AndSaysSo(set: True)` |

Updated: `PermissionOnPremChangeCancellationTests` (two commands per calendar change; the "cancel as the first command is sent"
case cancels at the first write; new `ACalendarChangeCancelledDuringItsFolderRead_SendsNoWrite`), `PermissionWriteSubmissionTests`
and `PermissionPagesSelfGrantTests` (the Add follows "no entry", three commands).

## Gates

Run once for the branch at its tip; see the sweep-f hand-back. Not run: a live on-premises grant.

## Deploy-time checks

- Grant Reviewer on an on-premises test mailbox's calendar to a user with no entry, then change it to Editor, then remove it:
  each should succeed with no "could not be confirmed" and no `_UNCONFIRMED` mail; the log shows the folder identity used.
- On a mailbox with a non-English calendar name, the grant names that folder.
