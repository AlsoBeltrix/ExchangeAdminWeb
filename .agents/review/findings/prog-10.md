# prog-10: Both Roslyn guards can be fooled by a local function or an overload

**Severity**: HIGH (event guard) + MEDIUM (progress registry). **Latent, not live** - no
current page carries either shape, verified by scan. These are guard holes, not page defects.
**Status**: Open
**Branch**: - (direct to main)
**Commit**: -

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

Pending. Codex's recommendations:

- (a) treat `LocalFunctionStatementSyntax` as a callable unit in the same recursive
  caller-window analysis, or fail closed on a subscription inside a local function unless the
  call site is proven guarded. **Reason about the invocation, not the declaration's position.**
- (b) build the method table from Roslyn declarations for component-level methods only, and
  either reject duplicate page method names or key entries by signature/arity. **At minimum,
  fail closed** on a duplicate name or a local-function match rather than first-match-wins.

Fail-closed is the cheap correct answer for both: no current page has the shape, so refusing
it costs nothing today and cannot silently pass tomorrow.

## Known gaps

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

Pending.
