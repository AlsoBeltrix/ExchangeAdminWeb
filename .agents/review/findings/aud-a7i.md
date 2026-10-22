# aud-a7i: Check Status names an unreadable schedule value's type but not the value, and hides the line when only an end was reported

**Severity**: LOW - no wrong write. (1) Since aud-a7h, a schedule time Exchange reports in a form the
app cannot read is shown as "reported in a form this app cannot read: System.String", but the text
itself is not shown, although showing it word for word involves no guess and would let the operator
see what Exchange said. (2) A Scheduled auto-reply whose start is not reported (null) but whose end
is shows no schedule line at all; the end is dropped silently. Pre-existing.
**Status**: Merged to master (`4c54621b`); closed on guard proof; codex ops-batch-9 clean
**Branch**: `worktree-agent-a569fa37de1b7388c` (forked at `f1460447`)
**Commit**: `1622f027` (merged `4c54621b`)
**Reviewer**: implementer (surfaced by the aud-a38k/a7h agent, 2026-10-22, candidates 2 and 4 of its hand-back)

## Evidence

At `0187257e`: `Models/LookupModels.cs` `ReportedTime` / `ShowReported(ReportedTime, zone)` (the
marker names the type only); `Components/Pages/OutOfOffice.razor`, the Check Status schedule line
(`checkResult.StartTime is { } start` gates the whole line).

## Approach (to be written by the implementing agent before code)

- The unreadable marker carries the raw text (HTML-encoded, length-capped) beside the type name:
  "reported in a form this app cannot read (System.String): <text>". No parsing, no guess.
- The schedule line renders whenever either a start or an end was reported; a missing one reads
  "(not reported)". Tests through the a7b..a7h harness: an unreadable string shows its text; an
  end-only schedule shows the end and "(not reported)" for the start. `ClickGateRegistry`: the page
  is tier 3 with no line-keyed entries; confirm. Mutations at the call site: drop the text from the
  marker; gate the line on the start again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); this record and the codex fold-in below were
admitted under it. Module-scoped: Out of Office (its version bump is the supervisor's;
`Modules/ModuleCatalog.cs` is not edited here). No new JS interop, no new handler or lifecycle method.
Written before the code; line numbers are at `f1460447`.

1. `Models/LookupModels.cs`, `ReportedTime` (159-222):
   - `UnreadableText` beside `UnreadableType`: what came back, as text -
     `Convert.ToString(<innermost wrapper, else the value>, InvariantCulture)`, which for a deserialized
     object is the text it was serialized with. Capped at 80 characters, a cut marked "..." (never
     inside a surrogate pair); blank text, or a ToString that throws, leaves it out - the type is still
     named. Held raw, not pre-encoded: the page renders it as a text frame, which Blazor HTML-encodes,
     so encoding it here would show `&lt;` to the operator.
   - `Read` (186-211), the fold-in: unwrap one wrapper at a time through `ImmediateBaseObject`; the name
     kept is the innermost `Deserialized.*` first type name if any wrapper carries one, else the
     innermost wrapper's first type name, else the value's .NET type. Today `??=` keeps the OUTERMOST
     wrapper's name and `BaseObject` jumps straight to the innermost value.
   - `Show(Func<DateTime, string> readable)`: a readable time as the caller shows one; an unreadable one
     as the marker `reported in a form this app cannot read (<type>): <text>` (no `: <text>` when there
     is none). One spelling of the marker, for every page that shows a reported time.
2. `OutOfOfficeRequest.ShowReported(ReportedTime, TimeZoneInfo)` (345-348): `reported.Show(time =>
   ShowReported(time, zone))`.
3. `Components/Pages/OutOfOffice.razor`: line 66 shows the schedule line, still only for Scheduled, when
   `StartTime is not null || EndTime is not null`; line 68 renders both through `ReadScheduleTime`
   (232-233), which takes a `ReportedTime?` and gives "(not reported)" for null. A start-only schedule
   keeps its start exactly as aud-a7f shows it; its missing end reads "(not reported)" where it read
   nothing - the Approach's "a missing one reads (not reported)", applied to either end. Markup line count
   unchanged; tier 3, no line-keyed `ClickGateRegistry` entry.
4. **Assumption, unverified (no live Exchange call):** the forms the Exchange Online module reports
   `StartTime`/`EndTime` in, and whether a real value ever arrives nested in two wrappers. The fix
   depends on neither: whatever arrives is shown as what it states, or named with its text.

**Tests** (`ExchangeAdminWeb.Tests/OutOfOfficeRequestCaptureTests.cs`, the aud-a7b..a7h harness; New
York as the browser's zone; literal expectations). `ReadBack` takes `object?`.

- The aud-a7h unreadable cases (773-807) expect the type AND the text: `System.Int32` with `42`;
  `System.String` with `10/30/2026 1:00:00 PM`; the deserialized property bag with PowerShell's text of
  it.
- Text carrying markup (`<b>...</b>`) is shown word for word in text frames only, never in a markup
  frame; text over 80 characters is cut there with "...".
- End only: "(not reported)" then the end; start only: the start as before, then "(not reported)";
  neither: no Schedule line.
- The fold-in: a `PSObject` wrapping a deserialized-shaped `PSObject` - one with a string base, one a
  property bag - whose outer first type name is generic and whose inner one is
  `Deserialized.Contoso.Data.ExDateTime`: the inner type and its text are shown, through the harness and
  through `ReportedTime.Read` directly. The test asserts its own construction (the two first names
  differ), so an SDK that made them equal fails it as stale instead of passing it vacuously.

**Mutations at the call site**, each by exact-text replacement asserted to match once, md5 before and
after, the solution rebuilt (Release), the `OutOfOffice`/`OofSchedule` tests run, the file restored,
touched, md5 re-confirmed, rebuilt, rerun green: I1 the marker drops the text; I2 the line is gated on the
start again; I3 (the fold-in) the outer wrapper's name is captured again.

**Gates**: Release build; full suite with `--blame-hang`; `dotnet format --verify-no-changes`;
`git diff --check HEAD`; ASCII lint after `git add -A`. Comments written after the mutation round.

## Guard proof

Built as the Plan.

- `Models/LookupModels.cs`, `ReportedTime`: `UnreadableText` - what came back, as text
  (`Convert.ToString(<innermost wrapper, else the value>, InvariantCulture)`), held raw, cut at 80
  characters with "..." (never inside a surrogate pair), left out when blank or when its ToString throws;
  `Show(readable)` - a readable time as the caller shows one, an unreadable one as the marker
  `reported in a form this app cannot read (<type>): <text>` (no `: <text>` when there is none), spelled
  once. `Read` unwraps one wrapper at a time through `ImmediateBaseObject` and keeps the innermost
  `Deserialized.*` first type name if any wrapper carries one, else the innermost wrapper's, else the
  value's .NET type. `OutOfOfficeRequest.ShowReported(ReportedTime, zone)` shows through `Show`.
- `Components/Pages/OutOfOffice.razor`: the Check Status schedule line shows, for Scheduled, when a start
  OR an end was reported; `ReadScheduleTime(ReportedTime?)` gives "(not reported)" for the one that was
  not. The markup's line count is unchanged; the comment above `ReadScheduleTime` grows by two lines.

| Reported | Check Status shows (browser zone New York) |
| --- | --- |
| an `Int32` | `reported in a form this app cannot read (System.Int32): 42` |
| text in a culture's format | `reported in a form this app cannot read (System.String): 10/30/2026 1:00:00 PM` |
| a deserialized-shaped property bag | `... (Deserialized.Contoso.Data.ExDateTime): @{DateTime=Friday, October 30, 2026 1:00:00 PM}` - PowerShell's own text of it |
| text with markup, `<b>Friday</b> 30/10/2026 13:00` | word for word, in a text frame (Blazor encodes it); no markup frame carries it |
| text over 80 characters | its first 80, then `...` |
| a wrapper whose first type name is generic, around a deserialized-shaped wrapper | the inner `Deserialized.Contoso.Data.ExDateTime` and the inner object's text |
| only an end | `(not reported) - 2026-11-06 17:00 (America/New_York, UTC-05:00)` (the page's em dash) |
| only a start | `2026-10-30 09:00 (America/New_York, UTC-04:00) - (not reported)` |
| neither | no Schedule line, as before |

**One visible change beyond the finding, flagged:** a start-only schedule's missing end read nothing (a
dash with nothing beside it); it now reads "(not reported)" - the Approach's "a missing one reads (not
reported)", applied to either end. Its start is shown exactly as before.

**Assumption, unverified (no live Exchange call):** the forms the Exchange Online module hands
`StartTime`/`EndTime` back in, and whether a real value ever arrives inside two wrappers. Checked on this
host instead (PowerShell SDK 7.6): `new PSObject(inner)` shares the inner's type names when the inner's
were set first, so the nested shape the fold-in names needs an outer whose names were read before the
inner was named; the tests build exactly that and assert it, and the harness stub hands such a value to
the service intact (the I3 harness cases fail).

| Test (`ExchangeAdminWeb.Tests/OutOfOfficeRequestCaptureTests.cs`) | Pins |
| --- | --- |
| `AReadScheduleThisAppCannotRead_IsShownAsAMarkerNamingWhatCameBackAndItsText_NeverAsAnEmptyWindow` (an `Int32`; culture text; a deserialized-shaped property bag) | **the finding, part 1**: the marker carries the type AND the text - `42`, `10/30/2026 1:00:00 PM`, `@{DateTime=...}`; the aud-a7h cases, their expectation extended |
| `AnUnreadableTimesText_IsShownWordForWord_AsTextTheBrowserEncodes_NeverAsMarkup` | `<b>Friday</b> 30/10/2026 13:00` is in the page's text frames word for word and in no markup frame |
| `AnUnreadableTimesText_IsCutAtEightyCharacters_AndTheCutIsMarked` | 200 characters: the first 80 then `...`; the 81st never shown |
| `AScheduleWithOnlyAnEnd_ShowsTheEnd_AndSaysTheStartWasNotReported` | **the finding, part 2**: the line is there, `(not reported)` then the end in the aud-a7f form |
| `AScheduleWithOnlyAStart_ShowsTheStartAsBefore_AndSaysTheEndWasNotReported` | the start in the aud-a7f form, then `(not reported)` |
| `AScheduleWithNeitherTime_ShowsNoScheduleLine` | neither: no line, no `(not reported)` (passes before and after by construction) |
| `ATimeInsideTwoWrappers_IsNamedByTheInnerDeserializedType_AndShownWithItsText` (a string inside; a property bag inside) | **the fold-in**, through the page and the real service: the inner `Deserialized.Contoso.Data.ExDateTime` and the inner text, not the outer wrapper's generic name |
| `TheReader_OfATimeInsideTwoWrappers_KeepsTheInnerDeserializedTypeName_AndItsText` (the same two) | the fold-in at `ReportedTime.Read` itself, independent of the stub; `Nested` asserts its own shape (the outer's and inner's first names differ) |
| (unchanged) every aud-a7f/a7g/a7h Check Status and refusal test | readable times exactly as before |

**Against the unfixed code** - `Models/LookupModels.cs` and the page at `HEAD` (LF md5s
`9e44af4fd9707e5c5d72df012235f1f9`, `c4204ad17885b5934569fe0e0a09d5f8`, the aud-a7h finals), the tests as
changed (they compile there: the direct test reads the marker through `ShowReported`): 76
`OutOfOffice`/`OofSchedule` tests, 11 failed - the three marker cases, markup, the cut, end only, start
only, and the four fold-in cases. The neither-time control passes there by construction. Restored from
the saved bytes, touched, md5s re-confirmed, rebuilt, 76 passed.

Mutations at the call site, each by exact-text replacement asserted to match once, md5 recorded, the
solution rebuilt (Release) and the 76 `OutOfOffice`/`OofSchedule` tests run, the file restored from its
saved bytes, touched (a restored copy keeps its old mtime and MSBuild would skip the rebuild), md5
re-confirmed equal, rebuilt, and the tests re-run green. First round on the pre-comment bytes (LF: models
`740d1b39d330e9b35fd4d24a6e40557a`, page `9501cc139ad5f221e1314d01ca6474bb`); re-run on the final bytes
with the same results - the md5s below are the re-run's.

| Mutation | File (final md5) | md5 after | Failed |
| --- | --- | --- | --- |
| I1 (the Approach's first): the marker drops the text (`... ({UnreadableType})` only) | models `c0a3736d162771bc2bc3c001d4db7116` | `43575d0262a821d89fb17fd8fd9132bf` | 9: the three marker cases, markup, the cut, the four fold-in cases |
| I2 (the Approach's second): the line gated on the start again (`checkResult.StartTime is not null`) | page `9444805e91b882495080977c81adeebc` | `2c557515b658f088198d226f723e1524` | 1: `AScheduleWithOnlyAnEnd_...` |
| I3 (the fold-in): the outer wrapper's name captured again (`reported is PSObject outermost ? FirstTypeName(outermost) : null`) | models | `bccbb25690765fb9a5c63c96038c66fc` | 4: both harness and both direct fold-in cases |

Comments were written after the first round. Final test-file md5 (LF):
`OutOfOfficeRequestCaptureTests.cs` `52c4bee88b40d18542e67934f9b5524e`.

**Pins.** `ClickGateRegistry`: the page is `"tier 3, not approved"` with no line-keyed entries, and the
markup's line count did not change - confirmed by the `ClickGate*` tests passing in the full suite.
`ProgressRegistry`: no handler or lifecycle method was added (`ReadScheduleTime` is a render helper whose
parameter became nullable); its pins are unchanged and its tests pass. `ValidatedWriteCallSite`, the
aud-a7b capture scan and the aud-a7c CheckStatus scan pass unchanged.

## Gates

On the final bytes of this finding (the branch at the aud-a7i commit):

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; no warning in a changed file.
- `dotnet test ExchangeAdminWeb.slnx -c Release --no-build --blame-hang --blame-hang-timeout 10m`: 5294
  passed, 3 skipped (the known `ADDirectorySearchServiceTests ...WhenAdUnavailable` cases), 0 failed -
  5297 in all, 9 of them new cases (and the three aud-a7h marker cases extended); 5 m 16 s, no hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean (exit 0).
- `git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` passed after `git add -A`. The page's one
  non-ASCII character is its existing em dash between the two times (`.razor` is outside the lint).

## Files changed

- `Models/LookupModels.cs` - `ReportedTime.UnreadableText`, `Show`, the nested-wrapper unwrapping, the
  text and its cut; `ShowReported(ReportedTime, TimeZoneInfo)` through `Show`; comments.
- `Components/Pages/OutOfOffice.razor` - the Check Status schedule line; `ReadScheduleTime(ReportedTime?)`.
- `ExchangeAdminWeb.Tests/OutOfOfficeRequestCaptureTests.cs` - `ReadBack` takes `object?`; `Frames` and
  `Nested`; the aud-a7h marker test extended; seven tests (nine cases) added.
- This record.

## Known gaps

- **Assumption, unverified (no live Exchange call):** the forms the live module reports (above).
- **The text of a property bag is PowerShell's own rendering** (`@{DateTime=...}`), not any one property:
  choosing a property would be a guess at which one names the time.
- **No browser ran**; the browser's zone came from the harness's fake `IJSRuntime`, as in aud-a7e/a7f/a7h.
  HTML-encoding is shown at the render-tree level (a text frame, not a markup frame), which is what Blazor
  encodes; no HTML was rendered.
- Out of Office module version bump is the supervisor's.

## Closeout

Fixed on branch; not merged, not pushed.

## Folded in: nested PSObject wrappers name the outer type (codex, ops-batch-8, 2026-10-22)

`Models/LookupModels.cs` `ReportedTime.Read` (around line 190 at `0187257e`): when the value is a
`PSObject` wrapping another `PSObject` whose inner base object is the unreadable value, the loop
captures `typeName` once with `??=` before unwrapping further, so the marker can name the outer
wrapper's generic type instead of the inner deserialized type. Unwrap through nested wrappers
keeping the most specific (innermost, or `Deserialized.*`) type name. Test: a `PSObject` wrapping a
deserialized `PSObject` whose base object is unreadable names the inner type (and, with this
record's part 1, shows its text). Mutation at the call site: capture the outer name again.
