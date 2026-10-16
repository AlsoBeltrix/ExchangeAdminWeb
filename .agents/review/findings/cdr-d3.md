# cdr-d3 - refuse overlapping Defender loads and CSV exports

Status: Implemented and locally verified on master, 2026-10-16; independent review pending.

Approved scope: `docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, D3.
Owner 2026-10-15: "approved. go." Recovery 2026-10-16: "system restarted. please
recover and continue." This single-writer slice starts on clean master
`4416aadf90a0b2c745934996f2b874174b360290`. Both live origin and github master
refs were checked with `git ls-remote` at
`eb0d19122f4921eca66e4300efe0605403214e23`; this workflow issues no push.

## Evidence and implementation

The actual committed handlers already raised their own busy flag before their
first await, but neither `LoadAsync` nor `DownloadCsvAsync` refused a second
entry. Disabled markup therefore did not prevent a stale/direct event from
starting another load or export. Two same-kind invocations could also lower
their shared flag while the other invocation still owned pending work.

Each handler now returns immediately on `IsBusy`, before snapshots, selection,
progress, audit or its cleanup scope. `IsBusy` already includes both load and
export flags, so the same guard handles both directions. The existing flag
raises and protected finally blocks remain; a rejected invocation never enters
the scope that lowers its owner's flag. No new lock or shared abstraction is
introduced. Empty/refused exports still start no work. D1's ready/default/blank
Search, D4's applied snapshot, D9's safe failure/audit semantics and D7's
partial/unavailable/deadline behavior remain in scope for affected verification.

The page grows from 1,271 to 1,277 lines. Committed-versus-working
`difflib.SequenceMatcher` equal opcodes map all 17 ClickGate anchors exactly;
there are no gated twins or unmapped anchors. The count is updated, and the
existing ProgressRegistry descriptions now name the early refusal/ownership
contract. Defender advances from 1.6.14 to **1.6.15**, with its catalog test pin.
Base remains 2.27.6 and Comms remains 1.4.18; shared shipped behavior is unchanged.
Known AdminModuleSpec/DeveloperGuide Version 2.0 / June 25 baseline drift remains
flagged; current code, catalog and Constitution govern this slice.

## Compiled guards and non-vacuity

`DefenderOperationConcurrencyTests` has eight runtime cases: Load -> Load,
Load -> Download, Download -> Load and Download -> Download, each with a
successful and a failed owner. The real compiled page is initialized/rendered
and starts the owning operation by its rendered event. Within that same
dispatcher stack, before the first continuation can run, the test checks the
owning flag and immediately invokes the competing handler. Deterministic
service/transfer barriers then hold the owner while two more stale events are
refused. The tests check loaded row identity, stored applied audit identity,
service/transfer counts, disabled controls, progress disposal/completion,
actual CSV row IDs, successful/failed JSONL audits and successful retry after
release. Production listing/CSV/audit code uses local canned HTTP and real
JSONL logging; no external integration or browser is contacted.

The existing D4 `AppliedQuestionSurvivesFormEditsAndBindsExportsAcrossAReplacementLoad`
case deliberately permitted both overlap directions before D3. It now exports
the first applied question after form edits but before the replacement load,
checks that a download during that load adds no audit/transfer, then checks
that a load during export cannot replace the selected inventory. The final
CSV/audit still matches the replacement load's stored applied question and
evaluation details. The eight new cases plus all eleven existing D4 cases
passed together (19/19).

Setup corrections are not mutation proof. The first setup run's selector
recognized Text frames but Search renders as Markup; adding Markup support
produced 8/8 passes. A preliminary guard-removal run then produced the intended
four failures, but custom Assert.True messages hid the required standard
assertion text. That run is unaccepted and preserved with its initial
baseline/source bytes under `precanonical-assert-messages/`. Removing only the
three custom messages precedes the fresh canonical baseline and all counted
regressions. The preliminary run is excluded from the totals below.

Fresh canonical baseline: **8/8 passed**. Six freshly compiled production
regressions produce **20 intended standard Assert.True failures**, with the
exact union of all eight new cases:

| Regression | Failed / passed | Intended guard |
|---|---|---|
| Remove Load's early busy return | 4 / 4 | Load refuses either active operation |
| Remove Download's early busy return | 4 / 4 | Download refuses either active operation |
| Raise Load's flag after its first await | 4 / 4 | Load owns busy before yielding |
| Raise Download's flag after its first await | 4 / 4 | Download owns busy before yielding |
| Rejected Load lowers its shared flag | 2 / 6 | Rejection cannot release an active Load |
| Rejected Download lowers its shared flag | 2 / 6 | Rejection cannot release an active Download |

Each probe runs the Release solution with the eight-case filter and
xUnit.MaxParallelThreads=4, with no `--no-build`. Full logs are scanned for
CS/RZ/MSB errors. TRX execution IDs, exact names/outcomes, expected failed sets
and standard assertions are checked. Every probe records fresh root app,
runtime-copy app and test DLL/PDB hashes and mtimes; root/runtime DLL/PDB bytes
match, and production binaries differ from the fixed baseline. Saved page bytes
are restored after each probe with exact-byte/MD5/SHA256 equality and a newer
mtime than the mutation and compiled output. All seven proof source paths stay
locked. Numeric child exits and invocation timings are persisted before receipt
parsing/assertions, including for the full suite.

Page MD5: `eec73d87f33c2f10f7ea3c82b9c806ac`.
Page SHA256: `fd7108a3ce428bfe7294f68be90a83f4149e3d0c84b0dff9fe30eca66f714e6e`.
New-test MD5: `30b24493274444e3f492b97623e5e9dc`.
New-test SHA256: `1b1d59216ba8a3deddd14fa1f6d07f798a0fb594140b85a72da148c9def1cd1c`.

Actual machine-local receipts are ignored under `.agents/review/out/cdr-d3/`:
setup and compatibility logs/TRX/run receipts, formatter logs, `proof.py`,
`proof.json`, `saved/`, named production probes, `proof-summary.json`,
`reanchor.py`/`reanchor.json`, `gates.py`, token usage and final gate receipts.
`closeout-reconciliation.json` checks exact source/compiled provenance, all
eight names, accepted assertions, restored affected results, anchors and gates;
`process-shutdown.json` records task/build-server closeout.
The committed-baseline token meter covers Claude transcripts only; no native
Sol token usage or cost is attributed.

## Verification

- Restored affected workflow passes **1,003/1,003**, covering Defender, AdSite,
  CloudSignIn, SectionAccess, catalog, and full ClickGate/Progress tests, including
  all eight new cases and the adapted D4 snapshot/audit case.
- Final ordered Release build: **40 existing warnings / 0 errors**. Full solution
  format passes before the single full solution suite, with
  xUnit.MaxParallelThreads=4: **4,239 passed / 3 known AD-unavailable skips /
  0 failed**, 4,242 actual result rows and unique execution IDs. Console duration
  is **5m7s**; complete dotnet invocation wall time is **319.23 seconds**. Numeric
  child exit **0**, start/end timestamps and wall time are saved before parsing.
- The three repeated display names are retained without assuming names are
  unique. Raw `notExecuted=0` is distinguished from the three actual
  `NotExecuted` rows, whose names match the prior accepted full suite.
- `final-diff.json` and `final-ascii.json` record the required
  `git diff --check HEAD` and ASCII checks after these records and both new files
  are staged. The final evidence reconciliation retains exact fixed sources and
  app/runtime/test DLL/PDB identity through the ordered gate. Normal hooks only;
  task build/test processes and build servers are stopped before handoff.
- PowerShell lint/Pester: not run; no tracked PowerShell code changes. Browser/dev
  acceptance and all live AD/tenant/vault integrations remain unrun, outside
  this authorization. Compiled renderer coverage does not claim browser
  acceptance. Historical mutation binaries are superseded by later builds;
  their complete logs/TRX/hashes/mtimes remain as receipts. No self-review or
  independent approval is implied by coder proof. No push/deployment/live probe,
  adjacent D5 work or hook override occurs.

# Review Packet

## Mode
IMPLEMENTATION_REVIEW

## Original Request
Owner approved all twenty Revision 6 fixes ("approved. go.") with fresh sequential
Sol 6.1 max implementation and independent Astra max review, and asked after a
restart to recover and continue. This slice implements D3 only: server-side
refusal of overlapping Load/Download invocations, owning flags before the first
await, and ownership-safe cleanup.

## Approved Plan
`docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, D3.
Scoped orchestration/authorization: `.agents/decisions.md`, 2026-10-15.

## Commit Range
`4416aadf90a0b2c745934996f2b874174b360290..HEAD` (the D3 implementation commit).

## Claimed Changes
- Both handlers reject `IsBusy` before work or cleanup; their existing owner
  raises precede the first await and finally blocks release only admitted work.
- Eight compiled event/handler cases cover all four overlap directions with
  success/failure, audit/row/transfer/progress behavior and successful retries.
- Adapt the affected existing D4 test while retaining applied snapshot/audit
  association, re-anchor the complete ClickGate block, update relevant Progress
  descriptions, and bump Defender/pin to 1.6.15.

## Constraints / Out Of Scope
- Preserve D1's no-fetch-on-open/default/blank Search, D4's requested/applied
  question and row association, D9's handled CSV failures/audit semantics, and
  D7's partial/unavailable/deadline behavior.
- D5's parsing/filter cache remains a separate slice. No broad D6 comment audit,
  new lock/protection/auth/credential policy or adjacent deferred fix.
- No live AD/tenant/vault probe, browser deployment, push, branch/merge/deletion,
  history rewrite, hook override/trust/config change or self-review.

## Design Decisions
- Use the existing page-wide `IsBusy` predicate and flags rather than adding
  synchronization. Blazor's dispatcher serializes synchronous handler prefixes;
  raising before await is the operation admission boundary.
- Place rejected returns before handled/finally scopes so rejection cannot
  lower another invocation's flag. Retain export's empty-selection-before-raise
  order and D9's enclosing handled selection scope.
- Exercise actual rendered owner events and directly invoke stale competing
  events on the same dispatcher, with explicit service/JS barriers and local
  production listing/CSV/audit services.

## Deferred Work
- Fresh independent Astra max workflow review of this slice; D5 follows only
  after this committed boundary is accepted.
- The approved plan owns deferred authorization, continuation URL,
  physical-location acceptance and broad D6 work.
- Browser/live acceptance remains separately gated.

## Review Focus
- Independently evaluate the whole page lifecycle, both handlers and their
  callers/events, selection/snapshot publication, cleanup, progress and audits.
- Check owner admission before the first await, both overlap directions and
  rejected-event cleanup ownership, including read/transfer failure and retry.
- Verify intent/scope/Constitution first, then reconcile all eight new names,
  exact compiled proof union, restoration/provenance, all anchors and ordered
  gate. The known module document drift remains flagged. No self-review is claimed.

## Verification Run
Fresh canonical baseline 8/8; six compiled regressions / 20 standard assertion
failures / exact eight-case union; restored affected workflow 1,003/1,003.
Final ordered Release build: 40 existing warnings / 0 errors; full solution
format passes before the single full suite: 4,239 passed / 3 known skips /
0 failed, 4,242 unique executions, child exit 0 persisted before parsing.
Final staged diff/ASCII and exact source/proof/artifact/anchor/gate reconciliation
receipts are linked above. Task/build servers are stopped before reviewer
handoff. Browser/live acceptance and PowerShell lint/Pester are not run as
described above. Independent review is pending.
