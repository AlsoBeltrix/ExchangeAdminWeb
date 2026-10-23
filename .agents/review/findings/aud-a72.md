# aud-a72: A dynamic distribution group is classified as a group but has no membership read, so every grant to one is refused as incomplete

**Severity**: MEDIUM - fail-closed, but a legitimate grantee type is unusable under self-grant prevention.
`ExchangeRecipientKinds` (aud-a60) names `DynamicDistributionGroup` as a group; `ExchangeGroupMembers` has
no dynamic-group expansion path, so the membership read throws and `PermissionValidator` refuses the grant
as incomplete. Exchange Online exposes a dynamic group's current membership through
`Get-DynamicDistributionGroupMember` (and the filter preview), so the operator's membership can be
compared without evaluating the filter in the app.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (MEDIUM); folded into the aud-a69 agent's scope; fixed on the branch below, not merged; closed on guard proof; codex review pending
**Branch**: `worktree-agent-ac701914283a170ea` (from master `15eb1449`)
**Commit**: plan `8391a40a`; fix `6a9f3fe7`; one sentence in the comments commit `a9ce01f5`
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (perm-batch-7, 2026-10-23, candidate 2)

## Evidence

At `105a129f`: `Services/IIdentityResolver.cs` around 51 (`ExchangeRecipientKinds`);
`Services/ExchangeGroupMembers.cs` around 120 (the kinds it expands; the throw for any other kind);
`Services/PermissionValidator.cs` around 906 (the incomplete refusal).

## Approach (to be written by the implementing agent before code)

- `ExchangeGroupMembers.Read` expands a `DynamicDistributionGroup` through
  `Get-DynamicDistributionGroupMember -Identity <address> -ResultSize Unlimited` under the same
  pooled-read path and bound; its members compare like a distribution group's (nested groups within it
  follow the same rules). A read that fails or returns nothing at all is still incomplete (refuse); a
  membership that answers "not a member" allows. Nothing evaluates the filter in the app and nothing names
  the environment.
- Tests through `ExchangeGroupMembersTests` and `PermissionValidatorSelfGrantTests` with a dynamic-group
  stub (operator a member; not a member; the read fails). Mutation at the call site: the dynamic kind back
  to the throw.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 (`.agents/decisions.md` 2026-10-21), within the 2026-10-23 stopping rule (MEDIUM). Folded into the
aud-a69 agent by the coordinator on 2026-10-23, the record taken from master `3a65ad62`. Branch
`worktree-agent-ac701914283a170ea` from master `15eb1449`: this record alone, then one fix commit
`fix(exchange-group-members): ... (aud-a72)`, third of three (after aud-a69's `828fc33a`); comments, if any, in one
comments-only commit after the three mutation rounds. Behaviour: the shared `ExchangeGroupMembers` (base app version), and so
what a Mailbox Permissions or Calendar Permissions grant to a dynamic distribution group is refused or allowed on (both module
versions); the bumps are the supervisor's.

Evidence re-read at `828fc33a` (the file as at `15eb1449`):
- `ExchangeRecipientKinds.NamesAGroup` (`Services/IIdentityResolver.cs`) reads every kind carrying "Group" as a group,
  `DynamicDistributionGroup` among them (aud-a60; `ExchangeGroupMembersTests.ARecipientIsAGroup_WhenEitherKindExchangeGaveNamesOne`
  pins it).
- `ExchangeGroupMembers.MembersOf` (109-134) reads `GroupMailbox` with `Get-UnifiedGroupLinks` and the four
  `DistributionGroupKinds` with `Get-DistributionGroupMember`, and throws for every other kind (127-131), so `Read` (52-106)
  throws for a dynamic group at the root or nested in a cloud group.
- `PermissionValidator.CompareInExchangeGroupAsync` turns the throw into "whether you are a member of the group '<x>' could
  not be read": every grant to a cloud-only dynamic group, and to a cloud group nesting one, is refused, though Exchange can
  answer for its members.

1. **A dynamic distribution group is read.** `MembersOf` reads a `DynamicDistributionGroup` with
   `Get-DynamicDistributionGroupMember -Identity <address> -ResultSize Unlimited -ErrorAction Stop`, through the same pooled
   `invoke` (the pool's: an error throws, the pipeline is cleared) - the root and a nested one alike. Its rows are members as a
   distribution group's are (`ToMember`), and a group among them follows the same rules: a cloud-only one read once by its
   address and counted against `MaxNestedGroups`, a synced one listed for the directory, a group without an address or a sync
   state a throw. Nothing evaluates the group's filter in the app: the members are the ones Exchange calculated.
2. **A read that fails, or answers no member, refuses.** A failed read throws as every other does. A dynamic group Exchange
   reports no member of throws as well: its membership is calculated from a filter, not linked, and an empty answer cannot be
   told from one not yet calculated - so the check refuses as incomplete, never "not a member". A membership that answers
   without the operator allows.

Labelled assumptions (Exchange Online documentation, not probed; no test makes a live call):
`Get-DynamicDistributionGroupMember` takes `-ResultSize Unlimited` and lists the members Exchange last calculated for the
group's filter, each row with the properties `Get-DistributionGroupMember` gives (`PrimarySmtpAddress`,
`ExternalDirectoryObjectId`, `RecipientType`, `RecipientTypeDetails`, `IsDirSynced`). That membership is as current as
Exchange's last calculation of it.

Tests. `ExchangeGroupMembersTests`, over a stub `Get-DynamicDistributionGroupMember` (an advanced function: an unexpected
parameter is a binding error):
- a dynamic group's members read through it, with `-ResultSize Unlimited` and no other member cmdlet;
- a cloud group nested in a dynamic group, and a dynamic group nested in a distribution group, each read in turn;
- the failure theory: its "a dynamic group" row - which threw as a kind no cmdlet reads - becomes "a group of a kind no cmdlet
  reads" (`UniversalSecurityGroup`), and it gains "a dynamic group that reports no member" and "a dynamic group whose read
  fails";
- the resolver reads a dynamic group's members through the harness's pool.
`ExchangeWriteHarness`: a stand-in `Get-DynamicDistributionGroupMember` reporting `ListMembers`, as its
`Get-DistributionGroupMember` does. `PermissionValidatorSelfGrantTests`, the group's members read by the real resolver through
the harness's pool and stand-ins - the reader itself, not a stub of it: a dynamic group holding the operator, refused as a
self-grant; not holding them, allowed; its read failing, and its read answering no member, each refused as incomplete.

Mutation at the call site (the protocol aud-a71 records): M1 the dynamic kind back to the throw (`MembersOf` without its dynamic
branch). Extras: E1 an empty dynamic membership taken as no member (its refusal removed); E2 the dynamic read without
`-ResultSize Unlimited`.

Files: `Services/ExchangeGroupMembers.cs`, `ExchangeAdminWeb.Tests/ExchangeGroupMembersTests.cs`,
`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`, `ExchangeAdminWeb.Tests/PermissionValidatorSelfGrantTests.cs`, this record.

Caller-visible change, by design: a grant to a dynamic distribution group only Exchange Online holds, or to a cloud group nesting
one, is checked against the members Exchange calculated for it - refused as a self-grant when they include the operator, allowed
when they do not, and refused as incomplete when they cannot be read or Exchange reports none.

## Fix

- `Services/ExchangeGroupMembers.cs` `MembersOf`: a `DynamicDistributionGroup` (new `DynamicGroupKind`) is read with
  `Get-DynamicDistributionGroupMember -Identity <identity> -ResultSize Unlimited -ErrorAction Stop` through the pooled
  `invoke`; an answer of no row throws ("Exchange Online reports no member of the dynamic distribution group '<x>', so whether
  its membership was calculated cannot be told"). Its rows go through `Read`'s loop as every group's do. The class remarks name
  the dynamic read and its empty rule.
- `Services/IIdentityResolver.cs`: `ReadGroupMembersAsync`'s exception remarks name the empty dynamic membership (comment
  only).

Tests: `ExchangeGroupMembersTests` +5 - the stub `Get-DynamicDistributionGroupMember`;
`TheMembersOfADynamicDistributionGroup_AreTheOnesExchangeCalculated` (its members; the two calls, `-ResultSize Unlimited`, no
other member cmdlet), `ACloudGroupInADynamicGroup_AndADynamicGroupInACloudGroup_AreEachReadInTurn` (both nestings, every call in
order), `TheResolver_ReadsADynamicGroupsMembers_ThroughThePool` (the harness's pool: its stand-in, `-ResultSize Unlimited`,
one connection); `AMembershipThatCannotBeReadWhole_Throws` - its "a dynamic group" row (aud-a52's: a kind no cmdlet read)
is now "a group of a kind no cmdlet reads" (`UniversalSecurityGroup`), with two new rows, "a dynamic group that reports no
member" and "a dynamic group whose read fails". `ExchangeWriteHarness`: the stand-in `Get-DynamicDistributionGroupMember`
(`ListMembers`, an error without `-ResultSize`). `PermissionValidatorSelfGrantTests` +4, the group's members read by the
real `ExchangeIdentityResolver` through the harness's pool (`ExchangeHoldsADynamicGroup`):
`ADynamicDistributionGroup_IsCheckedAgainstTheMembersExchangeCalculated` (the operator among the members: refused; not: allowed;
one read, `-ResultSize Unlimited`), `ADynamicDistributionGroupWhoseMembersCannotBeRead_IsRefused_AsIncomplete` (the read
fails; no member reported).

## Guard proof

The aud-a71 driver and protocol (that record's Guard proof; log `a72-mutations.log`). Filter
`FullyQualifiedName~PermissionValidator|FullyQualifiedName~ExchangeGroupMembers|FullyQualifiedName~PermissionPagesSelfGrant|FullyQualifiedName~ProtectedGroupMembership|FullyQualifiedName~MailboxPermission|FullyQualifiedName~CalendarPermission|FullyQualifiedName~ExchangeIdentityResolver|FullyQualifiedName~BulkPermission|FullyQualifiedName~BulkAndOnPrem|FullyQualifiedName~ExchangeWriteHarness`
(476 tests). Fixed-file MD5: `Services/ExchangeGroupMembers.cs` `C979F088900D515F674B4F394C5B3C02` (unmutated:
`Services/IIdentityResolver.cs` `234D2095774F7A9B53CB34A014919476`, `ExchangeAdminWeb.Tests/ExchangeGroupMembersTests.cs`
`EF76E521ED69DCDE8B5C019DAE9EAAA9`, `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs` `1CC6709EB13BEBF98D7F7207C669CB6F`,
`ExchangeAdminWeb.Tests/PermissionValidatorSelfGrantTests.cs` `2985DDD2D010A083A4CAD0F30EF8BE3A`). Every mutant built with 0
errors and every restore matched. After the round, the restored tree rebuilt and the filter: 476/476 green.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 | the dynamic kind back to the throw (`MembersOf`'s dynamic branch never taken) | `ExchangeGroupMembers.cs` `6337FDA731EEDEBFD345015D01B3BA7D` | 7 of 476: both `ADynamicDistributionGroup_IsCheckedAgainstTheMembersExchangeCalculated` rows (refused as incomplete), both `ADynamicDistributionGroupWhoseMembersCannotBeRead_IsRefused_AsIncomplete` rows (no dynamic read made), `TheMembersOfADynamicDistributionGroup_AreTheOnesExchangeCalculated`, `ACloudGroupInADynamicGroup_AndADynamicGroupInACloudGroup_AreEachReadInTurn`, `TheResolver_ReadsADynamicGroupsMembers_ThroughThePool` |
| E1 | extra: an empty dynamic membership taken as no member | `D306FB3585E9470D13C105334F366914` | 2 of 476: `AMembershipThatCannotBeReadWhole_Throws(shape: "a dynamic group that reports no member")`, `ADynamicDistributionGroupWhoseMembersCannotBeRead_IsRefused_AsIncomplete(shape: "no member is reported")` - allowed |
| E2 | extra: the dynamic read without `-ResultSize Unlimited` | `13E6D4403EB678ECD25C12C0F830286C` | 5 of 476: both `ADynamicDistributionGroup_IsCheckedAgainstTheMembersExchangeCalculated` rows, `TheMembersOfADynamicDistributionGroup_AreTheOnesExchangeCalculated`, `ACloudGroupInADynamicGroup_AndADynamicGroupInACloudGroup_AreEachReadInTurn`, `TheResolver_ReadsADynamicGroupsMembers_ThroughThePool` |

No separate pre-fix reproduction: M1 is the reader as it stood, at its call site.

## Known gaps

- Labelled assumptions, as the plan records them: the cmdlet, its `-ResultSize` and its rows as Exchange Online documents
  them. Nothing ran against a tenant.
- The members are the ones Exchange last calculated for the filter. A recipient the filter matches now and Exchange's last
  calculation did not is not among them until Exchange calculates again; the check does not evaluate the filter (the
  record's approach). A candidate in the hand-back (LOW).
- Whether Exchange accepts a dynamic distribution group as the trustee of a mailbox or folder permission at all is not
  verified here; the check now answers for one instead of refusing it, and the write answers for itself.

## Comments after the round

The branch's comments commit `a9ce01f5` adds one sentence for this record to `PermissionValidator.CompareInExchangeGroupAsync`'s
remarks (a dynamic group's members are the ones Exchange calculated); aud-a69's record has the comment-only check of that commit.
`Services/IIdentityResolver.cs`'s remark was written with the fix (comment only; a file outside the plan's list, named here).

## Gates - 2026-10-23 (branch tip `a9ce01f5`: the three fixes and the comments)

As aud-a71's record gives them: the Release build 0 errors and the 19 baseline warnings, none in a touched file; `dotnet test`
6,569 passed / 0 skipped / 0 failed, nothing hung; format, `git diff --check HEAD` and the ASCII lint exit 0; the pages 808
and 762 lines. No AD, Exchange Online, Graph, DHCP or IIS call. No coder self-review verdict is claimed.

## Closeout

Fixed on branch `worktree-agent-ac701914283a170ea`, not merged, not pushed. Due at merge: the base app version - the shared
`ExchangeGroupMembers` - and the Mailbox Permissions and Calendar Permissions module versions: what a grant to a dynamic
distribution group, or to a cloud group nesting one, is refused or allowed on.
