# Commit Review - 2026-06-17

Review mode: REVIEW_ONLY

Reviewed committed range: `f3f9eb7..dafc040`

Scope: commits authored on 2026-06-17 through `dafc040` (`Close out ProdReadiness AC16 and the work stream`). This review intentionally excludes the current uncommitted TestAccountPool-removal worktree changes.

## Findings

### High - Room Finder can partially mutate EXO metadata before the new AD step fails

`Services/ConferenceRoomService.cs:360` runs `Set-Place` and writes Building/Capacity/Floor/device metadata before the code validates the new `Set-ADUser` prerequisites at `Services/ConferenceRoomService.cs:390`. The AD helper only maps/validates country and resolves the ConferenceRooms AD credential afterward at `Services/ConferenceRoomService.cs:1189` and `Services/ConferenceRoomService.cs:1199`.

Failure path: a row with an unmappable country, missing `DelineaSecretId`, unavailable PAM credential, ambiguous/missing AD object, or AD write failure will be reported as failed, but `Set-Place` has already committed the EXO-side metadata. The plan says room-list membership remains gated behind successful required metadata and that all-or-nothing behavior is unchanged unless the owner approves partial apply. This path creates a partially applied row without surfacing it as partial.

Suggested fix: preflight everything that can be checked without mutation before `Set-Place`: build the synced AD attributes, validate country, verify the AD credential is configured, and ideally resolve the AD object to exactly one objectGUID. Then perform the mutating steps. If full preflight is too expensive per row, the result should explicitly report partial success and audit the already-applied `Set-Place` state.

### Medium - GroupManagement protected-principal denials can still return without audit

The service now enforces the protected-principal check, which is the right direction, but `Components/Pages/GroupManagement.razor` still performs the older page-level `@`-gated check before calling the service. On `ResolutionStatus.Unavailable` or `Ambiguous`, Add returns at `Components/Pages/GroupManagement.razor:281`; Remove returns at `Components/Pages/GroupManagement.razor:364`. The catch paths also return without audit at lines 304 and 387.

Those are failed user-facing mutation attempts, but they bypass the later audited service call at lines 308/391 and the audit write at lines 310/393. The Constitution requires every successful or failed user-facing mutation to write an audit event. This means the new service-level gate fixed non-page/non-`@` callers, but the page can still produce unaudited failed mutation attempts for common UPN/email inputs when protection resolution is unavailable or ambiguous.

Suggested fix: remove the duplicated page-level protected-principal gate and let the service return the denial so the existing post-service audit path records it, or add audit writes to every early-return denial branch. The first option avoids future drift between the page and service gates.

## Notes

- I did not rerun build/test for this review. The worktree was dirty with uncommitted TestAccountPool-removal changes, so local verification would not represent the reviewed committed range.
- I did not find a blocker in the deny-by-default fallback policy, SectionAccess cache invalidation, AD allowlist corruption gate, PowerShell `icacls` checks, Delinea test-script sanitization, or audit category filing changes during this pass.

## Resolution (2026-06-18)

Both findings fixed in dev (owner-directed). Behavior of each fix restores the *documented*
intent, so neither required a new plan.

- **High (Room Finder partial mutation): FIXED.** `ConferenceRoomService` now runs a
  non-mutating AD preflight (`PreflightSyncedAttributesViaAdAsync`) before `Set-Place` —
  country mapping, credential availability, and resolving the AD object to exactly one
  ObjectGUID. A row with bad AD prerequisites now fails before any EXO write. Object
  resolution is shared with the write via a new `ResolveAdObjectGuid` helper so the two
  cannot drift. Residual: a genuine `Set-ADUser` failure after a passing preflight is still
  possible (two-system writes are not atomic) — inherent and accepted, as the finding noted.
  ConferenceRooms module 2.0.8 → 2.0.9.
- **Medium (GroupManagement unaudited denials): FIXED.** Removed the duplicated page-level
  protected-principal gate in `GroupManagement.razor` (Add and Remove). Enforcement now lives
  solely in `GroupManagementService` (`CheckProtectedAsync`), which fails closed on
  Unavailable/Ambiguous resolution and covers the non-`@` identities the page gate missed;
  every denial flows through the single audited post-service path. Guarded by the existing
  `GroupManagementServiceTests` (proven non-vacuous). GroupManagement module 2.0.1 → 2.0.2.
