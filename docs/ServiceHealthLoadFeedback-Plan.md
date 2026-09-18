# Service Health Load Feedback Plan

Status: Implemented 2026-09-18. Plan approved by the owner the same day; steps 1-5
landed in the commit that carries this status change. The manual acceptance
checklist below is outstanding, and the implementation codereview has not been
dispatched.
Remedy shape ruled by the owner 2026-09-18: Option B, keep prerendering and defer
the data load to `OnAfterRenderAsync`, matching the precedent already set in
`Components/Pages/BlockedSenders.razor`.

Verification run at implementation: `dotnet build -c Release` clean (0 errors, 23
pre-existing warnings), `dotnet test ExchangeAdminWeb.slnx` 2510 passed / 0 failed
/ 3 skipped, `dotnet format --verify-no-changes` clean, `git diff --check HEAD`
clean. Guard proof: mutations A, B and C (steps 1, 2 and 3 reverted independently)
each produced exactly 1 failed / 34 passed against the named tripwire, and all
three restores were byte-identical by SHA256.

Base app version at drafting: 2.21.0 (unchanged - module-scoped change)
ServiceHealth module version: 1.3.1 at drafting, 1.3.2 as shipped
Repository base: `8c1f7d8`

Owner-reported issue 7 of the 2026-09-15 queue, recorded in `.agents/state.md`.

## Reported symptom

Owner's words: "O365 status module is slow to load, inviting repeated clicks or
refreshes; it needs to be obvious when loading."

## What this is NOT

The page already has three spinners and they are all correct:

- the pre-authorization spinner, `Components/Pages/ServiceHealth.razor:15-23`
- the first-load board spinner, `:63-70`, gated on `isLoading && status == null`
- the refresh-button spinner, `:34-45`, with the button disabled while busy

Do not implement this as "add a spinner". Adding a fourth would change nothing.

## Root cause

Diagnosed by reading the code at `8c1f7d8`. Not yet observed in a running browser;
see Assumptions.

1. `Program.cs:374` calls `AddInteractiveServerRenderMode()` and no call site
   anywhere in the app passes `prerender: false`. `ServiceHealth.razor:4` declares
   `@rendermode InteractiveServer`, so the page is **prerendered**.
2. Prerendering emits no HTML until the component reaches quiescence, which means
   `OnInitializedAsync` must run to completion first.
3. `OnInitializedAsync` (`:317-336`) finishes with `await LoadAsync(forceRefresh: false)`,
   so the whole Microsoft Graph round trip happens inside the prerender pass:
   a Delinea secret read (`ServiceHealthService.cs:59`), a Graph token, then
   `FetchServicesAsync` and `FetchIssuesAsync` awaited **one after the other**,
   not concurrently (`ServiceHealthService.cs:115-116`).
4. Therefore the browser receives its first byte of this page only after the Graph
   calls have already returned, and what it receives is the finished board. None of
   the three spinners can ever render on the initial load.

The operator-visible consequence is worse than a blank page. The app uses Blazor
enhanced navigation (`Components/App.razor` subscribes to `blazor:enhancedload`),
so clicking the sidebar link fetches the new page in the background while the
**previous page stays fully rendered and interactive**. There is no visual change
of any kind for the duration of the Graph call. The UI looks idle, so the operator
clicks again. That is exactly the reported behaviour.

`await Task.Yield()` at `LoadAsync:344` appears to have been intended to let a
spinner paint. It cannot: during prerender there is no circuit to paint to, and the
method never calls `StateHasChanged` in any case.

Aggravating factor, not the cause: `CacheTtlSeconds = 600` is an in-process cache
(`ServiceHealthService.cs:23,29`), so the first visit after every IIS app-pool
recycle pays the full cold cost.

## Why the authorization round trip is not part of the problem

`.agents/state.md` flagged this as unverified. Checked and resolved:
`GroupAuthorizationHandler.HandleRequirementAsync`
(`Authorization/GroupAuthorizationHandler.cs:48-65`) is synchronous over in-memory
state - the catalog, the cached enablement service and the Windows token's group
SIDs - and returns `Task.CompletedTask`. It performs no directory or network I/O.
Leaving the authorization check in `OnInitializedAsync` therefore does not
meaningfully delay prerender, which is also why the `BlockedSenders` precedent
leaves it there. No change is proposed to it.

## Goal

Navigating to Service Health must produce visible feedback immediately, before the
Graph call starts, and that feedback must persist for the whole duration of the
call.

## Non-goals

- No redesign, no charts, no gradients. `docs/ServiceHealth-Plan.md` binds the
  appearance and stays binding; this change adds no visual element that is not
  already in the page today.
- Not queue item 2 (`status.cloud.microsoft` as a second source). Untouched.
- No change to the cache TTL, and no change to authorization.
- No sweep of other pages. Other modules may well prerender a slow load the same
  way, but that is unscoped and needs its own go.

## Steps

1. **Split the initial load out of `OnInitializedAsync`.**
   In `Components/Pages/ServiceHealth.razor`, `OnInitializedAsync` keeps the
   authentication and authorization block and the client-IP resolution exactly as
   they are, then sets `isLoading = true` and `authChecked = true` and returns
   without awaiting the Graph call. A comment states why, in the same terms as
   `BlockedSenders.razor:166-169`.

2. **Add `OnAfterRenderAsync` with a one-shot guard.**
   A new `private bool loadStarted` field. `OnAfterRenderAsync(bool firstRender)`
   returns immediately unless `firstRender && !loadStarted && authChecked`, then
   sets `loadStarted = true` and awaits `LoadAsync(forceRefresh: false)`. The
   `authChecked` term matters: on the denied path `OnInitializedAsync` navigates
   away and leaves `authChecked` false, and an unauthorized visitor must not
   trigger a Graph call on the way out.

3. **Render after the deferred load completes.**
   Blazor does not automatically re-render after `OnAfterRenderAsync`. Add
   `StateHasChanged()` to the existing `finally` in `LoadAsync` (`:364-367`),
   alongside `isLoading = false`, with a comment recording that without it the
   spinner stays up and Refresh stays disabled after the data has arrived. This is
   harmless on the Refresh button path, which already re-renders on its own.

4. **Fetch the two Graph collections concurrently.**
   In `Services/ServiceHealthService.cs`, the serial `await` pair at `:115-116`
   becomes one `Task.WhenAll` over `FetchServicesAsync` and `FetchIssuesAsync`.
   Both are independent reads against the same `GraphTokenClient`, both already
   take the client as an argument, and neither touches shared mutable state. This
   is inside the existing `_cacheLock` critical section, so concurrency here does
   not widen any race the class does not already own. Roughly halves the wait the
   spinner has to cover.

5. **Bump the module version.**
   `Modules/ModuleCatalog.cs`, ServiceHealth `Version` 1.3.1 -> 1.3.2. No base app
   version bump: this is module-scoped behaviour, per
   `docs/ProjectConstitution.md` Deployment And Versioning.

## Tests

`ExchangeAdminWeb.Tests/ServiceHealthPageTests.cs` is a source scanner over the
`.razor` text, which is the only harness available - there is no bUnit here, so
nothing below proves what the browser renders. **Every scanner added must strip
comments before matching**, per the lesson recorded in `.agents/state.md` from the
migration button-gating work, where two tripwires matched the words "await" and
"finally" inside explanatory comments.

Assert the negative per occurrence, not the ordering per method - the other lesson
from that work, where ordering assertions passed the bug.

1. `OnInitializedAsync_DoesNotAwaitTheHealthServiceDirectly` - with comments
   stripped, the body of `OnInitializedAsync` contains no call to `LoadAsync` and
   no reference to `HealthService`. This is the tripwire that bites if the load is
   ever moved back and the whole fix is undone.
2. `TheInitialLoadRunsFromOnAfterRenderAsyncBehindAOneShotGuard` - an
   `OnAfterRenderAsync` override exists, it tests `firstRender`, `loadStarted` and
   `authChecked`, and it sets `loadStarted` before awaiting.
3. `LoadAsyncCallsStateHasChangedInItsFinallyBlock` - with comments stripped, the
   `finally` of `LoadAsync` contains `StateHasChanged()`. Without this the deferred
   load leaves a permanent spinner, which is a worse bug than the one being fixed.
4. `TheGraphCollectionsAreFetchedConcurrently` - over
   `Services/ServiceHealthService.cs` with comments stripped, `FetchServicesAsync`
   and `FetchIssuesAsync` are not each preceded by their own `await` on separate
   statements; a `Task.WhenAll` covers both.
5. `TheModuleVersionWasBumped` - ServiceHealth catalog version is 1.3.2.

Guard proof, per `.agents/repo-guidance.md`: mutate each of steps 1, 2 and 3 back
to its pre-fix form independently, confirm the matching test fails and the others
behave as expected, restore, confirm the restored files are byte-identical and the
suite is green. Note the mutation-probe restore trap: `Copy-Item` preserves the old
timestamp and MSBuild will skip the rebuild, so touch each restored file.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`

## Manual acceptance checklist

Automation cannot reach the rendered page, so these are the only evidence the
operator actually sees the fix. All require a dev deploy.

1. With a cold cache (immediately after an app-pool recycle), click Service Health
   in the sidebar. A spinner must appear **immediately**, before the board does.
2. The spinner must remain visible for the whole load, and the board must replace
   it when the data arrives. It must not stay up after the board is populated.
3. Click Refresh. The button must disable and show its inline spinner, then
   re-enable when the refresh completes.
4. Within the 10-minute cache window, navigate away and back. The page must render
   from cache without a long wait, and must not be left showing a stuck spinner.
5. As a user without Service Health access, navigate to `/service-health`. The
   redirect to `access-denied` must still happen, and no Service Health audit entry
   may be written for that visit.
6. Confirm the board looks exactly as it did before this change.

## Assumptions

- The root cause is established by reading the code, not by observing a running
  browser. The prerender-blocks-first-byte behaviour is standard Blazor and the
  repo already documents it at `BlockedSenders.razor:166-169`, but manual check 1
  is what confirms it for this page.
- The magnitude of the remaining wait after step 4 is unmeasured. If a single Graph
  call is itself slow enough that operators still complain with a spinner on
  screen, that is a separate performance question, not this plan.

## Out of scope, noted for the record

Because prerender and the interactive circuit each run `OnInitializedAsync`, and
`LoadAsync` writes an audit entry, **every Service Health page view currently
writes two `ServiceHealthView` audit entries**. Step 1 removes the load from
`OnInitializedAsync`, and `OnAfterRenderAsync` does not run during prerender, so
this change incidentally reduces that to one. This is a side effect worth knowing
about, not a claimed deliverable, and the audit-duplication question for other
pages is not addressed here.
