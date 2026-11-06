# sweep-d-32: Emergency Disable's notice stopped reaching the administrators, and a semicolon list failed (sweep item 32)

**Severity**: MEDIUM - `.agents/review/live-risk-sweep-2026-10-30.md` item 32: since A27 the notice went only to the
module's NotifySecurityTeam setting (`EmergencyDisableService.cs:1155`, `:1181-1182`), so administrators no longer
received it; addresses were split on commas only (`EmailService.cs:176`), so a semicolon list failed with "SECURITY TEAM
NOT NOTIFIED"; a blank setting shows the page warning (`EmergencyDisable.razor:33`).
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/sweep-d` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-d`, from master `6ea8963b`)
**Commit**: `4040c435` (`fix(sweep-d, item 32): ...`) | **Reviewer**: implementer (candidate)

## Approach (as built)

- **Both recipient sets.** `SendSecurityNotificationAsync` sends the administrators (`EmailService.AdminRecipients`,
  i.e. `Email:AdminNotificationEmail`) their own copy of the same notice, less any address also on the security list (so
  a person on both gets one mail), and then the security team. The outcome (`Sent` / `PartiallySent` / `Failed` /
  `NotConfigured`) and the operator's sentence stay about the security team alone; an administrator delivery shortfall
  is logged. A blank setting - or one of separators only - is still `NotConfigured` ("SECURITY TEAM NOT NOTIFIED ..."),
  and the administrators are still sent it. This restores the pre-A27 recipients without dropping A27's.
- **Commas or semicolons everywhere.** `EmailService.SplitAddresses` (split on `,` and `;`, trimmed, blanks dropped,
  case-insensitively de-duplicated) is the one rule for the admin overload's list, `SendModuleNotificationAsync`'s
  recipients, `AdminRecipients`, and Migration's administrators list handed to Exchange (`MigrationService`, same
  setting). The page's "recipient configured" test uses it too. `ParseRecipientInput` already took both.
- **Help.** NotifySecurityTeam field help ("Email address(es) ..., comma- or semicolon-separated. The administrators ...
  are notified as well; blank notifies the administrators only"); the page banner ("The security team and the
  administrators are notified immediately." / "... the security team will NOT be notified - tell them yourself. The
  administrators are still notified."), kept on its one line; README Emergency Disable bullet. No other help text in the
  repo described a recipient list as comma-separated (searched: Services, Components, Modules, README).
- EmergencyDisable `1.7.2`. The separator change is shared code; the base app stays `2.36.0` (undeployed, already
  absorbing shared changes).

## Where the code goes beside the fix shape

- Two sends, not one combined send, so the security-team outcome is not blurred by an administrator failure.
- Migration's administrators list is not EmailService's, but it reads the same setting; leaving it on commas only would
  make one setting mean two things.

## Guard proof (`EmergencyDisableNotificationTests`, `EmailAddressListTests`)

| Mutation | Failing tests |
|---|---|
| E1 administrators not sent the notice | `TheSecurityTeamAndTheAdministrators_BothReceiveTheNotice`, `AnAddressInBothLists_GetsOneMail`, `ABlankSetting_StillWarns..._ButNotifiesTheAdministrators`, `ASettingOfSeparatorsOnly_IsNotConfigured` |
| E2 commas only | `ALists_AddressesAreSplitOnCommasAndSemicolons` (several), `TheAdministratorsList_TakesEitherSeparator` (2), `ASettingOfSeparatorsOnly_IsNotConfigured`, `AnAddressInBothLists_GetsOneMail` |
| E3 no cross-list de-duplication | `AnAddressInBothLists_GetsOneMail` |
| E4 blank setting skips the administrators | `ABlankSetting_StillWarns...`, `ASettingOfSeparatorsOnly_IsNotConfigured` |
| E5 no de-duplication within a list | `ALists_AddressesAreSplitOnCommasAndSemicolons(soc;SOC)` |

The six existing A27 tests (configured address is the recipient, no address sends nothing when no administrator is
configured, outcome follows delivery, a throwing service reports Failed, the sentences) pass unchanged.

## Gates

Run once for the branch at its tip; see the sweep-d hand-back. Not run: an SMTP send.

## Manual checks for the deploy list (from the sweep)

- Module Configuration > Emergency Disable > NotifySecurityTeam: confirm the address(es); a semicolon list now works.
- Confirm `Email:AdminNotificationEmail` on both instances is the list that should receive every disable notice.
