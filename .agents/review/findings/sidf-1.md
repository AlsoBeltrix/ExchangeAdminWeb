# sidf-1: Static AdminGroups are filtered out as non-SIDs (admin lockout)

**Severity**: HIGH -- deploying this range to the current production config would deny every
administrator the `AdminSettings` policy, including the page needed to repair section-access
migration fallout.
**Status**: Verified
**Branch**: -- (default-branch mode)
**Commit**: `4f1de2b`

## Evidence

`Modules/ModuleCatalog.cs:84-86` registers system-module policies such as `AdminSettings` with
the **static** `Security:AdminGroups` array. The sid-1 fix applied `UsableSidsOnly` at
`Authorization/GroupAuthorizationHandler.cs:92` to **every** requirement, static and dynamic
alike.

`Security:AdminGroups` is appsettings config that no migration converts, and it ships as
`DOMAIN\Name` (`appsettings.json.sample:37-39`,
`tools/Install-ExchangeAdminWeb.ps1:468-475`).

**Verified against the live production config** (`D:\inetpub\ExchangeAdminWeb\appsettings.json`)
rather than the sample:

```
AllowedGroups : ANALOG\ExchangeWebAdmins, ANALOG\ExchangeWebPerms
AdminGroups   : ANALOG\ExchangeWebAdmins
```

Every one is a name. All would be discarded before the claims and `IsInRole` checks, leaving the
allowed set empty.

## Predicted observable failure

On deploying `2.3.35`, a real member of `ANALOG\ExchangeWebAdmins` is denied `/admin-settings`
and every module configuration page. The allowed set is empty, so the handler's own
"no groups configured - denying all access" branch fires.

This is worse than a normal lockout: the admin page is where an operator would go to inspect or
repair section-access rows, so the failure removes its own remedy. Recovery would be editing
appsettings on the host by hand.

## What

The sid-1 fix was correct about the `section_access` store and wrong about its blast radius. Two
different stores feed the same handler:

- **`section_access`** -- migrated to SIDs; a name there is an unconverted row and must not
  authorize (sid-1).
- **`Security:AllowedGroups` / `Security:AdminGroups`** -- appsettings, never migrated, an
  explicit **Non-Goal** of `docs/SectionAccessSidStorage-Plan.md`; a name there is the normal and
  only supported format.

Applying one store's rule to the other is the defect. The plan even said the appsettings fallback
was out of scope; the implementation did not honor that.

## Approach

Scope the filter to `requirement.ResolveDynamically`, which is exactly the flag distinguishing
the two stores. Static requirements keep the previous behavior (blank entries skipped, exact
comparison).

The filter also moved OUT of `GroupMembershipChecker.IsMemberOfAny`, which both callers share:
filtering inside it applied the SID rule to whichever caller it was wrong for, whichever way it
was written. The caller knows the store; the comparison should not guess.

`JobAuthorizationSnapshot.Capture` keeps its unconditional filter, verified rather than assumed:
its only caller (`Components/Pages/ConferenceRooms.razor:586`) feeds it from
`SectionAccess.GetGroupsForSection` -- always the dynamic store.

## Files changed

- `Authorization/GroupAuthorizationHandler.cs` -- filter applied only on the dynamic path.
- `Authorization/GroupMembershipChecker.cs` -- `IsMemberOfAny` no longer filters; the rule is the
  caller's, with the reasoning recorded.
- `Services/Jobs/JobAuthorizationSnapshot.cs` -- comment recording why its filter IS
  unconditional.
- `ExchangeAdminWeb.Tests/GroupAuthorizationHandlerTests.cs` -- two tests, one using the exact
  live prod value.
- `ExchangeAdminWeb.Tests/GroupMembershipCheckerTests.cs` -- the sid-1 rule reasserted on the
  filter rather than on `IsMemberOfAny`.

## Guard proof

`GroupAuthorizationHandlerTests.StaticAdminGroups_ByName_StillAuthorize` (uses
`ANALOG\ExchangeWebAdmins`, the real deployed value) and
`StaticGroups_ByName_AreNotFilteredWhileDynamicOnesAre`, which pins both halves together so a
later refactor cannot collapse them.

Reverting to the unconditional filter fails 2 tests -- reproducing the lockout.

## Coder dispute (if any)

None. The finding is correct, and the severity is right: HIGH understates it slightly, since the
failure disables its own repair path.

## Known gaps

`Security:AllowedGroups` / `AdminGroups` remain name-based and therefore carry the original
cross-domain ambiguity for the `Application` and admin policies. That is the pre-existing
condition this work stream explicitly did not take on (plan Non-Goals), not a regression -- but it
means the ambiguity is closed for module access and still open for admin access. Worth its own
decision.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier (fallback grade)`
Harness: codex-cli 0.146.0 (`codex exec`, generation pass).
Range: `d2844e188e2598d96607c5b64b415584eb4928df..019b8144f558f378cc1577fae1e93038a39afb15`
(both SHAs echoed correctly). `capability_ok: true`. Verdict: **findings** (1).
Timestamp: 2026-08-04T04:05Z.

**This was the owed frontier pass**, dispatched after the owner ruled that codex at its default
configured model is the strongest reviewer available here. Probing established that `max` effort
is rejected by this gateway (supported: none/minimal/low/medium/high/xhigh), so **xhigh is the
ceiling** and frontier now resolves to the same pair as standard -- recorded in
`.agents/review/harnesses.local.json` with `grade: fallback`, meaning a future escalation must
halt to the owner rather than redispatch, because it would buy nothing.

The pass was given the earlier sid-1/sid-2 records as readable evidence but explicitly told not
to treat them as a checklist or as assurance. It found a defect **introduced by the sid-1 fix
itself** -- which is the argument for second passes over security-critical code.
