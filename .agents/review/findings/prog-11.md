# prog-11: RazorSyntax cannot parse a `@code` block containing template markup

**Severity**: MEDIUM - latent. Test tooling only; no shipped behaviour is affected and no
current page is mis-analysed. Raised as the first known way for a subscription to be INVISIBLE
since `leak-1(c)` was closed - **and that part of the diagnosis turned out to be wrong, see
Approach.** What is real is that both guards read error-recovery output on three pages and
nothing said so.
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: this commit

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

Re-measured at this head, over all 55 files under `Components/`. **Exactly three fail to
parse, and they are the three named:**

| Page | `@code` block | Template at | First error | Errors | Method nodes recovered |
|---|---|---|---|---|---|
| `Migration.razor` | 1220-4170 | 1227, 1381, 1445 | 1226 (CS1525) | 711 | 2 of 118 |
| `ServiceHealth.razor` | 196-540 | 221 | 220 (CS1525) | 382 | 2 of 19 |
| `AdminBulkJobs.razor` | 138-404 | 368 (`@<div>` at 369) | 368 (CS1525) | 146 | 13 of 13 |

"of N" is the count the progress scanner's regex table finds at component level.

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

**Option 2 taken: detect and refuse, with a written allowlist.** Option 1 (a
`Microsoft.AspNetCore.Razor.Language` dependency in the test project) is a bigger change than
this finding warrants; option 3 (accept and record in prose) leaves a blind spot that grows
every time somebody writes a fourth template.

`RazorSyntax` now exposes what the parser actually said - `CodeRegion.Errors` and
`ParsedSource.FirstError()`, positioned in the RAW file rather than in the synthetic tree -
and `RazorParseGate` is the rule over it:

- Every component under `Components/`, taken from the filesystem, must parse cleanly.
- A page that does not is **refused by name**, with its first error line and the diagnostic,
  unless it is on `RazorParseGate.Allowed`.
- An allowlist entry carries a **written reason**, in the shape `ClickGateRegistry` uses, and
  a blank one is refused.
- **The allowlist is a ceiling, not a floor.** An entry whose page parses CLEANLY is refused,
  so taking the template markup out of a page forces its entry out too. An entry naming a page
  that is not on disk is refused as stale. Both are what stop the list quietly growing.

### What the three allowlisted pages actually lose - measured, not assumed

The split is **lexis survives, structure does not**, and it is not the split the finding
predicted.

**Lexical facts survive error recovery.** Comment trivia and string-literal tokens are still
produced for the whole block, so `CodeView` and `CommentsBlanked` blank the same characters
they would on a clean parse. Measured: all **1,098** quote characters inside Migration's block
are blanked in the code view, and a C# comment and a string literal injected AFTER the template
on each of the three pages were blanked in both views. Consequence: **no registered operation
on any of the three pages is analysed against a structural claim** - the progress scanner's
method table is a regex over those views with brace depth counted over the code view, never
over the tree. That covers Migration's 61 registered operations, ServiceHealth's 6 and
AdminBulkJobs' 5, and `EveryRegisteredOperationNamesAMethodThatStillExists` passing over all
three is the standing evidence that the table resolves for them.

**Structure does not survive.** `MethodDeclarationSyntax` recovery stops at the template, and
`EventSubscriptionScan` is the only reader of structure.

- `Migration.razor` loses the most: the first template is near the TOP of the block, so two
  method nodes survive out of 118 - `OnInitializedAsync` (1743-1768, body intact, four awaits)
  and `OnParametersSetAsync` (1773, signature only). Roughly 2,950 lines of the block sit
  inside no method node. Note the asymmetry: recovery is not uniformly useless, and a
  subscription inside the intact `OnInitializedAsync` would be judged normally.
- `ServiceHealth.razor` the same shape, smaller: 2 nodes of 19, `OnInitializedAsync` (316-340)
  and `OnAfterRenderAsync` (342-352).
- `AdminBulkJobs.razor` loses the least, because its template is the LAST member of the block:
  **all 13 methods recover intact** and the only structure-less text is the fragment body.

### The finding's sharp end does not hold, and this is the one contradiction to report

prog-11 predicted that a `+=` after the template "may not be seen at all". **It is seen.**
Injected after the template on each of the three pages, the subscription was found, got
`FirstAncestorOrSelf<MethodDeclarationSyntax>() == null`, and was reported with the
`NoEnclosingMethod` message - **a refusal, not a pass.** The same injection into
`ConferenceRooms.razor` (which parses) was attributed to its method and judged by the real
rule, so the difference is the parse and not the probe.

So the hazard today is fail-closed, which is the direction a guard must fail in. That does not
make the finding wrong, it relocates it: the guards were resting on Roslyn's error-recovery
behaviour, which is not a contract and which nobody had checked. Option 2 is still the right
call - it is the thing that stops the resting being silent - but it is justified by "the
guards read a guess and nothing said so", not by "a subscription can be invisible".

## Files changed

- `ExchangeAdminWeb.Tests/RazorSyntax.cs` - `CodeRegion.Errors`, the `ParseError` record,
  `ParsedSource.FirstError()`, and a class-remarks paragraph naming the limitation.
- `ExchangeAdminWeb.Tests/RazorParseGateTests.cs` (new) - `RazorParseGate` with the allowlist,
  `UnallowedParseFailures` and `AllowlistFaults`, plus eight tests.

## Guard proof

Every probe below was BUILT before it was counted; the Release build succeeded with 0 errors
on both live mutations.

**The gate bites.**

- A fixture page with `@<text>` markup in its `@code` block, not allowlisted: refused.
- The same fixture, allowlisted: not refused. The same fixture with the template taken out:
  not refused. So the test above is not passing because everything is refused.
- An allowlist entry with `""`, `"   "` or `"\t\n"` as its reason: refused, "allowlisted with
  no reason". The same entry with a real reason: clean.
- **The ceiling rule, on a fixture AND on a real page.** A cleanly-parsing fixture on the
  allowlist is refused; adding a real page that parses (`Comms10k.razor`) to
  `RazorParseGate.Allowed` produced "Comms10k.razor: allowlisted, but its @code block now
  parses CLEANLY. The allowlist is a ceiling, not a floor".
- An entry naming a page that is not on disk: refused as stale.
- **The live gate is load-bearing**: renaming the `Migration.razor` entry so it no longer
  matched the file turned three tests red, the live one reading "Migration.razor: first parse
  error at line 1226 (CS1525: Invalid expression term ''), 711 errors in total".

**The two standing mutations, re-run.**

- Deleting `if (_disposed) return;` from `AdminBulkJobs.razor:178-179` fails
  `NoComponentSubscribesToAnEventAfterAnAwaitWithoutADisposedGuard` at
  `AdminBulkJobs.razor:178` with the after-await message. **This is also the direct answer to
  "is AdminBulkJobs' subscription at :181 genuinely seen today" - yes.** It sits inside
  `OnInitializedAsync` (154-184), which recovers whole because the template is below it, and
  the guard is credited on the real tree. Restored and touched.
- A handler after a URL on `CloudPasswordReset.razor` - `title="https://example.invalid/help"`
  then `@onclick="MutationProbeAsync"` on the same tag, with a probe method in the `@code`
  block - fails `EveryOperationHandlerOnACoveredPageIsClassified`. The prog-4b shape still
  bites. Restored and touched.

Full suite 3707 passed / 0 failed / 3 skipped (baseline 3699 + the 8 new tests), Release build
clean, `dotnet format --verify-no-changes --no-restore` clean, `git diff --check HEAD` clean.

## Known gaps

**The gate names the blind spot; it does not remove it.** Those three pages still hand
error-recovery trees to `EventSubscriptionScan`. Option 1 remains the real fix and is not
taken.

**The measured numbers in the reason strings are not enforced.** Only the page name, the
non-blankness of the reason, and the fact of failing to parse are tested. The line spans and
counts in each reason were true when this landed and will drift as the pages change; they are
a description of the damage, not a tripwire on it.

**A reason is only checked for being non-blank.** A one-word reason would pass. The rule the
owner asked for is that an empty one cannot, and a longer minimum would be an invented
threshold.

**The allowlist is keyed by bare file name**, matching `ClickGateRegistry` and
`EventSubscriptionScan`. Two components sharing a base name under `Components/` would be
indistinguishable; there are none today, and `AllowlistFaults` refuses that shape if one
appears.

This is the eleventh guard finding in seven days and the second to come from the Roslyn
rewrite itself. The pattern holds: each tool closed the previous tool's holes and introduced
its own, and every single one was found by review or by an agent tripping something while
doing adjacent work - never by the guard's author reading their own code. **This one adds a
variant worth naming: the finding's own mechanism was wrong.** The blind spot was real, the
consequence predicted for it was not, and only measuring it said so.

## Reviewer comments

Not raised by a reviewer. **Found by the agent closing `prog-10`, while implementing an
approach it then measured and abandoned.** The finding exists because that agent reported
what the abandoned attempt taught it rather than only reporting the route it shipped.

Independently confirmed by codex, which parsed `Migration.razor` itself: 2 methods recovered,
first parse error at `:1226`. Both numbers reproduce at this head.

## Closeout

Closed with option 2, one commit, direct to master. Three pages are allowlisted with written
reasons saying what each loses; a fourth cannot join them silently, and none of the three can
keep its entry after its template markup goes.

One correction recorded above rather than quietly dropped: the invisible-subscription hazard
this finding was raised on **does not exist today** - the event guard sees such a subscription
and refuses it as `<no enclosing method>`. The finding stands on the weaker and true claim
that both guards were reading error-recovery output with nothing saying so.
