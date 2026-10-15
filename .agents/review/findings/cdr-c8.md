# cdr-c8 - safe Comms-10k CSV delivery and audit

Status: Implemented and verified on master; independent Astra max workflow review and push pending.

Approved scope: `docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, C8.
Owner 2026-10-15: "approved. go." Implementation begins on clean master
`2013553fb531837ebf890f99ff354ad92e58640b`; both origin and github master were checked
with `git ls-remote` and remain `885397981d7550afebf6518ef21b4fc54b062859`.

## Evidence and declared scope (before code changes)

`Components/Pages/Comms10k.razor` DownloadFull and DownloadRejections use raw
CsvWriter.WriteRecords, then browser interop, then Audit.LogLookupAction inside the
same failure-reporting try/catch. Formula-leading data is not neutralized, and an
audit exception after delivery reaches the export failure result.

Reuse `Services/CsvExport.cs` CsvExport.Write with explicit existing headers and
column ordering. Reuse the established Defender page SafeAudit pattern locally,
logging audit exceptions separately while preserving genuine download failures.
Add compiled page regressions for actual exported formula-leading and ordinary
quoted data and for post-delivery audit faults on each handler. Prove every new
case with compiled production regressions and explicit standard Assert.* failures;
restore exact bytes, MD5/SHA256 and advanced mtimes after each probe.

Preserve C7 first-statement busy refusal, captured inputs and raw rejection mapping,
C2 current actor/authorization and C5 cached jobs/lifecycle behavior. Keep headers,
column ordering, row ordering, filenames, transfer mechanism and existing results.
No shared service behavior, ticket locking (C6), protection/serialization changes,
browser/live integrations, push, deployment, branch/history/hook/trust changes,
self-review or further delegation. Module patch only; base remains 2.27.3.

Comms10k has no line-keyed ClickGate entries, as confirmed in ClickGateRegistry.
ProgressRegistry descriptions will be updated to match the established safe CSV
and audit path. Known module-spec Version 2.0 / June 25 versus app 2.27.3 drift is
flagged, not silently treated as current.

## Implementation

Both downloads now call the unchanged shared CsvExport.Write path. The explicit
headers remain `Email,SamAccountName,DisplayName` and `Address,Reason`; projections
retain every row in its existing order. UTF-8/base64 interop, MIME type and filename
patterns remain the existing ones. All cells receive the shared formula protection
and CsvHelper quoting contract.

Both successful-delivery audit calls use a page-local SafeAudit copied from the
established Defender pattern: invoke the audit action, catch its exception and log
it separately. Existing catches still report real read/serialization/interop faults.
The completion call remains below delivery and SafeAudit, and both existing finally
blocks release isLoading. No export success banner is added or existing result
cleared: an already-present result survives a successful export and audit fault.

The first-statement busy refusals, full-list local, pre-await rejection snapshot,
AllRejections raw mapping, C2 actor/authorization, and C5 cache generations are
unchanged. Comms-10k advances 1.4.16 -> 1.4.17; base stays 2.27.3. No shared
production helper changes. ProgressRegistry pins CsvExport.Write and records the
safe-audit order. Comms10k remains outside line-keyed ClickGate registration, so
the re-anchor playbook requires no line-key rewrite. An existing source guard's
writer-name assertion follows the new writer; new C8 coverage is compiled behavior.

## Behavioral regression coverage

`ExchangeAdminWeb.Tests/Comms10kDownloadTests.cs` extends the existing partial
Comms10kRefusalTests fixture/C7 interop recorder/C4 compiled page helpers. The
existing TestService listing seam can supply export details; actual GetMembersAsync,
member row mapping, page handlers, CsvExport and interop argument generation execute.
No rendered browser, AD module, directory, tenant or vault is contacted.

- Fourteen cases cover both downloads and all seven leading formula/control
  characters in every column. Each decodes the actual browser interop payload,
  parses it with CsvReader and checks exact headers, 14 ordered rows, unmodified
  ordinary text with embedded formula operators, quotes, commas and CR/LF, empty
  cells, existing literal quotes, MIME/filename patterns and busy release. Fourteen
  rows also keep the ten-row preview cap out of the export.
- Two cases use the existing concrete AuditService fault seam. The interop callback
  establishes successful delivery before the fault; the existing result remains
  the same instance, completion is successful with its row count, and exactly one
  separate error log contains the audit exception.
- Two cases throw the actual JSException from the awaited delivery and retain its
  failure message/result, avoid successful completion and any audit attempt, dispose
  the activity and release busy.
- One case fails the local credential response; the actual member read reports
  its existing credentials-unavailable failure with no transfer, audit or successful
  completion. Existing C7 cases continue to exercise download reentry and overlaps.

## Compiled mutation proof

Every run used `dotnet test ExchangeAdminWeb.slnx -c Release` with the C8 filter,
four xUnit threads and a fresh TRX; no --no-build or compile-error evidence. The
probe checker rejects compiler errors, unexpected case names, missing failures or
non-assertion failures. Every mutated app DLL has a timestamp newer than its changed
page. All 19 new cases first passed, then the following actual production regressions
produced 19 standard Assert.* failures in three sequential groups:

| Probe | Production regression | Failed / passed | Assertion |
|---|---|---|---|
| raw-csv | Restore both original raw CsvWriter.WriteRecords blocks | 14 / 5 | Assert.Equal |
| unguarded-audit | Restore both original unguarded post-delivery audit calls | 2 / 17 | Assert.Same |
| false-success | Change both existing export failure results to Success=true | 3 / 16 | Assert.False |

Exact per-case union (all names below are in
`ExchangeAdminWeb.Tests.Comms10kRefusalTests`; displayed control prefixes use the
same escaped spelling as xUnit):

| Method | Exact argument instances | Probe | Cases |
|---|---|---|---|
| C8DownloadsNeutralizeEveryColumnAndPreserveQuotedRowsAndOrder | handler="DownloadFull"; prefix="=", "+", "-", "@", "\\t", "\\r", "\\n" | raw-csv | 7 |
| C8DownloadsNeutralizeEveryColumnAndPreserveQuotedRowsAndOrder | handler="DownloadRejections"; prefix="=", "+", "-", "@", "\\t", "\\r", "\\n" | raw-csv | 7 |
| C8AuditFaultAfterDeliveryLogsSeparatelyAndKeepsTheSuccessfulOutcome | handler="DownloadFull" | unguarded-audit | 1 |
| C8AuditFaultAfterDeliveryLogsSeparatelyAndKeepsTheSuccessfulOutcome | handler="DownloadRejections" | unguarded-audit | 1 |
| C8BrowserDeliveryFailureRetainsItsFailureResultAndReleasesBusy | handler="DownloadFull" | false-success | 1 |
| C8BrowserDeliveryFailureRetainsItsFailureResultAndReleasesBusy | handler="DownloadRejections" | false-success | 1 |
| C8MemberReadFailureRetainsItsFailureResultWithoutDeliveryOrAudit | no arguments | false-success | 1 |

The failure-name union equals the passing baseline's exact 19-name set, with zero
missing or unexpected names. A false-success probe pins preserved failure behavior;
it is deliberately additional to reverting C8's two fixes.

After every probe the page was restored to exact original fixed bytes, MD5 and
SHA256, with a modification time newer than the mutation and its compiled DLL.
Fixed/restored MD5: `c6105461cff38b408ee88b997d51807a`.
Fixed/restored SHA256:
`bfcb07a9245f4d4bef3be53a3819b58ff254d565619ac2552f533ff6a8e59a7c`.
The restored Comms/ClickGate/Progress run passed 803/803; all 19 C8 cases passed.

Supplemental ignored receipts under `.agents/review/out/`: `cdr-c8-proof.json`,
`cdr-c8-{raw-csv,unguarded-audit,false-success}-evidence.json` and corresponding
TRX/log directories; `cdr-c8-restored-evidence.json` maps every exact name to its
probe. Those files supplement the durable record above. The first Python command
hit the Windows Store alias before any mutation; using the installed py launcher
resolved it. No failed proof run or stale result is counted as evidence.

## Final verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`: exit 0, 40 existing NuGet warnings
  and 0 errors (incremental after the restored focused compilation).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0,
  existing workspace-loading warning only; completed before full tests.
- `dotnet test ExchangeAdminWeb.slnx -c Release -- xUnit.MaxParallelThreads=4`:
  exit 0, 4,140 passed / 3 skipped / 0 failed, 4,143 total, 5 m 5 s. This was the
  slice's only full-suite run; no redundant full baseline was run.
- Exact-case reconciliation confirms all 19 current C8 cases passed, the three
  actual skipped rows, ordered build/format/test timestamps and the unchanged page
  hashes. Current test-source SHA256:
  `f5d35d850cb8f6efbee8fceb84837253adf17c09cc8b8da5affced0883f2b563`.
- `git diff --check HEAD`: exit 0. Staged `tools/Test-AsciiOnly.ps1`: exit 0 with
  the new test file included. Both repeated after final bookkeeping/staging.

Logs under `.agents/review/out/` use `cdr-c8-build`, `cdr-c8-format`,
`cdr-c8-final.log`, `cdr-c8-final/cdr-c8-final.trx`, `cdr-c8-final-gate.json` and
`cdr-c8-final-evidence.json`. Browser/hosted-circuit behavior and installed AD
cmdlet interoperability remain unverified. PSScriptAnalyzer/Pester were not run:
no shipped PowerShell file changed. No self-review ran.

The committed-baseline token meter ran successfully; its Claude-only transcript
scope excludes this native Sol slice, so no slice usage/cost is attributed.

# Review Packet

## Mode

IMPLEMENTATION_REVIEW

## Original Request

Implement approved C8 only: both Comms-10k downloads must reuse CsvExport.Write
formula-neutralizing output and the established SafeAudit behavior, so audit faults
after browser delivery cannot report a delivered export as failed. Preserve genuine
download failures, C7 busy ownership/captured inputs/raw rejections, C2 current actor
and authorization, C5 job caches, columns/order/names. Add compiled page regressions,
prove every new case through compiled regressions, restore exact bytes/hashes/newer
mtimes, run the ordered full local gate, record and commit this one finding. Fresh
independent Astra max workflow review follows; no self-review.

## Approved Plan

`docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, C8.
Owner 2026-10-15: "approved. go." `.agents/decisions.md` owns the sequential fresh
Sol 6.1 max implementation and independent Astra max review arrangement.

## Commit Range

`2013553fb531837ebf890f99ff354ad92e58640b..HEAD` (the implementation commit carrying this packet).

## Claimed Changes

- Explicit existing column projections through unchanged CsvExport.Write in both downloads.
- Post-delivery audit failures logged separately through the established local SafeAudit pattern.
- Nineteen compiled behavioral cases, all mutation-proven with exact-name union and restores.
- ProgressRegistry/source-writer pointer updated; Comms-10k 1.4.17; base stays 2.27.3.

## Constraints / Out Of Scope

No shared helper behavior, worker/service/write changes, ticket locking (C6), current
authorization/cache changes, protection or serialization/locking change, transfer
redesign, broad cleanup, live AD/tenant/vault/browser probe, push, deployment,
branch/history/hook/trust change, self-review or further delegation. Known
module-spec header drift stays flagged.

## Design Decisions

- Spell the existing property headers and column order explicitly because the shared
  writer operates on cells; preserve all rows and the local captured sources.
- Keep SafeAudit local, matching Defender's existing pattern without a shared refactor.
- Preserve the prior page result and progress success after delivered exports, including
  audit faults; the existing catches still own actual download failure reporting.
- Reuse the existing compiled harness and concrete audit fault seam; no new production seam.

## Deferred Work

C6 is next in Part 1, then approved Defender slices in plan order (D2 before D7).
psl-2/3/4 and cdr-followup-1 remain unapproved. Browser/hosted-circuit behavior and
installed AD cmdlet interoperability remain unverified. All remote work is unpushed
under policy ask.

## Review Focus

Trace the whole affected download workflow: membership listing through explicit
projection, AllRejections parse/resolver/duplicate/raw mapping through its pre-await
snapshot, shared quoting and formula protection, awaited interop, safe audit,
completion, catch and finally. Include direct busy reentry and interactions with
validation/submission and jobs ownership. Check preservation of columns, row order,
filenames, prior results and genuine failures, and the meaning of every compiled
assertion/probe. C6 and deferred follow-ups are outside this slice.

## Verification Run

Baseline C8 19/19; three compiled mutation groups / 19 explicit Assert.* failures
cover all 19 new cases; exact bytes/MD5/SHA256/newer mtimes restored after each.
Restored affected workflow 803/803 passed. Final Release build passed (40 existing
warnings / 0 errors), full format verification completed before the single full
suite (4,140 passed / 3 skipped / 0 failed); staged ASCII and diff checks passed.
No browser/live integration, push, deployment or self-review was run. Independent
Astra max workflow review is pending.
