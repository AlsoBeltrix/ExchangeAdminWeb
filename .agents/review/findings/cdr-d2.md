# cdr-d2 - isolate Defender hunting diagnostics per call

Status: Implemented and locally verified on master; independent Astra review pending, 2026-10-16.

Approved scope: `docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6,
D2, with dependency guidance in `.agents/review/cdr-d7-design.md`. Owner
2026-10-15: "approved. go." Recovery 2026-10-16: "system restarted. please recover
and continue." Starts on clean master
`eb0d19122f4921eca66e4300efe0605403214e23`. Both live origin and github master
refs checked with `git ls-remote` remain
`885397981d7550afebf6518ef21b4fc54b062859`.

## Evidence and declared scope (before code changes)

D2 is confirmed at the actual await boundary. Program.cs:210 registers the
service as a singleton. In DefenderEndpointDeviceService.cs:885-888,
GetDiscoveryEnrichmentAsync awaits a Boolean TryEnrichBatchAsync result, then
reads `_lastBatchFailureReason` into its local accumulator. The helper writes
that instance field at :964 (filtered send exception), :970 (no document/status
failure) and :978 (unreadable results collection). Another run can complete its
helper and overwrite the reason before the first run's awaiting aggregation
continuation executes. The shared field declaration is at :934. The observer
helper at :998-1035 independently returns only a Boolean and loses its reason.

Replace both helpers' Boolean outcome with an immutable local
HuntingBatchResult(Succeeded, Reason). Read the posture reason from that awaited
result; keep all counts, sets and dictionaries local to the run. Observer
outcomes carry their own safe reason, while existing public reporting continues
to expose observer failure counts/cells and the posture reason. D7 will own any
additional stop/deadline diagnostic contract. Retain singleton registration and
all dependency/lifetime behavior. Successful empty batches stay successful;
partial counts, unavailable cells, merged posture/observer data, inventory,
paging, filters and exports keep their existing contracts. No page edits expected.

Class-field sweep evidence: the complete declaration scan shows nine readonly
instance fields at :29-45: ModuleConfigService, DelineaService, IHttpClientFactory,
two client factories, AdSiteDirectory and the three test overrides. Assignments
are confined to the production/test constructors (:251-298). The only other
instance storage is the init-only HuntingBatchSizeOverride at :98 and the mutable
reason at :934. The constants have no per-call state. EffectiveHuntingBatchSize,
IncludeDiscoverySources and other computed properties derive from that fixed
configuration/dependencies. Inventory byId/proven/pending, enrichment rows and
unavailable sets, counters and reasons are method locals. PartFetch records are
created per part. No second per-run mutable diagnostic field was found; the
readonly dependency references retain their own existing behavior, outside D2.

Add deterministic overlapping real ListDevicesAsync calls through canned HTTP
clients. A controlled synchronization context will pause after a batch helper
has completed and before its caller consumes that result, then let the competing
run complete a different failure. Exercise filtered send, no-document/status and
malformed-collection posture paths, whole versus partial aggregation, independent
observer failures, successful-empty batches and retained row/CSV coverage.
Mutation-prove every new runtime case with freshly compiled production regressions,
complete logs/TRX, exact case-name union, source/app/runtime/test/PDB hashes and
mtimes. Restore saved exact production bytes, MD5/SHA256 and advance source mtimes;
no stale --no-build result counts as proof. Run restored affected coverage and one
final ordered build/format/full-suite/diff/staged-ASCII gate.

Defender advances 1.6.10 -> 1.6.11 with its pinned ModuleCatalogTests expectation.
Base remains 2.27.4 and Comms remains 1.4.18. Known AdminModuleSpec Version 2.0 /
June 25 drift stays flagged. D7 bounds/cancellation/systemic stop, D1 first render,
D3 reentry and D5 caching/subnet work are separate. D6-local comments only in
these touched files. psl-2/3/4, cdr-followup-1 and continuation-host validation
remain deferred/unapproved. No protection/locking, authorization, credential,
dependency, config/runtime database, shipped PowerShell, live tenant/directory,
push/deploy, branch/history/gitconfig/hooks/trust changes, agents or self-review.
Commit this verified one-finding slice normally, stop its build/test processes,
then hand off for independent Astra max workflow review before the next slice.

## Implementation

The private immutable HuntingBatchResult carries Succeeded and the safe Reason
from every posture and observer path: filtered send exceptions, absent document /
HTTP failure, malformed results, and successful (including empty) results.
GetDiscoveryEnrichmentAsync consumes its awaited posture result directly. Counts,
last posture failure, unavailable sets and merged rows remain local to that call.
The observer outcome independently controls its existing count and unavailable
cells. Its reason is local return data for D7; D2 preserves the current public
contract (posture Reason, observer count/cells), including empty Reason on a
Succeeded posture run. No shared diagnostic field or synchronization is introduced.

The field sweep's complete declaration/assignment receipt is
`.agents/review/out/cdr-d2/field-sweep.json`, generated from the saved original and
fixed source bytes by `field_sweep.py`. It records all ten original instance
fields, the reason's initializer and three writes, the nine surviving readonly
fields and each constructor assignment, the init-only configuration property,
computed configuration and the actual per-run collections/counters. All surviving
field assignments are constructor-only. This does not make a claim about the
internal synchronization of dependency objects, which D2 does not change.

Defender is 1.6.11, including its pinned catalog test. Base remains 2.27.4 and
Comms remains 1.4.18. DI registration, constructors, client factories and public
models are unchanged. No page or registry edit occurred. The touched helper's
"shared dictionary" comment now explicitly identifies the caller's per-run
collection; it is shared among that run's batches, never between runs.

## Compiled coverage and production regression proof

`ExchangeAdminWeb.Tests/DefenderConcurrentEnrichmentTests.cs` runs two actual
ListDevicesAsync calls on the same service instance through real DefenderApiClient
HTTP paths with canned inventory/token/hunting responses. Its controlled
SynchronizationContext queues actual async continuations with a fresh context
identity for each dispatch, preventing inline resumption from closing the gap.
It pauses the named GetDiscoveryEnrichmentAsync continuation after the first
helper has returned. The competing run completes a different Forbidden posture
failure, then pauses with its independent observer failure pending. Only then is
the original call resumed. No production hook, private field inspection,
timing-dependent sleep, live credential, network, directory or config database is
used. The name identifies the caller's aggregation boundary; no helper is invoked
directly by a test.

Six cases exercise no-document/status (429), unreadable results collection and
filtered send exception, each through whole-run and partial aggregation. They
require the first call's own distinct reason, the competing run's Forbidden
reason, exact failure/batch/observer counts, retained inventory/completion metadata,
request counts and device membership. Successful empty posture/observer responses
stay blank; failed rows render unavailable on screen/export, and an observer
failure retains all five posture fields with only that cell unavailable.

The first setup run caught fixture DNS sorting: ids named "empty" sorted before
"failed", so the intended continuation script reached a different batch. Fixture
DNS names now retain input order under the unchanged service sort. No production
change was needed; setup logs/TRX are retained and do not count as proof.
A scoped formatter ran before locking bytes. The separately compiled final proof
baseline and restored fixed run both pass 6/6.

Authoritative ignored receipts are under `.agents/review/out/cdr-d2/`.
`proof.py` freshly compiles the solution for each named probe (no --no-build),
scans complete logs for CS/RZ/MSB compiler errors, reads exact TRX case names and
requires intended standard Assert.Equal failures. The source, app DLL/PDB,
test-runtime app DLL/PDB and test DLL/PDB each have MD5/SHA256, size and mtime
provenance. The changed app differs from the fixed baseline and matches the
runtime copy byte for byte, including PDBs; fresh app/test artifacts are newer
than their touched source. Every probe restores saved exact source bytes,
verifies MD5/SHA256 and advances the source mtime beyond the mutated compilation.
Final test bytes remain unchanged throughout.

Locked source:

- Service MD5 `1ac322f67e5f83a0bc0a5319c5a7693e`, SHA256
  `09e19464868ca7a509f7dec96a78914e29db2a6d699cc0daffac0c9fc14df1a7`.
- New test MD5 `03c7eded6f9ffeb8aeb60d7587bbf2d9`, SHA256
  `e382bbe728b0ee100f8954f4cfc842938193205195854ff3df022664269a7213`.

| Freshly compiled production regression | Failed / passed | Intended assertion |
|---|---|---|
| Restore the original shared-field service from the starting commit | 6 / 0 | First call's reason equals the competing Forbidden reason rather than its own |
| Treat all three observer failure paths as successful | 6 / 0 | Expected one observer failure, got zero |
| Treat a successful empty posture collection as failed | 6 / 0 | Empty batch overwrites the correct last failure reason and alters availability |

Eighteen intended assertion failures have an exact six-case union. All names
start `ExchangeAdminWeb.Tests.DefenderConcurrentEnrichmentTests.`:

```text
OverlappingListingsKeepTheirOwnFailuresAndAvailableCells(failure: "malformed", partial: False)
OverlappingListingsKeepTheirOwnFailuresAndAvailableCells(failure: "malformed", partial: True)
OverlappingListingsKeepTheirOwnFailuresAndAvailableCells(failure: "send", partial: False)
OverlappingListingsKeepTheirOwnFailuresAndAvailableCells(failure: "send", partial: True)
OverlappingListingsKeepTheirOwnFailuresAndAvailableCells(failure: "status", partial: False)
OverlappingListingsKeepTheirOwnFailuresAndAvailableCells(failure: "status", partial: True)
```

`proof-summary.json` owns that exact union; each `<probe>-run.json`,
`<probe>-evidence.json`, complete `<probe>.log` and `<probe>/<probe>.trx` records
its actual results and artifact provenance. The first original-service probe
already produced all six intended failures, but its receipt checker expected
"Forbidden" beyond xUnit's truncated display. That attempt and checker remain in
`initial-checker-original/` and are not counted. The corrected checker matches the
actual displayed "Microsoft Graph refused..." string; a separate fresh original
compilation produced the authoritative accepted receipt. This changed only the
ignored checker. All authoritative receipts verify exact restored bytes/hashes,
unchanged test bytes and newer restoration mtimes.

## Closeout

Restored affected coverage passes 743/743. The single ordered final gate passes
as recorded in the review packet below. Independent Astra max workflow review
remains the next gate after the normal one-finding master commit.

# Review Packet

## Mode

IMPLEMENTATION_REVIEW

## Original Request

Owner approved all twenty Comms10kDefenderRemediation Revision 6 findings on
2026-10-15 ("approved. go.") with sequential fresh Sol 6.1 max implementation and
independent Astra max workflow reviews. On 2026-10-16 the owner said "system
restarted. please recover and continue." This slice implements only D2: remove
shared singleton hunting diagnostics, return per-call batch outcomes and prove
concurrent ListDevicesAsync reason isolation after sweeping this class for the
same state shape. D2 precedes D7 by the recorded dependency design.

## Approved Plan

`docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, D2.
Dependency/per-call guidance: `.agents/review/cdr-d7-design.md`.
Implementation/review authority: `.agents/decisions.md`, 2026-10-15.

## Commit Range

`eb0d19122f4921eca66e4300efe0605403214e23..HEAD` (the single D2 code commit).

## Claimed Changes

- Both private hunting helpers return immutable Succeeded/Reason outcomes;
  posture aggregation reads that awaited result instead of a shared field.
- Singleton/dependency lifetimes, public inventory/enrichment models, posture
  reason policy, observer failure counts and unavailable cells remain intact.
- Deterministic actual overlapping listing calls cover three failure paths,
  whole/partial aggregation, independent observer failures and empty-success
  row/export semantics. Every new case has compiled production regression proof.
- Defender 1.6.11 and the pinned catalog expectation, with required records.

## Constraints / Out Of Scope

No protection/locking, authorization, credential, dependency, config/runtime DB,
shipped PowerShell, live tenant/directory call, browser acceptance, push/deploy,
branch/history/gitconfig/hooks/trust change, agents or self-review. No page edit
or registry reanchor was needed. Preserve inventory completion/paging/export,
all five posture fields and observer isolation. No Comms-10k behavior change or
alteration of its recorded exemption. Known module-spec Version 2.0 / June 25
drift against base 2.27.4 remains flagged.

## Design Decisions

- The batch result is private and immutable; dictionaries remain per-run mutable
  collections shared only by that run's batches. No global serialization hides
  the race or changes singleton behavior.
- Observer failure reasons travel on the same local result shape, ready for D7.
  D2 preserves the public contract: a succeeded posture run still has empty
  Reason, while observer failure has its existing count and per-cell marker.
- Empty results are successful. A returned false status, filtered exception or
  malformed collection carries the existing safe reason; unexpected exceptions
  retain existing propagation. No raw exception text is exposed.
- A fresh test synchronization-context identity per dispatch prevents inline
  continuation execution, so another real listing can run in the precise
  helper-completed / aggregation-not-resumed gap. Matching the aggregation
  state-machine name is confined to the fixture; production has no test hook.
- Existing whole-failure FailedBatches=0 and TotalBatches count semantics remain;
  this slice does not change public count definitions or D7's planned bounds.

## Deferred Work

D7 shared deadline, systemic stop and cancellation propagation; D1 deliberate
first Search and empty state; D3 page reentry; D5 subnet/filter caching. The
codebase-wide D6 comment audit is separate. psl-2/3/4, cdr-followup-1,
continuation-host validation, page-initialization authorization and physical
location acceptance remain recorded/unapproved. No live directory/tenant probe
is authorized here. D7 does not inherit a claim that bounds already exist.

## Review Focus

Independently judge intent/scope, then trace the full singleton call lifecycle:
client construction, complete inventory, posture and observer batching, exception
and malformed/empty outcomes, local aggregation, unavailable-row merge and CSV
projection. Check concurrent calls retain distinct reasons on all three actual
posture failure paths and that observer failure cannot blank successful posture
or turn empty-success data unavailable. Verify the class-field sweep against
code, exact case-name union, intended assertion messages, source restoration,
app/runtime/test/PDB provenance and ordered final gate. Previous review verdicts
are not premises. Deadline/stop/token behavior, page reentry and performance are
separate approved slices and not claimed fixed here.

## Verification Run

The proof section above owns the six-case exact union, 18 intended assertion
failures and source/hash/mtime/app/runtime/test/PDB evidence. The final restored
new cases pass 6/6 and affected coverage passes 743/743. That selector covers all
Defender, AdSite, ModuleCatalogTests, ClickGate and Progress cases. The prior
slice used a broader ModuleCatalog name substring that also selected one
unrelated EmergencyDisable catalog case; it is covered by the full suite.
The final Release build passes with 40 existing warnings / 0 errors and the
full solution format check passes before the single full suite.
The final ordered gate passes:

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 40 existing warnings, 0 errors.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: pass,
  before the single full-suite run.
- `dotnet test ExchangeAdminWeb.slnx -c Release -- xUnit.MaxParallelThreads=4`:
  4,190 passed, 3 known AD-unavailable skips, 0 failures. The complete log and
  actual named TRX are `final-full-test.log` and `final-full/final-full.trx`.
- `git diff --check HEAD` and `tools/Test-AsciiOnly.ps1`: staged checks recorded
  below before the normal commit, including both new files.

Exact commands/times/results, complete logs and app/runtime/test/PDB provenance
are `final-build.json`, `final-format.json`, `final-full-test.json`,
`final-diff.json` and `final-ascii.json`, with matching `.log` files.
`closeout-reconciliation.json` requires unchanged locked source bytes, exact proof
union/restoration, field sweep, focused/full counts and sequential final gate
order before commit. The adapter's raw TRX notExecuted counter is zero; its three
NotExecuted case rows, 4,193 total minus 4,190 executed, and the complete log all
independently establish the three known skips. The receipt preserves raw counters.

The scoped formatter ran before locking proof bytes; final format verifies them
without changes. No self-review ran. PSScriptAnalyzer/Pester did not run because
no shipped PowerShell changed; proof tooling is ignored and machine-local. No
live AD/Defender/Graph, visual browser/circuit, deployment or manual workflow
acceptance was run. The compiled HTTP fixtures establish service/export behavior,
not the deferred physical-location workflow. The established token meter ran
against the committed baseline (`token-usage.json`); its Claude-only scope excludes
this native Sol session, so no per-slice native usage/cost is attributed.
Fresh independent Astra max workflow review remains owed after the code commit.
