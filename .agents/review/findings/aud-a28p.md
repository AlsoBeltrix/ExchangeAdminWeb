# aud-a28p: The protected-principal check reads the legacy ExcludedUsers list fail-open after its corruption probe

**Severity**: MEDIUM - a fail-open read on the protection gate (Known Failure Class 3: an
authorization store must deny when a read fails). `ProtectedPrincipalService.GetLegacyExclusions`
reads the Mailbox Permissions module configuration's `ExcludedUsers` list after the corruption probe
has passed; a read failure between the probe and this read returns an empty list, so the principals
that list protects are un-protected for that check and a write against them goes through.
**Status**: Merged to master (`7b65d27e`); closed on guard proof; codex attr-batch-10 clean (raised aud-a28s)
**Branch**: `worktree-agent-a3c115693e85a6623`
**Commit**: `0b92c35f` (plan `ff78ff27`; comments `389a34a2`; merged `7b65d27e`)
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

## Executed reproduction - 2026-10-22

Branch `worktree-agent-a3c115693e85a6623`, the fix's tests in place and `Services/ProtectedPrincipalService.cs` as it
was at `1db00236` swapped in (`git show`; `git hash-object` equal to the blob, MD5
`14B9247E048AD369950DA41119EAC53F`), build 0 errors: `ProtectedPrincipalExclusionsReadTests` 2 failed / 2 passed. The
AD Attribute Editor save against a target on ExcludedUsers, with the module-config store failing after its first
read, was written (`result.Success` true: the probe took the one read that answered and the list's read failed open
to "no exclusions"), and one `LoadEffectiveConfig` read the store twice. The store unreadable from its first read
was refused and the empty-list control passed - both as they must against the old code too, which refused a store
the probe could not read. The fixed service was restored to `062DE6CF308D399A1A730EBA53057678` and touched.

## Fix

`Services/ProtectedPrincipalService.cs`: `TryGetLegacyExclusions(out string[] exclusions)` replaces
`GetLegacyExclusions` - one read of the MailboxPermissions rows through `ModuleConfigService.TryGetModuleConfig`,
false when they cannot be read, otherwise the `ExcludedUsers` value split as before. `LoadEffectiveConfig` calls it
first, in place of the `IsModuleCorrupt` probe: false returns the probe's error unchanged; true carries the list to
the cache-hit and the fresh return. No consumer changed.

Tests: new `ExchangeAdminWeb.Tests/ProtectedPrincipalExclusionsReadTests.cs` (4 cases): the store failing after its
first read and the store unreadable from its first, each through the AD Attribute Editor's save over the real
service (one refusal record, no session, nothing set); the empty list over a healthy store; one module-config read
per load, fresh and from the rules' cache. `ProtectedPrincipalServiceTests`' appsettings-retirement comment names
the renamed method. One departure from the plan: the empty-list control is asked of the service (`CheckAsync`
answers NotProtected, not failed), not through a save. A save that passes the check opens a PowerShell session,
and in a filtered run two test classes opening their first sessions at the same moment tripped PowerShell's own
initialisation race ("An item with the same key has already been added. Key: Alias") in an unrelated
`ADAttributeEditorSaveTests` case, so in its passing state this class opens none. That the editor built over the
real service writes when the check lets it is what mutation 1 shows: under it the save is written.

Caller-visible change, by design: none while the store reads. When the MailboxPermissions rows cannot be read,
every protected-principal check fails closed with the corruption error, as it did whenever the probe caught it; the
window in which the probe's read succeeded and the list's did not - which left the list's principals unprotected
for that check - is gone.

## Guard proof

Session-local driver (`mutate.ps1` in the scratchpad's `a28p-a26n-work`): back up, mutate by exact text (one match
required), MD5, `dotnet build` of the test project, `dotnet test` filtered to
`FullyQualifiedName~ProtectedPrincipalExclusionsReadTests|FullyQualifiedName~ProtectedPrincipalServiceTests|FullyQualifiedName~BlockedSenderProtectionGateTests|FullyQualifiedName~PermissionValidatorTests|FullyQualifiedName~ADAttributeEditorSaveTests`
(172 tests), restore from the backup, set the write time, MD5 re-confirmed. Fixed-file MD5:
`Services/ProtectedPrincipalService.cs` `062DE6CF308D399A1A730EBA53057678`; every mutant built with 0 errors and
the file was restored to that MD5 each time. After the round, the restored tree rebuilt and the same filter: 172/172
green.

| # | Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|---|
| 1 | `LoadEffectiveConfig`: the list read fail-open again - the `IsModuleCorrupt` probe restored and the list read through `GetValue` after it | `C16A72CF94E0C926314483782BB48F8D` | `AStoreThatFailsAfterItsFirstRead_DoesNotUnprotectAnExcludedTarget` (the save written: `result.Success` true), `OneLoad_ReadsTheModuleConfigOnce_FreshAndFromTheCache` (2 reads, 1 expected) |
| E1 | extra, `TryGetLegacyExclusions`: rows that cannot be read taken as an empty list (`return true`) | `39330F7346E67605EE74BB015B50AEDF` | `AStoreThatCannotBeRead_RefusesTheCheck_AsCorrupt` (the save written), `ProtectedPrincipalServiceTests.Check_CorruptMailboxPermissionsModuleConfig_FailsClosed` |

A first E1 run also failed `ADAttributeEditorSaveTests.AnErrorTheWriteReportsWithoutThrowing_IsNotTakenForAFailedWrite`
with the PowerShell initialisation race above (the mutant let this class's save open a session beside it); that test
passed three runs out of three on the restored tree, and the E1 row above is the re-run against the final tests.

The code comments are written after the round, in their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; 61 warnings, all pre-existing, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0,
  5,473 passed / 3 skipped (the three known AD skips) / 0 failed, 5,476 total, 4m56s. No hang. (Baseline at
  `1db00236`: 5,469 / 3 / 0, 5,472.)
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check`: exit 0.
  `tools/Test-AsciiOnly.ps1` with the files staged: exit 0.

No PowerShell source, AD module, directory, Exchange Online, Delinea, SMTP, push or deployment: the editor's
directory is the LocalAd stand-in and no case here opens a session in its passing state. No coder self-review
verdict is claimed.

## Known gaps

- The list is read on every check, uncached, as before; one read now where there were two.
- Each gate records the refusal under its own protection step; there is no one `ProtectionRead` step across the
  gates (deliberate, plan item 2).
- The list stays a comma-separated value in the Mailbox Permissions module config, a key the Module Configuration
  page does not show (aud-a28n); unchanged.
- The switch store stands in for a store fault; nothing ran against a damaged database.

## Branch-tip gates - 2026-10-22 (`389a34a2`: both fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 61 warnings, all pre-existing.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0,
  5,495 passed / 3 skipped (the three known AD skips) / 0 failed, 5,498 total, 5m15s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
