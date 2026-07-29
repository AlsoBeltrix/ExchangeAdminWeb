# Agent State

First place to read for current repo state. Keep it short; update it when important repo facts
change. Resolved work lives in the plan/decision/incident docs, not here — this file records only
what is live: current versions, in-flight work, what to do next, blockers, and open gaps.

## Now

- **Retire `Security:ExcludedUsers` appsettings fallback — DONE (code half), landed 2026-07-28.**
  Plan `docs/RetireExcludedUsersAppsettingsFallback-Plan.md` Status: Implemented;
  `.agents/decisions.md` 2026-07-28. Both readers (`PermissionValidator.GetConfiguredExclusions`,
  `ProtectedPrincipalService.GetLegacyExclusions`) no longer fall back to the invisible
  `Security:ExcludedUsers` appsettings array; exclusions come only from the DB protected-principal
  store + `MailboxPermissions/ExcludedUsers` module config. Base app `2.3.29 -> 2.3.30`.
  Commits `4dff069`(plan) `f5b329b`(slice1) `942dd10`(slice2) `5c7cc93`(slice3 tests)
  `c35f056`(slice4 docs) `456e07c`(version). Build/format/827 tests green; two new guard tests
  non-vacuity-proven (fallback restored -> both fail).
  **Slice 5 host cleanup DONE (owner, 2026-07-29):** the `Security.ExcludedUsers` block was
  removed from the deployed `appsettings.json` on both dev and prod (`PreventSelfGrant`,
  `AllowedGroups` left in place). The ExcludedUsers-fallback retirement is now fully complete,
  code + host, both environments.

- **MessageTrace null-pipeline-row NRE — FIXED in repo 2026-07-29, NOT yet deployed.**
  Plan `docs/MessageTraceNullRow-Plan.md` Status: Implemented. Live prod symptom: an EXO
  summary search failed with `Object reference not set to an instance of an object.` (banner
  doubled because `:348` and `:423` both format the same string). Root cause at
  `Services/MessageTraceService.cs:386`: the `Get-MessageTraceV2` pipeline returned a
  collection containing a **null element** and the loop dereferenced it; the `?.` chain guarded
  the property and its value but not `msg` itself. Latent since `b70b59d` (2026-06-04), NOT an
  MT-detail regression; data-dependent (the detail export emailed successfully the same day).
  Same defect class fixed in all four mapping loops (`:277`, `:314`, `:384`, `:501`) — every
  `GetProperty*` helper takes a non-nullable `PSObject` and dereferences `.Properties`.
  MessageTrace module `1.2.0 -> 1.2.1`, no base app bump. 830 tests green; non-vacuity proven
  per guard against the exact production exception.
  **OPEN:** deploy + live re-run of the failing search. **OPEN (OQ-1, non-blocking):** why EXO
  emits a null row at all is undiagnosed; the guard is correct regardless.

- **MessageTrace export delivery: reports page + notification link — PLAN APPROVED 2026-07-29,
  implementation starting at slice 1.** Plan `docs/MessageTraceDownloadLink-Plan.md`
  Status: Approved. Replaces the emailed zip attachment with a Downloadable Reports page inside the
  app; the email carries a link to that page, so an arbitrary notification recipient is safe (the
  data never leaves the login gate) and admins leave the trace-data path. Supersedes
  `docs/MessageTraceDetail-Plan.md` decisions 5 + 6. Owner rulings recorded in the plan:
  **D1** retention is out-of-process (a host scheduled task deletes exports older than 30 days;
  the app never deletes and must render a missing file as "expired"); **D2** the gate is the
  existing `MessageTrace` module policy with no per-user ownership check, and the ticket number is
  an audit prompt only, never an authorization control; **D3** delivery is a Razor page reusing the
  existing base64 + `downloadFile` JS blob mechanism — **no HTTP endpoint** (owner rejected the
  first draft's minimal-API premise; routing through a page also makes the ticket prompt real
  rather than an empty `?ticket=` in an emailed URL); **D4** (ruled 2026-07-29) the recipient box
  is pre-filled with the operator's own address, editable and clearable -- a default, not a floor,
  and never a required field. Base app `2.3.30 -> 2.3.31` + MessageTrace `1.2.1 -> 1.3.0`.
  All four decisions ruled, no open owner gates, plan approved by the owner 2026-07-29.
  **Slice 1 DONE** (`b007ad5`): `MessageTraceExportStore` -- export-path resolver, GUID-"N" jobId
  whitelist, traversal guard, pinned 30-day constant; 19 tests.
  **Slice 2 DONE** (`87941b0`): `MessageTraceExportListing` (page logic as a testable service --
  the repo has no bUnit harness), `Components/Pages/MessageTraceReports.razor` at
  `/message-analysis/reports`, and `GetFinishedByType` on `BulkJobRepository`/`BulkJobService`.
  Build/format/ASCII clean, 875 tests green; non-vacuity proven per guard by reverting each
  (ticket check 3 failures, Failed-vs-Expired 2, unfiltered limit 1, malformed payload 1).
  One fact recorded in code at the point of use: `<ModuleVersion />` resolves the descriptor by
  route and so renders nothing on the sub-route; kept per `docs/AdminModuleSpec.md` (PAGE009)
  rather than hand-rolling the lookup.
  **Slice 3 DONE** (`e4d2497`): the completion mail now carries a link to the reports page instead
  of the export. `EmailService.SendMessageTraceResultAsync` (no attachment, states the retention
  expiry and the ticket prompt) + new `SendMessageTraceFailureAsync`; `NormalizeRecipients`
  replaces `ResolveMessageTraceRecipients` and **adds nothing**, so the configured admin address is
  never merged into a trace-export recipient set (owner ruling). F3: `ResolveReportsUrl` returns
  null -- never a relative path -- when `Application:PublicBaseUrl` is unset or non-absolute, and
  the caller falls back to prose; it never fails the send. F1: the save result branches the
  notification and stamps the job record. New `BulkJobRepository.AppendMessage` /
  `BulkJobService.AppendJobMessage` -- additive, so a note can never erase a cancel/interrupt
  reason -- resolves the slice-2 inherited gap: `MessageTraceExportState.Failed` is now reachable
  in production. `Application:PublicBaseUrl` added to `Install-ExchangeAdminWeb.ps1` (blank
  default, environment-neutral), `deploy.ps1`, `promote-dev-to-prod.ps1` (empty leaves prod
  untouched), and `README.md`. Build/format/ASCII clean, **897 xUnit + 65 Pester green**;
  PSScriptAnalyzer adds one finding per touched script, each in a category already dominant there,
  none at Error severity. Non-vacuity proven per guard by reverting each: save-failure branch 4
  failures, no-relative-link rule 2, admin exclusion 6.
  **NEXT: slice 4** (UI: remove the leaked string at `MessageTrace.razor:399`/`:409`, drop
  `DestinationDisplay()` at `:916-925`, add the D4(a) recipient input pre-filled with the
  operator's own address -- editable, valid when cleared, no required-field validation, client-side
  format check only -- leave `DownloadSelectedDetails()` untouched, and say on the page that only
  emailed/bulk exports are listed). Version bumps (`2.3.30 -> 2.3.31`, MessageTrace
  `1.2.1 -> 1.3.0`) land at the end. One commit per slice. Nothing deploys without a separate
  owner go.
  **Independently reviewed 2026-07-29** (openreview, codex-commercial / gpt-5.6-sol / max, range
  `68bfd25..1e98eaf`): verdict **findings** (4), all repaired in the plan; record at
  `.agents/review/findings/mt-export-delivery-plan.md`. The one that mattered (F1, HIGH): removing
  the mail attachment makes the saved file the sole delivery, so `SaveToLogPath`'s swallowed catch
  would turn a disk-full failure into a "ready" email plus a row the page mislabels as Expired.
  The plan now branches the notification on the save result and separates **Failed** from
  **Expired**. Also: a non-blank ticket is now required at download (F2); an unset
  `Application:PublicBaseUrl` omits the hyperlink instead of emitting an email-unresolvable
  relative path, and the key is added to both config writers (F3); the global
  `Export:RetentionDays` key is dropped for a pinned 30-day constant -- it broke the
  Constitution's module-config rule and created a second retention truth that could drift from
  the host scheduled task (F4). Plus F5, coder-raised: the D3 rewrite had dropped the
  Constitution-conflict record about writing state into an externally-pruned directory; restored.
  **Non-blocking assumptions the owner may reverse:** OQ-3 (required ticket is this plan's reading
  of "requiring", not an explicit ruling) and OQ-4 (the Failed state is scope the owner did not
  request).

- **MessageTrace per-message delivery-detail (MT-detail) — DONE, landed 2026-07-27.**
  Plan `docs/MessageTraceDetail-Plan.md` Status: Implemented; all 9 slices on `master`,
  each codex-reviewed accepted. MessageTrace module 1.1.1 -> 1.2.0, no base app bump.
  Full record archived: `docs/history/state-archive.md`. Live validation not yet performed
  (runs against real PROD EXO/on-prem AD, from the dev instance): detail drill-in, download/email/zip delivery,
  Blazor selection UI (enumerated in the plan).

- **GM-3 self-service group management (on-prem AD only) — task set DONE, landed 2026-07-27;
  two follow-ups landed 2026-07-28.**
  Plan `docs/SelfServiceGroupManagement-Plan.md` Status: Approved / on-prem only; all 6 tasks
  (plan section 7) complete and codex-reviewed. M365/delegated-Entra dropped 2026-07-22.
  Full record + the scope-narrowing and ACL-scan-drop history archived:
  `docs/history/state-archive.md`.
  - **2026-07-28 DACL-read fix (`28efc88`):** eligibility read was `Get-Acl AD:\<DN>`, which
    returned an empty `.Access` in the service runspace, fail-closed-excluding every group (page
    always showed "no groups"). Now reads `Get-ADGroup -Properties nTSecurityDescriptor`. Module
    1.1.0 -> 1.1.1.
  - **2026-07-28 member listing + AD picker (`a190664`,`fd16982`,`428a19e`,`80fe2a5`):** plan
    `docs/SelfServiceGroupsMemberListingAndPicker-Plan.md` (Approved 2026-07-28). Manage panel now
    lists current members with per-user Remove; member add box uses the shared
    `ADIdentityAutocomplete` (Option A: suggestions under app-pool identity, write stays isolated +
    re-validated — `.agents/decisions.md` 2026-07-28). Module 1.1.1 -> 1.2.0. Build/format/824
    tests green (17 new `GroupMemberClassifierTests`, non-vacuity proven).
  SelfServiceGroups module now at **1.2.0** (added at 1.0.0, no base app bump).
  Live validation not yet performed (runs against real PROD AD, from the dev instance): live AD
  add/remove, member list render + remove, audit write, admin + affected-user email, the Blazor
  page flow. Owner will deploy + test the DACL fix; a proper plan+review is the fallback if it
  does not resolve the "no groups" symptom.

- **App version `2.3.30`** (`<VersionPrefix>` in `ExchangeAdminWeb.csproj`). Bumped from `2.3.29`
  (`456e07c`) for retiring the `Security:ExcludedUsers` appsettings fallback; `2.3.29` was the
  app-wide log-root fail-fast change (`3eac48a`).
- **Deployed:** **prod and dev are both on `2.3.30`, validated good (owner, 2026-07-29)** — deployed
  to dev, validated, then promoted to prod. `2.3.30` supersedes all prior deployed builds, so it
  carries the `2.3.28` Bulk Job Runner, the `2.3.29` log-root fail-fast, and the `2.3.30`
  ExcludedUsers-fallback retirement. Prior prod baseline was `2.3.27` (validated 2026-06-29).
- **Log-root fail-fast IMPLEMENTED + pushed** (2026-07-22, `docs/RemoveHardcodedLogRoot-Plan.md`).
  Hardcoded `E:\WWWOutput` fallback removed from all three services; startup guard aborts boot if
  `Audit:LogRoot` is unset/blank. Commits `fa40485` (helper + guard), `b14fce6` (services),
  `821a2f8` (docs), `3eac48a` (app version bump 2.3.28 -> 2.3.29). Build + all 676 tests green.
  **Deploy note:** the new build fails to start if `Audit:LogRoot` is unset; the target env's
  `appsettings.json` must set it before deploying `2.3.29`.
- **RESOLVED (2026-07-29):** `2.3.29`'s log-root fail-fast is now validated in prod — it ships
  inside `2.3.30`, which the owner deployed + validated + promoted to prod. The startup guard is
  inherently exercised: the app cannot boot without `Audit:LogRoot`, and it booted.
- **2026-07-21 landed slices** (ff443ca, c2e2f6f, 502dd0e, 8c6f83f, 9dd39cd, b978362, 71d1daa)
  archived verbatim: `docs/history/state-archive.md` (Archived 2026-07-29).
- **AccountLockoutRemediation: TURNED OFF by owner** (2026-07-21). Does not work in this environment:
  WinRM reaches only ~5 of 38 domain controllers (HTTP 400 / Access denied / unreachable); permanent
  (owner: "won't be changed"). Discovery hides unreachable DCs (looks like "no lockouts found"); sweep
  silently drops the ~33 it can't reach. Owner disabled the module (runtime enablement, no code change).
- **Toolkit bug filed:** roethlar/AgentGovernanceBootstrap#7 -- completing a tracked item should
  auto-update the state record, not gate it behind an owner ask.

## Last work stream — Bulk Job Runner (DONE, pending dev validation)

`docs/BulkJobRunner-Plan.md` (Status: Implemented) · `.agents/decisions.md` 2026-07-02.
App `2.3.27`→`2.3.28`; ConferenceRooms module `2.1.0`→`2.2.0`.

ConferenceRooms bulk apply (Finder/Type CSV) now runs as a durable server-side job (separate
`config/exchangeadmin-jobs.db`, never promoted). <!-- lint: allow (owner ruled leave-it, 2026-07-27: runtime jobs DB is intentionally created outside source control) --> Self-pumping singleton runner (not a hosted timer);
single active job + FIFO queue; startup flips non-terminal jobs to Interrupted (no resume); always
cancellable; per-row failure aggregation; completion email fires from the job. Off-circuit auth =
option (a) (capture the authorization decision at submit, re-check per row via shared pure
`GroupMembershipChecker`). Protected-principal gate enforced in-job per row on **both** Finder and
Type bulk paths (closes GAP 3). Deploy scripts warn (not block) on active jobs before recycle
(`tools/JobStateWarning.psm1`). ~671 xUnit + 65 Pester green (as of `9d26b5f`); build/format/diff-check
clean; each slice codex-reviewed with findings fixed before commit.

**Next action:** run live validation from the dev instance against PROD EXO/AD -- the UI and end-to-end job lifecycle are not
covered by automated tests. (Dev deploy done 2026-07-20.)

## Next up (prioritized)

Live backlog only. Items need an approved plan before code unless noted.
1. **Live-validate the Bulk Job Runner (owner-deferred, 2026-07-20).** Runs from the dev instance
   against real PROD AD/Exchange (the only tenant there is; both instances on this server point at
   it). The runner *logic* is already covered by xUnit without a live run -- lifecycle (FIFO queue,
   cancel, recycle->Interrupted via `Initialize_FlipsOrphanedNonTerminalJobsToInterrupted`), per-row
   failure aggregation, completion notification (all variants), and the protected-principal block on
   **both** Finder and Type paths (`ConferenceRoomBulkProcessorTests`, closes GAP 3). What stays
   unvalidated until a live run: the Blazor UI (submit/progress/reconnect) and an actual EXO/AD room
   write. Do not close out until performed.
2. **Single-room Finder protected-principal gap** — **DONE** (2026-07-21, commit 2a97d09;
   `docs/ConferenceRoomsFinderProtectedPrincipalGate-Plan.md` Implemented). Consolidated the
   module PP check into one `ConferenceRoomProtectionGate` (C2-G). Only remaining follow-up is
   live-instance/UI validation not yet performed (runs against PROD from the dev instance).
3. **Module packaging/import — DEFERRED (owner, 2026-07-22)** as low-value/high-cost. Not to be
   worked on or raised as next; no plan. End-state direction retained only as history in
   `.agents/decisions.md` (2026-07-22 deferral, refining 2026-06-29 & 06-18).
4. **AccountLockout user-notification — PARKED with the module (owner, 2026-07-22).** The whole
   `AccountLockoutRemediation` module is disabled/deferred (unusable in this environment); the
   user-notification question is parked with it and will be decided only if the module is picked
   back up. Not to be worked on or raised as next.
5. **GM-3 self-service group management (on-prem AD only) — DONE 2026-07-27.** All 6 tasks (plan
   section 7) landed and codex-reviewed; see the `## Now` pointer and `docs/history/state-archive.md`.
   Only follow-up is live validation, not yet performed (runs against PROD AD from the dev instance). Not next.
6. **ASCII cleanup sweep + enforcement lint** -- **DONE** (2026-07-21). Scope narrowed by owner to
   code/logging only (`.cs`/`.ps1`/`.psm1`); docs, `.razor` UI, and `EmailService.cs` email emoji
   excluded. (a) Sweep landed commit `c2e2f6f` (329/329 char swaps, 77 files, 672 tests green).
   (b) CI gate `tools/Test-AsciiOnly.ps1` wired into `.github/workflows/ci.yml` `powershell` job,
   non-vacuity proven. See `.agents/decisions.md` 2026-07-21.

Ops track (not engineering): configure ConferenceRooms AD `DelineaSecretId` in the prod instance
(gates CR-1 in prod); `deploy.ps1` native `-PlanOnly` (workaround: `deploy-pipeline -PlanOnly`).

## Blockers / open gaps

- **CLOSED (2026-07-24) — ptk blocker + AD scan-sizing.** At the 2026-07-24 close ptk had been
  removed (server + the shell-blocking hook that forced AD calls through it), so the "ptk down is
  a STOP; no direct PowerShell fallback" rule no longer applied. (2026-07-28: ptk is available
  again this session — use it per global guidance when present.) The one AD read it had gated (a
  domain-wide `Get-ADGroup` count) was run directly: **41,368 groups** (now in the archive). Moot
  regardless: the scaled-back task-2 design (2026-07-24) dropped the domain-wide scan entirely.
  single-room Room Finder page path (`ConferenceRooms.razor` `SetupSingleRoom` →
  `SetRoomMetadataAndListAsync`) previously wrote with no PP gate. Fixed by consolidating the
  module's protected-principal check into one `ConferenceRoomProtectionGate` (C2-G
  guarded-execution helper): page Finder+Type and each bulk row route through `GuardThenRunAsync`;
  the write runs only when the gate clears; the two prior near-duplicate inline checks were removed.
  672 tests pass; non-vacuity verified. Plan Implemented
  (`docs/ConferenceRoomsFinderProtectedPrincipalGate-Plan.md`). **Live/UI validation not yet
  performed** (runs against PROD from the dev instance, same as Bulk Job Runner).
- **OPEN — AccountLockoutRemediation not yet exercised on dev** (owner deferred, 2026-06-29). Run
  the package's own Manual Validation steps (live 4740 read, WinRM, quser/logoff parsing, real
  dry-run+logoff, protected-block) when ready. Gates the rule-3 user-notify decision above.
- **Prod BlockedSenders version uncertainty:** the two BlockedSenders fixes (`17910f3`→1.0.1,
  `cde778f`→1.0.2) are module bumps, not app bumps, so "prod = app 2.3.27" does not confirm prod
  includes them. Confirm the prod build commit if BlockedSenders behaviour matters in prod.
- **All known protected-principal gaps CLOSED:** GAP 1 (`M365GroupManagementService`, 2026-06-29),
  GAP 2 (`MigrationService`, 2026-06-30), GAP 3 (ConferenceRooms Finder bulk, 2026-07-02), and the
  single-room Finder page path (2026-07-21, commit 2a97d09 — consolidated into
  `ConferenceRoomProtectionGate`). No known open PP gap remains. Governing rule:
  `.agents/decisions.md` 2026-06-29 + Constitution §Protected Principals.

## Verification

- **Code:** `dotnet build ExchangeAdminWeb.slnx -c Release` then `dotnet test ExchangeAdminWeb.slnx`
  (always target the `.slnx`; bare `dotnet test` runs zero tests). Add
  `dotnet format ExchangeAdminWeb.csproj --verify-no-changes --no-restore` and
  `git diff --check HEAD` where practical.
- **PowerShell:** `Invoke-ScriptAnalyzer -Path . -Recurse` (CI fails on Error severity only) and
  `Invoke-Pester tests/ps`. Deploy-host dependency for the ops scripts: `sqlite3.exe` on PATH.
- **Non-vacuous rule:** a change shipping with a new test must be proven — revert the fix, see the
  test fail, restore. Full policy + manual-check list: `.agents/repo-map.json`, `AGENTS.md`.

## Findings (environment / CI — still live)

- CI is real: it fails on real problems. Trust it. (`.github/workflows/ci.yml`, `windows-latest`.)
  Note: `dotnet format --verify-no-changes` treats analyzer *warnings* as fatal, so a stray
  warning (not just a failing test) reddens build-test. This bit master 2026-07-20..07-21: the
  Bulk Job Runner (`971555f`) left an xUnit1051 warning that kept build-test red for ~13 commits
  until fixed in `8c6f83f` (2026-07-21). Lesson: run the format check locally, not just the tests.
- On local macOS, a missing Windows COM DLL can nondeterministically drop xUnit collections (totals
  vary) — trust the failure *list*, not the total. `windows-latest` CI is unaffected. macOS builds
  need `-p:EnableWindowsTargeting=true`; Pester needs `pwsh` +
  `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec`.
- On this Windows dev box, `sqlite3.exe` is on PATH via winget; Pester runs under `pwsh`.
- `deploy.ps1` still lacks a native `-PlanOnly` (deferred with owner visibility;
  `deploy-pipeline -PlanOnly` covers the prod dry-run requirement).

## Active sources

- `AGENTS.md` — process/behavioral contract (Prime Invariants first).
- `docs/ProjectConstitution.md` — highest engineering authority.
- `.agents/decisions.md` — durable decisions (most recent: Bulk Job Runner, 2026-07-02).
- `.agents/repo-map.json` — automated verification map.
- Active plans: `docs/BulkJobRunner-Plan.md` (Implemented, live validation pending);
  `docs/ConferenceRoomsFinderProtectedPrincipalGate-Plan.md` (Implemented 2026-07-21,
  live/UI validation pending); `docs/MessageTraceDownloadLink-Plan.md` (Approved 2026-07-29,
  **In progress** -- slices 1-3 landed, slice 4 next).
- Review loop finding pp-finder-1: implemented and committed (`.agents/review/index.md`).

## Unrecorded repo memory

- None known. Engineering rules → `docs/ProjectConstitution.md`; module contract →
  `docs/AdminModuleSpec.md`; work-stream history → `docs/*-Plan.md` + git log.
