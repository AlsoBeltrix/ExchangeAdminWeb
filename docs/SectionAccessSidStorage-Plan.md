# Section-Access Groups Stored As SIDs -- Plan

Status: **Approved (owner, 2026-08-03).** D1 ruled: store an unambiguous identifier, display
the friendly name; the existing 11 unqualified rows are migrated by lookup, neither rejected
nor grandfathered. D2 withdrawn as a decision -- the single unresolved row was a probe bug,
not a data class; see its section. No open owner gates. Not yet implemented.
App version: `2.3.34` -> `2.3.35` (shared authorization path + config store).
Module: none. `AdminSettings` `1.0.2` -> `1.0.3` only if the admin page markup changes
(slice 3); the authorization change itself is app-wide, not module-scoped.
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`,
`docs/AdminModuleSpec.md`. On conflict the higher source wins.

## Problem

`section_access` stores a free-text group string per policy alias. Verified against the
prod store 2026-08-03: **58 rows across 30 policy aliases; 47 carry a `DOMAIN\` prefix (46
`ANALOG\`, 1 `winroot\`), 11 do not.** The 11 unqualified rows span 6 aliases:

```
AccountLockoutRemediation        | ExchangeWebAdmins
AccountLockoutRemediationLogoff  | ExchangeWebAdmins
BlockedSenders                   | ExchangeWebAdmins
BlockedSenders                   | ExchangeWebPerms
BlockedSendersUnblock            | ExchangeWebAdmins
BlockedSendersUnblock            | ExchangeWebPerms
MfaReset                         | ITServiceDesk-Team
MfaReset                         | TCS.WinAdmins
SelfServiceGroups                | $KOO300-S3AMUVVBVMI1
SelfServiceGroups                | ADI_ALL_EMPLOYEES
SelfServiceGroups                | IAM
```

A bare name does not identify a group. `GroupAuthorizationHandler:97-101` and
`GroupMembershipChecker:38-45` both strip any `DOMAIN\` prefix from the *stored* value and
accept a match on either the full or the bare form, so `ANALOG\ExchangeWebAdmins` and a
foreign-domain `ExchangeWebAdmins` are indistinguishable to the comparison.

**Exposure, measured rather than assumed.** 10 trusts exist, but direction decides who can
authenticate *here*: 8 are Inbound (this domain's users out to them) and only **2 are
BiDirectional** -- `winroot.analog.com` and `maxim-ic.internal` -- neither with selective
authentication. So the collision surface is those two domains plus the local one, not ten.
Narrow, not zero, and it sits in the field that decides who gets into a privileged module.

Domain SIDs confirmed distinct, which is why a SID closes this outright:

```
ad.analog.com       S-1-5-21-8915387-325452579-1788637320
winroot.analog.com  S-1-5-21-725345543-2052111302-839522115
maxim-ic.internal   S-1-5-21-1473421086-2623460355-3555319897
```

A group SID is that domain prefix plus a per-domain RID, so `IAM` in this domain
(`...-1788637320-586078`) cannot collide with an `IAM` anywhere else. **The SID is
self-qualifying; the name is not.**

### The Windows token already carries SIDs

Measured on this host: a `WindowsIdentity` exposes **333 group entries, every one a SID**,
surfaced as `groupsid` claims (`http://schemas.microsoft.com/ws/2008/06/identity/claims/groupsid`).
The app converts those to names to compare against stored names. Storing SIDs **removes**
a translation from the authorization path rather than adding one.

### Which comparison path actually runs -- this constrains the design

The handler has two paths. Prod log evidence for 2026-08-03:

```
"authorized via a section-access group claim"  (GroupMembershipChecker, ClaimTypes.Role):     0
"authorized via group <x>"                     (user.IsInRole, handler:101):               1687
```

`ClaimTypes.Role` is **never populated under Negotiate** on this deployment -- the app's own
debug line `User <x> has role claims:` is empty on every request. Every real authorization
today goes through `user.IsInRole(...)`.

Two consequences an implementer must not miss:

1. **`GroupMembershipChecker` is dead in the live path** but is NOT dead code: the bulk job
   runner's off-circuit re-check (`JobAuthorizationSnapshot`) depends on it, because a job
   worker has no live principal. Changing only the handler leaves the job runner comparing
   names against SIDs.
2. **`user.IsInRole()` accepts a SID string.** `WindowsPrincipal.IsInRole` resolves a
   `S-1-5-21-...` argument against the token's SIDs directly, so the migration does not
   require rewriting the comparison -- it requires the *stored value* to be a SID and the
   name-stripping normalization to be deleted.

## Owner Decisions

### D1 -- Unambiguous storage, friendly display (RULED: GO, 2026-08-03)

**Owner ruling, verbatim:** "the existing groups need to be validated, they're all in
ad.analog.com, and need to be stored in canonical or some other unambiguous format", and
"displayname in UI, SID for actual lookups".

Explicitly rejected by the owner, recorded so neither is revived:
- **Reject unqualified entries on load** -- "should neither be rejected and break the whole
  fucking app".
- **Validate new input only, grandfather the existing rows** -- "nor should we only handle
  future changes".

The data is migrated. `DOMAIN\name` was considered and is insufficient: it still breaks on
a group rename, so it is not *unambiguous*, only *less ambiguous*.

### D2 -- WITHDRAWN: not a decision (2026-08-03)

Drafted as "what should the migration do with a row that resolves to zero or multiple
groups", on the strength of one row that appeared unresolvable. The owner declined to
generalize from a single instance -- "if it's only one, and you've verified that it's only
one, then fix that one specifically" -- and the challenge was correct: the row resolved
once the probe was fixed, so the class it was meant to govern is empty.

`$KOO300-S3AMUVVBVMI1` is a **sAMAccountName**, not a group name. The group's `cn` is
`Employees-All`:

```
CN=Employees-All,OU=Automated Groups,OU=Recipients,OU=Analog,OU=Exchange,DC=ad,DC=analog,DC=com
SID: S-1-5-21-8915387-325452579-1788637320-123668
```

The earlier probe used `Get-ADGroup -Filter "name -eq '$KOO300-...'"`, which fails twice
over: `-Filter` performs PowerShell variable expansion, so `$KOO300` was consumed as a
variable reference, and `name` would not have matched regardless because the value is a
sAMAccountName. `-LDAPFilter '(sAMAccountName=...)'` in single quotes resolves it.

**All 11 unqualified rows now resolve to exactly one group in `ad.analog.com`,** verified
2026-08-03 with SIDs captured. There is no unresolved-row class in this data.

Two requirements survive, as engineering rules rather than owner decisions:

- **The resolver must query `sAMAccountName`, `cn` and `name`,** not just one. This row
  proves stored values are not consistently any single attribute.
- **The migration still halts on a row it cannot resolve to exactly one SID**, reporting it.
  Not because such a row exists today, but because a half-migrated authorization table is
  unreasonable-about and silently dropping an access grant is an outage with no audit
  trail. This is fail-closed behavior, not policy for an observed case.

## Non-Goals

- **No change to who can access what.** Every principal authorized before the migration must
  be authorized after it, and no one else. This is a representation change.
- **No change to the two-path handler structure**, beyond making both paths compare SIDs.
  Collapsing `IsInRole` and the claims path into one is a separate question.
- **No touching the 47 already-qualified rows' semantics** -- they migrate to SIDs by the
  same lookup, but `winroot\Enterprise Admins` is a deliberate cross-domain grant and must
  survive as its winroot SID, not be dropped as "foreign".
- **No well-known SIDs.** `S-1-1-0` (Everyone), `S-1-5-32-*` (BUILTIN\*) must be refused by
  the validator: they are unambiguous but grant far more than an admin intends.
- **No Event Log dropdown change here.** That is a separate defect (hardcoded category list,
  `AdminEventLog.razor:48-66`, already missing `SelfServiceGroups`, `BlockedSenders`,
  `AccountLockoutRemediation`) and gets its own plan. The two only look related because both
  surfaced from the same question.
- **No `Security:AllowedGroups` appsettings migration.** That legacy fallback is a separate
  store with its own retirement history; leave it.

## Design

### Storage

`section_access.group_value` holds a SID string (`S-1-5-21-...`). A new sibling column
`group_display_name` holds the resolved friendly name **for display only** -- never read by
any authorization path. Stale display names are cosmetic; the SID is the identity.

An implementer must not "simplify" by storing only the name and resolving to a SID at read
time: that reintroduces the lookup this plan removes, and makes authorization depend on AD
being reachable.

### Comparison

Delete the `DOMAIN\`-stripping normalization in both places
(`GroupAuthorizationHandler:97-99`, `GroupMembershipChecker:38-40`). It exists solely to
make bare names match qualified ones, which is the defect. With SIDs stored:

- Handler: `user.IsInRole(sid)` -- `WindowsPrincipal` resolves SID strings natively.
- `GroupMembershipChecker`: compare the stored SID against `groupsid` claims, not
  `ClaimTypes.Role`. **This is a behavior fix, not a port:** the current claims path matches
  nothing on this deployment (0 of 1687 authorizations), so the job runner's off-circuit
  re-check is silently relying on `JobAuthorizationSnapshot`'s own `IsInRole` call at
  capture time. Verify that path end-to-end rather than assuming it works.

### Migration

A store migration (`ConfigStoreMigrator`, next schema version) that:

1. Reads every `section_access` row.
2. For each distinct group value, resolves to exactly one SID: a value already matching
   `^S-1-` passes through; `DOMAIN\name` resolves against that domain; a bare name resolves
   against the app's own domain. **Match on `sAMAccountName`, `cn` AND `name`** -- stored
   values are not consistently one attribute (`$KOO300-S3AMUVVBVMI1` is a sAMAccountName
   whose `cn` is `Employees-All`; a `cn`-only or `name`-only query returns nothing for it).
   Use `-LDAPFilter` with single-quoted values, never `-Filter`, which expands `$` as a
   PowerShell variable. **A `DOMAIN\name` value must be resolved against THAT domain** (`-Server`
   its DNS root, mapped from the NetBIOS name via the `CN=Partitions` crossRef objects):
   verified 2026-08-03, `Enterprise Admins` queried without `-Server` returns **0** matches, so
   treating the domain half as noise loses the winroot grant. Slice 1's `Parse` preserves it.
3. **Refuses well-known SIDs** (`S-1-1-0`, `S-1-5-32-*`) and anything resolving to 0 or 2+
   objects.
4. On any failure: **halt, roll back, log every offending row** (D2 option (a), pending the
   ruling).
5. On success: rewrite each row with the SID plus the display name, in one transaction.

The migration needs AD, which every other startup path does not. It must therefore be
**idempotent and re-runnable**, not a one-shot: if AD is unreachable at startup the app must
start with the store untouched and retry on the next start, never half-write and never
block boot.

### Admin page

`ModuleConfig.razor:552,717` and the section-access editor bind to group strings today. They
render `group_display_name` and keep the SID as the value. The existing
`ADIdentityAutocomplete` already returns a resolved directory object, so the picker supplies
both halves; free-typed text is refused, the same rule slice 2 of
`docs/ProtectedPrincipalInputValidation-Plan.md` applies to protected principals.

### Non-negotiable invariants

- No authorization decision may consult a group *name*.
- The migration is all-or-nothing per run and must never reduce anyone's access silently.
- Well-known SIDs are refused at every entry point, not just the migration.
- A stale `group_display_name` must never affect a decision.
- ASCII only in `.cs` (CI gate).

## Slices

1. **SID validation + resolution helper**, with well-known-SID refusal. Pure functions,
   fully unit-testable, no store or UI change. **DONE 2026-08-03** --
   `Authorization/SectionAccessGroupIdentity.cs`, 43 tests, non-vacuity proven per guard.
   Two additions the plan did not specify, both forced by live-AD evidence gathered while
   implementing: `Parse` **keeps** the NetBIOS domain half rather than stripping it (resolving
   `Enterprise Admins` without `-Server` returns 0 matches, so stripping loses the winroot
   grant), and a DN-shaped value is reported `Unusable` rather than split on its backslash --
   in a DN that backslash escapes a comma, which is review finding ppv-2's exact defect. All
   18 distinct prod values re-verified as resolving to exactly one group.
2. **Store migration + schema column.** Idempotent, transactional, halts on any unresolved
   row. Ships with all 11 real values as fixtures, including `$KOO300-S3AMUVVBVMI1` ->
   `S-1-5-21-8915387-325452579-1788637320-123668` (`Employees-All`), which is the case that
   proves the multi-attribute lookup is required rather than merely tidy.
3. **Comparison switch** in handler, `GroupMembershipChecker`, and `JobAuthorizationSnapshot`;
   normalization deleted. Version bumps land here.
4. **Admin page display/picker.**

## Verification

Per `.agents/repo-guidance.md`:

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`

`SectionAccessServiceTests`, `GroupAuthorizationHandlerTests`, `GroupMembershipCheckerTests`
and `JobAuthorizationSnapshotTests` all assert on **name** matching today and will need
rewriting -- treat a test that still passes unchanged as suspect.

**Non-vacuity is mandatory on the access-preservation property:** a test must prove that a
principal authorized before the migration is still authorized after, and that a same-named
foreign-domain group is now refused where it previously matched. Reverting the SID
comparison must fail that second test.

### Manual post-deploy checks

Authorization cannot be fully proven off-host. **Run on dev first -- a mistake here locks
people out of every module.**

1. Each of the 6 affected aliases: a current member still gets in.
2. `HOtis` still reaches Self-Service Groups (the `$KOO300-...` / `Employees-All` row).
3. `winroot\Enterprise Admins` still reaches DhcpAuthorization (cross-domain grant intact).
4. The admin page shows friendly names, not SIDs.
5. Adding a group by picker stores a SID; free-typed text is refused.
6. With AD unreachable at startup, the app still boots and authorization still works from
   stored SIDs.

## Open Questions

- **OQ-1. CLOSED (2026-08-03).** `$KOO300-S3AMUVVBVMI1` resolves to `Employees-All`,
  SID `S-1-5-21-8915387-325452579-1788637320-123668`. The probe, not the directory, was
  wrong; see D2.
- **OQ-2.** Whether `ClaimTypes.Role` being empty is itself a latent defect worth its own
  fix. Out of scope here; noted because slice 3 touches that code and an implementer will
  see it. The practical consequence is already captured in Design: the claims path
  authorizes nobody today, so any test asserting it works is asserting nothing.
