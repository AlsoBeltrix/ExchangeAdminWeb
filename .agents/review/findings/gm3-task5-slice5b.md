# gm3-task5-slice5b: member add/remove with pre-write re-checks (GM-3 task 5, section 6.5)

**Severity**: n/a (slice-landing review, not a pre-recorded finding)
**Status**: Verified (accepted after two fixes)
**Branch**: none (committed directly to master per repo policy)
**Commit**: `5ef1b0d` (HEAD after fixes), base `6fd722f` (slice-landing commit)

## Scope
Slice-landing review of the live self-service member add/remove write path (GM-3 task 5,
slice 5b): the credentialed AD glue around the pure decision core reviewed in slice 5a.

File:
- `Services/SelfServiceGroups/SelfServiceGroupService.cs` -- `ChangeMemberAsync` (~L241),
  `CheckMemberProtectedAsync` (~L356), `ResolveUserMember` (~L445). Resolves the affected
  member with this module's SelfServiceGroups credential through the bound, RFC 4515-escaped
  USER-ONLY LDAP filter; runs fresh group/ACL eligibility + protected-principal checks;
  writes via Add-/Remove-ADGroupMember; reconciles with a post-write read-back.

## Review mandate
Judge against plan `docs/SelfServiceGroupManagement-Plan.md` section 6.5 and the invariants:
fail-closed authorization/eligibility/protected-principal on every write (Known Failure
Class #3); idempotent desired-state + post-write reconciliation (Known Failure Class #2);
injection-safe AD resolution (RFC 4515 escaping, bound -LDAPFilter, no PowerShell string
interpolation of identity, codex F11); credential isolation; USER-ONLY members (codex F7).

## Round 1 (reopened) -- base 6fd722f
The first dispatch on the slice-landing commit `6fd722f` REOPENED with two findings:
- **F1**: the protected-principal check resolved a SEPARATE lookup of the raw identity
  (DirectoryRead credential, untrimmed, NotFound-> allowed) than the write target
  (SelfServiceGroups credential, trimmed, person-bound). The checked principal was not
  guaranteed to be the written one -- a check/write mismatch (Known Failure Class #3).
- **F2**: the write `ps.Invoke()` sat outside any reconciliation catch. With
  `ErrorAction=Stop`, a terminating error AFTER commit could skip the read-back and return
  no `PermissionResult` -- an ambiguous write not failed closed (Known Failure Class #2).

## Fixes (one finding per commit)
- `246a197` (F1): resolve the affected member EXACTLY ONCE via new `ResolveUserMember`
  (SelfServiceGroups credential, trimmed input, `BuildUserByIdentityFilter` person/user
  bound, exactly-one-match + non-blank DN required, fail-closed on none/ambiguous/error)
  into a `ResolvedDirectoryPrincipal`; run `ProtectedPrincipalService.CheckAsync` on THAT
  principal; write to THAT principal's captured DistinguishedName. Removed the old
  `ResolveUserMemberDn` and the string-based `CheckMemberProtectedAsync`.
- `5ef1b0d` (F2): capture the write exception (`writeError`), clear streams, then run a
  GUARDED post-write read-back; success is decided ONLY by `IsDesiredStateReached`, never
  by absence of the throw; a read-back exception returns Fail (fail-closed).

## Guard proof
Both fixes are credentialed AD glue with no new pure-unit surface. The pure decision core
(`MembershipChangeReconciler`, 6 tests) and the filter builder (`AdOwnershipFilter`, 21
tests) are unchanged and green. Coder-side runtime verification on HEAD `5ef1b0d`: build
0 errors, ASCII lint clean, `dotnet format --verify-no-changes` clean, `dotnet test` on
the .slnx 740/740 pass. The live AD write path is manual-validation-on-dev (no dev tenant).

## Reviewer comments

Reviewer: codex-commercial (MCP transport), default model and effort. Dispatched
read-only, static code judgment only (no dotnet execution) -- the mode established for
task 4 after the sandbox's blocked NuGet feed killed build-running dispatches. The runtime
guard proof is the coder's and was done (see ## Guard proof); the reviewer's contribution
is independent static judgment of the cumulative diff `6fd722f..5ef1b0d`.

Round 2 verdict: **accepted**, `guard_confirmed=false` (static-only by design; runtime
proof is the coder's). reviewed_sha 5ef1b0d, base_sha 6fd722f. No reopened/invalid findings.

Reviewer's confirmations:
- `SelfServiceGroupService.cs:264` -- F1 complete: trimmed input resolved once with the
  SelfServiceGroups credential through the bound, RFC 4515-escaped person/user-only filter;
  non-singleton results, missing DN, and exceptions fail closed; that same
  ResolvedDirectoryPrincipal is passed to CheckAsync and its captured DistinguishedName
  feeds the membership checks and both write commands; no second raw-identity lookup remains.
- `SelfServiceGroupService.cs:355` -- F2 complete: the write Invoke is caught and cleared
  before a guarded post-write read-back; Ok is reachable only after IsDesiredStateReached;
  a write exception neither proves failure nor success; a read-back exception returns Fail;
  the separate AlreadySatisfied Ok path is an idempotent no-write result from the pre-write
  state read.
- `SelfServiceGroupService.cs:491` -- the flow preserves USER-ONLY resolution, bound
  PowerShell parameters, RFC 4515 escaping, module-credential isolation, fresh group/ACL
  eligibility checks, and fail-closed protected-principal enforcement.

Orchestrator (coder) acceptance decision: envelope well-formed and SHA-consistent with the
reviewed range (base 6fd722f, HEAD 5ef1b0d, covering both fix commits); verdict accepted
with no material issue; both reopened findings independently confirmed addressed;
non-vacuity carried by the unchanged pure-unit suites plus the coder-side runtime proof.
Accepted.
