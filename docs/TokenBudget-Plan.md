# Token-Budgeted Implementation - Plan

Status: Draft - awaiting owner go to implement. D1 ruled and D2 withdrawn, both 2026-08-14. **No
owner decision is outstanding.** Intended for use from 2026-09-01, when the current work pause
lifts.

Owner request 2026-08-14: *"let's make an implementation plan that is token-budget friendly.
I will use that next month and see how it does. add something that tracks token usage as part
of the implementation so we can track."*

Two deliverables, one plan:

1. A **working protocol** for implementing the queued plans at materially lower token cost,
   derived from measured session data rather than from general advice. Its durable home once
   implemented is a new section in `.agents/repo-guidance.md`.
2. A **measurement tool** (`tools/Get-TokenUsage.ps1`) plus a committed baseline and a
   per-slice log, so the protocol's effect is observed rather than assumed.

Deliverable 2 exists because of deliverable 1: a cost-reduction protocol with no measurement
is a belief. The tool is what makes September comparable to August.

## Measured baseline

All figures below were measured on 2026-08-14 from the Claude Code session transcripts for this
project (`C:\Users\mcoelho\.claude\projects\D--source-ExchangeAdminWeb\*.jsonl`). They are this
repository's own numbers, not published benchmarks.

**August 2026, this project only** (the month's total spend was roughly twice this - other
projects account for the rest):

| Measure | Value |
| --- | --- |
| Requests | 7,876 |
| Mean context per request | 394,283 tokens |
| Largest single request | 954,008 tokens |
| Requests over 200K context | 5,693 (72.3%) |
| Input tokens | 1,444,721 |
| Cache-write tokens | 106,963,204 |
| Cache-read tokens | 2,993,850,720 |
| Output tokens | 5,527,623 |
| Estimated cost | $2,310.86 |

**Cost composition, August** - this is the part that determines which levers matter:

| Component | Share of cost | Share of tokens |
| --- | --- | --- |
| Cache read | 65% | 96% |
| Cache write | 29% | 3.4% |
| Output | 6% | 0.2% |
| Fresh input | 0.3% | 0.05% |

**A single day, 2026-08-14** (one plan drafted, two independent reviews, findings folded in):

| Measure | Value |
| --- | --- |
| Requests | 297 |
| Mean context | 280,102 tokens |
| Cache-read | 70,764,256 tokens -> $35.38 |
| Cache-write | 4,875,589 tokens -> $30.47 |
| Output | 299,472 tokens -> $7.49 |
| Estimated cost | $73.49 |

Rates used throughout: Claude Opus 5 first-party - $5.00/M input, $25.00/M output, cache read
$0.50/M, cache write (5-minute TTL) $6.25/M. **These are Anthropic list rates and this
deployment routes through a Portkey gateway to Vertex/Bedrock, which are separately priced.
Every figure in this plan is therefore an estimate, and the tool must say so wherever it prints
one.**

## The cost model, in one line

    cost ~= SUM over requests of ( context_tokens x cache_read_rate )
          + SUM over re-primes of ( context_tokens x cache_write_rate )
          + ( output_tokens x output_rate )

Everything below follows from it. **At the August mean of 394K context, each request costs about
20 cents before it does anything useful**; at today's 280K, about 14 cents. Output is a rounding
error. The two variables worth attacking are **context size** and **number of requests**, and
the third, **re-primes**, is the one that was invisible until measured.

### The finding that reorders the priorities

**Cache writes were 41% of today's cost while being 6% of the tokens.** A cache write costs
12.5x a cache read, so every re-prime of a 280K context costs about **$1.75**. Today's
4.88M cache-write tokens are roughly **17 full re-primes**.

The prompt cache has a **5-minute TTL**. Any gap longer than that - stepping away, waiting on a
long-running background task, a slow review - expires the prefix, and the next turn pays a full
re-prime. Several of today's re-primes are directly attributable to sitting idle while the two
codex reviews ran, each of which took minutes.

That means the two reviews cost more than their 1.9% share of input tokens suggests: the token
share is real, but the idle time they caused is billed separately as cache writes.

## Levers, ranked by measured impact

### L1 - Shrink the always-loaded prefix (largest single lever)

Every request in a session carries the governance and state files. Current sizes:

| File | Size | Approx tokens |
| --- | --- | --- |
| `.agents/state.md` | 138 KB | ~51,800 |
| `.agents/decisions.md` | 64.6 KB | ~24,000 |
| `.agents/repo-guidance.md` | 9.2 KB | ~3,400 |
| `AGENTS.md` + `CLAUDE.md` | ~6 KB | ~2,200 |

`state.md` alone is roughly **18% of a 280K request**, carried on nearly every turn of every
session. At the measured rate it costs on the order of **$6 to $8 per working day just to be
present**, before anything reads it deliberately.

It is also, by its own admission, carrying history: it still contains a `Deployed:` paragraph
from 2026-08-05 that its own Blockers section flags as stale, alongside fully-resolved work
streams whose detail belongs in the plan and decision documents.

**This repository already has a playbook for it, and this plan should not reinvent one.**
`.agents/playbooks/drift.md` is the isolated state-hygiene sweep, reached either through
`catchup` (which offers it as step 0 and runs it in a throwaway agent, so the main context pays
one summary line) or directly on the owner's words `playbook drift`.

Its checklist is the mechanism this lever needs, and its first item is the reduction:

> Rotate landed or superseded `## Now` entries in `state.md` verbatim to
> `docs/history/state-archive.md` (create on first use).

**Verbatim rotation, not summarisation** - which answers the risk this plan would otherwise have
had to carry. Nothing is lost; it moves to an archive a cold session can read on demand instead
of paying for it on every request. The playbook also handles the adjacent decay: `as of <commit>`
on volatile facts, counts pointed at rather than copied, and push-state lines deleted on sight.

So the action here is **run the existing playbook**, not design a bespoke trim. It needs no slice
in this plan and no owner decision beyond the word that invokes it.

**Target for the sweep: `state.md` under 10K tokens.** That is the measurable outcome; the
playbook is the method.

### L2 - Do not let the cache expire (largest surprise)

At $1.75 per re-prime, TTL discipline is worth more than most editing habits.

- **Work in sustained bursts.** A session with 17 idle gaps pays about $30 for the gaps alone.
- **When dispatching a long background task** (a codex review, a long build), either do other
  work in the same session while it runs, or accept the re-prime knowingly. Do not sit idle.
- **Do not switch models mid-session.** The cache is model-scoped; a switch discards it
  entirely and re-primes from zero. Switch at session boundaries only (relevant to D1).
- Prefer one long session per slice over several short ones: each fresh session pays a cold
  prime regardless.

### L3 - Fewer requests

297 requests in a day at ~14 cents of carried context each is ~$42 before any work. Reductions
that do not cost correctness:

- **Batch independent tool calls into one message.** Two greps and a read that do not depend on
  each other are one turn, not three.
- **Do not re-read a file already in context.** The transcript is the cache; re-reading pays
  twice.
- **Do not verify an edit by reading the file back.** `Edit` and `Write` fail loudly; a
  successful return is the verification.
- **Read the range, not the file.** The queued plans cite exact line numbers - use `offset`
  and `limit`, or `Grep` with context, rather than pulling a 2,000-line file for 30 lines.
- **Delegate wide searches to a subagent** when the answer is small and the search is broad: the
  subagent's context is discarded, and only its conclusion enters the main context. Do not
  delegate a single-file read - the subagent's own prime costs more than the read.

### L4 - Model tier per phase

Verified against the live model overview on 2026-08-14. **The owner holds API keys for every
Claude model except Fable, plus GPT-5.5 and Gemini.** Rebilling August's exact token mix:

| Model | Context | Price /MTok | August cost | vs Opus 5 | Verdict |
| --- | --- | --- | --- | --- | --- |
| Claude Opus 5 | 1M | $5 / $25 | $2,310.86 | - | Viable |
| Claude Sonnet 5 | 1M | $2 / $10 | $924.35 | **40%** | Viable |
| Claude Sonnet 4.6 | 1M | $3 / $15 | $1,386.51 | 60% | **Dominated** |
| Claude Opus 4.8 / 4.7 / 4.6 | 1M | $5 / $25 | $2,310.86 | 100% | **Dominated** |
| Claude Haiku 4.5 | 200K | $1 / $5 | $462.17 | 20% | **Disqualified** |

**The list collapses to exactly two live options, and that is the useful finding.**

- **Haiku 4.5 is disqualified on a hard constraint, not on judgment.** 200K context, and
  **72.3% of August's requests exceeded it**; the mean request is roughly twice its entire
  window. Recorded so it is not re-proposed.
- **Sonnet 4.6 is strictly dominated by Sonnet 5** - it costs *more* ($3/$15 against $2/$10)
  and is the older model.
- **Every older Opus is strictly dominated by Opus 5** - all priced identically at $5/$25,
  all less capable.

So there is no fine-grained tier ladder to tune. The choice is Sonnet 5 or Opus 5, per phase.

*Correction on the record:* an earlier revision of this plan priced Sonnet 5 at $3/$15 with an
introductory $2/$10 expiring 2026-08-31, and warned that September would cost 1.5x more. The
live documentation shows **$2/$10 as the standing rate with no expiry noted**. The cached figure
was stale; September runs at the 40% column. The pricing page remains the authority, and these
are first-party rates against a Portkey-routed deployment - still estimates.

### L4b - Do not starve the cheaper model

This follows from the cost composition and is counterintuitive enough to state plainly:
**output is 6% of cost, and thinking rides in output, so reasoning effort is nearly free in this
workload.**

At Sonnet 5 rates, today's entire 299K output tokens cost about $3. Doubling the thinking budget
costs a few dollars a day; carrying context costs tens. **Effort is not a meaningful cost lever
here - context and request count are.**

The conclusion: buy the cheaper model *and run it at high or extra-high effort*. Do not do the
intuitive thing of economising on both. Anthropic's guidance is `xhigh` for the hardest coding
and agentic work, and Sonnet 5 defaults to `high` on Claude Code - so the default is already
reasonable and `xhigh` is affordable on the hard slices. This is how most of the capability gap
gets bought back for almost nothing.

### L4c - The model assignment (D1, ruled)

The owner's stated principle - *"minimum model for reliable work, complex model for checking"* -
matches the measured cost shape exactly: implementation is 98% of tokens, review is 2%. Put the
cheap model where the volume is and the strong model where the judgment is.

| Phase | Model | Why |
| --- | --- | --- |
| **Planning** | Claude Opus 5 | Lowest volume, highest leverage. Today's plan review found three real defects *in a plan* before any code existed; a defect here multiplies across every slice. |
| **Implementation** | **Claude Sonnet 5 @ `high`, `xhigh` on hard slices** | 40% of Opus cost, 1M context, and the plans are written to be executed by exactly this - see below. |
| **Deterministic gates** | none (model-independent) | Build, `dotnet test` (1,701 tests), format, `Test-AsciiOnly.ps1`, mutation probes. Free, and the reason a cheaper implementer is safe here. |
| **Adversarial review** | codex / GPT-5.5 @ xhigh | Cross-vendor independence. 1.9% of tokens - keep it at full strength (L6). |
| **Escalation / adjudication** | Claude Opus 5 | See the escalation-ladder note below. |
| **Contested findings** | Gemini, owner-dispatched only | Third harness, per the `codereview` playbook's optional-adjudicator knob. Never self-dispatched. |

**Why Sonnet 5 is defensible for implementation specifically here.** The four queued plans are
unusually specified - exact file paths, line numbers, acceptance criteria, slice boundaries drawn
on compilation order - and `.agents/playbooks/plan.md` *requires* a plan be
*"implementable by a completely cold, less-capable agent than the one that wrote it"*. The
repository's own governance anticipates this choice. Behind the plans sit 1,701 tests, CI, the
ASCII gate and the mutation-probe discipline.

**The genuine risk, stated plainly.** Every one of today's five review findings was of the form
*the plan asserted something about code it had not read*. That is precisely the failure a less
capable implementer makes more of, not less. The mitigations are the deterministic gates, review
kept at full strength, and the revert trigger below - not optimism.

**A capability gain that comes free with the change.** Today the implementer and the reviewer are
both frontier-tier, which is why `.agents/review/harnesses.local.json` records codex's frontier
grade as `fallback`: escalation cannot buy a stronger adjudicator, so it halts to the owner
instead. Moving implementation to Sonnet 5 makes **Opus 5 a genuinely stronger adjudicator than
the implementer for the first time**, restoring a real escalation ladder. The cheaper implementer
does not weaken review - it is what makes escalation mean something again.

**Revert trigger, measured not felt.** Track admitted findings per landed slice in
`.agents/review/index.md`. If Sonnet-implemented slices produce materially more admitted findings
than the August baseline, or any CRITICAL/HIGH finding of a kind the deterministic gates should
have caught, move implementation back to Opus 5 and record why in `.agents/decisions.md`. The
cost saving is not worth a defect in an authorization path.

### L5 - One slice, one session

Each slice in the queued plans is sized to be independently committable. Size sessions to match:
start a session, land one slice, close it. Two reasons, both measured:

- Context grows monotonically within a session, so late turns in a long session are the most
  expensive turns in it. Today's mean was 280K; the tool will report the distribution so this
  can be seen rather than assumed.
- A session that outgrows the context window triggers compaction, which is a summarization pass
  over the whole accumulated context - paid at output rates on top of everything else.

### L6 - Keep review at full strength

Measured today: the two codex review dispatches were **1.9% of the session's input tokens and
7.3% of its output**. Reviews are one-shot against a large context; implementation is hundreds
of turns each re-carrying a large context. Two dispatches against 297 turns.

**Scope that claim carefully - and an earlier revision of this plan did not.** The 1.9% figure is
**tokens inside the Claude Code harness**. The reviewer runs on a different harness billing
through the gateway, so *none* of its spend appears in that measurement. The month-scale
reconciliation above found ~$915 of budget use invisible to Claude Code transcripts, and
reviewer dispatches are the leading candidate for the bulk of it.

**So "review is 2% of cost" may be wrong by an order of magnitude in dollars, and is unproven
either way.** What survives is narrower and still decisive: review consumes a negligible share of
the *implementation* budget, so **moving implementation to a cheaper model captures the saving
without touching review** - the two are independently billed. Five real defects surfaced today
from two dispatches; adversarial review remains the highest-value-per-token lever here.

**What is genuinely unmeasured, and should be:** the per-dispatch cost of a review. The codex
event stream reports usage (today: 1.61M input, 23.9K output across two dispatches), so the
figures exist - what is missing is the gateway's rate table. Until someone supplies it, do not
assert that review is cheap in dollars; assert only that it is cheap in Claude-side tokens, which
is what was measured.

The other review-related cost, and this one *is* Claude-side: the idle-gap re-prime named in L2.

## Deliverable 2: the measurement tool

### `tools/Get-TokenUsage.ps1`

Modelled on `tools/Test-CoverageFloor.ps1`, which is this repo's existing precedent for a
read-only measurement script with a committed baseline (`.agents/review/coverage-floor.txt`) and
Pester coverage (`tests/ps/CoverageFloor.Tests.ps1`).

Parameters:

| Parameter | Purpose |
| --- | --- |
| `-TranscriptRoot` | Directory of Claude Code `*.jsonl` transcripts. Defaults to the path recorded in `.agents/machines.md`; machine-specific, so never hardcoded in the script body. |
| `-Since` / `-Until` | Date bounds, inclusive. Default: the current month. |
| `-GroupBy` | `Day` (default), `Session`, or `Total`. |
| `-Model` | Rate table selector: `opus-5` (default), `sonnet-5`, `sonnet-5-intro`, `haiku-4-5`. |
| `-Baseline` | Path to the committed baseline; prints a delta column against it. |
| `-AsJson` | Machine-readable output for the per-slice log. |

Reported per group: requests, input, cache-write, cache-read and output tokens, mean and maximum
context, count of requests over 200K, and estimated cost.

Design points that are not obvious and must not be re-litigated during implementation:

- **Parsing.** A full `ConvertFrom-Json` on every line of ~100 MB of transcripts is far too slow.
  A pure regex extraction is fast but fragile. The tool does both: a cheap substring filter
  (`-notmatch '"output_tokens"'`) to skip the ~99% of lines that carry no usage, then a real
  `ConvertFrom-Json` on the survivors. This was measured during planning - the filter is the
  expensive part, and the surviving line count is in the low thousands per month.
- **Streaming read.** `[System.IO.File]::ReadLines` line by line, never `Get-Content -Raw` on a
  100 MB tree.
- **No `-PlanOnly`.** `.agents/repo-guidance.md` Architectural Invariant 4 requires every
  ops-script step to support `-PlanOnly` via `Invoke-PlanOrAction` / `Write-Plan`. That invariant
  governs steps with side effects; this script performs none - it reads transcripts and prints.
  Adding a dry-run flag to a read-only reporter would be a flag that does nothing. **Recorded as
  a deliberate, reasoned exemption rather than an oversight**, because a reviewer should
  otherwise flag it. Note also that `Write-Plan` and `Invoke-PlanOrAction` are defined locally
  inside `Install-ExchangeAdminWeb.ps1` and `promote-dev-to-prod.ps1` - there is no shared module
  to import even if the flag were wanted.
- **Cost is labelled an estimate at every print site.** Rates are Anthropic first-party; this
  deployment bills through Portkey to Vertex/Bedrock at partner rates. A bare dollar figure
  written into a repository file becomes durable truth - this is the `idm-2` failure class from
  today's review, where an unverified inference was stated as fact. The tool prints the rate
  table it used and the words "estimated, first-party rates" alongside any total.
- **`-Calibration <factor>`, defaulting to `1.0`.** Set it only from agreeing multi-interval
  evidence recorded in `.agents/token-log.md`, never from a single observation - see the
  correction below for why that rule exists.
- **The tool measures Claude Code transcripts and nothing else. This is its most important
  limitation and it must be printed with every total.** Measured 2026-08-14 across **all**
  project transcript directories for August: 14,448 requests, **$3,901 at list rates**, against
  **$4,816.26** reported budget use. The gap is **~$915, about 19% of the month, and it is
  invisible to this tool** - the reviewer harnesses (codex/GPT-5.5, any Gemini) bill through the
  gateway and write no Claude Code transcript. A run of this tool is a floor on spend, not a
  measure of it.
  **Correction, recorded because the mistake is instructive.** An earlier revision of this plan
  concluded from **one** interval that the model ran 1.8x *high* and wrote that into the design.
  The month-scale sample says the opposite - it runs ~19% *low*. The single interval was almost
  certainly transcript-flush lag at the measurement boundary: the "before" snapshot missed turns
  not yet written to disk, inflating the computed delta. **n=1 was never a calibration**, and the
  plan said so in the same breath as acting on it anyway. The defaulting-to-`1.0` rule above is
  what survives; the 1.8x figure is withdrawn.
- **ASCII only.** CI fails on non-ASCII in any tracked `.ps1`/`.psm1`.
- **Reads outside the repository.** The transcript root is outside the working tree and contains
  full conversation transcripts. The tool reads token counts only and must never print, copy or
  summarise transcript content - only the numeric `usage` fields.

### `.agents/token-baseline.json`

August 2026 measured figures, committed once, in the shape the tool emits with `-AsJson
-GroupBy Total`. This is what September is compared against. Mirrors the role of
`.agents/review/coverage-floor.txt`.

### `.agents/token-log.md`

One appended line per landed slice:

    2026-09-02  GroupMemberNesting S1  sonnet-5  reqs 41  mean-ctx 96K  est $8.10

**Appended as part of the existing paperwork motion.** `AGENTS.md` already requires that each
slice be committed and its paperwork closed in the same motion; this adds one line to that
motion. It is not a separate ritual and must not become one.

## Slices

Each slice is one commit and must build and test green on its own.

### S1 - the tool

`tools/Get-TokenUsage.ps1` per the design above. No baseline, no log, no protocol changes yet -
the tool stands alone and is useful on its own.

### S2 - Pester coverage

`tests/ps/TokenUsage.Tests.ps1`, following `tests/ps/CoverageFloor.Tests.ps1`.

Fixture-driven, with a small synthetic `*.jsonl` tree created in the test's temp directory:
**the tests must not read the real transcript root**, which is machine-specific, mutable, and
absent on CI. Cases: a known token mix produces the known cost for each rate table; day
grouping; session grouping; the over-200K counter; a malformed line is skipped rather than
throwing; an empty directory reports zero rather than dividing by zero; `-AsJson` round-trips.

Prove non-vacuity per the repo standard: change a rate in the table, confirm the cost assertion
fails, restore, confirm green.

### S3 - baseline and log

`.agents/token-baseline.json` generated from August, and `.agents/token-log.md` created with its
header and the first entry. Both committed.

### S4 - the protocol

A new **Token Budget** section in `.agents/repo-guidance.md` carrying L1 through L6 in condensed
form, plus the one-line-per-slice logging rule folded into the existing paperwork expectation.

Deliberately last: the protocol references the tool, and a protocol whose measurement does not
yet exist is the belief this plan was written to avoid.

### No S5 - the state.md reduction is not a slice here

An earlier revision of this plan carried a bespoke `state.md` reduction slice gated on an owner
decision. **Both were wrong: `.agents/playbooks/drift.md` already owns this**, and `state.md`
itself said so - *"Reconcile on the next `catchup`"* - in the same paragraph this plan quoted as
evidence of the problem.

The action is `playbook drift` (or `catchup`, which offers it). It runs in a throwaway agent,
commits once, and turns contested items into flags rather than changes. Nothing about it belongs
in this plan's slice list; L1 points at it and that is the whole integration.

## Acceptance criteria

- AC1 `Get-TokenUsage.ps1` reproduces the **token counts** in this document from the real
  transcripts, within rounding. **Deliberately not the dollar figures** - an earlier revision
  made reproducing this plan's own cost estimates the criterion, which would have tested the tool
  against a model already measured to be ~1.8x high rather than against reality. Tokens are
  observed; cost is inferred, and the inference is what needs calibrating.
- AC1b Over at least three intervals where a reported budget movement is known, the tool's
  calibrated estimate tracks the movement. Until that holds, `-Calibration` stays at `1.0`.
  Intervals shorter than a day are not admissible evidence: transcript-flush lag at the boundary
  produced the withdrawn 1.8x figure.
- AC1c Every total states that it covers Claude Code transcripts only and excludes reviewer-
  harness spend, which bills through the gateway and writes no transcript. A printed total that
  reads as whole-budget spend fails this criterion.
- AC2 The same run against a fixture tree produces exactly the fixture's expected numbers.
- AC3 Every printed cost is accompanied by the rate table used and an explicit estimate
  qualifier. No bare dollar figure reaches stdout or any written file.
- AC4 The tool never emits transcript content - only numeric usage fields, dates, and session
  identifiers. Asserted by a test that feeds a fixture containing a recognisable sentinel string
  and greps the entire output for it.
- AC5 Pester passes on a machine with no transcript root present.
- AC6 `Invoke-ScriptAnalyzer -Path . -Recurse` clean; `tools/Test-AsciiOnly.ps1` clean.
- AC7 The baseline file is valid JSON, matches `-AsJson -GroupBy Total`, and is diffable.
- AC8 A run over a single day completes in under 30 seconds on the real tree.
- AC9 September's first three slices each append one line to `.agents/token-log.md` in the same
  commit that lands the slice.
- AC10 After `playbook drift` has run: `state.md` is under 10K tokens and
  `docs/history/state-archive.md` contains the rotated entries verbatim. Not an acceptance
  criterion of this plan's slices - recorded here as the measurable outcome of the sweep, which
  is invoked separately.

## Verification

`Invoke-ScriptAnalyzer -Path . -Recurse` and `Invoke-Pester tests/ps` per
`.agents/repo-guidance.md`. No C# changes, so no `dotnet` build is required - state that
explicitly at completion rather than implying the full suite ran.

## Versioning

**No version bump of any kind.** No module behaviour changes and no shared application code is
touched; `tools/` and `.agents/` are outside both versioning rules
(`docs/ProjectConstitution.md`, Deployment And Versioning). Recorded because this repository has
twice shipped a wrong version by assuming a rule fired when it did not - the discipline runs in
both directions.

## What this plan does not claim

- **It does not promise a percentage.** The levers are ranked by measured cost composition, but
  the achieved saving depends on how the work actually goes. The tool exists precisely so the
  answer is measured in September rather than asserted here.
- **It does not reduce review.** See L6.
- **It does not change what gets built.** The four queued plans are unaffected; this changes how
  they are executed, not what they deliver.
- **It is itself not free.** Writing this plan cost tokens, and the measurement runs that produced
  its numbers cost more. The baseline was worth buying once; re-deriving it every session would
  defeat the purpose, which is why S3 commits it.

## Owner decisions

### D1 - RULED 2026-08-14, delegated to the agent by the owner

Owner: *"that's what I'm asking you. I have API keys for all the Claude models except Fable,
GPT-5.5, and Gemini. Pricing tiers I'm not sure about, but model capability is the driver here.
Minimum model for reliable work, complex model for checking. IF that makes sense. relying on you
to plan this. research models if needed."*

**The principle holds and is adopted.** It matches the measured cost shape: implementation is
98% of tokens, review is 2%.

The ruling is the assignment table in **L4c**, with L4 and L4b as its evidence. In short:
Opus 5 plans, Sonnet 5 at `high`/`xhigh` implements, the deterministic gates catch mechanics,
codex/GPT-5.5 reviews, Opus 5 adjudicates escalations, Gemini stays in reserve as an
owner-dispatched third harness for contested findings.

**Three findings shaped it, none of which were obvious before measuring:**

1. The model list collapses to two. Haiku is context-disqualified; Sonnet 4.6 and every older
   Opus are strictly dominated on price *and* capability. There is no ladder to tune.
2. Effort is nearly free in this cost profile, so the cheap model runs at high effort rather
   than being economised twice (L4b).
3. Moving implementation down a tier *upgrades* review, by making Opus 5 a genuinely stronger
   adjudicator than the implementer for the first time.

**A split-by-slice variant was considered and rejected.** Routing only "mechanical" slices to
Sonnet sounds prudent, but nearly the whole queue is authorization-adjacent - `GroupMemberNesting`
is protection checks end to end, `ProtectedGroupWriteTarget` is entirely authorization, and both
new modules carry protected-principal gates. Most slices would route to Opus anyway, capturing
little saving while adding a per-slice judgment call and a cache-discarding switch. The
phase-based split in L4c gets the saving without the per-slice decision.

Review stays at full strength regardless (L6). The revert trigger in L4c is the safety valve.

### D2 - WITHDRAWN 2026-08-14: the playbook already owns it

Owner: *"isn't this something we have a playbook for?"* - and it is.
`.agents/playbooks/drift.md`, reached via `catchup` or `playbook drift`.

**This should never have been put to the owner.** An existing playbook answered it, `state.md`
carried a note saying so (*"Reconcile on the next `catchup`"*), and this plan quoted the very
paragraph containing that note while treating the question as open. Presenting settled process as
a fresh decision costs the owner exactly the attention the governance is designed to protect.

The playbook is also better than the option set below. D2's option 1 proposed moving detail into
plan documents and carried a real risk of losing cold-start context; `drift` rotates entries
**verbatim** into `docs/history/state-archive.md`, so the information moves rather than being
rewritten. The mitigation this plan would have had to invent already exists and is stronger.

Two things were corrected on discovering this:

- **A push-status paragraph was deleted from `state.md`.** `drift.md` records a 2026-07-11 ruling
  that push status is never recorded in state files - git owns it, sessions check it live - and
  that any such line is *deleted on sight, not refreshed*. Earlier in the same session that wrote
  this plan, finding `idm-4` "fixed" that paragraph by removing its brittle commit count while
  keeping the paragraph. That fixed the symptom and preserved the violation.
- **L1 and the slice list were rewritten** to point at the playbook instead of specifying a
  bespoke trim.

The options recorded below are kept only as the reasoning that was superseded.

#### Superseded option set

The largest single lever, and the only proposal here that can destroy information.

1. **Reduce to under 10K tokens.** Resolved work streams move to their plan documents; `state.md`
   keeps current versions, in-flight work, next actions, blockers and open gaps - what `AGENTS.md`
   says it is for. Saves roughly 15% of every request in every future session.
2. **Trim only what is provably stale** - the `2.5.5` `Deployed:` paragraph its own Blockers
   section already flags, and nothing else. Small saving, no risk.
3. **Leave it.** Accept ~$6 to $8 per working day.

**The risk in option 1 is specific and worth stating plainly.** The pause to 2026-09-01 exists so
the queued plans are startable cold with no conversation, and `state.md` is the file a cold
session reads to achieve that. Reducing it is reducing exactly the artifact the pause was
protecting. The mitigation is that the detail moves rather than disappears - into plan documents
that a cold session reads anyway once it picks up a plan - but the mitigation must actually be
performed, which is what AC10 checks.

Not proposed: any automatic or scheduled trimming. A file that prunes itself is a file that loses
something nobody noticed.
