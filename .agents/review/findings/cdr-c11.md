# cdr-c11 - report interrupted membership writes without invented counts

Status: Implemented on master; restored full gate passed; independent Astra workflow review pending.
Approved scope: `docs/Comms10kDefenderRemediation-Plan.md`, Revision 6, C11.
Baseline: clean master `958cce71a973f45afa9cb3c892ce286e287aa930`.
Both origin and github master were checked live at
`885397981d7550afebf6518ef21b4fc54b062859` on 2026-10-15. This stream remains local.

## Evidence and final scope declared before code edits

`BulkJobService.InitializeAsync` interrupts nonterminal persisted jobs and resolves a fresh
processor in a fresh scope for completion. That processor has no `_result` and currently
reports "did not complete" with "Members written: 0". No persisted count establishes zero.
`Comms10kReplaceWriter.Derive` assigns the requested set size to MembersWritten on every
post-clear outcome. A successful clear followed by a failed first add and a confirmed empty
read-back therefore claims 10,000 written for a request of that size. The service information
log, processor audit and completion email propagate that misleading claim.

This slice changes reporting provenance, not the accepted clear-then-fill procedure:

- Separate the resolved requested count from a nullable written count. A complete sequence
  of acknowledged add calls can establish its requested-member total; a thrown clear/add,
  an unexpected service exception, a standalone read-back or a restart cannot. Final
  membership remains the observed count and never supplies missing write provenance.
- Label requested, acknowledged-written and observed-final counts distinctly in the service
  log, mutation audit and completion notification. Refusals before dispatch establish zero
  written; lost results establish unknown. Preserve existing outcome/final-count derivation,
  concrete failure reasons, one refusal audit and the primary-member recovery correction.
- Completion without an in-memory result names an interrupted job as Interrupted, retains
  its persisted reason and warns that membership may be empty or partial and must be checked.
- Trace unchanged page consumers: the Comms page renders Outcome/Message only and never reads
  MembersWritten; the shared jobs views render persisted job status and stage messages.
  No page or shared runner change is planned. C5 owns submitter visibility/Submitted wording.

Declared files:

- `Services/Comms10kReplaceWriter.cs`: requested/written provenance in sequence and derivation.
- `Services/Comms10kService.cs`: result count contract, predispatch zeros and service log labels.
- `Services/Jobs/Comms10kReplaceProcessor.cs`: refusal/exception count state, audit labels and
  completion reporting when the in-memory result is absent.
- `ExchangeAdminWeb.Tests/Comms10kInterruptionTests.cs`: real runner/repository/processor
  restart and completion coverage, real local PowerShell service/writer cases for ordinary
  failed/partial/successful writes, lost acknowledgement and refusals, with compiled mutants.
- `ExchangeAdminWeb.Tests/Comms10kReplaceWriterTests.cs`: replace the existing assertion that
  treats requested size as a written count when only a read-back was supplied.
- `Modules/ModuleCatalog.cs`: Comms-10k 1.4.10 to 1.4.11; app remains 2.27.2 because all
  changed production behavior belongs to this module, including its Jobs processor.
- This record, `.agents/review/index.md`, `.agents/state.md`, `.agents/token-log.md` and the
  approved plan's implementation row: evidence, Constitution review packet and next gate.

## Constraints and contract check

The Constitution's audit, notification, result isolation and module-version rules govern.
The known AdminModuleSpec 2.0 / June 25 versus app 2.27.2 drift remains flagged; its applicable
audit/notification rules agree with the Constitution and current service/processor wiring.
No C5/C2/C7/C8/C6 behavior, shared PowerShell follow-ups, new retries, rollback, compensation,
locking, protection checks, live directory/tenant call, deployment, push, branch operation,
hook/configuration change or coder self-review. Preserve the fixed target and member GUIDs,
C9 command cleanup, C3 preparation/CouldNotConfirm distinction and C4 primary-member advice.

## Verification plan

Every new case must fail an intended assertion against compiled production regressions.
Restore exact saved bytes in finally, verify MD5/SHA256 and advance mtime; reject compiler
errors or stale --no-build evidence. Focused proof precedes one restored full gate: Release
solution build, solution format verification before tests, full solution tests with
xUnit.MaxParallelThreads=4, diff check and staged ASCII check. No PowerShell source change
is planned, so Pester/PSScriptAnalyzer are not applicable. Browser rendering and installed
AD-cmdlet integration remain unverified. Independent review is the next gate; C5 follows it.

## Implementation

MembersRequested is the resolved requested-set count. MembersWritten is nullable and describes
the complete acknowledged add sequence, not the final membership or the requested size. The
sequence supplies that count only after every add returns; a failed clear/add leaves it unknown.
A derivation given only an observation also leaves it unknown. An acknowledged sequence keeps
its known written count even if the later observation fails or another writer changes the group.
Predispatch refusals retain a proven zero. An unexpected processor-level service exception
retains CouldNotConfirm, unknown written/final counts and its concrete reason.

The audit uses separate MembersRequested/MembersWritten/FinalCount fields and records the
written-count basis. The notification labels acknowledged adds explicitly; the service log
separates requested, written (acknowledged adds) and final values. Unavailable requested sets
are unknown rather than zero. Unknown final counts now say no confirmed read-back, including
refusals where no read-back was attempted. Restart completion reports Interrupted, retains
the runner's persisted reason and warns that the group may be empty or partial.

Read-back remains authoritative for Outcome and FinalCount. In particular, a lost acknowledgement
followed by a matching observation still succeeds, but its written count stays unknown; its
original write diagnostic is retained in the success message as well. Ordinary partial writes
keep the original diagnostics and CSV recovery advice. The C4 primary-membership advice and
all mutation/identity/PowerShell boundaries remain intact.

The unchanged Comms page only renders Outcome/Message and has no count field to migrate.
AdminBulkJobs renders persisted job status, job.Message and row.Message, so restart status
and normal result messages remain truthful. The global frame only surfaces active jobs;
C5 remains the approved next slice for submitter access to terminal outcomes and Submitted
wording. No page, registry or shared runner edit is part of C11.

## Review Packet

### Mode

IMPLEMENTATION_REVIEW (independent Astra max workflow review pending).

### Original Request

Owner approved all twenty Revision 6 findings with sequential fresh Sol 6.1 max implementation
and independent Astra max review. This slice is C11: report interrupted replaces with unknown
written counts, including restart reconciliation and ordinary clear-success/add-failure cases.
Distinguish the requested set, proven write evidence and observed final membership throughout
the affected reporting outputs, preserving honest outcomes, final counts and failure reasons.

### Approved Plan

`docs/Comms10kDefenderRemediation-Plan.md`, approved Revision 6, C11 and its C9 review extension.

### Commit Range

`958cce71a973f45afa9cb3c892ce286e287aa930..HEAD` at the commit carrying this record.
The parent supplies the resolved head for review.

### Claimed Changes

- Separate nullable requested/written counts and keep write evidence independent of read-back.
- Report explicit unknown values after interrupted writes or lost processor state.
- Carry the count contract through service information logs, mutation audits and completion.
- Retain refusal zeros, observed outcomes/final counts, concrete reasons and recovery advice.
- Comms-10k 1.4.10 to 1.4.11; the base app remains 2.27.2.

### Constraints / Out Of Scope

No shared runner/schema change, page flow, Comms protection check, lock, rollback,
compensation, new retry, live directory call, deployment, push, branch/hook/configuration
change or self-review. C5/C2/C7/C8/C6 and psl-2/3/4 remain separate work.

### Design Decisions

Use the established result shape with nullable counts rather than inventing persisted
per-member progress. A completed add sequence provides acknowledged-write evidence. Any
throw may hide partial effects, so its written total is unknown even after a matching or
empty observation. A persisted stage success is not durable proof of which calls returned
in the lost process. Restart completion therefore does not parse stage prose into a count.
The uploaded address count also cannot supply requested membership after restart: multiple
addresses can resolve to one GUID, and the surviving approved fingerprint carries no cardinality.
Requested membership is therefore explicitly unknown without the resolved result. Written count
and observed final count answer different questions and may legitimately differ.

### Deferred Work

C5 submitter job visibility and Submitted wording is next after independent review. C2 fresh
authorization snapshots/write boundaries, C7 handler reentry/atomic CSV/raw rejection text,
C8 safe exports/audit and C6 ticket display remain pending, then Defender in plan order.
psl-2/3/4 are separately unapproved follow-ups. No browser/installed-cmdlet verification is claimed.

### Review Focus

Trace the actual service/writer/result/processor/runner restart and completion lifecycle plus
all result consumers. Challenge count provenance, lost acknowledgements, final-set mismatch,
no-read-back cases and the no-result path; verify that C3/C4/C9 contracts survive. Assess
scope and the test/mutation evidence independently of the implementation narrative.

### Verification Run

Focused reproduction, compiled mutation receipts and the restored final gate are recorded
below when complete. The initial executable focused run passed all 15 then-present cases;
one additional refusal-before-resolution control was added before the proofs. Initial solution
format verification passed with the existing workspace-loading warning only.

## Compiled guard proof and restored focused gate

All 16 new cases and the adapted existing writer case failed intended assertions against
compiled production regressions. Eight sequential groups produced 34 assertion failures.
Every probe used Release solution tests with --no-restore, a focused filter and
xUnit.MaxParallelThreads=4; none used --no-build. Each TRX was checked for the exact expected
total/failure counts, exit 1 and Assert.* failures. Compiler/build errors were rejected.

| Probe | Production regression | Intended failures / cases |
|---|---|---|
| M1 | no-result completion defaults written count to zero | 4 / 4 restart states |
| M2 | Derive substitutes requested size for missing write evidence | 6 / 9, including adapted writer case |
| M3 | discard the total from a completed acknowledged add sequence | 3 / 8 |
| M4 | predispatch writer refusal loses its proven zero | 2 / 2 |
| M5 | processor refusal becomes unknown and unexpected exception becomes zero | 2 / 2 |
| M6 | audit substitutes requested count for written | 6 / 9 |
| M7 | notification substitutes requested count for written | 6 / 9 |
| M8 | service information log substitutes requested count for written | 5 / 8 |

The restart cases exercise BulkJobService.InitializeAsync over actual persisted Queued/Running
jobs, including zero, one and two recorded stages, and the fresh scoped Comms processor's
completion hook. Stage counts and even a persisted success message never become a written
count. The service cases use real harmless local PowerShell functions through the actual
service/writer, runner, row persistence, audit and notification. They cover clear-success/first-
add-failure with a confirmed empty group; a second-add failure leaving 2,000 members; a failed
clear; the last add applying before losing its acknowledgement; acknowledged success; read-back
failure with known or unknown write evidence; and a concurrent removal after all adds returned.
Two service refusals and a semantic refusal establish zero before dispatch. An unexpected
service exception establishes unknown while preserving one failed audit and the original reason.

Every mutation restored saved fixed bytes in finally, verified MD5 and SHA256, and advanced
mtime beyond the mutated source. Final restored production files matched every receipt:

| Source | MD5 | SHA256 |
|---|---|---|
| Comms10kReplaceWriter.cs | `DE0E67774012F73BA87010214BC8FDD2` | `58F28033EA5F18B0D682B88D68AB0F592C92E93C642CB186FA3EF5039875B575` |
| Comms10kService.cs | `C0EF7EE2A3D8855CF15414C87BBCBE0B` | `C0CB842F12B486789210C41877061279B2B532759C726B0A60AB35CC91BBE2D3` |
| Comms10kReplaceProcessor.cs | `B0AF244F2577AFFEB3666E4D92CCC255` | `FA501C9CF5D2A7E620B40B045823D22ADCB6D56B4357444E31BFF1BF70515F52` |

The restored focused Release run passed all 218 Comms cases, 0 failed/skipped. Its only test
edit after the mutation runs was a null-forgiving annotation on a restart reason whose exact
value was already asserted, removing the harness's nullable warning without changing coverage.

Machine-local gitignored evidence under `.agents/review/out/`: `cdr-c11-focused.log`/TRX,
`cdr-c11-M1` through `M8` logs/TRX, fixed-byte backups, `cdr-c11-mutations.ps1`,
`cdr-c11-mutation-receipts.json`, and `cdr-c11-restored.log`/TRX. The record owns the durable
results; those logs are supplemental. No baseline full-suite run was performed.

## Restored final gate

- `dotnet build ExchangeAdminWeb.slnx -c Release`: exit 0, 40 existing warnings / 0 errors.
  No warning refers to the new C11 test file; the nullable harness warning was removed.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0,
  workspace-loading warning only. Run before the full test suite.
- `dotnet test ExchangeAdminWeb.slnx -c Release -- xUnit.MaxParallelThreads=4`:
  exit 0, 3,972 passed / 3 skipped / 0 failed (5 m 03 s). This was the only full-suite run.
- `git diff --check HEAD`: exit 0 after the final bookkeeping edits.
- `tools/Test-AsciiOnly.ps1`: exit 0 with the new test source and all changed sources staged.

Logs use `cdr-c11-build.log`, `cdr-c11-format.log`, `cdr-c11-test.log`, `cdr-c11-diff.log`
and `cdr-c11-ascii.log` in the same ignored evidence directory. All production hashes still
match the exact restored mutation receipts. No PowerShell source changed, so Pester and
PSScriptAnalyzer were not required or run. The token meter ran against the committed baseline;
its Claude-only transcript scope excludes this native Sol slice, so no slice usage is attributed.

The configured global pre-commit wrapper only chains an executable local pre-commit hook,
which is absent. This slice uses a normal configured git commit; no hook/trust/configuration
change or bypass. Direct master work has no branch closeout. No live directory/tenant action,
browser render, installed-cmdlet check, deployment or push was run. Self-review was not run.

Independent Astra workflow review at max is the pending next gate. C5 follows that review;
the implementation commit remains local under push policy ask.
