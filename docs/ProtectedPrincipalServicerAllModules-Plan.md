# Protected-principal servicing across every module

Status: **Draft, awaiting owner approval.**

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
| `PermissionValidator` | 1 | Mailbox + Calendar Permissions |
| `SelfServiceGroupService` | 1 | in service |

## The blocker, and it shapes everything

**None of the ten services can see who is acting.** `ClaimsPrincipal` appears zero times in every
one of them - verified across all ten. `Evaluate(user, moduleId)` needs a principal, so each
service must be given one before it can consult the servicer decision.

This is the whole cost of the work. Threading an identity into ten services that deliberately have
none is not mechanical, and it is exactly where a mistake becomes a silent authorization hole:

- A service that **defaults** the principal when none is supplied fails OPEN.
- A service that reads an **ambient** principal (thread/async-local) attributes a bypass to whoever
  happens to be on the thread - which in a bulk job is nobody, or the wrong person.

**Bulk jobs have no operator at all.** The circuit is gone; a job carries `SubmittedBy`,
`SubmittedIp` and `AuthSnapshotJson` (`Services/Jobs/BulkJobModels.cs`). The existing plan already
ruled: **no bypass for off-circuit bulk-job execution.** That ruling stands here and is not
reopened - a job hitting a protected principal still refuses, whoever submitted it.

Two modules are affected by that: Conference Rooms (bulk room operations) and Licensing Updates
(CSV rows). Their INTERACTIVE paths can service; their job paths cannot.

## Decisions

**D1 - How does the principal reach a service?** An explicit parameter on the operation, never an
ambient lookup and never a nullable-defaulting one. Where a service method already takes an
operation context, extend it; otherwise add a required parameter so every caller is forced to
supply one and the compiler finds them all. Recommended: a missing argument becomes a build error
rather than a runtime fail-open.

**D2 - What does a service do when the principal is absent (bulk job)?** Refuse, exactly as today.
A null principal is not "unknown, allow" - `Evaluate` already denies a null user, and this plan
keeps that as the only behaviour for off-circuit work.

**D3 - One servicer group for all modules, or one per module?** **Per module**, unchanged. Owner
ruled this 2026-08-06 and nothing here reopens it. The owner may configure the SAME group in every
module, which achieves "one group everywhere" as a configuration choice rather than by removing the
boundary. That distinction matters: it keeps the audit answerable and lets one module be revoked
without touching the rest.

**D4 - Order of work.** Riskiest-value first, one module per commit:
1. Conference Rooms (a gate already exists; proves the pattern beyond Blocked Senders)
2. MFA Reset, Emergency Disable, Comms-10k, AD Attribute Editor (inline pages, principal at hand)
3. Mailbox + Calendar via `PermissionValidator` (one change, two modules)
4. Group Management, M365 Groups, Self-Service Groups
5. Migration, Licensing Updates, Account Lockout Remediation

**OQ-1 (owner, non-blocking):** should the AD Attribute Editor **undo** path service too? Undo
reverses a change to a principal that may since have become protected. Recommended yes, for
symmetry - but it is the one place where servicing lets someone modify a protected principal
without an explicit forward action, so it is called out rather than assumed.

## Per-module slice shape

Each commit does exactly this, and nothing else:

1. Thread the acting `ClaimsPrincipal` to the enforcement point (D1).
2. On a protected result, consult `_servicers.Evaluate(user, ModuleId)`.
3. Allowed: proceed, and carry the authorising group into the audit record via `extra`
   (**never** `errorDetail` - `LogModuleAction` writes `["error"] = success ? null : errorDetail`,
   so a detail attached to a successful serviced action is silently discarded; that was blr-era
   finding and it must not recur).
4. Add the module id to `ModulesWithProtectedPrincipalServicing` so the editor appears - **in the
   same commit**, never before.
5. Tests: protected + non-servicer refuses; protected + servicer proceeds and audits the group;
   `IsProtected` stays true on a serviced result; and where a job path exists, the job still
   refuses.

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
