# aud-a58: The Home page parses a configuration boolean during render, so a non-Boolean value breaks the Home page for every user

**Severity**: MEDIUM - availability. `Home.razor` runs `bool.Parse(Config["Email:NotifyUsersOnPermissionGrant"]
?? "false")` during render; a value such as "yes" or "1" throws `FormatException` and the Home page fails
to render for every user until the setting is corrected (the aud-a28y/a43 `bool.Parse` class, on the
landing page).
**Status**: Fixed on branch, not merged; guard proof below
**Branch**: `worktree-agent-a7b48a52dcf67eb2b` (from `a7300dee`)
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a51/a48/a49 agent, 2026-10-22, candidate 6 of its hand-back)

## Evidence

At `81809833`: `Components/Pages/Home.razor` line 39; `git grep -n "bool.Parse"` across `Components/` and
`Services/` for the same shape elsewhere (list every site in the record; fix each one the same way).

## Approach (to be written by the implementing agent before code)

- `bool.TryParse` with the default on an unparseable value and one warning naming the setting and the value,
  once per process per value (the aud-a43 shape); the page renders. Apply the same to every other
  `bool.Parse` on a configuration value the grep finds.
- Tests: a render test (the page's reflection driver or `BuildRenderTree`) with "yes"; a source check that
  no `bool.Parse(Config[` remains in pages or services. Mutation at the call site: `bool.Parse` again.

*Amended 2026-10-23 by the implementing agent, from the sites the grep found (Plan, evidence 3 and 4):* "with the default"
holds for Home's switch, and for the same switch in `EmailService`. Two of the other switches guard something, and their
default is the permissive reading. `Email:SmtpUseSsl` read as false sends mail in clear text. `ServiceNow:Enabled` read as
false makes every ticket valid. A value that is neither true nor false is read the way that keeps what the switch guards
in force: TLS on, tickets validated. Blank and absent keep the documented default everywhere.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"). Admitted from the aud-a51/a48/a49 hand-back, candidate 6. One
commit `fix(config): ... (aud-a58)`, third of three on branch `worktree-agent-a7b48a52dcf67eb2b` (from `a7300dee`), after
aud-a56 and aud-a57. The Home page, the mail service and the ServiceNow client are shared and belong to no module, so the
base app version is due; the supervisor owns the bump.

Evidence, re-read at `a7300dee`. `git grep -n "bool\.Parse\|Boolean\.Parse\|Convert\.ToBoolean"` over `Components/` and
`Services/`, comments aside, finds every site that parses a configuration value:
1. `Components/Pages/Home.razor` 39: `bool.Parse(Config["Email:NotifyUsersOnPermissionGrant"] ?? "false")`, during render.
   A value such as "yes" throws `FormatException`, and the landing page fails for every user.
2. `Components/Pages/MailboxPermissions.razor` 22 and `Components/Pages/CalendarPermissions.razor` 22: the same
   expression, during render. Both files belong to another agent, so they are left for a follow-up (the section below).
3. `Services/EmailService.cs`:
   - 50: `bool.Parse(config["Email:SmtpUseSsl"] ?? "false")`.
   - 54: `bool.Parse(config["Email:NotifyUsersOnPermissionGrant"] ?? "false")`.
   - Both run in the constructor of a singleton that every notifying page and service injects. A value that is not a
     Boolean fails that construction at every resolution.
   - `_smtpUseSsl` picks `SecureSocketOptions.SslOnConnect` or `None` (993-996). False sends in clear text, password
     resets included.
4. `Services/ServiceNowService.cs` 30: `bool.Parse(config["ServiceNow:Enabled"] ?? "false")`, in the constructor of the
   singleton every ticketed page injects. While it is false, `ValidateTicketAsync` answers every ticket valid (55-62).
   Nineteen call sites in ten components call it directly.
5. Comments only, not sites: `Services/PermissionValidator.cs` 129, `Services/MigrationService.cs` 1076,
   `Models/MigrationModels.cs` 109.

Seen but not this record's, because the parser is a different one: `Services/OperationTraceService.cs` 15
(`config.GetValue<bool?>("OperationTrace:Enabled")`, which throws on a value that is not a Boolean) and
`Services/EmailService.cs` 47 (`int.Parse(config["Email:SmtpPort"] ?? "25")`). Both are listed in the hand-back as
candidates.

**Rule.** A configuration Boolean never throws. Absent or blank is its documented default. A value that is neither true
nor false is read the way that does not widen what the app does, and is named in one warning, once per process for each
setting and value.

1. New `Services/ConfigBoolean.cs`, with
   `internal static bool Read(IConfiguration config, string key, bool whenBlank, bool whenNotBoolean, ILogger logger)`:
   - `bool.TryParse`, so any case is read and surrounding whitespace is ignored.
   - Absent or blank is `whenBlank`.
   - A value that is neither true nor false is `whenNotBoolean`, with the warning "The configuration value {Key} is
     '{Value}', which is neither true nor false; it is read as {Reading} until it is corrected".
   - The warning is given once per process for each key and value: a static set and `TryAdd`, the aud-a43 shape. It sits
     inside a try whose catch takes any exception (aud-a28u).
2. `Components/Pages/Home.razor`:
   - `@inject ILogger<Home> Logger`.
   - Line 39 reads `ConfigBoolean.Read(Config, "Email:NotifyUsersOnPermissionGrant", whenBlank: false, whenNotBoolean:
     false, Logger)`.
   - That is the default, as the record says, and the same reading `EmailService` makes. The notice therefore never
     claims notifications that are not sent.
   - Home is "no clickable controls" in `ClickGateRegistry`, so the added line moves no key.
3. `Services/EmailService.cs`:
   - `_smtpUseSsl`: blank false, not a Boolean true.
   - `_notifyUsers`: false for both.
4. `Services/ServiceNowService.cs` `_enabled`: blank false, not a Boolean true.
5. Why the two `true` readings: before this fix, each of these values stopped every page that injects the service.
   - Neither reading is a step back in availability. Only the restrictive one is not a step back in security.
   - A relay without TLS then refuses the connection. A deployment without ServiceNow then fails each ticket's check.
     Both fail visibly, and the warning names the value.
   - If the supervisor rules otherwise, each is a one-word change at its call site.
6. `README.md`, "Email Notifications": how the three Boolean settings read a value that is neither true nor false.
7. Comments after the mutation round, checked code-identical.

Tests, new `ExchangeAdminWeb.Tests/ConfigBooleanTests.cs`. Each case uses a value of its own, because the set of named
values lasts for the process:
- `Read_TakesTrueAndFalseInAnyCase_BlankAsItsDefault_AndAnythingElseAsItsReading` (theory).
- `AValueThatIsNotABoolean_IsNamedOncePerProcess`: two reads of one value give one warning, and another value gives one
  more.
- `ALoggerThatThrows_DoesNotStopTheAnswer`.
- `TheHomePage_GivenANotificationSwitchThatIsNotABoolean_Renders_WithoutClaimingNotifications`:
  - The page's own `BuildRenderTree` is driven twice, with the injected services set by reflection.
  - Neither render throws, the notice is absent, and one warning names the setting and the value.
  - With "True" the notice is shown.
- `TheMailService_GivenSwitchesThatAreNotBooleans_IsBuilt_WithTlsOn_AndUserNotificationsOff`.
- `TheServiceNowClient_GivenAnEnabledSwitchThatIsNotABoolean_IsBuilt_AndValidatesTickets`.
- `NoPageOrService_ParsesAConfigurationBooleanWithBoolParse`:
  - A source check over `Components/` and `Services/` (`.razor`, `.cs`), with comments blanked by
    `RazorSyntax.CommentsBlanked`, for `bool.Parse(` and `Boolean.Parse(`.
  - Its one allowance is the two pages below, at most one each, until the follow-up removes it.

Mutations at the call site (md5 before and after, the named failing test, restored from the index, touched, rebuilt,
md5 matched, green):
- M1 Home's `bool.Parse` again.
- Extra E1 `EmailService`'s `Email:SmtpUseSsl` `bool.Parse` again.
- Extra E2 `ServiceNowService`'s `bool.Parse` again.
- Extra E3 the once-per-value guard removed.
- Extra E4 `Email:SmtpUseSsl`'s reading of a value that is not a Boolean made the default, false.

Filter:
`FullyQualifiedName~ConfigBooleanTests|FullyQualifiedName~EmailService|FullyQualifiedName~ServiceNow|FullyQualifiedName~TicketValidation|FullyQualifiedName~CloudPasswordResetEmailTests|FullyQualifiedName~IntuneDeviceServiceTests`.
`ClickGate*` and `ProgressRegistryTests` are run alone as well.

Files:
- `Services/ConfigBoolean.cs` (new)
- `Components/Pages/Home.razor`
- `Services/EmailService.cs`
- `Services/ServiceNowService.cs`
- `ExchangeAdminWeb.Tests/ConfigBooleanTests.cs` (new)
- `README.md`
- this record

## Left for a follow-up (file owned by another agent)

`Components/Pages/MailboxPermissions.razor` 22 and `Components/Pages/CalendarPermissions.razor` 22, both at `a7300dee`:

    <small>All permission changes are logged for audit purposes.@if (bool.Parse(Config["Email:NotifyUsersOnPermissionGrant"] ?? "false")){<text> Affected users will receive email notifications when permissions are granted or removed.</text>}</small>

The change needed, the same on both pages:
- Replace `bool.Parse(Config["Email:NotifyUsersOnPermissionGrant"] ?? "false")` with
  `ConfigBoolean.Read(Config, "Email:NotifyUsersOnPermissionGrant", whenBlank: false, whenNotBoolean: false, Logger)`.
- Both pages already inject `Logger` (line 16), so the edit stays on line 22, and no `ClickGateRegistry` line key moves.
- Then remove the two pages from `NoPageOrService_ParsesAConfigurationBooleanWithBoolParse`'s allowance.

Until then, a value that is neither true nor false still breaks both pages' render, as it broke Home's.

## Fix

- New `Services/ConfigBoolean.cs`, `internal static class ConfigBoolean`:
  - `Read(IConfiguration config, string key, bool whenBlank, bool whenNotBoolean, ILogger logger)`. Blank or absent is
    `whenBlank`; `bool.TryParse` decides the rest; anything else is `whenNotBoolean`.
  - A value that is not a Boolean is warned about through `Named.TryAdd((key, value), 0)`, a static
    `ConcurrentDictionary`, so once per process for each key and value. The warning is "The configuration value {Key} is
    '{Value}', which is neither true nor false; it is read as {Reading} until it is corrected", with `{Reading}` "true" or
    "false". It sits in `try { ... } catch (Exception) { }`.
- `Components/Pages/Home.razor`:
  - `@inject ILogger<Home> Logger`.
  - The notice's `@if` reads `ConfigBoolean.Read(Config, "Email:NotifyUsersOnPermissionGrant", whenBlank: false,
    whenNotBoolean: false, Logger)`, under a two-line Razor comment.
  - 103 lines -> 106. "no clickable controls" in `ClickGateRegistry`; `ProgressRegistry`'s one exemption,
    `OnInitializedAsync`, is untouched.
- `Services/EmailService.cs`:
  - `_smtpUseSsl` is `ConfigBoolean.Read(config, "Email:SmtpUseSsl", whenBlank: false, whenNotBoolean: true, logger)`.
  - `_notifyUsers` is `ConfigBoolean.Read(config, "Email:NotifyUsersOnPermissionGrant", whenBlank: false, whenNotBoolean:
    false, logger)`.
  - A three-line comment sits above them.
- `Services/ServiceNowService.cs`: `_enabled` is `ConfigBoolean.Read(config, "ServiceNow:Enabled", whenBlank: false,
  whenNotBoolean: true, logger)`, under a two-line comment.
- `README.md`, "Email Notifications": a "Boolean settings" paragraph covering the three settings, their readings, and the
  two pages still left.

Tests, new `ExchangeAdminWeb.Tests/ConfigBooleanTests.cs`, 20 cases:
- `Read_TakesTrueAndFalseInAnyCase_AndBlankAsItsDefault` (10).
- `Read_TakesAValueThatIsNotABoolean_AsItsReading_AndNamesIt` (4: "yes", "1", "no", "on").
- `AValueThatIsNotABoolean_IsNamedOncePerProcess`.
- `ALoggerThatThrows_DoesNotStopTheAnswer`.
- `TheHomePage_GivenANotificationSwitchThatIsNotABoolean_Renders_WithoutClaimingNotifications`.
- `TheMailService_GivenSwitchesThatAreNotBooleans_IsBuilt_WithTlsOn_AndUserNotificationsOff`: also the defaults when left
  out, and "True" / "true" read as written.
- `TheServiceNowClient_GivenAnEnabledSwitchThatIsNotABoolean_IsBuilt_AndValidatesTickets`: also dormant when left out.
- `NoPageOrService_ParsesAConfigurationBooleanWithBoolParse`.
- Helpers: `HomePageWith` (a real module catalog, module config, enablement and a telemetry service that is off, set by
  reflection), `Rendered`, `RepoRoot`, `CapturingLogger<T>` and `ThrowingLogger`.

Caller-visible changes, by design:
- A Boolean setting that is neither true nor false no longer throws. That applies to Home's render, `EmailService`'s
  construction and `ServiceNowService`'s construction. It is warned about once per process for each setting and value.
- Read as before: "true" and "false" in any case, with surrounding white space; absent is false.
- Blank is now false. It threw before.
- A value that is not a Boolean is now read as:
  - `Email:SmtpUseSsl` `true`, TLS on connect.
  - `ServiceNow:Enabled` `true`, tickets checked.
  - `Email:NotifyUsersOnPermissionGrant` `false`, and the Home notice not shown.

## Guard proof

Reproduction:
- M1, E1 and E2 each put one call site back as it was at `a7300dee`, `bool.Parse` with its `?? "false"`.
- Each threw `FormatException` on a value that is not a Boolean: the Home page out of its render, the mail service and the
  ServiceNow client out of their constructors. That is the defect as the record states it.
- The source check named the line each time.

Same driver as aud-a56's (session scratchpad `mutate/mutate.ps1`, spec `mutate/round-a58.ps1`, log
`mutate/a58-mutations.log`). The filter (206 tests) covers the new tests, every suite that builds the mail service or the
ServiceNow client, ticket validation, the cloud password reset's mail gate and the Intune notifier's:
`FullyQualifiedName~ConfigBooleanTests|FullyQualifiedName~EmailService|FullyQualifiedName~ServiceNow|FullyQualifiedName~TicketValidation|FullyQualifiedName~CloudPasswordResetEmailTests|FullyQualifiedName~IntuneDeviceServiceTests`

Every mutant built with 0 errors, and each restore matched its original MD5. After the round, a rebuild on the restored
bytes and the filter: 206/206 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 Home's `bool.Parse(Config["Email:NotifyUsersOnPermissionGrant"] ?? "false")` again | `Home.razor`, `B6274EEF6A124C8775908479E9D88052` | `A4ED2C604803A8ADA21EA0309766FEC0` | 2 of 206: `TheHomePage_GivenANotificationSwitchThatIsNotABoolean_...` - the render threw (`FormatException`, inside the reflection call's `TargetInvocationException`); `NoPageOrService_ParsesAConfigurationBooleanWithBoolParse` - "Components/Pages/Home.razor:42" |
| extra E1 `EmailService`'s `bool.Parse(config["Email:SmtpUseSsl"] ?? "false")` again | `EmailService.cs`, `9EEDE15E04C51C3F2F07CA26726D6363` | `64013B8FF7325A55A89482CF6E512266` | 2 of 206: `TheMailService_GivenSwitchesThatAreNotBooleans_...` - `FormatException` out of the constructor; the source check - "Services/EmailService.cs:53" |
| extra E2 `ServiceNowService`'s `bool.Parse(config["ServiceNow:Enabled"] ?? "false")` again | `ServiceNowService.cs`, `ED7947483948A0C4E6D14383E6220E0C` | `95675807051CD4E7B3D48168B02465D0` | 2 of 206: `TheServiceNowClient_GivenAnEnabledSwitchThatIsNotABoolean_...` - `FormatException` out of the constructor; the source check - "Services/ServiceNowService.cs:32" |
| extra E3 the once-per-value guard removed (`Named.TryAdd((key, value), 0) \|\| true`) | `ConfigBoolean.cs`, `BBFA5690D797294A12D18C38F046BCA9` | `679B6B7D333DC70E255813F3AA97450E` | 2 of 206: `AValueThatIsNotABoolean_IsNamedOncePerProcess` - two warnings where one; `TheHomePage_...` - two renders, two warnings where one |
| extra E4 `Email:SmtpUseSsl`'s reading of a value that is not a Boolean made the default (`whenNotBoolean: false`) | `EmailService.cs`, `9EEDE15E04C51C3F2F07CA26726D6363` | `7964C5641AEE029921901FAE46D7E3BC` | 1 of 206: `TheMailService_...` - "a mistyped Email:SmtpUseSsl was read as clear text" |

Unchanged through the round: `ConfigBooleanTests.cs` `6AECACFDAD1ABFDD6C999BC47E11C036`.

`ClickGate*` and `ProgressRegistryTests` passed on this tree, in the same run as the filter before the round (557 of 557).
They are run alone again at the branch tip.

Not run, by design:
- The Home page in a browser - its own render tree is driven twice with the setting the record names.
- A real SMTP relay or ServiceNow instance - the readings are asserted on the built services, and nothing is sent.

## Known gaps

- The two pages under "Left for a follow-up" still throw during render on a value of
  `Email:NotifyUsersOnPermissionGrant` that is not a Boolean. The source check allows exactly those two sites, one each,
  until the follow-up.
- The source check's allowance does not demand that the two sites still be there. A follow-up that fixes them passes
  the check before it removes the allowance; the record names that removal as part of the follow-up.
- `whenNotBoolean: true` for `Email:SmtpUseSsl` and `ServiceNow:Enabled` is this agent's reading of "fail closed", argued
  in the Plan's amendment and in point 5. If the supervisor rules for the documented default instead, the change is one
  word at each call site, plus E4's test assertion.
- The once-per-value set lasts for the process and is never trimmed. It holds one entry per distinct mistyped value seen.
- Other configuration reads with a parser that throws are not this record's: `Services/OperationTraceService.cs` 15
  (`GetValue<bool?>`) and `Services/EmailService.cs` 47 (`int.Parse`). Both are listed in the hand-back as candidates.
