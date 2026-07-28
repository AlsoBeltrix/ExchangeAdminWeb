# mt-detail-slice3: pure detail-export CSV builder + threshold helper (slice 3)

**Severity**: n/a — slice-landing review of a new pure helper + its tests
**Status**: Accepted (round 1, 2026-07-27) — verified, slice 3 complete
**Branch**: `master` (committed directly per repo policy; no per-finding branch)
**Commit**: `2df0f48` (slice 3), base `7181db5`

## Evidence
`Services/MessageTraceDetailReport.cs` — new pure static class: `BuildCsv`,
`ResolveAction`, `SelectAllCount`, `CsvEscape`, and the `MessageTraceDetailAction`
enum. `ExchangeAdminWeb.Tests/MessageTraceDetailReportTests.cs` — 25 tests.

## Predicted observable failure
Without this slice there is no shared, testable assembly of the per-message
delivery-detail export or the selection-count threshold rule, so the download path
and the email/bulk-job path would drift and the "1-10 live / 11-50 email-only"
owner rule (decision 4) would live only in UI code. The core guards:
`ResolveAction_NeverAllowsLiveAboveLiveMax` (a count above 10 must never yield
`LiveOrEmail`) and the CSV-injection escaping (`=`/`+`/`-`/`@`-prefixed fields
neutralized). Guards: `dotnet build ExchangeAdminWeb.slnx -c Release` (0 errors),
`dotnet test ExchangeAdminWeb.slnx --filter FullyQualifiedName~MessageTraceDetailReportTests`
(25/25).

## What
Third slice of the Message Analysis detail work stream (plan task 3). Adds the pure
export builder and threshold rule shared by both the download and email/bulk-job
paths, so the two produce identical CSV content and honor one threshold definition.

## Approach
`MessageTraceDetailReport.BuildCsv(IReadOnlyList<MessageTraceDetail>)` emits, per
message in supplied order, a numbered header, a summary header block (Origin
Date-Time, Backend, Sender/Recipient, Subject, Status, Message ID, Trace ID), an
optional `Detail error` line, then the full event trail (Date, Event, Action,
Source, Detail) with every row preserved (no collapse) and each field CsvEscaped.
A failed-fetch message still gets a block with its error surfaced (Known Failure
Class #2 — never silently dropped). `ResolveAction` maps selection count to
`None`/`LiveOrEmail`/`EmailOnly`, fail-closed above `LiveMax` (10). `SelectAllCount`
caps at `EmailMax` (50). `CsvEscape` mirrors the summary export in
`MessageTrace.razor:701` exactly (leading formula/control char prefixed with a
single quote; comma/quote/newline quoted with doubled quotes).

## Files changed
- `Services/MessageTraceDetailReport.cs` — new pure static class (new file).
- `ExchangeAdminWeb.Tests/MessageTraceDetailReportTests.cs` — 25 tests (new file).

## Guard proof
Two independent guards, both proven non-vacuous:
1. Threshold: removing the `if (selectedCount <= LiveMax)` branch (so every
   positive count returns `LiveOrEmail`) makes 4 tests FAIL (incl.
   `ResolveAction_NeverAllowsLiveAboveLiveMax`); restore -> 25 pass.
2. CSV injection: removing the leading-char neutralization in `CsvEscape` makes 3
   tests FAIL; restore -> 25 pass.
Build 0 errors, ASCII-clean (no non-ASCII lines), `dotnet format` no changes,
`git diff --check HEAD` clean.

## Coder dispute (if any)
None.

## Known gaps
The download and email paths that will consume this builder do not exist yet
(slices 4-7); this slice is the pure helper only. The exact CSV column ordering and
header labels are chosen to mirror the existing summary export and are not yet
validated against an operator's spreadsheet expectations (manual-validation-on-dev,
standing gap).

## Reviewer comments

**Round 1 - codex CLI (codex exec, portkey gpt-5.5-dzs/xhigh), 2026-07-27 - ACCEPTED.**
Verdict JSON:
`{"verdict":"accepted","guard_confirmed":true,"capability_ok":true,"reviewed_sha":"2df0f48267beadbf42212cd28c77812909deeadc","base_sha":"7181db5d58b72275f96b9b95ed4bd062eb0fb986","comments":[]}`

- `guard_confirmed=true`: the reviewer ran the threshold guard in its own isolated
  copy tree (`%TEMP%\ExchangeAdminWeb-review-copy-...`), never the coder tree.
  Removing the `if (selectedCount <= LiveMax) return LiveOrEmail;` branch made
  exactly the 4 predicted tests FAIL (`ResolveAction_NeverAllowsLiveAboveLiveMax`
  plus `ResolveAction_MapsCountToAllowedAction` cases 11/50/999); restore -> 25
  passed. Baseline at head was 25/25 before mutation.
- `capability_ok=true`: read repo files and ran `dotnet build ExchangeAdminWeb.slnx`
  (EXIT=0) in the isolated tree.
- SHAs match dispatch: `reviewed_sha` 2df0f48 = head, `base_sha` 7181db5 = base.
  Reviewer `git rev-parse HEAD` confirmed the coder tree stayed at 2df0f48 (all
  mutation isolated to the temp copy tree).
- No comments. Clean pass.

Reviewer note: the reviewer's isolated-tree setup hit a Windows `%TEMP%`
environment class (`MSB5021` Roslyn worker termination) that made its first
guard-proof runs report false failures; it self-recovered by rebuilding the
isolated tree (full copy + robocopied `.git`, `UseSharedCompilation=false`,
`MSBUILDDISABLENODEREUSE=1`) and re-ran the proof cleanly. Environment artifact,
not a code defect; the coder tree's local run was 25/25 with both guards proven
before dispatch.

Reviewer: codex CLI (codex-cli 0.145.0, `codex exec`, portkey
@azure-openai-eus2-global/gpt-5.5-dzs, xhigh; standard tier).
