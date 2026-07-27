# gm3-task5-slice5a: membership change decision core (GM-3 task 5, section 6.5)

**Severity**: n/a (slice-landing review, not a pre-recorded finding)
**Status**: Verified (accepted)
**Branch**: none (committed directly to master per repo policy)
**Commit**: `08a2a53` (slice), base `ed65be8`

## Scope
Slice-landing review of `git show 08a2a53` (GM-3 task 5, slice 5a: the PURE decision core
for the self-service member add/remove write). No AD access, no service call - pure logic.

Files:
- `Services/SelfServiceGroups/MembershipChangeReconciler.cs` (new) - pure, AD-free.
  `PlanWrite(op, present)`: idempotent desired-state (add-if-absent / remove-if-present).
  `IsDesiredStateReached(op, presentAfter)`: post-write read-back reconciliation.
  Enums `MembershipOperation` and `MembershipWriteAction`.
- `ExchangeAdminWeb.Tests/MembershipChangeReconcilerTests.cs` (new) - 6 xUnit tests.

## Review mandate
Judge against plan `docs/SelfServiceGroupManagement-Plan.md` section 6.5. Two contracts:
- Idempotent desired-state (add-if-absent / remove-if-present) so a retry is a safe no-op.
- Post-write read-back reconciliation (codex F10 / Known Failure Class #2): a write is
  "success" only when the group actually reached the requested end state; a
  timed-out-but-uncommitted write must NOT read as success.
Plus: the type must stay genuinely pure (no AD/IO/PowerShell/DirectoryServices coupling)
so it is unit-testable without a DC; an unknown operation throws (fail-closed spirit).

## Guard proof
6 tests. Non-vacuity (coder-side runtime proof): inverting `PlanWrite`'s Add branch fails
2 tests; inverting `IsDesiredStateReached`'s Add branch fails 1 test; restore passes 6/6;
working tree clean.

## Reviewer comments

Reviewer: codex-commercial (MCP transport), default model and effort. Dispatched
read-only, static code judgment only (no dotnet execution) - the mode established for
task 4 after the sandbox's blocked NuGet feed killed two build-running dispatches. The
runtime guard proof is the coder's and was done (see ## Guard proof); the reviewer's
contribution is independent static judgment of the diff against section 6.5.

Verdict: **accepted**, `guard_confirmed=false` (static-only by design; runtime proof is
the coder's). reviewed_sha 08a2a53, base_sha ed65be8. No reopened/invalid findings.

Reviewer's confirmations:
- `MembershipChangeReconciler.cs:45` -- PlanWrite implements all four required
  desired-state outcomes; both switches throw on unknown operations.
- `MembershipChangeReconciler.cs:58` -- reconciliation correctly oriented (Add requires
  presence, Remove requires absence); deterministic, no AD/IO/PowerShell/DirectoryServices
  coupling.
- `MembershipChangeReconcilerTests.cs:17` -- all four PlanWrite combinations pinned;
  inverting the Add branch would fail both Add tests.
- `MembershipChangeReconcilerTests.cs:46` -- both post-write states pinned per operation;
  inverting Add reconciliation would fail. No tests were executed.

Orchestrator (coder) acceptance decision: envelope well-formed and SHA-consistent with the
reviewed slice; verdict accepted with no material issue; non-vacuity independently proven
by the coder-side runtime guard proof. Accepted.
