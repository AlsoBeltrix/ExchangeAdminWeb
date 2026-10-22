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

Pending.
