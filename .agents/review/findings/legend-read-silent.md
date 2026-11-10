# legend-read-silent: a failed read of the attribute legend returned an empty legend with no log

**Severity**: LOW - the legend only adds choices and descriptions to Choice attributes; an attribute's own configured
choices are still offered and nothing is written wrongly. But the choices the legend supplies vanished from the
editor with no word on the page and no line in any log, so nobody could tell a broken store from an empty legend.
**Status**: Merged to master at 27f53db7 (2026-11-10) on guard proof (LOW, no review round); master gates green (9073 xUnit, 687 Pester, 19 warnings, format/diff-check/ASCII clean)
**Branch**: `fix/p0-c-phase0-lows` (worktree `D:\source\ExchangeAdminWeb-wt\p0-c`, from master f151f527)
**Commit**: d942d1d1 (the log, the report, the page warning, tests, README), 97a49b6f (version) |
**Implementer**: Claude (supervised agent)
**Reviewer**: Fable 5.2 audit 2026-11-10, confirmed by Gemini 4 Argon (High) through agy; orchestrator triage
**Plan**: `docs/SharedPiecesAndSlop-Plan.md` slice 0.7 (defect #4)

## Evidence (master f151f527)

- `Services/Storage/AttributeEditorRepository.cs:183-212` (`ReadLegend`): the whole read sat in a `try` whose bare
  `catch` returned an empty map. The repository has no logger.
- `Services/ADAttributeEditorService.cs:782-785` (`GetLegend`): took that map as the legend and cached it. Its two
  readers, `GetEffectiveChoices` and `GetChoiceDisplayText`, then found nothing.

## Fix

- `ReadLegend` no longer catches: a read failure throws to its one caller.
- `GetLegend` catches it, logs an error with the exception ("The attribute legend could not be read; the choices and
  descriptions it supplies are missing from the editor"), and serves an empty legend exactly as before - cached the
  same way, so it is one log line per load, not one per choice rendered, and a later load that succeeds clears it.
- `IsLegendUnreadable()` (new, on the service) says which of the two an empty legend is. The editor calls it and
  shows, above the attribute table: "The list of choices could not be read, so some choices and their descriptions
  are missing below." One sentence, 103 characters.
- README: one bullet under AD Attribute Editor.
- The page gained four lines. `ADAttributeEditor.razor` has no line-keyed click-gate entry (the registry lists it as
  not started), so nothing needed re-anchoring; the click-gate and progress tests pass.
- App code: +55 / -35 lines (`git diff --numstat d942d1d1~1 d942d1d1 -- Services Components`); most of both counts
  is the repository method losing one level of indentation.

## Tests

`ADAttributeEditorServiceTests`, 2 new, over the real service, repository and SQLite store:

- `Legend_ThatCannotBeRead_IsLoggedAndReported_AndTheAttributesOwnChoicesAreStillOffered`: a legend is seeded and
  read (choices HR and IT); the legend table is then dropped, so the read fails for real. The service reports the
  legend unreadable, serves an empty one, still offers the attribute's own choice, shows the bare value, and logs
  one error.
- `Legend_ThatIsEmptyOrPresent_IsNotReportedUnreadable_AndLogsNothing`: no legend, then a legend - never reported
  unreadable, nothing logged. Pins what must not change.

The page's four lines of markup are not executed by a test: the test project has no component renderer (no bUnit).
The method the markup calls is what is tested.

No test deleted or changed.

## Guard proof (reverted, targeted run of the class's 81 tests, restored)

| Reverted | Failed | Tests that failed |
|---|---|---|
| The repository swallows the failure again (file as at master) | 1 | `Legend_ThatCannotBeRead_...` |
| The failure is not logged | 1 | the same |
| The failure is not reported (`unreadable` left false) | 1 | the same |

Reverting the whole fix does not compile (the tests call `IsLegendUnreadable`), so the three parts were reverted one
at a time. `Legend_ThatIsEmptyOrPresent_...` passes throughout. Restored: 81 of 81 pass.

## Left alone, and why

- The legend stays fail-open: an unreadable legend does not stop the editor, as before.
- A failed load does not keep serving the last good legend; it serves an empty one, as before. Changing that is a
  behaviour change the plan does not ask for.
- `IsLegendConfigured` and the import path are untouched.
- Finding IDs were stripped from the comment of `GetLegend` and nowhere else.
- Version: AD Attribute Editor 1.7.11. No base bump: the repository and service are this module's own.
