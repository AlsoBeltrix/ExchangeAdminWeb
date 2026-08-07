# Protected-principal servicing across every module

Status: **Draft, revised after review, awaiting owner approval.**

Reviewed by grok (`grok-4.5-build`): **5 findings, 3 HIGH, all verified against the code and all
accepted.** Two of the first draft's factual claims were wrong, and one HIGH finding would have
stalled three slices on the first day. The revisions are folded in below; what changed:

- **F1 (HIGH)** - only `LogModuleAction` accepts `extra`. Nine other audit methods do not, so on
  most modules there was **no channel** for the authorising group. Verified by inspecting every
  `Log*` signature. New slice 0 below.
- **F2 (HIGH)** - **Out of Office** is a third `PermissionValidator` consumer and was missing from
  the surface entirely. Worse than an omission: threading it with a borrowed `moduleId` would make
  a Mailbox Permissions grant authorise OOF changes, which is a per-module scoping leak.
- **F3 (HIGH)** - `PermissionValidator` and `M365GroupManagementService` are **singletons**
  (`Program.cs:181`, `:127`) and `ProtectedPrincipalServicerService` is **scoped** (`:183`). Those
  slices could not have been built as written; the likely workarounds are exactly the ambient /
  nullable-default shapes D1 forbids.
- **F4 (MEDIUM)** - Licensing Updates was misclassified as an off-circuit job. There is no
  `IBulkJobProcessor` for it; `ApplyCsvAsync` runs **on the circuit**. Only Conference Rooms has a
  real job path.
- **F5 (LOW)** - Account Lockout **already** carries `ClaimsPrincipal` in
  `AccountLockoutOperatorContext`, so the "ten services, zero principals" claim was overstated.

Owner, 2026-08-07: *"all of them. every place where a principal is protected we need to allow a
priv group to act on them anyway."*

## What exists and what does not

Servicing works in **Blocked Senders** only. `ProtectedPrincipalServicerService.Evaluate` is sound
and stays unchanged; `BlockedSenderProtectionGate` is the worked example to copy.

Every other module refuses a protected principal with no way to authorise anyone, and the editor
deliberately does not appear for them (`ModulesWithProtectedPrincipalServicing`), because a grant
that does nothing is worse than no grant.

## The actual surface

**19 enforcement points across 15 files.** Measured, not estimated:

| Where | Sites | Shape |
| --- | --- | --- |
| `BlockedSenderProtectionGate` | 2 | done |
| `ConferenceRoomProtectionGate` | 1 | dedicated gate - copy Blocked Senders |
| `MfaReset.razor` | 2 | inline in page, has `authState` |
| `EmergencyDisable.razor` | 1 | inline in page |
| `Comms10k.razor` | 1 | inline in page |
| `ADAttributeEditor.razor` | 1 | inline in page |
| `ADAttributeEditorService` / `UndoService` | 3 | in service |
| `EmergencyDisableService` | 1 | in service |
| `GroupManagementService` | 1 | in service |
| `M365GroupManagementService` | 1 | in service |
| `MigrationService` | 1 | in service |
| `LicensingUpdatesService` | 1 | in service, per CSV row |
| `AccountLockoutRemediationService` | 1 | in service |
| `PermissionValidator` | 1 | Mailbox + Calendar + **Out of Office** (three modules, one site) |
| `SelfServiceGroupService` | 1 | in service |

## Correction to the surface (F2, F5)

**Out of Office is a THIRD `PermissionValidator` consumer** and was missing above.
`Components/Pages/OutOfOffice.razor:264` calls `ValidateTargetMailboxAsync` and audits
`OutOfOffice_Denied`. It must be threaded with its OWN module id - borrowing `MailboxPermissions`
would let a Mailbox grant authorise OOF changes to protected principals, collapsing the per-module
boundary this design rests on. `OutOfOffice` joins the opt-in list in the same commit.

So `PermissionValidator` serves **three** modules, not two, and one change there needs three module
ids threaded correctly rather than one.

**Account Lockout Remediation already has the acting principal.**
`AccountLockoutOperatorContext.Principal` (`Models/AccountLockoutRemediation/AccountLockoutModels.cs:62`)
is already passed and already used for `AuthorizeAsync`. That slice calls
`Evaluate(context.Principal, ModuleId)` with **no new threading** - do not add a second parameter,
and do not ignore the context in favour of one.

## The blocker, and it shapes everything

**Nine of the ten services cannot see who is acting** (Account Lockout excepted, above).
`ClaimsPrincipal` appears zero times in each. `Evaluate(user, moduleId)` needs a principal, so each
must be given one before it can consult the servicer decision.

This is the whole cost of the work. Threading an identity into ten services that deliberately have
none is not mechanical, and it is exactly where a mistake becomes a silent authorization hole:

- A service that **defaults** the principal when none is supplied fails OPEN.
- A service that reads an **ambient** principal (thread/async-local) attributes a bypass to whoever
  happens to be on the thread - which in a bulk job is nobody, or the wrong person.

**Bulk jobs have no operator at all.** The circuit is gone; a job carries `SubmittedBy`,
`SubmittedIp` and `AuthSnapshotJson` (`Services/Jobs/BulkJobModels.cs`). The existing plan already
ruled: **no bypass for off-circuit bulk-job execution.** That ruling stands here and is not
reopened - a job hitting a protected principal still refuses, whoever submitted it.

**Exactly one module is affected: Conference Rooms.** `ConferenceRoomBulkProcessor` is the only
`IBulkJobProcessor` that performs a protected-principal check (the other two processors are
Message Trace detail export, which touches no principal).

**Licensing Updates is NOT a job path** - corrected from the first draft (F4). There is no
`IBulkJobProcessor` for it; `LicensingUpdatesService.ApplyCsvAsync` runs **on the circuit** from
`LicensingUpdates.razor:320` with the live session. It is a bulk CSV operation, not a bulk job, and
it services like any other interactive path. Treating it as off-circuit would have left the module
permanently unable to service, or invited a synthetic principal built from an operator STRING -
which cannot satisfy `IsInRole` and is the wrong identity model outright.

## Decisions

**D1 - How does the principal reach a service?** An explicit parameter on the operation, never an
ambient lookup and never a nullable-defaulting one. Where a service method already takes an
operation context, extend it; otherwise add a required parameter so every caller is forced to
supply one and the compiler finds them all. Recommended: a missing argument becomes a build error
rather than a runtime fail-open.

**D2 - What does a service do when the principal is absent (bulk job)?** Refuse, exactly as today.
A null principal is not "unknown, allow" - `Evaluate` already denies a null user, and this plan
keeps that as the only behaviour for off-circuit work.

**D1a - DI lifetimes, and this one blocks three slices (F3).** `PermissionValidator`
(`Program.cs:181`) and `M365GroupManagementService` (`:127`) are **singletons**;
`ProtectedPrincipalServicerService` is **scoped** (`:183`). A singleton cannot inject a scoped
service - DI validation refuses it, and forcing it creates a captive dependency that outlives its
scope.

**Resolution: register `ProtectedPrincipalServicerService` as a singleton.** It is stateless and its
only dependencies are `SectionAccessService` (already a singleton) and a logger, so the lifetime is
wrong today rather than load-bearing. This is a one-line change and it unblocks Mailbox, Calendar,
Out of Office and M365 Groups at once.

Rejected alternatives, recorded so they are not revisited: moving `PermissionValidator` to scoped
(it is on the hot authorization path and its lifetime was chosen deliberately); resolving through
`IServiceScopeFactory` per call (adds a scope to an authorization check for no benefit, and the
pattern already caused a null-factory fail-closed branch elsewhere). **`BlockedSenderProtectionGate`
stays scoped and keeps working** - a scoped consumer of a singleton is always legal.

The verification for this slice is the one that matters: the app must **start**. A captive
dependency surfaces at startup validation, not at compile time.

**D3 - One servicer group for all modules, or one per module?** **Per module**, unchanged. Owner
ruled this 2026-08-06 and nothing here reopens it. The owner may configure the SAME group in every
module, which achieves "one group everywhere" as a configuration choice rather than by removing the
boundary. That distinction matters: it keeps the audit answerable and lets one module be revoked
without touching the rest.

**D4 - Order of work.** Two enabling slices first - neither is optional, and both were missing from
the first draft:

0a. **Audit channel (F1).** Only `LogModuleAction` accepts `extra`. Nine other methods do not:
    `LogMailboxPermission`, `LogCalendarPermission`, `LogMfaResetAction`, `LogMigrationBatch`,
    `LogMigrationAction`, `LogMigrationCheck`, `LogADAttributeEdit`, `LogLookupAction`,
    `LogSettingsChange` - verified by reading every signature. Without a channel, an implementer
    either drops the authorising group or passes it as `errorDetail`, where it is **discarded on
    success** (`AuditService.cs:177`) - which is exactly the blr-era defect, on the only records
    that would ever explain a VIP mailbox change. Add `extra` to the methods the servicing modules
    use, written regardless of outcome, before any module slice lands.

0b. **DI lifetime (D1a / F3).** Make the servicer service a singleton. Verify the app starts.

Then one module per commit:

1. Conference Rooms (a gate already exists; proves the pattern beyond Blocked Senders, and is the
   only module with a genuine job path to keep refusing)
2. MFA Reset, Emergency Disable, Comms-10k, AD Attribute Editor (inline pages, principal at hand)
3. Mailbox + Calendar + **Out of Office** via `PermissionValidator` - **three** module ids, each
   passed explicitly; a shared or defaulted id is a scoping leak (F2)
4. Group Management, M365 Groups, Self-Service Groups
5. Migration, Licensing Updates (on-circuit, F4), Account Lockout (principal already present, F5)

**OQ-1 (owner, non-blocking):** should the AD Attribute Editor **undo** path service too? Undo
reverses a change to a principal that may since have become protected. Recommended yes, for
symmetry - but it is the one place where servicing lets someone modify a protected principal
without an explicit forward action, so it is called out rather than assumed.

## Per-module slice shape

Each commit does exactly this, and nothing else:

1. Thread the acting `ClaimsPrincipal` to the enforcement point (D1).
2. On a protected result, consult `_servicers.Evaluate(user, ModuleId)`.
3. Allowed: proceed, and carry the authorising group into the audit record via the module's own
   audit method, in `extra` (**never** `errorDetail` - every one of these writes
   `["error"] = success ? null : errorDetail`, so a detail on a successful serviced action is
   silently discarded; that was the blr-era finding and it must not recur). **Name the specific
   audit method in the commit**, since most of them only gained an `extra` parameter in slice 0a.
4. Add the module id to `ModulesWithProtectedPrincipalServicing` so the editor appears - **in the
   same commit**, never before.
5. Tests: protected + non-servicer refuses; protected + servicer proceeds and **the written audit
   event actually contains the group** (assert the event, not the call - a group passed to a
   parameter that discards it looks identical at the call site); `IsProtected` stays true on a
   serviced result; a servicer for a DIFFERENT module is refused here; and where a job path exists,
   the job still refuses.

## Non-goals

- Changing `Evaluate`, its fail-closed rules, or the section-access storage.
- Any bypass for bulk-job execution (D2).
- Collapsing per-module scoping into a global grant (D3).
- Reopening the no-ceremony ruling: no confirmation dialog, no typed reason, no alert on use.

## Verification

```powershell
dotnet build ExchangeAdminWeb.slnx -c Release
dotnet test ExchangeAdminWeb.slnx
dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore
git diff --check HEAD
```

Per slice, non-vacuity per guard: revert, confirm FAIL, restore, confirm PASS - **and confirm the
revert actually applied to the file first.** A probe whose edit silently matched nothing has
produced a false verdict twice here.

**One additional gate specific to this work:** after every slice, the existing protection suites
must pass UNMODIFIED. Editing one to accommodate a servicer path signals that a refusal became an
allow somewhere it should not have, and is a stop.

## Manual validation

Per module, on dev, with a real group:

1. Non-member acts on a protected principal: **refused**, exactly as before.
2. Member of that module's servicer group: **allowed**, and the audit record names the group.
3. Member of ANOTHER module's servicer group: **refused** here.
4. No group configured for the module: **refused** for everyone.
5. Where a bulk path exists (Conference Rooms, Licensing Updates): the job **still refuses**, even
   when submitted by a servicer.

Checks 3 and 5 are load-bearing - they are the two ways this work could quietly become a global
bypass.

## Risk

This is the largest authorization change in the app's history: 19 refusal points, each becoming
conditional. Stated plainly so the shape of the risk is not lost in the mechanics:

- **A threading mistake fails open.** Mitigated by D1 (required parameter, compiler-enforced) and
  by the unmodified-suite gate above.
- **Blast radius.** Every module that protects anything gains a configurable bypass. The default
  stays inert - no group configured, nothing granted - and `ppsvc-1` closed the fallback that made
  an unconfigured store permissive.
- **Reviewing 12 commits is not reviewing one.** Each slice is independently revertible, and the
  per-module scope means a mistake in one module cannot grant anything in another.
