# Production Memory - Parent Plan

Status: **DRAFT - awaiting owner go.** Nothing below is authorised yet.

Parent of `docs/CircuitLifetimeLeak-Plan.md`, `docs/BoundedJobQueries-Plan.md` and
`docs/DownloadMemoryRetention-Plan.md`. Those three were first written as independent
plans; a review found that framing wrong, because they interact. **This document owns the
order and the measurement gates. Read it before any of the three.**

---

## Exec summary

*This is the only part of this plan the owner reads or is bound by
(`.agents/decisions.md` 2026-09-30, 2026-10-01). Everything below it is agent-facing.*

**What it does.** Sets the order for fixing production's memory growth, and puts a
measurement between each step so we find out whether each fix worked instead of assuming it.
Fix the one proven defect, bound the data it holds, measure, and only then decide whether the
expensive download rewrite is justified.

**What it gets you.** An answer, not just changes. Production is growing roughly 14 GB a day
on a 32 GB server; it already starved the machine badly enough to kill a tool mid-run. The
order here is cheapest-and-certain first, so the small fixes ship in days rather than waiting
behind a UI rewrite that may turn out to be aimed at the wrong thing.

**What it costs.** The first two steps are small. The third is large and is deliberately not
yet approved. Each step adds a 24-hour measurement before the next, so this is paced by
observation rather than by how fast code can be written.

**Biggest risk.** Declaring victory early. One verified defect has been found, and it is
genuinely unbounded, so it is tempting to call it the answer. A review named five more places
this app can hold multiple gigabytes - unlimited Exchange result sets, a 100,000-device
inventory, retained migration state - none of which has been ruled in or out. **The incident
is not explained until a measurement says so**, and this plan refuses to close on the fix
alone.

**One thing you should know.** We still have not taken the one twenty-second reading that
would tell us whether this memory is the app holding objects or something lower level. Every
hypothesis here is source reasoning until that number exists. It needs an elevated shell and
the script is already on your machine.

**What approving authorises:** the sequence and the measurement gates, plus the first two
work items by reference to their own plans. It does NOT authorise the download rewrite, any
garbage-collector limit, or any change to circuit retention settings.

---

## Order, and why this order

Each step ends with a measurement against the **2026-10-07 baseline: 13.8 GB working set /
15.0 GB private at 1.0 days uptime, production; 0.89 GB dev, same build and uptime; handles
1753 vs 1255 and threads 45 vs 41, both flat.**

**Step 0 - measure before changing anything.** Counters on the production worker: private
bytes, GC heap size, Gen2, LOH, allocation rate. Elevated shell;
`capture-exchangeadminweb-memory.ps1`. This single reading splits managed from native and
decides whether step 3 is real work or a wrong turn. **Do this before recycling the pool** -
a recycle frees the memory and destroys the evidence.

**Step 1 - the circuit leak** (`docs/CircuitLifetimeLeak-Plan.md`). First because it is the
only *verified unbounded lifetime defect* found: everything else is bounded per use, per tab
or per job. Also the cheapest, and the fix pattern already exists in the codebase.

**Step 2 - the unbounded job query** (`docs/BoundedJobQueries-Plan.md`). Immediately after
step 1, not independently. It is the multiplier: a leaked circuit holding a 10,000-row job is
how one leaked subscription becomes hundreds of megabytes. Landing it alone leaves the rows
pinnable; landing it after the leak fix bounds what a surviving circuit can cost.

**Step 3 - downloads and GC** (`docs/DownloadMemoryRetention-Plan.md`). **Gated on step 0's
number and on the re-measurement after step 2.** If the heap is mostly native, this plan is
discarded, not deferred.

**Step 4 - the unexplained remainder.** If growth continues after steps 1 and 2, work the
candidate list below. Do not close the incident before this step answers.

## Candidates nobody has ruled out

Raised by the plan review, with file:line, and NOT covered by the three plans. These are
hypotheses, not findings - none has been measured.

| Candidate | Evidence |
|---|---|
| Singleton EXO runspace pool | `Program.cs:297`, `Services/ExoConnectionPool.cs:62`, `:101`, `:418-459`, `:470-483` |
| Defender inventory, up to 100,000 devices | `Services/DefenderEndpointDeviceService.cs:101`, `:324-335`, `:399-422`; `Components/Pages/DefenderEndpointDevices.razor:384`, `:578-579` |
| `PermissionValidator` expanding EXO groups with `ResultSize Unlimited`, caching identities | `Services/PermissionValidator.cs:10-20`, `:384-424`, `:501-506` |
| Migration loading users and reports with `ResultSize Unlimited`, retained page and report state | `Services/MigrationService.cs:661-666`, `:942-957`; `Components/Pages/Migration.razor:1550`, `:1590`, `:1596`, `:3724`, `:3804` |
| Risky Users retaining a 10,000 default / 200,000 max result set | `Services/RiskyUsersService.cs:104-155`, `:407-408`; `Components/Pages/RiskyUsers.razor:469`, `:686` |

Note the interaction that makes these worse than they look: **a leaked circuit (step 1) that
had Defender, Migration or Risky Users open pins that page's full result set too.** The leak
is not only expensive in its own right; it is the mechanism by which every other large page
state becomes permanent.

These are the signatures to look for in a heap dump. They do not block steps 1 and 2.

## The ranking, stated precisely

Corrected after review; the first draft overclaimed.

1. **The circuit leak is first because it is the only verified unbounded lifetime defect** -
   not because it is proven to be the largest contributor. It is not proven to be that.
2. **Base64 downloads under Server GC are the leading MANAGED-HEAP magnitude hypothesis,
   pending counters.** Not a finding.
3. **Native sources - PowerShell runspaces above all - and large retained page graphs remain
   live alternatives** until the managed/native split is measured. Flat handle and thread
   counts argue against a runspace *handle* leak but do not exclude native heap growth.

## Verification

Each step's own plan owns its gates. This document adds one: **every step re-measures against
the baseline before the next begins.** A step that lands green tests and moves the number not
at all has told us something, and the next step must be chosen with that in mind rather than
run on schedule.

## Owner gate

Go or no-go on the sequence. The open questions it depends on are listed in the handoff and
are not restated here.

---

## Plan review, 2026-10-07

`openreview`, pins `9098dc9..bdb688b` (the three child plans as first written).
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier (`fallback`
alias; effort `max` re-probed 2026-10-06 and still rejected by this gateway).
Raw output: `.agents/review/memplans.result.json`.

**Verdict: acceptable with changes.** Six findings, all six applied. It independently
verified every load-bearing source claim - the two unguarded subscriptions, the guarded
precedent in `GlobalProgress`, `GetRows` unbounded against four bounded siblings, and the
absence of GC properties in the csproj. None was wrong.

1. **HIGH - the three plans are not independent.** This parent document exists because of
   this finding: the leak pins the very rows plan 3 bounds, and a heap limit changes
   behaviour while the leak is still present.
2. **HIGH - the GC heap limit must not be a first slice.** `hostingModel="inprocess"` means
   the limit applies inside w3wp, so it converts host starvation into `OutOfMemoryException`
   and possible pool instability rather than fixing a cause. Demoted to an operational
   option needing owner and ops acceptance, sizing, staging and rollback.
3. **MEDIUM - the measurement gate needed strengthening.** Private bytes alone cannot split
   the cases; the reading must capture GC heap, Gen2, LOH and allocation rate, preferably
   with an object-type breakdown.
4. **MEDIUM - five candidates nobody had looked for**, now the table above. This is the most
   valuable thing the review returned and the reason step 4 exists.
5. **LOW - the ranking overclaimed.** Rewritten: verified-unbounded is why the leak is first,
   not proven-largest.
6. **LOW - plan 3 must stay coupled to the leak fix.** Now step 2, sequenced, not independent.
