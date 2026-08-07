# Message Analysis: make the two exports comprehensible

Status: **Draft, awaiting owner approval.**

Owner, 2026-08-07, using the deployed app: *"message analysis won't let me export items. the
Download details button doesn't click. it's unclear how to get anything. email? it says export to
get them all but select all is capped at 50 and the download button doesn't work."*

## What is actually wrong

Nothing in the export code misbehaves. Every control does what it was built to do, and the page
never explains any of it — so a competent operator concluded it was broken. That conclusion is the
defect.

There are **two different exports** and the page never distinguishes them:

| Control | Scope | Contents | Delivery |
| --- | --- | --- | --- |
| **Export CSV** (top right) | ALL results (831 in the report) | Summary rows - the trace table | Immediate download |
| **Download details** / **Email details** | Selection, max 50 | Per-message delivery trail, one EXO call each | Live, or background job |

Four specific failures of explanation, each verified against the source:

1. **`MessageTrace.razor:367` — "export to get them all".** True only of Export CSV, which sits at
   the far right of the same line. The operator read it as pointing at the selection controls
   directly below, which cap at 50. The sentence is accurate and still misleading.
2. **`:383` — "Download details" is disabled at 50 selected.** Correct behaviour
   (`ResolveAction`: 1-10 -> LiveOrEmail, 11-50 -> EmailOnly), because each detail row is a
   separate Exchange round-trip. But a disabled button states no reason, and the explanation at
   `:404` renders *below* the buttons and reads as a note rather than an instruction. Owner
   experience: "the Download details button doesn't click."
3. **The 50 cap is a hard ceiling, not paging.** There is genuinely **no way to obtain delivery
   detail for all 831**, and the page never says so. Narrowing the search is the only route, and
   nothing suggests it.
4. **"Email details" does not read as the export path.** After the D4 recipient work the notify box
   is optional and clearable, so "email" now means "produce the export as a background job, and
   optionally send a link". The button name still says the old thing.

## Non-goals

- **Raising the 50 cap.** It exists because detail is one EXO call per message against a shared
  5-slot pool; 831 would be a long-running job that starves other operators. That is a real
  constraint, not an arbitrary limit.
- **Changing `LiveMax`, `EmailMax`, `ResolveAction`, or the job pipeline.** No threshold moves.
- **Paging detail exports across multiple jobs.** Materially more work, and it inherits the pool
  problem it would be trying to solve.

This plan changes what the page SAYS and how the two exports are presented. It changes no
threshold, no service, and no delivery mechanism.

## Decisions

**D1 - Rename the buttons.** "Download details" -> **"Download details (up to 10)"**;
"Email details" -> **"Export details as a job"**. The limit belongs on the control, not in a note
under it, and the second button's name should describe what it produces rather than its optional
notification. Recommended.

**D2 - Disabled controls must state why.** Add a `title` and inline reason when
`action == EmailOnly`: *"Live download is limited to 10 messages. Use Export details as a job for
this selection."* A disabled control with no explanation is what produced the report.

**D3 - Separate the two exports visually.** Move Export CSV out of the results-count line into the
selection panel, under a heading that names its scope: *"All 831 results, summary rows"* against
*"Selected messages, full delivery detail"*. Recommended: this is the root confusion and relabelling
alone will not fix it.

**D4 - Say the cap is a ceiling.** When the result set exceeds `EmailMax`, state plainly that detail
covers at most 50 per export and that narrowing the search is how to cover more. Currently the
operator is left to infer that no path exists.

**OQ-1 (owner):** should Export CSV also be offered when a search returns 0 results? It is hidden
today. Not blocking; it renders inside the results block.

## Slices

One commit each.

**Slice 1 - labels and reasons.** D1 and D2. Button text, `title` attributes on the disabled
states, and the EmailOnly explanation moved above the buttons. No layout change.

**Slice 2 - separate the exports.** D3 and D4. Export CSV moves into the selection panel with a
scope heading; the results-count line stops saying "export to get them all" and simply states the
count. Add the ceiling sentence.

**Slice 3 - guards.** Source-level assertions in the style of `MessageTracePageRoutingTests`:
the disabled Download button carries a reason; the results-count line no longer claims "export to
get them all"; both export controls are present and distinctly labelled. These are tripwires, not
behavioural coverage - stated as such in the test.

**Slice 4 - version and docs.** MessageTrace `1.4.0 -> 1.4.1` (module-scoped, no base app bump).
`README.md` Message Analysis section gains the two-exports table.

## Verification

```powershell
dotnet build ExchangeAdminWeb.slnx -c Release
dotnet test ExchangeAdminWeb.slnx
dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore
git diff --check HEAD
```

Non-vacuity: revert each slice-3 assertion's target, confirm FAIL, restore, confirm PASS. **Prove
the revert actually applied before trusting the result** - a probe whose edit silently matched
nothing has produced a false verdict twice in this repo already (`blr-3`, `blr-4`).

**The suite is not the gate.** Every one of these is markup, and no test renders this page. The
manual checks are the evidence.

## Manual validation

Requires a dev deploy.

1. Search returning >50 results. The results line states the count and does **not** say "export to
   get them all".
2. Export CSV is visibly labelled as all results, summary rows, and downloads all of them.
3. Select 5 rows: "Download details (up to 10)" is enabled and downloads immediately.
4. Select 50 rows: the button is disabled and **says why** on hover and in text; "Export details as
   a job" is enabled.
5. Run the job export with the notify box cleared: the export still appears on Downloadable
   Reports and no mail is sent.
6. An operator who has not seen this page before can tell, without being told, which control gives
   summary-of-everything and which gives detail-of-some. **This is the actual acceptance test** and
   the only one that judges the reported problem.

Check 6 is the point of the plan; 1-5 are its mechanics.

## Rollback

Four independent commits, all presentation. Revert any without touching behaviour.

## Risk

Markup-only, and absent Razor markup is not a compile error - this repo has lost whole blocks that
way with a green build (`AdminUIRedesign`). Diff the markup by eye before committing each slice, and
check 5 exercises the neighbouring job path to catch collateral deletion.
