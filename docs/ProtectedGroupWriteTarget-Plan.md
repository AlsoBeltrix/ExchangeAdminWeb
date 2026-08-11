# Protected Groups As Write Targets

Status: Draft, pending owner ruling on OQ1 (whether the `Groups` protection list also
protects the group object itself as a write target). Depends on
`docs/GroupMemberNesting-Plan.md` S1 having landed.

## Problem

Every group module protection-checks the MEMBER being added or removed and never the
GROUP being written into. Protection therefore stops you touching a protected person and
does nothing to stop you granting an ordinary person protected access.

Owner-identified 2026-08-11 while reading the nesting plan.

### Four instances, one shape

1. **On-prem AD, membership.** `GroupManagementService.AddMemberAsync` (`:251-298`) and
   `RemoveMemberAsync` (`:302-351`) call `CheckProtectedAsync(member, actingUser)`
   (`:253`, `:304`) and never examine `groupIdentity`. The page delegates deliberately and
   pre-checks nothing (`Components/Pages/GroupManagement.razor:271-276`). **An operator
   with `GroupManagementOnPrem` can add any unprotected account to `Domain Admins`**, with
   `Domain Admins` listed as a protected group, and no gate fires.
2. **M365, membership and ownership.**
   `M365GroupManagementService.AddDirectoryObjectAsync` (`:253-272`) and
   `RemoveDirectoryObjectAsync` (`:274-288`) check only `identity`; `groupId` flows
   straight into the Graph call. Same hole, plus OWNERSHIP - adding an owner to a
   protected group hands over control of it.
3. **M365, the group object itself.** `UpdateGroupAsync` (`:125-141`) and
   `DeleteGroupAsync` (`:143-152`) have NO protection gate of any kind. **A protected M365
   group can be renamed or deleted outright.** The page gates only on a ticket number
   (`M365GroupManagement.razor:286`).
4. **Self-service.** `SelfServiceGroupService.ChangeMemberAsync` checks the member
   (`:397`) and gates the group on DACL ownership (`CallerCanManageMembers`), not on
   protection. Ownership is a real constraint, so this is the weakest of the four - but a
   protected group with a `managedBy` owner is editable by that owner with no protection
   check.

### Why the existing check cannot simply be pointed at the group

`ProtectedPrincipalService`'s group-membership rule is user-only and, separately, matches
only a target's MEMBERSHIP of a protected group, never the group itself. Both are fixed by
`docs/GroupMemberNesting-Plan.md` S1 (cmdlet change plus DN self-match). **That work is a
hard dependency: without it, every check this plan adds returns "not protected" for a
group and the whole change is inert while appearing to work.** Sequence S1 first, and prove
the dependency with a test that fails if S1 is reverted.

## Design

### T1. A shared target gate

`Services/ProtectedPrincipalServicing.cs` already owns the servicer half of every gate
(`NoteFor`, `Extra`). Add the target half beside it rather than four copies: a helper
taking a resolved target principal, a module id, and the acting principal, returning the
same `(Denial, ServicedNote)` shape the modules already consume. Four hand-written gates
is how two of them come to disagree about what "protected" means.

The invariants are the ones the servicer stream already established and must not be
re-litigated: protection is evaluated FIRST and never weakened; fail-closed outranks
servicing (unavailable / ambiguous / check-failed all deny); a null acting principal
refuses; the grant is per module; the note names both the authorising group and the rules
overridden; it travels in the audit event's `extra`, never `errorDetail`.

### T2. Call sites

- `GroupManagementService`: resolve the target group once - the module already resolves it
  for the write (`ResolveAdGroupIdentity`, `:368-391`) - and gate on that same resolution.
  One resolution feeding both the check and the write, so the object cleared is the object
  written.
- `M365GroupManagementService`: gate `AddDirectoryObjectAsync` and
  `RemoveDirectoryObjectAsync` on `groupId`, and give `UpdateGroupAsync` /
  `DeleteGroupAsync` a gate they currently lack entirely. Graph returns an object id, not
  a DN; the target principal carries `EntraObjectId` and the group's mail/displayName, and
  matching runs on those.
- `SelfServiceGroupService`: gate the target group in `ChangeMemberAsync` after the
  existing eligibility check. A self-service user will essentially never hold a servicer
  grant, so in practice this is a hard refusal pointing at the IT Support Desk - which is
  correct: a protected group is not a self-service object.

### T3. Removal is not exempt

Refuse membership REMOVAL from a protected group as well as addition. Removing the last
legitimate member of a protected group is as damaging as adding an illegitimate one, and
an attacker's first move against a protected group is often to remove someone, not add
themselves. The servicer override is the intended route for both.

## Non-goals

- No change to what the protected-principals config stores or how it is edited.
- No new module, no new permission, no change to section access.
- Conference Rooms, Mailbox/Calendar Permissions, and the other servicer-capable modules
  are out of scope: their write target is a mailbox and is already validated
  (`ValidateTargetMailboxAsync`). This plan is the GROUP modules only.
- No retro-fitting of a protection gate onto read paths.

## Acceptance criteria

- AC1: Adding a member to a protected on-prem group is refused; the refusal is audited.
- AC2: Removing a member from a protected on-prem group is refused (T3).
- AC3: An operator holding `ProtectedServicer:GroupManagement` may do both, and the audit
  event's `extra` names the authorising group and the rules overridden.
- AC4: The same three for M365 members AND owners, under
  `ProtectedServicer:M365GroupManagement`.
- AC5: Renaming or deleting a protected M365 group is refused, servicer-overridable,
  audited.
- AC6: A self-service owner is refused on a protected group they own, with the ITSD
  message.
- AC7: Every gate fails CLOSED - an unavailable, ambiguous, or errored check denies.
- AC8: Reverting `GroupMemberNesting-Plan.md` S1 makes at least one test here FAIL. The
  dependency is real, and a green suite over an inert gate is the failure mode this
  criterion exists to catch.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`, `dotnet test ExchangeAdminWeb.slnx`,
  `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`,
  `git diff --check HEAD`, ASCII check.
- Non-vacuity per gate: revert it, confirm the allow-path tests fail and the refusal tests
  still pass, restore. Confirm each revert landed on disk before trusting the verdict.
- **The existing protection suites must pass UNMODIFIED.** Editing one to accommodate a
  target gate means a refusal quietly became an allow; that is a stop.
- Manual, dev: add to a protected group and be refused; repeat as a servicer-group member
  and succeed with the note in the audit record; delete a protected M365 group and be
  refused.

## Versioning

- Base app `2.9.0` -> `2.10.0` (assumes the nesting plan landed first):
  `ProtectedPrincipalServicing` is shared.
- `GroupManagement` `2.3.0` -> `2.4.0`, `M365GroupManagement` `1.3.0` -> `1.4.0`,
  `SelfServiceGroups` `1.4.0` -> `1.5.0` (all assume the nesting plan's bumps landed).

## Open questions

- **OQ1 (owner ruling required before any code).** Should a group listed in the
  protected-principals `Groups` list be protected AS A WRITE TARGET, or only as a
  membership rule for the people inside it?
  **This is the whole risk of the plan.** That list's current meaning is "everyone inside
  this group is protected", and administrators may well have listed broad groups - a
  large staff group, a licensing group - purely to protect the people in them. Reading the
  same list as target protection would make every one of those groups unmanageable through
  the app overnight, and the symptom is an operator being refused a routine change with no
  obvious cause. `sidf-1` is the precedent: a change near this code locked every admin out
  of the page needed to repair it.
  Options: (a) any matching rule protects the target, simplest and strictest; (b) target
  protection uses only the direct-identity, OU and pattern rules, treating the `Groups`
  list as members-only; (c) a separate list for protected TARGETS, no reinterpretation of
  existing config, at the cost of another thing to configure.
  Whichever is chosen, the live config in both `config/exchangeadmin.db` files must be read
  and every group that would newly become unmanageable listed BEFORE deploy, not after.
- OQ2: whether a protected group should also be undeletable in on-prem AD. This app has no
  on-prem group delete, so the question is theoretical today; it is recorded so a future
  delete feature does not ship without it.
