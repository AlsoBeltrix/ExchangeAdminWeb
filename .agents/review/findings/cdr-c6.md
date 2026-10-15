# cdr-c6 - keep the displayed Comms-10k ticket bound to approval

Status: Implemented and verified on master; independent Astra max workflow review pending. Push pending.

Approved scope: `docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, C6.
Owner 2026-10-15: "approved. go." Implementation begins on clean master
`0413e3b81cabda8b9c70e9feed6e4d8beab8f5b5`; both origin and github master were checked
with `git ls-remote` and remain `885397981d7550afebf6518ef21b4fc54b062859`.

## Evidence and declared scope (before code changes)

`Components/Pages/Comms10k.razor` binds the ticket input directly to ticketNumber
and disables it only while isLoading. ValidateEmails captures and validates a
trimmed ticket before its awaits, and Comms10kConfirmation retains that ticket.
ExecuteReplace correctly enqueues the captured approval ticket. After validation,
an edit changes the displayed ticket while the job/audit/notification keep the old
one. Revision 6 correctly identifies a display defect, not unvalidated submission.

Lock the ticket input while busy or while a successful approval exists; render the
approval's captured ticket and refuse delayed input callbacks during those periods.
Normalize the displayed input to the captured trimmed ticket before validation's
first yield. Preserve optional blank tickets, current validation, editability after
failure, and existing new-upload/successful-submission reset flow. Add minimal
compiled render-frame/event/handler coverage through actual job persistence and
processor audit/notification. Prove every new case with compiled production
regressions and standard Assert failures, then exact-byte/MD5/SHA256 restoration
and newer mtimes before the final sequential gates.

Preserve C4 immutable ticket/member approval, C2 authorization/current actor,
C5 cached job scope, C7 busy/atomic parse behavior and C8 exports. No service,
worker or shared production change, new ServiceNow behavior, protection or
serialization change, live integration probe, self-review, subagent, push,
deployment, branch/history operation, hook/trust/config change or broad cleanup.
Comms module patch only; base stays 2.27.3. Deferred psl-2/3/4 and
cdr-followup-1 remain outside this slice.

Comms10k has no line-keyed ClickGate entries; the re-anchor playbook therefore
requires no line-key rewrite. ProgressRegistry continues to pin both validation
calls and their activity window; no call or window changes are planned. Known
module-spec Version 2.0 / June 25 versus base app 2.27.3 drift is flagged.

## Implementation

The existing text input now binds through the private TicketInput property.
TicketLocked is true while isLoading or confirmation exists. Both the generated
input's disabled attribute and its setter use that same predicate, so a delayed
oninput callback cannot replace a captured ticket before the lock reaches the
browser. The getter uses confirmation.Ticket whenever approval exists. Validation
normalizes ticketNumber to its already-captured trimmedTicket before its first
yield, making the pending display match the exact validation/approval value.

No validation rule, optional-ticket behavior or write payload changed. A failed
ticket/directory validation has no approval and releases busy, so the field stays
editable. New uploads and successful enqueue already clear confirmation; those
existing resets release the lock. The prior ticket remains in the editable field
as before, and the next approval captures its then-current value. Comms advances
1.4.17 -> 1.4.18; base remains 2.27.3. There is no shared production change.

Comms10k remains outside line-keyed ClickGate registration. The re-anchor
playbook therefore needs no line mapping or count update. ProgressRegistry's
ServiceNow/AD call pins and activity envelope still apply without alteration;
the restored affected-workflow test run includes both registry suites.

## Compiled behavioral coverage

`ExchangeAdminWeb.Tests/Comms10kTicketDisplayTests.cs` adds eight cases to the
existing partial fixture. It reads the compiled input's value/disabled attributes
and invokes the actual generated oninput binder delegate. This is compiled
render-frame and handler evidence, not a browser or rendering-host claim.

- Two cases (entered ticket with surrounding whitespace, optional blank ticket)
  inspect the locked approval display, invoke callbacks captured before and after
  validation, and assert the underlying ticket remains captured. ExecuteReplace
  persists the actual job, whose ticket matches the display. The real processor
  then resolves/replaces through existing local service substitutes and writes
  the real audit JSONL; its completion notification receives that same ticket.
  Successful submission unlocks the next draft without altering the queued job.
- Three cases suspend at the first yield, the configured ticket HTTP await or
  the credential/directory await. The input displays the exact trimmed capture,
  remains disabled, rejects a previously captured input callback and ends with
  matching approval. The configured validator requests the original ticket.
- Two cases fail configured ticket validation or directory resolution and prove
  the resulting field remains editable, without an approval or queued job.
- One case replaces an approved upload, proves the lock releases and approval is
  lost, then changes the ticket and validates/queues a fresh matching approval.

The existing HTTP fixture now permits an empty ticket response, defaulting to
its previous valid behavior. Its audit assertion accepts a configurable expected
ticket, still requiring existing nonblank metadata. The first focused attempt
found the fixture assumed an audit ticket property always exists; AuditService
intentionally omits optional blanks. The corrected assertion accepts omission
only for an expected empty ticket. The accepted compiled baseline passed 8/8.

## Compiled mutation proof

All probes run `dotnet test ExchangeAdminWeb.slnx -c Release` with the C6 filter,
four xUnit threads and fresh TRX output. No --no-build or compile-error proof.
The checker rejects compiler errors, unexpected/missing cases, failures outside
the expected per-case set, or failures without standard Assert.* messages.
Each runtime app DLL hash equals its freshly compiled app DLL hash, and its
compile mtime is newer than the mutated production page.

| Production regression | Intended assertion failures | Passing controls |
|---|---:|---:|
| Restore the original raw ticket binding and busy-only input disable | 6 | 2 |
| Prevent ticket edits while a validation failure result exists | 2 | 6 |
| Remove pre-yield normalization of the captured ticket display | 7 | 1 |
| Retain approval across the existing upload and enqueue resets | 3 | 5 |

The 18 assertion failures have an exact per-case union of all eight new cases.
Each probe restores exact fixed bytes, verifies MD5/SHA256 and advances the
restored page mtime beyond its mutated mtime. The fixed page hashes are:

- MD5 `12081992fa5e4183788d6118f8091572`
- SHA256 `0e6213967a701acef42fe255f2ae954d6e0a13c44b0269055cfcae5f4b726d4e`

Machine-local evidence is under `.agents/review/out/cdr-c6/`: `probe.py`,
`proof.json`, the four named `*-evidence.json` receipts and TRX/log directories,
and `fixed-page.bin`. These outputs are gitignored evidence, not durable status.

## Limits and remaining work

No browser/rendering host, live AD/tenant/vault, deployment or push was exercised.
HTTP responses use local handlers and reserved invalid hosts; persisted jobs stay
in the fixture SQLite queue without a background worker dispatch. Processor
execution uses the existing local service substitute while its audit and email
argument handling are real. This proves the compiled ticket binding and its
durable handoff; it does not claim browser acceptance or actual directory writes.
No shipped PowerShell changed, so PSScriptAnalyzer/Pester were not run. No
self-review was performed. The token meter ran against the committed baseline;
it reads Claude transcripts and cannot attribute this native Sol slice.

Independent Astra max review is the next step; the parent coordinates it before
Defender D8. Remaining approved Defender work and deferred psl-2/3/4 and
cdr-followup-1 retain their existing dispositions. The known module-spec drift
remains flagged.

# Review Packet

## Mode

IMPLEMENTATION_REVIEW

## Original Request

Owner approved all twenty Revision 6 findings (2026-10-15: "approved. go.") and
sequential fresh Sol 6.1 max implementation with independent Astra max review.
Implement only C6: the visible ticket must agree with the captured submitted
ticket; prefer locking after Validate, preserve existing optional-ticket
validation and failure/new-upload behavior, and preserve the prior Comms fixes.

## Approved Plan

`docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, C6.

## Commit Range

`0413e3b81cabda8b9c70e9feed6e4d8beab8f5b5..HEAD` (this single C6 slice).

## Claimed Changes

- Bind the compiled input through a guarded property, disable it while busy or
  approved, and display the exact trimmed capture during validation and approval.
- Preserve failure editability and existing approval resets on new upload and
  successful enqueue, keeping previously queued job metadata immutable.
- Add eight compiled behavior cases and narrowly extend existing fixture helpers.
- Advance only Comms-10k to 1.4.18 and record scope, proof, gates and limitations.

## Constraints / Out Of Scope

- Preserve C4 immutable ticket/member approval, C2 current-principal authorization
  and refusal actor, C5 cached owned-job scope, C7 busy/atomic parse and C8 exports.
- No service/worker/shared refactor, ServiceNow change, protection/serialization
  replacement, live probe, deployment/push, branch/history/hook/trust/config change,
  unrelated cleanup, self-review or further delegation.
- Base app remains 2.27.3; known module-spec Version 2.0 / June 25 drift is flagged.

## Design Decisions

- Reuse confirmation existence as the approval lifetime; no new workflow state.
- Both disabled markup and the binder setter enforce the existing busy/approval
  lock because a previously dispatched input event may arrive after validation
  starts. The getter reads approval's ticket; submission still uses the existing
  immutable captured approval.
- Normalize only to the trim already used by validation, before its first yield.
  Optional blanks remain allowed; failure and reset release the input for edits.
- The generated binder delegate is exercised without ComponentBase render
  dispatch, matching the existing unattached compiled handler/frame harness.

## Deferred Work

- Independent Astra max C6 workflow review, then the approved Defender D8 sequence.
- psl-2/3/4 and cdr-followup-1 remain deferred; no scope added here.
- Browser and live integration behavior remain unexercised, as described above.

## Review Focus

Check the complete validate/confirm/submit/reset workflow: actual input attributes
and binder callbacks, delayed edits across each await boundary, optional blanks,
trimmed display equality, failure editability and approval lifetime. Follow the
captured ticket through actual persisted job, processor audit and notification
arguments. Check preservation of C4 approval, C2 authorization, C5 job caches,
C7 busy guards and C8 download behavior. No prior review conclusion substitutes
for this independent affected-workflow review.

## Verification Run

- Accepted compiled C6 baseline: 8/8 passed. Four compiled production probes
  produced 18 standard Assert failures with an exact union of all eight cases;
  runtime assembly provenance and exact-byte/MD5/SHA256/newer-mtime restores passed.
- Restored Comms/ClickGate/Progress run: 811/811 passed.
- Final Release solution build: 40 existing warnings / 0 errors.
- Full solution format verification: exit 0 before the single final full suite.
- Single final full Release suite: 4,148 passed / 3 skipped / 0 failed, using
  `-- xUnit.MaxParallelThreads=4` after full format verification passed.
- `git diff --check HEAD`: passed. `tools/Test-AsciiOnly.ps1`: passed with the
  new test file staged. No shipped PowerShell changed; no lint/Pester run.
- The final proof reconciliation (`proof-summary.json`) confirms all eight case
  names, the 18-failure union, fresh runtime assembly equality, exact restored
  page/test source hashes, 811 restored affected passes and the full-suite results.
  `final-gate.json` records build -> format -> full-test timestamps and exit 0.
  `closeout-gate.json` records the following diff/staged-ASCII checks, both exit 0.
