# leak-1: The recurrence guard is bypassable three ways

**Severity**: HIGH - the two page fixes are sound; the test written to stop the bug
recurring does not
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: see Closeout

## What is NOT wrong

Codex found no issue in the two page fixes, the ClickGate re-anchor, the nine re-anchored
`ProgressRegistry` pointers, the service-lifetime audit, or the module-only version bumps.
**The production defect is fixed.** This finding is entirely about the recurrence guard.

## Evidence - each bypass was EXECUTED against the compiled scanner, not argued

`ExchangeAdminWeb.Tests/EventSubscriptionLifetimeTests.cs`.

**(a) Helper-mediated subscription.** `:189` computes awaits only inside the method that
physically contains the `+=`; `:192-194` then declare the subscription safe when that helper
has no local `await`. Fixture: `OnInitializedAsync` awaits authentication, then calls
`SubscribeToJobs()`, which does `BulkJobs.JobChanged += OnJobChanged`. Scanner reported
`Method: SubscribeToJobs` with an EMPTY problem. Leaks; passes.

**(b) Non-dominating guard.** `:198-201` accepts the first disposed-guard found anywhere
lexically between the last `await` and the subscription. It never proves the guard
*dominates* the subscription. Two fixtures confirmed passing:

```csharp
await ...; if (showingOptionalPanel) { if (_disposed) return; } BulkJobs.JobChanged += OnJobChanged;
try { if (_disposed) return; } finally { BulkJobs.JobChanged += OnJobChanged; }
```

In the first, the guard is skipped whenever the branch is not taken. In the second, the
`finally` runs ON the guarded return.

**(c) Anonymous `delegate` not seen at all.** `:161-166` treats a right-hand side as a
delegate only when it contains `=>`. Fixture:
`BulkJobs.JobChanged += delegate (string jobId) { OnJobChanged(jobId); };` - scanner reported
**0 subscriptions**. Not merely unguarded: invisible. And an anonymous delegate cannot be
unsubscribed, so this shape is strictly worse than the bug being guarded against.

## Coder dispute

None. The implementing agent's five probes all passed because it probed the shapes it had
thought of, including a genuinely strong one - a brand-new eighth page. Codex probed shapes
it had not. That is the difference between self-review and review, and it is why
`AGENTS.md` forbids the former.

**This is the sixth vacuous-guard finding in five days.** The through-line is now specific
enough to name: **regex over source cannot see control flow.** Findings (a) and (b) are both
that - (a) crosses a method boundary, (b) crosses a branch. Every guard in this repo built
this way has been breakable in exactly these two directions.

## Approach

Roslyn, which is the second option and the only one that closes (b). The scanner is no longer
a regex over blanked text; it is a walk of a real syntax tree.

**Razor extraction.** Roslyn does not parse Razor, so `ExchangeAdminWeb.Tests/RazorSyntax.cs`
extracts each `@code { ... }` body by position - the closing brace found by LEXING with
`SyntaxFactory.ParseTokens`, so a brace inside a string, a comment or an interpolation hole is
not a brace - and parses each body wrapped in a synthetic `class` declaration so a member list
parses as one. Every tree position maps back to the raw file by one constant offset, so line
numbers and spans still address the file on disk. Markup is outside every region and no tree
covers it: assertions about MARKUP stay text-based on purpose, which is the same asymmetry
`prog-4` is about.

**The rule, restated.** On every control-flow path reaching a `+=` from the last preceding
`await`, a returning disposed-check must DOMINATE it. Dominance is structural: a guard is
credited only when it is an earlier statement in a statement list that encloses the
subscription. That is what rejects both (b) shapes - a guard nested in an unrelated `if`, and
a guard in a `try` whose `finally` subscribes, both sit in a statement list that is not on the
subscription's ancestor chain. Where the await is in a CALLER, the same check is applied at
the call site, recursively, which is (a).

## Files changed

- `ExchangeAdminWeb.Tests/RazorSyntax.cs` - new. Razor `@code` extraction and Roslyn parsing,
  shared with the progress scanner.
- `ExchangeAdminWeb.Tests/EventSubscriptionLifetimeTests.cs` - `EventSubscriptionScan`
  rewritten on the syntax tree; four bypass fixtures added, plus two "this must still pass"
  theories and a scanner anti-vacuity test.
- `ExchangeAdminWeb.Tests/ExchangeAdminWeb.Tests.csproj` - `Microsoft.CodeAnalysis.CSharp`
  named explicitly. It already resolved transitively through the app's
  `Microsoft.PowerShell.SDK`, so no package entered the graph and **nothing shipped gained a
  dependency**; the reference exists so a guard does not rest on another package's dependency
  list staying as it is.

## Guard proof

Each bypass was run against the OLD compiled scanner and then against the new one.

| Bypass | Before | After |
|---|---|---|
| (a) helper-mediated | 1 subscription, problem EMPTY - accepted | rejected: "is reached from OnInitializedAsync() ... no disposed-guard" |
| (b1) guard nested in an unrelated `if` | 1 subscription, problem EMPTY - accepted | rejected: "no disposed-guard dominating it" |
| (b2) guard in `try`, `+=` in `finally` | 1 subscription, problem EMPTY - accepted | rejected: "no disposed-guard dominating it" |
| (c) `delegate (string jobId) { }` | **0 subscriptions** - invisible | seen as a subscription, target `BulkJobs.JobChanged`, rejected |

Not-broken, both directions:

- The five live subscription sites (AdminBulkJobs, ConferenceRooms, GlobalProgress,
  InFlightWorkGuard, UsageTracker) still pass, including UsageTracker's, where the `+=`
  precedes every await in the method.
- The helper shape guarded AT THE CALL, and guarded INSIDE the helper, both pass. A rule that
  demanded the guard in one particular place would report every page that factors its
  subscription out.
- A guard and a subscription in the same branch passes; so does a guard in the block that
  encloses the branch. The rule is dominance, not "nothing nested".
- Mutation: deleting `if (_disposed) return;` from the live `AdminBulkJobs.razor` makes the
  rule fail, naming `AdminBulkJobs.razor:179 OnInitializedAsync`. Restored, green again.
- `TheLiveComponentsStillPresentSubscriptionsToJudge` is new: every rule here passes on an
  empty set, so a Razor-extraction bug that found no `@code` would make the suite green while
  proving nothing. It asserts the live components still hand the scanner work.

Full suite observed after the change: **3683 passed, 0 failed, 3 skipped** (baseline before
this slice was 3674/0/3). Build, `dotnet format --verify-no-changes` and `git diff --check`
all clean.

## Known gaps

- **Syntactic, not semantic.** There is no `Compilation` behind the trees, so a call is matched
  to a method by NAME. An overload, or a same-named method on another type, is treated as the
  page's own - over-inclusive, which is the safe direction for a guard.
- **`goto` is refused, not analysed.** A method containing any `goto` or label gets no guard
  credited at all. No component has one.
- **A call-graph cycle stops the walk** at the repeated method rather than reasoning about it.
  Whichever call site in the cycle really follows an unguarded await is still reached on its
  own arm, so nothing is lost; it is recorded because it is an assumption, not a proof.
- **Unremovability is still not an error.** An anonymous `delegate` or a lambda handler cannot
  be unsubscribed, which the finding notes is worse than being unguarded. This slice makes both
  forms VISIBLE and subject to the dominance rule - it does not add a new rule banning them.
  That would be a scope change; no component uses either form today.
- The reviewer could not reproduce the full suite on either of the last two reviews. This run
  was observed end to end on the coder side: 3683 passed in 4m48s.

## Reviewer comments

Change review of `e7be472..3204977`.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
Raw output: `.agents/review/leak.result.json`.

## Closeout

Pending.
