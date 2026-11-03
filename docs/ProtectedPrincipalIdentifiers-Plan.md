# Protected principals by immutable identifier - Plan

Status: **Approved 2026-11-03 (owner: "go"). In progress - S1 first.**
Owner: Michael
Date: 2026-11-03
Last verified against code: local `master` `c94b443e`, 2026-11-03. Every file:line cited below was read there; lines
move, so re-read before editing (Known Failure Class 4).
Versions (proposed): base app stays `2.36.0` (not yet on prod per `.agents/state.md:15`; it absorbs every shared change
since 2.35.0); ConferenceRooms `2.19.11` -> `2.20.0`; MailboxPermissions, CalendarPermissions and AdminSettings one
patch each (section H).
Authority: subordinate to `docs/ProjectConstitution.md` and `AGENTS.md`. A written plan is required: this changes the
shared protection service, an authorization store, a config-store data conversion and a module's write path.
Rulings implemented (`.agents/decisions.md`):
- 2026-11-03 "Thorough fixes by default; protected principals and module-admin grants by immutable identifier"
  (`decisions.md:8-32`). Verbatim: "do it correctly. assume I will ALWAYS say do it correctly, regardless of the work
  involved." And: "Protected principals are stored and compared by immutable identifier. Users and groups by SID, OUs by
  objectGUID; existing rows migrated by directory lookup, unresolvable ones surfaced on the admin page. A write target is
  identified by the SID Exchange reports for the mailbox and read from Active Directory by that SID; the address lookup
  stays only as the fallback when Exchange reports no SID, and the msExchMailboxGuid proof (aud-a2c, sweep item 6)
  retires with it." And item 8: "stored names are migrated to SIDs by directory lookup; a name that does not resolve to
  exactly one group is listed on the Module Configuration page for re-picking. Nothing the directory can resolve is
  re-picked by hand."
- 2026-11-03 "Conference Rooms runs no protected-principal check at all" (`decisions.md:34-46`): "protected principals
  does not apply to conference rooms." ... "`ConferenceRoomProtectionGate` and its call sites are removed. The
  Constitution's third module-scoped exemption is widened to match." Same entry: "ONE DECISION AT A TIME" - the open
  decisions in section J are asked one by one.
- 2026-10-30 near-instant requirement (`decisions.md:48-53`): "sub-second to low-single-digit seconds end to end,
  measured and logged. No unanchored domain-wide LDAP walk; one credential fetch per operation; a warm directory
  session."
- Mandate (2026-10-26/27 item 0): find and fix bugs; remove or significantly change no intended functionality.
Precedent mirrored: `docs/SectionAccessSidStorage-Plan.md` (store the SID, compare exactly, keep a display label beside
it that no decision reads, migrate by lookup, idempotent and re-runnable at startup, never block boot).

## Exec summary

*The only part of this plan you need to read. Everything after it is for the agent that builds it.*

**What it does.** Every protected user and group is stored by its security identifier, and every protected
organisational unit by its directory ID - values that never change when someone is renamed, moved or given a new
address. The name you see on the page stays, but only as a label; no decision reads it. When an operator acts on a
mailbox, the app asks Exchange which mailbox it is and which on-premises account sits behind it, by that same
identifier, and checks that account. It no longer matches by address, and the slow, fragile mailbox-ID cross-check added
in October goes. Conference Rooms stops checking protected principals altogether, as you ruled; it still checks that
the thing it writes is a room. Module-admin grants stored as names are converted to identifiers the same way.

**What you get.** A protected person stays protected through a rename, a new address or a move between OUs. Two
accounts that share an address can no longer be confused. A protected account in the other domain is found where today
it can be missed. Module admins whose grants stopped working when names were retired get them back automatically, and
only the ones the directory cannot pin down are listed for you to re-pick. Each check makes one directory read fewer.

**What it costs.** Six slices of work and their tests. No new screens. One new read per check (asking Exchange or Entra
for the identifier), offset by the read removed. Possibly one new permission: if Exchange does not report the on-premises
identifier, the app needs Entra read access to users, which would be a new app-wide setting (decision 2 below).

**The biggest risk.** The shared settings database is read by dev and prod at once, and prod will run the old build for
a while. The plan keeps every existing entry in the form the old build understands, beside the new one, so neither
build loses protection during the overlap. The second risk: if no source reports the identifier for synced mailboxes,
the address lookup carries on as the fallback, which is today's behaviour, not a new hole.

**Decisions for you, asked one at a time.** (1) An old entry the directory cannot resolve: keep enforcing it exactly as
today until it is re-picked (recommended), or stop enforcing it. (2) Only if the live check shows Exchange does not
report the identifier: add an app-wide Entra read setting. (3) Module-admin grants that come back to life on
conversion: convert them all (recommended, it is what you ruled) or hold them for you to confirm.

**Approving it authorises** the six slices below, a one-time conversion of the stored entries and module-admin grants
in the shared settings database (old entries kept), removing Conference Rooms' protection check and its servicing
option, and the matching changes to the Constitution, the repo guidance, the in-app help and the README. It does not
authorise deploying, or running anything against the live directory or database; the read-only checks in section I
are yours to run.

## A. Stored identifiers

### A.1 What is stored today

- `Services/ProtectedPrincipalEntryValidator.cs:165-182` `CanonicalValue`: Group and OU store the DN
  (`match.DistinguishedName`); GroupTarget stores `ProtectedGroupTargetEntry.Format(match.ObjectGuid!, DN)`
  (`objectGUID|DN`); everything else (Users) stores `match.UserPrincipalName ?? match.Email`.
- `Services/ProtectedPrincipalService.cs:47-62` `ProtectedPrincipalConfig`: `Users`, `Groups`, `OrganizationalUnits`,
  `SamAccountNamePatterns`, `GroupTargets` - exactly those five.
- Storage: `protected_principal (kind, value)` (`Services/Storage/ConfigStoreMigrator.cs:58-62`), kinds `user`,
  `group`, `ou`, `sam_pattern`, `group_target` (`Services/Storage/ProtectedPrincipalRepository.cs:15-25`). The reader
  switches on kind with no default arm, so an unknown kind is silently ignored (`ProtectedPrincipalRepository.cs:224-233`).
  Every save is a whole-store replace: `DELETE FROM protected_principal;` then re-insert (`:81`, `:122`).
- Group entries already accept a SID: `ProtectedGroupEntry.Parse` recognises `Sid`, `DistinguishedName`,
  `DomainQualified` and bare `Name` (`Services/ProtectedGroupMembership.cs:5-52`), resolves each to one
  `DirectoryGroup` (`:281`, `Resolve` at `:508`) and matches by SID against the target's `tokenGroups`
  (`:292`). So groups are already COMPARED by SID; what is stored is a DN that must be re-resolved on every check.
- Users are compared by string: `CheckDirectUserMatches`/`MatchesIdentity` (`ProtectedPrincipalService.cs:1471-1507`)
  compare the stored value case-insensitively against the target's UPN, primary SMTP, sAMAccountName, DN, objectGUID,
  Entra object id, and `DOMAIN\sam` by its second half only. OUs by DN suffix (`:1527-1538`). Patterns by wildcard on
  sAMAccountName (`:1509-1525`).
- `ADSearchResult` (`Services/ADDirectorySearchService.cs:990-1000`) already has `ObjectSid` and `ObjectGuid` fields,
  but `ValidationProperties` (`:534-542`) requests `ObjectGUID` for Group only and `objectSid` for no kind, and
  `ExecuteValidateExists` populates only `ObjectGuid` (`:521-531`). Users and OUs are validated in the local domain only;
  only DN-shaped Group input is routed to its own domain (`:483-497`).

### A.2 What is stored after

New kinds in the same table, value format `<identifier>|<label>`, split on the FIRST `|` (neither a SID nor a GUID
contains one):

| kind        | identifier                         | label                                          |
|-------------|------------------------------------|------------------------------------------------|
| `user_sid`  | the user's objectSid, canonical    | the value an older build would store (UPN/mail) |
| `group_sid` | the group's objectSid, canonical   | the group's DN                                  |
| `ou_guid`   | the OU's objectGUID, "D" form      | the OU's DN                                     |

`sam_pattern` unchanged (patterns are not objects). `group_target` unchanged (already `objectGUID|DN`).

**One format for all kinds, and why this one.** `identifier|label` is the `group_target` precedent
(`ProtectedGroupTargetEntry.Format`), so there is one parser for every identifier kind, one rule ("the label is never
read by a decision") and the page renders all kinds the same way. A separate label column (the section-access v6
precedent) would need a DDL step; the in-value form needs none (section B.1 explains why that matters here).

**Why the label is "the value an older build would store".** Section B.1: during the mixed-version window every
identifier row has a companion legacy row, and they are paired by `label == legacy value`. For Groups and OUs that is the
DN; for Users it is the UPN-or-mail `CanonicalValue` produces today.

`ProtectedPrincipalConfig` gains `UserIds`, `GroupIds`, `OuIds` (`string[]` of `id|label`). The legacy arrays stay and
carry (a) companions and (b) unmigrated rows; section B says how each is treated.

### A.3 Entry validation

`ProtectedPrincipalEntryValidator.Decide` and `CanonicalValue` return the identifier row (and the companion):

- `ValidationProperties` adds `objectSid` and `ObjectGUID` for User and Group, `ObjectGUID` for OU;
  `ExecuteValidateExists` fills `ObjectSid` (text form; `objectSid` arrives as a `SecurityIdentifier` in-process - reuse
  the `SidText` conversion `Services/AccountLockoutRemediationService.cs:820-830` documents).
- Refuse a Found match with no identifier ("found, but its directory identifier could not be read"), as GroupTarget
  already does (`ProtectedPrincipalEntryValidator.cs:95-118`).
- **Bug fixed in passing:** `result.Ambiguous` is refused only for GroupTarget (`:95`). A User, Group or OU input that
  matches two objects is accepted today and stores whichever AD returned first. With an identifier stored that would
  silently pin the wrong object, so ambiguity is refused for every kind.
- **Bug fixed in passing:** Users and OUs are validated in the local domain only (`ADDirectorySearchService.cs:483-497`),
  so a protected user in another forest domain cannot be entered by UPN unless it happens to be in the local domain.
  User lookups go to the global catalog (UPN, mail, sAMAccountName are in the partial attribute set) with the
  exactly-one rule; DN-shaped OU input is routed to the DN's own domain as Group already is. The live-proven contract
  that a bogus-domain DN answers NotFound (`:487-491`, `ADDirectoryLiveTests`) is kept by routing only DNs whose domain
  the forest's crossRefs know, and answering NotFound for the rest.
- Well-known SIDs (`S-1-1-0`, `S-1-5-32-*`, and the rest `SectionAccessGroupIdentity` refuses) are refused as Group
  entries, as section access does.

## B. Migration

### B.1 The shared database and the old build

Dev and prod open the same database (repo-guidance Invariant 2; decision 2026-09-04), and prod will run a build without
this plan for some time after dev has it. Three facts read from the code decide the design:

1. The old reader ignores kinds it does not know (`ProtectedPrincipalRepository.cs:224-233`), so new kinds are
   invisible to it.
2. The old writer replaces the whole table (`:81`, `:122`), so an old-build save deletes every new-kind row.
3. The old matcher would mis-read an `id|label` value placed in a legacy kind: a Group value containing `=` parses as a
   DN (`ProtectedGroupMembership.cs:45-46`), fails its read and makes every check incomplete; a User value would match
   nothing.

Therefore: **no legacy row is ever rewritten in place.** A converted entry is a NEW `*_sid`/`*_guid` row added beside
its legacy row, which stays as its companion. The old build keeps reading, enforcing and saving the legacy rows exactly
as today. An old-build save wipes the identifier rows; the next migration pass (B.3) re-creates them from the surviving
legacy rows. A new-build add writes both rows; a new-build remove deletes both. No DDL, so no schema step, and
`RequiredSchema` (`ConfigStoreMigrator.cs:152-162`) is unchanged. Retiring the companions is out of scope; they cost
nothing and keep a rollback to the old build safe.

### B.2 How the new build reads a mixed store

- An identifier row is authoritative for its object.
- A legacy row whose value equals the label of an identifier row of the same base kind is a companion: not evaluated,
  not shown as its own entry.
- A legacy row with no identifier row is **unmigrated**. Open decision D1 (section J) decides how it is enforced. The
  recommendation: **enforce it exactly as today** - the legacy matcher for Users and OUs, `ProtectedGroupEntry.Parse`
  for Groups - until it is converted, re-picked or removed, and flag it on the page.

  Why not "inert", as the brief proposed: today an unresolvable Group row is not inert, it makes the group check
  incomplete (`ProtectedGroupMembership.cs:281-288`), and an incomplete check with no match refuses the write
  (`ProtectedPrincipalService.cs:1576-1582`) - fail closed. A User row that no longer resolves still string-matches a
  target carrying that address. Making unmigrated rows inert would turn both into an allow, during exactly the window
  (an old-build save, a directory blip at startup) in which every row is unmigrated. That would remove protection,
  which the mandate forbids, and violate Known Failure Class 3. Keeping today's behaviour means the conversion can only
  ever narrow a match from "any object with this string" to "this object", never drop one.

### B.3 When it runs

At startup, from `Program.cs` beside the section-access pass (`Program.cs:294` registration, `:421` invocation), as a
singleton `ProtectedPrincipalIdentifierMigration` modelled on `Services/SectionAccessSidMigration.cs` (status enum, never
blocks boot, never half-writes, re-checks every row under the write lock and converts only rows still as read -
aud-a33b). Plus a **Resolve now** button on the Protected Principals pane of Admin Settings that runs the same pass on
demand, so an admin need not restart after an old-build save or a directory outage.

Why not on first admin-page open: the page would then do forest I/O on load, and enforcement of the rows would depend
on whether an admin happened to open a page. Why startup is safe: unmigrated rows are enforced as today (B.2), so a pass
that cannot run changes nothing.

### B.4 The pass

1. Read every `user`, `group`, `ou` row with no identifier companion. Nothing to do -> `AlreadyMigrated`.
2. One `DirectoryReadOperation.Begin()` around the pass: **one** directory-read credential fetch (kd-88), one warm
   session from `DirectorySessions`.
3. Resolve each row ONCE, no lock held:
   - User (UPN or mail): global-catalog `Get-ADObject -LDAPFilter "(&(objectCategory=person)(objectClass=user)(|(userPrincipalName=v)(mail=v)))"`
     with `objectSid`, `objectGUID`, DN. Exactly one -> `user_sid` row.
   - Group: `ProtectedGroupEntry.Parse(value)` and the existing `Resolve` (`ProtectedGroupMembership.cs:508`) - DN read
     in its own domain, `DOMAIN\Name` in that domain via `ResolveDomainServer` (`PowerShellProtectedGroupDirectory.cs:62`),
     a bare name forest-wide with the exactly-one rule. Exactly one -> `group_sid` row (label = the legacy value).
   - OU (DN): base read of that DN in the DN's own domain for `objectGUID`. Exactly one -> `ou_guid` row.
   - Zero, two or more, a well-known SID, or a lookup that could not run: no row written; the row is recorded with its
     reason (`not found`, `matches N objects`, `well-known SID refused`, `directory unavailable`).
4. Under the write lock, insert the identifier rows whose legacy row is still present and unchanged; skip and log the
   rest (`SkippedChangedRows`). One transaction.
5. Audit: one audit event per pass (actor `system:protected-principal-migration`, or the admin for **Resolve now**),
   listing each conversion `kind: legacy value -> identifier` and each flagged row with its reason. The application log
   carries the same lines.
6. Idempotent: a converted row has a companion and is skipped next time. A failed pass leaves the store untouched.

The flagged list is persisted for the page as an `app_setting` key (JSON: row, reason, time of pass) - an existing table,
no DDL. It is display-only.

### B.5 Admin page

The Protected Principals pane (Admin Settings) shows identifier rows by label, with the identifier in the hover. Each
unmigrated row gets a badge **"not converted"** with its reason, and the pane opens with a warning naming the count and
saying what it means: "These entries are still matched by name, as before. Re-pick each from the search, or press
Resolve now." The existing **not in AD** badge (`Components/Pages/AdminSettings.razor:957-960`) says "it protects
nothing", which is false for a Group row (it fails every check closed); its wording is corrected in the same slice
(Known Failure Class 5).

### B.6 Module-admin grants

- Today: the picker stores a SID (`Components/Pages/ModuleConfig.razor:650` `ReturnValueKind="SID"`; `AddModuleAdmin`
  refuses anything else, `:2288-2309`). `IsModuleAdmin` filters to usable SIDs, so a legacy name grant has authorised
  nothing since aud-a1 (`Services/ModuleAdminService.cs:30-58`, owner ruling 2026-10-19). `GetUnusableGrants` /
  `TryGetUnusableGrants` (`:60-108`) build the inventory and have no production caller.
- After: a `ModuleAdminSidMigration` pass at startup (and a **Resolve now** on the Module Configuration Admins pane).
  Each name is resolved the way section access resolved its names (`docs/SectionAccessSidStorage-Plan.md` Migration
  step 2): `DOMAIN\name` in that domain; a bare name forest-wide; match `sAMAccountName`, `cn`, `name` AND `displayName`
  (the old picker stored either the sAMAccountName or the display name); `-LDAPFilter`, never `-Filter`; groups only;
  well-known SIDs refused. Exactly one group -> the row is replaced by the SID in one transaction (here in-place is safe:
  every build since aud-a1 ignores the name, and every build before it accepts a SID through `IsInRole`). Zero or many ->
  left as is (it is already inert) and listed on the Admins pane through `TryGetUnusableGrants`, which gets its first
  production caller, with the reason. Same credential and concurrency rules as section access's pass; one fetch.
- Consequence to state plainly: a converted grant **starts authorising again**. That is what the ruling orders
  ("Nothing the directory can resolve is re-picked by hand"); D3 in section J asks only whether the owner wants the list
  before it takes effect.

## C. Target binding by SID

### C.1 What happens today

`ResolveExchangeTargetAsync` (`ProtectedPrincipalService.cs:716-784`) asks Exchange (`AskExchangeAsync`, `:1023-1055`,
`Get-Recipient`), then AD by the typed string (`ResolveWithStatusAsync`, `:524-575` -> `ResolveViaActiveDirectory`,
`:1086-1124`: `Get-ADUser` with `(|(userPrincipalName=)(mail=)(sAMAccountName=))`, **local domain only**, no
`-Server`), re-reads AD by the canonical address when it differs, requires the two AD answers to agree by objectGUID
(`IsSameDirectoryObject`, `:1008-1015`), and then proves mailbox ownership: `ProveMailboxBackingAsync` (`:792-816`)
-> `FindMailboxGuidHoldersAsync` (`:882-929`, a `msExchMailboxGuid` search) -> `SelectMailboxBacking` (`:838-868`).
Callers: `Services/ConferenceRoomProtectionGate.cs:165` and `Services/PermissionValidator.cs:378` (Mailbox and Calendar
Permissions' full-resolution path). `ResolvedRecipient` (`Services/IIdentityResolver.cs:30-36`) carries no SID;
`MapRecipient` (`Services/ExchangeIdentityResolver.cs:82-114`) reads PrimarySmtpAddress, ExternalDirectoryObjectId,
RecipientType(Details), IsDirSynced, ExchangeGuid from `Get-Recipient` (`:35-37`).

### C.2 What happens after

`ResolvedRecipient` gains `OnPremisesSid` (nullable). `ResolvedDirectoryPrincipal`
(`ProtectedPrincipalService.cs:18-27`) gains `ObjectSid`. A new `ResolveExchangeTargetAsync` body:

1. Ask Exchange (unchanged). Not answered -> Unavailable. No recipient -> AD's reading of the string as today (an
   Exchange write cannot land anywhere; unchanged branch `:727-731`).
2. Cloud-only recipient (`ExistsOnPrem == false`): unchanged branch `:733-745` (AD object under the same string ->
   Ambiguous; else `CloudOnlyPrincipal`).
3. Synced recipient: obtain its on-premises SID from the first source the live check (section I) shows populated, in
   this order:
   1. Exchange Online `Get-User -Identity <ExternalDirectoryObjectId>` `Sid`. **UNVERIFIED**: EXO exposes a `Sid` on
      `Get-User`, but it may be a cloud-generated SID rather than the on-premises objectSid. The live check compares it
      with AD's objectSid for the same person; anything but an exact match rules this source out.
   2. Microsoft Graph `GET /users/{ExternalDirectoryObjectId}?$select=onPremisesSecurityIdentifier,onPremisesSyncEnabled`
      (documented). The app reads `/users` in several module services through `GraphTokenClient.GetWithStatusAsync`
      (`Services/GraphTokenClient.cs:32`; e.g. `Services/EmergencyDisableService.cs:529`,
      `Services/CloudPasswordResetService.cs:324`), but each with its own module's app registration; there is no
      app-wide Graph credential for a shared service. Using this source needs one (`User.Read.All`) - decision D2.
   3. On-premises Exchange `Get-User <identity>` `Sid` where an on-premises connection exists
      (`Services/ExchangeServiceBase.cs:1004` already reads `Get-RemoteMailbox` there). This reads AD through Exchange,
      so it is a second opinion only where the first two are absent.
   Not considered: Entra `onPremisesImmutableId`. It maps to objectGUID or `ms-DS-ConsistencyGuid` depending on how
   the sync was configured - an environment fact, so a safety argument on it would breach Invariant 7.
4. With a SID: **one** indexed read at the global catalog, `Get-ADObject -Server <gc:3268> -LDAPFilter
   "(objectSid=<sid>)"` (`ADDirectorySearchService.BuildSidFilter`, `:410-411`), properties objectSid, objectGUID, DN,
   sAMAccountName, userPrincipalName, mail, objectClass. `objectSid` is indexed and in the partial attribute set, so this
   is an equality lookup, not a walk. The GC host comes from `PowerShellProtectedGroupDirectory.GlobalCatalog()`
   (`:180-210`, discovered from the host's own forest, cached per session); `IProtectedGroupDirectory.FindObjectDnsBySid`
   (`ProtectedGroupMembership.cs:160-164`) is the existing shape. The owning domain for every later read comes from the
   returned DN (`GroupManagementService.ServerFromDn`, as `Evaluate` does at `ProtectedGroupMembership.cs:249`).
   Exactly one object of class user (rooms and equipment are user objects) -> Resolved, with `ObjectSid`,
   `ObjectGuid`, DN, the recipient's `ExchangeGuid`. Zero -> Unavailable ("Exchange names an on-premises account the
   forest does not hold"). Two or more -> Ambiguous. Not a user -> Ambiguous.
   Then the typed string is checked against it as aud-a2b requires: if AD's reading of the typed string (the existing
   `ResolveWithStatusAsync` call, `:723`) is a different object by objectGUID -> Ambiguous (unchanged rule).
5. Synced, and no source reports a SID: the address path - the current canonical-address resolution with its one-match
   rule (`:747-773`) - **without** the mailbox-GUID proof. This is the ruling's fallback. It logs
   "no on-premises SID reported for {Identity}; bound by address" so its frequency is visible.
6. A source that could not be asked (throttle, error, no credential) is Unavailable, never "no SID". Only an answered
   read with an empty SID falls to step 5.

`ResolveWithExchangeFallbackAsync` (`:609-702`, AD-first, 13 callers) is not re-ordered; its AD read
(`ResolveViaActiveDirectory`) adds `objectSid` to the properties so its principals carry the SID and match `user_sid`
rows. Its canonical re-resolve for an alias (`:647`) uses step 3-4 when a SID is available.

**Comparison** (`CheckAsync`): `user_sid` rows compare `target.ObjectSid` exactly (canonical `SecurityIdentifier`
values, ordinal); `group_sid` rows are passed to `ProtectedGroupMembership.Evaluate` as SIDs (the `Sid` entry kind it
already supports), so an entry the target's token carries matches with **no** per-entry resolve read, and only entries
needing the domain-local or universal path (`:296-372`) are resolved; `ou_guid` rows are matched by one read of the
target's ancestor containers by DN (`(|(distinguishedName=<parent1>)...)`, `ProtectedGroupMembership.AnyDnFilter`
`:624`) in the target's domain, comparing their objectGUIDs with the stored ones - one read however many OUs are
protected, and a moved OU still matches. Patterns unchanged. Unmigrated legacy rows: as B.2.

**Cloud-only targets.** Entry validation refuses cloud-only objects ("cloud-only objects cannot be protected",
`ProtectedPrincipalEntryValidator.cs:88-90`), so every identifier row names an on-premises object. A cloud-only target
has no on-premises SID, no DN and no sAMAccountName: `user_sid`, `group_sid` (a cloud-only account cannot be a member of
a synced group), `ou_guid` and pattern rules cannot match it by construction, and are reported as not applicable in the
log line `CloudOnlyPrincipal` already writes (`:1061-1083`). Unmigrated legacy User rows still string-match it as
today. `DirectoryObjectGuid` is null (unchanged).

**Retired:** `ProveMailboxBackingAsync`, `SelectMailboxBacking`, `FindMailboxGuidHoldersAsync`,
`FindMailboxGuidHoldersViaActiveDirectory`, `ParseMailboxGuid`, `BuildMailboxGuidFilter`, `MailboxGuidHolder`
(`ProtectedPrincipalService.cs:29-34`, `:786-1000`), their tests, and the aud-a2c refusal texts ("no Active Directory
object holds this mailbox GUID ...", `:844-846`). `ConferenceRoomProtectionGate.AmbiguousMessage`'s remark about
msExchMailboxGuid goes with the gate (D). **Kept:** aud-a2d - writes bound to `ValidatedMailbox`
(`Services/ValidatedMailbox.cs`); `From(ResolvedDirectoryPrincipal)` now carries the objectGUID of the object found by
SID; its doc comment's aud-a2c wording is rewritten.

## D. Conference Rooms

### D.1 What the gate does today

`Services/ConferenceRoomProtectionGate.cs` (225 lines): `EvaluateAsync` (`:153-224`) opens one check
(`BeginCheck`, `:164`), calls `ResolveExchangeTargetAsync` (`:165`), refuses Unavailable/Ambiguous (`:166-172`), runs
`CheckAsync` (`:175`) and the servicer grant (`:185-197`), then binds `ValidatedMailbox.From(resolved)` (`:207`) and
refuses when there is none (`:215-219`). So it does two jobs: the verdict (to go) and binding the room's Exchange GUID
and AD objectGUID (to stay). The AD writes refuse without the objectGUID (`Services/ConferenceRoomService.cs:1774`,
`:1890`, `:2085`, `:2199`) and trace it (`:701`, `:923`). The gate does **not** check the recipient type; that check is
`ValidateRoomRecipientAsync` after the bind (`ConferenceRoomService.cs:481-503`, RoomMailbox / RemoteRoomMailbox).
Call sites: `Components/Pages/ConferenceRooms.razor:17` (inject), `:1084`, `:1485`;
`Services/Jobs/ConferenceRoomBulkProcessor.cs:27`, `:49`, `:146`; `Program.cs:234`.

### D.2 After

`ConferenceRoomBinder` replaces the gate, keeping its check-then-run shape so the write is still unreachable on a
refusal (Known Failure Class 1): `BindThenRunAsync(identity, onRefused, onBound)`. It:

1. Calls `IIdentityResolver.ResolveRecipientAsync(identity)` once. Throws -> "Exchange Online could not be asked.
   Nothing was written." No recipient -> "Room '{x}' not found."
2. Refuses a RecipientTypeDetails other than RoomMailbox / RemoteRoomMailbox, with the wording
   `ValidateRoomRecipientAsync` uses (that later read-back check stays). No ExchangeGuid -> the existing no-room refusal.
3. Synced: binds the AD object through the shared SID binder from C.2 steps 3-6 (one directory-read credential fetch,
   only for this lookup), and checks the typed string does not name a different AD object (aud-a2b, a correctness rule:
   the writes hand Exchange a GUID and AD an objectGUID, which must be the same room). Zero / many / unavailable ->
   refused with the existing `AmbiguousMessage`-style text, minus the protection wording.
4. Cloud-only: `ValidatedMailbox` with no objectGUID, exactly as today.
5. No `CheckAsync`, no servicer evaluation, no `ProtectedPrincipalServicerService` dependency, no rules read.

Why the directory-read credential and not the module's own AD credential (`ConfigFields` `DelineaSecretId`,
`Modules/ModuleCatalog.cs:1197`): that one is a write credential for Set-ADUser; a read should run under the read
identity. If the directory-read credential is not configured, synced rooms are refused as they are today.

Removed with the gate: the `user` parameter and `CurrentUserAsync()` at both page call sites; the
`protectedPrincipalServiced` audit extra (`ConferenceRooms.razor:1786-1821`); the bulk processor's servicer comment
(`ConferenceRoomBulkProcessor.cs:132-145`); `ProtectionDenial`/`ProtectionOutcome` (check `grep` first - nothing else
used them on `c94b443e`); `"ConferenceRooms"` from `ModulesWithProtectedPrincipalServicing`
(`Components/Pages/ModuleConfig.razor:723-730`), whose own remark says a module that runs no check must be absent. Any
servicer group already configured for ConferenceRooms becomes dead config: left in the store, not shown, logged once at
startup.

### D.3 Docs, help, tests

- `docs/ProjectConstitution.md:89` third scoped-exception bullet: rewritten to the 2026-11-03 ruling - Conference Rooms
  runs no protected-principal check of any kind (room mailbox, room-list group, members); scoped to that module.
- `.agents/repo-guidance.md` Known Failure Class 3, third bullet: same rewrite.
- `Modules/ModuleCatalog.cs:1098` ConferenceRooms `Version` `2.19.11` -> `2.20.0` with a note.
- `Components/Pages/ConferenceRooms.razor`: the comments naming the gate (`:1053-1054`, `:1080`, `:1296`, `:1482`,
  `:1640`); no operator-visible help text mentions protection (grep on `c94b443e`: only comments and the inject).
- Tests: delete `ConferenceRoomProtectionGateTests.cs` and `ConferenceRoomServicerTests.cs`; add
  `ConferenceRoomBinderTests.cs`; re-point `ProtectionCheckCallSiteTests.cs:50-52` S7 (remove it - Conference Rooms is no
  longer a protection call site - and add an assertion that `ConferenceRoomBinder` calls neither `CheckAsync` nor
  `BeginCheck`); `ConferenceRoomAmbiguityMessageTests.cs:86-143` moves to the binder's message; constructors in
  `ConferenceRoomBulkCancellationTests.cs:257-264`, `ConferenceRoomBulkProcessorTests.cs:148`,
  `ConferenceRoomBulkTicketTests.cs:182`, `ConferenceRoomCancellationTests.cs:582-589`,
  `ConferenceRoomDirectoryAuditTests.cs:32`, `:474-481`; `ProgressRegistry.cs:1072`, `:1087`, `:1105`, `:1122` anchor
  `ProtectionGate.GuardThenRunAsync(` and must follow the rename. The page edit moves lines: re-anchor every
  `ClickGateRegistry` entry below it with `.agents/playbooks/clickgate-reanchor.md`.

## E. Performance

Per check on a synced mailbox, `ResolveExchangeTargetAsync` + `CheckAsync`, typed string = primary address:

| step                                   | before (`c94b443e`)                     | after                                     |
|----------------------------------------|-----------------------------------------|-------------------------------------------|
| Exchange `Get-Recipient`               | 1                                       | 1                                         |
| SID source (EXO / Graph / on-prem)     | -                                       | 1 (not a directory read)                  |
| AD read of the typed string            | 1 (local domain)                        | 1 (unchanged, the aud-a2b agreement)      |
| AD read by canonical address           | 0-1 (alias only)                        | 0                                         |
| AD object by SID at the GC             | -                                       | 1 (indexed equality)                      |
| `msExchMailboxGuid` holders search     | 1                                       | 0                                         |
| group check: target + tokenGroups      | as kd-88                                | as kd-88                                  |
| group check: resolve each protected group | 1 per entry                          | 0 for entries the token carries; 1 for the rest |
| OU check                               | 0 (string suffix)                       | 1 (ancestors by DN, all OUs at once)      |
| credential fetches                     | 1                                       | 1                                         |

Conference Rooms after: `Get-Recipient`, one SID-source read, one GC read, one typed-string read; one credential fetch;
no rules read and no group check, so it is faster than today's gate by the whole group check.

The timing line (`Protected-principal check for {Target} took {ElapsedMs} ms ({Reads} directory reads, {Fetches}
credential fetches)`, `ProtectedPrincipalService.cs:389-394`) will show `Fetches` = 1 and `Reads` down by one for a
typical check, more when several protected groups are in the target's token. The SID-source read is Exchange or Graph,
not a directory read; it gets its own elapsed figure in the same line (`sid source {Source} {SidMs} ms`) so a slow source
is visible. Conference Rooms no longer emits this line; its binder logs `Room binding for {Target} took {ElapsedMs} ms`.
The ruling-5 target is measured on dev after S3 and S4 and recorded in the slice's record.

## F. Help and docs (Known Failure Class 5)

Folded into the slice that changes the behaviour, not a separate slice:
- S1/S2: the Protected Principals pane's hover tips and page notes (what is stored, what "not converted" and
  "not in AD" mean, **Resolve now**); `README.md` section "Protected Users / Excluded Users" (`:1074`) and the
  Directory Read Credential pointer (`:644`).
- S3: README `:583` ("re-resolved ... with an immutable-GUID re-check") rewritten to the SID binding.
- S4: README "Protected principal servicing" (`:1090-1106`). It already disagrees with the code - it says servicing is
  offered for "Blocked Senders alone" (`:1098-1099`) while `ModuleConfig.razor:726-729` lists seventeen modules. S4 fixes
  the list to match the code without Conference Rooms.
- S5: the Module Configuration Admins pane's help (what a listed unresolved grant means, **Resolve now**).

## G. Slices

One finding per commit; each slice independently mergeable onto `master` and leaves every build on the shared database
correct. Verification for each: `dotnet build ExchangeAdminWeb.slnx -c Release`, `dotnet test ExchangeAdminWeb.slnx`,
`dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`, `git diff --check HEAD`. Every new test is proven
non-vacuous: revert the fix, watch it fail, restore, watch everything pass - recorded in the commit body.

**S1 - identifier storage, validator, ADSearchResult.** Kinds `user_sid`, `group_sid`, `ou_guid` and the shared
`ProtectedPrincipalIdEntry.Parse/Format`; repository read/write of both rows; `ProtectedPrincipalConfig` arrays; the
validator changes in A.3 (identifier required, ambiguity refused for every kind, GC user lookup, OU routing); companion
pairing (B.2); comparison of identifier rows (C.2 Comparison) with `ResolvedDirectoryPrincipal.ObjectSid` filled by
`ResolveViaActiveDirectory`. Page: new entries stored as identifier + companion; help per F. Tests: entry format
round-trip; validator refuses ambiguous User/Group/OU (non-vacuous: restore the GroupTarget-only condition, the test
fails); `user_sid` row matches the target by SID and not by a shared address (revert to string compare -> fails);
`group_sid` entry the token carries matches with zero resolve reads (fake directory counts); `ou_guid` matches after the
OU is renamed (fake directory); an old-format reader (the `c94b443e` switch) ignores the new kinds - a tripwire that
fails if a new kind is ever written into a legacy kind; remove-deletes-both.

**S2 - migration pass and admin-page flagging.** `ProtectedPrincipalIdentifierMigration`, startup wiring, **Resolve
now**, flagged-row list, audit event, "not converted" badge and corrected "not in AD" wording. Tests: each kind converts
on exactly one match; zero/many/unavailable leave the row and record the reason; unmigrated rows enforced as today (a
legacy Group row that cannot resolve still fails the check closed - non-vacuous: make it inert, the test fails);
concurrent change skipped (aud-a33b shape); second pass is a no-op; one credential fetch for N rows; an old-build
whole-store save followed by a pass restores the identifier rows.

**S3 - SID binder; retire the GUID proof.** `ResolvedRecipient.OnPremisesSid`, the source chosen by section I, the GC
read, the new `ResolveExchangeTargetAsync` body (C.2), `ResolveWithExchangeFallbackAsync` alias branch, removal of the
aud-a2c code and tests, `ValidatedMailbox` comment. Written only after the owner has run section I. Tests: synced
recipient binds the object whose SID Exchange reports even when another account carries its address in `mail`/UPN (the
aud-a2c scenario, now closed by construction - non-vacuous: bind by address, the test fails); zero/two objects for the
SID refuse; source unanswered -> Unavailable, not the address fallback; answered-empty SID -> address fallback with its
one-match rule; cloud-only unchanged; typed string naming a different object -> Ambiguous; a target in another domain
resolves (GC); timing line shows one fetch.

**S4 - Conference Rooms gate removal.** D.2 and D.3 in full; Constitution and repo-guidance bullets; servicer list;
README servicing section; ConferenceRooms `2.20.0`. Depends on S3 for the SID binder. Tests: binder refuses a non-room,
an unknown address, an Exchange failure; binds the room by SID; never calls `CheckAsync`/`BeginCheck` (a protected room
in the rules is written - non-vacuous: call the old gate, the test fails); bulk row the same; ClickGate re-anchor.

**S5 - module-admin migration and listing.** B.6. Tests: `DOMAIN\name` resolves only in its domain; a display-name
grant resolves; zero/many listed with reason through `TryGetUnusableGrants`; well-known SID refused; a converted grant
authorises (`IsModuleAdmin`) - non-vacuous: skip the conversion, the test fails; one credential fetch; idempotent.

**S6 - versions and records.** Not a code slice: the module version bumps land in the slice that changes the module
(section H); S6 is the closing record - plan status, `.agents/state.md`, token log, the dev timing figures.

## H. Versions

- Base app: `2.36.0`, unchanged - shared services change, and 2.36.0 is not yet on prod (`.agents/state.md:15`) and
  absorbs every shared change since 2.35.0, per the Constitution's versioning rule as the state file applies it.
- ConferenceRooms `2.19.11` -> `2.20.0` (S4): the module stops consulting protected principals and its servicing
  option.
- MailboxPermissions (`Modules/ModuleCatalog.cs:152`) and CalendarPermissions (`:225`) one patch each in S3: their
  full-resolution path (`PermissionValidator.cs:378`) now binds by SID, a mailbox in another domain resolves, and the
  aud-a2c refusals no longer appear. The S3 implementer greps for any other module reaching
  `ResolveExchangeTargetAsync` and bumps it the same way.
- AdminSettings (`:1713`) one patch in S2 (the Protected Principals pane), and the Module Configuration page's owning
  module one patch in S5.

## I. Risks and open checks

### I.1 Live read-only checks the owner runs before S3 is written

Pick (a) a synced user mailbox, (b) a synced room mailbox, (c) a cloud-only mailbox. For each, in Exchange Online
PowerShell:

```
Get-Recipient <address> | Format-List PrimarySmtpAddress,RecipientTypeDetails,IsDirSynced,ExternalDirectoryObjectId,ExchangeGuid
Get-User -Identity <ExternalDirectoryObjectId> | Format-List Name,Sid,SidHistory,IsDirSynced,RecipientTypeDetails
```

For (a) and (b), in Active Directory PowerShell on a domain-joined host:

```
Get-ADUser -LDAPFilter "(mail=<address>)" -Server <the account's domain> -Properties objectSid,objectGUID,msExchMailboxGuid |
    Format-List DistinguishedName,objectSid,objectGUID,msExchMailboxGuid
```

For all three, Graph (Graph Explorer, or `Connect-MgGraph -Scopes User.Read.All`):

```
GET https://graph.microsoft.com/v1.0/users/<ExternalDirectoryObjectId>?$select=onPremisesSecurityIdentifier,onPremisesSyncEnabled,onPremisesImmutableId
```

and, where on-premises Exchange is reachable, in its management shell: `Get-User <address> | Format-List Sid`.

What the answers decide: EXO `Sid` equal to AD `objectSid` for (a) and (b) -> source 1, no new permission. Otherwise
Graph's `onPremisesSecurityIdentifier` equal to AD's -> source 2 and decision D2. (c) must show no on-premises SID in any
source; if one does, stop and report. Record the results (values redacted to their domain part) in the S3 record.

### I.2 Legacy row counts (owner runs; the agent does not touch the shared database)

```
sqlite3 -readonly "<ConfigStore:Path>" "SELECT kind, COUNT(*) FROM protected_principal GROUP BY kind;"
sqlite3 -readonly "<ConfigStore:Path>" "SELECT module_id, admin_group FROM module_admins WHERE admin_group NOT LIKE 'S-1-%';"
```

### I.3 Risks

- **Mixed-version window.** Addressed by B.1-B.2: no legacy row changes, unmigrated rows enforced as today. Residual:
  an entry added on prod's old build is enforced on dev by name until the next pass converts it - today's behaviour.
- **No SID source populated.** Then every synced target takes the address fallback (C.2 step 5), which is today's
  resolution minus the mailbox-GUID proof - the ruling's stated trade. The log line makes its frequency visible.
- **EXO `Sid` looks like a SID but is not the on-premises one.** Section I.1 compares exactly; the implementation also
  refuses (Unavailable) when the GC holds no object for the SID, so a cloud-generated SID fails closed, not open.
- **Module-admin grants come back to life** (B.6, D3). The pass's audit event lists each.
- **Graph source** adds an app-wide credential and an outward dependency (D2); not taken unless I.1 requires it.
- **Group check depends on the token** as kd-88 made it; unchanged here.

## J. Open decisions (ask one at a time, per decisions.md 2026-11-03)

D1 and D3 are settled by the 2026-11-03 rulings and are not asked: "do it correctly" rules out an inert legacy row
that protects less than today (D1: enforce each unconverted entry exactly as today until converted, re-picked or
removed, and flag it), and "nothing the directory can resolve is re-picked by hand" rules D3 (convert every name that
resolves to exactly one group, effective at once; the pass's audit event lists each). One decision remains, and only
conditionally:

- **D2 - Graph as the SID source** (asked only if I.1 shows Exchange Online does not carry the on-premises SID).
  Action: add an app-wide Graph read setting (`User.Read.All`) on Admin Settings for the protection service.
  Consequence: a new credential to hold and an Entra dependency in the write path; without it, synced targets bind by
  address as today. **Recommendation: yes, if and only if I.1 requires it.**
