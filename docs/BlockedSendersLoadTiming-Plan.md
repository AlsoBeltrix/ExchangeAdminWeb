# Blocked Senders — fix slow/blank page load

Status: Approved

## Problem

Clicking the Blocked Senders module in the sidebar gives no feedback and takes
10–15s before the page appears. BlockedSenders is the only module that calls
Exchange Online automatically on open: `OnInitializedAsync` ends with
`await LoadBlockedSenders()` (`Components/Pages/BlockedSenders.razor:161`), which
runs `Get-BlockedSenderAddress`.

With `@rendermode InteractiveServer`, `OnInitializedAsync` runs during server
prerender — before the page HTML is delivered to the browser. The EXO round-trip
therefore blocks the entire page from rendering, and the `isLoading` spinner
cannot show because it is part of a page that has not yet been sent. The 10–15s
is the EXO connect + cmdlet happening before anything is visible.

## Fix — auto-load on open, page appears immediately

Keep auto-load, but defer the EXO call until after first render so the page (and
spinner) is on screen first.

1. `OnInitializedAsync`: keep the auth check and client-IP resolution; set
   `isLoading = true`; set `authChecked = true`. Do **not** call
   `LoadBlockedSenders()` here.
2. Add `OnAfterRenderAsync(bool firstRender)`: on `firstRender`, guarded by a
   one-shot `loadStarted` flag, call `await LoadBlockedSenders()`. This runs after
   the HTML is delivered and the circuit is interactive.
3. `LoadBlockedSenders` already sets/clears `isLoading` and calls `StateHasChanged`
   via the normal render after the await; no logic change to the load itself.

UI behavior: spinner only (Refresh-button spinner), exactly as today — no extra
loading text.

## Scope

- One file: `Components/Pages/BlockedSenders.razor`. No service/logic change.
- `Services/BlockedSenderService.cs` unchanged.

## Versioning

- Module-scoped bug fix → bump `BlockedSenders` `Version` in
  `Modules/ModuleCatalog.cs` 1.0.0 → 1.0.1. No app-version bump (render-timing
  fix, not shared/app-wide).

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release` then
  `dotnet test ExchangeAdminWeb.slnx` (existing `BlockedSendersTests` stay green;
  no new unit test — this is a render-timing change with no testable logic).
- `dotnet format --verify-no-changes`, `git diff --check HEAD`.
- Manual on dev: clicking the module shows the page + spinner immediately, list
  fills in after the EXO round-trip; Refresh and unblock unchanged.
