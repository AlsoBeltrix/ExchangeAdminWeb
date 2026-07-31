# ppv-1: Cloud-only recipient reached by alias returns NotFound, bypassing the NotFound-allowing gates

**Severity**: HIGH — a protected cloud-only principal addressed by a secondary alias is
allowed through the ConferenceRooms and GroupManagement gates, which is the exact bypass
class the Exchange-fallback work existed to close.
**Status**: Verified
**Branch**: default-branch mode (one commit per finding, per repo policy)
**Commit**: `a6927b2`

## Evidence

`Services/ProtectedPrincipalService.cs:352-360` branches on **address equality alone**:

```csharp
if (!string.Equals(recipient.PrimarySmtpAddress, identity, StringComparison.OrdinalIgnoreCase))
{
    return await ResolveWithStatusAsync(recipient.PrimarySmtpAddress);
}
```

The cloud-only branch at `:379-389` is therefore reachable **only** when Exchange echoes
back the same address that was typed. A cloud-only recipient addressed by any secondary
alias takes the alias path instead, re-queries AD for the canonical address, and gets
`NotFound` — because a cloud-only object is not in AD under any address.

`ResolvedRecipient.ExistsOnPrem` exists precisely to distinguish these cases
(`Services/IIdentityResolver.cs:13-22`, populated at
`Services/ExchangeIdentityResolver.cs:100-104`) and
`ResolveWithExchangeFallbackAsync` **never reads it**. Confirmed by grep: outside its own
declaration and tests, the property has no consumers.

Both allow-on-NotFound gates then let the operation through:
`Services/ConferenceRoomProtectionGate.cs:67-95`,
`Services/GroupManagementService.cs:49-67`.

## Predicted observable failure

Protected list contains `vip.cloud@contoso.com` (cloud-only). Operator targets
`vip.alias@contoso.com`:

1. AD misses the alias -> `NotFound`.
2. Exchange resolves it to primary `vip.cloud@contoso.com`, `ExistsOnPrem=false`.
3. Address differs -> alias path -> `ResolveWithStatusAsync("vip.cloud@contoso.com")`.
4. AD has no such object -> `(null, NotFound)`.
5. ConferenceRooms / GroupManagement see `NotFound` and **allow**.

The same principal addressed by its primary address is correctly **denied** (it reaches
the cloud-only branch and matches the protected user row). So protection depends on which
address the operator types — the defining shape of the alias bypass.

Test that would catch it: a fallback test with `ExistsOnPrem: false` **and** a primary
address differing from the queried identity, asserting `Resolved` with a non-null
principal. The existing cloud-only test
(`ProtectedPrincipalExchangeFallbackTests.cs:337`) uses a primary address **equal** to
the input, so it never exercises this combination.

## What

The alias case and the cloud-only case were treated as mutually exclusive. They are not:
a cloud-only mailbox can have secondary aliases, and that intersection falls through the
alias branch into an affirmative `NotFound` that the gates read as "not protected".

## Approach

Branch on `ExistsOnPrem` rather than address equality. When Exchange reports the recipient
is not on-prem-backed, build the null-DN cloud-only principal from the **canonical**
address regardless of what was typed. Re-resolve in AD only for on-prem-backed recipients.
This also makes `ExistsOnPrem` load-bearing rather than dead.

## Files changed

- `Services/ProtectedPrincipalService.cs` — reorder the branches in
  `ResolveWithExchangeFallbackAsync`.
- `ExchangeAdminWeb.Tests/ProtectedPrincipalExchangeFallbackTests.cs` — cover the
  alias + cloud-only intersection.

## Guard proof

`ProtectedPrincipalExchangeFallbackTests` — forcing the pre-fix address-equality branch
(`if (true)` in place of `if (recipient.ExistsOnPrem)`) fails **5** tests, including
`CloudOnlyRecipient_AddressedByAlias_StillResolves` and
`CloudOnlyRecipient_AddressedByAlias_IsProtectedByTheUserRow`. Restored: 18/18 in the
file, 1039/1039 across the solution.

## Coder dispute (if any)

None. Verified against the code; the reviewer is correct and this is the more serious of
the two protection-relevant findings.

## Known gaps

Whether a cloud-only mailbox with secondary aliases exists in this tenant is unverified —
but the plan's own evidence table lists cloud-only mailboxes
(`Jabil.support@analog.com`, `sporting.tickets@analog.com`), and every protected user row
was found to carry three aliases each, so the intersection is not hypothetical.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard`
Harness: codex-cli 0.146.0 (`codex exec --json`, `-s read-only`).
Reviewed SHA: `521bb6e62741c7433a827079d1c53eef0b3b4fec`
Base SHA: `10d159363eeed955d825bf304a143594686b034b`
`capability_ok`: true. Verdict: **findings** (4). 2026-07-31 UTC.

Reviewer's better_approach, adopted as written: "Branch on ExistsOnPrem, not address
equality alone: when Exchange says the recipient is not on-prem-backed, construct the
null-DN ResolvedDirectoryPrincipal using the canonical primary address regardless of
whether the operator typed a secondary alias. Only re-resolve the canonical address in AD
for on-prem-backed recipients."
