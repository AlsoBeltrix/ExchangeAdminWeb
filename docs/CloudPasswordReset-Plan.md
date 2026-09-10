# Cloud Password Reset Module (Entra ID cloud-only accounts)

Status: **Draft -- awaiting owner go.** One open owner decision (D1). D2 is a risk
acceptance the owner must make explicitly before the app registration is consented.

New module `CloudPasswordReset`. **No base app version bump** (Constitution, Deployment
And Versioning: adding a module is not a shared-infrastructure change).
`Services/GraphTokenClient.cs` already exposes `PatchWithStatusAsync`
(`GraphTokenClient.cs:134`), so this module needs no shared-infrastructure change and the
exception applies cleanly.

## Purpose

Owner request 2026-09-10, verbatim: *"can we explore writing a module for this that would
allow L2 to reset microsoft passwords? currently we sync on-prem ad to Azure, but we also
have Azure-only accounts for admins and other tactical needs. L2 can reset local AD
passwords, but not Azure."*

Give L2 a password reset path for Entra ID **cloud-only** accounts -- the population that
has no on-premises object and is therefore unreachable from the existing AD tooling. The
account population the owner named is admin and tactical accounts, so role-holding targets
are IN scope (see Scope).

## Scope

IN:

- Cloud-only Entra ID user accounts (`onPremisesSyncEnabled` not true).
- Targets holding Entra administrative roles, behind their own permission tier (see
  Authorization). The owner named these accounts as the reason for the module.
- One target at a time. No bulk reset.

OUT (non-goals):

- Synced accounts. Their password is mastered on-premises; see "The synced-account rule".
- Guest / external (`userType` `Guest`) accounts.
- Any change to a user's MFA methods. That is the existing `MfaReset` module.
- Self-service password reset for the signed-in operator. Not this module, and Microsoft's
  own reset API refuses self-reset regardless.
- Unblocking / unlocking an account, enabling a disabled account, or any other
  `passwordProfile`-adjacent property. This module writes `passwordProfile` and nothing
  else.

## The Graph surface, and why the obvious API is the wrong one

Verified against Microsoft Learn 2026-09-10.

**`POST /users/{id}/authentication/methods/28c10230-.../resetPassword` is not usable by
this app.** Its permissions table reads, verbatim:

| Permission type | Least privileged | Higher privileged |
| --- | --- | --- |
| Delegated (work or school account) | UserAuthenticationMethod.ReadWrite.All | Not available. |
| Delegated (personal Microsoft account) | Not supported. | Not supported. |
| **Application** | **Not supported.** | **Not supported.** |

Every Graph module in this repo authenticates app-only with a client secret out of Delinea
(`MfaResetService.cs:20-37` is the pattern). That shape structurally cannot call
`resetPassword`. Reaching it would require a delegated flow in which the signed-in operator
personally holds *Authentication Administrator* or *Privileged Authentication
Administrator* -- a different authentication architecture for the whole app, and it would
hand each L2 the role directly rather than mediating it, which defeats the point of the
module.

**The app-only path is `PATCH /users/{id}` with `passwordProfile`.** Learn, on that
operation, verbatim: *"In app-only scenarios using Microsoft Graph application permissions,
User-PasswordProfile.ReadWrite.All is the least privileged permission."*

| Operation | Method and path | Permission | Success |
|---|---|---|---|
| Resolve target | `GET /users/{upn}?$select=id,displayName,userPrincipalName,accountEnabled,onPremisesSyncEnabled,userType` | `User.Read.All` | 200 |
| Detect admin roles | `GET /users/{id}/transitiveMemberOf/microsoft.graph.directoryRole?$select=id,displayName` | `RoleManagement.Read.Directory` (ASSUMPTION -- confirm at consent; `Directory.Read.All` is the fallback and is wider) | 200 + `value[]` |
| Reset password | `PATCH /users/{id}` body `{"passwordProfile":{"password":"...","forceChangePasswordNextSignIn":true}}` | `User-PasswordProfile.ReadWrite.All` | 204 |

All v1.0. `GraphTokenClient` hardcodes the v1.0 base (`GraphTokenClient.cs:16`).

Consequences of the PATCH path, all load-bearing:

1. **There is no system-generated password option.** That is a `resetPassword` feature only.
   The PATCH body requires a password the caller supplies. D1 exists because of this.
2. **The write is synchronous.** `resetPassword` is a long-running operation returning
   `202` plus a `Location` header to poll; PATCH returns `204` and is done. Simpler, and no
   polling loop to get wrong.
3. **A rejected password comes back as `400`.** Tenant banned-password and complexity
   policy are evaluated server-side. The page must surface the rejection as a rejection,
   not as a generic failure, and must not report success.

## The synced-account rule

A synced account's password is mastered in on-premises AD. Writing `passwordProfile` on one
is wrong even where Graph permits it: it either fails or is overwritten at the next sync.
The module reads `onPremisesSyncEnabled` during preflight and **refuses any target where it
is true**, naming the existing on-premises path in the refusal. This is the boundary that
makes the module coherent with the owner's own framing -- L2 already resets those.

Fail-closed corollary: if the resolve call does not return a definite
`onPremisesSyncEnabled: false`, the target is refused. An unreadable or absent property is
a refusal, never an assumption of cloud-only (Known Failure Class 3).

## The PIM trap

`transitiveMemberOf/microsoft.graph.directoryRole` returns **active** role assignments
only. A PIM-*eligible* administrator who has not activated reads as a non-admin and would
fall into the lower permission tier. Recorded because it is silent: nothing errors, the
target simply looks ordinary.

Mitigation in scope: the protected-principal list is the hard fence and does not depend on
role detection at all (see Authorization). Role detection is a *tiering* signal, not the
security boundary. Reading PIM eligibility (`roleEligibilityScheduleInstances`) needs
`RoleEligibilitySchedule.Read.Directory` and Entra ID P2, and is OUT of scope here -- but
if the owner wants eligible admins tiered as admins, that is the change, and it is a
plan revision, not an implementation detail.

## Authorization

Three gates, all fail-closed, all evaluated server-side immediately before the write
(Constitution: UI hiding is not security).

1. **`CloudPasswordReset`** -- the main permission. Opens the module, resolves and previews
   a target, resets a cloud-only account that holds no active directory role.
2. **`CloudPasswordResetPrivileged`** -- required *in addition* when the resolved target
   holds one or more active directory roles. A holder of only the main permission gets a
   refusal naming the roles. Modeled on the `IntuneDevices` two-tier shape (D1 of
   `docs/IntuneDeviceManagement-Plan.md`).
3. **Protected principals** -- `ProtectedPrincipalService.CheckAsync` on the target before
   the write, fail-closed. Cloud-only targets carry no DN, SamAccountName or ObjectGUID, so
   the AD-shaped rules cannot match; the binding identifier is `EntraObjectId`, which is the
   Graph `user.id`. `MatchesIdentity` consults it
   (`ProtectedPrincipalService.cs:718-722`), and `RiskyUsers` S6 already relies on exactly
   this, so the mechanism is proven in-repo rather than assumed.

The servicer override (`ProtectedServicer:CloudPasswordReset`) is honoured. No such row
exists in either config store on first deploy -- scope, not oversight.

**This module must be added to `ModuleConfig.razor`'s servicer opt-in set.** Omitting it is
the `ppsvc-1` / `pgwt-1` / `idm-3` finding recurring a fourth time: the capability exists in
code and is unreachable from the admin UI.

## Ticket

The module requires a ServiceNow ticket for the reset, validated through the
`ITicketValidator` / `TicketValidationService` seam introduced by
`docs/BitLockerMandatoryTicket-Plan.md` S1, with the per-module `ValidateTickets` Boolean
config field (`ConfigFieldType.Boolean`, default false). Both Rejected and Unavailable
refuse before the write.

This deliberately adopts the newer seam rather than the direct
`ServiceNowService.ValidateTicketAsync` call that `RiskyUsers` used
(`RiskyUsers.razor:577`); that plan recorded the divergence as unreconciled and flagged the
newer seam as the likely direction.

## Audit, notification and the password itself

- **The new password never leaves the screen.** It is not written to the audit event, the
  operation trace, the administrator email, the affected-user email, or any log line.
  Constitution, Credential Isolation: *"Never log secret values ... passwords ..."*. A
  source-text test asserts no audit or email call site in this module receives the password
  field.
- **Audit** (mandatory, Constitution): one `CloudPasswordReset_ResetPassword` event per
  attempt carrying target UPN, target object id, whether the target held roles and which,
  `forceChangePasswordNextSignIn`, the ticket, and the outcome. A refusal by any gate is
  itself an audited event with the refusal reason.
- **Administrator notification** (mandatory, Constitution, Notifications): one
  `EmailService` administrator email per reset.
- **Affected-user notification:** the Constitution requires notifying the affected user of
  a change to their access. A password reset makes the user's mailbox unreachable to them
  until they are given the new password, so their mailbox is not a reliable channel; the
  email goes to `otherMails` / the account's alternate address where one exists, and where
  none exists the page states on screen that no user notification was sent. Notification
  failure never changes the operation result (Constitution).
- `EmailService`'s app-wide `_notifyUsers` switch outranks anything this module sets. A
  deployment with user notifications off says so on screen and in the audit rather than
  reading as decorative (the `IntuneDevices` D2 rule).

## Owner decisions

### D1 -- OPEN: who chooses the new password?

Because the app-only path has no system-generated option (see the Graph surface above), the
password has to come from somewhere. Two shapes:

- **(a) App-generated, shown once.** The module generates a strong random password, PATCHes
  it, and displays it once on the result panel with a copy control. It is never stored,
  logged or emailed. The operator reads it to the user over the phone. Lower variance --
  every reset produces a compliant password, and no operator ever invents one or reuses a
  house pattern.
- **(b) Operator-typed.** A password field on the page. The operator chooses. More familiar
  to anyone used to the on-premises tooling, and it lets the operator pick something
  speakable over a phone -- at the cost of operators converging on a predictable pattern,
  which is the classic helpdesk weakness.

Recommendation: **(a)**, with the generated value shown once and never persisted. It is the
option that cannot degrade over time.

Consequence of the choice: (a) needs a generator plus a one-shot reveal panel; (b) needs a
password input, client-side confirmation, and careful handling so the typed value never
reaches a log or a re-render. Neither changes the Graph call.

### D2 -- OPEN, and it is a risk acceptance rather than a design fork

**An app-only grant of `User-PasswordProfile.ReadWrite.All` is not role-limited.** The
role-based restrictions Microsoft documents under "Who can reset passwords" govern
*delegated* callers -- a signed-in admin is bounded by their own role. An application
permission has no role, so the app registration this module uses will be able to reset the
password of **any** user in the tenant, Global Administrators included. This is the widest
grant this application would hold.

Inside the app, the fences are this module's two permission tiers and the protected-principal
list. Outside the app, the fence is the Delinea secret. Neither fences Graph itself: anyone
who obtains that client secret owns every password in the tenant.

Recorded as an ASSUMPTION requiring live confirmation: that app-only
`User-PasswordProfile.ReadWrite.All` does in fact succeed against a Global Administrator
target. Learn states the permission without a role carve-out for the app-only case, but the
repo's rule is to verify rather than trust a doc statement. The first live test must be
against a *disposable* admin-role account, not a real one.

The owner should accept this explicitly before the app registration is consented, and
should decide whether the protected-principal list is pre-populated with the tenant's
break-glass accounts as part of this work.

## External prerequisites

Outside the codebase; neither blocks the build, both block the first live call.

1. **A dedicated Entra app registration**, admin-consented, with application permissions:
   - `User.Read.All` -- resolve the target and read `onPremisesSyncEnabled`.
   - `User-PasswordProfile.ReadWrite.All` -- the reset. See D2 before consenting.
   - `RoleManagement.Read.Directory` -- admin-role detection. If consent shows this is
     insufficient for `transitiveMemberOf/microsoft.graph.directoryRole`, the fallback is
     `Directory.Read.All`, which is wider; record which was granted.

   Keep the three distinct. Do not reuse the `MfaReset`, `RiskyUsers`, `IntuneDevices` or
   `M365GroupManagement` registration: each already carries an unrelated blast radius, and
   this is the one grant that should not be widened by convenience (Constitution,
   Credential Isolation rule: module credentials are per-module).

2. **A Delinea Secret Server record** holding `Tenant ID`, `Application ID`,
   `Client Secret`, directly readable by the Delinea API bootstrap credential with no
   checkout or approval workflow (Constitution, Credential Isolation rule 5). Its id goes
   into the module's `GraphDelineaSecretId` config field.

## Catalog descriptor

```
Id = "CloudPasswordReset"
DisplayName = "Cloud Password Reset"
Description = "Reset the password of an Entra ID cloud-only account that has no on-premises Active Directory object."
Route = "cloud-password-reset"
Category = "Identity & Access"
SortOrder = 760            (immediately after MfaReset at 750)
EnabledByDefault = false
IsSystemModule = false
Version = "1.0.0"
MainPermission = ("Access", "CloudPasswordReset", <description>, FailClosed: true)
Permissions += ("Privileged", "CloudPasswordResetPrivileged", <description>, FailClosed: true)
ConfigFields = [ GraphDelineaSecretId, ValidateTickets (Boolean) ]
```

Every `ModulePermission` requires a non-blank `Description` -- a catalog tripwire enforces
it (`f42fdf0`).

## Slices

Each slice compiles and passes `dotnet test` on its own commit. The ordering exists because
of a mistake this repo already made twice: `ModuleCatalogTests.Catalog_RoutesHaveMatchingPagesAndPolicies`
asserts every descriptor has a matching page, so a descriptor-only commit fails the suite
(`docs/RiskyUsersModule-Plan.md`, Revision 2026-09-01). Service first, descriptor and page
together.

- **S1 -- service, models, DI.** `Services/CloudPasswordResetService.cs`: Delinea/Graph
  client bootstrap (the `MfaResetService.cs:20-46` shape including `IsAvailable`), target
  resolve, `onPremisesSyncEnabled` and `userType` refusals, active-role read, and the PATCH
  write returning a status-bearing result. No descriptor, no page. Tests for every refusal
  path and for the Known Failure Class 3 shape: a failed Graph read must never read as
  "not synced" or "no roles".
- **S2 -- descriptor and read-only page.** Catalog entry, both permissions,
  `Components/Pages/CloudPasswordReset.razor` with search plus a preflight panel stating:
  resolved identity, cloud-only yes/no, active roles held, protection status, and which
  permission tier the reset would require. No write path. Catalog tests.
- **S3 -- the write.** D1's chosen password shape, all three gates evaluated immediately
  before the write, the ticket gate, the PATCH, `400` surfaced as a policy rejection, the
  audit event, the administrator email, the affected-user email with its
  no-address-so-not-sent statement, and the `ModuleConfig.razor` servicer opt-in entry.
- **S4 -- records.** README section, plan status and traceability, `.agents/state.md`
  entry, `.agents/token-log.md` line.

Module version stays `1.0.0` across all four -- everything lands before any deploy, so it
ships once.

## Verification

Automated, per `.agents/repo-guidance.md`:

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`
- Every new test mutation-probed: revert the guard, confirm the specific test fails,
  restore, confirm green.

No `.ps1` / `.psm1` is touched, so ScriptAnalyzer and Pester are not in this stream's gate.

Manual, needing a deployed instance and the app registration -- none of these are run at
implementation time:

1. A cloud-only non-admin account resets; the user signs in with the new password and is
   prompted to change it.
2. A **synced** account is refused at preflight, naming the on-premises path, with no Graph
   write attempted.
3. A guest account is refused.
4. A cloud-only account holding a directory role is refused for an operator holding only
   `CloudPasswordReset`, and succeeds for one holding `CloudPasswordResetPrivileged`. Use a
   **disposable** admin-role account (D2).
5. A protected principal is refused, and the refusal is audited.
6. A password that violates tenant policy returns a stated policy rejection, not a generic
   failure, and no success is reported.
7. The audit event, the administrator email and the affected-user email are all present and
   **none contains the password**.
8. With `ValidateTickets` on, a bad ticket refuses before any Graph call.

## Acceptance criteria

- AC1 Cloud-only enforcement: a target with `onPremisesSyncEnabled` true is refused, and so
  is one whose sync status could not be read.
- AC2 The reset uses `PATCH /users/{id}` `passwordProfile`; the `resetPassword` endpoint is
  never called.
- AC3 A target holding an active directory role requires `CloudPasswordResetPrivileged`;
  refusal names the roles.
- AC4 `ProtectedPrincipalService.CheckAsync` runs against the target immediately before the
  write, binds on `EntraObjectId`, and fails closed.
- AC5 The password appears in no audit event, email, log or trace. Enforced by a
  source-text test, not by inspection.
- AC6 A Graph failure on any read is never interpreted as a permissive answer.
- AC7 A `400` from the PATCH is reported as a password-policy rejection and never as
  success.
- AC8 `CloudPasswordReset` is present in `ModuleConfig.razor`'s servicer opt-in set.
- AC9 Every gate refusal is audited.

## Known Failure Classes checked

1. **Side-effect ordering** -- the audit event and both emails are on the post-write path
   and unreachable when the PATCH throws; the refusal audit is on the refusal path only.
2. **Success aggregation** -- not applicable: one target per operation, by design. If bulk
   is ever added this becomes the dominant risk.
3. **Fail-closed authorization** -- all three gates deny on read failure; the sync-status
   and role reads deny on failure rather than defaulting permissive.
4. **Stale references** -- every file and line cited in this plan was read on 2026-09-10.

## Review log

(to be completed)
