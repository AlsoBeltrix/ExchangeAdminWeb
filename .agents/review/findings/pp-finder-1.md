# pp-finder-1: Single-room Finder metadata path has no protected-principal check

**Severity**: HIGH — a protected principal (mailbox on the protected-principals list) can have its metadata rewritten through the single-room Finder page with no authorization block; the sibling Type path blocks the same action.
**Status**: Verified (finding confirmed real by independent review; fix not yet written)
**Branch**: (none yet — pre-code validation of the finding)
**Commit**: (n/a)

## Evidence
- `Components/Pages/ConferenceRooms.razor:669-717` — `SetupSingleRoom()`: validates the ServiceNow ticket, calls `ReauthorizeAsync()` (L688), then writes via `RoomService.SetRoomMetadataAndListAsync(...)` (L702). No call to `CheckProtectedPrincipalAsync` anywhere on this path.
- `Components/Pages/ConferenceRooms.razor:929-939` — the Type path: after `ReauthorizeAsync()` (L929) it calls `CheckProtectedPrincipalAsync(email, ticket, "ConferenceRooms_SetType")` (L938) and returns early if it yields a block (L939) BEFORE `RoomService.SetRoomTypeAsync(...)` (L942).
- `Components/Pages/ConferenceRooms.razor:1176-1205` — `CheckProtectedPrincipalAsync` is the existing page-local guard: resolves identity, fails closed on Unavailable/Ambiguous, and blocks when `check.IsProtected`.

## Predicted observable failure
Submitting the single-room Finder/metadata form with a room email that resolves to a protected principal performs the metadata write (`SetRoomMetadataAndListAsync`) instead of being blocked. Detectable by a unit test on the page path (or a manual run) asserting that a protected target is NOT written on the Finder path — mirroring the existing Type-path protected-target test in `ConferenceRoomBulkProcessorTests` / any single-room Type test.

## What
The single-room metadata ("Finder") write path omits the protected-principal gate that the parallel Type path enforces. This is the same authorization-gap class as the now-closed GAP 3 (ConferenceRooms Finder bulk path), but on the single-room UI path, which was outside the Bulk Job Runner's approved scope and therefore flagged, not fixed. Governing rule: `.agents/decisions.md` 2026-06-29 + `docs/ProjectConstitution.md` §Protected Principals — every mutating path must route its write target through the protected-principal check before writing.

## Approach
(pre-code — proposed fix, not yet implemented) Insert a `CheckProtectedPrincipalAsync(email, ticket, "ConferenceRooms_SetMetadata")` call immediately after `ReauthorizeAsync()` at L688, returning early on a non-null result exactly as the Type path does at L938-939, before the metadata write at L702.

## Files changed
(none yet)

## Guard proof
(pre-code) Planned: a test asserting the Finder path does not write for a protected target; reverting the inserted check makes it FAIL, restoring makes it PASS.

## Coder dispute (if any)
None — the coder raised this finding.

## Known gaps for the reviewer to grade explicitly
1. Is HIGH the right severity given rooms are non-person mailboxes and rarely protected? (practical likelihood low; authorization-invariant violation is the impact.)
2. `CheckProtectedPrincipalAsync` internally calls `AuditTypeAction` for its denial audits (L1186, L1194, L1199). Reused verbatim on the Finder path, a denial would be audited under "Type" action semantics rather than "Finder/SetMetadata". Is that an acceptable minor audit-labeling imprecision, or should the fix also parameterize the audit sink so Finder denials audit as Finder actions?
3. Are there any OTHER single-room write paths on this page (beyond SetupSingleRoom and the Type path) that also mutate a room and should be checked for the same gap?

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs (inline, session-only) / xhigh / standard`
- Harness: codex-cli 0.144.6 · reviewed_sha=base_sha=`27d7918` · guard_confirmed=false (pre-code, no diff to prove) · **verdict: accepted** · 2026-07-20 ~17:57 UTC · dispatch exit 0, schema-valid `--output-last-message` payload (`.agents/review/pp-finder-1.result.json`). Note: the `--json` event stream was piped to Out-Null this run, so the per-event session-id transcript line was not retained; evidence of dispatch is exit 0 + the schema-conforming result file.

Verdict mapping (pre-code): accepted = the finding is real and correctly analyzed.

Confirmed:
- Point 1 — `ConferenceRooms.razor:688,702` + `Services/ConferenceRoomService.cs:360,405`: `SetupSingleRoom` reauthorizes then writes via `SetRoomMetadataAndListAsync` with no PP gate; the service method (`Set-Place` at ~L405) provides no PP check either, so the page omission is uncovered.
- Point 2 — `ConferenceRooms.razor:938-942`: Type path gates correctly (checks then returns before `SetRoomTypeAsync`).
- Point 3 — `ConferenceRooms.razor:1180,1191,1195,1200,1204-1208`: `CheckProtectedPrincipalAsync` is the correct existing guard; fails closed on Unavailable/Ambiguous, blocks on CheckFailed/IsProtected, catches+blocks exceptions.

### Round 4 — consolidation-plan review (owner-requested design weigh-in)

`Reviewer: codex-commercial (MCP) / gpt-5.6-sol / max / frontier`  `escalated: owner (frontier force)`
- Transport: MCP thread `019f8137-963d-7be0-ae88-510990d5c5e9` · reviewed_sha=base_sha=`27d7918` · guard_confirmed=false · **verdict: accepted** · 2026-07-20 (overnight) · schema-valid payload returned in MCP result envelope.
- Owner delegated the OD-1 consolidation-shape decision to this review (owner, 2026-07-20). Two prior CLI dispatches of this same review failed on wrapper arg-mangling; re-run over the newly-registered `codex-commercial` MCP server.
- **OD-1 recommendation: C2-G** — a module-scoped *guarded-execution* variant of C2. Check protected-principal **once**, return a structured denial/audit detail, and invoke a supplied **write delegate** only when allowed; each caller keeps its own audit context + label. Key points:
  - **Ordering (the C1/C3 flaw it fixes):** C1/C3 as written would reach a service-level gate only *after* the page/processor trace scope has already begun (`ConferenceRooms.razor:700`/`941`, `ConferenceRoomBulkProcessor.cs:93`). C2-G places trace creation *inside* the allowed-write delegate, so the gate is genuinely before every side effect (Known Failure Class #1) without threading job context into `ConferenceRoomService`.
  - **Audit stays with the caller:** processor keeps its `_Bulk` per-row denial audit with captured job actor/ip/ticket; page callers select `AuditFinderAction` vs `AuditTypeAction` from the structured denial; return immediately after the single denial audit (no duplicate generic-result audit).
  - **Non-vacuity:** tests must exercise the *production* C2-G executor with a spy write delegate (protected/fail-closed ⇒ delegate uncalled; allowed/NotFound ⇒ called once; removing the denial branch ⇒ assertion failure). Existing bulk tests use the real gate + fake room ops and assert exactly one protection evaluation per row.
  - **Version caveat:** `2.2.0→2.3.0` no-app-bump holds **only if the helper stays ConferenceRooms-scoped**; adding the combined operation onto shared `ProtectedPrincipalService` would be shared-infrastructure and reopen the app-version question.
- Also confirmed: two-commit slicing consistent with one-item-per-commit.
- **Plan updated 2026-07-20** to adopt C2-G as the resolved OD-1 approach (docs only). Implementation of code remains gated on explicit owner go.

### Round 3 — revised-plan re-review (repair-delta)

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs (inline, session-only) / xhigh / standard`  `escalated: T5`
- codex-cli 0.144.6 · reviewed_sha=base_sha=`27d7918` · guard_confirmed=false · **verdict: accepted** · 2026-07-20 ~18:15 UTC · exit 0, schema-valid (`.agents/review/pp-finder-plan-r3.result.json`).
- All four round-2 reopen points confirmed closed: (1) guarded-orchestration seam routes the real write through an observable delegate; tests assert the write delegate is NOT invoked for protected/fail-closed targets; (2) `NotFound ⇒ allow` explicitly covered; (3) non-vacuity instruction now targets the runtime gate branch requiring an assertion failure; (4) commit slice kept to one finding fix, no Type-path expansion. Previously-accepted parts (gate ordering, audit-label scoping, versioning 2.2.0→2.3.0 no app bump, verification) intact. OD-1 (Option A vs B) correctly left as the owner gate.

### Round 2 — plan review (`docs/ConferenceRoomsFinderProtectedPrincipalGate-Plan.md`)

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs (inline, session-only) / xhigh / standard`  `escalated: T5` (reopen escalates on redispatch)
- codex-cli 0.144.6 · reviewed_sha=base_sha=`27d7918` · guard_confirmed=false · **verdict: reopened** · 2026-07-20 ~18:05 UTC · exit 0, schema-valid (`.agents/review/pp-finder-plan.result.json`).
- Confirmed correct: gate placement + fail-closed ordering; audit-mislabeling description matches code (`CheckProtectedPrincipalAsync` ignores `action`, hardcodes `AuditTypeAction`→`ConferenceRooms_SetType`); audit fix sound and won't change Type label; versioning `2.2.0`→`2.3.0`, no app bump, correct; verification commands + deferred manual check correct.
- **Reopen reason (valid):** Option A's *pure decision seam is vacuous* — if the gate call in `SetupSingleRoom` is later removed but the helper stays, decision-only tests still pass. Fix: route the write through an observable *guarded-orchestration* seam and assert the write delegate is NOT invoked for protected/fail-closed targets; the non-vacuity revert must target the gate call, not the helper.
- Smaller fixes: add explicit `NotFound ⇒ allow` test case; tighten non-vacuous instruction to the gate call; commit-slice note (gate+audit-label = one fix, don't expand into Type-path changes).
- **Plan revised 2026-07-20** to address all four (guarded-orchestration seam in Option A + §1; observable write-delegate assertions + NotFound case + gate-targeted non-vacuity in §4; slice note). Round 3 = repair-delta re-review of the revised plan.

Known-gap gradings (round 1):
1. **Severity HIGH is defensible** (Constitution forbids any mutating op against a protected principal; fail-closed required), **not Critical** (targets are room mailboxes → lower practical likelihood). Matches the coder's grading.
2. **Confirmed, and sharper than raised:** `CheckProtectedPrincipalAsync` **ignores its `action` parameter entirely** — denial audits hardcode via `AuditTypeAction` (L1186/1194/1199/1207), which itself hardcodes `ConferenceRooms_SetType` (~L1220). Reused verbatim on Finder, denials would be mislabeled as Type actions. Reviewer: the fix should make the guard honor the passed `action` (or route to Finder/Type audit helpers) — an audit-labeling correction, not a reason to pick a different guard.
3. **No other single-room page gap.** The only direct page `RoomService` writes are metadata (L702) and type (L942); the second UI handler `SetSingleRoomType` (~L359) drives the already-gated type write. `ApplyFinderCsv`/`ApplyTypeCsv` (~L881) enqueue durable jobs whose processor applies its own PP gate per row, so they are not additional page gaps.
