# EmergencyDisable — Synced-User Entra Disable Plan

Status: Implemented
Owner: Michael
Last verified against code: commit (feature/blocked-senders-module HEAD), 2026-06-29

## 1. Goal  [YOU]

Emergency Disable is reporting **failed** on the last step ("Disable Entra account") even
though the account is actually disabled. Root cause: for users synced from on-prem AD,
`accountEnabled` is mastered on-premises; Entra holds a read-only copy, so a direct Graph
`PATCH /users/{upn} accountEnabled=false` is rejected. The on-prem AD disable already
succeeded and will propagate to Entra on the next sync. Fix: for synced users, skip the
direct Entra PATCH and record that step as not-applicable, so the operation is not falsely
marked failed.

## 2. Non-goals  [YOU]

- Do not trigger or wait for an Entra Connect sync (separate option, not chosen).
- Do not change the AD disable, password reset, or session-revoke steps.
- Do not change cloud-only (non-synced) user behavior — they must still be PATCHed.
- No change to protected-principal, ticket, credential, or audit-category contracts.

## 3. Acceptance criteria  [YOU approve each; model may propose]

- AC1: For a user where Entra reports `onPremisesSyncEnabled = true`, the Entra disable step
  is **skipped** and recorded as `SKIPPED` / N/A with a clear reason ("synced from on-prem;
  disabled via AD, propagates on next directory sync"). It is NOT counted as a failure.
- AC2: When AD disable, password reset, and session-revoke all succeed and the Entra disable
  is skipped for a synced user, the overall operation result is **success**.
- AC3: For a cloud-only user (`onPremisesSyncEnabled` false/absent), behavior is unchanged:
  the Entra PATCH still runs and a real failure still fails the operation.
- AC4: When the Entra PATCH does run and fails, the trace/step detail includes the Graph
  HTTP status (and safe error code/message), not a bare "returned non-success" — so a future
  failure is diagnosable from logs. (Closes the swallowed-error gap that blocked this
  investigation.)
- AC5: The sync state is read from the same Graph user the operation already reads in
  pre-state; no second identity lookup that could resolve a different object.

## 4. Failure behavior  [YOU own]

| Step / dependency | If it fails | The user sees | System state afterward |
|---|---|---|---|
| Graph pre-read can't determine sync state | Treat as cloud-only and attempt PATCH (current behavior; no worse than today). Trace records the unknown. | Existing step result | No change vs today |
| PATCH runs and Graph returns "on-premises mastered" for a user we thought was cloud-only | Step fails with the real Graph message surfaced (AC4) | Failed step with diagnostic detail | AD already disabled; operator escalates |
| Synced user, AD disable failed earlier | Unchanged — AD failure already fails the op before Entra step matters | Failed AD step | Per existing flow |

## 5. Rollback / blast radius  [YOU own]

- Single service file (`Services/EmergencyDisableService.cs`) plus its tests; module-scoped.
- Reversible by reverting the commit. No config, schema, or cross-module impact.
- Risk if wrong: a synced user's Entra copy is not *directly* disabled — but it is disabled
  at the on-prem master and syncs, and sign-in sessions are already revoked, so access is cut
  immediately regardless. The change cannot leave an account *more* enabled than today.
- Module version bumps 1.0.4 → 1.0.5 (module-scoped behavior change). App version untouched.

## 6. Design sketch  [MODEL — Michael skims]

All in `Services/EmergencyDisableService.cs` (read end-to-end for this plan):

1. **Read sync state from the existing pre-read.** `ReadEntraEnabledState` (line 333) already
   does `GET /users/{upn}?$select=accountEnabled`. Extend the `$select` to
   `accountEnabled,onPremisesSyncEnabled` and return both (small struct or out param) so the
   pre-state captures `preEntraEnabled` AND `isSynced` from one call (AC5). No new lookup.
2. **Branch the Entra disable step.** In `DisableAsync` (line 234) where `ExecuteDisableEntra`
   is called: if `isSynced`, do not call it; add a `DisableEntra` step with status `SKIPPED`
   and the synced reason, and emit a trace `Step("DisableEntra","Skipped",...)`. Otherwise
   call `ExecuteDisableEntra` as today (AC1, AC3).
3. **Overall-success accounting.** `allMutationsSucceeded` (line 249) currently requires
   `disableEntraResult.Status == "OK"`. Change to accept `OK` **or** `SKIPPED` for the Entra
   disable step only (AC2). AD disable / reset / revoke still must be `OK`.
4. **Surface the Graph error (AC4).** Today `ExecuteDisableEntra` (line 496) only gets a
   `bool` from `GraphTokenClient.PatchAsync` (`GraphTokenClient.cs:98`), so the status/body
   are lost. Add a status-returning PATCH (mirror of `GetWithStatusAsync`, line 32):
   `PatchWithStatusAsync` returning `(bool ok, HttpStatusCode status, string? safeError)`,
   and have `ExecuteDisableEntra` include status + sanitized Graph error code in the step
   detail and trace. Do not log tokens or full bodies (Constitution §Auditing: no secrets/raw
   auth bodies) — extract Graph `error.code`/`error.message` only.

Conformance: matches the existing snapshot/step/trace shape; `DisableStepResult` already has a
free-form `Status` string so `SKIPPED` needs no type change. Snapshot persistence (steps 5/7)
is unchanged in shape.

## 7. Task breakdown  [MODEL — Michael skims]

1. (AC4) Add `PatchWithStatusAsync` to `GraphTokenClient`; keep `PatchAsync` as a thin
   wrapper for other callers (M365GroupManagement etc.) to avoid scope creep.
2. (AC5) Extend pre-read to also return `onPremisesSyncEnabled`; thread `isSynced` into the
   snapshot/flow.
3. (AC1, AC3) Branch the Entra disable step on `isSynced` (skip + SKIPPED step vs run).
4. (AC4) Have `ExecuteDisableEntra` record Graph status + sanitized error in step/trace.
5. (AC2) Update `allMutationsSucceeded` to accept `OK`/`SKIPPED` for Entra disable.
6. Bump module version 1.0.4 → 1.0.5; update the version assertion in
   `EmergencyDisableServiceTests.ModuleCatalog_EmergencyDisable_IsFailClosedAndVersioned`.
7. Tests (§8).

## 8. Test plan  [MODEL writes; YOU check the mapping only]

The live Graph path can't be unit-hosted (no real Graph; tests today stop before Graph
steps). So extract the decision into a pure, testable helper and test that, plus the
accounting:

- AC1/AC3: a pure `ShouldSkipEntraDisable(bool? onPremisesSyncEnabled)` helper →
  true when sync enabled, false when false/null. Unit-tested both ways.
- AC2: `IsOverallSuccess(...)` (or the existing accounting refactored to a pure method) →
  success when Entra step is `SKIPPED` and the three AD/revoke steps are `OK`; failure when a
  required step is not `OK`. Unit-tested.
- AC4: `GraphTokenClient.PatchWithStatusAsync` is exercised against a stub `HttpMessageHandler`
  returning 400 with a Graph `error` body → asserts status + extracted code surfaced, no token
  in output. (Mirror any existing GraphTokenClient handler test; if none, add a minimal one.)
- AC6 (version): catalog test asserts `Version == "1.0.5"`.
- Each new test proven non-vacuous (revert the change, see red, restore).

## 9. Traceability check  [MODEL fills when iteration ends; YOU read]

All §6–§7 elements trace to an AC:
- PatchWithStatusAsync + ExtractGraphError + ExecuteDisableEntra detail → AC4.
- ReadEntraEnabledState returns onPremisesSyncEnabled from the existing pre-read → AC5.
- ShouldSkipEntraDisable branch (skip + SKIPPED step) → AC1/AC3.
- IsOverallSuccess accepts OK/SKIPPED for Entra only → AC2.
- Module version 1.0.5 + test → version rule.
No untraceable elements. No scope creep: PatchAsync kept as wrapper so the three other
callers (M365GroupManagement, NamedLocations x2) are untouched; no sync trigger added.

## 10. Review log  [MODEL appends each round]

- Round 1 (2026-06-29): Implemented. Edited `Services/GraphTokenClient.cs` (add
  PatchWithStatusAsync + ExtractGraphError; PatchAsync now a thin wrapper),
  `Services/EmergencyDisableService.cs` (pre-read returns onPremisesSyncEnabled;
  ShouldSkipEntraDisable + IsOverallSuccess pure helpers; synced users get a SKIPPED
  Entra step; non-synced PATCH failures surface Graph status+code),
  `Modules/ModuleCatalog.cs` (EmergencyDisable 1.0.4→1.0.5), and tests
  (`EmergencyDisableServiceTests.cs` +6, `GraphTokenClientTests.cs` +7). Gates: build
  Release clean (pre-existing NU1903 only); 566/566 tests green (was 553); format clean;
  git diff --check clean. Non-vacuous: reverting the OK||SKIPPED accounting turned the
  synced-success test red, restored→green. App version untouched (module-scoped change).
- Round 0 (2026-06-29): Drafted from the live-log root cause. Evidence: trace op
  `09791391fe7c4626a82ca58bc53c5ee5` (2026-06-29) — DisableAD/ResetPassword/
  RevokeEntraSessions = Success, DisableEntra = Failed. Cause: `accountEnabled` is on-prem
  mastered for synced users; direct Graph PATCH rejected. Owner chose "skip + mark N/A for
  synced users". AC4 (surface Graph error) added because the swallowed `bool` return in
  `GraphTokenClient.PatchAsync` is what prevented seeing the literal error and is a real
  diagnostic defect in a security module. No code yet — awaiting approval.
