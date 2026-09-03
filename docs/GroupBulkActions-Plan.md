# Bulk member actions for the on-prem group modules

Status: Approved for implementation 2026-09-03 (owner goal-directive the same day:
"continue with the plan and codereview with codex (default) then implement upon
consensus"). Codex openreview over `f1bec06..2e89f7a` returned
`acceptable_with_changes` with three findings (gba-1..3, section 10), all admitted
and folded in below. One owner decision is drafted with a default (D1, section 1);
implementation proceeds on that default unless the owner overrules it.
Owner: Michael
Last verified against code: `f1bec06` / 2026-09-03
Versions: base app `2.17.0` -> `2.18.0` in S1 (shared helpers, gba-2);
`GroupManagement` `2.8.0` -> `2.9.0` (S2) -> `2.10.0` (S3); `SelfServiceGroups`
`1.8.0` -> `1.9.0` (S4) -> `1.10.0` (S5).
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`,
`docs/AdminModuleSpec.md`. On conflict the higher source wins.

Owner request 2026-09-03 (recorded in `.agents/state.md`): *"we need checkboxes and
bulk actions for group management modules. removing a single entry at a time is slow
and cumbersome."* Scope is the two on-prem group modules, `GroupManagement`
(`Components/Pages/GroupManagement.razor`, `Services/GroupManagementService.cs`) and
`SelfServiceGroups` (`Components/Pages/SelfServiceGroups.razor`,
`Services/SelfServiceGroups/SelfServiceGroupService.cs`). `M365GroupManagement` is
OUT (owner ruling 2026-08-11, `.agents/state.md`).

The Constitution requires a written plan: this adds a new write surface (batch
add/remove) over the protected-principal and protected-target gates, and new audit
events (Planning Rules: authorization changes, logging/audit changes, high-blast-radius
workflows).

## 1. Owner decisions

Agreed in chat 2026-09-03 and binding (recorded in `.agents/state.md`, restated here
so the plan stands alone):

- **Bulk remove:** checkbox per member row plus select-all; a "Remove selected"
  button; ONE confirmation listing the selected names; one ticket where the module
  already requires one; each member then runs the EXISTING per-member path unchanged
  (protection gates, servicer override, member-attribute write, read-back),
  SEQUENTIALLY, so one refusal never hides another; a per-row outcome table; one
  audit event per member as today PLUS one batch summary event. Known Failure Class 2
  is the whole risk: never a blanket success.
- **Bulk add via paste list:** a textarea taking usernames, emails or UPNs (one per
  line or comma-separated); one click resolves ALL lines against AD as a batch (the
  queries run together, not one spinner each); a resolution table shows each line as
  resolved (with the AD name), not found, or ambiguous; only resolved rows are
  committable and the rest stay listed with their reason; one commit runs the
  per-member add path for the resolved set with the same per-row outcome table.
  Self-service adds USERS only (its standing rule, D1 of
  `docs/GroupMemberNesting-Plan.md`); the existing typeahead stays for one-off adds.
- **Rejected by the owner:** a "staged picker" (typeahead feeding a pending list). It
  still types one at a time and waits on AD per entry. The paste list with batch
  resolution is the answer to "we would lose the AD validation on the input control",
  not a replacement for it.

**D1 - administrator notification for a batch (DRAFTED DEFAULT, owner may
overrule):** a batch sends ONE administrator notification via `EmailService`
listing every requested member with its outcome, instead of one email per member.
Reason: the Constitution's rule is that every mutating action notifies
administrators; a batch is one operator action and the summary names every
mutation it contained, so nothing goes un-notified, while N emails for one click
would be filtered as noise and hide the one that matters. The per-member AUDIT
events are unaffected (they stay one per member, as today). The affected-USER
notification in self-service stays per member: it goes to a different recipient
per row and rides the existing `NotifyAffectedUser` predicate unchanged.
Alternative if overruled: keep the existing per-member admin email inside the loop
and drop the summary email; a one-line change in S2/S4 each.

Settled by existing rulings or code, not open:

- A nested GROUP row in the self-service bulk remove is included, and the single
  confirmation carries the D2 warning text for each group row (re-adding needs an IT
  Support Desk ticket) - the same warning the inline confirm shows today, moved into
  the batch confirmation. No separate second click per group.
- The admin module's paste list resolves users OR groups (its existing typed path,
  `ResolveMemberForWrite`, is class-agnostic); the nesting guards (self-nest, cycle)
  run inside the per-member write exactly as today.
- Protected target groups: `GroupManagement` refuses a non-servicer at group
  selection (`CheckTargetProtectionAsync`), so the bulk controls never render for a
  protected group; the write-path target gate inside each per-member call stays as
  the backstop. Self-service is not target-gated (owner ruling 2026-08-31).
- No ServiceNow lookup beyond what the admin module already does (it validates the
  ticket through `ServiceNow.ValidateTicketAsync` before every write); the batch
  validates ONCE before the loop. Self-service has no ticket field and gains none.
- The outcome table has two states, Done and Not done, plus the service's message
  verbatim. `PermissionResult` carries no structured refused-vs-failed code and the
  service messages already say which it was ("protected principal", "Remove failed:
  ..."); adding a code to the shared model would be a base bump for presentation.
- Batch size cap: 200 identities per batch, a page-level constant. Lines beyond it
  are listed as "Not attempted - over the batch limit of 200" and never resolved or
  written. One Blazor circuit handler runs the whole batch; an unbounded paste could
  hold the circuit for many minutes.

## 2. Non-goals

- `M365GroupManagement` (owner ruling 2026-08-11).
- Any change to the per-member service write paths (`AddMemberAsync`,
  `RemoveMemberAsync`, `ChangeMemberAsync`, `RemoveListedMemberAsync`,
  `ApplyMembershipChangeAsync`) - the batch calls them, it does not alter them.
- Parallel writes. Members are written one at a time, in list order.
- Cancel mid-batch, background execution, or the Bulk Job Runner. A batch is one
  synchronous handler; the cap in section 1 bounds it.
- CSV upload. The paste list is the input control.
- Removing a member from its primary group or acting on an unresolved row (no
  objectGUID) - those rows stay inert exactly as their single Remove button is today.
- A "refused vs failed" classification beyond the service's message text.
- Changing `PermissionResult`, `MembershipChangeResult`, or the audit schema.
- Localization, keyboard shortcuts, persisting a batch draft across navigation.

## 3. Acceptance criteria

- AC1: Both member tables have a checkbox per row and a select-all checkbox in the
  header. A row whose single Remove is disabled (admin: blank `ObjectGuid` or
  `IsPrimaryMember`; self-service: `!IsRemovable`) has its checkbox disabled and is
  skipped by select-all. Select-all covers only the rows currently rendered.
- AC2: "Remove selected (N)" is enabled only when N > 0 and (admin only) the ticket
  box is non-blank. Clicking it shows ONE confirmation listing every selected
  member's display name; in self-service, each GROUP row in that list carries the D2
  one-way warning. Confirm runs the batch; Cancel clears nothing but the confirmation.
- AC3: The batch runs the members SEQUENTIALLY through the same private per-member
  handler the single Remove button uses (S2/S4 extract it), so per-member
  authorization, protection, servicer, write, read-back, per-member audit and
  (self-service) affected-user notification are byte-for-byte the single path. The
  module authorization re-check (`AuthorizationService.AuthorizeAsync` on the
  `GroupManagementOnPrem` / `SelfServiceGroups` policy) lives INSIDE the per-member
  handler and therefore runs immediately before EVERY row's service call (gba-1;
  Constitution, Authorization: "immediately before the write"); a per-row denial is
  audited as the single path audits it today and the loop continues. The batch
  handler's upfront authorization check is a UX shortcut for early refusal, never
  the only check. The admin ticket is validated once per batch (section 1). One
  member's refusal or exception is recorded for that row and the loop continues.
- AC4: After the loop, a per-row outcome table shows each member's display name,
  Done or Not done, and the service message. The member list refreshes ONCE at the
  end (never per row).
- AC5: One batch summary audit event per batch, written after the loop:
  action `<Module>_BulkRemoveMembers` or `<Module>_BulkAddMembers`, category = the
  module id, target = the group name, `success` = TRUE ONLY IF EVERY ROW WAS DONE,
  `ticketNumber` = the batch ticket (admin) or empty (self-service),
  `errorDetail` = "<m> of <N> not done" when any row failed, `extra` =
  `{ requested, done, notDone, members: [ "<label>: Done|Not done - <message>", ... ] }`.
  The per-member events keep their existing action names.
- AC6 (D1): One administrator notification per batch via
  `EmailService.SendAdminNotificationAsync`, action as AC5, success as AC5, details
  `Group`, `Requested`, `Done`, `Not done`, `Members` (the AC5 member lines joined
  with "; "). The per-member admin email is NOT sent inside the bulk loop. The single
  Remove/Add buttons keep sending theirs.
- AC7: Bulk add: a textarea plus "Resolve" button. Resolve parses the text
  (`BulkIdentityList.Parse`: split on CR, LF, comma and semicolon; trim; drop blanks;
  case-insensitive de-duplication keeping the first occurrence and marking later ones
  "Duplicate of line <n>"; cap per section 1), then resolves every kept line in ONE
  batch query per chunk of at most 50 identities (section 6), then renders a
  resolution table: line text, status (Resolved / Not found / Ambiguous / Duplicate /
  Not attempted), and for Resolved the AD display name and kind (admin) or display
  name (self-service). A resolution failure (exception, no credentials) marks every
  unresolved line "Not attempted - <reason>" and never renders as "Not found".
- AC8: "Add resolved (N)" is enabled only when N > 0 and (admin) the ticket is
  non-blank; it runs the resolved rows sequentially through the same private
  per-member ADD handler the single Add button uses, with the same outcome table,
  batch audit (AC5) and batch email (AC6). Admin passes the resolved row's DN as
  `memberDn` (the picker path); self-service passes the resolved user's UPN (or
  sAMAccountName when the UPN is blank) as the identity to `ChangeMemberAsync`.
- AC9: Self-service batch resolution is USER-only and binds to the module
  credential's home domain (the same filter shape and binding as `ResolveUserMember`),
  so a line that previews as Resolved resolves the same way at commit. A line that
  names a group previews as "Not found - '<x>' is a group; only users can be added
  here" using the existing `GroupWithIdentityExists` probe semantics, only when the
  user query missed.
- AC10: Admin batch resolution is class-agnostic (user or group) against the forest
  global catalog (the `ResolveSearchGlobalCatalog` endpoint with its `:3268` guard),
  returning DN, objectClass, Name, display name, UPN, sAMAccountName and mail (Name is
  projected because the group clause of the filter matches on it, gba-3); a line
  matching more than one object is Ambiguous; an object matching more than one line
  makes the later line a Duplicate.
- AC11: `BulkIdentityList.Match` is pure and total: given the kept lines and the
  candidate objects it assigns exactly one status per line and never throws on
  missing attributes.
- AC12: `isLoading` (admin) / `isChanging` (self-service) is held for the whole
  batch; every bulk control is disabled while it runs; a group switch mid-batch does
  not redirect the writes (the group is snapshotted before the first await, gmn-7)
  and the final refresh applies only if that group is still selected.
- AC13: Versions per the header: the base app triple (`<VersionPrefix>`,
  `AssemblyVersion`, `FileVersion` in `ExchangeAdminWeb.csproj`) bumps IN S1, the
  slice that lands the shared helpers (gba-2; the csv-2 rule: the bump ships with the
  shared change); module versions bump in the slice that ships each behavior. No
  `ModuleCatalog` permission or alias change (`ModuleCatalogTests.cs` untouched).
- AC14: README gains a bulk bullet in both module sections.

## 4. Failure behavior

| Step / dependency | If it fails | The operator sees | System state afterward |
|---|---|---|---|
| Ticket validation (admin, once before the loop) | Batch does not start | Existing "Ticket validation failed" alert | One failed batch audit event (AC5, requested = N, done = 0), no member events, no writes |
| Upfront authorization check (page, before the loop - UX shortcut) | Batch does not start | "Authorization denied." | One failed batch audit, no writes |
| Per-row authorization re-check inside `RemoveOneAsync`/`AddOneAsync` (gba-1) | That row marked Not done "Authorization denied." | Row in the outcome table | Per-row failed audit (the single path's auth-denial branch); loop continues; batch `success=false` |
| One member's service call returns `Success=false` | Row marked Not done with the message | Row in the outcome table | Per-member failed audit as today; loop continues |
| One member's service call THROWS | Caught per row; row marked Not done with the exception message | Row in the outcome table | Per-member failed audit (the single path's catch branch, reused); loop continues |
| Batch audit write throws | Caught, logged | Nothing | Member audits already written; batch summary missing, logged as audit failure |
| Batch email throws | Caught, logged | Nothing | Operation result unchanged (Constitution: notification failure never masks the result) |
| Member list refresh after the loop throws | Caught | Existing member-load error alert | Writes already confirmed per row by read-back |
| Batch resolution query throws / no credentials | Every kept line "Not attempted - <reason>" | Resolution table with the reason on each line | Nothing written; "Add resolved" disabled (N = 0) |
| Paste exceeds the cap | Lines past 200 listed "Not attempted - over the batch limit of 200" | Resolution table | Only the first 200 are resolved |
| Zero rows selected / zero resolved | Button disabled | Disabled button | Nothing runs |
| Circuit drops mid-batch | Loop dies with the circuit | Page reconnects; outcome table lost | Rows written before the drop are in AD and audited per member; no batch summary event. The operator reloads the members to see the state. Accepted: same as the Bulk Job Runner's pre-runner shape; the cap bounds the window |

## 5. Rollback / blast radius

Revert the commit(s). No schema, config, authorization-policy or stored-state
change. Per module the blast radius is its page, one new internal virtual batch
query on its service, and its version string. Shared blast radius is one new pure
static class with no callers outside the two pages. The per-member write paths are
untouched, so the single-member behavior on both pages is unchanged by construction
(AC3, AC8 route the single buttons through the same extracted handler; the
extraction is a move, not a rewrite, and section 8 pins it).

## 6. Design sketch

### Current code (read at `f1bec06`; spot-verify line numbers at implementation)

- `GroupManagement.razor`: member table `:132-158` (columns Name, Email, Kind, action);
  the single Remove button `:149-151` disables on `isLoading`, blank ticket, blank
  `member.ObjectGuid`, `member.IsPrimaryMember`. `RemoveMember(GroupMemberInfo)`
  `:393-452` does: snapshot group, ticket validation, auth re-check, service
  `RemoveMemberAsync(group.Identity, member, user, group.SamAccountName,
  listed.ObjectGuid, listed.DistinguishedName)`, per-member audit with
  `memberObjectGuid`/`memberDn`/serviced note, admin email, refresh. `AddMember()`
  `:321-391` is the same shape with `AddMemberAsync(..., selection?.DistinguishedName)`.
  Injected: `GroupService`, `Audit`, `Email`, `ServiceNow`, `AuthStateProvider`,
  `AuthorizationService`, `ClientInfo`, `Logger`.
- `SelfServiceGroups.razor`: member table `:247-306`; user rows call
  `RemoveMember(member)`, group rows go through `BeginGroupRemoval` /
  `ConfirmGroupRemoval` (D2 inline warning `:289-303`); both end in
  `RemoveListedMember(GroupMember)` `:447-517` (auth re-check, service
  `RemoveListedMemberAsync(callerSid, group.ObjectGuid, member.ObjectGuid, user,
  member.DistinguishedName)`, audit, admin email, affected-user email, refresh).
  Typed add/remove: `ChangeMember(MembershipOperation, string? explicitIdentity)`
  `:519-595` calling `ChangeMemberAsync(callerSid, group.ObjectGuid, identity, op,
  user)`. `MembershipChangeResult.NotifyAffectedUser` gates the user email.
- `GroupManagementService`: `ResolveMemberForWrite` `:967` (internal virtual seam;
  DN/GUID path routes `Get-ADObject -Server ServerFromDn(dn)`; typed path is a
  home-domain class-agnostic `-LDAPFilter`), `ResolveSearchGlobalCatalog` `:181`,
  `GetCredentialsAsync` `:1115` (internal virtual), `ServerFromDn` `:884`,
  `AdOwnershipFilter.EscapeLdapFilterValue`.
- `SelfServiceGroupService`: `ResolveUserMember` `:902` (static, home domain,
  `AdOwnershipFilter.BuildUserByIdentityFilter`, exactly-one), `GroupWithIdentityExists`
  `:1001`, `ComposeMemberNotFoundMessage` `:986`, credential fetch via
  `_moduleCredentials.GetCredentialsAsync("SelfServiceGroups", ...)`.
- Test seams already used: subclass overriding `GetCredentialsAsync` and
  `ResolveMemberForWrite` (`GroupManagementServiceTests.cs:242-257`,
  `GroupManagementTargetGateTests.cs:329-348`).
- Catalog versions: `GroupManagement` `ModuleCatalog.cs:363` (`2.8.0`),
  `SelfServiceGroups` `:450` (`1.8.0`).

### Shared helpers: `Services/BulkIdentityList.cs` (new, pure, static)

```
public static class BulkIdentityList
{
    public const int MaxBatch = 200;

    public sealed record Line(int Number, string Text);

    // Parse: split on '\r', '\n', ',', ';'; Trim; drop blanks; number lines from 1
    // in input order. Returns kept lines, duplicate lines (with the number they
    // duplicate), and lines past MaxBatch (not attempted). Case-insensitive
    // de-duplication on the trimmed text.
    public static ParsedList Parse(string? text);
    public sealed record ParsedList(
        IReadOnlyList<Line> Kept,
        IReadOnlyList<(Line Line, int DuplicateOf)> Duplicates,
        IReadOnlyList<Line> OverCap);

    // One directory object as the batch query returns it. All strings nullable. Name is
    // the AD `name` (RDN) attribute - the group clause of the filter matches on it, so
    // the matcher must see it (gba-3).
    public sealed record Candidate(
        string? DistinguishedName, string? ObjectClass, string? Name, string? DisplayName,
        string? UserPrincipalName, string? SamAccountName, string? Mail, string? ObjectGuid);

    public enum Status { Resolved, NotFound, Ambiguous, Duplicate, NotAttempted }

    public sealed record Resolution(Line Line, Status Status, Candidate? Match, string Reason);

    // Match: for each kept line, the candidates whose UPN, mail, or sAMAccountName
    // equals the line text (OrdinalIgnoreCase); for group candidates (ObjectClass
    // "group", only when allowGroups) also Name. A group candidate is never a match
    // when allowGroups is false, whatever attribute equals the line. Exactly
    // one -> Resolved; zero -> NotFound; more -> Ambiguous ("matches <k> directory
    // objects"). A candidate already claimed by an earlier line makes the later line
    // Duplicate ("Duplicate of line <n>: same object"). Total: never throws on nulls.
    public static IReadOnlyList<Resolution> Match(
        IReadOnlyList<Line> kept, IReadOnlyList<Candidate> candidates, bool allowGroups);

    // Chunking for the query builder: at most 50 lines per LDAP filter.
    public static IEnumerable<IReadOnlyList<Line>> Chunk(IReadOnlyList<Line> kept, int size = 50);

    // The OR filter for one chunk. Each value RFC 4515-escaped via
    // AdOwnershipFilter.EscapeLdapFilterValue. allowGroups adds the group clause.
    //   (|(&(objectCategory=person)(objectClass=user)(|(userPrincipalName=a)(mail=a)(sAMAccountName=a)))
    //     (&(objectCategory=group)(|(name=a)(sAMAccountName=a)(mail=a)))   <- only when allowGroups
    //     ... repeated per line ...)
    public static string BuildBatchFilter(IReadOnlyList<Line> chunk, bool allowGroups);
}

public sealed record BulkRowOutcome(string Label, bool Done, string Message);

public static class BulkOutcomeSummary
{
    // success = Done for every row (Known Failure Class 2); counts; the audit
    // member lines "<label>: Done|Not done - <message>"; errorDetail
    // "<notDone> of <requested> not done" or null.
    public static (bool Success, int Requested, int Done, int NotDone,
        IReadOnlyList<string> MemberLines, string? ErrorDetail) Of(IReadOnlyList<BulkRowOutcome> rows);
}
```

Shared helpers are shared infrastructure (gba-2; Constitution, Deployment And
Versioning): a new `Services/` class consumed by two modules bumps the base app
version, and the bump ships in S1 with the class (the csv-2 rule). The drafted
alternative - module bumps only, on the precedent of cross-module helpers living on
`GroupManagementService` - was declined by the reviewer and is withdrawn: those
helpers were added to an existing module service during module-scoped fixes; a new
shared class is the shape the rule names.

### Batch query seams (one per service)

`GroupManagementService`:

```
// Internal virtual TEST SEAM. Resolves every chunk against the forest global catalog
// (ResolveSearchGlobalCatalog; falls back to the home domain when null, as search
// does), Get-ADObject -LDAPFilter BuildBatchFilter(chunk, allowGroups: true)
// -Properties Name,DisplayName,UserPrincipalName,SamAccountName,mail,DistinguishedName,
// ObjectGUID -Credential ... -ErrorAction Stop. Projects each row to a Candidate
// (Name from the `Name` property, gba-3).
// Throws on a query error (the page maps it to NotAttempted for every line).
internal virtual IReadOnlyList<BulkIdentityList.Candidate> QueryBatchCandidates(
    (string username, string password, string domain) creds, IReadOnlyList<BulkIdentityList.Line> kept);

// Public entry the page calls: credentials via GetCredentialsAsync (null -> every
// line NotAttempted "AD credentials unavailable."), then QueryBatchCandidates under
// ThrottledAdAsync + Task.Run, then BulkIdentityList.Match(kept, candidates, true).
public async Task<IReadOnlyList<BulkIdentityList.Resolution>> ResolveBatchAsync(
    IReadOnlyList<BulkIdentityList.Line> kept);
```

`SelfServiceGroupService`:

```
// Same shape, USER-only, home domain (no -Server), Get-ADUser -LDAPFilter
// BuildBatchFilter(chunk, allowGroups: false) -Properties Name,DisplayName,
// UserPrincipalName,SamAccountName,mail,DistinguishedName,ObjectGUID. For every
// NotFound line, one class-bounded probe (the existing GroupWithIdentityExists
// filter, batched the same way with BuildGroupProbeFilter per line OR'd) rewrites the
// reason to ComposeMemberNotFoundMessage(text, Add, identityIsGroup: true) when it
// hits. Probe failure keeps the generic reason (the probe shapes wording, never the
// outcome).
internal virtual IReadOnlyList<BulkIdentityList.Candidate> QueryBatchCandidates(
    (string username, string password, string domain) creds, IReadOnlyList<BulkIdentityList.Line> kept);

public async Task<IReadOnlyList<BulkIdentityList.Resolution>> ResolveBatchAsync(
    string callerSid, IReadOnlyList<BulkIdentityList.Line> kept);
```

`ResolveBatchAsync` in self-service validates `callerSid` as a SID (the existing
`IsSecurityIdentifier` guard, same as every other entry point), fetches THIS module's
credential, and does not check group eligibility - resolution is read-only lookup of
users; eligibility is re-checked inside `ChangeMemberAsync` per write as today.

### Page shape (both pages, same skeleton)

State: `HashSet<string> selectedGuids` (keyed on `ObjectGuid`), `bool showRemoveConfirm`,
`List<BulkRowOutcome>? bulkOutcome`, `string pasteText`, `IReadOnlyList<Resolution>?
resolution`, `bool isResolving`.

Extraction (the load-bearing move, S2/S4): the body of the existing single handler
becomes `private async Task<BulkRowOutcome> RemoveOneAsync(GroupSnapshot group,
<RowType> listed, string ticket, bool sendAdminEmail)` containing, in order: the
module authorization re-check (`AuthStateProvider` + `AuthorizationService.AuthorizeAsync`
on the module's write policy, with its audited denial branch - gba-1: this is what
makes "immediately before the write" hold per row), the service call, the per-member
audit (all branches), the affected-user email (self-service) and, when
`sendAdminEmail`, the per-member admin email. It never throws: the existing catch
branch becomes the `Not done` return. The single button's handler becomes: snapshot,
ticket validation (admin), `RemoveOneAsync(..., sendAdminEmail: true)`, refresh. Same
for `AddOneAsync(group, label, memberDn | identity, ticket, sendAdminEmail)`. The
`ClaimsPrincipal` the service receives is the one the per-row check just authorized,
fetched inside the handler, never a principal captured before the loop.

Bulk remove handler:

```
var group = selectedGroup; var rows = members where selectedGuids contains ObjectGuid;  // snapshot
hold loading flag; ticket validation once (admin); upfront auth check once (UX shortcut - refuse early, audit the batch as failed)
foreach row in rows (list order): outcomes.Add(await RemoveOneAsync(group, row, ticket, sendAdminEmail: false));   // per-row auth re-check is INSIDE
summary = BulkOutcomeSummary.Of(outcomes)
try { Audit.LogModuleAction(user, ip, "<Module>_BulkRemoveMembers", "<Module>", group.Name, summary.Success, ticket, summary.ErrorDetail, extra{requested, done, notDone, members}) } catch { log }
try { await Email.SendAdminNotificationAsync(user, ip, "<Module>_BulkRemoveMembers", summary.Success, ticket, details{Group, Requested, Done, Not done, Members}, summary.ErrorDetail) } catch { log }
selectedGuids.Clear(); bulkOutcome = outcomes; if (still selected) refresh members once
```

Ticket or auth failure before the loop writes the AC5 batch audit with `done = 0` and
`errorDetail` = the refusal, and returns.

Bulk add handler: same loop over `resolution.Where(Status == Resolved)`, calling
`AddOneAsync`. Admin passes `match.DistinguishedName` as `memberDn` and the line text
as the label. Self-service passes `match.UserPrincipalName` (fallback
`SamAccountName`) as the identity. After the loop: clear `pasteText` and `resolution`
only when every row is Done, so a partial batch keeps its input visible for retry.

Rendering: the selection column is first; the header checkbox is checked when every
selectable rendered row is selected, indeterminate is not required. The confirmation
is an inline `alert-warning` above the table (the D2 inline shape) listing names,
with "Remove <N> members" / "Cancel". The outcome table is a `table-sm` beneath the
op-result alert with a dismiss button; it stays until dismissed or the group changes.
The bulk add card sits under the single add row, collapsed behind a "Bulk add" toggle
button (C# toggle; no Bootstrap JS in this app).

### Audit action names

`GroupManagement_BulkRemoveMembers`, `GroupManagement_BulkAddMembers`,
`SelfServiceGroups_BulkRemoveMembers`, `SelfServiceGroups_BulkAddMembers`. The
per-member events keep `GroupManagement_RemoveMember`, `GroupManagement_AddMember`,
`SelfServiceGroups_RemoveMember`, `SelfServiceGroups_AddMember`.

## 7. Task breakdown

One commit per slice. S1 first; S2 and S4 depend on S1; S3 depends on S2 (the
extracted `AddOneAsync` shape and the outcome table); S5 depends on S4 for the same
reason. S2/S3 and S4/S5 are independent of each other.

**S1 - `Services/BulkIdentityList.cs` + `BulkOutcomeSummary` + tests + base app
bump.** Serves AC7's parser, AC11, the summary rule behind AC5, and the base-bump
half of AC13: the csproj triple `2.17.0` -> `2.18.0` lands here with the shared
class (gba-2). No page change, no module bump.

**S2 - GroupManagement bulk remove.** Extract `RemoveOneAsync`; checkboxes,
select-all, confirmation, loop, outcome table, batch audit and email. Serves AC1-AC6,
AC12 for the admin module. `ModuleCatalog.cs:363` `2.8.0` -> `2.9.0` with a version
comment.

**S3 - GroupManagement bulk add.** `QueryBatchCandidates` + `ResolveBatchAsync` on the
service; extract `AddOneAsync`; textarea, Resolve, resolution table, "Add resolved",
loop, outcome table, batch audit and email. Serves AC7, AC8, AC10 for the admin
module. `2.9.0` -> `2.10.0`.

**S4 - SelfServiceGroups bulk remove.** Extract `RemoveOneAsync` from
`RemoveListedMember`; checkboxes on `IsRemovable` rows; confirmation with the D2
warning on group rows; loop; outcome table; batch audit and email. Serves AC1-AC6,
AC12 for self-service. `ModuleCatalog.cs:450` `1.8.0` -> `1.9.0`.

**S5 - SelfServiceGroups bulk add.** `QueryBatchCandidates` (user-only, home domain,
group probe for misses) + `ResolveBatchAsync`; extract `AddOneAsync` from
`ChangeMember`'s Add branch; textarea, Resolve, resolution table, "Add resolved",
loop. Serves AC7, AC8, AC9. `1.9.0` -> `1.10.0`.

**S6 - README + plan status + state.** Serves AC14. One bullet per module section
(`README.md:109-141`). Plan `Status:` -> Implemented; section 9 completed.

## 8. Test plan

`ExchangeAdminWeb.Tests/BulkIdentityListTests.cs` (S1):

| AC | Test | What it proves | Non-vacuity |
|---|---|---|---|
| AC7 | `Parse_SplitsOnNewlineCommaSemicolon_TrimsAndDropsBlanks` | `"a\r\nb, c;d\n\n e "` -> a,b,c,d,e numbered 1..5 | Drop a separator; FAIL |
| AC7 | `Parse_DeduplicatesCaseInsensitive_KeepingFirst` | `A`,`a` -> one kept, one duplicate-of-1 | Make the comparer ordinal; FAIL |
| AC7 | `Parse_LinesPastCap_AreOverCap` | 201 lines -> 200 kept, 1 over cap, numbered 201 | Remove the cap; FAIL |
| AC11 | `Match_ExactlyOne_Resolved` | One candidate with matching UPN -> Resolved with that candidate | Swap the status; FAIL |
| AC11 | `Match_MatchesMailAndSam_CaseInsensitive` | Lines by mail and by sAM both resolve to their objects | Drop one attribute; FAIL |
| AC11 | `Match_Zero_NotFound` | No candidate -> NotFound | n/a (shape) |
| AC11 | `Match_Multiple_Ambiguous` | Two candidates share a sAM -> Ambiguous, reason names the count | Take the first; FAIL |
| AC11 | `Match_SameObjectTwice_LaterLineIsDuplicate` | `jdoe` and `jdoe@x` resolving to one object -> second is Duplicate of line 1 | Remove the claimed set; FAIL |
| AC10/AC9 | `Match_GroupCandidate_OnlyWhenAllowed` | A group candidate matching by sAMAccountName resolves with allowGroups true, is NotFound with false | Ignore the flag; FAIL |
| AC10 | `Match_GroupCandidate_ByNameOnly_Resolves` (gba-3) | A group candidate whose ONLY matching attribute is `Name` (sAM, mail, UPN all different) resolves with allowGroups true | Drop Name from the comparison; FAIL |
| AC9 | `Match_UserCandidate_NameDoesNotMatch` | A USER candidate whose `Name` equals the line but whose UPN/mail/sAM do not is NotFound - Name is a group-only key | Compare Name for every class; FAIL |
| AC11 | `Match_NullAttributes_DoNotThrow` | Candidate with all-null strings -> NotFound, no exception | Dereference without null check; FAIL |
| AC7 | `BuildBatchFilter_EscapesEachValue_AndOrsLines` | Two lines, one containing `*(`, yield one `(|...)` filter with `\2a\28` and no raw metacharacter; group clause present only with allowGroups | Skip escaping; FAIL |
| AC7 | `Chunk_SplitsAtFifty` | 120 lines -> 50, 50, 20 | Change the size; FAIL |
| AC5 | `Summary_SuccessOnlyWhenEveryRowDone` | 3 done + 1 not done -> Success false, counts 4/3/1, ErrorDetail "1 of 4 not done", member lines carry the message | Any-done success; FAIL |
| AC5 | `Summary_AllDone_NoErrorDetail` | Success true, ErrorDetail null | n/a |

Service-level (S3, S5), via the existing subclass-seam pattern (override
`GetCredentialsAsync` and `QueryBatchCandidates`):

| AC | Test | What it proves | Non-vacuity |
|---|---|---|---|
| AC7 | `ResolveBatchAsync_NoCredentials_EveryLineNotAttempted` | Null creds -> every line NotAttempted with "AD credentials unavailable" | Return NotFound instead; FAIL |
| AC7 | `ResolveBatchAsync_QueryThrows_EveryLineNotAttempted` | Seam throws -> NotAttempted with the message, no throw to the caller | Let it propagate; FAIL |
| AC10 | `ResolveBatchAsync_ScriptedCandidates_MatchPerLine` (admin) | Seam returns a user and a group -> the user line Resolved, the group line Resolved | Pass allowGroups false; FAIL |
| AC9 | `ResolveBatchAsync_ScriptedCandidates_GroupLineNotFound` (self-service) | Seam returns a group candidate -> NotFound with the group-scope reason | Pass allowGroups true; FAIL |
| AC9 | `ResolveBatchAsync_RejectsNonSid` (self-service) | Non-SID caller -> ArgumentException | Remove the guard; FAIL |

Source guards (S2-S5), `ExchangeAdminWeb.Tests/GroupBulkActionsWiringTests.cs`, the
`EventLogCsvWiringTests` / `GroupMemberNestingProtectionTests` mechanism (read the
page text, bound the method body, assert call shapes):

| AC | Test | What it proves | Non-vacuity |
|---|---|---|---|
| AC3 | `<Page>_SingleAndBulkRemove_ShareRemoveOneAsync` | Both the single handler and the bulk loop body call `RemoveOneAsync(` and the page contains exactly one `RemoveMemberAsync(`/`RemoveListedMemberAsync(` service call | Inline a second service call; FAIL |
| AC8 | `<Page>_SingleAndBulkAdd_ShareAddOneAsync` | Same for `AddOneAsync(` and `AddMemberAsync(`/`ChangeMemberAsync(` | Same |
| AC3 | `<Page>_PerRowHandlers_RecheckAuthorization` (gba-1) | The bodies of `RemoveOneAsync` and `AddOneAsync` each contain `AuthorizationService.AuthorizeAsync(` on the module's write policy and an audited "Authorization denied" branch; the bulk loop bodies contain no `GroupService.` call outside those handlers | Hoist the check above the loop; FAIL |
| AC5 | `<Page>_BulkAudit_UsesSummarySuccess` | The bulk handler's `LogModuleAction` call passes `summary.Success` and the `_Bulk` action name | Pass `true`; FAIL |
| AC6 | `<Page>_BulkLoop_DoesNotSendPerMemberAdminEmail` | The bulk loop calls `RemoveOneAsync(..., sendAdminEmail: false)` and one `SendAdminNotificationAsync` follows the loop | Flip the flag; FAIL |
| AC1 | `<Page>_SelectAll_SkipsDisabledRows` | The select-all handler filters on the same predicate as the row checkbox's `disabled` (`IsPrimaryMember`/`ObjectGuid` for admin, `IsRemovable` for self-service) | Drop the filter; FAIL |
| AC2 | `SelfService_BulkConfirm_CarriesGroupWarning` | The confirmation markup renders the D2 warning text for `Kind == "Group"` rows | Remove it; FAIL |
| AC12 | `<Page>_BulkHandlers_SnapshotGroupBeforeFirstAwait` | `var group = selected...` precedes the first `await` in each bulk handler body | Move it; FAIL |
| AC13 | (existing) `ModuleCatalogTests` untouched and green | No permission/alias change | n/a |

Source-guard caveat, as in the sibling plans: guards pin wiring, behavior lives in the
pure and seamed tests, and the manual checks are the end-to-end proof. There is no
bUnit harness.

Manual checks after deploy (dev, against a THROWAWAY ANALOG group the owner creates
for this, since `ExchangeWebAdmins` is a Protected Group Target on dev):

1. Admin: select three members including one primary-group row (checkbox disabled,
   skipped by select-all); Remove selected; confirmation lists exactly the enabled
   selections; outcome table shows three Done; member list shows them gone; Event
   Log shows three `GroupManagement_RemoveMember` events plus one
   `GroupManagement_BulkRemoveMembers` with `success=true`, `requested=3`; ONE admin
   email arrived listing the three.
2. Admin: bulk add a paste list of five lines - two valid UPNs, one sAMAccountName,
   one misspelled, one that matches two objects (or a duplicate of line 1). Resolution
   table: 3 Resolved with names, 1 Not found, 1 Ambiguous/Duplicate. Add resolved:
   three Done. Batch audit `success=true`, `requested=3`.
3. Admin: include a protected user (or a `Domain Admins`-style protected group) in a
   bulk remove alongside an ordinary member. Outcome: the protected row is Not done
   with the protection message, the ordinary row is Done, the batch audit has
   `success=false`, `errorDetail` "1 of 2 not done", and the protected row's own
   failed `GroupManagement_RemoveMember` event exists.
4. Admin: blank ticket -> "Remove selected" and "Add resolved" disabled.
5. Self-service (as a group owner): bulk remove two users and one nested group; the
   confirmation shows the one-way warning under the group; outcomes Done; affected
   users each got their notification; ONE admin email.
6. Self-service: paste a group name in the bulk add; it previews as Not found with
   the "is a group; only users can be added here" reason.
7. Self-service: bulk add including a WINROOT-domain user by UPN: expect Not found
   (home-domain binding, the same answer the single Add gives today). Record the
   result; if the owner wants forest-wide self-service adds that is a separate plan.

Verification commands (from `.agents/repo-guidance.md`):

```
dotnet build ExchangeAdminWeb.slnx -c Release
dotnet test ExchangeAdminWeb.slnx
dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore
git diff --check HEAD
```

Non-vacuity: revert the named target of each new test, confirm FAIL, restore,
confirm PASS.

## 9. Traceability check

Completed in S6: each AC above maps to its slice commit and test names.

## 10. Review log

- 2026-09-03: openreview codex (`@azure-openai-eus2-global/gpt-5.5-dzs` @ xhigh,
  grade fallback; codex-cli 0.152.1, `codex exec -s read-only`, native command
  execution working again on this version) over `f1bec06..2e89f7a`: verdict
  `acceptable_with_changes`, capability_ok true, both SHAs echoed. Reviewer's own
  approach matched the plan's shape (plan first, batch only the operator workflow
  and read-side resolution, sequential writes through extracted single-member
  handlers, per-row outcomes, per-member audits plus one batch summary). Three
  material changes, three findings, all admitted and folded in:
  **gba-1 (HIGH)** - the authorization re-check ran once before the loop, so later
  rows could write under a stale page-level result and AC3's "byte-for-byte the
  single path" claim was false; now inside `RemoveOneAsync`/`AddOneAsync`, per
  row, audited (AC3, section 4, section 6, new source guard).
  **gba-2 (MEDIUM)** - the shared helpers were declared module-bump-only on a
  precedent that does not fit; base app `2.17.0` -> `2.18.0` now lands in S1
  (header, AC13, S1).
  **gba-3 (MEDIUM)** - the group clause of the LDAP filter matched on `name` but
  `Candidate` had no Name field, so a group found only by name could never be
  matched back to its line; `Name` added to the record, the projection, the
  matcher (group-only key) and two new tests (AC10, section 6, section 8).
  Records: `.agents/review/findings/gba-{1,2,3}.md`; envelope
  `.agents/review/gba.result.json` (gitignored scratch).
