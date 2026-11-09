# Dev deploy 2026-11-09 - post-deploy test checklist

Dev runs master code at 350c63b5 (base 2.36.0). Consolidated from every "deploy check" paragraph in `.agents/state.md`
2026-11-05 .. 2026-11-09. Record the result of each line here (`[x]` pass, `[!]` fail with a note) as the owner reports
it. Environment names belong in the owner's notes, not in this file.

## A. Setup first (nothing else is meaningful before these)

- [ ] A1. Admin Settings: fill the Graph Directory Read Credential. Until it is filled, protection checks on
  alias-addressed synced recipients refuse as unavailable.
- [ ] A2. Confirm `User.Read.All` (Application) on the M365 Groups app registration; Graph grants and roles for Cloud
  Password Reset; the `employeeID` `searchFlags` index.
- [ ] A3. `NotifySecurityTeam` and `Email:AdminNotificationEmail` set on both instances.
- [ ] A4. Remove the scratch publish folder used for the identifier conversion.

## B. Protected-principal path (highest risk; the rev-abc fixes)

- [ ] B1. A grant to a user who reaches a protected security group only through a distribution group must REFUSE.
  Then read the log line "Protected-principal check ... took n ms (r directory reads)": nowhere near 10 s.
- [ ] B2. A write to a user or group inside a protected OU that has been RENAMED since the rule was saved must refuse.
- [ ] B3. A protected check, a Verify and (if any legacy row remains) a conversion against a group in the other tree of
  the forest.
- [ ] B4. Rotate the directory-read secret under the same Secret Server ID; expect one "rejected ... fetching it afresh"
  log line, then success.
- [ ] B5. Protected principals pane: pick a replacement for a row (Replace) with the same object; ignores on that row
  must survive. Start an edit, press Verify while editing: the pending edit stays; Save after a concurrent change refuses
  on the version check.

## C. Session revalidation (rev-b5, rev-e1)

- [ ] C1. Block the directory for your account's lookup (or use a test account whose lookup fails). One tab: wait for one
  interval check to fail (Warning "1 failed lookup(s) in a row for this account"), then reload: expect "This page was not
  shown" (403) and a `SessionRevalidation` audit line with trigger `PageRequest`, reason `LookupFailed`.
- [ ] C2. Two tabs open, one stall: one "1 in a row" warning per tab, both tabs stay.

## D. Verify stored directory objects (V1-V4)

- [ ] D1. Press Verify on Admin Settings Protected principals; Module Configuration Access and Module Admins for one
  module; the config pane of Migration, Emergency Disable, AD Attribute Editor, BitLocker Recovery, Conference Rooms.
- [ ] D2. A Conference Rooms address reads Verified (recipient group types were unverified against live Exchange).

## E. Modules

- [ ] E1. Comms-10k: download the membership CSV of a scratch group, run the module's clear-then-add path against it;
  time it against the plan's 7.9 s class; read-back matches by GUID.
- [ ] E2. Bulk permissions: grant FullAccess to a user who already has it (expect "No change", no mail); a 3-row CSV for
  one trustee (expect ONE digest mail).
- [ ] E3. Calendar on-prem: grant, change and remove on a test mailbox, including a non-English calendar folder name:
  no "could not be confirmed", no `_UNCONFIRMED` mail. Cloud: Reviewer grant to a user with no entry succeeds; an
  unresolvable user's refusal begins "Set-MailboxFolderPermission failed for".
- [ ] E4. Auto-reply scheduled from a non-UTC browser reads back the typed clock in OWA and
  `Get-MailboxAutoReplyConfiguration`.
- [ ] E5. AD Attribute Editor: save one extensionAttribute and one msExchRecipientDisplayType; expect Undo offered, no
  "was not confirmed", and a log line "pinned to domain controller <name>" naming a WRITABLE DC.
- [ ] E6. Conference Rooms: one synced-list Room Finder row with City/State/Country: one credential purpose logged; time
  against the old 25-30 s.
- [ ] E7. Tickets (after the 2.36.2 redeploy): paste a ticket containing a zero-width space; no message, button enabled, the
  write goes ahead and the audit row shows the ticket without the character.
- [ ] E8. MFA Reset: double-click Reset; exactly one reset and one notification.
- [ ] E9. Defender: look for `runHuntingQuery ... 429` lines and the two new warnings only if a throttle occurs.
- [ ] E10. Cloud Password Reset: two operations within 5 minutes reuse one Graph client; after a secret rotation a new
  operation re-reads.
- [ ] E11. Show the Splunk owner the README audit result and refusal-convention section (item 44 pending your ruling).

## F. Deploy scripts (sweep-g, rev-f, rev-g1, rev-i)

- [x] F1. `deploy-pipeline.ps1 -Dev -PlanOnly`: 0 refusals after the host ACL narrowing (owner, 2026-11-09 12:22).
- [ ] F2. `deploy-pipeline.ps1 -Prod -PlanOnly` elevated under Windows PowerShell 5.1: steps, serving-pool answer, db
  tool verdict, "0 refusal(s)", nothing changed.
- [ ] F3. The cutover plan on the cut-over host: same expectations.
- [ ] F4. `(Get-ItemProperty IIS:\AppPools\<pool> -Name processModel.shutdownTimeLimit)`: type and value (expect
  00:01:30).
- [ ] F5. Only before making `-NarrowPublishAcl` the default: install dev with it, `Get-Acl` the folder, confirm the
  app starts.
- [ ] F6. Post-deploy drift check and `PRAGMA integrity_check` lines in the deploy output were clean.

## G. Open owner rulings (not tests; listed so they are not lost)

Item 38 binaries backup; rev-f2 and rev-e1 scope; item 44 audit convention; item 24 SyncedWithErrors; Constitution
lines 80 and 95; rev-x1 scope; default review harness after codex-commercial re-login; `-TrustedPrincipal` parameter.
