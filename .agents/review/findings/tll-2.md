# tll-2: Three tasks are started on a false promise that none of them can throw

**Severity**: MEDIUM - no wrong ANSWER is produced (the call throws rather than lying), but a
whole class of failure bypasses the per-source error model the service exists to provide, two
started tasks are abandoned unobserved, and the code carries a comment asserting the opposite of
what it does.

**Status**: Verified
**Branch**: -- (direct-to-main)
**Commit**: recorded in the closeout below

## Evidence

`Services/CloudSignInService.cs:194-203`:

```csharp
// Started together, awaited one at a time. ... Nothing below throws, so no task is left
// with an unobserved exception.
var activityTask = ReadActivityAsync(v1, upn);
var interactiveTask = ReadLogAsync(v1, upn, nonInteractive: false);
var nonInteractiveTask = ReadLogAsync(beta, upn, nonInteractive: true);
```

The promise is false. Both readers await `client.GetWithStatusAsync(...)` with no catch
(`:284`, `:324`), and `DefenderApiClient.SendAsync` wraps its body in a catch for
`TaskCanceledException` ONLY. Everything else escapes:

- `DefenderApiClient.GetAccessTokenAsync` throws `InvalidOperationException` when the token
  request returns a non-success status (`Services/DefenderApiClient.cs:284`) and when the client
  is not configured;
- `JsonDocument.Parse` / `GetProperty("access_token")` throw on a malformed token response;
- `_httpClient.PostAsync` to login.microsoftonline.com throws `HttpRequestException` on a DNS or
  socket failure.

Triggering condition: an expired or revoked client secret, a Secret Server entry pointing at a
deleted app registration, or the app pool losing outbound DNS. All three are ordinary operational
states, not exotic ones.

## Predicted observable failure

`GetCloudSignInAsync` throws on the first await. The other two tasks - already running, and
failing the same way, since all three share one credential - are never awaited. The operator gets
an unhandled service exception instead of the structured "this source did not answer, here is
why" result the whole class is built around, and the abandoned faults surface later as
`TaskScheduler.UnobservedTaskException` on finalization rather than at the call site.

The inconsistency is the sharpest part: a 403 FROM Graph returns a named per-source error, while
a 401 from the token endpoint - a closely related and more likely misconfiguration - throws.

## Approach

Catch unexpected exceptions inside each reader and convert them to `DidNotAnswer` with a named,
sanitized reason, so every started task completes and the per-source error model covers the token
and transport layer too. Log the exception through the module logger rather than putting its
message in the operator-visible string, so nothing from an auth response body can reach the page.
Correct the comment to say what the code now guarantees.

A genuinely unconfigured module still throws before any task starts, at the existing null-client
check - that is a different condition and keeps its current behaviour.

## Files changed

- `Services/CloudSignInService.cs`
- `ExchangeAdminWeb.Tests/CloudSignInServiceTests.cs`

## Guard proof

Two tests in `ExchangeAdminWeb.Tests/CloudSignInServiceTests.cs`, driven by a stub token endpoint
that returns 401 - the real shape of an expired secret, reproduced through
`DefenderApiClient.GetAccessTokenAsync` rather than by faking an exception:

| Test | Mutation | Result |
| --- | --- | --- |
| `ASignInFailureIsReportedPerSourceRatherThanThrownOutOfTheLookup` | activity reader's catch replaced with `throw;` | FAIL (2), restore -> 26/26 |
| `NothingFromTheAuthResponseReachesTheOperatorVisibleError` | log reader's catch replaced with `throw;` | FAIL (2), restore -> 26/26 |

Both probes were run separately, and each on its own makes both tests fail - which is the point:
before the fix a single unguarded source was enough to throw the whole lookup.

The second test is a leak guard on the catch itself. The stubbed auth response carries a
`CANARY-...` correlation marker; the operator-visible error must contain the exception TYPE and
must not contain the marker. Without it, "improving" the message to include `ex.Message` would be
an invisible regression.

## Coder dispute

None. The comment is mine and it is wrong; I asserted a property of `DefenderApiClient` I had not
checked.

## Known gaps

When every source fails this way the result is `Unverified` with two errors, which is correct but
is a worse operator experience than a single clear "the credential is bad" message. Naming that
case specifically is a page concern and belongs to S3, not to this service.

## Reviewer comments

Same dispatch as `tll-1`:
`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard`
Harness: codex-cli 0.154.0 (`codex exec`, CLI transport, `-s read-only`, generation pass).
Range: `046e3d213586b769680826354d714102ce4af721..cb968daa76da3711f238c80af6c6f252e1cf17d5`.
`capability_ok: true`. Verdict: **findings** (2). Timestamp: 2026-09-30.
Escalation triggers: none matched.

No verification round: MEDIUM, so it closes on the coder-side guard proof
(`.agents/decisions.md` 2026-08-31).

The reviewer's reasoning was checked against the code before admitting, and the part worth
keeping is that it did not stop at the comment. It read `DefenderApiClient.SendAsync`, saw that
the catch covers `TaskCanceledException` only, and named the three concrete escape routes.

## Closeout

Fixed directly on `master`. Commit and completion receipt are in the follow-up bookkeeping
commit that fills in the SHA here and in `.agents/review/index.md`.
