# Agent State

Current work and blockers only. Rules live in `docs/ProjectConstitution.md` and
`.agents/repo-guidance.md`, decisions in `.agents/decisions.md`, and machine observations in
`.agents/machines.md`. Each plan owns its implementation and manual acceptance checklist.
Superseded descriptions are verbatim in `docs/history/state-archive.md` (newest section first;
the latest sweep is Archived 2026-09-23).

## Now

**HANDOFF 2026-09-24.** On `master`, working tree clean. Gates are green **as of `bd29a9c`**
(build 0 errors, `dotnet test` 3064 passed / 0 failed / 3 skipped, format clean,
`git diff --check` clean, Pester 157/0, PSScriptAnalyzer 0 errors); every commit since is
docs-only, so nothing has been re-run against them. Nothing is half-finished.

- **TWO OWNER-REPORTED DEFECTS WERE PLANNED AND REVIEWED 2026-09-24. BOTH PLANS ARE DRAFT AND
  AWAIT APPROVAL; NO CODE IS WRITTEN. Each has ONE open owner question.** They jumped ahead of
  queue item 14, which is still the next *queued* item.
  - **`docs/RiskyUsersCompleteResults-Plan.md`** (`dc79994`, revised `fa0dd1b`). The module
    returns one 500-row Graph page in an unspecified order and runs the UPN search inside it, so
    a High-risk account visible in the Entra portal is absent from the page. This is the S4a
    slice `docs/RiskyUsersModule-Plan.md` deferred; both halves of its trigger have fired.
    Server-side UPN matching is ruled out on the Graph v1.0 reference (`$filter`/`$select` only,
    no documented `contains()`), so paging is the only route, and it needs `GraphTokenClient` to
    accept a guarded absolute `@odata.nextLink`. **FOUR slices.** Base app bump in S1; module
    bumps in BOTH S2 (`1.2.0`) and S4 (`1.3.0`).
    **Open Q1: may the page render several thousand rows, or render the top N by severity
    and say so** - the Defender "Rejoining the server" hazard. Blocks S3 only; it is the plan's
    only remaining owner gate.
    **S4 was added 2026-09-24 on a second owner report** (`b5052d8`, redesigned `71c5390`). The
    Remediate buttons read "Close as handled" / "This was the real user" / "Account was breached"
    and nothing on the page maps them to Entra's "Dismiss user(s) risk" / "Confirm user(s) safe" /
    "Confirm user(s) compromised" - the consequence text exists but only as a `title` attribute,
    so it is invisible on touch, to a keyboard and in a screenshot. This REFINES the 2026-09-02
    L2-wording ruling rather than reversing it: one string was doing two jobs.
    **The first S4 draft was rejected by the owner and the rule that replaced it is the durable
    part:** it proposed Microsoft's full toolbar strings on every button, and the owner answered
    *"we do not need the same long string on every button. that's not UI, that's your context
    leaking into the product."* **A control repeated on every row carries only what differs;
    everything shared belongs to the column header, the row, or the confirmation step.** So:
    `Dismiss` / `Safe` / `Compromised`, full Microsoft term as the accessible name, consequence
    sentence at the confirm step only. A test asserts no two labels share a word.
    `riskDetail` renders raw camelCase in the next column - same defect, also in scope, with the
    raw value as the fallback for anything unrecognised.
    **The hazard to carry: the three label strings live in TWO files** (`RiskyUsers.razor:759`
    and `RiskyUsersService.cs:165`) kept in step by a comment, and the service's copy reaches the
    outcome message. A half-rename makes the operator click one name while another is reported.
    Separately, `AuditActionFor` (`RiskyUsers.razor:745`) produces the STABLE ids
    `RiskyUsers_Dismiss` etc. that `AuditService.cs:215` stores - those are record keys and must
    NOT be swept into a rename.
    **Q3 is CLOSED coder-side** (relabel in place; the one-control-per-row restructure is
    recorded as deliberately not taken - it is a redesign and overlaps queue 14).
  - **`docs/SidebarScrollbar-Plan.md`** (`941cd77`, revised `afdacaa`). The sidebar scrollbar
    never leaves because the scroll pane overflows by a constant 0.7rem: the first category
    heading's `margin-top` collapses out of a `nav` that has no padding-top, `.nav-scrollable` is
    a BFC so it cannot escape, and `nav` is `min-height: 100%`. `nav` is a block, not flex -
    Bootstrap's `.flex-column` sets `flex-direction` only - which is also why `mt-auto` on the
    version footer does nothing. One slice, CSS only, base app PATCH bump. **Open Q1: may the
    version footer move to the bottom** (making `nav` flex activates the existing `mt-auto`).
  - **Both were openreviewed by codex** (`@azure-openai-eus2-global/gpt-5.5-dzs` @ xhigh,
    fallback, codex-cli 0.154.0), both `acceptable_with_changes`, **three material changes each,
    all six admitted and folded in**. Each plan's `## Review` section owns the detail.
    **The one worth carrying:** the Risky Users fix as first drafted would have shipped and done
    nothing. Redefining `MaxRows` from page size to total ceiling reads back the value the owner
    stored on 2026-09-02 - 500 - so the ceiling would have been 500 and the module would still
    have fetched one page, from correct code. Gates and a dev deploy would both have passed it.
    A new `MaxTotalRows` key instead. **Also found while verifying the reviewer's own citations:
    `docs/AdminModuleDeveloperGuide.md` contradicts itself** - `:645` requires Graph endpoints to
    start with `/`, `:649` requires following `@odata.nextLink`, which is absolute. That is the
    root of the defect, and why `M365GroupManagementService.cs:304-309` and
    `NamedLocationsService.cs:78-84` each hand-strip the base URL. S1 amends the guide.

**QUEUE ITEM 14 remains the next queued task and it is a PLANNING task** - see the entry below.
Cloud Password Reset is complete and reviewed; queue 4 slice 1 is landed with slices 2-4 unstarted
and unblocked.

- **QUEUE 8 (Defender for Endpoint) IS PARKED BY THE OWNER, 2026-09-22, waiting on an updated
  requirements document from the stakeholder. Do not resume it until that arrives.** The module
  (`43a944e`) is not deployed and not resumed - the park stands.
  **Two defects are open against the shipped module and neither is fixed:**
  1. *The page is unusable at tenant scale.* It renders every device row, so the Blazor circuit
     dies ("Rejoining the server...") and nothing can be scrolled. The owner rejected virtualised
     scrolling outright - "not a solution in ANY way" - on the ground that 40,000 rows behind one
     scrollbar is useless however it is rendered. **The unanswered fork, put to the owner and not
     yet resolved: make browsing filter-first and paged (portal-style, 50 at a time) and leave the
     complete partitioned fetch for CSV export only.** Do not implement either shape without a go.
  2. *Discovery sources is the wrong data and mostly blank.* The stakeholder needs **which local
     machine discovered a new device**. The column shows which Microsoft product saw it and when.
  **What the live hunting queries settled on 2026-09-22** (`.agents/research/defender-discovery-source.kql`,
  results run by the owner in the Defender portal - these are measured, not inferred):
  - **Advanced hunting retention is exactly 30 days.** `DeviceInfo` spans 2026-08-23 to 2026-09-22,
    31,594,567 rows. That is the whole reason most discovery cells are blank: the inventory holds
    devices last seen in March through August, and there is no hunting row left to join to. No code
    change widens this.
  - **`HostDeviceId` is a dead end.** Populated on **1 device out of 113,102**, and that one row
    points at itself. It is not a discovery relationship.
  - **`DeviceNetworkInfo` carries nothing either** - adapters, IPs, MACs, no discovering device.
  - **`DeviceInfo` has 52 columns and none is a "discovered by".** Full schema in the CSV the owner
    ran; the candidate list is exhausted.
  - **The tenant holds 113,102 devices in `DeviceInfo`** (50,835 onboarded, 12,902 can be onboarded,
    30,637 insufficient info, 18,728 unsupported) - materially more than the 40,000 the rebuild was
    sized against. The partition handles it; the 100,000 ceiling does not.
  **The one open question, asked and unanswered:** does the Defender portal itself show a
  discovering or onboarded machine on a "Can be onboarded" device's page? If it does, the data is
  obtainable and the route needs finding. If it does not, the requirement cannot be met as written
  and the stakeholder's document has to change. Ask this before doing anything else on queue 8.

- **NEXT AGREED ITEM: queue 4, break out permissions for message trace vs header analysis.**
  Picked 2026-09-22 when the owner parked queue 8 and said "pick and handoff". Reasons it was
  chosen over 2, 5 and 6: it is self-contained, needs no new credential or app registration, has
  no blocked decision in front of it, and `.agents/state.md` already named it "the obvious
  alternative" when item 7 was taken. Item 2 is blocked on a decision about a second status
  source; items 5 and 6 were both re-scoped by the owner on 2026-09-21 (`.agents/decisions.md`)
  after this pick was made, so the reasons given here for skipping them no longer hold - see the
  weekend-run entry below for each item's current standing. **The plan already exists and is
  approved: `docs/MessageTracePermissionSplit-Plan.md` (it owns its own `Status:` header - read
  it there), codex consensus after three rounds (`155eaf7`, `c3b4239`, `5e69dd9`, `995b673`),
  drafted during the weekend run.**
  **ALL SEVEN of the plan's open questions are now settled and the plan body matches.** 1 and 2 by
  the owner 2026-09-21 (re-grant deliberately, alias `MessageTraceSearch`); 4 by the owner
  2026-09-22 (**hide** the Trace Search tab, overruling the plan's disabled-tab recommendation);
  3, 5, 6 and 7 as coder-side calls the same day, because a rule or a precedent already decided
  each - reports route and CSV export both move behind the granular, module version takes the
  MINOR position per the Migration button gate precedent, and the Roslyn harness improvement stays
  out of this work stream. Rulings are in `.agents/decisions.md`; the plan's Open questions section
  records each with its reasoning.
  **The hide ruling did real design work, not cosmetics:** `MessageTrace.razor:878` switches to the
  trace tab in code from the header-analysis handoff and can run the trace outright, so removing
  the tab button leaves that route open. Section 4 now requires the handoff control hidden, the
  switch refused and `RunTrace` gated server-side regardless.
  **SLICE 1 IS LANDED: `9d97e4b`.** Owner gave the go 2026-09-22 and
  authorized an Opus 5 coding subagent for it. The granular `MessageTraceSearch` is declared on the
  `MessageTrace` descriptor, the main permission's description no longer claims to grant trace, and
  the module version went `1.4.2` -> `1.5.0` (minor, per question 6; the plan text predates 1.4.2
  and the coder computed from the field instead of the plan). `ExchangeAdminWeb.csproj` untouched,
  verified by diff. Four new/changed catalog tests; the alias count in
  `Catalog_GetConfigurablePolicyAliases_MatchesExpected` went 42 -> 43, not the 41 -> 42 the plan
  predicted, for the same reason. Suite 2951 passed / 0 failed / 3 skipped; guard proof mutated the
  descriptor and all four tests failed for four distinct reasons, then restored with the mtime
  touch. **The slice is inert: nothing outside `ModuleCatalog.cs` consults the alias, so nobody's
  access changes.**
  **THE MANDATORY OWNER STEP IS DONE, 2026-09-22 - slice 2 is unblocked.** The owner deployed slice
  1 (the Access tab shows `v1.5.0`) and saved the grants, evidenced by a screenshot of the Module
  Config Access tab: `MessageTrace` holds 2 groups (`ANALOG\ExchangeWebAdmins`,
  `ANALOG\ExchangeWebPerms`), `MessageTraceSearch` holds 1 (`ANALOG\ExchangeWebAdmins`). That is
  the deliberate re-grant the owner's 2026-09-21 ruling called for, not a copy: trace search is
  narrower than module access by one group, which is the whole point of the split.
  Remaining work is slices 2, 3 and 4 (four commits total, not three as an earlier version of this
  entry said). **Nothing enforces yet** - slices 3 and 4 are what make these grants bite.
  **SLICE 1 WAS REVIEWED AND ONE DEFECT WAS FOUND AND FIXED: `db092e8`, module `1.5.0` -> `1.5.1`.**
  codex / `@azure-openai-eus2-global/gpt-5.5-dzs` / xhigh / standard over `9d97e4b^..9d97e4b`,
  verdict `unsound`, one MEDIUM, no CRITICAL and no HIGH; record `.agents/review/findings/mtps-1.md`.
  **It cleared the property slice 1 exists to hold** - the alias is declared and no runtime check
  consults it - so inertness is confirmed rather than asserted. The defect was the admin-facing
  copy: both Access-tab descriptions described the FINISHED split while every gate still accepts
  the parent policy, so the page told the owner that `ExchangeWebPerms`, holding the parent alias
  alone, cannot search traces. It can. Fixed with transitional wording plus a tripwire test that
  fails until slice 3 flips it; the plan gained step 11a to make that flip a numbered step.
  **DEPLOYED AND VERIFIED BY THE OWNER 2026-09-22**, together with the Access tab labels
  (`bdd01a8`) and the version bumps. The corrected copy is live on dev.
  **A rule slices 2-4 must not break, kept live here because no other file states it:** the
  aliases `MessageTrace` and `MessageTraceSearch` must NOT be renamed. Each is the section-access
  storage key and the store is fail-closed, so a rename orphans the groups granted against it and
  denies them rather than failing open. Operator-facing wording is changed through the
  `ModulePermission.DisplayName` field instead (added in `bdd01a8`, rotated to
  `docs/history/state-archive.md` 2026-09-23); the alias stays on screen because it is what the
  denial log line names.

- **CLOUD PASSWORD RESET (queue items 10 and 11) IS BUILT, REVIEWED AND NOT DEPLOYED. It has never
  run against a real tenant.** `docs/CloudPasswordReset-Plan.md`, `Status: Implemented, unproven
  against the live service`. Module `1.0.0`, base app `2.22.0` -> `2.23.0` (the `EmailService`
  method is shared infrastructure). Module doc: `docs/CloudPasswordReset.md`.
  Nine commits: generator `c219a27`, service and the forest-wide employeeId lookup `56451bd`,
  owner email `e1b786a`, descriptor and preflight page `8a411c1`, write path `3b29e5f`, records
  `dad322e`, then the review fixes: `3190262` (four write-path findings), `ec613d0` (the audit-shape
  test that closed cpr-11's recorded gap and found AC18's one real exception) and `5d2913e` (two
  generator findings).
  **The design in one line:** the operator names a cloud-only account; the module reads the
  `employeeId` off it, finds the one directory user carrying that id, generates a password, writes
  it to Entra and mails it to that person. **The operator chooses no address and normally never
  sees the password** - which is the control that the earlier operator-typed design had given up.
  **Five destination refusals, none falling back to anything:** no employee ID, no match, more than
  one match, owner has no mailbox, lookup failed. A `CloudPasswordResetReveal` holder overrides all
  five including the lookup failure (owner ruling 2026-09-23, `.agents/decisions.md`), which settles
  D4 in favour of keeping that permission - with the destination derived it is the only route to a
  password and therefore a real boundary.
  **Force change at next sign-in defaults ON** (owner ruling 2026-09-23): the password travels by
  email, so forcing a change makes it a one-time handover. Clearable per reset, and clearing it
  shows the owner's verbatim instruction.
  **EVERY SLICE WAS REVIEWED BY CODEX. Every one came back `unsound`; ten findings, all admitted
  and fixed** - `.agents/review/findings/cpr-4.md` through `cpr-13.md` plus the index. Four HIGH,
  five MEDIUM, one LOW.
  **Four of the ten were the same mistake in different places: an unanswered question read as a
  negative answer** - a missing sync property read as cloud-only, a failed role read rendered as
  "None active", a lost PATCH response audited as "no change was made", and before them the
  deleted survey's confident 0%. That rule is now stated in the plan and pinned by tests.
  **FOUR were guards that existed and did not bite**, which is the more uncomfortable pattern: a
  test asserting the destination was re-derived passed on code that re-derived it from a stale
  cache, because it checked that the call happened rather than what it read from; a test asserted
  a tolerance where the plan states a guarantee; the audit-shape claim was invisible to
  source-text guards until a test read the emitted JSON back; and **AC15's entropy floor was
  covered only by a test that the CONSTANT was 60.0** - found by an acceptance-criteria audit
  AFTER every codex review had passed, and confirmed by a probe in which all 34 generator tests
  stayed green with the acceptance check deleted. **Codex did not catch that one and neither did
  five review rounds; walking the ACs one by one did.**
  **BLOCKED ON THE OWNER, and nothing works until these are done:**
  1. Create a dedicated Entra app registration with `User.Read.All`,
     `User-PasswordProfile.ReadWrite.All` and `RoleManagement.Read.Directory`, admin-consented.
     **Do not reuse another module's registration.**
  2. Create the Delinea record (`Tenant ID`, `Application ID`, `Client Secret`, no checkout
     workflow) and enter its id in the module's `GraphDelineaSecretId`.
  3. Deploy, enable the module (it ships disabled), and grant the section-access groups.
  4. Run the manual acceptance checklist in the plan. **None of it has been run.** The two items
     that matter most: a deliberately contrived two-people-share-an-employee-ID case, which is the
     failure that would otherwise mail an admin password to a guess; and the CLEARED
     force-change case against an account whose sign-in path cannot service a change prompt,
     because that path is the reason the checkbox still exists.

- **NEXT SESSION STARTS HERE: QUEUE ITEM 14, REDESIGN THE MIGRATIONS INTERFACE. It is a PLANNING
  task and the owner said so - "handoff so I can plan in a new session".** Added to `queue.txt`
  2026-09-23, verbatim:
  > 14. Precedes 12 & 13. Design a better, safer, and easier to use interface for managing
  > migrations. multi-pane or tabs or something that makes it less dense and easier to use without
  > UI-induced error. no burying controls off the screen, no hiding selected items off the page.

  **"Precedes 12 & 13" is the instruction that matters.** Items 12 and 13 are both additions to the
  Migration page, and the owner has said the page cannot take them in its current shape. So the
  order is 14 then 12 and 13, and **12 and 13 must not be started first** - building either into
  the existing layout is the thing item 14 exists to prevent.
  **What the owner named as the failure mode, in their own words:** "UI-induced error", "no burying
  controls off the screen", "no hiding selected items off the page". Read that as a constraint on
  the design rather than a style preference - it says an operator has made, or nearly made, a
  mistake because a control or a selection was not visible.
  **No plan exists and none should be written before the design question is put to the owner.**
  "Multi-pane or tabs or something" is explicitly a question, not a specification.
  **Where the relevant code and prior art are:** `Components/Pages/Migration.razor` is the page;
  `docs/MigrationBatchSelection-Plan.md`, `docs/MigrationButtonGating-Plan.md` and
  `docs/MigrationStaleReport-Plan.md` are the recent work on it. Migration is a CONVERTED page in
  `ExchangeAdminWeb.Tests/ClickGateRegistry.cs`, so any control the redesign moves or adds has to
  satisfy that suite - a layout change here is not cosmetic to the tests.
  **Two things a redesign should carry that are already known defects of the current page**, both
  recorded below in the queue 12/13 entry: destructive per-user actions in a dense table, and no
  per-row result reporting for a bulk selection.

- **QUEUE ITEMS 12 AND 13, BOTH ON MAILBOX MIGRATIONS, BLOCKED BEHIND ITEM 14. Read 2026-09-23;
  nothing started, no plan written for either.** Both are new operator-facing capability on a
  mutating module, so the Constitution's Planning Rules require a written plan before
  implementation - and item 14 now precedes both by the owner's instruction.
  - **Item 12 - scheduled completion.** Verbatim: *"Add option for CompleteAfter attribute in
    migration app so users can schedule migration completion, same ticket requirements as the
    other options."* **The plumbing is half there and that is the trap:** `MigrationService.cs:490`
    already passes `CompleteAfter` to `Set-MigrationBatch`, and `:698` to `Set-MigrationUser` - but
    both pass a time in the PAST (`AddHours(-1)`, `UtcNow`) to mean "complete now". The item asks
    for a FUTURE time, which is the attribute's actual purpose. So this is not "wire up an unused
    parameter"; it is a new operator input, a datetime, that changes what those two call sites
    mean. `:596` also reads `CompleteAfter != null` as a boolean "auto-complete" flag, which stops
    being a safe reading once a real schedule can be set.
  - **Item 13 - per-migration selection and actions.** Verbatim: *"Add checkboxes for individual
    migrations when the batch is expanded, and add Complete, Remove, Pause, Go, etc. options. name
    appropriately, my names are guesses."* Two things in one: row selection inside an expanded
    batch, and a set of per-user actions. **The owner explicitly delegated the naming** - the
    Exchange cmdlets are `Complete-MigrationBatch`/`Set-MigrationUser -CompleteAfter`,
    `Remove-MigrationUser`, `Stop-MigrationBatch`/`Stop-MigrationUser` and
    `Start-MigrationBatch`/`Resume-MigrationUser`, so the plan should propose names from what the
    actions DO rather than transliterate the guesses.
    **This one needs the most care of anything in the queue right now.** It adds destructive
    per-user actions to a table, which the Developer Guide's UI standards warn against directly
    ("avoid putting destructive actions directly in dense tables when a confirmation edit/detail
    view is more appropriate"), and bulk selection turns Known Failure Class 2 - success
    aggregation - from theoretical into the dominant risk: a loop over N selected users must
    report per-row outcomes and must never collapse partial success into a blanket result.
    `docs/MigrationBatchSelection-Plan.md` and `docs/MigrationButtonGating-Plan.md` are prior art
    for the selection and gating shapes respectively.
  - **Both inherit the module's existing obligations:** ticket required (the owner said so for 12
    and it applies equally to 13), protected-principal gate on every write target, audit per
    action, admin notification, and the click-gating rules - Migration is already a converted
    page in `ClickGateRegistry.Pages`, so any new control must satisfy that suite.
  **Also noted from the same read:** the owner has been maintaining status markers in
  `queue.txt` - 1, 3, 4, 7 are marked DONE, 6 ON-HOLD, 8 HOLD FOR REQS DOC, 10 and 11 IN-PROGRESS,
  and **9 is marked "UPDATE / IN-PROGRESS?"**, which reads as the owner asking where it stands
  rather than stating it. State says tier 1 is complete and tiers 2-4 are audited but unapproved;
  that is the answer if they ask. **Do not edit `queue.txt` - it is the owner's file and says so
  on its first line.**
- **THE WEEKEND BACKLOG RUN IS AT ITS END STATE (2026-09-18 to 2026-09-19); tier 1 is complete
  and only owner-blocked work remains. Read
  `.agents/decisions.md` 2026-09-18 "Weekend backlog run" for the authority it ran under - that
  authority lapses when the owner's goal is cleared, not before** - while it stands, plans
  self-approve on codex consensus, pushes go to both remotes, and implementation subagents are
  authorized. Afterwards the standing rules resume: ask-first pushes per
  `.agents/push-policy.md` and the Token Budget one-slice-one-session rule. 32 commits, every
  gate green at every commit. Per-item outcome:
  - **Queue 9, click-gating: TIER 1 IS COMPLETE, all nine pages.** See the entry below.
  - **Queue 8, Defender for Endpoint - REBUILT FOR SCALE after the first live run returned nothing.
    Module `1.1.0`.** `docs/DefenderEndpointDevices-Plan.md` owns its own `Status:` header (it
    still reads `In progress` - R1 (a)-(e) and the acceptance checklist are what is unfinished).
    Five slices landed first: S1 `4835942` (API client, models, service, paging),
    S2 `a4facc6` (descriptor, page, three config fields), S4 `8f2aa37` (discovery-sources
    enrichment), S3 `d4993f6` (CSV export, 27 columns), S5 `bb37bc9` (`docs/DefenderEndpointDevices.md`
    and the README section). **Then the dev deploy proved the design could not work here.**
    `GET /api/machines` returned exactly 10,000 rows and **no `@odata.nextLink`** - R1(g) answered
    empirically, there is no cursor on this endpoint - and the tenant holds over 40,000 devices, so
    the module refused and showed nothing. R1(f) answered the same way: the 20,000 ceiling was below
    the size of a real tenant.
    **The rebuild divides the QUESTION instead of paging the answer.** The inventory is partitioned
    on `lastSeen` into disjoint, exhaustive ranges; a part that returns under the cap has proved
    itself, a part that returns AT the cap has proved nothing and is split in two and asked again,
    and the answer is the union of the parts that proved themselves. Devices with no `lastSeen`
    satisfy neither bound, so they ride the undivided root request and get an explicit
    `lastSeen eq null` part the moment the root splits. Cost is driven by the ratio of matching
    devices to the cap, not by the device count: a 4.3x ratio measures at 12 requests against a
    250-request budget. Ceiling default 20,000 -> 100,000.
    **The two hardcoded filters are gone and the portal's set is in.** Device name prefix, onboarding
    status, platform, health status, risk score, exposure level and the Last seen window go to the
    API as `$filter`; machine tag, machine group, First seen and "any Windows" cannot be expressed
    against this collection and are applied after the fetch, labelled `(after fetch)` on the page and
    named in the ceiling refusal as filters that will NOT make the fetch smaller. The
    Windows-devices-only checkbox and its grey paragraph are gone; Platform is a dropdown defaulting
    to Any.
    **Every user-facing string states what happened and what to do** - owner ruling 2026-09-21, after
    the refusal text was called AI garbage. No design rationale, no self-reference, no repetition.
    Suite **2949 passed / 0 failed / 3 skipped**. Module `1.0.0` -> `1.1.0`, **no base app bump** -
    `ExchangeAdminWeb.csproj` byte-identical, verified by diff rather than assumed.
    **Codex reviewed the rebuild** (`.agents/review/q8-scale.result.json`): `unsound`, 1 MEDIUM and
    4 LOW, **no CRITICAL and no HIGH**. It cleared the completeness argument, termination and the
    filter ordering outright. All five closed on the coder-side guard proof (4 probes, Revision 4),
    which is the CRITICAL-only rule from `.agents/decisions.md` 2026-08-31, not a shortcut.
    **Codex also reviewed the pre-rebuild module: round 1 `unsound` with two findings, round 2 `sound`
    with none** (`95668c5` closed them; `.agents/review/q8-module*.result.json`). Round 1 **cleared the two
    properties the design exists to hold** - no path renders or exports a short device list as
    complete, and no page or CSV blank can mean a failed enrichment run.
    The HIGH finding is worth remembering: **the call site's own comment convicted the code.** It
    said enrichment failure "must never take the page down"; the method handled a null client and a
    status-bearing result and nothing else, while the credential read throws three ways and the token
    request throws on any non-success - so a *completed* inventory was replaced by a page-wide
    failure. Fixed with two narrow try blocks filtered to five named types, deliberately **not** a
    blanket catch: the merge code sits outside them, because a bug that greys five columns and blames
    Microsoft is a bug nobody finds.
    **Still the owner's, and nothing in the code can substitute:** create the app registration, obtain
    BOTH consents (`Machine.Read.All` on WindowsDefenderATP; `ThreatHunting.Read.All` on Graph, which
    needs a **Privileged Role Administrator or Global Administrator** - an Application Administrator
    cannot consent Graph app roles), create the Delinea record with fields named exactly `Tenant ID`,
    `Application ID`, `Client Secret` and no checkout workflow, enter its Secret ID on the module's
    config page, enable the module (it ships disabled) and grant the section-access group.
    **Then two gates that have never run:** R1 (a)-(e), the rest of the live reconnaissance pass, and
    the manual acceptance checklist. R1(f) and R1(g) are answered - the run of 2026-09-21 established
    the 10,000-row cap, the absent cursor and the tenant size - but no device row has ever been
    rendered, so no field name, casing or filter behaviour has been observed. `lastSeen eq null` is
    the one clause in the rebuild with no worked example on this collection: the code fails closed and
    names the action if the service rejects it, but that has not been seen either. R1's open items and
    the fallback that shipped for each are listed in the module doc; the two that matter are whether `ipAddresses` comes
    back populated (the IP and MAC columns shipped ahead of that answer, Revision 7 carries the undo)
    and whether `DiscoverySources` arrives as a JSON string or an array (both shapes handled,
    Revision 6). Plan questions Q2-Q5 remain unanswered and the shipped behaviour in each case is the
    plan's proposal, not a ruling.
  - **Queue 4, trace vs header-analysis permissions** - `docs/MessageTracePermissionSplit-Plan.md`,
    **codex consensus after three rounds**, `Status: Approved, in progress`. All seven questions
    settled and slice 1 landed 2026-09-22; see the Now entry above. **Deploy
    hazard, stated first in the plan on purpose:** a new fail-closed alias denies EVERY operator
    including the owner until a group is stored against it, and the alias cannot be granted before
    the descriptor deploys - hence three commits with a mandatory deploy boundary. Recovery needs
    a GLOBAL admin, not a module admin.
  - **Queue 6, containerize - THIS ENTRY'S VERDICT IS FALSIFIED. The owner OVERRULED it on
    2026-09-21** (`.agents/decisions.md`, verbatim *"we need to containerize the app and it needs
    to work. fix."*), after the recommendation and its blocker were put to them and they
    reaffirmed. `docs/Containerization-Feasibility.md` stands as the analysis, **not** as the
    recommendation; do not re-litigate the verdict. What the analysis established still binds and
    is enumerated in that decision: the Delinea bootstrap credential needs a provider seam,
    Windows containers only, gMSA plus SPN registration, the UNC guard would falsely pass, and the
    jobs/usage/log files land in scratch space. No plan is written. See `## Blockers` for the part
    that is not ours to do.
  - **Queue 2, status.cloud.microsoft** - `docs/ServiceHealthPublicStatus-Plan.md`, `Status:
    Draft`, **PARKED after FOUR codex rounds, by judgement rather than blockage.** Each round found real
    defects, but rounds 2 and 3 found them in the verification apparatus rather than the design,
    and that apparatus is now larger than the feature. **Open question 1 decides whether it is
    worth building at all:** the public page carries ONE SENTENCE per surface, not a second
    incident list, so it will never show an Exchange incident Graph missed. If the owner expected
    a second incident feed, the right deliverable is the ten-line link-out in section 16 and
    nearly all of the plan evaporates. **Round 4 re-affirmed section 16 as honest scoping rather
    than an escape hatch**, after three further rounds of growth - so the plan is trustworthy about
    its own limits. It also caught a hazard this run created: the plan said the base app version
    stays 2.21.0, which `3c21270` falsified, so a literal implementer would have DOWNGRADED three
    version fields. Fixed. Three findings are recorded and deliberately unfixed as
    pre-implementation work - they are cheap once the feature is known to be wanted and wasted
    otherwise.
  - **Queue 5, other tenants/domains** - `docs/MessageTraceMultiTenant-Plan.md` (Draft; the plan
    owns its status). **THE "MAY BE ZERO WORK" READING RECORDED HERE IS FALSIFIED.** Owner,
    2026-09-21 (`.agents/decisions.md`): *"there's one other tenant. creds for that tenant will
    live in delinea."* So this is the SEPARATE-TENANT case, not extra accepted domains on the
    existing tenant, and its hardest blocker stands: per-tenant authorization has no expression in
    the current model. **One assumption was stated to the owner rather than asked, and it changes
    the design if wrong:** tenant 2 is being designed around a client secret in Delinea, because
    the existing tenant does not authenticate that way - it uses a `LocalMachine\My` certificate
    plus AppId/Organization from module config, and `MessageTrace`'s `DelineaSecretId` is the
    on-prem credential only.
- **A live defect was found while scoping queue 4 and is NOT fixed. It needs its own commit.**
  `Components/Pages/MessageTraceReports.razor` carries only the main `MessageTrace` policy
  (`:4`, `:126`), and `Exports.GetExports()` (`:143`) calls
  `_jobs.GetFinishedByType(ModuleName, JobType, ListLimit)` with **no submitter filter** - the
  table renders `@item.SubmittedBy` per row (`:78`) precisely because it is an all-operators
  listing - while `Download` (`:146`) serves any listed export to any policy holder. So every
  operator can download every other operator's full message-trace detail exports. This is
  independent of the permission split; the split makes it worse by admitting header-only
  operators to that page. Not caused by this run's work.

- **A defect in committed TEST infrastructure, found in plan review and not yet fixed.**
  `ExchangeAdminWeb.Tests/ClickGateSource.cs:364-384` (re-verified 2026-09-23 as of `26b414b`;
  the earlier `:213-228` citation was stale), `ExtractBlock`, counts `{` and `}`
  without being quote-aware, so a brace inside a string or interpolated string miscounts the
  depth and the extracted block ends in the wrong place - usually over-capturing into the code
  that follows. **The asymmetry is the tell:** `Tags()` in the same file IS quote-aware and its
  doc comment explains exactly why a naive scan breaks; `ExtractBlock` never got the same
  treatment. Consequence measured in the queue-4 review: an assertion looking for a `return`
  inside a denial block can find one that is actually in later code, so a handler that performs
  a protected operation after an authorization denial would pass. It is used by the live
  click-gating suite, so any assertion built on the block's END offset is suspect; assertions
  that only test containment within a generously-sized block are less affected. **Its own slice
  with its own guard proof** (an interpolated string containing a brace, placed inside an
  extracted block); deliberately not folded into another agent's work.
- **Mailbox Migrations no longer shows a stale open report; only the manual checks remain.**
  `docs/MigrationStaleReport-Plan.md` is Implemented and owns the acceptance checklist. Landed
  2026-09-17 in `3e4f13d`, with the review fix `2b93fdc` on top; Migration module version 1.8.2,
  no base app bump.
  The earlier batch-name-caching hypothesis was FALSIFIED by reading the page: the report was
  never keyed on batch name, and that fix would not have touched the reported repro, which
  reuses the name. Actual cause: `userReport` is a one-time snapshot, the render guard at
  `Migration.razor:771` keys on the email address alone, and seven paths reloaded or discarded
  `batchUsers` without clearing it. One `CloseUserReport()` helper now owns the clear at all
  seven, and a `reportGeneration` counter drops the result of a fetch a reload superseded.
  Ten source-level tripwires guard it, anchored per branch; all three guard-proof mutations
  bit. Nothing here reaches the rendered page - no bUnit harness exists - so the plan's manual
  checks are the only evidence that the operator sees the fix. **Owner 2026-09-17: those
  checks are deferred indefinitely, not skipped** - migrations are production actions, and the
  owner will verify opportunistically the next time a real migration comes up. Do not re-queue
  them as blocking work and do not treat their absence as drift. **The implementation codereview is done** -
  codex, `@azure-openai-eus2-global/gpt-5.5-dzs` at xhigh, standard tier, over
  `1250220..3e4f13d`, verdict **findings (1)**, 2026-09-17, capability proof passed. Four of the five things it was
  asked about came back clean and stand as reviewed - no eighth reload path, the
  generation guard correct on all three continuation paths, no half-populated field
  combination, and the module-only bump right. The fifth produced **`msr-1`** (MEDIUM,
  `.agents/review/findings/msr-1.md`), **fixed 2026-09-17 in `2b93fdc`**: closing the report
  before the await was not enough, because the refresh-in-place paths leave the old rows and
  their **Report** button rendered for the whole Exchange call. One helper,
  `ReplaceBatchUsers`, now owns every assignment of `batchUsers` and closes the report
  itself, so the close lands after the await. **The lesson worth keeping is about the tests,
  not the code:** the original ten tripwires asserted that the close precedes the refetch,
  which the defective code did, so they passed the bug - the guard proof's first mutation is
  literally the pre-fix code and only the three new tripwires caught it. Assert the negative
  per occurrence (nothing assigns `batchUsers` outside the helper), not the ordering per
  method.

- **Module ordering is alphabetical and `SortOrder` is gone; the sidebar check is unrun.**
  `docs/AlphabeticalModuleOrdering-Plan.md` is Implemented and owns the manual acceptance
  checklist. Landed 2026-09-15 in `e5a8ccd`, `73be51c`, `7057f9c`, `12b699f`, `5090996`,
  `b657ac0`; app version 2.21.0. Every catalog-driven list now orders by `DisplayName` with
  `StringComparer.OrdinalIgnoreCase`, nav categories are named from `Modules/ModuleCategories.cs`,
  and the bottom sidebar block is selected by `ModuleCategories.Administration` rather than the
  old integer threshold. A new module declares no position: the author picks a category and the
  name decides the order. Blazor markup has no test harness here, so the rendered sidebar,
  Module Config tree and home tiles are unverified by automation - run the plan's manual checks
  after a dev deploy. The plan's implementation codereview has not been dispatched.

- **Service Health is implemented; design acceptance and manual checks remain.**
  `docs/ServiceHealth-Plan.md` owns the design and checklist. The original dashboard appearance
  is binding: no redesign, charts or gradients; incident HTML must pass through the sanitizer.
  Enablement, Graph secret ID and section access are now set; secret validity and live Graph
  authentication were not checked. Implementation codereview remains outstanding and needs a go.

- **Shared config cutover is reflected in both deployed appsettings files.**
  `docs/SharedConfigDb-Plan.md` is Implemented and its implementation codereview is closed.
  Do not repeat the one-time cutover based on the old state. Section 8 manual checks (startup
  logs and cross-instance refresh) remain unrecorded. Section 9's decision-wording and
  Constitution flags remain open. Jobs and usage stores stay per instance.

- **Usage telemetry and the save-then-prompt fix are landed.**
  `docs/UsageTelemetry-Plan.md` owns the telemetry implementation and acceptance checklist;
  `.agents/review/findings/utei-{1,2,3,4}.md` own the closed findings. The forced-reload fix is
  guarded by `AdminPageDirtyStateTests.ClearingDirtyStateIsRenderedBeforeAForcedReload`.
  Nothing is queued code-side. Run telemetry's manual checks and confirm saving module
  enablement no longer asks to discard already-saved changes.

- **Group bulk actions and cross-domain fixes are landed; live writes remain unverified.**
  `docs/GroupBulkActions-Plan.md` owns the checklist. Validate remove/re-add and bulk operations
  on a throwaway group: the original admin group is protected and cannot serve as the repro.
  Self-service's home-domain users-only add scope remains intentional. D1 used its drafted
  default (one batch administrator email, per-member audit and affected-user notices);
  the owner may overrule it. Implementation codereview remains outstanding and needs a go.

- **Intune Devices and Risky Users are implemented; remaining manual checks stay live.**
  `docs/IntuneDeviceManagement-Plan.md` and `docs/RiskyUsersModule-Plan.md` own scope and checks.
  Both registrations were proven live in owner validation recorded 2026-09-02; shared-store
  configuration remains present in the dated machine receipt. The owner confirmed Intune
  search on dev 2026-09-03 after `acfabe9`, superseding the earlier combined-filter uncertainty.
  Intune notification/Entra removal are act-time choices, not Module Config defaults.
  Risky Users reads audit without alert emails. Its direct ServiceNow validation, instead of
  the newer per-module seam, remains an unscheduled judgment call, not an admitted defect.

- **The remaining implemented queue awaits manual acceptance only. Do not restart it.**
  Checklists: `docs/BooleanConfigControls-Plan.md`, `docs/BitLockerMandatoryTicket-Plan.md`,
  `docs/ModuleCsvExport-Plan.md`, and `docs/EventLogCsvTicket-Plan.md`. The sidebar Home link
  removal (`2128610`) also needs a visual check; the brand link stays. BitLocker CSV includes
  keys under the owner's ruling, with ticketed disclosure audit and no keys in audit logs.

- **Nesting and protected group targets are implemented; remaining checks stay live.**
  `docs/GroupMemberNesting-Plan.md` and `docs/ProtectedGroupWriteTarget-Plan.md` own the lists.
  Cross-domain member listing was confirmed in both group modules on dev 2026-08-31;
  nested add/remove, cross-domain picker behavior and admin refusal still need applicable
  checks. Self-service is exempt from Protected Group Targets by the 2026-08-31 ruling;
  member protection stays. Review loops are closed; pgwt-3 remains declined at intake
  (`.agents/review/pgwt-3.contested.md`).

## Next

- **QUEUE 9 CLICK-GATING: TIER 1 IS COMPLETE. All nine approved pages converted, plus the
  harness, plus five defects fixed in already-shipped code. Tiers 2, 3 and 4 remain UNAPPROVED.**
  `docs/ClickGatingAudit-Plan.md` owns the findings and Revision 1 owns the design; read Revision 1
  before touching any page. **The plan's own acceptance checklist is the outstanding work and only
  the owner can run it** - nothing in this repo reaches the rendered page, there is no bUnit
  harness, and every assertion is a source-level scan that proves a shape, never a behaviour.
  Pages, in the order converted: DhcpAuthorization, NamedLocations, MailboxPermissions,
  CalendarPermissions, IntuneDevices, GroupManagement, M365GroupManagement, ConferenceRooms,
  SelfServiceGroups. Harness grew 14 -> 31 assertions + 3 fixtures, 266 instantiated cases;
  full suite 2510 -> 2791. Base app 2.21.0 -> 2.21.1 once, for the shared-component change only.
  **Defects found in code that had already shipped and been reviewed, all fixed:**
  1. `Migration` - two `@onkeydown` paths reached operations their buttons refused; one destructive,
     and it avoided double-execution only by accident. Seven bespoke tripwires and a codex review
     had missed them because nothing could see a keyboard path.
  2. `MailboxPermissions` - a tab click mid-write **revoked the permission the operator asked to
     grant**, and mislabelled the audit row and both emails on the way past.
  3. `CalendarPermissions` - the audit row and both emails said *Set* while the code performed a
     *Remove*, from one handler in one run.
  4. `ConferenceRooms` - `RemoveJob` re-looked its row up in a live windowed collection a
     background callback replaces, so a durable record could be hard-deleted with an audit row
     carrying no ticket and no old values.
  5. `SelfServiceGroups` - a null dereference **inside a catch block**, reachable today, which with
     no `ErrorBoundary` tears the circuit down.
  **Known limits of the harness, all measured rather than argued, all recorded on their entries:**
  - **Over-gating is undetectable.** Nothing distinguishes a correct exemption from a control gated
    into a trap. `SelfServiceGroups` M11 proves it.
  - **CLOSED in `ddb0f83`:** the button sweep matched the whole TAG, so a `title` naming the
    predicate satisfied it with the `disabled` attribute deleted - measured on page 9. It now
    reads the attribute value via `AttributeValue`. Two siblings shared the hole and were fixed
    with it, one worse than the original: the non-button refusal check tested for the bare WORD
    "disabled" anywhere in the tag, and **Migration's tab anchor carries a CSS class named
    "disabled"**, so a class name was satisfying a gating assertion.
  - A **token-guarded lowering** cannot be enforced (`GroupManagement`); reverting it to the shape
    that sticks the flag true and deadens the page fails nothing.
  - `ConferenceRooms`' **background-callback hazard** is structurally unreachable - the snapshot
    assertion is defined over a first await and the handler has none, which is the point.
  - `SpinnerExpressions` cannot see a duplicated condition; `ScannerFalsePositives` is read by no
    assertion at all; `ExemptControl.ConditionThatKeepsItTrue` and `BecauseOfControl` are prose.
  - `ExemptControl.PrerequisiteBeforeExemptionHolds` cannot express a **method-reference**
    exemption - it reads the tied field out of the snippet via a regex matching only an inline
    `() => field = null`.
  **Found and deliberately NOT fixed - each needs an owner go:**
  - **`IntuneDevices` typed device-name confirmation is markup-only.** `ExecuteActionAsync` never
    re-reads `wipeConfirmName`, so the only enforcement of the second key on a factory wipe is one
    clause in a disabled expression. A concrete server-side shape is proposed in the token log.
  - **`M365GroupManagement` validates no ticket anywhere** - no ServiceNow check, no handler
    re-check, so four markup clauses are the entire enforcement. Same shape on `NamedLocations`
    delete and twice on `ConferenceRooms`.
  - **`ConferenceRooms.CancelJob` audits nothing** while `RemoveJob` audits - cancelling a running
    bulk job mid-write to Exchange leaves no record. Constitution-shaped.
  - **`SelfServiceGroups` cross-group result bleed** - a previous group's operation can publish its
    banner into a different group's view. Misleading rather than wrong; closing it changes what
    those fields *are*.
  - **`RiskyUsers.razor` has `IntuneDevices`' old partial shape** (`ActionsDisabled` missing
    `historyLoading`, 4 of 7 buttons outside the gate). Tier 3, unapproved, untouched.
  - **`BlockedSenderService.UnblockSenderAsync` still takes no `CancellationToken`** and the file
    has no timeout - the one confirmed live instance of the page-deadening hazard the whole sweep
    was about.

- **The owner's issue queue is the enumeration of outstanding items and this file does not copy
  it.** It lives at the path recorded in `.agents/machines.md` (machine-local, not in the repo,
  and not ours to write to - its own first line says so). Read it for the owner's wording and
  their own status markers. Each item's current standing is in `## Now`: 14 is next and is a
  planning task, 12 and 13 sit behind it, 8 is parked, 4 is in progress, 9's tier 1 is complete
  with tiers 2-4 unapproved, and 2, 5 and 6 are covered in the weekend-run entry. The previous
  copy of the queue was rotated to `docs/history/state-archive.md` on 2026-09-23: its "not yet
  started" header was false for four of its six items, and the entries for 5 and 6 had been
  overtaken by owner rulings of 2026-09-21.

- **Manual validation is outstanding operational work.** Start with
  `docs/DevValidation-2.3.34.md`: Admin Settings access, protected-user alias refusal and the
  reported MailboxPermissions friction are high-consequence unverified behavior. It owns older
  streams' checks; newer plans above own their own. Both instances address live production
  AD/Exchange, so manual writes still need authority for their named scope.
- **Coverage follow-up:** the 2026-08-14 ruling supersedes the standalone "one of three files
  done" task. `ProtectedPrincipalService` work was folded into the now-landed queued plans;
  re-measure on green CI and ratchet per `.agents/review/coverage-floor.txt`.
  `PermissionValidator` still needs its own approved plan for the credential-carrying I/O seam.
  Old percentages are dropped; the floor file owns the measured baseline.
- **Owner-deferred validation stays deferred:** Bulk Job Runner live UI and writes,
  ConferenceRooms protection, GM-3 self-service groups, and servicer override end-to-end proof,
  inverse denial and per-target batch notes. Sources: `docs/BulkJobRunner-Plan.md`,
  `docs/ConferenceRoomsFinderProtectedPrincipalGate-Plan.md`,
  `docs/SelfServiceGroupManagement-Plan.md`, and the archived 2026-08-11 servicer record.
  The owner accepted waiting for real production servicer use; do not re-queue it as urgent.
- **Module packaging/import stays deferred** (owner 2026-07-22, `.agents/decisions.md`).
  **AccountLockoutRemediation and its notification question stay parked** with the disabled
  module; disablement is re-verified in `.agents/machines.md`. No resumption authorized.

## Blockers

- **Falsified deployment/configuration blockers:**
  the old dev/prod versions, incomplete shared cutover and missing initial ServiceHealth,
  RiskyUsers and IntuneDevices configuration disagree with the host. Evidence is canonical in
  `.agents/machines.md`, whose receipt was re-measured 2026-09-23 as of `26b414b` - **dev and prod
  are no longer at the same base version.** Manual acceptance and exact deployed module versions
  remain unverified.
- **Queue 6, containerize: the SPN and gMSA work is not ours to do.** The owner's 2026-09-21
  ruling makes containerization a requirement, but a containerised app runs under Kestrel rather
  than IIS, so Windows Authentication needs a gMSA, a credential spec and SPNs registered in the
  forest - `deploy.ps1:381` removes the Negotiate provider today precisely to avoid that. That is
  the owner's AD team's work. Also unanswered and it changes the design: where "elsewhere" is. A
  container outside line of sight of on-prem AD and Exchange cannot do the AD, on-prem Exchange or
  DHCP work at all. Evidence: `.agents/decisions.md` 2026-09-21, items 3 and the closing paragraph.
- **SQLite upgrade is no longer blocked by package availability.** The 2026-06-26 decision's
  "no patched package exists" basis is falsified, re-verified 2026-09-23 as of `26b414b`:
  `obj/project.assets.json` still resolves `SQLitePCLRaw.lib.e_sqlite3/2.1.11`, while
  [NuGet publishes newer builds](https://www.nuget.org/packages/SQLitePCLRaw.lib.e_sqlite3),
  including 3.53.3 (the package version identifies its SQLite engine).
  The [advisory](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) still lists affected versions
  through 2.1.11 and its patched-version field says None. Compatibility is not established.
  Next action: plan and verify a supported update; do not suppress NU1903.
- **CloudPasswordReset residuals:** owner disposition of remaining survey export(s); current
  inventory differs from the old three-file claim (machine receipt). Survey registration
  `16866221-3e2b-4ea5-8aae-157b043b6d7c` was recorded as holding unnecessary
  `Directory.ReadWrite.All`, `User.ReadWrite.All`, `Group.ReadWrite.All` and
  `UserAuthenticationMethod.ReadWrite.All` grants. Consent was not re-queried under the hold;
  revocation needs separate authority.
- **Unsettled guidance conflicts:** the 2026-09-01 owner ruling in the archived queue permits
  one session for all slices with coding subagents; `.agents/repo-guidance.md` Token Budget
  and the 2026-08-27 decision still require a fresh Fable session per slice. Also, the 2026-07-31
  protected-principal input-validation rationale relies on this deployment's read visibility,
  while the 2026-09-11 rule rejects environmental safety assumptions. No contested rule changed;
  owner reconciliation is required.
- **SharedConfigDb record flags:** the decision says SQLite `data_version` / next read;
  implementation uses the store's change token with a throttle. The additive-only rule has
  operative homes in the decision, guidance and test but is absent from the Constitution.
  Evidence: `docs/SharedConfigDb-Plan.md` section 9. No ruling inferred.
- **Plan-status drift remains, re-verified 2026-09-23 as of `26b414b`:**
  `docs/BlockedSendersLoadTiming-Plan.md` and
  `docs/Comms10kReplaceUx-Plan.md` still say Approved;
  `docs/ConferenceRooms-OnPremRoomListAdd-Plan.md` still says Approved / In progress.
  Implementation was recorded but full completion is unverified. Do not silently relabel them.
  `docs/AdminUIRedesign-Plan.md` remains In progress with manual checks.
- **Unscheduled M365 protection gap:** group update/delete and owner adds were recorded as
  ungated, and protection configuration cannot identify a cloud-only group. Re-verified
  2026-09-23 as of `26b414b`: `Services/M365GroupManagementService.cs` calls its
  `CheckProtectedAsync` gate from the member/owner paths only - `UpdateGroupAsync` and
  `DeleteGroupAsync` still call neither.
  The owner excluded this module from the on-prem target work; no work approved.
- **Older questions:** `docs/MessageTraceNullRow-Plan.md` needs a live rerun; the upstream
  null-row cause remains undiagnosed (OQ-1). `docs/ProtectedPrincipalResolution-Plan.md`
  retains OQ-2 on whether the reported cloud-only mailbox targets should remain reachable.
  Alias-protection and MailboxPermissions fixes are not field-validated by deployment alone.
- **Ops gaps:** ConferenceRooms AD secret configuration on prod was previously outstanding
  and was not checked here. `deploy.ps1` still lacks native `-PlanOnly`; the pipeline dry-run
  is the recorded workaround. Reviewer sandbox capability was not re-probed; use dated
  machine notes, not superseded CLI-version assumptions.

## Verification

Commands and mandatory guards are owned by `.agents/repo-guidance.md` and `AGENTS.md`.
**Last full run 2026-09-23, as of `bd29a9c`:** build Release 0 errors, `dotnet test
ExchangeAdminWeb.slnx` **3064 passed / 0 failed / 3 skipped**, `dotnet format
--verify-no-changes` clean, `git diff --check` clean, Pester 157 passed / 0 failed,
PSScriptAnalyzer 0 errors. Commits after `bd29a9c` are docs-only and were not re-run.
Every commit of the weekend run passed these gates before it landed. Browser acceptance, AD/Graph
queries and reviewer dispatches were not run. Current CI status does not belong here.
Per-finding status is owned by `.agents/review/index.md`.

## Active sources

- `AGENTS.md`, `.agents/repo-guidance.md`, and `docs/ProjectConstitution.md` govern work.
- `.agents/decisions.md` owns decisions; `.agents/machines.md` owns host facts.
- Referenced plans own scope, status and acceptance checklists.
- `Modules/ModuleCatalog.cs` owns module versions and enumeration;
  `ExchangeAdminWeb.csproj` owns the current base app version.
