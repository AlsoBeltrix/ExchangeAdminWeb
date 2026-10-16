# cdr-d1 - open Defender with a ready form and search deliberately

Status: Implemented and independently APPROVED on master, 2026-10-16 (`568534c`).

Approved scope: `docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, D1.
Owner 2026-10-15: "approved. go." Recovery 2026-10-16: "system restarted. please
recover and continue." This single-writer slice starts on clean master
`a4c019255b85d69ddca3d9cd1d9d185d20e78b80`. Both live origin and github master
refs were checked with `git ls-remote` at
`eb0d19122f4921eca66e4300efe0605403214e23`; this workflow issues no push.

## Evidence and implementation

The committed page called `LoadAsync` from its first `OnAfterRenderAsync` and
rendered only comments/whitespace while `result == null`. This contradicted the
form's instruction to choose filters and Search. The first-render override and
its only-use `loadStarted` field are removed. The authorized/configured page now
shows "No search has been run." with an enabled Search button. That notice is
hidden during the first load; the shared frame continues to own progress.
Completed-empty, refused and failed reads continue through their existing
separate branches. Search and Clear retain `CanBeOnboarded` as the default;
deliberately selecting Any permits a completely blank search.

The obsolete first-load ProgressRegistry exemption is removed. ClickGateRegistry
is re-anchored using committed-versus-working `difflib.SequenceMatcher` equal
opcodes, per `.agents/playbooks/clickgate-reanchor.md`: 1,293 to 1,271 page lines,
all 17 anchors mapped, zero twin anchors and zero unmapped anchors. Only touched
stale lifecycle comments are updated. D4 applied/requested audit snapshots, D9
CSV failure handling and D7 partial/unavailable/deadline behavior are retained.

Defender advances from 1.6.13 to **1.6.14**, with its existing catalog pin updated.
Base remains 2.27.6 and Comms remains 1.4.18; no shared shipped behavior changes.
AdminModuleSpec/DeveloperGuide Version 2.0 / June 25 baseline drift remains
explicitly flagged; current catalog, code and Constitution govern this slice.

## Compiled guards and non-vacuity

`DefenderSearchLifecycleTests` contains exactly four runtime cases. Its custom
renderer attaches the real compiled page, executes initialization and first and
subsequent render callbacks, and dispatches the rendered Search event. The real
listing service uses canned local HTTP; configuration availability and JSONL
audit use production services, and the real page's progress calls are recorded.
Child ModuleVersion rendering is also
retained. No browser, network service, AD, tenant or vault is contacted.

- First and repeated renders perform no read, HTTP inventory request, progress
  begin or lookup audit. The selected default remains CanBeOnboarded; deliberate
  Search sends that filter and displays/audits its result.
- A deliberately blank form enables Search, sends a request without `$filter`,
  and displays/audits its result.
- Two cases (empty result and thrown read failure) distinguish ready, pending
  and settled states; pending work disables Search and reports progress, and the
  settled result restores the button with the correct empty/failure text and audit.

An initial setup run passed three cases and found a test selector that included
the select's nested option attributes. Restricting it to direct attributes fixes
the fixture; its xUnit2031 assertion shape is corrected before proof-byte lock.
This setup failure is not counted as mutation proof. Fresh canonical baseline:
**4/4 passed**. Seven fresh compiled production regressions then yield **14
intended standard Assert failures**, with an exact union of all four new cases:

| Regression | Failed / passed | Intended guard |
|---|---|---|
| Restore committed automatic first-load lifecycle | 4 / 0 | No unrequested query; real initial ready state |
| Remove ready notice | 4 / 0 | Visible initial state |
| Render ready while Search is pending | 2 / 2 | Pending differs from not-yet-searched |
| Render a completed-empty result as ready | 1 / 3 | Searched-empty differs from initial |
| Refuse blank Search | 1 / 3 | Blank search remains allowed |
| Remove the default onboarding filter | 1 / 3 | CanBeOnboarded default survives |
| Hide a read failure behind ready state | 1 / 3 | Failed search differs from initial/empty |

Every regression runs `dotnet test ExchangeAdminWeb.slnx -c Release` with the
four-case filter and xUnit.MaxParallelThreads=4, with no `--no-build`. Full logs
are scanned for CS/RZ/MSB errors. TRX names and expected failed sets must match
exactly and failures must be standard assertions. Each run records fresh root
app, runtime-copy app, test assembly and all corresponding PDB hashes/mtimes;
root/runtime DLL/PDB bytes match. Saved source bytes are restored after every
probe with MD5/SHA256 equality and newer mtimes, including after compiled output.
Six source/test/catalog/registry paths remain locked to the canonical proof bytes.

Page MD5: `1ef84f18ef6ff4c52742b2aaddde2492`.
Page SHA256: `32bb422b9f814a4be824fe24be38553f15d7980c20954ddd73cffbb7b87ce942`.
New-test MD5: `acf534a69f85a8f29de6143f9130b2c1`.
New-test SHA256: `5daa83a36ccc6b6505a55dfaf4ab63716ba3584a1756608a090b8addf7cffa10`.

Actual machine-local receipts are ignored under `.agents/review/out/cdr-d1/`:
`setup.log`/`setup/setup.trx`, `format-touched.log`, `proof.py`, `proof.json`,
`saved/`, `proof-baseline-run.json`, each named probe's full log/TRX/run/evidence,
`proof-summary.json`, `reanchor.py`/`reanchor.json`, `token-usage.json`, final
gate logs/receipts, `recover-full-receipt.py` and `closeout-reconciliation.json`.
No native Sol token usage or cost is attributed by the Claude-only meter.

## Verification

- Restored affected workflow: **995/995 passed**, including Defender, AdSite,
  CloudSignIn, SectionAccess, catalog, and full ClickGate/Progress coverage.
- Ordered final Release build: **40 existing warnings / 0 errors**. Full solution
  format passed before the single full solution test with
  xUnit.MaxParallelThreads=4: **4,231 passed / 3 known AD-unavailable skips /
  0 failed**, 4,234 actual result rows; console duration **5m57s**.
- The full test completed passing, then the receipt wrapper asserted that all
  display names were unique. This is a parser-only error: three display names
  repeat. The original logs/TRX are preserved and no suite rerun is performed.
  `recover-full-receipt.py` reconciles **4,234 unique execution IDs**, actual
  outcomes and TRX `Completed`/console `Passed`. The raw notExecuted counter is
  zero despite three actual NotExecuted rows. The wrapper's exit was 1; its
  nested dotnet numeric exit code and invocation timestamps were not persisted
  before that assertion, so no numeric child exit is invented. The reconstructed
  receipt labels this explicitly and takes execution timing from the TRX.
- `final-diff.json` and `final-ascii.json` record the required final
  `git diff --check HEAD` and ASCII results, run after these records and the new
  test are staged. `closeout-reconciliation.json` reconciles source bytes,
  proof/TRX names and assertions, compiled provenance, registry mapping and gates.
- Task build/test processes and build servers are stopped before handoff;
  `process-shutdown.json` preserves the receipt. Only normal hooks are used.
- Browser/dev acceptance of open-without-fetch and all live integrations: unrun,
  outside this authorization. Compiled renderer coverage does not claim browser
  acceptance. No self-review or independent approval is implied by coder proof.

## Independent review

Fresh native `gpt-6-astra` at max reviewed
`a4c019255b85d69ddca3d9cd1d9d185d20e78b80..568534c514594e1a2c1b839bcdbb7602cf3faae9`
and returned **APPROVE**, with no actionable findings. Intent, scope and
Constitution invariants were checked before the complete page lifecycle,
Search/result/export workflow, callers, progress, audits and registries.

The independent Release rebuild passed with 38 warnings / 0 errors. Focused
tests passed **747/747**, including all four compiled lifecycle cases. Its
**164 evidence checks** reconciled seven regressions / 14 standard failures,
the exact four-case union, restoration and compiled provenance, all 17 anchors,
the original 995 affected passes and ordered final gates. The original full
TRX confirms 4,231 passes / 3 known skips with 4,234 unique execution IDs. The
receipt-parser failure is distinguished from the completed passing test run;
the unpersisted numeric child exit remains unknown.

Receipt: `.agents/review/out/cdr-d1/astra-independent/review.md`, with linked
reconciliation, build, focused TRX and closeout records. Historical mutation
artifacts were assessed through preserved provenance; the independent focused
run used freshly rebuilt assemblies. No full-suite or mutation replay, live
services, browser acceptance or deployment occurred. Final checkout remained
clean at the reviewed hash, with build servers and review task processes stopped.
D1 is closed locally; D3 is next, followed by D5.

# Review Packet

## Mode
IMPLEMENTATION_REVIEW

## Original Request
Owner approved all twenty Revision 6 fixes ("approved. go.") with fresh sequential
Sol 6.1 max implementation and independent Astra max review, and asked after a
restart to recover and continue. This slice implements D1 only: no inventory read
on open, a real initial ready state, preserved default and allowed blank Search.

## Approved Plan
`docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, D1 and D6-local.
Scoped orchestration/authorization: `.agents/decisions.md`, 2026-10-15.

## Commit Range
`a4c019255b85d69ddca3d9cd1d9d185d20e78b80..568534c514594e1a2c1b839bcdbb7602cf3faae9`.

## Claimed Changes
- Opening Defender renders an authorized/configured ready form with no inventory
  work. Search starts the read; pending, completed-empty and failure states differ.
- Preserve default CanBeOnboarded and deliberate filter-free Search.
- Remove obsolete lifecycle state/exemption, re-anchor the complete ClickGate
  block, update touched stale comments, and bump Defender to 1.6.14 with its pin.
- Four compiled lifecycle/event/service/audit cases and exact fresh compiled proof.

## Constraints / Out Of Scope
- D3 handler concurrency guards and D5 filtering/subnet optimization are separate
  future slices. No broad comment/documentation sweep or adjacent deferred fix.
- Preserve D4 question/result audit binding, D9 export failure behavior and D7
  partial/unavailable/deadline behavior. No lock/protection/credential/auth changes.
- No live AD/tenant/vault probe, browser deployment, push, merge, branch deletion,
  history rewrite, hook override/trust/config change or self-review.

## Design Decisions
- Delete the automatic callback rather than leave a no-op lifecycle method and an
  exemption claiming delegated work. The default stays in the existing form.
- The ready notice requires no result and no active load, after the load-error
  branch. The shared progress frame owns pending work; existing result branches
  retain their meanings. Keep the final explicit render with an accurate comment.
- Use the real compiled page and rendered event handler, canned HTTP service seam,
  actual configuration availability and real JSONL audit for the guards.

## Deferred Work
- D3 is next after this slice's independent review; D5 follows after D3.
- The approved plan owns the separately deferred authorization, continuation URL,
  physical-location acceptance and broad D6 work.
- Live/browser acceptance is still required when separately authorized.

## Review Focus
- Independently evaluate the complete affected lifecycle and Search workflow,
  including its callers, form initialization/clear, loading/result/error branches,
  actual event bindings, audits/progress and registry obligations.
- Check scope/intent and Constitution invariants before ordinary defect review.
- Reconcile all four new names, seven probes/14 failures, exact restoration and
  fresh compiled provenance, then the ordered gate. No self-review is claimed.

## Verification Run
- Compiled canonical baseline 4/4; seven probes/14 standard failures, exact
  four-case union and saved-byte/hash/newer-mtime/app/runtime/test/PDB receipts.
- Restored affected workflow 995/995 passes. Ordered Release build 40 warnings /
  0 errors, full format before the single full suite (4,231 passed / 3 known
  AD-unavailable skips / 0 failed), then final staged diff/ASCII receipts above.
- The passing full run's receipt-parser correction, execution-ID/raw-counter
  reconciliation and unpersisted numeric child exit are disclosed above; no rerun.
- Manual browser/dev/live acceptance unrun; no external action is authorized.
