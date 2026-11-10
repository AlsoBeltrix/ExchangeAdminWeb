# Repo audit for AI slop and LLM code defects - combined report, 2026-11-10

Sources: `llm-slop-audit-2026-11-10.md` (Fable 5.2 agent), `shared-and-unwired-audit-2026-11-10.md` (Opus 5.5 agent),
both read-only at master 4a5786d2; reviewed by Gemini 4 Argon (High) through agy, run by the owner at 231edef3
(`audit-review-gemini.result.txt`, gitignored). Codex did NOT review: the corporate gateway is shut down (two
Cloudflare 403 failures, then owner confirmation). So every ruling below has one outside reviewer, not two.
Nothing was built or run by any of the three; all findings are from reading. No code has changed.

## Verdict

The inventory holds: Gemini re-counted every headline number and confirmed all five verdict points (one understated).
The auditors' "no new confirmed behavioural defect" does not hold: Gemini confirmed nine defects from the two reports
and claims four more the auditors missed, two of them HIGH. Those four are one model's reading and are NOT yet
verified by anyone else. UPDATE (round 1 triage): three of the four are admitted, one declined - see the triage section.

## Confirmed by Gemini (auditor finding, reviewer agrees)

| # | Sev | What | Where |
|---|---|---|---|
| 1 | MEDIUM | Message Trace detail and Migration report export jobs run with no enablement or authorization check; enqueuers save no snapshot. A queued job keeps running and mails results after the module is disabled or access removed | `Services/Jobs/MessageTraceDetailJobProcessor.cs:76`, `MigrationReportExportProcessor.cs:56`, `MessageTrace.razor:1333`, `Migration.razor:2958` |
| 2 | MEDIUM | On-prem message trace silently narrows to one server when the transport-server lookup fails or is empty; no warning, no log | `Services/MessageTraceService.cs:570-572, 675, 686-691` |
| 3 | MEDIUM | Three AD `-Filter` strings built by quote-doubling; `$name` in a group name expands as a PowerShell variable (the repo's own comment at `SectionAccessGroupDirectory.cs:136` says so). Wrong or no group found | `GroupManagementService.cs:135, 1552`, `ConferenceRoomService.cs:2666` |
| 4 | LOW | Attribute legend read returns empty on any failure with no log; choices vanish from the editor | `Services/Storage/AttributeEditorRepository.cs:183, 208` |
| 5 | LOW | Four ticket boxes bind on change, so the gated button stays disabled until blur and the first click is lost | `AdminEventLog.razor:436`, `Migration.razor:63, 237` (and the email box at `:52` gating the same button), `NamedLocations.razor:187` |
| 6 | LOW | Migration and Out of Office admin mail uses the mailbox-permission template ("Permission Change Notification", "Affected User: N/A") | `EmailService.cs:82-137`, `Migration.razor:2006, 3402`, `OutOfOffice.razor:562` |
| 7 | LOW | MFA Reset passes a null Entra object id to the protection check for an unresolved user; a legacy exclusion listed by Entra id is not matched | `MfaReset.razor:442-450` |
| 8 | LOW | `placeholder="user@analog.com"` in shipped UI (Invariant 7) | `MfaReset.razor:53` |
| 9 | LOW | `deploy.ps1 -HybridEndpoint` accepted and ignored | `deploy.ps1:31` |

## Struck by Gemini

- Blank-domain credential (`"\user"`): not a defect. `DelineaService.cs:250` refuses a secret with an empty domain before any builder runs. Whitespace-only domain is the only residue.
- Directory search failure looks like "no match" in pickers: not a defect. Typeahead, logged, gates nothing.
- Licensing `ParseCsv` vs `ParseCsvAsync`: debt, not a live bug. The two are line-for-line identical today.
- "Invented cmdlet `Complete-MigrationUser`": wrong; the cmdlet exists.
- "The three OData escapers behave identically": wrong; see new claim 4.

## Gemini's four new claims - assessed by the orchestrator against the code (round 1 triage)

Each was traced by reading the cited lines and the paths around them at master 769c422e. Nothing was run.

### G1 - protected-principal resolution by address reads one domain: ACCEPTED, split in two

Evidence. `ProtectedPrincipalService.ResolveViaActiveDirectory` (`:1560-1575`) runs `Get-ADUser -LDAPFilter ... -Credential`
with no `-Server`, so it reads the default domain only. The global catalog is used only by the read by SID
(`:1318-1392`, `FindObjectsBySidAsync`, `BindBySidAsync`). `ResolveWithExchangeFallbackAsync` (`:856-960`) reaches the
SID bind only in the alias branch (`:887`).

- **G1a, MEDIUM, wrong refusal (fail-closed).** Target: a synced user whose account is in another domain of the forest,
  typed by its PRIMARY address. AD by address: NotFound. Exchange: synced recipient, same address. Code falls to `:941-945`
  "directories disagree, failing closed" - Unavailable, refused. The same user typed by an alias is bound by SID at the
  global catalog and resolves. So the primary address of an out-of-domain user is refused by every module that uses the
  fallback resolve. Not a bypass. The comment at `:878-884` explains why the branch is on ExistsOnPrem; nothing explains
  why the SID bind is inside the alias test, and `ResolveExchangeTargetAsync` (`:994-1029`) binds by SID in both cases.
- **G1b, HIGH, protection not applied (fail-open by absence).** Target: a synced user in another domain of the forest
  with NO Exchange recipient (no mailbox, not a mail user). AD by address: NotFound. Exchange: no recipient. `:869-875`
  returns NotFound. The cloud-user callers then build an unresolved principal with no SID, DN or sAMAccountName
  (`MfaReset.razor:442-450`, `RiskyUsers.razor:959-969`, `IntuneDevices.razor:1131-1141` - round 2 correction: Cloud Password Reset is NOT affected, `CloudPasswordResetService.cs:401-405` refuses every synced account before any write - and the M365 Groups gate at
  `M365GroupManagementService.cs:226` skips the check entirely when nothing resolved), so no user-SID, group, OU or
  pattern rule can match and the write proceeds. Precondition is real but narrow: more than one domain in the forest AND
  the target outside the domain the lookup defaults to AND no Exchange recipient object. It is HIGH because the only thing
  that makes it safe today is the shape of this forest and of who has mailboxes, which Architectural Invariant 7 forbids
  as a safety argument. The modules that reach this path hold the target's Graph object, and Graph reports its
  on-premises SID; the SID source (`IOnPremisesSidSource`) already exists and is not consulted on this path.

Gemini's wording "aud-a2c address-spoofing gap still open" is NOT accepted: the rev-b4 comment at `:890-896` and the
NotAnswered refusal at `:898-904` close it for the alias branch, and the same-address branch refuses.

### G2 - M365 Groups protection gate: ACCEPTED at MEDIUM, narrower than claimed

Evidence. `M365GroupManagementService.CheckProtectedAsync` `:204-205`: a blank identity returns "proceed". `:226`:
when resolution returns NotFound (`resolved == null`) the gate returns "proceed" with no `CheckAsync` at all, so not even
the legacy exclusions by Entra id or address are consulted - the sibling pages at least check an unresolved principal.
`RemoveDirectoryObjectAsync` `:403-409` checks `identityForCheck` (a UPN or mail string) and deletes `objectId`: the
object checked and the object written are named by different values, the class aud-a2b closed for Exchange writes.
`ResolveDirectoryObjectIdAsync` `:310-311` accepts a typed GUID as the object id after the gate ran on the string.

Narrowing. A member with both UPN and mail empty is a non-user object (device, service principal), which no protected
row can name, so the blank-identity remove is fail-open in form more than in reach. A typed Entra object id is resolved
by Exchange when the user has a recipient object. What remains reachable is G1b's population (no recipient) plus any
caller that passes an empty string. Fail-open on blank is still a Known Failure Class 3 violation regardless of reach.
One fix serves G1b and G2: bind the Graph object the write will touch to its on-premises SID and check that.

### G3 - Account Lockout order of checks and refusal mail: ACCEPTED IN PART at LOW

Evidence. `EmptyLogoffResult(message, execute)` (`:1157`) sets `Executed` from the REQUEST flag, and
`NotifyLogoffAdminAsync` (`:1171`) mails whenever it is true. So an execute request refused for a missing ticket
(`:324-328`), a denied logoff policy (`:333-342`), corrupt config or missing credentials mails the administrators as a
failed execution with zero machines. Discovery, the credential fetch and `Get-ADComputer` (`:151`, `:242`, `:252`) run
before the ticket and logoff-policy checks.

Not accepted: "unauthorized pre-check side effects". Those reads run after the access-policy check (`:227`) and are what
that policy grants; the dry run performs the same reads. The defects are (1) cheap refusals placed after directory work,
and (2) `Executed` meaning "was asked to execute", which makes a refusal read as a failed logoff in the admin mail.
Whether a refused execute should mail at all is an owner question, not a bug. This is the item the rev-j1 agent left
alone on 2026-11-09.

### G4 - whole-filter OData encoding: DECLINED as a defect; kept as debt

Evidence. `M365GroupManagementService.cs:75, :353` and `RiskyUsersService.cs:113` percent-encode the whole filter;
`IntuneDeviceService.cs:672-678` and Defender encode only the value and record that the whole-filter form returned an
empty array on that endpoint. The inconsistency is real (three shapes for one job - already a copied-pattern item).
The predicted failure is not established: percent-encoded parentheses, commas and quotes in a query string are valid
and are decoded by the server, M365 group search and the Risky Users filter are in routine use and return rows, and
the Intune note describes one endpoint. A claim that these calls return nothing needs a failing call to show, and the
live behaviour contradicts it. Listed for one live check (group search by a name with a space and an apostrophe).

### Effect on the verdict

Admitted from the Gemini-only set: G1a (MEDIUM), G1b (HIGH), G2 (MEDIUM), G3 (LOW). Declined: G4. With the nine
confirmed above, the audit stands at 13 admitted defects: 1 high, 5 medium, 7 low. None has a finding record or a fix
yet; that is the plan's first section, pending the round-2 review of this triage.

## Slop (not defects) - all counts re-measured by Gemini

| Area | Measure | Reviewer |
|---|---|---|
| Comments as change log | 25.0% of 115,645 app lines are comments; 2,889-3,023 cite a finding ID only `.agents/` decodes; 9 of 17 file:line citations stale | holds |
| Logic in pages | handlers of 250-362 lines doing authorization, protection, audit, mail; 5 pages over 1,700 lines (Migration 4,474) | holds |
| Tests that scan source text | auditor 781 of 5,461 (14%); Gemini 851 (15.6%) | understated |
| ClickGate line-number pin | 216 entries; production methods squashed onto 458-1,150 character lines to keep line numbers still; one service placement chosen for it | holds; "the pin mechanism is broken" |
| Module catalog | 52% comment; version history wrong for 21 of 31 modules | holds |
| Paperwork | 894 markdown files, 12.8 MB (twice the code); 610 finding files; state.md 76.5 KB against a 10K-token rule; 79 implemented plans (1.95 MB) in `docs/`; module contract docs 2,720 commits stale | holds; "fair, and earned" |
| Unwired code | 46 items, all remove; 12 spot-checked | all hold |
| Copied patterns | 17 (6 high); 6 spot-checked | all hold |

Sound, both agree, do not spend here: fail-closed stores, LDAP escaping, secret handling, async and threading
hygiene, no TODOs or suppressed warnings, interface count, ops-script error model, the 79 result types and 12 runspace
factories (churn for no gain), mass-stripping finding IDs from comments (fix only in files being edited).

## Reviewer's order of work

1. Protected-principal gaps (G1, G2) and one shared cloud-user protection check.
2. Run-time enablement and authorization snapshot in the job runner for every processor.
3. The three `-Filter` strings, the message-trace narrowing, Account Lockout's check order.
4. Re-key ClickGate by control identity; un-squash the one-line methods.
5. Shared ticket, audit-refusal and notification pieces; delete the 46 unwired items; archive implemented plans and
   closed findings; cut state.md.

## Round 2 (Gemini, 2026-11-10, at 1fd96c8e) - triage confirmed

Result file `audit-review-gemini-r2.result.txt` (gitignored). Gemini re-traced each ruling and AGREES with all five:
G1a MEDIUM, G1b HIGH, G2 MEDIUM, G3 LOW, G4 declined as a defect and kept as debt (it could not show a failing call;
`CloudSignInService.cs:237` uses the whole-filter form on `/users` deliberately). It found no path that resolves G1a
(no default `-Server`, no session default parameter, no referral) and confirmed no caller reads the on-premises SID
from Graph on the not-found path. Tally confirmed: 13 admitted defects - 1 high, 5 medium, 7 low. Two corrections,
both checked against the files and applied above: Cloud Password Reset is not in G1b's reach (it refuses synced
accounts first) while Risky Users and Intune Devices are; the Migration email box at `:52` has the same bind-on-change
defect as the ticket box beside it.

## Open

No disputes remain between the orchestrator and the reviewer. No second outside reviewer exists (codex gateway shut
down). Finding records and fixes follow the plan: `docs/SharedPiecesAndSlop-Plan.md`.
