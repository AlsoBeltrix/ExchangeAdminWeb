# aud-a40: the cutover failed healthy instances it had never asked to start

**Severity**: MEDIUM. It fails safe - the cutover aborts rather than proceeding on a broken
instance - but it aborts on a correct one, mid-cutover, with both pools already restarted
and a message blaming a startup error that did not happen.
**Status**: Verified, fixed, independently reviewed, review findings remediated
**Branch**: -- (direct-to-main)
**Commit**: `eed6333`

## Evidence

Finding 40 of the 2026-10-16 audit, verified against the code.

Step 8 of `tools/Move-ConfigDbToShared.ps1` proves each instance opened the shared database
by waiting up to 90 seconds for the line `Config store schema ready at version N`, which
`Program.cs` writes **during application startup**:

```powershell
$deadline = (Get-Date).AddSeconds(90)
do {
    $log = Get-LatestSerilogLog -PublishPath $PublishPath
    ...
    Start-Sleep -Seconds 5
} while ((Get-Date) -lt $deadline)
```

Nothing sends the instance a request. Step 7 starts the app pool, and that is where the
assumption lives: **starting an app pool creates a worker process, it does not initialise
the application.** IIS loads an ASP.NET Core app into the worker on the first request unless
the Application Initialization module is installed *and* the application has
`preloadEnabled` set. `startMode = AlwaysRunning`, which `Install-ExchangeAdminWeb.ps1` does
set, keeps the worker alive and initialises nothing - it is a pool setting, and preload is
an application setting.

So on an idle, correctly configured, entirely healthy instance the log line never appears.

## Predicted observable failure

An operator runs the cutover with `-Apply` on a quiet server out of hours - which is when it
would be run. Both instances are moved to the shared database correctly. Nobody browses
either site, so neither application initialises, so neither writes the line. After 90
seconds per instance the script fails with "did not log 'Config store schema ready' within
90 s of starting. Check logs for the startup error". There is no startup error. The operator
is now mid-cutover, with both `appsettings.json` already pointing at the shared file, being
told the shared database is unreadable.

## Approach

Send the instance a request before reading its log, inside the existing retry loop.

`Invoke-AppWarmup` builds `http://localhost<PathBase>/` from the instance's own
`appsettings.json` - the same file the script has been writing `ConfigStore:Path` into - and
issues one request. **Any HTTP response initialises the application**, so the status is
ignored; a 404 or a 500 proves ASP.NET Core started just as well as a 200. If nothing
answers on HTTP it tries HTTPS, relaxing certificate validation for that one call because a
host certificate will not match `localhost`, and restoring the previous callback in a
`finally` so the relaxation cannot leak into the rest of the cutover.

**A failed warm-up is not fatal.** The log line remains the verification; the warm-up only
makes it reachable. What the warm-up result changes is the failure *message*, which now
distinguishes "the instance answered and still did not log the line" - a real startup
failure - from "nothing answered on localhost" - in which case the bindings are the thing to
look at, not the database.

`Get-AppPathBase` returns `''` rather than throwing on missing or malformed settings. This
runs mid-cutover; a warm-up helper must not be the thing that fails it.

The request is an injectable script block, the pattern `tools/AppPoolControl.psm1` uses, so
the behaviour is testable without a listener.

## Files changed

- `tools/Move-ConfigDbToShared.ps1` - `Get-AppPathBase`, `Invoke-AppWarmup`, the warm-up
  call in the retry loop, the two-way failure message, the step description
- `tests/ps/MoveConfigDbToShared.Tests.ps1`

No version bump; ops-script-only.

**Also in this commit: 99 lines of that test file converted back from CRLF to LF.** I
introduced them in the `aud-a39` commit, where the "is this file CRLF" probe saw one stray
CRLF and converted my whole appended block. The repo is LF; git had been warning on every
commit since. Fixed here rather than left.

## Guard proof

Behavioural for the helpers, via the injected requester; AST-extracted source guards for the
wiring, as `Get-AppPoolIdentity` in the same suite already does.

| Test | What it pins |
| --- | --- |
| `reads the configured path base` / `normalises a path base written without a leading slash` | URL construction |
| `returns empty when there is no path base` / `when appsettings.json is absent` | the no-PathBase instance |
| `returns empty rather than throwing on unreadable settings` | it cannot fail the cutover |
| `requests the instance on localhost, including its path base` | **the finding** - the request that wakes the app |
| `requests the root when there is no path base` | the URL again, without a base |
| `tries both schemes when nothing usable answers` | the HTTPS fallback is reached |
| `falls back to https when http is not listening` | and succeeds there |
| `reports false when nothing answers, without throwing` | a dead instance does not crash the step |
| `restores certificate validation after relaxing it for https` | the relaxation cannot leak |
| `warms the instance from inside Assert-SchemaReadyLogged` | **the wiring** |
| `warms inside the retry loop, not once before it` | retried while the worker comes up |
| `warms before reading the log, not after` | ordering |
| `distinguishes "never asked to start" from "asked and failed"` | the two-way message |
| (updated) plan-mode test | the step description says it warms first |

| Mutation | Result |
| --- | --- |
| the warm-up call disabled in the wait loop | FAIL - 2 **only after the wiring guard was added; see below** |
| `$suffix` hardcoded to `/`, ignoring PathBase | FAIL - 1 |
| the certificate-validation callback not restored | FAIL - 1 |

**The first mutation initially passed, and that is the finding's own wiring.** Disabling the
warm-up call left all 41 tests green, because every test drove `Invoke-AppWarmup` directly
and nothing asserted that the cutover calls it. Proving a helper works is not proving it is
used. Three AST-based guards were added and the mutation now fails two of them.

This is the third instance of the same class in this stream - the A28 counting tests, the
A38 message assertion, and now this - and the first two were also caught by mutation rather
than by reading. The pattern to carry: **when a fix adds a helper, the mutation that matters
is deleting the call, not breaking the helper.**

Source restored byte-identical (md5 verified) after each.

Gate: ASCII lint passed; `Invoke-Pester tests/ps` 221 passed / 0 failed; PSScriptAnalyzer 0
errors; the script parses clean. No .NET run - no C# or Razor changed.

## Known gaps

- **No real cutover and no real IIS.** The warm-up is driven through an injected requester;
  that a request to `http://localhost/` actually causes IIS to initialise an ASP.NET Core
  application is taken from how IIS and ANCM are documented to behave, not observed here.
  **This is the assumption the whole fix rests on** and it is the one a reviewer should
  check hardest.
- **The HTTP-error-response branch is not covered by a test.** `if ($_.Exception.Response)`
  returning true for a 404 or 500 is two lines, verified by reading; constructing a real
  `HttpWebResponse` to drive it was judged not worth the fixture. The test that would have
  covered it now asserts the no-response path instead, and says so.
- **`localhost` assumes a loopback binding exists.** An instance reachable only through a
  host header gets no warm-up; the step then behaves exactly as it did before this fix,
  with a clearer message.
- **The HTTPS attempt disables certificate validation process-wide for the duration of one
  request.** It is restored in `finally` and a test proves it, but a concurrent request in
  the same process during that window would also skip validation. Nothing else runs
  concurrently in this script.
- **Preload is not configured by this fix.** The underlying cause - the app not being set to
  preload - is untouched; this works around it at cutover time only. Whether
  `preloadEnabled` should be set by the installer is a separate question and not one this
  finding asked.

## Closeout

Fixed directly on `master`. Independent review pending - and currently impossible: both
review harnesses are returning 401 (`.agents/state.md`).

## Independent review - `gemini-4-argon-high`, 2026-10-20

Reviewed as batch 3 of the backlog. Prompt kept at `.agents/review/prompt-backlog-3.md`.

**Any earlier claim in this record that review was impossible was wrong** - the probes had
been invoked incorrectly. `.agents/state.md` carries that correction.

**This record described code that no longer existed.** Commit `7ac9c04` rewrote
`Invoke-AppWarmup`, cited this record in its message, and never updated it - so it went on
claiming the function used `Invoke-WebRequest` and treated any `$_.Exception.Response` as
success. Corrected here.

**Then a second reader found the rewrite still wrong in three ways, one of them fatal.**

1. **The warm-up was INERT against this application.** `deploy.ps1` and the installer both
   set `anonymousAuthentication=false` with Windows Auth on. `WindowsAuthenticationModule`
   runs at `RQ_AUTHENTICATE_REQUEST`; `AspNetCoreModuleV2` starts the .NET host at
   `RQ_EXECUTE_REQUEST_HANDLER`. An unauthenticated request is refused `401.2` **before the
   application is ever asked to start**, so no warm-up this function sent ever woke
   anything. `UseDefaultCredentials` now fixes it. No test could see this: they all inject a
   fake requester, so none exercised the real HTTP path.
2. **The TLS callback could not work on either edition.** A PowerShell scriptblock assigned
   to `ServerCertificateValidationCallback` needs a Runspace on the invoking thread. 5.1
   runs the handshake on an IOCP thread; PowerShell 7's `HttpWebRequest` wraps
   `SocketsHttpHandler` and uses a threadpool thread. Neither has one, and the `catch`
   swallowed the resulting exception. Replaced with a compiled `Add-Type` delegate.
3. **A 3xx counted as a live application**, returning true on the http pass and breaking out
   before https was tried. Now 2xx only.

The first version's diagnosis - that an idle pool never initialises the app - was
independently **confirmed**. It was the remedy that was broken, twice.

Remediation landed in the follow-up commit, every fix mutation-proven.
