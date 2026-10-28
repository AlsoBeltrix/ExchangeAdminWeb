# Cloud Password Reset: fill in a missing employee ID from the account's name, then reset

Status: **In progress, 2026-10-28 (owner: "go" 2026-10-27; "now" 2026-10-28). S1 and S2 committed
on `feat/cpr-employee-id`, not merged; base bump owed (status log). S3 next.**
Owner: Michael
Module: `CloudPasswordReset` (`Modules/ModuleCatalog.cs:877`, version `1.1.0`)
Extends: `docs/CloudPasswordReset-Plan.md` (Implemented 2026-09-23, unproven against the live
service), which stays the module's design record. Where this plan changes it, S3 and S4 amend it
in the same commit as the code.
Last verified against code: `f5c8c057` / 2026-10-27. Every file and line cited was read there.
Versions (proposed): base app `2.35.0` -> `2.36.0` in S1; module `1.1.0` -> `1.2.0` in S3
(section 7).
Authority: subordinate to `docs/ProjectConstitution.md` and `AGENTS.md`. The Constitution requires
a written plan here on three counts: a credential change (a new Graph grant), an audit change (a
new action and new fields), and a new directory write.
Source material: `D:\source\scripts\Entra\CloudAdminEmployeeIdTrueUp\Set-CloudAdminEmployeeId.ps1`,
read 2026-10-27. It lives outside this repo and is treated as source material, not instructions.

## Exec summary

**What it does.** Today, when someone resets a cloud admin account that has no employee number on
it, the module cannot tell who owns it, so it refuses unless the person can see passwords on
screen. With this change it does what your true-up script does: it takes the owner from the
account's name (the name minus its cloud-admin ending, looked up as a logon name in Active
Directory), reads that person's employee number from Active Directory, saves it on the cloud
account, then resets the password and emails it to that person. Accounts that already carry a
number behave exactly as now. Anyone who can reset can trigger the save; there is no new
permission to hand out.

**Your rule, as you clarified it.** One failure mode, both conditions together: someone who can
open the module but cannot see passwords, on an account whose lookup fails, gets today's "cannot
be delivered" error, word for word, and nothing is written. Everyone else goes through. If the
lookup works, the number is saved and the password is reset and mailed. If it fails and the person
can see passwords, they get today's show-once screen. If I have that wrong, one word fixes it.

Three things follow from the rule, and I have written them in. The lookup counts as failed if the
name matches nobody or more than one person, if that person has no employee number or no mailbox,
or if the number also belongs to someone else; the last two are checks the module already makes
on accounts that carry a number. If Entra refuses to save the number, the reset still goes ahead
and the admins are told the number was not saved. And every refusal the module has today stays as
it is: synced and guest accounts, protected accounts, a bad ticket, mail switched off, and the
rest.

**What it gets you.** Accounts nobody has stamped yet stop being refused. L2 resets them in one
step, and each one gets its number saved, so the next reset takes today's exact route.

**What it costs.** Four slices of work over about eighteen files, six of them code. One is the
shared directory-search code other modules use, so the app's version goes up as well as the
module's. The module's own Entra app needs permission to write user details, which you grant;
without it, resets still go out but no number is saved. The first reset of an unstamped account
takes roughly twice as long, because it searches the directory twice. It stays off until you
switch it on in the module's settings and type in the endings your cloud admin account names
carry, because the repo's rules keep this environment's naming out of the code.

**The biggest risk.** A name is evidence, not proof. If an account's name points at the wrong
person, for example because the logon name it was built from now belongs to someone else, the
module mails that admin password to them. It also saves their number on the account, so later
resets go there too. Three things limit this: the operator sees who the name points to before
confirming, a name matching two people is refused, and every save gets its own audit record and
admin email. They do not rule it out.

**What approving authorises.** The four slices in section 8, with their version bumps and
documentation, and nothing else. Granting the Entra permission, deploying, and switching the
feature on stay with you.

## 1. The request, the clarification, and how this plan reads them

Owner, 2026-10-27, verbatim:

> look at D:\source\scripts\Entra\CloudAdminEmployeeIdTrueUp\Set-CloudAdminEmployeeId.ps1 and see
> if we can integrate that with the cloud password reset module so if the employee number is not
> already present, a process similar to what this script does will add it and then do the reset
> and mail the password to the correct target. the only declination should be if 1. the user
> making the request does not hold the privilege AND 2. the lookup here fails.

Owner, the same day, clarifying the last sentence, verbatim:

> to clarify, the failure mode is that 1. a user with permission to load the module but not to
> view the password AND 2. the lookup fails, the existing cannot process error holds. prove to me
> that you understand

**Wording rule (owner, 2026-10-27).** Describe permissions by what a person can do - "can open the module", "cannot see the password" - never by a permission name with "but not" in front of it; the owner read "holds X but not Y" backwards and so will others.`n`n**The reading.** One failure mode, both conditions together. "Permission to load the module" is
the main permission `CloudPasswordReset`. "Permission to view the password" is the granular
`CloudPasswordResetReveal`. "The existing cannot process error" is the refusal the page already
gives an operator without the reveal permission when no destination resolves (quoted in 3.3).
For an account with no employee ID:

| Operator | Lookup succeeds | Lookup fails |
|---|---|---|
| Can open the module AND can see the password (`CloudPasswordReset` with `CloudPasswordResetReveal`) | Number saved, password reset, mailed to the owner | Today's reveal override: reset, shown once, nothing mailed, nothing saved |
| Can open the module, CANNOT see the password - the L2 tech (`CloudPasswordReset` alone) | Number saved, password reset, mailed to the owner | **The existing refusal, verbatim. The only decline on the new path.** |
| Cannot open the module | Never gets here: access-denied at load, and a direct event invocation is refused as `PermissionDenied`, as today | Same |

The drafting brief proposed reading the "AND" as two separate refusal reasons. The owner's
clarification supersedes that, and this plan follows the owner.

**Four consequences of the rule. Each is a reading this plan makes:**

1. **"The lookup" means the script's lookup plus the module's existing destination checks on the
   number it finds.** It fails if the fill-in is switched off, or the name matches no directory
   account, or matches more than one. It also fails if the matched account has no employee ID,
   if another account carries the same ID too, or if the ID leads to a different account. And it
   fails if the owner has no mailbox, or if a directory search did not complete. The mailbox,
   shared-ID and search-failure checks are rules the module already applies to a stamped account
   (`DestinationNoMailbox`, `DestinationAmbiguous`, `DestinationLookupFailed`;
   `docs/CloudPasswordReset-Plan.md`, "Where the password goes"). Applying them to a number the
   lookup found keeps intended functionality. It does not add a decline.
2. **A failed save is not a decline.** The rule names no other reason to stop, and the destination
   is verified before the save (4.2). So the reset and the mail go ahead, and the failed save is
   audited and alerted on its own.
3. **"Only" governs the new path.** The new path adds no reason to decline of its own. Every
   refusal the module has today stays (section 5). None is removed or loosened.
4. **The save rides the reset permission.** There is no new operator permission (section 6).

**What this supersedes, named so no later reader takes it for drift:**

- `.agents/decisions.md` 2026-09-14, the runtime owner investigation entry: *"A maintained owner
  map and employeeId backfilling remain rejected approaches."* This is superseded **for
  backfilling only, and only for the one account being reset, at the moment of the reset.** The
  owner map stays rejected. Nothing is stored except the number on the account itself, which is
  the directory's own field.
- `docs/CloudPasswordReset-Plan.md`, "What is not built": *"the owner's employeeId true-up files
  are how the directory got populated and are **not part of this module** (owner, 2026-09-23)."*
  The files stay out, and no CSV is read. The script's per-account process comes in.
- `.agents/decisions.md` 2026-09-11 (*"if we cannot get a 100% working match, then matching is off
  the table"*), and the module plan's revision note that name corroboration "is not coming back".
  This plan **brings a name-based match back, narrowly**: the name proposes a number, and the
  number decides the destination under the module's existing exact rules. The owner's 2026-10-27
  rule accepts that the name match fails for some accounts and names the outcome. No
  corroboration rule returns. The script has none, and its own README makes the operator's reading
  of the shown owner the check (4.5).

**Architectural Invariant 7, environment neutrality.** The script hard-codes the AD domain it
searches (`Set-CloudAdminEmployeeId.ps1:71`), its cloud domain (`:72`), and the `-CLD` and `_CLD`
endings (`:171`). None of these can enter source:

- The module searches the forest its host belongs to, discovered at runtime the way the existing
  employee-ID lookup does it (`Services/ADEmployeeIdLookup.cs:177-218`).
- It ignores the account's domain.
- The endings are a module setting with no default, entered by whoever enables the feature (4.3).

The safety argument (4.7) rests on the lookup's own fail-closed checks and on the operator seeing
the inferred owner. It does not rest on how this forest or its names are shaped. What remains
environment-shaped, that a name points at its owner at all, is stated as the biggest risk
(section 11), not argued away.

**Judgement calls made in this draft.** Each can be reversed in a word, and none changes what was
asked:

- J1: the lookup includes the existing destination checks (consequence 1 above).
- J2: a failed save does not stop the reset (consequence 2).
- J3: the feature is off until switched on, and the endings are configured, not coded (4.3).
- J4: no new operator permission (section 6).
- J5: the module's own app registration gets the write grant, rather than a second registration
  (4.6).
- J6: the script's "disabled target" doubt is not carried over. The module resets disabled
  accounts today, and the rule names no other decline.
- J7: the save never overwrites. It runs only when the account has no number, and the script's
  `-Force` has no counterpart.
- J8: no read-back after the save (4.4).
- J9: no automatic name corroboration and no check that the owner's account is enabled. Both are
  consistent with the current module and the supersessions above.
- J10: no notification to the affected user for the save (4.7).
- J11: no operation trace is added (4.8). The module has none today.
- J12: the panel gains one line saying why a lookup failed. The existing refusal text is
  unchanged.

## 2. What the script does (source material)

`Set-CloudAdminEmployeeId.ps1` is 410 lines. A README sits beside it, and 47 Pester tests (by the
README's count) are in `D:\source\scripts\Entra\tests\Set-CloudAdminEmployeeId.Tests.ps1`. It sets
an Entra cloud admin account's `employeeId` to its owner's on-premises AD `employeeID`, for one
account or for a CSV of them.

**What it looks up, and where:**

- **Active Directory, for the owner:**
  `Get-ADUser -Server $Server -LDAPFilter ... -Properties employeeID, displayName, userPrincipalName, sAMAccountName`
  (`:111`).
  - `$Server` defaults to one named domain (`:71`), and nothing else is searched.
  - A bare name is looked up as `(sAMAccountName=...)`, and a value containing `@` as
    `(|(userPrincipalName=...)(mail=...))` (`:110`).
  - Exactly one match is required. None, or several, is recorded as a reason rather than thrown
    (`:112-114`).
  - It escapes `\`, `*`, `(` and `)` in the value, but not NUL (`:109`).
- **Microsoft Graph, for the cloud account:**
  `GET /v1.0/users/{upn-or-id}?$select=id,userPrincipalName,displayName,employeeId,accountEnabled,onPremisesSyncEnabled,userType`
  (`:77`, `:97-101`).
  - It goes through `Invoke-MgGraphRequest` and follows the connected cloud's Graph endpoint
    (`:331-339`).
  - It retries 429, 503 and 504 up to four attempts with 2, 4 and 8 second waits (`:85-95`), and
    never swallows an authentication failure (`:79-83`).
- **No HR system.** The number comes from AD's `employeeID` attribute, and that is the script's
  only source for it.

**How it derives the ID.** There are two directions, and the module needs only the second:

1. *From the owner*, when an AD identity is given. The script finds the AD account, then looks in
   the cloud domain for `<stem>-CLD@`, `<stem>_CLD@` and `<stem>@`, where the stems are the
   owner's sAMAccountName and UPN local part. Exactly one must exist (`:117-132`, `:212-228`).
2. *From the cloud account*, when only a target is given. The script strips a trailing `-CLD` or
   `_CLD` from the UPN's local part and looks up the remainder as a sAMAccountName (`:165-174`).
   PowerShell's `-replace` is case-insensitive, so any casing of the ending is stripped (`:171`).
   With no such ending, the whole local part is the name (test "handles a bare target name with
   no suffix"). The result is labelled `IdSource=ADByTargetName`, and the AD account found is
   always printed, because it is *"an inference about who owns the account rather than a lookup
   of a name you gave ... Check it before writing"* (`:20-23`; README, "Starting from the cloud
   account").

The value is AD's `employeeID`, trimmed (`:245`), or an operator-supplied `-EmployeeId`
(`:251-259`).

**What it refuses (its "doubts"):**

- A synced target, always and with no override, because AD Connect masters `employeeId` there
  (`:273-276`).
- Unless `-Force` is given: an existing different ID, an AD value that disagrees with a supplied
  one, a disabled target, and a non-Member target (`:247-250`, `:282-289`).
- An identical existing value is reported as `AlreadySet` and not rewritten (`:277-281`).
- No AD owner, several owners, or an owner with no `employeeID` is skipped with a reason
  (`:260-268`).

**What it writes, and with what credential:**

- It writes only the cloud account's `employeeId`: `PATCH /v1.0/users/{id}` with body
  `{"employeeId":"<value>"}` (`:299-301`).
- It then reads back up to three times, two seconds apart, to tell `Updated` from
  `WrittenNotYetVisible`, and treats the latter as accepted but lagging, not as a failure
  (`:308-320`).
- Nothing is written to AD.
- It uses whatever Microsoft Graph PowerShell connection the session already holds, and never
  connects itself (`:40`, `:328-329`). Its README asks for `User.Read.All` and `User.ReadWrite.All`.
  Per `.agents/decisions.md` 2026-09-22 the owner connects through `GraphConnect` from his
  M365Connections module, which is app-only.
- AD is read under the running user's own Windows identity, with no `-Credential`.

**Other modes.** A CSV with exactly the columns `Identity,CloudUPN,EmployeeId` (`:134-147`). It
previews by default, writes with `-Apply`, and puts a timestamped report beside the input
(`:371-410`).

**Not on disk.** The README names a read-only companion, `Get-EmployeeIdRunRecord.ps1`. No such
file is in the folder (checked 2026-10-27). The port does not need it.

**What the module takes from the script, and what it leaves:**

| Script behaviour | In the module |
|---|---|
| Owner read from the cloud account's name (`ADByTargetName`) | **Ported, and the only path.** The endings come from module config, not code. |
| Name searched in one named AD domain | Searched across the host's forest, discovered at runtime. Two hits anywhere is ambiguous. |
| Value = AD `employeeID`, trimmed | Ported. |
| Write only when the account has no value | Ported and narrowed: the fill-in exists only for that case. |
| `PATCH {"employeeId": ...}` | Ported, through the module's own Graph credential, bound to the object id. |
| Read-back, `WrittenNotYetVisible` | Not ported (J8). |
| Synced target refused | Already refused earlier by the module (`SyncedAccount`). |
| Disabled and non-Member doubts | Disabled is not carried (J6). Guests are already refused by the module. |
| `-Force` | Not ported. The module never overwrites a number (J7). |
| `-Identity`, `-CloudUPN` and `-EmployeeId` escape hatches | **Not ported.** Each lets the operator steer where a password goes, which the module forbids (`docs/CloudPasswordReset-Plan.md` Scope OUT, AC3). |
| Forward search, owner to cloud account | Not needed. The operator names the cloud account. |
| CSV mode and report file | Not ported. One account per operation, no enumeration (`.agents/decisions.md` 2026-09-22). The audit log is the record. |
| Graph retry on 429, 503 and 504 | Not ported, because the module retries nothing today. A throttled save is a failed save, so J2 applies. |
| No mailbox check, no check that the number is unique | Added, by the module's existing destination rules (J1). |

## 3. The module today (read at `f5c8c057`)

### 3.1 How the target is resolved

**Preflight.** `LookupAsync` (`Components/Pages/CloudPasswordReset.razor:255-322`) calls
`ResolveTargetAsync(upn)` (`Services/CloudPasswordResetService.cs:175-240`):

- The call is `GET /users/{upn}` with `$select` covering `employeeId`, through the module's own
  `GraphDelineaSecretId` credential (`:136-164`).
- It refuses a synced account (`:212-217`), an account whose sync state is unknown (`:219-227`),
  and a guest account (`:229-234`).
- It reads the account's directory roles for display only.

The page then calls `DeriveDestination(target)` on the thread pool (page `:309`).

**Confirm.** `ExecuteResetAsync` (page `:336-544`) runs in this order:

1. Re-checks authorization (`:374`).
2. Validates the ticket (`:382`).
3. Runs the protection check on the target (`:390`).
4. Re-reads the account from Entra (`:408`) and re-derives from that fresh read (`:427`).
5. Stops if what the operator confirmed has changed (`:433-445`).
6. Applies the reveal and notification gates (`:447-460`).
7. Writes the password (`:464`) and delivers it (`:489-528`).

### 3.2 What the employee ID is for

The employee ID is how the module decides where the password goes. `DeriveDestination`
(`Services/CloudPasswordResetService.cs:346-371`):

```csharp
var employeeId = target.EmployeeId?.Trim();
DirectoryValidationResult? lookup = null;
if (!string.IsNullOrWhiteSpace(employeeId))
    lookup = _directory.FindUserByEmployeeId(employeeId);
var destination = ClassifyDestination(employeeId, lookup);
```

`FindUserByEmployeeId` (`Services/ADEmployeeIdLookup.cs:60-98`) searches every domain of the host's
forest directly, never the global catalog, with `ResultSetSize 2` per domain. If any domain fails,
the whole lookup is `Unavailable` (`:110-164`).

`ClassifyDestination` (`Services/CloudPasswordResetService.cs:392-415`) decides in this order:

1. No ID: `DestinationNoEmployeeId`.
2. Lookup unavailable: `DestinationLookupFailed`.
3. Nobody carries it: `DestinationNoMatch`.
4. Several carry it: `DestinationAmbiguous`.
5. No mail: `DestinationNoMailbox`.
6. Otherwise: the match's mail, trimmed and lowercased.

The ID is also shown on the panel (page `:94`), recorded on every audit event as
`destinationEmployeeId` (`:662`), and named in the admin alert as "Employee ID used" (`:697`).

### 3.3 What happens today when it is missing

`ClassifyDestination` returns `DestinationNoEmployeeId` without asking the directory.

- The panel's "Password goes to" row says *"No employee ID is set on this account, so its owner
  cannot be identified."* (page `:728-729`).
- `CanReset` is then true only for a reveal holder (`:226-227`). For anyone else the button is
  disabled with *"The password cannot be delivered, and you do not hold the permission to see it
  on screen."* (`:229-232`).
- A direct invocation is refused at the server-side gate (`:447-452`):

```csharp
if (revealing && !(await AuthorizationService.AuthorizeAsync(authState.User, "CloudPasswordResetReveal")).Succeeded)
{
    await RefuseAsync(upn, ticket, destination.Refusal,
        "The password could not be delivered to an owner, and you do not hold the permission to see it on screen.");
    return;
}
```

That refusal is audited as `CloudPasswordReset_Execute`, failed, with
`refusalReason=DestinationNoEmployeeId`, and the admins are alerted (`RefuseAsync`, `:547-556`).
**These two sentences are "the existing cannot process error". This plan keeps both verbatim.**

A reveal holder instead resets and sees the password once, and nothing is mailed. That is audited
as `CloudPasswordReset_Revealed` with `refusalReason=DestinationNoEmployeeId` (`:489-502`; owner
ruling 2026-09-23).

### 3.4 Every refusal path today

Listed in section 5, with what happens to each.

## 4. Design

### 4.1 Where it fits

**Preflight** (`LookupAsync`). It stays read-only and writes nothing:

1. Resolve the account. Unchanged.
2. Derive the destination: the same call, on the thread pool as now. **New, inside it:** when the
   account carries no employee ID and the fill-in is switched on, run the lookup in 4.2.
   - On success, the panel shows the number that will be saved, the directory account it comes
     from, and the address the password will go to.
   - On failure, the panel shows today's refusal plus one line saying why the lookup failed.

**Confirm** (`ExecuteResetAsync`). Today's order is kept and one step is inserted:

1. Authorization re-check.
2. Ticket.
3. Protection check on the target.
4. Fresh read of the account.
5. Derive. This now includes the lookup when the fresh read has no number.
6. Confirmed-plan comparison. This now also compares the number to be saved.
7. Reveal gate. Unchanged, and this is where the owner's failure mode lands.
8. Notifications gate. Unchanged.
9. **New: when the plan includes a save, re-check authorization, then save the number, audit the
   save and alert the admins. Continue whatever the outcome.**
10. Password write. Unchanged.
11. Delivery. Unchanged.

Every gate that can refuse runs before the first write, which is now the save. That keeps the
page's ordering rule (page `:330-335`): nothing is written for an operation that then refuses.

### 4.2 The lookup

It runs for the one account being reset, at the moment of the reset, and never for a population.

1. **The name.** Take the local part of the account's userPrincipalName **as Entra returned it on
   the read**, never the operator's typed text.
   - If it ends with one of the configured endings, compared case-insensitively, remove that
     ending once. If more than one ending matches, remove the longest.
   - Otherwise use the whole local part.
   - Make no other change: no punctuation folding, name-order handling or numeric-suffix rules
     (J9).
   - A blank result is `OwnerNotFound`, without a query.
2. **The owner.** Find the directory user whose `sAMAccountName` equals the name.
   - Search every domain of the host's forest exactly as `FindUserByEmployeeId` does: each domain
     directly with `-Server`, `ResultSetSize 2`, and the filter value escaped with
     `ProtectedPrincipalService.EscapeLdapFilter`.
   - If any domain fails, the whole lookup is unavailable. Two hits anywhere is ambiguous.
   - Return the user's ObjectGUID, logon name, UPN, display name, mail and `employeeID`.
3. **The number.** The owner's `employeeID`, trimmed. If blank, the outcome is
   `OwnerHasNoEmployeeId`.
4. **The destination.** Derive it from that number by the module's existing rule:
   `FindUserByEmployeeId(number)`, classified as today. **The single match must be the owner found
   in step 2, with the same ObjectGUID.** The mailbox is that match's mail. This is what makes the
   address come from the number rather than from the name, and it refuses a number that another
   account also carries.

The outcomes are decided in this order by a pure classifier, testable without a forest in the
style of `ClassifyDestination`:

| # | Condition | `employeeIdFill` |
|---|---|---|
| 1 | Name blank | `OwnerNotFound` |
| 2 | Owner search unavailable, or never run | `OwnerLookupFailed` |
| 3 | Owner search found nobody | `OwnerNotFound` |
| 4 | Owner search found several | `OwnerAmbiguous` |
| 5 | Owner has no `employeeID` | `OwnerHasNoEmployeeId` |
| 6 | Number search unavailable, or never run | `OwnerLookupFailed` |
| 7 | Number search found nobody (the directory disagrees with itself) | `EmployeeIdMismatch` |
| 8 | Number carried by several accounts | `EmployeeIdShared` |
| 9 | The single carrier is not the owner, or either GUID is missing | `EmployeeIdMismatch` |
| 10 | Owner has no mailbox | `OwnerHasNoMailbox` |
| 11 | Otherwise | `Planned`, with the owner's mail as the destination, trimmed and lowercased |

An unavailable search is never read as "not found". That is Known Failure Class 3, and the rule is
already encoded in `ADDirectorySearchService.ClassifyOutcome`
(`Services/ADDirectorySearchService.cs:444`). Any outcome other than `Planned` leaves the
destination where it is today, `DestinationNoEmployeeId`, and the table in section 1 decides what
follows.

**Where it lives:**

- **Step 2** is a new public method on the shared `ADDirectorySearchService`, placed beside
  `FindUserByEmployeeId` in `Services/ADEmployeeIdLookup.cs`. It shares that method's throttle,
  runspace, forest discovery and outcome vocabulary, for the reasons that file gives (`:9-14`).
  - The per-domain loop should become one private helper that both lookups use, so their failure
    semantics cannot drift. The existing lookup's tests and source guards must stay green.
  - The owner's `employeeID` travels on `ADSearchResult` as a new optional trailing parameter with
    a null default. That is the local precedent set by `ObjectSid`, `DnsDomain` and `ObjectGuid`
    (`Services/ADDirectorySearchService.cs:963-972`).
- **Steps 1, 3 and 4, and the classifier**, live in `CloudPasswordResetService`. They are called
  from inside `DeriveDestination`, so both handlers' existing thread-pool hop and progress
  reporting cover them unchanged.

**Credential for the reads.** The reads run under the app pool's ambient identity, the same as
`FindUserByEmployeeId` (`Services/ADEmployeeIdLookup.cs:56-58`), inside the Constitution's carve-out
for an explicitly read-only operation. This inherits, and does not widen, an open item in
`.agents/state.md` (Blockers, "Unsettled"): the 2026-07-31 rationale for that carve-out rests on
this deployment's read visibility.

### 4.3 The setting

Two new module config fields, both defaulting to off:

| Key | Type | Default | Meaning |
|---|---|---|---|
| `FillMissingEmployeeId` | Boolean (checkbox) | `false` | On: an account with no employee ID gets one from the lookup, then the reset proceeds. Off: today's behaviour. |
| `OwnerNameEndings` | Text, optional | blank | Comma-separated endings that the deployment's cloud admin account names add to the owner's logon name. Blank: the whole name is the logon name. |

- **Fail closed.** Two conditions give `SettingUnreadable`, which means off and is logged at
  Error: a switch value that is not a boolean, and a module config that is unreadable or corrupt
  (`ModuleConfigService.IsModuleCorrupt`). This follows the `ValidateTickets` switch
  (`Services/TicketValidationService.cs:84-97`). An absent key is the default, `Off`.
- **Neutral wording.** Neither field's description, nor any source file outside test fixtures,
  names this deployment's endings. Whoever switches the feature on enters them.
- **Read at operation time,** not cached, so a change takes effect at the next preflight.
- **Shared across instances.** The config database is shared by dev and prod
  (`.agents/decisions.md` 2026-09-04), so switching the feature on in one switches it on in both.

### 4.4 The save

A new method, `CloudPasswordResetService.SetEmployeeIdAsync(objectId, employeeId)`:

- **The request.** `PATCH /users/{objectId}` with a body of exactly `{"employeeId":"<value>"}` and
  nothing else, through `PatchWithStatusAsync` on the module's own `GraphTokenClient`
  (`Services/GraphTokenClient.cs:196`). It is bound to the object id from the fresh read, never
  the UPN, for the reason `ResetPasswordAsync` gives (`Services/CloudPasswordResetService.cs:444-445`).
- **Outcomes:**
  - a 2xx status is `Written`;
  - any other status is `WriteRejected`, with the status in the audit error text and no response
    body;
  - a throw after the request left is `WriteIndeterminate`, never `WriteRejected` (the `cpr-8`
    lesson, `:495-513`).
- **When it runs:** only for the plan the operator confirmed (step 6 compares it), only when the
  fresh read showed no number, and never as an overwrite (J7).
- **No read-back (J8).** The script reads back to tell `Updated` from `WrittenNotYetVisible`.
  Nothing downstream here depends on visibility: the destination was verified from the directory
  before the save and never reads the saved value. The next reset's fresh read will see it.
- **Authorization is re-checked immediately before it,** because the Constitution requires every
  mutating operation to re-check immediately before the write. A failure there refuses as
  `PermissionDenied` through `RefuseAsync`, before anything is written.
- **The reset continues whatever the outcome (J2).** The result message reports what happened to
  the save: written; not saved, with the reason; or unknown.

**Residual, accepted.** Graph has no conditional write here. If another writer sets a number in
the seconds between the fresh read and the save, the save overwrites it. The window is the same as
the existing one between the fresh read and the password write.

### 4.5 The page

- **Employee ID row** (page `:94`):
  - unchanged when the account has a number;
  - with a plan: *Not set; will be set to `<number>`, from directory account `<logon name>`
    (`<display name>`)*;
  - after a failed lookup: *Not set*, plus one muted line saying why. There is one sentence per
    outcome in 4.2, written like `DestinationRefusalText`: what is wrong with the account, and what
    to do about it.
- **"Password goes to"** (`:95-111`). Unchanged. With a plan it shows the owner's address, as for
  any resolved destination.
- **Confirm prompt** (`:155-169`). With a plan it adds: *"Its employee ID will first be set to
  `<number>`, taken from `<display name>`'s directory account because this account's name points
  to them. Check that is the account's owner."* This is the script README's own instruction ("Read
  that owner line before you write"), moved to where the operator decides.
- **No new input of any kind.** There is no field for a number, an owner or an address, and
  `Page_offers_NO_destination_control_of_any_kind` stays green.
- **Unchanged, verbatim:** `WhyResetUnavailable` (`:229-232`), the reveal-gate message (`:450`) and
  `DestinationRefusalText` (`:726-739`).
- **Confirmed-plan comparison** (`:404-405`, `:433-445`). It also captures and compares the number
  the preflight said it would save, whether none or a value. A difference stops the reset with
  today's "the account changed while you were confirming" message.
- **Progress.** The reset activity goes from five steps to six (`:363-364`). The new step sits
  between "finding who the password goes to" and "resetting the password". It is reported on
  every path that gets that far, as "saving the employee ID" or "employee ID already set", so the
  size stays fixed.
- **Click gating.** The page has no line-keyed `ClickGateRegistry` entries, because it is on the
  not-converted list (`ExchangeAdminWeb.Tests/ClickGateRegistry.cs:68`). No control is added. If
  the page has gained entries by S3, re-anchor them with `.agents/playbooks/clickgate-reanchor.md`.

### 4.6 Credentials

- **Graph.** The module's own registration, through its own `GraphDelineaSecretId`. Never the
  script's connection, and never another module's registration (Constitution, Credential
  Isolation; `docs/AdminModuleSpec.md`, "For Graph API modules").
- **The grant.** Writing `employeeId` needs a permission the registration does not hold today. Its
  listed grants are `User.Read.All`, `User-PasswordProfile.ReadWrite.All` and
  `RoleManagement.Read.Directory`, plus the Privileged Authentication Administrator role
  (`docs/CloudPasswordReset.md:49-61`).
  - **Assumption A1:** the grant is `User.ReadWrite.All` (application), the one the script's
    README names for the same write. It is not established whether the directory role the
    registration must already hold covers this write without it.
  - The first live save settles it. Record what was actually needed in `docs/CloudPasswordReset.md`
    and in the module plan's prerequisites.
- **J5: one registration, not two.** The recommendation is to add the grant to the module's own
  registration.
  - A second registration just for the save would leave the reset credential's grant as it is. It
    would cost a second Delinea record, a second config field and a second token path.
  - The reset credential already holds a directory role that can take over any account in the
    tenant. Beside that, an attribute write adds little power.
  - The module doc warns against reusing another module's registration, and against widening this
    one for convenience (`docs/CloudPasswordReset.md:51-52`). This widening is for a named write,
    requested by the owner and recorded here.
- **Until the grant is in place,** every save is refused. The resets still go out (J2), and each
  fill-in alerts the admins that the number was not saved.
- **AD.** Read-only, under the app pool's identity (4.2). The module still has no AD secret and
  needs none.

### 4.7 The Constitution's rules, applied

- **Credential isolation.** See 4.6.
- **Audit of every write.** The save is its own audited action, on success and on failure (4.8).
- **Notification.**
  - Every save attempt gets an administrator notification immediately after it, through
    `EmailService.SendAdminNotificationAsync`. No new `EmailService` method is needed.
  - **The affected user is not notified of the save (J10).** That rule covers a change to a user's
    permissions or access, and an employee ID is a directory attribute. The owner receives the
    password mail when the reset delivers. The owner's 2026-10-27 kd-44 ruling ("remove to affected
    target, keep to admin") leans the same way.
- **Protected principals.**
  - The save's write target is the reset's target. The existing check (page `:390`,
    `CheckProtectionAsync` `:569-621`) runs on it before both writes and fails closed.
  - A servicer note covers both writes and rides the save's audit event too.
  - The directory account the number is read from is only read, so it is not a write target.
  - The module stays out of every scoped exception in the Constitution.
- **Fail-closed authorization.** The save rides `CloudPasswordReset`, which is fail-closed. It is
  re-checked immediately before the save, and the reveal gate is unchanged.
- **Mutations bind to immutable identifiers.** The account is bound by the object id from the
  fresh read, and the owner by ObjectGUID.
- **Environment neutrality.** See section 1.

**The safety argument, stated without facts about this environment.** The module saves a number
and mails a password only when all of these hold:

- exactly one directory account in the host's forest answers to the configured rule;
- that account carries an employee ID;
- that ID belongs to that account and to no other;
- that account has a mailbox.

The operator has been shown that account by name before confirming. Every outcome is audited and
alerted, and any unanswered question refuses. A deployment whose account names do not follow such
a rule leaves the switch off.

### 4.8 Audit, notification, log and trace

**Audit.** Category `CloudPasswordReset`, with the cloud account's UPN as the target. Every event
goes through the page's single builder (`AuditReset`, page `:634-680`), so every event keeps one
key set (module plan AC18).

- **A new action, `CloudPasswordReset_EmployeeIdSet`,** is written immediately after the save.
  `result` is success for `Written` and failure otherwise, with the safe error text on failure.
- **Three new fields appear on every event of the module.** They carry the `"n/a"` sentinel where
  they do not apply, because the writer drops nulls (`cpr-11`):

| Field | Type | Values |
|---|---|---|
| `employeeIdFill` | string | Closed list: `NotNeeded` (the account had a number), `NotAttempted` (the operation stopped before the lookup), `Off`, `SettingUnreadable`, `OwnerNotFound`, `OwnerAmbiguous`, `OwnerLookupFailed`, `OwnerHasNoEmployeeId`, `EmployeeIdShared`, `EmployeeIdMismatch`, `OwnerHasNoMailbox`, `Planned` (found, but the reset stopped before the save), `Written`, `WriteRejected`, `WriteIndeterminate`. |
| `employeeIdFillValue` | string | The number the lookup found or the save wrote. `"n/a"` until step 3 of the lookup has produced one. |
| `employeeIdFillSource` | string | ObjectGUID of the directory account the number was read from. It is immutable, the same choice `targetObjectId` makes. `"n/a"` when no single owner was found. |

- **Existing fields on the new path:**
  - `refusalReason` stays `DestinationNoEmployeeId` on the owner's failure mode and on a reveal
    over a failed lookup. The account has no number, and that is the refusal; `employeeIdFill` says
    why the lookup failed.
  - `destinationEmployeeId` keeps its meaning: the number the address was derived from. On a
    planned or saved fill-in it is the found number. After a failed lookup it is `"n/a"`, because
    the account had none.
  - On the save's own event, `passwordDelivery` is `"n/a"` and `revealUsed` is false, because that
    event is not about the password.
- **A successful reset of an unstamped account writes two events:** `CloudPasswordReset_EmployeeIdSet`
  (success, `Written`), then `CloudPasswordReset_Execute` (success, `Sent`,
  `employeeIdFill=Written`). A refused save writes `CloudPasswordReset_EmployeeIdSet` (failure,
  `WriteRejected`), then the reset's own event.
- **The module plan holds the canonical table,** "Audit fields, and Splunk"
  (`docs/CloudPasswordReset-Plan.md:682-750`). S3 adds these rows there in the same commit as the
  code, as the refusal enum's own comment requires (`Services/CloudPasswordResetService.cs:6-10`).
  Field names are frozen once shipped.

**Administrator email.**

- For the save: the target, the operator, the ticket, the number, the directory account it came
  from (logon name and display name), and the outcome.
- For the reset (`NotifyAdminsAsync`, page `:685-716`): two more lines, "Employee ID filled in"
  (the `employeeIdFill` value) and "Filled in from".
- Neither carries the password.

**App log**, at the module's documented levels (`docs/CloudPasswordReset.md`, "Logging"):

| Level | What is logged |
|---|---|
| Debug | The name tried, the ending removed, the domains searched, matches per domain, the owner's GUID and logon name, the number, the GUID comparison |
| Information | A number saved |
| Warning | Every lookup outcome that is an answer about the data: `OwnerNotFound`, `OwnerAmbiguous`, `OwnerHasNoEmployeeId`, `EmployeeIdShared`, `EmployeeIdMismatch`, `OwnerHasNoMailbox` |
| Error | `OwnerLookupFailed`, `SettingUnreadable`, `WriteRejected`, `WriteIndeterminate` |

A search that failed and one that found nobody stay distinguishable by level.

**Operation trace.** The module writes none today: neither the page nor the service calls
`OperationTraceService` (grep at `f5c8c057`), although the module plan says "the operation trace
still carries the step transcript" (`docs/CloudPasswordReset-Plan.md`, "Diagnostic logging"). The
new step is visible as the new activity step (4.5) and in the app log. This plan adds no trace
(J11), and the drift is logged in section 14.

## 5. Refusals: the complete set, and what happens to each

Nothing below is removed or loosened. Under the 2026-10-27 mandate, a refusal that is itself a bug
would be a separate finding, not part of this plan. None was found.

| Refusal | Raised at | After this plan |
|---|---|---|
| Access denied (no `CloudPasswordReset`) | Page policy and `OnInitializedAsync` (page `:3`, `:243-248`) | Stays. |
| "Microsoft Graph is not configured" | `IsAvailable` (page `:37-41`) | Stays. |
| `PermissionDenied` | Re-check before the writes (page `:374-379`) | Stays. Also raised by the new re-check before the save. |
| `TicketInvalid`, `TicketValidatorUnavailable` | Page `:382-387` | Stays. |
| `ProtectedPrincipal`, `ProtectionCheckFailed` | Page `:390-397` | Stays. Now also guards the save. |
| `GraphReadFailed` | Not configured, credentials unreadable, read failed, or sync state unknown (service `:178-227`); a non-400 write failure (service `:533-535`); the catch (page `:530-538`) | Stays. |
| `SyncedAccount` | Service `:212-217` | Stays. Runs before any lookup. |
| `GuestAccount` | Service `:229-234` | Stays. |
| `DestinationNoEmployeeId` | `ClassifyDestination` (service `:394-395`) | **The owner's change.** With the fill-in on, it is raised only when the lookup fails. The reveal override still applies, and without the reveal permission the operator gets the existing message, verbatim. With the fill-in off, exactly as today. |
| `DestinationNoMatch`, `DestinationAmbiguous`, `DestinationNoMailbox`, `DestinationLookupFailed` | Service `:400-411`, for an account that has a number | Stays, unchanged. The fill-in never runs for an account with a number and never replaces one. |
| Reveal gate: "could not be delivered ... permission to see it on screen" | Page `:447-452` | Stays, verbatim. The owner's failure mode lands here. |
| Preflight: button disabled with "cannot be delivered ..." | Page `:142-151`, `:229-232` | Stays, verbatim. |
| "The account changed while you were confirming" (audited, `refusalReason` n/a) | Page `:433-445` | Stays. Now also compares the number to be saved. |
| `NotificationsDisabled` | Page `:454-460` | Stays. |
| `GeneratorFailed`, `PasswordPolicyRejected` | Service `:466-476`, `:526-531` | Stays. |
| `WriteIndeterminate` and the delivery failure (neither is a refusal) | Page `:466-479`, `:507-521` | Stays. |

The new step adds no refusal value of its own. The two refusals it can lead to already exist:
`DestinationNoEmployeeId` through the reveal gate, and `PermissionDenied` through its re-check.

## 6. Privilege

**Recommendation: the save rides `CloudPasswordReset`, with no new permission (J4).**

- The owner's clarified rule distinguishes operators by one privilege only: seeing the password.
- The save is never a standalone action. It runs only inside a reset, only for the account being
  reset, and only when that account has no number. Its value is read from the directory, and the
  operator cannot type or choose it.
- A separate permission that every L2 operator would need to reset an unstamped account is the
  decorative-control class the module plan warns about (`idm-3`, "The population").
- In S3 the module's permission description changes to say a reset may first save an employee ID
  on the account, so whoever grants the section sees the write. `docs/AdminModuleSpec.md` requires
  the description to name what a holder can do.

**How an operator without a privilege is answered and audited:**

- **Without `CloudPasswordReset`:**
  - At load they are sent to access-denied. That is navigation, not audited by the module, as
    today.
  - A direct event invocation is refused at the re-check before any write, and again before the
    save, with *"You are not authorized to reset cloud passwords."*
  - It is audited as `CloudPasswordReset_Execute`, failure, with `refusalReason=PermissionDenied`
    and `employeeIdFill=NotAttempted`, and the admins are alerted (`RefuseAsync`).
- **The L2 tech - can open the module, cannot see the password - after a failed lookup:**
  - The operator gets the existing refusal (3.3), and nothing is written.
  - It is audited as `CloudPasswordReset_Execute`, failure, with
    `refusalReason=DestinationNoEmployeeId` and `employeeIdFill` saying why, and the admins are
    alerted.
- **With both, after a failed lookup:**
  - The operator gets today's reveal, and nothing is saved.
  - It is audited as `CloudPasswordReset_Revealed`, with `refusalReason=DestinationNoEmployeeId`
    and `employeeIdFill` saying why.

## 7. Versioning

- **Base app `2.35.0` -> `2.36.0`,** in S1. This covers `<VersionPrefix>`, `AssemblyVersion` and
  `FileVersion` (`ExchangeAdminWeb.csproj:11-15`).
  - The reason: the shared `ADDirectorySearchService` gains a public method and the shared
    `ADSearchResult` gains a field. That is shared infrastructure (Constitution, Deployment And
    Versioning).
  - It is a minor bump, as for a new shared capability; `86970a58` set `2.34.0` the same way.
- **Module `CloudPasswordReset` `1.1.0` -> `1.2.0`,** in S3, with a `1.2.0:` line added to the
  descriptor's comment block (`Modules/ModuleCatalog.cs:885-895`).
  - S2 changes service code but not behaviour, because the setting cannot be switched on until S3
    adds it (4.3). So S2 bumps nothing.
- If master has moved by the time a slice lands, bump from what master holds. This plan bumps
  each layer once.

## 8. Slices

**How the slices run:**

- One agent and one compile at a time, with Opus 5.5 implementers (`.agents/decisions.md`
  2026-10-26).
- Each slice is one commit. It compiles and passes the gate on its own, proves each new test by
  mutation (section 9), and carries its `.agents/token-log.md` line.
- Review: under the standing 2026-10-26 rulings there is no per-slice review. The range joins the
  single end-of-stream review if the owner includes it. Self-review is forbidden.

**S1: the shared lookup, a host change that lands first.**

- The new logon-name lookup beside `FindUserByEmployeeId`, the shared per-domain helper, and the
  escaped filter builder.
- `ADSearchResult`'s optional `EmployeeId`.
- Tests T1-T5, mutation rows M1-M3.
- Base version `2.36.0`.
- No module change. This is the host change landing on its own before the module depends on it
  (`.agents/decisions.md` 2026-10-22).

**S2: the service.**

- In `CloudPasswordResetService`: the setting read (4.3; absent keys mean `Off`), the name rule,
  the classifier, the lookup inside `DeriveDestination`, `SetEmployeeIdAsync`, and the fill-in
  outcome on the destination record.
- Update `DeriveDestination`'s comment ("none falls back to another destination"). The rule still
  holds for an account with a number; the fill-in is the owner-requested route for an account
  without one.
- Tests T6-T12, mutation rows M4-M10.
- No page change, no catalog change and no version bump. Behaviour is unchanged, because nothing
  can switch the feature on yet.

**S3: the page and the descriptor.**

- The page changes in 4.5 and the save in the confirm flow (4.4).
- The audit and alert changes (4.8).
- The six-step activity and its `ProgressRegistry` entry.
- The two config fields, the permission and module descriptions, and module version `1.2.0`.
- The audit-field table rows in the module plan, in the same commit (4.8).
- Tests T13-T22, mutation rows M11-M18.

**S4: records.**

- `docs/CloudPasswordReset.md`: what it does, configuration, the grant, refusals, audit and failure
  modes.
- `README.md`'s module section.
- `docs/CloudPasswordReset-Plan.md`: pointers wherever its text no longer holds ("Where the password
  goes", "What is not built", Scope, AC3, AC4, External prerequisites).
- This plan's status.
- `.agents/state.md`.
- The `.agents/decisions.md` entry in section 15, if it was not recorded at approval.
- Gate: `git diff --check`.

## 9. Tests and mutation proofs

**Pattern.** Where the logic can be pure, test it pure. Elsewhere use source-text guards, as the
module's existing tests do. `ADDirectorySearchService` is sealed and the page needs a circuit, so
the existing suite pairs pure classifiers with ordering guards
(`ExchangeAdminWeb.Tests/CloudPasswordResetWritePathTests.cs:1-15`).

**S1, the shared lookup:**

- **T1.** The logon-name filter is an exact `sAMAccountName` equality, with `*`, `(`, `)`, `\` and
  NUL escaped.
- **T2.** The lookup searches every domain from `GetForestDomains` with `-Server` per domain, and
  never the global catalog.
- **T3.** Any one domain's cmdlet error, a throw, or an empty forest gives `Unavailable`, never a
  partial answer or `NotFound`.
- **T4.** `ResultSetSize 2` per domain, and hits from two domains read as ambiguous.
- **T5.** `ADSearchResult` carries `employeeID`. Every existing `FindUserByEmployeeId` test and
  guard stays green.

**S2, the service:**

- **T6, the name rule (pure):**
  - each configured ending is stripped case-insensitively, only at the end, and only once;
  - the longest ending wins;
  - a bare name gives the whole local part, and so do blank endings;
  - the domain part is ignored;
  - a blank result performs no query.
- **T7, the classifier (pure):**
  - every row of 4.2's table;
  - an unavailable search at either step never yields a not-found outcome;
  - a missing GUID on either side is a mismatch;
  - an ambiguous result outranks the GUID and mailbox checks;
  - the planned address is trimmed and lowercased.
- **T8, the setting:**
  - an absent switch is off;
  - `true`, in any casing, is on;
  - `yes`, `1` or any other non-boolean value is `SettingUnreadable`, and so is a corrupt module
    config;
  - endings parse on commas, with whitespace trimmed and blank entries dropped.
- **T9.** An account that carries a number never reaches the owner lookup, and its outcome is
  `NotNeeded`. This is the guard that the ordinary path is unchanged.
- **T10.** With the switch off or unreadable, an account without a number triggers no directory
  query and still yields `DestinationNoEmployeeId`.
- **T11, `SetEmployeeIdAsync`,** driven through a stub `HttpMessageHandler` behind
  `GraphTokenClient`:
  - it sends `PATCH /users/{objectId}`, with the object id, not the UPN;
  - the body has exactly one property, `employeeId`, with the value byte-identical, leading zeros
    kept;
  - 204 is `Written`;
  - 400 and 403 are `WriteRejected`, carrying the status;
  - a throw after send is `WriteIndeterminate`.
  The suite already stubs handlers behind Graph clients: `ExchangeAdminWeb.Tests/GraphTokenClientTests.cs`
  and `CloudSignInServiceTests.cs`'s `GraphStub`. Follow whichever fits.
- **T12.** No production file touched by this plan names this deployment's account-name endings or
  domains.

**S3, the page:**

- **T13.** Every refusable gate precedes `SetEmployeeIdAsync(`, and `SetEmployeeIdAsync(` precedes
  `ResetPasswordAsync(`. The gates are the `CloudPasswordReset` re-check, `ValidateTicketAsync`,
  `CheckProtectionAsync`, `DeriveDestination(fresh)`, the reveal gate and
  `Email.UserNotificationsEnabled`. This extends `Every_refusable_gate_runs_before_the_write`.
- **T14.** A `CloudPasswordReset` authorization re-check sits between the last gate and
  `SetEmployeeIdAsync(`.
- **T15.** The save's outcome handling calls no `RefuseAsync(` and returns nothing before
  `ResetPasswordAsync(` (J2).
- **T16.** The save is reachable only on the planned branch, and it receives the fresh target's
  object id.
- **T17.** The existing texts are byte-identical: the reveal-gate message, both strings in
  `WhyResetUnavailable`, and the `DestinationNoEmployeeId` line of `DestinationRefusalText`.
- **T18.** The confirmed-plan comparison includes the number to be saved.
- **T19, audit shape:**
  - `RequiredExtraKeys` (`CloudPasswordResetAuditShapeTests.cs:35-47`) gains the three fields;
  - `Every_outcome_emits_an_identical_key_set` covers a save success, a save failure, a reset with
    a fill-in, a refusal after a failed lookup, and a reveal after a failed lookup;
  - an enum-to-string sweep proves every `employeeIdFill` value a path can produce is on the
    closed list;
  - the save event's `passwordDelivery` is `"n/a"`.
- **T20.** The save is audited and alerted on every outcome. Neither the audit builder nor either
  admin notification receives the password variable. This extends the two existing
  password-absence guards.
- **T21, mail target end to end.** On a planned fill-in, the address sent to, the one in
  `destinationAddress`, and the one named in the admin alert are all the number lookup's match,
  not the name lookup's. `employeeIdFillSource` is that match's GUID.
- **T22, catalog and progress:**
  - the two fields exist with the stated types and defaults;
  - the module is at `1.2.0`, and its permission description mentions the save;
  - the `ProgressRegistry` entry still holds the activity open from before the derive to after the
    last notification;
  - `GlobalProgressWiringTests.CloudPasswordResetReportsItsDirectoryLookupInsteadOfFinishingBeforeIt`
    stays green.

**Existing tests that must stay green unchanged:**

- every `CloudPasswordResetDestinationTests` case (`ClassifyDestination` is not touched);
- `Page_offers_NO_destination_control_of_any_kind`;
- `The_destination_is_re_derived_at_write_time_not_taken_from_preflight`;
- `A_destination_that_changed_during_confirmation_stops_the_reset`;
- `The_reveal_path_requires_the_reveal_permission_server_side`.

**Mutation proofs, using the repo convention.** For each guard: revert or alter the guarded line,
run the named test and watch it fail, restore the line byte-exact, touch the file so MSBuild
rebuilds, then confirm everything is green. Record each row in its slice's commit message or
record.

| Row | Slice | Mutation | Must fail |
|---|---|---|---|
| M1 | S1 | Drop the escaping from the logon-name filter | T1 |
| M2 | S1 | Let one domain's error continue the loop | T3 |
| M3 | S1 | Query the global catalog instead of each domain | T2 |
| M4 | S2 | Compare endings case-sensitively, or shortest-first | T6 |
| M5 | S2 | Map an unavailable search to `OwnerNotFound` | T7 |
| M6 | S2 | Drop the GUID equality | T7 |
| M7 | S2 | Treat a non-boolean switch as on | T8 |
| M8 | S2 | Run the lookup when the account has a number | T9 |
| M9 | S2 | Report a throw after send as `WriteRejected` | T11 |
| M10 | S2 | Add a second property to the save body | T11 |
| M11 | S3 | Move the save above the protection check | T13 |
| M12 | S3 | Remove the re-check before the save | T14 |
| M13 | S3 | Return after a rejected save | T15 |
| M14 | S3 | Change one character of the reveal-gate message | T17 |
| M15 | S3 | Drop the number from the confirmed-plan comparison | T18 |
| M16 | S3 | Omit one new field on one path | T19 |
| M17 | S3 | Skip the admin alert on a failed save | T20 |
| M18 | S3 | Send to the name lookup's mail instead of the number lookup's | T21 |

## 10. Acceptance criteria

- **AC-F1.** An account that has an employee ID behaves exactly as at `1.1.0`: no owner lookup, no
  save and the same refusals. Its audit values are the same, plus `employeeIdFill=NotNeeded`.
- **AC-F2.** With the fill-in off, an account without an employee ID behaves exactly as at `1.1.0`,
  plus `employeeIdFill=Off`.
- **AC-F3.** With the fill-in on and the lookup succeeding, any operator holding `CloudPasswordReset`
  sees the number, the account it comes from and the destination before confirming. On confirm,
  the number is saved, and the password is reset and mailed to that destination.
- **AC-F4.** With the fill-in on and the lookup failing, an operator without
  `CloudPasswordResetReveal` gets the existing refusal verbatim, nothing is written, and the audit
  records why the lookup failed. A reveal holder gets today's reveal, and nothing is saved.
- **AC-F5.** A failed or unknown save never stops the reset and is never recorded as "no change".
  It is audited and alerted.
- **AC-F6.** Every gate that can refuse runs before the save, and authorization is re-checked
  immediately before it.
- **AC-F7.** The save writes only `employeeId`, only when the fresh read had none, and only to the
  object id read. It never overwrites.
- **AC-F8.** No operator input reaches the number, the owner or the address.
- **AC-F9.** One account per operation. No query in the new code lists or filters a directory for
  more than the one name or the one number (module plan AC4a).
- **AC-F10.** An unavailable search is never read as an absence, anywhere in the lookup.
- **AC-F11.** Every module audit event carries the full key set, including the three new fields.
  Every enumerated value is on its closed list, and no password appears anywhere (module plan AC7).
- **AC-F12.** No source file outside test fixtures names this deployment's account-name endings or
  domains.

## 11. Risks, biggest first

1. **A name points at the wrong person, and the save makes it stick.**
   - *Evidence.* `.agents/research/cloud-password-owner-runtime.md` records real cases in this
     population where a name match is wrong or incomplete: AD logins that gained a numeric suffix,
     legacy admin suffixes, nicknames, and one person's logon name surviving as another person's
     alias.
   - *What happens.* When the name maps to the wrong single account, the module mails an admin
     password to that person. **It also saves their number on the account, so every later reset
     takes the ordinary exact path to the same wrong person, with nothing marking it as
     inferred.**
   - *Mitigations:*
     - the operator is shown the owner's logon name and display name, beside the cloud account's
       own display name, and told to check;
     - a name matching more than one account anywhere in the forest refuses;
     - the number must lead back to that same account and no other;
     - each save is its own audit event and admin alert, naming the account the number came from;
     - the owner email already says "If you were not expecting this, contact the IT Service Desk
       immediately", which acts as a tripwire on the receiving end.
   - *Fix path.* Correct the number on the account, in the portal or with the script's `-Force`.
   - *Not ruled out.* The rate cannot be measured, because measuring it would mean surveying the
     population, which this repo's tooling does not do (`.agents/decisions.md` 2026-09-22).
2. **Writing an identity attribute from an admin tool.** `employeeId` can matter to systems beyond
   this module: dynamic group rules, lifecycle workflows, and licensing or reporting keyed on it.
   - **Assumption A4:** nothing in the tenant grants access on the strength of an admin account's
     `employeeId`. This is not verified.
   - The script has already set the attribute on the in-scope accounts, so this adds no new kind
     of effect, but it can now happen as a side effect of a reset.
   - It is bounded by: no overwriting, one account per reset, a value that is always the
     directory's own, and an audit record and alert for every save.
3. **The directory is unavailable.** The script reads no HR system; AD's `employeeID` is the only
   source for the number.
   - An unreachable domain makes the lookup fail closed (`OwnerLookupFailed`). Non-reveal
     operators get the existing refusal, and reveal holders get today's reveal. That is the owner's
     rule exactly, and the 2026-09-23 reasoning about outages applies unchanged.
   - The cost is time: the first reset of an unstamped account roughly doubles its wait, because
     of the second forest search. The code's own comments put each search at about 30 seconds
     (`Components/Pages/CloudPasswordReset.razor:297-307`, `Modules/ModuleCatalog.cs:885-890`;
     assumption A3).
4. **The password is mailed to the wrong target by another route.** The module's design is
   unchanged here: the destination comes from the directory, and the module does not second-guess
   it.
   - The new exposure: whoever can rename a cloud admin account, or create or rename an AD account
     to match its name, can now steer the reset of an unstamped account.
   - **Assumption A6:** L2 holds neither right, in Entra or through this app's attribute editor.
     The 2026-09-14 investigation lists that as untested.
   - A rename that collides with an existing name is refused as ambiguous. A rename to a fresh name
     is the residual. It is in the same class as the module plan's accepted residual, "an operator
     who edits the directory to redirect a future reset".
5. **The write grant.** Until the module's registration can write `employeeId`, nothing is saved.
   Resets still deliver (J2), and each attempt alerts the admins. If the grant turns out to need
   more than `User.ReadWrite.All`, that is the owner's decision at the first live call.
6. **Two accounts carrying one number.** If AD stamps an owner's `employeeID` on their AD admin
   account as well (assumption A5), the existing lookup already refuses that owner as ambiguous,
   and the fill-in inherits that refusal. This plan does not change it.
7. **The module has never run live.** It is blocked on its app registration and Delinea record
   (`.agents/state.md`, Blockers). This feature can only be proven on the same first live run, and
   its manual checks join that list.

## 12. Assumptions and facts not established

- **A1.** The Graph grant that writes `employeeId` app-only is `User.ReadWrite.All`. This is taken
  from the script's README, not from the module's registration. Whether the Privileged
  Authentication Administrator role covers the write without it is unknown.
- **A2.** Entra limits `employeeId` to 16 characters, so a longer AD value is refused and becomes
  `WriteRejected`, and the reset proceeds. This comes from recollection of the Graph user resource
  documentation and is not verified here.
- **A3.** Each forest search takes about 30 seconds. This comes from the code's comments and is not
  measured here.
- **A4.** No access-granting rule in the tenant keys on an admin account's `employeeId`.
- **A5.** Whether AD admin accounts carry their owner's `employeeID` is unknown.
- **A6.** L2 cannot rename cloud admin accounts, or create or rename AD accounts or edit their
  `employeeID` or `mail`, in Entra or through this app. Untested, and the 2026-09-14 investigation
  says the same.
- **A7.** The script's own Graph connection is app-only through `GraphConnect`
  (`.agents/decisions.md` 2026-09-22). Which registration and grants it holds is not established
  here. `.agents/state.md` records a survey registration with broad grants, and whether it is the
  same app is unknown. This matters only to confirm the module must not use it.
- **A8.** The write works app-only on a cloud-only admin account given the right grant. The owner's
  2026-09-22 *"already updated the accounts in-scope"* shows it working through his connection, not
  through the module's registration.
- **A9.** `Get-ADUser -LDAPFilter "(sAMAccountName=...)"` returns user objects only, which is the
  same assumption the existing lookup makes. A computer's `sAMAccountName` ends in `$`, so it
  cannot equal a name without one.

## 13. Verification

**Automated**, per `.agents/repo-guidance.md`, run for each slice:

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`

No `.ps1` is touched, so the PowerShell gate is a no-op for this plan; run it anyway. None of these
was run for this draft.

**Manual.** These need a deployed instance and the module's registration, and none has been run:

1. On the first live save, record the grant that was actually needed (A1).
2. Take an unstamped admin cloud account whose name follows the configured rule:
   - the panel shows the number, the account it comes from and the address;
   - after confirming, Entra shows the number and the password arrives;
   - the audit log has the two events in 4.8, and the admins get two alerts.
3. As a non-reveal operator, try three cases: a name that matches nobody, an owner with no
   `employeeID`, and the switch off.
   - Each gives the existing refusal verbatim, writes nothing and audits the reason.
   - As a reveal operator, the same accounts reveal, and nothing is saved.
4. A name that matches accounts in two domains refuses. Contrive this case deliberately: it is the
   one that would otherwise mail a password to a guess.
5. Before the grant is in place:
   - the save is refused;
   - the reset still delivers;
   - the alert says the number was not saved.
6. An account that already carries a number takes the ordinary path, and the Debug log shows no
   owner lookup.
7. A synced account is refused as today, and nothing is written.
8. A garbage switch value turns the feature off and logs at Error.

## 14. Follow-ups logged, not authorised

These were found while drafting. Each is a separate finding if pursued.

- The module plan says the operation trace carries the step transcript, but no code in the module
  calls `OperationTraceService`.
- The module plan lists a `CloudPasswordReset_Preview` audit action that nothing emits. The
  preflight read writes no audit record, while the Constitution describes the app's reads as all
  auditing.
- `Services/ADEmployeeIdLookup.cs:24` says the lookup finds "the single enabled directory user".
  The filter does not test `Enabled`, and nothing reads the `Enabled` property it requests. Either
  the comment is stale (the 2026-09-14 revision dropped the leaver rule) or a check is missing.
- `docs/AdminModuleSpec.md`'s header reads "Version: 2.0 / Last verified against code: commit
  cd83823 (2026-06-25)", against app `2.35.0`. This is drift per the repo guidance, and was not
  acted on.
- The script's README names `Get-EmployeeIdRunRecord.ps1`, which is not on disk.

## 15. Records on approval, and status log

**Decision to record in `.agents/decisions.md` on approval.** Title: *2026-10-27, CloudPasswordReset:
a missing employee ID is filled in from the account's name, then the reset proceeds.* The entry
should carry:

- the owner's request and clarification, verbatim (section 1);
- the one failure mode, both conditions together: holds `CloudPasswordReset`, lacks
  `CloudPasswordResetReveal`, and the lookup fails, giving the existing refusal;
- the supersessions in section 1: backfilling for the one account being reset (the owner map stays
  rejected), and the true-up process coming into the module (the files stay out);
- the judgement calls J1-J12, as the owner leaves them.

**Status log:**

- 2026-10-27: drafted from the owner's request and his same-day clarification. Not approved.
- 2026-10-28: **S1 committed on `feat/cpr-employee-id`, not merged.** `FindUserByLogonName` sits
  beside `FindUserByEmployeeId` in `Services/ADEmployeeIdLookup.cs`, and both now run through one
  wrapper (`FindOneUserAcrossForest`) and one per-domain loop (`SearchEveryDomainForOneUser`), so
  their failure semantics cannot drift. `BuildLogonNameFilter` is the escaped equality;
  `ADSearchResult` gains an optional `EmployeeId` (null default) that both lookups fill. The
  existing lookup's own log lines render as before; the forest-enumeration line now says "a
  forest-wide user lookup", because both lookups use it. Tests T1-T5 are `ForestUserLookupTests` (48 cases)
  over a new stand-in forest, `ExchangeAdminWeb.Tests/StandInForest.cs`, behind the psl-11
  search-session seam: the real service runs its real commands, no directory is reached, and every
  failure case runs against both lookups. Mutations, each restored byte-exact (MD5
  `327a0b2a28d9ff3576a23b129f3c34e5` before and after every row): M1, escaping dropped, failed 7
  T1 cases; M2, a domain's error continuing the loop, failed 6 T3 cases; M3, the global catalog
  queried instead of each domain, failed 27 cases including every T2 case and the source guard.
  **Base version bump owed, not made** (the supervisor's instruction for this dispatch): master
  already holds `2.36.0`, the value section 7 expected S1 to set, so by section 7's own rule ("bump
  from what master holds") the owed bump is `2.36.0` -> `2.37.0` in `<VersionPrefix>`,
  `AssemblyVersion` and `FileVersion`. `2.36.0` is not yet deployed; whether it absorbs this change
  instead is the owner's call.
- 2026-10-28: **S2 committed on `feat/cpr-employee-id`, not merged.** In
  `CloudPasswordResetService`: the setting (`FillMissingEmployeeIdKey`, `OwnerNameEndingsKey`; read
  at each operation through one `TryGetModuleConfig` call, the same test `IsModuleCorrupt` makes),
  the name rule (`DeriveOwnerLogonName`), the classifier (`ClassifyEmployeeIdFill`, the 4.2 table
  in order, rows 6-8 and 10-11 through the untouched `ClassifyDestination`), the lookup inside
  `DeriveDestination` (`FillMissingEmployeeId`), and `SetEmployeeIdAsync` with its stub-testable
  core `SendEmployeeIdAsync`. The fill-in outcome rides the destination record as an optional
  `Fill` (`EmployeeIdFill`: outcome, number, source GUID, logon name, display name), and the closed
  list of 4.8 is the enum `EmployeeIdFillOutcome`, whose zero value is `NotAttempted`.
  `DeriveDestination`'s comment is updated as section 8 asks. Behaviour is unchanged: nothing can
  switch the fill-in on until S3 declares the fields. **What S2 does not do, by section 8:** the
  audit action `CloudPasswordReset_EmployeeIdSet`, the three fields on every event, and the alerts
  are written by the page's single builders, so S3 wires them; S2 supplies the values they carry.
  **Implementation choices S3 and a reviewer should know:**
  - The name rule reads only a UPN Entra returned on the read. `CloudPasswordTarget` gains an
    optional `UserPrincipalNameFromEntra` (default false), set by `ResolveTargetAsync`, which keeps
    falling back to the typed text exactly as before when Graph omits the property. A name not
    from Entra is `OwnerNotFound`, and no query runs (4.2 step 1, "never the operator's typed
    text").
  - A blank switch reads as off, as `ValidateTickets` reads one; only a non-blank non-boolean is
    `SettingUnreadable`.
  - A directory answer of Found with no user in it is `OwnerLookupFailed`: no answer to act on.
  - GUIDs are compared in canonical form, and an empty GUID counts as missing.
  - A Graph client that cannot be built is `WriteRejected`, since nothing was sent. Any throw from
    the PATCH itself is `WriteIndeterminate`, because a throw cannot be placed before or after the
    send (the `ResetPasswordAsync` precedent). The result message carries the status and never a
    response body; the sanitized Graph error goes to the log alone.
  - No length check on the number before the save: assumption A2 stays an assumption, and an
    over-long value comes back from Entra as `WriteRejected`.
  Tests: T6 `CloudPasswordResetOwnerNameTests`, T7 `CloudPasswordResetFillClassifierTests`, T8
  `CloudPasswordResetFillSettingTests`, T9 and T10 with the fill-in end to end in
  `CloudPasswordResetFillDerivationTests` (the real service over a real config store and the S1
  stand-in forest, `ExchangeAdminWeb.Tests/CloudPasswordResetHarness.cs`), T11
  `CloudPasswordResetEmployeeIdSaveTests` over a recording handler behind `GraphTokenClient`, T12
  `CloudPasswordResetNeutralityTests`, and `CloudPasswordResetFillVocabularyTests` pinning the
  closed list, the keys and the absence of any owner or address parameter. All in
  `ExchangeAdminWeb.Tests/CloudPasswordResetEmployeeIdFillTests.cs`, 136 cases. Mutations, each
  restored byte-exact: M4 case-sensitive endings failed 4 T6 cases and shortest-first failed 2; M5
  failed 3 T7 cases; M6 failed 8 (T7 and the GUID-less end-to-end case); M7 failed 10 (T8 and
  T10); M8 failed the 3 T9 cases; M9 failed 2 T11 cases; M10 failed 3. Beyond the table: a
  deployment ending written into the service failed T12; a save inserted into the derive path
  failed its source guard; an owner-address parameter on `DeriveDestination` failed the parameter
  guard. Every one of those rows ran against the final `CloudPasswordResetService.cs` and was
  restored to it byte-exact, MD5 `81c565b171fb4c53d4b93b84d56ec3f7` before and after each. A
  deployment domain written into the shared search service's code failed T12's code-only guard,
  restored to `d520887ab3fedd37a7e698546055c51c`. No version bump, per section 7.
