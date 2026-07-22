# Self-Service Group Management (GM-3) Plan

Status: Draft
Owner: Michael
Last verified against code: 7b64b17 (2026-07-22)

<!-- Sections marked [YOU] are written or approved by Michael, in plain language.
     Sections marked [MODEL] are drafted by the model and only skimmed by Michael.
     This is a change ticket for source code. Treat it like one.

     Design authority for this plan: .agents/decisions.md 2026-07-22
     "GM-3 self-service group management: design direction (delegated Entra auth
     for cloud ownership)". Read that entry in full before implementing; it carries
     the delegated-auth decision, the security requirements, the on-prem
     reverse-lookup approach, and the codex-consult findings folded into scope. -->

<!-- NOTE (§1-§5 are [YOU] sections): drafted here from the design-direction
     decision because the operator's goal-statement field was empty. Michael owns
     and must approve these; open questions are listed at the top of the review
     hand-back, not silently resolved. -->

## 1. Goal  [YOU — 3 to 6 sentences]

A user (ultimately all staff, gated by this module's own per-module access group)
can see one unified list of the groups they are allowed to change, spanning BOTH
on-prem Active Directory groups and Microsoft 365 groups, without having to know
where any group lives or what it is exactly called. The list is populated by an
explicit "load the groups I can manage" button (with a "this can take some time"
note), not preloaded on page open, and shows each group's type, its location
(on-prem or M365), and its other owners. This is a SELF-SERVICE feature only: it
always operates on the signed-in user's OWN groups. There is no admin
"manage for another user" mode -- admins continue to use the existing
search-by-name group-management screens for both AD and M365 (see §2 and the
2026-07-22 scope decision). For this first cut the only change a user can make is
adding or removing members; owner changes, group creation/deletion, and
dynamic-group edits are out of scope. Cloud (M365) ownership must be discovered by
signing the user in to Entra so Microsoft returns exactly the groups they own,
because the app cannot answer "which groups does this user own" with its existing
app-only Graph credential at any scale (verified 2026-07-22: no app-only Graph
route exists for owned groups; delegated sign-in is the only efficient path).

## 2. Non-goals  [YOU — bullets]

- No owner/manager mutation of any kind (adding, removing, or transferring group
  owners). Owner changes alter the authorization predicate itself and are excluded.
- No group create, update (rename/description), or delete.
- No editing of dynamic M365 groups — they are shown read-only.
- No admin "manage groups for a specified user" mode. Admins use the existing
  search-by-name AD Group Management and M365 Group Management screens, unchanged.
  (Dropped 2026-07-22: Graph has no efficient app-only "groups owned by user X"
  call, so an admin cannot stand in for a user; only the user's own delegated token
  answers this.)
- No change to the existing admin group-management pages; legacy CRUD stays where
  it is. This is an additive self-service surface, not a replacement.
- No background worker, no periodically-refreshed ownership index (owner rejected
  the maintained-index approach; consistent with the app's no-background-worker
  posture).
- No search-then-validate UX where the user must type a group's exact name — the
  manageable list is presented up front.
- Not requesting `offline_access` / refresh tokens unless a concrete need appears
  (see §4 failure behavior); relying on short access-token lifetime instead.

## 3. Acceptance criteria  [YOU approve each; model may propose]

- AC1: An authorized user clicks "load the groups I can manage" and sees a single
  merged list containing both the on-prem AD groups and the M365 groups they own,
  each row showing group type, location (on-prem/M365), and other owners.
- AC2: The list is NOT loaded on page open; it loads only after the button is
  clicked, and a "this can take some time" note is shown before/while loading.
- AC3: A user can add and remove members on a group they are eligible to manage;
  the change is applied to the correct backend (AD or Graph) and reflected on
  re-load.
- AC4: A user who owns a group they are NOT eligible to manage (fails the
  fail-closed manageable-group eligibility rule, e.g. a privileged/out-of-scope
  group) cannot change its membership — the group is either not offered or the
  action is refused. (Ownership alone never grants management.)
- AC5: Every membership change re-checks, immediately before the write: the group
  still exists, the group is still eligible, the caller still owns it (by immutable
  directory id), and the affected member passes the protected-principal check. Any
  failed re-check blocks the write.
- AC6: The self-service owner is ALWAYS the authenticated principal. A user cannot
  manage another user's groups through the self-service path regardless of any
  submitted identifier.
- AC7: (REMOVED 2026-07-22.) There is no admin manage-for-others path. Admins use
  the existing search-by-name group screens. This AC and its former admin-path tasks
  are dropped, not deferred.
- AC8: When one backend is unavailable, the list shows the healthy backend's
  results plus a prominent "incomplete — <source> unavailable" banner, never "no
  groups found" and never a silent drop; stale/unavailable selections are disabled.
- AC9: Within the loaded manageable list, a user can filter/find a group by a
  non-prefix term (a word in the middle of the name, or a description word) —
  because the primary path loads owned groups rather than searching, this is
  in-list filtering, not tenant search. The design authority also folds in a fix to
  the sibling M365 module's prefix-only `startsWith(displayName)` search bug; see the
  ONE open scope question on whether that sibling-module fix ships with this work or
  separately.
- AC10: Every membership change writes an audit record and sends notifications per
  the Constitution (admin notification on the change; affected-user notification on
  on-prem security-group membership changes). Per-user Entra tokens are never
  written to any log or audit record.

## 4. Failure behavior  [YOU own — this is the risk section of a change ticket]

| Step / dependency | If it fails | The user sees | System state afterward |
|---|---|---|---|
| Entra delegated sign-in (OIDC) fails or is cancelled | Do not fall back to app-only for ownership; M365 side treated as unavailable | Prompt to sign in / retry; M365 portion shows the "incomplete — M365 unavailable" banner | No membership change; no token stored |
| M365 (Graph) ownership lookup fails after sign-in | M365 side treated as unavailable; on-prem still shown | On-prem groups + "incomplete — M365 unavailable" banner; M365 selections disabled | No change; on-prem list valid |
| On-prem AD reverse-lookup fails | AD side treated as unavailable; M365 still shown | M365 groups + "incomplete — on-prem unavailable" banner; on-prem selections disabled | No change; M365 list valid |
| Eligibility store unreadable | Fail closed: deny all management | No manageable groups / action refused | No change |
| Pre-write re-check fails (group gone, no longer eligible, ownership lost, protected principal) | Block the single write; continue others | That row's change refused with reason; others proceed | Only permitted changes applied |
| Per-row membership write fails (AD or Graph) | Aggregate per-row; never blanket success | Per-member success/failure summary | Successful members changed; failed ones unchanged |
| Delegated token expires mid-session | Re-authenticate (no refresh token by default) | Sign-in prompt on next M365 action | No change until re-auth |

<!-- QUESTION for Michael: the rows above are drafted from the design decision's
     "fail-closed per backend" and "partial failure shows a banner" requirements.
     Confirm the "user sees" / "system state" wording is what you want, especially
     the token-expiry row (no refresh token => re-sign-in). -->

## 5. Rollback / blast radius  [YOU own]

- New module, additive: shipped as its own `ModuleCatalog` descriptor, its own
  service(s), and its own page(s). It does not modify existing group-management
  modules, so disabling its module enablement toggle removes the surface entirely
  at runtime without a redeploy.
- The one genuinely app-wide change is adding a SECOND authentication scheme
  (Entra/OIDC) alongside the existing Windows/Negotiate scheme. This is the real
  blast radius: a misconfiguration of the OIDC scheme could affect app sign-in
  broadly. [QUESTION for Michael — see open questions on how the second scheme is
  scoped so it cannot disturb the existing Negotiate flow.]
- Reversal: revert the module descriptor + code, and remove the OIDC scheme
  registration and its config. If only the feature (not the auth scheme) needs
  pulling, disable the module via enablement.
- Data blast radius on a bad write: bounded to group membership add/remove on
  groups the caller is eligible to manage; no owner/create/delete surface exists to
  misuse. Protected-principal and eligibility re-checks gate every write.
- Requires an explicit security-review gate before it ships (per the design
  decision) because of the new auth scheme and delegated tokens.

## 6. Design sketch  [MODEL — Michael skims]

All existing-code claims below were verified against the repo at commit 7b64b17.

### 6.0 Starting point (verified against current code)

- **Auth today is Windows/Negotiate ONLY.** `Program.cs:38-39`
  `AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate()`. There
  is NO OIDC/OpenIdConnect/MSAL/`Microsoft.Identity.Web` anywhere in code, and NO
  `Azure.Identity` / `Microsoft.Graph` SDK package reference in any `.csproj`. The
  delegated-Entra sign-in is entirely net-new.
- **Graph access today is hand-rolled app-only client credentials.**
  `Services/GraphTokenClient.cs:167-174` posts `grant_type=client_credentials`,
  `scope=.default` to the tenant token endpoint using a raw `HttpClient` (named
  client `"MicrosoftGraph"`, `Program.cs:102-106`); no SDK. As the design decision
  notes, app-only cannot answer `/users/{id}/ownedObjects`, so ownership needs a
  delegated token.
- **M365 group service:** `Services/M365GroupManagementService.cs` (singleton,
  `Program.cs:125`). Builds a `GraphTokenClient` from Delinea secret
  `GraphDelineaSecretId` on module `"M365GroupManagement"`
  (`GetGraphClientAsync():33-51`). Has `AddMemberAsync`/`RemoveMemberAsync`
  (L157/L163) that already call the protected-principal gate (L176 `CheckProtectedAsync`,
  invoked L216/L237). Its search is prefix-only single-field:
  `startsWith(displayName,...)` on Unified groups, `$top=50` (L62-85) — this is the
  reported "can't find it" bug. `RemoveMemberAsync` takes `memberObjectId` (Graph
  needs a directory id, not UPN — resolve first).
- **On-prem AD group service:** `Services/GroupManagementService.cs` (scoped,
  `Program.cs:127`). PowerShell + `ActiveDirectory` module, throttle 2
  (`_adThrottle` L13). Has `AddMemberAsync`/`RemoveMemberAsync` (L213/L263) and a
  protected gate (L36 `CheckProtectedAsync`). Search is substring `Get-ADGroup`,
  `ResultSetSize=200` then `RankGroups(...).Take(100)` (L93-122). **No ownership
  query exists** — `managedBy` / `msExchCoManagedByLink` are not queried anywhere in
  the repo. "Groups I own" is net-new on both backends.
- **Protected-principal gate:** `Services/ProtectedPrincipalService.cs` (singleton,
  `Program.cs:148`). `CheckAsync(ResolvedDirectoryPrincipal)` L120;
  `ResolveWithStatusAsync(string)` L213 (both `virtual` test seams). Every write
  routes through this per the fail-closed invariant.
- **Per-user state:** NO `ProtectedSessionStore`/`ISession` exists (searched, zero
  hits). Per-circuit state is scoped services (`ClientInfoService`,
  `ClientInfoCircuitHandler.cs`); identity is read via `AuthenticationStateProvider`
  (e.g. `GroupManagement.razor:176`). **This matters for where the delegated token
  lives — see 6.2 and the open question.**
- **Audit/notify:** `AuditService.LogModuleAction(...)` (`AuditService.cs:156`,
  synchronous void); `EmailService.SendAdminNotificationAsync(...)`
  (`EmailService.cs:37`, `virtual`), `SendUserNotificationAsync(...)` (L167).
- **Catalog:** `Modules/ModuleCatalog.cs` `RegisterAll()` L111 (collection
  expression of `AdminModuleDescriptor`). "Directory & Groups" band SortOrder
  150/155/160/170. `Validate()` (L529) enforces unique Id/Route/PolicyAlias.

### 6.1 Module shape

One new module descriptor in `ModuleCatalog.RegisterAll()`, per
`docs/AdminModuleSpec.md`:

- `Id = "SelfServiceGroups"`, `Route = "self-service-groups"`,
  `Category = "Directory & Groups"`, `SortOrder ≈ 165` (between M365GroupManagement
  155 and Comms10k 160/170 — [QUESTION: exact slot]), `EnabledByDefault = false`,
  `Version = "1.0.0"`. Adding a module does NOT bump the base app version
  (`.agents/decisions.md` 2026-07-21).
- `MainPermission = new("Access", "SelfServiceGroups", FailClosed: true)` — the
  all-staff self-service gate (ultimately the broad access group).
- `GranularPermissions = []` — no granular permission. The admin "manage for a
  specified user" path is dropped (2026-07-22 scope decision), so the former
  `ManageOthers` permission is removed. The module is a single self-service surface
  that always acts as the signed-in user.
- Credential isolation (Spec §Credential Isolation): the module reuses the M365
  **app-only** Graph credential pattern via its OWN `GraphDelineaSecretId` config
  field for any app-only Graph calls it still needs (e.g. resolving a directory
  object id), and its OWN `DelineaSecretId` for on-prem AD reads. The **delegated**
  Entra flow (6.2) uses a separate Entra app registration configured for delegated
  scopes — it must NOT reuse another module's app-only secret. [QUESTION: is the
  delegated Entra app registration config a new module config field, or app-level
  config? See open questions — it straddles module and app scope.]

### 6.2 Delegated Entra authentication (the riskiest, foundational slice)

Net-new second authentication scheme alongside Negotiate. `Program.cs:38-39` keeps
Negotiate as the **explicit** default authenticate/challenge scheme; the OIDC +
cookie schemes are added with UNIQUE names and must NEVER be a default (codex F2).

**Library decision (was an open question; now settled in-plan, still needs owner
sign-off):** use `Microsoft.Identity.Web` (MIW) + MSAL for the auth-code flow and
token cache. Hand-rolling OIDC, nonce/state validation, and a token cache is more
security surface than the vetted library buys. This is the one place the "no SDK"
status quo changes. (codex F8: MIW mandated, not optional.)

**Scheme wiring without destabilizing Windows auth (codex F2):**
- Negotiate stays the default for the app and the Blazor hub. Add two uniquely
  named auxiliary schemes: an OIDC challenge scheme and its companion cookie
  sign-in scheme. The auxiliary cookie must be scoped so it can NEVER satisfy app
  or SignalR-hub authorization (distinct scheme name; module/hub policies continue
  to require the Negotiate identity).
- A Blazor Server interactive circuit CANNOT issue an OIDC redirect after response
  headers have started. The sign-in is driven through dedicated HTTP endpoints
  (challenge / callback / sign-out) reached by FULL-PAGE navigation, not an
  in-circuit redirect. "Load my M365 groups" navigates to the challenge endpoint;
  on return the circuit reads the resulting delegated session.

**Identity binding — the actor and the Entra account must be bound (codex F1,
BLOCKER):**
- The Windows/Negotiate principal is the ACTOR (authorization + audit identity).
  The Entra `/me` account is a separate identity. They must be bound: on callback,
  map the Entra `(tid, oid)` to the Negotiate principal's immutable SID/objectGUID
  through an authoritative directory lookup. On mismatch, REJECT the delegated
  session and audit both identities. A user must not be able to sign in as a
  different Entra account and act under their Windows authorization.
- All authorization and audit records use the bound Windows actor; the Entra token
  is only the mechanism for the ownership query and cloud write.

**Scopes (codex F8):** request only the settled, narrowest delegated scopes that
support read-own-groups + user member add/remove (e.g. `GroupMember.ReadWrite.All`,
`User.Read` — the exact set is fixed in an endpoint/scope matrix in task 1 before
coding, not assumed here). Do NOT request `offline_access`; explicitly TEST that no
refresh token is issued or cached rather than assuming it.

**Token storage / cache isolation (codex F3, BLOCKER):** a scoped
`ITokenAcquisition` does NOT by itself make the token cache circuit-private —
redirects replace circuits and cookies span browser tabs. Tokens live ONLY in a
bounded server-side MIW cache; the circuit holds an opaque handle bound to
(Windows SID + Entra account + an auxiliary-session nonce). Define eviction and
sign-out. For any multi-node deployment the cache must be encrypted distributed
storage (single-node today, but the design must not assume it). Tokens are never
logged, never written unencrypted, never placed in audit/trace (AC10).
- The self-service owner is ALWAYS the bound authenticated principal; any submitted
  owner id is ignored on the self path.

### 6.3 Ownership discovery (net-new on both backends)

- **M365 (delegated), self only:** call Graph `/me/ownedObjects/microsoft.graph.group`
  with the bound per-user delegated token — Microsoft returns exactly the groups
  the user owns. Follow `@odata.nextLink` pagination fully; honor `Retry-After`
  throttling; distinguish 401 / 403 / conditional-access claims challenges from a
  hard failure (codex F12). There is no admin/subject variant — the 2026-07-22 scope
  decision dropped the admin-for-others path precisely because `/users/{id}/ownedObjects`
  has NO application permission and no app-only owner-filter exists, so an admin
  cannot query another user's owned groups. Only `/me` (the signed-in user) applies.
- **On-prem (app-only AD reads, per-user query):** new reverse-lookup on the
  existing `GroupManagementService` (or a sibling service) that queries `Get-ADGroup`
  filtered by `managedBy` = the user's DN AND the Exchange multi-owner
  `msExchCoManagedByLink` list. Per-user server-side query, not a tenant scan. All
  identity input (typed admin id, DN, UPN) is resolved ONCE to an immutable id via
  parameterized APIs with RFC-compliant LDAP escaping and NO PowerShell string
  interpolation (codex F11); the same resolved object is used for both
  authorization and mutation. This is new code; the existing search-by-substring
  path is untouched.
- **Eligibility (fail-closed, ON TOP of ownership) — an OU/scope allowlist is NOT
  sufficient (codex F5, BLOCKER):** ownership is not authorization, and an ordinary
  user could otherwise add themselves to an eligible group that confers privilege
  through nesting, application access, or a directory role. Eligibility is an
  ADMIN-CONTROLLED immutable-ID allowlist (or an equally unforgeable
  classification). It DENIES role-assignable groups, groups nested into privileged
  authorization groups, and app-access-conferring groups. The AD write credential's
  ACL/JEA rights are additionally constrained to the same approved set (defense in
  depth — the credential cannot write outside the allowlist even if a check is
  bypassed). Unreadable eligibility store = deny all (Known Failure Class #3,
  fail-closed). Applied to BOTH backends.

### 6.4 Unified surface

- New page `Components/Pages/SelfServiceGroups.razor` with
  `@attribute [Authorize(Policy = "SelfServiceGroups")]`, `OnInitializedAsync`
  re-check + `<ModuleVersion />` in the heading (Spec §UI Rendering, REQUIRED).
- Explicit "load the groups I can manage" button; nothing loads on page open (AC2).
- Query both backends concurrently behind small adapters returning a normalized
  `ManageableGroup` (id, displayName, type, location on-prem/M365, other owners,
  capability flags `CanManageMembers`, `IsDynamic`). Merge into one list; preserve
  per-source capabilities. Dynamic M365 groups shown read-only.
- Partial failure (one backend down): show healthy results + a prominent
  "incomplete — <source> unavailable" banner; disable stale selections; NEVER "no
  groups found", NEVER silent drop (AC8, Known Failure Class #2).
- No admin "manage for user X" entry point (dropped 2026-07-22). The page always
  acts as the signed-in user.

### 6.5 Member add/remove (only mutation in first cut)

- New service `Services/SelfServiceGroupService.cs`. **Cloud writes MUST use the
  bound delegated identity — NO app-only fallback (codex F4, BLOCKER).** The
  existing `M365GroupManagementService.AddMemberAsync`/`RemoveMemberAsync` build a
  `GraphTokenClient` from the app-only `GraphDelineaSecretId` (verified 6.0); reusing
  them as-is would bypass Graph's delegated owner enforcement. Either pass a
  delegated `GraphTokenClient` into a shared code path, or write a delegated-only
  add/remove; app-only must never be the credential for a self-service cloud write.
  (No admin app-only mutation path exists -- the admin-for-others path is dropped,
  2026-07-22.)
- **First-cut membership is USER-ONLY (codex F7, BLOCKER).** Security groups accept
  users, groups, devices, and service principals; restricting to users bounds the
  blast radius. Resolve and validate exactly one immutable object id. The Graph
  removal is the reference form `DELETE /groups/{groupId}/members/{memberId}/$ref`
  — omitting `/$ref` can delete the directory object itself; this exact URI is
  contract-tested.
- Every membership write re-checks, immediately before writing (AC5): re-query the
  actor's module + granular permission (a Blazor circuit principal can be stale —
  codex F9), re-read group, re-check eligibility, re-check ownership by immutable
  directory id, protected-principal check on the affected member
  (`ProtectedPrincipalService.CheckAsync`). Fail-closed on any failed re-check.
  Same-group operations are serialized; the residual TOCTOU window between check and
  the service-account/directory write is either closed with a conditional write or
  explicitly accepted and documented (codex F9). Downstream least-privilege (the
  ACL/JEA-constrained write credential, 6.3) is the backstop.
- Per-row failure aggregation, never blanket success (Known Failure Class #2).
- **Atomicity across write + audit + notify (codex F10):** use an operationId
  (`OperationTraceService` scope, Spec §Audit). Write a durable pre-write audit
  intent, then the directory write, then reconcile ambiguous results (e.g. a
  timeout AFTER the write may have committed — post-write read-back). Membership
  changes are expressed as idempotent desired-state (add-if-absent /
  remove-if-present) so a retry is safe. Notification failure must not lose the
  audit record; notifications go through the shared `EmailService` with a
  retry/outbox path, never a module mailer.
- Audit every change via `AuditService.LogModuleAction(...)`; admin notification on
  every change; affected-user notification on on-prem security-group membership
  changes (the no-user-notify exception is scoped to M365 only — decision +
  Constitution §Notifications). Tokens never enter audit/trace/log.

### 6.6 Bug fixes folded in (codex findings) — scope-gated

Per the design decision these pre-existing bugs are in scope "as part of this,"
but each is only justified where it serves an AC:

- M365 search moved to Graph `$search` (tokenized) + `ConsistencyLevel: eventual` +
  `$count=true` + pagination + post-ranking over displayName/description; requires
  adding explicit-header support to the Graph client (`GraphTokenClient` currently
  has no per-request header hook — verified L32-121). Serves AC9.
- On-prem 200-cap may miss the exact match; on-prem ranking searches email but
  ignores it when ranking. Serves the "find the group" experience.

These are in the design authority's "codex findings folded into the design" and
"pre-existing bugs to fix as part of this." The ONLY residual scope question is
sequencing: ship these sibling-module search fixes inside this work stream, or as a
separate commit. Listed once in open questions; not re-litigated here.

### 6.7 Invariant conformance checklist

- Fail-closed authorization + eligibility + protected-principal on every write
  (Known Failure Class #3). Pure ASCII in all `.cs` (`.agents/decisions.md`
  2026-07-21). New service ⇒ new xUnit tests before "done" (repo-guidance
  Verification). No code before this plan is Approved. Security-review gate before
  ship (design decision §Security requirements).

## 7. Task breakdown  [MODEL — Michael skims]

Ordered; slice 1 is foundational (everything M365 depends on it). Each task cites
the ACs it serves.

0. **Auth/permission matrix (design task, before any code).** Fix the exact
   delegated scopes, the challenge/callback/sign-out endpoint contract, the
   token-cache/handle model, and the actor↔Entra-account binding rule. Output is an
   endpoint/object-type/scope matrix appended to §6. Closes the design ambiguity
   behind codex F1/F2/F3/F8 before implementation. (all ACs) (F6's admin-role
   question no longer applies -- admin-for-others dropped 2026-07-22.)
1. **Delegated Entra auth foundation** (AC1, AC3, AC6, AC10). Add uniquely-named
   OIDC + cookie schemes with Negotiate kept as the explicit default and the aux
   cookie unable to satisfy app/hub authorization (F2); challenge/callback/sign-out
   HTTP endpoints via full-page navigation (F2); actor↔Entra `(tid,oid)`↔SID binding
   with mismatch rejection + dual-identity audit (F1); MIW server-side token cache
   with opaque circuit-bound handle, eviction, sign-out (F3); narrowest scopes, no
   `offline_access` with a test proving no refresh token (F8). Token never logged.
2. **M365 ownership adapter, self path** (AC1). Delegated
   `/me/ownedObjects/microsoft.graph.group` with full `@odata.nextLink` pagination,
   `Retry-After` handling, and 401/403/claims-challenge distinction (F12);
   normalized `ManageableGroup`; fail-closed on token absence.
3. **On-prem ownership adapter** (AC1). New `managedBy` + `msExchCoManagedByLink`
   reverse-lookup in/beside `GroupManagementService`; per-user query, no scan;
   injection-safe single-resolution-to-immutable-id contract, LDAP escaping, no PS
   interpolation (F11).
4. **Fail-closed eligibility rule** (AC4). Admin-controlled immutable-ID allowlist
   (not just OU/scope); denies role-assignable / nested-privileged / app-access
   groups; AD write credential ACL/JEA constrained to the same set (F5); unreadable
   store = deny all. Applied to both adapters.
5. **Module descriptor + page skeleton** (AC1, AC2, AC6). `SelfServiceGroups`
   descriptor with `Access` only (no granular permission); `SelfServiceGroups.razor`
   with policy attribute, `OnInitializedAsync` re-check, `<ModuleVersion />`, the
   load button (nothing on open). Single self-service surface; no admin entry point.
6. **Unified merge + partial-failure banner** (AC1, AC8). Concurrent query behind
   adapters; merged normalized list with type/location/other-owners columns;
   per-backend fail-closed banner; cancel abandoned loads; disable stale selections.
7. **Member add/remove with pre-write re-checks** (AC3, AC4, AC5, AC10). USER-ONLY
   members, single immutable object id, contract-tested `.../$ref` removal (F7);
   delegated-only cloud write, no app-only fallback (F4); re-query actor
   permissions + re-read group + eligibility + ownership-by-id + protected-principal
   before each write, serialize same-group ops, close-or-document TOCTOU (F9);
   operationId + pre-write audit intent + post-write reconciliation + idempotent
   desired-state + notify-retry (F10); per-row aggregation; audit + notify
   (affected-user on on-prem changes).
8. **(REMOVED 2026-07-22.)** Admin manage-for-user path is dropped, not deferred
   (former AC7 / codex F6). No task.
9. **M365 `$search` rewrite + Graph explicit-header support** (AC9). In scope per
   design authority; sequencing (this work stream vs separate commit) is the one
   open scope question.
10. **On-prem search cap/ranking fixes** (AC9). Same sequencing question as task 9.
11. **Security review gate** (all ACs). Explicit review before ship (design
    decision); includes end-to-end validation in a NON-PRODUCTION Entra tenant —
    stubbed tests cannot prove scheme isolation, account binding, cache separation,
    Graph roles, or exact mutation behavior (F13). Not a code task; a required gate.

## 8. Test plan  [MODEL writes; YOU check the mapping only]

xUnit for services (`ExchangeAdminWeb.Tests/`); non-vacuity proven per change
(revert fix → test fails → restore). Blazor UI behaviors that automation cannot
cover are called out for manual validation (no dev tenant — same gap as prior work).

| AC | Test(s) |
|---|---|
| AC1 | xUnit: M365 ownership adapter returns normalized groups from a stubbed `/me/ownedObjects`; on-prem adapter returns groups from stubbed `managedBy`/`msExchCoManagedByLink`; merge produces one list with type/location/other-owners. Manual: real load on dev. |
| AC2 | UI/component test: no backend call until the button is clicked. Manual: page-open shows no list. |
| AC3 | xUnit: add/remove routes to the correct backend and applies the change (stubbed backends). Manual: real add/remove on dev. |
| AC4 | xUnit: an owned-but-ineligible group is excluded/refused; eligibility store unreadable ⇒ deny all (fail-closed). |
| AC5 | xUnit: each pre-write re-check (group gone / ineligible / ownership lost / protected principal) independently blocks the write; prove each non-vacuously. |
| AC6 | xUnit: owner is always the bound authenticated principal; a submitted owner id is ignored. Host-level: a delegated Entra account NOT bound to the Windows SID is rejected (F1); the aux cookie cannot satisfy app/hub authorization (F2); cross-user/circuit token cache separation (F3, F13). |
| AC7 | (REMOVED 2026-07-22.) No admin manage-for-others path; nothing to test. |
| AC8 | xUnit: one backend faulted ⇒ merged result contains the healthy side + "incomplete — <source> unavailable" marker, never empty/silent; stale selections flagged disabled. Multi-page and fan-out ("other owners") partial-failure cases covered (F12). |
| AC9 | xUnit: `$search` query finds a mid-string / description-word match a `startsWith(displayName)` query misses; Graph explicit-header support unit-covered. (Only if AC9 in scope.) |
| AC10 | xUnit: every successful change calls `AuditService.LogModuleAction`; admin notification sent; affected-user notification sent on on-prem membership change; assert no token string appears in audit/trace/log output. Reconciliation: a write that times out after committing is detected and audited once (F10). |
| AC3/AC5 (write contracts) | xUnit: cloud write uses the delegated client, never the app-only `GraphDelineaSecretId` path (F4); member is user-only, single immutable id; contract-test the exact `DELETE /groups/{g}/members/{m}/$ref` URI (F7); idempotent add-if-absent/remove-if-present (F10); injection-safe on-prem resolution with LDAP escaping (F11). |
| Security gate | End-to-end validation in a NON-PRODUCTION Entra tenant before ship — stubs cannot prove scheme isolation, binding, cache separation, Graph roles, or exact mutation (F13). Stated as a required manual gate, not automatable here. |

## 9. Traceability check  [MODEL fills when iteration ends; YOU read]

<!-- Filled when plan iteration ends (after codex rounds). Empty until then. -->

## 10. Review log  [MODEL appends each round]

### Round 1 — codex-commercial (gpt-5.6-sol), 2026-07-22 — NOT-CONVERGED at issue

13 findings (7 BLOCKER, 4 HIGH, 1 MED, plus F13 BLOCKER on testability). All folded
into §6-§8. Resolutions:

- **F1 (BLOCKER) — actor/Entra identity unbound.** RESOLVED in §6.2: bind Entra
  `(tid,oid)` to the Negotiate SID/objectGUID on callback; reject mismatch; audit
  both. Test AC6.
- **F2 (BLOCKER) — named OIDC handler is not a safe aux identity; Blazor circuit
  cannot redirect after headers start.** RESOLVED in §6.2: Negotiate stays explicit
  default; uniquely-named aux OIDC+cookie schemes; challenge/callback/sign-out HTTP
  endpoints via full-page navigation; aux cookie cannot satisfy app/hub auth.
- **F3 (BLOCKER) — scoped `ITokenAcquisition` != circuit-scoped cache.** RESOLVED in
  §6.2: bounded server-side MIW cache; opaque handle bound to SID+account+nonce;
  eviction/sign-out; encrypted distributed store for multi-node.
- **F4 (BLOCKER) — "reuse existing add/remove" would write via app-only credential.**
  RESOLVED in §6.5: delegated-only cloud write, no app-only fallback; admin app-only
  path (if any) isolated behind a separate credential+authorizer. Test AC3/AC5 row.
- **F5 (BLOCKER) — OU/scope allowlist does not prove a group safe.** RESOLVED in
  §6.3: admin-controlled immutable-ID allowlist; deny role-assignable/nested-priv/
  app-access groups; AD write credential ACL/JEA constrained to the same set.
- **F6 (BLOCKER) — admin path undefined; `/me` wrong subject; `ownedObjects` has no
  app permission; `ManageOthers` != Entra write role.** RESOLVED in §6.3/§6.5/task8:
  model actor vs subject; `/users/{subject}/ownedObjects`; explicit Entra-role vs
  isolated-app-only decision deferred to task 0; admin write MAY be load-only in
  first cut until that resolves.
- **F7 (BLOCKER) — member types + exact removal op unspecified; missing `/$ref` can
  delete the object.** RESOLVED in §6.5: user-only members, single immutable id,
  contract-tested `DELETE .../members/{m}/$ref`.
- **F8 (HIGH) — MIW vs hand-rolled, scopes, client cred, refresh unresolved.**
  RESOLVED: MIW mandated in §6.2; scope/endpoint matrix is task 0; test that no
  refresh token issues.
- **F9 (HIGH) — pre-write check not airtight; stale circuit principal; non-atomic
  AD check+write.** RESOLVED in §6.5: re-query permissions per mutation; serialize
  same-group ops; close-or-document TOCTOU; least-privilege backstop.
- **F10 (HIGH) — timeout-after-commit, audit/notify failure absent.** RESOLVED in
  §6.5: operationId, pre-write audit intent, post-write reconciliation, idempotent
  desired-state, notify retry/outbox. Test AC10.
- **F11 (HIGH) — identifier resolution not injection-safe.** RESOLVED in §6.3:
  resolve once to immutable id, LDAP escaping, no PS interpolation. Test AC3/AC5.
- **F12 (MED) — ownership discovery omits pagination/throttling/claims/partial
  fan-out.** RESOLVED in §6.3: full nextLink paging, Retry-After, 401/403/claims
  distinction, cancel abandoned loads. Test AC8.
- **F13 (BLOCKER) — stubs cannot prove isolation/binding/cache/roles/mutation; no
  dev tenant.** RESOLVED in task 11/§8: host-level dual-scheme + cross-user/circuit
  tests; forged-input/revocation tests; mandatory non-prod Entra tenant validation
  before ship.

### Round 2 — codex-commercial (gpt-5.6-sol), 2026-07-22 — NOT-CONVERGED (final round)

Re-judged F1-F13 against the revision. CLOSED: F1, F2, F3, F4, F5, F7, F11 (7).
Remaining (owner decision required — the 2-round codex budget is spent):

- **F6 RESOLVED BY REMOVAL (owner, 2026-07-22).** The admin manage-for-others path
  is dropped entirely (scope decision `.agents/decisions.md` 2026-07-22), after
  verifying against current Microsoft docs that no app-only Graph route returns
  "groups owned by user X." AC7, the `ManageOthers` permission, and former task 8 are
  removed. The whole actor-vs-subject / delegated-admin-role surface is gone, not
  deferred. This also moots the F6 half of the round-2 finding.
- **F8 STILL-OPEN (HIGH).** Exact delegated scopes, confidential-client credential
  placement, and admin roles are deferred to task 0. Legitimate as a design-task
  deferral, but a cold implementer cannot start slice 1 until task 0 produces the
  matrix; task 0 is a real prerequisite, not optional.
- **F9 STILL-OPEN (HIGH).** The pre-write authorization TOCTOU is left as "close
  with a conditional write OR document as accepted risk" — the decision itself is
  unresolved. Owner must pick: implement conditional/atomic write, or accept+document
  the residual race.
- **F10 STILL-OPEN (HIGH).** The durable pre-write audit intent + notification
  outbox/retry design has no storage or dispatch mechanism, and an outbox/retry loop
  arguably conflicts with the §2 non-goal "no background worker." Genuine internal
  tension — needs either a concrete no-worker design (e.g. synchronous best-effort +
  reconciliation-on-next-load) or an explicit carve-out.
- **F12 STILL-OPEN (MED).** "Other owners" fan-out partial-failure appears only as a
  test obligation (§8 AC8 row); no §6 design for what the column shows when the
  owner-lookup for a row fails.
- **F13 STILL-OPEN (BLOCKER-class for ship, not for plan approval).** The mandatory
  non-production Entra tenant validation has no provisioning task or owner. Consistent
  with the standing "no dev tenant" gap; blocks ship, not plan approval.
- **NEW HIGH.** AC9's in-list filter (find by mid-string / description term within
  the loaded list) has no design in §6, no task in §7, and its §8 test row actually
  describes the sibling-module `$search` fix, not in-list filtering. AC9 is currently
  unimplemented by the plan.

Convergence assessment: the auth/security core (F1-F5, F7, F11) converged — the
delegated-auth foundation is now soundly specified. The residue is mostly
owner-level scope/design decisions the plan deliberately parked (F6, F8, F9, F10)
plus two genuine coverage gaps (F12 design, AC9/NEW). None reopen the closed
security core; all are resolvable by owner direction without a third codex round.

### Post-round-2 scope change (owner, 2026-07-22) — resolves/moots several open items

After the two-round codex budget, the owner narrowed scope (`.agents/decisions.md`
2026-07-22 "GM-3 scope narrowed"): admin manage-for-others is DROPPED. Effect on
the remaining open findings:
- **F6 — resolved by removal** (admin path gone; see the F6 entry above).
- **F8 (scopes/creds)** — still a task-0 design item, but simpler now: only the
  user's own `/me` delegated scopes, no admin-role/consent question.
- **F9 (TOCTOU)**, **F10 (audit-intent/notify durability)**, **F12 (other-owners
  fan-out)**, **AC9/NEW HIGH (in-list filter)** — unaffected by the scope change;
  still open, still owner decisions/design gaps to close before/at implementation of
  their slices. Tracked here; not blocking plan approval of the self-service core.
