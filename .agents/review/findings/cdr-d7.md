# cdr-d7 - bound Defender enrichment after inventory

Status: Independent Astra max CHANGES REQUIRED for `421588e`; its one P2 is
corrected and closed on coder-side guard proof, 2026-10-16. The original verdict
is preserved; no second review or independent approval is claimed.

## Independent review and P2 correction scope (before coding)

Fresh native Astra max reviewed `134b22d..421588eb44b6d20e11e1e37f196bac53ec14e160`
and returned **CHANGES REQUIRED**, exactly one **P2 / MEDIUM**: "Honor the deadline
during site matching after the directory read." It found no additional actionable
defects in the reviewed workflow. Original verdict and receipts are preserved at
`.agents/review/out/cdr-d7/reviewer-verdict.json`, `reviewer-reconciliation.json`,
`reviewer-probe/Program.cs` and `reviewer-probe.log`.

The confirmed mechanism is ApplySitesAsync's unguarded continuation and device /
subnet loops after ReadSubnetsAsync returns. The deterministic reviewer probe
paused the captured UI continuation, fired the deadline, resumed it and observed
all 5,000 devices mapped across 1,000 synthetic subnets after cancellation, with
SiteMapRead true and DeadlineReached. Its extra 580 ms is an observation, never a
timing assertion or a production-forest assumption. Independent affected coverage
passed 988/988; 865 checks reconciled the original fifteen groups / 63 canonical
failures / exact forty-case union and provenance. Twenty-one independent probe
checks covered native blocked Open, retained admission, eventual disposal, no
later commands and transient-token recovery. Review source reference:
https://github.com/PowerShell/PowerShell/blob/v7.6.1/src/System.Management.Automation/engine/hostifaces/PowerShell.cs

Correction starts on clean master `421588e`; both live remote master refs remain
`eb0d19122f4921eca66e4300efe0605403214e23`. Enforce the same token before mapping,
between devices and within each device's subnet scan; preserve completed Site /
hunting data and inventory while unevaluated Site cells remain unavailable.
Keep row mutations on the awaiting response path; the directory worker still
returns data only. Update incomplete-site wording and its existing per-row filter /
audit / CSV evidence. No D5 parsing/cache optimization, deadline reset or adjacent
scope. Shared matching changes require base 2.27.5 -> 2.27.6 and Defender
1.6.12 -> 1.6.13, including all version properties and catalog pin.

Add minimal deterministic continuation / enumeration guards, mutation-prove every
new case with fresh production compilation and standard assertions, preserve exact
bytes / MD5 / SHA256 / newer-mtime and runtime/test/PDB provenance, then restored
affected coverage and one ordered correction gate. Distinct correction receipts:
`.agents/review/out/cdr-d7/p2-correction/`. Close this noncritical finding on
coder-side proof under repo policy; preserve the original CHANGES REQUIRED verdict
and do not commission a second review. No self-review, further agents, live calls,
push/deploy, dependency/config/policy/hook/history changes are authorized.

## P2 correction implementation and controlled guards

The response path now establishes unavailable Site markers, checks the original
token before matching, between devices, within each subnet scan and before
publishing each completed match. Cancellation preserves already completed Site
values; the current and remaining devices stay unavailable. SiteMapRead is false
when any evaluation is incomplete. The native directory worker still returns
subnet data only. Existing synchronous FindSite callers retain their public
two-argument behavior; an internal cancellable entry point serves Defender.
No CIDR parse/cache optimization was introduced.

Three deterministic compiled guards pass initially. A held directory worker and
paused SynchronizationContext reproduce the review's deadline-before-continuation
case through actual ListDevicesAsync. Controlled enumeration fires the same fake
budget within a later device's subnet scan and at the final MoveNext before
publication. The guards retain complete inventory/posture and check exact per-row
Site markers. The actual page's applied London filter, filter-audit builder and
CSV projection retain unknowns and exclude known negatives: unavailable evaluation
is 0 evaluated / 3 unavailable, partial evaluation is 1 / 2, and returned/exported
counts are 3 and 2 respectively. Compiled rendering checks truthful partial-site
wording. No elapsed-time race or long real wait determines an assertion.

Touched files were formatted before proof lock. The initial setup run passed
3/3, but exposed xUnit1051 warnings on existing two-argument subnet callers.
Keeping the cancellable path internal and separately named preserves that public
contract without editing unrelated callers or suppressing diagnostics. Setup
receipts are not counted as regression proof. The page's exact HEAD/current equal
opcode map confirms all 17 anchors stay fixed, no twin anchors, and line count
1293; ProgressRegistry now names matching within the same reported operation.
Correction proof/gate receipts remain separate from the accepted original D7
and independent-review receipts.

### Accepted P2 regression proof

The formatted baseline passes all three new cases. Six freshly compiled
production regressions produce eight intended standard assertion failures:

| Production regression | Exact failed cases | Failure |
| --- | --- | --- |
| Reviewed original unchecked matching | All three new cases | Assert.False |
| Missing per-subnet cancellation check | WithinOneSubnetScan | Assert.Equal (4 visits instead of 3) |
| Missing final scan / publication checks | AtEndOfSubnetEnumeration | Assert.False |
| Missing unevaluated Site markers | BeforeSiteContinuationResumes | Assert.All / Assert.Equal |
| Discarding completed Site values on cancellation | WithinOneSubnetScan | Assert.Equal (Elsewhere lost) |
| Original all-sites-unavailable banner | WithinOneSubnetScan | Assert.Contains (compiled rendering) |

`proof-summary.json` records the exact three-name union. Every accepted run
includes complete console output/TRX, no compiler errors, exact saved-byte /
MD5 / SHA256 restoration with source mtimes advanced beyond mutated mtimes, and
fresh production/runtime/test DLL/PDB provenance. Runtime app/PDB copies equal
the freshly built app/PDB, and every mutation changes the production assembly.
The original four independent-review receipt hashes remain unchanged. The
initial setup pass is excluded from regression counts; no compiler/fixture failure
is counted and no original D7 mutation group is replayed. Distinct receipts:
`p2-correction/proof.py`, `proof.json`, `saved/`, six `*-evidence.json`, complete
`*.log` / per-run TRX directories, `anchor-map.json` and `proof-summary.json`.

### P2 correction verification and closure

Restored affected coverage passes 991/991, including all three correction cases,
original D7 and D2 overlap cases, shared Delinea/directory/API/CloudSignIn and
section-access callers, full ClickGate and Progress groups and catalog pin.
One final ordered correction gate passes: Release solution build (40 warnings /
0 errors), full solution format verification, then the single full Release suite
with xUnit.MaxParallelThreads=4 (4,227 passed / 3 known AD-unavailable skips /
0 failed; 4,230 total; 5 minutes 11 seconds), followed by diff/staged-ASCII guards.
Complete console output/TRX and ordered start/end receipts are retained; no
compiler failure counts as assertion proof. No production/test bytes changed
after restored affected verification. The initial setup pass and formatting
preparation are excluded from canonical proof/gate counts.

Final correction receipts are `final-*.json` / `.log`, `final-full/`,
`closeout.json` (source / artifact / exact-union / ordered-gate reconciliation),
`token-usage.json`, and `process-shutdown.*` under the isolated correction folder.
The committed-baseline Claude-only meter ran; no native session usage or cost
is attributed. No shipped PowerShell changed. The original independent review
verdict and its receipts retain their exact hashes.

This closes the single noncritical P2 on coder-side guard proof under repo policy.
It does not replace Astra's original CHANGES REQUIRED verdict with an approval.
No second independent review is required or commissioned. D7 is locally closed;
the next approved slice is D1, then D3 and D5. No push or deployment occurred.
Cancellation remains cooperative: a finite per-CIDR parse/match and the cheap
finite-inventory marker pass can finish between checks. Native cleanup may
continue after response return while retaining its one admission slot; the
worker never mutates rows. No physical hard-stop or live AD/tenant/vault/browser
acceptance is claimed.

## Evidence and declared scope (before original code changes)

Owner approved all Revision 6 findings on 2026-10-15 ("approved. go.") and
requested recovery/continuation on 2026-10-16. Approved scope is D7 in
`docs/Comms10kDefenderRemediation-Plan.md`; canonical implementation guidance is
`.agents/review/cdr-d7-design.md`, read in full. Start: clean master
`134b22df84956503ed408d4cbdf6118138f972b8`. Live origin and github master both
verified with `git ls-remote` at `eb0d19122f4921eca66e4300efe0605403214e23`.

Current ListDevicesAsync finishes and validates inventory before awaiting hunting
setup, posture/observer batches and synchronous ApplySites. The factory and
BuildClientAsync call Delinea without cancellation. Delinea's token-lock, auth
retry, secret retry and body reads do not accept cancellation; its blanket catch
would swallow one. DefenderApiClient also lacks cancellation and only identifies
its independent HTTP timeout. It is shared with CloudSignInService. Hunting
continues after 401/403 and counts planned batches as attempted; Succeeded clears
the reason even if the final observer fails. ApplySites calls synchronous
AdSiteDirectory.ReadSubnets, whose PowerShell commands lazily Open and Invoke a
runspace. Section-access callers use the same synchronous commands interface.

Implement one centrally named four-minute deadline beginning after complete
inventory and before hunting setup. Pass its token through actual credential,
token and API I/O. Keep immutable per-call outcomes; stop known credential/auth
setup failures and 401/403 in either hunting pass, retaining successful posture
and empty responses. Keep observer and stop diagnostics separate. Report actual
attempted, failed and unattempted batches and mark skipped rows unavailable.

Add an opt-in asynchronous site read. A single admitted worker per directory
instance owns commands/runspace through eventual cleanup. Cancellation stops the
response wait and requests cooperative nonblocking stop, prevents further
commands, and never awaits cleanup or disposes native work on the response path.
Admission remains occupied until cleanup completes. Worker returns data only.
The bound is response wait/cooperative cancellation, not physical native cutoff.
Preserve synchronous section-access behavior, D2 concurrency isolation, D4 applied
result auditing, unavailable filters/CSV and CloudSignIn callers.

Use deterministic fake time and controlled continuations for deadline, I/O,
systemic/isolated failures and stalled site Open/Invoke/cleanup. Prove every new
case with freshly compiled production regressions, exact case union, full logs/
TRX, app/runtime/test/PDB provenance and saved-byte MD5/SHA256/newer-mtime restores.
Re-anchor page registries with exact old/new diff mapping including twin anchors.
Run restored affected coverage and the ordered build/format/full-suite/diff/
staged-ASCII gate. Bump Defender 1.6.11 -> 1.6.12 and shared base 2.27.4 -> 2.27.5;
Comms remains 1.4.18. Known AdminModuleSpec Version 2.0 / June 25 drift stays flagged.

D1, D3, D5, psl-2/3/4, cdr-followup-1 and continuation-host validation are separate
and deferred. No protection, locking/auth policy, config DB, dependencies, shipped
PowerShell, live AD/tenant/vault, push/deploy, branch/history/gitconfig/hooks/trust,
self-review or further agents. One normal master commit, then fresh independent
Astra max workflow review coordinated by the parent.

## Implementation

ListDevicesAsync starts its one TimeProvider-backed CancellationTokenSource after
inventory completeness and client-side service filters, before hunting setup.
DefaultEnrichmentDeadline is the centrally named four-minute default. Its token
reaches the factory/BuildClient and actual Delinea token-lock, OAuth, secret,
forced refresh/retry and body I/O, plus DefenderApiClient's token-lock, OAuth,
API and body I/O. Requested cancellation escapes blanket catches; an independent
HTTP timeout retains the existing TimedOut result. Optional tokens preserve
unaffected callers, including Delinea's credential-reader sibling.

Known setup failures stop without a hunting request. Typed token/configuration
failures and hunting 401/403 stop futile work from posture or observer. Token
400/401/403 stop; isolated token 429/5xx and query 429/5xx/malformed/transport
failures retain existing batch isolation. Arbitrary exception text is never used
for classification. Observer 400 remains isolated. Counts now distinguish
planned, attempted, failed and unattempted requests; unsafe ids are unavailable
without inventing a failed batch. Observer-unattempted includes observers skipped
after posture failure. Separate observer and stop reasons survive a successful
posture state, including a deadline on the final observer. Per-call immutable
outcomes and local collections preserve D2 isolation and successful-empty data.

The opt-in site path admits one worker per AdSiteDirectory instance. The worker
owns creation, Open/Invoke and eventual disposal; only subnet data crosses back.
Canceled response waits request cooperative stop and return without waiting for
stop, disposal or cleanup. PowerShell's asynchronous invocation admission shares
a short lock with stop admission, and StopAsync initiation runs off the response
path. No CloseAsync while Opening or synchronous Stop is used. Cleanup observes
stop completion and retains the admission slot even when native shutdown stalls.
Synchronous section-access callers keep synchronous Invoke/Open behavior. Internal
bootstrap-reader, runspace-factory, TimeProvider and TaskScheduler seams allow
controlled synthetic evidence without live vault/AD calls or timing races.

CloudSignInService maps the internal typed authentication failure back to its
existing safe InvalidOperationException label; its result/error contract remains.
The existing D2 overlap fixture now uses an isolated 503 instead of a systemic
403 and retains all six deterministic reason/cell/CSV assertions. D4's isolated
observer fixture uses a 400 rather than 403; its eleven audit scenarios and
assertions remain. These adjustments preserve their original independent-failure
questions under the new approved stop semantics.

Defender is 1.6.12 and shared base is 2.27.5 (all three version properties).
Comms is 1.4.18. ClickGate anchors were mapped from committed old/new page equal
opcodes, including examination of every twin field (none in this page entry).
ExpectedLineCount is 1,293; unchanged form anchors stay, export/pager move
392->400 / 470->478 / 472->480. ProgressRegistry retains the same handler and
slow-call pins and describes the bounded service tail. Exact mapping receipt:
`.agents/review/out/cdr-d7/page-anchor-map.json`.

## Setup and verification history

The initial compile caught an Invoke/EndInvoke result collection type mismatch;
both implement IEnumerable and the declaration was corrected. The first fixture
setup incorrectly returned inventory JSON to the inventory token request; it was
corrected and that interrupted setup is excluded from guard proof. A first
affected run exposed the shared CloudSignIn label and the isolated 403 fixtures
described above, plus the existing observer-banner source pin. The pin is retained
as an explicit failed-or-unattempted condition. Corrected affected coverage passes
988/988, including complete Defender, AdSite, CloudSignIn, SectionAccess, catalog,
ClickGate and Progress selections. No full-suite baseline ran.

Touched-file formatting finished before proof bytes were locked. The proof
baseline passes 40/40: 34 new cases plus the six preserved D2 overlap cases.
Fresh compiled regression probes and restoration evidence are retained under
`.agents/review/out/cdr-d7/`; these receipts are ignored operational artifacts.

The first fourteen groups were accepted: 58 intended standard Assert failures,
with an exact 40-case union, including all 34 new cases and the six D2 cases.
Their original complete receipts are preserved at
`.agents/review/out/cdr-d7-pre-wording-proof/`. Before final gates, the observer
banner's old "every other column is unaffected" sentence was corrected: systemic
observer failure now skips later posture batches. The final text says completed
posture details are retained, with an added assertion in the same two page cases.
This changes one page literal and one assertion line only; all other production
and test bytes are reconciled exactly. Parent explicitly directed reproof of the
changed page cases and the explicit per-batch timer-reset gap, preserving the
unaffected accepted receipts rather than repeating them for aggregate DLL hashes.
Final proof/affected/gate outcomes follow below.

## Compiled guard proof and provenance

Final fresh baseline: 40/40. Three new test files contain 34 runtime cases:
five shared-stage budget cases, four systemic posture/observer cases, three token
auth failures, four isolated failure cases, one inter-batch deadline case, two
compiled-page cases, six production vault setup/retry/body cases, three API I/O
cases, two canceled-lock-waiter cases, one independent HTTP timeout, and three
native site lifecycle cases. The six existing D2 overlap cases are retained.
Fake TimeProvider timers and controlled gates supply the deadline; no case waits
four real minutes. Twenty-second watchdogs only guard broken fixture/native
setup; no accepted mutation failure is a watchdog or timing assertion. Local
synthetic PowerShell functions exercise Open/Invoke without AD/RSAT/live calls.

| Production regression | Intended standard Assert failures |
|---|---:|
| Four-minute deadline extended | 8 |
| Budget restarted per batch | 5 |
| Factory-to-Delinea token dropped | 6 |
| Delinea I/O/lock tokens dropped | 7 |
| API I/O/lock tokens dropped | 4 |
| Independent HTTP timeout result lost | 1 |
| Posture/observer systemic stop removed | 7 |
| Isolated failures treated as systemic | 4 |
| Skipped-row unavailable marking removed | 6 |
| Final observer reason dropped | 1 |
| Compiled page failed/skipped counts lost | 2 |
| Site worker admission bypassed | 3 |
| Site stop request removed | 2 |
| Site command cancellation guards removed | 1 |
| Shared posture reason race reintroduced | 6 |

Canonical total: fifteen groups, 63 intended assertion failures; exact failed-name
union is all 40 baseline cases, including every new case. Initial accepted proof
had fourteen groups / 58 failures; the final page reproof repeats two failures
and the new timer-reset probe adds five. There are 65 physical observations with
the duplicate two page observations excluded from the canonical 63. The initial
page literal/assertion bytes are not presented as final bytes. See
`wording-reconciliation.json` for exact two-path edits and the seventeen unchanged
source/test paths, and the preserved sibling archive for original receipts.

Each accepted run uses dotnet test on the solution with compilation enabled,
touches test source to force fresh test/PDB output, retains complete console/TRX,
checks all console output for compiler errors, compares built app/runtime DLL and
PDB bytes, records source/test/app/runtime hashes and mtimes, and requires fresh
production output after the mutated source. Saved exact bytes, MD5/SHA256 and
newer restored mtimes are checked in finally. Every observed failure is a standard
Assert failure with the exact intended case set; compilation and fixture failures
are excluded. No --no-build pass is counted. No full-suite baseline or self-review
ran. The final restored affected run passes 988/988 and includes every proof case,
all ClickGate/Progress tests and the shared directory/API/CloudSignIn callers.

Canonical ignored receipts at `.agents/review/out/cdr-d7/`:

- `proof.py`, `proof.json`, `proof-summary.json`, `saved/` and each probe's
  complete `.log`, TRX, `-run.json` and `-evidence.json`.
- `wording-reconciliation.json`, `page-anchor-map.json` and `restored-affected/`
  with `restored-affected-run.json`.
- `gates.py`, ordered `final-*.json` / `.log`, `final-full/` and
  `token-usage.json`. The committed-baseline meter ran successfully; its Claude
  transcript scope excludes this native Sol session, so no usage/cost is invented.

## Test-run token and gate correction

The first ordered Release build passed with 40 warnings / 0 errors. The full
format gate then stopped (exit 2) on xUnit1051 diagnostics, before any full-suite
run: the new optional token parameters made sixteen existing API test calls and
seven new watchdog/directory calls require TestContext.Current.CancellationToken.
No guard or analyzer was bypassed. Those test-only arguments were added; production,
case names, assertions and fake deadline tokens did not change. Exact normalization
back to the accepted bytes is in `test-token-reconciliation.json`. Original
receipts are preserved in `.agents/review/out/cdr-d7-pre-test-token-proof/` and
`final-build-attempt1.*` / `final-format-attempt1.*`. The fresh final-byte baseline
passes 40/40 and restored affected coverage passes 988/988. The ordered gate is
restarted after that actual test-source correction; no full suite had run before it.

## Operational limits

The deadline bounds the enrichment response wait and requests cooperative I/O /
PowerShell cancellation. It does not assert a physical native-operation hard stop.
A stalled native worker keeps its one admission slot until eventual cleanup; later
searches can consequently return Site unavailable at their own deadline without
creating more workers. No stop/cleanup wait or native disposal occurs on the
canceled response path. Completed posture and inventory remain available, with
unknown values marked; successful empty results remain empty. The existing
unavailable-row filter escape, applied-result audits and CSV contracts are covered.

No live AD, tenant, vault, browser/dev workflow or deployment was run. Physical
location / SeenBy schema acceptance remains unproven and requires separate owner
authority. D1, D3 and D5 remain after this slice; the other deferred items named
above remain unapproved. Independent Astra workflow review is the next gate.

# Original implementation review packet (historical)

This packet records the `421588e` handoff. Its pending-review wording is historical:
the preserved independent result is CHANGES REQUIRED with the one P2 above.

## Mode

IMPLEMENTATION_REVIEW

## Original Request

Owner approved all twenty Revision 6 Comms-10k/Defender findings ("approved. go.")
and sequential fresh Sol 6.1 max implementations with independent Astra max
workflow reviews. After a restart: "system restarted. please recover and
continue." This slice implements only D7: return complete inventory within one
bounded enrichment wait, stopping futile systemic hunting while retaining partial
data and unavailable reporting. Parent supplied the canonical D7 design and
explicitly authorized its documented shared cancellation paths, normal commit,
proof and gates. No push, live probe or deployment is authorized.

## Approved Plan

`docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, D7;
`.agents/review/cdr-d7-design.md` is the canonical implementation guidance.

## Commit Range

`134b22df84956503ed408d4cbdf6118138f972b8..HEAD` (one normal master finding commit).

## Claimed Changes

- One four-minute deadline spans setup, posture, observers and site admission/wait.
- Optional tokens reach actual shared credential/API lock, HTTP, retry and body I/O;
  requested cancellation escapes while independent HTTP timeout behavior remains.
- Known systemic setup/auth failures and either hunting pass's 401/403 stop later
  batches. Partial and successful-empty data survive with honest per-pass counts,
  unavailable coverage, observer diagnostics and a separate stop reason.
- One opt-in site worker owns its entire native lifetime and deferred cleanup;
  response cancellation requests stop and leaves admission occupied until cleanup.
- Compiled page banners show failed versus unattempted requests and retained
  completed posture. Registries are mapped and both versions are bumped.
- Deterministic compiled tests, production regression proof and durable records.

## Constraints / Out Of Scope

Read-only inventory; preserve module policy, isolated credentials, no secrets in
operator errors, applied-result audit and unavailable filter/CSV behavior. Keep
synchronous section-access semantics and shared CloudSignIn behavior. No live
systems, protection/auth policy/config DB/dependencies, shipped PowerShell,
push/deploy, branches/history/gitconfig/hooks/trust, additional agents or self-review.
No adjacent D1/D3/D5 work. Known module-spec drift stays flagged.

## Design Decisions

- Start after complete inventory and keep one CTS; no per-stage/batch reset.
- An attempted batch includes a lookup that fails acquiring its token. Planned,
  attempted, failed and unattempted counts are distinct; skipped observers include
  those skipped because their posture failed. Unsafe ids never invent a batch.
- Succeeded remains posture success. Observer/stop diagnostics are independent,
  preserving posture when only the observer fails, including the final observer.
- Typed internal auth failures avoid message classification; CloudSignIn preserves
  its existing safe exception label. Isolated token/query failures remain isolated.
- The site worker, not the response task, owns native cleanup; admission cannot
  reopen while cleanup stalls. Stop initiation is asynchronous and no Closing
  request is made while Opening. This is cooperative cancellation, not native kill.
- Internal clock/scheduler/bootstrap/runspace seams provide controlled evidence
  without production policy or lifetime changes to existing synchronous callers.
- Initial accepted proof is retained with exact wording/assertion reconciliation;
  final page scope and the timer-reset gap are freshly compiled and proved.

## Deferred Work

D1 first-render fetch, D3 reentry, D5 recomputation; psl-2/3/4, cdr-followup-1,
continuation-host validation and broad D6 audit. Physical AD/SeenBy acceptance is
not inferred from synthetic evidence. Push/deployment require separate authority.

## Review Focus

Independently trace the full listing/enrichment workflow, shared credential/API
callers, retry/lock cancellation, final observer diagnostics, actual counts and
unavailable/filter/audit/export behavior. Check worker admission through stalled
Open/Invoke/stop/disposal, no later command after cancellation, observation and
resource ownership when the response returns. Reconcile the mixed proof receipt
provenance explicitly; no aggregate DLL-hash equivalence is claimed across the
documented literal/assertion edit. Review scope against the approved design first.

## Verification Run

Final fixed baseline 40/40; canonical fifteen-group proof / 63 standard assertion
failures / exact forty-case union; restored affected 988/988, including every proof
case. The final ordered Release build passes with 40 warnings / 0 errors. Full
solution format passes before the single full suite: 4,224 passed / 3 known
AD-unavailable skips / 0 failed, 4,227 total, duration 5 minutes 21 seconds.
All full console output was checked for compiler errors; none were present.
The failed first format attempt is retained and no full suite preceded it.

Final `git diff --check HEAD` and `tools/Test-AsciiOnly.ps1` both pass (exit 0),
with all new test/finding files staged. Their completion receipts are
`final-diff.json` / `final-ascii.json` with complete logs. No code or test bytes
change after the final affected/gate runs. Final reconciliation binds current
source/runtime/test/PDB hashes, exact proof scope, anchor map and ordered receipts.
No shipped PowerShell changed, so PSScriptAnalyzer/Pester are inapplicable. Manual
live checks were not run. Independent Astra review remains the pending step.

# Review Packet

## Mode

IMPLEMENTATION_REVIEW - P2 correction record and coder-side guard closure;
no self-review or second independent verification review is requested.

## Original Request

Owner approved all twenty Revision 6 findings and sequential Sol 6.1 max
implementation / independent Astra max review, then requested recovery and
continuation. The D7 reviewer returned exactly one P2 in `421588e`: deadline
checks ended at the site read, leaving the captured continuation and nested
matching loops unbounded by cancellation. Parent authorized correcting this P2,
proving each new guard with fresh production regressions and committing normally.
Preserve CHANGES REQUIRED as the original verdict and close on coder-side proof
under the noncritical-review policy; no second review or independent approval.

## Approved Plan

`docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, D7;
`.agents/review/cdr-d7-design.md`, plus the independent P2 described above.

## Commit Range

`421588eb44b6d20e11e1e37f196bac53ec14e160..HEAD` - one normal master P2 correction.
Original independently reviewed range remains `134b22d..421588e`.

## Claimed Changes

- Observe the original deadline before matching, between devices, within each
  subnet scan and immediately before publishing a completed Site result.
- Preserve inventory, completed posture and completed Site values; report
  incomplete Site coverage and leave all unevaluated Site cells unavailable.
- Correct the page message for partial Site data. Existing per-row filters,
  applied audit counts and CSV behavior remain and are exercised by compiled tests.
- Preserve synchronous shared callers and data-only directory worker ownership.
  Base 2.27.6 and Defender 1.6.13 include all version fields and catalog pin.
- Three deterministic new guards; six compiled production regressions prove an
  exact three-case union through eight intended standard assertion failures.

## Constraints / Out Of Scope

No D5 parse-once/filter caching, D1 initial-load or D3 reentry change. No
credential/auth policy, protection, locking, config DB, dependency, privilege or
native worker redesign. No live AD/tenant/vault calls, push/deploy, branches,
history rewrite, git config/hooks/trust, further agents or self-review. Preserve
original accepted receipts and the independent CHANGES REQUIRED verdict.

## Design Decisions

- Initialize cheap unavailable markers for the finite fetched inventory before
  evaluating rows. Only a fully completed scan publishes a Site; cancellation
  leaves completed earlier rows intact and marks current/later rows honestly.
- Check cancellation inside the CIDR loop without changing longest-prefix rules
  or introducing D5's parse/cache optimization. One finite CIDR parse/match can
  complete between checks; this is cooperative cancellation, not a native hard stop.
- Keep the public synchronous FindSite entry point and use a separately named
  internal cancellable path for Defender. Section-access callers and native worker
  ownership are unchanged. A worker still never receives the device rows.
- SiteMapRead means every Site evaluation completed. Filters/audits already
  inspect each unavailable marker, allowing truthful partial coverage without
  replacing completed data or changing audit policy. The banner now describes that.
- Deterministic held worker / queued SynchronizationContext and controlled subnet
  enumeration reproduce cancellation at each boundary; watchdogs detect hangs
  but no elapsed duration or scheduling race decides correctness.

## Deferred Work

D1, D3 and D5 remain approved separate slices; D1 is next after this correction.
psl-2/3/4, cdr-followup-1, continuation-host validation and broad D6 audit remain
deferred/unapproved. Physical AD/SeenBy acceptance is unproven; no deployment or
push is included. Known AdminModuleSpec Version 2.0 / June 25 drift stays flagged.

## Review Focus

No second review is commissioned for this P2. The coder's guard evidence targets
the reviewed original behavior, inner-scan cancellation, final publication,
unavailable markers, completed Site retention and compiled partial-site wording.
It also checks actual applied filtering, exact audit and exported-row counts with
complete inventory/posture retained. Original D7 proof is retained without replay;
correction receipts are isolated in `out/cdr-d7/p2-correction/`.

## Verification Run

Formatted fixed baseline 3/3; six fresh compiled production regression groups /
eight intended standard assertion failures / exact three-case union; saved bytes,
MD5/SHA256, newer-mtime restoration and app/runtime/test DLL/PDB provenance pass.
Restored affected tests pass 991/991, including shared callers, all correction and
original D7/D2 cases, complete ClickGate and Progress groups. Exact page mapping
retains all 17 anchors (no twins) and ExpectedLineCount 1293; ProgressRegistry
describes the same operation through matching. Ordered correction Release build
passes with 40 warnings / 0 errors, full solution format verification passes
before the single full Release suite (4,227 passed / 3 known AD-unavailable skips /
0 failed; 4,230 total; 5 minutes 11 seconds), then diff/staged-ASCII guards pass.
`closeout.json` reconciles the exact fixed bytes, current app/runtime/test/PDB
artifacts, accepted proof union and ordered gate receipts. Task build/test servers
are stopped before handoff, with a retained process-shutdown receipt.
All complete test/build logs are checked for compiler errors; none are accepted
as assertion proof. The initial setup run is excluded from proof counts.
The Claude-only token meter ran against the committed baseline; it excludes this
native session, so no native usage/cost is invented. No shipped PowerShell changed;
PSScriptAnalyzer/Pester are inapplicable. No live/browser/deployment check was run.
