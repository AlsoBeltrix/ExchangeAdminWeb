# Message Trace: retire the historical-search branch

Status: **Implemented 2026-08-07 in `4b976e9`. NOT YET PROVEN — the 7 manual checks below have not
been run, and the automated suite is not evidence here.** Owner approved 2026-08-07.

Landed as one commit rather than the four slices planned below: the deletions are interdependent
(removing the page branch orphans the service method, and the guard test asserts the result of
both), so splitting them would have left intermediate commits that either did not build or asserted
something untrue.

One correction found during implementation, recorded because it nearly repeated the original
mistake: slice 4's day-count guard was first written against `RunTrace`'s method body. The original
defect declared the comparison as a FIELD and only used the flag inside `RunTrace`, so the
body-scoped assertion passed while the reinstated defect was present. The guard is now scoped to the
whole page source. Caught by running the non-vacuity probe, not by reading the test.

Repairs a defect the owner found on dev/prod running `2.6.0`: a search wider than
9 days still says results will be emailed, instead of running the chunked search
that `03a9999` built.

## The defect

`Components/Pages/MessageTrace.razor:633` defines
`IsHistoricalRange => (endDate - startDate).TotalDays > 9`. At `:780` a search in
that range calls `RunHistoricalSearch()` instead of `RunRealtimeTrace()`.

So the 90-day chunking is in the build and the page walks past it. Every range
above 9 days is submitted to `Start-HistoricalSearch` and the operator is told to
wait for an email.

Worse than a stale label: `docs/MessageTraceAccuracy-Plan.md` records, from live
measurement, that `Get-HistoricalSearch` returns only a `FileUrl` on
`admin.protection.outlook.com` which requires an interactive portal sign-in. The
app cannot fetch it, and operators without cloud admin accounts cannot open it.
The page currently promises a report that its intended user may be unable to read,
in place of a search that would have worked in-app.

Four consequences of the same branch, all now false:

| Site | Currently |
| --- | --- |
| `:317` banner | states realtime covers only the last 10 days |
| `:292` Subject | disabled above 9 days |
| `:296` Message ID | disabled above 9 days |
| `:759` | rejects a Message ID search above 9 days outright |

### How it was introduced

`72b8047` deleted the historical branch, correctly, but was built on a false
premise about `Get-MessageTraceV2` and was reverted whole in `90486d2` -- the
correct deletion went with it. `03a9999` then re-implemented chunking in the
service and touched the page only for the 50-row bound and validation.
`git show 03a9999 -- Components/Pages/MessageTrace.razor` matched on "historical"
returns nothing: the branch was never restored to the delete list.

### Why no test caught it, and why that matters for this plan

1483 tests pass against the defect. `MessageTraceWindowPlannerTests` proves the
planner; the service tests prove chunking. **Nothing asserts which branch the page
takes**, because this repo has no bUnit harness.

This is the same blind spot that let `72b8047` ship broken with 1386 green. A
green suite is therefore not evidence for this plan, and slice 4 exists because of
it.

## Scope

In scope: the page's historical branch and the now-dead service method behind it.

Out of scope, explicitly:

- The chunking mechanism (`MessageTraceWindowPlanner`, `GetChunkedCloudMessageTraceAsync`).
  It is correct and stays untouched.
- On-prem tracking-log search. `Get-MessageTrackingLog` has no span limit and is
  already queried once for the whole range.
- The detail-export email path (`EmailSelectedDetails`, `MessageTraceDetailReport`).
  That is a *different* email feature, still wanted, and must not be disturbed.
  Only historical-search email goes.

## Decisions

**D1 -- delete `StartHistoricalSearchAsync`, or keep it dead?** DELETE.
This closes the D5 left open in `docs/MessageTraceAccuracy-Plan.md` on 2026-08-06.
It is the only route to >90-day data, but it cannot deliver a report in-app, so
keeping it invites a future caller to reintroduce exactly this defect. The plan
document records why it existed and why it went; that is the durable form.
`HistoricalSearchResponse` (`Models/LookupModels.cs:96`) has no other consumer and
goes with it.

**D2 -- re-enable Subject and Message ID above 9 days?** YES.
Verified in `Services/MessageTraceService.cs:397`: both parameters are passed to
`GetCloudMessageTraceAsync` for every window, so they filter each chunk correctly.
The disable was a property of the historical path, not of chunking.

**D3 -- keep the 90-day retention validation?** YES, unchanged.
`MessageTraceWindowPlanner.ValidateRange` still refuses a start older than 90 days.
That is a real Exchange limit, measured. Only the 10-day *branch* goes.

## Slices

One commit each, verified before the next starts.

### Slice 1 -- delete the page branch

`Components/Pages/MessageTrace.razor`:

- Remove `IsHistoricalRange` (`:633`) and `historicalSubmitted` (`:591`).
- `:292`, `:296` -- Subject and Message ID always enabled; placeholder back to
  `(optional)`.
- `:314-326` -- delete the historical banner and the read-only "Send report to" box.
- `:334-338` -- delete the "results will be emailed" success alert.
- `:757-764` -- delete the Message ID rejection and the `subjectFilter = ""` clear.
- `:780-787` -- `RunTrace` calls `RunRealtimeTrace()` unconditionally.
- `:818-858` -- delete `RunHistoricalSearch`.

`userEmail` and `ResolveOperatorEmailAsync` **stay**: the detail-export recipient
box uses them (`:685`).

### Slice 2 -- delete the dead service method

- `Services/MessageTraceService.cs:65-95` -- delete `StartHistoricalSearchAsync`.
- `Models/LookupModels.cs:96` -- delete `HistoricalSearchResponse`.
- Leave the `Start-HistoricalSearch` mention in `ExoConnectionPool.cs:232`: it is a
  comment about single-write retry semantics generally, not a caller.

### Slice 3 -- version and docs

- `Modules/ModuleCatalog.cs` -- MessageTrace `1.3.1 -> 1.4.0`.
  **No base app bump**: module-scoped behaviour (Constitution, Deployment And
  Versioning).
  Note: `1.4.0` was the number `72b8047` used and the revert took back; the three
  repair commits never restored it, so the catalog has understated the module
  through this whole work stream.
- `README.md` -- the Message Analysis section must stop describing a 10-day
  realtime limit and an emailed historical report.
- `docs/MessageTraceAccuracy-Plan.md` -- record that its "Implemented" status was
  true of the service and false of the page, and close D5 per D1 above.

### Slice 4 -- the guard that would have caught this

`ExchangeAdminWeb.Tests/MessageTracePageRoutingTests.cs`, source-level, in the
style of `PageAuthorizationRecheckTests` (which exists for the same reason: Razor
handlers are not reachable without a component host).

Assertions:

1. `MessageTrace.razor` contains no `IsHistoricalRange` and no `RunHistoricalSearch`.
2. `RunTrace`'s body calls `RunRealtimeTrace` and contains no branch on a day count.
3. The page source contains no `StartHistoricalSearchAsync` call.

A string-matching test is a tripwire, not a proof -- and that is exactly right
here. The defect was a *reintroduced branch*, and a tripwire is the only automation
that can see one when no harness can render the page. Stated as such in the test's
own comment so a later reader does not mistake it for behavioural coverage.

## Verification

```powershell
dotnet build ExchangeAdminWeb.slnx -c Release
dotnet test ExchangeAdminWeb.slnx
dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore
git diff --check HEAD
```

Non-vacuity: reinstate the historical branch, confirm slice 4's tests FAIL,
restore, confirm they PASS.

**The suite passing is necessary and NOT sufficient.** It passed against this
defect. The plan is not complete until the manual checks below run.

## Manual validation -- the actual gate

Requires a dev deploy. None of these can be automated here.

1. **A 30-day search returns rows in the page.** No email message, no "submitted"
   alert. This is the defect, directly.
2. **A 90-day search returns rows in the page**, and spans the full range -- check
   the oldest and newest rows against the dates asked for.
3. **No gap at a chunk boundary.** Nine windows share endpoints; confirm rows exist
   either side of a boundary date and that the same message is not listed twice.
4. **Subject filter works on a 30-day search** (field enabled, results filtered).
5. **Message ID search works on a 30-day search** -- previously refused outright.
6. **A 91-day search is still refused** with the retention message, not a raw
   cmdlet error.
7. **Detail export email still works** -- the feature that shares the word "email"
   and must not have been caught in the deletion.

Checks 1 and 3 are load-bearing. Check 3 is the one no unit test can reach: a
missing window is invisible, because the rows either side look continuous.

## Rollback

Four independent commits; revert any or all. The module holds no state and the
deletion changes no data.

## Risk

The deletion is mechanical and the compiler finds every reference to the removed
method and type. The residual risk is in slice 1's markup: absent Razor markup is
not a compile error, so a block deleted too greedily builds clean and fails only
on the page. This bit this repo before (`AdminUIRedesign`, scripted line-range
edits silently removed two markup blocks with a green build). Mitigation: diff the
markup region by eye before committing slice 1, and manual check 7 exercises the
neighbouring feature.
