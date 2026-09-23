# cpr-4: A missing sync-status property admitted a synced account as cloud-only

**Severity**: HIGH - the synced-account rule is the module's scope boundary. A synced account's
password is mastered on-premises, so writing `passwordProfile` to it either fails or is silently
overwritten at the next sync. The operator is told the reset succeeded either way, and the owner
is mailed a password that does not work.
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: the commit that completes this record

## Evidence

`Services/CloudPasswordResetService.cs` as of `56451bd`:

```csharp
var syncEnabled = root.TryGetProperty("onPremisesSyncEnabled", out var syncProp)
    && syncProp.ValueKind == JsonValueKind.True;
```

Two states out of three. A property that is ABSENT from the response, or present with an
unexpected kind, produced `false`, and the refusal below it only fires on `true`. The target was
then returned with `CloudOnly: true`.

`docs/CloudPasswordReset-Plan.md`, "The synced-account rule": *"absent a definite
`onPremisesSyncEnabled: false`, the target is refused. An unreadable or missing property is a
refusal, never an assumption of cloud-only (Known Failure Class 3)."* AC1 says the same.

## Predicted observable failure

Graph answers 200 for a synced user but does not project `onPremisesSyncEnabled` - a `$select`
that did not apply, a permissions-shaped partial response, or a schema change. Preflight admits
the account, the operator proceeds, and the module PATCHes a password onto an account mastered
on-premises. Entra either rejects the write or accepts it and the next directory sync overwrites
it. The operator sees a success; the owner gets a password that does not work.

## Approach

Three states, not two, in a named pure function `ClassifySyncState`:

- present and `true` -> `Synced`. Refuse, as before.
- present and `null` or `false` -> `CloudOnly`. In scope.
- absent, or any other JSON kind -> `Unknown`. **Refuse**, as `GraphReadFailed`, with a message
  that says the sync status could not be read rather than implying the account is synced.

**The plan's literal wording could not be implemented and the plan is corrected in the same
commit.** Graph does not return `false` for this property: it returns `true` for a synced account
and `null` for one that is not. Requiring "a definite `false`" would have refused this module's
entire population. The rule the plan is expressing - an unanswered question must not be read as a
permissive answer - is what the three states implement.

## Files changed

- `Services/CloudPasswordResetService.cs` - `ClassifySyncState`, the `SyncState` enum, and the
  Unknown refusal branch.
- `ExchangeAdminWeb.Tests/CloudPasswordResetDestinationTests.cs` - `CloudPasswordResetSyncStateTests`.
- `docs/CloudPasswordReset-Plan.md` - the synced-account rule and AC1, corrected against the API.

## Guard proof

`CloudPasswordResetSyncStateTests`, six tests. Mutation: restore the two-state read
(`TryGetProperty(...) && ValueKind == JsonValueKind.True`) and
`An_absent_property_is_unknown_and_must_not_read_as_cloud_only`,
`An_unexpected_kind_is_unknown` and `Only_a_definite_negative_admits_the_target` all fail.

## Coder dispute

None on the defect. One correction to the recommendation: it proposed refusing "unless the
property is present and definitively proves the target is cloud-only", which taken literally
means requiring `false` and would refuse every cloud-only account, since Graph sends `null`. The
fix treats `null` as the definite negative it is.

## Known gaps

`ClassifySyncState` is tested against constructed JSON, not against a live Graph response. Whether
Graph can return 200 with the property absent is an assumption about the API, not a measured fact -
the guard is cheap and fails closed, so it is worth having either way, but the live reconnaissance
pass should record what the real payload looks like.

## Reviewer comments

Round 1, Change review over `56451bd^..56451bd`:
Reviewer: codex / `@azure-openai-eus2-global/gpt-5.5-dzs` / xhigh / standard, 2026-09-23.
Capability proof passed. Verdict **unsound**, one HIGH finding, no CRITICAL.
It cleared the destination decision, the fail-closed directory reads, the password-in-logs check,
the LDAP escaping and the log levels; the only finding was this one.

Per `.agents/decisions.md` 2026-08-31, reviewer verification rounds are CRITICAL-only and need an
explicit owner go. A HIGH closes on the coder-side guard proof above.

## Closeout

Fix, tests, plan correction and this record land in one commit on master.
