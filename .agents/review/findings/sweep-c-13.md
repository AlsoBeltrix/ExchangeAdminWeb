# sweep-c-13: a refused mailbox-GUID identity says what it is; a tripwire keeps aud-a2d whole (sweep item 13)

**Severity**: MEDIUM - `.agents/review/live-risk-sweep-2026-10-30.md` item 13: every Exchange write and pre-write read names
the mailbox by `ValidatedMailbox.Identity` (its ExchangeGuid, aud-a2d), which only the test harness has accepted. If Exchange
does not take that form, every change is refused with "couldn't be found", naming a GUID the operator never typed - and the
deploy check would not know at once that this is item 13.
**Status**: Fixed on branch (legibility and tripwire); the live answer is the owner's at the one dev deploy
**Branch**: `fix/sweep-c` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-c`, from master `72611fc2`)
**Commit**: see `git log master..fix/sweep-c` (`fix(sweep-c, item 13): ...`) | **Reviewer**: implementer (candidate)

## Evidence

- At `72611fc2`: `Services/ValidatedMailbox.cs:51` `Identity => ExchangeGuid.ToString("D")`; the call sites the sweep lists
  (`ExchangeServiceBase.ReadValidatedMailbox`, `MailboxPermissionService` cloud and on-premises, `CalendarPermissionService`,
  `OutOfOfficeService`, `ConferenceRoomService` Set-Place / Add-DistributionGroupMember -Member / Get-Place). A failed command
  reached the operator as Exchange's raw text through `ExchangeServiceBase.Invoke`.

## Approach (as built)

- **One place.** `ExchangeServiceBase.Invoke` (both failure branches) asks `MailboxGuidIdentityRefusal.Describe` before the
  pipeline is cleared. It answers only when the error is Exchange's not-found wording AND names a GUID the command passed
  as the mailbox (`-Identity`, `-Member`, a `"<guid>:\Calendar"` folder identity, or an on-premises `Invoke-Command`'s first
  argument - never a trustee). Then the operator message (and so the audit row's error) is "Exchange refused the mailbox
  GUID identity <guid> for <cmdlet>: Exchange answered "<its words>". Every change names the mailbox by its Exchange
  mailbox GUID, and this Exchange did not accept that form, so this step changed nothing. Tell your administrator: it is
  the mailbox-GUID identity check of the dev deployment (sweep item 13)." - first in the error detail too, which
  `ResolveFailure` reads - and a Warning logs the cmdlet (for Invoke-Command, the cmdlets its script runs) and the GUID.
  The logger is set once in `Program.cs`.
- **Get-Place** runs under SilentlyContinue, so Exchange's words never arrive: an empty answer for the GUID logs a Warning
  (`NoPlaceFor`) saying the place fields - and an audit row's old values - read blank, and what it means if every room does.
- **Tripwire.** `MailboxGuidIdentityTests`: every `AddCommand` of the eight cmdlets the sweep names is chained with
  `.AddParameter("Identity"|"Member", mailbox|room.Identity)` (Get-Mailbox: the read-back's); the four on-premises
  Mailbox Permissions and two Calendar `ArgumentList`s start with `mailbox.Identity`; the calendar folder, auto-reply read,
  room read and location read helpers are passed `.Identity`; `Identity` is still the ExchangeGuid.
- **aud-a2d not reverted**; nothing retried; no other identity tried.
- **Help.** README Troubleshooting entry. No page edited. **Versions.** None (shared base code; base 2.36.0 undeployed).

## Where the code goes beside the brief

- Recognition is generic (any command whose mailbox argument is a GUID Exchange calls not found), so a later cmdlet is
  covered without a list; the list is the tripwire's.

## Guard proof

| Revert | Failing test |
|---|---|
| M1 `Describe` never answers | `AReadBackRefusedForTheGuid_...`, `AWriteRefusedForTheGuid_SaysSo_ForThatRight`, `EachWayACommandNamesTheMailbox_IsRecognised` (4) |
| M2 any parameter counts (a trustee GUID too) | `AGuidPassedAsTheTrustee_OrAnErrorThatIsNotNotFound_IsLeftAlone`, `EachWayACommandNamesTheMailbox_IsRecognised` (on-premises) |
| M3 a GUID-bound write's -Identity back to the typed string (Remove-MailboxPermission) | `EveryCmdletTheSweepNamed_IsStillBoundToTheValidatedMailboxIdentity` |
| M4 the auto-reply read passed another form | `EveryOtherPathTheSweepNamed_StillPassesTheValidatedMailboxIdentity` |

## Gates

Run once for the branch at its tip: see the sweep-c report. Not run: any live Exchange call - the sweep's check 2 is the
owner's at the dev deploy.
