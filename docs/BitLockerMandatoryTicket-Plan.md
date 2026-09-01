# BitLocker Recovery mandatory ticket field + per-module ticket-validation switch

Status: Draft 2026-08-31, revised twice the same day: first for the owner's
ServiceNow ruling (`.agents/decisions.md` 2026-08-31, "ServiceNow ticket validation:
required later, seam built now, per-module switch"), then corrected against code -
the first revision asserted no ServiceNow client exists, and one does
(`Services/ServiceNowService.cs`, dormant). No owner decision is outstanding;
awaiting a go to implement at S1.
Owner: Michael
Last verified against code: `21870ed` / 2026-08-31
Versions: `BitLockerRecovery` `1.0.2` -> `1.1.0` AND a base app bump
(`ExchangeAdminWeb.csproj` `<VersionPrefix>`, read current value at implementation
time) - the validator and a one-property change to `ServiceNowService` are shared
infrastructure.
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`,
`docs/AdminModuleSpec.md`. On conflict the higher source wins.

Owner request 2026-08-27 (recorded in `.agents/state.md`, older queue item 7):
`BitLockerRecovery` *"must require a ticket number before the search runs and before
any result is displayed."*

Owner ruling 2026-08-31 (verbatim in `.agents/decisions.md`): ServiceNow ticket
validation IS coming app-wide; build the validation seam now with a per-module
on/off switch - off accepts any text, on actually validates; no ServiceNow API
access exists yet, so the point of this work is to make adding it later cheap.

## 1. Goal

The BitLocker Recovery page is the most sensitive read in the app - a result row is
one Reveal click away from a working disk-decryption key. Today a search runs with no
ticket, and neither the search audit (`BitLockerSearchByName` / `BitLockerSearchByKeyId`)
nor the reveal audit (`RevealRecoveryKey`) carries one.

Done means:

1. No BitLocker search runs, and therefore no result renders, without a ticket that
   passes validation; the ticket is written on both the search and the reveal audit
   events, so the Event Log page and its CSV export (which already carries a `Ticket`
   column, `docs/EventLogCsvTicket-Plan.md`) tie every key disclosure to a ticket.
2. A shared per-module ticket-validation policy layer exists that ANY module with a
   ticket field can later route through, with a per-module Module Config switch:
   - **Off (default): presence-only.** Any non-blank ticket is accepted as plain
     audit metadata - the existing repo shape (`M365GroupManagement.razor:117-125`,
     `string.IsNullOrWhiteSpace`, placeholder "INC/REQ number"). No format regex.
   - **On: real validation** through the EXISTING dormant ServiceNow client
     (`ServiceNowService.ValidateTicketAsync`, `Services/ServiceNowService.cs:33`).
     While that client is dormant (`ServiceNow:Enabled` false in deployment config -
     the current state; no API access exists), On refuses with a message saying
     validation is switched on but the integration is dormant. Fail closed and
     visible. The dormant client's own everything-passes behavior
     (`ServiceNowService.cs:35-43`) must NOT be surfaced through a module switch
     that claims to validate - that is the decorative-control trap this repo has
     already paid for (idm-3, and the `EmailService._notifyUsers` shape pinned in
     `docs/IntuneDeviceManagement-Plan.md`).

What already exists and is NOT rebuilt: `ServiceNowService` (singleton,
`Program.cs:192`, named HttpClient `Program.cs:97`) validates INC/REQ tickets
against the Table API with an active-state check, and eight pages already call it
inline at action time (MailboxPermissions, CalendarPermissions, ConferenceRooms,
Comms10k, GroupManagement, MfaReset, DhcpAuthorization, and the never-referenced
shared component `Components/Shared/TicketNumberInput.razor`). What is missing is
exactly the per-module switch, and any validation at all on this module.

Auditing needs no shared change: `AuditService.LogLookupAction`
(`Services/AuditService.cs:219-227`) and `LogModuleAction` (`:191-200`) already take
an optional `ticketNumber` and write it as the `ticket` field (`:238`, `:211`). The
page currently passes neither.

## 2. Non-goals

- Changes to `ServiceNowService` beyond exposing its enabled state (one read-only
  property). Its table routing, state policy, HTTP handling, and dormant behavior
  for the eight existing callers are untouched.
- Enabling ServiceNow. That is deployment config plus one recorded pre-condition
  that is NOT this plan's work: `ServiceNowService` reads `ServiceNow:Password`
  from appsettings (`ServiceNowService.cs:21`), which the Constitution's credential
  rule (PAM-held service-integration passwords, ServiceNow named explicitly) does
  not permit for live use. Recorded in `.agents/decisions.md` 2026-08-31 as
  go-live work.
- Deciding the ServiceNow-unreachable policy (block vs allow-with-warning when the
  live API is down). The validator's result type carries a distinct `Unavailable`
  outcome so that decision stays in one place when it is made. Note the existing
  client folds "API error" and "ticket rejected" into one `IsValid=false`
  (`ServiceNowService.cs:64-72`); refining that mapping belongs to the go-live
  work, and until then both refuse.
- Rewiring the eight existing `ValidateTicketAsync` call sites, or the other
  modules' presence-only ticket fields, through the new validator. That sweep is
  future work named in the decision entry.
- Adopting or deleting the unused `TicketNumberInput.razor` component.
- Ticket writeback to ServiceNow.
- A ticket format regex or checksum in the presence-only mode.
- Showing the ticket in the results table or changing result columns. The future
  BitLocker CSV export (`.agents/state.md` older queue item 6) gets the ticket from
  the audit trail and page state; wiring that export is item 6's plan.
- A second ticket prompt at Reveal time. The gate is on the search; the reveal
  reuses the ticket that authorized the displayed result set.
- Changes to `AuditService` or the Event Log page.
- Changing search behavior, limits, truncation handling, or the live-AD path.

## 3. Acceptance criteria

Validator (shared):

- AC1: With the module's `ValidateTickets` config absent, blank, or `false`, a null,
  empty, or whitespace ticket is Rejected with an error naming the missing ticket;
  any non-blank ticket is Accepted. No ServiceNow call is made in this mode.
- AC2: With `ValidateTickets` set `true` while `ServiceNowService` is dormant
  (`Enabled` false), every ticket - including a non-blank one - returns Unavailable
  with a message stating validation is on but the ServiceNow integration is dormant.
- AC3: With `ValidateTickets` set `true` and `ServiceNowService` enabled, the
  validator delegates to `ValidateTicketAsync`: `IsValid=true` maps to Accepted,
  `IsValid=false` maps to Rejected carrying the client's message.
- AC4: The validator is per-module: it reads config for the `moduleId` it is given,
  and a corrupt module config yields Unavailable (fail closed), not Accepted. An
  ABSENT or blank `ValidateTickets` value behaves as Off (unset is not a mistype);
  a NON-EMPTY value that `bool.TryParse` rejects (`yes`, `on`, a typo) yields
  Unavailable with a message naming the invalid value - fail closed. This
  deliberately diverges from the `PreventSelfGrant` convention
  (`PermissionValidator.cs:62`, unparseable -> default): that flag is a behavior
  preference, this one is a switch an operator believes is enforcing validation,
  and a mistype must not silently mean Off (review finding btv-1).
- AC5: A blank ticket is Rejected in BOTH modes; the switch never waives presence.

BitLocker gate:

- AC6: `BitLockerRecoveryService.SearchByComputerNameAsync` and `SearchByKeyIdAsync`
  refuse (`Success = false`, the validator's message) whenever the validator returns
  Rejected or Unavailable, without opening the archive - and for `SearchByKeyIdAsync`
  even when the identifier itself is valid.
- AC7: With the switch Off and a non-blank ticket, both searches behave exactly as
  today - the existing service suite, updated only to pass a ticket and a fake
  accepting validator, stays green with no other edits.
- AC8: The search audit event carries the trimmed ticket in its `ticket` field, on
  success and on failure.
- AC9: The reveal audit event (`RevealRecoveryKey`) carries the ticket captured when
  the displayed result set was produced - not the live contents of the ticket box,
  which the operator may have edited since.
- AC10: The page cannot start a search without a ticket: Search button disabled,
  Enter-key handler refuses, and - because UI hiding is not security (Constitution,
  Authorization) - a directly invoked `SearchAsync` still ends in the service
  refusal, shown in the existing error banner and audited as a failed search.

Versions, config surface, docs:

- AC11: `BitLockerRecovery` catalog version is `1.1.0` (S3) and the base app
  version is bumped in S1, the slice that lands the shared change (shared
  validator + `ServiceNowService` property + `Program.cs` DI; the csv-2 rule).
- AC12: `BitLockerRecovery` gains a `ValidateTickets` ConfigField (Required: false,
  DefaultValue `"false"`) whose description states both modes plainly, that the
  value must be exactly true or false (anything else refuses searches), and the
  dormant-refusal behavior. Check
  `ExchangeAdminWeb.Tests/ModuleCatalogTests.cs` for assertions the new field
  breaks (none assert ConfigFields counts today; the alias count at `:109` is
  untouched because no new permission is added).
- AC13: `docs/BitLockerRecovery.md` and the README BitLocker Recovery section state
  that a ticket is required, audited, and validated per the switch.

## 4. Failure behavior

| Step / dependency | If it fails / is bypassed | The operator sees | System state afterward |
|---|---|---|---|
| Ticket blank, button path | Button is disabled | Disabled Search button | Nothing runs, nothing audited |
| Ticket blank, Enter key | Handler guard returns | Nothing happens | Nothing runs, nothing audited |
| Ticket blank, direct event invocation | Validator Rejects; service returns `Fail` before any I/O | Error banner: ticket required | Failed search audited via the existing `result.Success == false` path; `ticket` field null |
| Switch On, ServiceNow dormant (the current deployment state) | Validator returns Unavailable; service refuses | Error banner: validation on, integration dormant | Failed search audited; fail closed, not decorative |
| Switch On, ServiceNow enabled, ticket not found / not active / API error | Client returns `IsValid=false`; validator Rejects with the client's message | Error banner with the ServiceNow reason | Failed search audited |
| Module config corrupt | Validator returns Unavailable (AC4); the archive path's own corrupt-config refusal (`BitLockerRecoveryService.cs:182-187`) backstops it | Config error banner | Fail closed |
| `ValidateTickets` mistyped (`yes`, `on`, ...) | Validator returns Unavailable naming the invalid value (AC4, btv-1) | Error banner naming the setting | Fail closed - never silently presence-only |
| Operator clears/edits the ticket box after a search, then clicks Reveal | Reveal uses the captured search ticket (AC9) | Reveal works normally | Reveal audit carries the ticket that authorized the visible results |
| Audit write fails | `SafeAudit` unchanged (`BitLockerRecovery.razor:429-439`) | Result unchanged | Audit failure logged separately; never masks the operation (Constitution, Auditing) |
| The eight existing `ValidateTicketAsync` callers | Untouched - they keep calling the client directly, with its dormant pass-through | No change anywhere else | Per-module isolation preserved; the validator takes `moduleId` explicitly |

## 5. Rollback / blast radius

Revert the commit(s). No schema, no stored-state, no authorization-policy change; the
audit `ticket` field is one the writers and the Event Log reader already handle. The
`ValidateTickets` config field defaults off, so deploying this changes nothing for
any module except BitLocker's new required input. `ServiceNowService` gains a
read-only property; its behavior for existing callers is unchanged.

Blast radius: the BitLocker page and service, one new shared service registered in
DI (consumed by nothing else yet), one property on `ServiceNowService`, one catalog
config field. The operator-visible change is a new required input; an operator
mid-recovery-call after deploy must have a ticket number to proceed, which is the
point of the feature and is called out in the docs update.

## 6. Design sketch

### Current code (read at `21870ed`, not remembered)

- `Components/Pages/BitLockerRecovery.razor` - search card `:37-78` (two inputs +
  Search button gated on `HasSearchTerm` `:245-246`), Enter handler `:267-273`,
  `SearchAsync` `:275-349` (audits via `LogLookupAction` `:332-338`, no ticket),
  `RevealAsync` `:351-372` (audits via `LogModuleAction` `:357-369`, no ticket).
- `Services/BitLockerRecoveryService.cs` - `SearchByComputerNameAsync:42` and
  `SearchByKeyIdAsync:80` each open with a blank-input guard returning
  `BitLockerSearchResult.Fail(...)` (`:46-49`, `:85-89`). The ticket gate goes first.
- `Services/ServiceNowService.cs` - `ValidateTicketAsync:33`; dormant short-circuit
  `:35-43` returns `IsValid=true` when `_enabled` is false; `_enabled` is private
  with no accessor (`:12,22`); the result type is named `TicketValidationResult`
  (`:142`) - **name collision**: the new validator's types must not reuse that
  name. Registered `AddSingleton` (`Program.cs:192`) with a named HttpClient
  (`Program.cs:97`).
- Per-module bool switch precedent: `PreventSelfGrant`
  (`Modules/ModuleCatalog.cs:150`, DefaultValue `"true"`; read at
  `PermissionValidator.cs:62` via `bool.TryParse`). `ModuleConfigService.GetValue`
  does NOT apply catalog defaults - callers own the absent-value fallback (the
  CloudQuotaGB lesson, `.agents/state.md`), so the validator treats absent as Off.
  `ModuleConfigService` is a singleton (`Program.cs:110`).
- `Services/AuditService.cs` - `LogLookupAction` `ticketNumber` param `:226`,
  written `:238`; `LogModuleAction` param `:198`, written `:211`. Untouched.

### Change to `ServiceNowService` (one line)

`public bool Enabled => _enabled;` - the validator must distinguish "dormant" from
"validated", and today nothing outside the class can see `_enabled`. No behavior
change for existing callers.

### New shared file: `Services/TicketValidationService.cs`

```
public enum TicketGateOutcome { Accepted, Rejected, Unavailable }

public sealed record TicketGateResult(TicketGateOutcome Outcome, string? Message)
{
    public bool Accepted => Outcome == TicketGateOutcome.Accepted;
}

public interface ITicketValidator
{
    Task<TicketGateResult> ValidateAsync(string moduleId, string? ticketNumber);
}

public sealed class TicketValidationService : ITicketValidator
```

(`TicketGate*` names avoid the existing `TicketValidationResult` at
`ServiceNowService.cs:142`.)

Behavior of `ValidateAsync`, in order:

1. `IsModuleCorrupt(moduleId)` -> Unavailable ("module configuration is
   unreadable...") - fail closed (Constitution, Configuration).
2. Blank/whitespace ticket -> Rejected ("A ticket number is required.") - in BOTH
   modes; the switch never waives presence.
3. Read `GetValue(moduleId, "ValidateTickets")`. Absent/blank or `false` ->
   Accepted (presence-only mode; no ServiceNow call). Non-empty and not parseable
   as a boolean -> Unavailable ("The ValidateTickets setting for this module is
   '<value>', which is not true or false. Fix it in Module Config.") - fail
   closed per AC4.
4. `true` and `ServiceNowService.Enabled` false -> Unavailable ("Ticket validation
   is switched on for this module, but the ServiceNow integration is not enabled
   on this deployment.").
5. `true` and enabled -> `await _serviceNow.ValidateTicketAsync(ticket)`;
   `IsValid=true` -> Accepted, else Rejected with the client's `Message`.

Constructor: `ModuleConfigService`, `ServiceNowService` (both singletons). DI:
`builder.Services.AddSingleton<ITicketValidator, TicketValidationService>();` in
`Program.cs` near the other shared services. Consumers audit; the validator does
not - actor and IP live at the page.

### BitLocker service gate (the enforcement point)

Add a required `string ticketNumber` parameter to both public search methods:

```
SearchByComputerNameAsync(string computerName, string ticketNumber, bool includeLiveAd = false)
SearchByKeyIdAsync(string keyId, string ticketNumber, bool includeLiveAd = false)
```

First statement of each: `await _ticketValidator.ValidateAsync(ModuleId, ticketNumber)`;
not Accepted -> `BitLockerSearchResult.Fail(result.Message ?? ...)`. Constructor
gains `ITicketValidator`. Required parameter, no default, so every caller decides at
compile time; the page is the only caller today (`Program.cs:134` registers the
service; grep confirms no other consumer), and the ~30 existing test call sites gain
a ticket argument.

### Page

In the search card: a Ticket Number input (`@bind="ticketNumber"
@bind:event="oninput"`, placeholder "INC/REQ number", label marking it required),
shaped like the M365GroupManagement one - the inline-input pattern every ticketed
page uses; the unused `TicketNumberInput` component is deliberately not adopted.
New `HasTicket => !string.IsNullOrWhiteSpace(ticketNumber)`. Button
`disabled="@(isSearching || !HasSearchTerm || !HasTicket)"`; Enter handler adds
`HasTicket`. The ticket is NOT cleared by a search: one recovery call is one ticket
across several search refinements.

In `SearchAsync`: `var ticket = ticketNumber.Trim();` captured into a `searchTicket`
field alongside the result state, passed to the service call and as `ticketNumber:`
on the existing `LogLookupAction` call (success and failure alike).

In `RevealAsync`: pass `ticketNumber: searchTicket` on the existing
`LogModuleAction` call. `searchTicket` is always non-blank when results exist,
because results only come from a gated search.

### Why the gate is in the service and not only the page

Constitution, Authorization: *"UI hiding is not security. Direct URL access and
direct event invocation must still be denied."* This repo has shipped a page-only
gate that was bypassed (`GroupManagementService.cs:35-40` records it: the page's
'@'-gated protected-member check was skipped by `DOMAIN\user` or sAMAccountName
input, and by any non-page caller; review finding gmn-2 caught the same shape being
planned again). The service refusal is the control; it is also the only layer a test
can exercise - there is no bUnit harness, so nothing automated renders the page.

## 7. Task breakdown

One commit per slice; each slice compiles and passes on its own (the ru-2 lesson).

**S1 - `ServiceNowService.Enabled`, shared validator, DI, tests, base app bump.**
Serves AC1-AC5 and the base-bump half of AC11.

- The one-line `Enabled` property on `ServiceNowService`.
- `Services/TicketValidationService.cs` as specified in section 6.
- `Program.cs` DI registration.
- New `ExchangeAdminWeb.Tests/TicketValidationServiceTests.cs` (section 8).
- Base app bump: `<VersionPrefix>` + `AssemblyVersion` + `FileVersion` in
  `ExchangeAdminWeb.csproj`. It lands here, not in S3, per the csv-2 review rule
  in the sibling plan (`.agents/review/findings/csv-2.md`): the bump ships with
  the shared-infrastructure change, so no deploy can carry the new shared code
  under the old base version.
- Nothing consumes the validator yet; behavior is unchanged.

**S2 - BitLocker gate, page wiring, config field, tests.** Serves AC6-AC10, AC12.

- Service: `ITicketValidator` constructor dependency + required `ticketNumber`
  parameter + first-statement gate on both search methods.
- Page: ticket input, `HasTicket`, button/Enter gating, `searchTicket` capture,
  both audit `ticketNumber:` arguments.
- Catalog: `ValidateTickets` ConfigField on the `BitLockerRecovery` entry
  (Required: false, DefaultValue `"false"`).
- Tests: update every existing call site in
  `ExchangeAdminWeb.Tests/BitLockerRecoveryTests.cs` to pass a ticket and a fake
  accepting validator (a shared constant like `"INC0000001"` keeps the diff
  readable); add the new gate tests (section 8); fix any `ModuleCatalogTests`
  assertion the config field breaks (none expected - no new permission alias).

**S3 - module version and docs.** Serves the module half of AC11, and AC13.

- `Modules/ModuleCatalog.cs:513` `Version = "1.1.0"`. (The base app bump already
  landed in S1.)
- `docs/BitLockerRecovery.md`: ticket required before any search, recorded on the
  search and reveal audit events; the `ValidateTickets` switch, both modes, and the
  dormant-refusal behavior. README BitLocker Recovery section: one bullet to the
  same effect. Locate both sections by reading at implementation time.

## 8. Test plan

Every AC appears at least once.

`ExchangeAdminWeb.Tests/TicketValidationServiceTests.cs` (S1). Fixture notes:
`ModuleConfigService` over a temp store, same style as
`BitLockerRecoveryTests.CreateModuleConfig`; `ServiceNowService` is concrete and
constructible in tests with an in-memory `IConfiguration` and a fake
`IHttpClientFactory` whose `HttpMessageHandler` stub returns canned responses -
dormant cases need no handler at all (`ServiceNow:Enabled` absent -> false).

| AC | Test | What it proves | Non-vacuity |
|---|---|---|---|
| AC1/AC5 | `Off_BlankTicketRejected` | null/empty/whitespace -> Rejected naming the ticket | Remove the blank guard; FAIL |
| AC1 | `Off_AnyNonBlankTicketAccepted` | "asdf" and "INC0001" both Accepted with the switch absent or `false`, and the HTTP handler records zero calls | Force a ServiceNow call in Off mode; FAIL |
| AC2 | `On_DormantServiceNowUnavailable` | `ValidateTickets=true` + `ServiceNow:Enabled` false -> Unavailable, message names the dormant integration | Fall through to the client's dormant `IsValid=true`; FAIL |
| AC3 | `On_EnabledDelegatesToServiceNow` | `ValidateTickets=true` + enabled + stubbed 200 with an active ticket -> Accepted; stubbed not-found -> Rejected with the client message | Bypass delegation, hardcode Accepted; FAIL |
| AC4 | `ReadsConfigForTheModuleItIsGiven` | Module A On / module B Off validate differently | Hardcode the module id; FAIL |
| AC4 | `CorruptConfigUnavailable` | Corrupt store -> Unavailable, not Accepted | Fall through to Off on corrupt; FAIL |
| AC4 | `UnparseableSwitchUnavailable` | `ValidateTickets=banana` -> Unavailable, message names `banana`; `ValidateTickets=` (blank) -> presence-only | Treat unparseable as Off; FAIL |
| AC5 | `On_BlankTicketStillRejected` | Blank + On -> Rejected, before any dormancy/delegation logic | Reorder guards; FAIL |

`ExchangeAdminWeb.Tests/BitLockerRecoveryTests.cs` (S2):

| AC | Test | What it proves | Non-vacuity |
|---|---|---|---|
| AC6 | `Search_RejectedTicketRefusesWithoutSearching` | Fake validator returning Rejected: both methods return `Success = false`, zero keys, against an archive containing a matching row | Delete the gate; rows come back; FAIL |
| AC6 | `Search_UnavailableValidatorRefuses` | Unavailable also refuses (fail closed), message surfaced | Map Unavailable to allow; FAIL |
| AC7 | Entire existing suite, ticket + accepting fake added | Off-mode ticketed searches behave exactly as before | The suite is the proof |
| AC8 | `SearchAsync_PassesTicketToServiceAndAudit` (source guard on the razor file, same mechanism as `EventLogCsvWiringTests`) | `SearchAsync` passes the ticket into both service calls and `ticketNumber: searchTicket` into `LogLookupAction` | Remove either argument; FAIL |
| AC9 | `RevealAsync_AuditsWithCapturedSearchTicket` (source guard) | `RevealAsync` contains `ticketNumber: searchTicket` | Remove the argument; FAIL |
| AC10 | `SearchControls_GateOnTicket` (source guard) | Button `disabled` expression and Enter handler both reference `HasTicket` | Remove either; FAIL |
| AC12 | (implementation check) | Catalog field present; `ModuleCatalogTests` green | n/a |

Source-guard caveat, known from blr-3/blr-4: a source-text assertion can be satisfied
by a comment. The gate is therefore behaviorally enforced and tested in the service;
the source guards only pin page-to-audit wiring, which has no behavioral test surface
without bUnit. Manual check 3 is the end-to-end proof.

Manual checks after deploy (the suite cannot render the page):

1. Open the page: Search stays disabled until both a search value and a ticket are
   present.
2. With a search term and no ticket, press Enter: no search runs.
3. Run a ticketed search and reveal one key; open Admin Event Log, expand the
   `BitLockerSearchByName` and `RevealRecoveryKey` events, confirm both show the
   ticket; download the Event Log CSV and confirm the `Ticket` column carries it on
   both rows.
4. In Module Config, set BitLocker `ValidateTickets` to `true`; confirm any search
   now refuses with the dormant-integration message; set it back to `false`.

Verification commands (from `.agents/repo-guidance.md`):

```
dotnet build ExchangeAdminWeb.slnx -c Release
dotnet test ExchangeAdminWeb.slnx
dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore
git diff --check HEAD
```

Non-vacuity: revert the named target of each new test, confirm FAIL, restore,
confirm PASS. Prove the revert actually applied before trusting the result.

## 9. Traceability check

To be completed at implementation time: each AC named against the landed test or
commit, plus the non-vacuity run record, as in `docs/EventLogCsvTicket-Plan.md`
section 9.

## 10. Review log

- 2026-08-31: openreview codex (`@azure-openai-eus2-global/gpt-5.5-dzs` @ xhigh,
  grade fallback, owner-named dispatch; codex-cli 0.150.1) over `a9b0ebc..533c1fe`
  (this plan together with `docs/ModuleCsvExport-Plan.md`): verdict
  `acceptable_with_changes`, capability_ok, both SHAs echoed. One finding against
  this plan: **btv-1 (HIGH)** - the unparseable-switch rule was fail-open
  (mistyped `ValidateTickets` silently meant Off). Admitted and folded in: AC4,
  design step 3, failure table, test table, AC12. The reviewer's alternate remedy
  (a new Boolean `ConfigFieldType` rendered as a checkbox) was NOT adopted - no
  such field type exists (`Modules/ModuleConfigField.cs:3-8`) and adding one is
  Module Config UI scope this plan does not need; the fail-closed parse achieves
  the safety property. Record: `.agents/review/findings/btv-1.md`.
