# sweep-e-43: the permission pages parsed a config Boolean with bool.Parse during render (sweep item 43)

**Severity**: MEDIUM - `.agents/review/live-risk-sweep-2026-10-30.md` item 43: `MailboxPermissions.razor:22` and
`CalendarPermissions.razor:22` read `Email:NotifyUsersOnPermissionGrant` with `bool.Parse`, so a value such as "yes"
failed both pages for every user. aud-a58 fixed Home only. Both instances hold `true` today.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/sweep-e` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-e`, from master `54dcfb40`)
**Commit**: `c9a2f7ce` (`fix(sweep-e, item 43): ...`) | **Reviewer**: implementer (candidate)

## Approach (as built)

- Both lines read the switch as `Home.razor:42` does: `ConfigBoolean.Read(Config, "Email:NotifyUsersOnPermissionGrant",
  whenBlank: false, whenNotBoolean: false, Logger)` - never throws, a mistyped value is named once in the log, and the
  note claims notifications only for a value that is true (as `EmailService` reads it). Same line, no markup line moves.
- `ConfigBooleanTests.NoPageOrService_ParsesAConfigurationBooleanWithBoolParse` loses its two-page allowance (aud-a58's
  recorded follow-up), and a render test drives both pages' own render tree with "yes", "True" and nothing.
- Help: README Boolean settings (the "until a follow-up lands" sentence replaced). Covered by this branch's
  MailboxPermissions `1.6.11` and CalendarPermissions `1.4.11`.

## Guard proof

| Mutation | Failing tests |
|---|---|
| B Mailbox Permissions back to `bool.Parse` | `ThePermissionPages_GivenANotificationSwitchThatIsNotABoolean_Render_WithoutClaimingNotifications(MailboxPermissions)`; `NoPageOrService_ParsesAConfigurationBooleanWithBoolParse` |
| B Calendar Permissions back to `bool.Parse` | the same theory's CalendarPermissions case; the same scan |

## Gates

Run once for the branch at its tip; see the sweep-e hand-back.
