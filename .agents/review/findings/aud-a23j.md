# aud-a23j: Migration - a failed excluded-group check leaves the user Eligible, and the group match is a substring match on the DN

**Severity**: MEDIUM - fail-open eligibility, the aud-a23h class on the AD step. When the excluded-group
check throws (AD unreachable, the group unresolvable), the eligibility check only adds a warning and the
user stays Eligible, so a batch can be created for a member of an excluded group. The match itself is a
substring test on the group DN, so a group whose DN contains the configured DN as a substring matches too.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: `worktree-agent-a03d6bd111b571e5a` (forked at `b16898dc`)
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a23h/a23i agent, 2026-10-22, candidate 1 of its hand-back)

## Evidence

At `ab5361c8`: `Services/MigrationService.cs`, the excluded-group check inside the eligibility path (its
catch adds a warning; the `Contains` on the DN).

## Approach (to be written by the implementing agent before code)

- A failed excluded-group check yields `MigrationStatus.Undetermined` with "the excluded-group check could
  not be completed: <reason category>" - the aud-a23h shape; batch creation stays blocked.
- The group match compares DNs exactly (ordinal, ignoring case, as AD does) against the user's memberOf
  values, never a substring.
- Tests through `MigrationStubExchange` and the AD stand-in; mutations at the call site: the warning-only
  catch again; `Contains` again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `fffdc451`. One commit `fix(migration): ...
(aud-a23j)` on branch `worktree-agent-a03d6bd111b571e5a` (forked at `b16898dc`), after aud-a23n. Module-scoped:
Migration (the bump is the supervisor's). Line numbers at `b16898dc`.

**Found while planning.** The check is not in `MigrationService` but in `Services/ExchangeServiceBase.cs`
`CheckAdGroupMembership` (703-748, the substring at 741), whose only caller is `MigrationService` 173. It returns early,
leaving the user Eligible, when no directory user has the address as its UPN (727: `memberOf == null`) - the same
fail-open as a throw. And the setting is "Comma-separated AD groups" (`Modules/ModuleCatalog.cs` 435), split at commas
(`MigrationService` 38-49), so a configured value is a group NAME in practice: a DN cannot survive the split, and the
substring is what made a name match its group's DN. Comparing the configured text to `memberOf` exactly would never
match a name, and every member of an excluded group would read Eligible. The Constitution's rule decides it: "OU and
group checks must be based on resolved directory objects, not substring matches".

1. `Services/MigrationService.cs`: the check moves here, in a new private `CheckExcludedGroups`, and 173 calls it.
   `ExchangeServiceBase` is not changed: `CheckAdGroupMembership` is left unused (its removal is a base-app change -
   named in the hand-back).
   - Its own Active Directory session, as before (not the Exchange pool), opened through a new `internal virtual
     Runspace CreateDirectoryRunspace()` test seam - the shape of `GroupManagementService`'s. Production builds the
     session `CheckAdGroupMembership` built. Every command through the base's `Invoke` (fails on an error, and on
     HadErrors), under `-ErrorAction Stop`.
   - `Import-Module ActiveDirectory`; `Get-ADUser -Filter "UserPrincipalName -eq '<address>'" -Properties memberOf`,
     as before. Exactly one user is required: none, or more than one, cannot be checked. Its `memberOf` must be
     reported as a list (absent, or not a list, cannot be checked); each value is a DN.
   - Each configured group is RESOLVED, `Get-ADGroup -Identity <value>` (a DN, GUID, SID or sAMAccountName, what the
     cmdlet accepts), to exactly one group with a `DistinguishedName`. The match is that DN against the user's
     `memberOf` values, exact, ordinal ignoring case (as AD compares DNs) - never a substring. A match is
     "Member of excluded group: <configured value>", Ineligible, as before.
2. 175-179, the catch: a check that cannot be completed - any throw, or any of the "cannot be checked" shapes above -
   is no answer. An Eligible verdict becomes `Undetermined` (the aud-a23h shape: the size step and every creation path
   pass it by); an Ineligible one stands, its reason known. The reason, in the cmdlet's own words (`ResolveFailure`):
   "Could not be determined: the excluded-group check could not be completed: <reason>", where the reason names the
   step - "the ActiveDirectory module could not be loaded: ...", "Get-ADUser failed: ...", "no directory user has the
   user principal name <address>", "N directory users have ...", "the directory reported no memberOf for <address>",
   "the excluded group '<value>' could not be resolved: ...", "... resolved to N groups".
3. **Labelled consequences, not assumptions about Exchange or AD behaviour:** a configured value must be something
   `Get-ADGroup -Identity` resolves; a display name, a CN that differs from the sAMAccountName, or a fragment of a DN
   cut at its commas by the setting now reads "could not be resolved", and every ToCloud check is Undetermined until
   the setting is corrected - visible and blocking, never a guess. The user is still found by UPN in the host's own
   domain (no `-Server`, as before): a user it does not find there is Undetermined, not Eligible. **Assumption,
   labelled:** the AD module reports a requested `memberOf` as a list, empty for a user in no group; if it reports
   none instead, such a user reads Undetermined - visible and fail-closed. Membership is still direct (`memberOf`), as
   before; nested membership is not this finding.

**Tests** - `MigrationStubExchange` gains an Active Directory stand-in (no directory): `StubMigrationService` (the
real service, its `CreateDirectoryRunspace` overridden) hands out a runspace of stub `Import-Module`, `Get-ADUser` and
`Get-ADGroup` over in-memory users (UPN to rows, each with its `memberOf`) and groups (identity to DN), through the
same failure table; `ExcludeGroups(...)` saves the module setting. New `ExchangeAdminWeb.Tests/MigrationExcludedGroupTests.cs`,
ToCloud over a mailbox not in Exchange Online (Eligible before the AD step):
- **the finding**: the check failing at each step (the module, `Get-ADUser` thrown and written, a group that does not
  resolve, no user, two users, no `memberOf`) - Undetermined, the reason "Could not be determined: the excluded-group
  check could not be completed: ...", no size step; an Ineligible verdict ("Already a cloud mailbox") whose check fails
  stays Ineligible with both reasons.
- **the match**: a member of the resolved group - Ineligible, "Member of excluded group: Sales", no size step; DNs
  that contain the group's name or DN as a substring ("CN=Sales Team,...", "CN=Other,OU=Sales,...") - not a member,
  the check passes and the size step runs (its own reason, the only one); a `memberOf` value differing only in case -
  a member; a DN typed into the comma-separated setting - "could not be resolved", Undetermined, where the substring
  matched every group in its domain.

**Mutations at the call site** (md5 before/mutated/after, failing tests named, restored, touched, rebuilt, green):
J1 (the record's) the warning-only catch again; J2 (the record's) `Contains` on the DN again. Extras: J1b no user
returns as the old code did (Eligible); J2b the configured text compared to `memberOf` without resolving it.

**Gates** as the brief; comments after the mutation round.

## Guard proof

Built as the Plan. Line numbers are at the fix commit's bytes, which carry no new code comments (they come in their own
commit after the last finding's round). `Services/ExchangeServiceBase.cs` is not changed: `CheckAdGroupMembership`
(703-748) has no caller now.

- `Services/MigrationService.cs`:
  - 174: `CheckExcludedGroups`, where `CheckAdGroupMembership` was called; the catch (176-181) logs, turns an Eligible
    verdict into `Undetermined` (an Ineligible one stands), and adds "Could not be determined: the excluded-group check
    could not be completed: <reason>" (181), the reason through `ResolveFailure`.
  - `CreateDirectoryRunspace` (250): `internal virtual`, the session the base helper built (`CreateDefault`, Bypass).
  - `CheckExcludedGroups` (257): `Import-Module ActiveDirectory`, `Get-ADUser -Filter "UserPrincipalName -eq '...'"
    -Properties memberOf`, then `Get-ADGroup -Identity` per configured value, each under Stop through `DirectoryStep`
    (305: the base's `Invoke`; a failure names its step); exactly one user (274); `memberOf` through
    `MemberOfDistinguishedNames` (317: absent - "reported no memberOf", not a list - "could not be read as a list of
    distinguished names"; a single string or any enumerable, wrappers unwrapped); exactly one group with a
    `DistinguishedName`; the match (296) the resolved DN in a `HashSet` under `StringComparer.OrdinalIgnoreCase` -
    never a substring.
- `ExchangeAdminWeb.Tests/MigrationStubExchange.cs`: `Service()` returns `StubMigrationService` (the real service, its
  `CreateDirectoryRunspace` the stand-in's); `DirectoryStandIn` - stub `Import-Module`, `Get-ADUser` (the UPN parsed from
  the filter) and `Get-ADGroup` (a group's DN, or the AD module's not-found under ObjectNotFound) over `DirectoryUsers`
  and `DirectoryGroups`, through the same failure table and call log; `DirectoryUser`, `Users`, `DirectoryNotFound`,
  `ExcludeGroups` (saves the module's comma-separated setting).

New `ExchangeAdminWeb.Tests/MigrationExcludedGroupTests.cs`, 7 tests (15 cases), each a ToCloud check of a mailbox not
yet in Exchange Online (Eligible before the AD step; one that passes the AD step reaches the size step, whose reason
alone shows it):

| Test | Pins |
| --- | --- |
| `AnExcludedGroupCheckThatCannotBeCompleted_IsUndetermined_NeverEligible` (the module, `Get-ADUser` thrown and written, a group that does not resolve, no user, two users, no `memberOf`) | **the finding**: Undetermined, exactly "Could not be determined: the excluded-group check could not be completed: <the step's reason>", and no size step |
| `AnIneligibleAnswer_WhoseGroupCheckCannotBeCompleted_StaysIneligible_WithBothReasons` | "Already a cloud mailbox" stands, the incomplete check listed after it |
| `AMemberOfTheResolvedGroup_IsIneligible` | "Member of excluded group: Sales", no size step |
| `AMemberOfValueThatDiffersOnlyInCase_IsTheGroup` | DNs compared ignoring case |
| `AGroupThatOnlyContainsTheNameOrTheDn_IsNotTheGroup` ("CN=Sales Team,...", "CN=Other,OU=Sales,...", "CN=Archive,CN=Sales,OU=Groups,...") | **the finding**: a substring of the name or of the whole DN is not the group - the check passes |
| `AUserInNoExcludedGroup_PassesTheCheck` | the control |
| `ADnTypedIntoTheCommaSeparatedSetting_CannotBeResolved_RatherThanMatchingEveryGroupNearIt` | the setting splits a DN at its commas; "the excluded group 'CN=Sales' could not be resolved: ...", Undetermined |

**Against the unfixed code: not runnable as a whole** - the seam and the stand-in are new, so each behaviour was put
back at its own call site instead, and each revert fails the tests that pin it.

Mutations at the call site, by the same driver and protocol as aud-a23n's (`round-a23j.ps1`, session scratchpad), over
the 1089 tests of the same filter. Service bytes (LF) before every mutation and after every restore:
`cb38d7ecf272c2b9313a623f547e27e8`.

| Mutation | md5 mutated | Failed (of 1089) | Restored, rerun |
| --- | --- | --- | --- |
| J1 (the record's): the warning-only catch again ("Warning: Could not verify AD group membership (...)", the verdict untouched) | `1189952f8748ad82ab4423645a855dae` | 9: every `AnExcludedGroupCheckThatCannotBeCompleted...` case (7), `AnIneligibleAnswer_WhoseGroupCheck...`, `ADnTypedIntoTheCommaSeparatedSetting...` - Eligible again | equal; 1089/0 |
| J2 (the record's): the substring again - the configured text inside each `memberOf` DN, as the base helper matched | `3c40c13dc7fecd3f6e72ee1d54453c6b` | 3: `AGroupThatOnlyContainsTheNameOrTheDn...` (all three) | equal; 1089/0 |
| J1b: no directory user returns, as the base helper's `memberOf == null` did | `524f27601f67325bb8394100aa9a96c6` | 1: `...CannotBeCompleted...` ("no directory user") | equal; 1089/0 |
| J2b: the configured text compared to `memberOf` exactly, without resolving it | `7805375131cbf671445b01d601fea991` | 2: `AMemberOfTheResolvedGroup...`, `AMemberOfValueThatDiffersOnlyInCase...` - a name never matches a DN | equal; 1089/0 |
| J2c: a substring of the resolved DN | `ceaf8b786b5c065821256b6322a4bc87` | 1: `AGroupThatOnlyContainsTheNameOrTheDn...` ("CN=Archive,CN=Sales,...") | equal; 1089/0 |

md5s (LF) of the other files at the fix commit: `MigrationExcludedGroupTests.cs` `85dba9c910c34a42a79a2659dc747147`,
`MigrationStubExchange.cs` `5d2b3e581749b505a69318b31a9038ea`.

On the fix commit's tree: Release build 0 errors, 61 warnings; the 1089 focused tests 1089/0; `dotnet format
--verify-no-changes` exit 0; `git diff --check HEAD` exit 0. No directory was asked anything: the AD session in every
test is the stand-in.
