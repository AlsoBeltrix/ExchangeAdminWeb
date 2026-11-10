# placeholder-neutral: MFA Reset's user box showed this environment's mail domain as its placeholder

**Severity**: LOW - a grey example string in an empty box; it decides nothing. But it is this environment's own
domain in shipped markup, which Invariant 7 forbids (no source file names an ADI domain as behaviour or default).
**Status**: Merged to master at 27f53db7 (2026-11-10) on guard proof (LOW, no review round); master gates green (9073 xUnit, 687 Pester, 19 warnings, format/diff-check/ASCII clean)
**Branch**: `fix/p0-c-phase0-lows` (worktree `D:\source\ExchangeAdminWeb-wt\p0-c`, from master f151f527)
**Commit**: 23d55c9a (the placeholder), c1a0ab07 (version) |
**Implementer**: Claude (supervised agent)
**Reviewer**: Fable 5.2 audit 2026-11-10, confirmed by Gemini 4 Argon (High) through agy; orchestrator triage
**Plan**: `docs/SharedPiecesAndSlop-Plan.md` slice 0.7 (defect #8)

## Evidence (master f151f527)

- `Components/Pages/MfaReset.razor:53`: `placeholder="user@analog.com"`.

## Fix

`placeholder="user@example.com"` - the form the app's address boxes use most (three other boxes carry exactly it;
the rest are `sender@`, `recipient@`, `name@` at `example.com`, or `contoso.com`). One line, edited in place; no line
moved.

## The search of `Components/`

`grep -rnE "ashb|ANALOG|\bADI\b|adi\.|analog" Components` and a list of every `placeholder="...@..."`:

| Where | What | Done |
|---|---|---|
| `MfaReset.razor:53` | the placeholder | fixed |
| `ModuleConfig.razor:902` | a `///` remark naming `ANALOG\ExchangeWebAdminsExecSupport` as the group a past incident was found on | left: an explanatory comment, which Invariant 7 exempts; not in a method this fix touches |
| `ModuleConfig.razor:423` | shipped help text: for Graph modules, use the "ADI - Azure App Registration" template | **left, for the owner**: it is not a domain, host, OU, group or address but the name of a Secret Server template, and it is the instruction an administrator here follows to pick one. Making it neutral ("a template with Tenant ID, Application ID and Client Secret fields") changes help an operator relies on, which is a wording decision beyond this defect |

No other placeholder or example in `Components/` names this environment.

## Tests

None added. A placeholder is a literal attribute with no logic behind it; the only test that could be written
asserts the string against itself. Verified instead: the search above returns no markup hit after the change; the
Release build passes; the page's line count is unchanged and the click-gate tests pass (full gates in the hand-back).

## Guard proof

Not applicable - no test bites on a literal placeholder. Stated, not skipped.

## Left alone, and why

- `Services/` comments that name this forest's domains as examples (`ADDirectorySearchService.cs` and others) are
  explanatory comments outside `Components/` and outside this defect.
- No help or README text mentions the placeholder.
- Version: MFA Reset 1.6.6. No base bump: no shared code changed.
