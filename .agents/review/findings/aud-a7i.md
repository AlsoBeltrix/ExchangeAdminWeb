# aud-a7i: Check Status names an unreadable schedule value's type but not the value, and hides the line when only an end was reported

**Severity**: LOW - no wrong write. (1) Since aud-a7h, a schedule time Exchange reports in a form the
app cannot read is shown as "reported in a form this app cannot read: System.String", but the text
itself is not shown, although showing it word for word involves no guess and would let the operator
see what Exchange said. (2) A Scheduled auto-reply whose start is not reported (null) but whose end
is shows no schedule line at all; the end is dropped silently. Pre-existing.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
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

Pending.

## Folded in: nested PSObject wrappers name the outer type (codex, ops-batch-8, 2026-10-22)

`Models/LookupModels.cs` `ReportedTime.Read` (around line 190 at `0187257e`): when the value is a
`PSObject` wrapping another `PSObject` whose inner base object is the unreadable value, the loop
captures `typeName` once with `??=` before unwrapping further, so the marker can name the outer
wrapper's generic type instead of the inner deserialized type. Unwrap through nested wrappers
keeping the most specific (innermost, or `Deserialized.*`) type name. Test: a `PSObject` wrapping a
deserialized `PSObject` whose base object is unreadable names the inner type (and, with this
record's part 1, shows its text). Mutation at the call site: capture the outer name again.
