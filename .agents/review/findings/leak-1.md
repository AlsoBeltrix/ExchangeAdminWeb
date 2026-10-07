# leak-1: The recurrence guard is bypassable three ways

**Severity**: HIGH - the two page fixes are sound; the test written to stop the bug
recurring does not
**Status**: Open
**Branch**: - (direct to main)
**Commit**: -

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

Pending an owner decision, recorded in the handoff: patch the regex again, or move this one
guard to a Roslyn syntax-tree check that can answer "does every path from the last await to
this `+=` pass through a returning disposed check". The second is the only shape that
actually closes (b).

## Known gaps

**Codex could not reproduce the full suite** - `dotnet test ExchangeAdminWeb.slnx` stalled
silently for several minutes and it stopped it rather than claim a result. This is the
SECOND consecutive review with that outcome, so it is a property of the environment, not a
coincidence. Targeted run `EventSubscriptionLifetimeTests`: 9 passed. The coder-side
3660-passed figure stands unconfirmed by a reviewer for the second time running.

## Reviewer comments

Change review of `e7be472..3204977`.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
Raw output: `.agents/review/leak.result.json`.

## Closeout

Pending.
