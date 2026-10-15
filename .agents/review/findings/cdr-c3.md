# cdr-c3 - refuse incomplete member resolution and record why

Status: Implemented on `master`; restored full gate passed; independent workflow review pending.
Push remains pending under policy `ask`.
Approved scope: `docs/Comms10kDefenderRemediation-Plan.md`, Revision 6, C3.
Baseline: clean `master` at `2b87f0e27fc0ed0af0d23fa0bc308c2e70626808`.
Origin HEAD verified at `885397981d7550afebf6518ef21b4fc54b062859` on 2026-10-15;
the implementation stream remains local under push policy `ask`.

## Evidence and declared files (before implementation)

`Comms10kService.ResolveEmailsAsync` currently returns `Success = true` even with
rejections. `Comms10kReplaceProcessor.ResolveAsync` publishes the shortened executable
DN list and returns Partial; Replace writes it. A failed resolve returns before the
only mutation audit and leaves `_result` unset for the completion notification.
`PerformReplace` opens its second runspace and imports AD outside its re-read catch;
a preparation exception escapes the processor's await and skips result/audit assignment.
The page offers Replace after a partial validation and lacks a direct-invocation
resolution guard. These are verified current control flows, not live AD observations.

Files declared for this finding:

- `Services/Comms10kService.cs`: all-or-nothing resolve success; catch preparation
  failures only while no mutation has been dispatched; narrow local test seams.
- `Services/Jobs/Comms10kReplaceProcessor.cs`: publish paired executable members only
  after full resolution; one refusal audit/result; specific completion reason;
  conservative treatment of an unexpected exception after dispatch may have occurred.
- `Components/Pages/Comms10k.razor`: require full, nonempty success and zero parse or
  resolver rejections before offering or enqueuing Replace; audit/notify refused submits.
- `Modules/ModuleCatalog.cs`: Comms-10k patch version bump only.
- `ExchangeAdminWeb.Tests/Comms10kRefusalTests.cs`: behavioral service, processor and
  page guard coverage; every new case receives a compiled mutation proof.
- `.agents/review/findings/cdr-c3.md`, `.agents/review/index.md`, `.agents/state.md`,
  `.agents/token-log.md`, `docs/Comms10kDefenderRemediation-Plan.md`: evidence and records.
- `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` / `ProgressRegistry.cs` only if page
  re-anchoring proves necessary; follow the clickgate-reanchor playbook. Comms has
  no ClickGate line entries; progress handlers remain covered by registry tests.

## Boundaries

No C4 fingerprint or primary-member logic, C2 authorization snapshots, C11 count
redesign, locking or protected-principal checks. Preserve C10's paired GUID/DN values.
No self-review, agents/harness dispatch, branch changes, hooks, push, deploy, live AD
or tenant writes. The module spec's stale 2.0/June header versus app 2.27.1 is known;
Constitution audit, notification and fail-closed contracts govern this slice.

## Implementation

- The service's resolution `Success` now requires zero rejections; paired resolved
  identities and every rejection remain available for reporting.
- The processor publishes a copy of paired members only after complete, nonempty
  success. Any returned failure, rejection, empty result or thrown lookup refuses.
  Replace never calls the writer after refusal, even though the runner continues.
- One `Refuse` / `RecordResult` path stores the first refusal reason and emits one
  failed mutation audit. Completion reads that result, so it names the reason.
  C4 fingerprint and C2 authorization guards can reuse it; their logic is deferred.
- The page's offer and direct submission handler require full success, nonempty
  members and zero combined parse/resolver rejections. Refused submissions, including
  authorization denial, audit and admin-notify with their reason. The existing
  errors downloads remain reachable; the partial-apply exclusion promise is removed.
- Service runspace creation/Open/import failures before write-sequence entry become
  refusals. Exceptions after sequence entry escape that filtered catch; the processor
  records `CouldNotConfirm` and their reason, preserving possible changes. The
  service's ordinary read-back outcomes remain authoritative when returned.
- Module version `1.4.7` -> `1.4.8`; base version unchanged. No ClickGate or progress
  registry change was needed: Comms has no ClickGate line entries, and its registered
  progress handler/call identities survive the page line movement.

The existing C11 count-label limitation remains: a result that cannot confirm the
write may still carry a zero/default requested-written count. This slice preserves
the honest outcome and unknown final count; C11 owns the count model and labels.

## Guard coverage and execution limits

23 new cases use the real service resolver, a real PowerShell runspace with harmless
local functions for preparation faults, the real processor over a temporary audit
store and captured EmailService calls, and the compiled page property/direct handler.
The credential HTTP handler serves dummy values locally; no vault or AD is contacted.
Page offer wiring is a source assertion; it does not establish browser rendering.

The first harness build found a missing required SubmittedAtUtc test value; the
first test execution found pooled SQLite cleanup handles, then three assertions
expecting an unprefixed service message. These harness issues were corrected before
proofs; failure messages remain prefixed by the existing writer's outcome derivation.
The corrected focused Release run passed 520/520 (Comms, ClickGate, ProgressRegistry).

## Compiled mutation proof

All 23 new cases failed an intended assertion against compiled production mutations.
Eleven sequential groups produced 31 expected assertion failures; M3's thrown-lookup
case remained green as its control and was proved independently by M4. No unexpected
green, compiler-error failure or stale `--no-build` result was accepted. Each group
ran `dotnet test ExchangeAdminWeb.slnx -c Release --no-restore --filter ...` with
`xUnit.MaxParallelThreads=4`; TRX results were checked for exact failure counts and
`Assert.*` errors. Final restored tests build normally, without `--no-build`.

| Probe | Production regression | Assertion failures |
|---|---|---|
| M1 | service reports success despite rejections | 4 rejection causes |
| M2 | service rejects a complete resolution | 1 complete identity case |
| M3 | processor accepts failed/rejected/empty resolution | 3 cases |
| M4 | processor omits the mutation audit | 4 failed resolves + missing resolve |
| M5 | service lets preparation exceptions escape | 3 create/Open/import cases |
| M6 | processor loses its completion result | returned refusal + successful write |
| M7 | unknown write state claims refusal | 1 post-dispatch uncertainty case |
| M8 | page completeness guard always allows | 5 incomplete file states |
| M9 | page authorization refusal omits audit/notification | 1 denial |
| M10 | page offers Replace through its old conditions | 1 offer-wiring case |
| M11 | page direct handler skips completeness guard | 5 incomplete file states |

Each restore wrote saved fixed bytes, checked both hashes and advanced the modified
time beyond the mutated version. Restored hashes:

| Source | MD5 | SHA256 |
|---|---|---|
| Comms10kService.cs | `FFB365057B92C2567C41DFBFBF4B8F09` | `867AED08E54A3B40A40F450CF14A563AE7EF26C894AF1F6FCEF9665DA5BAA62C` |
| Comms10kReplaceProcessor.cs | `C496F7367413DFAC8A151CE8C5D28F0A` | `DE085529381233075F3416B93E1D18F0D286074AA53F2A83F7646500527FD437` |
| Comms10k.razor | `02ABD0ACD2C620F3E560257D70892685` | `51BA3FD624E6ACAF6CE7412AB39F608271937DEBD5BD0B7C18BE09776796B61B` |

Machine-local evidence is gitignored under `.agents/review/out/`: the focused log/TRX,
`cdr-c3-M*.log`, corresponding TRX directories, fixed byte backups, and
`cdr-c3-mutation-receipts.json` (test names, assertion messages, hashes and per-restore
mtime receipts). Those artifacts are diagnostic supplements; this record is durable.

## Verification

The restored final gate passed:

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors / 39 existing warnings.
- `dotnet test ExchangeAdminWeb.slnx -c Release -- xUnit.MaxParallelThreads=4`:
  3,907 passed / 3 skipped / 0 failed (3,910 total).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0;
  existing workspace warning only.
- `git diff --check HEAD`: exit 0.
- `tools/Test-AsciiOnly.ps1`: passed after staging the new test file.

Machine-local gate logs are `cdr-c3-build.log`, `cdr-c3-test.log`,
`cdr-c3-format.log`, `cdr-c3-diff.log` and `cdr-c3-ascii.log` under the same ignored
output directory. Global pre-commit configuration was re-checked read-only: its
existing script chains an executable repository pre-commit hook, which is absent.
No hook installation, trust/configuration change or bypass was performed.

## Review packet

### Mode

IMPLEMENTATION_REVIEW; independent review owed, no self-review performed.

### Original Request

Owner approved all twenty Revision 6 findings and sequential Sol 6.1 implementation
with independent Astra workflow review. This commit implements C3 only, including
the additional service-preparation case assigned by C1's reviewer.

### Approved Plan

`docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, C3.

### Commit Range

`2b87f0e27fc0ed0af0d23fa0bc308c2e70626808..HEAD` for this finding's commit.

### Claimed Changes

Complete resolution is required at the service, executable processor state, page offer
and direct submit guard. Refusals preserve the first concrete reason and audit once;
completion notifies with that reason. Pre-write preparation faults are refusals;
unexpected faults that may follow dispatch remain unconfirmed. Comms-10k is 1.4.8.

### Constraints / Out Of Scope

The Boundaries section above applies. No shared/base version bump, browser/live AD
acceptance, deployed behavior claim, hook changes, branch operation or outward action.

### Design Decisions

- Keep paired GUID/DN members through processor state; derive the compatibility DN
  list only when calling the current writer. C4 owns immutable member writes.
- Failed Resolve audits immediately because cancellation could prevent Replace.
  The runner still advances after failure, so Replace reuses the first result and
  avoids a second mutation audit.
- Known setup faults are caught before write-sequence entry; a later arbitrary throw
  cannot justify claiming nothing changed and therefore becomes CouldNotConfirm.
- UI validation/resolve itself remains a read. A direct failed Replace submission
  is the mutating request that audits/notifies on the page before any job exists.

### Deferred Work

C4 fingerprints/primary members/member identity, C11 written-count truth, C5 submitter
job visibility, C2 live authorization snapshots, and C7 shared busy/re-entry/stale
confirmation handling remain separate approved findings. No locking or protection
checks are added under any of them here.

### Review Focus

Trace validate/submit -> runtime resolve -> failed-row continuation -> replace ->
completion. Check service success semantics, executable-state publication, no writer
call on any rejection, one audit per attempt, and preservation of the refusal reason.
Trace runspace create/Open/import throws separately from exceptions that may follow
dispatch; check the write-uncertainty claim. Include the page's parse rejections,
direct guard and authorization denial. Review the whole affected workflow, with C4,
C2 and C11 still pending; do not infer their planned guards already exist.

### Verification Run

See the compiled proof and Verification sections above. No browser render, live
directory/tenant query, mutation or deployment. No shipped PowerShell source changed;
PSScriptAnalyzer/Pester were not required or run.
