# tll-1: An unreadable signInActivity shape is reported as a verified absence

**Severity**: HIGH - it manufactures the one reading this module exists to prevent. A cloud
source that came back in a shape we cannot read is reported as a source that answered "no
sign-ins", and with both log queries empty that composes to `LogVerified` - the single state the
plan says is safe to act on. Acting on it means disabling a live account.
**Status**: Open
**Branch**: -- (direct-to-main)
**Commit**: --

## Evidence

`Services/CloudSignInService.cs:301-302`:

```csharp
if (!user.TryGetProperty("signInActivity", out var activity) || activity.ValueKind != JsonValueKind.Object)
    return (new CloudSignInAnswer(true, null, null), null);
```

That single branch covers three different facts and returns the same answer for all of them:

1. the property is **absent** - Graph's shape for an account with no recorded activity, and a
   genuine answer;
2. the property is **JSON null** - also Graph's shape for no recorded activity, also genuine;
3. the property is **any other non-object** (string, number, array) - a response this module
   cannot read at all.

The third is not an answer. The class remarks four paragraphs above the method state the rule it
breaks: *"A value that cannot be read is a source that did not answer."*

A second instance of the same rule break, `Services/CloudSignInService.cs:404-406`:

```csharp
var raw = value.GetString();
if (string.IsNullOrWhiteSpace(raw))
    return DateRead.Absent;
```

A date field that is PRESENT and blank is unreadable, not absent. `Absent` is not discarded at
`:307-311` - only `Malformed` is - so a blank date string reads as "no sign-in of that kind".

Triggering condition, either form:

```json
{"value":[{"userPrincipalName":"u@example.test","signInActivity":"bad"}]}
{"value":[{"userPrincipalName":"u@example.test","signInActivity":{"lastSignInDateTime":""}}]}
```

with both raw-log queries returning an empty `value` array - the ordinary shape for an account
with nothing in the retained 30-day window.

## Predicted observable failure

`CloudSignInLookup.Result.Verified == LogVerified` and `NoSignInReported == true`, with no error
on either source. The page renders the strongest possible claim - both sources agree this person
has never signed in - from a response it could not parse. Per `docs/TrueLastLogon-Plan.md`,
`LogVerified` is the only value marked "safe to act on", so this is the exact input that reaches
an account-disable decision.

## Approach

Split the branch so each fact gets its own answer, and make a present-but-unreadable date
`Malformed` rather than `Absent`:

- absent or JSON null -> answered, no activity (unchanged, and it is the real Graph shape);
- any other non-object -> `DidNotAnswer` with a named reason;
- a present blank date string -> `Malformed`, which the caller already discards.

## Files changed

- `Services/CloudSignInService.cs`
- `ExchangeAdminWeb.Tests/CloudSignInServiceTests.cs`

## Guard proof

To be recorded with the fix commit.

## Coder dispute

None. The finding is correct and the contradiction is mine: the file states the rule in its own
class remarks and then does not apply it at this branch.

## Known gaps

Graph emitting a non-object `signInActivity` has not been observed and is unlikely. The finding
stands on impact rather than probability: the failure is silent, it is the module's worst
reading, and the guard costs one branch.

## Reviewer comments

To be recorded.

## Closeout

Pending.
