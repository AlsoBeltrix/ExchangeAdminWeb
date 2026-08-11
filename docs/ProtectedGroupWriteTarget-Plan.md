# Protected On-Prem Groups As Write Targets

Status: Draft, pending owner go. No open question - the identity-model fork that was OQ1
is resolved in Design (T0) and the reasoning is recorded there for the owner to reverse if
they disagree. Depends on `docs/GroupMemberNesting-Plan.md` S1 having landed.

**Scope is ON-PREM ACTIVE DIRECTORY ONLY (owner, 2026-08-11: "we're not touching the cloud
groups module").** An earlier draft covered `M365GroupManagement` as well; that was scope I
added unasked, and it is removed. `M365GroupManagement` is not touched by this plan - not
its service, not its page, not its tests. What was found there while it was in scope is
recorded in `.agents/state.md` as an unscheduled gap, not carried here.

Reviewed as a plan by openreview `codex`
(`@azure-openai-eus2-global/gpt-5.5-dzs` @ xhigh, grade fallback) over
`2eedaa9..503c1a8`: verdict `acceptable_with_changes`, two findings, both admitted -
`pgwt-1` (HIGH), `pgwt-2` (MEDIUM), records in `.agents/review/findings/pgwt-*.md`.
`pgwt-2` applies unchanged. **`pgwt-1` was entirely about M365 and is moot under the
narrowed scope**, but the criterion it earned (AC9) is kept, because the new target list
needs an admin input of its own and a capability nobody can configure is the failure it
warned about, cloud or not.

## Problem

The group modules protection-check the MEMBER being added or removed and never the GROUP
being written into. Protection therefore stops you touching a protected person and does
nothing to stop you granting an ordinary person protected access.

Owner-identified 2026-08-11 while reading the nesting plan.

### Two instances in scope, one shape

1. **On-prem AD, membership.** `GroupManagementService.AddMemberAsync` (`:251-298`) and
   `RemoveMemberAsync` (`:302-351`) call `CheckProtectedAsync(member, actingUser)`
   (`:253`, `:304`) and never examine `groupIdentity`. The page delegates deliberately and
   pre-checks nothing (`Components/Pages/GroupManagement.razor:271-276`). **An operator
   with `GroupManagementOnPrem` can add any unprotected account to `Domain Admins`**, with
   `Domain Admins` listed as a protected group, and no gate fires. This is the case the
   owner raised and the reason this plan exists.
2. **Self-service.** `SelfServiceGroupService.ChangeMemberAsync` checks the member
   (`:397`) and gates the group on DACL ownership (`CallerCanManageMembers`), not on
   protection. Ownership is a real constraint, so this is the weaker of the two - but a
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

### T0. The target identity model: a separate Protected Targets list

**A new list, stored beside the existing four rule kinds, never reinterpreting them.**
Everything else in this plan depends on this choice, so the reasoning is recorded in full.

Three options were live. Reinterpreting the existing `Groups` list as target protection
(a) is the smallest change and the most dangerous: that list means "everyone inside this
group is protected", and an administrator may well have listed a large staff or licensing
group purely to protect its members. Re-reading it as target protection makes every such
group unmanageable through the app the moment the build deploys, and the symptom is an
operator refused a routine change with no visible cause - the `sidf-1` shape, where a
change near this code locked every admin out of the page needed to repair it. Restricting
target protection to the direct/OU/pattern rules (b) avoids that but leaves no way to say
"this group is protected" other than listing it as a direct identity, in a field the UI
labels and validates as a USER (`AdminSettings.razor:144`).

(c), the separate list, is chosen. It is the only option under which nothing already
stored changes meaning, which is what makes this deployable without a config audit. The
new list:

- stores an on-prem group's immutable `objectGUID` plus a display label, so a rename or a
  move cannot silently un-protect it;
- is matched only when the question is "may this object be WRITTEN TO", never when the
  question is "is this principal protected" - the two are separate rule sets and must not
  bleed;
- gets its own admin input on the protected-principals panel, an
  `ADIdentityAutocomplete ObjectKind="Group"` beside the existing four, captioned to say
  it protects the GROUP rather than its members. The existing Groups input keeps its
  current caption and meaning; two lists that both say "Protected Groups" would be worse
  than the gap this closes;
- joins the existing read-modify-write save path rather than adding a second one.
  `SaveSectionAccess` -> `SaveAll` -> `ClearAndInsert` REPLACES the whole store; a second
  save path silently destroys authorization state, whose only symptom is a team quietly
  losing access.

### T1. A shared target gate

`Services/ProtectedPrincipalServicing.cs` already owns the servicer half of every gate
(`NoteFor`, `Extra`). Add the target half beside it rather than a copy per call site: a
helper taking a resolved target principal, a module id, and the acting principal, returning
the same `(Denial, ServicedNote)` shape the modules already consume. Two hand-written gates
is how they come to disagree about what "protected" means.

The invariants are the ones the servicer stream already established and must not be
re-litigated: protection is evaluated FIRST and never weakened; fail-closed outranks
servicing (unavailable / ambiguous / check-failed all deny); a null acting principal
refuses; the grant is per module; the note names both the authorising group and the rules
overridden; it travels in the audit event's `extra`, never `errorDetail`.

### T2. Call sites

- `GroupManagementService`: resolve the target group once and gate on that resolution, so
  the object cleared is the object written.
  **`ResolveAdGroupIdentity` (`:368-391`) is NOT sufficient for the gate (pgwt-2).** It
  returns a bare DN string (`:385-386`), and a principal carrying only a DN reaches just
  two of the four rules: `CheckOuMatches` and the `Groups` in-chain rule both key on DN,
  `MatchesIdentity` (`:581-598`) loses its `SamAccountName` / `ObjectGuid` /
  `PrimarySmtpAddress` comparisons, and `CheckPatternMatches` (`:610-620`) **returns at its
  first line** when `SamAccountName` is empty - so every pattern rule is skipped in
  silence. Resolve into a full snapshot instead: `DistinguishedName`, `SamAccountName`,
  `ObjectGuid`, `mail`, `Name`. The DN from that snapshot is what the write uses.
  A DN is enough to WRITE to a group and is not enough to ASK whether it is protected.
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

- **`M365GroupManagement` is not touched.** Owner ruling, 2026-08-11.
- No change to the MEANING of the four existing rule kinds (`Users`, `Groups`,
  `OrganizationalUnits`, `SamAccountNamePatterns`). T0 adds a list beside them; it
  reinterprets none of them, and nothing already stored changes behaviour.
- No new module, no new permission, no change to section access.
- Conference Rooms, Mailbox/Calendar Permissions, and the other servicer-capable modules
  are out of scope: their write target is a mailbox and is already validated
  (`ValidateTargetMailboxAsync`).
- No retro-fitting of a protection gate onto read paths.

## Acceptance criteria

- AC1: Adding a member to a protected on-prem group is refused; the refusal is audited.
- AC2: Removing a member from a protected on-prem group is refused (T3).
- AC3: An operator holding `ProtectedServicer:GroupManagement` may do both, and the audit
  event's `extra` names the authorising group and the rules overridden.
- AC4: A self-service owner is refused on a protected group they own, with the ITSD
  message.
- AC5: Every gate fails CLOSED - an unavailable, ambiguous, or errored check denies.
- AC6: Reverting `GroupMemberNesting-Plan.md` S1 makes at least one test here FAIL. The
  dependency is real, and a green suite over an inert gate is the failure mode this
  criterion exists to catch.
- AC7: An operator can mark a group as a protected TARGET from the admin page and see it
  take effect. Proven through the stored representation the UI produces, never a
  hand-built fixture - a fixture that bypasses the store is what hides a capability nobody
  can configure (the `ProtectedPrincipalBreakGlass` failure, and the surviving half of
  pgwt-1).
- AC8: Nothing already in the protected-principals store changes behaviour. A group listed
  under `Groups` today is still manageable tomorrow unless it is ALSO added to the new
  target list. This is the anti-lockout criterion and is load-bearing.
- AC9: A target protected by a sAMAccountName PATTERN is refused, not only one matched by
  the target list or an OU (pgwt-2). Asserted separately, because a DN-only principal
  satisfies the DN rules and silently skips the pattern rule.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`, `dotnet test ExchangeAdminWeb.slnx`,
  `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`,
  `git diff --check HEAD`, ASCII check.
- Non-vacuity per gate: revert it, confirm the allow-path tests fail and the refusal tests
  still pass, restore. Confirm each revert landed on disk before trusting the verdict.
- **The existing protection suites must pass UNMODIFIED.** Editing one to accommodate a
  target gate means a refusal quietly became an allow; that is a stop.
- Coverage must include a target matched by PATTERN and a target matched by OU, not only
  one matched by the new target list (pgwt-2). Three rules need identifiers a DN does not
  carry, and the DN path is the one that passes by accident.
- At least one test drives the admin save path and reads the value back through the store,
  rather than constructing a principal directly.
- `git diff` the M365 module before commit and confirm it is untouched. The owner ruled it
  out of scope after it had been in an earlier draft, which is exactly the condition under
  which a stray edit survives.
- Manual, dev: add to a protected group and be refused; repeat as a servicer-group member
  and succeed with the note in the audit record.

## Versioning

- Base app `2.9.0` -> `2.10.0` (assumes the nesting plan landed first):
  `ProtectedPrincipalServicing` and the protected-principals store are shared.
- `GroupManagement` `2.3.0` -> `2.4.0`, `SelfServiceGroups` `1.4.0` -> `1.5.0` (both assume
  the nesting plan's bumps landed). `M365GroupManagement` is NOT bumped - it is not
  changed.

## Open questions

- OQ1 is resolved in T0 (separate Protected Targets list). Recorded as a design choice
  with its reasoning rather than left open, because the reinterpretation options carry a
  lockout risk this plan should not hand to an implementer as a coin-flip. The owner can
  reverse it; if they do, the live config in both `config/exchangeadmin.db` files must be
  read and every group that would newly become unmanageable listed BEFORE deploy.
- OQ2: whether a protected group should also be undeletable in on-prem AD. This app has no
  on-prem group delete, so the question is theoretical today; it is recorded so a future
  delete feature does not ship without it.
