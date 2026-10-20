# aud-a27: the security team was not the recipient, and the page said they were

**Severity**: HIGH. An emergency disable is the operation the security team most needs to
hear about, and the operator was told they had heard about it. The UI claim is the worse
half: a true statement that nobody was notified leaves the operator able to act, and a false
one removes the only remaining chance to.
**Status**: Verified and fixed; review pending
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Finding 27 of the 2026-10-16 audit, verified against the code.

### The recipient

`EmergencyDisableService.SendSecurityNotificationAsync` read its own configured address and
then never used it:

```csharp
var notifyEmail = _moduleConfig.GetValue("EmergencyDisable", "NotifySecurityTeam");
if (string.IsNullOrWhiteSpace(notifyEmail))
{
    _logger.LogWarning("EmergencyDisable NotifySecurityTeam email not configured, skipping notification");
    return;
}
...
await _email.SendAdminNotificationAsync(performedBy, ip, "EmergencyDisable", success, ticket, stepDetails, ...);
```

`notifyEmail` is a **switch**, not a destination. `SendAdminNotificationAsync` addresses
`Email:AdminNotificationEmail`. So a site that pointed this module at its SOC had the notices
land with the general administrators instead, and configuring the module's own field changed
only whether any mail was sent at all.

### The claim

`Components/Pages/EmergencyDisable.razor` asserted the notification in three places, none of
them conditional on anything:

- the pre-action banner - "This action is audited and security team is notified immediately"
- the result panel - "Security team has been notified."
- the result line - "Account {name} has been disabled. Security team notified."

All three were printed when no recipient was configured, and when SMTP refused the message -
`SendEmailAsync` swallows its exceptions and logs, so a total delivery failure was invisible
to the caller.

## Predicted observable failure

An operator emergency-disables a compromised account during an incident. No
`NotifySecurityTeam` value is set, or the SMTP host rejects the message. The page says the
security team was notified. The operator, reasonably, does not tell them. The security team
learns about the disabled account from the user.

## Approach

**The configured address is the recipient.** A new `EmailService.SendModuleNotificationAsync`
takes an explicit recipient list, builds the same mail as the admin overload, and returns a
`NotificationDispatch(Requested, Delivered, FirstError)`. The existing
`SendAdminNotificationAsync` now delegates to it with `_adminEmail`, so its behaviour is
unchanged for every other caller.

**The outcome is carried, not assumed.** `SendSecurityNotificationAsync` returns a
`SecurityNotificationOutcome` - `NotAttempted`, `NotConfigured`, `Sent`, `PartiallySent`,
`Failed` - which `EmergencyDisableResult` carries to the page. `PartiallySent` exists because
a two-recipient send that reached one is not a success (Known Failure Class 2).

**One sentence, one source.** `SecurityNotificationText.For(outcome)` lives beside the enum,
and the page prints it rather than composing its own. The pre-action banner is the one claim
made before the outcome exists, so it is driven by whether a recipient is configured - which
is knowable at render time.

**The gap is recorded.** When the outcome is anything but `Sent`, a
`EmergencyDisable_NotificationGap` audit record is written. The operation's own audit record
is written *before* the notification is attempted and so cannot carry the outcome; reordering
the two would put a sent notification ahead of the durable record of the operation, which is
the wrong way round.

## Files changed

- `Services/EmailService.cs` - `NotificationDispatch`, `SendModuleNotificationAsync`
- `Services/EmergencyDisableService.cs` - outcome enum, text, recipient, gap record
- `Components/Pages/EmergencyDisable.razor` - three claims, one new property
- `ExchangeAdminWeb.Tests/EmergencyDisableNotificationTests.cs`
- `ExchangeAdminWeb.Tests/EmergencyDisableServiceTests.cs` - the catalog version pin
- `ExchangeAdminWeb.csproj` - 2.29.0 -> 2.30.0. `EmailService` is shared code, so the base
  app version bumps as well as the module's.
- `Modules/ModuleCatalog.cs` - EmergencyDisable 1.4.0 -> 1.5.0

`EmergencyDisable.razor` carries no `ClickGateRegistry` line keys (it is listed "tier 3, not
approved"), so the added lines move nothing.

## Guard proof

Behavioural. The notification step is reachable on its own with a substituted `EmailService`,
so these drive the real method and read the real recipient it addressed - no source scan
stands in for the recipient claim.

| Test | What it pins |
| --- | --- |
| `TheConfiguredSecurityAddress_IsTheRecipient` | the mail goes to `NotifySecurityTeam`, and **not** through the admin overload |
| `NoConfiguredAddress_ReportsNotConfiguredAndSendsNothing` | no recipient means no send and an honest outcome |
| `TheOutcomeFollowsWhatWasActuallyDelivered` (3 cases) | 1/0 is Failed, 2/1 is PartiallySent, 2/2 is Sent |
| `AThrowingEmailService_ReportsFailedRatherThanSent` | an exception does not read as success |
| `OnlyASentNotification_ClaimsTheSecurityTeamWasNotified` | only `Sent` produces the claim; the two "nobody heard" outcomes say NOT NOTIFIED |
| `NoRecipient_IsNeitherFullyNorPartlyDelivered` | zero requested must not read as "all of them got it" |
| `ThePageNoLongerStatesTheNotificationUnconditionally` | the page repeats the service's sentence instead of writing its own |

The admin list is deliberately configured to a *different* address in the fixture, so a
regression has somewhere wrong to go and the test can see it.

| Mutation | Result |
| --- | --- |
| restore `SendAdminNotificationAsync` with a hardcoded delivered dispatch | FAIL - 4 tests, including the recipient assertion |
| `Failed` mapped to "Security team notified." | FAIL - `OnlyASentNotification_...` |
| page claim restored unconditionally | FAIL - `ThePageNoLongerStatesTheNotificationUnconditionally` |

Both sources restored byte-identical (md5 verified), touched, rebuilt.

Gate: Release 0 errors; suite 4408 passed / 3 known AD skips / 0 failed; format and
`git diff --check` clean.

## Known gaps

- **No SMTP was exercised.** `NotificationDispatch` is produced by a substitute in every
  test; that the real `SendEmailOrThrowAsync` throws on the failures we count as failures is
  taken from its code, not demonstrated here.
- **The banner is a prediction, not a result.** It reports whether a recipient is configured
  at render time. A recipient that is configured but undeliverable still shows the
  reassuring banner before the click - only the result sentence can know better.
- **Other callers of the admin overload were not audited** for the same confusion. The
  finding named this module. `SendAdminNotificationAsync` keeps its old behaviour exactly, so
  nothing else changed, but whether another module also has its own recipient field going
  unused is an open question.
- The gap record is written only when the outcome is not `Sent`. A reader counting
  notifications cannot confirm from the audit log alone that the other ones went.

## Closeout

Fixed directly on `master`. Independent review pending - and currently impossible: both
review harnesses are returning 401 (`.agents/state.md`).
