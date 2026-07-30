# Protected-Principal Identity Resolution via Exchange -- Plan

Status: **Approved (owner, 2026-07-30).** D1 ruled go. D2 and D4 were withdrawn as decisions --
both dissolved on owner challenge into consequences of D1 rather than choices; see their
sections for the reasoning, which is load-bearing for the design. D3 withdrawn as a decision:
the owner ruled "anywhere it's broken it needs to be fixed", so all three gated modules are in
scope unconditionally. No open owner gates. Not yet implemented.
App version: `2.3.32` -> `2.3.33` (shared service change in `ProtectedPrincipalService`,
consumed by more than one module).
Module: `MailboxPermissions` `1.0.3` -> `1.0.4` (module behavior change: targets that
previously produced a blanket denial now resolve or produce an accurate message).
`ConferenceRooms` `2.3.0` -> `2.3.1` and `GroupManagement` `2.1.0` -> `2.1.1`; see Versioning.
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

All settled 2026-07-30. D1 ruled; D2, D3 and D4 withdrawn as decisions on owner challenge.
The withdrawal reasoning is recorded because it constrains the design -- an implementer who
revives one of these as an open question has misread the code.

### D1 -- Fall back to Exchange Online when AD does not resolve (RULED: GO)

**Problem.** On-prem AD alone cannot resolve cloud-only mailboxes, mail-enabled groups, or
alias-addressed recipients. All three are legitimate administrative targets and all three
currently produce a blanket denial that reads like a system outage.

**Change.** When AD resolution returns `NotFound`, query Exchange with `Get-Recipient`. Use the
canonical primary SMTP address it returns to re-attempt AD resolution, then evaluate the
protection rules against whichever identity was obtained.

**Cost / risk.** One additional EXO round-trip on the AD-miss path only (measured EXO calls in
these logs: 0.1-4s). Successful AD resolutions are unaffected. Introduces a dependency on EXO
availability for a path that currently depends only on AD -- mitigated by D2's fail-closed rule.

### D2 -- EXO unavailable still denies (WITHDRAWN: not a decision)

Drafted as a policy choice. The owner challenged it and the challenge is correct: there is no
deployment state in which the choice is observable.

- **MailboxPermissions** writes through the same `ExoConnectionPool` the fallback lookup uses
  (`Services/MailboxPermissionService.cs:44`, `:63`, `:343`). If EXO is unreachable the write
  fails regardless of what the gate decides. Allow and deny are indistinguishable to the
  operator.
- **GroupManagement** never touches EXO -- it writes on-prem via `Add-ADGroupMember`
  (`Services/GroupManagementService.cs:251`). EXO reachability is irrelevant to whether its
  write succeeds. And per D4 the fallback cannot produce an addable member for this module
  anyway.

What survives is a **message** requirement, not a policy: `Unavailable` continues to deny (that
is unchanged current behavior, preserving Known Failure Class #3), and the message must name
which directory could not be reached. Implementers: do not re-open this as an allow/deny
question.

| Situation | Message |
| --- | --- |
| AD unreachable | `Access denied: Protected-principal identity resolution is unavailable (Active Directory unreachable). Contact your administrator.` |
| AD missed, EXO unreachable | `Access denied: Protected-principal identity resolution is unavailable (Exchange Online unreachable). Contact your administrator.` |
| Both answered; no such recipient | `Access denied: '<target>' was not found in Active Directory or Exchange Online. Check the address.` |
| Ambiguous | existing ambiguous message, unchanged |

### D3 -- Scope (WITHDRAWN: ruled unconditionally)

Drafted as a choice between fixing `ProtectedPrincipalService` alone versus also reviewing the
`NotFound`-allows rule in the two other gated modules. Owner ruling, 2026-07-30: **"anywhere
it's broken it needs to be fixed"** -- no per-module scoping.

All three gated modules switch to the new resolution entry point:

- `Services/PermissionValidator.cs:124-131` (MailboxPermissions)
- `Services/ConferenceRoomProtectionGate.cs:60-92` (ConferenceRooms)
- `Services/GroupManagementService.cs:36-68` (GroupManagement)

The last two treat `NotFound` as **not protected** and allow (documented at
`ConferenceRoomProtectionGate.cs:56-58` as an accepted limitation). Combined with the alias gap
in Problem, that is a **live** bypass today, not a masked one: a protected principal addressed
by secondary alias resolves `NotFound` and is allowed through. The shared fix closes it in all
three, because resolution now returns the canonical identity instead of `NotFound`.

### D4 -- Confirmed cloud-only recipient (WITHDRAWN: not a decision)

Drafted as allow-vs-deny for a recipient that Exchange confirms exists with no on-prem AD
object. The owner challenged the framing -- "we cannot add Exchange Online only mailboxes to
on-prem groups, they don't exist in the on-prem directory" -- and the code agrees. The branch
is reachable in exactly one module, and there it relaxes nothing.

**GroupManagement: unreachable.** `AddMemberAsync` resolves the member with `Get-ADUser` and
throws `User '<member>' not found in AD.` at `Services/GroupManagementService.cs:246-247`
before `Add-ADGroupMember` at `:251`, which needs an on-prem DN
(`:253`). A cloud-only mailbox cannot be added to an on-prem group by any path. The protection
gate's answer for such a member never affects an outcome.

**ConferenceRooms: not applicable in practice.** Room mailboxes targeted by that module are
on-prem-backed; a cloud-only room hitting this branch is not a case the module serves.

**MailboxPermissions: the only live case, and nothing is relaxed.** The target is an EXO
mailbox and the write is `Add-MailboxPermission` against EXO. On-prem group rules were never
applicable to such a target -- not "applicable but skipped". Today it is denied by an accident
of the AD-only lookup, not by a protection decision.

So the behavior is a consequence of D1, not a policy choice: a confirmed cloud-only recipient
is checked against the protected **user** rows and the SamAccountName patterns, and allowed if
it clears them. Group rules cannot apply to an object that cannot be a member of an on-prem
group -- `CheckTransitiveGroupMembership` (`ProtectedPrincipalService.cs:554-631`) evaluates
membership by on-prem DN and `:587-588` returns no matches when `targetDn` is empty.

**Still required of the implementer:** record the protected-groups-cannot-reach-cloud-only
boundary in `docs/ProjectConstitution.md`'s protected-principal section as a documented
limitation. It is a true constraint on how protection can be configured -- a cloud-only mailbox
must be protected by address, never by group membership -- and it must not stay implicit in
code. Verified 2026-07-30: all 4 current protected `user` rows are synced on-prem accounts with
DNs, so no currently-protected principal depends on group rules reaching a cloud-only object.

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

Fully approved; no part of this section waits on a ruling.

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
would allow the operation through the cloud-only branch. **This is the single most dangerous
line in the change; get it right.**

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

`ConferenceRoomProtectionGate` (`:64`) and `GroupManagementService` (`:43`) inherit the fix by
switching their `ResolveWithStatusAsync` call to `ResolveWithExchangeFallbackAsync`. Their
`NotFound`-allows rule is otherwise untouched -- the fix works by making the alias case stop
returning `NotFound`, not by changing what `NotFound` means.

### Mail-enabled groups

`Get-Recipient` returns groups, so `adspstaff@analog.com` and `globalevents@analog.com` resolve
at step 6 or 7. No group-specific code path is needed. The implementer must verify that a
resolved group principal flows through `CheckAsync` without error -- `MatchesIdentity` compares
strings and does not assume a user object.

### Non-negotiable invariants

- No path may return `NotFound` when a directory was unreachable.
- No path may allow an operation that today denies it, except the cloud-only branch described
  under D4 -- and that branch must still clear the protected user rows and patterns.
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
3. **Cloud-only branch (step 7)**, plus the Constitution note required by D4.
4. **Caller switch + messages.** All three gated modules. Version bumps land here.

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

1. `Jabil.support@analog.com` as target -- resolves and the operation proceeds.
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
- `ConferenceRooms` `2.3.0` -> `2.3.1` and `GroupManagement` `2.1.0` -> `2.1.1`, since their
  observable denial behavior changes (D3).

## Open Questions

- **OQ-1.** Which `Get-Recipient` property authoritatively distinguishes a synced from a
  cloud-only recipient in this tenant. Must be settled against a live session during slice 1.
  Blocks slice 3 only.
- **OQ-2.** Whether `sporting.tickets@analog.com` and `Jabil.support@analog.com` are mailboxes
  that *should* be administratively reachable at all, or artifacts of a decommission that was
  never finished. Not a blocker -- the plan makes them resolvable either way -- but worth an
  answer before treating the L1/L2 friction as fully closed.
- **OQ-3.** CLOSED by the D3 ruling (2026-07-30): all three gated modules are in scope. The
  `NotFound`-allows rule in `ConferenceRoomProtectionGate` and `GroupManagementService` stays
  as written; the fix removes the alias case from reaching it. No separate follow-up item.
