# cdr-c2 - current submission authorization and command-dispatch gates

Status: Implemented on master; restored full gate passed; independent Astra max review pending.
Push remains pending under policy ask.
Approved scope: `docs/Comms10kDefenderRemediation-Plan.md`, Revision 6, C2, with
`.agents/review/cdr-c2-design.md` defining the capture/dispatch boundaries.
Baseline: clean master `ec824c03fcef2cb6abf357378d8950b17f976df2`, base 2.27.3,
Comms-10k 1.4.13. Both origin and github HEAD verified live at
`885397981d7550afebf6518ef21b4fc54b062859` on 2026-10-15. Work remains local.

## Evidence and declared scope (before code changes)

ExecuteReplace obtains the current principal and passes live policy, but persists no
authorization snapshot. Comms10kReplaceProcessor has no authorization or enablement
gate. The service performs asynchronous credential retrieval and runspace/target/
primary-member preparation before clear. ExecuteSequence assumes that entering its
clear delegate means dispatch occurred, so a gate inside that delegate needs an
explicit dispatch boundary to distinguish a refusal from possible changes.

Declared files:

- `Components/Pages/Comms10k.razor`: current-principal/current-section snapshot and
  effective enablement immediately with enqueue; same principal as SubmittedBy;
  preserve frozen C4 confirmation and C5 actor-bound caches/denial cleanup.
- `Services/Jobs/Comms10kReplaceProcessor.cs`: readable exact-section snapshot,
  originally authorizing usable SID still allowed, effective module enablement at
  stage entry and a live callback for each actual mutation attempt/retry.
- `Services/Comms10kService.cs`, `Services/Comms10kReplaceWriter.cs`: carry the required
  gate through all preparation, check immediately before clear/add dispatch, track
  actual clear attempts, refuse before any dispatch, and read back after any attempt.
- `Modules/ModuleCatalog.cs`: Comms patch only; no shared behavior change intended.
- `ExchangeAdminWeb.Tests/Comms10kAuthorizationTests.cs`: snapshot, revocation,
  dispatch/count, audit/reason, and fresh same-principal page capture regressions.
- Existing Comms fixtures in `Comms10kRefusalTests.cs`, `Comms10kApprovedSetTests.cs`,
  `Comms10kInterruptionTests.cs`, `Comms10kReplaceSequenceTests.cs`,
  `Comms10kOwnedJobsPageTests.cs`, `Comms10kOwnedJobsCacheTests.cs` and
  `AuditCategoryFilingTests.cs` as required by the new authorization contract.
- ClickGateRegistry/ProgressRegistry only if the re-anchor playbook establishes a
  necessary change; Comms has no current line-keyed ClickGate entries.
- This finding, review index, approved plan, current state and token log: evidence,
  compiled mutation/restoration receipts, final gates and independent-review packet.

## Boundaries

Detect allowed-group configuration removal and module disablement, not Windows-token
membership refresh; a dispatched cmdlet cannot be retracted. Preserve original
AuthorizedGroups semantics; RoleClaims containing a newly allowed SID cannot replace
the original grant. No grandfathering jobs with missing/invalid snapshots.
Preserve C1/C3/C4/C5/C9/C11 contracts, one failure audit and the specific first reason
even when the runner advances after failed Resolve. C7 reentry/CSV/raw rejection text,
C8 export and C6 ticket work remain separate. No protection checks, locking, broad
cleanup, shared authorization refactor, live AD/tenant probes, deployment, push,
branch/history/hook/trust changes, self-review or further subagents. The known
module-spec 2.0/June 25 header drift remains; Constitution/current code govern.

## Implementation

ExecuteReplace still freezes the C4 confirmation before its first await. It obtains the
current principal, requires live policy and a usable authenticated full identity, and
binds the C5 cache actor before completeness checks. Immediately with enqueue it reads
current section groups, captures the full live authorization decision, checks effective
enablement and a captured grant, and persists the snapshot. SubmittedBy/SubmittedByDisplay
use that same principal's full Identity.Name. Denied/unverifiable capture uses the C5
cache cleanup and the existing one refusal audit/notification path.

Both worker stages require a readable snapshot for exact ordinal section Comms10k,
effective module enablement, and an originally authorizing AuthorizedGroups SID still
present among current usable section-group SIDs. Raw RoleClaims cannot substitute a
new grant. The gate re-reads the existing repositories; no shared service changed.

A required callback crosses service credential awaits, target lookup, runspace Open,
module import, GUID reread and primary-member preflight. Clear and each add invoke the
gate immediately before PowerShell.Invoke, inside command cleanup and the operation's
retry delegate. Denial leaves no queued command to run later during read-back. The
clear-dispatched flag changes only after its gate passes, immediately before Invoke.
The writer's explicit dispatch observation returns RefusedBeforeAnyChange with known
zero writes when the gate refused before any clear; any clear attempt instead stops
subsequent writes and runs the normal strict immutable-identity read-back.

The outcome still follows the observation: mismatch is PartlyApplied, unreadable is
CouldNotConfirm, and an exact approved set can still be Succeeded even if authorization
stopped further adds. In that last case, the message retains the denial and acknowledged
write count remains unknown. Read-back cannot establish which operation wrote members.
This preserves C11's evidence model rather than treating permission denial as proof
that a dispatched cmdlet did nothing. No dispatched cmdlet can be retracted.

The first refusal reason and one mutation audit survive a runner continuation after
failed Resolve. A pre-write stage refusal retains requested count when resolution was
already known; unavailable resolution stays unknown. Comms-10k is 1.4.14; the base stays
2.27.3. The page re-anchor procedure mapped all 1,164 old page lines to the 1,187-line
page. Comms has only the existing ClickGate tier-3 exclusion, no line-keyed entries.
ProgressRegistry's existing handlers and call identities remain accurate; no registry
edit or new UI handler was needed.

## Regression coverage and limits

46 new compiled cases exercise the real processor, service and PowerShell SDK against
harmless local command functions, real temporary SQLite authorization stores, captured
audits/notifications, the real runner's failed-row continuation, and the compiled page
submission handler. They cover missing/malformed/wrong-section snapshots, no original
grant, unusable groups, original SID removal, a newly allowed RoleClaims SID, disabled
or unreadable configuration, both stage entries, surviving original grants (including
claims-free decisions), revocation during awaited credentials and every later preparation
phase, clear/add attempts and retries, denial before actual dispatch, command cleanup,
after-clear read-back outcomes, specific reasons and exactly one audit/notification.
Two cases restore configuration between a refused Resolve and the continuing Replace
row, proving that restoration cannot overwrite the original authorization reason.
Page cases change current groups across an await, switch the authenticated full owner,
exercise Windows-only IsInRole, fail live policy/enablement/current-section capture,
and clear cached results after capture failure while preserving frozen C4 inputs.

Older executable fixtures now provide valid snapshots and enabled section access;
unapproved older jobs receive no exception. Existing source wiring assertions accept
the required authorization callback while retaining their identity/retry contracts.
The first harness compile found an incorrect FailedRows property name. The first
686-case run passed 683 and failed three corrupt-store fixture disposals because an
ad hoc SQLite connection used a distinct pool string; that connection now disables
pooling. The corrected fresh C2 run passed 44/44, and the expanded run with the two
restored-configuration cases passed 46/46 before their mutation proof.

These checks do not prove live AD interoperability, browser rendering or hosted-circuit
behavior. They detect configuration removal/disablement and re-evaluate the captured
decision; they do not refresh Windows-token membership or detect revocation hidden by
an existing token. No directory, vault or tenant was contacted.

## Compiled mutation proof

All 46 new cases failed intended Assert.* assertions across 19 sequential compiled
production regressions, producing 75 assertion failures. Each probe rebuilt with
`dotnet test ExchangeAdminWeb.slnx -c Release --no-restore --filter ...` and
`xUnit.MaxParallelThreads=4`. No --no-build result, compiler failure, non-assertion
failure or unexpected failure count was accepted. The first 14 groups proved all
44 initial cases. Five supplemental groups isolate the original-grant/SID/section/
enablement guards and prove both restored-configuration cases. Their per-case union
is exactly the expanded 46-case baseline, with no missing name.

| Probe | Compiled production regression | Assertion failures / executed |
|---|---|---|
| M1 | omit worker stage-entry gates | 18 / 18 |
| M2 | deny surviving original grants | 2 / 2 |
| M3 | omit authorization immediately before clear Invoke | 8 / 8 |
| M4 | omit authorization immediately before add Invoke | 6 / 7 |
| M5 | move add gate outside the retry delegate | 1 / 6 |
| M6 | move clear gate outside the retry delegate | 1 / 6 |
| M7 | assume delegate entry proves clear dispatch | 8 / 8 |
| M8 | treat a dispatched clear as a pre-write refusal | 6 / 6 |
| M9 | throw denial outside queued-command cleanup | 2 / 2 |
| M10 | omit persisted submission snapshot | 2 / 2 |
| M11 | reuse initialization-time SubmittedBy | 2 / 2 |
| M12 | read allowed groups before the authorization await | 2 / 2 |
| M13 | omit page policy/current-grant/enablement refusal gates | 4 / 5 |
| M14 | retain cached jobs after snapshot-capture failure | 1 / 5 |
| M15 | authorize raw RoleClaims instead of original AuthorizedGroups | 3 / 17 |
| M16 | allow unusable section-group values to authorize | 1 / 17 |
| M17 | omit exact-section check | 3 / 17 |
| M18 | omit worker effective-enablement check | 3 / 17 |
| M19 | overwrite the first refusal after configuration restoration | 2 / 2 |

Per-case union coverage (method names are in Comms10kAuthorizationTests.cs):

| Case family / parameter partition | Cases | Proving probes |
|---|---|---|
| EveryStage, stage 0: missing, malformed, wrong-section, wrong-case, no-grant, null-grant, removed, replacement-claim, unusable, disabled, unreadable-sections, unreadable-enablement | 12 | M1; narrower M15-M18 where applicable |
| EveryStage, stage 1: missing, wrong-section, removed, replacement-claim, disabled | 5 | M1; narrower M15/M17/M18 where applicable |
| OriginalSurvivingGrant: first/second original SID retained | 2 | M2 |
| RevocationDuringAwaitedCredentials: section removal/module disablement | 2 | M3, M7 |
| FinalClearGate: target lookup, runspace, import, target reread, primary preflight with section removal/disablement | 6 | M3, M7 |
| AfterAnyClearAttempt: after clear (remove/disable/unreadable read-back), clear retry, between batches, add retry | 6 | M8 for all; M4 for all except clear retry; M5 add retry; M6 clear retry |
| ReadbackCanConfirmApprovedPrimarySet after denied adds | 1 | M4 |
| DeniedQueuedMutation: clear/add command cleanup | 2 | M9 |
| RunnerStillAdvances after denied Resolve | 1 | M1 |
| SubmissionCapturesFreshGroups: group claims/Windows-only role | 2 | M10, M11, M12 |
| SubmissionDenial: policy/new section grant/disabled/unreadable section | 4 | M13 |
| SubmissionDenial: unreadable live role capture clears caches | 1 | M14 |
| RestoredConfiguration: section access/module enablement restored before Replace | 2 | M19 |

Every group restored saved exact bytes in finally, verified MD5 and SHA256, and
advanced source mtimes beyond the mutated version. Both scripts' fixed hashes agree:

| Source | MD5 | SHA256 |
|---|---|---|
| Comms10kReplaceProcessor.cs | `C8EAE0021E2AA4A6A35CF719F014CD48` | `8C687D356FCFB94EA5C6C35F545AA693FFC8E4E1172C599EACDA654A4727B475` |
| Comms10kService.cs | `A077BEFED3DE001B47E07AF50ED1FA58` | `8CB2E2278A65076498851681CBD473B8C73C5F22BD83F11B09F3D0D8F6F76205` |
| Comms10kReplaceWriter.cs | `F1F5F8795B39D0BCECA79D617C4564DD` | `988D67B3FCF552A5EEA878425E8B6525A59799E6ABF4A116763B4D22583915C1` |
| Comms10k.razor | `12A22318E1D551061D2A2C6D0978368C` | `7FBF469DCEC5463C45557903EC4CE7274F387D89D10A4B216FEB1DAABB82F3C9` |

Ignored machine-local supplements: `.agents/review/out/cdr-c2-mutations/` and
`cdr-c2-supplement-mutations/` each contain logs/TRX, fixed-byte backups and receipts.json
with exact case names, assertion messages, hashes and per-restore mtimes. Their scripts
are `cdr-c2-mutations.py` and `cdr-c2-supplement-mutations.py` in the parent output folder.
The corrected 44-case and expanded 46-case baselines have separate TRX directories
`cdr-c2-regressions/` and `cdr-c2-expanded/`; the initial broader run is `cdr-c2-focused/`.
This record is the durable evidence; those ignored artifacts supplement it.

## Verification

- Restored `dotnet build ExchangeAdminWeb.slnx -c Release`: exit 0, 57 warnings / 0 errors.
  The prior C5 correction build emitted 40 NuGet warnings and no C# recompile diagnostics.
  This build has the identical dependency advisory set plus 17 existing nullable/unused
  C# diagnostics emitted by recompilation. No diagnostic names new C2 logic or tests.
  The sole Comms warning at line 577 exactly maps to unchanged HEAD line 575; the other
  C# diagnostics are in untouched files. No dependency or shared/base version changed.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0,
  existing workspace-loading warning only, completed before the full suite.
- `dotnet test ExchangeAdminWeb.slnx -c Release -- xUnit.MaxParallelThreads=4`:
  exit 0, 4,091 passed / 3 skipped / 0 failed (4,094 total), 5 m 41 s. TRX confirms
  every one of the 46 new case names passed against the restored production sources.
  This was the slice's only full-suite run; no full baseline suite was run.
- `git diff --check HEAD`: exit 0, repeated after final bookkeeping.
- `tools/Test-AsciiOnly.ps1`: exit 0 with both new finding/test files staged, repeated
  after final staging. The new .cs source is included in the tracked-file ASCII scan.

Gate logs use the `cdr-c2-build`, `cdr-c2-format`, `cdr-c2-test`, `cdr-c2-diff` and
`cdr-c2-ascii` prefixes in `.agents/review/out/`; the final TRX is
`cdr-c2-final/cdr-c2-final.trx`. `cdr-c2-warning-comparison.json` records the warning
classification against C5's saved build log and verifies that the other C# warning
files are untouched. All four production MD5/SHA256 hashes still match both mutation
receipts after the final build, format verification and full test suite.

No shipped PowerShell changed; PSScriptAnalyzer/Pester were not required or run.
Browser/hosted circuit, installed AD cmdlet interoperability, live AD/tenant/vault
operations and deployment were not run. No self-review, other agent, hook/trust/config
change, branch/history action or push occurred. The configured normal commit is used.
The committed-baseline token meter ran; its Claude-only transcript scope excludes
this native Sol slice, so no slice usage or cost is attributed.

## Review packet

### Mode

IMPLEMENTATION_REVIEW; independent Astra max review is pending. No self-review ran.

### Original Request

The owner approved all twenty Revision 6 findings and sequential fresh Sol 6.1 max
implementation with independent Astra max workflow review. Implement C2 only: capture
current-principal authorization with enqueue, gate worker stages and actual command
dispatch/retries against original grants and effective enablement, and report an
honest refusal or observed/unknown post-clear state without losing reasons or audits.

### Approved Plan

`docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, C2; implementation
boundary guidance in `.agents/review/cdr-c2-design.md`.

### Commit Range

`ec824c03fcef2cb6abf357378d8950b17f976df2..HEAD` for this finding's normal master commit.

### Claimed Changes

Current exact-section snapshots use the same fresh full principal as SubmittedBy.
Original usable authorizing SIDs and effective enablement gate both stages and every
actual clear/add attempt, including retries after all preparation. Explicit clear
dispatch tracking separates known-zero refusal from actual/unknown read-back after
an attempt. First refusal, one audit/notification, frozen approved GUID set, actor-bound
jobs cache and C11 count evidence are preserved. Comms is 1.4.14; base remains 2.27.3.

### Constraints / Out Of Scope

The Boundaries and Regression coverage/limits sections apply. No C7/C8/C6 work,
protection checks, locking, shared authorization refactor, hook/trust/config changes,
branch/history operation, deployment, push, live probe or new agent was performed.

### Design Decisions

- Require the authorization callback in the writer service API so the worker cannot
  silently omit it. Existing shared authorization stores/types keep their behavior.
- Gate inside queued-command cleanup, immediately before Invoke. A denied queued
  mutation must not survive to a later read-back or retry.
- Mark clear dispatched only after its gate passes. After any attempt, even one that
  throws, preserve uncertainty and read back using pinned immutable identities.
- Read-back may confirm exact success after denial stops adds; the denial remains in
  the message and acknowledged writes stay unknown. This is existing C11 outcome/count
  semantics, not authorization to issue another mutation after the gate denies.
- Accept only an original AuthorizedGroups SID still in usable current section groups;
  newly allowed RoleClaims cannot replace that original submission decision.
- Older missing-snapshot jobs fail closed. Tests receive valid snapshots rather than
  a production compatibility exception.

### Deferred Work

C7 reentry/atomic CSV/original rejection text, C8 exports, C6 ticket display and the
remaining approved Defender findings are separate slices. The unapproved psl-2/3/4
and cdr-followup-1 remain deferred. Browser/live cmdlet acceptance remains unverified.

### Review Focus

Trace current page principal/policy -> current groups/capture/enablement -> enqueue;
preserve C4 frozen inputs and C5 cache binding/denial behavior. Trace both stages,
configuration changes across credential awaits and all preparation, then callback/
Invoke/cleanup ordering. Distinguish entering a delegate from actual clear dispatch,
and re-check inside each clear/add retry. After any dispatched clear, require no more
mutation after denial and actual/unknown read-back with the specific reason. Confirm
that newly allowed RoleClaims do not authorize and that a recovered configuration
cannot erase the first refused Resolve result, audit or completion reason.

Review the whole affected workflow without treating earlier review conclusions as
proof. Per-case union, compiled failure counts and exact restoration hashes are above.

### Verification Run

See Verification and compiled mutation proof above: build 57 warnings / 0 errors;
full format verification before the single restored full suite; 4,091 passed /
3 skipped / 0 failed; staged ASCII/diff passed; 46/46 new cases mutation-proven in
19 compiled probes (75 intended assertion failures), exact bytes/hashes/mtime restored.
No live AD/tenant/vault call, browser/hosted circuit,
deployment or self-review. No shipped PowerShell changed, so PSScriptAnalyzer/Pester
were not required or run. The token meter ran against the committed baseline, but its
Claude-only scope excludes this native Sol slice and supplies no attributed usage.
