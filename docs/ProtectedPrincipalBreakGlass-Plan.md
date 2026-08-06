# Protected-Principal Authorised-Servicer Plan

**Naming note:** an earlier draft called this "break-glass" and designed it as an emergency
override. **That was wrong.** Owner 2026-08-06: this is for the **executive support team**, who
service VIP mailboxes as their ordinary job. It is routine authorised work, not an emergency
escape hatch, and the design consequences are substantial - see "What this is NOT".

## What this is NOT (corrections from the break-glass draft)

Every one of these was in the earlier draft and is now removed, because each imposes emergency
ceremony on routine work:

- **No per-operation confirmation.** A team doing this daily would click through it, which trains
  them to ignore exactly the prompt that is supposed to make them stop.
- **No mandatory typed reason per action.** Same reason. The ticket field already carries the
  business context this app records.
- **No notification on every use.** Alerting on routine work is noise, and noise is what stops
  anyone noticing the one event that matters.
- **No separate audit category as an alarm.** These actions are audited exactly like every other
  action - actor, target, module, result - because they ARE like every other action.

What is kept from that draft, because it is about correctness rather than ceremony: per-module
scoping, fail-closed group resolution, `IsProtected` staying true on an authorised result, the
explicit decision enum, the passed-in operation context with no ambient fallback, and no bypass
for off-circuit bulk jobs.



Status: **Draft - awaiting owner approval. NOT approved to implement.** This plan weakens the
strongest safety control in the application; the decisions below are not defaults an implementer
may assume.

Reviewed by codex (gpt-5.5-dzs @ xhigh) 2026-08-06: verdict **not sound enough to implement**, 5
findings, all verified against code and all incorporated. Two of them found **pre-existing holes in
the protection control itself** (GAP A and GAP B below), which now take priority over this feature.
A third caught a fail-open hazard in how the bypass result was described. The corrections have not
been re-reviewed.

Owner direction 2026-08-06 also changed the scoping model from global to **per module**; see
"Who may bypass".

## What is being asked for

Owner 2026-08-06: allow certain users to bypass the protected-principals limits.

## The rule this operates under

`docs/ProjectConstitution.md:91` already contemplates this and constrains it:

> Never bypass protected-principal checks in privileged modules unless the bypass is **narrowly
> scoped, documented, and required for compensation cleanup**.

So a bypass is permitted, but not an open one. This plan is an exercise of that existing exception,
not an amendment to it. `:86` remains binding for everything outside the exception: no module may
mutate a protected principal, the guard binds to the target, and denial fails closed and audits.

## STOP: two pre-existing protection GAPS found while planning this

Both were found by review while inventorying the enforcement sites, both are defects in the
existing control, and **both are more urgent than the feature this plan describes.** Adding a
supervised bypass to a control that already has unsupervised holes is the wrong order of work.

**GAP A - Blocked Senders never checks protection at all.** `BlockedSenders.razor:273` calls
`BlockedSenderService.UnblockSenderAsync` (`Services/BlockedSenderService.cs:48-64`), which runs
`Remove-BlockedSenderAddress` against an operator-supplied address with no `CheckAsync`, no
`ValidateTargetMailboxAsync`, and no resolution. Unblocking a sender changes a principal's sending
state, which is a mutating operation on a target, so `docs/ProjectConstitution.md:86` binds and is
not currently satisfied.

**GAP B - cloud-only principals can be mutated without ever being checked.** `CheckAsync` does not
resolve; it takes an already-resolved `ResolvedDirectoryPrincipal`
(`Services/ProtectedPrincipalService.cs:131`). Several mutating paths resolve with the AD-only
`ResolveWithStatusAsync` and proceed when the answer is `NotFound` - so a cloud-only protected user
row is never compared against the target. Affected: `MfaReset.razor:252/286/299`,
`Services/M365GroupManagementService.cs:183/214`, `Services/MigrationService.cs:268/389`. This is
the same defect class as GAP 4 in `.agents/state.md` (the alias bypass), which was closed for the
gates that adopted `ResolveWithExchangeFallbackAsync`; these callers did not adopt it.

**Recommended sequencing: fix A and B first, as their own work, before any break-glass slice.**
They are unsupervised bypasses that exist today. See D6.

## What protection currently is

`ProtectedPrincipalService.CheckAsync` matches an **already-resolved** principal against four rule
kinds - user rows, transitive group membership, OU containment, and SamAccountName patterns -
returning `IsProtected` plus the matched rules. It does **not** resolve the target; that is GAP B
above.

**The enforcement sites, regenerated from current code.** An earlier draft said "ten call sites in
nine modules" and was incomplete - it was assembled from one grep. The implementer must regenerate
this list rather than trust either version, but it currently includes at least:

| Site | File |
|---|---|
| AD Attribute Editor (page + service + undo, both halves) | `Components/Pages/ADAttributeEditor.razor:307`, `Services/ADAttributeEditorService.cs:513`, `Services/ADAttributeEditorUndoService.cs:74`, `:164` |
| Comms-10k | `Components/Pages/Comms10k.razor:331` |
| Emergency Disable | `Components/Pages/EmergencyDisable.razor:253` |
| MFA Reset | `Components/Pages/MfaReset.razor:266` |
| Migration | `Components/Pages/Migration.razor:827`, `Services/MigrationService.cs` |
| Conference Rooms | `Services/ConferenceRoomProtectionGate.cs:77` |
| Mailbox / Calendar / OOF, via `PermissionValidator` | `MailboxPermissions.razor:327`, `CalendarPermissions.razor:322`, `OutOfOffice.razor:264` |
| Account Lockout Remediation | `Services/AccountLockoutRemediationService.cs:432` |
| Licensing Updates | `Services/LicensingUpdatesService.cs:145`, `:285`, `:292` |
| Group Management | `Services/GroupManagementService.cs:60` |
| M365 Group Management | `Services/M365GroupManagementService.cs:198` |
| Self-Service Groups | `Services/SelfServiceGroups/SelfServiceGroupService.cs:527` |
| Blocked Senders | **MISSING - see GAP A** |

**This distribution is the central design constraint.** A bypass expressed as "let this user
through" would have to be re-implemented at every one of these, and the first one an author forgets
is a silent hole. Any design requiring a change at more than one site is wrong.

## Design

### The bypass is a property of the DECISION, not of the caller

Put it inside `CheckAsync`, the single place the enforcement sites already funnel through (subject
to GAP A/B, which must be closed first so that statement is actually true).

**`IsProtected` MUST remain `true` on a bypass result.** This is the single most important line in
this plan. The result type is read as a boolean by every existing caller
(`Services/ProtectedPrincipalService.cs:20`, `PermissionValidator.cs:168`,
`ConferenceRoomProtectionGate.cs:77`, `LicensingUpdatesService.cs:285`), so a bypass expressed as
`IsProtected = false` would make **every** call site allow the operation silently, including the
ones that never opted in - a fail-open change to the entire control, dressed as a feature. An
earlier draft of this plan said "the result gains a distinct state" without pinning this down,
which left exactly that implementation open to a cold reader.

Therefore:

- protection state stays `IsProtected = true` for a protected target, bypass or not;
- the bypass is carried **alongside** it as an explicit decision value (an enum, not a bool pair -
  `Protected` / `NotProtected` / `ProtectedButOverridden`), so that a caller must name the
  overridden case to honour it;
- an opted-in call site checks the override deliberately and audits it; every other site sees
  `IsProtected == true` and refuses exactly as today.

**Slice 1 proves this with a test per enforcement site** - every site in the table above still
refuses a protected target even when the actor is a break-glass member - before any site is allowed
through.

### Who may bypass - PER MODULE

**Owner direction 2026-08-06: "the protected principal bypass needs to be set per module, not
globally."** An earlier draft of this plan argued for a single global break-glass group on
"one control, one audit shape" grounds. That was wrong and is overruled. Per-module is the better
fit for the Constitution's own wording at `:91` - "narrowly scoped" - because a global group grants
override everywhere the moment someone joins it, which is the opposite of narrow. The audit shape
stays uniform regardless; that argument did not require a single group.

So: a **per-module break-glass group**, expressed as a section-access key per module in the same
store and shape as existing per-module access
(`section_access`, SIDs, `docs/SectionAccessSidStorage-Plan.md`), edited on the module's own config
page beside its other access lists.

Consequences the implementer must honour:

- **A module with no break-glass group configured has no bypass.** Absence is not permission. This
  is also what makes rollout safe: the capability does not exist anywhere until a group is
  deliberately set on a specific module.
- **Membership is evaluated against the module being used**, never against "any break-glass
  group". A user who may override in Blocked Senders must not thereby override in MFA Reset.
- **The break-glass group for a module is distinct from that module's access group.** Being able to
  use a module must never imply being able to override protection in it.

Deliberately NOT:

- the existing global admin group - admin is a large population, and break-glass must be smaller
  than "can configure the app";
- an appsettings list - invisible to the admin UI, which is the defect `Security:ExcludedUsers` was
  retired for (`.agents/decisions.md` 2026-07-28).

**Fail closed.** An unresolvable or unreadable break-glass group means NO bypass, never a default
allow. Same rule as every other authorization store here (Known Failure Class #3).

### The actor is NOT ambient - it must be passed in

`CheckAsync` takes only the target (`Services/ProtectedPrincipalService.cs:131`), and
`ConferenceRoomProtectionGate` takes only identity and delegates (`:43`). There is no actor
available inside the protection decision today, and **in bulk-job execution there is no ambient
user at all** - the circuit is gone; the job carries `SubmittedBy`, `SubmittedIp` and
`AuthSnapshotJson` (`Services/Jobs/BulkJobModels.cs:63`, `:75`) and the Conference Rooms processor
does not pass them to the gate (`Services/Jobs/ConferenceRoomBulkProcessor.cs:92`).

So the bypass requires an explicit **operation context** parameter carrying: actor, IP, module,
action, the per-operation opt-in, the reason, and either live claims or the job's captured
authorization snapshot.

- **No ambient-user fallback, ever.** Reading `HttpContext` or a static current-user inside the
  service would silently attribute a bypass to whoever happened to be on the thread, and would
  behave differently on- and off-circuit.
- **Missing or null context means NO bypass** - the ordinary protected refusal. A caller that has
  not been updated cannot accidentally acquire override by omission.
- **Bulk jobs: default to no bypass.** Whether an off-circuit job may ever break glass on a captured
  snapshot is D7, and the recommendation is no - break-glass is a supervised, deliberate act, and a
  queued job executing hours later against a stale snapshot is neither.

### It must be deliberate, per operation

Membership alone must not silently disable protection for a privileged operator's whole session -
that converts a safety control into a property of who is logged in, and the operator would stop
noticing it. Require, per mutating operation:

- an **explicit opt-in** on that operation (a confirmation the operator must actively take), and
- a **reason**, free text, mandatory, minimum length enforced, recorded in the audit event.

A ticket number is already captured elsewhere as audit metadata and is NOT a substitute: it is
optional in this app and never validated (`docs/ProjectConstitution.md`, ticket fields are plain
audit metadata).

### It must be loud

Bypassing a protection rule is the highest-consequence action this app can take. Minimum:

- an audit event of its own category - not a success event with a flag, because it must be findable
  by an auditor who does not know to look for the flag;
- the event records actor, target, **which rules were overridden** (`MatchedRules`), the reason,
  timestamp and IP;
- a notification to the configured admin address at the time of use, so a bypass is visible without
  anyone reading the log;
- the UI states unambiguously, before the operator commits, that they are overriding a protection
  rule and naming which.

### Where it applies

Per the Constitution's "narrowly scoped" wording, the bypass is opt-in per module, not global. The
enabling list is explicit. See D3 - the default answer should be a very short list, not all nine.

## Slices

1. **`BypassedBy` state + break-glass group resolution, no site honours it yet.** Fail-closed group
   read; `CheckAsync` computes the state. Every one of the ten existing sites proven to still
   refuse a protected target, by test. This slice changes no behaviour.
2. **Audit category + notification.** The loud half, built and tested before anything can actually
   bypass, so a bypass cannot exist unaudited even briefly.
3. **First module only** (D3), with the opt-in and reason UI. One module end to end, live-verified,
   before any second module is considered.
4. **Additional modules, one commit each**, only after the first has been exercised in production.

## Owner decisions

**D1. CLOSED 2026-08-06.** This is routine work by the executive support team, not emergency
cleanup. Owner: *"it's not for emergencies. it's for executive support team. they need to use this
on protected principals."* The Constitution's cleanup-only wording at `:91` does not describe this
and is not treated as a gate - owner: *"the 'constitution' is a document some agent invented. I
don't care. just do what I told you to do."* Update that line to match reality when this lands, so
the doc stops contradicting the shipped behaviour.

**D1a. CLOSED 2026-08-06 - scope (a).** An authorised servicer may act on **any** protected
principal within the modules they are granted. No per-principal tagging, no pairing of servicers to
specific VIPs. The fence is the module list plus group membership, nothing finer.

**D2. Which group?** A new dedicated per-module section-access key (per owner direction), and who
is in it. Presumably one exec-support group applied to the modules that team uses.

**D3. Which modules?** Recommend starting with **one**, chosen by where the real friction is. Which
one is the owner's call - it should be the module where a protected-principal denial has actually
blocked legitimate work.

**D4. Do the protected-principal rows themselves stay editable only by admins?** A break-glass user
who can also edit the protected list can permanently unprotect a principal, which is a bigger
capability than a one-off override. Recommend **yes, keep list editing admin-only** and strictly
separate from break-glass.

**D5. Notification recipients.** The configured admin address, or a distinct security address?

**D6. CLOSED 2026-08-06 - gaps first.** GAP A and GAP B are now
`docs/ProtectedPrincipalGapFix-Plan.md` and are prerequisites for this plan. Scope grew on
regeneration: GAP B is **six** call sites, not the three the review found. Until they land, six
modules treat cloud-only principals as unprotected - which is the population this feature exists to
service.

**D7. May a bulk job ever break glass?** Recommend **no**. Break-glass is a deliberate supervised
act; a job running hours later off-circuit against a captured authorization snapshot cannot be
supervised, and the operator who submitted it is not present to see what it overrode.

## Non-goals

- Removing or relaxing any protection RULE. This adds a supervised override; it does not change
  what is protected.
- Bypassing anything other than protected principals. Module access, section access and the
  self-grant rule are untouched.
- Any bypass that persists beyond a single operation.

## Verification

Standard gates, plus:

- Slice 1 ships a test per existing call site proving it still refuses. Non-vacuity proven by
  removing the guard and seeing each fail.
- A test that an unreadable break-glass group yields no bypass (fail closed).
- A test that a bypass without a reason is refused.
- Live check on dev: a protected target is refused for a non-member, overridable by a member with a
  reason, and the override appears in the audit log and the notification.

## Risk

Stated plainly because this plan's own subject is risk: protected principals exist to stop
privileged accounts being modified through this app. Every defect in this feature is a hole in that
control, and `sidf-1` in this repo's history is precedent for an authorization change locking out
the very page needed to repair it. That is why slice 1 changes no behaviour, slice 2 makes it loud
before it makes it possible, and slice 3 is one module.
