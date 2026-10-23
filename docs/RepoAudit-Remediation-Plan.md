# Repo Audit Remediation - Plan

Status: **IMPLEMENTED 2026-10-23 - all four waves landed, reviewed and pushed (`.agents/review/index.md`); the 2026-10-21 fix-everything stream closed the follow-ups.** Source: the six-agent read-only audit of local
master at `34bac08`, 2026-10-16 (owner-supplied, `repo_audit.md`). Eight high findings, 32
medium, plus lower-priority items. Nothing in this plan has been implemented.

## Exec summary

**What this is.** An outside audit read the whole repository and found 40 defects. I verified
ten of them against the code myself - all eight of the high-priority ones and two mediums
picked at random - and every single one was real and described accurately. On that basis I am
treating the other 30 as sound, but each still gets checked before anyone fixes it.

**The shape of it.** Five of the eight worst are the same kind of mistake: **the app checks one
identity and then acts on a different one.** It checks `FOREIGN\Ops` and accepts a member of
`HOME\Ops`. It checks a mailbox's primary address and lets a secondary alias through. It checks
whether `A\alex` is protected and logs off `B\alex`. It validates mailbox A and then writes
whatever is in the box by the time the write happens. That is one class, not five accidents,
and the plan treats it as one.

**What it gets you.** The authorization and protected-user guards actually hold against the
cases they were written for. Right now several of them can be walked past without anything
looking wrong in the audit trail.

**What it costs.** Roughly 30 slices across four waves. Wave 1 is the eight highs and is the
only part I would call urgent. Waves 3 and 4 are mostly reporting, notification and deployment
scripts and could be deferred without risk.

**The biggest risk in doing it.** Wave 1 changes how authorization decides, in five modules. A
mistake there locks people out of their own tools, or worse, quietly loosens a check while
appearing to tighten it. Every slice in Wave 1 needs a test that fails before the fix, and two
of them (group SID matching, Self-Service deny evaluation) change behaviour you will notice.

**What was approved.** All four waves, plus everything listed under "Added after the audit".
Wave 1 is still first, because it will teach us things about the identity-handling code that
should shape the rest. Defender/MDE work is P2, last.

---

## Verification status - read this before fixing anything

**Verified by me against the code at `34bac08`, all confirmed exactly as described:** A1, A2,
A3, A4, A5, A6, A7, A8 (all eight highs), plus A30 and A31.

**Not independently verified:** A9-A29, A32-A40 and the lower-priority list. They carry precise
file:line citations and the verified sample was 10 for 10, so the prior is strong - but
**every slice below re-verifies its own finding first.** A finding that does not reproduce is
recorded as not-reproduced, not fixed around.

**Audit scope limits, stated by the auditors:** no tests, builds, browser workflows or live
infrastructure were exercised. Deployment activation of ServiceNow validation (A30) was not
checked. Dependency review did not establish an exploitable package vulnerability; resolved
transitive packages and deployed runtime versions remain unverified.

---

## Constraints

- **Recorded exemptions are not defects. Do not "fix" them.** Comms-10k consults neither
  protected-principal check (owner 2026-09-30); Self-Service Groups does not consult Protected
  Group Targets but keeps its member check (owner 2026-08-31). A4 is about ACL **deny**
  evaluation inside Self-Service Groups and does NOT touch that exemption.
- `docs/ProjectConstitution.md` outranks this plan. The notification findings (A23-A27) and the
  audit findings (A28) are Constitution-shaped - read its notification and audit sections before
  designing those slices.
- **Environment neutrality** (`.agents/repo-guidance.md` invariant 7) applies to every slice
  here. The waiver granted for the Comms-10k group GUID is scoped to that one value and does
  not extend to anything in this plan. Several fixes need directory scope - **discover it at
  runtime and fail closed; never name or default a domain.**
- One finding per commit. Full gate before each: Release build, `dotnet test
  ExchangeAdminWeb.slnx`, `dotnet format --verify-no-changes`, `git diff --check`,
  `tools/Test-AsciiOnly.ps1`. **Grep builds for `error CS`; never tail them.**
- Every new test mutation-proven: revert, watch it fail, restore, md5-verify, `touch`.
- PowerShell slices (Wave 4) additionally need `Invoke-ScriptAnalyzer -Path . -Recurse` and
  `Invoke-Pester tests/ps`. `deploy.ps1` and anything it imports must stay pure ASCII - Windows
  PowerShell 5.1 reads BOM-less non-ASCII as ANSI and fails to parse.
- Page edits move `ClickGateRegistry` line keys. Re-anchor per
  `.agents/playbooks/clickgate-reanchor.md`; check `ProgressRegistry` too.
- **Line numbers below are the audit's, as of `34bac08`, and drift as slices land. Re-locate by
  symbol.**

---

# Wave 1 - identity and authorization (the eight highs)

**The unifying defect: a check is performed against one identity and the action is taken
against another.** A1-A5 and A7 are all instances. Fix them as a class - the same test shape
applies to each - but one commit each.

## W1-A1 - group grants match across domains

`Services/ModuleAdminService.cs:31`. `IsModuleAdmin` strips the domain
(`group.Split('\\')[1]`) and then accepts a bare-name role match. A grant for `FOREIGN\Ops`
is satisfied by a member of `HOME\Ops`. The group picker also discards domain information, so
the stored value may already be ambiguous.

**The picker is the other half, and it is worse than the audit stated.**
`Components/Shared/ADGroupAutocomplete.razor:137` does
`Value = result.SamAccountName ?? result.DisplayName`. It has already resolved a real directory
object - it is holding the search result - and then stores only a name, falling back to the
**display name** when `SamAccountName` is absent. So the stored value is not merely
domain-less, it is not reliably even a SAM account name. Owner, 2026-10-19: *"so even though
the GUI resolves groups on entry, it's still storing the arbitrary display name? yes, fix
that."*

Fix: store and compare **validated group SIDs**, not names. The picker already has the resolved
object, so capture the SID there rather than re-resolving later. Fail closed when a name cannot
be resolved to exactly one SID.

**Migration of existing grants - decided, 2026-10-19.** Existing stored grants are names.
Resolve them once. A grant that does not resolve to exactly one SID is **refused**, not
silently honoured. **Produce the list of non-resolving grants and give it to the owner BEFORE
the change ships** - refusing is the safe behaviour, but it can remove an admin's access to
their own module, and that must not be a surprise. The read path keeps honouring names only
until that list is reviewed.

Exposure if unfixed: module configuration and the AD attribute allowlist.

## W1-A2 - a secondary alias bypasses protected-user rules

`Services/PermissionValidator.cs:163`. `requiresFullResolution` is false when the protected
config holds only address-form users, so `ResolveWithExchangeFallbackAsync` never runs. A
secondary alias of a protected mailbox is then compared as a literal string and passes.
Affects Mailbox Permissions, Calendar Permissions and Out of Office.

Fix: resolve the target identity and compare the **resolved** identity, then write to that same
resolved identity. Check and write must name one object.

## W1-A3 - foreign-domain protected membership is checked in the wrong domain

`Services/ProtectedPrincipalService.cs:904`. The transitive membership query
(`memberOf:1.2.840.113556.1.4.1941:`) passes `-LDAPFilter` and `-Credential` but **no `-Server`
and no `-SearchBase`**, so it runs against the host's default domain. A foreign-domain object
returns a clean "no match" and Group Management treats it as unprotected.

Fix: resolve the authoritative directory scope for the target at runtime and query there. **Fail
closed when visibility is uncertain** - an unanswerable question is not a negative answer.
Environment neutrality applies: discover the scope, do not configure or default it.

## W1-A4 - Self-Service Groups ignores denies that apply through a group

`Services/SelfServiceGroups/SelfServiceGroupService.cs:1359`. The ACE loop `continue`s on every
ACE whose SID is not the caller's own, so a Deny carried by a group the caller belongs to is
never evaluated. A group-level deny followed by a direct user allow reads as allow, and the
privileged module credential performs the write.

Fix: evaluate ACEs applicable to the caller's **token groups**, and honour ACE ordering (deny
precedence). **This is not the Protected Group Targets exemption** - that ruling stands
untouched.

**Consequences to plan for:** this can start refusing writes that currently succeed. That is the
point, but it is operator-visible.

## W1-A5 - lockout remediation can log off the wrong account

`Services/AccountLockoutRemediationService.cs:727`. The embedded script matches `quser`'s bare
username column against the target list and calls `logoff.exe $sessionId`. Guarding `A\alex`
can log off `B\alex` or a local `alex` - bypassing the protection and destroying unsaved work.

Fix: resolve the session owner's domain/SID and compare that before logoff. Skip, and report,
any session whose owner cannot be identified.

## W1-A6 - a borrowed EXO connection can target the previous organization

`Services/ExoConnectionPool.cs:389`. The new configuration marker (`_connectedUnder = current`)
is published inside `_connectedLock`, and the pool drain happens **after** the lock is
released. A borrower in that window reads the new configuration and receives a runspace
connected under the old one, then executes against the previous organization.

Fix: make the configuration identity and the retirement generation one atomic transition, so no
borrower can observe one without the other.

## W1-A7 - Out of Office writes a mailbox it did not check

`Components/Pages/OutOfOffice.razor:301`. `setEmail` is read, awaited protection validation
runs, and the field is still editable throughout. Changing A to protected B during the wait
means B is written although only A passed.

Fix: capture the request into a local and acquire the busy guard **before** the first await -
the same shape as the Comms-10k C7 fix landed on 2026-10-16. Reuse that pattern.

## W1-A8 - Conference Rooms jobs run after the module is disabled

`Services/Jobs/ConferenceRoomBulkProcessor.cs:78`. Each row re-checks the captured snapshot
against the section's current allowed groups, but **not module enablement**. With
ExchangeOnline still enabled, a disabled Conference Rooms module keeps changing rooms.

Fix: re-check effective module enablement before dispatch. **The Comms-10k C2 slice already
added exactly this check on 2026-10-16** (`.agents/review/findings/cdr-c2.md`) - reuse it rather
than inventing a second shape.

---

# Wave 2 - wrong target, lost work, false success

Same family as Wave 1 but lower blast radius, plus the "a failure reads as a clean result"
class that `mde-*` and `cdr-*` kept finding.

**Stale target / state across an await** - the C7 class, now found in five more places:
- A13 `Components/Pages/MfaReset.razor:50` - results follow the editable UPN, not the listed user.
- A14 `Components/Pages/ADAttributeEditor.razor:543` - a save and a concurrent search cross; the
  message and email name the wrong person and A's values populate B's display.
- A15 `Components/Pages/LicensingUpdates.razor:84` - Cancel clears state while Apply continues;
  completion dereferences the cleared preview and the required notification is suppressed
  **after the backend mutation**.
- A16 `Components/Shared/CountryCodePicker.razor:47` - initialises once, so switching location
  A to B can save A's countries into B.
- A7 is the Wave 1 instance. **Fix them with one shared discipline, not five bespoke patches.**

**A failure reads as success:**
- A17 `Services/M365GroupManagementService.cs:298`, `Services/NamedLocationsService.cs:59` -
  collection reads return empty or truncated after an HTTP failure.
- A18 `Services/DelegationReportService.cs:72` - a failed delegation read renders "No calendar
  delegates" and the report records success.
- A19 `Services/AccountLockoutRemediationService.cs:366` - refused users vanish from totals in a
  mixed batch (Known Failure Class 2, success aggregation).
- A32 `Services/Jobs/MessageTraceDetailJobProcessor.cs:101` + `Services/MessageTraceExportListing.cs:263`
  - cancelled exports are emailed as ready and then refused on read.

**Wrong object identity:**
- A9 `Services/ADAttributeEditorUndoService.cs:177` - undo resolves the audit UPN without the
  original object GUID, so a reassigned UPN can be modified instead. Same lesson as the
  Comms-10k C4 fingerprint: **bind to the immutable id.**
- A10 `Services/M365GroupManagementService.cs:262` - UPN/email passed where Graph requires an
  object id, so ordinary UI input builds an invalid `directoryObjects/{UPN}` reference.

**Other:**
- A11, A12 `Services/MfaResetService.cs:140`, `:114` - phone deletion ordering is ineffective
  (every phone maps to `Phone`), and a later throw discards known-successful deletions.
- A20 `Components/Pages/AdminEventLog.razor:559` - FIFO truncation applies before timestamps
  merge, so diagnostics can evict newer audit events. **Audit retention - Constitution-shaped.**
- A21, A22 `Services/HeaderAnalysisService.cs:562`, `:418` - ARC passes mask direct
  authentication failures; expanded spam-score keys never match the threshold checks.
- A29 `Components/Pages/AdminBulkJobs.razor:294` - revoked Bulk Jobs access stays usable through
  an already-open page, including cross-module cancel and delete.
- A30 `Services/ServiceNowService.cs:69` - `number={ticketNumber}` is interpolated into
  `sysparm_query`; URL-encoding does not neutralise `^OR` as **query grammar**, and the returned
  number is never compared to the requested one. Fix both: encode for the grammar, and verify
  the response matches the request. **Confirm whether validation is enabled in this deployment
  before rating urgency** - the auditors did not check.
- A31 `Components/Pages/Migration.razor:3953` - a circuit-local invalidation calls
  `ReportStore.DeleteAll()`, destroying every operator's stored reports instance-wide.
- A33 `Services/Storage/ConfigStoreMigrator.cs:200` - both instances read the old version before
  locking, so the second repeats an applied `ALTER TABLE` and can abort startup. **Shared
  config DB; additive-only migration rule applies.**

---

# Wave 3 - notification and audit obligations

All Constitution-shaped. Read its notification and audit sections first; these are policy
questions as much as code.

- A23 `Components/Pages/MailboxPermissions.razor:514` - partial cloud changes suppress
  affected-user notices because notification requires overall success.
- A24 `MailboxPermissions.razor:602`, `CalendarPermissions.razor:559` - bulk rows get audit
  records and an admin summary but no affected-user notice.
- A25 `Components/Pages/GroupManagement.razor:651` - security-group changes notify
  administrators only. **Owner ruling 2026-10-19 CHANGES THIS FINDING'S FIX. Do NOT email the
  group's members** - *"that's just noise."* Email the **group's manager** (the `managedBy`
  attribute) with a **summary of the changes**, not one message per member. A group with no
  manager has no recipient: say so in the audit record rather than falling back to spraying
  members or to the admin address.
- A26 `Services/ADAttributeEditorUndoService.cs:251` - undo sends no administrator notification
  at all, because it bypasses the forward-edit page that owns dispatch.
- A27 `Services/EmergencyDisableService.cs:1060` - sends to the global admin address instead of
  the configured security recipient, while the UI may still claim the security team was
  notified. **The false UI claim is the worse half.**
- A28 `EmergencyDisableService.cs:157`, `ADAttributeEditorService.cs:534` - protection and
  preflight refusals return before recording the attempted mutation, so refused attempts leave
  no durable audit.

**Owner ruled 2026-10-19, verbatim:** *"it ABSOLUTELY needs to email users when mailbox or
calendar permissions are changed. NOT for group membership changes. Email the manager of the
group with a summary of the changes, but do not email every user in the group."*

So this wave splits:

- **A23, A24 - affected-user email is required.** Mailbox and calendar permission changes
  notify the person whose mailbox or calendar it is, including on partial success (A23's
  current gate on overall success is the defect) and on every successful row of a bulk run
  (A24).
- **A25 - affected-user email is explicitly refused.** Manager summary only, one message per
  operation.
- A26, A27, A28 are unaffected by the ruling and stand as written.

---

# Wave 4 - operations, deployment and docs

PowerShell gates apply. None of these is urgent; several are recovery-path defects that only
bite during an incident, which is also when they are least affordable.

- A34 `tools/Install-ExchangeAdminWeb.ps1:598` - an update that omits the service account
  silently replaces it with `ApplicationPoolIdentity`, potentially losing credential-store,
  certificate, directory and database access.
- A35 `deploy.ps1:496`, `Install-ExchangeAdminWeb.ps1:710` - pool-shutdown exceptions are
  swallowed before mirroring, permitting partial replacement of a **live** application.
- A36 `Install-ExchangeAdminWeb.ps1:698` - generic installer upgrades skip the verified database
  backup, so binaries can start migrations with no recovery snapshot.
- A38 `tools/promote-dev-to-prod.ps1:384` - restart sits in `finally`, outside the rollback
  `catch`, so a restart failure leaves new files deployed with the pool stopped.
- A39 `tools/Move-ConfigDbToShared.ps1:351` - accepts a database path inside a publish tree,
  which later mirroring is entitled to delete.
- A40 `tools/Move-ConfigDbToShared.ps1:515` - waits for startup logs without warming the app;
  `AlwaysRunning` does not guarantee application initialisation, so cutover can fail on an idle
  app that is fine.
- A37 `docs/ConfigDbOperations.md:3` - the operator recovery guide still describes the retired
  storage arrangement: local paths, single-pool shutdown, database recreation, name-based
  authorization rows. **Docs-only, and arguably the cheapest high-value fix in this plan** -
  it is what someone reads at 2am.

**Lower-priority, same wave:**
- `tools/deploy-pipeline.ps1:124` - provenance check fails open; a missing commit id or a Git
  failure still yields "matches source HEAD".
- `tools/SqliteConfigBackup.psm1:202` - `-WhatIf` creates a directory before `ShouldProcess`.
- `tools/New-ExchangeAdminWebGraphApps.ps1:101` - a failure during the second registration
  strands the first and suppresses its one-time secret output.
- Header-processing bounds: capped raw headers still feed uncapped derived routing collections;
  large-input runtime impact unmeasured.
- Operational hardening: narrow publish-folder Modify permissions; require the SQLite-dependent
  CI tests to actually run; exercise 5.1-compatible scripts under Windows PowerShell 5.1.

---

## Sequencing

**Wave 1 in order: A1, A3, A2, A4, A5, A8, A6, A7.** Identity resolution first (A1, A3) because
A2 and A4 build on knowing who someone actually is. A8 is a near-copy of an already-landed fix.
A6 is self-contained. A7 reuses the C7 pattern.

**Then Wave 2**, stale-target class together, then false-success class, then the rest.
**Then Wave 3**, only after its owner decision. **Then Wave 4.**

**A37 (the recovery doc) may be pulled forward at any time** - it is docs-only, blocks nothing,
and is actively misleading during an incident.

## Verification

Per-slice gate as under Constraints. One full-suite run at the end of each wave.

**The audit's own closing note is a requirement for this plan, not a suggestion:** *"Several
existing tests validate individual pieces whose combined behavior is wrong."* Wave 1 and the
Wave 2 stale-target slices need **composed-workflow tests** - paused operations, target changed
mid-await, cross-circuit activity, partial failures - not another per-piece assertion.

**Not covered by any of this:** that any page renders (no bUnit harness); live directory,
tenant or deployment behaviour. A1, A3, A4 and A5 all touch directory semantics that only a
real forest can confirm - each needs a stated manual check, and **probing Active Directory
remains forbidden without a direct owner go.**

## Already recorded elsewhere - not re-raised here

`psl-2` (licensing diagnostics), `psl-3` (DHCP diagnostics), `psl-4` (EXO exception
classification), `cdr-followup-1` (Comms count wording), `csv-1` (Event Log CSV formula
injection). The broad navigation-cancellation promise exceeding implemented cancellation is
recorded in `.agents/state.md`.

**Unconfirmed, needs investigation before it can be planned:** protection rules retaining stale
group DNs; legacy name-valued servicer grants; Emergency Disable's cloud UPN binding. The first
two interact with A1 - resolving grants to SIDs may settle them.

**Still open from the previous stream:** the `ps.Invoke(); ps.Commands.Clear();` pattern without
`try`/`finally` at ~47 sites across `GroupManagementService`, `EmergencyDisableService`,
`DhcpAuthorizationService`, `ConferenceRoomService`, `ProtectedPrincipalService` and
`ADAttributeEditorService`. The pattern is confirmed present and the `PowerShell` object is
confirmed reused; **reachability per site is not confirmed** - it bit in Comms-10k because a
retry caught and resumed on the same object. Worth an audit slice, not 47 assumed defects.

## Owner rulings, 2026-10-19

1. **Everything gets planned and fixed.** *"All items that need to be remediated need to be
   planned if not already and fixed."* All four waves are approved, and anything not yet
   planned is added - see **Added after the audit** below.
2. **A1** - store SIDs; fix the picker too. Non-resolving legacy grants are refused, with the
   list given to the owner first.
3. **A23/A24** - affected-user email required. **A25** - manager summary only, never per-member.
4. **A30** - ServiceNow validation is **not enabled** and will not be until an API login exists.
   Fix it anyway (ruling 1), at the bottom of Wave 2's priority.
5. **Comms-10k manual acceptance** - the comms team will exercise it on their next real update.
   Not a blocker, and not something to simulate.
6. **MDE/Defender work is P2**, after everything else.

## Added after the audit - needs the same treatment

- **Blocked Senders: the unblock reason is passed through unbounded.**
  `Components/Pages/BlockedSenders.razor:119` has no `maxlength` and
  `Services/BlockedSenderService.cs:59` only trims before passing `-Reason` to
  `Remove-BlockedSenderAddress`. **Field evidence, owner 2026-10-19: one L2 operator hits
  "The Hygiene DAL retried a transient condition the maximum number of times" every time, and
  a shorter reason works.** Microsoft documents no length cap on `-Reason`, so the limit is
  undocumented rather than absent - cap it defensively, and surface our own message instead of
  EXO's raw error. **I previously retracted this theory on the grounds that the docs show no
  cap; the field result overrides that and the retraction was wrong.**
- **`psl-2`, `psl-3`, `psl-4`, `cdr-followup-1`, `csv-1`** - recorded findings, previously
  deferred as unapproved. Ruling 1 brings them in scope. They keep their existing records.
- **The bare `ps.Invoke(); ps.Commands.Clear();` pattern at ~47 sites** across
  `GroupManagementService`, `EmergencyDisableService`, `DhcpAuthorizationService`,
  `ConferenceRoomService`, `ProtectedPrincipalService` and `ADAttributeEditorService`. Pattern
  confirmed, reachability per site not. **First slice is the audit that establishes which sites
  are reachable**, then fixes only those - do not rewrite 47 call sites on a presumption.
- **`docs/ConfigDbOperations.md`** - documents the retired storage arrangement. Docs-only,
  blocks nothing, pull forward whenever.
- **Unconfirmed, investigate before planning:** protection rules retaining stale group DNs;
  legacy name-valued servicer grants; Emergency Disable's cloud UPN binding. The first two
  interact with A1 and may be settled by it.

---

# Wave 1 outcome and what it added, 2026-10-19

All eight highs are landed and seven of the eight independently reviewed (A5's review was in
flight at the time of writing). `.agents/state.md` owns the commit table; this section owns
only what the wave **added to this plan's remaining scope**.

Review found a repair necessary in six of seven slices, and four of those repaired a
regression the fix itself had introduced. Waves 2-4 should be costed on that basis: a slice is
not done when its tests pass, and the review round is doing generation work, not confirmation.

Three new findings came out of Wave 1 and are in scope under the owner's ruling 1 of
2026-10-19 ("all items that need to be remediated need to be planned if not already and
fixed"). They are small and each has a record; they do not need their own wave.

## W1F-A5B - blocked lockout targets vanish from the operator's report

`Services/AccountLockoutRemediationService.RunLogoffAsync`. `GuardTargetUsersAsync`'s blocked
rows are surfaced only when **every** target was blocked; block one protected user out of
three and the operator is shown two successes and no mention of the third. The refusal is
audited, so the record exists - the operator is simply never told.

Fix: add the guard's rows to `allRows` alongside the SID-resolution rows, keeping the
all-blocked early return so its message stays specific. One line plus a two-user guard.
Record: `.agents/review/findings/aud-a5b.md`.

## W1F-A7B - Out of Office captures only the mailbox

`Components/Pages/OutOfOffice.razor`. A7 captured the mailbox; state, messages, schedule and
ticket are still live field reads after awaits, so the audit row can name a ticket the change
was not made under.

Fix: capture them all as one snapshot before the first await. **Take the behavioural harness
with this slice** - A7's guards are source scans, and they stayed green while the page's form
was left permanently disabled. Comms-10k's reflection harness already drives a real page
instance. Record: `.agents/review/findings/aud-a7b.md`.

## W1F-A6B - Disconnect-ExchangeOnline may not be per-runspace - OWNER DECISION FIRST

**This one is different and must not be picked up as a slice.** A reviewer reports, from IL
inspection of the installed ExchangeOnlineManagement 3.9.2 and 3.10.1, that
`Disconnect-ExchangeOnline` with no connection selector clears a **static**
`connectionContextMap`. If so, `ExoConnectionPool.DestroyRunspace` tearing down one retired
connection also tears down every other borrower's live connection, mid-operation, possibly
after they have already written changes - and the pool's whole premise of N concurrent
connections in one process is unsound.

It is **unverified**: it rests on someone else's reading of a module nobody here has opened,
and nothing has been reproduced against a tenant.

**Steps 1 and 2 are DONE, 2026-10-19, and both confirm it.** Evidence is on the finding: the
default disconnect calls `RemoveAllConnectionContexts()` (`psm1:945`), and
`connectionContextMap` is a **static** field, so the map is process-wide rather than
per-runspace. Verified locally, metadata-only, no tenant and no module code executed. Only
3.9.2 is installed here, so the reviewer's 3.10.1 half is still unchecked.

A `ConnectionId` parameter set does exist, but `Connect-ExchangeOnline` generates the id
rather than accepting one, and the only way to read it back (`Get-ConnectionInformation`)
reads that same global map - so attributing an id to a specific runspace needs connection
establishment serialised first.

**Step 3 stands: report and stop.** The repair is a design change to the connection pool, not
a fix, and it is outside everything this plan has been approved for. The finding carries three
costed options for the owner; the cheapest is to stop pooling EXO concurrently at all.

Record: `.agents/review/findings/aud-a6b.md`.

## Pending decisions

One, and it is W1F-A6B: whether to spend the verification step above, and if it confirms,
whether a connection-pool redesign is in scope at all. Nothing else is blocked - Wave 2 starts
whenever.
