# sid-1: Unmigrated section-access names still authorize users

**Severity**: HIGH - the authorization table can still grant privileged module access on a
name, so the same-named-group ambiguity this work stream exists to remove stays live whenever
the SID migration has not completed.
**Status**: Verified
**Branch**: -- (default-branch mode)
**Commit**: `b8e6dd0`

## Evidence

`Authorization/GroupAuthorizationHandler.cs:106-109` calls `user.IsInRole(allowedGroup)` with
whatever the store holds, and `Authorization/GroupMembershipChecker.cs:87-88` exact-matches the
stored value against extracted claims, which include `ClaimTypes.Role`
(`GroupMembershipChecker.cs:31-35`). Neither requires the value to be a SID.

Trigger: a deferred or halted migration leaves `section_access.group_value` as a legacy name
(`Services/SectionAccessSidMigration.cs:92-110` returns without writing on
`DirectoryUnavailable` or `Halted`), and the principal is a member of a group by that name.

## Predicted observable failure

A user is authorized on a group NAME rather than the resolved SID, so a same-named group in
another trusted domain still matches. Slice 3's commit message claimed the opposite -- that an
unmigrated value "simply stops matching, which fails CLOSED". That claim was wrong.

**Measured on this host, 2026-08-03:**

```
IsInRole('Domain Users')        = True
IsInRole('ANALOG\Domain Users') = True
IsInRole('<a real group SID>')  = True
IsInRole('NoSuchGroupXYZ')      = False
```

`WindowsPrincipal.IsInRole` resolves names AND SIDs. The design note in slice 3 -- that
`IsInRole` "resolves a SID string against the token's SIDs natively" -- is true but not
exclusive, and the fail-closed conclusion drawn from it does not follow.

## What

Deleting the `DOMAIN\`-stripping normalization removed the bare-name-matches-qualified-name
hole, but not name matching itself. As long as a name can reach `IsInRole` or an exact claim
comparison, an unmigrated row authorizes exactly as it did before this work stream -- and the
window where that is true is precisely the window the migration was designed to survive
(AD unreachable at startup, or a halted migration awaiting an admin fix).

## Approach

Filter the allowed-group set to usable SIDs at the point of comparison, in both the handler and
`GroupMembershipChecker`, using the slice-1 validator. A non-SID stored value is now genuinely
inert: it cannot match a claim and cannot reach `IsInRole`.

This makes the fail-closed claim true rather than asserted. The consequence is real and
intended: if the migration has not run, a section whose rows are all names denies everyone, and
the log says so. That is the correct trade -- the alternative is authorizing on an identifier
the app cannot disambiguate. The migration is idempotent and retries every start, and an
administrator can still reach the admin page through a section whose rows did migrate.

Filtering happens in the authorization path rather than at load, because the store is read
fresh on every call (`SectionAccessService.GetGroupsForSection`) and a load-time filter would
have to be duplicated at each of its callers.

## Files changed

- `Authorization/GroupMembershipChecker.cs` -- `IsMemberOfAny` skips any allowed value that is
  not a usable group SID; new `UsableSidsOnly` helper.
- `Authorization/GroupAuthorizationHandler.cs` -- filters `groups` before both the claims match
  and the `IsInRole` loop; logs when rows were dropped as unmigrated.
- `ExchangeAdminWeb.Tests/GroupMembershipCheckerTests.cs` -- the
  `ExactNonSidValuesStillMatch_...` test asserted the now-corrected behavior and is inverted.
- `ExchangeAdminWeb.Tests/GroupAuthorizationHandlerTests.cs` -- static-group tests use SIDs;
  a new test pins that a name-valued row authorizes nobody.

## Guard proof

`GroupMembershipCheckerTests.NonSidAllowedValueMatchesNothing` and
`GroupAuthorizationHandlerTests.DynamicGroups_UnmigratedNameValue_DeniesEveryone`. Reverting
the filter makes both FAIL; restoring makes them PASS.

## Coder dispute (if any)

None. The finding is correct and contradicts a claim I made in the slice 3 commit message. The
reviewer's severity is right: this is the difference between the work stream closing the hole
and merely appearing to.

## Known gaps

The `Security:AllowedGroups` appsettings fallback (`SectionAccessService:68`) is out of scope
per the plan's Non-Goals and still holds names; a section falling back to it now denies rather
than matching by name. That path is legacy and separately slated for retirement.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard`
Harness: codex-cli 0.146.0 (`codex exec`, generation pass).
Range: `b872861db2580e7b3edb00be5c8ab15b7b1a21f4..0a50d01812c95a13824e3db2a05873f33500a65f`
(both SHAs echoed correctly). `capability_ok: true`. Verdict: **findings** (2).
Timestamp: 2026-08-03T18:20Z.

Note on routing: T1 (sensitive paths -- `**/auth*`, `**/schema*`, `**/migrations/**`) matched
this diff and should have routed to frontier. The recorded frontier pin
(`@azure-openai-eus2-global/gpt-5.6-sol`) 404s at the gateway, so the pass ran at standard.
Recorded in `.agents/review/harnesses.local.json` under `tiers.frontier.unavailable`; the owner
must name a live frontier model before a frontier dispatch is possible.
