# kd-7: M365 Group Management validates no ticket; the same shape on Named Locations delete and twice on Conference Rooms

**Severity**: MEDIUM - mutating actions without the ticket validation the Constitution and the other modules apply. `M365GroupManagement` validates no ticket on its writes; `NamedLocations` delete and two Conference Rooms paths share the shape. Conference Rooms and Named Locations were not re-verified at the last state pass; the implementing agent verifies each before writing.
**Status**: Open - released by the owner's 2026-10-23 evening ruling (`.agents/decisions.md`); approved under the 2026-10-21 go
**Branch**: -
**Commit**: -
**Reviewer**: implementer (recorded in `.agents/state.md` "Known defects"; M365 re-verified 2026-10-23 as of `d881ea58`)

## Evidence

`Components/Pages/M365GroupManagement.razor` and `Services/M365GroupManagementService.cs`; `Components/Pages/NamedLocations.razor` (delete); `Components/Pages/ConferenceRooms.razor` (two paths - locate). Trigger: a write with an empty or malformed ticket. Predicted failure: the write proceeds and the audit row carries no valid ticket.

## Approach (to be written by the implementing agent before code)

- Each listed write path validates the ticket through the shared `TicketNumberInput` / validator shape the other modules use, refuses with an audited denial, and the page tests pin it; pages are line-keyed.
- Mutation: one validation removed per page.

## Guard proof

Pending.