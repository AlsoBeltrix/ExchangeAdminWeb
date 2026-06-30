# Comms-10k Replace UX Clarity — Plan

Status: Approved (owner go 2026-06-29; items 1–5, ticket made optional)
Module: `Comms10k` (version `1.0.3` → `1.0.4`; app version unchanged)
Scope: `Components/Pages/Comms10k.razor` only. No service/logic change.

## Problem (evidence-backed)

A requester reported that Comms-10k membership "did not sync": the app showed
**"4309 users resolved"** but the Entra distribution-list count stayed at the old value.

Audit logs (`E:\WWWOutput\ExchangeAdminWeb`) settle the cause — it is **not a write
failure**:

- `2026-06-29 23:04` user `DGarcia4`: a single `Comms10k_Export` event (downloaded the
  list). **No `Comms10k_Replace`, no blocked/failed attempt — nothing was written to AD.**
- Last actual write of any kind: `2026-06-11` (`AKumar48`, 6840 members, Success). The
  group has not been changed since.

So the replace was never executed. The user stopped after **Validate**, treating
"4309 users resolved" as "done," then exported the list and left. This is a UX trap, not
a defect:

1. **"resolved" reads as "applied."** Validate only does an AD lookup; it writes nothing.
2. The **yellow "Replace All Members" button** (the actual apply step) does not read as
   "you are not finished — the list is unchanged until you click this."
3. There is no persistent **"not applied yet"** signal between Validate and Confirm.

## Goal

Make it unmistakable that, after Validate, **nothing has changed yet** and an explicit
apply step remains. Pure presentation change in the Razor page; the two-step
Validate → Replace → Confirm flow and all server-side logic stay exactly as they are.

## Changes (all in `Components/Pages/Comms10k.razor`)

1. **Relabel the post-Validate result.** Replace the heading wording
   `"<N> users resolved."` (line ~105) with language that names it as a check, not an
   action, e.g.:
   > **Validated — not applied yet.** `<N>` of `<parsed>` email(s) matched in AD. The
   > distribution list has **not** changed. Click **Replace All Members** below to apply.

2. **Add a persistent "not applied" status line** while `resolveResult != null` and the
   replace has not yet succeeded, so the unfinished state is visible even after scrolling
   past the alert (small `text-warning` / badge: "Pending apply — list unchanged").

3. **Strengthen the apply call-to-action.** Keep the existing warning-coloured
   "Replace All Members (N users)" button but make its role explicit — a short helper line
   directly above it ("This is the step that changes the list") and ensure it is the
   visually dominant control in that card after validation.

4. **Clarify the success state.** On a successful replace, the result alert already shows
   "Successfully updated … (was M)". Add a one-line note that the Entra/Outlook member
   count updates on the **next directory sync**, not immediately — pre-empting the exact
   "count didn't change" confusion.

5. **Make the ticket number optional** (owner direction 2026-06-29: the comms team are
   the consumers and won't always have a ticket). Today the Validate button is disabled
   until a ticket is typed (`Comms10k.razor:96`), which is what drove the requester to
   invent "INC1". Changes:
   - Remove `string.IsNullOrWhiteSpace(ticketNumber)` from the Validate button's
     `disabled` condition so Validate works with no ticket.
   - In `ValidateEmails`, only call `ServiceNow.ValidateTicketAsync` **when a ticket was
     provided**; skip ticket validation when the field is blank. A provided ticket is
     still validated (so ServiceNow enforcement, where enabled, is unchanged).
   - Relabel the field "Ticket Number (optional)" and soften the placeholder.
   This is consistent with `docs/ProjectConstitution.md:104` — ticket fields are plain
   audit metadata unless ServiceNow validation/writeback is requested; the empty ticket is
   simply recorded as such in the audit entry.

Apart from the ticket-optional change above, no wording change implies a behaviour change:
Validate still only validates, Replace still requires Confirm, protected-principal gating
and audit/notification are untouched.

## Out of scope

- No change to `Comms10kService` or any AD/Graph call.
- No change to the Validate → Replace → Confirm step structure (still two explicit clicks).
- No auto-apply, no removal of the confirm dialog.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release` then `dotnet test ExchangeAdminWeb.slnx`
  (no logic touched, but confirm the page still compiles and existing tests pass).
- `dotnet format ExchangeAdminWeb.csproj --verify-no-changes --no-restore`;
  `git diff --check HEAD`.
- Manual (owner, on dev): upload sample CSV → Validate → confirm the new wording reads
  "not applied yet"; confirm Replace → Confirm still works and success note mentions sync
  delay. This is a visual change with no automated UI coverage, so the manual pass is the
  real gate.

## Version / commit

- Bump `Comms10k` `Version` `1.0.3` → `1.0.4` in `Modules/ModuleCatalog.cs`
  (module-scoped behaviour/UX change; app `<VersionPrefix>` unchanged per the two-rule
  versioning policy).
- One commit; update `.agents/state.md` "Now" with the fix.
