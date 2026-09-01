# csv-1: Shared CSV helper omitted formula-injection neutralization

**Severity**: MEDIUM - exported cells carrying externally influenced strings can
execute as spreadsheet formulas on the operator's machine when opened in Excel.
**Status**: Verified (plan revised; docs-only - no code exists yet)
**Branch**: -
**Commit**: lands in the same commit as this record and the plan revision; read it
from `git log -1 -- .agents/review/findings/csv-1.md`.

## Evidence

`docs/ModuleCsvExport-Plan.md` (pre-fix, at `533c1fe`): AC1 required only the
CsvHelper `WriteField` quoting contract (commas, quotes, newlines). The repo's own
stronger standard exists at `Services/MessageTraceDetailReport.cs:216-225` -
`CsvEscape` is documented as "CSV field escaping with CSV-injection
neutralization" and prefixes a leading `=`/`+`/`-`/`@`/tab/CR/LF with a single
quote (verified by reading the function, not the reviewer's citation of `:210`).
Exported cells include sender addresses (`BlockedSenderInfo.SenderAddress`),
location display names, and migration batch names - externally influenced values.

## Predicted observable failure

A blocked sender named `=HYPERLINK(...)` or a location named `+1|cmd` exports
verbatim; Excel interprets the leading character as a formula when the operator
opens the file. CsvHelper quoting does not prevent this.

## What

Plan defect: the new shared writer specified a weaker escaping contract than the
repo's existing export standard for the same threat.

## Approach

Plan revised: new AC1b requires `CsvExport.Write` to apply the
`MessageTraceDetailReport.CsvEscape` leading-character rule before CsvHelper
quoting, with a dedicated non-vacuous test. Also recorded (not fixed - survey is
not authorization): `EventLogCsvFormatter` has the same gap for Event Log
exports; pre-existing, owner's call.

## Files changed

- `docs/ModuleCsvExport-Plan.md` - AC1b, design sketch (neutralize before
  `WriteField` + recorded Event Log gap), test table
  (`Write_NeutralizesFormulaLeadingCells`), Review log.

## Guard proof

Docs-only. The plan's `Write_NeutralizesFormulaLeadingCells` test carries its own
non-vacuity mutation (remove the neutralization -> FAIL), biting at
implementation. `git diff --check` clean on the fold commit.

## Coder dispute (if any)

None.

## Known gaps

`EventLogCsvFormatter` (Event Log export) still lacks the neutralization -
recorded in the plan as a pre-existing gap, not scheduled.

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier
  (grade fallback; owner-named dispatch: "have codex review all the plans")
Harness: codex-cli 0.150.1. Reviewed SHA `533c1fe3...`, base `a9b0ebc3...`,
capability_ok true, verdict `acceptable_with_changes` (openreview; also material
change 2). Dispatched 2026-08-31; envelope at `.agents/review/plans2.result.json`.
