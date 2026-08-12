# ru-2: S1 registers a DI service that S2 introduces, so the first slice cannot build

**Severity**: MEDIUM -- following the slice table literally produces a first commit that
fails `dotnet build`, defeating the per-slice revertibility the slicing exists for.
**Status**: Verified
**Branch**: --
**Commit**: (this commit) -- plan revision

## Evidence

`docs/RiskyUsersModule-Plan.md` `## Slices` listed S1 as "Catalog descriptor +
`Program.cs` DI + catalog tests" while S2 was where `RiskyUsersService` is first written.
The same section required each slice to be committed independently, and
`## Verification` requires a build at each.

`builder.Services.AddSingleton<RiskyUsersService>()` names a type that does not exist
until S2, so the S1 commit does not compile.

## Predicted observable failure

An implementer follows S1, commits, and runs
`dotnet build ExchangeAdminWeb.slnx -c Release`. It fails with CS0246 on
`RiskyUsersService`. The likely recoveries are both bad: fold S2 into S1 (losing the
slice boundary), or commit a known-broken S1 (so the slice cannot be reverted to, which
is the property per-slice commits exist to give).

## What

A slice boundary drawn on conceptual grouping -- "all the registration wiring together"
-- rather than on compilation order. The descriptor and the DI line look like one kind of
work; only one of them has a code dependency.

## Approach

`Program.cs` registration moves from S1 to S2, into the same commit that introduces the
type. S1 is now catalog descriptor plus catalog test updates only, which has no code
dependency -- the descriptor is data in a list.

The slices section states the rule that produced the change ("every slice must build and
test green on its own commit") rather than only the corrected table, so a later slice
added to this plan is drawn on the same line. S2's own section repeats the placement and
why, because an implementer working slice by slice reads the section, not the table.

The stub alternative the reviewer offered (a compiling `RiskyUsersService` stub in S1)
was declined: it makes S1 build at the cost of committing a type with no behaviour and a
registration pointing at it, which is a worse artifact to revert to than no type at all.

## Files changed

- `docs/RiskyUsersModule-Plan.md` -- S2 section registration note, `## Slices` table rows
  S1/S2, per-slice build rule

## Guard proof

Not applicable: plan document, plan revision. The guard is mechanical and belongs to the
implementation -- build at the S1 commit before starting S2, which the plan now states as
an instruction rather than an assumption.

## Coder dispute (if any)

None. Verified by reading the plan's own slice table against its own S2 section.

## Known gaps

None. The same class of error would recur if a future slice is added between S1 and S2;
the stated rule is the mitigation.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier`
(grade: fallback -- frontier equals standard on this transport, owner-ruled 2026-08-03)

openreview over `d877294281f694ff3490af9cbedc5a2eb6ca68fa..a2c4c77ad41834b7899edb07e214088c51edd29e`,
verdict `acceptable_with_changes`, `capability_ok: true`, 2026-08-12.
