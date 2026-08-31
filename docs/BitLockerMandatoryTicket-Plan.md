# BitLocker Recovery mandatory ticket field + shared ticket-validation seam

Status: Draft 2026-08-31, revised same day after the owner's ServiceNow ruling
(`.agents/decisions.md` 2026-08-31, "ServiceNow ticket validation: required later,
seam built now, per-module switch"). No owner decision is outstanding; awaiting a go
to implement at S1.
Owner: Michael
Last verified against code: `a9b0ebc` / 2026-08-31
Versions: `BitLockerRecovery` `1.0.2` -> `1.1.0` AND a base app bump
(`ExchangeAdminWeb.csproj` `<VersionPrefix>`, read current value at implementation
time) - the validator is shared infrastructure registered in `Program.cs`.
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
2. A shared, backend-agnostic ticket validator exists that ANY module with a ticket
   field can later route through, with a per-module Module Config switch:
   - **Off (default): presence-only.** Any non-blank ticket is accepted as plain
     audit metadata - exactly the existing repo shape
     (`M365GroupManagement.razor:117-125`, `string.IsNullOrWhiteSpace`, placeholder
     "INC/REQ number"). No format regex.
   - **On: real validation.** Until a validation backend exists, On refuses with a
     message naming the unconfigured integration - fail closed and visible, never a
     switch that silently accepts (the decorative-control trap this repo has already
     paid for: idm-3, and the `EmailService._notifyUsers` shape pinned in
     `docs/IntuneDeviceManagement-Plan.md`).

Auditing needs no shared change: `AuditService.LogLookupAction`
(`Services/AuditService.cs:219-227`) and `LogModuleAction` (`:191-200`) already take
an optional `ticketNumber` and write it as the `ticket` field (`:238`, `:211`). The
page currently passes neither.

## 2. Non-goals

- The ServiceNow client itself - API base URL, auth, credential, reachability
  policy. No API access exists yet. That is its own future plan; its credential
  comes from the PAM store (Constitution, Credential Isolation, which already names
  ServiceNow). This plan's seam is where it will plug in.
- Deciding the SNow-unreachable policy (block vs allow-with-warning when ServiceNow
  is down). The validator's result type carries a distinct `Unavailable` outcome so
  that decision stays confined to the future backend plan.
- Rewiring the other modules' existing ticket fields (MailboxPermissions,
  CalendarPermissions, Migration, M365GroupManagement, ...) through the validator.
  They keep their current presence-only gates until the backend lands; the rewiring
  sweep is future work named in the decision entry.
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
  any non-blank ticket is Accepted.
- AC2: With `ValidateTickets` set `true`, every ticket - including a non-blank one -
  returns Unavailable with a message stating ticket validation is switched on but no
  validation backend is configured.
- AC3: The validator is per-module: it reads config for the `moduleId` it is given,
  and a corrupt module config yields Unavailable (fail closed), not Accepted.
- AC4: An unparseable `ValidateTickets` value (not `true`/`false`) behaves as Off -
  same convention as `PreventSelfGrant` (`PermissionValidator.cs:62`,
  `bool.TryParse` guarded).

BitLocker gate:

- AC5: `BitLockerRecoveryService.SearchByComputerNameAsync` and `SearchByKeyIdAsync`
  refuse (`Success = false`, the validator's message) whenever the validator returns
  Rejected or Unavailable, without opening the archive - and for `SearchByKeyIdAsync`
  even when the identifier itself is valid.
- AC6: With the switch Off and a non-blank ticket, both searches behave exactly as
  today - the existing service suite, updated only to pass a ticket and a fake
  accepting validator, stays green with no other edits.
- AC7: The search audit event carries the trimmed ticket in its `ticket` field, on
  success and on failure.
- AC8: The reveal audit event (`RevealRecoveryKey`) carries the ticket captured when
  the displayed result set was produced - not the live contents of the ticket box,
  which the operator may have edited since.
- AC9: The page cannot start a search without a ticket: Search button disabled,
  Enter-key handler refuses, and - because UI hiding is not security (Constitution,
  Authorization) - a directly invoked `SearchAsync` still ends in the service
  refusal, shown in the existing error banner and audited as a failed search.

Versions, config surface, docs:

- AC10: `BitLockerRecovery` catalog version is `1.1.0` and the base app version is
  bumped (shared service + `Program.cs` DI registration).
- AC11: `BitLockerRecovery` gains a `ValidateTickets` ConfigField (Required: false,
  DefaultValue `"false"`) whose description states both modes plainly, including
  that On refuses everything until the validation backend exists. Check
  `ExchangeAdminWeb.Tests/ModuleCatalogTests.cs` for assertions the new field breaks
  (the ru-3 lesson: count/shape assertions live at `:16,109`).
- AC12: `docs/BitLockerRecovery.md` and the README BitLocker Recovery section state
  that a ticket is required, audited, and validated per the switch.

## 4. Failure behavior

| Step / dependency | If it fails / is bypassed | The operator sees | System state afterward |
|---|---|---|---|
| Ticket blank, button path | Button is disabled | Disabled Search button | Nothing runs, nothing audited |
| Ticket blank, Enter key | Handler guard returns | Nothing happens | Nothing runs, nothing audited |
| Ticket blank, direct event invocation | Validator Rejects; service returns `Fail` before any I/O | Error banner: ticket required | Failed search audited via the existing `result.Success == false` path; `ticket` field null |
| Switch On, no backend configured (the only On state this plan ships) | Validator returns Unavailable; service refuses | Error banner: validation on, backend not configured | Failed search audited; fail closed, not decorative |
| Module config corrupt | Validator returns Unavailable (AC3); the archive path's own corrupt-config refusal (`BitLockerRecoveryService.cs:182-187`) backstops it | Config error banner | Fail closed |
| Operator clears/edits the ticket box after a search, then clicks Reveal | Reveal uses the captured search ticket (AC8) | Reveal works normally | Reveal audit carries the ticket that authorized the visible results |
| Audit write fails | `SafeAudit` unchanged (`BitLockerRecovery.razor:429-439`) | Result unchanged | Audit failure logged separately; never masks the operation (Constitution, Auditing) |
| A future module wires in wrongly | The validator takes `moduleId` explicitly; each caller names itself, so one module's switch cannot gate another | n/a | Per-module isolation preserved |

## 5. Rollback / blast radius

Revert the commit(s). No schema, no stored-state, no authorization-policy change; the
audit `ticket` field is one the writers and the Event Log reader already handle. The
`ValidateTickets` config field defaults off, so deploying this changes nothing for
any module except BitLocker's new required input.

Blast radius: the BitLocker page and service, one new shared service registered in
DI (used by nothing else yet), one catalog config field. The operator-visible change
is a new required input; an operator mid-recovery-call after deploy must have a
ticket number to proceed, which is the point of the feature and is called out in the
docs update.

## 6. Design sketch

### Current code (read at `a9b0ebc`, not remembered)

- `Components/Pages/BitLockerRecovery.razor` - search card `:37-78` (two inputs +
  Search button gated on `HasSearchTerm` `:245-246`), Enter handler `:267-273`,
  `SearchAsync` `:275-349` (audits via `LogLookupAction` `:332-338`, no ticket),
  `RevealAsync` `:351-372` (audits via `LogModuleAction` `:357-369`, no ticket).
- `Services/BitLockerRecoveryService.cs` - `SearchByComputerNameAsync:42` and
  `SearchByKeyIdAsync:80` each open with a blank-input guard returning
  `BitLockerSearchResult.Fail(...)` (`:46-49`, `:85-89`). The ticket gate goes first.
- Per-module bool switch precedent: `PreventSelfGrant`
  (`Modules/ModuleCatalog.cs:150`, DefaultValue `"true"`; read at
  `PermissionValidator.cs:62` via `bool.TryParse`). `ModuleConfigService.GetValue`
  does NOT apply catalog defaults - callers own the absent-value fallback (the
  CloudQuotaGB lesson, `.agents/state.md`), so the validator treats absent as Off.
- `Services/AuditService.cs` - `LogLookupAction` `ticketNumber` param `:226`,
  written `:238`; `LogModuleAction` param `:198`, written `:211`. Untouched.

### New shared file: `Services/TicketValidationService.cs`

```
public enum TicketValidationOutcome { Accepted, Rejected, Unavailable }

public sealed record TicketValidationResult(
    TicketValidationOutcome Outcome, string? Message)
{
    public bool Accepted => Outcome == TicketValidationOutcome.Accepted;
}

public interface ITicketValidator
{
    Task<TicketValidationResult> ValidateAsync(string moduleId, string? ticketNumber);
}

public sealed class TicketValidationService : ITicketValidator
```

Behavior of `ValidateAsync`, in order:

1. `IsModuleCorrupt(moduleId)` -> Unavailable ("module configuration is
   unreadable...") - fail closed (Constitution, Configuration).
2. Blank/whitespace ticket -> Rejected ("A ticket number is required.") - in BOTH
   modes; the switch never waives presence.
3. Read `GetValue(moduleId, "ValidateTickets")`; absent/blank/unparseable/`false`
   -> Accepted (presence-only mode).
4. `true` -> Unavailable ("Ticket validation is switched on for this module, but no
   ticket validation backend is configured yet."). This is the ONLY On behavior this
   plan ships. The future backend plan replaces step 4 with the real call; `Rejected`
   vs `Unavailable` is the seam that keeps the unreachable-policy decision out of
   every consumer.

Backend-agnostic on purpose (the PAM-seam reasoning, Constitution, Credential
Isolation): ServiceNow is the planned backend, but the interface and result type do
not name it. DI: `builder.Services.AddScoped<ITicketValidator, TicketValidationService>();`
in `Program.cs` near the other shared services. `Task`-returning now precisely so the
HTTP-backed implementation is not a signature change later.

Consumers audit; the validator does not - actor and IP live at the page, and the
validator has neither.

### BitLocker service gate (the enforcement point)

Add a required `string ticketNumber` parameter to both public search methods:

```
SearchByComputerNameAsync(string computerName, string ticketNumber, bool includeLiveAd = false)
SearchByKeyIdAsync(string keyId, string ticketNumber, bool includeLiveAd = false)
```

First statement of each: `await _ticketValidator.ValidateAsync(ModuleId, ticketNumber)`;
not Accepted -> `BitLockerSearchResult.Fail(result.Message ?? ...)`. Constructor
gains `ITicketValidator` (DI unchanged shape). Required parameter, no default, so
every caller decides at compile time; the page is the only caller today
(`Program.cs:134` registers the service; grep confirms no other consumer), and the
~30 existing test call sites gain a ticket argument.

### Page

In the search card: a Ticket Number input (`@bind="ticketNumber"
@bind:event="oninput"`, placeholder "INC/REQ number", label marking it required),
shaped like the M365GroupManagement one. New
`HasTicket => !string.IsNullOrWhiteSpace(ticketNumber)`. Button
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

**S1 - shared validator + DI + tests.** Serves AC1-AC4.

- `Services/TicketValidationService.cs` as specified in section 6.
- `Program.cs` DI registration.
- New `ExchangeAdminWeb.Tests/TicketValidationServiceTests.cs` (section 8).
- Nothing consumes it yet; the app builds and behaves identically.

**S2 - BitLocker gate, page wiring, config field, tests.** Serves AC5-AC9, AC11.

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
  assertion the config field breaks.

**S3 - versions and docs.** Serves AC10, AC12.

- `Modules/ModuleCatalog.cs:513` `Version = "1.1.0"`.
- Base app bump: `<VersionPrefix>` + `AssemblyVersion` + `FileVersion` in
  `ExchangeAdminWeb.csproj` (read the current number there, then minor-bump).
- `docs/BitLockerRecovery.md`: ticket required before any search, recorded on the
  search and reveal audit events; the `ValidateTickets` switch, both modes, and that
  On refuses until the validation backend exists. README BitLocker Recovery
  section: one bullet to the same effect. Locate both sections by reading at
  implementation time.

## 8. Test plan

Every AC appears at least once.

`ExchangeAdminWeb.Tests/TicketValidationServiceTests.cs` (S1, real
`ModuleConfigService` over a temp store, same fixture style as
`BitLockerRecoveryTests.CreateModuleConfig`):

| AC | Test | What it proves | Non-vacuity |
|---|---|---|---|
| AC1 | `Off_BlankTicketRejected` | null/empty/whitespace -> Rejected with a message naming the ticket | Remove the blank guard; FAIL |
| AC1 | `Off_AnyNonBlankTicketAccepted` | "asdf" and "INC0001" both Accepted when the switch is absent or `false` | Force Rejected; FAIL |
| AC2 | `On_NonBlankTicketUnavailable` | `ValidateTickets=true` -> Unavailable, message names the unconfigured backend | Make On accept; FAIL |
| AC2 | `On_BlankTicketStillRejected` | Blank + On -> Rejected (presence is never waived) | Reorder guards; FAIL |
| AC3 | `ReadsConfigForTheModuleItIsGiven` | Module A On / module B Off validate differently | Hardcode the module id; FAIL |
| AC3 | `CorruptConfigUnavailable` | Corrupt store -> Unavailable, not Accepted | Fall through to Off on corrupt; FAIL |
| AC4 | `UnparseableSwitchBehavesAsOff` | `ValidateTickets=banana` -> presence-only | Treat unparseable as On; FAIL |

`ExchangeAdminWeb.Tests/BitLockerRecoveryTests.cs` (S2):

| AC | Test | What it proves | Non-vacuity |
|---|---|---|---|
| AC5 | `Search_RejectedTicketRefusesWithoutSearching` | Fake validator returning Rejected: both methods return `Success = false`, zero keys, against an archive containing a matching row | Delete the gate; rows come back; FAIL |
| AC5 | `Search_UnavailableValidatorRefuses` | Unavailable also refuses (fail closed), message surfaced | Map Unavailable to allow; FAIL |
| AC6 | Entire existing suite, ticket + accepting fake added | Off-mode ticketed searches behave exactly as before | The suite is the proof |
| AC7 | `SearchAsync_PassesTicketToServiceAndAudit` (source guard on the razor file, same mechanism as `EventLogCsvWiringTests`) | `SearchAsync` passes the ticket into both service calls and `ticketNumber: searchTicket` into `LogLookupAction` | Remove either argument; FAIL |
| AC8 | `RevealAsync_AuditsWithCapturedSearchTicket` (source guard) | `RevealAsync` contains `ticketNumber: searchTicket` | Remove the argument; FAIL |
| AC9 | `SearchControls_GateOnTicket` (source guard) | Button `disabled` expression and Enter handler both reference `HasTicket` | Remove either; FAIL |
| AC11 | `ModuleCatalogTests` adjustments as needed | Catalog shape assertions still bite | n/a |

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
   now refuses with the backend-not-configured message; set it back to `false`.

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

None yet.
