# Protected-Principal Admin Input Validation -- Plan

Status: **Approved (owner, 2026-07-31).** D1 (refuse on AD unreachable) and D2 (validate under
the app-pool identity, not the Delinea directory-read secret) both ruled. No open owner gates.
Not yet implemented.
App version: `2.3.33` -> `2.3.34` (shared `ADDirectorySearchService` gains a new method).
Module: `AdminSettings` `1.0.1` -> `1.0.2` (the page's observable behavior changes).
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`,
`docs/AdminModuleSpec.md`. On conflict the higher source wins.

## Problem

`Components/Pages/AdminSettings.razor` saves protected-principal entries that do not correspond
to any Active Directory object. The four add-handlers (`:394-397`) trim the input, check it is
non-blank and not a duplicate, and append it to the list:

```csharp
private void AddPpUser() { var v = ppNewUser.Trim(); if (!string.IsNullOrWhiteSpace(v) && !ppUsers.Contains(v, StringComparer.OrdinalIgnoreCase)) ppUsers.Add(v); ppNewUser = ""; }
private void AddPpGroup() { var v = ppNewGroup.Trim(); if (!string.IsNullOrWhiteSpace(v) && !ppGroups.Contains(v, StringComparer.OrdinalIgnoreCase)) ppGroups.Add(v); ppNewGroup = ""; }
```

`SaveProtectedPrincipals` (`:399-420`) then filters blanks and calls
`ProtectedPrincipalService.SaveConfig`. No AD lookup occurs at any point.

Users and Groups already render `ADIdentityAutocomplete` (`:127`, `:149`), so a suggestion
dropdown exists -- but it only *suggests*. The component writes every keystroke back through
`ValueChanged` (`Components/Shared/ADIdentityAutocomplete.razor:70-72`), so the bound value is
whatever was typed, whether or not a suggestion was selected. Typing a name and clicking Add
saves the raw string. OUs (`:171`) and sAMAccountName patterns (`:193`) are plain
`<input type="text">` with no picker at all.

### Why an unresolvable entry is worse than a rejected one

A saved entry that matches no AD object is not inert -- it reads as configured protection while
protecting nobody.

- **Users.** `CheckDirectUserMatches` (`Services/ProtectedPrincipalService.cs:545-552`) compares
  the stored string against the resolved target's identity fields. A typo matches nothing, every
  time, silently.
- **OUs.** `CheckOuMatches` (`:580-591`) is a DN suffix comparison. A malformed or non-existent
  DN suffix-matches nothing, silently.
- **Groups.** Distinct behavior worth stating precisely, because it is the one case that is
  *not* silent. `ResolveProtectedGroupDn` (`:749-789`) tries `Get-ADGroup -Identity`, then an
  LDAP fallback on `cn`/`sAMAccountName`/`name`. An unresolvable group returns null, and
  `CheckTransitiveGroupMembership` (`:690-695`) logs a warning and sets `expansionHadErrors`.
  `CheckGroupMembershipAsync` (`:629-633`) then fails **closed** when no other rule matched.
  So a bad group entry does not un-protect anyone -- it converts every check into a denial that
  reads as a directory fault. That is the L1/L2 friction class this repo just spent four slices
  removing in `docs/ProtectedPrincipalResolution-Plan.md`.

Either way the operator gets no feedback at configuration time, when the mistake is cheap to
fix. They discover it later, either as a protection that never fired or as denials that look
like an outage.

### Cloud-only entries are out of scope by ruling, not by omission

Owner ruling 2026-07-31: "Anything that exists only in O365, currently, should be considered
non-protected." This is consistent with `docs/ProjectConstitution.md` (Protected Principals),
which already records that a cloud-only principal must be protected by address and can never be
matched by a group rule. Validation against on-prem AD is therefore the *correct* gate, not a
limitation to work around: an Entra-only group is exactly the input that should be refused.
Do not add Microsoft Graph lookups to satisfy this plan.

## Owner Decisions

Both settled 2026-07-31.

### D1 -- AD unreachable refuses the entry (RULED: GO)

**Problem.** Validation requires a directory call, which can fail. Refusing on failure means an
AD outage blocks editing the protection list; allowing on failure means an outage silently
reintroduces the unvalidated-input defect this plan exists to close.

**Owner ruling, verbatim (2026-07-31):** "AD failures are always transient, so the message is
'AD unreachable, try again later'. this is an admin-only page, so we are the right people to
tell that to. refuse until AD is up. nothing this tool does works without AD anyway."

**Change.** A lookup that cannot run refuses the Add and shows a message naming AD as the cause
and directing the operator to retry. It must never present as "no such object" -- see the
absence/failure split below, which is the same distinction
`docs/ProtectedPrincipalResolution-Plan.md` established for the resolution path.

**Cost / risk.** The protection list cannot be edited while AD is down. Accepted: the ruling
notes the app is non-functional in that state regardless, and the audience is administrators.

### D2 -- Validate under the app-pool identity, not the Delinea secret (RULED: GO)

**Problem.** Two credentials could perform the lookup: the app pool's ambient Windows identity
(what `ADDirectorySearchService` already uses) or the protected-principal directory-read secret
from Delinea (what enforcement uses). The drafted recommendation was the Delinea secret, on the
grounds that only the enforcement credential proves the rule will work at enforcement time.

**Owner ruling, verbatim (2026-07-31):** "anonymous ldap lookups are allowed and working here.
any authenticated user will also be able to look up any user/group. at present, in this
environment, this is a non-issue. the delinea stored secret has vastly more permissions than the
app pool, and we need to use it only when necessary."

**Change.** Validation uses `ADDirectorySearchService` (app-pool ambient identity, no secret).
The drafted concern -- that the two credentials could disagree and let an entry validate clean
yet fail at enforcement -- is answered by the environment: read access to user and group objects
is not differentiated here, so the divergence the recommendation guarded against does not exist
in this deployment.

**Cost / risk.** Least privilege is the governing principle: the high-permission secret is used
only where its permissions are required, and a read-only existence check does not require them.
This also keeps the change inside `docs/ProjectConstitution.md`'s Credential Isolation carve-out
for "an operation that is explicitly read-only and approved for ambient Windows identity".

**Standing constraint this creates.** The ruling is scoped to *this environment*
("at present, in this environment"). If a future deployment restricts directory read access such
that the app pool and the directory-read secret see different objects, this decision must be
revisited -- an entry could then validate clean and fail at enforcement. Record this in the
decisions log so the assumption is not silently inherited.

### Not decisions -- folded in on owner acknowledgement

- **OUs gain a picker.** Currently raw text (`:171`); a mistyped OU protects nobody just as
  silently as a mistyped user. Same treatment as Users and Groups.
- **Existing saved entries are re-checked and flagged.** Validating new input says nothing about
  the rows already in the store. Prod currently holds 4 `user` and 3 `group` rows
  (`docs/ProtectedPrincipalResolution-Plan.md`, verified 2026-07-30).

## Non-Goals

- **No Microsoft Graph / Entra lookups.** A cloud-only object is non-protected by ruling; the
  input should refuse it. Adding Graph would contradict D2's least-privilege direction and the
  Constitution's cloud-only boundary.
- **No validation of sAMAccountName patterns.** They are wildcards (`adm-*`), matched by
  `MatchesWildcardPattern` (`:574-578`) against a resolved target at check time. There is no
  object for a pattern to correspond to. Leave the plain text input; do not invent a
  "does this pattern match at least one account today" check -- a pattern matching nothing now
  is legitimate (it may exist to catch future accounts).
- **No change to the protection engine.** `CheckAsync`, `ResolveWithStatusAsync`,
  `ResolveWithExchangeFallbackAsync`, the LDAP filters, and the fail-closed group behavior are
  all untouched. This plan changes what can enter the store, not how the store is evaluated.
- **No change to the `protected_principal` table shape** or to `ProtectedPrincipalConfig`.
- **No change to `ADIdentityAutocomplete`'s existing behavior** for its other consumers
  (`ModuleConfig.razor:163`, `:457`, `SelfServiceGroups.razor`). Any new capability is additive
  and opt-in by parameter.
- **No blocking of Save on stale existing entries.** Flagging is informational; an operator must
  still be able to save (e.g. to remove the stale row). Blocking Save on data the operator did
  not just enter would make a decommissioned group unremovable.
- **No re-validation on load of every page render.** One check per explicit action.

## Design

Fully approved; no part of this section waits on a ruling.

### The absence/failure split is the load-bearing property

`ADDirectorySearchService.Search` is **fail-soft**: every failure path returns an empty list.
Unavailable returns `[]` (`:75-76`), throttle timeout returns `[]` (`:80-84`), and the outer
catch returns `[]` (`:103-107`). Term shorter than 3 characters also returns `[]` (`:69-70`).

An empty list therefore cannot distinguish "AD says no such object" from "the lookup did not
run". D1 requires exactly that distinction: the first refuses with "not found", the second
refuses with "AD unreachable, try again later". **Reusing `Search` for validation would collapse
both into "not found" and tell the operator their correct entry was a typo during an outage.**

This is the same defect class `docs/ProtectedPrincipalResolution-Plan.md` addressed for
`ResolveRecipientAsync`, and it is settled the same way: a dedicated method whose failures
propagate rather than flattening to a "not found" answer.

### New method on `ADDirectorySearchService`

```csharp
public enum DirectoryLookupOutcome { Found, NotFound, Unavailable }

public sealed record DirectoryValidationResult(
    DirectoryLookupOutcome Outcome,
    ADSearchResult? Match);

/// Exact-match existence check for admin input validation. Unlike Search, distinguishes an
/// affirmative absence from a failed lookup.
public DirectoryValidationResult ValidateExists(string identity, string objectKind);
```

Rules:

1. `IsAvailable == false` -> `Unavailable`. (Never `NotFound`.)
2. Throttle-lock timeout -> `Unavailable`.
3. Any exception from the directory call -> `Unavailable`, logged at `LogWarning`.
4. The call ran and returned zero objects -> `NotFound`.
5. The call ran and returned one or more -> `Found`, carrying the first match.

Follow the runspace discipline the existing methods use: acquire `_runspaceLock` with the same
30s timeout, and on exception dispose `_searchRunspace` and null it before rethrowing
(`:90-97`, `:144-151`).

### Exact match, not the autocomplete's wildcard search

`Search` builds a substring filter (`:239`, `:271`):

```
(|(displayName=*{escaped}*)(sAMAccountName=*{escaped}*)(userPrincipalName=*{escaped}*)(mail=*{escaped}*))
```

That is wrong for validation in two directions: `jdoe` matches `jdoe2`, and a 2-character entry
returns `[]` from the length guard rather than a real answer. `FindUserBySid`
(`:110-123`) already documents this exact reasoning -- its doc comment rejects reusing `Search`
as an identity oracle because a wildcard query "can return a confidently wrong user". Apply the
same judgement here.

`ValidateExists` uses exact-match filters, with LDAP metacharacters escaped via the existing
`ProtectedPrincipalService.EscapeLdapFilter` (already used at `:230`):

- **User** -- `(|(userPrincipalName={escaped})(mail={escaped})(sAMAccountName={escaped}))`.
  Deliberately mirrors `ResolveViaActiveDirectory`'s filter
  (`ProtectedPrincipalService.cs:291`) so validation accepts exactly the identity forms
  enforcement resolves. Do not add `proxyAddresses`: `docs/ProtectedPrincipalResolution-Plan.md`
  Non-Goals rejects broadening the AD filter, and Exchange already normalizes aliases.
  Note the page's own hint text (`:111`) also offers `DOMAIN\username`; strip the domain prefix
  before the lookup, the way `ResolveProtectedGroupDn` does for groups (`:757-759`).
- **Group** -- `Get-ADGroup` with `(|(distinguishedName={escaped})(cn={escaped})(sAMAccountName={escaped})(name={escaped}))`,
  matching the three formats `MatchesDnToProtectedGroup` (`:798-830`) supports and the fallback
  filter `ResolveProtectedGroupDn` uses (`:782`).
- **OU** -- `Get-ADOrganizationalUnit` with `(distinguishedName={escaped})`. `CheckOuMatches`
  (`:580-591`) does a DN suffix comparison, so only a real DN is meaningful.

**Ambiguity is not an error here.** Unlike resolution -- where two matches must fail closed
because the app must pick one object to act on -- validation only asks "does this correspond to
something real". Multiple matches still answer yes. Do not copy `ResolveViaActiveDirectory`'s
ambiguity throw (`:304-308`) into this path.

### Page changes (`Components/Pages/AdminSettings.razor`)

Each add-handler becomes async and gates on the outcome:

```csharp
private async Task AddPpUser()
{
    var v = ppNewUser.Trim();
    if (string.IsNullOrWhiteSpace(v)) return;
    if (ppUsers.Contains(v, StringComparer.OrdinalIgnoreCase)) { ppNewUser = ""; return; }

    var result = await Task.Run(() => ADSearch.ValidateExists(v, "User"));
    switch (result.Outcome)
    {
        case DirectoryLookupOutcome.Unavailable:
            ppAddError = "Active Directory is unreachable. Try again later.";
            return;
        case DirectoryLookupOutcome.NotFound:
            ppAddError = $"'{v}' was not found in Active Directory. Check the name, " +
                         "or note that cloud-only objects cannot be protected.";
            return;
    }

    ppAddError = null;
    ppUsers.Add(v);
    ppNewUser = "";
}
```

Points the implementer must not get wrong:

- **`Task.Run` is required.** `ValidateExists` is synchronous and takes a lock that can block up
  to 30 seconds; calling it inline freezes the Blazor circuit. This is the same defect
  `docs/OperatorEmailResolution-Plan.md` finding F2 raised, fixed the same way.
- **The two refusal messages must stay distinct.** Collapsing them defeats D1.
- **Disable the Add button while a validation is in flight**, so a slow lookup cannot be queued
  behind itself. A per-field `bool` is sufficient.
- **The `@onkeydown` Enter handlers (`:389-392`) go through the same path.** They currently call
  the sync handlers directly; they must not become a validation bypass.
- **The duplicate check runs before the lookup** -- no directory call for an entry already in
  the list.
- **Patterns keep the existing sync handler unchanged** (see Non-Goals).

The error surfaces per field, not as the page-level `statusMessage`, so a failed Add does not
overwrite an unrelated save result.

### Flagging existing entries

On `LoadProtectedPrincipals` (`:375-386`), validate each already-saved entry and render an
indicator next to any that does not resolve. Requirements:

- **Off the render path.** Run from `OnAfterRenderAsync`, not during load: 7 sequential lookups
  behind a 30s-capable lock must never delay first paint. The list renders immediately,
  indicators appear when the checks finish.
- **Three visual states, matching the three outcomes.** `NotFound` is a warning badge
  ("not found in AD"). `Unavailable` renders **no badge at all** -- an unreachable directory is
  not evidence an entry is bad, and a warning that appears during an outage would be read as
  data loss. Silence is correct there.
- **Informational only.** Never blocks Save, never auto-removes a row (see Non-Goals).
- Patterns are not checked.

### Non-negotiable invariants

- No validation path may report `NotFound` when the lookup did not run.
- Refusing an Add must never mutate the list or clear the input box -- the operator must be able
  to see and correct what they typed.
- No path may write to AD. `ValidateExists` is read-only.
- The Delinea directory-read secret is not used by any code this plan adds (D2).
- ASCII only in all `.cs` and `.razor` changes (CI gate).

## Slices

Each slice is one commit, verified before the next begins.

1. **`ValidateExists` on `ADDirectorySearchService`**, with `DirectoryLookupOutcome` and
   `DirectoryValidationResult`. Exact-match filters for User / Group / OU; the absence/failure
   split; `DOMAIN\` prefix stripping. Tests cover each outcome and the filter shape.
2. **Add-handler gating on the page.** All four handlers plus the Enter keydown paths; the two
   distinct messages; in-flight button disable; `Task.Run` offload.
3. **OU picker.** `ADIdentityAutocomplete` gains OU support (additive parameter; existing
   consumers unaffected), and the OU field switches from raw text to the picker.
4. **Existing-entry flagging** in `OnAfterRenderAsync`, three-state rendering. Version bumps
   land here.

## Verification

Per `.agents/repo-guidance.md`:

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`

`ADDirectorySearchService` gains a new method, so tests are required
(`.agents/repo-guidance.md`, new/rewritten Services rule). The service is `sealed` and its
directory calls need a live AD, so slice 1 must extract the filter construction and the
outcome-mapping into testable units the way `ExchangeIdentityResolver` does with
`IsRecipientNotFound` / `MapRecipient` (`internal static`, `InternalsVisibleTo` already
configured).

**Do not gate a test on `IsAvailable`.** The existing `ADDirectorySearchServiceTests` uses
`if (!svc.IsAvailable)` guards (`:100`, `:111`, `:121`), which were written assuming the test
host has no RSAT. **This dev box does** -- verified 2026-07-31 by importing the ActiveDirectory
module in a bare runspace: it loads with no errors, so `IsAvailable` is true and every such
guard silently skips. A validation test written that way passes whether or not the code is
correct; the slice-1 non-vacuity probe caught exactly that. The absence/failure split is
therefore asserted against a pure `ClassifyOutcome(ValidationStep)` function, which holds on
any host. Treat CI (`windows-latest`, no RSAT) and this dev box as *different* environments for
any AD-touching test.

**Non-vacuity proof is mandatory for the absence/failure split.** Change the `Unavailable`
branch to return `NotFound`, confirm the test fails, restore, confirm green. That collapse is
the specific defect this design exists to prevent.

Page markup is not unit-testable (no bUnit harness in this repo), so slices 2-4 rest on the
manual checks below. State that plainly rather than implying coverage.

### Manual post-deploy checks

On dev, after deploy:

1. Add a real AD user by UPN -- accepted.
2. Add a typo'd user -- refused, message says not found, **the typed text stays in the box**.
3. Add a real AD group by name -- accepted, stored as the DN the picker returned.
4. Add an O365/Entra-only group by name -- refused as not found. This is the case that prompted
   the work.
5. Add a real OU via the new picker -- accepted.
6. Add a pattern (`adm-*`) -- accepted with no directory call (patterns are unvalidated).
7. **Stop the ActiveDirectory path or point at an unreachable DC, then Add** -- refused with
   "Active Directory is unreachable. Try again later." **Not** a not-found message. This is the
   D1 check and the most important in the list.
8. With AD unreachable, load the page -- existing entries render with **no** warning badges.
9. Restore AD, load the page -- any genuinely stale entry shows its badge; valid ones do not.
10. Confirm a flagged stale entry can still be removed and saved.

## Versioning

Per `docs/ProjectConstitution.md` (Deployment And Versioning), both rules fire:

- App `2.3.33` -> `2.3.34` in `ExchangeAdminWeb.csproj` (`VersionPrefix`, `AssemblyVersion`,
  `FileVersion`): `ADDirectorySearchService` is shared infrastructure.
- `AdminSettings` `1.0.1` -> `1.0.2` in `Modules/ModuleCatalog.cs`.

`ADIdentityAutocomplete` is a shared component, not a module, and carries no version of its own.

## Open Questions

- **OQ-1.** Whether the 3 protected `group` rows and 4 `user` rows currently in prod all still
  resolve. Unknown until slice 4 ships or an equivalent check is run by hand. Not a blocker --
  the flagging exists to answer it -- but a stale group row is currently producing fail-closed
  denials that read as directory faults, so the answer is worth having early.
- **OQ-2.** Whether `Get-ADOrganizationalUnit` is available under the app-pool identity in this
  environment. The RSAT AD module is confirmed loadable (`ADDirectorySearchService` depends on
  it), and D2's ruling states read access is undifferentiated here, so this is expected to work.
  Confirm during slice 1 rather than assuming; if it does not, the OU picker degrades to the
  current raw text input and slice 3 is dropped, with slices 1, 2 and 4 unaffected.
