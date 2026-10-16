# cdr-d9 - handled Defender CSV export failures

Status: Implemented and independently APPROVED on master, 2026-10-16.

Approved scope: `docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, D9.
Owner 2026-10-15: "approved. go." Recovery 2026-10-16: "system restarted. please
recover and continue." Implementation begins on clean master
`1f1b93d89f503b4c37171d547409533b7dcebbf4`; both origin and github master were
checked with `git ls-remote` and remain `885397981d7550afebf6518ef21b4fc54b062859`.

## Evidence and declared scope (before code changes)

`Components/Pages/DefenderEndpointDevices.razor` DownloadCsvAsync selects
ExportableDevices before its try. The try builds CSV, encodes it, awaits browser
delivery and safely audits success. It has a finally that lowers isDownloadingCsv
but no catch. A selection, projection or JS exception therefore escapes without a
handled export notice or a failed-export audit. The loaded inventory otherwise
remains in result; using loadError would hide it behind a failed-inventory branch.

Handle export selection/projection/delivery failures with a separate operator-safe
notice and a failed ExportCsv event through the existing SafeAudit wrapper. Never
copy raw exception messages/payloads into that notice, audit or new diagnostics.
Keep the loaded result, eligibility, filtering, unavailable markers, CsvExport
protections, successful filename/MIME/transfer/audit semantics and finally cleanup.
Clear the export notice on a new load or attempted nonempty export. Re-anchor the
page's ClickGate controls with committed-to-working diff equal-opcode mapping,
update ExpectedLineCount and the affected ProgressRegistry description.

Add minimal compiled page regressions for real selection/projection/JS failures,
failed-audit isolation and successful delivery. Prove every new case with freshly
compiled production regressions and intended standard Assert.* failures, restoring
exact bytes plus MD5/SHA256 and advancing mtimes after each probe. Run the full
affected ClickGate/Progress set before one sequential final full local gate.

Module-only behavior requires Defender 1.6.8 -> 1.6.9 and the existing catalog
version assertion; base stays 2.27.4 and Comms 1.4.18. Known AdminModuleSpec Version
2.0 / June 25 drift against base 2.27.4 remains flagged.

D4 requested/applied filter snapshots and final/degraded counts, D2 reasons, D7
budget/cancellation, D1 first-load, D3 reentry and D5 performance are separate
approved slices. D6-local stale comments in touched files are permitted. The
continuation-host check, psl-2/3/4 and cdr-followup-1 remain deferred/unapproved.
No protection/locking, auth, credentials, dependencies, config/runtime databases,
shipped PowerShell, live integrations, push/deploy, branch/history/hook-trust
changes, self-review or delegation are included. Commit this slice and stop for
the parent-coordinated fresh independent Astra review.

## Implementation

The handler catches failures from export-row selection, CSV projection/encoding
and awaited browser delivery. A separate csvExportError notice renders above the
inventory branches, so a failed export does not replace result, set loadError or
hide the loaded table. The notice says only that the export could not complete
and invites retry; it does not claim that a failed interop acknowledgement proves
zero delivery. A new load or nonempty export clears it.

The failure path logs only the exception type, with no raw message/exception
object, and writes one failed ExportCsv / DefenderEndpointDevices audit with the
safe notice and target `Device inventory`. That target avoids claiming a known
row count when selection itself failed. D4 owns filter/count audit improvements.
The established SafeAudit wrapper isolates audit faults on both outcomes. The
success path retains row-count audit, filename/MIME, UTF-8/base64 delivery and
completion below awaited interop. No success notice is added.

The empty-set return still precedes the busy raise and starts no activity, audit
or transfer. A nested try/finally protects only actual started export work,
including progress creation and yield; it does not clear a busy flag on an empty
early return. The outer catch handles selection as well. Progress disposal remains
the failure fallback. Eligibility, applied location filtering, unavailable rows,
paging-independent complete export and CsvExport are unchanged. Reentry remains
D3's separate scope; no new guard/locking was introduced.

The committed-to-working page diff's equal opcodes mapped all 17 registered
controls. The first fourteen stay at their original lines. Download moves
387 -> 392 and the pager controls 465 -> 470 / 467 -> 472. There were no unmapped
or GatedTwinButtonLine entries in this page's registry. ExpectedLineCount is 1217
(actual lines, excluding a trailing newline split artifact). ProgressRegistry now
describes the handled selection/failure/audit path; stale touched flag comments
follow the actual flags and cleanup. Re-anchor receipt:
`.agents/review/out/cdr-d9/reanchor.json`.

Defender advances to 1.6.9, including the existing catalog assertion. No shared
production behavior changed, so base stays 2.27.4 and Comms stays 1.4.18.

## Compiled coverage and production regression proof

`ExchangeAdminWeb.Tests/DefenderCsvDownloadTests.cs` invokes the compiled private
page handler, real filtering, BuildCsv/CsvExport, UTF-8/base64 JS arguments and
concrete AuditService/JsonlLogService. The local IConfigStore stub supplies only
the configured-page credential ID for compiled frame construction; it opens no
database and makes no credential/API/directory request. Reflection and compiled
render frames follow the repository's existing page-test pattern. No browser or
attached rendering host is used.

- Six cases fail actual row enumeration during selection, enumeration during CSV
  projection, or awaited JS delivery, each with normal or faulting concrete audit.
  They require a handled Task, safe visible error, same loaded result, no load
  error, busy release, no successful completion, expected progress disposal and
  no completed transfer. With a functioning audit they read one actual failed
  JSONL event and check actor, IP, category, action, target and safe error; secret
  sentinels in the thrown message must be absent from UI/audit/diagnostics. With
  faulting audit they require its separate error log without changing export
  failure. After the local collection fault is removed, compiled render frames
  contain both the export notice and the retained inventory row.
- Two successful-delivery cases export the complete 52-row applied Site/Seen-by
  set while browsing page two, including the unavailable row and excluding the
  nonmatching row. They parse the actual JS payload, check row order, the 29-column
  header width, formula protection, quotes/commas/newline round-trip, ordinary embedded
  equals, unavailable cells, filename/MIME, prior-error clearing, unchanged result
  and page index, completion and busy/disposal. A post-delivery concrete audit
  fault logs separately and leaves the successful outcome intact.
- Three cases preserve no-result, complete-empty and refused inventory: no
  transfer, progress, audit, log, error or busy state is introduced.

An initial fixture compile failed because a Refusal factory argument was missing;
that was corrected before tests ran and is not proof. An initial focused 11/11
pass preceded the cleanup-scope adjustment. The separately named proof baseline
freshly rebuilt the final fixed page and tests and passed 11/11. No full baseline
suite was run.

Every proof run targets `dotnet test ExchangeAdminWeb.slnx -c Release` with the
DefenderCsvDownloadTests filter, fresh named TRX and four xUnit threads. The runner
advances test mtimes for fresh test compilation and records production/test source
hashes/timestamps, compiled app, runtime app, test DLL and PDB. It scans each full
log for compiler errors and rejects non-assertion, missing or unexpected failures.
Every mutated app DLL is newer than changed production source, differs from the
fixed baseline and matches the runtime app copy. Every test DLL is newer than its
test source. No --no-build or stale result is used.

| Compiled production regression | Failed / passed | Intended standard assertion |
|---|---|---|
| Entire original DownloadCsvAsync from base 1f1b93d | 8 / 3 | Assert.Null |
| Unguarded success audit after completed delivery | 1 / 10 | Assert.Null |
| Skip awaited browser delivery | 4 / 7 | Assert.Equal / Assert.Single |
| Remove the empty-set return | 3 / 8 | Assert.Equal |

Sixteen assertion failures have exactly this eleven-name union (no missing or
unexpected names):

```text
ExchangeAdminWeb.Tests.DefenderCsvDownloadTests.DeliveredExportKeepsFilteredRowsAndClearsFailureEvenIfAuditFails(auditFails: False)
ExchangeAdminWeb.Tests.DefenderCsvDownloadTests.DeliveredExportKeepsFilteredRowsAndClearsFailureEvenIfAuditFails(auditFails: True)
ExchangeAdminWeb.Tests.DefenderCsvDownloadTests.ExportFailureIsHandledAuditedAndKeepsInventory(stage: "browser", auditFails: False)
ExchangeAdminWeb.Tests.DefenderCsvDownloadTests.ExportFailureIsHandledAuditedAndKeepsInventory(stage: "browser", auditFails: True)
ExchangeAdminWeb.Tests.DefenderCsvDownloadTests.ExportFailureIsHandledAuditedAndKeepsInventory(stage: "projection", auditFails: False)
ExchangeAdminWeb.Tests.DefenderCsvDownloadTests.ExportFailureIsHandledAuditedAndKeepsInventory(stage: "projection", auditFails: True)
ExchangeAdminWeb.Tests.DefenderCsvDownloadTests.ExportFailureIsHandledAuditedAndKeepsInventory(stage: "selection", auditFails: False)
ExchangeAdminWeb.Tests.DefenderCsvDownloadTests.ExportFailureIsHandledAuditedAndKeepsInventory(stage: "selection", auditFails: True)
ExchangeAdminWeb.Tests.DefenderCsvDownloadTests.IneligibleInventoryStillStartsNoExportOrAudit(state: "empty")
ExchangeAdminWeb.Tests.DefenderCsvDownloadTests.IneligibleInventoryStillStartsNoExportOrAudit(state: "no-result")
ExchangeAdminWeb.Tests.DefenderCsvDownloadTests.IneligibleInventoryStillStartsNoExportOrAudit(state: "refused")
```

After every probe the page was restored to the saved fixed bytes, verified with
both MD5/SHA256, and its mtime advanced beyond the mutation and its compiled DLL.
The test bytes were unchanged and hash-verified throughout. Fixed/restored page:
MD5 `19cab3dab9515d471bcef8c6b2c02506`, SHA256
`dd17daf40554c94901550ced7626bff65bb1e2cfd255521ccd80ce57bd7fcf83`.
Mutation-proven test source SHA256:
`319ef6a698252bafc4f52b10e69645d50c7cd93d5777a114b69ea26d1f289231`.

The first full-format gate rejected one test object initializer's whitespace.
The exact correction only moves whitespace around its braces/properties; all
nonwhitespace test bytes are identical (`format-correction.json`). Production
bytes did not change. Original proof receipts remain untouched. Following the
parent's instruction, no mutation group was repeated solely for this nonsemantic
delta. A separately named fresh fixed compilation passed 11/11 in
`final-proof/proof-baseline/`; `final-proof/proof.json` records the final test DLL,
PDB and app/runtime provenance. Final formatted test SHA256:
`980dcfda62e8c5977bcb2cd8834c261a0e6f22fa56a6bfba671e8e5cef18e9fe`.

Supplemental ignored receipts are in `.agents/review/out/cdr-d9/`: `proof.json`,
`proof-summary.json`, `fixed-page.bin`, `fixed-tests.bin`, each probe's
`*-evidence.json`, complete logs and named TRX directories. The same directory
contains the proof and sequential-gate runners. These supplement the durable
scope/evidence record here; they are not shipped PowerShell.

# Review Packet

## Mode

IMPLEMENTATION_REVIEW

## Original Request

Owner 2026-10-15: "approved. go." for all twenty Revision 6 findings and the
sequential fresh Sol 6.1 max implementer / independent Astra max workflow review
arrangement. Owner 2026-10-16: "system restarted. please recover and continue."
Implement D9 only: export exceptions must produce a handled honest result and
safe failed-export audit, preserving loaded inventory, successful delivery and
audit-failure isolation. Prove every new compiled case, run the required local
gate, record and commit one slice, then stop for fresh independent review.

## Approved Plan

`docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, D9.
`.agents/decisions.md` owns approval and the model/workflow exception. D6-local
allows stale comments in touched files. Constitution versioning requires Defender
1.6.9; base 2.27.4 and Comms 1.4.18 remain current.

## Commit Range

`1f1b93d89f503b4c37171d547409533b7dcebbf4..HEAD` (one D9 implementation commit).

## Claimed Changes

- Handle export selection/projection/interop failures with a separate sanitized
  visible notice and safe failed ExportCsv audit; retain the loaded inventory.
- Preserve successful browser delivery, safe success audit and progress outcome,
  CSV contents/eligibility/filtering and finally cleanup.
- Add eleven compiled handler/CSV/audit/frame cases, with an exact eleven-name
  mutation-failure union, sixteen standard assertion failures, exact restoration
  and fresh compiled/runtime/test provenance.
- Re-anchor seventeen registered controls by diff mapping, update page line count
  and affected progress description, and advance only Defender to 1.6.9.

## Constraints / Out Of Scope

D4 requested/applied filter snapshots, evaluated/degraded filters and final audit
counts, D2 per-call reasons, D7 budget/cancellation, D1 first-load, D3 reentry and
D5 performance remain separate slices. No protection/locking, auth, credential,
dependency, shared-service, runtime-config/database, shipped PowerShell, live
integration, push/deploy, branch/history or hook trust change is authorized.
Self-review and further delegation were not performed. Known AdminModuleSpec
Version 2.0 / June 25 drift against base 2.27.4 remains flagged.

## Design Decisions

- The export error has its own field/banner. Reusing loadError would hide a valid
  loaded table and falsely describe export failure as inventory-read failure.
- Selection belongs inside the outer handled scope. Started work has an inner
  finally, preserving the no-work empty return before raising any busy state.
- A failed JS acknowledgement does not establish whether bytes reached a browser.
  The notice says the export could not complete without asserting zero delivery.
- The failed audit target is `Device inventory`, since selection can fail before
  an export count exists. D4 owns richer filter/count snapshots.
- New diagnostics log exception type only; notice and audit share fixed safe
  text. Raw backend/auth exception messages and objects are excluded there.
- Success remains below actual browser transfer and SafeAudit. Audit faults
  cannot turn a delivered file into an export failure. Progress disposal remains
  the failed-operation fallback.
- The final formatter correction changed test whitespace only. Original mutation
  receipts are preserved, exact nonwhitespace-byte parity is recorded, and a fresh
  fixed focused compilation establishes provenance for the final test bytes.

## Deferred Work

Fresh independent Astra max workflow review of this commit, then D4 and the
remaining approved Defender order. The continuation-host check, psl-2/3/4 and
cdr-followup-1 remain deferred/unapproved. Browser/circuit delivery and live
physical-location integration remain outside local test evidence.

## Review Focus

First verify original intent, approved scope and Constitution invariants. Review
the full affected export workflow, callers, inventory/error rendering, filtering,
result eligibility and progress/busy lifecycle. Check selection/projection/interop
faults, sanitized audit/logging, failed-audit isolation, completed delivery and
unavailable rows. Reconcile exact failed case names, full compiler logs, saved
restoration hashes, the nonsemantic formatter delta and final runtime/test/PDB
provenance with the receipts. Trace the surrounding workflow as needed; prior
review conclusions are not premises. D4's incomplete filter/count audit and the
other separately approved/deferred findings are not fixes claimed by this slice.

## Verification Run

The coverage/proof section above owns the exact eleven-name union, sixteen
standard assertion failures and restoration/provenance. Restored Defender,
AdSite, catalog and full ClickGate/Progress affected tests passed 727/727.
Final formatted fixed focused tests passed 11/11. The sequential final gate and
its exact counts/limits are recorded below; no self-review ran.

## Final verification and limits

- Restored affected set: `dotnet test ExchangeAdminWeb.slnx -c Release` with the
  Defender/AdSite/catalog/ClickGate/Progress filter and four xUnit threads passed
  727/727. This includes the entire ClickGate/Progress set and all eleven D9 cases.
- After the documented whitespace-only correction, a fresh final fixed focused
  compilation passed 11/11; final test bytes are bound to the retained mutation
  proof through exact nonwhitespace-byte equivalence.
- Final `dotnet build ExchangeAdminWeb.slnx -c Release`: exit 0, 40 existing
  warnings / 0 errors. Warning messages match the D10 final build. Production
  hashes remain the saved fixed bytes.
- Final `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`:
  exit 0, existing workspace-loading warning only, completed before full tests.
- The slice's only full suite, `dotnet test ExchangeAdminWeb.slnx -c Release --
  xUnit.MaxParallelThreads=4`, passed 4,173 / skipped 3 / failed 0 (4,176 total).
  Exact runner timestamps: 2026-10-16 13:02:47.858721 through 13:07:54.653004 UTC.
  The three skips are ADDirectorySearchServiceTests SearchUsers/SearchGroups/
  Search_WhenAdUnavailable_ReturnsEmpty. Every D9 exact case passed.
- `git diff --check HEAD` and staged `tools/Test-AsciiOnly.ps1`: exit 0, including
  the new test file. Both are repeated after final documentation staging.
- Final compiled and runtime app SHA256:
  `3ce7c8832e021950a7089124f9913da94acd53cacf4a99d7168ecd60d5fe8c90`.
  Final test DLL SHA256:
  `f4aacab8db6e0034e8cc44a8b3ae4e0934fad03c6d19e264b713c386ae672bc2`.
  Final test PDB SHA256:
  `9d9614b96a4db36df5618dc831c0bde762ed2189d2d9eae10fd39bca17630e6a`.
- No rendered browser, attached circuit, live AD/Defender/tenant/vault probe,
  push, deployment or self-review. PSScriptAnalyzer/Pester were not run because
  no shipped PowerShell changed. No auth, notification, configuration/database
  or adjacent-finding behavior changed.
- The committed-baseline token meter ran successfully. Its Claude-only transcript
  scope excludes this native Sol session; no per-slice token usage/cost is invented.

`.agents/review/out/cdr-d9/` owns supplemental `final-affected.json`,
`final-build.json`, `final-format.json`, `final-full-test.json`, `final-diff.json`,
`final-ascii.json`, corresponding full logs, and `final-full/final-full.trx`.
`closeout-reconciliation.json` binds the exact case union, every proof receipt,
restoration/format equivalence, final app/test/PDB provenance, ordered gate
timestamps, staged paths and unchanged warning set. Original proof receipts were
preserved. The pending step is fresh independent Astra max workflow review of
the committed range; the parent coordinates that review before D4 implementation.

## Independent workflow review - APPROVED, 2026-10-16

Fresh native `gpt-6-astra` at `max` reviewed immutable range
`1f1b93d89f503b4c37171d547409533b7dcebbf4..15e6531692d936ae368cf0539c85ca32d9cb9740`.
Verdict: **APPROVED**, no actionable findings. Intent and scope matched. The review
traced access/config gates, result eligibility, filtering, CSV generation, browser
delivery, notice/table rendering, audit isolation and real progress cleanup.

Before rebuilding, 117 independent reconciliation checks passed, including the
exact eleven-case/sixteen-assertion mutation union, independently reconstructed
mutation hashes, source restoration, formatter whitespace equivalence, final
assembly/PDB provenance, all seventeen registry mappings and ordered gates.
The recorded 727 affected passes and 4,173 full-suite passes / 3 skips reconciled.

Independent focused tests passed 727/727 with four xUnit threads. An ignored
compiled renderer/progress probe passed thirteen scenarios and 173 assertions.
Generated download events automatically rendered a safe notice alongside retained
table frames and released busy/progress after delayed JS failure, cancellation,
disconnect, uncertain delivery, CSV-only projection failure and transient selection
failure. Successful retry cleared the notice; audit faults preserved success.
The CSV-only failure rendered without repairing the test inventory first.

The receipt is `.agents/review/out/cdr-d9/astra-review.md`; supplemental evidence
includes `astra-evidence-reconciliation.json`, `astra-checks.json`,
`astra-affected/astra-affected.trx` and `astra-workflow.log`. No full-suite repeat,
tracked changes, browser or live integration checks occurred. The renderer probe
evaluated compiled page frames without activating unrelated child components.
Final diff checks passed; checkout/index stayed clean at the reviewed hash and no
task process remained. D9 is closed locally; D4 is next in the approved sequence.
