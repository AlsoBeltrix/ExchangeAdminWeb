# Operator Email Resolution From Active Directory -- Plan

Status: **Draft, awaiting owner approval.** Decisions D1-D3 are ruled (owner, 2026-07-29);
see Owner Decisions. No open owner gates. Implementation may not begin until the status
line reads Approved.
App version: `2.3.31` -> `2.3.32` (shared service + DI registration, used by more than one
page over time).
Module: `MessageTrace` `1.3.0` -> `1.3.1` (module behavior change: the recipient box now
pre-fills and historical search stops mis-blaming the operator).
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`,
`docs/AdminModuleSpec.md`. On conflict the higher source wins.

## Problem

`Components/Pages/MessageTrace.razor:610-613` resolves the signed-in operator's mailbox
address from **claims**:

```csharp
userEmail = user.FindFirst(ClaimTypes.Email)?.Value
    ?? user.FindFirst("email")?.Value
    ?? user.FindFirst(ClaimTypes.Upn)?.Value
    ?? "";
```

The app authenticates with **Negotiate only** (`Program.cs:38-39`,
`AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate()`). A Kerberos/NTLM
token carries the account name (`DOMAIN\user`, read at `:601`) and group SIDs. It does not
carry `mail`, does not carry an `email` claim, and does not carry a UPN claim. All three
lookups miss, so `userEmail` is `""` on every request.

Two consumers are broken by this, both pre-existing:

| Consumer | Line | Observed symptom |
| --- | --- | --- |
| Detail-export recipient box (Notify) | `:619` | Renders empty; the D4(a) pre-fill never happens. Reported by the owner on dev, 2026-07-29, against `2.3.31`. |
| Historical search notify address | `:322`, `:709-714` | The disabled "Send report to" field renders blank, and `RunHistoricalSearch` refuses with "Your authenticated email address is required for historical search results delivery." |

The claim read dates to `e62ae73` (the page's original commit) and has been carried through
`7c4baee` (2026-05-07), `079613c`, `4c2ff83`, and `0999c0a` unchanged. This is **not** a
regression from `docs/MessageTraceDownloadLink-Plan.md` slice 4 (`2f0b99c`); that slice
consumed an already-empty variable.

**Historical search has never been used** (owner, 2026-07-29). This is why a permanently
blocking guard survived three months unreported: `:710` refuses before reaching
`StartHistoricalSearchAsync`, and no operator ever hit it. Treat the historical-search path as
**unproven end to end**, not as working code with one broken guard -- nothing downstream of
`:710` has executed in this deployment.

It is nonetheless **wanted functionality**: historical search is the only way L2 can run a
search beyond the realtime window without escalating to L3-4 (owner, 2026-07-29). Unblocking it
is a real capability gain for its intended users, not incidental tidying -- but proving it works
end to end is out of this plan's scope (OQ-2).

Consequence for this plan: removing the false refusal is in scope, but making historical search
*work* is not, and cannot be verified here. Manual check 5 below is therefore exploratory --
its purpose is to discover what the next failure is, if any, not to gate this plan. See OQ-2.

## Owner Decisions

### D1 -- Resolve from Active Directory, not from the token (RULED)

Ruled by the owner, 2026-07-29 ("2nd" -- fix `userEmail` at the source rather than patching
only the new Notify box).

The address is looked up in AD from the authenticated account name. Fixing only the Notify
box would leave the same empty variable feeding historical search, guaranteeing a return
visit to this code.

### D2 -- `mail`, falling back to `userPrincipalName` (RULED)

Ruled by the owner, 2026-07-29: "UPN is the same as email here."

Read `mail` first; when it is absent or blank, use `userPrincipalName`. In this directory the
two agree, so the fallback costs nothing and removes the only realistic empty-result case (an
account with no `mail` attribute set).

**Explicitly rejected:** synthesizing the address as `samAccountName + "@" + <configured
domain>`. It works only while UPN matches samAccountName and fails **silently** by mailing a
plausible-looking wrong address. A directory read is authoritative; string concatenation is a
guess. Do not add a domain-suffix config key for this.

### D3 -- AD unreachable keeps the existing block (RULED)

Ruled by the owner, 2026-07-29: "if AD is unreachable, the whole app stops being relevant."

When the lookup yields nothing, historical search continues to refuse. The refusal **message**
changes to name the real cause instead of blaming the operator's account. Do not make the
disabled "Send report to" field editable, and do not add a typed-address escape hatch: AD being
down already disables autocomplete, group membership checks, and protected-principal
enforcement, so a bypass here would buy nothing and would widen where mail-flow reports can be
sent.

Note for the implementer, so the rejected option is not revived as an "improvement": the
historical-search notify address is passed to Microsoft's `Start-HistoricalSearch` cmdlet as
`NotifyAddress` (`Services/MessageTraceService.cs`, `StartHistoricalSearchAsync`). Microsoft
generates and sends that report; this app never renders it and cannot route it through the
Downloadable Reports page.

## Non-Goals

- No change to authentication. Negotiate stays; no claims transformation middleware, no
  `ClaimsPrincipal` enrichment pipeline. Those are larger changes with app-wide blast radius,
  and D1 asks for a lookup.
- No new module, no new page, no `ModuleCatalog` entry.
- No change to `DownloadSelectedDetails()`, the reports page, the email bodies, or the
  retention behavior landed by `docs/MessageTraceDownloadLink-Plan.md`.
- No change to who receives detail exports. The Notify box keeps D4(a) semantics from that
  plan: pre-fill is a default, not a floor, and a cleared box remains valid.
- No caching layer beyond the per-circuit resolution described below.
- Other pages that could use operator email are out of scope; this plan fixes the two live
  consumers on `MessageTrace.razor`.

## Design

### New service: `Services/OperatorEmailResolver.cs`

A narrow, testable seam. Registered `AddSingleton` in `Program.cs` beside
`ADDirectorySearchService` (`Program.cs:133`).

```csharp
public class OperatorEmailResolver
{
    public virtual string? Resolve(string? accountName);
}
```

- Accepts the Negotiate account name as `Identity.Name` supplies it: `DOMAIN\user`, or a bare
  `user`, or a UPN-shaped string. Strip a leading `DOMAIN\` before querying; pass the
  remainder as the samAccountName.
- Returns the trimmed `mail`, else the trimmed `userPrincipalName`, else `null`. Never returns
  an empty or whitespace string -- callers branch on null only.
- **Fail-soft.** Any exception, AD unavailability, or no match returns `null` and logs a
  warning. This must never throw into `OnInitializedAsync`; a directory hiccup must not break
  the whole page when only a pre-fill depends on it.
- Not sealed, and `Resolve` is `virtual`, so page-level tests can substitute it
  (`Substitute.ForPartsOf<T>`), matching the seam style used by `MessageTraceExportListing`.

**Reuse `ADDirectorySearchService` rather than opening a second runspace.** It already holds a
pooled runspace with the `ActiveDirectory` module imported, a 30-second throttle lock, LDAP
filter escaping via `ProtectedPrincipalService.EscapeLdapFilter`, a cached availability probe,
and it already requests the `mail` and `UserPrincipalName` properties
(`ADDirectorySearchService.cs:148`) and surfaces them on `ADSearchResult`
(`:263-269`). A second runspace would duplicate the module-import cost and the escaping rules.

Implementation note -- `Search` is a **wildcard, substring** search with a 3-character minimum
(`ADDirectorySearchService.cs:69-70, 144`). It is built for autocomplete, not identity
resolution. Therefore:

- Do not treat a single result as authoritative by position. Filter the returned
  `ADSearchResult` list to an **exact, case-insensitive** `SamAccountName` match.
- Return `null` when zero rows match exactly, and when more than one does. A samAccountName is
  unique within a domain, so multiple exact matches means the assumption is wrong and guessing
  would mail the wrong person -- the D2 rejection applies to this case too. Log a warning
  naming the count.
- Accounts shorter than 3 characters cannot be searched. Return `null` and log; do not
  special-case around the minimum.

Where the exact-match filter would be a meaningful cost or the semantics are awkward, adding a
dedicated `FindUserBySamAccountName` method to `ADDirectorySearchService` (non-wildcard
`Get-ADUser -Identity`) is **in scope** and preferred over loosening the filter above. Keep it
fail-soft in the same style as the existing methods.

### `Components/Pages/MessageTrace.razor`

Replace the claim chain at `:610-613`:

```csharp
userEmail = OperatorEmail.Resolve(currentUser) ?? "";
```

Inject the resolver. Resolution happens **once per circuit** in `OnInitializedAsync`, which is
the existing caching story -- do not add a memory cache. Keep `recipientInput = userEmail;` at
`:619` and its comment; with `userEmail` now populated, D4(a) pre-fill starts working with no
other change.

`currentUser` is already `Identity?.Name ?? "Unknown"` at `:601`. Pass `"Unknown"` through to
the resolver; it will not match and returns `null`, which is correct.

Update the refusal at `:710-714`. Current text blames the operator's account:

> "Your authenticated email address is required for historical search results delivery."

Replace with text naming the real cause and the action -- the address could not be resolved
from the directory, and this usually means AD is unreachable. Do not promise a retry the code
does not perform. Keep the guard's structure: it still returns without calling
`StartHistoricalSearchAsync`, and still requires an `@`.

Leave the disabled field at `:322` disabled (D3). Its `value="@userEmail"` binding needs no
change; it starts rendering the address once resolution works.

## Tests

`ExchangeAdminWeb.Tests/OperatorEmailResolverTests.cs`. New services require tests before the
work stream is done (`.agents/repo-guidance.md` Verification).

Drive the tests through a substituted `ADDirectorySearchService` seam rather than a live
directory; the suite must not require a domain controller. If `ADDirectorySearchService` is not
substitutable as written, introduce the narrow interface needed -- do not add a live-AD test.

- `mail` present -> returned, trimmed.
- `mail` null, empty, or whitespace -> `userPrincipalName` returned (D2). One case per input
  shape; a whitespace `mail` must not win over a valid UPN.
- Both absent -> `null`, never `""`.
- `DOMAIN\user` -> the domain prefix is stripped before the query. Assert the term the search
  received, not merely the result, or a refactor that stops stripping still passes.
- Bare `user` and a UPN-shaped input both resolve.
- Exact-match filter: a wildcard result set containing a near-miss (`jdoe2` when asked for
  `jdoe`) resolves to `jdoe` and never the near-miss. **This is the highest-value test here** --
  it guards against mailing trace data to the wrong person.
- Zero exact matches -> `null`. Two or more exact matches -> `null` plus a warning.
- The search throwing -> `null`, no exception escapes (fail-soft).
- `null`, `""`, `"Unknown"`, and a sub-3-character account each -> `null` without throwing.

**Non-vacuity proof (required):** for each new guard -- the UPN fallback, the domain-prefix
strip, the exact-match filter, the ambiguous-match rejection, and the fail-soft catch -- revert
the guard, confirm the matching test fails, restore, confirm green. Record the observed failure
count per guard. A test that passes with its guard removed is vacuous and must be replaced.

Page-level behavior (pre-fill rendering, the changed refusal text) is **not** unit-testable:
the repo has no bUnit harness and this plan does not add one. It is covered by the manual
checks below.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx` (always target the `.slnx`; bare `dotnet test` runs zero
  tests)
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD` (scope to changed paths if the pre-existing unstaged `.gitignore`
  whitespace still makes the repo-wide check return non-zero)
- ASCII gate: `tools/Test-AsciiOnly.ps1`
- PowerShell gates are **not** required: this plan touches no `.ps1`/`.psm1`.
- **Manual, post-deploy, cannot be automated** -- state plainly if not run:
  1. Open Message Analysis; the Notify box is pre-filled with the operator's own address.
  2. Select 11+ messages and export without editing the box; the notification arrives at that
     address.
  3. Clear the box and export; no mail is sent and the export still lists on the reports page
     (D4(a) is unchanged by this plan).
  4. The historical-search "Send report to" field renders the address and remains disabled.
  5. Submit a historical search with a range beyond the realtime window; confirm it is accepted
     rather than refused. **Exploratory, not a gate** -- this path has never been exercised
     (see Problem), so a failure here is a discovery about untested code, not a defect in this
     change. Record what happens and stop; do not fix it under this plan (OQ-2).
  6. Sign in as a second operator; the box shows *their* address, not the first operator's
     (guards against a resolution cached across circuits).

## Open Questions

- **OQ-1 (non-blocking):** other pages may want the operator's address later. This plan
  registers a shared singleton so they can, but converts no other page. Any such conversion is
  separate work.
- **OQ-2 (does not block THIS plan, but is real work that follows it):** the historical-search
  path beyond `:710` has never executed in this deployment (owner, 2026-07-29). Anything it
  hits once the refusal is removed -- EXO permissions on `Start-HistoricalSearch`, the cmdlet
  rejecting the address, an unhandled response shape -- is undiscovered, not regressed.

  This is **wanted functionality**, not a curiosity: it is L2's only route to a beyond-realtime
  search without escalating to L3-4 (OQ-3). So manual check 5 is exploratory only in the sense
  that its outcome must not gate *this* change; the outcome itself matters and must be recorded
  in `.agents/state.md` either way. If it fails, raise a follow-up plan to make historical
  search work end to end rather than absorbing the fix here.
- **OQ-3 -- CLOSED (owner, 2026-07-29): keep historical search; do not remove it.** It is the
  only way L2 can run a search beyond the realtime window without escalating to L3-4. Removal
  was considered and rejected. Do not re-raise deletion as a simplification in any later work
  on this page.
