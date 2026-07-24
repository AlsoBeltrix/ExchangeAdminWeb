# Self-Service Group Management (GM-3) Plan

Status: Approved (on-prem AD only — see 2026-07-22 scope-narrowed-again decision; task 2 SCALED BACK 2026-07-24 — admin allowlist + domain-wide ACE-scan dropped; eligibility = manager-can-update-membership + on-demand single-group search; see §6.3 and `.agents/decisions.md`)
Owner: Michael
Last verified against code: 22c0510 (2026-07-22)
Approval: self-service core approved by owner 2026-07-22 ("okay, start implementing").

**SCOPE NARROWED AGAIN 2026-07-22 (`.agents/decisions.md` "on-prem AD only"): the M365 /
delegated-Entra half is DROPPED ENTIRELY.** GM-3 is now on-prem Active Directory
self-service group management only. Reason: the delegated design forced an
actor↔Entra-account binding decision (F1); the owner's real cross-identity need (Windows
on-prem login acting on an Azure-only -CLD account's groups) is cross-identity management
better served by the Microsoft portal, which already enforces proper auth. On-prem AD
self-service is the value the portal does NOT give. DROPPED: second auth scheme,
Microsoft.Identity.Web/MSAL, token cache, actor↔Entra binding, `/me/ownedObjects`,
dedicated Entra registration, delegated security-review gate (codex F1/F2/F3/F4/F8/F12/F13
moot). RETAINED and now the whole feature: on-prem ownership reverse-lookup, fail-closed
eligibility (F5), user-only add/remove with pre-write re-checks + protected-principal (F7,
F9), injection-safe resolution (F11), audit + affected-user notify (F10).

Sections below still referencing M365/Graph/delegated auth are HISTORY as of this
narrowing; the authoritative current scope is: on-prem AD only. Task 0 (§6.8) is moot.

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

(SCOPE: on-prem AD only, per the 2026-07-22 "on-prem AD only" decision. The M365
paragraph below is superseded and kept for history.)

A user (ultimately all staff, gated by this module's own per-module access group)
can see a list of the on-prem Active Directory groups they are allowed to change —
the groups they own (via `managedBy` or the Exchange multi-owner
`msExchCoManagedByLink`) — without having to know a group's exact name. The list is
populated by an explicit "load the groups I can manage" button (with a "this can take
some time" note), not preloaded on page open, and shows each group's type and its
other owners. This is a SELF-SERVICE feature only: it always operates on the
signed-in user's OWN on-prem groups (bound to their Windows/Negotiate identity).
There is no admin "manage for another user" mode. For this first cut the only change
a user can make is adding or removing USER members; owner changes, group
creation/deletion, and any M365/cloud group management are out of scope (users manage
their own M365 group membership directly in the Microsoft portal).

~~[SUPERSEDED] ...one unified list spanning BOTH on-prem AD and M365, discovered via a
delegated Entra sign-in...~~ Dropped 2026-07-22 — see the header banner and decision.

## 2. Non-goals  [YOU — bullets]

(SCOPE: on-prem AD only, per 2026-07-22 decision.)

- **No M365 / cloud group management of any kind.** Dropped entirely 2026-07-22. No
  delegated Entra sign-in, no second auth scheme, no Graph ownership query, no
  Microsoft.Identity.Web. Users manage their own M365 group membership in the
  Microsoft portal.
- No owner/manager mutation of any kind (adding, removing, or transferring group
  owners). Owner changes alter the authorization predicate itself and are excluded.
- No group create, update (rename/description), or delete.
- No admin "manage groups for a specified user" mode. Admins use the existing
  search-by-name AD Group Management screens, unchanged.
- No change to the existing admin group-management pages; legacy CRUD stays where
  it is. This is an additive self-service surface, not a replacement.
- No background worker, no periodically-refreshed ownership index (owner rejected
  the maintained-index approach; consistent with the app's no-background-worker
  posture).
- No search-then-validate UX where the user must type a group's exact name — the
  manageable list is presented up front.
- First cut: member changes are USER members only (not nested groups, devices, or
  service principals) — bounds blast radius (codex F7).

## 3. Acceptance criteria  [YOU approve each; model may propose]

(SCOPE: on-prem AD only. AC-numbering preserved; M365/delegated clauses struck.)

- AC1: An authorized user clicks "load the groups I can manage" and sees the list of
  on-prem AD groups they own (via `managedBy` or `msExchCoManagedByLink`), each row
  showing group type and other owners.
- AC2: The list is NOT loaded on page open; it loads only after the button is
  clicked, and a "this can take some time" note is shown before/while loading.
- AC3: A user can add and remove USER members on a group they are eligible to manage;
  the change is applied to on-prem AD and reflected on re-load.
- AC4: A user who owns a group they are NOT eligible to manage (fails the
  fail-closed manageable-group eligibility rule, e.g. a privileged/out-of-scope
  group) cannot change its membership — the group is either not offered or the
  action is refused. (Ownership alone never grants management.)
- AC5: Every membership change re-checks, immediately before the write: the group
  still exists, the group is still eligible, the caller still owns it (by immutable
  directory id / objectGUID), and the affected member passes the protected-principal
  check. Any failed re-check blocks the write.
- AC6: The self-service owner is ALWAYS the authenticated Windows/Negotiate principal.
  A user cannot manage another user's groups through the self-service path regardless
  of any submitted identifier.
- AC7: (REMOVED 2026-07-22.) There is no admin manage-for-others path.
- AC8: (SUPERSEDED 2026-07-22.) Was: partial-failure banner across two backends. With
  a single on-prem source there is no merge; an AD failure shows a clear error
  ("couldn't load your groups"), never "no groups found" / silent drop.
- AC9: Within the loaded manageable list, a user can filter/find a group by a
  non-prefix term (a word in the middle of the name, or a description word). Because
  the list is already loaded, this is pure in-list client-side filtering. (The former
  M365 `$search` sibling-module fix is out of scope — no M365 in this feature.)
- AC10: Every membership change writes an audit record (`AuditService.LogModuleAction`)
  and sends notifications per the Constitution: admin notification on the change, and
  affected-user notification on on-prem security-group membership changes.

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
- **Eligibility — SCALED BACK 2026-07-24 (owner). Manager-can-update-membership IS
  the eligibility rule; the admin allowlist and the ACE-scan are both DROPPED.** The
  earlier admin-controlled immutable-ID allowlist (codex F5) and the 2026-07-23
  broad "any group the user can update via GenericAll/GenericWrite/WriteMember ACE"
  rule are superseded. Both required either an admin-maintained store or a
  domain-wide ACL scan (41,368 groups, ~6-12 min); the owner ruled that cost/risk
  unjustified and no OU scope viable (AD grown since NT4.0 — any OU allowlist is
  brittle and silently drops groups). The eligibility rule is now exactly: **the
  caller is the group's declared `managedBy` manager AND "Manager can update
  membership" is on** (the WriteProperty-on-`member` ACE the AD checkbox grants the
  manager). This is authorization, not mere ownership — the manager already holds the
  directory right to edit membership, so surfacing it confers nothing new (the owner's
  framing: they could do the same in ADUC). SelfMembership (self-only) does NOT
  qualify. Still fail-closed: a group that cannot be confirmed manageable by the
  caller is refused, and a hard AD read failure is an error, never an empty/allowed
  result (Known Failure Class #3). The AD write credential's ACL/JEA rights remain the
  least-privilege backstop.
- **On-demand single-group search (net-new, 2026-07-24):** because there is no
  domain-wide scan, a user who knows they can manage a group (e.g. via a direct
  per-group ACE — the discovery finding, `tools/Discover-GroupMembershipDelegation.ps1`,
  showed edit rights are almost all direct per-user ACEs, not helpdesk-group
  delegation) can type that group's name. Resolve the name ONCE to an immutable id
  (injection-safe, RFC 4515-escaped, no PowerShell interpolation — codex F11), read
  the group, and check whether the caller can manage its membership (manager-with-
  WriteMember as above, OR a direct membership-write ACE held by the caller's SID).
  If manageable, return it; if not, return an error telling the user to contact the
  IT Support Desk. This is a single per-name lookup, not a scan.

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

### 6.8 Task 0 output — auth / permission matrix (owner-settled 2026-07-22)

This section is the task-0 deliverable the plan requires before any slice-1 code
(codex F8). It fixes the identity, credential, scopes, endpoints, token-cache model,
and actor-binding rule. Owner decisions this session are marked (OWNER 2026-07-22).

**A. Entra app registration.**
- **Dedicated, separate from the app-only registration (OWNER 2026-07-22).** The
  existing app-only registration (used by `GraphTokenClient` client-credentials for
  every other Graph call) is NOT reused. A new registration is provisioned for this
  module's delegated flow only, scoped to exactly the delegated permissions in (C).
  Rationale: least privilege + the repo credential-isolation invariant (codex F3);
  reusing the powerful app-only identity and widening it with a redirect URL + user
  scopes is rejected.
- **Confidential client, authenticating with a client secret stored in Delinea
  (OWNER 2026-07-22).** Mirrors the existing Graph secret pattern exactly: a Delinea
  secret whose fields are `Tenant ID`, `Application ID`, `Client Secret` (verified
  against `M365GroupManagementService.cs:43-45`). Certificate auth was considered and
  declined for now (ops parity with the current Delinea-secret flow). The delegated
  secret is a DISTINCT Delinea secret id from any app-only module secret — never
  shared (codex F3).
- **Config field:** a new module config field on `SelfServiceGroups`, e.g.
  `DelegatedGraphDelineaSecretId`, read the same way as
  `M365GroupManagement/GraphDelineaSecretId` (`ModuleConfigService.GetValue`). This is
  module-scoped config, not app-level. The authority (tenant) and client id come from
  the Delinea secret fields, not appsettings.

**B. Authentication scheme wiring (codex F2).**
- Negotiate stays the EXPLICIT default authenticate + challenge scheme
  (`Program.cs:38-39` unchanged in intent — Negotiate remains `DefaultScheme`). Two
  uniquely-named auxiliary schemes are added via `Microsoft.Identity.Web`: an OIDC
  challenge scheme (e.g. `"SsgEntra"`) and its companion cookie sign-in scheme (e.g.
  `"SsgEntraCookie"`). Neither is ever a default; app + Blazor-hub authorization
  policies continue to require the Negotiate identity, so the aux cookie can NEVER
  satisfy app/hub auth.
- Sign-in is driven by dedicated HTTP endpoints (challenge / callback / sign-out)
  reached by FULL-PAGE navigation, never an in-circuit redirect (a Blazor Server
  circuit cannot redirect after response headers start). Endpoint contract:
  - `GET /ssg/signin` — MIW challenge on the aux OIDC scheme; `redirectUri` returns
    to the SelfServiceGroups page.
  - `GET /ssg/callback` — MIW-handled OIDC code redemption; on success performs the
    actor-binding check (D) then establishes the aux cookie session; on binding
    failure rejects and audits both identities.
  - `POST /ssg/signout` — clears the aux cookie + evicts the token-cache entry (E).

**C. Delegated scopes (self `/me` only; codex F8, narrowed by 2026-07-22 scope cut).**
- `User.Read` — sign-in + read `/me` (bind actor, no directory-wide read).
- `GroupMember.Read.All` — enumerate `/me/ownedObjects/microsoft.graph.group` and
  read a group's members for the load list.
- `GroupMember.ReadWrite.All` — add/remove a user member on an owned+eligible group
  (the only mutation in first cut).
- `offline_access` is NOT requested. Slice 1 ships a test proving no refresh token is
  issued or cached (codex F8). Admin/`Directory.*`/application scopes are NOT
  requested — the admin-for-others path is dropped (2026-07-22).
- Delegated calls are still gated by the SAME fail-closed eligibility allowlist (6.3)
  and protected-principal check (6.5); the Graph scope is the OUTER bound, the
  allowlist the INNER bound.

**D. Actor <-> Entra-account binding (codex F1, BLOCKER).**
- The Windows/Negotiate principal is the ACTOR (all authorization + audit identity).
  On `/ssg/callback`, map the Entra token's `(tid, oid)` to the Negotiate principal's
  immutable SID/objectGUID via an authoritative directory lookup. On mismatch REJECT
  the delegated session and audit BOTH identities. A user must not sign in as a
  different Entra account and act under their Windows authorization.
- Every ownership query and cloud write uses the bound actor; the Entra token is only
  the mechanism. The self-service owner is ALWAYS the bound authenticated principal;
  any submitted owner id is ignored (AC6).

**E. Token cache / handle model (codex F3, BLOCKER).**
- Tokens live ONLY in a bounded server-side MIW token cache. The circuit holds an
  OPAQUE handle bound to (Windows SID + Entra `(tid,oid)` + an aux-session nonce);
  the raw token never enters the circuit, a log, a trace, or an audit record (AC10).
- Eviction: on `/ssg/signout`, on aux cookie expiry, and on circuit teardown. Access
  token lifetime is the natural expiry (no refresh token by default); on expiry the
  next M365 action re-challenges (§4 token-expiry row).
- Single-node today; the design must not ASSUME single-node. For any multi-node
  deployment the cache becomes encrypted distributed storage. Documented, not built,
  in first cut.

### 6.7 Invariant conformance checklist

- Fail-closed authorization + eligibility + protected-principal on every write
  (Known Failure Class #3). Pure ASCII in all `.cs` (`.agents/decisions.md`
  2026-07-21). New service ⇒ new xUnit tests before "done" (repo-guidance
  Verification). No code before this plan is Approved. Security-review gate before
  ship (design decision §Security requirements).

## 7. Task breakdown  [MODEL — Michael skims]

(SCOPE: on-prem AD only, per 2026-07-22 decision. The former delegated-Entra tasks
0/1/2/8/9 and the delegated security gate are DROPPED. Renumbered on-prem task set:)

1. **On-prem ownership reverse-lookup** (AC1). New `managedBy` + Exchange multi-owner
   `msExchCoManagedByLink` reverse-lookup on/beside `GroupManagementService`. Resolve
   the signed-in Windows principal ONCE to an immutable id (objectGUID/DN) and query
   `Get-ADGroup` filtered to groups that principal owns — per-user query, NO tenant
   scan. Injection-safe: parameterized/escaped, no PowerShell string interpolation
   (F11). Returns a normalized `ManageableGroup` (id, name, type, other owners,
   `CanManageMembers`). This is new code; the existing search-by-substring path is
   untouched.
2. **Eligibility = manager-can-update-membership + on-demand single-group search**
   (AC4). SCALED BACK 2026-07-24 (see §6.3): the admin allowlist and the domain-wide
   ACE-scan are DROPPED. (a) The passive list rule is exactly task 1's output — groups
   where the caller is the `managedBy` manager with "Manager can update membership" on;
   no separate eligibility store. (b) Net-new single-group search: user types a group
   name; resolve once injection-safe (F11), read the group, confirm the caller can
   manage its membership (manager-with-WriteMember OR a direct membership-write ACE on
   the caller's SID); return it if manageable, else an error to contact the IT Support
   Desk. Fail-closed: unconfirmable ⇒ refused; hard AD read failure ⇒ error, never an
   empty/allowed result (Known Failure Class #3). AD write credential ACL/JEA is the
   least-privilege backstop.
3. **Module descriptor + page skeleton** (AC1, AC2, AC6). `SelfServiceGroups`
   descriptor: `Access` main permission only (FailClosed), `Category = "Directory &
   Groups"`, `SortOrder ≈ 165`, `EnabledByDefault = false`, `Version = "1.0.0"`, an
   on-prem `DelineaSecretId` config field for the AD write credential. Adding a module
   does NOT bump the base app version (`.agents/decisions.md` 2026-07-21).
   `SelfServiceGroups.razor` with `[Authorize(Policy="SelfServiceGroups")]`,
   `OnInitializedAsync` re-check, `<ModuleVersion />`, the load button (nothing loads
   on page open, AC2). Owner is always the bound Windows principal (AC6).
4. **List + in-list filter** (AC1, AC9). Render the loaded owned-groups list with
   type + other-owners columns; a load failure shows a clear error, never "no groups
   found" (AC8 collapsed to single source). Client-side in-list filter matches a
   mid-string name term or a description word (AC9) — pure filtering over the loaded
   list, no directory round-trip.
5. **Member add/remove with pre-write re-checks** (AC3, AC4, AC5, AC10). USER-ONLY
   members; resolve exactly one immutable member id. Before each write re-check:
   re-query the actor's module permission (a Blazor circuit principal can be stale —
   F9), re-read the group, re-check eligibility, re-check ownership by immutable id,
   and run the protected-principal check on the affected member
   (`ProtectedPrincipalService.CheckAsync`). Fail-closed on any failed re-check.
   Serialize same-group operations; the AD check→write TOCTOU is closed or explicitly
   documented, with the ACL/JEA-constrained write credential as the least-privilege
   backstop (F9). Idempotent desired-state (add-if-absent / remove-if-present) so a
   retry is safe; per-row failure aggregation, never blanket success (Known Failure
   Class #2). Audit via `AuditService.LogModuleAction` + admin notification +
   affected-user notification on on-prem security-group changes (F10, AC10). No
   background worker / outbox needed — single synchronous on-prem write with
   post-write read-back reconciliation.
6. **Verification + manual-validation note.** New service ⇒ xUnit before "done"
   (repo-guidance). Fail-closed and pre-write-recheck paths proven non-vacuous. No
   delegated security-review gate (no cloud tokens). Live AD write / Blazor UI remain
   manual-validation-on-dev items (no dev tenant — same standing gap).

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
- **F5 (BLOCKER) — OU/scope allowlist does not prove a group safe.** SUPERSEDED
  2026-07-24 (owner scale-back, §6.3): the admin allowlist that resolved F5, and the
  2026-07-23 broad ACE-scan that replaced it, are BOTH dropped. The eligibility rule
  is now manager-can-update-membership (the caller already holds the directory right
  to edit the group's membership), plus an on-demand single-group search that confirms
  the caller's manage right before returning a group. F5's concern — ownership ≠
  authorization — is met because the rule keys on an actual membership-write right,
  not mere ownership; and the AD write credential's ACL/JEA rights are the
  least-privilege backstop. No allowlist store to maintain, no domain-wide scan.
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
