# gm3-task5-slice5c: self-service member add/remove UI + audit/notify (GM-3 task 5, section 6.5)

**Severity**: n/a (slice-landing review, not a pre-recorded finding)
**Status**: Verified (accepted)
**Branch**: none (committed directly to master per repo policy)
**Commit**: `b461fed` (HEAD), base `fe41e14`

## Scope
Slice-landing review of the PAGE WIRING for the self-service on-prem AD group member add/remove
(GM-3 task 5, slice 5c), plus the supporting return-shape change and email method. The pure decision
core (slice 5a) and the live AD write path `SelfServiceGroupService.ChangeMemberAsync` (slice 5b) were
reviewed and accepted already; the write/reconcile body is UNCHANGED here - only its return values are
wrapped. Review covered the cumulative diff `fe41e14..b461fed` across three commits:
- `164b83a` - `ChangeMemberAsync` returns new `MembershipChangeResult` (PermissionResult + notify
  metadata: affected member SMTP/display from the SAME single resolution the write used, and the
  group's security-vs-distribution category from the re-read group). Pure `NotifyAffectedUser` gate.
  New `MembershipChangeResultTests` (8 xUnit).
- `aafc13e` - new `EmailService.SendGroupMembershipUserNotificationAsync` (affected-user email for
  security-group changes); mirrors the `Send*NotificationAsync` family, gated by `_notifyUsers`, virtual.
- `b461fed` - `Components/Pages/SelfServiceGroups.razor` Manage-members panel + `ChangeMember` handler;
  `Modules/ModuleCatalog.cs` SelfServiceGroups Version 1.0.0 -> 1.1.0.

## Review mandate
Judge 5c against: Constitution "Notifications" (highest authority) + plan AC10 - every change audits AND
admin-notifies, a user access change also notifies the affected user, notification is in addition to the
audit and never masks the operation result; owner decision B (2026-07-27) audit-first best-effort, no
background worker. Plus fail-closed authz (Known Failure Class #3), `NotifyAffectedUser` purity, no codex
F1 regression (notify metadata from the single existing resolution, not a second lookup),
UI-hiding-is-not-security (page gate is defense-in-depth only), and module-only versioning.

## Guard proof
8 new tests on the pure `NotifyAffectedUser` gate. Non-vacuity (coder-side runtime proof): inverting the
`IsSecurityGroup` term of the gate failed the distribution-group test (1 of 8); restore passed 8/8;
working tree clean. The page wiring and email method are AD/SMTP glue with no new pure-unit surface; the
existing pure cores (`MembershipChangeReconciler` 6, `AdOwnershipFilter` 21, `MembershipChangeResult` 8)
are green. Live add/remove + notify is manual-validation-on-dev (no dev tenant). Full suite 748/748,
build 0 errors, ASCII lint clean, `dotnet format --verify-no-changes` clean, `git diff --check` clean.

## Reviewer comments

Reviewer: codex (CLI, `codex-cli 0.145.0`, `codex exec` headless, default model and effort). Dispatched
read-only, static code judgment only (no dotnet execution). NOTE ON TRANSPORT: the codex-commercial MCP
route was abandoned for this slice - a first MCP dispatch timed out at the 30-min idle limit (the
NuGet-feed build-hang failure class), and a second returned `invalid` because, under a hard no-shell
constraint, the MCP reviewer had no non-shell file reader and could not fetch HEAD `b461fed` from the
connected GitHub repo. The owner directed switching to codex headless CLI, which has direct read-only
local repo access and completed cleanly. (A recurring `Failed to refresh token` line appears in the CLI
run log but did not prevent the review from running and returning a verdict.) The runtime guard proof is
the coder's and was done (see ## Guard proof); the reviewer's contribution is independent static judgment.

Verdict: **accepted**, `guard_confirmed=false` (static-only by design; runtime proof is the coder's).
reviewed_sha b461fed, base_sha fe41e14. No reopened/invalid findings.

Reviewer's confirmations:
- `SelfServiceGroups.razor:284` - ChangeMember re-checks the SelfServiceGroups policy immediately before
  the service write and uses the PrimarySid-derived callerSid, not submitted caller identity.
- `SelfServiceGroups.razor:300` - service outcomes are audited first in an isolated try/catch, then admin
  and affected-user notifications are best-effort and do not alter changeResult.
- `SelfServiceGroups.razor:289` - authorization-denial and exception paths fail closed and write audit
  records; the catch block also audits the failed attempt.
- `SelfServiceGroupService.cs:343` - already-satisfied success returns MembershipChanged=false; confirmed
  write success returns MembershipChanged=true and carries member notify metadata from the single
  resolved member plus security-group status from the re-read group.
- `MembershipChangeResult.cs:39` - NotifyAffectedUser is pure and requires success, real membership
  change, security group, and non-blank affected-user email.
- `EmailService.cs:369` - new affected-user group-membership notification method is virtual, uses the
  shared EmailService path, and is gated by the existing user-notification switch.
- `ModuleCatalog.cs:343` - SelfServiceGroups module version bumped to 1.1.0; no base app version bump in
  the reviewed diff.

Orchestrator (coder) acceptance decision: envelope well-formed and SHA-consistent with the reviewed range
(base fe41e14, HEAD b461fed, covering all three sub-commits); verdict accepted with no material issue; the
mandatory notification/audit contract, fail-closed authz, gate purity, no-F1-regression, and module-only
versioning all confirmed; non-vacuity carried by the coder-side runtime guard proof. Accepted.
