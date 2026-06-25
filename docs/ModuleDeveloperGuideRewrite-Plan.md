# Module Developer Guide Rewrite Plan

Status: Implemented (app 2.3.22, 2026-06-25)
Owner: Michael
Last verified against code: commit 49e3010 (2026-06-24)

<!-- Sections marked [YOU] are written or approved by Michael, in plain language.
     Sections marked [MODEL] are drafted by the model and only skimmed by Michael.
     This is a change ticket for source code. Treat it like one. -->

## 1. Goal  [YOU — 3 to 6 sentences]

`docs/AdminModuleDeveloperGuide.md` is the reference a developer follows when
building a new ExchangeAdminWeb module. Its version header says 1.3, last
verified at commit 6e2fbb6 (2026-06-05), but the app is now at 2.3.21 and the
SQLite config-store migration (Phases A–E) made substantial portions of the
guide wrong: it still describes JSON fragment files for section access, module
enablement, and module config, references per-module `config/*.json` files the
deploy scripts no longer preserve, and misses the non-destructive startup
seeding behavior. `AdminModuleSpec.md` carries its own drift warning against
app 2.3.9. The validator (`tools/validate-module-package.ps1`) has a pending
note about `<ModuleVersion />` enforcement that is not yet reflected in its
code. The goal is to fully re-verify both documents against the current codebase
and rewrite them so they are accurate, self-contained, and usable by a developer
who has no access to this conversation.

## 2. Non-goals  [YOU — bullets]

- No new module behavior is added as part of this work stream.
- No changes to the validator's runtime behavior beyond adding the pending
  `<ModuleVersion />` check (AC5 below, if approved — see open questions).
- The module-packaging / runtime-upload feature is out of scope and has its own
  future plan.
- `docs/AdminModuleSpec.md` is referenced but a deep architectural redesign is
  not in scope; the goal is accuracy, not expanding its surface.

## 3. Acceptance criteria  [YOU approve each; model may propose]

- **AC1**: `AdminModuleDeveloperGuide.md` version header and "last verified"
  date are updated to reflect app 2.3.21 and this plan's commit.
- **AC2**: Every reference to `config/sectionaccess.json`,
  `config/modules-enabled.json`, `config/module-config-*.json`, and the
  per-file fragment shape is removed or replaced with the SQLite equivalents
  (`config/exchangeadmin.db`, `ModuleConfigService`, DB tables). No JSON
  fragment examples or "add dedicated config file, update deploy scripts"
  guidance remain.
- **AC3**: Module enablement behavior in the guide is accurate: missing row →
  `EnabledByDefault`; store unreadable → fail-closed; startup non-destructive
  seeding (`INSERT … ON CONFLICT DO NOTHING`) is documented; no-startup-write
  rule is stated.
- **AC4**: The Deployment Considerations section lists `config/exchangeadmin.db`
  as the preserved runtime config store, not a list of JSON fragment files.
- **AC5**: The validator raises Error `PAGE009` when no `<ModuleVersion` token
  is found in the module's Razor page. A Pester test covers both the true (fires)
  and false (does not fire) cases. The guide's "Pending check" note is removed
  and PAGE009 is added to the validator's documented check list.
- **AC6**: `AdminModuleSpec.md` drift warning is removed; the spec is re-verified
  against the current descriptor shape in `Modules/AdminModuleDescriptor.cs`,
  the five missing fields (`Category`, `Version`, `DependsOn`, `IsConfigOnly`,
  `ConfigFields`) are present in its example, and the version header is updated.
- **AC7**: The guide's "Run before submitting" commands are updated: the bare
  `dotnet build`/`dotnet test` lines are replaced with the solution-file forms
  (`ExchangeAdminWeb.slnx`) per the AGENTS.md verification rules.
- **AC8**: No claim in either document is contradicted by the current code at the
  time of the commit. (Verified by a diff-driven review during implementation.)

## 4. Failure behavior  [YOU own — this is the risk section of a change ticket]

*These are docs-only changes (plus one optional script change if AC5 is
approved). There is no runtime component and no data migration.*

| Step / dependency | If it fails | The user sees | System state afterward |
|---|---|---|---|
| Guide rewrite ships with a stale claim | A new module author follows wrong guidance | Module may be built incorrectly (wrong config pattern, wrong deployment note) | No runtime impact; next review cycle catches it |
| AC5 validator check ships with a false positive | A valid module page is flagged Error PKG009 | Validator run fails; author blocked until false positive is diagnosed | Validator exits 1; no host change |
| AC5 validator check ships with a false negative | A page missing `<ModuleVersion />` passes | Review reviewer must catch it manually (same as today) | No regression vs. current state |

## 5. Rollback / blast radius  [YOU own]

*Docs-only (+ optional validator script change).* Reverting is a one-commit
`git revert`. There is no database migration, no runtime behavior change, and no
deployment script change. The validator script change (AC5) is a new Error code
on a read-only tool; the host is unaffected and the validator is opt-in per run.

## 6. Design sketch  [MODEL — Michael skims]

### Files changed

| File | Change |
|---|---|
| `docs/AdminModuleDeveloperGuide.md` | Full rewrite of Module Enablement, Section Access, Module Configuration, Deployment Considerations sections; update version header and "last verified"; update "Run before submitting" commands; remove/update `<ModuleVersion />` pending note (if AC5 approved) |
| `docs/AdminModuleSpec.md` | Remove drift warning; add missing descriptor fields to example; update version header; update Naming Conventions "config persisted" note |
| `tools/validate-module-package.ps1` | Add `PAGE009` check (AC5 only, if approved). Check: source `.razor` files in `src/` that lack a `<ModuleVersion` token emit Error PAGE009. |

### What "accurate SQLite" looks like in each section

**Module Enablement (guide §431–448 → rewrite):**
- Replace "stored in `config/modules-enabled.json`" with: stored in
  `module_enablement` table in `config/exchangeadmin.db` via
  `ModuleEnablementRepository`.
- Replace JSON shape example with table description.
- Add: startup non-destructive seeding via `SeedMissingModules`; no row until
  admin explicitly saves enablement or seeding runs.
- Remove "Missing file: modules use `EnabledByDefault`" → "No row: modules use
  `EnabledByDefault`."

**Section Access (guide §449–472 → rewrite):**
- Replace "stored in `config/sectionaccess.json`" with: stored in
  `section_access` table in `config/exchangeadmin.db` via
  `SectionAccessRepository`.
- Remove JSON shape example; replace with table description.
- Keep fail-closed semantics paragraph (still accurate).

**Module Configuration (guide §473–509 → rewrite):**
- Remove "stored in per-module files `config/module-config-{ModuleId}.json`."
- Remove "If a module adds a dedicated config file, update deploy/promotion
  scripts." These files no longer exist.
- Keep `ModuleConfigService` API table — it is still accurate.
- Add note: the store is SQLite; `IsModuleCorrupt` trips on DB-level failures,
  not JSON parse failures.
- Remove `HasConfigFile` (deprecated property name) — confirm against current
  `ModuleConfigService.cs` before removing.

**Deployment Considerations (guide §1004–1028 → rewrite):**
- Replace the six-item JSON file list with: `config/exchangeadmin.db` is the
  runtime config store; it is excluded from robocopy mirroring by the exclusion
  of the entire `config/` directory; the ops scripts perform a verified online
  backup before each deploy.
- Remove "If a module introduces a new durable config or state file, update
  deployment and promotion scripts." No new JSON fragments are expected;
  non-trivial module-scoped durable state still needs a plan, but the
  instruction is now misleading.

**"Run before submitting" commands (guide §994–1001 → update):**
- Replace bare `dotnet build ExchangeAdminWeb.csproj` with
  `dotnet build ExchangeAdminWeb.slnx -c Release`.
- Replace bare `dotnet test ExchangeAdminWeb.Tests\...` with
  `dotnet test ExchangeAdminWeb.slnx`.

**AdminModuleSpec.md drift warning:**
- Remove the warning block (lines 3–14 of the current file).
- Update descriptor example to include `Category`, `Version`, `DependsOn`,
  `IsConfigOnly`, `ConfigFields`.
- Update version header to 2.0 and "last verified: commit <this commit>".

**Validator PAGE009 (AC5 only):**
- After the existing `PAGE008` block in `validate-module-package.ps1`, add:
  ```powershell
  if ($pageText -notmatch '<ModuleVersion') {
      Add-Issue "Error" "PAGE009" "Razor page is missing the <ModuleVersion /> component." $pageRel
  }
  ```
- Remove the "Pending check" advisory note from the guide.
- Add PAGE009 to the validator's documented check list in the guide.

### Conformance checks (read before editing)
Before editing, the implementer must re-read:
- `Modules/AdminModuleDescriptor.cs` — verify current field list
- `Services/ModuleConfigService.cs` — verify current public API
- `Services/ModuleEnablementService.cs` and `SeedMissingModules` behavior
- `Services/SectionAccessService.cs` — verify stored in DB
- `Services/Storage/ModuleEnablementRepository.cs` — verify table name
- `Services/Storage/SectionAccessRepository.cs` — verify table name
- `tools/validate-module-package.ps1` — verify current PAGE008 context before
  inserting PAGE009

## 7. Task breakdown  [MODEL — Michael skims]

**T1** — Verify current code surface against guide claims. Read
`AdminModuleDescriptor.cs`, `ModuleConfigService.cs`,
`ModuleEnablementService.cs`, `SectionAccessService.cs`, and the storage
repositories. Note any claim in the guide that contradicts what you read. This
is the evidence base for every edit. (Serves AC1, AC2, AC3, AC4, AC6, AC8.)

**T2** — Rewrite Module Enablement section to SQLite reality (AC3).

**T3** — Rewrite Section Access section to SQLite reality (AC2).

**T4** — Rewrite Module Configuration section to SQLite reality (AC2, AC8).
Remove deprecated per-file guidance; confirm `HasConfigFile` status.

**T5** — Rewrite Deployment Considerations section (AC4).

**T6** — Update "Run before submitting" commands (AC7).

**T7** — Update version header and "last verified" date (AC1).

**T8** — Update `AdminModuleSpec.md`: remove drift warning, add missing
descriptor fields to example, update version header (AC6).

**T9** *(conditional on AC5 approval)* — Add PAGE009 check to
`validate-module-package.ps1`; remove pending note from guide; add PAGE009 to
guide's validator check list. Add Pester test in
`tests/ps/DeployInvariants.Tests.ps1` (or a new `ValidatorChecks.Tests.ps1`)
proving the check fires when `<ModuleVersion` is absent and does not fire when
it is present. (AC5.)

**T10** — Build + test green (`dotnet build ExchangeAdminWeb.slnx -c Release`,
`dotnet test ExchangeAdminWeb.slnx`); format check; run
`Invoke-ScriptAnalyzer` and `Invoke-Pester tests/ps` if T9 was done; bump
app version.

## 8. Test plan  [MODEL writes; YOU check the mapping only]

| Criterion | Test |
|---|---|
| AC1 | Human visual check: version header and last-verified date updated in both documents. No automated test needed for doc-string accuracy. |
| AC2 | Human review of diff: search `config/*.json`, `sectionaccess.json`, `modules-enabled.json`, `module-config-` in the edited files; none should appear except in historical notes or as deprecated references. |
| AC3 | Human review of diff: Module Enablement section contains "ON CONFLICT DO NOTHING", "no startup write", and "no row" phrasing; does not contain "missing file". |
| AC4 | Human review of diff: Deployment Considerations references `exchangeadmin.db` and robocopy `config/` exclusion; the old per-file list is gone. |
| AC5 (if approved) | Pester: `validate-module-package.ps1 -PackagePath <page-without-ModuleVersion>` exits 1 with PAGE009. `validate-module-package.ps1 -PackagePath <page-with-ModuleVersion>` exits 0. Add to existing `tests/ps/` suite. |
| AC6 | Human review of diff: drift warning block removed from AdminModuleSpec.md; all five previously missing fields present in descriptor example; version header updated. |
| AC7 | Human review of diff: "Run before submitting" commands use `.slnx` solution file. |
| AC8 | All 508 existing xUnit tests remain green (`dotnet test ExchangeAdminWeb.slnx`). If T9 was done, Pester suite also green. |

## 9. Traceability check  [MODEL fills when iteration ends; YOU read]

*(Left empty — filled when plan iteration ends.)*

## 10. Review log  [MODEL appends each round]

*(None yet.)*
