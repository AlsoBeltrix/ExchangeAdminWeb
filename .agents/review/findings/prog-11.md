# prog-11: RazorSyntax cannot parse a `@code` block containing template markup

**Severity**: MEDIUM - latent. Test tooling only; no shipped behaviour is affected and no
current page is mis-analysed. It is the first known way for a subscription to be INVISIBLE
since `leak-1(c)` was closed.
**Status**: Open
**Branch**: - (direct to main)
**Commit**: -

## Evidence

`ExchangeAdminWeb.Tests/RazorSyntax.cs` extracts each `@code { ... }` body and parses it as
C# inside a synthetic class. **Three pages declare a `RenderFragment<T>` whose value is
inline Razor template markup (`@<text>` / `@<div>`) written inside the `@code` block**:
`Migration.razor`, `ServiceHealth.razor`, `AdminBulkJobs.razor`. That is Razor, not C#, and
Roslyn cannot parse it.

Measured by the agent implementing `prog-10(b)`, with a throwaway probe: `Migration.razor`'s
region yields **2 methods where the regex yields ~200**, with parse errors starting at
`Migration.razor:1226`. A Roslyn-backed `DeclaredMethods` went red with ~90 orphaned
Migration entries and 5 ServiceHealth entries. It was implemented, measured, and **backed
out** - which is why `prog-10(b)` took the depth-zero route instead.

## Why it matters beyond the method table

Both guards read the same trees:

- The progress scanner's CODE and DISCOVERY views come from these regions, so blanking on
  the tail of those three blocks rests on parser **error-recovery** output rather than a
  clean parse.
- `EventSubscriptionScan` reads them too. **A `+=` written after the template markup in one
  of those three blocks may not be seen at all** - an invisible subscription, which is a
  worse class than an unguarded one because no rule can fire on something it cannot see.

Latent today, verified: `AdminBulkJobs` subscribes at `:181` and its template starts at
`:369`; the other two pages carry no subscriptions.

## Approach

Pending, and it needs a decision rather than a default. Options as they stand:

1. **Razor-aware parsing.** `Microsoft.AspNetCore.Razor.Language` would handle the template
   markup properly. It is a new test-project dependency and a larger change than anything in
   this stream so far.
2. **Detect and refuse.** Fail closed when a `@code` block does not parse cleanly, naming
   the page. Cheap, honest, and immediately turns three pages red - so it needs either an
   allowlist with written reasons or the work in option 1 behind it.
3. **Accept and record.** Leave it, with the limitation written where the next agent will
   read it. Defensible only while no subscription sits after a template in those three
   pages, which nothing enforces.

**Option 2 with a written allowlist is the smallest honest step**, because it converts a
silent blind spot into a named one that cannot grow without someone noticing.

## Known gaps

This is the eleventh guard finding in seven days and the second to come from the Roslyn
rewrite itself. The pattern holds: each tool closed the previous tool's holes and introduced
its own, and every single one was found by review or by an agent tripping something while
doing adjacent work - never by the guard's author reading their own code.

## Reviewer comments

Not raised by a reviewer. **Found by the agent closing `prog-10`, while implementing an
approach it then measured and abandoned.** The finding exists because that agent reported
what the abandoned attempt taught it rather than only reporting the route it shipped.

## Closeout

Pending.
