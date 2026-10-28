# Recipient Lookup: say what a recipient is, and where its mailbox is - Plan

Status: **Approved 2026-10-28 (owner: "add everything free and clarify what's displayed") - the accuracy core plus extras 1, 2, 3 (not 3b), 7, 8; the implementing agent spells out the displayed fields per recipient kind before code. In progress 2026-10-28 on `feat/recipient-lookup-accuracy`: the display is spelled out in "Displayed fields per recipient kind", below the Exec summary, before any code.**
Owner: Michael
Module: `RecipientLookup` (`Modules/ModuleCatalog.cs:550-572`, version `1.2.1`)
Last verified against code: local master `9241fa0d`, 2026-10-27. `origin` and `github` master are at `f09893c4`, an
ancestor of it. Every file and line cited was read there; lines move, so re-read before editing (Known Failure Class 4).
Versions (proposed): module `1.2.1` -> `1.3.0` in S1, then one patch per later slice; base app `2.35.0` unchanged
(section 6).
Authority: subordinate to `docs/ProjectConstitution.md` and `AGENTS.md`. A written plan is required twice over: the owner
asked for one, and the Constitution's Planning Rules name an Exchange service change and a trace change (the new
on-premises read's trace steps; extra E8 is an audit change).
Mandate: `.agents/decisions.md`, the 2026-10-26/27 entry item 0 (find and fix bugs; remove or significantly change no
intended functionality) and the 2026-10-27 entry item 2 (the test is the user's experience). Section 1 applies it.
Builds on: kd-37 (a cancel reaches the on-premises credential reads), kd-57 (a cancel is filed Cancelled), aud-a23o,
aud-a54, aud-a68 and kd-14 (a read that fails is a warning in its own words, never an absence). None of their behaviour
is undone (3.8).

## Exec summary

*The only part of this plan you need to read. Everything after it is for the agent that builds it.*

**What it does.** Recipient Lookup will tell you what a recipient is, in plain words, instead of repeating Exchange
Online's internal label. Your on-premises mailbox will show as "Mailbox", location "On-Premises". A distribution list will
show as "Distribution list", with its mailbox location "Not applicable" instead of "Unknown". Every other kind gets its
own name too: shared mailbox, room, equipment, Microsoft 365 group, mail-enabled security group, dynamic list, room list,
contact, guest, mail user, mail-enabled public folder. Exchange's own words stay on screen, in small print under the
plain one, for anyone who needs them.

**Why it was wrong.** Exchange Online records a mailbox that lives on your own servers as a "mail user": an entry that
passes mail on to on-premises. The module printed that label as the type. It did ask on-premises Exchange, and found the
mailbox there, but used the answer only for the location row. A distribution list has no mailbox at all, and "Unknown"
was the module's word for "no mailbox anywhere".

**The same kind of fault, fixed in the same pass.** Rows that do not apply, or were never read, stop stating things. A
list's sizes, archive and forwarding say "Not applicable" instead of "N/A", "Disabled" and "None". An on-premises
mailbox's forwarding, item counts and last logon say "Not read", because the module never asks on-premises for them;
today they say "None" or "N/A". "Primary Email" shows the real primary address, not whatever was typed in the box.
"Created" says so when the date is that of Exchange Online's copy. When the two sides disagree, or one cannot be asked,
the page says "Could not be determined" and why. It never guesses, and nothing on the page today is removed.

**Optional extras. None is included unless you name it by number.**
1. Quotas beside the size. Free. The module's own description already promises quotas, and it has never shown one.
2. Directory facts: synced from on-premises or cloud-only, the Entra object ID, alias, hidden from the address book, and
   where a mail user's or contact's mail goes. Free.
3. Mailbox settings: archive status, whether forwarding keeps a copy, send-on-behalf delegates, last real user activity.
   Free. Holds (litigation and the rest) are a separate 3b, because they tell everyone who can open this module who is
   under a legal hold.
4. The same detail for on-premises mailboxes as for cloud ones (counts, last logon, forwarding, database, an archive kept
   in the cloud), using one on-premises connection instead of two, so those lookups also get faster.
5. A button that fetches on-premises detail on demand: where the account sits in Active Directory, when it was created
   there, and whether on-premises agrees with Exchange Online about a cloud mailbox. A few seconds, only when pressed.
6. For groups: owners, member count (counted up to 1,000), who may send to it, moderation and join rules. One extra read
   per group lookup.
7. A "show all details" switch: every value Exchange returned, and every address rather than the first 20. Free.
8. The audit record also keeps the address the lookup resolved to, so searching the log for someone's primary address
   finds the lookups that were typed by alias. Free.
9. Whether the recipient is a protected account. I recommend no: it would show the protected list to everyone who can
   open this module, and it needs directory reads this module does not make.
10. Entra sign-in and licence details. I recommend no: it needs a new app registration and secret for this module, which
    you would create, and True Last Logon already covers sign-ins.

My recommendation: 1, 2, 3 (without 3b), 4, 5, 6, 7 and 8.

**What it gets you.** Operators see at a glance what they are dealing with and where it lives, and the page stops saying
"None", "Disabled" or "Unknown" about things it never checked. Lookups of lists, contacts and cloud-only objects get
faster, because the module stops asking on-premises Exchange about things that cannot be there, and every lookup stops
reading the same mailbox from Exchange Online twice. No lookup gets slower.

**What it costs.** The accuracy fix is four slices of code and one of records, about sixteen files, five of them code,
all inside this module. The app's own version does not move, only the module's. Each extra you take is one more slice.

**The biggest risk.** A wrong answer is worse than a vague one: an operator told "Mailbox" about something that is
really a mail user may go to the wrong system. The plan names a type only when Exchange itself reports it, from the side
that holds the object, and says "Could not be determined" otherwise. The price of that caution: where on-premises
Exchange cannot be asked (not set up for this module, its credentials unavailable, or the account comes from a directory
that server does not serve), an on-premises mailbox reads "Could not be determined", with the reason, instead of a type.

**What approving authorises.** The accuracy fix as described, with its version bumps and documentation, and only the
extras you name by number. Deploying stays with you.

## Displayed fields per recipient kind

*Written by the implementing agent before any code, as the owner asked on 2026-10-28 ("clarify what's displayed"). It is
for the owner and the operator as much as the builder: it states what the page shows once the core (S1-S4) and the
accepted extras (E1, E2, E3 without 3b, E7, E8) have landed. Where it settles something sections 3 and 4 leave open, the
last subsection says so. `README.md`, Recipient Lookup, says the same in operator terms. Code re-read at `01bf455e`.*

Abbreviations used in this section only: **N1** Exchange Online `Get-Recipient`; **N2** Exchange Online `Get-Mailbox`;
**N3** Exchange Online `Get-MailboxStatistics` and its `-Archive`; **N4** on-premises `Get-Recipient`; **N5** the shared
on-premises size read (`Get-MailboxStatistics` and `-Archive`, `TotalItemSize` only). Section 3.2 describes each.

### The page, top to bottom

1. **The error box**, instead of everything below, when the lookup itself failed: "Recipient '...' not found.", the
   reason Exchange Online could not be asked, or "Cancelled by the operator" (kd-57). Unchanged.
2. **The warning box**: one line per read that failed and per answer that could not be settled. Every "Could not be
   read" and every "Could not be determined" on the page has its reason here.
3. **The left table**, the same rows for every kind, in this order: Display Name, Primary Email, Recipient Type, Mailbox
   Location, Created, Last Logon, Last User Activity (E3), Synced from On-Premises (E2), Entra Object ID (E2), Alias
   (E2), Hidden from Address Book (E2), External Address (E2), Last Changed (E2).
4. **The right table**, the same rows for every kind, in this order: Mailbox Size, Warning Quota (E1), Prohibit Send
   Quota (E1), Prohibit Send/Receive Quota (E1), Send Quota Used (E1), Deleted Item Size, Total Size, Item Count, Deleted
   Items, Archive, Archive Status (E3); then, only when an archive was found, Archive Size, Archive Quota (E1), Archive
   Warning Quota (E1), Archive Deleted, Archive Items; then Forwarding, Send on Behalf (E3), Account Disabled (E3).
5. **The "Show all details" switch** (E7), off by default.
6. **Email Addresses**: the first 20 and "...and N more", as today; with the switch on, every one.
7. **All details** (E7), only with the switch on: every property each read returned, by read.

Every row is shown for every kind; a row that does not apply says so rather than disappearing (R5). The archive rows
under Archive are hidden when no archive was found, as today.

### The words

| Word | Where | Means |
| --- | --- | --- |
| "Not applicable" | mailbox rows, Mailbox Location, External Address | The recipient has no mailbox (or, for External Address, is not a mail user, contact or guest), so the row can have no value. |
| "Not read for on-premises mailboxes" | an on-premises mailbox's rows | On-premises Exchange holds the value and this module does not ask for it (extra 4 was not taken). Not an absence. |
| "Not read for this recipient type" | a Microsoft 365 group's mailbox rows, and the other Exchange-held types | Exchange Online holds a mailbox of a kind this module does not read. Not an absence. |
| "Not read" | the mailbox rows of a recipient that could not be determined | What the recipient is was not settled, so nothing about a mailbox was asked. |
| "Could not be read" | any row whose read was made | The read failed or answered nothing; the warning box says why (aud-a54, aud-a68). |
| "Could not be determined" | Recipient Type, Mailbox Location | The two sides disagree, or a side that has to be asked could not be. The warning box says why; the small print gives each side's words. |
| "Not reported by Exchange" | the rows the extras add | The read was made and Exchange's answer did not carry the property. Never a default. |
| "N/A" | the rows shown before this plan | Exchange's answer did not carry the value (aud-a23d). Never with a unit after it. |
| "None" | Forwarding, Send on Behalf | Exchange's own answer that there is none. |

### Rows that read the same for every kind

| Row | Shows | From |
| --- | --- | --- |
| Display Name | as Exchange reports it | N1 `DisplayName` |
| Primary Email | the primary address, then "(looked up as {what was typed})" when the operator typed anything else (compared without case); when Exchange reports none, what was typed, then "(as typed; Exchange reported no primary address)" | N1 `PrimarySmtpAddress` |
| Recipient Type | the plain label (per kind, below), and beneath it, in small print, Exchange's raw words: `Exchange Online: {N1's RecipientTypeDetails}`, then `; Get-Mailbox: {...}` when N2 decided, then `; on-premises: {...}` when N4 was asked | N1, N2, N4 `RecipientTypeDetails` |
| Mailbox Location | a badge: "Cloud" (blue), "On-Premises" (grey), "Not applicable" (neutral, with a one-line note), "Could not be determined" (the warning colour) | per kind, below |
| Created | the time in the browser's zone (aud-a23e), then " (Exchange Online's copy)" when Exchange reports the recipient as synced from on-premises; "N/A" when none was reported | N1 `WhenCreated`, `IsDirSynced` |
| Synced from On-Premises | "Yes", "No", or "Not reported by Exchange" | N1 `IsDirSynced` |
| Entra Object ID | the ID, or "Not reported by Exchange" | N1 `ExternalDirectoryObjectId` |
| Alias | the alias, or "Not reported by Exchange" | N1 `Alias` |
| Hidden from Address Book | "Yes", "No", or "Not reported by Exchange" | N1 `HiddenFromAddressListsEnabled` |
| External Address | for an object Exchange Online holds as `MailUser`, `GuestMailUser`, `MailContact` or `MailForestContact` - an on-premises mailbox included, whose cloud object is a mail user - the address its mail goes to, or "Not reported by Exchange"; for every other kind, "Not applicable" | N1 `ExternalEmailAddress` |
| Last Changed | the time, with " (Exchange Online's copy)" as for Created; "Not reported by Exchange" | N1 `WhenChanged` |
| Email Addresses | as today | N1 `EmailAddresses` |

### Per kind

Each kind names the reads made, what Recipient Type and Mailbox Location show, and every mailbox row: Last Logon, Last
User Activity, and the whole right table.

#### Cloud mailbox: user, shared, room, equipment

- **Is:** N1 type `UserMailbox`, `SharedMailbox`, `RoomMailbox` or `EquipmentMailbox` - and the same for
  `SchedulingMailbox`, `LinkedMailbox` and `DiscoveryMailbox` - and N2 found the mailbox.
- **Recipient Type:** "Mailbox", "Shared mailbox", "Room mailbox", "Equipment mailbox" ("Scheduling mailbox", "Linked
  mailbox", "Discovery mailbox"). Small print `Exchange Online: UserMailbox`.
- **Mailbox Location:** "Cloud".
- **Reads:** N1, N2, N3. No on-premises session.

| Row | Shows | From |
| --- | --- | --- |
| Last Logon | the time; "N/A" when none was reported | N3 `LastLogonTime` |
| Last User Activity | the time; "Not reported by Exchange" | N3 `LastUserActionTime` |
| Mailbox Size | "1.50 GB"; "N/A" | N3 `TotalItemSize` |
| Warning Quota, Prohibit Send Quota, Prohibit Send/Receive Quota | "49.50 GB"; "Unlimited"; "The database default (not read)" when the mailbox uses its database's defaults; "Not reported by Exchange" | N2 `IssueWarningQuota`, `ProhibitSendQuota`, `ProhibitSendReceiveQuota`, `UseDatabaseQuotaDefaults` |
| Send Quota Used | "3.0% of the send quota"; "Not applicable (no send quota)" when it is Unlimited; "Not read (the database default applies)"; "Not reported by Exchange" when the quota was not returned; "N/A" when the size was not | Mailbox Size over Prohibit Send Quota |
| Deleted Item Size | "0.20 GB"; "N/A" | N3 `TotalDeletedItemSize` |
| Total Size | the sum, only when both sizes were read; otherwise "N/A" | the two sizes |
| Item Count, Deleted Items | the number; "N/A" | N3 `ItemCount`, `DeletedItemCount` |
| Archive | "Enabled", "Disabled", "Could not be read" | N3 `-Archive` |
| Archive Status | Exchange's words: `ArchiveStatus`, then `(state: {ArchiveState})` when reported; "Not reported by Exchange" | N2 |
| Archive Size, Archive Deleted (archive found) | GB; "N/A" | N3 `-Archive` |
| Archive Quota, Archive Warning Quota (archive found) | as the quotas above | N2 `ArchiveQuota`, `ArchiveWarningQuota` |
| Archive Items (archive found) | the number; "N/A" | N3 `-Archive` `ItemCount` |
| Forwarding | the address, then ", and keeps a copy" or ", forward only" - or " (whether a copy is kept was not reported by Exchange)"; "None" | N2 `ForwardingSmtpAddress`, else `ForwardingAddress`; `DeliverToMailboxAndForward` |
| Send on Behalf | the delegates, separated by "; "; "None"; "Not reported by Exchange" | N2 `GrantSendOnBehalfTo` |
| Account Disabled | "Yes", "No", "Not reported by Exchange" | N2 `AccountDisabled` |

When a read fails or says no:

- **N2 fails:** the type and "Cloud" stand - Exchange Online typed it as a mailbox of its own - and every row of the
  table above reads "Could not be read" (3.8, change 1). Warning: "The Exchange Online mailbox could not be read: ...".
- **N2 answers that there is no mailbox (D4):** Recipient Type and Mailbox Location "Could not be determined", small
  print `Exchange Online: UserMailbox; Get-Mailbox: no mailbox`, every row of the table above "Not read". Warning:
  "Exchange Online reports {address} as UserMailbox but returned no mailbox for it."
- **The statistics read fails or answers no row:** Last Logon, Last User Activity, Mailbox Size, Send Quota Used,
  Deleted Item Size, Total Size, Item Count and Deleted Items read "Could not be read"; the warning as today.
- **The archive read fails or answers no row:** Archive reads "Could not be read" and its rows stay hidden, as today.

#### On-premises mailbox

- **Is:** N1 type `MailUser`, not reported as cloud-only, and N4 holds it as `UserMailbox`, `SharedMailbox`,
  `RoomMailbox`, `EquipmentMailbox`, `LinkedMailbox`, `LinkedRoomMailbox` or `DiscoveryMailbox`.
- **Recipient Type:** "Mailbox", "Shared mailbox", "Room mailbox", "Equipment mailbox", "Linked mailbox", "Linked room
  mailbox", "Discovery mailbox". Small print `Exchange Online: MailUser; on-premises: UserMailbox`.
- **Mailbox Location:** "On-Premises".
- **Reads:** N1; N4 in one on-premises session; N5 in its own session, as today. N2 and N3 are not asked.

| Row | Shows | From |
| --- | --- | --- |
| Last Logon, Last User Activity | "Not read for on-premises mailboxes" | - |
| Mailbox Size | GB; "Could not be read" | N5 |
| the three quotas, Send Quota Used | "Not read for on-premises mailboxes" | - |
| Deleted Item Size, Total Size, Item Count, Deleted Items | "Not read for on-premises mailboxes" | - |
| Archive | "Enabled", "Disabled", "Could not be read" (aud-a68) | N5 `-Archive` |
| Archive Status | "Not read for on-premises mailboxes" | - |
| Archive Size (archive found) | GB; "Could not be read" | N5 `-Archive` |
| Archive Quota, Archive Warning Quota, Archive Deleted, Archive Items (archive found) | "Not read for on-premises mailboxes" | - |
| Forwarding, Send on Behalf, Account Disabled | "Not read for on-premises mailboxes" | - |

External Address shows N1's: where Exchange Online sends this mailbox's mail.

#### Mail user, and the remote mailboxes on-premises reports

- **Is:** N1 type `MailUser`. Whether Exchange Online reports it as synced, and what N4 answers, decide the rest.
- **Reads:** N1; N4 unless Exchange Online reports it as cloud-only. N2, N3 and N5 are not asked.

| `IsDirSynced` | N4 answers | Recipient Type | Small print | Mailbox Location | Warning |
| --- | --- | --- | --- | --- | --- |
| false | not asked | Mail user | `Exchange Online: MailUser` | Not applicable: "A mail user has no mailbox. Its mail goes to another address." | none |
| true or not reported | `MailUser` | Mail user | `Exchange Online: MailUser; on-premises: MailUser` | Not applicable, the same note | none |
| true or not reported | `RemoteUserMailbox`, `RemoteSharedMailbox`, `RemoteRoomMailbox`, `RemoteEquipmentMailbox`, `RemoteTeamMailbox` | Could not be determined | `Exchange Online: MailUser; on-premises: RemoteUserMailbox` | Could not be determined | D1 (3.5) |
| true or not reported | any other type, or none | Could not be determined | `Exchange Online: MailUser; on-premises: {type, or "no type"}` | Could not be determined | D2 |
| true or not reported | no such recipient | Could not be determined | `Exchange Online: MailUser; on-premises: no such recipient` | Could not be determined | D3 |
| true or not reported | could not be asked | Could not be determined | `Exchange Online: MailUser; on-premises: not read` | Could not be determined | U1: "Could not determine mailbox location: ...", today's words |
| true or not reported | more than one recipient | Could not be determined | `Exchange Online: MailUser; on-premises: more than one recipient` | Could not be determined | U2 |

Mailbox rows: "Not applicable" for a mail user, "Not read" when it could not be determined. External Address: N1's.

#### Distribution list

- **Is:** N1 type `MailUniversalDistributionGroup` ("Distribution list"), or `MailNonUniversalGroup` ("Distribution
  group (non-universal)").
- **Small print:** `Exchange Online: MailUniversalDistributionGroup`.
- **Mailbox Location:** "Not applicable", not in the warning colour: "A distribution list has no mailbox."
- **Every mailbox row:** "Not applicable"; the archive rows hidden. External Address: "Not applicable".
- **Reads:** N1 only. No N2, no on-premises session.

#### Mail-enabled security group

As a distribution list, for N1 type `MailUniversalSecurityGroup`: "Mail-enabled security group", "A mail-enabled
security group has no mailbox."

#### Dynamic distribution list

As a distribution list, for `DynamicDistributionGroup`: "Dynamic distribution list", "A dynamic distribution list has no
mailbox."

#### Room list

As a distribution list, for `RoomList`: "Room list", "A room list has no mailbox."

#### Microsoft 365 group

- **Is:** N1 type `GroupMailbox`: "Microsoft 365 group", small print `Exchange Online: GroupMailbox`.
- **Mailbox Location:** "Cloud": Exchange Online holds its mailbox.
- **Every mailbox row:** "Not read for this recipient type" (`Get-Mailbox` does not return a group's mailbox, A5); the
  archive rows hidden. External Address: "Not applicable".
- **Reads:** N1 only.
- The same for the other types Exchange Online holds a mailbox for and this module does not read: `TeamMailbox` "Site
  mailbox", `PublicFolderMailbox` "Public folder mailbox", and `ArbitrationMailbox`, `AuditLogMailbox`,
  `AuxAuditLogMailbox`, `MonitoringMailbox`, `SupervisoryReviewPolicyMailbox` "System mailbox ({Exchange's word})".

#### Contact

As a distribution list, for `MailContact` ("Contact", "A contact has no mailbox.") or `MailForestContact` ("Contact
(another forest)", "A contact (another forest) has no mailbox."), except that External Address shows where its mail
goes (N1), or "Not reported by Exchange".

#### Guest

As a contact, for `GuestMailUser`: "Guest", "A guest has no mailbox."; External Address N1's.

#### Mail-enabled public folder

As a distribution list, for `PublicFolder`: "Mail-enabled public folder", "A mail-enabled public folder has no mailbox."

#### A type this app does not name, or none

- **Recipient Type:** "Not a type this app names: {Exchange's word}", or "Exchange reported no type". Never mapped to the
  nearest label.
- **Reads:** N1 and N2; then:

| N2 answers | `IsDirSynced` | Recipient Type | Small print | Mailbox Location | Mailbox rows |
| --- | --- | --- | --- | --- | --- |
| found, its own type a cloud mailbox type above | any | that type's label | `Exchange Online: {word}; Get-Mailbox: UserMailbox` | Cloud | as a cloud mailbox (N3 read) |
| found, any other type | any | unchanged | `Exchange Online: {word}; Get-Mailbox: {type or "no type"}` | Cloud | as a cloud mailbox |
| no mailbox | false | unchanged | `Exchange Online: {word}; Get-Mailbox: no mailbox` | Not applicable: "Exchange Online holds no mailbox for it and reports it as cloud-only." | Not applicable |
| no mailbox | true or not reported | N4's on-premises mailbox type names it; any other N4 answer, Could not be determined | `...; Get-Mailbox: no mailbox; on-premises: {N4's answer}` | On-Premises (rows as an on-premises mailbox); otherwise Could not be determined, with a warning naming both sides' words, or D3, U1 or U2 | as an on-premises mailbox; otherwise "Not read" |
| fails | any | Could not be determined | `Exchange Online: {word}; Get-Mailbox: not read` | Could not be determined | "Could not be read", with N2's warning and one saying why the type is not settled |

#### Could not be determined, in general

Recipient Type and Mailbox Location read "Could not be determined", the location in the warning colour. The small print
gives each side's words; the warning box gives the reason in a sentence (3.5). Every mailbox row reads "Not read", except
where a read was made and failed, which reads "Could not be read". The rows that read the same for every kind still show.

### Show all details (E7)

A switch above Email Addresses, off by default. On, it lists every email address, and under "All details" every property
each read returned - N1, N2, N3, N3's `-Archive`, N4, in that order, each by name and value in the order Exchange gave
them; a value longer than 500 characters is cut there and says so. Hold properties are left out, because extra 3b was not
taken: every property whose name contains "Hold" (litigation, retention, in-place, compliance-tag and delay holds), and
`RetentionComment` and `RetentionUrl` (A16). N5's values are on the page already; the shared size read returns sizes, not
rows, so it is not listed (widening it would change a shared file, section 6).

### Audit (E8)

The lookup's audit row is unchanged - `target` is what was typed - and gains `resolvedAddress`, the primary address N1
returned, when the lookup found a recipient that has one. A cancelled lookup is filed Cancelled, as today (kd-57).

### What this section settles that sections 3 and 4 leave open

1. **"Could not be read" for a cloud mailbox's statistics rows, and an on-premises mailbox's size, when their read failed
   or answered no row**, and for every mailbox row when N2 failed. 3.6 says "as today" for them, which was "N/A"; AC5
   ("no row states an absence the module did not read") decides it. The reads and warnings are unchanged.
2. **An on-premises archive's Deleted and Items read "Not read for on-premises mailboxes"**, not "N/A": 3.6 says "as
   today", but N5 never asks for them (2.5 d, AC5). Its Archive Size stays as today.
3. **The unknown and missing types' small print and warnings** (3.3 gives their outcomes, not their words): as the table
   above. Where N4 answered with a type that does not settle it, the warning is "Exchange Online reports {address} as
   {word}, a type this app does not name, and holds no mailbox for it; on-premises Exchange holds it as {type}." (or
   "Exchange Online reported no type for {address} and holds no mailbox for it; ..."); where N2 failed, "Exchange Online
   reports {address} as {word}, a type this app does not name, and its mailbox could not be read."
4. **External Address for an on-premises mailbox** shows N1's address: its cloud object is a mail user, and the address
   is where Exchange Online sends its mail.
5. **The " (Exchange Online's copy)" note** follows Created and Last Changed only when Exchange reports the recipient as
   synced (`IsDirSynced` true); when the sync state is not reported, neither carries it, and Synced from On-Premises
   says "Not reported by Exchange".
6. **"Show all details" leaves out hold properties by name** (A16), and does not list N5.

New assumptions, labelled as section 11's are, and not resolved by this section:

- **A16.** Exchange's hold properties are named with "Hold" (`LitigationHoldEnabled`, `LitigationHoldDate`,
  `LitigationHoldOwner`, `LitigationHoldDuration`, `InPlaceHolds`, `ComplianceTagHoldApplied`, `DelayHoldApplied`,
  `DelayReleaseHoldApplied`, `RetentionHoldEnabled`, `StartDateForRetentionHold`, `EndDateForRetentionHold`), with
  `RetentionComment` and `RetentionUrl` beside them. From knowledge; no live call. A property this filter misses would
  show on the switch's list.
- **A17.** Exchange Online's `Get-Mailbox` returns `ArchiveStatus`, `ArchiveState`, `DeliverToMailboxAndForward`,
  `GrantSendOnBehalfTo` and `AccountDisabled`, and `Get-MailboxStatistics` returns `LastUserActionTime`. From knowledge;
  a property that does not come back reads "Not reported by Exchange".

## 1. The request and how this plan reads it

Owner, 2026-10-27, verbatim:

> recipient lookup is a bit ambiguous. an on-prem mailbox shows as 'MailUser' and OnPremises, but it should show
> Mailbox. and a DL shows Mailbox Location Unknown. plan to make that module more accurate and useful.

Mode: `PLAN_ONLY` (Constitution, Task Modes). No code is changed by this document.

**Readings.** Each is a judgement this plan makes; the owner can overrule any in one word.

- **R1, "it should show Mailbox".** The Recipient Type row names what the recipient is ("Mailbox"); the Mailbox Location
  row keeps saying where ("On-Premises"). The type label does not repeat the location.
- **R2, "a DL shows Mailbox Location Unknown".** A recipient that has no mailbox reads "Not applicable". "Unknown" is
  retired: a read that failed, or two sides that disagree, read "Could not be determined" with the reason.
- **R3, "more accurate".** The same class of fault elsewhere on the page - a row that states something the module never
  read, or something that does not apply - is fixed in the same pass (2.5). Under the mandate these are bugs: fixing them
  removes no intended behaviour, and the operator's flow (type an address, press Lookup) does not change.
- **R4, "more useful".** Section 4's extras. Each is the owner's to accept or decline; none is in scope without his word.
- **R5, nothing removed** (the drafting brief: "Nothing the module shows today may be removed"). Every row stays. A row
  that does not apply says "Not applicable" rather than disappearing. Exchange Online's raw type moves from the row's
  value to small print beneath it, still on screen.

## 2. The module today (read at `9241fa0d`)

### 2.1 The reads, in order

`RecipientLookupService.GetRecipientInfoAsync` (`Services/RecipientLookupService.cs:13-206`), called by the page's
`RunLookup` with the typed text trimmed (`Components/Pages/RecipientLookup.razor:214`) and the activity's token (`:230`):

1. **Exchange Online `Get-Recipient -Identity <typed>`**, under Stop (22-25). No row is "Recipient '<typed>' not found."
   (28-32); any failure is the lookup's error (144-152).
2. **Exchange Online `Get-Mailbox -Identity <typed>`**, under Stop through `ReadUnlessNotFound` (57-63). A failure sets
   `MailboxUnreadable` and `ArchiveUnreadable` and a warning (65-69).
3. **Only when read 2 found a mailbox:** `Get-MailboxStatistics` (76-103) and `Get-MailboxStatistics -Archive` (110-141).
   Reads 1-3 are one pooled Exchange Online query (16; `allowRetry: true` at 155).
4. **When there is no error, the shared location read** `GetMailboxLocationAsync(<typed>, token)` (161), in
   `Services/ExchangeServiceBase.cs:730-746`:
   - 4a. Exchange Online `Get-Mailbox -Identity <typed>` **again**, in a second pooled query (`HasCloudMailboxAsync`,
     735 and 762-775). Found is "Cloud".
   - 4b. Otherwise one on-premises session (780-827): the module credential "on-prem mailbox location check" (788), then
     on-premises `Get-Mailbox` (809-810, "OnPrem"), else `Get-RemoteMailbox` (812-813, "Cloud"), else `return null;` (815),
     which 745 makes "Unknown". Each read is `{commandName} -Identity $Identity -ErrorAction Stop | Select-Object -First 1
     Identity` (837) and returns only whether a row came back (840). A side that could not be asked throws (785, 792,
     820).
   - The service maps "OnPrem" to "On-Premises", "Cloud" to "Cloud", anything else to "Unknown" (161-166); a throw is
     "Unknown" plus the warning "Could not determine mailbox location: <reason>" (168-174).
5. **Only for "On-Premises":** a second on-premises session, `GetOnPremMailboxSizeAsync` (184; base 628-715), credential
   "on-prem mailbox size check" (637): on-premises `Get-MailboxStatistics` and its `-Archive`, each selecting
   `TotalItemSize` only (663, 678).

Two things follow. Read 4a repeats read 2 exactly. And read 4b runs for every recipient Exchange Online holds no mailbox
for - every list, contact, guest, mail user and Microsoft 365 group, synced or cloud-only - at the cost of a credential
fetch and a Kerberos session each time.

### 2.2 Every field, and where it comes from

| Row (page line) | Shown as | Value from (service line) | Read |
| --- | --- | --- | --- |
| Display Name (64) | `@result.DisplayName` | Get-Recipient `DisplayName` (34) | 1 |
| Primary Email (65) | `@result.EmailAddress` | **the typed text**: `new RecipientInfoResult { EmailAddress = emailAddress }` (18); nothing from Exchange | none |
| Recipient Type (66) | `@result.RecipientType` | Get-Recipient `RecipientTypeDetails` (35) - Exchange Online's only | 1 |
| Mailbox Location (67-74) | badge: "Cloud" blue, "On-Premises" grey, anything else the warning colour (70) | the shared location read (161-174) | 4a, 4b |
| Created (78) | `ShowTime(result.WhenCreated)` | Get-Recipient `WhenCreated` (40) | 1 |
| Last Logon (79) | `ShowTime(result.LastLogonTime)` | `LastLogonTime` (87) | 3 - cloud mailbox only |
| Mailbox Size (86) | `F2`, or "N/A", then " GB" | `TotalItemSize` (88); for "On-Premises", the on-premises size (185-186) | 3 or 5 |
| Deleted Item Size (87) | the same | `TotalDeletedItemSize` (89) | 3 - cloud mailbox only |
| Total Size (88) | the same | the sum, a missing part counted as 0 (`Models/LookupModels.cs:145-146`) | - |
| Item Count (89), Deleted Items (90) | `N0` or "N/A" | `ItemCount`, `DeletedItemCount` (85-86) | 3 - cloud mailbox only |
| Archive (92) | "Could not be read" / "Enabled" / "Disabled" | the cloud archive read (110-141); for "On-Premises", the on-premises one (191-192) | 3 or 5 |
| Archive Size, Deleted, Items (93-98) | only when Enabled | cloud archive statistics (125-128); on-premises size only (193-194) | 3 or 5 |
| Forwarding (99) | "Could not be read" / the address / "None" | `ForwardingSmtpAddress ?? ForwardingAddress` (73-74), read only when read 2 found a mailbox (71) | 2 |
| Email Addresses (105-118) | the first 20, then "...and N more" | Get-Recipient `EmailAddresses` (42-50) | 1 |
| Warnings (50-58) | one line each | every read's failure | all |

### 2.3 Symptom 1: an on-premises mailbox shows "MailUser"

The type row prints Exchange Online's `RecipientTypeDetails` and nothing else:

```csharp
r.RecipientType = recip.Properties["RecipientTypeDetails"]?.Value?.ToString();   // Services/RecipientLookupService.cs:35
```
```razor
<tr><td class="fw-bold">Recipient Type</td><td>@result.RecipientType</td></tr>   @* Components/Pages/RecipientLookup.razor:66 *@
```

In a hybrid deployment, Exchange Online's object for a mailbox held on-premises is a mail user (assumption A1): it exists
so mail sent in the cloud can be routed to the on-premises mailbox, and Exchange Online holds no mailbox for it. So the
row reads "MailUser". The location row is right because read 4, finding no Exchange Online mailbox, asks on-premises
`Get-Mailbox` and finds one:

```csharp
if (OnPremRecipientExists(ps, session, "Get-Mailbox", identity))
    return "OnPrem";                                                            // Services/ExchangeServiceBase.cs:809-810
```

But that read selects `Identity` alone (837) and answers `ReadUnlessNotFound(ps, new ConnectionErrorTracker()).Count > 0`
(840): only whether a row came back. **The on-premises recipient type is never read.** The only thing that reaches the
result is the word "OnPrem", which the service puts in `MailboxLocation` (163) and nowhere else. Nothing reconciles the two
sides: "MailUser" is accurate about Exchange Online and misleading about the recipient.

### 2.4 Symptom 2: a distribution list shows "Mailbox Location: Unknown"

A distribution list has no mailbox. Read 2 is Exchange's not-found, which `ReadUnlessNotFound` makes no rows (63): no
mailbox, no statistics. Read 4a asks Exchange Online the same question again (735: no). Read 4b asks on-premises:
`Get-Mailbox`, no; `Get-RemoteMailbox`, no; `return null;` (815); `?? "Unknown"` (745). The base's own comment says what
that "Unknown" means:

```csharp
// for. "Unknown" means both sides answered that they hold no such mailbox.     // Services/ExchangeServiceBase.cs:729
```

The service passes it through (`_ => "Unknown"`, 165), and the page shows it in the warning colour (70-71). **The module
has the answer - no mailbox on either side - and prints a word that says it does not know.** Where on-premises cannot be
asked at all (no server configured, no credentials, a session that fails), the read throws (785, 792, 820), the catch
sets `result.MailboxLocation = "Unknown";` (172) and adds "Could not determine mailbox location: ..." (173): the same word
for a different situation. Which of the two paths the owner's lookup took is not known (A7); both are fixed.

The deeper fault is the order of questions. The module asks where a recipient's mailbox is before asking whether it has
one, though read 1 already answered: a list's `RecipientTypeDetails` is `MailUniversalDistributionGroup`, and nothing
reads it. The same path gives every contact, guest, mail user and Microsoft 365 group "Unknown", with "Archive: Disabled"
(page 92) and "Forwarding: None" (99) beside it.

### 2.5 The same class of fault elsewhere on the page

Each of these states something the module did not establish. All are fixed by the core slices (section 7).

- **(a) "Primary Email" is the typed text** (service 18, page 65). Typing a secondary address, an alias or a name shows
  that as the primary. Every later read also re-resolves the typed text on its own (57, 78, 112, 161, 184), rather than
  asking about the object read 1 found.
- **(b) "Forwarding: None" for any recipient with no Exchange Online mailbox** - every on-premises mailbox, list, contact
  and group (page 99: `result.MailboxUnreadable is not null ? "Could not be read" : result.ForwardingAddress ?? "None"`).
  Forwarding is read only when read 2 found a cloud mailbox (71-74). An on-premises mailbox with forwarding set
  on-premises is shown "None": false.
- **(c) "Archive: Disabled" and "N/A GB" sizes for a recipient that has no mailbox** (page 86-92). Not disabled: not
  applicable. "N/A GB" also puts a unit after a missing number.
- **(d) An on-premises mailbox's Deleted Item Size, Item Count, Deleted Items and Last Logon read "N/A"**, though they
  were never asked for (the on-premises statistics select `TotalItemSize` alone, base 663, 678). **"Total Size" then shows
  the mailbox size alone, as if it were the total** (`(MailboxSizeGB ?? 0) + (DeletedItemSizeGB ?? 0)`, model 145-146).
- **(e) "Created" for a synced recipient is when Exchange Online's copy was created** (read 1's `WhenCreated`, 40), not
  the on-premises account or mailbox (A6). For the owner's on-premises mailbox it can be years later than the mailbox.
- **(f) The catalog promises quotas** - "Look up mailbox details including size, quotas, archive status, and recipient
  type." (`Modules/ModuleCatalog.cs:554`) and the permission text "read mailbox details such as size, quotas, archive state
  and recipient type" (568). The page shows no quota and never has: `git log -S Quota` over the page, the service and the
  model finds nothing since the module landed (`e62ae736`, 2026-05-05).
- **(g) Wasted reads**: read 4a repeats read 2, and read 4b asks on-premises about recipients that cannot have an
  on-premises mailbox there (a cloud-only list or contact) or that Exchange Online has already typed as a group or contact.
- **(h) A guess where the sides disagree**: on-premises `Get-RemoteMailbox` found, Exchange Online no mailbox, reads
  "Cloud" (base 812-813). The operator is told the mailbox is in Exchange Online while Exchange Online holds none - a
  mailbox not yet provisioned, unlicensed or removed. That disagreement is exactly what an operator needs to see.

## 3. Design: the accuracy fix

### 3.1 The rule

**A type is named only when Exchange reports it, from the side that holds the object.** When the two sides disagree, or
a side that has to be asked cannot be, the page says "Could not be determined" and why. Nothing is inferred from a hint:
for example, a mail user that carries a mailbox GUID is evidence of an on-premises mailbox, not proof, so it is never
classified on (E7 can show the value).

### 3.2 The reads, in order

Numbered N1-N5 to keep them apart from today's reads 1-5.

1. **N1, Exchange Online `Get-Recipient`.** The same read, the same not-found and failure handling. From its row the
   lookup now also keeps `RecipientType`, `PrimarySmtpAddress` and `IsDirSynced`, beside the `DisplayName`,
   `RecipientTypeDetails`, `WhenCreated` and `EmailAddresses` it keeps today. `IsDirSynced` is read as true, false or "not
   reported" (a `PSObject`-wrapped value unwrapped, a string parsed, anything else not reported) by a parser in the
   module. `ExchangeIdentityResolver.IsDirSynced` (`Services/ExchangeIdentityResolver.cs:147-156`) is private and stays
   so: no shared file is edited (section 6).
2. **N2, Exchange Online `Get-Mailbox`,** asked only when N1's type is a mailbox type `Get-Mailbox` returns (3.3, the
   "N2 asked" column), a type the table does not hold, or none. The same read and failure handling (aud-a54). For a list,
   contact, guest, mail user, Microsoft 365 group or public folder it is not asked: its answer is known before it is made.
   When N1 reported no type, or one the table does not hold, and N2 found a mailbox, N2's own `RecipientTypeDetails`
   names it.
3. **N3, statistics and archive:** unchanged, when N2 found a mailbox. N1-N3 stay one pooled query.
4. **The shared location read is no longer called by this module.** It stays, unchanged, for the two permission pages
   that route writes by it (`Components/Pages/MailboxPermissions.razor:461`, `CalendarPermissions.razor:431`). Its
   Exchange Online half was N2 again; its on-premises half is replaced by N4.
5. **N4, one on-premises read, made only when 3.4 needs it:** N1 reports a mail user (or a type the table does not hold
   with no Exchange Online mailbox) and does not report the object as cloud-only (`IsDirSynced` true or not reported).
   It is the module's own, in `RecipientLookupService`, built from the base's protected surface exactly as the base's
   location read is built:
   - the module credential with the purpose "on-prem mailbox location check" - the string today's location read uses,
     so the vault log and the existing cancellation and warning tests keep their keys - with the page's token passed to
     it (kd-37's rule: the token reaches the credential read; the throttle, session and read take none, as today);
   - `ThrottledAsync(..., _onPremThrottle)` (base 137, 19), a fresh runspace, `ConnectOnPrem` (506, the test seam), and
     `RemoveOnPremSession` (848) in a `finally`;
   - the remote command `Get-Recipient -Identity $Identity -ErrorAction Stop | Select-Object -First 2 RecipientType,
     RecipientTypeDetails`, sent as `Invoke-Command -Session <session> -ScriptBlock { param($Identity) ... } -ArgumentList
     <address> -ErrorAction Stop` - the shape of the base's private `AddOnPremRead` (`ExchangeServiceBase.cs:717-725`),
     written out in the module (four statements) rather than widened, so that no shared file changes. **The address is
     passed as an argument, never spliced into the script text: it began as operator input, and the session runs under a
     privileged credential.**
   - Exchange's not-found, read by structure (`ReadUnlessNotFound`, `IsNotFound`), is an answer: no such recipient
     on-premises. Every other failure is a read that failed, a cmdlet the session lacks included (`IsNotFound` never takes
     a `CommandNotFoundException`, base 397-399 and 413-415). A cancellation is let through (`IsCancellation`, aud-a23g).
   - **Failures keep today's words.** Not configured: `on-premises Exchange could not be asked: <OnPremNotConfigured>`;
     no credentials: `... <OnPremCredentialsUnavailable>` (base 188, 190); a session or read failure: `on-premises
     Exchange could not be asked: Get-Recipient failed: <the cmdlet's own words>`. A credential fetch that throws
     propagates. All of them reach a catch of today's shape (service 168-174), which adds "Could not determine mailbox
     location: <reason>". So every warning a test pins today reads the same.
   - **Trace**: an `OnPremRecipientTypeRequested` step (backend `OnPremExchange`, target the address), and a `Failed` step
     with the fixed reason "Credentials unavailable" when the credential cannot be had - the size read's own pattern (base
     636, 641). No exception text in trace details.
   - Two rows: "On-premises Exchange returned more than one recipient for <address>" (U2 in 3.5).
6. **N5, the on-premises sizes:** when N4 places a mailbox on-premises, the shared `GetOnPremMailboxSizeAsync` exactly as
   today (service 177-203), unchanged.

**Which object N2-N5 ask about:** the `PrimarySmtpAddress` N1 returned, or the typed text when Exchange reported none.
S1 binds N4 to it from the start; S3 moves the existing reads (3.7).

**Reads per lookup, today against this design:**

| Lookup of | Exchange Online pooled queries | On-premises sessions |
| --- | --- | --- |
| a cloud mailbox | 2 -> 1 | 0 -> 0 |
| an on-premises mailbox | 2 -> 1 | 2 -> 2 (1 with E4) |
| a list, security group, dynamic list, room list, contact, guest or public folder | 2 -> 1 | 1 -> 0 |
| a Microsoft 365 group | 2 -> 1 | 1 -> 0 |
| a cloud-only mail user | 2 -> 1 | 1 -> 0 |
| a synced mail user that is not a mailbox | 2 -> 1 | 1 -> 1 |

No case gains a round trip. The on-premises session is where the time goes (a credential fetch, a Kerberos session with
a 10-second open and 15-second operation timeout, base 526); no figure was measured (A13).

### 3.3 Exchange Online's types: the closed table

| N1 `RecipientTypeDetails` | Recipient Type shown | N2 asked | Mailbox Location |
| --- | --- | --- | --- |
| `UserMailbox` | Mailbox | yes | Cloud |
| `SharedMailbox` | Shared mailbox | yes | Cloud |
| `RoomMailbox` | Room mailbox | yes | Cloud |
| `EquipmentMailbox` | Equipment mailbox | yes | Cloud |
| `SchedulingMailbox` | Scheduling mailbox | yes | Cloud |
| `LinkedMailbox` | Linked mailbox | yes | Cloud |
| `DiscoveryMailbox` | Discovery mailbox | yes | Cloud |
| `GroupMailbox` | Microsoft 365 group | no (A5) | Cloud; mailbox rows "Not read for this recipient type" |
| `TeamMailbox`, `PublicFolderMailbox`, `ArbitrationMailbox`, `AuditLogMailbox`, `AuxAuditLogMailbox`, `MonitoringMailbox`, `SupervisoryReviewPolicyMailbox` | Site mailbox; Public folder mailbox; System mailbox (`<raw>`) | no | Cloud; mailbox rows "Not read for this recipient type" |
| `MailUser` | reconciled with on-premises (3.4) | no | 3.4 |
| `GuestMailUser` | Guest | no | Not applicable |
| `MailContact` | Contact | no | Not applicable |
| `MailForestContact` | Contact (another forest) | no | Not applicable |
| `MailUniversalDistributionGroup` | Distribution list | no | Not applicable |
| `MailUniversalSecurityGroup` | Mail-enabled security group | no | Not applicable |
| `MailNonUniversalGroup` | Distribution group (non-universal) | no | Not applicable |
| `DynamicDistributionGroup` | Dynamic distribution list | no | Not applicable |
| `RoomList` | Room list | no | Not applicable |
| `PublicFolder` | Mail-enabled public folder | no | Not applicable |
| any other value | "Not a type this app names: `<raw>`", unless N2 found a mailbox whose own type the table holds (3.2, N2) | yes | N2 found: Cloud. N2 found none and `IsDirSynced` false: Not applicable (Exchange reports no mailbox and nothing on-premises behind it). Otherwise on-premises is asked as for `MailUser` in 3.4: an on-premises mailbox type names it and places it On-Premises; every other answer is Could not be determined. Never named from the unknown word |
| none reported | "Exchange reported no type", with the same exception | yes | as the row above |

**Corroboration by N2, for the seven mailbox rows.** N2 found: as shown. Exchange's not-found: a disagreement (D4 in
3.5). N2 failed: N1's answer stands - Exchange Online typed it as a mailbox of its own - so the location stays "Cloud",
with the mailbox rows "Could not be read" as today (aud-a54).

The strings are Exchange's documented `RecipientTypeDetails` names, from knowledge, not checked against a live tenant
(A8). A value outside the table is shown as Exchange gave it and is never mapped to the nearest label.

The labels for the "Not applicable" rows all take "a" in the location note (3.5), so no article logic is needed.

### 3.4 A mail user, reconciled with on-premises

Exchange Online's `MailUser` covers three different things: an on-premises mailbox seen from the cloud (the owner's
case), a mail user whose mail goes to an outside address, and a mail user caught between states. Only the side that
holds the object can say which.

| N1 `IsDirSynced` | N4, on-premises `RecipientTypeDetails` | Recipient Type | Mailbox Location |
| --- | --- | --- | --- |
| false | not asked | Mail user | Not applicable |
| true or not reported | `UserMailbox`, `SharedMailbox`, `RoomMailbox`, `EquipmentMailbox`, `LinkedMailbox`, `LinkedRoomMailbox`, `DiscoveryMailbox` | Mailbox, Shared mailbox, Room mailbox, Equipment mailbox, Linked mailbox, Linked room mailbox, Discovery mailbox | On-Premises |
| true or not reported | `MailUser` | Mail user | Not applicable |
| true or not reported | `RemoteUserMailbox`, `RemoteSharedMailbox`, `RemoteRoomMailbox`, `RemoteEquipmentMailbox`, `RemoteTeamMailbox` | Could not be determined (D1) | Could not be determined |
| true or not reported | any other type, or none | Could not be determined (D2) | Could not be determined |
| true or not reported | Exchange's not-found | Could not be determined (D3) | Could not be determined |
| true or not reported | could not be asked | Could not be determined (U1) | Could not be determined |
| true or not reported | two rows | Could not be determined (U2) | Could not be determined |

**The not-found row is deliberately not "Mail user".** A recipient synced from a directory the configured on-premises
server does not serve - another forest, another organisation's Exchange - is not found there and may still be a mailbox.
That is Architectural Invariant 7 applied: the answer must not rest on this deployment having one forest. Today's code
reads the same case as "both sides hold no such mailbox" (base 729).

### 3.5 Disagreement and unavailability: what the page says

Recipient Type reads "Could not be determined" in every row below, and Mailbox Location reads the same.

| Case | Small print under the type | Warning in the banner |
| --- | --- | --- |
| D1 | `Exchange Online: MailUser; on-premises: RemoteUserMailbox` | "Exchange Online and on-premises Exchange disagree about {address}: on-premises Exchange holds it as {onprem}, a mailbox in Exchange Online, and Exchange Online holds no mailbox for it ({cloud})." |
| D2 | `Exchange Online: MailUser; on-premises: {onprem or "no type"}` | "Exchange Online and on-premises Exchange disagree about {address}: Exchange Online holds it as {cloud}, on-premises Exchange as {onprem}." |
| D3 | `Exchange Online: MailUser; on-premises: no such recipient` | "On-premises Exchange holds no recipient at {address}, though Exchange Online reports it as synced from an on-premises directory; it may be held by a directory this module's on-premises server does not serve." When `IsDirSynced` was not reported: "...though Exchange Online did not report whether it is synced; ..." |
| D4 | `Exchange Online: {cloud}; Get-Mailbox: no mailbox` | "Exchange Online reports {address} as {cloud} but returned no mailbox for it." |
| U1 | `Exchange Online: MailUser; on-premises: not read` | today's "Could not determine mailbox location: {reason}" (3.2, N4) |
| U2 | `Exchange Online: MailUser; on-premises: more than one recipient` | "On-premises Exchange returned more than one recipient for {address}." |

**The other location words.** "Not applicable" carries the note "A {label, first letter lower-cased} has no mailbox." A
mail user's adds "Its mail goes to another address." Its badge is a neutral style, not the warning colour. "Could not be
determined" keeps the warning colour.

**The small print, outside the undetermined cases:** `Exchange Online: {cloud}` when on-premises was not asked;
`Exchange Online: MailUser; on-premises: {onprem}` when it was. It is in the default view, not behind a switch: it is
what the row showed before (R5).

Every case above is a lookup that succeeded with a warning, as a failed statistics read is today, so its audit result
stays Success (section 5).

### 3.6 What every row shows

Columns: C, an Exchange Online mailbox; O, an on-premises mailbox; N, a recipient with no mailbox (list, group, contact,
guest, mail user, public folder, room list); G, a Microsoft 365 group or another Exchange-held type of 3.3; U, could not
be determined. "As today" includes "Could not be read" where a read failed (aud-a54) and "N/A" where Exchange reported
nothing (aud-a23d).

| Row | C | O | N | G | U |
| --- | --- | --- | --- | --- | --- |
| Display Name | as today | as today | as today | as today | as today |
| Primary Email | Exchange's primary (3.7) | the same | the same | the same | the same |
| Recipient Type | label and small print | label and small print | label and small print | label and small print | "Could not be determined" and small print |
| Mailbox Location | Cloud | On-Premises | Not applicable, with its note | Cloud | Could not be determined |
| Created | as today, plus " (Exchange Online's copy)" for a synced recipient | the same | the same | the same | the same |
| Last Logon | as today | Not read for on-premises mailboxes | Not applicable | Not read for this recipient type | Not read |
| Mailbox Size | as today | as today (the on-premises read) | Not applicable | Not read for this recipient type | Not read |
| Deleted Item Size | as today | Not read for on-premises mailboxes | Not applicable | Not read for this recipient type | Not read |
| Total Size | the sum only when both parts were read, else "N/A" | Not read for on-premises mailboxes | Not applicable | Not read for this recipient type | Not read |
| Item Count, Deleted Items | as today | Not read for on-premises mailboxes | Not applicable | Not read for this recipient type | Not read |
| Archive | as today | as today (aud-a68) | Not applicable | Not read for this recipient type | Not read |
| Archive Size, Deleted, Items | as today, when enabled | as today | hidden, as today | hidden | hidden |
| Forwarding | as today | Not read for on-premises mailboxes | Not applicable | Not read for this recipient type | Not read |
| Email Addresses | as today | as today | as today | as today | as today |

A size or count that is missing reads "N/A" with no unit after it. E4 replaces every "Not read for on-premises
mailboxes" with a value; until then the row says what was not done rather than stating an absence.

### 3.7 Primary Email, and which object the later reads ask about

- The row shows the `PrimarySmtpAddress` N1 returned. When the operator typed anything else (another of its addresses,
  an alias, a name - compared case-insensitively), it is followed by "(looked up as {typed})". When Exchange reported no
  primary address, the typed text is shown with "(as typed; Exchange reported no primary address)".
- N2-N5 ask about that address, so every figure on the page is about the object N1 found. An SMTP address is passed as a
  parameter value (Exchange Online) or an `-ArgumentList` value (on-premises), never as script text.
- The audit row's `target` stays the typed text, unchanged. E8 adds the resolved address.

### 3.8 What does not change, and the failure cases that do

**Unchanged:** the operator's flow; the page banner ("All lookups are logged for audit purposes.", page 20); the error and
warning boxes; the cancel path (kd-37, kd-57: page 228-242); the not-found error and its audit; every aud-a23o, aud-a54,
aud-a68 and kd-14 rule; the reported-time display (aud-a23d, aud-a23e); the 20-address list; the shared location read
and the shared size read, and their other callers (the two permission pages; `MigrationService.cs:203`).

**Changes in failure and edge cases, each named so no reader takes it for drift:**

1. A mailbox whose Exchange Online `Get-Mailbox` read fails: the location was "Unknown" (the repeated read 4a failed
   too); it becomes "Cloud", from N1's own answer, with the mailbox rows still "Could not be read".
2. On-premises remote mailbox, Exchange Online no mailbox: was "Cloud"; becomes "Could not be determined", the
   disagreement named (D1).
3. A cloud-only list, contact or mail user where on-premises is not configured or unreachable: was "Unknown" plus a
   warning about on-premises; becomes "Not applicable", no warning. On-premises is not asked.
4. A synced mail user that on-premises does not hold: was "Unknown", read by the base as "no mailbox on either side";
   becomes "Could not be determined" with the reason (D3).

## 4. Optional extras - each the owner's to accept or decline

None is in scope unless the owner names it. Each is its own slice after S4, with its own version bump. "Free" means no
read is added. Every new value that Exchange does not return reads "Not reported by Exchange", never a default.

**E1, quotas.**
- *What:* the warning, prohibit-send and prohibit-send-and-receive quotas beside the size, and the share of the send
  quota used; the archive quota beside the archive size. "Unlimited" when Exchange says so.
- *From:* the `Get-Mailbox` row N2 already returns for a cloud mailbox (`IssueWarningQuota`, `ProhibitSendQuota`,
  `ProhibitSendReceiveQuota`, `ArchiveQuota`, `ArchiveWarningQuota`, `UseDatabaseQuotaDefaults`; A10). An on-premises
  mailbox's quotas come only with E4; without it they read "Not read for on-premises mailboxes". A mailbox on its
  database's defaults says "the database default" without reading the database.
- *Cost:* free for cloud mailboxes.
- *If declined:* S4 drops "quotas" from the description and the permission text (2.5 f), so they stop promising it.
- *Recommendation:* yes.

**E2, directory facts.**
- *What:* "Synced from on-premises: Yes / No / Not reported"; the Entra object ID; alias; hidden from the address book;
  for a mail user, contact or guest, where its mail goes (the external address); last changed.
- *From:* N1's row. `IsDirSynced` and `ExternalDirectoryObjectId` are already read in production by the protected-principal
  resolver (`Services/ExchangeIdentityResolver.cs:81-115`); `Alias`, `HiddenFromAddressListsEnabled`,
  `ExternalEmailAddress` and `WhenChanged` are unverified (A2).
- *Cost:* free.
- *Recommendation:* yes. The external address is what makes "Mail user" useful.

**E3, mailbox settings (Exchange Online mailboxes).**
- *What:* archive status; whether forwarding keeps a copy (`DeliverToMailboxAndForward`, so "Forwarding" reads "x, and
  keeps a copy" or "x, forward only"); send-on-behalf delegates (`GrantSendOnBehalfTo`); last user activity
  (`LastUserActionTime`, beside Last Logon, which Exchange Online also moves for background access, A11); account
  disabled.
- *From:* N2's and N3's rows. *Cost:* free.
- **E3b, holds** (litigation hold, retention hold, in-place and compliance holds) only if named separately. A hold usually
  means a legal or HR matter. Recipient Lookup's access permission is not fail-closed: when its section access is not
  configured it falls back to the app's global allowed groups (`Modules/ModuleCatalog.cs:565-568` has no `FailClosed`;
  `ExchangeAdminWeb.Tests/SectionAccessServiceTests.cs:143`), so a hold would reach that wider audience. And the
  2026-06-30 classification of this module's reads as non-alerting rests on their exposing only address-book data
  (`.agents/decisions.md` 2026-06-30, rule 2); a hold is not that.
- *Recommendation:* E3 yes; E3b no.

**E4, on-premises mailbox parity, in one connection.**
- *What:* for an on-premises mailbox, every row 3.6 marks "Not read for on-premises mailboxes" is read: item count,
  deleted items, deleted item size and the true total, last logon, forwarding (and E1's quotas and E3's settings where
  on-premises Exchange holds them), and its database. Also an archive kept in Exchange Online for a mailbox that stays
  on-premises - a supported hybrid arrangement, which today reads "Disabled" or "Could not be read" (A12).
- *From:* N4's session continues, for an on-premises mailbox, with on-premises `Get-Mailbox` and `Get-MailboxStatistics`
  (and `-Archive`) selecting those properties, instead of the module calling the shared size read, which opens a second
  session. A cloud-held archive: Exchange Online's `Get-MailboxStatistics -Archive` for the mail user (A12).
- *Cost:* faster, not slower: one credential fetch and one Kerberos session fewer per on-premises mailbox lookup.
- *Risk:* it re-applies the aud-a23o and aud-a68 size rules in the module (Exchange's not-found is no archive; no row or
  an unparseable size is a marker, never 0) with the base's `ReadSize` (605). The shared size read stays unchanged for
  Migration, which is closed by owner order (`.agents/state.md`, Carried forward): this extra must not touch it. The tests
  that pin those rules through this module (`RecipientLookupOnPremArchiveTests`, the `RecipientLookup_*` tests in
  `ExchangeServiceBaseOnPremReadTests`, the size cases in `RecipientLookupFailureTests`) keep pinning them; the two keyed
  on the size read's credential purpose and second vault request move with it.
- *Recommendation:* yes.

**E5, on-premises details on demand.**
- *What:* a "Read on-premises details" button, shown for a recipient synced from on-premises when on-premises Exchange is
  configured. It shows the on-premises distinguished name and OU, when the recipient was created on-premises, and, for a
  cloud mailbox, how on-premises Exchange holds it ("as RemoteUserMailbox"; "as MailUser, not as a remote mailbox"; "as
  a mailbox too").
- *From:* one on-premises session; on-premises `Get-Recipient` selecting `DistinguishedName`, `OrganizationalUnit`,
  `WhenCreated` and `RecipientTypeDetails`.
- *Cost:* seconds, only when pressed (A13). Each press is its own audited lookup (action `RecipientLookupOnPremDetails`,
  the same fields), reports to the status bar, passes the page's token to its credential read, and is gated while it runs.
- *Recommendation:* yes, after E4 (they share the session code).

**E6, group details.**
- *What:* for a list, mail-enabled security group, dynamic list or room list: owners (`ManagedBy`), member count, who may
  send to it (anyone; only senders inside the organisation; only named senders), moderation and moderators, and join and
  leave rules; for a dynamic list, its filter. For a Microsoft 365 group: owners, member and guest counts, private or
  public.
- *From:* one Exchange Online read per group lookup, inside the same pooled query: `Get-DistributionGroup` (the pool
  already runs it, `Services/ConferenceRoomService.cs:346`, `:412`), `Get-DynamicDistributionGroup` or `Get-UnifiedGroup`
  (A14). Member count: `Get-DistributionGroupMember` or `Get-DynamicDistributionGroupMember` with a result size of 1,001,
  shown as "more than 1,000" at the cap (the pool already runs both, `Services/ExchangeGroupMembers.cs:127`, `:139`).
- *Cost:* one read plus a member read bounded at 1,001 rows: a 10,000-member list costs the bounded read. For a group
  synced from on-premises the membership is Exchange Online's copy, and the row says so.
- *Recommendation:* yes.

**E7, show all details.**
- *What:* a switch, off by default, listing every property each read returned (by read, name and value; a long value cut
  at a fixed length and said to be), and every email address rather than the first 20 - the page hides the rest today
  behind "...and N more" with no way to see them (page 109-116).
- *From:* the rows already read. *Cost:* free.
- *Risk:* the hold properties of E3b are left out of this list unless E3b is accepted.
- *Recommendation:* yes.

**E8, the audit row records the resolved address.**
- *What:* the lookup's audit row gains `resolvedAddress`, the primary address N1 returned, when the lookup found a
  recipient.
- *Why:* `target` is the typed text (page 250), so a search of the audit log for someone's primary address misses every
  lookup typed by alias or by another address.
- *Cost:* free in reads. An audit-shape change: README's audit field list (`README.md:1017`, "Lookup: `target`") changes
  in the same slice.
- *Recommendation:* yes.

**E9, protected-account status.**
- *What:* whether the recipient is a protected principal.
- *From:* the shared protected-principal check, which resolves the target in Active Directory through the shared
  directory-read credential.
- *Cost:* directory reads on every lookup, waiting on the directory when it is slow.
- *Risk:* it shows the protected set to everyone who can open this module, a wider audience than any write module's
  (E3b); it adds a read of data that is not address-book data, which removes the basis of the 2026-06-30 non-alerting
  classification; and the check is built to refuse writes, so its "could not be determined" answers would surface during
  any directory trouble.
- *Recommendation:* no.

**E10, Entra account details.**
- *What:* sign-in state, licences and last sign-in, from Entra.
- *From:* Microsoft Graph, which this module does not use. It would need a Graph app registration and secret of its own
  (Constitution, Credential Isolation: Graph credentials are per module), which the owner creates. Last sign-in needs the
  `AuditLog.Read.All` grant that True Last Logon already holds behind its own secret (`docs/TrueLastLogon-Plan.md`).
- *Recommendation:* no. True Last Logon covers sign-ins.

**Not proposed:** mailbox permissions (Delegation Report shows them), automatic replies (Out of Office's Check Status) and
message traces (Message Trace). Copying another module's reads here doubles their cost and splits their permission.

## 5. The Constitution's rules, applied

- **Authorization.** The page's catalog-backed policy is unchanged (`@attribute [Authorize(Policy = "RecipientLookup")]`,
  page 5; the load-time re-check, 141-146). The module is read-only: there is no write, so no pre-write re-check. E5's
  button reads under the same circuit's authorization; a revoked operator is handled by session revalidation (aud-a29b).
- **Protected principals.** Not applicable: the guard binds to the target of a write (Constitution, Protected
  Principals), and no write is added. E9 is the only extra that touches the protected-principal service; recommended no.
- **Credential isolation.** N4 uses the module's own on-premises secret (`DelineaSecretId`,
  `Modules/ModuleCatalog.cs:569-571`), as today's location read does: no new secret, no borrowing. Exchange Online reads go
  through the shared pool owned by the Exchange Online parent module (`DependsOn = "ExchangeOnline"`, 564; Constitution,
  External Integrations). **Permissions:** on-premises `Get-Recipient` must be within the module account's Exchange role
  (A3); E4's on-premises `Get-Mailbox` and statistics already run today. E6's `Get-DynamicDistributionGroup` and
  `Get-UnifiedGroup` are not run by the app today (A14). A cmdlet outside a role fails visibly as a read that failed,
  never as an absence. E10 is the only extra that needs a new credential.
- **Audit.** Today's row (`AuditService.LogLookupAction`, `Services/AuditService.cs:278-303`, called at page 250-251):
  `ts`, `user`, `ip`, `action` "RecipientLookup", `category` "Lookup", `result` Success or Failed (Cancelled through
  `FiledCancelled`, kd-57), `target` the typed text, `ticket` (none on this page), and `error` on failure. **The core does
  not change it.** A lookup whose type could not be determined is one that succeeded with a warning, as a lookup with a
  failed statistics read is today. E8 adds one field. E5 adds its own row per press, so the banner "All lookups are logged
  for audit purposes" stays true. The audit failure rule (page 247-256: an audit write that throws never becomes the
  lookup's failure) is untouched.
- **Notifications.** None. The module's reads are classified non-alerting (`.agents/decisions.md` 2026-06-30, rule 2);
  the core and E1-E8 expose Exchange data of the kind the page already shows. E3b and E9 would not.
- **Trace.** N4 writes `OnPremRecipientTypeRequested` steps as the size read writes its own; sanitized, no exception text
  (`docs/AdminModuleSpec.md`, Audit And Operation Trace Requirements).
- **Code discipline.** Module-local. The shared location and size reads are not changed; the classifier is a small pure
  helper in the module, as `CalendarFolderIdentity` and `ExchangeGroupMembers` are elsewhere.
- **Architectural Invariant 7.** No source names an environment: the tables hold Exchange's product vocabulary, the
  server comes from `OnPremExchange:ServerUri`, and the credential from the module's settings. Fixtures use
  `contoso.test`. The safety of each answer rests on Exchange's own reports, never on this deployment's shape: an
  on-premises not-found is undetermined precisely because the recipient may be held by a directory the configured server
  does not serve, and an object Exchange Online reports as cloud-only is not asked about on-premises because Exchange says
  nothing backs it there.
- **Never-do list.** None is touched: no ServiceNow, no credential reuse, no global group gate, no secret logged, no
  config fallback, no UI-only control, no deployment change.

## 6. Versioning

- **Module `RecipientLookup`:** S1 `1.2.1` -> `1.3.0` (the reconciled type, a new capability); S2 `1.3.1`; S3 `1.3.2`;
  S4 `1.3.3`. Each with its line in the descriptor's comment block (the pattern at `Modules/ModuleCatalog.cs:560-562`).
  Each accepted extra bumps once in its own slice (minor: a new field or control). Bump from what master holds when the
  slice lands.
- **Base app: none.** No shared service or shared model changes. `RecipientInfoResult` is this module's own model,
  though it sits in the shared file `Models/LookupModels.cs`: only `Components/Pages/RecipientLookup.razor` and
  `Services/RecipientLookupService.cs` use it (grep at `9241fa0d`). That is kd-59's precedent for `MessageTraceDetail` in
  the same file. The classifier and N4 live in the module; `ExchangeServiceBase`, `ExchangeIdentityResolver` and
  `AuditService` are not edited. **If an implementer finds a shared file must change** - to widen `AddOnPremRead` or the
  resolver's `IsDirSynced` parser, say - that is a scope change: back to this plan, and then a base bump.
- The base bump owed at the close of the fix-everything stream (`.agents/state.md`) is that stream's, not this plan's.

## 7. Slices

**How they run.** One agent and one compile at a time, Opus 5.5 implementers (`.agents/decisions.md` 2026-10-26). Each
slice is one commit and one finding; it compiles and passes the gate on its own; each new test is proven by mutation
(section 8); its `.agents/token-log.md` line rides in the same commit. Under the standing 2026-10-26 rulings there is no
per-slice review: the range joins the single end-of-stream review if the owner includes it. Self-review is forbidden.

**When.** Not started. By default it queues behind the fix-everything close-out (`.agents/state.md`, Now, step 6).

**S1 - what it is (symptom 1).**
- The classifier: a pure, module-owned static class (proposed `Services/RecipientClassification.cs`) holding 3.3's and
  3.4's tables and the reconciliation. It returns the label, the location class (Cloud, OnPremises, NotApplicable,
  ExchangeHeldNotRead, Undetermined), the small print, and the disagreement or unavailability reason.
- The read plan of 3.2: N2 only for mailbox and unknown types; N4 in the service, bound to the primary address, with
  today's catch shape around it; the shared location read no longer called.
- `RecipientInfoResult` gains the on-premises type, the label, the small print, `IsDirSynced`, the primary address (used
  by S3) and the location class.
- The page: Recipient Type shows the label and the small print. **Mailbox Location keeps today's three words in this
  slice** (NotApplicable and Undetermined both still read "Unknown"), so S2's wording change is its own commit. 3.8's
  failure-case changes 1-4 land here.
- The stand-in: on-premises `Get-Recipient` added to `OnPremStubs` (`ExchangeAdminWeb.Tests/MigrationStubExchange.cs:411`),
  answering from a new `OnPremRecipients` table, else from `OnPremMailboxes` (125) as `UserMailbox` and
  `OnPremRemoteMailboxes` (128) as `RemoteUserMailbox`, else Exchange's not-found. Every existing fixture keeps its meaning.
- Tests T1-T12; mutations M1-M9; the two S1 rewrites in 8.3.
- Module `1.3.0`.

**S2 - "Not applicable", "Could not be determined", and the rows (symptom 2; 2.5 b, c, d).**
- Mailbox Location's four values, their badge styles and notes (3.5).
- Every row per 3.6; Total Size only when both parts were read; "N/A" without a unit.
- Tests T13-T18; mutations M10-M13; the S2 expectation changes in 8.3.
- Module `1.3.1`.

**S3 - Primary Email, and the reads bound to it (2.5 a).**
- The row per 3.7; N2, N3 and N5 ask about the primary address.
- Tests T19-T21; mutations M14-M15. Module `1.3.2`.

**S4 - Created, and the descriptions (2.5 e, f).**
- " (Exchange Online's copy)" after Created for a synced recipient.
- If E1 is declined: "quotas" leaves the description (`ModuleCatalog.cs:554`) and the permission text (568). If E1 is
  accepted, both stay and E1 makes them true.
- Tests T22-T23; mutation M16. Module `1.3.3`.

**S5 - records.**
- `README.md`: a Recipient Lookup section under Features; there is none today. What the page shows, the type and location
  rules in a sentence each, and what "Could not be determined" means.
- This plan's status; `.agents/state.md`; the `.agents/decisions.md` entry of section 14, if not recorded at approval.
- Gate: `git diff --check`.

**Extras, if accepted:** one slice each after S4, in this order: E2, E1, E3, E8, E4, E5, E6, E7. E4 before E5 (they
share the session code); E1's on-premises half needs E4; E7 last, because it lists what the others add.

**Registries.** `ExchangeAdminWeb.Tests/ProgressRegistry.cs:2237-2250` keys this page by method name; S1-S4 add no
handler. E5's handler takes a Reports entry; E7's switch, if it is a named method, an Exempt entry (page state only).
`ExchangeAdminWeb.Tests/ClickGateRegistry.cs:93` lists the page as "tier 4, approved 2026-10-12, not started": no
line-keyed entries, so no re-anchor. E5's button is gated on its own busy flag, as Lookup is (page 31, 35). The tier-4
click-gate conversion and the approved Verbosity Sweep also touch pages like this one; whichever lands second takes
account of the other.

**Size.** Code: five files (`Services/RecipientLookupService.cs`, the new `Services/RecipientClassification.cs`,
`Models/LookupModels.cs`, `Components/Pages/RecipientLookup.razor`, `Modules/ModuleCatalog.cs`). Tests: two new files and
about four changed (the stand-in, `RecipientLookupCloudReadTests`, `ExchangeServiceBaseOnPremReadTests`,
`RecipientLookupCancellationTests`). Records: `README.md`, this plan, `.agents/state.md`, `.agents/token-log.md`,
`.agents/decisions.md`. About sixteen files.

## 8. Tests and mutation proofs

### 8.1 Pattern

The module's two existing kinds: the real `RecipientLookupService` over `MigrationStubExchange` for what is read, which
calls were made (`CallList()`) and which warnings result; and the page's compiled render tree (`Rendered(result)`,
`ExchangeAdminWeb.Tests/RecipientLookupCloudReadTests.cs:51`) for what is shown. The classifier is tested pure and
table-driven. New files: `RecipientClassificationTests.cs` (pure) and `RecipientLookupClassificationTests.cs` (service and
page). Nothing runs against Exchange, a vault or a directory.

### 8.2 New tests

**S1:**
- **T1.** Each Exchange Online mailbox type of 3.3, N2 found: its label, "Cloud", and no call starting "OnPrem".
- **T2.** Each non-mailbox Exchange Online type (lists, groups, contacts, guest, public folder, room list), synced and
  not: its label; neither N2 nor any on-premises read is made.
- **T3.** `GroupMailbox` and the other Exchange-held types: label, "Cloud", N2 not made.
- **T4.** `MailUser` with `IsDirSynced` false: "Mail user"; on-premises not asked.
- **T5 (the owner's case).** `MailUser`, `IsDirSynced` true and not reported, crossed with each on-premises mailbox type:
  its label ("Mailbox" for `UserMailbox`), "On-Premises", and the small print `Exchange Online: MailUser; on-premises:
  UserMailbox` rendered.
- **T6.** A synced `MailUser` that on-premises holds as `MailUser`: "Mail user".
- **T7.** D1-D4: "Could not be determined", the warning naming both sides' words, never a mailbox label.
- **T8.** U1 (not configured; no credentials; the session cannot be opened; the read fails; the cmdlet is missing via
  `Missing`, stand-in 112) and U2 (two rows): "Could not be determined" and today's warning words.
- **T9.** Unknown and missing types on either side: shown raw, never mapped to a label; N2 made; N2 found is "Cloud";
  N2 found none on a cloud-only object is "Not applicable"; otherwise on-premises decides as in 3.4, and any answer but
  an on-premises mailbox type is "Could not be determined".
- **T10. The vocabulary is closed.** Over the cross product of the classifier's inputs (every table value, plus an
  unknown word and none, on each side, by each sync state and each read outcome), every label returned is on a fixed list
  or is the raw-quoting form.
- **T11. The identity reaches on-premises as data.** A primary address carrying PowerShell metacharacters (an apostrophe,
  `$(...)`; both legal in an address's local part) reaches the stand-in's `Get-Recipient` verbatim, and no other
  on-premises command runs.
- **T12. Fewer reads.** A cloud mailbox lookup makes `Get-Mailbox` once (today twice); a list lookup makes it never and
  opens no on-premises session.
- kd-37's `ACancelDuringACredentialRead_SurfacesAsACancel` stays green unchanged: its first vault request is now N4's
  credential read, its second the size read's. That is the proof N4 passes the token.

**S2:**
- **T13 (the owner's second case).** A distribution list renders "Mailbox Location" as "Not applicable" with its note, and
  every mailbox row as "Not applicable"; no "Unknown", "N/A GB", "Disabled" or "None".
- **T14.** An on-premises mailbox renders "Not read for on-premises mailboxes" for Forwarding, Item Count, Deleted Items,
  Deleted Item Size, Total Size and Last Logon; Mailbox Size and Archive as today.
- **T15.** An undetermined recipient renders "Could not be determined" in both rows and "Not read" in the mailbox rows.
- **T16.** Total Size: both parts give the sum; either missing gives "N/A".
- **T17.** A Microsoft 365 group renders "Cloud" and "Not read for this recipient type".
- **T18.** "Not applicable" does not take the warning style; "Could not be determined" does.

**S3:**
- **T19.** A typed secondary address: Primary Email shows Exchange's primary, then "(looked up as ...)".
- **T20.** Every later read - `Get-Mailbox`, both statistics reads, on-premises `Get-Recipient`, the on-premises sizes -
  was made with the primary address (`CallList()`).
- **T21.** No primary reported: the typed text with its note, and the reads use the typed text.

**S4:**
- **T22.** A synced recipient's Created ends " (Exchange Online's copy)"; a cloud-only one's is as today, so
  `RecipientLookupReportedTimeTests` stays unchanged.
- **T23** (only if E1 is declined). Neither catalog text promises quotas.

### 8.3 Existing tests whose expectations change

Read, not run; the slice's gate is the authority. Any other existing test that fails is a behaviour change to explain in
the slice's record, not one to paper over.

- `RecipientLookupCloudReadTests.NoMailbox_IsNoMailbox` (100-117). It pins "Forwarding</td>None" and "Archive</td>Disabled"
  for a mail user with no mailbox anywhere - the fault S2 fixes - and its premise (N2 asked for a `MailUser`) is gone in
  S1. S1 keeps its purpose, Exchange's not-found in each shape read as "no Exchange Online mailbox", by moving it to a
  recipient whose type Exchange did not report; S2 updates its page expectations.
- `ExchangeServiceBaseOnPremReadTests.RecipientLookup_ACloudLocationReadThatFails_IsUnknown_SayingWhy` (364-375). Its
  `MailUser` no longer has `Get-Mailbox` asked. S1 replaces it with 3.8's change 1 (a `UserMailbox` whose `Get-Mailbox`
  fails is "Cloud", its mailbox rows "Could not be read") and a `MailUser` case that on-premises decides.
- `RecipientLookupCancellationTests.AVaultRequestThatTimesOutUnasked_IsStillAWarning` (175-185). S1 leaves it green ("Unknown"
  is still the word); S2 changes "Unknown" to "Could not be determined". The warning text is unchanged.

### 8.4 Mutation proofs

The repo convention (AGENTS.md, Verification; `.agents/repo-guidance.md`): apply the mutation, watch the named tests fail,
restore byte-exact (md5-checked, with a fresh mtime so MSBuild rebuilds - `.agents/state.md`, method traps), watch
everything pass. Confirm each mutation applied before trusting its result. Record each in the slice's record.

| Mutation | What it does | Must fail |
| --- | --- | --- |
| M1 | the type row shows N1's `RecipientTypeDetails` (today's service 35) | T5 |
| M2 | an on-premises not-found classified "Mail user" | T7 (D3) |
| M3 | on-premises remote mailbox with an Exchange Online mail user classified "Cloud" (base 812-813's rule) | T7 (D1) |
| M4 | an on-premises read failure classified as a type | T8 |
| M5 | `IsDirSynced` false ignored, so on-premises is asked about a cloud-only mail user | T4 |
| M6 | N2 asked for every type | T2, T12 |
| M7 | the shared location read called as well | T12 |
| M8 | N4's credential read without the token | kd-37's `ACancelDuringACredentialRead_SurfacesAsACancel("the location read", 1)` |
| M9 | the identity spliced into the remote script text | T11 |
| M10 | the NotApplicable class shown as "Unknown" | T13 |
| M11 | a recipient with no mailbox shows today's mailbox rows | T13 |
| M12 | an on-premises mailbox's unread rows shown as "None" and "N/A" | T14 |
| M13 | Total Size sums a missing part as 0 (model 145-146) | T16 |
| M14 | Primary Email shows the typed text | T19 |
| M15 | one later read made with the typed text | T20 |
| M16 | the Created note shown for a cloud-only recipient, or not for a synced one | T22 |

## 9. Acceptance criteria

- **AC1.** An on-premises mailbox (Exchange Online `MailUser`, on-premises `UserMailbox`) reads Recipient Type "Mailbox",
  Mailbox Location "On-Premises", with "MailUser" and "UserMailbox" visible in the small print.
- **AC2.** A distribution list reads "Distribution list", Mailbox Location "Not applicable", and every mailbox row "Not
  applicable"; no on-premises session is opened for it.
- **AC3.** No mailbox label appears unless Exchange reported a mailbox type from the side that holds the object.
- **AC4.** Every disagreement and every side that could not be asked reads "Could not be determined" with its reason; the
  word "Unknown" no longer appears on the page.
- **AC5.** No row states an absence ("None", "Disabled", "N/A") the module did not read.
- **AC6.** Nothing is removed: every row of 2.2 is still shown, and Exchange's raw type is still on screen.
- **AC7.** No lookup makes more reads than today (3.2's table).
- **AC8.** kd-37, kd-57, kd-14, aud-a23o, aud-a54 and aud-a68 behave as before; their tests are green.
- **AC9.** No shared file is changed; the base version is unchanged.
- **AC10.** No source file names this deployment's domains, hosts or forests.

## 10. Risks, biggest first

1. **A wrong classification.** An operator told "Mailbox" about a mail user, or "On-Premises" about a mailbox that has
   moved, works on the wrong system. The permission modules route their writes by their own location read, not by this
   page, but the operator's choice of module, and of where to look, is made here.
   - *Contained by* 3.1's rule: labels only from Exchange's affirmative report on the holding side; N2 corroborates the
     mailbox types; every disagreement and every unasked side is undetermined; the raw words are always visible; T5-T10
     cover the whole input space; M1-M4 prove the guards bite.
   - *Residual:* Exchange itself reporting a stale type, such as replication delay just after a move. The page shows what
     Exchange said, as every read does.
2. **On-premises Exchange is not available everywhere (Architectural Invariant 7).** It is discovered at runtime: a
   server configured or not, a credential or not, a session or not, the recipient found or not - each a stated outcome.
   Where no on-premises server is configured, every synced mail user reads "Could not be determined: on-premises Exchange
   is not configured", the same information today's warning gives. Cloud mailboxes, lists, contacts and cloud-only
   objects are not affected, and they stop carrying an on-premises warning (3.8, change 3). A recipient held by another
   directory is undetermined, never "Mail user".
3. **Latency.** Per 3.2's table, no case adds a round trip and most remove one. Of the extras, E5 is on demand, E6 is
   bounded at 1,001 rows, and E4 removes a session. Nothing was measured (A13).
4. **Exchange's property shapes are assumptions** (A2, A3, A10, A11, A12, A14): no live call was made. Every new value
   that does not come back reads "Not reported by Exchange", never a default, and one lookup of each type in production
   settles them (section 12).
5. **Test pins and wording.** 8.3 names the tests that change on purpose. The registries are covered in section 7. The
   tier-4 click-gate conversion and the Verbosity Sweep are approved and not started, and also touch this page.
6. **Sensitive values in extras.** E3b and E9 would show legal holds or the protected set to a module whose access falls
   back to the app's global allowed groups. Both are recommended no; E7 honours E3b.
7. **Cancellation depth.** N4 takes the token only at its credential read, kd-37's rule. The throttle wait, the session
   open and the read itself do not: that is kd-58's plan, which this plan does not change.

## 11. Assumptions and facts not established

- **A1.** Exchange Online's object for an on-premises mailbox in a hybrid deployment is a `MailUser`. Evidence: the
  owner's report; this module's fixtures model it so (`ExchangeAdminWeb.Tests/RecipientLookupOnPremArchiveTests.cs:31-32`);
  Exchange's hybrid model, from knowledge. No live call.
- **A2.** Exchange Online's `Get-Recipient` returns `PrimarySmtpAddress`, `IsDirSynced`, `RecipientType` and
  `ExternalDirectoryObjectId`. The protected-principal resolver reads all four and fails closed without the first two
  (`Services/ExchangeIdentityResolver.cs:88-105`), so guarded writes would fail in production if they were absent:
  indirect evidence. `Alias`, `HiddenFromAddressListsEnabled`, `ExternalEmailAddress` and `WhenChanged` are unverified.
- **A3.** The module's on-premises account can run `Get-Recipient`. It runs `Get-Mailbox`, `Get-RemoteMailbox` and
  `Get-MailboxStatistics` today. If not, the case is U1, in the cmdlet's own words: visible, never a guess.
- **A4.** Remoting keeps on-premises `Get-Recipient`'s not-found record shape, as aud-a23o assumes for `Get-Mailbox` and
  `Get-RemoteMailbox` (base 402-407). If it does not, D3 becomes U1 with Exchange's words: the same page outcome.
- **A5.** `Get-Mailbox` without `-GroupMailbox` does not return a Microsoft 365 group, so its not-found is expected for
  `GroupMailbox`. That is why N2 is not asked for one.
- **A6.** Exchange Online's `WhenCreated` for a synced recipient is when Exchange Online's copy was created.
- **A7.** Which of today's two "Unknown" paths the owner's distribution list took (on-premises answered that it holds no
  mailbox, or could not be asked) is not known: he did not say whether a warning showed. Both are fixed.
- **A8.** The `RecipientTypeDetails` names in 3.3 and 3.4 are Exchange's documented values, from knowledge, not checked
  against a live tenant. Any value outside them is shown raw.
- **A9.** Every lookup that reaches the type row has a recipient Exchange Online holds: the module depends on Exchange
  Online, and N1's not-found ends the lookup (service 28-32), as today. A recipient that exists only on-premises (not
  synced) is "not found", as today.
- **A10.** Exchange Online's `Get-Mailbox` returns the quota properties E1 names.
- **A11.** Exchange Online's `LastLogonTime` moves for background access, which is why E3 adds `LastUserActionTime`.
- **A12.** For an on-premises mailbox whose archive is in Exchange Online: how today's on-premises archive read answers,
  and whether Exchange Online's `Get-MailboxStatistics -Archive` reads it through the mail user.
- **A13.** No latency was measured. "Seconds" for an on-premises session is an estimate from the code's timeouts (base
  526), not a measurement.
- **A14.** `Get-DynamicDistributionGroup` and `Get-UnifiedGroup` are within the Exchange Online app's role (the app runs
  neither today), and `Get-UnifiedGroup` returns member and guest counts.
- **A15.** On-premises `Get-Recipient` returns mail-enabled recipients only, so a synced object that is not mail-enabled
  on-premises is not found there. That is D3, undetermined: the honest outcome either way.

**Not established, and why:** every live property shape (a draft makes no live call, and Active Directory is not probed
without a direct go, `.agents/state.md`, Standing owner instructions); A7; the module account's Exchange role; how the
page reads today for an on-premises mailbox with a cloud archive (A12).

## 12. Verification

**Automated**, per `.agents/repo-guidance.md`, for each slice: `dotnet build ExchangeAdminWeb.slnx -c Release`; `dotnet
test ExchangeAdminWeb.slnx` (always the `.slnx`); `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`;
`git diff --check HEAD`; `tools/Test-AsciiOnly.ps1`. No `.ps1` is touched, so the PowerShell gate is a no-op; run it
anyway. **None was run for this draft**: it is a document, and another agent held the host's one compile slot.

**Manual**, as production use (`.agents/decisions.md` 2026-10-12). One lookup of each, recording Exchange's raw words
beside the page's: a cloud mailbox; the owner's on-premises mailbox; a shared and a room mailbox on each side; the owner's
distribution list; a mail-enabled security group; a dynamic list; a Microsoft 365 group; a contact; a guest; a cloud-only
mail user; a synced mail user that is not a mailbox; a remote mailbox still being provisioned, if one exists; and a
lookup by a secondary address. These settle A1-A15.

## 13. Follow-ups logged, not authorised

Found while drafting. Each is a separate finding if pursued.

- **F1.** The shared location read answers "Cloud" when on-premises holds a remote mailbox and Exchange Online holds no
  mailbox (base 812-813): the guess this plan removes from Recipient Lookup. The two permission pages route writes by it.
  A write so routed is refused by Exchange Online visibly, so no silent harm was found. A candidate finding.
- **F2.** The base's "Unknown" carries two meanings: "both sides answered that there is no such mailbox" (729) and "no
  validated mailbox" (759-760).
- **F3.** A lookup of a recipient that does not exist is audited Failed (`RecipientLookupFailureTests.cs:209-227` pins it).
  Whether "not found" is a failed lookup or an answer has never been asked. Left as it is.
- **F4.** `docs/AdminModuleSpec.md` reads "Version: 2.0 / Last verified against code: commit cd83823 (2026-06-25)" against
  base `2.35.0`. Already listed in `.agents/state.md`, Blockers.
- **F5.** `README.md` has no feature section for Delegation Report or Out of Office either. S5 adds only this module's.

## 14. Records on approval, and status log

**Decision to record in `.agents/decisions.md` on approval.** Title: *Recipient Lookup says what a recipient is, and
where its mailbox is.* It should carry the owner's request verbatim (section 1); the rule of 3.1; the readings R1-R5 as
he leaves them; and the extras he accepted and declined, by number.

**Status log:**

- 2026-10-27: drafted from the owner's request, against local master `9241fa0d`. Not approved.
- 2026-10-28: approved (`.agents/decisions.md` 2026-10-28): the core and extras 1, 2, 3 without 3b, 7, 8. Implementation
  started on branch `feat/recipient-lookup-accuracy` (worktree `rla`, from `01bf455e`) by one Opus 5.5 agent. Before any
  code, "Displayed fields per recipient kind" was written below the Exec summary, and README.md gained the Recipient
  Lookup section saying the same for operators.
- 2026-10-28, **S1 landed** (what it is): `Services/RecipientClassification.cs` (3.3, 3.4, 3.5's words); Exchange Online's
  `Get-Mailbox` only where its answer is open; the module's own on-premises `Get-Recipient` (N4) in place of the shared
  location read, its address an `-ArgumentList` value; the type row with Exchange's raw words beneath; Recipient Lookup
  1.3.0. Stand-in: on-premises `Get-Recipient` over `OnPremRecipients`, then `OnPremMailboxes` as `UserMailbox` and
  `OnPremRemoteMailboxes` as `RemoteUserMailbox`; a session counter. 8.3's two S1 rewrites made. Gate: Release build 0
  errors (19 known warnings); 7914 tests passed, 0 failed; format, `git diff --check`, ASCII lint clean. Mutations, each
  confirmed applied by its MD5, its tests failing, then restored byte-exact with a fresh mtime (MD5 equal to the original:
  page `596682828911129e9e99609bd3b9f1f0`, classifier `fb33400476648a8221d790808bc7f608`, service
  `367c06f5c5010446139262fac01c7e99`); counts are of the 215 Recipient Lookup tests run:

  | Mutation | Mutated MD5 | Failed | Including |
  | --- | --- | --- | --- |
  | M1 type row shows N1's `RecipientTypeDetails` | `8b13950ca856d9feb892564973149d03` | 43 | every T5 case |
  | M2 on-premises not-found is "Mail user" | `c75b98ede1af9f4530744bec7e957b0a` | 4 | T7 (D3), both sync states |
  | M3 on-premises remote mailbox is "Cloud" | `41aed1cf3991392fc359292d1c142060` | 4 | T7 (D1) |
  | M4 a side that could not be asked is "Mail user" | `68380745de53b5a5d0df6061cd473d05` | 7 | every T8 (U1) case |
  | M5 `IsDirSynced` false ignored | `39c517cee71d9af7ca2ab02406a48ef1` | 10 | T4 |
  | M6 `Get-Mailbox` asked for every type | `84c5f6066074b107d12aacb82c103d48` | 32 | T2, T12 |
  | M7 the shared location read called as well | `767165d29ae996bfb89775fe4316beb4` | 48 | T12 |
  | M8 N4's credential read without the token | `18612efd63ecaa25dd611d7b5f39a0a9` | 3 | kd-37's `ACancelDuringACredentialRead_SurfacesAsACancel("the location read", 1)` |
  | M9 the address spliced into the remote script | `4110feef0dd3c61cf57856b79f7c9aab` | 1 | T11 |
- 2026-10-28, **S2 landed** ("Not applicable", "Could not be determined", and the rows): "Unknown" retired; the location
  badge per 3.5 with the "Not applicable" note; every mailbox row per 3.6 and the display section's settlements 1 and 2
  ("Could not be read" for a statistics read or on-premises size that failed, `StatisticsUnreadable`; an on-premises
  archive's Deleted and Items "Not read for on-premises mailboxes"); Total Size a sum only when both sizes were read; "N/A"
  with no unit; Recipient Lookup 1.3.1. 8.3's S2 expectation changes made (`NoMailbox_IsNoMailbox`'s page rows,
  `AVaultRequestThatTimesOutUnasked_IsStillAWarning`'s word). Tests T13-T18 and four more (each kind's note; an
  on-premises size that could not be read; a cloud mailbox whose statistics, and one whose mailbox read, failed). Mutations
  (restored byte-exact; originals: classifier `a5d9ef47fb4e4f4c0dbbe3037db14859`, page `92f4631b80b8d3d304b04395ecb6d20e`,
  model `2c7c9d424e8f129c09f2fe5a02a4ac4a`; counts of 234):

  | Mutation | Mutated MD5 | Failed | Including |
  | --- | --- | --- | --- |
  | M10 the NotApplicable class shown as "Unknown" | `d53fc9f66fb4fa206c2e3e62ecc42a3e` | 10 | T13, T18 |
  | M11 a recipient with no mailbox shows today's mailbox rows | `3190e7cd516ccee78c07693a8e97929d` | 4 | T13 |
  | M12 an on-premises mailbox's unread rows shown as "None" and "N/A" | `87b231dfede711ac3d7a8ded7252c9cf` | 1 | T14 |
  | M13 Total Size sums a missing part as 0 | `8537284d4ede51aa9320464319a9c244` | 2 | T16, both missing-part cases |
- 2026-10-28, **S3 landed** (Primary Email): the row shows Exchange's primary address, then "(looked up as ...)" when the
  typed text was anything else, or the typed text "(as typed; Exchange reported no primary address)"; N2, both N3 reads
  and N5 ask about the primary address; Recipient Lookup 1.3.2. Tests T19-T21 and the case-only control. Mutations
  (restored byte-exact; originals: page `b3c3684ceead807eb837f711ddcd967d`, service `85d0cb35e1e0fa39d3ecb44c9ed83453`;
  counts of 240):

  | Mutation | Mutated MD5 | Failed | Including |
  | --- | --- | --- | --- |
  | M14 Primary Email shows the typed text | `78c63ae3fa12cb461bd7ce7ad598f204` | 3 | T19 |
  | M15 the Exchange Online statistics read made with the typed text | `cfc8bcd988ffb854acbeee813d79e7e3` | 1 | T20, in the cloud |
  | M15, second form: the on-premises size read made with the typed text | `cf7bd66d5ad44f11ac6635594a5c2888` | 1 | T20, on-premises |
- 2026-10-28, **S4 landed** (Created): " (Exchange Online's copy)" after Created for a recipient Exchange reports as synced
  (`IsDirSynced` true); a cloud-only one's, and one whose sync state was not reported, read as before, and
  `RecipientLookupReportedTimeTests` is unchanged and green. Extra 1 was taken, so the description and the permission text
  keep "quotas" (E1 makes them true) and T23 does not apply. Recipient Lookup 1.3.3. Tests T22 and the no-time control.
  Mutations (restored byte-exact; original page `fdeaee9e481572fc315bd9cc64983784`; counts of 244):

  | Mutation | Mutated MD5 | Failed | Including |
  | --- | --- | --- | --- |
  | M16 the note for a cloud-only recipient, not for a synced one | `d19812384d88509f247a2812fa10c76b` | 2 | T22, synced and cloud-only |
  | M16, second form: the note when the sync state was not reported | `4a84bbec9505142b83ba8c18bd159525` | 1 | T22, not reported |
- 2026-10-28, **E2 landed** (directory facts): Synced from On-Premises, Entra Object ID, Alias, Hidden from Address Book,
  External Address (a mail user's, contact's or guest's - an on-premises mailbox's cloud mail user included - else "Not
  applicable"), Last Changed (with the copy note when synced), each "Not reported by Exchange" when absent; from N1's row,
  no read added; Recipient Lookup 1.4.0. New file `RecipientLookupExtrasTests` (14 tests). Mutations (restored
  byte-exact; originals: classifier `aac26e744e19a476ebf2a50f5ec376d5`, page `c4c7e233315536cc8c99c368f4bdeeca`, service
  `4b647c04bf0b083f1b71c6a3113f10f6`; counts of 258):

  | Mutation | Mutated MD5 | Failed | Including |
  | --- | --- | --- | --- |
  | ME2a a mail user's external address withheld | `d59432536f90f694977b3142b5714be2` | 3 | the on-premises mailbox's, the mail user's |
  | ME2b an external address shown for every kind | `92294670ac7bf086f8d80347f790cb22` | 4 | every "Not applicable" case |
  | ME2c a fact Exchange did not return shown as "No" | `49d947ca53eab9a22bc0f21bacb62256` | 1 | the not-reported test |
  | ME2d Last Changed not read | `f487d8c08e07393419dffa6951f6ceb9` | 3 | both Last Changed cases |
