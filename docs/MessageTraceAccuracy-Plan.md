# Message Trace Accuracy Plan

Status: **Draft - awaiting owner approval.** Nothing here is implemented.

Supersedes `docs/HistoricalSearchInApp-Plan.md` (never implemented) and repairs commit `72b8047`,
which is defective and is on `master`.

Reviewed by codex (gpt-5.5-dzs @ xhigh), two rounds, 7 findings, all verified against code and all
incorporated. Round 1: the swallowing mechanism was misattributed (see F3), chunking would have
re-queried on-prem, the revert silently drops a good validation check, three distinct result caps
were conflated, and two decisions were missing (D6, D7). Round 2: the slice-2 repro instruction was
impossible after slice 1's revert, and the existing per-call cap sits inside what becomes the chunk
loop. Round 2 confirmed D7's asymmetry is defensible. The round-2 corrections have not themselves
been re-reviewed.

## Why this plan exists

Commit `72b8047` widened the trace page to a 90-day window on the claim that
`Get-MessageTraceV2` serves that window in one call. **That claim was false and the commit is
harmful.** It was drawn from probes that used 2-hour windows at various offsets, which prove only
that old timestamps are addressable - not that a wide query works. No wide query was tested before
committing.

Everything below was measured against the live tenant 2026-08-06 with the app's own certificate
identity. Each figure is reproducible.

## Measured facts

**F1. The cmdlet enforces a 10-day maximum SPAN, at any offset.**
Same recipient (`Fu.Sun@analog.com`), window ending today: 8d -> 171 rows, 9d -> 180, 10d -> 183,
**11d -> 0**, 12d -> 0. Re-measured with the window ending 30 days ago: identical wall at 10/11.

**F2. The limit is span, NOT recency. Full 90-day retention is reachable.**
A 10-day window at increasing offsets: today -> 263 rows, -15d -> 251, -30d -> 183, -50d -> 332,
-70d -> 294, **-80d -> 205**. Data 80+ days old is available; it just cannot be asked for in one
wide call.

**F3. An over-span query raises a real, specific error.** The cmdlet returns a 400 whose message is
`"The interval between StartDate and EndDate can't be longer than 10 days."`

**Correction to an earlier draft of this plan, recorded so it is not repeated.** That draft claimed
the app hides this because the cloud path passes `-ErrorAction SilentlyContinue`. **That was wrong.**
The cloud call uses `ErrorAction Stop` (`Services/MessageTraceService.cs:376`) and `Invoke` throws
on both exceptions and `ps.HadErrors` (`Services/ExchangeServiceBase.cs:190`). The
`SilentlyContinue` I saw was in my own probe script, and I attributed my test artifact to the
application. Verify a mechanism in the code before prescribing a fix for it.

**What actually happens** must therefore be established by slice 2 rather than assumed. Traced so
far: the throw is caught at `Services/MessageTraceService.cs:434` and becomes
`response.Error = "Exchange Online trace failed: {message}"`. `GetMessageTraceAsync` then merges
cloud and on-prem, and a partial's `Error` is demoted into `merged.Warnings` (`:27-29`); it is
promoted back to `merged.Error` only when there are **no results at all** (`:40-41`). So with
`72b8047` live, a >10-day search that returns nothing shows the operator the real reason, but a
search where on-prem returned rows and cloud failed shows the cloud failure only as a **warning
banner beside a result set that silently omits every cloud message**. That second case is the
dangerous one and it predates `72b8047`.

Evidence this is not hypothetical: the prod audit log for 2026-07-29 carries repeated
`"Exchange Online trace failed: Object reference not set to an instance of an object."` entries for
real operator searches.

**F4. Chunking works and is fast.** Nine sequential 10-day windows covering the full 90 days for
one recipient: **2203 rows in 10.2 seconds**.

**F5. Chunk boundaries do not double-count.** Adjacent chunks (-20d..-10d and -10d..now):
170 + 263 = 433 rows, 433 distinct `MessageTraceId`. **Overlap: 0.**

**F6. `ResultSize` caps at 5000.** 5000 accepted; 5001 and 10000 rejected with an
`InternalServerError` from the service.

**F7. A saturated chunk truncates SILENTLY.** An unscoped 10-day window returned **exactly 5000**
rows in 12.9s with **no warning and no error**. Nothing in the result distinguishes "5000 messages
existed" from "more existed and you got the first 5000". This is the most dangerous finding here: a
truncated trace looks identical to a complete one.

**F8. Pagination exists and works.** `Get-MessageTraceV2` accepts `StartingRecipientAddress` as a
cursor alongside a narrowed `EndDate`. Page 1 (5 rows, ending 14:00:38) then page 2 seeded from that
row returned 5 further rows at 14:00:37 with **0 overlapping** `MessageTraceId`. Results are ordered
newest-first by `Received`, then by recipient.

**F9. The app's own cap is 1000** (`Models/LookupModels.cs:78`, `MaxResults`), below the service's
5000, and it sets `Truncated` when reached.

## What is actually wrong today

1. **`72b8047` is live on `master` and offers ranges that cannot work.** Any search over 10 days
   returns an empty page. Worse than the old behaviour, which at least emailed a report.
2. **Over-span errors are swallowed**, so the cause is invisible to the operator (F3).
3. **Truncation is invisible at the service boundary** (F7). Even within 10 days, a busy query
   silently loses rows. This predates `72b8047`.
4. The old historical path delivered results only to a cloud-admin mailbox - the original
   complaint, still unaddressed.

## Design

### Slice 1 - revert `72b8047` (do this first, alone)

A plain `git revert`, its own commit, no other change. `master` returns to the working 10-day split.
Everything after this slice is built on a known-good base. The revert must NOT be bundled with the
fix: if anything in the fix goes wrong, the revert has to stand on its own.

**Known cost of the revert, to be repaid in slice 3.** `72b8047` contained one genuinely good
change amongst the bad: a start-AGE check. The reverted-to validator
(`Components/Pages/MessageTrace.razor:679-690`) bounds only ordering, width and future dates, so a
narrow window older than Exchange's retention still passes validation and fails at the service.
Slice 3 must restore that check with its test, using the same inclusive `EffectiveEndDate`
semantics the page already applies (`:673-677`, end date + 1 day, clamped to now). Do not let it
disappear silently with the rest of the commit.

### Slice 2 - a cloud failure must never read as "no cloud results"

**Reproduce before fixing - but NOT through a >10-day UI search.** F3's mechanism was got wrong
once already, so this slice starts with a reproduction. The obvious one is unavailable: after slice
1's revert, `IsHistoricalRange` (`Components/Pages/MessageTrace.razor:601`) routes anything over 10
days to `RunHistoricalSearch` (`:720-726`), so it never reaches `GetMessageTraceAsync` at all -
realtime service calls happen only in `RunRealtimeTrace` (`:735-744`). An implementer following a
"do a 90-day search and watch" instruction after the revert would observe nothing and conclude the
bug is absent.

Reproduce at the **service** level instead, with a deterministic test that supplies a failing cloud
partial and a succeeding on-prem partial to the merge. That is where the defect lives, it needs no
live EXO, and it does not depend on which UI path is wired up at the time. (If a live UI repro is
wanted for confirmation, it must be done **before** slice 1 lands, while `72b8047` still permits
wide realtime queries.)

The defect to fix is the second case. When the cloud partial fails and the on-prem partial
succeeds, `GetMessageTraceAsync` demotes the cloud error to a warning (`:27-29`) and returns the
on-prem rows as the result set; the promotion back to `Error` only fires when the merged set is
empty (`:40-41`). The operator gets a plausible-looking table that is missing every cloud message,
with the reason in a warning banner above it.

That is Known Failure Class #2 in its purest form: partial success reported as success. The fix is
that a **failed backend must be distinguishable from an empty backend** in the result, and the page
must render a partial result as partial - not as a complete table with a warning. Whether a partial
cloud failure should block the whole result is D7.

This slice is independent of chunking and worth landing even if slices 3-5 never happen.

### Slice 3 - chunked queries: any range up to 90 days, in-app

Split the requested range into <=10-day windows and query them in sequence (F1, F2), concatenating
results. Proven: 9 chunks, 2203 rows, 10.2s, zero duplicates (F4, F5).

**Chunk the CLOUD branch only.** `GetMessageTraceAsync` (`Services/MessageTraceService.cs:15-43`)
queries Exchange Online and on-prem **in parallel** via `Task.WhenAll` and merges the two result
sets. The 10-day span rule is a `Get-MessageTraceV2` limit; the on-prem
`Get-MessageTrackingLog` path has no such limit and must keep receiving the operator's full range
as one query. Chunking the merge, or chunking both branches, would issue N pointless on-prem
queries and risk duplicating on-prem rows - the zero-overlap property measured in F5 was measured
on the CLOUD path only and says nothing about on-prem. The merge, the `MaxResults` trim and the
`Truncated` flag stay where they are.

Rules the implementer must not get wrong:

- **Chunk boundaries are half-open.** Measured overlap is 0 with adjacent windows sharing an
  endpoint (F5), so do not "fix" a duplicate problem that does not exist by adding a fudge factor
  that would instead create a gap. Assert the zero-overlap property in a test.
- **Sequential, not parallel.** All EXO work funnels through one 5-slot pool
  (`Services/ExoConnectionPool.cs:73`); 9 parallel chunks would fight for slots and invite
  throttling. 10.2s sequential is acceptable.
- **A failed chunk fails the search.** Known Failure Class #2: returning 8 of 9 chunks as though
  complete is a wrong answer presented as a right one. Report which window failed. (This is
  deliberately stricter than D7's rule for a whole failed backend: a missing backend can be named
  in the UI, whereas a missing window is a hole *inside* one backend's answer and is invisible.)
- **Collect first, cap last. The existing per-call cap must move out of the chunk loop.** The cloud
  mapper today breaks as soon as `allResults.Count >= MaxResults`
  (`Services/MessageTraceService.cs:419-423`) - inside what will become one chunk's mapping. Left
  there, the cap is consumed by whichever chunks run first: iterate oldest-to-newest and the newest
  messages are never mapped at all, while the final sort (`:32-38`) makes the result look ordered
  and complete. **Required shape:** map every chunk with no early break, carry per-window
  saturation metadata (slice 4), sort descending once across the whole set, then apply the app and
  merge caps. If a per-chunk guard is kept for memory safety it must be the service `ResultSize`
  ceiling, never `MaxResults`, and it must record saturation rather than silently stopping.
- **Cap the work.** A 90-day unscoped query is 9 chunks x up to 5000 rows. See D2.
- **The final trim silently discards the OLDEST chunks, which is backwards for this feature.**
  (Slice 4b changes where this bites but does not remove it: the page shows the newest 50 by
  design, which is honest as long as the true total is stated - but the EXPORT must carry the full
  set, so the `MaxResults` trim must not be applied to the export path.)
  The merge does `OrderByDescending(r => r.Received).Take(MaxResults)`
  (`Services/MessageTraceService.cs:32-36`). The measured 90-day scoped search returned **2203
  rows** (F4) against a 1000 cap, so 1203 rows would be dropped - and because the sort is
  newest-first, every dropped row is from the OLDEST part of the range. An operator who widened the
  window specifically to find something 80 days ago would get only recent mail, flagged with a
  generic "results limited" banner that does not say the old end of their range was cut off. This
  interacts with D2 and must be settled with it: raising the cap alone narrows the window of harm
  without closing it. At minimum the truncation message must state that the oldest results were
  dropped and name the effective date floor actually returned.

### Slice 4 - make truncation impossible to miss (the important one)

F7 is the finding that matters most: today a truncated result is indistinguishable from a complete
one, and that is true *before* any of this plan's changes.

**There are THREE separate caps in this path and they must not be conflated.** An earlier draft
treated them as one, which would have produced a detector that fires on the wrong condition:

| # | Cap | Where | Meaning |
|---|---|---|---|
| 1 | Service ceiling, `ResultSize 2000` as called (max 5000 per F6) | `MessageTraceService.cs:375` | how many rows EXO will return for one call |
| 2 | App cap, `MaxResults = 1000` | `Models/LookupModels.cs:78`, enforced `MessageTraceService.cs:419` | mapping stops early and sets `Truncated` |
| 3 | Merge cap, `MaxResults` again | `MessageTraceService.cs:32-38` | final trim across cloud + on-prem |

Cap 2 fires *before* cap 1 is reachable today (1000 < 2000), so the current `Truncated` flag means
"the app stopped mapping", not "EXO had more". Those are different facts and only the first is
currently observable.

- **Detect saturation on the RAW cmdlet result count**, before mapping, `MessageId` filtering or
  app capping. A raw count equal to the requested `ResultSize` means "at least this many" and must
  be treated as possibly-incomplete. Measuring it after the mapping loop would conflate cap 1 with
  cap 2 and misreport both.
- **Never infer completeness from the absence of a warning** - measured, there is no warning (F7).
- **Report per-window.** A 90-day search that saturated in one chunk is still complete in the other
  eight; the message must name which window and what to do (narrow the range, or add a
  sender/recipient).
- **Keep the three states distinct in the response**, since an operator's next action differs:
  service saturated (narrow the window), app cap hit (raise it or narrow), backend failed (retry or
  escalate).

Optionally, paginate a saturated chunk to completion using the `StartingRecipientAddress` cursor
(F8). That turns truncation into slowness rather than data loss. See D3 - it is a real capability
but it multiplies query count and belongs behind an explicit decision.

### Slice 4b - the render boundary: 50 on the page, everything in the export

**Owner direction 2026-08-06: "render the first 50, and have an export option for more. export
should show what the UI shows. what the UI shows needs to be maximally useful."**

This settles the result-cap question and replaces D2. The cap was being treated as a number to
tune; it is actually a boundary between two different destinations.

**Why a page cannot be the destination for bulk results.** The results table is a plain
`@foreach` over `response.Results` with no virtualization
(`Components/Pages/MessageTrace.razor`, results table). This is Blazor Server: every row is
rendered server-side and pushed to the browser over the SignalR circuit, and the render tree is
held in server memory for the life of the page. Tens of thousands of rows degrade the circuit;
hundreds of thousands would take down a shared process, harming other users' sessions. Raising
`MaxResults` from 1000 to 5000 would have walked toward that edge, not away from it.

So:

- **The page renders the newest 50 rows.** Fixed, not configurable. Enough to recognise the
  message being investigated, cheap enough that a wide search never threatens the circuit.
- **The page always states the true total**, e.g. "Showing the newest 50 of 2,203 found". The
  count comes from the full chunked result set, not from what was rendered - an operator must
  never mistake the page limit for the size of the answer. This is the same honesty rule as
  slice 4; a display limit and a truncated query are different facts and must read differently.
- **Export delivers the whole set**, built server-side and streamed, never assembled into HTML.
- **Export contains exactly the columns the page shows, in the same order.** The export is the
  page, extended - not a second, differently-shaped artifact. Anything added to one is added to
  the other, and a test should pin that so they cannot drift.

**"Maximally useful" is the requirement for the column set**, and it applies to page and export
alike. The audience is L1/L2 closing a ticket, and the question is almost always "where did this
message go and why". The current summary set is date, sender, recipient, subject, status, message
id, trace id, size, sender IP, recipient IP, backend. Two things it lacks that answer that
question directly, and which the app already has:

- **the failure/defer reason** - the `Detail` text from the delivery trail, which is literally the
  answer to "why did this not arrive" and today is reachable only by expanding one message at a
  time;
- **the final delivery event and where it landed** - the last hop's event and destination host.

Adding those to the summary row is the difference between an export that proves a message existed
and one that closes the ticket without a second query. What must NOT happen is bolting the whole
per-hop trail onto every row: that is the multi-shape file slice 5 exists to fix, and the trail
belongs to the detail export.

Cost to check before committing to it: the trail currently requires a per-message
`Get-MessageTraceDetailV2` call, so populating a reason column for N messages is N extra service
calls. That is fine for 50 rendered rows and potentially expensive for a 2,203-row export. Measure
it; if it is too slow for large exports, populate the reason for the rendered page and for failed
messages only, and say so in the column header rather than silently leaving blanks.

### Slice 5 - the detail export CSV

Currently unreadable: one file containing three different row shapes stacked
(`Services/MessageTraceDetailReport.cs:63-118`), so Excel parses the first line as a header and
everything else lands in one column. Owner direction 2026-08-06: **the CSV must be maximally
useful**, and the audience is L1/L2 closing tickets.

One row per message. Columns: the full summary set the page already exports (date, sender,
recipient, subject, status, message id, trace id, size, sender IP, recipient IP, backend), plus the
delivery trail. The trail is what this export exists for and what the summary export lacks; the
failure/defer reason within it is usually the answer to "why did this not arrive", so it must not be
buried at the end of a long cell. Exact trail encoding is D4.

## The operator must never learn these rules

Both limits are ours to enforce, not the operator's to memorise. Owner direction 2026-08-06 on
avoiding frustration:

- **Make bad dates unpickable.** The date control is bounded to the rolling retention floor
  (today minus 90 days) and today. A range that cannot work should not be selectable; there is then
  no error to write, because there is no mistake to make. Note the floor MOVES daily, so it is
  computed per render, never cached across a long-lived circuit.
- **Never surface the 10-day span rule.** It is an implementation detail of the cloud backend. The
  operator picks any range inside retention; chunking is invisible. The only observable effect is
  elapsed time on a wide search, so a wide search needs a progress indication rather than a page
  that appears frozen (measured: ~10s for a 90-day scoped search, F4).
- **Defaults and presets over typing.** Keep a short default window, and offer one-click
  *Last 7 days* / *Last 30 days* / *Full retention* so the common cases need no date entry.
- **When refusal is unavoidable, name the cause, not the mechanism.** A start date pasted from an
  old ticket gets "Exchange Online keeps 90 days; the oldest searchable date is <date>", never a
  cmdlet error. This is the same principle as slice 2: the current failure mode is an empty page
  that reads as "no such mail", which is the single most expensive thing this feature does to
  L1/L2 time.

## Owner decisions - open

**D1. Does the trace page offer up to 90 days, or stay at 10?** Chunking makes 90 achievable (F4).
Recommend **90**, since reaching old data without a cloud admin account is the original ask.

**D2. CLOSED by owner direction 2026-08-06** - see slice 4b. The page renders 50; the export
carries the full set. The question was wrongly framed as "what number should the cap be": raising
`MaxResults` toward 5000 would have pushed a Blazor Server circuit toward the volume that breaks
it. There is still a ceiling on how much a single export may hold, but it is bounded by memory and
export time rather than by what a table can display - to be measured in slice 4b, not guessed at
here.

**D3. Paginate saturated chunks?** (F8) Turns silent data loss into a slower but complete answer.
Costs more queries and time. Recommend **yes for scoped searches, no for unscoped**, but this is a
real fork.

**D4. Trail encoding in the CSV.** One column of `time Event: Detail | time Event: Detail`, or
separate columns for the first/last event plus a full-trail column. Recommend the latter: it puts
the outcome and its reason in their own filterable columns, which is what a ticket needs.

**D6. Do >10-day searches include on-prem tracking results?** Blocking, and currently
self-contradictory in the product: the long-range UI banner says on-prem tracking is realtime-only
(`Components/Pages/MessageTrace.razor:317`), while `GetMessageTraceAsync` always merges on-prem for
every realtime search (`Services/MessageTraceService.cs:17-19`). Options: (a) chunk cloud, query
on-prem once for the full range, merge as today - one answer, both backends; (b) cloud-only beyond
10 days, labelled in the UI. Recommend **(a)**: on-prem has no span limit, so a single query covers
the range, and an L1/L2 tracing a hybrid delivery needs both halves.

**D7. Should a partial backend failure block the whole result?** From slice 2: today a cloud
failure with on-prem rows renders as a complete-looking table plus a warning. Options: (a) show the
partial result but mark it unmistakably as partial, naming the failed backend; (b) fail the whole
search. Recommend **(a)** - on-prem-only results are genuinely useful for on-prem delivery
questions, and the honest-reporting requirement is met by labelling, not by withholding. This
differs deliberately from the chunk rule, where a failed chunk fails the search: chunks are slices
of one backend's answer and a hole inside them is invisible, whereas a whole missing backend can be
named.

**D5. Keep `StartHistoricalSearchAsync` at all?** With 90 days reachable in-app, its only remaining
use is >90-day data, which Exchange does not retain for trace. Recommend **delete it** once slice 3
lands, rather than leave a method whose report cannot be retrieved by this app
(`admin.protection.outlook.com` requires an interactive sign-in - measured 2026-08-05).

## Non-goals

- Retrieving `Start-HistoricalSearch` reports into the app. Measured impossible: the `FileUrl`
  redirects to `login.microsoftonline.com` and returns a sign-in page for the app identity.
- Changing the on-prem tracking-log path.
- Any change to the bulk job runner. Its defects (no early stop, registry keyed on module id alone,
  strictly serial) are recorded in `docs/HistoricalSearchInApp-Plan.md` and are not touched here.

## Verification

Per `.agents/repo-guidance.md`: build, `dotnet test ExchangeAdminWeb.slnx`, format check,
`git diff --check HEAD`. Every new test proven non-vacuous by reverting its guard.

**Live checks - mandatory, and the reason this plan exists.** `72b8047` passed 1386 unit tests and
was still wrong, because no test could see the service's 10-day rule. Nothing here is "done" until
these run against real EXO:

1. A 30-day scoped search returns rows (not zero) and its total matches the sum of its chunks.
2. A search whose span exceeds 10 days never returns a silent empty result.
3. A deliberately saturating chunk (unscoped 10-day window) is reported as truncated, not
   presented as complete.
4. An 85-day-old narrow window returns rows, proving retention reach.
5. Chunk boundaries lose nothing: sum of chunk row counts equals a distinct count over the union.
