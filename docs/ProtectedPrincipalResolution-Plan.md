# Protected-Principal Identity Resolution via Exchange -- Plan

Status: **Draft -- awaiting owner approval.** Two open owner decisions (D3, D4) block the
slices that depend on them; D1 and D2 are the plan's core and are also unruled. No code
moves until the owner rules. Nothing here is implemented.
App version: `2.3.32` -> `2.3.33` (shared service change in `ProtectedPrincipalService`,
consumed by more than one module).
Module: `MailboxPermissions` `1.0.3` -> `1.0.4` (module behavior change: targets that
previously produced a blanket denial now resolve or produce an accurate message).
Additional module bumps depend on D3; see Versioning.
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`,
`docs/AdminModuleSpec.md`. On conflict the higher source wins.

## Problem

`Services/PermissionValidator.cs:124-131` blocks every mailbox-permission operation whose
target cannot be resolved in on-prem Active Directory, whenever any Group / OU / SamAccountName
pattern rule is configured:

```csharp
var resolved = await _protectedPrincipalService.ResolveDirectoryPrincipalAsync(targetMailbox);
if (resolved == null)
{
    _logger.LogWarning(
        "Blocking operation on {Target} - cannot resolve full identity but Group/OU/Pattern rules are configured",
        targetMailbox);
    return "Access denied: Protected-principal identity resolution is unavailable. Contact your administrator.";
}
```

Rules are configured in this deployment (prod `config/exchangeadmin.db`, table
`protected_principal`, verified 2026-07-30): 4 `user` rows and 3 `group` rows. So the
`requiresFullResolution` branch at `:116-118` is always taken.

`ResolveDirectoryPrincipalAsync` (`Services/ProtectedPrincipalService.cs:270-274`) is a legacy
wrapper that discards the status from `ResolveWithStatusAsync` (`:213-264`) and returns `null`
for both `NotFound` and `Unavailable`. The caller therefore cannot distinguish "AD answered,
no such object" from "AD could not be reached", and treats both as a hard denial.

The underlying lookup is `ResolveViaActiveDirectory` (`:276-321`), a single `Get-ADUser`:

```
(|(userPrincipalName={escaped})(mail={escaped})(sAMAccountName={escaped}))
```

Three properties of that filter drive every observed failure:

1. It queries **users only**. A mail-enabled group or distribution list can never match.
2. It ignores **`proxyAddresses`**. A mailbox addressed by any secondary SMTP alias
   cannot match.
3. It queries **on-prem AD only**. A cloud-only recipient with no synced on-prem object
   cannot match.

### Observed impact

16 occurrences across prod logs `D:\inetpub\ExchangeAdminWeb\logs\app-*.log` between
2026-06-30 and 2026-07-30, over 7 distinct targets. Classified against live AD
(verified 2026-07-30, forest `ad.analog.com`, GC `ASHBDC1`):

| Target | AD reality | Class |
| --- | --- | --- |
| `Jabil.support@analog.com` (8 hits) | No object of any class; no `proxyAddresses` match in the GC | Cloud-only |
| `sporting.tickets@analog.com` | No object of any class | Cloud-only |
| `adspstaff@analog.com` (3 hits) | Exists as `group` | Group, unreachable by `Get-ADUser` |
| `globalevents@analog.com` | Exists as `group` | Group, unreachable by `Get-ADUser` |
| `EMEALeaveOfAbsence@analog.com` | `user`, `whenCreated` 2026-07-16 | Sync timing -- blocked 2026-07-15, resolves now |
| `LayoutTechnicalConference@analog.com` | `user`, `whenCreated` 2026-07-23 | Sync timing -- blocked 2026-07-24, resolves now |
| `ADIPhils_F3_FLEX/ACTTPETechnicians@analog.com` | No object; contains `/` | Malformed input |

The two sync-timing cases self-resolved and need no code change. The malformed input needs
no code change. The remaining four are permanent failures under the current filter.

`Get-Recipient` in Exchange Online resolves all four. Owner-verified 2026-07-30:
`Get-Recipient Jabil.support@analog.com` returns `Jabil.support / UserMailbox`.

### Security defect (previously unrecorded)

The same filter gap is a **protection bypass**, not only a usability problem. Protected user
rows are stored as primary SMTP addresses. Verified against live AD, 2026-07-30, for the CEO
row `vincent.roche@analog.com`:

```
smtp:VRoche@O365.analog.com
smtp:VRoche@analog.mail.onmicrosoft.com
smtp:Vincent.Roche@exchange.analog.com
```

Running the app's exact filter against `VRoche@O365.analog.com` returns **0 matches**; adding
`(proxyAddresses=smtp:...)` to the same filter returns **1**. All four protected user rows
carry 3 secondary aliases each with the same shape.

Today that bypass is masked: 0 matches means `null` means denial, so an alias lookup is
refused rather than allowed. **The blanket denial is currently the only control preventing
alias-addressed access to protected mailboxes.** Any fix that relaxes `NotFound` to "allow"
without also broadening resolution converts this masked bypass into a live one. This
constraint is binding on the design; see Non-Goals.

`Services/ProtectedPrincipalService.cs:438-465` (`MatchesIdentity`) compares only
`UserPrincipalName`, `PrimarySmtpAddress`, `SamAccountName`, `DistinguishedName`, `ObjectGuid`,
`EntraObjectId`. Aliases are absent from the candidate set as well as from the query, so
broadening the query alone is what closes this -- resolution must return the *canonical*
identity, which the existing candidate set then matches.

### The capability already exists in the app

`Services/ExchangeIdentityResolver.cs:10-31` already performs the required Exchange lookup on
the pooled EXO session:

```csharp
ps.AddCommand("Get-Recipient")
  .AddParameter("Identity", identity)
  .AddParameter("ErrorAction", "Stop");
var results = Invoke(ps, tracker);
var recipient = results.FirstOrDefault();
return recipient?.Properties["ExternalDirectoryObjectId"]?.Value?.ToString();
```

Registered `AddScoped<IIdentityResolver, ExchangeIdentityResolver>()` (`Program.cs:173`).
`PermissionValidator.IsUserExcludedAsync` (`:81-99`) already consumes it through
`IServiceScopeFactory`. `ProtectedPrincipalService` does not consume it at all. This plan
wires the existing capability into the protection path; it does not add a new EXO dependency
to the app.

## Owner Decisions

Each decision below is unruled. Implementation proceeds only behind the rulings it has.

### D1 -- Fall back to Exchange when AD does not resolve (UNRULED)

**Problem.** On-prem AD alone cannot resolve cloud-only mailboxes, mail-enabled groups, or
alias-addressed recipients. All three are legitimate administrative targets and all three
currently produce a blanket denial that reads like a system outage.

**Change.** When AD resolution returns `NotFound`, query Exchange with `Get-Recipient`. Use the
canonical primary SMTP address it returns to re-attempt AD resolution, then evaluate the
protection rules against whichever identity was obtained.

**Cost / risk.** One additional EXO round-trip on the AD-miss path only (measured EXO calls in
these logs: 0.1-4s). Successful AD resolutions are unaffected. Introduces a dependency on EXO
availability for a path that currently depends only on AD -- mitigated by D2's fail-closed rule.

### D2 -- Exchange unavailable still denies (UNRULED)

**Problem.** Adding a second directory adds a second thing that can be down.

**Change.** `Unavailable` from either directory continues to deny. Only an affirmative
"this recipient does not exist" from Exchange, or an affirmative resolution, changes the
outcome. The denial message distinguishes the three cases:

| Situation | Message |
| --- | --- |
| Neither directory can be reached | `Access denied: Protected-principal identity resolution is unavailable. Contact your administrator.` (unchanged) |
| Both answered; no such recipient | `Access denied: '<target>' was not found in Active Directory or Exchange Online. Check the address.` |
| Ambiguous | existing ambiguous message, unchanged |

**Cost / risk.** None to the security posture -- this preserves Known Failure Class #3
(fail-closed authorization) exactly. The gain is diagnostic: an operator learns whether to fix
their typing or raise a ticket.

### D3 -- Scope: MailboxPermissions only, or all three gated modules (UNRULED)

**Problem.** `ConferenceRoomProtectionGate.EvaluateAsync`
(`Services/ConferenceRoomProtectionGate.cs:60-92`) and `GroupManagementService.CheckProtectedAsync`
(`Services/GroupManagementService.cs:36-68`) already treat `NotFound` as **not protected** and
allow the operation (documented at `ConferenceRoomProtectionGate.cs:56-58` as an accepted
limitation). Combined with the alias gap above, those two modules have a **live** bypass today,
not a masked one: a protected principal addressed by secondary alias resolves `NotFound` and is
allowed through.

**Change.** Option A: fix `ProtectedPrincipalService` only, and let all three modules inherit
the improvement (they share `ResolveWithStatusAsync`). Option B: Option A plus an explicit
review of the `NotFound`-allows rule in those two modules.

**Cost / risk.** Option A closes the alias bypass in all three modules automatically, because
resolution now returns the canonical identity instead of `NotFound`. It leaves the
`NotFound`-allows rule itself unexamined for genuinely-cloud-only conference rooms and group
members. Option B is a larger diff touching three modules and three version bumps.

**Recommendation: Option A**, with the `NotFound`-allows rule recorded in `.agents/state.md`
as a separate open item. Rationale: Option A removes the exploitable path with the smallest
diff; the residual question is about cloud-only objects, which is exactly what D4 settles and
should be settled once rather than per-module.

### D4 -- What happens to a confirmed cloud-only recipient (UNRULED)

**Problem.** After D1, a recipient like `Jabil.support@analog.com` is confirmed by Exchange to
exist with no on-prem AD object. It can be checked against the 4 protected **user** rows by
address, but it cannot be checked against the 3 protected **group** rows -- group membership is
evaluated by on-prem DN via `memberOf` chasing (`ProtectedPrincipalService.cs:554-631`,
`CheckTransitiveGroupMembership`), and a cloud-only object has no DN. `:587-588` returns no
matches when `targetDn` is empty.

**Change.** Option A: allow the operation once the address clears the user rows and the
SamAccountName patterns, accepting that group rules cannot apply to an object that cannot be a
member of an on-prem group. Option B: continue to deny, with the accurate message from D2.

**Cost / risk.** Option A is the only option that makes `Jabil.support` and `sporting.tickets`
manageable -- the L1/L2 friction that prompted this plan. Its exposure: a cloud-only mailbox
that *should* be protected must be listed by address, because group rules cannot reach it. All
4 current protected users are synced on-prem accounts with DNs, so no currently-protected
principal relies on group rules being applied to a cloud-only object. Option B keeps today's
denial for exactly the cases the owner reported as friction.

**Recommendation: Option A.** The exposure is bounded and already accepted elsewhere in the
app (`ConferenceRoomProtectionGate.cs:56-58` documents the same trade-off), and Option B
delivers no relief for the reported problem.

**If D4 is ruled Option A**, the implementer must add the protected-groups-do-not-apply
limitation to `docs/ProjectConstitution.md`'s protected-principal section as a documented,
owner-accepted boundary -- not leave it implicit in code.

## Non-Goals

- **No relaxation of `NotFound` to "allow" without broadened resolution.** These must land
  together or not at all; separating them converts the masked alias bypass into a live one.
  See Problem / Security defect.
- No change to `ResolveViaActiveDirectory`'s LDAP filter to add `proxyAddresses`. Exchange
  normalizes aliases to the primary address, which makes the AD-side alias search redundant.
  Adding both would be two mechanisms for one job.
- No change to authentication, to the Protected Principals admin UI, or to the shape of the
  `protected_principal` table.
- No new module, no new page, no `ModuleCatalog` entry.
- No caching of Exchange resolution results beyond the existing 30-second config cache.
- No attempt to make on-prem group rules apply to cloud-only objects (see D4). Chasing cloud
  group membership requires Microsoft Graph and is a materially larger change.
- No change to `PermissionValidator.IsUserExcludedAsync`'s existing `IIdentityResolver` use.

## Design

Behind D1 + D2. Slice 3 additionally behind D4.

### Extend `ResolutionStatus` consumption, not the enum

`Services/ProtectedPrincipalService.cs:204` already defines
`enum ResolutionStatus { Resolved, NotFound, Ambiguous, Unavailable }`. The enum is
sufficient; the defect is that `ResolveDirectoryPrincipalAsync` (`:270-274`) discards it.

Add a new resolution entry point rather than changing `ResolveWithStatusAsync`'s contract --
that method is `virtual` and is overridden by test fakes at
`ExchangeAdminWeb.Tests/ConferenceRoomProtectionGateTests.cs:45-52` and
`ExchangeAdminWeb.Tests/ConferenceRoomBulkProcessorTests.cs:65-72`. Changing its signature
breaks both.

```csharp
// New, in ProtectedPrincipalService. Virtual for the same test-seam reason as
// ResolveWithStatusAsync.
public virtual async Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)>
    ResolveWithExchangeFallbackAsync(string identity)
```

Sequence:

1. `var (principal, status) = await ResolveWithStatusAsync(identity);`
2. If `status != NotFound`, return unchanged. (`Resolved`, `Ambiguous`, `Unavailable` all
   keep today's behavior exactly. This is the fail-closed guarantee.)
3. Resolve `IIdentityResolver` from a DI scope and ask Exchange for the recipient.
4. Exchange unreachable or threw: return `(null, Unavailable)`. **Not** `NotFound` -- an
   unreachable directory must never present as an affirmative absence.
5. Exchange returned nothing: return `(null, NotFound)` -- now an *affirmative* absence,
   confirmed by both directories.
6. Exchange returned a recipient whose primary SMTP differs from `identity` (the alias case):
   re-run `ResolveWithStatusAsync` with the canonical address and return that result. This is
   what closes the alias bypass -- a synced user reached by alias now resolves to a full
   principal with a DN, and group / OU / pattern rules apply normally.
7. Exchange returned a recipient with no on-prem object (the cloud-only case): construct a
   `ResolvedDirectoryPrincipal` with `Source: "ProtectedPrincipalService-EXO"`,
   `PrimarySmtpAddress` and `UserPrincipalName` set to the canonical address,
   `DistinguishedName: null`, `EntraObjectId` from `ExternalDirectoryObjectId`. Return
   `(principal, Resolved)`.

Step 7's principal has a null DN. `CheckOuMatches` (`:485-496`) already returns early on a
null DN, and `CheckTransitiveGroupMembership` (`:587-588`) already returns no matches on an
empty `targetDn`. Both degrade correctly without modification -- but **silently**, which is
precisely the D4 exposure. Do not rely on that silence: step 7 must log at
`LogInformation` naming the identity and stating that group and OU rules were not evaluated.

### `IIdentityResolver` needs more than the object ID

`ExchangeIdentityResolver.ResolveToObjectIdAsync` returns only
`ExternalDirectoryObjectId`. Steps 5-7 need the primary SMTP address and enough to tell
"cloud-only" from "synced", so add a second method to the interface rather than overloading
the existing one:

```csharp
// Services/IIdentityResolver.cs
Task<ResolvedRecipient?> ResolveRecipientAsync(string identity);

public sealed record ResolvedRecipient(
    string PrimarySmtpAddress,
    string? ExternalDirectoryObjectId,
    string? RecipientType,          // Get-Recipient RecipientType
    bool ExistsOnPrem);             // true when Get-Recipient reports an on-prem-backed object
```

`ExistsOnPrem` is derived from the recipient's `RecipientTypeDetails` / `IsDirSynced`
properties as returned by `Get-Recipient`. The implementer must confirm which property is
authoritative against a live EXO session before relying on it, and must treat "cannot
determine" as `false` (forcing the step 7 cloud-only path, which is the more conservative
branch because it cannot match group rules and therefore cannot silently un-protect).

Distinguish "returned nothing" from "call failed": `ExchangeIdentityResolver`'s existing
`catch` (`:26-30`) collapses both to `null`. `ResolveRecipientAsync` must not repeat that --
it needs to throw or signal failure distinctly so step 4 and step 5 stay separable. A caught
exception that becomes `null` would make an EXO outage look like an affirmative absence and
would allow the operation under D4/Option A. **This is the single most dangerous line in the
change; get it right.**

### Caller change

`Services/PermissionValidator.cs:124-131` becomes:

```csharp
var (resolved, status) = await _protectedPrincipalService.ResolveWithExchangeFallbackAsync(targetMailbox);
if (status == ResolutionStatus.Unavailable || status == ResolutionStatus.Ambiguous)
    return /* existing unavailable / ambiguous message */;
if (status == ResolutionStatus.NotFound)
    return $"Access denied: '{targetMailbox}' was not found in Active Directory or Exchange Online. Check the address.";
principal = resolved!;
```

`ConferenceRoomProtectionGate` and `GroupManagementService` inherit the fix by switching their
`ResolveWithStatusAsync` call to `ResolveWithExchangeFallbackAsync`. Under D3/Option A their
`NotFound`-allows rule is otherwise untouched.

### Mail-enabled groups

`Get-Recipient` returns groups, so `adspstaff@analog.com` and `globalevents@analog.com` resolve
at step 6 or 7. No group-specific code path is needed. The implementer must verify that a
resolved group principal flows through `CheckAsync` without error -- `MatchesIdentity` compares
strings and does not assume a user object.

### Non-negotiable invariants

- No path may return `NotFound` when a directory was unreachable.
- No path may allow an operation that today denies it, except via the D4-ruled cloud-only
  branch.
- Every new denial path logs at `LogWarning` with the target identity, as the current code does.
- ASCII only in all `.cs` changes (CI gate).

## Slices

Each slice is one commit, verified before the next begins.

1. **`ResolvedRecipient` + `IIdentityResolver.ResolveRecipientAsync`.** Implementation in
   `ExchangeIdentityResolver` with the failure/absence distinction of the Design section.
   Tests: absence returns a null result; EXO failure surfaces as failure, not absence.
2. **`ProtectedPrincipalService.ResolveWithExchangeFallbackAsync`.** Steps 1-6 only --
   Resolved / Ambiguous / Unavailable pass through untouched; alias re-resolution works;
   confirmed absence returns `NotFound`. Cloud-only (step 7) is **not** in this slice; it
   returns `NotFound` here. Tests cover each status transition with a substituted
   `IIdentityResolver`.
3. **Cloud-only branch (step 7).** Behind D4/Option A only. If D4 is ruled Option B, this
   slice is dropped and step 7 keeps returning `NotFound`.
4. **Caller switch + messages.** `PermissionValidator`, and under D3/Option A the two other
   gated modules. Version bumps land here.

Slices 1, 2 and 4 are implementable under D1 + D2 alone.

## Verification

Per `.agents/repo-guidance.md`:

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`

New tests are required in `ExchangeAdminWeb.Tests/` (new/rewritten Services rule). Existing
`ProtectedPrincipalServiceTests.cs` and `PermissionValidatorTests.cs` are the homes.

**Non-vacuity proof is mandatory for the fail-closed tests.** For the EXO-unreachable test
specifically: revert step 4 to return `NotFound`, confirm the test fails, restore, confirm
green. A fail-closed test that passes with the fix reverted is worthless and this is the
exact defect class the plan exists to prevent.

### Manual post-deploy checks

Automated tests cannot cover live directory behavior. On dev, after deploy:

1. `Jabil.support@analog.com` as target -- resolves, or gives the accurate not-found message
   (which it is depends on D4).
2. `adspstaff@analog.com` as target -- resolves as a group, operation proceeds to the EXO call.
3. `VRoche@O365.analog.com` as target -- **must be denied as a protected principal**, citing
   the CEO user rule. This is the alias-bypass regression test and is the most important
   check in this list. It must be re-run on prod after promotion.
4. A normal synced mailbox by primary address -- unchanged behavior, no added latency.
5. A deliberately malformed address -- accurate not-found message, no "contact your
   administrator".
6. With EXO credentials deliberately wrong (dev only) -- an AD-miss target still **denies**,
   with the unavailable message.

## Versioning

Per `docs/ProjectConstitution.md` (Deployment And Versioning), both rules fire:

- App `2.3.32` -> `2.3.33` in `ExchangeAdminWeb.csproj` (`VersionPrefix`, `AssemblyVersion`,
  `FileVersion`): `ProtectedPrincipalService` and `IIdentityResolver` are shared.
- `MailboxPermissions` `1.0.3` -> `1.0.4` in `Modules/ModuleCatalog.cs`.
- Under D3/Option A, `ConferenceRooms` `2.3.0` -> `2.3.1` and `GroupManagement` `2.1.0` ->
  `2.1.1` as well, since their observable denial behavior changes.

## Open Questions

- **OQ-1.** Which `Get-Recipient` property authoritatively distinguishes a synced from a
  cloud-only recipient in this tenant. Must be settled against a live session during slice 1.
  Blocks slice 3 only.
- **OQ-2.** Whether `sporting.tickets@analog.com` and `Jabil.support@analog.com` are mailboxes
  that *should* be administratively reachable at all, or artifacts of a decommission that was
  never finished. Not a blocker -- the plan makes them resolvable either way -- but worth an
  answer before treating the L1/L2 friction as fully closed.
- **OQ-3.** The `NotFound`-allows rule in `ConferenceRoomProtectionGate` and
  `GroupManagementService` (see D3). Recorded in `.agents/state.md` as an open item under
  D3/Option A.
