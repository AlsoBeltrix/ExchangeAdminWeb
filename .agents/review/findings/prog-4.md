# prog-4: Two lexical blind spots remain in the progress scanner

**Severity**: MEDIUM x2 - guard false-negatives only. **No operator-facing defect; no
current page uses either shape** (verified: no `https://` attribute precedes an `@onclick`
or `@onchange` on any line under `Components/Pages`).
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: see Closeout

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

Neither minimum patch was taken. Round four of the same cycle is the thing the finding says
not to do, so the hand-written lexer was DELETED - `SkipLiteral`, `SkipInterpolationHole` and
`SkipRawLiteral` are gone from the file - and the two views are now built from a real parse
(`ExchangeAdminWeb.Tests/RazorSyntax.cs`, added by the `leak-1` fix).

**(a) is structural now.** The code view blanks Roslyn comment trivia and string, char,
interpolated and raw literal tokens. There is no brace-counting hole skipper left to teach
about comments, because a parser does not find the end of an interpolation by counting braces.

**(b) was never a C# question, and that is the honest fix.** `//` is a C# comment marker, so
it is only treated as one INSIDE a `@code` block, where Roslyn says where the comments are.
In markup it is text - `https://` is the common case and a CSS `/* */` the other - and Razor's
own `@* *@` is what gets blanked out there. The asymmetry the finding is really about is now
stated rather than implied: C# questions are answered by a tree, markup questions by text.

**Razor extraction.** Roslyn does not parse Razor. Each `@code { ... }` body is taken by
position, its closing brace found by LEXING with `SyntaxFactory.ParseTokens` so a brace inside
a string or comment is not a brace, and parsed wrapped in a synthetic class; one constant
offset maps every tree position back to the raw file, which is what keeps both views
same-length and every existing index valid.

**One behaviour change beyond the finding, stated plainly**: the code view now blanks markup
whole. The character scanner used to blank quoted markup attribute values by accident, because
it took every `"` for a string delimiter; blanking markup outright is the same effect made
deliberate, and nothing in a component's markup is C# that a "does this method DO x" scan has
any business reading. `DeclaredMethods` also delimits method bodies over the code view instead
of carrying its own literal skipping, which is how the hand lexer's last caller went away.

## Files changed

- `ExchangeAdminWeb.Tests/ProgressRegistryTests.cs` - `StripComments` and `CodeView` delegate
  to `RazorSyntax`; the three hand-lexer methods deleted; `MatchDelimiter`, `StatementEnd` and
  `EnclosingBlockEnd` simplified to read the code view; `ScanText` added so discovery can be
  tested against fixture markup; seven tests added.
- `ExchangeAdminWeb.Tests/RazorSyntax.cs` - `@* *@` blanking for the markup half of the
  discovery view.

## Guard proof

Both bypasses were run against the OLD compiled scanner and then against the new one.

| Bypass | Before | After |
|---|---|---|
| (a) `$"{Fmt(/* } */ "Payload.Call(")} tail"` | payload survives into the code view = **True**, and 1 activity seen, so conditions 2 and 3 were both true on string text | blanked; `ACommentInsideAnInterpolationHoleCannotCounterfeitACoveredCall` asserts the payload is gone and the real activity above it survives |
| (b) `<a href="https://example.test/x" @onclick="SlowHandler">` | discovery view truncates to `<a href="https:` - handler attribute survives = **False** | handler discovered; `AUrlInAMarkupAttributeDoesNotEraseAHandlerAfterItOnTheSameTag` asserts it |

Mutation, on a live page, which is the half the reviewer could not complete:

- **(a) on `CloudPasswordReset.razor`.** Replaced the real `ResetService.DeriveDestination(`
  call in `ExecuteResetAsync` with an aliased call plus the counterfeit interpolation-hole
  literal, so the registered text exists only as string content. Condition 1 FAILS:
  "CloudPasswordReset.razor: ExecuteResetAsync does not call 'ResetService.DeriveDestination('".
  Before the fix the payload survived into the code view, so it would have been satisfied.
- **(b) on `CloudPasswordReset.razor`.** Added
  `<a href="https://example.test/help" @onclick="MutationProbeAsync">` and an unregistered
  `MutationProbeAsync`. `EveryOperationHandlerOnACoveredPageIsClassified` FAILS, naming
  `CloudPasswordReset.razor:256 MutationProbeAsync`. **The same mutated page was then run
  against the OLD scanner and PASSED** - the predicted silent ship, reproduced end to end.
- Both mutations reverted; tree clean.

Not-broken:

- All eight pre-existing rows of `NoStringFormSmugglesTextPastTheBlanker` still pass, including
  the verbatim `//` row the prog-3 one-pass argument rests on.
- The a3da1e3 asymmetry assertion `HandlerDiscoveryStillReadsQuotedMarkupAttributes` is
  unchanged and still true: discovery finds both quoted `@onclick` values, and
  `ExecuteResetAsync"` is absent from the code view. Blanking markup makes it hold more
  strongly, not less.
- New counterpart tests pin the other direction, so "discovery now finds everything" is not
  the fix: a handler inside `@* *@` is still not discovered, and a `//` comment inside `@code`
  is still blanked in the discovery view.
- `EveryCoveredPageStillYieldsItsDeclaredMethods` is new: every view in this suite is derived
  from `@code` extraction, so a page yielding an empty method table would make the whole suite
  green. Operations are deliberately not asserted per page - `Error.razor` legitimately has
  one method and no operator control at all.

Full suite observed after the change: **3690 passed, 0 failed, 3 skipped**. Build,
`dotnet format --verify-no-changes` and `git diff --check` all clean.

## Known gaps

- **A call written inside an interpolation hole is not seen as a call.** Interpolated strings
  are blanked WHOLE, holes included, which is what the character scanner did and what keeps
  the existing rows passing. A covered call written inside a hole would be reported as missing
  - loud, and the direction a guard should fail in - but it is a false positive waiting to
  happen if a page ever does it. None does.
- **`CodeView` takes text, not a file name**, so it decides "is this Razor" by whether the text
  carries a `@code` block. Every current caller passes either a whole page (all of which have
  one) or a C# method body (none of which do). A `.razor` file with no `@code` block handed to
  it as text would have its markup parsed as C#; `App.razor`, `MainLayout.razor`,
  `Routes.razor` and `_Imports.razor` are the four such files and no caller passes them.
  `RazorSyntax.ParseFile` decides by extension and is what the file-driven scanners use.
- **`DeclaredMethods` is still regex-anchored** for finding the DECLARATION; only the body
  delimiting moved to the tree. Replacing it outright would change which methods are
  discovered, hence which handlers resolve, hence what the registry has to account for - a
  scope change, not this finding.

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
