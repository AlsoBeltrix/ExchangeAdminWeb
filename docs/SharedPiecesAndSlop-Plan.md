# One shared piece per shared behaviour; fix what the audit found; stop the slop at its sources

Status: Approved 2026-11-10 (owner: "approved"); all phases 0 to 5 authorised 2026-11-10 (owner: "go for all phases. once all are done, I'll do a dev deployment."). No dev deploy between slices or phases. In progress: phase 0; slices 0.1 (adfb2a89), 0.2 (8b0fbb28), 0.3 (5896bae9) merged.
Owner: Michael
Last verified against code: `7aa3dd19` / 2026-11-10
Evidence: `.agents/review/slop-audit-report-2026-11-10.md` (combined report, two Gemini review rounds),
`.agents/review/shared-and-unwired-audit-2026-11-10.md` (rows A1-A46, patterns B1-B17),
`.agents/review/llm-slop-audit-2026-11-10.md` (categories 1-13).
Decisions it implements: `.agents/decisions.md` 2026-11-10 (shared piece, not a copy per module; unwired code is
wired in or removed), 2026-11-10 (ServiceNow possible later, required by nothing now), 2026-11-09 (ticket sanitized),
2026-11-09 (in-app help short).
Authority: subordinate to `docs/ProjectConstitution.md` and `AGENTS.md`. Standing mandate applies throughout: find and
fix bugs; do not remove or significantly change intended functionality.

## Exec summary

The audit found that the app's safety checks are sound where they exist, but that the same job is done by hand in
many places, and the copies have drifted. That drift is where the bugs are. Thirteen defects were confirmed by two
models reading the code. One is serious: for a user whose account lives in another domain of the forest and who has
no mailbox, four modules (MFA Reset, Risky Users, Intune Devices, M365 Groups) do not apply the protected-principal
rules. It is narrow, and nothing suggests it has been hit, but its only protection today is the shape of this forest.

This plan does five things, in this order:

1. **Fix the thirteen defects**, the serious one first. About two weeks of agent work.
2. **Delete the 46 pieces of code nothing uses**, including the ticket component written in May.
3. **Remove the line-number test pin** that has been forcing production code onto single lines over a thousand
   characters long, so that later page changes stop costing a re-anchoring pass each.
4. **Build one shared piece for each behaviour several modules need** - the ticket box, the refusal audit row, the
   admin mail, the job authorisation check, the protection check for cloud users, and ten smaller ones - and move every
   module onto it, deleting the copies. The ticket piece has one place where ServiceNow validation can be switched on
   later; nothing requires it now.
5. **Stop the slop at its sources**: move the work out of the pages into services so tests run behaviour instead of
   matching source text; delete the version-history comments; archive finished plans and closed findings; change the
   rules that produced the comment and paperwork sprawl.

What you get: one place to change each behaviour, modules that cannot drift from each other, tests that test, and a
repo an agent can read without loading twelve megabytes of history.

What it costs: the largest piece of work this repo has had. Roughly 40 to 55 slices, each one agent, each gated and
merged before the next starts. Every slice touches working code, so every slice can break something; the answer to
that is small slices, behaviour unchanged by rule, and a dev deploy with a short checklist at the end of each phase.

Biggest risk: step 5 moves the logic that authorises, protects, audits and mails out of 23 pages. Done carelessly it
is exactly the kind of rewrite that drops a check. The plan forbids changing behaviour while moving it, and requires
the old page tests to keep passing until the new behaviour tests replace them one for one.

Approving this authorises phases 0 to 2 in full. Phases 3 to 5 each start only after you have seen the phase before
on dev and said go. You can stop after any phase and keep everything landed so far.

## 1. Rules of the work (what keeps this from being slop)

These bind every slice. A slice that breaks one is not merged.

1. **Behaviour does not change** except where a slice is fixing a listed defect, and then only that defect. A message
   an administrator sees, an audit row's fields, who gets mail: unchanged unless the ledger row says otherwise. Where
   copies differ today, the slice lists the differences and the plan section or an owner ruling says which one wins;
   an agent never picks.
2. **Nothing new is left unwired.** A shared piece lands in the same slice as its first two callers at least, and the
   phase is not done until every caller in the ledger is on it and the copies are deleted. A helper with one caller is
   not written.
3. **Delete more than you add** in consolidation slices. The hand-back states lines added and removed in app code.
4. **Tests execute behaviour.** A new test that reads a source file as text is allowed only as a tripwire for a rule
   that cannot be executed, and says so in one line. Moving logic to a service comes with behaviour tests of that
   service; the source-scan tests it replaces are deleted in the same slice.
5. **Comments explain the code to a reader who has never seen `.agents/`.** No finding IDs, no history, no "used to",
   in any line a slice adds or edits. A slice strips such comments from the methods it touches and nowhere else.
6. **No abstraction ahead of need.** No interface without a second implementation or a test double that needs it; no
   option nobody sets; no base class.
7. **One finding or one consolidation step per commit; one agent at a time; one compile at a time.** Gates as in
   `.agents/repo-guidance.md`; new tests proven to bite.
8. **Help and README move with the behaviour** (Known Failure Class 5), within the length limits.
9. **Review.** The owner names the reviewer per phase. Today only Gemini through agy is available and the owner starts
   it. The orchestrator triages every reviewer claim against the code, records the ruling, and resubmits until no
   dispute remains.
10. **The ledger is the truth.** Section 8 lists every audit item with a status. An item is closed only with a commit.
    Anything deliberately not done is marked "declined" with the reason, by the owner.

## 2. Phase 0 - the thirteen defects

One finding record each under `.agents/review/findings/`, one fix commit each, guard proof each. Order is by severity,
then by shared root cause.

| Slice | Fixes | What changes |
|---|---|---|
| 0.1 | G1b HIGH, G2 MEDIUM, report 2 bug 8, pattern B3 | One service method for "is this cloud-addressed user protected". It takes the Graph object the write will touch, reads that object's on-premises SID from Graph (the existing SID source), binds the account by SID at the global catalog, and checks it. No SID reported by Graph for a synced account, or Graph not answering, refuses. A cloud-only account is checked as today. MFA Reset, Risky Users, Intune Devices and M365 Groups call it; their four hand-written blocks go. M365 Groups: blank identity refuses; "not found" no longer proceeds unchecked; remove checks the object id it deletes. Cloud Password Reset moves to the same method for consistency (it is not exposed today). |
| 0.2 | G1a MEDIUM | `ResolveWithExchangeFallbackAsync`: a synced recipient is bound by SID whether it was typed by primary address or alias, as `ResolveExchangeTargetAsync` already does. |
| 0.3 | #1 MEDIUM, pattern B4 | The job runner checks module enablement and the saved authorisation snapshot before every row for every processor; Message Trace detail and Migration report export save a snapshot when they queue. The two processors that check for themselves keep working and lose their private copies. |
| 0.4 | #3 MEDIUM | The three AD `-Filter` strings become `-LDAPFilter` with the existing escaper. |
| 0.5 | #2 MEDIUM | On-prem message trace: a failed or empty transport-server lookup is logged and shown as a warning on the result ("searched one server only"). The search still runs. |
| 0.6 | G3 LOW | Account Lockout: ticket and logoff-policy checks run before directory work on an execute request; `Executed` means a logoff was attempted. OWNER QUESTION 1 (section 7) decides whether a refused execute mails. |
| 0.7 | #4, #5, #6, #7 (closed by 0.1), #8, #9 LOW | Legend read logs and reports its failure; the four boxes bind on input (interim - phase 3 replaces the ticket boxes); Migration and Out of Office mail uses the dictionary-shaped notification with honest row labels; neutral placeholder; `-HybridEndpoint` removed. One commit each. |

End of phase: dev deploy, checklist of the user-visible changes (0.5 warning, 0.6 mail, 0.7 boxes and mail), Gemini
review of the phase range.

## 3. Phase 1 - remove what nothing uses

Rows A1-A46 of the unwired audit, all "remove". Grouped into about six slices by kind (components and policy; types;
members nothing calls; members only tests call, with their tests; unused injected dependencies; config keys, script
parameters, descriptor fields, route alias, orphan doc). Each slice re-runs the audit's grep for every row before
deleting, because the code has moved since, and declines any row that now has a caller. The five deliberate
test-seeding seams (one audit row) stay and are marked declined in the ledger. The eight legacy appsettings fallback
keys are removed only if no deployed `appsettings.json` on dev or prod still carries them: the slice lists the keys and
the owner checks both files first (OWNER ACTION, section 7).

No behaviour changes. No version bump unless a removed config key was documented, in which case base patch.

## 4. Phase 2 - retire the line-number pin

`ClickGateRegistry` keys 216 controls by page line number. It exists to prove every button that starts a write is
guarded against a double click. The guarantee stays; the key changes.

- 2.1 Re-key every entry by page plus a stable identity read from the markup (the handler name, plus the element's
  label or an explicit `data-gate` attribute where one handler serves several controls). `ProgressRegistry` is already
  keyed by method name and is the model. The scan fails on a write control with no entry and on an entry with no
  control, exactly as now.
- 2.2 Un-squash the production lines written to keep line numbers still (the audit lists them; search for the
  "so no line-keyed entry moves" comments and lines over 300 characters in pages). Formatting only.
- 2.3 Delete `.agents/playbooks/clickgate-reanchor.md` and the guidance sentence that points to it.

Guard proof for 2.1: remove a gate from three different controls and watch the scan fail for each; add an unregistered
write button and watch it fail.

## 5. Phase 3 - one shared piece per shared behaviour

Order is the audit's risk order. Each row is one to four slices. "Callers" counts are from the audit and are
re-counted by the slice.

| # | Shared piece | Replaces | Notes |
|---|---|---|---|
| 3.1 | **Ticket**: one component (the box, bound on input, required or optional by parameter) and one gate method in a service, `Check(typed)` returning the sanitized ticket or the refusal | 23 hand-written boxes, 43 enable predicates, 51 sanitize calls, five check shapes, six refusal wordings | The gate method is the single place a ticket passes before any write. It holds the presence rule today and one extension point where a validator can be registered later; with none registered it does nothing more. The existing ServiceNow call moves behind that point and stays switched as it is now. The unused May component is deleted in phase 1, not revived. Refusal wording: "Ticket number is required" everywhere, except where a module's own wording is listed in the slice and the owner keeps it. |
| 3.2 | **Refusal audit row**: the existing `RefusalExtra` builder, widened to cover every refusal | 18 hand-built dictionaries, mixed shapes on one page | Depends on OWNER QUESTION 2 (state item 44, the audit convention). Until ruled, the slice only replaces hand-built copies that already produce the builder's exact fields. |
| 3.3 | **Cloud-user protection check** | done in 0.1 | Ledger only. |
| 3.4 | **Job authorisation at run time** | done in 0.3 | Ledger only. |
| 3.5 | **Admin notification**: the dictionary-shaped call only | the mailbox-shaped overload and its remaining callers | After 0.7 only the permission modules use the old shape; they move and the overload is deleted. |
| 3.6 | **Operation trace scope**: opened by the shared operation runner of phase 4 | six modules open one by hand, the rest rely on the audit write | Folded into phase 4 for modules that move; no separate slice unless phase 4 is not approved. |
| 3.7 | Graph client and secret setup | 10 copies, two failure behaviours | The shared factory refuses a missing secret one way. |
| 3.8 | PowerShell credential building | 14 copies | One builder, with BitLocker's handling of a blank domain. |
| 3.9 | AD module import and runspace creation | partly adopted helper; 12 factories | Finish adoption. The 12 factories are consolidated only where they are identical. |
| 3.10 | Bulk CSV limits and parsing | partly adopted `BulkCsvRowLimit` | Finish adoption. |
| 3.11 | Module config defaults | written twice per module | One source. |
| 3.12 | Time-zone handling | several copies | One helper. |
| 3.13 | Graph user lookup and the OData literal escaper | several lookups; three escaping shapes | One escaper, value-only form. Before it lands, one live check on dev that group search and the risky-user filter return the same rows (this is G4's check). |
| 3.14 | LDAP filter escaper | 4 identical copies | One. |
| 3.15 | Confirm bars, result banners, identity entry, verify metadata | UI copies | Last. Components only where three or more pages share the markup. |

End of phase: dev deploy; checklist is one ticket-required write and one refusal in each of five modules of the
owner's choosing, plus one bulk job.

## 6. Phase 4 and 5 - the sources of the slop

**Phase 4 - the work moves out of the pages.** Handlers of 250 to 360 lines in `.razor` files authorise, check
protection, audit, trace and mail. Nothing can execute a page, which is why about 850 tests match source text.

- 4.1 One operation runner in a service: given the acting user, the module, the ticket and the target, it runs the
  access check, the ticket gate (3.1), the protection check, the write, the audit row (3.2), the trace scope and the
  admin mail (3.5), in one fixed order, and returns one result the page displays. The three protected-principal
  exemptions are parameters of the module's registration, not branches a page writes.
- 4.2 Modules move one at a time, smallest first to prove the shape (Out of Office, Blocked Senders), then the rest,
  largest last (Migration). Per module: the handler becomes a call; behaviour tests of the service replace that
  module's source-scan tests one for one; the page keeps only what it displays.
- A module is not moved if its flow does not fit the runner without bending it; it is listed in the ledger with the
  reason and stays as it is. The runner does not grow options to fit one module.

**Phase 5 - comments, catalog, paperwork, rules.**

- 5.1 `ModuleCatalog.cs`: delete the per-version history comments (about a thousand lines; git has the history).
- 5.2 Fix the nine stale `file:line` comments and the false comment in Risky Users; elsewhere comments are cleaned only
  as rule 5 reaches them. No mass rewrite.
- 5.3 Move the 79 implemented plans to `docs/history/`; move closed finding files to `.agents/review/archive/` with
  the index rows; cut `.agents/state.md` to current work only (the `drift` playbook).
- 5.4 Re-verify `docs/AdminModuleSpec.md` and `docs/AdminModuleDeveloperGuide.md` against the code as it stands after
  phase 4, including "a new module uses the shared pieces; a copy is a defect".
- 5.5 Guidance changes so the sprawl does not return, each an owner decision recorded in `.agents/decisions.md`:
  comments rule (rule 5 above, permanent); module version comments are not kept; a closed finding's record is its
  commit message plus one index row, not a file, unless it is HIGH or CRITICAL.
- 5.6 A tripwire test per shared piece: a page that declares its own ticket string, builds its own refusal dictionary,
  or calls the removed notification shape fails the build. These are the legitimate source scans.

## 7. Owner decisions and actions

Asked one at a time, each when its slice is next.

1. (0.6) When Account Lockout refuses an execute request before anything is attempted - missing ticket, not allowed to
   log users off - should the administrators still get a mail? Recommendation: mail for "not allowed", none for a
   missing ticket.
2. (3.2) State item 44, the audit convention for refusals and results. The audit's pattern B2 lists what the copies do
   today.
3. (Phase 1) Check dev and prod `appsettings.json` for the eight legacy fallback keys before they are removed.
4. (5.5) The three guidance changes.
5. (Each phase) The reviewer for the phase, and go for the next phase.

## 8. Ledger

Kept in this section as slices land: one row per audit item (13 defects; A1-A46; B1-B17; slop categories 1-13) with
status `open`, `done <commit>`, or `declined <reason>`. Names are one line each; the evidence files hold the evidence.

| Item | Name | Status |
|---|---|---|
| #1 | Message Trace detail and Migration report export jobs run with no enablement or authorization check | done, merged 5896bae9 |
| #2 | On-prem message trace narrows to one server silently when the transport-server lookup fails | open |
| #3 | Three AD `-Filter` strings built by quote-doubling | open |
| #4 | Attribute legend read returns empty on any failure with no log | open |
| #5 | Four ticket boxes (and the Migration email box) bind on change | open |
| #6 | Migration and Out of Office admin mail uses the mailbox-permission template | open |
| #7 | MFA Reset passes a null Entra object id to the protection check (bug 8) | done, merged adfb2a89 |
| #8 | Environment address as a placeholder in MFA Reset | open |
| #9 | `deploy.ps1 -HybridEndpoint` accepted and ignored | open |
| G1a | Synced out-of-domain user typed by its primary address is refused (fallback resolve binds by SID only for an alias) | done, merged 8b0fbb28 |
| G1b | Synced out-of-domain user with no Exchange recipient: protection rules not applied by the cloud-user modules | done, merged adfb2a89 |
| G2 | M365 Groups gate: blank identity and NotFound proceed unchecked; remove checks a string and deletes an id | done, merged adfb2a89 |
| G3 | Account Lockout: cheap refusals after directory work; `Executed` means "asked to execute" | open |
| A1 | `TicketNumberInput.razor` | open |
| A2 | `AuthorizationCheck.razor` | open |
| A3 | `"GroupPolicy"` authorization policy | open |
| A4 | `PermissionType`, `PermissionAction` enums | open |
| A5 | `RoomTypeDefinition`, `CalendarPermissionEntry` | open |
| A6 | `IExoRecipientCommands`, `PooledExoCommands` | open |
| A7 | `NamedLocationsService.GetByIdAsync` | open |
| A8 | `BulkJobProcessorRegistry.IsRegistered` | open |
| A9 | `BulkJobService.IsStalled` | open |
| A10 | `AuthorizationGroupInventory.GetConsultedSids` | open |
| A11 | `BulkJobService.GetRecentJobsBySubmitter` | open |
| A12 | `ProtectedPrincipalService.MatchesDnToProtectedGroup` | open |
| A13 | `ProtectedPrincipalService.ResolveDirectoryPrincipalAsync` | open |
| A14 | `ADDirectorySearchService.SearchUsers` | open |
| A15 | `MigrationReportStore.DeleteAll` | open |
| A16 | `ModuleAdminService.GetUnusableGrants` | open |
| A17 | `ModuleAdminService.IsModuleAdminForAny` | open |
| A18 | `AdminPageDirtyState.ClearSection` | open |
| A19 | `ModuleAdminRepository.GetForModule` | open |
| A20 | `SectionAccessService.GetSectionAccess` | open |
| A21 | `LicensingUpdatesService.ParseCsv` | open |
| A22 | `AttributeEditorRepository.TryReadAllowlist` | open |
| A23 | `ADAttributeEditorService.PreviewAsync` | open |
| A24 | `AuditFirstMerge.Merge` | open |
| A25 | `PermissionValidator.IsUserExcludedAsync` | open |
| A26 | `ADAttributeEditorService.IsAllowlistCorrupt` | open |
| A27 | `AdSiteSubnetMap.FindSite` | open |
| A28 | `IActivityHandle.AsProgress` | open |
| A29 | Test-seeding seams (five) | open (plan section 3: to be declined by the owner) |
| A30 | `Comms10kService._config` | open |
| A31 | `EmergencyDisableService._env` | open |
| A32 | `GroupManagementService._moduleConfig` | open |
| A33 | `M365GroupManagementService._audit`, `_operationTrace` | open |
| A34 | `ProtectedPrincipalService._env` | open |
| A35 | `RiskyUsersService._logger` | open |
| A36 | `ServiceHealthService._logger` | open |
| A37 | `ADAttributeEditor.razor` unused `OperationTrace` inject | open |
| A38 | `AdminEventLog.razor` unused `Config` inject | open |
| A39 | `Home.razor` unused `AuthorizationService`, `Navigation` injects | open |
| A40 | Legacy appsettings fallback keys (eight) | open |
| A41 | `deploy.ps1 -HybridEndpoint` | open |
| A42 | Unread PowerShell function parameters | open |
| A43 | `ModulePermission.Name` | open |
| A44 | `ConfigFieldType.AdUser` | open |
| A45 | Route `/message-trace` | open |
| A46 | `tools/test-plan-bulk-csv.md` | open |
| B1 | Ticket box and its gate | open |
| B2 | Audit rows: refusal shape and result values | open |
| B3 | Protected-principal check for a cloud (Graph) user | done, merged adfb2a89 |
| B4 | Authorization of a queued job when it runs | done, merged 5896bae9 |
| B5 | Admin and user notifications | open |
| B6 | Operation trace scopes | open |
| B7 | Graph client and secret setup | open |
| B8 | PSCredential from a vault secret | open |
| B9 | Importing the ActiveDirectory module | open |
| B10 | Bulk upload limits, CSV reading, results | open |
| B11 | Module config defaults written twice | open |
| B12 | Typed date and time and its zone | open |
| B13 | Looking up a Graph user by UPN | open |
| B14 | Confirm before write | open |
| B15 | Result banners, errors and "No change" | open |
| B16 | Identity entry | open |
| B17 | Verify metadata held beside the catalog | open |
| S1 | History comments | open |
| S2 | Defensive code | open |
| S3 | Over-engineering | open |
| S4 | Size and sprawl (logic in pages) | open |
| S5 | Sibling inconsistency | open |
| S6 | Correctness smells | open |
| S7 | Hallucination residue | open |
| S8 | Tests that cannot fail (source scans) | open |
| S9 | Implementation pins (ClickGate line keys) | open |
| S10 | PowerShell | open |
| S11 | Docs and process | open |
| S12 | User-facing text | open |
| S13 | Version noise | open |

## 9. Versions

Base app version bumps once per slice that changes shared behaviour (0.1-0.3, 3.x, 4.1); module patch bumps for each
module whose behaviour or page changes; pure deletions and formatting bump nothing. Stated per slice in its hand-back.

## 10. Not in this plan

Merging the 79 result types; rewriting the runspace factories beyond identical ones; mass-stripping finding IDs from
comments; the picker's fail-soft search; anything the two reports call sound. Both models advised against them as
churn without gain.
