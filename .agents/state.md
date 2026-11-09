# Agent State

Current work and blockers only. Rules live in `docs/ProjectConstitution.md` and
`.agents/repo-guidance.md`, decisions in `.agents/decisions.md`, and machine observations in
`.agents/machines.md`. Each plan owns its implementation and manual acceptance checklist.
Superseded descriptions are verbatim in `docs/history/state-archive.md` (newest section first;
the latest sweep is Archived 2026-10-23).

## Now
### 2026-11-07 00:25 - weekend run paused: two blockers only the owner can clear
### 2026-11-09 15:00 - second dev deploy (help sweep, base 2.36.1) done by the owner; checklist handed over again

Session reset after a host hang; nothing was in flight (master ffa6b822, no worktrees). Owner: "deployed". Dev now
runs 337aaccd code. Checklist: `.agents/review/deploy-checklist-2026-11-09.md` (F1 done); first look at Admin Settings
and Module Configuration wording.

### 2026-11-09 14:30 - in-app help sweep merged (base 2.36.1); a second dev deploy is owed for the owner to see it

Owner, on the deployed Admin Settings page: "what the fuck is this screed doing in the ui? ... sweep the app for
this kind of nonsense." Ruling recorded (decisions 2026-11-09 "In-app help is short"; Known Failure Class 5 amended).
help-sweep merged at 337aaccd: 397 help blocks surveyed, 58 over the limit cut to 0 (Protected principals note 1613
-> 160 chars; Verify notes 1064-1378 -> 240-306; 34 catalog descriptions to one sentence; ~30 hover tips to one
sentence); every cut fact is in README (four sections gained a sentence; a stale 500-row Risky Users bullet
corrected) or in the refusal message at the moment it matters. 19 module patch bumps; base 2.36.1 because Module
Config is a shared page and 2.36.0 has shipped to dev. Master gates at 337aaccd: 8861 xUnit, 19 warnings, format,
diff-check, ASCII clean. New LOW finding from the sweep: `env-neutrality-delinea-banner.md` (Module Config banner
names a deployment-specific secret template).

**Next:** the owner deploys dev again (`deploy-pipeline.ps1 -Dev`; no conversion step this time) and looks over Admin
Settings and Module Configuration; then the post-deploy checklist `.agents/review/deploy-checklist-2026-11-09.md`.

### 2026-11-09 13:00 - dev deployed by the owner ("deployed"); post-deploy test checklist handed over

Dev now runs master at 350c63b5 code (records to 4e2084e7). The consolidated post-deploy checklist (from every
"deploy check" paragraph in the 2026-11-05 .. 2026-11-09 entries) was handed to the owner in chat and is
reproduced in `.agents/review/deploy-checklist-2026-11-09.md`; results go there as the owner reports them.

### 2026-11-09 12:40 - identifier conversion run live by the owner; clean

`db convert-identifiers` with the build about to ship, against a verified copy then the live shared database: 8 rows
converted each time (4 protected users, 3 protected groups, 1 module-admin grant); `db identifier-status` on the live
database: 7 converted, 0 identifier, 0 legacy, 1 grant-sid, 0 grant-name, exit 0. Legacy companions stay for prod's
old build (plan B.1). Next, owner: `deploy-pipeline.ps1 -Dev`, then remove the scratch publish folder used for the
conversion, then the post-deploy check list (Graph Directory Read Credential first).

### 2026-11-09 12:22 - dev plan clean (0 refusals) after the owner narrowed the host ACLs; deploy is the owner's to declare

Three plan runs by the owner: (1) refused on Authenticated Users Modify on the drive root (inherited by the web root)
and a direct admin-group FullControl on the web root; (2) after the first narrowing, refused on Authenticated Users
Write inherited into a new staging folder; (3) clean: "only the administrators, SYSTEM, TrustedInstaller and the
account running this script can change the code ... so the elevated db tool may run from it", 0 refusal(s). The rev-i
message fixes were confirmed live in run (2). Item 12's host half is done.

**Owner-side order at the deploy (docs/ConfigDbOperations.md "convert-identifiers"):** publish the build about to be
deployed to a scratch folder, run `db convert-identifiers` against a copy of the shared database, then live, then
`db identifier-status` (exit 0); then `deploy-pipeline.ps1 -Dev`; then the post-deploy checks in the entries above.
The legacy rows stay beside the new ones, so prod on the old build keeps working (plan B.1).

### 2026-11-09 (plan run) - the owner ran the dev plan; the ACL refusal fired as designed; host ACLs narrowed

The owner ran `deploy-pipeline.ps1 -Dev -PlanOnly` elevated: every step printed, nothing changed, one refusal: the
drive root granted Authenticated Users Modify (inherited by the web root) and an admin group held FullControl
directly on the web root. The owner narrowed the host ACLs ("acls updated", 2026-11-09); the plan is to be re-run by
the owner (it needs an elevated shell) and must show 0 refusal(s) before the real deploy.

Three message defects seen in that output were fixed and merged at 350c63b5 (rev-i1 raw rights number for an
inherit-only generic ACE, rev-i2 the same parent entry reported twice for a not-yet-created folder, rev-i3 "REFUSES"
doubled in the plan line). Message-only; classification unchanged; 686 Pester passed, PSSA 0 errors, ASCII and
diff-check clean. No codex round for these (owner verifies the wording on the next plan run).

Design note for the owner: the check trusts Administrators, SYSTEM and TrustedInstaller by name and cannot see that
a group is a member of Administrators, so a direct grant to an admin group on a parent folder is reported like any
other. A runtime `-TrustedPrincipal` parameter would be new functionality (owner question 9).

### 2026-11-09 (close) - sweep complete; every admitted finding fixed, merged and reviewed; master ready for the one dev deploy

**Master 6422a3a8 (plus this record).** Gates at 6422a3a8: 676 Pester passed, PSSA 0 errors, ASCII and diff-check
clean; xUnit 8861 passed and 19 warnings at the last C# change (454fa2b7); format clean. Base version 2.36.0.
Nothing pushed (local is ahead of `origin/master`; push policy applies), nothing deployed, no host or config change.

**Landed this cycle since the dev build 8557a15a:** ppi-s1..s6, sweep-c, dov-v1..v4, sweep-d, sweep-e, Comms-10k
clear-then-add revert, sweep-f, item 45, sweep-g (items 12, 34-39), and the review fixes rev-abc (13), rev-e1,
rev-f1..f3, rev-g1. Codex reviews A-D (codex-commercial) and E-H (codex, Portkey) ran over every merged range; every
finding admitted was fixed, merged on guard proof and re-reviewed; reviews G and H closed the last ones (rev-h
CLEAN). Open and NOT fixed: rev-x1 (LOW-MEDIUM, catalog searches outside the protection checks still send no
SearchBase; owner scope question).

**Owner questions, one at a time (newest first):**
1. Item 38 `deploy.ps1` half: add a pre-mirror binaries backup so a failed dev mirror can roll back (new
   functionality), or accept re-running the deploy as the recovery. `sweep-g-38.md`.
2. rev-f2 scope: an unreadable IIS application list now refuses (fail closed), withdrawing item 34's downgrade for
   every read the probe cannot complete; keep, or restore the warning for that one case (reopens the hole).
3. rev-e1 departure: overlapping failed reads during one stall count as one failure (keeps one-forgiven-failure for
   multi-tab operators); keep, or the strict literal rule (one-branch removal).
4. Item 44 audit refusal convention (`sweep-e-44.md`). 5. Item 24 approving from SyncedWithErrors (`sweep-f-24.md`).
6. Constitution edits lines 80 and 95 (`sweep-45.md`). 7. rev-x1 scope. 8. Default review harness after a
   codex-commercial re-login (decision 2026-11-09 covers only "while logged out").

**The one dev deploy (owner declares it; all check lists accumulated in the 2026-11-06 .. 2026-11-09 entries):**
first `deploy-pipeline.ps1 -Dev -PlanOnly` elevated under Windows PowerShell 5.1: "App folder permissions" must not
be a `!` refusal (if it is, it names the parent folder and principal; `D:\` or `D:\inetpub` granting Authenticated
Users Modify is the likely cause), no "Cannot read the IIS sites and applications" refusal unless IIS has an
unreadable node, "0 refusal(s)" otherwise; then `-Prod -PlanOnly` and the cutover plan; `shutdownTimeLimit` type and
value; then the deploy itself, then the checks of the earlier entries (convert-identifiers on a DB copy then live,
Graph Directory Read Credential filled first, User.Read.All, host ACLs, Verify on each page, Comms-10k scratch-group
run, protected check through distribution-group nesting refuses with the timing log line, secret rotation re-fetch,
other-tree group checks, attribute editor "pinned to domain controller <writable DC>", renamed protected OU group
write refuses, revalidation reload refusal with the directory blocked, two tabs kept on one stall).

### 2026-11-09 (evening) - rev-f merged; closing codex review G running

**Merged:** rev-f at dde1b7fb (rev-f1 the db tool's folder check reads every folder above the app folder and refuses
on an untrusted owner or replacement right, pool identity not trusted on parents, junctions not followed; rev-f2 any
IIS node the probe cannot fully read refuses the serving-pool check wherever it sits, the item-34 warning survives
only for a fully read node elsewhere; rev-f3 deploy.ps1 -PlanOnly wraps every real-run read, and the folder-check
verdict is a counted refusal via a new additive `AclRefuses`). Master gates at dde1b7fb: 664 Pester passed (641 +
23), PSSA 0 errors, ASCII and diff-check clean; no C# changed (xUnit stays 8861 at 454fa2b7).

**Implementer departures, accepted by the supervisor as fail-closed within the mandate (owner may relax):** rev-f2
also makes an unreadable APPLICATION LIST refuse, not only an unreadable virtual-directory list; the practical
effect is that item 34's downgrade is withdrawn for every read the probe cannot complete (one line, `PathsRead =
$true` on that node, brings the warning back and with it the fail-open).

**Live consequence to check before the deploy (rev-f1):** if `D:\` or `D:\inetpub` grants Authenticated Users an
inheritable Modify, every deploy, promotion and cutover now refuses naming that parent until it is narrowed. Run
`deploy-pipeline.ps1 -Dev -PlanOnly` first: the "App folder permissions" line must not be a `!` refusal. Also: no
"Cannot read the IIS sites and applications" refusal unless IIS really has an unreadable node; a plan against a
scratch publish folder with a broken appsettings.json shows the refusal and every later step.

**Review G (codex, over 934d9587..454fa2b7 and 636295cd..dde1b7fb) dispatched.** Next: its findings fixed the same
way if any, then the final state entry; the one dev deploy waits for the owner's declaration.

### 2026-11-09 (later) - rev-e1 merged; review F found three deploy-script defects, fix slice running

**Merged:** rev-e1 at 454fa2b7. The consecutive-failed-lookup count now lives on the per-account recorded answer,
so every tab, page request, reload and new circuit charges and reads the same count; the second failure in a row
refuses the page (403, audit trigger `PageRequest`) and ends the session. Master gates at 454fa2b7: 8861 xUnit
passed (8852 + 9), 19 warnings, format, diff-check, ASCII clean. Implementer's departure, accepted by the supervisor
as keeping sweep-c item 15's one-forgiven-failure rule for multi-tab operators (owner may overrule): failed reads
that were already in flight when a failure was charged count as the same outage and are not charged again; every
read that starts after a charge is charged. README "Session Revalidation" describes the rule as built.

**Review F (codex, over 341750c0..59522ca6, `.agents/review/rev-f.result.txt`):** rev-f1 HIGH (the item-12 folder
verdict does not check the parents of the app folder, so a parent-folder swap defeats it), rev-f2 MEDIUM (an unread
virtual-directory list of an application elsewhere warns instead of refusing), rev-f3 LOW (deploy.ps1 -PlanOnly ends
on two unguarded reads instead of printing the refusal and the remaining steps). Fix slice dispatched: branch
`fix/rev-f`, worktree `D:\source\ExchangeAdminWeb-wt\rev-f`, from 454fa2b7.

**Deploy checks added by rev-e1 (dev):** with the directory blocked, one tab, wait for one failed interval check
(Warning "1 failed lookup(s) in a row for this account"), reload: expect "This page was not shown" (403) and a
`SessionRevalidation` audit line with trigger `PageRequest`, reason `LookupFailed`; with two tabs open, one stall
gives one 1-in-a-row warning per tab and both tabs stay.

**Next:** rev-f hand-back -> merge on guard proof -> PowerShell gates; a codex review G over rev-e1 + rev-f
(reasonable scope: the two ranges only); then the final state entry and the one dev deploy when the owner declares it.

### 2026-11-09 - owner back: sweep-g merged, review E done (rev-e1 admitted), reviews move to codex

**Owner clears the blockers:** no permission prompt was ever shown, so the sweep-g stalls were inside a tool call
(most likely a script or Pester run reaching an interactive read). A third Opus 5.5 agent, briefed to run every pwsh
call `-NonInteractive` under a timeout and to commit per item, finished items 36-39 and the records with no hang
(~316K, 155 tool uses, 1 h 18 min). Reviews run on plain codex (Portkey) while codex-commercial's login is invalid
(owner, decision 2026-11-09).

**Merged:** sweep-g at 59522ca6 (items 12, 34, 35, 36, 37, 38, 39; records `sweep-g-*.md`). Master gates at
59522ca6: 8852 xUnit passed, 0 failed; 641 Pester passed; PSSA 0 errors; format, diff-check, ASCII clean. Item 36
note: the stopped agent's `deploy.ps1` work (a per-write `if ($PlanOnly)`) broke 20 structural tests and was
discarded; `deploy.ps1 -PlanOnly` is now one script-level plan block that makes the real run's reads and returns
before the first write; the real run's code is unchanged.

**Review E (codex, over 3f2bcb3b..78c03e49, `.agents/review/rev-e.result.txt`):** twelve rev-abc findings CLOSED,
rev-b5 PARTLY: **rev-e1 (MEDIUM)** page requests bypass the consecutive-fallback counter, so a reload during a
directory outage keeps access until the recorded answer ages out. Fix slice dispatched (branch `fix/rev-e1`, worktree
`D:\source\ExchangeAdminWeb-wt\rev-e1`, from 59522ca6). Review F (codex, over 341750c0..59522ca6) dispatched.

**New owner question (from sweep-g, item 38):** `deploy.ps1` does not restart the dev pool after a failed mirror
and keeps no previous build to roll back to; restoring that needs a binaries backup, which is new functionality.
The implementer left the `deploy.ps1` half unchanged; the promotion half is done. Decide: add a pre-mirror binaries
backup to `deploy.ps1` (one more step, more disk), or accept that a failed dev mirror leaves dev down until re-run.
Related, not changed: the promotion's `finally` restarts prod even when its rollback robocopy failed (exit >= 8),
which can serve a mixed build; pre-existing (cdt-16 class), recorded in `sweep-g-38.md`.

**Deploy checks added by sweep-g (not run on a host):** run `deploy-pipeline.ps1 -Dev -PlanOnly` and `-Prod -PlanOnly`
elevated under Windows PowerShell 5.1 and the cutover plan on the cut-over host: expect the steps, the serving-pool
answer, the db tool verdict and "0 refusal(s)" with nothing changed; before the item-12 host ACL change the plan
must name Authenticated Users in its REFUSES verdict, after it the verdict is clean; check
`(Get-ItemProperty IIS:\AppPools\<pool> -Name processModel.shutdownTimeLimit)` type and value (expect 00:01:30);
before making `-NarrowPublishAcl` the default, install dev with it, `Get-Acl` the folder, confirm the app starts.
Note: the item-12 tests run real `icacls` on temp folders they create (not mocked); acceptable, recorded.

**Next:** rev-e1 hand-back -> merge on guard proof -> master gates; review F result -> fix findings the same way;
then the final state entry and the one dev deploy when the owner declares it.


**Done since the owner left (2026-11-06 ~16:00):** sweep-e, Comms-10k clear-then-add revert, sweep-f (items 23, 25,
26, 27; 24's wording), item 45 records, codex-commercial defect reviews A-D over everything merged since the dev build
(13 findings, all fixed and merged as rev-abc), and rev-x1 admitted. Master `78c03e49` + records; gates there: 8852
xUnit, 19 warnings, format, diff-check and ASCII clean. Nothing deployed, pushed, or changed outside the repo.

**Blocked 1 - sweep-g (scripts: items 36-39 and records; 12, 34, 35 committed on the branch).** Two agents stalled the
same way in the worktree (entry above). Proposed action: owner answers any pending permission prompt in this terminal,
or says what the agents may not do; the supervisor then resumes the slice with the same brief.

**Blocked 2 - review E (codex-commercial over the rev-abc range `3f2bcb3b..78c03e49`).** codex-commercial now fails with
`401 Unauthorized: Encountered invalidated oauth token for user` (ChatGPT-subscription token invalidated after four
successful reviews today). Proposed action: owner runs `C:\Users\mcoelho\.local\bin\codex-commercial.cmd login`
(interactive) and says go; the supervisor re-dispatches review E (prompt ready in its scratchpad: `cx/review-E.txt`),
then review F over sweep-g once that lands.

**Owner questions queued (one at a time):** item 44 audit refusal convention (`sweep-e-44.md`); item 24 approve from
SyncedWithErrors (`sweep-f-24.md`); confirm the two Constitution edits (`sweep-45.md`); rev-x1 scope.

**Then:** the one dev deploy with its check list (all additions are in this file's entries from 2026-11-06).
### 2026-11-06 (sweep-g, blocked) - second attempt stalled the same way; slice deferred to the owner's return

A fresh agent resumed `fix/sweep-g` at 22:30, edited `tools/deploy-pipeline.ps1` and `README.md` for item 36 until 22:35,
then went silent (last text: "Now add README text for item 36."), no process running, no reply to a status message;
stopped 23:20. Both attempts stalled with no tool process alive, which fits a tool call held on a permission prompt that
only the owner's terminal can answer. The worktree keeps the uncommitted item-36 work (`deploy.ps1`,
`tools/promote-dev-to-prod.ps1`, `tools/Move-ConfigDbToShared.ps1`, `tools/deploy-pipeline.ps1`, `README.md`,
`tests/ps/AppPoolControl.Tests.ps1`), unverified. **Blocked; proposed action:** owner, on return, look for a pending
permission prompt in this terminal and answer it (or tell the supervisor what the agents may not do), then the slice
resumes with the same brief (`brief-sweep-g.md.full` in the supervisor's scratchpad; its content is the sweep items
12, 34-39 with the rules in this file's earlier sweep-g entry). Items 12, 34 and 35 are committed on the branch and
unmerged; nothing on master depends on them.
### 2026-11-06 (rev-abc) - all thirteen review findings fixed and merged

Merged `fix/rev-abc` into master at `78c03e49` (`--no-ff`, cherry 0, worktree and branch removed) on the agent's guard
proof (records `rev-a1..a3`, `rev-b1..b5`, `rev-c1..c3`, `rev-d1..d2` updated in place; all thirteen verified real,
none declined). Master gates: 8852 xUnit, 19 warnings, format, diff-check and ASCII clean. Versions: GroupManagement
2.16.5, SelfServiceGroups 1.14.3, LicensingUpdates 1.1.9, ADAttributeEditor 1.7.9, AdminSettings 1.3.2, MfaReset 1.6.3;
base stays 2.36.0.

Behaviour the owner should know: (a1) a token miss on a protected security group now goes to kd-88's link-read path
instead of being read as a confirmed negative; cost is one ancestry read per check (+3 reads for an ordinary account,
+2 per nesting level; estimated well under a second warm; the timing log line reports the real number). (a2) the
directory credential cache expires five minutes from retrieval and is dropped and re-read once when the directory
rejects it. (b4) until the Graph Directory Read Credential is filled on the deployed build, a protection check on a
synced recipient addressed by an alias AD does not hold refuses as unavailable (Blocked Senders included) - fail closed,
and the deploy step already on the list. (b2) every on-premises write target now carries its SID; a target without one
refuses. (b5) two consecutive failed revalidations end the session, as the README says. Departures and the other sites
with the same SearchBase root are in `rev-x1` (admitted, not fixed).

Deploy check list additions: fill the Graph credential first; a grant to a user reaching a protected security group only
through a distribution group must refuse, then read the "Protected-principal check ... took n ms (r directory reads)"
line; rotate the directory-read secret under the same ID and see one "rejected ... fetching it afresh" line then success;
run a protected check, a Verify and a conversion against a group in the other tree; an attribute-editor save logs
"pinned to domain controller <writable DC>"; a group write inside a renamed protected OU refuses.

### 2026-11-06 (sweep-g, stalled) - scripts slice stopped after three of seven items; resume later

`fix/sweep-g` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-g`, from master `145f513f`) holds items 12 (`ff681201`),
34 (`e02c4c0c`) and 35 (`aa0082f0`) committed, and item 36 (plan mode across `deploy.ps1`, `promote-dev-to-prod.ps1`,
`Move-ConfigDbToShared.ps1`, with `tests/ps/AppPoolControl.Tests.ps1`) UNCOMMITTED and unverified in the worktree. The
agent went silent at 19:12 with no commit, no file change and no reply to a status message for an hour (its last text:
"Now the password, pool config, ACL and IIS auth steps."); the supervisor stopped it at 20:17 as stalled, most likely on
a permission prompt nobody could answer. Nothing was merged. **Resume:** a fresh agent on the same branch finishes item
36 from the working tree (verify before trusting it), then 37, 38, 39, records; brief in the supervisor's scratchpad
`brief-sweep-g.md` plus this note. Owner: if a permission prompt is pending in the terminal on return, that was it.
### 2026-11-06 (reviews A-D) - codex-commercial defect reviews of everything merged since the dev build

Four read-only reviews (codex-commercial, gpt-6-astra, xhigh; prompts and reports in `.agents/review/rev-{a,b,c,d}.*`,
gitignored; findings committed as records): A `8557a15a..5c4c2970` (kd-88 family) 3 findings; B `5c4c2970..82a67e2f`
(identifiers, sweep-c) 5; C `82a67e2f..fe3b86b8` (Verify) 3; D `fe3b86b8..de53b714` (sweep-d/e/f, Comms order) 2.
Thirteen records `rev-a1..a3`, `rev-b1..b5`, `rev-c1..c3`, `rev-d1..d2`, index rows `[ ]`. Highest HIGH, no CRITICAL, so
no owner-gated verification round applies (2026-08-31 rule). The HIGH ones are all on the protected-principal path:
a token miss past a distribution group read as a confirmed negative (a1); the credential cache's sliding expiry (a2);
no empty SearchBase on global-catalog queries, in three places (a3, b1, c1); label fallback when a caller omits the SID
(b2); OU rules by DN label for group targets (b3); Graph-unanswered falling through to address resolution (b4).
Next slice `fix/rev-abc` fixes all thirteen (brief drafted), one Opus agent, after sweep-g lands. sweep-g gets its own
review (E) when merged.
### 2026-11-06 (sweep-f) - sweep items 23, 25, 26, 27 landed; item 24 widening is an owner question

Merged `fix/sweep-f` into master at `de53b714` (`--no-ff`, cherry 0, worktree and branch removed; one index-row
conflict resolved by keeping both sides) on the agent's guard proof (records `.agents/review/findings/sweep-f-2*.md`).
Master gates: 8810 xUnit, 19 warnings, format, diff-check and ASCII clean. Versions: CalendarPermissions 1.4.13,
Migration 1.27.3, RecipientLookup 1.8.3, OutOfOffice 1.1.6, ADAttributeEditor 1.7.7; base stays 2.36.0.

Item 24, not widened: Microsoft documents Complete-MigrationBatch from Synced or SyncedWithErrors, but the 2026-09-25
decision ("a migration batch with errors is not completable") and aud-a23r (HIGH) hold approval to exactly Synced.
The refusal now says the batch holds errors and what to do. **Owner question (ask one at a time, with item 44's):**
should a single-user batch be approved and completed from SyncedWithErrors? Read-only check that shows whether it
blocks anyone today: `Get-MigrationUser -ResultSize Unlimited | ? Status -eq NeedsApproval | % { Get-MigrationBatch
-Identity $_.BatchId } | select Identity,Status`.

Deploy check list additions (each in its record): on-prem calendar grant/change/remove on a test mailbox with no
"could not be confirmed" and no `_UNCONFIRMED` mail, including a non-English calendar name; cloud Reviewer grant to a
user with no entry succeeds, and an unresolvable user's refusal begins "Set-MailboxFolderPermission failed for"; an
auto-reply scheduled from a non-UTC browser reads back the typed clock in OWA and Get-MailboxAutoReplyConfiguration;
an attribute-editor save of one extensionAttribute and one msExchRecipientDisplayType shows Undo, no "was not
confirmed", and a "pinned to domain controller" log line.

### 2026-11-06 (item 45) - record drift fixed; two Constitution sentences corrected for owner confirmation

Sweep item 45 done directly on master as records (`.agents/review/findings/sweep-45.md`): finding status lines for
aud-a2ab/a2z/a2aa/a2ac/kd-66 match their index rows; aud-a2ad has an index row and a merged status; the live-assumptions
list names aud-a2ac/a2ad and Recipient Lookup A4. `docs/ProjectConstitution.md` line 80 now carries the kd-44
exception (unconfirmed outcome notifies administrators only, ruling 2026-10-27) and line 95 now says a cloud-only
principal cannot be protected by this app (structural, since protected rows are on-premises identifiers), replacing the
2026-07 "protected by address" sentence. **Owner to confirm both Constitution edits on return**; they record rulings
and an approved plan, not new policy.

### 2026-11-06 (comms10k-order) - Comms-10k write order back to clear-then-add

Merged `fix/comms10k-order` into master at `0d3fcac8` (`--no-ff`, cherry 0, worktree and branch removed) on the
agent's guard proof (record `.agents/review/findings/comms10k-order.md`). Master gates: 8757 xUnit (seven item-19
writer tests removed, two added), 19 warnings, format, diff-check and ASCII clean. Comms10k 1.4.21.

Restored: clear, then add every file member as `<GUID=...>` in 2,000 batches, as `docs/Comms10kBulkResolveScale-Plan.md`
approved. Kept from sweep-d: a failed add after the clear names the batch and error; item 20's single authorization
check now sits immediately before the clear. Removed: the pre-write membership read, the remove set and `-Remove`.
Deploy check: the scratch-group `-Remove <GUID=...>` step is withdrawn (form no longer used); the module's own
clear-then-add run on a scratch group stays as the end-to-end check.

### 2026-11-06 (sweep-e) - sweep items 21, 28, 40, 41, 42, 43 and 44 (docs half) landed; weekend goal set

Merged `fix/sweep-e` into master at `39544c0a` (`--no-ff`, cherry 0, worktree and branch removed) on the agent's guard
proof (records `.agents/review/findings/sweep-e-*.md`). Master gates: 8762 xUnit, 19 warnings, format, diff-check and
ASCII clean. Versions: MailboxPermissions 1.6.11, CalendarPermissions 1.4.11, ConferenceRooms 2.20.1, AdminBulkJobs
1.2.3, plus 16 module patch bumps for the ticket rule (item 40); base stays 2.36.0.

Worth knowing: item 21 bulk runs now send one digest per trustee and per owner after the loop, and a row Exchange
answers with a no-change warning is shown "No change" and mails nobody (warning wording assumed, harmless if wrong).
Item 28 is per row, not per job. Item 44 changed no audit action or result value: the README documents the closed
`result` list and six refusal shapes; the convention question is open for the owner (`sweep-e-44.md`). Account Lockout
Remediation's ticket check (IsNullOrWhiteSpace, no ServiceNow) was outside item 41's list and is recorded, not fixed.
The README Comms-10k field note "clear-then-fill never reads the prior membership" is stale since sweep-d item 19 and
becomes true again with the revert slice; that slice fixes it.

Deploy check list additions: bulk-grant FullAccess to a user who already has it (expect "No change", no mail); a 3-row
CSV for one trustee (expect one mail); one synced-list Room Finder row with City/State/Country (one credential purpose
logged, time against the old 25-30 s); paste a ticket with a zero-width space (message under the box, button disabled);
show the Splunk owner the README result and refusal-convention section.

Owner, 2026-11-06, leaving for the weekend: a `/goal` stop hook keeps this session working unattended with this
authority: merge agent branches on guard proof after green gates; dispatch, one Opus 5.5 agent at a time, the Comms-10k
clear-then-add revert, items 23-27, scripts 34-39, records 45, item 12 code half; then codex-commercial defect reviews
per merged group (reasonable scope, authorization and write paths first) with findings fixed and merged. No deploy, no
push, no config or host change, no owner decision (item 44 convention included). If blocked: obstacle and proposed
action here, then wait.

**Next:** see the 2026-11-07 entry at the top: two owner-cleared blockers (sweep-g permission prompt; codex-commercial login), then review E, sweep-g, review F,
then master gates and the final state entry; the one dev deploy waits for the owner.

### 2026-11-06 (sweep-d) - sweep items 19, 20, 22, 29, 30, 31, 32 landed; three owner questions settled

Merged `fix/sweep-d` into master at `c06b07b0` (`--no-ff`, cherry 0, worktree and branch removed) on the agent's
guard proof (every mutation caught; tables in `.agents/review/findings/sweep-d-*.md`). Master gates: see the
2026-11-06 token-log line. Module versions: Comms10k 1.4.20, GroupManagement 2.16.2, M365GroupManagement 1.9.1,
CloudPasswordReset 1.2.2, DefenderEndpointDevices 1.6.19, EmergencyDisable 1.7.2; base stays 2.36.0.

Departures worth knowing: item 19 add-before-remove makes a full swap to a wholly different list about 53 s against
7.9 s before (plan's own measurement); the `-Remove <GUID=...>` form is assumed accepted wherever `-Add` is (deploy
check). Item 29's "run each search once" was not done: the confirm re-derives the destination because cpr-9 (HIGH)
requires it. Item 31 changed five tests that used a 429 to mean "one batch fails, the rest carry on".

Deploy check list additions: Comms-10k scratch-group run of the module path (GUID member form verified live by the owner 2026-11-06,
decisions.md) after downloading the membership CSV; Graph grants and roles for Cloud Password Reset plus the `employeeID` `searchFlags` index; User.Read.All
(Application) on the M365 Groups registration; `runHuntingQuery ... 429` lines and the two new Defender warnings;
`NotifySecurityTeam` and `Email:AdminNotificationEmail` on both instances.

Owner rulings 2026-11-06 (decisions.md): item 16 keep revalidation (5-minute window); item 33 keep through-group
rights (no validation-time cost, verified); end-of-work codex-commercial defect reviews run per merged group, kept
reasonable.

**Next:** see the sweep-e entry above.

### 2026-11-06 - Verify plan Implemented (V1-V4 landed); back to the sweep

**Master `fe3b86b8`** (merge of `fix/dov-v4`; worktree and branch gone). `docs/DirectoryObjectVerify-Plan.md` Implemented;
section H lists the as-built departures. V4: `Services/ModuleConfigVerifyAdapter.cs` and `ModuleConfigVerifyActions.cs`
verify the config pane's text settings one list entry at a time - Migration excluded groups through the module's own
per-domain lookup (`MigrationService.TryResolveExcludedGroup`, throw preserved), the lockdown OU via
`Get-ADOrganizationalUnit` (container = missing), search bases by base read with DN spacing ignored, the eight Conference
Rooms addresses via `Get-Recipient` (not-found = missing, non-group = wrong type, several = ambiguous); field types
`SearchBase` and `RecipientGroup`; Edit (opens the field or OU browser), Remove one entry (last search base needs the
in-page confirmation, which the write also enforces), Ignore (display-only); global admin or that module's module admin;
run-book step "press Verify on each page". Record dov-v4 (31 mutations caught). Supervisor's gates on master at
`fe3b86b8`: Release build 0 errors (19 known warnings); xUnit 8601 passed, 0 failed; format, whitespace and ASCII clean.
Not pushed. Not run anywhere in V1-V4: the PowerShell and Exchange reads against a real forest or tenant.

**Deploy check list additions from the Verify work:** press Verify on Admin Settings Protected principals, Module
Configuration Access and Module Admins for one module, and the config pane of Migration, Emergency Disable, AD Attribute
Editor, BitLocker Recovery and Conference Rooms; confirm a Conference Rooms address reads Verified (recipient group types
unverified against live Exchange).

**Next:** see the sweep-d entry above.

### 2026-11-05 (V3) - Verify on the Module Configuration group panes landed; V4 next

**Master `7c15250a`** (merge of `fix/dov-v3`; worktree and branch gone). `Services/ModuleGroupVerifyAdapter.cs`,
`ModuleGroupVerifyActions.cs`, `Storage/ModuleGroupVerifyStore.cs`: section-access, servicer and module-admin grants verified
by SID at the catalog in the press's warm session; legacy names by each store's existing rule (A3 planner in the host's
domain through the migration's own directory, A4 forest-wide); one Verify button per tab (Access covers servicer);
Rename, Convert, Replace (picker + save), Remove now, Ignore (display-only) with global-admin re-authorization at the click,
refusals audited under `Verify:<Store>`; ignores pruned on the tabs' ordinary saves. Engine gained a `ResolveOutsideDirectory`
lookup kind. Module Configuration rides the base version (2.36.0, unchanged). Record dov-v3 (29 mutations caught; commits
carry an Opus trailer). Supervisor's gates on master at `7c15250a`: Release build 0 errors (19 known warnings); xUnit 8563
passed, 0 failed; format, whitespace and ASCII clean. Not pushed. Watch item: the implementer saw one load-flaky failure of
`MessageTraceSubmittedValueTests.AKeystrokeLanding...` ("finished without yielding") that passed on re-run; it did not
recur in the supervisor's run.

**Next:** V4 (text settings A5-A8 and the post-deploy "press Verify on each page" run-book sentence), the last Verify slice.

### 2026-11-05 (later) - Verify slice V2 landed (an ignored missing group no longer refuses); V3 next

**Master `b0c1b06e`** (merge of `fix/dov-v2`; worktree and branch gone). Plan B.4 as built: `ProtectedGroupMembership.Evaluate`
takes the set of SIDs whose Missing finding is ignored; the token match and the lookup still run on every check and only a
lookup that confirms zero groups is skipped (Debug line, `Result.Ignored`, ", N ignored missing groups" in the timing line);
a failed read, several matches, or a found group are evaluated as before. The set is built in `ProtectedPrincipalService`
from the ignore list read in the same transaction as the rules (per-rule-set table, never a second read); a legacy Group row
stored as the same SID is excluded and still refuses. A non-ignored confirmed zero reads "no such group - run Verify on
Admin Settings". README Verify paragraph and a new Troubleshooting entry, run-book line, pane tips. AdminSettings 1.3.1.
Record dov-v2 (15 mutations caught). Supervisor's gates on master at `b0c1b06e`: Release build 0 errors (19 known
warnings); xUnit 8515 passed, 0 failed; format, whitespace and ASCII clean. Not pushed. The deleted-group open question
of 2026-11-03 is closed by this slice. Left for V4: the post-deploy "press Verify on each page" run-book sentence (plan D).

**Next:** V3 (Module Configuration groups: Access, Protection servicer, Module Admins), then V4, one Opus agent at a time.

### 2026-11-05 - Verify slice V1 landed (engine, ignore list, Protected Principals pane); V2 next

**Master `77ad7fcd`** (merge of `fix/dov-v1`; worktree and branch gone). `docs/DirectoryObjectVerify-Plan.md` (Approved
2026-11-04) slice V1: `Services/DirectoryObjectVerifier.cs` with `IVerifyStoreAdapter` and the A1/A2 adapter (one operation,
one warm session with the directory-read credential, catalog lookups in chunks of 50, forest check on every stored DN,
unreadable never Verified or Missing); `verify_ignores` with compare-and-swap, read with the rules in one transaction, pruned
on Remove/Replace/ordinary save; Rename, Replace, Convert (`ConvertProtectedRow`, shared with the CLI), Remove now, Ignore -
version-checked, re-authorized at the click, success and refusal audited apart; Verify button, badges, `verify_last` and
help replace the page-load sweep and the not-converted banner; `db identifier-status` prints the ignore list. AdminSettings
1.3.0. Record dov-v1 (28 guard proofs). Departures the implementer recorded: forest check applied before any lookup (an
out-of-forest DN is unreadable with Remove its only action); a `user_sid` row's identifier the catalog lacks reads Missing
(assumption: another forest's user reads as missing; display-only); `ShouldFlagAsStale` and its 4 tests deleted with the
sweep. Supervisor's gates on master at `77ad7fcd`: Release build 0 errors (19 known warnings); xUnit 8488 passed, 0 failed;
format, whitespace and ASCII clean. Not pushed. The PowerShell session reads (`FindVerifyObjects`, `ForestDomainServer`)
were not run against a forest; the Verify press is on the deploy check list.

**Next:** V2 (live-check effect of an ignore, plan B.4 and E "V2"), then V3, V4, one Opus agent at a time. The
deleted-group open question below is answered by the plan (B.4) and closes when V2 lands.

### 2026-11-03 (later) - sweep items 13-18 and S6 landed; the identifier conversion is a one-time owner-run db verb

**Master `82a67e2f`** (merge of `fix/sweep-c`; worktree and branch gone). Owner rulings, `.agents/decisions.md` 2026-11-03
"Identifier conversion is a one-time command-line step, not a startup pass" and plan section K: the S2/S5 startup passes,
flags, Resolve now buttons and badges are removed; `db convert-identifiers <db> [--plan]` and `db identifier-status <db>`
(`Services/Storage/ConfigDbIdentifiers.cs`, directory as the invoking user via `InvokingUserIdentifierDirectory`) convert
legacy protected-principal rows and module-admin name grants, all or nothing; the new build never resolves or matches
names - an unconverted row fails the check closed, named ("not converted - run db convert-identifiers"). Run-book:
`docs/ConfigDbOperations.md` (copy, plan, convert, status, then live). Sweep items 13, 14, 15, 18 fixed (records
sweep-c-13/14/15/18), 17 rebuilt under K (sweep-c-17), 16 is a record for the owner
(`.agents/review/findings/item-16-revalidation-decision.md`). Records ppi-s6. Supervisor's gates on master at `82a67e2f`:
Release build 0 errors (19 known warnings); xUnit 8461 passed, 0 failed; format, whitespace and ASCII clean. Not pushed.

**Open owner question (closed by V2 at `b0c1b06e`, plan B.4):** a `group_sid` row whose group has since been deleted. The directory definitively
answers "no such object" for the SID; under K as built that makes every check that finds no other match incomplete,
naming the row - the item-17 outage shape for a deleted protected group. Then item 16 (keep session revalidation).

**Still in flight before the deploy is declared:** sweep MEDIUM items 19-45, item 12 code half, kd-94's shape if needed.
Owner-side at the deploy, in order: `db convert-identifiers` against a copy then live (plan K.4); fill the Graph field;
confirm application `User.Read.All`; host ACLs (item 12); then the supervisor's check list.

### 2026-11-03 - protected principals by identifier landed (S1-S5); one dev deploy owed when everything in flight is done

**Master `2e80f488`** (superseded above for S2/S5: the passes are gone). `docs/ProtectedPrincipalIdentifiers-Plan.md` Implemented: protected users/groups by SID and OUs by
objectGUID in their own kinds with companion legacy rows (S1); conversion pass at every start plus Resolve now and badges
(S2); synced write targets bound by the on-premises SID Graph reports through the new Admin Settings field "Graph Directory
Read Credential (Delinea Secret ID)", the aud-a2c GUID proof retired (S3); Conference Rooms runs no protected-principal
check - binder replaces the gate, servicing option gone, Constitution and repo guidance rewritten, ConferenceRooms 2.20.0
(S4); module-admin name grants converted to SIDs at start and on Resolve now, the rest listed with reasons (S5). Records
ppi-s1..ppi-s5. Supervisor's full gate run on master at `2e80f488`: Release build 0 errors (19 known warnings); xUnit 8412 passed, 0 failed; format, whitespace and ASCII clean.

**Owner rulings today (`.agents/decisions.md` 2026-11-03):** Conference Rooms - no protected-principal check at all; one
decision at a time; "do it correctly" is the standing answer (thorough form, never ask about cost); the sweep's open
decisions resolved by it (item 6 via the plan, item 8 via S5, item 12 both halves, items 9 and 21 by judgement, the cache
stays); Graph is the SID source, through its own field, nothing pre-filled from Cloud Password Reset; ONE dev deploy,
declared by the supervisor when everything in flight is merged and green - dev stays on `8557a15a` until then, and users
who land on `/ExchangeAdminWebDev` (three did today, hitting kd-88's walk) are told to use prod.

**Still in flight before the deploy is declared:** the sweep's MEDIUM items 13-45 (`.agents/review/live-risk-sweep-2026-10-30.md`;
item 6 closed by the plan, 8 by S5, 17 and 28 largely by S1-S4 - each still gets its own check), the script refusal for a
non-admin-writable folder (item 12 code half), kd-94's shape if the dev check shows one. Owner-side, at the deploy: fill
the Graph field; confirm the registration holds application `User.Read.All`; the host ACL commands (item 12 host half);
then the check list the supervisor hands over.

### 2026-10-31 - after the first dev deploy since 14 October: the live failures fixed, prod waits on three owner decisions

**Master `5c4c2970`, every gate green, re-run by the supervisor:** Release build 0 errors (19 known warnings); xUnit 8316
passed, 0 failed, 0 skipped; Pester 554; PSScriptAnalyzer 0 errors; `dotnet format`, `git diff --check`, ASCII lint clean.
Base app version 2.36.0 (not yet on prod; absorbs every shared change since 2.35.0). No worktree or fix branch remains.
Master is NOT pushed. Dev runs `8557a15a` (deployed 2026-10-30 08:18) and needs a redeploy to pick up everything below.

**What the dev deploy exposed, all fixed on master.** The dev build before 2026-10-29 was `107cfe4f` (14 October); prod is
`32d28f96` (9 October). About 1,300 commits had never run live. Found live: cdt-61 (the pre-stop serving-pool check read a
property 5.1 sites lack - every deploy refused), kd-87 (tokenGroups read by SID - every page refused), kd-88 (the
protected-group check's domain-wide walk and triple credential fetch - every grant waited two minutes and refused; owner:
"this has to be near-instant" - now one tokenGroups read, one credential fetch per operation kept five minutes in memory, a
warm session, a timing line, swept across 17 modules), kd-90 (a silent sign-out on a disposed Windows identity). Found by
the live-risk sweep (`.agents/review/live-risk-sweep-2026-10-30.md`, 12 HIGH / 33 MEDIUM / 32 LOW) and fixed: kd-89
(Exchange's not-found sentence recognised again - Migration checks and on-premises permission targets), kd-91 (connect
gate re-checks the pool; autocomplete cancels superseded searches), kd-92 (Group Management's manager notice sent, no false
Failed row), kd-93 (Defender's Site read outside the hunting budget), cdt-62 (installer identity-type cast). Also:
CloudPasswordReset 1.2.1 (card and permission text trimmed).

**Open:**
- kd-94 (`[ ]`): on-premises "no archive" may read as Undetermined; no log carries the error text - settled by ONE dev
  Migration ToCloud eligibility check of an on-premises mailbox with no archive.
- Three OWNER decisions from the sweep (HIGH items 6, 8, 12): (a) the mailbox-GUID proof (aud-a2c) refuses a grant to a
  cloud mailbox whose on-premises object lacks msExchMailboxGuid or lives in another domain - revert, or warn instead of
  refuse until counted; (b) module-admin grants stored by name stopped authorizing when dev deployed (shared database) -
  re-pick each group in Module Configuration; the list of affected grants aud-a1 promised is not yet produced; (c) `D:\`
  grants Authenticated Users Modify by inheritance, so elevated scripts run a user-writable DLL and anyone can replace both
  pools' binaries - break the inheritance under `D:\inetpub`, `D:\backups`, `E:\WWWOutput`, or make the scripts refuse.
- The sweep's 33 MEDIUM items are unaddressed; the file ranks them with a check each. Notables the owner should read:
  Comms-10k's GUID form (item 19, can empty a list on first run), bulk-run notifications per person (item 21), the
  manager-notice template with the operator's IP (item 9 "Also").
- Manual dev checks still owed: one ToCloud eligibility check; one grant on an on-premises mailbox and its duration from
  the new timing line; one approval of a mailbox in a batch; one Group Management change; a day of logs with no "error
  occurred while revalidating"; Recipient Lookup one of each kind; Cloud Password Reset once switched on.

**Plans:** CloudPasswordResetEmployeeId, RecipientLookupAccuracy, CancellationDepth - Implemented. InAppHelp - Approved,
starts after 2026-10-31. Next-round rule (owner 2026-10-28): briefs point agents at sections and line ranges; model Opus 5.5.

**Next step:** redeploy dev (`tools/deploy-pipeline.ps1 -Dev`, elevated), run the checks above, then the owner's three
decisions, then prod.
### Fix-everything work stream - HALTED TWICE on 2026-10-26, now by the gateway key limit

**What it is.** Owner go of 2026-10-21, verbatim: "fix all the bugs that you know about and any
more you find while doing so", then "ALL the bugs includes ALL the bugs. those too. all of them.
... EVERYTHING." That approved every open follow-up in `.agents/review/index.md` recorded "not
approved" or "owner decision required" (`.agents/decisions.md` 2026-10-21). The stopping rule of
2026-10-23 narrowed it for one afternoon and the owner's 2026-10-23 EVENING ruling lifted that hold
again: every remaining LOW, MEDIUM and HIGH is fixed before any deploy. That is the live scope.

**Arrangement.** Opus 5.5 implementation agents dispatched from a supervising Fable session,
each in its own worktree; one finding per commit; mutation proof per guard; a codex (gpt-5.5,
xhigh) defect review of each merged group. The findings and their status live in
`.agents/review/index.md` and `.agents/review/findings/`; this file does not copy their counts.
Each slice's arrangement and cost go in `.agents/token-log.md`.

**Master's position and gates are in the halt block below, not here.** `Modules/ModuleCatalog.cs`
owns which module versions moved; `ExchangeAdminWeb.csproj` owns the base version.

**HALTED AGAIN, 2026-10-26 evening, by a different limit.** The first halt was the account's individual spend limit on
`claude-opus-5-5` (HTTP 429); the owner cleared it, every agent resumed from its transcript and the stream ran a full
round. All six agents still working then died within minutes of each other on **HTTP 412, "Portkey API Key Usage Limit
Exceeded", on the gateway route for `anthropic.claude-opus-5`** - the gateway key's own cap, not the Anthropic spend
limit. The supervising session (this one) still has requests, and so does codex through its separate route.

**Master after the evening consolidation (`5b786996`); every gate re-run by the supervisor:**

| Gate | Result |
| --- | --- |
| Release build | 0 errors, 0 warnings |
| xUnit suite | 7347 passed, 0 failed, 0 skipped |
| Pester | 508 passed, 0 failed, 0 skipped |
| PSScriptAnalyzer | 0 errors |

`dotnet format`, `git diff --check` and the ASCII lint are clean. The base app version is still 2.35.0 and **one base
bump is owed at close**; the shared services that changed are named in the findings.

**What the evening consolidation merged** (every slice each agent had committed when it died): kd-69; kd-62, kd-63,
kd-64, kd-65; kd-66; kd-45, kd-46, kd-48, kd-49; kd-18, kd-40, kd-43, kd-44 (Calendar Permissions, completing both
pages); cdt-57. **None of it has had a codex review of its own.** Four merge conflicts needed real judgement and each is
recorded in its merge commit: five module version entries where parallel branches each bumped the same module (both note
sets kept, versions renumbered), the Emergency Disable catalog test reconciled with its new version, and
`Services/TicketPresence.cs`, where master already carried the stronger kd-48 rule and another branch still carried the
kd-24 one it forked with - master's was kept.

**Eight worktrees are kept** (`git worktree list`). `fix/kd-misc` is unmerged IN FULL - fourteen verified fixes - because
its first commit is `kd-1`, which waits on an owner answer (below). The other seven are merged up to their last commit
and hold uncommitted mid-slice work; do not delete any of them.

**Still owed:** every `[ ]` and `[~]` row in `.agents/review/index.md`. The open MEDIUMs worth naming are `cons-7` (a
duplicate same-target module load strands the page blank), `kd-68` (the missing-room-list refusal carries no refusal
fields), `kd-52`'s descendants and `cdt-58`; aud-a67 **(12)** and `kd-58` are the two that need a written plan rather
than code.

**Owner decisions, unchanged and still unanswered** - the seven listed below, plus one added this evening: **aud-a2ab
removed a feature.** Room Finder apply no longer creates a building's room list, because the list's identity cannot be
known before Exchange assigns its address, so a protected-target rule cannot be checked against it. The operator now
creates the list in Exchange Online and re-runs. Reversing it means accepting a write before the check.

**NEXT ACTION: the owner's.** Either clear the gateway key's cap and say to resume - the agents resume from their
transcripts, and each needs its queue re-sent - or name a smaller scope. Nothing deploys: the 2026-10-23 evening ruling
stands that every open LOW, MEDIUM and HIGH is fixed first.

**The first halt, 2026-10-26 morning: every implementation agent was killed mid-slice by the account's individual spend limit**
(HTTP 429 on `claude-opus-5-5`, eight agents within minutes of each other). Nothing was lost: each agent's
committed slices were merged in a consolidation pass and master is green, but **no agent finished its queue and
the merged consolidation has had no codex review of its own until `.agents/review/consolidation-1.result.txt`
says otherwise.** This is the live blocker; the owner decides how the work resumes.

**Master as of the consolidation (`8f55802a`, gates re-run by the supervisor on `f07f2db7`):** Release build 0
errors / 0 warnings; full suite 7161 passed / 0 failed / 0 skipped; Pester 504 passed / 0 failed; PSScriptAnalyzer
0 errors; `dotnet format`, `git diff --check` and the ASCII lint clean. The base app version is still 2.35.0 and
**one base bump is owed at close** - several shared services changed (`TicketPresence`, `ModuleCredentialService`,
`ADDirectorySearchService`, the revalidator, the log services).

**What the consolidation merged** (`00798adb..8f55802a`): cdt-52, cdt-53, cdt-54; aud-a64 items (1)-(4); kd-33,
kd-34, kd-35, kd-36; kd-18 and kd-40 for Mailbox Permissions only; kd-52; kd-47. One cross-branch defect was found
by the gate and fixed at `8f55802a`: kd-52 made Conference Rooms' access refusal audited, which broke a kd-7
control test pinning its absence. Expect more of that shape - the branches were written in parallel against
different bases.

**Eight worktrees are kept, each with uncommitted mid-slice work** (`git worktree list`; branches `fix/a2e`,
`fix/cdt36`, `fix/a64-a67`, `fix/a68-kd2`, `fix/a70`, `fix/kd-misc`, `fix/psl10-a2y`, `fix/kd7-kd8`). Do not delete
them: they hold the half-finished slices and each agent can be resumed from its transcript. `fix/kd-misc` is
unmerged IN FULL - thirteen verified fixes - because its first commit is `kd-1`, which waits on an owner answer
(below).

**Still owed, by row:** every `[ ]` and `[~]` row in `.agents/review/index.md`. The large groups are aud-a67 (not
started), aud-a64 (5)-(9), the Calendar Permissions half of kd-18 and kd-40, kd-37, kd-38, kd-39, kd-41, kd-43,
kd-44, kd-45, kd-46, kd-48..kd-51, kd-53, kd-54, kd-55, kd-56, cdt-55, cdt-56, aud-a2z, aud-a2aa, aud-a2ab (HIGH),
aud-a2ac.

**Owner decisions the stream is blocked on** (none were answered; each is recorded in its finding):
1. `kd-1` - Message Trace detail exports: the fix reverses D2 and D4 of `docs/MessageTraceDownloadLink-Plan.md`
   (no ownership check; a mailed link anyone can use). Keep it and supersede those rulings, or drop the one commit
   and close `kd-1` as by design. **`fix/kd-misc` cannot merge either way until this is answered.**
2. `aud-a70` (6) - refuse every grant while the directory-read credential cannot be had. Agent recommends yes.
3. `aud-a70` (13) - stop counting Undetermined as a success in telemetry and the trace operation. Agent
   recommends yes.
4. `aud-a70` (10) - the agent DISPUTES the lazy credential fetch (it would look up Exchange-named people as the
   app pool even with the credential configured, against Architectural Invariant 7). Needs adjudication.
5. `psl-10` (5) - tag `ADDirectoryLiveTests` and `AdDirectoryForestSearchLiveTests` so the default gate and CI
   stop reading the live directory. Until then the mandated gate itself makes live calls.
6. `aud-a2e` - `docs/ProjectConstitution.md` says a cloud-only principal is protected by a user row, but
   `Services/ProtectedPrincipalEntryValidator.cs` refuses any entry Active Directory does not hold, so a cloud
   room list cannot be protected today. Which wins.
7. `aud-a29g` - accept the one-interval window on page renders, one audit line per refused page request with a
   standalone 403 page, and cache-free circuit checks.

**NEXT ACTION: the owner's.** Either raise the spend limit and say to resume (the agents resume from their
transcripts; send each one its queue), or name a smaller scope. Nothing deploys: the owner's 2026-10-23 evening
ruling stands that every open LOW, MEDIUM and HIGH is fixed first. The paragraph that follows is the 2026-10-23
afternoon hand-over, kept for its checklist pointers.

**The stream was reopened 2026-10-23 evening and ran until the halt above.** Owner ruling
(`.agents/decisions.md` 2026-10-23 evening, verbatim there): every remaining LOW, MEDIUM and HIGH issue is
resolved before any dev or prod deploy; the LOW hold and the three never-dispatched items were released.
Arrangement (below) was unchanged: a thin supervising session, Opus 5.5 implementation agents in their own
worktrees, a codex defect review per merged group. Roughly sixty findings were fixed, merged and reviewed before
the halt; each merged group's codex verdict is in its finding record.
 Owner ruling (`.agents/decisions.md` 2026-10-23 evening, verbatim there): every
remaining LOW, MEDIUM and HIGH issue is resolved before any dev or prod deploy; the LOW hold and the three
never-dispatched items are released now. Work in progress: the `.agents/review/index.md` rows whose Status begins
"Open; released by the 2026-10-23 evening ruling" (the six held LOW records, `aud-a2e`, `cdt-36`, `aud-a29g`, and
`kd-1`..`kd-9`, promoted from this file's former "Known defects" list and the ClickGateRegistry comment blocker).
Arrangement unchanged (below). Fourteen stale `[~]` parent rows whose follow-ons were already complete were closed
as bookkeeping in the same pass. **NEXT ACTION while the stream is open:** the supervising session dispatches,
merges, codex-reviews and records; the owner deploys nothing until this block says closed again. The paragraph
that follows is the 2026-10-23 afternoon hand-over, kept for its gate results and checklist pointers.

**The stream was closed (2026-10-23 afternoon).** `aud-a76` landed (`03261f7c`, codex perm-batch-10 clean); every
merged finding is codex-reviewed (`.agents/review/index.md`). Final gates on master as of `cfccec59`:
Release build 0 errors / 19 warnings; full suite 6693 passed / 0 failed / 0 skipped; `dotnet format`,
`git diff --check` and the ASCII lint clean. **NEXT ACTION: the owner's first dev deploy and the checklist
below.** Deployment is the owner's; nothing in this stream deploys. The held LOW backlog and the three
never-dispatched items (`aud-a2e`, `cdt-36`, `aud-a29g`) are the index rows whose Status says "held by the
2026-10-23 stopping rule"; `aud-a2e` is an authorization gap (Known Failure Class 3) and is the first to take
up after the deploy. Handoff 2026-10-23, verified head `e314166a`, tree clean but for the owner's untracked
`docs/TacticalAdminAccounts-Plan.md` (not this stream's; left alone). Owner rulings after the hand-over: the Directory
Read Credential will be the privileged write secret (`.agents/decisions.md` 2026-10-23); the held backlog was asked
about, not released - no agent runs. Dev may deploy any green master; dev does not depend on prod (the audit-file lock
and the SQLite engine notes describe the dev/prod interval only).

### The owner's checklist around the first dev deploy

None of it is automatable, and each record owns its detail. Several records mark their item "dev
acceptance owed", while `.agents/decisions.md` 2026-10-12 makes manual acceptance production use
rather than owed work, so whether any of these gates a promotion to prod is the owner's call.

Before deploying:

- `aud-a60` / `aud-a69`: configure the Directory Read Credential in Admin Settings, Protected
  principals, first - without it every Mailbox or Calendar Permissions grant to a directory-held
  group is refused.
- `aud-a47`: dev and prod may share a log root only on this build, so both instances must run it.
- `cdt-41`: the SQLite engine moves to 3.53.3. Dev and prod share one config database and will
  run two engines until both are deployed.

The deploy and the host:

- The first dev deploy itself (`./tools/deploy-pipeline.ps1 -Dev`, elevated).
- ConfigDbTool AC7: the plan-mode runs, `deploy-pipeline.ps1 -Prod -PlanOnly` among them
  (`docs/ConfigDbToolInApp-Plan.md`, section 10).
- Remove the per-user `sqlite3.exe` (`aud-sqlite3-path`; the path is in `.agents/machines.md`).

After the deploy:

- `aud-a2d`: the grant and revoke paths its record lists.
- `aud-a29b`: a revocation takes effect while a circuit stays open.
- Conference Rooms: the room-list path in a second domain (`aud-a2j`).
- `aud-a26l`: manager and Boolean edits in the attribute editor.

Labelled live assumptions, each named in its record and proven only in production:

- `aud-a23h`, `aud-a23n`, `aud-a23r`, `aud-a23j`, `aud-a23o`, `aud-a54`, `aud-a55`: Exchange
  not-found shapes and `Get-MigrationUser` status values.
- `aud-a43`: no UPN claim under Negotiate.
- `aud-a2ac`, `aud-a2ad` (Conference Rooms room-list lookup) and Recipient Lookup A4 (`docs/RecipientLookupAccuracy-Plan.md`): rest on the Exchange not-found recogniser that kd-89 restored (sweep item 2).

**Held LOW backlog:** released 2026-10-23 evening; see the REOPENED paragraph above. No index row reads "held by
the 2026-10-23 stopping rule" any more.

**Open owner decisions:** whether `-NarrowPublishAcl` becomes the installer default
(`aud-operational-hardening`), which needs a dev install that exercises the modules. No index row
reads "OWNER DECISION" any more (the 2026-10-21 go approved them all; checked as of `d881ea58`).

## Carried forward

What is still live from blocks rotated to `docs/history/state-archive.md` (Archived 2026-10-23
and earlier). Each line names its owner; the derivations are in the archive.

### Standing owner instructions recorded only here

- **Do not probe Active Directory without a direct go** (owner, 2026-10-14).
- **The Migration module is closed by owner order** (2026-09-29). Work on it only for a prod
  blocker the owner names; his two named exceptions were the `@key` checkbox fix and queue item
  23 (both 2026-09-30), and the 2026-10-21 go covered its findings in the review index.
- **Do not rename the section-access aliases `MessageTrace` and `MessageTraceSearch`.** Each is a
  storage key in a fail-closed store, so a rename denies the groups granted against it. Operator
  wording changes through `ModulePermission.DisplayName`.
- **Emergency Disable's lockdown checkbox is a temporary escape hatch** until the stakeholder says
  what they want; then it goes away or becomes the settled default. The default lives in the one
  `LockdownDefaultArmed` constant (`.agents/decisions.md` 2026-10-05 and 2026-10-06;
  `docs/EmergencyDisableLockdownOU-Plan.md`).

### Engineering rules with no other home

- Item 24 page conversions take `[Inject] private IActivityProgress Progress` inside `@code`,
  never an `@inject` directive, which sits above every registered control and shifts all of them.
- Remove a module's spinner only after its operation reports to the status bar.
- Per-row indicators and the pre-authorization latch stay (a reading the owner has not ruled on).
- Never report page-load state from the browser: page loads are server-side
  (`Services/PageLoadTracker.cs`, `Middleware/PageLoadMiddleware.cs`), and
  `wwwroot/nav-progress.js` must not come back (`Components/App.razor` says why).
- A live defect parked in a registry entry or a plan note also gets a line in this file; queue
  item 23 shipped because only the registry recorded it.
- Never run two agents in one checkout; each gets its own worktree (2026-09-25 incident).

### Live residue of archived streams

- **Defender for Endpoint** (`docs/DefenderEndpointDevices-Plan.md`, S1-S7 implemented; the twenty
  findings of `docs/Comms10kDefenderRemediation-Plan.md` closed). No live result is recorded for
  its three environmental unknowns, each of which fails visibly: the forest read behind Site, the
  `SeenBy()` observer column, and whether an AD site is a physical locator at all (owner: it is
  not). The owner's 26,812-row export found Site the only location signal present for
  essentially every device; measure any proposed locator against that table (archived).
- **Circuit lifetime leak** (`docs/CircuitLifetimeLeak-Plan.md`, Implemented). The fix was
  codex-reviewed (`leak-1`, 2026-10-07; its recurrence guard repaired 2026-10-08). Unproven is
  that the leak is gone: that needs a worker with about 24 hours of uptime and one elevated shell
  (`.agents/machines.md`). `docs/ProductionMemory-Plan.md` is a Draft awaiting an owner go.
- **Comms-10k** (`docs/Comms10kBulkResolveScale-Plan.md` implemented; the stakeholder's test failed
  2026-10-01). Point 3, missing addresses, is settled by the exact-match ruling and the
  rejected-address CSV (`.agents/decisions.md` 2026-10-12 ruling 1; `a01767c`). Point 2, progress
  visibility, is queue item 24. Point 1, 1.5 hours against 15-20 minutes, needs its baseline
  first: "the previous version" may not be this app, and the module's own write measured 7.9s
  for 10,001 members. Its removed guard, removed locking and protected-principal exemption are
  owner rulings with tripwires (`.agents/decisions.md` 2026-09-30); do not reintroduce or
  re-litigate them.
- **Queue item 24, global progress** (functionally complete). Four typeahead spinners survive in
  `Components/Shared` (`ADGroupAutocomplete`, `ADIdentityAutocomplete`, `RecipientAutocomplete`,
  `TicketNumberInput`); raised with the owner 2026-10-02, unanswered. Only Message Trace's bulk
  detail download honours the progress cancellation token (`Components/Pages/MessageTrace.razor`
  `:1189`). Both re-verified 2026-10-23 as of `d881ea58`.
- **True Last Logon** (`docs/TrueLastLogon-Plan.md`, built and reviewed; its owner rulings live
  there). The cloud half has no recorded run against a real tenant; it needs `AuditLog.Read.All`
  consented behind its secret, and the module ships disabled. The live comparison is production
  use (`.agents/decisions.md` 2026-10-12).

## Next

- **After the hand-over** (the order between these groups is the owner's): the held LOW backlog
  (`## Now`); the new module the owner has waiting (`.agents/decisions.md` 2026-10-23); and three
  plans approved 2026-10-12 and not started, in his stated order (`.agents/decisions.md`
  2026-10-12 - scope is not a gate, so do not ask him to re-approve them):
  1. Click-gating tiers 2-4, the remaining 19 pages: `docs/ClickGatingAudit-Plan.md`; read its
     Revision 1 before touching a page.
  2. The verbosity sweep at full scope, all 39 files, S0-S10: `docs/VerbositySweep-Plan.md`. The
     copy is not free - 20-plus test files pin operator-facing wording, and every page is
     line-keyed by `ClickGateRegistry` and `ProgressRegistry`.
  3. A rewrite, not an amendment, of the Service Health public-status plan from the owner's ask
     (`.agents/decisions.md` 2026-10-12 ruling 4); `docs/ServiceHealthPublicStatus-Plan.md` is
     REJECTED.
- **Defender S8** (docs, version and the `DeviceNetworkEvents` fallback) is not started. Subnet,
  gateway, DHCP server and MAC vendor need a `DeviceNetworkInfo` join whose column names are
  unverified; `column_ifexists` in a request of its own is the safe way in.
- **Former "Known defects recorded only in this file":** promoted to `.agents/review/index.md` as `kd-1`..`kd-9`
  on 2026-10-23 evening (the DC "required audit event could not be generated" refusal is not a code defect and
  stays the owner's to chase; it is not a row).
- **Implementation codereviews recorded as never dispatched:** `docs/ServiceHealth-Plan.md`,
  `docs/GroupBulkActions-Plan.md` and `docs/AlphabeticalModuleOrdering-Plan.md`.
- **Coverage:** re-measure on green CI and ratchet per `.agents/review/coverage-floor.txt`;
  `PermissionValidator` still needs its own approved plan for the credential-carrying I/O seam.
- **Module development platform** (`docs/ModuleDevelopmentPlatform-Plan.md`, Draft revision 2):
  awaits approval of the full cutover, including its structural-only Migration exception.
- **Parked or deferred by the owner, no resumption authorised:** module packaging and import
  (`.agents/decisions.md` 2026-07-22); Account Lockout Remediation and its findings `aud-a5b` and
  `aud-a5c` (2026-10-19; its disabled state is in `.agents/machines.md`).
- **The owner's issue queue** is at the path in `.agents/machines.md`; it is his file and is never
  written to. Its open rows as this file last read them (2026-10-14; the archived queue table has
  each reading): 2 (the Service Health rewrite above), 4 (`## Blockers`), 5
  (`docs/MessageTraceMultiTenant-Plan.md`, feasibility only - his decision whether to proceed), 6
  (containerize, on hold; `## Blockers`), 8 (Defender, above), 9 and 25 (above), 10 and 11 (Cloud
  Password Reset, `## Blockers`), 17 (True Last Logon), 20 (Comms-10k) and 24 (item 24).

## Blockers

- **Cloud Password Reset (queue 10 and 11) is blocked on the owner**, and nothing works until he
  creates a dedicated Entra app registration (`User.Read.All`, `User-PasswordProfile.ReadWrite.All`
  and `RoleManagement.Read.Directory`, admin-consented; never another module's registration) and
  its Delinea record (`Tenant ID`, `Application ID`, `Client Secret`, no checkout workflow), enters
  that id as the module's `GraphDelineaSecretId`, then deploys, enables the module (it ships
  disabled) and grants section access. `docs/CloudPasswordReset-Plan.md` owns the checklist; the
  cases that matter most are two people sharing an employee ID and the cleared force-change case.
  Residuals: the survey exports' disposition (none is in the workspace, re-checked 2026-10-23),
  and survey registration `16866221-3e2b-4ea5-8aae-157b043b6d7c`, recorded as holding unnecessary
  `Directory.ReadWrite.All`, `User.ReadWrite.All`, `Group.ReadWrite.All` and
  `UserAuthenticationMethod.ReadWrite.All` grants; revocation needs separate authority.
- **Queue 6, containerize: the SPN and gMSA work is the owner's AD team's.** A container runs
  Kestrel, so Windows Authentication needs a gMSA, a credential spec and SPNs in the forest
  (`deploy.ps1:444` removes the IIS Negotiate provider today to avoid that). Where "elsewhere" is
  remains unanswered and decides whether the AD, on-prem Exchange and DHCP work can run at all
  (`.agents/decisions.md` 2026-09-21).
- **Unsettled, the owner's to reconcile** (no rule was changed):
  - `.agents/decisions.md` 2026-10-01, no deployments until item 24 is done, was never restated or
    lifted, though both instances were redeployed during it and the 2026-10-23 rule hands over a
    deployable master.
  - The 2026-07-31 protected-principal input-validation rationale relies on this deployment's read
    visibility, while the 2026-09-11 rule (Architectural Invariant 7) rejects environmental safety
    arguments.
  - The 2026-10-12 manual-acceptance ruling against records that mark items "dev acceptance owed";
    `aud-a26l` says its check blocks the attribute editor's deploy.
  - `mde-9` changed a duplicate-row behaviour that `mde-3` recorded as deliberate. It serves his
    every-rejection-reported ruling, but he was never asked.
  - Queue 4 is owner-marked DONE, yet `MessageTraceSearch` is still inert: declared at
    `Modules/ModuleCatalog.cs:500` and consulted by no page, handler or policy, while
    `docs/MessageTracePermissionSplit-Plan.md` reads APPROVED and IN PROGRESS with slices 2-4
    unstarted. Re-verified 2026-10-23 as of `d881ea58`.
  - `ExchangeAdminWeb.Tests/ClickGateRegistry.cs:75` stale tiers 2-4 wording: now index row `kd-9`.
- **SharedConfigDb record flags:** the decision says SQLite `data_version` / next read; the
  implementation uses the store's change token with a throttle. The additive-only rule has
  operative homes in the decision, guidance and test but not in the Constitution.
  `docs/SharedConfigDb-Plan.md` section 9; no ruling inferred.
- **Doc and plan status drift, re-verified 2026-10-23 as of `d881ea58`** (docs, outside a drift
  sweep's reach; do not silently relabel; `docs/RepoAudit-Remediation-Plan.md` was relabelled
  IMPLEMENTED 2026-10-23 by the stream and is no longer in drift):
  `docs/BlockedSendersLoadTiming-Plan.md` and `docs/Comms10kReplaceUx-Plan.md` read Approved and
  `docs/ConferenceRooms-OnPremRoomListAdd-Plan.md` Approved / In progress, with completion
  unverified; `docs/AdminUIRedesign-Plan.md` reads In progress;
  `docs/MessageTracePermissionSplit-Plan.md` is above. `docs/AdminModuleSpec.md` and
  `docs/AdminModuleDeveloperGuide.md` both carry Version 2.0, last verified 2026-06-25, against
  the current base version.
- **Unscheduled M365 protection gap:** `Services/M365GroupManagementService.cs` calls
  `CheckProtectedAsync` only from the member and owner paths (`:255`, `:329`); `UpdateGroupAsync`
  (`:125`) and `DeleteGroupAsync` (`:143`) call neither, and protection configuration cannot
  identify a cloud-only group. Re-verified 2026-10-23 as of `d881ea58`. The owner excluded this
  module from the on-prem target work; no work approved.
- **Older questions:** `docs/MessageTraceNullRow-Plan.md` needs a live rerun (OQ-1, the upstream
  null-row cause undiagnosed); `docs/ProtectedPrincipalResolution-Plan.md` keeps OQ-2, whether the
  reported cloud-only mailbox targets should stay reachable.
- **Ops gaps:** ConferenceRooms' AD secret on prod was outstanding and has not been checked since;
  `deploy.ps1` still has no native `-PlanOnly` (Architectural Invariant 4), and the pipeline's dry
  run is the recorded workaround.

## Verification

Commands and mandatory guards are owned by `.agents/repo-guidance.md` and `AGENTS.md`: the
Release build, `dotnet test ExchangeAdminWeb.slnx`, `dotnet format ExchangeAdminWeb.slnx
--verify-no-changes --no-restore`, `git diff --check HEAD` and `tools/Test-AsciiOnly.ps1`; for
PowerShell, `Invoke-ScriptAnalyzer` and `Invoke-Pester tests/ps`. Host specifics (the xUnit thread
cap, the pinned Pester and PSScriptAnalyzer versions) are in `.agents/machines.md`.

Last results, each with its commit. The 2026-10-23 drift sweep changed records only and re-ran
none of them.

- Full suite as of `cfccec59`: 6693 passed / 0 failed / 0 skipped, as the supervising session
  reported it; Release build 0 errors / 19 warnings; format, diff-check and ASCII lint clean. The
  three former live-directory skips are stand-in passes since `psl-8`. The 19 warnings are down
  from 61 before `cdt-41`; the NuGet advisories are gone and NuGet audit is now a build error
  (`cdt-41`).
- Pester as of `7698c096`, `cdt-41`'s branch tip: 478 passed / 0 failed / 0 skipped. No `.ps1`,
  `.psm1` or `.psd1` file has changed since, as of `d881ea58`.
- PSScriptAnalyzer: 0 errors on each changed script in the ops-script slices; their records own
  the runs.

Not exercised by any gate: browser rendering (there is no bUnit harness; `.agents/decisions.md`
2026-10-01, "Rendered is not visible"), live writes, and the live forest and tenant. Both deployed
instances predate the stream; `.agents/machines.md` owns that receipt, re-measured 2026-10-23.
CI status is not recorded here, and per-finding status is `.agents/review/index.md`'s.

Method traps worth keeping in view (their derivations are archived):

- Restore a mutated file byte-exact, md5-checked, with a fresh mtime; a restore that keeps the old
  mtime lets MSBuild skip the rebuild, and the test then runs the mutated binary.
- Confirm a mutation applied before trusting its result: one that did not apply proves nothing,
  and one that passes may be an equivalent implementation rather than a weak guard.
- Grep build output for `error CS`; do not tail it.
- One unidentified single failure on 2026-10-19 (its name was lost to a `tail`) was never
  reproduced; treat it as unexplained, not absent.

## Active sources

- `AGENTS.md`, `.agents/repo-guidance.md`, and `docs/ProjectConstitution.md` govern work.
- `.agents/decisions.md` owns decisions; `.agents/machines.md` owns host facts.
- `.agents/review/index.md` and `.agents/review/findings/` own finding status and detail;
  `.agents/token-log.md` owns per-slice arrangement and cost.
- Referenced plans own scope, status and acceptance checklists.
- `Modules/ModuleCatalog.cs` owns module versions and enumeration;
  `ExchangeAdminWeb.csproj` owns the current base app version.
