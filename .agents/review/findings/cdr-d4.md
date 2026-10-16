# cdr-d4 - audit the applied Defender filter result

Status: Implemented and verified on master; independent Astra review pending, 2026-10-16.

Approved scope: `docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, D4.
Owner 2026-10-15: "approved. go." Recovery 2026-10-16: "system restarted. please
recover and continue." Implementation starts on clean master
`dc37c7ad87392ee82d4d211ec55e64c382f90153`. Both live origin and github master
refs were checked with `git ls-remote` and remain
`885397981d7550afebf6518ef21b4fc54b062859`.

## Evidence and declared scope (before code changes)

The current compiled-page source confirms D4. LoadAsync audits
`loaded.Devices.Count` before FilteredDevices applies Site and Seen-by.
DescribeFilters accepts only DefenderDeviceFilters, so neither page-only filter
is recorded. DownloadCsvAsync records only a row-count target. The unavailable
Site marker and whole-run/per-row Seen-by escape deliberately keep rows whose
location cannot be evaluated. Calling those filters simply applied would conceal
that degradation. In addition, LoadAsync currently overwrites appliedSite and
appliedSeenBy before its first await while the previous result remains stored.

Capture the entire requested question before awaiting. Publish its location
values and audit snapshot with the result they describe. Record requested
filters, request-filter completion, per-location-filter evaluated/unavailable
candidate counts and the final displayed/exported count. Availability counts
describe each filter independently across the loaded candidates; an unavailable
value still does not bypass a different filter's real negative. Export captures
the applied snapshot beside its selected rows before awaiting; form edits during
or after a load must not rename the result. Incomplete/failed inventory does not
claim completed evaluation, and export selection failure does not invent a count.

Keep exact row selection, all unavailable-row escapes, no-work empty/refusal
export behavior, paging-independent export, successful transfer/audit-failure
isolation and D9's safe failure notice. Use local page structures and established
AuditService extras. Add meaningful compiled page/audit regressions, freshly
compiled production-regression proof of every new case, exact-byte/MD5/SHA256
restoration with newer source mtimes, full logs/TRX and app/runtime/test provenance.
Re-anchor page registries through committed-to-working equal diff opcodes,
including GatedTwinButtonLine and ExpectedLineCount (currently 1217).

Defender 1.6.9 requires a module bump and its pinned catalog expectation. Base
stays 2.27.4 and Comms stays 1.4.18 unless shared production behavior changes.
Known AdminModuleSpec Version 2.0 / June 25 drift remains flagged.

D2 singleton reason isolation, D7 deadline/cancellation, D1 first-render fetch,
D3 server reentry and D5 caching/parsing are separate slices. Only touched-file
D6-local stale comments are included. psl-2/3/4, cdr-followup-1 and continuation
host validation remain deferred/unapproved. No protection/locking, authorization,
credential, dependency, config/runtime database, shipped PowerShell, live calls,
push/deploy, branch/history/hook-trust change, self-review or delegation is
included. Verify, commit this one slice, then stop for independent Astra review.

## Implementation

The page captures the service filters, Site and Seen-by before its first await.
Its two local location values, immutable FilterAuditSnapshot and returned result
publish together after the service returns. A pending load therefore leaves the
previous result's question intact. A failed load retracts result and its applied
audit snapshot. The existing first-render fetch, default CanBeOnboarded value,
service API, authorization and reentry behavior are unchanged.

RequestedFilters (also the lookup target) includes the requested location values.
RequestFiltersEvaluation distinguishes completed evaluation from an incomplete
or failed inventory. SiteFilterEvaluation and SeenByFilterEvaluation each carry
status plus evaluated/unavailable candidate counts. Those counts independently
cover the service-returned candidates, before combining location predicates.
Known blank values remain real negatives. Missing whole-run Seen-by enrichment
and the existing per-row markers remain unavailable, with their original escape
behavior. InventoryDevices retains the pre-location count; Devices is the final
displayed count, including a successful filtered-empty zero. Existing distinct,
request, range, ceiling and outcome metadata remain. Progress also reports the
displayed count.

Export captures the applied audit snapshot before row selection and awaits, then
uses it with the selected rows and captured enrichment. Successful export keeps
the row-count target and records the same filter/evaluation metadata plus the
actual exported Devices count. Failure keeps D9's safe notice, separate audit and
inventory, recording confirmed exported Devices as "Unknown" and SelectedDevices
as the selected count or "Unknown" when selection failed. Explicit unknowns are
needed because the established JSONL writer omits null fields. SafeAudit still
isolates audit failures after completed delivery. Empty/refused inventory starts
no export work or export audit.

Defender advances 1.6.9 -> 1.6.10, including the existing pinned catalog test.
Base remains 2.27.4 and Comms remains 1.4.18; no shared production behavior changes.
The touched CSV column comment now correctly says twenty-nine (D6-local).

The committed-to-working equal-opcode reanchor maps all seventeen registered
controls to the same lines, including export 392 and pager 470/472. This page has
no GatedTwinButtonLine entry and no unmapped controls. ExpectedLineCount changes
1217 -> 1285. ProgressRegistry documents the captured applied export audit.
Exact mapping receipt: `.agents/review/out/cdr-d4/reanchor.json`.

## Compiled coverage and production regression proof

`ExchangeAdminWeb.Tests/DefenderFilterAuditTests.cs` invokes the actual compiled
page handlers, existing internal service seam, canned HTTP clients, directory
command stub, real filtering/CSV projection, JS arguments, progress and concrete
AuditService/JsonlLogService. A minimal renderer attaches the page for its real
StateHasChanged call while suppressing unrelated child rendering. No database,
credential lookup, live network, directory or browser is used.

Eleven cases cover: a 100-candidate inventory reduced to five displayed rows;
known blanks/negative values versus unavailable Site and partial Seen-by values;
whole-run not-attempted/failed enrichment; a pending load with form edits, an
export of the prior result, post-load form edits and a replacement load during
export delivery; filtered-empty/refused/failed lookup after prior results; and
selection/browser failure metadata with safe D9 handling. Both unavailable and
whole-run tests compare actual exported row IDs with the displayed IDs. The
pending-export scenario intentionally exercises existing overlap behavior; D3
owns refusing that overlap later.

Two setup runs corrected fixture expectations for the existing JSONL camelCase
nested records, omitted nulls and a real two-row selection. They are not proof.
The first 11/11 fixed compilation and first eleven-failure probe were then
preserved under `pre-cancellation-proof/`: two analyzer warnings on timeout waits
were resolved by passing the test cancellation token before locking final test
bytes. The final separately compiled proof baseline passes 11/11 with those exact
bytes. All authoritative probes use that final baseline.

Authoritative proof and gate receipts are machine-local/ignored under
`.agents/review/out/cdr-d4/`. `proof.py` freshly compiles the solution for each
named eleven-case run, scans the complete log for CS/RZ/MSB compiler errors,
requires the exact expected failed-name set and standard Assert.* failures,
and records app/runtime/test DLL/PDB hashes and timestamps. The app must differ
from the fixed baseline, equal the runtime app copy, and be newer than changed
production source; the test DLL must be newer than its touched source. Every
probe restores the saved exact production bytes and MD5/SHA256, advances the
source mtime beyond the mutated compilation, and requires unchanged test bytes.
No --no-build or compiler/fixture failure counts as regression proof.

Final locked bytes:

- Page MD5 `9458d6e785cd5c5c3bdc6fde0e66bf94`, SHA256
  `c25a30bd1cda857acfb3081f81c56356223668ee356d4a3e827eab4fe5e74775`.
- New test MD5 `1e46bdee9a068acbff99022d4d656f01`, SHA256
  `e8196c1aee3ca259e27a80ff6da8f27112826f551b616b751bcc6ccce6b5fce3`.

Seven freshly compiled production regressions produced 28 intended assertion
failures. Their exact failed-name union equals all eleven final cases, with no
missing or unexpected failures. `proof-summary.json` owns the machine-readable
union; each `<probe>-evidence.json`, `<probe>-run.json`, full `<probe>.log` and
`<probe>/<probe>.trx` owns its actual failure messages and provenance.

| Compiled production regression | Failed / passed | Intended standard assertion |
|---|---|---|
| Omit requested Site and Seen-by from the stored snapshot | 11 / 0 | Assert.Contains |
| Audit the pre-location loaded count | 7 / 4 | Assert.Equal |
| Claim all requested location candidates were evaluated | 4 / 7 | Assert.Equal |
| Publish location values before the pending request returns | 1 / 10 | Assert.Equal |
| Rebuild export metadata from the current form | 3 / 8 | Assert.Contains |
| Read the applied audit after delivery and a replacement load | 1 / 10 | Assert.Contains |
| Report selected rows as confirmed delivery on export failure | 1 / 10 | Assert.Equal |

Exact eleven-case union, all prefixed
`ExchangeAdminWeb.Tests.DefenderFilterAuditTests.`:

```text
AppliedQuestionSurvivesFormEditsAndBindsExportsAcrossAReplacementLoad
EmptyOrUncompletedLookupReportsZeroDisplayedAndHonestEvaluation(outcome: "failed")
EmptyOrUncompletedLookupReportsZeroDisplayedAndHonestEvaluation(outcome: "filtered-empty")
EmptyOrUncompletedLookupReportsZeroDisplayedAndHonestEvaluation(outcome: "refused")
FailedExportKeepsAppliedFiltersAndDistinguishesSelectionFromConfirmedDelivery(stage: "browser")
FailedExportKeepsAppliedFiltersAndDistinguishesSelectionFromConfirmedDelivery(stage: "selection")
LookupAuditsTheFiveDisplayedRowsRatherThanTheHundredLoadedCandidates
LookupDistinguishesKnownNegativesFromUnavailableLocationValues(siteFails: False)
LookupDistinguishesKnownNegativesFromUnavailableLocationValues(siteFails: True)
WholeRunMissingEnrichmentDoesNotClaimTheSeenByFilterWasEvaluated(huntingFails: False)
WholeRunMissingEnrichmentDoesNotClaimTheSeenByFilterWasEvaluated(huntingFails: True)
```

All seven receipts verify exact restored bytes and MD5/SHA256, unchanged final
test bytes, newer restored source mtimes, a fresh changed app equal to its runtime
copy, and fresh test DLL/PDB provenance.

The first restored affected run passed 737 and failed one existing source-shape
test that searched for `appliedSeenBy = form.SeenBy`. Its invariant is capture
before the first await, now correctly expressed by `var seenBy = form.SeenBy`
followed by publication with the result. Only that existing literal/comment was
adapted; its before-await assertion remains, while the new compiled snapshot case
proves the behavior and failed the early-publication/live-form/late-result probes.
No production or new-case bytes changed. The failed receipt remains under
`initial-affected/`; the separately compiled restored affected run passes 738/738
with complete ClickGate/Progress plus Defender/AdSite/catalog coverage. The final
Release solution build passes with 40 existing warnings and zero errors.
The remaining ordered final gate passes as recorded below.

# Review Packet

## Mode

IMPLEMENTATION_REVIEW

## Original Request

Owner approved all twenty findings in Comms10kDefenderRemediation Revision 6 on
2026-10-15 ("approved. go."), with fresh sequential Sol 6.1 max implementation
and independent Astra max workflow review. On 2026-10-16 the owner said "system
restarted. please recover and continue." This commit implements only D4: audit
the requested filters, actual evaluation/degradation and final displayed/exported
count, using the applied result's stored snapshot rather than the editable form.

## Approved Plan

`docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, D4.
Scoped implementation/review authority: `.agents/decisions.md`, 2026-10-15.

## Commit Range

`dc37c7ad87392ee82d4d211ec55e64c382f90153..HEAD` (the single D4 code commit).

## Claimed Changes

- Requested filters include Site and Seen-by, with separate request completion
  and per-filter evaluated/unavailable candidate counts.
- Lookup Devices reflects final displayed rows, with InventoryDevices retaining
  the earlier count; filtered-empty and incomplete results remain honest.
- Location values and immutable audit metadata publish with their result.
  Export captures that applied metadata before selection/awaits and reuses it.
- Export failure distinguishes selected from unknown delivered count, retaining
  D9 notice/audit isolation and exact selection/unavailable escape semantics.
- Defender 1.6.10, catalog expectation, registry receipt and required records.

## Constraints / Out Of Scope

No protection/locking, authorization, credential, dependency, config/runtime
database, shipped PowerShell, live call, push/deploy, history/branch/hook-trust,
self-review or delegation change. Keep first-render loading, default filters,
blank Search, current reentry and uncached filtering until their separate slices.
Do not alter Comms-10k or its recorded exemptions. Known module-spec Version 2.0 /
June 25 drift against base 2.27.4 remains flagged.

## Design Decisions

- Evaluation counts describe each requested filter independently across the
  service's loaded candidates. Unavailability does not override another filter's
  known negative; the existing filtering expression remains intact.
- Missing whole-run Seen-by enrichment is unavailable even when cells are blank.
  Blank cells after successful enrichment remain real negatives.
- A completed zero-row filter is different from a refused inventory: the latter
  does not claim completed request or location-filter evaluation.
- The existing result and location fields remain; two small local immutable
  audit records add metadata without changing service contracts or implementing
  D5's future cache. Publication and export capture are synchronous.
- Failure audit Devices is explicitly "Unknown" for unconfirmed delivery, with
  SelectedDevices separately recording what was known. Null would be omitted by
  the existing JSONL writer. Existing safe error text and SafeAudit remain.

## Deferred Work

Fresh independent Astra max review is the next gate. After its disposition,
the approved order is D2, D7, D1, D3, D5. psl-2/3/4, cdr-followup-1 and the
continuation-host check remain deferred/unapproved. Physical-location integration
and actual browser/circuit delivery remain outside this local evidence.

## Review Focus

First verify intent alignment, approved scope and Constitution invariants, then
trace the complete lookup/filter/display/export/audit workflow. Check requested
versus evaluated filters, whole-run and per-row unavailable values, known blank
negatives, final counts, empty/refusal/failure, form edits during and after a
load, exports whose selected result is replaced while delivery awaits, and
completed-delivery audit-failure isolation. Reconcile exact failed names, complete
compiler logs, byte/hash/mtime restoration, runtime/test/PDB provenance, registry
mapping and sequential final gate. Prior independent clean verdicts are not
premises. Reentry enforcement, first-render behavior and caching belong to their
separate approved slices and are not claimed fixed here.

## Verification Run

The proof section owns the exact eleven-case union, 28 intended assertion
failures and exact-byte/hash/mtime/runtime/test/PDB evidence. The restored
affected run passes 738/738. The sequential final gate passes:

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 40 existing warnings, 0 errors.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: pass,
  before the single full-suite run.
- `dotnet test ExchangeAdminWeb.slnx -c Release -- xUnit.MaxParallelThreads=4`:
  4,184 passed, 3 known AD-unavailable skips, 0 failures. Actual named TRX and full
  log are under `final-full/` and `final-full-test.log`.
- `git diff --check HEAD` and `tools/Test-AsciiOnly.ps1`: pass with all twelve
  changed/new files staged, including the new test and finding.

Actual gate commands/times/results and complete logs are `final-affected.json`,
`final-build.json`, `final-format.json`, `final-full-test.json`, `final-diff.json`
and `final-ascii.json`, with matching `.log` files. `closeout-reconciliation.json`
requires exact hashes, proof union, registry mapping, final counts and sequential
gate order before the normal code commit. Final app SHA256 is
`2e6395f57114a31cf950e468060308ef96ac0ee6b874f392ffc544217ad6f7f0`, identical
to the test runtime app. Final test DLL SHA256 is
`cde4b3ae0224aabb2fddca2e4d4c10e4427873e0a08f7f3feb84d666a8521e20`; its
PDB and source timestamps/hashes are in the affected/full gate receipts. Both
production and new-case test source bytes remain exactly the locked proof bytes.

No self-review ran. PSScriptAnalyzer/Pester did not run because no shipped
PowerShell changed; proof runners are ignored local tooling. Live AD/Defender/
Graph, attached browser/circuit delivery, visual page acceptance and deployment
were not run. The minimal renderer supports handler lifecycle only and does not
establish whole-page rendering. The established Claude-only token meter ran with
the committed baseline; it does not measure this native Sol session, so no native
usage or cost is attributed. Fresh independent Astra max review is still owed.
