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
verified by anyone else.

## Confirmed by Gemini (auditor finding, reviewer agrees)

| # | Sev | What | Where |
|---|---|---|---|
| 1 | MEDIUM | Message Trace detail and Migration report export jobs run with no enablement or authorization check; enqueuers save no snapshot. A queued job keeps running and mails results after the module is disabled or access removed | `Services/Jobs/MessageTraceDetailJobProcessor.cs:76`, `MigrationReportExportProcessor.cs:56`, `MessageTrace.razor:1333`, `Migration.razor:2958` |
| 2 | MEDIUM | On-prem message trace silently narrows to one server when the transport-server lookup fails or is empty; no warning, no log | `Services/MessageTraceService.cs:570-572, 675, 686-691` |
| 3 | MEDIUM | Three AD `-Filter` strings built by quote-doubling; `$name` in a group name expands as a PowerShell variable (the repo's own comment at `SectionAccessGroupDirectory.cs:136` says so). Wrong or no group found | `GroupManagementService.cs:135, 1552`, `ConferenceRoomService.cs:2666` |
| 4 | LOW | Attribute legend read returns empty on any failure with no log; choices vanish from the editor | `Services/Storage/AttributeEditorRepository.cs:183, 208` |
| 5 | LOW | Four ticket boxes bind on change, so the gated button stays disabled until blur and the first click is lost | `AdminEventLog.razor:436`, `Migration.razor:63, 237`, `NamedLocations.razor:187` |
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

## Claimed by Gemini only - NOT verified (candidates, not findings)

| # | Sev claimed | Claim | Where | Caution |
|---|---|---|---|---|
| G1 | HIGH | Protected-principal resolution runs `Get-ADUser` with no `-Server`, so only one domain is searched; a synced user in another domain of the forest with no mailbox resolves NotFound and is treated as cloud-only, skipping SID, group, OU and pattern rules. Also: a primary address skips the bind-by-SID path that an alias takes | `ProtectedPrincipalService.cs:1570-1575, 861-945` | The rev-abc slices (2026-11-05..09) reworked forest scope and runtime discovery; Gemini may have read one path and not the one that serves it. Must be traced before it is believed |
| G2 | HIGH | M365 Groups protection check returns "allow" for a blank identity (remove member/owner with empty UPN and mail), and checks the raw typed string before Graph resolution, so a typed Entra object id for a mailbox-less synced user is not protected | `M365GroupManagementService.cs:202-205, 256-270, 306-311, 403-411`, `M365GroupManagement.razor:734-746` | Sibling fix exists in `GroupManagementService` (gmn-1/gmn-6); whether Graph can return a member with both fields empty is unverified |
| G3 | MEDIUM | Account Lockout fetches credentials and queries AD before the logoff-policy and ticket checks; every early refusal is marked Executed and mails the administrator as a failed execution | `AccountLockoutRemediationService.cs:151-189, 227-269, 324-342, 1157-1173` | Extends the observation the rev-j1 agent left alone on 2026-11-09 |
| G4 | LOW | M365 Groups and Risky Users percent-encode the whole OData filter, the form `IntuneDeviceService.cs:672` records as a shipped defect (200 with an empty result) | `M365GroupManagementService.cs:74, 353`, `RiskyUsersService.cs:113` | If true these searches would return nothing today; live behaviour says otherwise or nobody noticed |

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

## Open

G1-G4 need an independent trace before they are admitted or declined. No second outside reviewer has run.
