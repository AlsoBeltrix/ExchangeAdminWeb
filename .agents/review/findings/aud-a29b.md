# aud-a29b: revocation is not observable at all while a circuit stays open

**Severity**: HIGH - an operator removed from an allowed AD group keeps every permission they
had, on every page, for as long as their browser tab is open. `aud-a29` closed the
administrative half of this on one page; this is the rest.
**Status**: Open - needs an owner decision before any code
**Branch**: -
**Commit**: -

## Evidence

Raised by codex-commercial (gpt-6-astra, max) reviewing `d602cc5`, in answer to a direct
question about whether re-authorizing against a cached principal works.

`Program.cs:119` registers ordinary server components with no custom authentication-state
revalidator. `ServerAuthenticationStateProvider.GetAuthenticationStateAsync` therefore returns
the stored state task, and `WindowsPrincipal.IsInRole` tests the Windows access token rather
than asking the directory. `Authorization/GroupAuthorizationHandler` matches stored group SIDs
against the principal's claims (`:71`, `:117`, `:129`).

**Calling the authorization service again does not refresh group membership.** A Blazor Server
circuit can live for hours, so removing someone from an AD group does nothing until they
reload or the circuit drops.

This is app-wide. `aud-a29` added a per-click re-check to Admin Bulk Jobs and that re-check
does observe a **section-access** change, because that configuration is re-read from the
database. It cannot observe the directory change.

## Predicted observable failure

Someone leaves the team, or is removed from an admin group during an incident. Their open tab
continues to perform every action their former group allowed - across modules - and the audit
records each as a successful action by a named, then-unauthorized operator. Nobody reloads a
page to find out they no longer should have it.

## Options, for the owner

1. **A revalidating authentication state provider** - the framework's intended answer.
   `RevalidatingServerAuthenticationStateProvider` re-checks on an interval and closes the
   circuit when the principal is no longer valid. Fixes every page at once. Costs a periodic
   revalidation per circuit, and the interval is the window.
2. **A directory read per mutating action** - narrow, and only where it is added. One AD round
   trip on every click that changes something, on a path where the operator is already waiting.
   Does not help read-only disclosure.
3. **Accept it and shorten the circuit lifetime** - reduces the window without closing it.

Option 1 is the only one that makes the answer authoritative everywhere, and it is an app-wide
change to how authentication behaves. **It should not be chosen by an agent.**

## Reviewer comments

Source: codex-commercial / `gpt-6-astra` / max, 2026-10-19, finding 1 of the batched review of
`d602cc5`, `1a3c49f`, `87ec8cb`, `7799d6c`.

## Closeout

Not started. Owner decision required.
