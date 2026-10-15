# cdr-c9 - a terminating PowerShell invocation retains the failed command

Status: Implemented; approved Revision 6 C9, independent Astra review pending.

## Evidence and triggering condition

Baseline `8581484`, `Services/Comms10kService.cs`: `PerformReplace` shares one PowerShell
object between import, target re-read, clear, add batches and both read-back queries.
`ClearMembers` queues `Set-ADGroup -Clear member -ErrorAction Stop`, calls `ps.Invoke()`,
then calls `ps.Commands.Clear()`. A terminating invocation skips the clear. `WithRetry`
re-enters the operation on that object; `ExecuteSequence` catches a failed write and always
calls `ReadBackMembers` on it. Appending another command can therefore replay the failed
mutation during either a retry or the verification read. The add and read operations use
the same unsafe invocation/clear ordering.

The Release baseline passed on 2026-10-15: exit 0, 0 errors, 22 existing NU1903 warnings.
Local master was `8581484`; both remotes reported `8853979`, consistent with local-only
approval bookkeeping. Module-spec version drift (2.0 versus app 2.27.1) is already flagged
in state; relevant credential and AD contracts were read against the code.

## Declared files (before implementation)

- `Services/Comms10kService.cs` - central invocation cleanup used by every operation.
- `ExchangeAdminWeb.Tests/Comms10kPowerShellLifecycleTests.cs` - actual SDK regression guards.
- `ExchangeAdminWeb.Tests/Comms10kReplaceSequenceTests.cs` - correct the affected coverage note.
- `Modules/ModuleCatalog.cs` - Comms-10k patch version only.
- `docs/Comms10kDefenderRemediation-Plan.md` - C9 implementation row only.
- `.agents/state.md` - current introduction only.
- `.agents/review/index.md` - C9 row only.
- `.agents/token-log.md` - one concise appended slice receipt.
- `.agents/review/findings/cdr-c9.md` - this evidence and review packet.

## Approach

Keep the existing runspace and command shapes. Route each invocation through a module-local
helper that clears the command collection in `finally` and clears the error stream when
invocation throws, preserving the original exception for the retry classifier. Successful
invocations retain their errors only until existing per-operation error handling inspects
and clears them. Exercise the actual write/read methods with harmless local functions in
an isolated runspace; test queue/stream cleanup, explicit retry, and the writer's failure
read-back path. No AD module import, network request or directory mutation is used in tests.

## Guard proof

`Comms10kPowerShellLifecycleTests` runs the actual SDK against local functions named
`Set-ADGroup`, `Get-ADGroup` and `Get-ADObject`. Each failure first emits a non-terminating
error, then throws. Queue and error-stream guards cover clear, add, target re-read and both
read-back queries. The retry cases invoke the actual write operation twice; the read-back
cases pass the actual operations through `ExecuteSequence` and assert its calls and observed
membership. The classifier's existing pure tests remain separate from this wiring proof.

All 14 new cases passed in the initial compiled run (exit 0). The mutation restored the old
behavior in `InvokeAndClear`: invoke, clear commands only on return, return the output.
Both projects compiled with zero `error CS` matches, then every new case failed an assertion:

| Guard | Mutation result |
|---|---|
| No queued command after each of five terminating failures | 5 `Assert.Empty` failures |
| No retained error stream after each failure | 5 `Assert.Empty` failures |
| Retry executes one new clear/add | 2 failures: observed three invocations instead of two |
| Writer's failure read-back does not write | 2 failures: observed `clear`/`add` where `members` was expected |

Mutation test exit 1: 14 failed, 0 passed. Fixed service MD5 before and after restoration:
`F0779897C18DA12DB73153F38CA4159B`; mutated MD5: `A883F7621F5A6F3D4745D5080580E077`.
Restoration used the original bytes, verified the MD5 and updated mtime before the Release
build, so the subsequent tests cannot use the mutated binary. Machine-local full output:
`.agents/review/out/cdr-c9-mutation.log`.

The first format check found CRLF introduced by the edit; the service was then normalized to
the repository's required LF with no semantic or line-number change. Final LF service MD5:
`40CA202315E9F2152B897D25A860E2AC`. The original mutation proof hashes above are preserved.

## Verification

Release build after restoration: exit 0, 0 errors, 39 existing package/nullability/unused-field
warnings. Full Release solution tests: exit 0, 3,844 passed / 3 skipped / 0 failed, 3,847 total,
duration 6m02s. Commands used the solution and compiled normally, without `--no-build`.

Initial format verification exited 1 for the service's CRLF; the LF-normalized rerun exited 0
(workspace-loading warnings only). `git diff --check HEAD` and `tools/Test-AsciiOnly.ps1`
both exited 0; the new test file was staged before the tracked-file ASCII check. No PowerShell
source files changed, so PSScriptAnalyzer/Pester were not applicable. No live directory checks
were run. The newline-only correction does not require another full test or mutation run
(parent direction); semantics and line numbers are unchanged.

## Review packet

### Mode

IMPLEMENTATION_REVIEW

### Original request

Owner approved Revision 6 and sequential Sol 6.1 max implementation with independent Astra
max review. This slice implements C9 only: failure must discard queued PowerShell mutation
state before retry or read-back.

### Approved plan

`docs/Comms10kDefenderRemediation-Plan.md`, approved Revision 6, C9.

### Commit range

`8581484..HEAD` at this finding's implementation commit; parent supplies the resolved head.

### Claimed changes

- All 15 Comms-10k invocations route through cleanup in `InvokeAndClear`.
- Commands clear in `finally`; an invocation exception clears error records and is rethrown
  unchanged. Existing non-terminating-error handling and retry classification remain in place.
- Four static operations are internal so tests exercise their actual command wiring.
- Comms-10k version `1.4.4` -> `1.4.5`; directly affected coverage comments corrected.

### Constraints / out of scope

No C1 or later finding, push, deployment, live AD/tenant access, locking or protection changes.

### Design decisions

Keep one runspace and the existing PowerShell object so imports and session state are reused.
Cleanup belongs at every invocation, including read-only paths. Successful error streams stay
available to the caller's existing handling; thrown invocations must clear them immediately.
The helper rethrows rather than wrapping exceptions so the retry classifier keeps their types.

### Deferred work

All other approved findings remain pending. The C1 name/DN addressing gap is deliberately
unchanged. No live directory acceptance was run.

### Review focus

Trace the service, the retry delegate and the writer's failure-to-readback path. Assess the
complete command/error lifecycle and whether the SDK tests establish isolation. Independent
review must include callers and lifecycle, not just the diff. No coder self-review was run.

### Verification run

All required gates passed; see the guard proof and verification sections above. Independent
Astra review is the remaining step before this slice is accepted and C1 begins.
