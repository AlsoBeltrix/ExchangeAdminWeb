# Operator Email Resolution From Active Directory -- Plan

Status: **Draft, awaiting owner approval.** Decisions D1-D3 are ruled (owner, 2026-07-29);
see Owner Decisions. Implementation may not begin until the status line reads Approved.
**Independently reviewed 2026-07-29** (openreview, codex-commercial / gpt-5.6-sol / max,
range `64b211a..ace6230`): verdict **findings** (3), all accepted and repaired here; record
at `.agents/review/findings/operator-email-resolution-plan.md`. F1 (HIGH) replaced this
plan's central mechanism -- the lookup key is now the authenticated **primary SID**, not the
account name -- so the owner is ruling on a materially revised design. No open owner gates
(OQ-4 closed by the owner, 2026-07-29: dev is `2.3.31`).
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
token carries the account name (`DOMAIN\user`, read at `:601`) and SIDs -- including the
account's own **primary SID**, which this app already consumes elsewhere
(`Components/Pages/SelfServiceGroups.razor:325`). It does not carry `mail`, does not carry
an `email` claim, and does not carry a UPN claim. All three lookups miss, so `userEmail` is
`""` on every request.

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

The address is looked up in AD from the authenticated principal. Fixing only the Notify
box would leave the same empty variable feeding historical search, guaranteeing a return
visit to this code.

**Amended after review (openreview finding F1, 2026-07-29):** the lookup key is the
authenticated **primary SID**, not the account name. The ruling is unchanged -- it asked for
a directory lookup and still gets one; only the key it looks up by is corrected. See Design.

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
    public virtual Task<string?> ResolveAsync(string? primarySid);
}
```

- Accepts the operator's **primary SID** as the Negotiate scheme supplies it in
  `ClaimTypes.PrimarySid` (`S-1-5-21-...`). Not the account name. See "Why the SID" below.
- Validates the input is a well-formed SID before querying, then returns the trimmed `mail`,
  else the trimmed `userPrincipalName`, else `null`. Never returns an empty or whitespace
  string -- callers branch on null only.
- **Fail-soft.** Any exception, AD unavailability, a missing claim, a malformed SID, or no
  match returns `null` and logs a warning. This must never throw into `OnInitializedAsync`; a
  directory hiccup must not break the whole page when only a pre-fill depends on it.
- Not sealed, and `ResolveAsync` is `virtual`, so page-level tests can substitute it
  (`Substitute.ForPartsOf<T>`), matching the seam style used by `MessageTraceExportListing`
  (`Services/MessageTraceExportListing.cs:80`, `:214`).

### Why the SID, and not the account name (openreview F1, HIGH)

The first draft of this plan looked the operator up by samAccountName through
`ADDirectorySearchService.Search`, then post-filtered for an exact match. Review rejected that
design, and the rejection is correct. `Search` is a **wildcard, substring** query with a
3-character minimum and a `ResultSetSize` cap (`ADDirectorySearchService.cs:69-70`, `:144`,
`:149`), built for autocomplete. Four separate failures follow from using it as an identity
oracle, and an exact-match post-filter closes none of them:

1. A UPN-shaped input can never equal a samAccountName, so the "UPN-shaped input resolves"
   requirement and the exact-match rule contradict each other outright.
2. An account name under 3 characters is unreachable: the search returns `[]` before it runs.
3. The `ResultSetSize` cap can truncate the set before the exact match appears. The filter then
   finds nothing and returns `null` -- indistinguishable from a genuine no-match.
4. Stripping the `DOMAIN\` prefix discards domain identity. The search is not domain-scoped, so
   across a trust the same samAccountName in two domains can yield **one** row that is
   confidently the wrong person. The "two or more matches means stop" guard never fires,
   because only one matched. That mails mail-flow data to the wrong mailbox -- exactly the
   outcome D2's rejection of address synthesis exists to prevent.

The repo already solved this, and solved it against the rejected approach by name.
`Services/SelfServiceGroups/SelfServiceGroupService.cs:672-685` (`ResolveCallerDn`) turns an
authenticated principal into a directory object with a bound `Get-ADUser -Identity <sid>`; its
doc comment at `:64-68` explicitly refuses "an alternate identity form (DN, GUID,
sAMAccountName)", and `:73` hard-validates that the input is a SID. Follow that precedent.

A SID is immutable, unambiguous, and domain-qualified. It needs no wildcard, no length
minimum, no result cap, and no post-filter -- all four failures above disappear rather than
being guarded against.

**Do not use `ADDirectorySearchService.Search` for this.** Add a dedicated
`FindUserBySid(string sid)` to `ADDirectorySearchService` that runs a bound
`Get-ADUser -Identity <sid>` on the **existing pooled runspace**, requesting `mail` and
`UserPrincipalName` (already in the property set at `:148`, already surfaced on
`ADSearchResult` at `:263-269`). Reusing that runspace keeps the `ActiveDirectory` module
import, the throttle lock, and the availability probe; a second runspace would duplicate all
three. Keep it fail-soft in the style of the existing methods.

**No name-based fallback.** When the `PrimarySid` claim is absent or the SID does not resolve,
return `null` and take the D3 path. D3 has already ruled what an unresolvable address means:
historical search refuses with an honest message, and the pre-fill is simply empty. A second,
weaker resolution path would reintroduce the ambiguity above to avoid an outcome the owner has
already accepted.

### Asynchronous by construction (openreview F2, MEDIUM)

`ADDirectorySearchService` blocks: `_runspaceLock.Wait(TimeSpan.FromSeconds(30))`
(`ADDirectorySearchService.cs:80`) on a process-wide lock held by a singleton, so any
concurrent AD call in the app, or a cold `ActiveDirectory` module import, sits on the critical
path. A synchronous resolve called from `OnInitializedAsync` would stall server-side rendering
and the Blazor circuit for up to 30 seconds -- to populate a text box that D4(a) calls a
default and not a floor.

The repo already treats this call as blocking and already moves it off the renderer thread:
`Components/Shared/ADIdentityAutocomplete.razor:104` wraps it in `Task.Run`. Do the same.
`ResolveAsync` performs the PowerShell work under `Task.Run`; the page renders immediately and
fills the box when the lookup returns.

### `Components/Pages/MessageTrace.razor`

Replace the claim chain at `:610-613`:

```csharp
var primarySid = user.FindFirst(System.Security.Claims.ClaimTypes.PrimarySid)?.Value;
userEmail = await OperatorEmail.ResolveAsync(primarySid) ?? "";
```

Inject the resolver. Resolution happens **once per circuit** in `OnInitializedAsync`, which is
the existing caching story -- do not add a memory cache. Keep `recipientInput = userEmail;` at
`:619` and its comment; with `userEmail` now populated, D4(a) pre-fill starts working with no
other change.

Read the SID the same way `Components/Pages/SelfServiceGroups.razor:325` does. Do **not** pass
`currentUser` (the `DOMAIN\user` string at `:601`) to the resolver -- it is still used for
audit and display, but it is not an identity key here.

**Late-arriving resolution must never overwrite typed input.** The resolve is now awaited
rather than instant, so the operator can reach the recipient box before it returns. Write the
resolved value into `recipientInput` only while the box is still untouched -- track a
"operator has edited the recipient box" flag set on first input, and skip the pre-fill when it
is set. A pre-fill is a default (D4(a)); silently replacing an address the operator typed
would be the worst possible reading of that ruling. `RunHistoricalSearch` awaits the same
resolution rather than reading a field that may not be populated yet.

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

Drive the tests through a substituted directory seam rather than a live directory; the suite
must not require a domain controller. **`ADDirectorySearchService` is `sealed`
(`Services/ADDirectorySearchService.cs:11`), so NSubstitute cannot substitute it as written.**
Introduce the narrow interface the resolver depends on (the single `FindUserBySid` member) and
have `ADDirectorySearchService` implement it -- this is required, not a contingency. Do not add
a live-AD test, and do not unseal the class for test convenience.

- `mail` present -> returned, trimmed.
- `mail` null, empty, or whitespace -> `userPrincipalName` returned (D2). One case per input
  shape; a whitespace `mail` must not win over a valid UPN.
- Both absent -> `null`, never `""`.
- A valid SID is passed to the directory call **unmodified**. Assert the identity the lookup
  received, not merely the result, or a refactor that mangles the SID still passes.
- Malformed input -- `null`, `""`, `"DOMAIN\user"`, `"user@contoso.com"`, `"Unknown"`, and a
  non-SID string -- each returns `null` without throwing and **without reaching the directory
  at all**. Assert the lookup was not called. **This is the highest-value test here:** it is
  what keeps a name from ever re-entering the identity path and mailing trace data to the
  wrong person (F1).
- The directory call throwing -> `null`, no exception escapes (fail-soft).
- The lookup finding no user -> `null`.

**Non-vacuity proof (required):** for each new guard -- the UPN fallback, the SID-format
validation, the pass-through of the SID unmodified, and the fail-soft catch -- revert the
guard, confirm the matching test fails, restore, confirm green. Record the observed failure
count per guard. A test that passes with its guard removed is vacuous and must be replaced.

Page-level behavior (pre-fill rendering, the untouched-box rule, the changed refusal text) is
**not** unit-testable: the repo has no bUnit harness and this plan does not add one. It is
covered by the manual checks below.

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
  7. Confirm the address actually resolved rather than the resolver fail-softing to empty --
     i.e. that `ClaimTypes.PrimarySid` is populated on this deployment. Check 1 passing is
     sufficient evidence; if the box is empty, check the warning log before assuming AD is
     down (openreview F1, Known gap 1).
  8. Type an address into the recipient box immediately on page load, before it pre-fills;
     the typed value must survive -- the late resolution must not overwrite it (F2).

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
- **OQ-4 -- CLOSED (owner, 2026-07-29): dev is running `2.3.31`.** Raised by openreview (F3):
  `.agents/state.md` claimed both that `2.3.31` was deployed nowhere and that the missing
  pre-fill was seen on dev, which the box's existence makes impossible. The "deployed nowhere"
  half was the stale one. Consequences now recorded in `.agents/state.md`: the export-delivery
  redesign and the MessageTrace NRE fix are both live on dev and neither is on prod (`2.3.30`),
  and the empty pre-fill this plan fixes was observed against the real `2.3.31` build.
