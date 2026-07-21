# ConferenceRooms Finder Protected-Principal Gate — Plan

Status: Implemented (2026-07-21, commit 2a97d09). Approach: C2-G (guarded-execution
helper), recommended and accepted by gpt-5.6 review (effort max), owner-approved
2026-07-20. OD-1 resolved. Prior page-seam design (Options A/B) reviewed & accepted
codex xhigh round 3, then superseded by the owner's shared-code/no-double-check
direction.

Implementation notes: `ConferenceRoomProtectionGate` (new, module-scoped) is the
single enforcement point; page Finder/Type and each bulk row route through
`GuardThenRunAsync`; the two prior inline checks were removed. 672 tests pass;
non-vacuity verified (bypassing the denial branch fails 9 tests). No live-tenant/UI
validation (no dev tenant) — deferred to a future controlled run.
App version at draft: 2.3.28 (unchanged — module-scoped change)
Module: `ConferenceRooms` (Version `2.2.0` → `2.3.0`)
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`,
`docs/AdminModuleSpec.md`. On conflict the higher source wins.
Finding record: `.agents/review/findings/pp-finder-1.md` (independently confirmed
real, codex xhigh, 2026-07-20).

## Problem / Goal

The single-room Finder/metadata path on the ConferenceRooms page
(`Components/Pages/ConferenceRooms.razor` `SetupSingleRoom`) reauthorizes and then
writes room metadata via `RoomService.SetRoomMetadataAndListAsync` with **no
protected-principal check**. The sibling single-room Type path
(`SetSingleRoomType`) gates the write through `CheckProtectedPrincipalAsync` before
`RoomService.SetRoomTypeAsync`. A metadata write is a mutating action against the
target identity, so per the 2026-06-29 decision ("protected principals are
off-limits to every mutating module, no carve-outs";
`.agents/decisions.md`, `docs/ProjectConstitution.md` §Protected Principals) the
Finder path must gate the target before the write. This is the same class as the
now-closed GAP 3 (ConferenceRooms Finder bulk path), on the single-room UI path
that was outside the Bulk Job Runner's approved scope.

Goal: gate the single-room Finder metadata write through the protected-principal
check before `SetRoomMetadataAndListAsync` runs, fail-closed, with a correctly
labeled denial audit; add automated proof that a protected target is not written.

## Non-Goals

- No change to the Type path (already gated), the bulk Finder/Type CSV paths
  (already gated in the job processor — GAP 3 closed), room-list building, calendar
  permissions, or any other ConferenceRooms behavior.
- Not a new module; extends existing `ConferenceRooms`.
- No cloud-identity protection extension. The check resolves against on-prem AD;
  a target AD cannot resolve returns `NotFound` and is treated as not protected —
  the same accepted, documented limitation as GroupManagement / M365 / Migration.
  Rooms are typically resource mailboxes; this gap is accepted, not a defect.
- No change to what a *successful* (non-protected) metadata write does.

## Chosen approach (owner, 2026-07-20): gate in shared code, NO double-check

The owner chose to gate in the shared code path rather than duplicate the check in
the page, and explicitly ruled out leaving a redundant check: **consolidate the
protected-principal gate so every ConferenceRooms room-mutating path is protected
by ONE enforcement site, with no path checked twice.**

### The duplication this must resolve

The identical protected-principal check currently exists as **three** near-copies:

1. **Page** — `Components/Pages/ConferenceRooms.razor:1176-1210`
   `CheckProtectedPrincipalAsync`. Used by the single-room **Type** path (`:938`).
   **NOT** used by the single-room **Finder** path (`SetupSingleRoom` → the gap this
   plan closes).
2. **Bulk processor** — `Services/Jobs/ConferenceRoomBulkProcessor.cs:172-207`
   its own `CheckProtectedPrincipalAsync`, called per row for **both** Finder and
   Type before the write (`:87-89`), closing GAP 3 for bulk.
3. Both ultimately call the same **shared write methods** on `ConferenceRoomService`
   (`SetRoomMetadataAndListAsync` `:360`, `SetRoomTypeAsync`) via the
   `IConferenceRoomBulkOperations` seam.

The single-room Finder path is the one write path with no gate. Naively adding the
gate to `SetRoomMetadataAndListAsync` would fix it but would then run the check
**twice** on every bulk Finder/Type row (once in the processor, once in the
service) — the redundancy the owner rejected. So the fix must consolidate, not
just add.

### Resolved approach — C2-G (guarded-execution helper), reviewer-recommended

OD-1 was delegated to the gpt-5.6 review (owner, 2026-07-20). Verdict: **accepted**,
recommending **C2-G** — a module-scoped *guarded-execution* variant of C2. Candidate
shapes C1/C2/C3 are retained below as history; C2-G is what the plan now specifies.

**C2-G — one ConferenceRooms-scoped guard that checks once and executes the write
delegate only when allowed.** Introduce a single module-scoped guard unit (a small
`ConferenceRoomProtectionGate` type, or a method group; **not** added to shared
`ProtectedPrincipalService` — see the version caveat) with an executor shaped like:

```
Task<TResult> GuardThenRunAsync<TResult>(
    string identity,
    Func<ProtectionDenial, TResult> onDenied,   // caller builds its own audited result
    Func<Task<TResult>> onAllowed)              // caller's real write; trace opens INSIDE here
```

and a `ProtectionDenial` carrying the reason + the fail-closed status so each caller
audits with its own action label and context. Behavior: resolve + check **once**;
on protected / `Unavailable` / `Ambiguous` / `CheckFailed` / exception, call
`onDenied` and **never** call `onAllowed`; otherwise call `onAllowed`.

Why C2-G over the alternatives (reviewer reasoning, recorded in
`.agents/review/findings/pp-finder-1.md` round 4):

- **Ordering — the decisive point.** C1/C3 put the gate inside the service write
  methods, which are reached only *after* the caller's trace scope has already opened
  (`ConferenceRooms.razor:700`, `:941`; `ConferenceRoomBulkProcessor.cs:93`). That
  violates "gate before any side effect". C2-G has the caller open its trace scope
  **inside** the `onAllowed` delegate, so the check genuinely precedes every side
  effect (Known Failure Class #1) — without threading job context into
  `ConferenceRoomService`.
- **Audit stays with the caller.** The bulk processor keeps its `_Bulk` per-row
  denial audit with the captured job actor/ip/ticket; the page callers pick
  `AuditFinderAction` (`ConferenceRooms_SetMetadata`) vs `AuditTypeAction`
  (`ConferenceRooms_SetType`) from the `ProtectionDenial`. Each caller returns
  immediately after its single denial audit — no duplicate generic-result audit.
- **One implementation, structurally enforced single-check.** Because the write is
  only reachable *through* `GuardThenRunAsync`, a path cannot perform the write
  without passing the gate — this closes C2's "future fourth path forgets to call
  it" weakness, since the write delegate is not invoked except by the guard.

Candidate shapes (history — superseded by C2-G):

- **C1** — gate inside shared service write methods, delete the processor's check.
  Rejected: no job context for the per-row bulk audit; gate runs after trace opens.
- **C2** — shared helper called once per path, service check-free. C2-G is C2 plus
  guarded execution so the single-check is structural, not by discipline.
- **C3** — service gate + threaded audit context. Rejected: adds audit-context
  surface to the hot-path write methods; same after-trace ordering flaw as C1.

### Invariants any chosen approach must satisfy (not open)

1. **Exactly one enforcement site per write path.** No path checked zero times
   (the current Finder gap) and no path checked twice.
2. **Fail-closed ordering.** The gate is fully decided before any side effect
   (EXO `Set-Place`, AD writes, trace, notification). A protected/Unavailable/
   Ambiguous/CheckFailed/exception target never reaches the write (Known Failure
   Class #1).
3. **Audit preserved and correctly labeled.** A single-room Finder denial audits as
   `ConferenceRooms_SetMetadata`; single-room Type as `ConferenceRooms_SetType`;
   bulk rows keep their `_Bulk`-suffixed per-row denial audit with the captured
   job actor/ip/ticket. No denial goes unaudited; none is mislabeled. (Note: the
   page's current `CheckProtectedPrincipalAsync` ignores its `action` param and
   hardcodes `AuditTypeAction` → `ConferenceRooms_SetType` at `:1186,1194,1199,1207`
   — whatever approach is chosen must not carry that mislabel onto the Finder path.)
4. **`NotFound ⇒ allow`** (cloud-only target AD cannot resolve) — accepted,
   documented limitation, consistent with the other gated modules.

## Tests — `ExchangeAdminWeb.Tests/` (C2-G)

Tests exercise the **production `GuardThenRunAsync` executor** with a spy write
delegate — not a separate decision helper (the round-4 non-vacuity requirement):

- Protected target ⇒ the spy write delegate is **not** invoked; `onDenied` result
  returned. Assert on all three surfaces: single-room Finder (the finding),
  single-room Type, and each bulk row path (via the existing fake
  `IConferenceRoomBulkOperations` in `ConferenceRoomBulkProcessorTests`).
- Clear, resolved, non-protected target ⇒ write delegate invoked **once**.
- `NotFound` ⇒ write delegate invoked (allow).
- `Unavailable` / `Ambiguous` / `CheckFailed` / thrown exception ⇒ denied, write
  delegate **not** invoked (fail-closed) — per branch or parameterized.
- Denials audit with the correct per-path action label: Finder ⇒
  `ConferenceRooms_SetMetadata`, Type ⇒ `ConferenceRooms_SetType`, bulk ⇒ the
  `_Bulk`-suffixed action with captured job actor/ip/ticket.
- **No regression + single-check:** existing `ConferenceRoomBulkProcessorTests`
  protected-target tests still pass, using the **real** gate plus fake room ops, and
  assert **exactly one** protection evaluation per row (the anti-double-check proof).
- **Non-vacuity:** removing the denial branch from `GuardThenRunAsync` (so the write
  delegate is invoked even for a protected target) must make a "protected ⇒ write not
  invoked" test FAIL with an assertion failure (not a compile error); restore ⇒ PASS.

> Test seam note: the real `Set-Place`/EXO + AD writes are not unit-tested (no new PS
> seam introduced); spy/fake room-ops stand in. Tests assert the gate decision,
> ordering, and audit label. Live Blazor UI + real tenant write remain deferred
> (no dev tenant — `.agents/state.md`); do not claim UI/tenant validation not run.

## Version — `Modules/ModuleCatalog.cs`

- Bump `ConferenceRooms` `Version` `2.2.0` → `2.3.0` (single-module,
  security-relevant behavior change ⇒ module minor bump). **App `<VersionPrefix>`
  does NOT change** — confirm no shared/app-wide code semantics change beyond the
  gate consolidation (two-rule policy, `docs/ProjectConstitution.md` §Deployment And
  Versioning). Update any test pinning the `ConferenceRooms` module version.
- **C2-G version caveat (reviewer):** the no-app-bump holds **only if the guard stays
  ConferenceRooms-scoped** (its own type/method group). If implementation instead
  adds the combined guarded-execution operation onto shared `ProtectedPrincipalService`,
  that is a shared-infrastructure change and the app-version question reopens — do not
  do that without re-checking the versioning rule.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release` then
  `dotnet test ExchangeAdminWeb.slnx` (target `.slnx`; bare `dotnet test` runs zero
  tests).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` and
  `git diff --check HEAD`.
- New tests proven non-vacuous (revert/restore).
- Manual (deferred, no dev/QA AD/Exchange tenant — `.agents/state.md`): on a real
  tenant, submit the single-room Finder form for a protected target and confirm the
  write is blocked, the denial is shown, and an audit row exists labeled
  `ConferenceRooms_SetMetadata`. State clearly this was not run if it was not.

## Resolved decisions

1. Gate in shared code, consolidated to exactly one enforcement per write path, no
   double-check (owner, 2026-07-20).
2. Reuse the existing on-prem-AD protected-principal check; cloud-only `NotFound`
   gap accepted and documented (consistent with the other gated modules).
3. Fail-closed: `Unavailable` / `Ambiguous` / `CheckFailed` / exception ⇒ deny
   (nothing written).

## Open decisions

- **OD-1 — consolidation shape: RESOLVED to C2-G** (gpt-5.6 review, accepted,
  2026-07-20; owner delegated the design call to the review). See "Resolved approach"
  above. No open design decision remains.
- **Remaining owner gate:** this plan is reviewed and accepted but **not yet
  owner-approved for implementation**. Code is blocked until the owner gives an
  explicit go. (The owner delegated the *shape* decision to the reviewer, not the
  decision to start writing code.)

## Commit slices (one per landed slice, per AGENTS.md Git Safety)

Exact slices finalize once OD-1 is resolved. Expected shape:

1. Introduce the C2-G `ConferenceRoomProtectionGate.GuardThenRunAsync` guard; route
   all three surfaces through it (single-room Finder — closing the gap; single-room
   Type; bulk processor per row), removing the two now-redundant inline checks so the
   gate runs exactly once per write, with correct per-path denial audit labels, + the
   unit tests. One finding fix, lands together. Do not alter the already-correct
   Type/bulk *write* behavior — only the gate topology and the Finder gap.
2. Module version bump `2.2.0`→`2.3.0` + catalog test update + docs (this plan →
   Implemented; `.agents/state.md` single-room Finder gap closed;
   `.agents/decisions.md` if a durable decision beyond the 2026-06-29
   protected-principal decision is warranted — the consolidation topology likely
   qualifies).
