# kd-3: ClickGateSource.ExtractBlock counts braces without being quote-aware

**Severity**: LOW - a test-infrastructure defect. `ExchangeAdminWeb.Tests/ClickGateSource.cs` `ExtractBlock` (about 364) ends a block at the wrong place when a brace sits inside a string literal; `Tags()` in the same file is quote-aware. A guard reading a mis-sized block can pass or fail the wrong code.
**Status**: Verified on branch; awaiting merge and codex review
**Branch**: `fix/kd-misc` (worktree `D:\source\ExchangeAdminWeb-wt\kd-misc`, from master `eb017102`)
**Commit**: -
**Reviewer**: implementer (recorded in `.agents/state.md` "Known defects", re-verified 2026-10-23 as of `d881ea58`)

## Evidence

As above. Trigger: a handler body containing `"{"` or `'}'` in a string. Predicted failure: the extracted block is truncated or over-extended, and every ClickGate assertion over it reads the wrong text.

## Approach

Re-read at `eb017102`; every line below is that commit's.

- `ExchangeAdminWeb.Tests/ClickGateSource.cs:364-383` - `ExtractBlock(string source, string opener)`:
  `var start = source.IndexOf(opener, StringComparison.Ordinal);`, `var open = source.IndexOf('{', start + opener.Length);`,
  then `if (source[i] == '{') depth++; else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];` - raw
  characters, so a brace in a string, a char, a verbatim or interpolated string, or a comment counts, and an opener's
  text inside a literal is taken for the opener.
- Its one caller, `ExchangeAdminWeb.Tests/ClickGateTests.cs:268` (`EveryRegisteredFlagIsLoweredInAFinally`):
  `var cleanup = ClickGateSource.ExtractBlock(body, "finally");` over `MethodBody(...)`, which is the page with comments
  blanked by the regex `BlankComments` (`:61-72`) and string literals intact.
- What "the quote-aware lexing `Tags()` already does" turns out to be, read rather than assumed: `Tags` (`:102-123`)
  walks MARKUP tags with `WalkTag` (`:243-279`), whose one rule is that a quote opens an attribute value only directly
  after its `=`. That is right for an attribute list and wrong for C#: `'}'` and `"}"` in code follow no `=`, so
  sharing it would leave the record's own trigger unfixed. The lexer the test project already shares for C# is
  `RazorSyntax.CodeView` (`ExchangeAdminWeb.Tests/RazorSyntax.cs`), Roslyn-backed, which blanks comments and string,
  char, verbatim, raw and interpolated literals (holes included) to the same length - the progress guards moved to it
  after three hand-written lexers did not converge (review findings prog-1, prog-3, prog-4). So that is the one shared.

What changes: `ExtractBlock` finds the opener's first occurrence that starts in code (its first non-blank character
survives in the code view) and balances braces over the code view, then returns the SOURCE's text for the span (the
view keeps every offset), so callers still see literals and comments. An opener present only in literals is absent
(""), as an absent opener already was. C# only, and said so: the code view blanks a component's markup. A new
fixture test, `ClickGateTests.ExtractBlockBalancesTheBracesOfCodeNotOfLiteralsOrComments`, breaks the raw count in
each of the record's shapes. After the change the whole suite is run and every ClickGate result is compared with the
pre-change run; separately, every page method containing "finally" is extracted both ways (a throwaway comparison,
not committed) to find any block the old code mis-read.

Declared files: `ExchangeAdminWeb.Tests/ClickGateSource.cs`, `ExchangeAdminWeb.Tests/ClickGateTests.cs`, this record.
No module version: test infrastructure only.

Mutation: quote-awareness removed (the braces and the opener read from the raw source again).

## Files changed

- `ExchangeAdminWeb.Tests/ClickGateSource.cs` - `ExtractBlock` reads `RazorSyntax.CodeView(source)` for the opener and the
  braces and returns the source's own span; new private `IndexOfCode`; remarks say why and what it cannot read (markup).
- `ExchangeAdminWeb.Tests/ClickGateTests.cs` - new `ExtractBlockBalancesTheBracesOfCodeNotOfLiteralsOrComments`; five
  comments that gave "ExtractBlock is not quote-aware" as a present-tense reason (in
  `NoHandlerReadsARegisteredFieldLiveAfterItsFirstAwait`, `EveryRaiseThatMustFollowAnEarlyReturnStillDoes`, the
  `EarlyReturnGuard` remarks and two markup-walk remarks) now say it was, until kd-3, and that it reads C# only.
- This record.

## Guard proof

`ExtractBlockBalancesTheBracesOfCodeNotOfLiteralsOrComments` - a fixture method whose finally holds a "}" in a string, a
'}' char, a verbatim string with braces, an interpolated string whose hole holds "{" and "}" strings, and a line and a
block comment with a brace, preceded by the opener's text and a brace inside a string. The block must start and end
with braces, hold the clear at its end, hold neither the try's statement nor the one after the finally, and return the
string's own text; an opener present only in a literal yields "".

Mutations against `FullyQualifiedName~ClickGate` (317 tests), each md5-confirmed, restored byte-exact (md5 `1868df89`)
with a fresh mtime, rebuilt, rerun (317 passed each time):

| # | mutation | with it |
| --- | --- | --- |
| Q1 | **quote-awareness removed** (the record's): `var code = RazorSyntax.CodeView(source);` -> `var code = source;` | 1 failed: the new test, "busy = false;" not found (the block began inside the string) |
| Q2 | the opener read raw again: `IndexOfCode(source, code, opener)` -> `source.IndexOf(opener, StringComparison.Ordinal)` | 1 failed: the new test (the block found was the try's) |

**ClickGate results before and after (the brief's ask):** the `FullyQualifiedName~ClickGate` run at `b5673b23` (before)
and with this change (after), compared test by test from their TRX files: 316 passed before; 317 after, the only
difference the new test. **No ClickGate test changed result.**

**Blocks the old code mis-read:** a throwaway comparison (not committed) extracted `finally` from every method of every
page under `Components/Pages` that contains the word - 115 method bodies, a superset of
`EveryRegisteredFlagIsLoweredInAFinally`'s calls - with the old raw count and the new code: **0 differences**. No
registered page's finally contains a brace, or the word, inside a literal today, so no earlier result rested on a
mis-read block; the defect was latent.

## Coder dispute

None on the defect. On the record's wording only: `Tags()` is quote-aware for markup attribute values (`WalkTag`), not
for C# literals, so the lexer shared is the Roslyn code view the progress guards already use, not `Tags()`'s walker.
Sharing the walker would not have fixed the record's own trigger (`'}'`).

## Known gaps

- `ExtractBlock` now reads C# only: an opener in markup (`@if (...)`) is not found. Its one caller passes method bodies;
  the remarks say so.
- Candidate, not fixed: `ExchangeAdminWeb.Tests/MigrationStatusPageTests.cs:2577` keeps a private copy of the old raw-count
  `ExtractBlock`, used on markup openers as well as code; same defect class, latent (Migration is closed by owner order,
  and its markup callers could not use the code view).
- Candidate, not fixed: `ExchangeAdminWeb.Tests/ClickGateSource.cs` `BlankComments` (`:61-72` at the base) blanks block
  comments by regex, so a string containing "/*" ... "*/" would blank code between them in `Text`; no page has such a
  literal today (checked).

## Reviewer comments

None yet (codex review pending, dispatched by the supervising session).

## Closeout

Pending: merge into master, codex review, branch deletion.
