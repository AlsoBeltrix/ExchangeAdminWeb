# M365 Group Member/Owner Management — Plan

Status: Implemented (2026-06-29)
App version at draft: 2.3.27 (unchanged — module-scoped change)
Module: `M365GroupManagement` (Version `1.0.3` → `1.1.0`)
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`,
`docs/AdminModuleSpec.md`. On conflict the higher source wins.

## Problem / Goal

The `M365GroupManagement` module today is **create/update/delete group + read-only
members/owners**. `Services/M365GroupManagementService.cs` exposes `GetMembersAsync`
and `GetOwnersAsync` (read) but no add/remove; `Components/Pages/M365GroupManagement.razor`
renders members and owners as read-only tables. Owner direction (2026-06-26, `.agents/state.md`
Queued work): managing memberships/owners is the whole point of the module — build it.

Goal: add the ability to **add and remove both members and owners** of an M365 group
via Graph, from the module page, with full protected-principal gating, audit, and
mandatory notifications.

This plan also closes **GAP 1** from the 2026-06-29 protected-principal sweep
(`.agents/state.md`): `M365GroupManagementService` is currently UNGATED. The existing
create/update/delete writes are brought under the protected-principal rule as part of
this work (see "Scope" — the group-level writes get a name-based gate; the new
member/owner writes get a target-principal gate, which is the substantive requirement).

## Non-Goals

- No change to group create/update/delete behavior beyond adding a protected-principal
  check. Group CRUD UI and semantics stay as-is.
- Not GM-3 (self-service group management) — separate module, separate plan.
- Not the cloud-identity protection extension (see Decisions / accepted risk).
- No new module; this extends the existing `M365GroupManagement` module.

## Key Decision — protected-principal scope for cloud members

**Decision (owner, 2026-06-29): reuse the existing on-prem-AD-based protection check
as-is**, identical to how `GroupManagementService.CheckProtectedAsync` works.

The protection machinery (`ProtectedPrincipalService.ResolveWithStatusAsync` +
`CheckAsync`) resolves an identity against **on-prem Active Directory**. M365 groups can
contain **cloud-only** accounts that don't exist in on-prem AD.

- **Accepted risk:** a protected account that exists only in the cloud, and is listed in
  the protected list only by a cloud identity (UPN/email not resolvable in AD), could pass
  the check because AD resolution returns `NotFound` (treated as "not protected"). This is
  exactly the existing behavior of on-prem Group Management; we accept the same posture
  here for consistency and lower complexity. Documented as a known limitation, not a defect.
- **Fail-closed preserved:** when resolution is `Unavailable` or `Ambiguous`, or
  `CheckAsync` reports `CheckFailed`/`IsProtected`, the operation is refused. Only the clean
  `NotFound` case is the gap above.

If the owner later wants cloud-identity protection, it becomes its own plan (extend
`ProtectedPrincipalService` to match on Graph-returned UPN/mail/Entra object ID).

## Scope of change

### 1. Service — `Services/M365GroupManagementService.cs`

Add four write methods, each returning the existing `M365GroupResult`:

- `AddMemberAsync(string groupId, string memberUpnOrId)` → `POST /groups/{id}/members/$ref`
  with body `{ "@odata.id": "https://graph.microsoft.com/v1.0/directoryObjects/{objId}" }`.
- `RemoveMemberAsync(string groupId, string memberObjectId)` → `DELETE /groups/{id}/members/{objId}/$ref`.
- `AddOwnerAsync(string groupId, string ownerUpnOrId)` → `POST /groups/{id}/owners/$ref`.
- `RemoveOwnerAsync(string groupId, string ownerObjectId)` → `DELETE /groups/{id}/owners/{objId}/$ref`.

Each write method MUST, before issuing the Graph write:
1. Call a new private `CheckProtectedAsync(string identity)` that mirrors
   `GroupManagementService.CheckProtectedAsync` (lines 36–68): resolve via
   `ProtectedPrincipalService.ResolveWithStatusAsync`; fail closed on
   `Unavailable`/`Ambiguous`; on resolved principal call `CheckAsync` and fail on
   `CheckFailed` or `IsProtected`; on any exception, block as precaution.
2. Only on a clear (null) protection result, proceed to the Graph call.

**Refusal messages reused verbatim** from `GroupManagementService.CheckProtectedAsync`
(owner decision, 2026-06-29 — keep on-prem wording for consistency):
- protected → "This member is a protected principal. Operation not permitted."
- ambiguous → "Identity is ambiguous — matches multiple AD users."
- unavailable → "Protection check unavailable. Cannot verify if this member is protected."
- check failed → "Protection check failed: {reason}"

These surface as the page's existing top-of-card red `alert-danger` banner
(`M365GroupManagement.razor:40–46`); the operation is aborted and nothing is sent to
Graph. The literal word "member" is kept even on the owner add/remove paths (accepted as
cosmetic; not reworded).

Constructor/DI: add `ProtectedPrincipalService` to the service's constructor (it is a
registered singleton). Update DI in `Program.cs` only if constructor injection requires
it — `M365GroupManagementService` is already `AddSingleton`; `ProtectedPrincipalService`
must be resolvable (confirm it is registered before this module).

**Graph helper choice:** member/owner add uses `POST .../$ref` which returns 204 No
Content — use `GraphTokenClient.PostNoContentAsync` (returns bool), not `PostAsync`
(which parses a body and returns null on empty content — would misreport success as
failure). Remove uses `DeleteAsync` (returns bool). Map bool → `M365GroupResult`.

**Adding existing CRUD under the rule (GAP 1):** the substantive GAP-1 closure is the
member/owner gating above — that is where target principals are mutated. Group
create/update/delete are **not** gated by this plan (owner decision, 2026-06-29: member/owner
only, no protected-group gating). `CreateGroupAsync` has no existing target principal;
`UpdateGroupAsync`/`DeleteGroupAsync` act on a group object, not a user principal, and the
protected-principal list is principal-oriented. GAP 1 is considered closed for the
principal-write surface; group-object protection is explicitly out of scope.

### 2. Page — `Components/Pages/M365GroupManagement.razor`

Convert the read-only members/owners tables into editable lists:
- Per row: a Remove button (with inline confirm, matching the existing delete-confirm
  pattern at lines 241–249).
- Above each list: an add control (text input for UPN/email + Add button).
- Ticket Number input required for any member/owner mutation (reuse `formTicketNumber`
  pattern; the field already exists for group CRUD).
- Server-side re-authorization on every mutation (`AuthorizationService.AuthorizeAsync`
  with policy `M365GroupManagement`), mirroring `SaveGroup`/`DeleteGroup` (lines 417–426,
  482–489). UI hiding is not security (Constitution).
- After a successful mutation, reload members/owners for the selected group.

### 3. Audit + Notifications (admin-only — refines 2026-06-29 notifications decision)

For each of the four new actions:
- `Audit.LogModuleAction(...)` with action names
  `M365GroupManagement_AddMember`, `_RemoveMember`, `_AddOwner`, `_RemoveOwner`
  (success and failure, and the auth-denied path), following the existing try/catch audit
  pattern (lines 443, 464, 487, 495, 517).
- `Email.SendAdminNotificationAsync(...)` on success and failure (existing pattern,
  lines 446, 465, 498, 518).

**Affected-user notification: NO (owner decision, 2026-06-29).** The standing
notifications decision (`.agents/decisions.md` 2026-06-29) requires that a
permission/access change also notify the affected user. Owner has **refined** that rule
for this case: M365 group member/owner changes are not treated as user-notifying
permission changes — they are typically not tied to permissions, and even when they are,
user emails would just drive tickets. **Admin notification yes, affected-user
notification no.** This refinement MUST be recorded in `.agents/decisions.md` when this
work lands, so the narrower rule and the 2026-06-29 decision do not drift.

### 4. Catalog version — `Modules/ModuleCatalog.cs`

Bump `M365GroupManagement` `Version` `1.0.3` → `1.1.0` (new feature: member/owner
management). Per the two-rule versioning policy, this is a module-scoped behavior change
→ module version bump. **App `<VersionPrefix>` does NOT change** — no shared/app-wide
code changed. (Aligns with the owner's standing position that adding module capability
shouldn't move the base app version.) Confirm against
`docs/ProjectConstitution.md` §Deployment And Versioning at implementation time.

### 5. Tests — `ExchangeAdminWeb.Tests/`

New service tests (NSubstitute, mirroring `GroupManagementServiceTests.cs` and
`AccountLockoutRemediationServiceTests.cs`):
- Add/remove member/owner: **protected principal is refused** (substitute
  `ProtectedPrincipalService` to return `IsProtected` → assert no Graph write, Fail result).
- Protection check `Unavailable`/`Ambiguous` → fail closed, no write.
- Clean principal → write proceeds, success mapped from the bool helper.
- Each new test proven **non-vacuous** (revert the gate, see the test fail, restore) per
  AGENTS.md Verification.

`ModuleCatalogTests.cs`: update the module `Version` assertion for `M365GroupManagement`
if it pins `1.0.3`. Catalog count (22 modules / 31 aliases) is unchanged — no new module,
no new config field expected. Confirm no count assertion breaks.

> Test approach: **option (a), owner decision 2026-06-29.**
> `M365GroupManagementService` constructs `GraphTokenClient` internally via
> `GetGraphClientAsync()`, so the Graph HTTP layer is not trivially substitutable today.
> The protected-principal gate runs **before** `GetGraphClientAsync`, so the refusal-path
> tests (the security-critical ones) are testable without faking Graph and are the focus
> of this work. The happy-path "write reaches Graph and succeeds" hop is **not**
> unit-tested (no Graph seam is introduced); it is covered by the manual dev-deploy
> validation below. No refactor of the Graph construction is in scope.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release` then `dotnet test ExchangeAdminWeb.slnx`
  (target the `.slnx`; bare `dotnet test` runs zero tests).
- `dotnet format ExchangeAdminWeb.csproj --verify-no-changes --no-restore` and
  `git diff --check HEAD`.
- New tests proven non-vacuous (revert/restore).
- Manual (deferred to dev deploy, state clearly if not run): add/remove a real member and
  owner; confirm a protected principal is refused (red banner, no Graph write); confirm
  admin email fires (and that no affected-user email is sent); confirm audit rows.

## Resolved decisions (owner, 2026-06-29)

1. **Protection scope** — reuse on-prem-AD check as-is; cloud-only gap accepted (see Key
   Decision).
2. **Notifications** — admin notification yes; affected-user notification **no** (refines
   the 2026-06-29 standing decision; record the refinement in `.agents/decisions.md`).
3. **Group-object gating** — out of scope; gate member/owner writes only.
4. **Refusal wording** — reuse on-prem strings verbatim (keeps "member" on owner paths).
5. **Test approach** — option (a): unit-test refusal/fail-closed paths only; happy-path
   Graph hop covered by manual validation, no Graph seam introduced.

## Commit slices (one per landed slice, per AGENTS.md Git Safety)

1. Service: add four write methods + `CheckProtectedAsync` gate (+ DI wiring) + service tests.
2. Page: editable members/owners UI + audit + notifications.
3. Module version bump + catalog test update + docs (this plan → Implemented, state.md,
   decisions.md GAP-1 closure note).
