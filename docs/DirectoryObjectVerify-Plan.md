# Verify stored directory objects - Plan

Status: **Approved in principle 2026-11-04 ("approved, but do a review with codex as a sanity check"); under codex-commercial
review, up to three rounds to consensus ("clear to review. use up to 3 rounds for consensus."). Revision 2 after round 1
(ACCEPTABLE WITH CHANGES, findings F1-F7, all adopted - section G).**
Owner: Michael
Date: 2026-11-04
Last verified against code: local `master` `389d8dda`, 2026-11-04 (inventory pass plus the round-1 reviewer's re-verification).
Lines move; re-read before editing (Known Failure Class 4).
Versions (proposed): base app stays `2.36.0` (undeployed; absorbs shared changes); AdminSettings `1.2.6` -> `1.3.0`
(new button and actions on its page). Module Configuration has no catalogue entry. No other module's runtime behaviour
changes (C.3 changes field types and the editor only).
Authority: subordinate to `docs/ProjectConstitution.md` and `AGENTS.md`. A written plan is required: this adds a page
action that writes to authorization stores and changes how one class of protected-principal row is enforced.
Request (owner, 2026-11-04, verbatim): "let's add a 'Verify' function and button to all the places where there's a
directory object stored. on the protected principals page, this will verify that the objects exist and still match.
any inconsistencies get reported with options to fix, remove, or ignore, and the associated plumbing needed for those to
work."
Related: `docs/ProtectedPrincipalIdentifiers-Plan.md` (identifier storage; section K conversion verbs),
`docs/SectionAccessSidStorage-Plan.md` (section-access SIDs), `.agents/review/live-risk-sweep-2026-10-30.md` item 17,
`.agents/decisions.md` 2026-11-03 "Identifier conversion is a one-time command-line step" (unconverted rows fail closed).

## Exec summary

*The only part of this plan you need to read. Everything after it is for the agent that builds it.*

**What it does.** Every page that stores a directory object gets a **Verify** button. Pressing it asks the directory
about every stored entry on that page and reports what it finds, entry by entry: the object is gone; the object exists
but its name has changed since it was stored; the entry was stored by name and has never been converted to an
identifier; the entry is not one object but several; the entry is the wrong kind of object; or the directory could not
be asked about it. Beside each finding are the actions that make sense for it: **Fix** (update the stored name to the
object's current name, convert a name to its identifier, or pick a replacement), **Remove** (delete the entry), and
**Ignore** (keep the entry, record that you have seen the finding, and stop it being reported until something changes).
Entries with nothing wrong are marked as verified. Every Fix, Remove and Ignore is written to the audit log with who did
it and why the entry was flagged.

The pages: Admin Settings' protected principals (users, groups, organisational units, protected group targets);
Module Configuration's access groups, protection-servicer group and module admins for each module; and the module
settings that hold a directory object as text: Migration's excluded groups, Emergency Disable's lockdown OU, the two
search-base settings (AD Attribute Editor, BitLocker Recovery), and Conference Rooms' eight group addresses, which are
checked against Exchange rather than Active Directory. Each setting is checked by the same rule its module applies when
it uses the value, so Verify never calls a setting wrong that the module accepts, or right that it refuses.

**What it gets you.** One button answers "is this list still right?" for every list, instead of a refused write telling
you. It also settles the question left open on 3 November: a protected group that has been deleted. Until you act, that
entry keeps refusing writes that find no other protected match, exactly as today, but the refusal names the entry and
tells the operator to run Verify. You then choose per entry: Remove it, Fix it by picking a replacement, or Ignore it.
Ignoring a deleted group tells the check to stop refusing on that entry's account; because the entry still holds the
group's identifier, protection resumes by itself if the group is ever restored from the recycle bin. An entry that was
never converted to an identifier cannot be ignored, only fixed or removed: that keeps your ruling of 3 November that an
unconverted entry fails closed until it is converted. No entry stops protecting anything without an admin's named,
audited decision.

**What it costs.** Four slices of work and their tests, each carrying its own help and README changes. One button and
one results panel per page in the pages' existing layout; no new screens. One credential fetch and one warm directory
connection per press, carrying a handful of batched reads; the Conference Rooms addresses add one Exchange call each.
The background sweep that today runs when the protected-principals page opens is replaced by the button, so the page no
longer talks to the directory unless you ask it to.

**The biggest risk.** Ignore reduces protection by design, once, for one deleted group, on an admin's say-so. It is
audited, badged on the page, and undone by removing or re-picking the entry; the directory being unreachable is never
ignorable. The second risk: Fix rewrites a stored name. For protected users and organisational units that name is also
what two fallback checks compare when the directory gives them nothing better, so a Fix changes what those fallbacks
match, from the old name to the object's current one, which is the object you protected. Prod's old build, which still
matches by name, gets the same update.

**Decisions for you.** None beyond approving. The deleted-group behaviour above is this plan's answer to the open
question; say so if you want it otherwise.

**Approving it authorises** the four slices below: the Verify engine and button on the three pages, the Fix, Remove and
Ignore actions with their audit rows, an ignore list in the settings database, the change that lets an ignored deleted
group stop refusing, the removal of the page-load sweep, the new field types for the text settings, and the matching
help, README and run-book changes. It does not authorise deploying, or any run against the live directory or database.

## A. Inventory (what Verify covers)

From the read-only inventory of 2026-11-04 (file:line at `389d8dda`).

| # | Store | What is stored | Page | Verify checks (rule source) |
|---|---|---|---|---|
| A1 | `protected_principal` kinds `user_sid`, `group_sid` (`objectSid\|label`), `ou_guid`, `group_target` (`objectGUID\|DN`) - `Services/Storage/ProtectedPrincipalRepository.cs:15-40`, `:235-272` | identifier + label | Admin Settings, Protected Principals pane (`Components/Pages/AdminSettings.razor:133-306`) | object exists by identifier; object class matches the kind; label equals the object's canonical name (B.2) |
| A2 | `protected_principal` legacy kinds `user`, `group`, `ou` with no identifier companion (unpaired) | name, DN or SID string | same pane; today's "not in AD" badge `:1103-1116` and after-first-paint sweep `:778-788`, `:1139-1216` | not converted: resolves to exactly one object (convertible), none, several, or unreadable - `ProtectedPrincipalIdentifierResolver.Resolve` (`:119`) |
| A3 | `section_access` rows per policy alias (`Services/Storage/SectionAccessRepository.cs:39-55`) and `ProtectedServicer:<ModuleId>` rows (`Services/ProtectedPrincipalServicerService.cs:60-84`) | group SID + cosmetic display name (`group_display_name`) | Module Configuration, Access pane (`Components/Pages/ModuleConfig.razor:140-238`) and Protection servicer (`:249-338`) | group exists by SID; class group; display name equals current `DOMAIN\sAMAccountName`; a non-SID row -> not converted via `SectionAccessRepository.ConvertToSids` rules (`:364-440`) |
| A4 | `module_admins` (`Services/Storage/ModuleAdminRepository.cs:17-36`); display names borrowed from `section_access` (`ModuleConfig.razor:2254`) | group SID; legacy names | Module Configuration, Module Admins pane (`:612-675`) | as A3; name rows via `ProtectedPrincipalIdentifierResolver.ResolveModuleAdminGrants` (`:146`) rules |
| A5 | Migration `ExcludedADGroups` (`Modules/ModuleCatalog.cs:527`) | comma-separated: anything `Get-ADGroup -Identity` accepts (name, SID, GUID, DN) | Module Configuration config pane, text (`ModuleConfig.razor:480-490`) | each entry through `MigrationService.ResolveExcludedGroup` itself (`Services/MigrationService.cs:459`): one group -> verified; none -> missing; several domains -> several (the module excludes members of each and warns) |
| A6 | EmergencyDisable `LockdownOuDn` (`ModuleCatalog.cs:1302-1304`, `FieldType: OU`) | OU DN | config pane, text | `Get-ADOrganizationalUnit -Identity <dn>` in the DN's domain, the module's own precondition (`Services/EmergencyDisableService.cs:750-754`): an OU only, never a container |
| A7 | ADAttributeEditor `DefaultSearchBase` (`:1726`, semicolon list) and BitLockerRecovery `ActiveDirectorySearchBase` (`:1462`) | DNs used as `-SearchBase` | config pane (`:380-451`, text + OU browser for the first) | base read of the DN: any object that can be a search base (OU, container, domain root) -> verified; absent -> missing |
| A8 | ConferenceRooms eight group settings (`ModuleCatalog.cs:1217-1224`; `Services/ConferenceRoomService.cs:36-49`) | SMTP address text | config pane, text | Exchange `Get-Recipient -Identity <address>` through the module's session: exactly one recipient of a group type -> verified; confirmed not found -> missing; another type -> wrong type |

Not covered, and why: MailboxPermissions `ExcludedUsers` has no field and no page (`Services/ProtectedPrincipalService.cs:1636-1650`;
reachable only by database edit); `sam_pattern` is a pattern, not an object; `appsettings.json` `Security:*` groups are
file config with no page (startup refuses entries revalidation cannot re-check only while revalidation is enabled,
`Authorization/CircuitRevalidationSettings.cs:108-156`); jobs-database snapshots and Emergency Disable snapshot files are
operational records, not configuration; Conference Rooms' three contact e-mail settings are text shown in MailTips.

## B. The Verify engine

### B.1 One service, store adapters, two directories

`Services/DirectoryObjectVerifier.cs` (new): `VerifyAsync(IReadOnlyList<VerifyItem>, CancellationToken)` -> `VerifyReport`.
A `VerifyItem` carries its store (A1-A8), row key, kind (user, group, OU, group target, search base, recipient group),
identifier (SID / objectGUID / null), stored label or text, and the **snapshot version** of the store it was read from
(B.3). One `IVerifyStoreAdapter` per store (A1/A2 share one, A3/A4 one, A5-A8 one each) owns: how its rows become items,
which lookup rule applies (the module's own, per column 5 of the table), the permitted object classes, and which write
primitive each action uses (C.2). The engine owns batching, the report, and the finding taxonomy.

Findings: `Verified`, `Missing`, `LabelDrift(current)`, `NotConverted(resolution)`, `Ambiguous(matches)`, `WrongClass(actual)`,
`Unreadable(reason)`, and `Ignored(underlying)` when the ignore list covers the row (B.3).

Reads:

- **Active Directory items (A1-A7)** run under ONE `DirectoryReadOperation.BeginCheck` per press
  (`Services/ProtectedPrincipalService.cs:460-475`) on the protection service's warm `DirectorySessionPool`
  (`:279-298`) with the directory-read credential: one credential fetch, one session, the existing timing line. Several
  batched reads, not one round trip: identifier lookups at the global catalog in chunks of 50,
  `(|(objectSid=..)...)` / `(|(objectGUID=..)...)`, attributes `objectSid`, `objectGUID`, `objectClass`,
  `userPrincipalName`, `mail`, `distinguishedName`, `sAMAccountName`; DN reads (A6, A7, and label comparison for groups,
  OUs, targets) as base reads in the DN's own domain. No unanchored walk (decision 2026-10-30).
- **Forest scope (F6).** A DN whose domain components name a domain that is not in the host's forest is `Unreadable("domain
  is not in this forest")`, never `Missing`: the engine applies the forest check `ADDirectorySearchService` already makes
  for OU validation (`Services/ADDirectorySearchService.cs:485`), not only `GroupManagementService.ServerFromDn` (`:1086`),
  which extracts the domain but establishes nothing about membership.
- **Exchange items (A8)** go through the Conference Rooms module's Exchange session and credential, one `Get-Recipient`
  per address. Exchange reports absence by throwing; the engine uses `ExchangeServiceBase`'s existing not-found
  translation (`Services/ExchangeServiceBase.cs:479`) so a confirmed not-found is `Missing` and every other exception is
  `Unreadable`.
- A read that throws or times out (other than Exchange's confirmed not-found) is `Unreadable` for every item in that
  batch, never `Verified` and never `Missing` (Known Failure Class 3). The report counts them.

### B.2 Comparison rules

Canonical label per kind, matching what the validator stores on add (`Services/ProtectedPrincipalEntryValidator.cs:224`):
user = `userPrincipalName`, else `mail`, else the stored value unchanged (a user with neither is never `LabelDrift`);
group / OU / group target = `distinguishedName`. Comparison is OrdinalIgnoreCase.

- A1 `user_sid`: no object carries the SID -> `Missing`; class not user -> `WrongClass`; canonical label differs ->
  `LabelDrift(current)`; else `Verified`. `group_sid`: class group, DN. `ou_guid`: GUID, class `organizationalUnit`, DN.
  `group_target`: GUID, class group, DN.
- A2 unpaired legacy rows: `NotConverted` carrying the resolver's answer (exactly one object -> convertible; none;
  several; unreadable).
- A3/A4 SID rows: `Missing` / `WrongClass` / `LabelDrift` (stored display name, where one exists, vs current
  `DOMAIN\sAMAccountName`) / `Verified`. Non-SID rows: `NotConverted` by that store's own rule (A3 `ConvertToSids`,
  A4 `ResolveModuleAdminGrants`). A well-known SID (`IsUsableGroupSid` false) is `WrongClass("well-known SID")`, Remove only.
- A5: `MigrationService.ResolveExcludedGroup` per entry: one -> `Verified`; none -> `Missing`; several -> `Ambiguous(domains)`.
  The generic resolver is NOT used here: it has no GUID branch (`Services/ProtectedPrincipalIdentifierResolver.cs:323`)
  and would misreport a GUID entry the module accepts.
- A6: `Get-ADOrganizationalUnit -Identity` in the DN's domain: found -> `Verified`; not found -> `Missing` (a container at
  that DN is `Missing` too, because the module will refuse it at `EmergencyDisableService.cs:752`).
- A7: base read: present -> `Verified`; absent -> `Missing`. No class restriction (`-SearchBase` accepts any container).
- A8: exactly one recipient, group type -> `Verified`; none -> `Missing`; other type -> `WrongClass`.

### B.3 The ignore list and its concurrency (F4)

One `app_setting` key, `verify_ignores`: JSON array of `{store, rowKey, identifierOrText, finding, observed, by, at}`.
Additive, no DDL; prod's old build never touches it (its only `app_setting` use is `extended_log_level`,
`Services/ExtendedLogService.cs:147`, `:482`). Matching is on store + rowKey + identifierOrText + finding + observed: a
group ignored under name X is reported again when its name becomes Y; a missing object stays ignored until its row is
removed or replaced.

Because an ignore can change enforcement (B.4), the list is authorization state and is written like one:

- **Compare-and-swap.** New `AppSettingRepository.ReplaceIfEqual(key, expectedValue, newValue)` updates only when the
  stored value still equals `expectedValue`, in one transaction, and returns false otherwise (the existing `Set`, `:34`,
  upserts blind and is not used for this key). The page re-reads and retries once; a second mismatch is reported and
  nothing is written.
- **Atomic cleanup.** Remove and Replace on a protected-principal row delete the row (with its companion) AND its
  ignore entries in the same `_store.Write` transaction: new `ProtectedPrincipalRepository.RemoveWithCompanionAndIgnores`
  / `ReplaceWithCompanionAndIgnores`, which take the expected ignore-list value and apply the same compare-and-swap
  inside the transaction. The same for the section-access and module-admin removes.
- **Coherent read.** `ProtectedPrincipalService` loads the ignore list in the same connection as the rules
  (`LoadRules` path, versioned at `:1421`) and caches both together; a change to either invalidates the cache. Malformed
  JSON is treated as an empty list and logged at Warning (nothing is ignored on a corrupt value: fail closed).
- `db identifier-status` prints the ignore list at the end of its report (read-only).

### B.4 Effect of an ignore on the live check (F1 - precise)

Only one enforcement path can fail on a stored entry's account, so only one kind of ignore changes enforcement:

- **`group_sid` row, `Missing` ignore.** In `ProtectedGroupMembership.Evaluate` a SID the target's token carries matches
  before any resolution (`Services/ProtectedGroupMembership.cs:323-333`); only a SID the token does not carry is resolved,
  for the membership-link paths, and "no such object" there is `Incomplete`, naming the row (ppi-s6). With a matching
  ignore, that resolution step is skipped for that row with a Debug log line and an `ignored` count in the timing line;
  the token comparison still runs first. So: a deleted group stops refusing; a group restored from the recycle bin (same
  SID) protects again at once through the token path, and the ignore is reported as stale on the next Verify
  (`observed` no longer matches). Without an ignore the row stays `Incomplete`, and the banner text gains "run Verify on
  Admin Settings".
- **`user_sid` rows** are compared directly to the target's SID (`ProtectedPrincipalService.cs:1738-1744`); a deleted
  user's SID matches nothing and no failure branch exists. **`ou_guid` rows** are compared against the target's own
  ancestors (`Services/ProtectedOuMembership.cs:53`); same. Their `Missing` ignores are display-only.
- **`NotConverted` cannot be ignored** (F1, and decision 2026-11-03: an unconverted row fails closed until converted).
  Its actions are Fix (Convert or Replace) and Remove only.
- **`LabelDrift`, `WrongClass`, `Ambiguous`** ignores are display-only everywhere; `Unreadable` cannot be ignored.
- **Section access, servicer and module-admin SIDs** compare against token claims (`Authorization/GroupAuthorizationHandler.cs:75-129`,
  `Services/ModuleAdminService.cs:55-60`). A deleted group cannot be matched by a token issued after the deletion; a
  session whose claims were issued before it still carries the SID until revalidation. Ignores there are display-only.

### B.5 Labels that enforce (F2)

Two fallbacks compare a protected-principal label, not an identifier: a `user_sid` row against a target that reports no
SID is matched by its label through `MatchesIdentity` (`ProtectedPrincipalService.cs:1742`); in the directory-free
write-target check an `ou_guid` row is matched by its label's DN suffix (`:1809`). A `LabelDrift` Fix therefore changes
what those fallbacks match, in this build as well as on prod: from the stale name to the object's current canonical
name. That is the intended object, and the stale label is the defect; the plan says so in the exec summary rather than
claiming the rewrite is cosmetic.

## C. The page

### C.1 Button and results panel

A **Verify** button beside each pane heading, in the style of the validate-on-add buttons (`btn btn-outline-success`,
"Verifying..." while running, disabled while a save is pending). Progress through `Progress.Begin(... ActivitySize.Items(n))`.
Badges per row: `verified` (muted, time on hover), `missing` (danger), `name changed` (warning, stored vs current on
hover), `not converted` (warning, resolver's answer on hover), `several objects` (warning), `wrong type` (danger),
`could not verify` (secondary, reason), `ignored` (secondary, who/when/why). Summary line: "Verified N entries at <time>:
M findings, K ignored, J could not be verified." `StaleBadge` / `SweepExistingEntriesAsync` (`AdminSettings.razor:1103-1216`),
the sweep call in `OnAfterRenderAsync` (`:778-788`) and the "not converted" banner (`:120-129`) are replaced.

The last Verify per store (`{at, by, counts}`) is persisted in `app_setting` key `verify_last` (plain `Set`; display-only)
so another admin sees "Last verified <time> by <user>: ..." without pressing; findings are recomputed on press.

### C.2 Actions per finding and the write primitive each uses (F3)

| Finding | Fix | Remove | Ignore |
|---|---|---|---|
| `LabelDrift` A1 | **Rename**: new `ProtectedPrincipalRepository.RewriteLabel(kind, identifier, newLabel)` rewrites the identifier row's label and the legacy companion's value in one transaction | `RemoveWithCompanionAndIgnores` | display-only |
| `LabelDrift` A3 | `SectionAccessRepository.SaveDisplayNames` (`:303-324`) for that SID - the Access pane's save writes display names for newly added groups only (`ModuleConfig.razor:1821`, `:1855`), so this is a direct call, not the save path | pane's existing remove + ignore cleanup | display-only |
| `LabelDrift` A4 | same as A3 (module admins borrow section-access display names) | existing remove + cleanup | display-only |
| `Missing` A1 identifier rows | **Replace**: the row's picker; the chosen object replaces the row in place (same kind) via `ReplaceWithCompanionAndIgnores` | yes | `group_sid` only: enforcement per B.4; others display-only |
| `Missing` / `WrongClass` A3, A4 | Replace via the pane's picker and its save path | yes | display-only |
| `NotConverted` convertible A2 | **Convert**: `ConfigDbIdentifiers` is refactored so its per-row primitives (`ConvertProtectedRow`, `ConvertModuleAdminGrant`, from the whole-workset `Convert` at `Services/Storage/ConfigDbIdentifiers.cs:48-112`) are callable for one row; A2 uses `ConvertProtectedRow` | yes | **no** |
| `NotConverted` convertible A3 | `SectionAccessRepository.ConvertToSids` for that alias and value (`:364-440`) - section access is not covered by `ConfigDbIdentifiers` | yes | no |
| `NotConverted` convertible A4 | `ConvertModuleAdminGrant` | yes | no |
| `NotConverted` none / `Ambiguous` A2-A4 | Replace via picker | yes | no |
| `Ambiguous` A5 | edit the entry text (field focused) | remove that entry | display-only |
| `Missing` A5-A8 | edit the text; A6/A7 get the OU browser already on the page (`ModuleConfig.razor:380-451`) | A5: that entry; A6-A8: clear the value | display-only |
| `WrongClass` A1 / A8 | Replace via picker / edit the text | yes | display-only |
| `Unreadable` | - | yes | no |

Where an action goes through a pane's existing save path, the existing refusals, version checks and audit rows fire
(`AdminSettings.razor:1344-1591`; `ModuleConfig.razor:1471-2040`). Direct primitives (Rename, Convert, Replace on A1, the
display-name write) each check the store's snapshot version the item was read with and refuse on mismatch, the way the
protected-principal save does (`ProtectedPrincipalService.cs:1421`). Every action writes one audit row:
`AuditService.LogSettingsChange(user, ip, "Verify:<store>", before[], after[], extra{finding, observed, action})`
(`Services/AuditService.cs:415-441`); a press writes one row with the counts. Ignore writes the ignore entry and its
audit row and changes no stored rule.

### C.3 Text-setting panes (A5-A8)

`ModuleConfigField` gains `RecipientGroup`; `ExcludedADGroups` becomes `AdGroup` (list), `LockdownOuDn` keeps `OU`,
`DefaultSearchBase` and `ActiveDirectorySearchBase` become a new `SearchBase` type (any container, not OU-only - the
distinction A6 vs A7 requires), and the eight Conference Rooms group settings become `RecipientGroup`. The generic editor
(`ModuleConfig.razor:480-490`) gets one branch per type: text box, Verify badge, and for `OU`/`SearchBase` the OU browser.
Stored values are unchanged; module runtime is unchanged, so no module version bumps (Constitution: module-scoped
behaviour bumps; a shared editor change on an undeployed base does not). `docs/AdminModuleSpec.md` records the types.

## D. Help and docs (Known Failure Class 5; Constitution `docs/ProjectConstitution.md:132`)

Help moves with behaviour, in the slice that changes it (section E names each). Page notes and hover text for the
button, every badge and every action; README sections Protected Principals, Module Configuration (Access, Protection
servicer, Module Admins), Migration excluded groups, Emergency Disable, AD Attribute Editor, BitLocker Recovery,
Conference Rooms settings, Troubleshooting ("Protected-principal check was incomplete ... run Verify");
`docs/ConfigDbOperations.md` run-book: after `db identifier-status`, "press Verify on each page" as the post-deploy
check, and the ignores printout; `docs/AdminModuleSpec.md` for the field types.

## E. Slices (one Opus agent at a time, worktree each, merge on guard proof)

- **V1 - engine + Protected Principals pane.** B.1-B.3 for A1/A2 (adapter, batching, forest check, canonical labels,
  ignore list with compare-and-swap, atomic cleanup), C.1, C.2 rows for A1/A2 (Rename, Replace, Convert via the per-row
  primitives, Remove, Ignore), sweep removed, audit rows, `verify_last`. Help: Admin Settings notes/tips, README Protected
  Principals. Tests: every finding incl. user-without-UPN not drifting; batching; unreadable never verified or missing;
  foreign-forest DN unreadable; compare-and-swap refuses a stale list; remove+cleanup atomic; version mismatch refuses.
- **V2 - live-check effect.** B.4: ignore list read with the rules, `group_sid` resolution skipped for an ignored
  missing row, token path untouched, timing counter, banner text, `db identifier-status` printout, corrupt JSON = empty.
  Help: README Troubleshooting, run-book. Tests: ignored missing group stops refusing; restored group (same SID in token)
  protects again with the ignore present; non-ignored still incomplete and named; unreadable never skipped; a
  `NotConverted` row cannot be ignored; corrupt list ignores nothing.
- **V3 - Module Configuration groups.** A3/A4 on Access, Protection servicer, Module Admins: adapter, display-name
  rename via `SaveDisplayNames`, Convert via `ConvertToSids` (A3) and `ConvertModuleAdminGrant` (A4), Replace, Remove,
  Ignore (display-only). Help: pane notes, README Module Configuration. Tests per pane.
- **V4 - text settings.** C.3 types and editor branches; A5 via `ResolveExcludedGroup`, A6 via `Get-ADOrganizationalUnit`,
  A7 base read, A8 via the Conference Rooms session with not-found translation. Help: field tips, README for the five
  modules, `docs/AdminModuleSpec.md`. Tests: a GUID entry in A5 verifies; a container at A6's DN is missing; the same DN
  at A7 verifies; Exchange not-found is missing and another exception unreadable.

Each slice: full gates (`.agents/repo-guidance.md` Verification), non-vacuity proof per new test, finding record
`dov-v1..v4`, index row; the supervisor runs master gates after the last merge and logs costs.

## F. Risks

- **Protection reduced by Ignore** - one case (B.4), audited, badged, reversible; `Unreadable` and `NotConverted` are never
  ignorable; a restored group protects again without any action.
- **Fix rewrites enforced labels** (B.5) - disclosed; the new value is the protected object's current canonical name.
- **Ignore list as authorization state** - compare-and-swap and atomic cleanup (B.3); corrupt value ignores nothing.
- **An old-build save on prod wipes identifier rows** (plan K constraint) - Verify then reports every row `NotConverted`
  with Convert offered; the run-book already says to re-run the conversion.
- **Directory load** - batched reads under the warm pool; no unanchored walk.
- **Page line counts** - neither page has ClickGate line entries (ppi-s6 confirmed); re-check before each slice.

## G. Review rounds (codex-commercial, owner: up to three rounds to consensus)

- **Round 1** - 2026-11-04, head `389d8dda`, codex-commercial (`gpt-6-astra`, effort max, ChatGPT-subscription auth,
  read-only sandbox), openreview question. Verdict: **ACCEPTABLE WITH CHANGES.** Findings, all verified against the pinned
  head by the supervisor and adopted in this revision: F1 enforcement paths (B.4 rewritten; `NotConverted` not ignorable;
  exec summary corrected); F2 labels enforce in two fallbacks, canonical label is UPN then mail (B.2, B.5, exec summary);
  F3 label-only Fix and per-store conversion need named primitives (C.2); F4 ignore list concurrency (B.3); F5 per-setting
  lookup rules (A, B.2, C.3 `SearchBase` type); F6 forest scope and Exchange confirmed-not-found (B.1); F7 factual
  corrections (exec summary cost and sweep wording, B.4 claims-based matching, `appsettings` note, `ExtendedLogService`
  lines); help distributed per slice (D, E). Report saved by the supervisor at the dispatch; not committed.
