# prog-4: Two lexical blind spots remain in the progress scanner

**Severity**: MEDIUM x2 - guard false-negatives only. **No operator-facing defect; no
current page uses either shape** (verified: no `https://` attribute precedes an `@onclick`
or `@onchange` on any line under `Components/Pages`).
**Status**: Open
**Branch**: - (direct to main)
**Commit**: -

## (a) A comment inside an interpolation hole desynchronises the code view

`ProgressRegistryTests.cs:431` `SkipInterpolationHole` skips nested string and char literals
but does NOT skip comments before counting braces (`:447-450`).

Codex compiled `$"{Fmt(/* } */ "Payload.Call(")} tail"` under .NET 10, ran
`ProgressScan.CodeView` over it, and `Payload.Call(` survived into the code view. With a real
activity open earlier in the method, conditions 2 and 3 both evaluated true.

So a `Reported` entry can name a covered call that exists only as string text, and all three
conditions pass. Same counterfeit class as prog-3, one layer deeper.

## (b) A URL in a markup attribute erases later handlers on the same line

`ProgressRegistryTests.cs:123` builds the DISCOVERY view with a raw regex `//[^\n]*`, and
`:476` runs handler discovery over it. `https://` matches that regex.

Codex's probe: `<a href="https://example.test/x" @onclick="SlowHandler">`. `SlowHandler`
appears in `DeclaredMethods`, but `Handlers` returned **zero operations** - the URL blanked
the rest of the line before the handler regex ran.

**This is pre-existing**, not introduced by the prog-3 fix: the discovery view has stripped
comments by raw regex since S1. prog-3 moved the CODE scans to a proper lexer and left the
DISCOVERY view on the old regex, which is what exposed the asymmetry to a reviewer.

Predicted failure: a component puts real operator work behind an event attribute following a
URL on the same tag. `EveryOperationHandlerOnACoveredPageIsClassified` never sees it, no
registry entry is required, and slow or mutating work ships with no progress and no failing
test. Exactly prog-2's class.

## Coder dispute

None on either. Both are correct.

## The pattern, stated plainly because it now decides the next step

**Eighth guard finding in six days.** The prog-3 fix replaced a two-pass blanker with a
proper single-pass lexer, correctly, and was reviewed into two MORE lexical holes - one
inside the new lexer's interpolation handling, one in the view it deliberately left alone.

Three rounds on this one file have gone: regex -> partial blanking -> hand-written lexer,
each round closing the previous hole and exposing the next. **Hand-lexing C# and Razor is
not converging**, and each round costs an agent dispatch plus a review.

## Approach

Pending the owner's Roslyn decision, now open on `leak-1`, `prog-3` survivors and this. The
minimum patches are known and small - teach `SkipInterpolationHole` the same comment handling,
and stop treating `//` inside a quoted attribute as a comment - but that is round four of the
same cycle.

## Known gaps

Codex could not complete mutation verification: it attempted a disposable full run twice and
a direct one-mutation run once, and the builds stalled before useful output. It killed the
child `dotnet` processes and left the tree clean. Its findings come from reflection probes
against the COMPILED scanner, which is direct evidence for the two holes, but the fix's own
mutation coverage is reviewer-unconfirmed. Focused run observed: 25 passed.

## What this review CONFIRMED

- Every standard literal case holds: `$"...\"..."`, `$@"..."`, `@$"..."`, verbatim doubled
  quotes, nested literals in interpolation holes, char literals with quotes and escaped
  quotes, raw strings, and an empty string followed by real code.
- **The coder's one-pass argument is correct, verified mechanically**: the verbatim `//` row
  exposes `Payload.Call(` after a comments-first strip, while the plain-string row does not.
- **The non-discriminating row the coder kept is honestly labelled**, not padding.

## Reviewer comments

Change review of `9e32f52..c218401`.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
Raw output: `.agents/review/prog3.result.json`.

## Closeout

Pending.
