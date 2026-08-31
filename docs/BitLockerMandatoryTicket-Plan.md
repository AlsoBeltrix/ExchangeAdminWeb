# BitLocker Recovery mandatory ticket field

Status: Draft 2026-08-31, awaiting owner go to implement. No owner decision is
outstanding: the two questions the work item required this plan to answer (shape-only
vs looked-up validation; ticket reaching the audit record) are both settled by
`docs/ProjectConstitution.md` and existing code, recorded in section 1.
Owner: Michael
Last verified against code: `a9b0ebc` / 2026-08-31
Module: `BitLockerRecovery` `1.0.2` -> `1.1.0` (module-scoped; no base app bump)
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`,
`docs/AdminModuleSpec.md`. On conflict the higher source wins.

Owner request 2026-08-27 (recorded in `.agents/state.md`, older queue item 7):
`BitLockerRecovery` *"must require a ticket number before the search runs and before
any result is displayed."*

## 1. Goal

The BitLocker Recovery page is the most sensitive read in the app - a result row is
one Reveal click away from a working disk-decryption key. Today a search runs with no
ticket, and neither the search audit (`BitLockerSearchByName` / `BitLockerSearchByKeyId`)
nor the reveal audit (`RevealRecoveryKey`) carries one.

Done means: no search runs, and therefore no result renders, without a non-blank
ticket number; the ticket is written on both the search and the reveal audit events,
so the Event Log page and its CSV export (which already carries a `Ticket` column,
`docs/EventLogCsvTicket-Plan.md`) tie every key disclosure to a ticket.

Two questions this plan is required to answer, both already ruled by higher authority:

- **Validated for shape only, never looked up.** Constitution, External Integrations:
  *"Ticket fields are plain audit metadata unless ServiceNow validation or writeback
  is explicitly requested"* - and Never Do forbids sneaking ServiceNow validation in.
  This request did not ask for ServiceNow. "Shape" here is the existing repo shape:
  non-blank after trim, exactly the `string.IsNullOrWhiteSpace` gate
  `M365GroupManagement.razor:117-125` already uses (placeholder "INC/REQ number").
  No format regex: no module validates ticket format today, and a pattern would
  refuse legitimate tickets from any system whose numbers do not match it.
- **It reaches the audit record through parameters that already exist.**
  `AuditService.LogLookupAction` (`Services/AuditService.cs:219-227`) and
  `LogModuleAction` (`:191-200`) both take an optional `ticketNumber` and write it as
  the `ticket` field (`:238`, `:211`). The page currently passes neither. No
  `AuditService` change is needed, which is what keeps this module-scoped.

## 2. Non-goals

- ServiceNow lookup, validation, or writeback (Constitution, Never Do).
- A ticket format regex or checksum.
- A shared/reusable ticket-gate component or helper outside this module. That would
  be a base-app-version change; nothing here needs one. If the CSV-export plan
  (`.agents/state.md` older queue item 6) later wants a shared gate, that is its
  decision to put to the owner, not this plan's.
- Showing the ticket in the results table or changing the result columns. The future
  BitLocker CSV export gets the ticket from the audit trail and page state; wiring
  that export is item 6's plan.
- A second ticket prompt at Reveal time. The owner's gate is on the search; the
  reveal reuses the ticket that authorized the displayed result set.
- Changes to `AuditService`, other modules, or the Event Log page.
- Changing search behavior, limits, truncation handling, or the live-AD path.

## 3. Acceptance criteria

- AC1: `BitLockerRecoveryService.SearchByComputerNameAsync` with a null, empty, or
  whitespace ticket returns `Success = false` with an error naming the missing
  ticket, without reading module config or opening the archive.
- AC2: Same for `SearchByKeyIdAsync`, and the refusal happens even when the
  identifier itself is valid.
- AC3: With a non-blank ticket, both searches behave exactly as today - the existing
  service suite, updated only to pass a ticket, stays green with no other edits.
- AC4: The search audit event (`BitLockerSearchByName` / `BitLockerSearchByKeyId`)
  carries the trimmed ticket in its `ticket` field, on success and on failure.
- AC5: The reveal audit event (`RevealRecoveryKey`) carries the ticket that was
  captured when the displayed result set was produced - not the live contents of the
  ticket box, which the operator may have edited since.
- AC6: The page cannot start a search without a ticket: the Search button is
  disabled, the Enter-key handler refuses, and - because UI hiding is not security
  (Constitution, Authorization) - a directly invoked `SearchAsync` still ends in the
  service refusal from AC1/AC2, shown in the existing error banner and audited as a
  failed search.
- AC7: `BitLockerRecovery` catalog version is `1.1.0`
  (`Modules/ModuleCatalog.cs:513`). Base app version (`ExchangeAdminWeb.csproj`
  `<VersionPrefix>`) is unchanged.
- AC8: `docs/BitLockerRecovery.md` and the README BitLocker Recovery section state
  that a ticket number is required and is audited.

## 4. Failure behavior

| Step / dependency | If it fails / is bypassed | The operator sees | System state afterward |
|---|---|---|---|
| Ticket blank, button path | Button is disabled | Disabled Search button | Nothing runs, nothing audited |
| Ticket blank, Enter key | Handler guard returns | Nothing happens | Nothing runs, nothing audited |
| Ticket blank, direct event invocation | Service returns `Fail` before any I/O (AC1/AC2) | Error banner: ticket required | Failed search audited via the existing `result.Success == false` path; `ticket` field null |
| Operator clears/edits the ticket box after a search, then clicks Reveal | Reveal uses the captured search ticket (AC5) | Reveal works normally | Reveal audit carries the ticket that authorized the visible results |
| Audit write fails | `SafeAudit` unchanged (`BitLockerRecovery.razor:429-439`) | Result unchanged | Audit failure logged separately; never masks the operation (Constitution, Auditing) |
| Any other caller of the service | None exists (`Program.cs:134` DI, page is the sole consumer); the new parameter is required, so a future caller cannot compile without deciding | n/a | n/a |

## 5. Rollback / blast radius

Revert the commit(s). No schema, no config, no stored-state, no authorization-policy
change; the audit `ticket` field is one the writers and the Event Log reader already
handle (empty ticket is omitted from JSONL exactly as today).

Blast radius is this one page and its service. The operator-visible change is a new
required input; an operator mid-recovery-call after deploy must have a ticket number
to proceed, which is the point of the feature and is called out in the docs update.

## 6. Design sketch

### Current code (read at `a9b0ebc`, not remembered)

- `Components/Pages/BitLockerRecovery.razor` - search card `:37-78` (two inputs +
  Search button gated on `HasSearchTerm` `:245-246`), Enter handler `:267-273`,
  `SearchAsync` `:275-349` (audits via `LogLookupAction` `:332-338`, no ticket),
  `RevealAsync` `:351-372` (audits via `LogModuleAction` `:357-369`, no ticket).
- `Services/BitLockerRecoveryService.cs` - `SearchByComputerNameAsync:42` and
  `SearchByKeyIdAsync:80` each open with a blank-input guard returning
  `BitLockerSearchResult.Fail(...)` (`:46-49`, `:85-89`). The ticket guard is the
  same shape, placed first.
- `Services/AuditService.cs` - `LogLookupAction` `ticketNumber` param `:226`,
  written `:238`; `LogModuleAction` param `:198`, written `:211`. Untouched.

### What to change

1. **Service gate (the enforcement point).** Add a required `string ticketNumber`
   parameter to both public search methods:

   ```
   SearchByComputerNameAsync(string computerName, string ticketNumber, bool includeLiveAd = false)
   SearchByKeyIdAsync(string keyId, string ticketNumber, bool includeLiveAd = false)
   ```

   First statement of each: blank ticket returns
   `BitLockerSearchResult.Fail("A ticket number is required before recovery keys can be searched.")`.
   Required (no default) so every caller decides at compile time. The page is the
   only caller today; the ~30 existing test call sites gain a ticket argument.
   The service does not audit (it never has); auditing stays on the page, which is
   where actor and IP live.

2. **Page.** In the search card: a Ticket Number input
   (`@bind="ticketNumber" @bind:event="oninput"`, placeholder "INC/REQ number",
   label marking it required), shaped like the M365GroupManagement one. New
   `HasTicket => !string.IsNullOrWhiteSpace(ticketNumber)`. Button
   `disabled="@(isSearching || !HasSearchTerm || !HasTicket)"`; Enter handler adds
   `HasTicket`. The ticket is NOT cleared by a search: one recovery call is one
   ticket across several search refinements.

   In `SearchAsync`: `var ticket = ticketNumber.Trim();` captured into a
   `searchTicket` field alongside the result state, passed to the service call and
   as `ticketNumber:` on the existing `LogLookupAction` call (success and failure
   alike - the audit line already handles both).

   In `RevealAsync`: pass `ticketNumber: searchTicket` on the existing
   `LogModuleAction` call. `searchTicket` is always non-blank when results exist,
   because results only come from a gated search.

3. **Catalog + docs.** Version `1.0.2` -> `1.1.0` at `Modules/ModuleCatalog.cs:513`.
   `docs/BitLockerRecovery.md`: a short paragraph in its operator section - ticket
   required before any search, recorded on the search and reveal audit events, plain
   metadata (no ServiceNow lookup). README BitLocker Recovery section: one bullet to
   the same effect. Locate both sections by reading at implementation time.

### Why the gate is in the service and not only the page

Constitution, Authorization: *"UI hiding is not security. Direct URL access and
direct event invocation must still be denied."* This repo has shipped page-only gates
that were bypassed at the service layer before (`GroupManagementService.cs:36-38`
records one; review finding gmn-2 was the same shape). A disabled button is UX; the
service refusal is the control, and it is also the only layer a test can exercise -
there is no bUnit harness, so nothing automated renders the page.

## 7. Task breakdown

One commit per slice.

**S1 - service gate, page wiring, tests.** Serves AC1-AC6. One slice because the
required parameter breaks the page and the test suite in the same compile.

- Add the `ticketNumber` parameter and guard to both service methods.
- Add the Ticket input, `HasTicket`, button/Enter gating, `searchTicket` capture,
  and both audit `ticketNumber:` arguments to `BitLockerRecovery.razor`.
- Update every existing call site in `ExchangeAdminWeb.Tests/BitLockerRecoveryTests.cs`
  to pass a ticket (a shared constant like `"INC0000001"` keeps the diff readable).
- Add the new tests (section 8).

**S2 - version and docs.** Serves AC7, AC8.

- `Modules/ModuleCatalog.cs:513` `Version = "1.1.0"`.
- `docs/BitLockerRecovery.md` and README updates per section 6.

## 8. Test plan

Every AC appears at least once. New tests live in the existing
`ExchangeAdminWeb.Tests/BitLockerRecoveryTests.cs` (service-level, real archive
fixture via `CreateArchiveConfig`).

| AC | Test | What it proves | Non-vacuity |
|---|---|---|---|
| AC1 | `SearchByComputerName_BlankTicket_RefusesWithoutSearching` | Against an archive containing a matching row: null/empty/whitespace ticket each return `Success = false`, zero keys, error mentioning the ticket | Delete the guard; rows come back; FAIL |
| AC2 | `SearchByKeyId_BlankTicket_RefusesEvenWithValidIdentifier` | A valid key ID with a blank ticket is refused | Delete the guard; FAIL |
| AC1 | `BlankTicket_RefusesBeforeConfigIsRead` | Service built with corrupt/absent module config still returns the ticket error, not a config error - proves the guard runs first | Move the guard below the config check; FAIL (config error surfaces instead) |
| AC3 | Entire existing suite, ticket argument added | Ticketed searches behave exactly as before | The suite is the proof; any behavior drift fails it |
| AC4 | `SearchAsync_PassesTicketToServiceAndAudit` (source guard on the razor file, same mechanism as `EventLogCsvWiringTests`) | `SearchAsync` body contains `ticketNumber: searchTicket` on the `LogLookupAction` call and passes the ticket into both service calls | Remove either argument; FAIL |
| AC5 | `RevealAsync_AuditsWithCapturedSearchTicket` (source guard) | `RevealAsync` body contains `ticketNumber: searchTicket` | Remove the argument; FAIL |
| AC6 | `SearchControls_GateOnTicket` (source guard) | Button `disabled` expression and Enter handler both reference `HasTicket` | Remove either; FAIL |
| AC7 | (implementation check) | Catalog reads `1.1.0`; csproj `<VersionPrefix>` unchanged | n/a |
| AC8 | (implementation check) | Both docs updated | n/a |

Source-guard caveat, known from blr-3/blr-4: a source-text assertion can be satisfied
by a comment. The gate itself is therefore behaviorally enforced in the service
(AC1/AC2 tests), and the source guards only pin the page-to-audit wiring, whose
absence has no behavioral test surface without bUnit. Manual check 3 is the
end-to-end proof.

Manual checks after deploy (the suite cannot render the page):

1. Open the page: Search stays disabled until both a search value and a ticket are
   present.
2. With a search term and no ticket, press Enter: no search runs.
3. Run a ticketed search and reveal one key; open Admin Event Log, expand the
   `BitLockerSearchByName` and `RevealRecoveryKey` events, confirm both show the
   ticket.
4. Download the Event Log CSV; confirm the `Ticket` column carries the value on both
   rows.

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
