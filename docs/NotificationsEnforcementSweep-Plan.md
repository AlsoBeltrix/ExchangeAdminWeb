# Notifications Enforcement Sweep — Plan

Status: Implemented (2026-06-30; commits bd68d10, 6e83ef9, 14c6219, + this docs slice)
Owner: Michael
Authority: `docs/ProjectConstitution.md` §Auditing And Tracing → Notifications;
`.agents/decisions.md` 2026-06-29 "Notifications are mandatory…"

## Problem

The 2026-06-29 decision made notifications mandatory but was docs-only — older modules
predate the rule and were never retrofitted. A read-only audit (2026-06-30) of all 20
non-system modules against the three notification rules found the change-notification rule
(rule 1) is *mostly* already honoured, with three modules changing state silently, and the
security-read alert rule (rule 2) effectively unimplemented.

## Audit result (verified against code, 2026-06-30)

Rule 1 — **admin notification on every mutating action.** Already compliant (notify, mostly
from the Razor page): MailboxPermissions, CalendarPermissions, OutOfOffice, BlockedSenders,
GroupManagement, M365GroupManagement, Comms10k, EmergencyDisable, DhcpAuthorization,
LicensingUpdates, ADAttributeEditor, **Migration**, **NamedLocations**. (Migration and
NamedLocations notify from their pages — `Migration.razor` 7 call sites, `NamedLocations.razor`
4 call sites — so they are compliant despite an automated pass wrongly flagging them.)

**Three rule-1 gaps — state changes with no admin notification today:**

| Module | Write operation | Audits today (where) | Notifies today |
|---|---|---|---|
| MfaReset | `Delete` MFA auth methods (Graph) | page, `Audit.LogMfaResetAction` (`MfaReset.razor` ~185–305) | **no** |
| ConferenceRooms | `Set-Place`/`Set-ADUser`/room-list writes (Room Finder + Room Type, single + bulk CSV) | page, `AuditFinderAction`/`AuditTypeAction` (`ConferenceRooms.razor`) | **no** |
| AccountLockoutRemediation | remote session logoff (WinRM `logoff`) | service, `AuditLogoff` (`AccountLockoutRemediationService.cs` ~301–439) | **no** |

Rule 2 — **admin alert on security-sensitive reads.** No read module alerts today. Candidate
reads: DelegationReport, MessageTrace (Message Analysis), RecipientLookup, EventLog viewer,
AccountLockout discovery. All already **audit** their reads.

Rule 3 — **also notify the affected user on permission/access change.** The genuine permission
modules (Mailbox/Calendar) already do. The three gap modules are not user-permission grants;
owner direction is **admins only** for all three (see Decisions below).

## Owner direction (2026-06-30)

- MfaReset and ConferenceRooms: notify admins. (Clear gaps, fix them.)
- AccountLockoutRemediation: notify admins now. **User notification is an open question gated
  on real testing** — nobody uses the module yet and it is not yet validated. Record a decision
  to revisit user-notification after testing; do not build it now.
- Rule-2 reads (DelegationReport / MessageTrace / EventLog / lockout discovery / RecipientLookup):
  owner does not consider these genuinely security-sensitive — *"they're not actually sensitive
  reads, so as long as we're logging we're okay."* They already audit. **Add admin notification
  only if it is a small lift; otherwise defer.** **Never notify users for these** (the app
  exposes only data already in AD / the address book).

## Lift assessment for rule-2 reads → recommend DEFER

Mechanically each read page could inject `EmailService` and alert on each lookup — small per
module. But the cost is not the lift, it is **volume**: every message trace and every event-log
open would email admins, burying the change-notifications that matter. Owner already signalled
these are not truly sensitive and that audit logging satisfies the need. Recommendation: **defer
rule-2 read-alerting** and record the classification, rather than ship alert-fatigue noise.

This leaves a **drift** to reconcile: Constitution rule 2 lists "audit lookups" and
"protected-object inspection" among its own examples of security-sensitive reads, which
contradicts classifying the EventLog viewer / these reads as non-sensitive. Slice 4 reconciles
the wording so the Constitution and owner direction agree (it is not left contradicted-but-unenforced).

## Scope

In scope: rule-1 admin notifications for the three gap modules; the docs/decision slice
(defer lockout user-notify pending testing; classify rule-2 reads as non-sensitive + defer;
reconcile Constitution wording). Out of scope: rule-2 read-alerting implementation; any user
notification; any EmailService API change.

## Design

All three reuse the existing generic overload
`EmailService.SendAdminNotificationAsync(performedBy, ipAddress, action, success, ticketNumber, IReadOnlyDictionary<string,string> details, errorDetail?)`.
No new EmailService overload is needed, so **EmailService is untouched → no app-version bump**;
each module bumps only its own `Version` in `Modules/ModuleCatalog.cs` (per AGENTS.md invariant
#6 / Constitution §Deployment And Versioning).

Notification is placed **co-located with the existing audit call site** in each module, mirroring
every other compliant module. Fail-safe: notification is wrapped so a send failure logs but never
changes the operation result (Constitution rule: "Notification failure must not change or mask the
backend operation result"), matching the `try { … } catch { }` shape already used in
`NamedLocations.razor`.

### Slice 1 — MfaReset (page)
Add `@inject EmailService Email` and a `SendAdminNotificationAsync` call at the
`MfaReset_Execute` audit sites — on success and on each failure path, mirroring the existing
`Audit.LogMfaResetAction` calls. `action` = `"MfaReset_Execute"`, target detail = UPN. Not on
the read-only `MfaReset_ListMethods` path. Module `MfaReset` `1.0.3` → `1.0.4`. Page-only change
→ no unit test (consistent with all other page-notifying modules); manual validation on dev.

### Slice 2 — ConferenceRooms (page)
Add `@inject EmailService Email` and notifications at the four write audit sites:
- single Room Finder apply (`AuditFinderAction`, ~590) — one notification per apply;
- bulk Room Finder CSV (~758) — **one summary notification per CSV apply** (count
  succeeded/failed), following the `LicensingUpdates_BulkApply` precedent, not one per row;
- single Room Type apply (`AuditTypeAction`, ~829) — one per apply;
- bulk Room Type CSV (~990) — one summary per apply.
Success and failure both notify. Module `ConferenceRooms` `2.0.11` → `2.0.12`. Page-only → no
unit test; manual validation on dev.

### Slice 3 — AccountLockoutRemediation (service + tests)
Inject `EmailService` into `AccountLockoutRemediationService` (constructor change) and notify at
the `AuditLogoff` completion sites for **executed** logoffs only (`execute == true`): success,
failure, and authorization-denied / protected-blocked paths. **Dry-run (`execute == false`) does
not notify.** `action` = the existing logoff action string; details = target user(s) +
machine/DC context already assembled for the audit. Module `AccountLockoutRemediation` `1.0.0` →
`1.0.1`.
This is a service change, so per AGENTS.md it requires tests: prove notification fires on
executed success and on executed failure, does **not** fire on dry-run, and that a thrown
notification does not change the returned `AccountLogoffResult` (fail-safe). Each test proven
non-vacuous (revert the call, watch it fail).

### Slice 4 — docs / decisions (no code)
1. `.agents/decisions.md`: record (a) the three rule-1 fixes; (b) AccountLockout user-notification
   **deferred pending real testing** (admins-only for now); (c) rule-2 reads classified
   **not security-sensitive** for this app (audit logging suffices), read-alerting **deferred**,
   and **never** user-notified.
2. `docs/ProjectConstitution.md` §Notifications: reconcile rule-2 examples so "audit lookups"
   etc. no longer contradict the classification — narrow the wording to genuinely
   security-response reads, or note the deployment-specific classification. Exact wording in the
   commit; flagged here as a drift fix, not a silent edit.
3. `.agents/state.md`: move the sweep from "queued" to done with pointers.

## Commit discipline

One module per commit (AGENTS.md): slice 1, slice 2, slice 3 (code+tests), slice 4 (docs) — four
commits. Each builds + tests green before the next.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release` then `dotnet test ExchangeAdminWeb.slnx` after
  each code slice.
- `dotnet format --verify-no-changes` + `git diff --check HEAD` where practical.
- Slice 3 tests proven non-vacuous.
- Manual dev validation for slices 1–2 (page changes): perform an MFA reset and a Room Finder/Type
  apply on dev, confirm the admin notification arrives and the operation result is unaffected when
  SMTP is unreachable.

## Versioning (owner-decided 2026-06-30)

**Patch** bumps: this is bringing modules in line with already-expected (mandatory) behaviour —
conformance, not new capability — so each gap module takes a patch bump (`MfaReset` `1.0.3`→`1.0.4`,
`ConferenceRooms` `2.0.11`→`2.0.12`, `AccountLockoutRemediation` `1.0.0`→`1.0.1`). App version
unchanged (EmailService untouched).
