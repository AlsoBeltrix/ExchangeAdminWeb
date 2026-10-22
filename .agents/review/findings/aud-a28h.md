# aud-a28h: The other settings saves audit only successes

**Severity**: MEDIUM - the Module Configuration page's other tabs (enablement, section access,
module admins, protected principals, scalar settings), the Admin Settings page and the Exchange
Online Config page each write a settings-change record on success and nothing on a refusal or an
exception. Section access and protected principals are the authorization stores; a refused or
failed attempt to change them leaves no trace. aud-a28f fixed the allowlist save and introduced
`AuditService.LogSettingsChangeFailed` for exactly this.
**Status**: Fixed on branch; closed on guard proof; awaiting merge and review
**Branch**: `worktree-agent-aa5d4735f85b48351`
**Commit**: `87833faf` (comments `87660ef8`)
**Reviewer**: implementer (surfaced by the aud-a28f/a26e agent, 2026-10-22, candidate 2 of its hand-back)

## Evidence

At `488e535e`: `Components/Pages/ModuleConfig.razor`, every `Save*Async` other than the allowlist
one; `Components/Pages/AdminSettings.razor` and `Components/Pages/ExchangeOnlineConfig.razor`
save handlers. Each calls `LogSettingsChange` after a successful write; refusal returns and catches
write no record. `LogSettingsChangeFailed` exists since aud-a28f (`07c366ef`).

## Approach (to be written by the implementing agent before code)

- Survey every settings save handler on the three pages (list them in the record with line
  numbers). For each: every refusal return records through `LogSettingsChangeFailed` with
  `refusedAt` and `mutationAttempted: false`; every catch records a failure without the refusal
  fields. Same action, category and section as the matching success record so the Admin Event Log
  filters them together. Exactly one record per attempt; notifications unchanged.
- Where a page handler can move into its service the way aud-a28f moved the allowlist save, prefer
  that; otherwise record from the page through non-throwing helpers. Do not change what any save
  writes.
- Tests: for each handler, a refused attempt leaves exactly one failed record and the store is
  unchanged; an exception leaves one failure record; a success still leaves exactly one success
  record (no double logging). Page tripwires in the a28b/a28f style where a handler cannot be driven
  without bUnit. Mutations at the call site: drop one refusal record per page; drop one catch record
  per page.
- `ClickGateRegistry`: all three pages `NotYetConverted`? Confirm each; re-anchor per the playbook if
  any has line-keyed entries. `ProgressRegistry` pins unchanged.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the a28f/a26e hand-back
(`22833c0d`). One commit `fix(settings): every refused or failed settings save is audited
(aud-a28h)`, after aud-a28g. Behaviour: Module Configuration and Admin Settings (app-shared) and
Exchange Online Config (the Exchange Online module); the supervisor owns the bumps.

Survey at `22833c0d` - nine handlers, nineteen refusal returns, nine catches, none recorded:

| Page | Handler | Refusal returns (`refusedAt`) | Catch | Success record (section) |
|---|---|---|---|---|
| `ModuleConfig.razor` | `SaveConfigAsync` 1087 | 1099-1104 Authorization; 1106-1112 StoreCorrupt | 1139-1143 | 1127 `ModuleConfig:{id}`, when a key changed |
| | `SaveModuleEnablementAsync` 1147 | 1158-1163 Authorization; 1165-1171 StoreCorrupt | 1205-1209 | 1183 `Module:{id}`, when it changed |
| | `SaveSectionAccessAsync` 1216 | 1227-1232 Authorization; 1234-1240 StoreCorrupt; 1251-1256 GroupGrantCheck | 1299-1303 | 1287 one per changed alias (section = alias) |
| | `SaveModuleAdminsAsync` 1307 | 1318-1323 Authorization; 1330-1335 GroupGrantCheck | 1355-1359 | 1343 `ModuleAdmins:{id}` |
| `AdminSettings.razor` | `SaveEnablement` 560 | 570-575 Authorization; 577-583 StoreCorrupt | 622-626 | 597 one per changed module, `Module:{id}` |
| | `SaveLogLevel` 633 | 643-648 Authorization | 660-664 | 654 `ExtendedLogLevel` |
| | `SaveProtectedPrincipals` 940 | 950 Authorization; 958-963 ValidationInProgress | 992-996 | 980 `ProtectedPrincipals` |
| `ExchangeOnlineConfig.razor` | `SaveModuleStatus` 199 | 209 Authorization | 233-237 | 221 `Module:ExchangeOnline`, when it changed |
| | `SaveExoConfig` 241 | 287 Authorization; 302-307, 309-314, 316-321, 323-328 Validation | 352-357 | 342 `ExchangeOnlineConfig` |

Not attempts: ModuleConfig's `if (module == null) return;` (1089, 1149, 1218, 1309 - no module, so
no Save bar is rendered) and the dispatchers `SaveCurrentTabAsync` (ModuleConfig 841, AdminSettings
373). `SaveAllowlistAsync` is aud-a28f's.

1. **Recorded from the page.** These saves span five stores and their services, and what they record
   needs page state (the descriptor's secret masking, the alias snapshot, the directory check and its
   activity), so aud-a28f's move into one service does not fit. New `Services/SettingsSaveAudit.cs`:
   two extension methods on AuditService, `LogSettingsSaveRefused(logger, performedBy, ip, section,
   refusedAt, reason, sections)` and `LogSettingsSaveFailed(logger, performedBy, ip, section, reason,
   sections)`, writing `LogSettingsChangeFailed` (aud-a28f: the success record's action and
   category, result Failed, `error` = what the admin was shown); a refusal adds `refusedAt` and
   `mutationAttempted: false`, a failure neither; an audit failure is logged and never thrown. Not
   AuditService members, so its every-public-audit-method list does not change.
2. **Every refusal and catch.** Each refusal calls LogSettingsSaveRefused directly above its return
   (the one-line refusals at AdminSettings 950 and ExchangeOnlineConfig 209 and 287 become blocks);
   each catch calls LogSettingsSaveFailed. The ip is read once above the try so the catch has it; the
   success path reuses that value. Section = the success record's. The two saves whose success writes
   one record per changed section file their one record under the save's own scope and list the
   pending sections in `sections`: Admin Settings' enablement under `ModuleEnablement` (each
   `Module:{id}` whose toggle differs from what the page loaded); a module's section access under its
   main permission alias (each alias whose working list differs from the loaded one).
3. **Exactly one record per attempt.** A refusal returns before any write or record. The catch is
   reached before the success record on every path: each success record is its handler's last call
   that can throw (its own audit failure is already caught); what follows is page state and, for the
   two enablement saves, the render and reload navigation, which do not throw on a live circuit.
   Success records, store writes and notifications are unchanged (these saves send no notice).
4. **Registries.** ClickGateRegistry: all three pages NotYetConverted ("tier 2, not approved"), no
   line-keyed entries, no ExpectedLineCount - confirmed at `22833c0d`; nothing re-anchors.
   ProgressRegistry is keyed by handler name; no handler is added and SaveExoConfig's pins hold.

Tests. New `ExchangeAdminWeb.Tests/SettingsSaveAuditTests.cs` drives all nine handlers through the
compiled pages - reflection, the Comms10k/Defender handler pattern, attached to a do-nothing renderer
so the two enablement saves can render before their reload - over real stores on a temp SQLite file,
the real AuditService, a substituted authorization service and a recording NavigationManager. No
directory: the group-grant refusals are driven with a grant that is not a group SID, which
GroupGrantValidation refuses before any lookup, and the other cases add no grant. Per handler: each
refusal -> exactly one Failed record (section, `refusedAt`, `mutationAttempted: false`, the message
shown) and the store unchanged; an exception (a store whose writes fail; for SaveLogLevel, whose
SetLevel swallows store failures, an authorization call that throws) -> exactly one Failed record
without the refusal fields, store unchanged; a success -> exactly one Success record. Tripwire: in
each handler every refusal return has LogSettingsSaveRefused within the lines above it, and the catch
calls LogSettingsSaveFailed. Conditions asserted, not wording. Mutations at the call site: one
refusal record dropped per page (ModuleConfig SaveSectionAccessAsync Authorization, AdminSettings
SaveProtectedPrincipals Authorization, ExchangeOnlineConfig SaveExoConfig Authorization); one catch
record dropped (ModuleConfig SaveConfigAsync).

## Executed reproduction - 2026-10-22

Branch `worktree-agent-aa5d4735f85b48351` after aud-a28g (`e993a83f`). The new
`SettingsSaveAuditTests` run against the three pages as committed there (the pre-fix bytes swapped
in by a session-local driver, `reproduce.ps1` in the scratchpad's `a28g-a28h-a26f-a28i-work`;
ModuleConfig `2C6292E1A1575D6BA9039184C87B0DF5`, AdminSettings
`7B07FE69B8A715CFDFAB406980295846`, ExchangeOnlineConfig `8884749CEE5157A1C2FB0E98D090E382`), build
0 errors: 38 failed / 9 passed. Every refusal and exception case - 29 behavioural cases, the four
connection-validation theory rows among them - failed `Assert.Single() Failure: The collection was
empty`: the attempt left no record at all; the nine tripwire rows failed too. The nine success
controls passed: the success records were already there. The fixed bytes were restored to their
MD5s after the run.

## Fix

`Services/SettingsSaveAudit.cs` (new): `LogSettingsSaveRefused(this AuditService, logger,
performedBy, ip, section, refusedAt, reason, sections)` and `LogSettingsSaveFailed(this AuditService,
logger, performedBy, ip, section, reason, sections)`, both through `AuditService.LogSettingsChangeFailed`
(the success record's action and category, result Failed, `error` = what the admin was shown); a
refusal adds `refusedAt` and `mutationAttempted: false`, a failure neither; `sections`, when given,
is listed; an audit failure is logged and never thrown.

The nine handlers (survey above): every refusal records `LogSettingsSaveRefused` directly above its
return, every catch records `LogSettingsSaveFailed`. Each handler reads its ip once above the try and
names its section once; the success record uses both, unchanged in value. The three one-line
refusals became blocks. Two page-local helpers name what a multi-section save was changing:
ModuleConfig `PendingAccessSections()` (aliases whose working list differs from the loaded one) and
AdminSettings `PendingEnablementSections()` (`Module:{id}` for each toggle that differs). Refusal
steps: `Authorization`, `StoreCorrupt`, `GroupGrantCheck`, `ValidationInProgress`, `Validation`.

Tests: new `ExchangeAdminWeb.Tests/SettingsPageHarness.cs` (the compiled pages driven over real
stores, the real audit writer, a substituted authorization service and a recording
NavigationManager; Admin Settings' post-render directory sweep is overridden to nothing) and
`ExchangeAdminWeb.Tests/SettingsSaveAuditTests.cs` (47 cases: 29 refusal and failure, 9 success, 9
tripwire rows).

Caller-visible change, by design: every refused or failed attempt on these nine saves now leaves one
Failed row in the Admin Event Log (category AdminSettings, the section its success files under, or
`ModuleEnablement` / the module's main alias for the two multi-section saves, with the pending
sections listed). Nothing the pages show, write or send changes.

## Guard proof

Same session-local driver as aud-a28g (`mutate.ps1`), filter `FullyQualifiedName~SettingsSaveAuditTests`
(47 tests). Fixed-file MD5s: `Components/Pages/ModuleConfig.razor`
`BB3A2A282F0C06F9564F3CA6E8E21D0F`, `Components/Pages/AdminSettings.razor`
`57682F609A95E206B64A64F1DAE0621A`, `Components/Pages/ExchangeOnlineConfig.razor`
`39D84DA5CA333674631F2E8D12950BE9`, `Services/SettingsSaveAudit.cs`
`7C6D62143577DEC65EB3438D2FCCC92F`; each restored to its MD5 after every mutant, and every mutant
built with 0 errors. After the round, a rebuild on the restored bytes and the filter widened to the
page registries and tripwires (SettingsSaveAuditTests, ProgressRegistryTests,
PageAuthorizationRecheckTests, ClickGate*, AllowlistSaveAuditTests, GlobalProgress*,
AuditCategoryFilingTests): 468/468 green.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| ModuleConfig SaveSectionAccessAsync: the authorization refusal's record deleted | `BF8F3C62AEEAC6A9D621483C3CD4B416` | `ModuleConfig_AnUnauthorisedAccessSave_IsRecordedAsARefusal_NamingWhatItWouldChange` (and its tripwire row) |
| AdminSettings SaveProtectedPrincipals: the authorization refusal's record deleted | `745884A9FE3830B2664C5F1F3BFC8612` | `AdminSettings_AnUnauthorisedProtectedSave_IsRecordedAsARefusal_AndWritesNothing` (and its tripwire row) |
| ExchangeOnlineConfig SaveExoConfig: the authorization refusal's record deleted | `59CB5FA4AC9934D8F38AB83608529647` | `ExchangeOnlineConfig_AnUnauthorisedConnectionSave_IsRecordedAsARefusal_AndWritesNothing` (and its tripwire row) |
| ModuleConfig SaveConfigAsync: the catch's record deleted | `A8A59BC0ABFB23E104D243F9BCBDB1C4` | `ModuleConfig_AConfigSaveWhoseWriteFails_IsRecordedAsAFailure` (and its tripwire row) |
| extra, AdminSettings SaveEnablement: the catch's record deleted | `F570C94503D9C66E8EF107B2872B9E0F` | `AdminSettings_AnEnablementSaveWhoseWriteFails_IsRecordedAsAFailure` (and its tripwire row) |
| extra, the helper: a refusal written without its refusal fields | `23313870947203A9C4056315C9D52BE6` | every refusal case, 20 of 47 (`ModuleConfig_AnUnauthorisedConfigSave_IsRecordedAsARefusal_AndWritesNothing` first) |

The code comments are written after the round, in their own commit, so the MD5s above are this
commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; warnings all pre-existing, none in a
  touched file.
- `dotnet test ExchangeAdminWeb.slnx -c Release --no-build --blame-hang --blame-hang-timeout 10m`:
  exit 0, 5,196 passed / 3 skipped / 0 failed, 5,199 total, 5m30s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0.
  `git diff --check`: exit 0. `tools/Test-AsciiOnly.ps1` with the files staged: exit 0.

No PowerShell source, AD module, directory, Exchange Online, Delinea, SMTP, push or deployment: no
case adds a usable group SID, and the harness leaves the directory services unset. The pages were
not rendered in a browser and no live circuit was run. No coder self-review verdict is claimed.

## Known gaps

- The two multi-section saves file their one refusal or failure under the save's own scope
  (`ModuleEnablement`, the module's main alias), not the per-alias or per-module sections their
  successes use; `sections` lists those. One record per attempt was the requirement, and there is
  no single success section to match.
- A save whose write lands and whose next step throws (SaveExoConfig's DrainPool, SaveProtectedPrincipals'
  second write) is recorded as a failure, which is what the admin is shown; the record does not say
  the first write landed. Pre-existing ordering, not changed here.
- The pages are driven by reflection over their compiled handlers, not through a rendered circuit:
  the buttons' own gating is not exercised.

## Branch-tip gates - 2026-10-22 (`87660ef8`: all four fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, warnings all pre-existing.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is
  written): exit 0, 5,215 passed / 3 skipped (the three known AD skips) / 0 failed, 5,218 total,
  5m38s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check
  HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
