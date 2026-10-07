# Circuit Lifetime Leak - Plan

Status: **DRAFT - awaiting owner go.** Nothing below is authorised yet.

**`docs/ProductionMemory-Plan.md` is the parent and owns the ORDER and the measurement
gates - read it first.** This is step 1 of that sequence, and the only one of the three that
fixes an outright defect.

---

## Exec summary

*This is the only part of this plan the owner reads or is bound by
(`.agents/decisions.md` 2026-09-30, 2026-10-01). Everything below it is agent-facing.*

**What it does.** Fixes a bug that makes the app permanently hold on to a user's entire
session - every list they loaded, every file they uploaded - and never let go. Two pages have
it. It then adds a check that stops the same bug being written again, because it has already
been fixed twice in this codebase and missed twice.

**What it gets you.** Production is currently growing about 14 GB a day on a 32 GB server.
This is the only cause found so far that is genuinely unbounded - it does not level off, it
accumulates until the server runs out. Fixing it is what stops the app needing to be
restarted every couple of days.

**What it costs.** Small. Two pages get a one-line guard each; the pattern to copy already
exists in this codebase. The bulk of the work is the test that stops it recurring, and
re-checking the other pages for the same shape.

**Biggest risk.** The fix itself is near-trivial and well precedented. The real risk is
believing it is the whole answer. This is the only VERIFIED unbounded defect found - that is
why it goes first - but it is not proven to be the largest contributor, and a review named
five more places this app can hold gigabytes that nobody has ruled out. Fixing this and
seeing memory still climb would not mean this fix was wrong, and the parent plan refuses to
close the incident on this fix alone.

**One thing you should know.** This was already found, understood and fixed in this app in
August - there is a comment in `GlobalProgress.razor` describing this exact failure in plain
words. The fix was applied to two shared components and the two pages with the identical bug
were not touched. That is why this plan spends most of its effort on a test rather than on
the fix: the fix has been written before and did not stick.

**What approving authorises:** the two page fixes, the recurrence guard, and the audit of
remaining pages. It does NOT authorise changing Blazor's circuit retention settings, which is
raised as an open question below and needs its own decision.

---

## The defect

**Mechanism.** A component subscribes to an event on a DI **singleton**. The singleton
outlives every circuit. If the component is disposed before it subscribes, nothing ever
unsubscribes it, and the singleton's invocation list holds the dead component - and through
its injected `NavigationManager` and `IJSRuntime`, the whole circuit and its scoped container -
for the life of the process. Blazor's own disconnected-circuit eviction cannot reclaim it,
because an external GC root beats eviction.

**The window.** Both sites subscribe *after* two awaits:

| Page | awaits at | subscribes at | guard |
|---|---|---|---|
| `Components/Pages/ConferenceRooms.razor` | `:714` auth state, `:718` `AuthorizeAsync` | `:737` `BulkJobs.JobChanged +=` | none |
| `Components/Pages/AdminBulkJobs.razor` | `:155` auth state, `:163` `AuthorizeAsync` | `:170` `BulkJobs.JobChanged +=` | none |

`BulkJobService` is a singleton (`Program.cs:108`); `JobChanged` is declared at
`Services/Jobs/BulkJobService.cs:49`.

Both pages DO have a `_disposed` field (`ConferenceRooms.razor:764`,
`AdminBulkJobs.razor:186`) and both read it inside `OnJobChanged` to skip rendering. Neither
reads it before the `+=`. Their `Dispose` methods unconditionally `-=`, which is a no-op when
`Dispose` wins the race.

**The precedent, which is the reason this plan is shaped the way it is.**
`Components/Shared/GlobalProgress.razor:170-181` carries the fix AND an explanatory comment:

> "Dispose can run DURING the await above, on a circuit that went away while authentication
> was still resolving. It would find subscribed == false, unsubscribe nothing, and return -
> and then this continuation would subscribe a dead component to a SINGLETON event, where it
> stays reachable for the life of the process."

`Components/Shared/InFlightWorkGuard.razor:45-52` has the same guard. Commit `1ed797a`
applied it to `Components/Shared/` only. **The knowledge existed and the sweep was
incomplete.** A plan that only fixes two more files repeats that mistake.

**Prerendering widens the window.** Both pages are `@rendermode InteractiveServer` with no
`prerender: false` (`ConferenceRooms.razor:5`, `AdminBulkJobs.razor:5`), so
`OnInitializedAsync` runs on the prerender pass as well as on the circuit. A client that
aborts the prerender request disposes the renderer mid-`AuthorizeAsync`.

**Cost per occurrence.** One whole circuit. `ConferenceRooms` additionally pins
`finderCsvData`/`typeCsvData` (`:678`, `:693` - parsed rows from an upload capped at 16 MB)
and `finderRows`/`typeRows`/`detailsRows` (`:709`, `:710`, `:791`).

## Scope

**S1 - the two fixes.** A `disposed` check before each `+=`, copying
`GlobalProgress.razor:176` exactly rather than inventing a variant. One commit per page, per
repo guidance.

**S2 - the recurrence guard.** A test asserting the rule, not the two instances. Shape: for
every `+=` on a service event in `Components/`, the subscribing method either has no `await`
before the subscription, or a disposed-guard dominates it. This is a source-order check in
the same family as `ProgressRegistry` (landed `d5f3b3b`), and the same limitation applies -
it proves lexical order, not runtime reachability.

Deliberately written against the RULE. The agent survey found all seven current `+=` sites in
`Components/` have a matching `-=`; the two defects are not missing unsubscribes, they are
unguarded subscriptions. The guard must catch the eighth site nobody has written yet.

**S3 - the audit.** Re-check every component with an event subscription against the rule, and
record the result. The survey says the other five are clean; S3 verifies that independently
rather than inheriting it.

## Out of scope - each needs its own decision

- **Circuit retention settings.** `Program.cs:122-123` and `:452` register Blazor with no
  options lambda, so `DisconnectedCircuitMaxRetained = 100` and a 3-minute retention period
  are in force. 100 retained circuits, each able to hold a 16 MB upload's parsed rows, is a
  large standing ceiling - but it is a BOUND, not a leak, and lowering it changes behaviour
  for operators who briefly lose connectivity. Open question below.
- **Disabling prerender** on these two pages. It would narrow the window, but the guard
  closes it properly and turning off prerender has its own visible effect.
- Everything in the other two plans.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`
- `ClickGateRegistry` - `ConferenceRooms.razor` IS line-pinned (`ExpectedLineCount = 1576`),
  so a one-line insertion re-anchors every entry below it. Use
  `.agents/playbooks/clickgate-reanchor.md`, never a hand-computed offset. `AdminBulkJobs` is
  not line-pinned.
- Module version bumps for both modules; base app version does not move unless S2 touches
  shared code, which it should not.
- Non-vacuous proof: remove each guard, confirm the S2 test names that exact file and method,
  restore and `touch`.
- Codex review per slice.

## Manual acceptance - owner or operator

Nothing here proves the leak is gone; a source guard cannot. The real check is a memory
reading. After this ships, record the production worker's private bytes at deploy and again
24 hours later under normal traffic, and compare against the ~14 GB/day baseline measured
2026-10-07. **If growth continues at a similar rate, this was not the dominant cause** - which
is information, not failure, and points at `docs/DownloadMemoryRetention-Plan.md`.

## Owner gate

Go or no-go on S1-S3. The circuit-retention question is separate and listed with the other
open questions.
