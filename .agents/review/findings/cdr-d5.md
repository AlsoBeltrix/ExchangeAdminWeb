# cdr-d5 - prepare subnets once and reuse applied filter results

Status: Implemented and independently APPROVED on master, 2026-10-16 (`91517a3`).

Approved scope: `docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6,
D5. Owner 2026-10-15: "approved. go." Recovery 2026-10-16: "system restarted.
please recover and continue." This single-writer slice starts on clean master
`999df700f4faf1435903ac76505b6f46e361c2f7`. Both live origin and github master
refs were checked at `eb0d19122f4921eca66e4300efe0605403214e23`; this workflow
issues no push. Nineteen original findings and psl-1 were already closed locally;
D5 is the last original implementation slice.

## Finding and implementation

The finding reproduces in current source: ApplySiteMatches called the raw-subnet
FindSiteCancellable for every device, and that matcher parsed every subnet inside
each scan. FilteredDevices rebuilt its Where/ToList result on every read. No live
forest was read or real subnet count measured; all counts below are synthetic
behavioral fixtures, not claims about a deployment.

AdSiteSubnetMap.Prepare now parses the raw list once for a load, retaining input
order and skipping the same malformed CIDRs. Matching consumes these prepared
values, preserving address-family separation, longest prefix, non-byte prefixes,
first equal-prefix tie, unlinked narrower subnet, invalid IP and no-match rules.
The public synchronous FindSite still checks a null list before parsing the IP,
returns empty for an invalid IP without enumerating subnets, and prepares a map
only for that call. Other callers retain their synchronous contract.

The service's raw preparation and prepared matching helpers use the original
four-minute cancellation token. Preparation checks before enumeration, before
each parse and after enumeration. Matching retains checks before devices, within
each subnet scan, at scan completion and immediately before publishing a Site.
Cancellation during preparation leaves every Site unavailable. Matching preserves
completed Site values and marks only unfinished rows unavailable. Inventory and
posture remain intact. SiteMapRead, partial notice, applied audit/filter and CSV
semantics remain as defined by D7/D4. The native worker still returns subnet data
only; admission, eventual cleanup and response-cancellation limits are unchanged.

The optional internal parse observer is passed per call and invoked at the actual
parser entry. Production leaves it null; there is no shared counter or callback
state. Tests count parser attempts, including malformed CIDRs, across multiple
devices and subsequent loads.

FilteredDevices caches the computed list against the result's reference and the
two applied strings independently. Keys publish only after computation succeeds,
so a failed computation cannot bind old rows to new keys. Null and subsequent
replacement results invalidate too. The original predicate now lives in
ComputeFilteredDevices: trimmed case-insensitive substrings, original row order,
seenByWasEvaluated escape and exact Site/observer unavailable markers remain;
known blank/nonmatching values keep their previous negative semantics.

The page grows from 1,277 to 1,295 lines. Equal-opcode mapping against committed
source resolves all 17 ClickGate entries, with no twins or unmapped anchors.
ExpectedLineCount is updated. ProgressRegistry now names subnet preparation under
the shared deadline. Defender/pin advances to **1.6.16**, base VersionPrefix,
AssemblyVersion and FileVersion advance to **2.27.7** for the shared helper;
Comms stays 1.4.18. Known module spec/guide Version 2.0 / June 25 baseline drift
remains flagged.

## Behavioral guards and proof

Twelve new compiled DefenderRecomputationTests cases cover one parse per subnet
over six devices and two loads, repeated count/paging/export list reuse, each of
the three independent cache inputs (including null and replacement results), all
four enrichment states' unavailable/known-negative/filter-order semantics, and
cancellation before, within and at the end of preparation. Deadline fixtures use
the same fake four-minute clock as D7; correctness does not depend on elapsed time.

The canonical fixed baseline passes **43/43**: all twelve new cases, all three D7
matching-deadline cases, existing page paging/filter guards and shared subnet
tests. Three source-shape tests now locate ComputeFilteredDevices, which contains
the unchanged predicate. Their unavailable Site, unavailable observer and exact
two-state seenByWasEvaluated claims are additionally checked by the four compiled
behavioral filter cases; the source assertions themselves are retained.

An existing D4 selection-failure fixture previously changed private-init Devices
through reflection after Load had already computed its filtered rows. That does
not invalidate an immutable-result cache. The fixture now publishes a replacement
result carrying FaultingRows with the same inventory metadata/enrichment state,
leaving the stored applied audit snapshot untouched. Export must recompute, fail
selection before delivery, and audit Unknown selected/exported counts against the
same applied question. The browser-failure case remains a delivery failure with a
known selection count. This is a test-only injection change, not a new production
invalidation input. `fixture-before.bin` / `fixture-delta.json` retain the exact
delta and its timing after the first four accepted probes. All production and
twelve-new-case bytes remain their canonical baseline bytes. Earlier test binary
hashes legitimately predate this fixture change; no identical test DLL hash is
claimed across it. The adapted cases run in restored affected and full coverage.

The two D7 enumeration guards now instrument prepared subnet enumeration and call
the same prepared matching helper used by the service. Their original visitation,
completed Site/posture retention, unavailable remainder, filter/audit/export and
final-publication assertions are retained. Their separate fresh guard proof is
recorded independently of the twelve-new-case union; unchanged old D7 proofs are
not replayed or counted as new cases.

Seven fresh compiled production regressions produce **12 intended standard
assertion failures**, with the exact union of all twelve new cases:

| Regression | Failed / passed | Intended assertion |
|---|---|---|
| Prepare inside each device iteration | 1 / 0 | Exact parser-entry count per load |
| Bypass cache reuse | 1 / 0 | Reused filtered/export list identity |
| Omit result key | 1 / 2 | Result-only replacement changes rows |
| Omit applied Site key | 1 / 2 | Site-only change changes rows |
| Omit applied Seen-by key | 1 / 2 | Seen-by-only change changes rows |
| Remove unavailable/unevaluated escapes | 4 / 0 | Exact filtered IDs/order in all four states |
| Remove preparation cancellation checks | 3 / 0 | Stop parser work and reject incomplete preparation |

Each group recompiles the Release solution and its selected test cases, without
--no-build. Complete logs are scanned for CS/RZ/MSB compiler errors. Root app,
runtime-copy app and test DLL/PDB hashes and mtimes are recorded; root/runtime
artifacts match and all compiled artifacts are newer than the touched production
and test sources. Production binaries differ from the fixed baseline. Every
mutation restores exact saved bytes, MD5 and SHA256, with a source mtime newer
than both mutation and compiled output. The twelve locked production/test paths,
TRX execution IDs, exact selected/failed sets and standard assertion messages
reconcile. The D4-only fixture provenance boundary above is explicit.

Two separate fresh compiled regressions prove the adapted existing D7 guards:
removing the inner prepared-subnet check fails the original visitation Assert.Equal;
removing both scan-end and immediate pre-publication checks fails the original
final-publication Assert.False. Both receipts are accepted, with an exact two-case
union and the same restore/provenance checks. These **two failures are separate
from the twelve new-case failures**. The actual paused service continuation guard
is unchanged and passes in baseline/affected coverage; no old full mutation replay
is claimed. Restored affected workflow coverage passes **1,015/1,015**, including all new and
adapted cases, both D4 selection/browser failure cases, shared callers, catalog
and the full ClickGate/Progress groups. The final ordered Release build passes
with **40 existing warnings / 0 errors**. Full solution format passes before the
single full Release suite (xUnit.MaxParallelThreads=4): **4,251 passed / 3 known
AD-unavailable skips / 0 failed**, 4,254 actual result rows and unique execution
IDs; dotnet wall time **305.70 seconds**. Numeric child exit **0** and start/end
timings are persisted before parsing. Repeated display names are retained;
raw notExecuted=0 is distinguished from the three actual NotExecuted rows, whose
names match the previously accepted full suite.

Final staged diff/ASCII and exact source/artifact/proof/ordered-gate reconciliation
are retained with the receipts; task/build-server shutdown occurs before handoff.
No production or test source bytes change after the accepted affected/final gates.

Page MD5: `a8d97f2a9c22d0fa50c0ca34a2b156b1`.
Page SHA256: `41ed5adcfa1b6970972cc0b8ad00fca77e255bfb160c8ff64dd33cc5bdad75b7`.
New-test MD5: `f6c0ab908974a09c0817299855814d57`.
New-test SHA256: `39b0fd0ecbeb5c6cbfb5c48d79cecf733e284b5a7c8ed818336e27a45583a0d2`.

## Evidence locations and limitations

Actual complete machine-local logs/TRX/receipts are ignored under
`.agents/review/out/cdr-d5/`: setup, scoped formatting, canonical proof baseline,
`proof.py`/`proof.json`, exact saved sources, named compiled production regressions,
`adapted-proof.py`, reanchor mapping, restored affected coverage, final ordered
gates, token meter, closeout reconciliation and process shutdown. No compiler
failure, stale --no-build execution or source-only mutation is accepted as proof.
Each command persists the numeric child exit and timestamps before parsing.

No browser/dev acceptance, AD/tenant/vault probe, deployment or push was run.
Compiled page properties and renderer guards do not claim browser acceptance.
No tracked PowerShell changed, so PSScriptAnalyzer/Pester are inapplicable.
The committed-baseline token meter covers Claude transcripts only and excludes
this native Sol session; no native token usage or cost is invented.

## Independent review

Fresh native `gpt-6-astra` at max reviewed
`999df700f4faf1435903ac76505b6f46e361c2f7..91517a32f49ae171c778fc9e6447b624de7d9858`
and returned **APPROVE**, with no actionable findings. Scope and Constitution
checks preceded the complete inventory/enrichment/preparation/matching and page
load/filter/paging/audit/export review, including the shared helper's callers.

The independent Release rebuild passed with 38 warnings / 0 errors, followed
by **1,015/1,015** affected tests. Isolated probes passed **12,664 assertions**:
matching equivalence across 20 synthetic maps and 4,200 addresses, public
null-list/invalid-IP behavior, repeated cache failures and retry, parser-boundary
cancellation and concurrent per-call observer isolation. These are synthetic
behavioral checks, not claims about live forest size or production performance.

**490 evidence checks** reconciled the seven new-case regression groups and
separate two adapted D7 groups, exact failure unions, restoration and compiled
provenance, the D4 fixture boundary, all 17 anchors and the original ordered gate
with 4,251 passes / 3 known skips / 0 failures. Production DLL/PDB hashes match
the fixed originals. Test binary differences match the generated informational
version change from `999df700` to `91517a32`; exact test sources and fresh
execution were verified. Historical mutation evidence was reconstructed and
reconciled without replaying mutations or the full suite.

Receipt: `.agents/review/out/cdr-d5/reviewer-verdict.json`, with linked focused
TRX, compatibility/cache probes, reconciliation and shutdown records. The
reviewer left the exact reviewed checkout clean and its task/build servers
stopped. Browser/dev/live acceptance remains unrun. D5 closes the approved
original implementation/review sequence locally; all twenty originals and the
separately approved psl-1 are closed with their recorded dispositions.

# Review Packet

## Mode

IMPLEMENTATION_REVIEW - independent disposition recorded above.

## Original Request

Owner approved all twenty Revision 6 findings and sequential fresh Sol 6.1 max
implementation / independent Astra max review, then requested recovery and
continuation. D5 is the last original slice: prepare parsed subnets once per load
and cache the page's filtered rows, invalidating on result, applied Site and
applied Seen-by, with exact existing behavior preserved.

## Approved Plan

`docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, D5 and its
constraints; sequential scope exception in `.agents/decisions.md`.

## Commit Range

`999df700f4faf1435903ac76505b6f46e361c2f7..91517a32f49ae171c778fc9e6447b624de7d9858`.
One normal direct-master D5 commit, with records. No branch, merge, deletion or
history rewrite.

## Claimed Changes

- Parse raw subnets once per load; reuse their ordered parsed values for matching.
- Cache filtered rows with all three independent inputs; preserve the predicate.
- Keep the original deadline over both preparation and matching, completed data,
  unavailable escapes, SiteMapRead, notice, audit, CSV and busy ownership.
- Adapt affected guards without dropping their claims; prove new and adapted
  behavioral guards against fresh compiled production regressions.
- Update registry anchors/count, progress description and both version layers.

## Constraints / Out Of Scope

Optimization only. No changed filters, ordering, matching, authorization,
credentials, protected principals, locks, native worker admission/cleanup, replay,
configuration, dependency or privilege policy. No hooks/config/trust override,
extra agents, self-review, push/deploy/live probes or unrelated remediation.

## Design Decisions

- A per-load prepared list is local to the caller; no global map/cache lifetime or
  singleton mutable diagnostic is introduced.
- Preparation and matching are separate internal pure helper stages so the shared
  budget covers both and preserved matching guards can control enumeration.
- Cache keys use the raw applied strings and result reference, with publication
  after computation; form edits alone remain inert until Search.
- No timing/forest-size claim is made. Parser entries and reused list identities
  provide deterministic evidence of removed work.

## Deferred Work

The approved implementation/review sequence is locally closed by D5's approval.
Manual dev group-pin and Defender opening checks remain unrun. Physical AD/SeenBy
acceptance is still unproven. psl-2/3/4, cdr-followup-1, continuation-host checks,
initialization-only authorization and broad D6 stay deferred/unapproved. Known
module spec/guide drift is not repaired by this slice.

## Review Focus

Review intent alignment and the complete affected load/filter/export lifecycle.
Check parse lifetime, all cache inputs, unavailable versus known negatives,
matching order/ties, the shared preparation/matching deadline and native-worker
ownership. Verify D1/D3/D4/D7/D9 behavior, adapted guard coverage, exact failure
unions, restoration/provenance and ordered final gates. No self-review or
independent approval is implied by coder guard proof.

## Verification Run

Canonical baseline 43/43; seven compiled production regression groups / twelve
intended standard assertion failures / exact twelve-new-case union. Two separate
adapted D7 production regressions / two intended failures / exact two-existing-case
union. Exact saved-byte/MD5/SHA256/newer-mtime restoration and fresh app/runtime/test
DLL/PDB provenance pass. The D4 test-only fixture boundary is explicitly recorded.
Restored affected coverage passes 1,015/1,015. Ordered final Release build passes
with 40 warnings / 0 errors, full solution format passes before the single full
Release suite (4,251 passed / 3 known AD skips / 0 failed; 4,254 unique executions;
numeric child exit 0; wall 305.70 seconds), followed by staged diff/ASCII checks.
All 17 anchors map exactly, zero twins/unmapped, page count 1,295. Receipt
reconciliation binds exact fixed sources, accepted proof scope and final compiled
artifacts; task/build servers stop before handoff. No code/test bytes change after
the accepted gate. Browser/live/deployment acceptance is unrun. No self-review,
independent approval, push, deployment, live probe or adjacent work is claimed.
