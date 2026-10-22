# aud-a28p: The protected-principal check reads the legacy ExcludedUsers list fail-open after its corruption probe

**Severity**: MEDIUM - a fail-open read on the protection gate (Known Failure Class 3: an
authorization store must deny when a read fails). `ProtectedPrincipalService.GetLegacyExclusions`
reads the Mailbox Permissions module configuration's `ExcludedUsers` list after the corruption probe
has passed; a read failure between the probe and this read returns an empty list, so the principals
that list protects are un-protected for that check and a write against them goes through.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28n/a26k agent, 2026-10-22, candidate 1 of its hand-back)

## Evidence

At `3ef12e60`, `Services/ProtectedPrincipalService.cs` `GetLegacyExclusions` (the `ReadModuleConfig`
fail-open read, `ModuleConfigService.ReadModuleConfig` around 166-178, after `IsModuleCorrupt`).
Compare the fail-closed shape every other read on this gate uses since aud-a3b/a3c.

## Approach (to be written by the implementing agent before code)

- Read the list through a `Try...` read that distinguishes unreadable from empty (aud-a26k's
  `TryGetModuleConfig`), and treat unreadable as a failed check: the protection gate refuses the
  write (audited `refusedAt: ProtectionRead`, `mutationAttempted: false`), never as "no exclusions".
  Make the probe and the read one read where possible (one fresh read serving both, aud-a28f/a28i
  shape) so there is no window between them.
- Tests through `ProtectedGroupMembershipTests` / the protected-principal tests: a store that fails
  between the probe and the list read refuses the check; an empty list with a healthy store still
  passes; a corrupt store still refuses. Mutation at the call site: read fail-open again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `fbae13c7`. One commit
`fix(protected-principals): the legacy ExcludedUsers list is read once, fail-closed, and that one read is the
corruption probe (aud-a28p)`, first on this branch (then aud-a26n). Behaviour: the shared protected-principal
service, so every gate that consults it (base app version); no module version moves; the supervisor owns the bump.

Evidence re-read at `1db00236`: `ProtectedPrincipalService.LoadEffectiveConfig` 343-400 probes
`_moduleConfig.IsModuleCorrupt("MailboxPermissions")` (349-350: one read of the module's rows), then reads the
list a second time, on the cache-hit path (364) and the fresh path (399), through `GetLegacyExclusions`
1301-1313 -> `ModuleConfigService.GetValue` -> `ReadModuleConfig` 260-272, which logs a read failure and returns
an empty config. A failure of that second read is "no exclusions": `CheckAsync` 253-285 skips
`CheckLegacyExclusions` (277-278) and answers NotProtected, and `BlockedSenderProtectionGate.EvaluateAsync`
132-148 allows without resolving when the central rules and this list are both empty.

1. **One read, fail-closed.** `GetLegacyExclusions` becomes `TryGetLegacyExclusions(out string[] exclusions)`
   over `ModuleConfigService.TryGetModuleConfig` 75-76 (aud-a26k): false when the MailboxPermissions rows cannot
   be read; otherwise the `ExcludedUsers` value split as today (the read's dictionary is case-insensitive, as
   `GetValue`'s was). `LoadEffectiveConfig` calls it once, first, in place of the probe: false returns the probe's
   error unchanged ("MailboxPermissions module configuration is corrupt - protected-principal exclusions
   unavailable"), so every consumer refuses exactly as it does on a corrupt store today; true carries that list
   to both return paths. The probe and the list are one read - no window between them, and one read fewer per
   check. The `_legacyFileCorrupt` refusal and the cache stay where they are, after it.
2. **The refusal.** No gate changes. `CheckAsync` returns `Failed(loadError)`, which every gate refuses and
   records under its own protection step with `mutationAttempted: false` (the AD Attribute Editor's
   `ProtectedPrincipalCheck`, aud-a28b); `CheckWriteTarget` 306-330, `BlockedSenderProtectionGate`,
   `PermissionValidator.ValidateTargetMailboxAsync` (246) and Admin Settings' load gate (768) get the probe's
   error as today. The approach's `refusedAt: ProtectionRead` is not added as a step name: the failure reaches
   each gate as the same `Failed` result every unreadable protection read produces (aud-a3b, aud-a3c), and a
   second name for the same refusal would split each gate's filter in two.
3. **Registries.** No page, handler or markup changes: ClickGateRegistry and ProgressRegistry are untouched.

Tests, new `ExchangeAdminWeb.Tests/ProtectedPrincipalExclusionsReadTests.cs`: the real `ProtectedPrincipalService`
over a module-config store on the harness's database file that answers a set number of reads and then fails
every later one. Through the AD Attribute Editor's `SaveAsync` (an `AttributeEditorHarness.HarnessService` built
over that real service; the LocalAd stand-ins, no directory): a target listed in ExcludedUsers, the store failing
after its first read - the window between a probe and a second read - is refused, exactly one
`ADAttributeEdit_Refused` record (`refusedAt: ProtectedPrincipalCheck`, `mutationAttempted: false`), no directory
session, nothing set; the store unreadable from its first read is refused with the corrupt reason, one record; an
empty ExcludedUsers over a healthy store is written, one success record (the control). At the service: one
`LoadEffectiveConfig` reads the module-config store once, fresh and from the cache. The existing
`Check_CorruptMailboxPermissionsModuleConfig_FailsClosed` and `Check_LegacyExclusion_FromModuleConfig_IsProtected`
stay as they are. Mutation at the call site: (1) `LoadEffectiveConfig` reads the list fail-open again - the
`IsModuleCorrupt` probe restored and the list read through the fail-open `GetValue` after it.

## Guard proof

Pending.
