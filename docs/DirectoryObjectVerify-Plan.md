# Verify stored directory objects - Plan

Status: **Draft 2026-11-04 - awaiting the owner's go on the Exec summary.**
Owner: Michael
Date: 2026-11-04
Last verified against code: local `master` `9f56c345`, 2026-11-04. Every file:line cited below was read there by the
inventory pass; lines move, so re-read before editing (Known Failure Class 4).
Versions (proposed): base app stays `2.36.0` (undeployed; absorbs shared changes); AdminSettings `1.2.6` -> `1.3.0`
(new button and actions on its page). Module Configuration has no catalogue entry. No other module's behaviour changes.
Authority: subordinate to `docs/ProjectConstitution.md` and `AGENTS.md`. A written plan is required: this adds a page
action that writes to authorization stores and changes how one class of protected-principal row is enforced.
Request (owner, 2026-11-04, verbatim): "let's add a 'Verify' function and button to all the places where there's a
directory object stored. on the protected principals page, this will verify that the objects exist and still match.
any inconsistencies get reported with options to fix, remove, or ignore, and the associated plumbing needed for those to
work."
Related: `docs/ProtectedPrincipalIdentifiers-Plan.md` (identifier storage, section K conversion verbs),
`docs/SectionAccessSidStorage-Plan.md` (section-access SIDs), `.agents/review/live-risk-sweep-2026-10-30.md` item 17.

## Exec summary

*The only part of this plan you need to read. Everything after it is for the agent that builds it.*

**What it does.** Every page that stores a directory object gets a **Verify** button. Pressing it asks the directory,
once, about every stored entry on that page and reports what it finds, entry by entry: the object is gone; the object
exists but its name has changed since it was stored; the entry was stored by name and has never been converted to an
identifier; the entry is not one object but several; or the directory could not be asked. Beside each finding are the
actions that make sense for it: **Fix** (update the stored name to the object's current name, or convert a name to its
identifier, or pick a replacement), **Remove** (delete the entry), and **Ignore** (keep the entry, record that you have
seen the finding, and stop it being reported until something changes). Entries with nothing wrong are marked as
verified. Every Fix, Remove and Ignore is written to the audit log with who did it and why the entry was flagged.

The pages: Admin Settings' protected principals (users, groups, organisational units, protected group targets);
Module Configuration's access groups, protection-servicer group and module admins for each module; and the module
settings that hold a directory object as text: Migration's excluded groups, Emergency Disable's lockdown OU, the two
search-base settings (AD Attribute Editor, BitLocker Recovery), and Conference Rooms' eight group addresses, which are
checked against Exchange rather than Active Directory.

**What it gets you.** One button answers "is this list still right?" for every list, instead of waiting for a refused
write to tell you. It also settles the question left open yesterday: a protected group that has been deleted. Until you
act, that entry keeps refusing writes that find no other protected match, exactly as today, but the refusal names the
entry and tells the operator to run Verify. You then choose per entry: Remove it, Fix it by picking a replacement, or
Ignore it, which tells the check to treat that entry as protecting nobody from then on. No entry stops protecting
anything without an admin's named, audited decision.

**What it costs.** Four slices of work and their tests. One new button and one results panel per page, following the
pages' existing row and badge layout; no new screens. One directory round trip per Verify press, under the protection
service's existing warm connection and credential. The page-load sweep that today paints "not in AD" on old entries is
replaced by the button, so pages open faster and the directory is asked only when you ask.

**The biggest risk.** Ignore reduces protection by design, once, for one entry, on an admin's say-so. It is audited,
visible on the page as a badge, and undone by removing the entry or by re-picking it. The second risk: Fix rewrites the
stored name beside an identifier, which is also what the old build on prod still matches by; the rewrite brings prod's
copy up to date, it cannot make it worse than the stale name it holds now.

**Decisions for you.** None beyond approving. The deleted-group behaviour above is the plan's answer to yesterday's open
question; say so if you want it otherwise.

**Approving it authorises** the four slices below: the Verify engine and button on the three pages, the Fix, Remove and
Ignore actions with their audit rows, an ignore list in the settings database, the change that lets an ignored entry
protect nobody, the removal of the page-load sweep, and the matching help, README and run-book changes. It does not
authorise deploying, or any run against the live directory or database.

## A. Inventory (what Verify covers)

From the read-only inventory of 2026-11-04 (the stores that hold a directory reference; file:line at `9f56c345`).

| # | Store | What is stored | Page | Verify checks |
|---|---|---|---|---|
| A1 | `protected_principal` kinds `user_sid`, `group_sid` (`objectSid\|label`), `ou_guid`, `group_target` (`objectGUID\|DN`) - `Services/Storage/ProtectedPrincipalRepository.cs:15-40`, `:235-272` | identifier + label | Admin Settings, Protected Principals pane (`Components/Pages/AdminSettings.razor:133-306`) | object exists by identifier; label equals the object's current UPN (user) or DN (group, OU, target); object class matches the kind |
| A2 | `protected_principal` legacy kinds `user`, `group`, `ou` with no identifier companion (unpaired) | name, DN or SID string | same pane; today's "not in AD" badge `:1103-1116` and sweep `:1139-1216` | not converted (resolves to exactly one object -> convertible; none; several; unreadable) |
| A3 | `section_access` rows per policy alias (`Services/Storage/SectionAccessRepository.cs:39-55`) and `ProtectedServicer:<ModuleId>` rows (`Services/ProtectedPrincipalServicerService.cs:60-84`) | group SID + cosmetic display name | Module Configuration, Access pane (`Components/Pages/ModuleConfig.razor:140-238`) and Protection servicer (`:249-338`) | group exists by SID; display name equals current `DOMAIN\sAMAccountName`; a non-SID row -> not converted |
| A4 | `module_admins` (`Services/Storage/ModuleAdminRepository.cs:17-36`) | group SID; legacy names | Module Configuration, Module Admins pane (`:612-675`) | as A3 |
| A5 | Migration `ExcludedADGroups` (`Modules/ModuleCatalog.cs:527`; `Services/MigrationService.cs:342-400`) | comma-separated group names / SIDs / GUIDs | Module Configuration config pane, text (`:480-490`) | each entry resolves to exactly one group in the forest (named-domain rules of `MigrationService.ResolveExcludedGroup`); several -> reported |
| A6 | EmergencyDisable `LockdownOuDn` (`ModuleCatalog.cs:1302-1304`) | OU DN | config pane, text | OU exists at that DN |
| A7 | ADAttributeEditor `DefaultSearchBase` (`:1726`, semicolon list) and BitLockerRecovery `ActiveDirectorySearchBase` (`:1462`) | DNs | config pane (`:380-451`, text) | each DN exists |
| A8 | ConferenceRooms eight group settings (`ModuleCatalog.cs:1217-1224`; `Services/ConferenceRoomService.cs:36-49`) | SMTP address text | config pane, text | Exchange `Get-Recipient -Identity <address>` returns exactly one recipient and it is a group |

Not covered, and why: MailboxPermissions `ExcludedUsers` has no field and no page (`ProtectedPrincipalService.cs:1636-1650`;
reachable only by database edit); `sam_pattern` is a pattern, not an object; `appsettings.json` `Security:*` groups are
file config with no page (startup already refuses entries revalidation cannot re-check, `Authorization/CircuitRevalidationSettings.cs:108-156`);
jobs-database snapshots and Emergency Disable snapshot files are operational records, not configuration. Conference
Rooms' three contact e-mail settings are text shown in MailTips, not objects.

## B. The Verify engine

### B.1 One service, three directories

`Services/DirectoryObjectVerifier.cs` (new): `VerifyAsync(IReadOnlyList<VerifyItem> items, VerifyScope scope, CancellationToken)`
-> `VerifyReport`. A `VerifyItem` names its store (A1-A8), its row key, its kind (user, group, OU, group target,
recipient group), its identifier (SID / objectGUID / null) and its stored label or text. The report holds one
`VerifyFinding` per item: `Verified`, `Missing`, `LabelDrift(current)`, `NotConverted(resolution)`, `Ambiguous(matches)`,
`WrongClass(actual)`, `Unreadable(reason)`, plus `Ignored(previous finding)` when the ignore list covers it (B.3).

Reads:

- **Active Directory items (A1-A7)** go through the protection service's warm `DirectorySessionPool` and the directory-read
  credential under ONE `DirectoryReadOperation.BeginCheck` per Verify press (`Services/ProtectedPrincipalService.cs:279-298`,
  `:460-475`), so one credential fetch and the existing timing line. Identifier lookups are batched: global-catalog
  `(|(objectSid=..)(objectSid=..)...)` in chunks of 50 with `objectSid`, `objectGUID`, `objectClass`, `userPrincipalName`,
  `distinguishedName`, `sAMAccountName`; GUID lookups the same way on `objectGUID`. Names (A2, A5, non-SID A3/A4 rows) use
  the moved resolver `ProtectedPrincipalIdentifierResolver.Resolve` / `ResolveGroupEntry` (`:119`, the K slice's code),
  exactly-one rule. DNs (A6, A7, and label comparison) are base reads at the DN's own domain
  (`GroupManagementService.ServerFromDn`). No unanchored walk (decision 2026-10-30).
- **Exchange items (A8)** go through the Conference Rooms module's own Exchange session (`ConferenceRoomService`, its
  existing credential), one `Get-Recipient` per address, `RecipientTypeDetails` must be a group type.
- A read that throws or times out is `Unreadable` for the items in that batch, never a silent `Verified`, and never
  `Missing` (Known Failure Class 3). The report says how many could not be read.

### B.2 Comparison rules

- A1 `user_sid`: `Missing` if no object carries the SID; `WrongClass` if it is not a user; `LabelDrift` if
  `userPrincipalName` differs from the label (OrdinalIgnoreCase); else `Verified`. `group_sid`: same with `objectClass=group`
  and the DN. `ou_guid`: object by GUID, class `organizationalUnit`, DN. `group_target`: object by GUID, class group, DN.
- A2 legacy unpaired rows: `NotConverted` carrying the resolver's answer: exactly one object (convertible), none
  (`Missing`), several (`Ambiguous`), or `Unreadable`.
- A3/A4 SID rows: `Missing` / `WrongClass` / `LabelDrift` (display name vs `DOMAIN\sAMAccountName`, where the stored
  display name exists) / `Verified`. Non-SID rows: `NotConverted` as A2. Well-known SIDs (`IsUsableGroupSid` false):
  reported `WrongClass("well-known SID")` with Remove only.
- A5: per entry, the Migration resolver's rules (`MigrationService.ResolveExcludedGroup`): one -> `Verified`, none ->
  `Missing`, several -> `Ambiguous(domains)`.
- A6/A7: base read of the DN; present and class OU/container -> `Verified`; absent -> `Missing`.
- A8: exactly one recipient and a group type -> `Verified`; none -> `Missing`; not a group -> `WrongClass`.

### B.3 The ignore list

One `app_setting` key, `verify_ignores`, JSON array of `{store, rowKey, identifierOrText, finding, observed, by, at}`.
Additive, no DDL (`ConfigStoreMigrator` untouched); prod's old build never reads or writes it (it touches `app_setting`
only for `extended_log_level`, `Services/ExtendedLogService.cs:22,49`). A finding is `Ignored` when an entry matches
store + rowKey + identifierOrText + finding + observed: a renamed group ignored at name X is reported again when its
name becomes Y; a missing object ignored stays ignored until the row is removed or replaced. Removing or re-picking a
row deletes its ignore entries. The list is edited only through the page actions; `db identifier-status` prints the
ignores at the end of its report (read-only) so the run-book shows them.

### B.4 Effect of an ignore on the live check (the deleted-group answer)

`ProtectedPrincipalService` reads the ignore list with its rules (`ReplaceConfig`/`LoadRulesForEditing` path, cached
with the config). In `ProtectedGroupMembership.Evaluate`, a `group_sid` entry whose SID the directory cannot resolve
("no such object") is today `Incomplete`, naming the row (`ppi-s6`). With a matching `Missing` ignore it is skipped
with a Debug log line and counted in the timing line as `ignored`; without one it stays `Incomplete`, and the banner's
text gains "run Verify on Admin Settings". The same for a `user_sid` or `ou_guid` row the check cannot resolve and for
an unconverted legacy row with a `NotConverted` ignore (which the admin should normally Fix or Remove instead; Ignore is
offered so a row the directory cannot see any more can be acknowledged without being deleted during the prod overlap).
`LabelDrift` and `WrongClass` ignores change nothing in the check (it compares identifiers). An ignore never makes an
`Unreadable` answer complete: a directory failure still fails closed.

Section access, servicer and module-admin SIDs compare against token claims (`Authorization/GroupAuthorizationHandler.cs:75-129`,
`Services/ModuleAdminService.cs:55-60`); a missing group simply matches nobody today, so an ignore there is display-only.

## C. The page

### C.1 Button and results panel (every pane that lists objects)

A **Verify** button beside the pane heading, in the style of the validate-on-add buttons (`btn btn-outline-success`,
"Verifying..." while running, disabled while a save is pending). Progress through `Progress.Begin(... ActivitySize.Items(n))`
as the sweep does today. On completion each row gets a badge: `verified` (muted, with the time on hover), `missing`
(danger), `name changed` (warning, hover shows stored vs current), `not converted` (warning, hover shows the resolver's
answer), `several objects` (warning), `wrong type` (danger), `could not verify` (secondary), `ignored` (secondary, hover
shows who/when/why). A one-line summary above the table: "Verified N entries at <time>: M findings, K ignored, J could
not be verified." The existing `StaleBadge`/`SweepExistingEntriesAsync` (`AdminSettings.razor:1103-1216`) and the
"not converted" banner (`:120-129`) are replaced by this; `OnAfterRenderAsync` no longer sweeps (`:778-788`).

The summary of the last Verify per store (`{at, by, counts}`) is persisted in `app_setting` key `verify_last` so another
admin opening the page sees "Last verified <time> by <user>: ..." without pressing the button; findings themselves are
recomputed on press, never persisted.

### C.2 Actions per finding

| Finding | Fix | Remove | Ignore |
|---|---|---|---|
| `LabelDrift` (A1, A3, A4) | rewrite the label to the current name; for A1 also rewrite the legacy companion row's value to the current name so prod's old build matches the renamed object again | delete row (+ companion) | yes |
| `Missing` (A1 identifier rows, A3, A4) | **Replace**: opens the row's picker; the chosen object replaces the row in place (same kind), companion rewritten | yes | yes (A1: the check then treats it as protecting nobody, B.4) |
| `NotConverted` convertible (A2, non-SID A3/A4) | **Convert**: write the identifier companion (A2) or replace the name with the SID (A3/A4) - the same writes `db convert-identifiers` makes for one row (`Services/Storage/ConfigDbIdentifiers.cs:48`), reused not copied | yes | yes |
| `NotConverted` none / `Ambiguous` (A2, A3, A4, A5) | **Replace** via picker (A2-A4) or edit the text (A5) | yes | yes |
| `WrongClass` | Replace via picker | yes | yes (display only) |
| `Missing` (A5-A8 text settings) | edit the text (the field is focused; A6/A7 offer the OU browser already on the page for `DefaultSearchBase`, `:380-451`, extended to `LockdownOuDn` and `ActiveDirectorySearchBase`) | clear the entry (A5: that entry; A6-A8: the value) | yes |
| `Unreadable` | - | yes | no (nothing was learned) |

Every action is applied through the pane's existing save path, so the existing refusals, concurrency checks and audit
rows fire (`AdminSettings.razor:1344-1591`; `ModuleConfig.razor:1471-2040`). Each action also writes one audit row of its
own: `AuditService.LogSettingsChange(user, ip, "Verify:<store>", before[], after[], extra{finding, observed, action})`
(`Services/AuditService.cs:415-441`); a Verify press writes one row `extra{counts}`. Ignore writes the ignore entry and
an audit row; it is the only action that does not change the stored rules.

### C.3 Text-setting panes (A5-A8)

On the Module Configuration config pane, a field of type `AdGroup`, `OU` or a new `RecipientGroup` type gets the Verify
treatment; `ExcludedADGroups` becomes `AdGroup` (list), `LockdownOuDn`, `DefaultSearchBase`, `ActiveDirectorySearchBase`
become `OU` (the type exists, `Modules/ModuleConfigField.cs:3-15`, but only `LockdownOuDn` declares it and the editor has
no branch for it, `ModuleConfig.razor:480-490`), and the eight Conference Rooms group settings become `RecipientGroup`.
The generic editor gets one branch per type: the text box, the Verify badge beside it, and for `OU` the existing OU
browser. Values are unchanged in storage; only the field type and the editor change, so no module's runtime behaviour
changes and no module version bumps (Constitution: a module-scoped behaviour change bumps; a shared editor change on an
undeployed base does not).

## D. Help and docs (Known Failure Class 5)

Page notes and hover text for the button, each badge and each action, on both pages; README sections Protected
Principals, Module Configuration (Access, Protection servicer, Module Admins), Migration excluded groups, Emergency
Disable, AD Attribute Editor, BitLocker Recovery, Conference Rooms settings, and Troubleshooting ("Protected-principal
check was incomplete ... run Verify"); `docs/ConfigDbOperations.md` run-book: after `db identifier-status`, "press
Verify on each page" as the post-deploy check, and the ignores printout; `docs/AdminModuleSpec.md` for the new field
type `RecipientGroup` and the editor branches.

## E. Slices (one Opus agent at a time, worktree each, merge on guard proof)

- **V1 - engine + Protected Principals pane.** B.1-B.3 for A1/A2, C.1, C.2 for A1/A2, the sweep removed, audit rows,
  `verify_last`. Tests: `DirectoryObjectVerifierTests` (every finding, batching, unreadable never verified),
  page tests for badges and actions, repository tests for label rewrite and companion rewrite.
- **V2 - live-check effect.** B.4: ignore list read by the protection service, skip-with-log for ignored missing rows,
  banner text, timing-line counter, `db identifier-status` ignores printout. Tests: ignored missing row protects nobody
  and is logged; non-ignored still incomplete and named; unreadable never skipped; label-drift ignore changes nothing.
- **V3 - Module Configuration groups.** A3/A4 on the Access, Protection servicer and Module Admins panes; Convert reuses
  `ConfigDbIdentifiers` row writes. Tests per pane.
- **V4 - text settings.** C.3 field types and editor branches, A5-A8 checks (A8 through the Conference Rooms Exchange
  session), docs (D). Tests: resolver per type, editor branch per type, Conference Rooms recipient check with a stub.

Each slice: full gates (`.agents/repo-guidance.md` Verification), non-vacuity proof per new test, finding record
`dov-v1..v4`, index row; the supervisor runs master gates after the last merge and logs costs.

## F. Risks

- **Protection reduced by Ignore** - by design, per row, audited, badged, reversible (B.3/B.4). The check never treats
  `Unreadable` as ignorable.
- **Fix rewrites the legacy companion** (A1 LabelDrift) - prod's old build then matches the new name; it matched the stale
  one before, so this is a strict improvement; the write is one transaction with the identifier row's label.
- **An old-build save on prod wipes identifier rows** (plan K constraint) - Verify then reports every row `NotConverted`
  with Convert offered; the run-book already says to re-run the conversion.
- **Directory load** - one batched read set per press, under the warm pool; no unanchored walk.
- **Page line counts** - neither Admin Settings nor Module Configuration has ClickGate line entries (ppi-s6 confirmed);
  re-check before each slice.
