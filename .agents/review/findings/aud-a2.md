# aud-a2: A secondary alias bypasses protected-user rules

**Severity**: HIGH - a protected mailbox can be modified through one of its other addresses,
across Mailbox Permissions, Calendar Permissions and Out of Office.
**Status**: Verified
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Finding 2 of the 2026-10-16 repository audit, verified against the code before any change.

`Services/PermissionValidator.ValidateTargetMailboxAsync` decided whether to resolve the target
against the directory:

```csharp
bool requiresFullResolution = cfg != null &&
    (cfg.Groups.Length > 0 || cfg.OrganizationalUnits.Length > 0 || cfg.SamAccountNamePatterns.Length > 0
     || cfg.Users.Any(u => !u.Contains('@') && !u.Contains('\\')));
```

With only address-form users configured this is false, and the else branch builds the principal
straight from the operator's typed string:

```csharp
principal = new ResolvedDirectoryPrincipal(
    Source: "PermissionValidator",
    DisplayName: targetMailbox,
    UserPrincipalName: targetMailbox,
    ...
    PrimarySmtpAddress: targetMailbox.Contains('@') ? targetMailbox : null, ...);
```

`CheckAsync` then compares that address against the configured addresses.

**A mailbox has more than one address.** With `vip@contoso.com` protected, an operator who
targets a SECONDARY alias of that same mailbox is compared as `alias@contoso.com` against
`vip@contoso.com`, does not match, and is treated as unprotected. The write then lands on the
protected mailbox through its other name.

One method serves all three modules, so the bypass is available in each.

## Predicted observable failure

A non-servicer grants themselves Full Access to a protected VIP mailbox by entering one of its
secondary SMTP aliases instead of its primary address. The audit record shows a successful,
unremarkable permission change against an address that is not on the protected list.

## Approach

Always resolve. The only way to know which mailbox an address belongs to is to ask the
directory, so the shortcut is removed and `requiresFullResolution` becomes `cfg != null`. The
check and the thing being checked name one object.

**The cost is deliberate and it is an operator-visible behaviour change.** With an address-only
configuration and the directory unreachable, this previously proceeded; it now denies via the
existing `ResolutionStatus.Unavailable` branch. That is the same rule `aud-a3` applies to the
group check - an unanswerable question is not a negative answer - and the alternative is a
protection control that stops protecting exactly during an outage. It is pinned by a test so it
cannot be reverted silently.

No new code path was added; the existing resolution branch, including its Ambiguous and
NotFound handling, now runs for every configuration shape.

## Files changed

- `Services/PermissionValidator.cs`
- `ExchangeAdminWeb.Tests/PermissionValidatorTests.cs`

## Guard proof

The fixture already had a resolution seam (`ScriptedPpService`) but every test used a config
with a Group rule - the shape that already required resolution - so **no test exercised the
skipped path at all, in either direction.** The fixture is parameterised with
`addressOnlyConfig`, and the seam now counts resolution calls and can map an alias to a
different primary.

| Test | What it pins |
| --- | --- |
| `AddressOnlyConfig_StillAsksTheDirectory` | the directory is consulted at all - the finding is a path that never asked |
| `ASecondaryAliasOfAProtectedMailbox_IsRefused` | the alias resolves to the protected primary and is denied - the finding itself |
| `AddressOnlyConfig_DeniesWhenTheDirectoryCannotAnswer` | the accepted cost, so the trade-off cannot be reverted quietly |

Mutation: restoring the original `requiresFullResolution` expression **fails all three**.
Service restored byte-identical (md5 verified) and touched.

Full Release suite 4,261 passed / 0 failed / 3 skipped.

## Coder dispute

None.

## Known gaps

**This fixes which object is compared. It does not make the comparison itself complete.** The
protected-group half of the check has its own holes, raised against `aud-a3` and recorded as
the three holes of `aud-a3b` (an earlier draft of this sentence named them `aud-a3b`, `aud-a3c`
and `aud-a3d`; they were filed as one record, and `aud-a3c` is now a different finding):
domain-local nesting across a domain boundary, primary-group membership being invisible to
`memberOf`, and the protected-group resolver discarding the domain half of a stored
`DOMAIN\Group` entry. An alias is now resolved correctly and then handed to a membership check
that can still answer no when the truth is yes.

Resolution now happens on every validated operation in three modules, where previously an
address-only configuration skipped it. The added directory round-trip has not been measured.

The tests drive a scripted resolver, not a directory. Nothing here proves Exchange returns the
primary for a given alias - that is the assumption the fix rests on, and it is unverified
against the live tenant.

## Reviewer comments

Source: the 2026-10-16 audit, finding 2. Verified by the implementer before changing anything.
Independent review of the fix pending.

## Closeout

Fixed directly on `master`. Not pushed.
