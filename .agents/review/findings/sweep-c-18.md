# sweep-c-18: the self-grant refusal for a group names the group and how the operator is in it (sweep item 18)

**Severity**: MEDIUM - `.agents/review/live-risk-sweep-2026-10-30.md` item 18: a grant to any group the operator is in -
directly, nested or through their primary group (aud-a43, aud-a52) - was refused with "Access denied: You cannot grant
permissions to yourself.", worded as if they had named themselves and naming no group. The audit row carried the same text.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/sweep-c` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-c`, from master `72611fc2`)
**Commit**: see `git log master..fix/sweep-c` (`fix(sweep-c, item 18): ...`) | **Reviewer**: implementer (candidate)

## Evidence

- At `72611fc2`: `Services/PermissionValidator.cs` `CompareInDirectoryAsync` returned `SelfGrantRefusal` on any
  `membership.Matches`; `CompareInExchangeGroupAsync` returned it when Exchange listed the operator among a cloud-only group's
  members. The pages audit the validator's text as `errorDetail` (`MailboxPermissions.razor:446`, `CalendarPermissions.razor:416`,
  and the on-premises re-checks at `:715` / `:669`), so the audit row follows the message with no page change.

## Approach (as built)

- **The refusal is kept** (granting to a group you are in grants you; fail closed). The person-is-the-operator refusals keep
  `SelfGrantRefusal`.
- **Wording.** `PermissionValidator.SelfGrantGroupRefusal(group, through, throughPrimaryGroup)`:
  direct "You are a member of the group 'G', so a permission granted to that group is granted to you."; nested "... the group
  'G' through 'A', which is in 'B', ..."; primary "The group 'G' is your primary group, ..."; through the primary group
  "... through your primary group 'Domain Users', which is in 'All Staff', ..."; path unreadable "... (the groups that
  membership runs through could not be read), ...". The WRN log line carries the same text.
- **Path.** `ProtectedGroupMembership.TracePath(targetDn, groupDn, directory)`, run only after the evaluator matched, under the
  same directory-read credential and session: reads the target (primaryGroupID), its primary group by SID, then memberOf level
  by level at the target's domain and the global catalog (`ReadAncestry`'s reads), keeping where each group was reached from.
  Explanation only: a throw or no path changes the wording, never the answer. Cached per run (`SelfGrantRun.MembershipPathAsync`),
  so a bulk file's rows read it once.
- **Cloud-only groups.** `ExchangeGroupMember.Via` (init, default empty): `ExchangeGroupMembers.Read` records the nested cloud
  groups each member was reached through. A cloud match names the granted group and those groups innermost first; a synced group
  inside a cloud group is named with the directory path inside it, then itself, then the cloud groups outside it.
- **Help.** README Self-grant Prevention bullet. No page markup describes the refusal (grep), no page edited, no ClickGate move.
- **Versions.** Mailbox Permissions 1.6.9 -> 1.6.10, Calendar Permissions 1.4.9 -> 1.4.10, with notes. Base unchanged.

## Where the code goes beside the sweep's fix shape

- The sweep also asks "decide whether membership should refuse at all": not decided here - the brief keeps the refusal.
- A domain-local group of another domain holds its member links only in its own domain, where the operator's object has no
  memberOf back-link the trace can read; such a refusal names the group and says the path could not be read. Three of the
  existing nested-chain cases are of that shape and now pin that wording.
- One extra read set on a refusal only (no read on an allowed grant), so the near-instant requirement on the allowed path is
  unaffected.

## Guard proof

Each reverted, the named test failed, restored, the suite passed:

| Revert | Failing test |
|---|---|
| M1 directory match returns `SelfGrantRefusal` | `AGroupRefusal_NamesTheGroupAndHowTheOperatorIsInIt_NotAsIfTheyHadNamedThemselves` (4 cases), `AGroupRefusal_WhosePathCannotBeRead_StillRefuses_NamingTheGroup` |
| M2 primary group not traced | `AGroupRefusal_...` ("their primary group", "through their primary group") |
| M3 `Via` not carried | `ExchangeGroupMembersTests.EachMember_CarriesTheCloudGroupsItWasReachedThrough_OutermostFirst` |
| M4 cloud match returns `SelfGrantRefusal` | `ACloudOnlyGroupRefusal_NamesTheCloudGroupsExchangeReadThrough_InnermostFirst` |

## Gates

Run once for the branch at its tip: see the sweep-c report. Not run: any live directory or Exchange read.
