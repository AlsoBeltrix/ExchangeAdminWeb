# help-sweep - operator help cut to what an operator acts on

Status: in progress on `fix/help-sweep` (not merged). Owner-requested 2026-11-09 after the dev deploy:
the Protected principals note on Admin Settings had grown to nine sentences (1613 chars) of storage
mechanism, older-build compatibility and Graph SID binding, because every slice under Known Failure
Class 5 ("help left behind") appended to the help without anyone rewriting the whole.

## Rule applied

- Reader: an experienced Exchange/AD administrator. A page or pane note: at most 3 short sentences,
  under 350 characters. A hover tip or field help: one sentence.
- A cut fact is either carried by the app's own message at the moment it matters (the refusal, the
  Verify badge, the not-converted banner, the confirmation) or is in README. Where it was in neither,
  README gained it (below).
- Behaviour, markup structure, control labels, refusal strings, audit text and log lines unchanged.
- Line counts of the ClickGate-registered pages touched (Intune Devices, Self-Service Groups,
  Migration, Conference Rooms) kept identical, so no registry re-anchor;
  the ClickGate tests pass. Admin Settings and Module Configuration are not in the registry ("tier
  2, not started"), so their line counts were free to change.

## Survey

Scanner: markup `<small>`, `p.text-muted`, `form-text`, `alert-info/secondary/light` blocks, static
`title="..."` tips, `*Tip`/`*Note`/`*Help` string constants in `.razor` files, and every
`ModuleCatalog` module, permission and setting description. Before: 397 blocks, 58 over the limit.
After: 395 blocks, 8 flagged, none of them help over the limit (see "Left as is").

## Before / after

| Page / file | Block | Before (sentences / chars) | After |
|---|---|---|---|
| Admin Settings | Protected principals pane note | 9 / 1613 | 3 / 160 |
| Admin Settings | Verify pane note | 8 / 1064 | 3 / 306 |
| Admin Settings | Verify button tip | 4 / 329 | 1 / 160 |
| Admin Settings | Verify badge tips (8 kinds) | 1-4 each | 1 each |
| Admin Settings | Verify action tips (5) | 2-5 each | 1 each |
| Admin Settings | Directory Read Credential field help | 3 / 229 | 1 / 182 |
| Admin Settings | Graph Directory Read Credential field help | 3 / 597 | 1 / 185 |
| Module Configuration | Protection servicer warning | 7 / 558 | 3 / 286 |
| Module Configuration | Module Admins pane note | 4 / 664 | 1 / 88 |
| Module Configuration | Access tab Verify note | 7 / 1345 | 3 / 240 |
| Module Configuration | Admins tab Verify note | 8 / 1378 | 3 / 240 |
| Module Configuration | Configuration pane Verify note (per module) | 6-8 / 700-1000 | 2-3 / 208-330 |
| Module Configuration | Access, Admins, Configuration Verify button tips | 3-4 / 249-338 | 1 / 140-201 |
| Module Configuration | Group and config Verify badge and action tips | 1-4 each | 1 each (2 on the two reach-disclosed settings) |
| Module Configuration | Verified-setting field tips (4) | 1-3 each | 1 each |
| Services/ModuleConfigVerifyAdapter.cs | ReachDisclosure | 1 / 188 | 1 / 109 |
| ModuleCatalog | 34 module, permission and setting descriptions (largest: FillMissingEmployeeId 6 / 898, GraphDelineaSecretId True Last Logon 5 / 516, ExcludedADGroups 5 / 499, MessageTrace Search 3 / 432, Defender GraphDelineaSecretId 4 / 417) | 2-6 / 63-898 | 1 each, except FillMissingEmployeeId 2 / 274 |
| Intune Devices | standing audit note | 4 / 509 | 3 / 267 |
| Self-Service Groups | paste-list note | 4 / 328 | 3 / 265 |
| Emergency Disable | high-impact warning, no-recipient branch | 4 / 405 | 3 / ~300 |
| Migration | four button tips | 2 each | 1 each |
| BitLocker Recovery | export-date tip | 2 / 106 | 1 |
| Conference Rooms, Bulk Jobs | job-removal tip | 2 / 69 | 1 |
| Comms-10k | retained-job delete tip | 2 / 70 | 1 |

New text of the two Admin Settings notes the owner saw:

- Protected principals: "Users, groups, OUs and patterns listed here cannot be changed through any
  module. Changes take effect immediately. See README, Protected Users / Excluded Users."
- Verify: "Verify checks each entry against Active Directory and marks what it found, with actions
  beside each finding. Each action is written at once, without Save, and audited. One ignore changes a
  protection check: a protected group ignored as missing stops refusing writes while the directory
  confirms it is gone."

Where the cut Protected principals facts now live at the moment they matter: a not-converted entry -
its Verify badge ("Not converted: every protection check that finds no other match is refused until
it is converted") and the refusal ("not converted - run db convert-identifiers"); a missing protected
group - its badge and the refusal ("run Verify on Admin Settings"); the Graph credential - its own
field help and the refusal ("the Graph Directory Read Credential (Delinea Secret ID) is not set").
The Graph field help also dropped Conference Rooms from its list: protected principals do not apply
to Conference Rooms (owner ruling 2026-11-03), so that clause was already stale.

## README sections added to

- Risky Users: `MaxTotalRows` (paging to the ceiling, partial-list notice, why it is a new key) - this
  replaced a stale bullet that still said results are capped at 500 rows.
- Cloud Password Reset: the longest configured ending is removed, ignoring case.
- Emergency Disable: the lockdown OU is outside the delegation that allows routine re-enablement.
- True Last Logon: new section (it had none) - the Graph permissions both needed, that
  `User.ReadBasic.All` is not enough, that `AuditLog.Read.All` is used nowhere else and needs a
  Privileged Role Administrator or Global Administrator to consent, and that an unset secret reports
  the cloud half as not checked, never dormant.
- Everything else cut was already in README: Protected Users / Excluded Users (identifiers, companion
  rows, the conversion, Verify and its ignores), Per-Module Config Pages (store lookup rules, Verify
  actions), Exchange Migration (excluded-group resolution), Cloud Password Reset (employee-ID fill),
  Defender for Endpoint Devices (consent, MaxDevices), Intune Devices (Entra removal scope), BitLocker
  Recovery (local path), Admin Event Log (usage view keeps existing rows), Security & Compliance (the
  Graph credential's SID binding).

## Tests changed

Help-string pins updated to the new text, existence pins kept: `IdentifierConversionPageTests`
(pane test renamed `TheProtectedPrincipalsPane_SaysWhatItProtects_AndOffersVerify_WithNoBanner`, now
also pins that "older build" is gone), `DirectoryObjectVerifyPageTests`, `AdminSettingsGraphSecretTests`,
`SelfGrantDocumentationTests`, `ModuleGroupVerifyStoreTests` (the per-store lookup-rule pins replaced by
an under-350-chars guard on both notes), `ModuleConfigVerifyStoreTests`, `MigrationExclusionWordingTests`,
`IntuneDevicesPageTests`; version pins in `CloudPasswordResetDestinationTests`,
`CloudPasswordResetEmployeeIdPageTests`, `EmergencyDisableServiceTests`, `ModuleCatalogTests`,
`ServiceHealthPageTests`. No new test ships a fix, so there is no revert-and-watch-it-fail proof to run.

## Versioning

Rule applied: Constitution, Deployment And Versioning ("module-scoped behavior changes bump that
module's version") read with Code Change Discipline ("help text is part of the behaviour") and
`docs/InAppHelp-Plan.md` 5.3 ("help text is a module change: a patch bump"). Each module whose own
page or catalog help changed takes one patch bump on this branch, in the first commit that touches
it, with a version comment; a later commit on the same module extends that comment rather than
bumping again. Bumped: Admin Settings 1.3.3, Exchange Online 1.0.7, Migration 1.27.4, Message Trace
1.8.9, Cloud Password Reset 1.2.3, Account Lockout Remediation 1.3.3, Conference Rooms 2.20.2,
Emergency Disable 1.7.4, True Last Logon 1.0.3, Risky Users 1.6.3, DHCP Authorization 1.9.2, BitLocker
Recovery 1.3.3, Defender for Endpoint Devices 1.6.20, Intune Devices 1.6.2, Service Health 1.3.4,
Admin Event Log 1.5.3, Self-Service Groups 1.14.4, Bulk Jobs 1.2.4, Comms-10k 1.4.22. Module
Configuration is a shared page (no module of its own); its change would bump the base app version,
which the brief withheld - base stays 2.36.0. Open: the owner decides whether the Module
Configuration help change takes a base patch bump at close-out.

## Left as is, and kept over the limit

- Fill in a missing employee ID (Cloud Password Reset setting): 2 sentences. The second, "Anyone who
  can reset passwords here can trigger that save.", is the permission consequence an admin switching
  it on would not expect, and is test-pinned as that.
- The two reach-disclosed Configuration Verify settings (lockdown OU, BitLocker search base): badge
  tips are 2 sentences - the finding plus "Verify does not test that the module's own account ... can
  reach it", which is pinned in every hover of those settings (R5).
- Not help, so out of scope though the scanner flags them: Comms-10k's validation result, Defender's
  observer-lookup warning, Group Management's resolve rows, Migration's batch summary line, and the
  Mailbox/Calendar Permissions audit line (2 sentences rendered, under 350). Status and refusal
  messages built in `@code` (Out of Office time-zone, Event Log rotation, In-flight work guard) were
  not swept. Intune's "What do these actions do?" panel (3 sentences each, under 350) and Risky Users'
  owner-approved action consequences were left alone.
- Noticed, not changed: Module Configuration's Delinea banner names a deployment-specific secret
  template ("ADI - Azure App Registration"), which Architectural Invariant 7 forbids in source.
