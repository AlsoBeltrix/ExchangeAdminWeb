# aud-a29g: prerendered pages still render and run OnInitialized under a stale ticket

**Severity**: MEDIUM, design - the aud-a29b/c/d/e revalidation closes the circuit when an operator's authorization changes, and no handler can run under a stale ticket. But the prerender pass of a page request still renders the page and runs `OnInitialized` / `OnInitializedAsync` against the request's cached principal before the circuit's revalidation runs, so a revoked operator who requests a page still sees it rendered (reads in `OnInitialized` included) once. Closing it costs a directory read per page request, which is why the aud-a29b record left it for an owner cost decision; the 2026-10-23 evening ruling resolves everything open, so it is dispatched.
**Status**: Closed - merged to master `a5d12cc8` (fix `2678735e`); codex auth-batch-1 raised aud-a29i and aud-a29j, both landed at `d58b5d7c` and reviewed clean (auth-batch-2). Complete once fix/a29g is deleted. Owner calls from the hand-back (one-interval window; one audit line per refused request with a standalone 403; cache-free circuit checks) are held for the owner
**Branch**: `fix/a29g` (worktree `ExchangeAdminWeb-wt/a29g`), base `eb017102`
**Commit**: the `fix(aud-a29g)` commit on `fix/a29g` that carries this record (code and record together)
**Reviewer**: implementer (recorded by the supervisor from the aud-a29b record's hand-back)
**Parent**: [aud-a29b](aud-a29b.md), [aud-a29c](aud-a29c.md)

## Evidence

At `4498da49`: `.agents/review/findings/aud-a29b.md` (the mechanism and the options the owner was given); `Program.cs` component registration and the revalidating provider added by aud-a29b (`cb3be91`, `702f66f`, `0d30442`); `Components/App.razor` render mode. The implementing agent locates the exact prerender path and quotes it before writing the approach.

Trigger: an operator is removed from an allowed group, then requests a page URL. Predicted failure: the page renders its initial state (and any `OnInitialized` read) once before the circuit revalidation drops it.

### The prerender path, quoted at `eb017102`

The request pipeline, `Program.cs`:

```csharp
510    app.UseAuthentication();
511    app.UseAuthorization();
512    app.UseMiddleware<ClientInfoMiddleware>();
...
516    app.UseMiddleware<PageLoadMiddleware>();
517    app.UseAntiforgery();
518
519    app.MapRazorComponents<App>()
520        .AddInteractiveServerRenderMode()
521        .RequireAuthorization();
```

The host page renders the router with no render mode (`Components/App.razor:88`, `<Routes />`), so the
router, `AuthorizeRouteView` (`Components/Routes.razor:3-6`) and `MainLayout` render statically from the
request's principal, and every page is an island that declares its own mode, prerendering on - for
example `Components/Pages/RiskyUsers.razor`:

```razor
1    @page "/risky-users"
3    @attribute [Authorize(Policy = "RiskyUsers")]
4    @rendermode InteractiveServer
...
563      protected override async Task OnInitializedAsync()
564      {
565          var authState = await AuthStateProvider.GetAuthenticationStateAsync();
...
569          var authResult = await AuthorizationService.AuthorizeAsync(user, "RiskyUsers");
```

So on a page request (the aud-a29c record's trace, step 1): Negotiate authenticates (510), the
endpoint's policy runs against the request principal (511), and the components endpoint (519)
prerenders the page - `OnInitializedAsync` runs and its HTML is sent - before any circuit exists.
Nothing between 510 and 519 consults the directory, and under Kerberos the request principal's group
SIDs are the ticket's, so a removed operator passes 511 and the page renders. An enhanced navigation is
the same HTTP request (`Middleware/PageLoadMiddleware.cs:69-70`, "Enhanced navigation sends the
same"), so it prerenders too; only its circuit side runs under the circuit's check.

### The revalidation that exists, quoted at `eb017102`

`Authorization/CircuitMembershipRevalidator.cs`, the one verdict:

```csharp
89    public async Task<MembershipRevalidationResult> RevalidateAsync(
90        ClaimsPrincipal user, IReadOnlySet<AuthorizationGrant> captured, CancellationToken cancellationToken)
...
113            return await check.WaitAsync(_options.LookupTimeout, cancellationToken).ConfigureAwait(false);
...
193        if (held.Count == 0)
194            return MembershipRevalidationResult.Valid("The principal holds no group an authorization gate consults.");
195
196        var membership = await _directory
197            .GetMembershipAsync(userSid!, SectionAccessDirectoryReading.NetBiosFromNTAccount(user.Identity?.Name), cancellationToken)
198            .ConfigureAwait(false);
```

Its only caller is the circuit's provider, behind the off-switch
(`Authorization/MembershipRevalidatingAuthenticationStateProvider.cs`):

```csharp
117        if (!_options.Enabled)
118            return true;
...
126            result = await _revalidator.RevalidateAsync(user, CapturedGrants, cancellationToken).ConfigureAwait(false);
```

reached from the gate's open check (`Authorization/CircuitRevocationGate.cs:181-183`) and from the
framework's interval loop. There is no verdict cache: every check that holds a consulted group reads
the directory. The only cache on the read path is the domain-route cache in the singleton directory
(`Services/OperatorMembershipDirectory.cs:50`, `_domainServers`, NetBIOS name to DNS root, forgotten
after a failed read), which anything reading through that singleton shares.

## Approach

**Chosen: candidate (a) - every page request is checked before it renders, in middleware, by the
aud-a29b revalidator, with the directory's answer reused for one revalidation interval.**

- **Where.** New `Middleware/PageRequestRevalidationMiddleware.cs`, registered in `Program.cs`
  directly after `PageLoadMiddleware`: after authentication and the endpoint's own authorization (a
  request its policy denies costs no directory read), inside the page-load scope (the status frame
  reports the wait - server-side, as the page-load rule in `.agents/state.md` requires), before
  antiforgery and the components endpoint.
- **What it checks.** A request whose endpoint renders a Razor component page (endpoint metadata
  `ComponentTypeMetadata` - the page endpoints `MapRazorComponents` creates, `Error` and
  `access-denied` included) under an authenticated principal. Everything else - the `_blazor` hub,
  `_framework`, static files, an unauthenticated request - passes untouched: the circuit keeps its own
  gate, and checking the hub here would double every connection's reads and audit lines.
- **Same verdict code.** `CircuitMembershipRevalidator.RevalidatePageRequestAsync(user, ct)` is the
  existing check with nothing captured (a request has no circuit): the same compared set, the same
  fail-closed shapes, the same 60 s bound. No rule is implemented twice.
- **Refused.** `next` is not called, so nothing of the endpoint runs - no layout, no router, no page,
  no `OnInitialized`. The response is 403, `Cache-Control: no-store`, a short standalone page: a
  title, the reason in plain words with no group named, the existing reload advice, and a full-reload
  link (`data-enhance-nav="false"`). One warning in the log and one audit line in the circuit's shape
  (`SessionRevalidation`, `Authentication`, result Failed, `reason`, `removedGroupSids`), trigger
  `PageRequest`; the line's writer is shared with the provider rather than copied. An audit failure is
  logged and the refusal stands.
- **Off-switch.** As for the gate: with `Authentication:RevalidationEnabled = false` the middleware
  calls `next` before reading anything - no settings read, no directory read, no refusal, no audit.
- **Fail closed (Known Failure Class 3).** Every verdict that is not valid refuses, `LookupFailed`
  included (directory unreachable or slow past 60 s, a grant store that cannot be read, no account
  SID); an exception the check did not anticipate is a refusal too. In neutral words: "Your access
  could not be confirmed against Active Directory just now, so this page was not shown." A request
  the browser abandons while waiting ends with nothing rendered.
- **The interval cache.** aud-a29b keeps no verdict cache (above), so the one the record's candidate
  needs is added to the revalidator - the singleton both paths already share - not beside it. It holds
  the directory's ANSWER per account SID (the `tokenGroups` set and the enabled flag) with the time the
  read started, never a verdict: each page request recomputes its verdict from the current access
  settings and its own token, so a grant added or removed since is seen at once. Rules:
  1. Every successful read records its answer - the circuit-open check's, the interval loop's and a
     page request's alike - keeping the later-started of two.
  2. Only the page-request check consults it, and only an answer whose read started less than
     `Authentication:RevalidationInterval` ago (default 5 min), on a monotonic clock.
  3. A recorded answer can confirm a page request but never refuse one: an answer that would refuse
     is read again and the refusal decided on the fresh read, so restored access works at the next
     request rather than an interval later.
  4. A read that fails (throws, answers nothing, is cancelled or abandoned) forgets the account's
     answer unless a later-started read recorded one meanwhile - the domain-route cache's rule - so a
     failing directory does not go on confirming pages from an old answer.
  5. The circuit-open check and the interval loop never consult it; they read the directory every
     time, exactly as now, so aud-a29b's interval and aud-a29c's hold are unchanged.

**Cost (reasoned, not measured - by instruction nothing here reaches a directory).** A read is one
fresh PowerShell runspace with the ActiveDirectory module and one `Get-ADUser -Properties tokenGroups`
(`Services/PowerShellDirectoryCommands.cs:167-171`), at most 4 at once for the whole app
(`OperatorMembershipDirectory.MaxConcurrentLookups`), shared with the circuit checks.

| Per | Directory reads now | With this change |
| --- | --- | --- |
| Full page load, the account read within the interval (by an open tab's checks or an earlier request) | 1 (circuit open) | 1 - the request is confirmed from the recorded answer |
| Full page load, nothing read for the account within the interval (first load, after an idle interval or a restart) | 1 | 2 - one before the page renders (the response waits for it), then the circuit-open one |
| Enhanced navigation, read within the interval | 0 | 0 |
| Enhanced navigation, nothing read within the interval | 0 | 1, before the page renders |
| Operator holding no group any gate consults | 0 | 0 |
| Refused request | - | 1 per refused request (a refusal is never decided from the cache) |
| Open tab, steady state | 1 per interval | 1 per interval - the same read keeps that account's page requests confirmed |

Each checked request also re-reads the two grant tables (SQLite) and translates any name-configured
static group (LSA) - the work every circuit check already does; assumed to be milliseconds, not
measured. During a directory
outage a page request with no recorded answer younger than the interval waits for the lookup (at most
the 60 s timeout) and is refused: fail closed, the page shown as loading meanwhile. The remedy is the
circuit's: the off-switch.

**The window that remains, stated.** A page request can be confirmed by an answer up to one interval
old - the window an open tab already has, and the one the circuit side of an enhanced navigation
already has. So an operator removed within one interval of their account's last read can still have
one page prerendered; its circuit then fails its fresh open check (aud-a29c), whose read is recorded,
so every later request is refused. With nothing read for the account in the last interval, the first
page request after the removal is refused.

**Rejected: candidate (b), prerender off.** It costs no directory read, but: every page declares
`@rendermode InteractiveServer` itself and `Components/Pages/*.razor` may not be edited
(ClickGateRegistry keys by line); the only host-level switch is global interactivity (`<Routes
@rendermode=...>`), which turns the static router into in-circuit routing - a navigation would stop
being an HTTP request, so `PageLoadMiddleware` would no longer see page loads, and the layout's
islands would change shape; every page would be blank until its circuit connects and the open check
answers; and the static layout (`NavMenu`, built from the request principal) would still render under
the stale ticket.

**Rejected variant of (a): a fresh read on every page request.** It closes the window entirely, at one
read per page request and per enhanced navigation (two per full load), every one on the response path
and queued behind the same 4 slots as the circuit checks. Listed for the owner below.

Mutations planned: the per-request check removed (the middleware always calls `next`), and one per
rule and branch above.

## Files changed (declared before code)

- `Middleware/PageRequestRevalidationMiddleware.cs` (new) - the check, the refusal page and its
  messages.
- `Authorization/CircuitMembershipRevalidator.cs` - `RevalidatePageRequestAsync`; the recorded
  answers and rules 1-5; a test clock (`Clock`, an init-only property - the seam pattern of
  `OperatorMembershipDirectory.WorkerScheduler`).
- `Authorization/MembershipRevalidatingAuthenticationStateProvider.cs` - the audit line's writer,
  shared with the middleware; trigger `PageRequest`.
- `Program.cs` - the registration.
- `README.md` ("Session Revalidation"), `appsettings.json.sample` (the note).
- `ExchangeAdminWeb.Tests/PageRequestRevalidationTests.cs` (new).
- `.agents/review/findings/aud-a29b.md`, `aud-a29c.md` - the known gap each left for this record,
  amended.

Not touched: any page under `Components/Pages`, `App.razor`, `Routes.razor`, `MainLayout`, the gate,
`PageLoadMiddleware`, `PageLoadTracker`, the csproj (app-wide: the supervisor bumps the base version
once for the stream).

## Guard proof

Implemented as the Approach, with one change of detail: the test clock is an init-only `Clock`
property rather than an optional constructor parameter (the local seam pattern), so DI never sees
it. Nothing below reached a directory: the directory is an NSubstitute fake, the access settings are
the real services over a throwaway SQLite file, and time is a manual monotonic clock.

The end-to-end tests start a real ASP.NET Core host on `127.0.0.1:0` with the production order
(authentication, authorization, the new middleware, antiforgery, `MapRazorComponents`) over a probe
app: a router root and one routed page carrying the InteractiveServer render mode with prerendering
on - what `@rendermode InteractiveServer` compiles to, so what every app page is - whose
`OnInitialized` increments a counter. The accepted test proves the probe really takes the prerender
path (`<!--Blazor:` marker in the body, `OnInitialized` ran once during the GET), so the refused
tests' "no body, counter 0" is observed, not inferred.

| Test (`PageRequestRevalidationTests`, 16 tests, 18 cases) | Pins |
| --- | --- |
| `ARefusedPrincipal_GetsNoPageBodyAndThePageNeverInitializes` (MembershipChanged, AccountDisabled) | **the finding**: a stale-token page request the directory refutes gets 403, no page body, no prerender marker, `OnInitialized` never ran; the reason's own words; `no-store`; one audit line with every field (trigger `PageRequest`, the request's address, removed SIDs); one warning |
| `AnAcceptedPrincipal_GetsThePagePrerendered` | a confirmed principal gets the page, prerendered as an island, initialized once; one read; no audit |
| `AnAccessCheckThatCannotRun_RefusesThePage` (the directory, the access settings) | fail closed: an unreachable directory or an unreadable grant store refuses, in the neutral wording, audited `LookupFailed` |
| `TheRecordedAnswerIsReusedInsideTheInterval` | **the cache**: a second request inside the interval renders with no read of its own; at exactly one interval after the read started, a new read |
| `TheOffSwitch_LetsEveryPageThroughWithoutReadingAnything` | off: with a refusing answer configured the page renders - no directory read, no name translation, no audit |
| `ARequestThatRendersNoPage_IsNotChecked`, `AnUnauthenticatedRequest_IsNotChecked` | scope: a non-component endpoint and an anonymous request are not checked |
| `ARecordedAnswerNeverRefuses_RestoredAccessIsSeenAtTheNextRequest` | rule 3: a recorded refusal is re-read; restored access confirms at once, then from the new answer |
| `ARecordedAnswerIsComparedWithTheSettingsAsTheyAreNow` | answers, not verdicts: a group granted after the answer was recorded is compared too, and its refusal is a fresh read's |
| `TheCircuitChecksNeverUseARecordedAnswer` | rule 5: the interval check and the open check read the directory however fresh the recorded answer |
| `AReadByACircuitCheck_ConfirmsTheNextPageRequest` | rule 1: a circuit's read confirms the next page request |
| `AFailedRead_ForgetsTheRecordedAnswer` | rule 4: after the tab's check fails, a page request inside the good answer's interval reads, and is refused |
| `AnAnswerFromAReadThatStartedEarlier_DoesNotReplaceALaterOne` | rule 1: a slow read's pre-removal answer cannot displace the removal a later read recorded |
| `AFailureOfAReadThatStartedEarlier_DoesNotForgetALaterAnswer` | rule 4's guard: a slow read's failure leaves a later read's answer in place |
| `EveryRefusalReasonHasItsOwnPlainMessage_AndThePageIsStandalone` | three distinct messages, each naming Active Directory and no SID; the page has no script and a full-reload link |
| `ProgramCs_ChecksPageRequestsAfterAuthorizationAndBeforeTheComponentsRender` | Program.cs registers it once, after `UseAuthorization` and `PageLoadMiddleware`, before `MapRazorComponents<App>()` |

Every pre-existing test in `CircuitRevalidationTests` (101) and `OperatorMembershipDirectoryTests`
(20) still passes.

**Mutation proof.** Script `a29g-mutate.ps1` (session scratchpad): for each mutation, an exact-text
replacement asserted to match once; md5 of the original and the mutant; `dotnet build
ExchangeAdminWeb.Tests.csproj -c Release`; run `PageRequestRevalidationTests` and
`CircuitRevalidationTests` (119 cases) and record the failures; restore the original bytes, set a
fresh mtime, re-take the md5 (asserted equal); rebuild and run the same 119 again. All 20 below were
run on the final tree (pass 2; a first pass over the pre-clock-change files agreed on every
mutation it reached). Originals: middleware `60CB2B91049896B4EE7D80ABF4C6E765`, revalidator
`539EB87B9C0D977798D6BA8572DFEC50`, provider `FA3BFCF9EB51F998F40925CC1210EBEC`, `Program.cs`
`3FF8EB50A325ECB968C1E9F5CB595500`; test file `FEBF3215DE48493A15EB122AA59B707F`. **Every mutant
failed the named tests; every restore was md5-equal to the original and passed 119/119.**

| # | Mutation at the call site (file) | md5 mutated | Failed with it (cases) |
| --- | --- | --- | --- |
| M1 | **the per-request check removed** - the guard is `if (true)`, so `next` always runs (middleware) | `E21A9119924ED9E931964A7D9E4F65A7` | `ARefusedPrincipal_...` (both), `AnAccessCheckThatCannotRun_...` (both), `AnAcceptedPrincipal_...`, `TheRecordedAnswerIsReused...` (6) |
| M2 | a valid verdict refused too (middleware) | `67758DF2225B0521B5B263AF6CD031F4` | `AnAcceptedPrincipal_...`, `TheRecordedAnswerIsReused...` (2) |
| M3 | the off-switch ignored (middleware) | `66C5BE289EBB0787559D43A21930884B` | `TheOffSwitch_...` (1) |
| M4 | every request checked, page or not (middleware) | `5D0E71B4326B967E60326A80B8969389` | `ARequestThatRendersNoPage_IsNotChecked` (1) |
| M5 | an unauthenticated request checked (middleware) | `81E6B97C01D990ADFD504FA33FA33FF1` | `AnUnauthenticatedRequest_IsNotChecked` (1) |
| M6 | a check that cannot run lets the page through: `IsValid \|\| Reason == LookupFailed` (middleware) | `32E2BAEA438885BCA2075E3E4C3685DC` | `AnAccessCheckThatCannotRun_...` (both) (2) |
| M7 | the refusal writes no audit line (middleware) | `63D6E9BB5FAE8475AA1AFBCE569D3574` | `ARefusedPrincipal_...` (both), `AnAccessCheckThatCannotRun_...` (both) (4) |
| M8 | the registration removed (`Program.cs`) | `AC26B8EA6D6E5CC47EFC692912B50B65` | `ProgramCs_ChecksPageRequests...` (1) |
| M9 | the audit line loses the request's address (middleware) | `781B2B99A72F4DB0944B8547F0FE7B8B` | `ARefusedPrincipal_...` (both) (2) |
| W1 | a disabled account gets the lookup-failure message (middleware) | `CCB16BC47F0E2CD22104B92FF95FD67C` | `EveryRefusalReasonHasItsOwnPlainMessage_...` (1) |
| W2 | the reload link becomes an enhanced navigation (middleware) | `EC180FE3B5CD769E7F5F8B0D10491DA4` | `EveryRefusalReasonHasItsOwnPlainMessage_...` (1) |
| R1 | nothing is recorded (revalidator) | `F21F85E3BB429D1AF8B636E6A4B40FCA` | `TheRecordedAnswerIsReused...`, `AReadByACircuitCheck_...`, `ARecordedAnswerNeverRefuses_...`, `AFailureOfAReadThatStartedEarlier_...` (4) |
| R1b | an earlier-started read's answer replaces a later one (revalidator) | `80925A3F655036E33171561ED0C23191` | `AnAnswerFromAReadThatStartedEarlier_...` (1) |
| R2a | a page request never uses a recorded answer (revalidator) | `09F4F3E82DC69E1B6AD917D56E43B6CB` | the four of R1 (4) |
| R2b | an answer exactly one interval old still confirms: `<` to `<=` (revalidator) | `9438A211DABD5999A418C421CD651E1D` | `TheRecordedAnswerIsReused...` (1) |
| R3 | a recorded answer may refuse (revalidator) | `209D828D233CE82DBE68F6CC4298671F` | `ARecordedAnswerNeverRefuses_...`, `ARecordedAnswerIsComparedWithTheSettings...`, `AnAnswerFromAReadThatStartedEarlier_...` (3) |
| R4 | a failed read does not forget (revalidator) | `CB4728B728F99822DFBAAD0E8D666002` | `AFailedRead_ForgetsTheRecordedAnswer` (1) |
| R4b | a failed read forgets a later-started answer too (revalidator) | `0A70F090909C74A15C7F706D3270AFB8` | `AFailureOfAReadThatStartedEarlier_...` (1) |
| R5 | the circuit checks use recorded answers (revalidator) | `0BE583C30C3FFDC4525B827364F41E31` | `TheCircuitChecksNeverUseARecordedAnswer`, `AFailedRead_...`, and aud-a29d's `CircuitRevalidationTests.TheCapturedGrantsNeverShrink_...` (3) |
| P1 | the shared audit writer writes nothing (provider) | `A00DDF56A459860B1A668052E4D83D11` | 22 cases: both page-request refusal tests and 14 circuit tests that read the audit line, `Invalidation_WritesOneAuditLineWithItsReason` (all reasons) among them - the extraction serves both paths |

Not proved by a mutation: the middleware's catch-all (Known gaps).

After the mutation pass `dotnet format` asked for one whitespace change in the test file (an object
initializer moved to its own line); its final md5 is `9CE012F8A3E9EDB7B9C855DBAF054491`, and the
four classes around this change (`PageRequestRevalidationTests`, `CircuitRevalidationTests`,
`OperatorMembershipDirectoryTests`, `GlobalProgressWiringTests`) passed 181/181 on it.

Gates, on the final tree (base `eb017102`):

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors (`error CS` count 0); no warning in any
  file this change touches (the warnings that remain are in files it does not touch).
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m -- xUnit.MaxParallelThreads=4`:
  **6711 passed, 0 failed, 0 skipped, 6711 total** (6 m 17 s); nothing hung.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean (after the
  whitespace change above).
- `git diff --check HEAD` (after staging): clean. `tools/Test-AsciiOnly.ps1`: passed.

Not run: anything against a live directory (by instruction) and any browser (no harness) - see Known
gaps.

## Coder dispute

None.

## Known gaps

- **Not run against a live directory or in a browser.** By instruction nothing here reached a
  directory, and the repo has no browser harness. The directory read is the aud-a29b one, unchanged
  and still proved only through its fake command seam (aud-a29b, gap 1). The host tests run the real
  ASP.NET Core pipeline and the real prerender over a loopback port, but no browser. What a browser
  does with the 403 page after an enhanced navigation is read from the shipped script, not observed:
  `_framework/blazor.web.js` 10.0.12 retries a GET as a full page load only for a 2xx response that
  lacks the `blazor-enhanced-nav: allow` header (`c=t.status>=200&&t.status<300` ...
  `if(c&&"allow"!==t.headers.get("blazor-enhanced-nav")){if(s)return void Pi(e)`), and puts any
  other `text/html` response in place of the document (`if(h?.startsWith("text/html")&&i){...
  hi(document,e)`), so the refusal page replaces the page. Live check owed on dev, with the
  aud-a29b one: as an operator holding a section grant, load a page (rendered, no
  `SessionRevalidation` line); remove the operator from
  the group, close their tabs and wait one interval; request the page - "This page was not shown",
  HTTP 403, one `SessionRevalidation` line with trigger `PageRequest`; then follow a link from an open
  tab after its next check - the same.
- **The window is one interval.** A page request is confirmed by a directory answer up to one
  `Authentication:RevalidationInterval` old. An operator removed within that window of their
  account's last read can have one more page prerendered (its `OnInitialized` runs); in a browser the
  circuit that page opens then fails its fresh open check, whose read is recorded, so every later
  request is refused. A client that opens no circuit (a script fetching pages) is refused only when
  the recorded answer expires or another read records the removal - at most one interval.
- **A refused page request does not end the operator's other tabs.** Their circuits end at their own
  next check, within one interval, as before.
- **The response path now waits on the directory** for the first page load after a quiet interval:
  one read before the page renders, then the circuit-open read as before. After an app restart every
  operator's first page load pays it, and reads queue four at a time for the whole app (the existing
  `MaxConcurrentLookups`), so a slow directory slows first page loads; the 60 s lookup timeout
  includes the queueing, and a request past it is refused (fail closed). Not measured - no directory
  here. The settings read and the LSA translation of name-configured static groups now also run once
  per checked page request; normally served from the LSA cache (an assumption, not measured).
- **Concurrent cold requests for one account each read** (no single-flight): several tabs opened at
  once each pay a read, within the same four-slot bound.
- **One audit line per refused page request**, not one per account and interval: a reload loop, or
  every page load during an outage, writes one line each - the same rate as the circuit-open check's
  one line per circuit today.
- **The middleware's catch-all is not proved by a test.** It turns an exception the check did not
  anticipate into a refusal; the revalidator returns a verdict for every failure except the caller's
  own cancellation, so no test reaches it without a seam that does not exist.
- **Scope is the component endpoints.** Every page `MapRazorComponents` routes is checked, `Error` and
  `access-denied` included. The app maps no other endpoint that renders data today; one added later
  would need its own check.

## Choices the owner can veto

1. **Candidate (a) with the interval cache, not a fresh read per page request.** The window above, in
   exchange for no extra directory reads while an account is being read anyway. A fresh read per
   request closes the window at one read per page request and per enhanced navigation (two per full
   page load), every one on the response path.
2. **A recorded answer confirms but never refuses.** Restored access works at the next request; a
   refused operator pays one read per refused request.
3. **The circuit checks never use the recorded answers**, so aud-a29b's interval and aud-a29c's hold
   are unchanged, at the cost of a second read on a cold first page load.
4. **Every Razor component page is checked and nothing else** - the hub keeps its gate.
5. **The refusal is a 403 standalone page** (no app layout, no script), the reason in plain words, a
   full-reload link; **one audit line per refused request** in the circuit's shape, trigger
   `PageRequest`; a warning in the log. No admin email.
6. **After the page's own authorization and inside the page-load scope**: a request its policy denies
   costs no read, and the status frame shows a page waiting on the directory as loading.

## Reviewer comments

None yet.

## Closeout

Pending: codex review of the commit, merge to `master` by the supervisor (with the base version bump -
app-wide - and the index row), and the live dev check in Known gaps.

## Reviewer comments (supervisor, auth-batch-1)

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier=standard pair (grade fallback, owner 2026-08-03) [escalated: T1 Authorization/* paths]; codex-cli 0.163.0; base eb017102 head 2678735e (merged a5d12cc8); 2026-10-23T19:33-19:5xZ; capability proof: AGENTS.md read and git show --stat 2678735e both succeeded. Verdict: two LOW candidates, both admitted - aud-a29i (no path in the refusal audit row) and aud-a29j
(expired cache entries retained for operators who never return). Codex confirmed: the middleware reaches authenticated
component page endpoints after UseAuthorization (full loads and enhanced navigation); static files, the db mode and the
_blazor transport bypass it by design; fail-closed is intact (directory errors, timeouts, missing lookups and unreadable
grant stores become LookupFailed and a neutral 403 page; the catch-all does the same); the 403 body carries no group
names, SIDs or exception text; the loopback tests use a hand-built probe host, with Program.cs order covered by a
source-text tripwire. Artefacts: `.agents/review/auth-batch-1.result.txt`, `.stream.log`, `prompt-auth-batch-1.md`.
## Owner answers, 2026-10-26/27 - the held choices close

- (a) the one-interval window: owner "don't care. this will never happen in the real world. fix it or document it" - documented: this section and "Choices the owner can veto" item 1 describe the window; the fresh-read-per-request variant stays rejected.
- (b) the refusal: owner "403." - item 5 is what landed: a standalone 403 page, one audit line per refused request. Nothing to change.
- (c) cache-free circuit checks: item 3 stays as landed (the open tab's timer check reads the directory fresh); the owner asked what it was, it was explained, and nothing changes.

The row's "owner calls held" note is closed; fix/a29g was deleted 2026-10-27 after its content was verified in master by patch identity.
