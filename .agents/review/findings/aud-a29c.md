# aud-a29c: the circuit-open revalidation runs in the background, so a stale-ticket operator can act before it completes

**Severity**: HIGH - an operator removed from a group (or disabled), whose Kerberos ticket still
carries the old groups, gets an action window on every reload: whatever they click before the
directory answers runs as if nothing had changed.
**Status**: Fixed on branch; awaiting merge
**Branch**: `aud-a29cde` (worktree `agent-ad94a625885da3a95`), base `9fab2e4`
**Commit**: `<pending>`
**Reviewer**: codex / gpt-5.5-dzs / xhigh over 1633ad0..0d30442, 2026-10-21
**Parent**: [aud-a29b](aud-a29b.md)

## Evidence

`Authorization/CircuitRevocationGate.cs` at `9fab2e4`, the circuit-open hook:

```csharp
51    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
52    {
53        if (_authenticationState is MembershipRevalidatingAuthenticationStateProvider provider)
54            OpenCheck = Task.Run(() => provider.CheckAtCircuitOpenAsync(), CancellationToken.None);
55
56        return base.OnCircuitOpenedAsync(circuit, cancellationToken);
57    }
```

and the inbound gate:

```csharp
63        return context =>
64        {
65            if (!_revocation.IsRevoked)
66                return next(context);
```

The check is started and not awaited (the class remarks say so on purpose: "the circuit's first
render does not wait for the directory"), and the gate only knows two states, revoked and not
revoked. Until the directory answers - up to the 60 s lookup timeout - `IsRevoked` is false, so
every inbound activity passes: clicks, JS interop, navigation, and the first render itself (see
the trace below - in this framework version the circuit's root components are added through the
same inbound pipeline). When the check does revoke, the action has already run. A reload starts a
new circuit with a new, unrevoked `CircuitRevocation`, so the window reopens every time.

## The circuit pipeline, traced

Traced against the ASP.NET Core sources at tag `v10.0.12` (the newest runtime installed here,
`Microsoft.AspNetCore.App 10.0.12`; the lines quoted are identical at `v10.0.0`). Files, with
md5 of the copies read: `src/Components/Server/src/Circuits/CircuitHost.cs`
(`d5492b9b362bc791d30e877cd5f6c78e`), `Circuits/CircuitFactory.cs`
(`771eb775a0baaf918860d1f3d53f6759`), `ComponentHub.cs` (`1113366433cd40212c6d3785e47cf17a`).

This app is a Blazor Web App: `MapRazorComponents<App>().AddInteractiveServerRenderMode()`,
static router, pages are `@rendermode InteractiveServer` islands with prerendering on. In order:

1. **The page request (HTTP, no circuit).** Negotiate authenticates, endpoint authorization runs
   the page's policy against the request principal, and the page is prerendered: its
   `OnInitialized{Async}` runs and its HTML is sent. Nothing a page does here comes from the
   browser - no event handler exists outside a circuit - and nothing in this finding's fix runs
   here. (This is the recorded aud-a29b gap "A stale Kerberos ticket survives a reload"; it is
   unchanged and listed again under Known gaps.)
2. **`ComponentHub.StartCircuit`** (`ComponentHub.cs:87`). `CircuitFactory.CreateCircuitHostAsync`
   creates the circuit scope and resolves NO circuit handlers for a Blazor Web App
   (`CircuitFactory.cs:101`: `var circuitHandlers = components.Count == 0 ? [] : ...`), builds the
   host, whose inbound dispatcher is therefore empty (`CircuitHost.cs:84`), and hands the scope its
   principal (`CircuitFactory.cs:122`: `circuitHost.SetCircuitUser(user);` - our provider's
   `IHostEnvironmentAuthenticationStateProvider.SetAuthenticationState`). Then
   `_ = circuitHost.InitializeAsync(...)` (`ComponentHub.cs:145`), fire-and-forget; with no
   descriptors it skips the handlers (`CircuitHost.cs:139`: `if (Descriptors.Count > 0)`).
3. **The first `UpdateRootComponents`** (`ComponentHub.cs:218`:
   `_ = circuitHost.UpdateRootComponents(...)`). Not itself an inbound activity: it goes straight
   to `Renderer.Dispatcher.InvokeAsync` (`CircuitHost.cs:769`). On the first update:

   ```csharp
   823   _circuitHandlers = [.. _scope.ServiceProvider.GetServices<CircuitHandler>().OrderBy(h => h.Order)];
   824   _dispatchInboundActivity = BuildInboundActivityDispatcher(_circuitHandlers, Circuit);
   825   await OnCircuitOpenedAsync(cancellation);
   826   await OnConnectionUpAsync(cancellation);
   ...
   838   var operationsTask = PerformRootComponentOperations(operations, shouldWaitForQuiescence, postRemovalTask);
   ```

   So: handlers are constructed (this is when `CircuitRevocationGate` exists), the inbound
   pipeline is rebuilt with them, **then** every handler's `OnCircuitOpenedAsync` runs in `Order`
   and is awaited (`ClientInfoCircuitHandler` and `UsageSessionCircuitHandler` at 0, the gate at 1),
   **then** `OnConnectionUpAsync`, **then** the root components are added.
4. **The first render is an inbound activity.** `PerformRootComponentOperations` adds the root
   components inside the pipeline:

   ```csharp
   891   await HandleInboundActivityAsync(() =>
   ...                 webRootComponentManager.AddRootComponentAsync(...)  // -> SetParametersAsync, OnInitialized{Async}, render
   905                 pendingTasks?[i] = task;
   ...
   926   await Task.WhenAll(pendingTasks);
   ```

   This is the "first render" hole: the page island's circuit-side `OnInitialized{Async}` and
   first render run as an inbound activity, and at `9fab2e4` the gate passes it because the
   open check started one line earlier has not answered.
5. **Everything after that**, also through `HandleInboundActivityAsync` and so through the gate:
   events and JS interop (`BeginInvokeDotNetFromJS`, `CircuitHost.cs:407`; browser events are
   dispatched this way in .NET 8+), `EndInvokeJSFromDotNet`, `OnLocationChanged`/`Changing`,
   `ReceiveByteArray`, `ReceiveJSDataChunk`, render acknowledgements (`OnRenderCompletedAsync`,
   `CircuitHost.cs:386` - which is what runs `OnAfterRender{Async}`), and later root-component
   updates. Every hub entry point fires and forgets (`_ = circuitHost...`), so an activity can
   reach the gate on a hub thread while step 3 is still running on the dispatcher - between line
   824 (pipeline rebuilt, gate in it) and the gate's own `OnCircuitOpenedAsync`.
6. **Reconnect** (`ComponentHub.ConnectCircuit`, `ComponentHub.cs:240`:
   `circuitHost.SetCircuitUser(Context.User)`) is not a new circuit and runs no open check;
   **resume** (`ComponentHub.ResumeCircuit`, `CreateCircuitHostAsync([], ...)`) is a new circuit and
   goes through steps 2-5 like any other.
7. **A dropped first batch faults the circuit.** If the gate drops the activity at step 4,
   `pendingTasks` stays all null and `Task.WhenAll(pendingTasks)` (line 926) throws; the catch
   at `CircuitHost.cs:848-851` logs it, raises `UnhandledException` (the registry then terminates
   the circuit, `CircuitRegistry.cs:407-414`) and sends `JS.Error`, which shows the app's
   `#blazor-error-ui`.

## Plan (approved by the owner's EVERYTHING go)

Owner, 2026-10-21 (`.agents/decisions.md`, "Every open review follow-up is approved for
implementation"): "ALL the bugs includes ALL the bugs ... EVERYTHING." This admitted finding is
covered. Plan, as briefed by the coordinator:

1. **A new circuit is pending revalidation until its open check reaches a verdict.** The gate
   holds the verdict as a task: started at whichever comes first - its `OnCircuitOpenedAsync`, or
   the first inbound activity to reach it (step 5's race) - exactly once.
2. **The inbound gate awaits the verdict.** Revoked: drop (as before). Pending: wait for the
   verdict, then pass if it confirmed access and nothing has revoked the circuit since, else drop.
   Valid and settled: pass with no wait. The wait is bounded: the open check itself ends within
   the existing 60 s lookup timeout (`LookupFailed` on expiry, as before), and as a backstop a hold
   that outlives twice that revokes the circuit with a new reason, `PendingCheckTimedOut`, audited
   through the existing `SessionRevalidation` line (no group names).
3. **The first render waits too.** `OnCircuitOpenedAsync` awaits the same verdict, so the
   framework's root-component add (step 3, line 838) does not start until it is in; and on a
   verdict that does not confirm access it throws, so the circuit is never initialized. The
   inbound hold (step 4) covers the same add independently.
4. **The off-switch keeps its meaning.** With `Authentication:RevalidationEnabled = false`, or
   without the revalidating provider, nothing is held, nothing is started, nothing is revoked.
5. **`CheckAtCircuitOpenAsync` reports its verdict** (`Task<bool>`): true when access was
   confirmed or there was nothing to confirm, false when it revoked or the circuit closed first.
6. Tests: an activity before the verdict is held, then dropped when the check revokes and passed
   when it validates; one arriving before the circuit opens starts the check and is held; a lookup
   timeout revokes; the backstop revokes a check that never finishes; the off-switch holds nothing;
   every existing test still passes. Mutation at each call site, md5 before and after.
7. No `.razor` page edits. No version bump here (supervisor's at merge, as for aud-a29b).

## Approach

Implemented as the Plan, no change of scope.

- **The gate owns the verdict.** `CircuitRevocationGate._verdict` is the open check's task, null
  until started; `StartOpenCheck` starts it once under a lock, from `OnCircuitOpenedAsync` or from
  the first inbound activity, whichever is first. Off-switch or no revalidating provider: the
  verdict is a completed `true` and nothing is started.
- **Inbound:** revoked -> drop. Verdict completed and `true` -> `next` with no wait (the steady
  state costs what it did). Otherwise `HoldAsync`: await the verdict, then run `next` only if it
  is `true` and nothing has revoked the circuit since; anything else is dropped like a revoked
  circuit's activity.
- **`OnCircuitOpenedAsync`** awaits the same verdict and throws `InvalidOperationException` when it
  is not `true`. Per trace step 3 the framework awaits this before line 838, so the root components
  are never added; per step 7 the circuit is then terminated and the browser shows
  `#blazor-error-ui`.
- **The bound.** `AwaitVerdictAsync` waits `HoldTimeout` = 2 x `LookupTimeout` (120 s in
  production). The check enforces `LookupTimeout` itself (`CircuitMembershipRevalidator`, unchanged),
  so the backstop is reached only by a check stuck outside its lookup; it calls the provider's new
  `RevokeUnconfirmedOpenCheck`, which revokes with `PendingCheckTimedOut`, writes the one audit line
  through the existing `Revoke` (action `SessionRevalidation`, trigger `CircuitOpened`, no group
  names; the account only if the state is already there, else `Unknown`) and signs the circuit out.
- **`CheckAtCircuitOpenAsync`** now returns its verdict: `true` when access was confirmed or the
  circuit is unauthenticated (nothing to confirm - unchanged behaviour), `false` when it revoked
  or when the circuit was disposed first (no audit, and nothing held runs).
- `CircuitRevocationReason.PendingCheckTimedOut` and its plain message (no group named).
- README "Session Revalidation": what a connecting tab does, and what it shows when the check on
  connect ends the session.

## Files changed

- `Authorization/CircuitRevocationGate.cs` - the verdict, the hold, the awaited open hook, the
  backstop; options injected (the container already holds them).
- `Authorization/MembershipRevalidatingAuthenticationStateProvider.cs` - `CheckAtCircuitOpenAsync`
  returns `Task<bool>`; `RevokeUnconfirmedOpenCheck`.
- `Authorization/CircuitRevocation.cs` - `PendingCheckTimedOut` and its message.
- `ExchangeAdminWeb.Tests/CircuitRevalidationTests.cs` - the tests below; harness helpers
  `Gate`, `DirectoryAnswersLater`, `SignIn`, `Membership`.
- `README.md` - one bullet in "Session Revalidation".
- `.agents/review/findings/aud-a29b.md` - known gap "A stale Kerberos ticket survives a reload"
  and veto item 2, amended.

Not touched: `Program.cs` (the gate's registration is unchanged; DI supplies the options), any
page under `Components/Pages`, `Components/Layout/MainLayout.razor`, the csproj,
`Modules/ModuleCatalog.cs`.

## Guard proof

Nothing here reached a directory: the directory is an NSubstitute fake (one that answers only
when the test says so, for the holds), the stores are the real services over a throwaway SQLite
file.

| Test (`CircuitRevalidationTests`) | Pins |
| --- | --- |
| `ActivityBeforeTheOpenCheckCompletes_IsHeldThenPassedWhenAccessIsConfirmed` | **the finding**: while the directory has not answered, neither `OnCircuitOpenedAsync` (so the root-component add) nor an activity completes; both proceed on a confirming answer; nothing revoked or audited |
| `ActivityBeforeTheOpenCheckCompletes_IsDroppedWhenTheCheckRevokes` | **the finding**: the answer revokes, the held activity never runs, `OnCircuitOpenedAsync` throws (the circuit is not initialized); one `CircuitOpened` audit line |
| `ActivityThatReachesTheGateBeforeTheCircuitOpens_StartsTheCheckAndIsHeld` | trace step 5: an activity that beats `OnCircuitOpenedAsync` starts the check and waits; the open hook joins the same check - one directory read |
| `AnOpenCheckThatTimesOut_RevokesAndDropsTheHeldActivity` | a timeout revokes: the lookup timeout ends the check (`LookupFailed`, "did not answer"), the held activity is dropped, the open hook throws |
| `AnOpenCheckThatNeverFinishes_IsRevokedWhenTheHoldRunsOut` | the backstop: a check that never answers is revoked after `HoldTimeout` (`PendingCheckTimedOut`, one audit line, trigger `CircuitOpened`, target `Unknown`), the circuit is signed out, the open hook throws |
| `ACircuitThatClosesBeforeItsOpenCheckCompletes_RunsNothingItHeld` | a circuit disposed while pending runs nothing it held, and writes no audit line |
| `TheGate_HoldsNothingWhenRevalidationIsSwitchedOff` | off-switch: no check, no hold, no revocation, no audit - even with a state that would hold a check forever |
| `TheTracedCircuitPipelineIsTheOneRunning` | tripwire: `Microsoft.AspNetCore.Components.Server` is still major version 10, the version the trace above was made against |
| `TheGate_StartsTheCircuitOpenCheck` (changed) | the open hook still starts the check; now it also throws on a revoking verdict and `OpenCheck` is `false` |
| `TheGate_PassesActivityThroughWhileTheCircuitIsValid` (changed) | after a confirming verdict, activity passes on the fast path (twice) |
| `EveryReasonHasItsOwnPlainMessage` (unchanged) | covers the new reason |

Every pre-existing test in `CircuitRevalidationTests` and `OperatorMembershipDirectoryTests` still
passes (the five gate constructions take the options; nothing else in them changed).

Mutations by `a29cde-mutate.ps1` (session scratchpad): exact-text replacement asserted to match
once, md5 taken, `dotnet build ExchangeAdminWeb.Tests.csproj -c Release`, the
`CircuitRevalidationTests` class run, then the original bytes restored, the file touched and the
md5 re-taken. Every restore was md5-equal to the original; the restored tree then rebuilt and both
classes passed 99/99. md5s are of the pre-comment files (comments were added after this round):
gate `06AD7927C4C42260399477B341739D73`, provider `22F9ACD39F38C4BD07D842B1B8C8C4F5`, revocation
`7949BB2B67A8E79E1601D61D366CA78A`.

| # | Mutation at the call site (file) | md5 mutated | Named tests that failed |
| --- | --- | --- | --- |
| C1 | `return HoldAsync(context, next);` -> `return next(context);` - a pending circuit's activity passes (gate) | `F7AB974550520D7599DA66EAC11DDD54` | `..._IsHeldThenPassedWhenAccessIsConfirmed`, `..._IsDroppedWhenTheCheckRevokes`, `..._StartsTheCheckAndIsHeld`, `AnOpenCheckThatTimesOut_...`, `AnOpenCheckThatNeverFinishes_...`, `ACircuitThatCloses...` (6) |
| C2 | the open hook starts the check and does not wait - the behaviour at `9fab2e4` (gate) | `B35D901D7EB765474C4919D48A1E7214` | `..._IsHeldThenPassedWhenAccessIsConfirmed`, `..._IsDroppedWhenTheCheckRevokes`, `..._StartsTheCheckAndIsHeld`, `AnOpenCheckThatTimesOut_...`, `AnOpenCheckThatNeverFinishes_...`, `TheGate_StartsTheCircuitOpenCheck` (6) |
| C3 | the hold ignores the verdict; only a revocation stops it (gate) | `109CA734B91F63029961958E0EA4EF74` | `ACircuitThatClosesBeforeItsOpenCheckCompletes_RunsNothingItHeld` |
| C4 | an activity that arrives before the check has started passes unchecked (gate) | `751319D12895AFA24C52437F4D251E18` | `..._StartsTheCheckAndIsHeld`, `AnOpenCheckThatTimesOut_...`, `AnOpenCheckThatNeverFinishes_...`, `ACircuitThatCloses...` (4) |
| C5 | the backstop's revocation call removed (gate) | `EEE2C67290A16B264ECEED672587B831` | `AnOpenCheckThatNeverFinishes_IsRevokedWhenTheHoldRunsOut` |
| C6 | `WaitAsync(HoldTimeout, cancellationToken)` -> `WaitAsync(cancellationToken)` (gate) | `EFA5816EA2F7DAE41E8E4D61111059AF` | `AnOpenCheckThatNeverFinishes_IsRevokedWhenTheHoldRunsOut` (its 15 s wait expired) |
| C7 | `_options.Enabled &&` removed - the gate ignores the off-switch (gate) | `A8BC2A41CD9D668C5876D6913C47471B` | `TheGate_HoldsNothingWhenRevalidationIsSwitchedOff` |
| C8 | the open hook's throw replaced by `return;` (gate) | `56F6379068EE04AC34934ECB93A17FD1` | `AnOpenCheckThatTimesOut_...`, `AnOpenCheckThatNeverFinishes_...`, `TheGate_StartsTheCircuitOpenCheck`, `..._IsDroppedWhenTheCheckRevokes` (4) |
| C9 | a check stopped by disposal reports `true` (provider) | `219BC92A77010E0D8B9F1B8C5FE9C81D` | `ACircuitThatClosesBeforeItsOpenCheckCompletes_RunsNothingItHeld` |
| C10 | a check that revoked reports `true` (provider) | `AB7E72565E6D231E6BD4DE3CE1A42191` | `TheGate_StartsTheCircuitOpenCheck`, `..._IsDroppedWhenTheCheckRevokes` |
| C11 | `PendingCheckTimedOut` loses its own message (revocation) | `A256BB78C0D65AC445558A83CD39EECC` | `EveryReasonHasItsOwnPlainMessage` |

C3 and C9 are killed only by the disposal test: on every other path a verdict that is not `true`
comes with a revocation, which the hold also checks, so the two conditions guard each other.

## Gates

On the final (commented) tree:

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; no warning in any file this change
  touches.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: **4621 passed,
  3 skipped (the known AD skips), 0 failed, 4624 total**; the hang collector reported "All tests
  finished running" - nothing hung, the C6 theory included.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD` (after staging) and `tools/Test-AsciiOnly.ps1`: see the commit.

Not run: anything against a live directory (by instruction), and no browser check of a connecting
tab - the repo has no component-rendering harness; the order of events is pinned by the trace
and the gate tests.

## Known gaps

- **The server-rendered page is not held.** Trace step 1: a page request under a stale Kerberos
  ticket is still authorized and prerendered with that token; its initialization runs and its
  output is shown. It cannot run an event handler (there is no circuit), and the circuit it opens
  now does nothing until the check answers. Closing it would need a directory read in the request
  pipeline, per page load and per enhanced navigation - a different cost and availability profile
  (a directory outage would refuse every page), so it is the owner's call, not taken here.
- **Held activities are released together.** When nothing holds, the framework posts a
  connection's activities to the dispatcher in arrival order; activities held while a circuit is
  pending are released when the verdict arrives, and their order among themselves is that of their
  continuations. Only activities that arrive inside the pending window are affected, and the
  root-component add is not among them (the open hook has already waited when it arrives).
- **A legitimate operator's page is inert for the directory's latency** at every page load that
  opens a circuit - normally a fraction of a second, at most the 60 s lookup timeout (after which
  the session ends, fail closed).
- **A reconnect is not re-checked when it happens** (unchanged from aud-a29b): it is the same
  circuit, checked again at the next interval. A resumed circuit (`ResumeCircuit`) is a new one
  and is held like any other.
- **The trace is version-bound.** `TheTracedCircuitPipelineIsTheOneRunning` fails on the next major
  framework version, so the order above is re-traced before the gate is trusted on it.

## Choices the owner can veto

1. **Hold, not refuse,** while a circuit is pending: an activity waits for the verdict instead of
   being dropped, so a legitimate operator who clicks during the check loses nothing.
2. **The backstop is twice the lookup timeout** (120 s) and revokes with a new reason,
   `PendingCheckTimedOut`. It is reached only by a check stuck outside its own lookup timeout.
3. **A circuit the open check does not confirm is terminated, not left showing the notice.** The
   notice is one of the circuit's root components, which this fix exists to keep from being added;
   the operator sees the app's error banner and its Reload link, and the audit line and the log
   carry the reason. The alternative is a server-rendered "session ended" page reached by a forced
   navigation - a new page, not built here.
4. **The off-switch also switches the hold off** - no check, so nothing to wait for.
5. **A version tripwire** on the circuit pipeline the safety argument relies on.

## Closeout

Fixed on branch `aud-a29cde` under the owner's 2026-10-21 go. Pending: codex review of the
commit, merge to `master` by the supervisor (with the base version bump and the index row), and
the live dev check owed by aud-a29b, extended: on a page load, the tab does not respond until
the check has answered; with the operator removed from the group, the reload ends in the error
banner and one `SessionRevalidation` line with trigger `CircuitOpened`.
