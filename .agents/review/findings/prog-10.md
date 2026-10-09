# prog-10: Both Roslyn guards can be fooled by a local function or an overload

**Severity**: HIGH (event guard) + MEDIUM (progress registry). **Latent, not live** - no
current page carries either shape, verified by scan. These are guard holes, not page defects.
**Status**: In progress - (a) closed, (b) open
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

## Files changed

- (a) `ExchangeAdminWeb.Tests/EventSubscriptionLifetimeTests.cs` - `EnclosingCallable`,
  `DeferredProblem`, the `DeferredBody` and `NoEnclosingMethod` messages, the refusal at the
  top of `ProblemAt`, the class remarks, and six fixtures.
- (b) pending.

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

Pending.

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
