# kd-3: ClickGateSource.ExtractBlock counts braces without being quote-aware

**Severity**: LOW - a test-infrastructure defect. `ExchangeAdminWeb.Tests/ClickGateSource.cs` `ExtractBlock` (about 364) ends a block at the wrong place when a brace sits inside a string literal; `Tags()` in the same file is quote-aware. A guard reading a mis-sized block can pass or fail the wrong code.
**Status**: Open - released by the owner's 2026-10-23 evening ruling (`.agents/decisions.md`); approved under the 2026-10-21 go
**Branch**: -
**Commit**: -
**Reviewer**: implementer (recorded in `.agents/state.md` "Known defects", re-verified 2026-10-23 as of `d881ea58`)

## Evidence

As above. Trigger: a handler body containing `"{"` or `'}'` in a string. Predicted failure: the extracted block is truncated or over-extended, and every ClickGate assertion over it reads the wrong text.

## Approach (to be written by the implementing agent before code)

- Share the quote-aware lexer `Tags()` uses (strings, chars, verbatim and interpolated strings, comments) with `ExtractBlock`; add a unit test with a brace inside a string that fails on the old code.
- Mutation: quote-awareness removed.

## Guard proof

Pending.