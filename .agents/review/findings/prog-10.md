# prog-10: Both Roslyn guards can be fooled by a local function or an overload

**Severity**: HIGH (event guard) + MEDIUM (progress registry). **Latent, not live** - no
current page carries either shape, verified by scan. These are guard holes, not page defects.
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: 84debc8 (a)

## How this was found, which is the point

The agent that rebuilt both guards on Roslyn recorded "no semantic model" as a KNOWN GAP and
argued it was safe because name-matching is over-inclusive. Codex pressed exactly that
sentence: **over-inclusion is only safe while it can cause a false FAILURE. The moment it can
cause a false PASS it is a hole.**

Its first review session died mid-check on that question. Re-dispatched narrowly on it alone,
it confirmed the doubt and found a second, worse instance in the other guard. Both were
reproduced by reflection against the compiled test assembly, not argued.

## (a) HIGH - the event guard credits a guard that runs AFTER the subscription

`ExchangeAdminWeb.Tests/EventSubscriptionLifetimeTests.cs:140` attributes a `+=` inside a
local function to the nearest enclosing `MethodDeclarationSyntax`. `LastAwaitBefore`
(`:269-273`) then reasons over the OUTER method by source position, and
`DominatingGuard`/`Dominates` (`:279-320`) credit a guard that is lexically earlier than the
local function's DECLARATION.

Reproduced:

```csharp
await ...;
SubscribeToJobs();
if (_disposed) return;
void SubscribeToJobs() { BulkJobs.JobChanged += OnJobChanged; }
```

Returned one subscription, `Method = OnInitializedAsync`, **problem EMPTY - accepted.**

The subscription executes BEFORE the guard; the guard is credited because the local function
is DECLARED after it. **The dominance proof reasons about a declaration's lexical position
instead of the executable invocation.** That is the same class as leak-1(b), which this guard
was written to close, reached by a shape nobody tried.

## (b) MEDIUM - the progress registry can prove an operation against the wrong body

`ProgressRegistryTests.cs:213-222` builds a `Dictionary<string, PageMethod>` and SKIPS later
declarations with the same name. `CodeOf` (`:373-376`) returns only that first span, and
`CoveredCalls` feeds it into all three `Reported` conditions (`:562-571`). Existence and
classification are name-only (`:443-446`, `:483-520`).

Reproduced twice:

- A page with `@onclick="() => MutationProbeAsync(userId)"`, a first no-arg
  `MutationProbeAsync` containing `Progress.Begin` and the covered call, and a second
  `MutationProbeAsync(string)` doing the real silent work: one key, one operation, `CodeOf`
  returning the FIRST body, one activity. The bound handler is unreported and the suite is
  green.
- An earlier async LOCAL FUNCTION of the same name shadows the page method entirely.

`DeclaredMethods` being regex-anchored for FINDING declarations - which the Roslyn work left
in place deliberately and recorded - is what makes this reachable.

## Approach

Codex's recommendations were:

- (a) treat `LocalFunctionStatementSyntax` as a callable unit in the same recursive
  caller-window analysis, or fail closed on a subscription inside a local function unless the
  call site is proven guarded. **Reason about the invocation, not the declaration's position.**
- (b) build the method table from Roslyn declarations for component-level methods only, and
  either reject duplicate page method names or key entries by signature/arity. **At minimum,
  fail closed** on a duplicate name or a local-function match rather than first-match-wins.

### (a) FAIL CLOSED, at the one place position reasoning starts

Taken: refusal, not recursion. The recursive option was weighed and rejected on cost. The
caller-window walk is keyed on `MethodDeclarationSyntax` throughout - `Component.calls`,
`CallsTo`, `LastAwaitBefore`, `DominatingGuard` - and a local function is not one. Making it a
callable unit means a second kind of node in all of them, plus answers for the questions
methods do not raise: a local function nested in another local function or in a lambda, one
converted to a delegate rather than called (`Task.Run(Subscribe)`), one shadowing a page
method's name, and the fact that `method.DescendantNodes()` already descends INTO local
function bodies, so the outer method's own await and guard searches would need excluding them.
That is not a small change, and every part of it is more machinery of the kind that produced
two holes already.

The refusal is three lines, at the TOP of `ProblemAt`: the node being judged must be in the
body of the method being judged. Everything below that line reasons about source POSITION -
which await precedes the node, which guard encloses it - and position is execution order only
for a body entered at the top and run downward. A local function and a lambda both run where
they are CALLED; nothing available syntactically says when that is, so the shape is refused
with a message that names it.

Putting it there rather than at the subscription closes the same hole one hop deeper, which
the finding does not mention and which the fixtures now pin: when the `+=` is in an ordinary
helper and it is the CALL to that helper that is written inside a local function,
`CallerWindow` proved dominance over the declaration's position in exactly the prog-10(a) way.

Lambdas and anonymous delegates are refused by the same three lines, deliberately. The
attribution bug is identical - `FirstAncestorOrSelf<MethodDeclarationSyntax>()` walks straight
past both - and a deferred lambda body is the worse of the two, because it can run after
Dispose has been and gone. Verified accepted before the fix: a `+=` inside
`await InvokeAsync(() => { ... })` placed after a real dominating guard returned an empty
problem. Splitting that into its own finding would have left a known-identical hole open.

The price is paid knowingly: two CORRECT shapes are refused with it - guarded at the call
site, and guarded inside the local function - because telling either apart from the defect
needs the ordering fact the scanner does not have. No component under `Components/` carries
any of these shapes today, so the refusal costs nothing now, and the message says what to do
(subscribe from a method, where the call-site analysis applies).

### (b) THE MINIMUM, and the Roslyn route was tried first and rejected on evidence

The recommended route - build the method table from Roslyn declarations - was IMPLEMENTED and
then backed out, because it does not work on this repo's pages. `Migration.razor`,
`ServiceHealth.razor` and `AdminBulkJobs.razor` each declare a `RenderFragment<T>` whose value
is inline Razor TEMPLATE MARKUP (`@<text> ... `) written inside the `@code` block. That is
Razor, not C#. Roslyn stops understanding the block at it: measured against Migration.razor,
`MethodDeclarationSyntax` yields TWO methods where the regex yields two hundred, and the
suite's own `EveryRegisteredOperationNamesAMethodThatStillExists` failed with ~90 Migration
entries and 5 ServiceHealth entries orphaned. A parse beats a regex only where what it is
handed is the language it parses. Here it is not, and the honest answer was to keep the regex
and fix what the finding is actually about.

So: the minimum, done properly, and both halves of (b) close.

- **The local function.** It is excluded structurally rather than by failing closed, because
  "component level" has a cheap exact test that does not need a parser: a declaration must sit
  at brace depth ZERO inside its `@code` block. A local function lives in the body of the
  method holding it and is therefore at depth one or more, whatever modifiers it carries.
  Depth is counted over the CODE view, where a brace inside a literal or a comment is already
  blank and markup outside the blocks is blanked whole - so the count never leaves the blocks
  and never sees a brace that is not code. This is strictly better than refusing the page: the
  page method wins its own key back.
- **The overload.** Both declarations are now found and the name is then REFUSED - kept out of
  the table entirely and reported by `NoCoveredPageDeclaresTwoMethodsWithTheSameName`. The
  registry is keyed by name; a name that means two bodies cannot be registered, because
  whichever body an entry is proved against leaves the other unaccounted for. Keying by
  signature or arity was not taken: it would make the registry able to describe an overloaded
  operation, which is a design change to the registry, not a repair of a guard hole, and no
  page needs it.

## Files changed

- (a) `ExchangeAdminWeb.Tests/EventSubscriptionLifetimeTests.cs` - `EnclosingCallable`,
  `DeferredProblem`, the `DeferredBody` and `NoEnclosingMethod` messages, the refusal at the
  top of `ProblemAt`, the class remarks, and six fixtures.
- (b) `ExchangeAdminWeb.Tests/ProgressRegistryTests.cs` - `DeclaredMethods` returns
  `(Methods, Ambiguous)` and keeps only depth-zero declarations, the new `BraceDepths`,
  `PageScan.AmbiguousMethods`, and three tests.

## Guard proof

### (a)

Before, all five shapes run against the scanner as it stood at `0d345ed`, each returning a
subscription with `Problem` EMPTY - accepted:

1. the finding's own reproduction (guard written above a local function declared below it);
2. the same shape guarded AT the call site;
3. the same shape guarded INSIDE the local function;
4. a `+=` inside `await InvokeAsync(() => { ... })` after a dominating guard;
5. a call to a subscribing helper written inside a local function.

After: all five are rejected with a message naming the shape - "sits inside the local function
'SubscribeToJobs'" / "sits inside a lambda or anonymous delegate body", each followed by
"which does not run where it is written - it runs where it is CALLED". Shape 5 reads "is
reached from OnInitializedAsync() at line 5, where the call sits inside the local function
'Go'".

The guard's real job still works: all 24 tests in the class pass, which includes the live
rule over every component on disk, the five-file liveness assertion, both leak-1 helper shapes
that must PASS, and a new fixture proving a local function ELSEWHERE in the method does not
disturb the ordinary rule. Mutation re-run: deleting `if (_disposed) return;` from
`AdminBulkJobs.razor` fails the live rule at `AdminBulkJobs.razor:178` with the after-await
message; restored and touched.

Full suite 3696 passed / 0 failed / 3 skipped (baseline 3690), Release build clean,
`dotnet format --verify-no-changes` clean.

### (b)

Before, both reproductions run against the scanner as it stood at `84debc8`:

- The overload. `page.Methods` held ONE key for `MutationProbeAsync` - the observed failure
  reads `Collection: ["MutationProbeAsync"]` - and `CodeOf` returned the no-argument body
  carrying `Progress.Begin` while the bound one-argument overload did the silent work.
- The local function. `CodeOf("MutationProbeAsync")` returned
  `"        async Task MutationProbeAsync()\n        {\n"...` - the LOCAL FUNCTION's body. The
  page method of that name was invisible.

After: the overload's name is absent from `Methods`, `CodeOf` returns empty, and the name is
reported in `AmbiguousMethods`; the local function is gone from the table and `CodeOf` returns
the page method's body with no `Progress.Begin` in it - while `CodeOf("OnInitializedAsync")`
still contains the local function's `Progress.Begin`, because it is genuinely part of that
method's body.

The registry's real job still works: all 35 tests in the class pass over the live pages, which
includes every registry entry still naming a method that exists, every discovered handler
still classified, and every covered page still yielding its declared methods - so the depth
rule dropped no registered method, on the three pages with template markup in their code block
included. Mutations on a live page, each checked to COMPILE first: the prog-4b probe (a
handler after a URL on `CloudPasswordReset.razor`) still fails classification at
`CloudPasswordReset.razor:194`, and a second `ExecuteResetAsync` overload on that page fails
the new ambiguity rule by name. The first attempt at the overload probe did NOT compile
(`CS1503` on `DeriveDestination`); it was fixed and re-run rather than counted.

Full suite 3699 passed / 0 failed / 3 skipped, Release build clean, `dotnet format
--verify-no-changes` clean.

## Known gaps

**RazorSyntax cannot parse a code block containing Razor template markup, and three pages have
one.** Found while implementing (b), not part of the finding. `Migration.razor`,
`ServiceHealth.razor` and `AdminBulkJobs.razor` declare a `RenderFragment<T>` whose value is
`@<text>`/`@<div>` markup written inside `@code`. Roslyn stops understanding the block there:
Migration.razor parses to two methods out of two hundred, and ServiceHealth.razor similarly.
The consequences are not confined to (b):

- The progress scanner's CODE and DISCOVERY views are built from those same regions, so
  blanking on the tail of those three blocks rests on error-recovery output.
- `EventSubscriptionScan` reads the same trees. A `+=` written after the template markup in
  one of those three blocks may not be seen AT ALL - which is the invisible-subscription
  class, not the unguarded one. None of the three carries such a subscription today
  (AdminBulkJobs subscribes at :181, its template begins at :369), so this is latent. It is
  also the first known way for a subscription to be invisible to the guard since leak-1(c) was
  closed, and it deserves its own finding and an owner decision on the fix (a Razor-aware
  parse, or refusing to judge a block the parse reported errors in).

This is the ninth and tenth guard finding in this repo in seven days. Worth stating plainly:
**every one has been found by review or by an agent tripping something, never by the guard's
own author.** The Roslyn rewrite was correct and necessary - it closed five bypasses that
regex could not - and it still shipped two more, because a syntax tree answers "what is
written where" and these two questions are "what executes when".

## Reviewer comments

Raised by codex in a narrow follow-up after its first review of `ed3e97a..e039cd9` ended
mid-check on this exact sentence. That first session DID confirm the range's headline claim:
the URL mutation on `CloudPasswordReset.razor` fails classification on `e039cd9` and passes
on `ed3e97a` - the predicted silent ship, reproduced end to end.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard.
Raw output: `.agents/review/roslyn2.result.json`; first session's stream in `roslyn.stream.log`.

## Closeout

Both halves closed, one commit each, direct to master. (a) 84debc8, (b) this commit.
One new gap recorded above: RazorSyntax cannot parse a code block holding Razor template
markup, which affects three pages and both guards, and needs its own finding.
