# aud-a29i: a refused page request's audit row does not say which page was refused

**Severity**: LOW - audit completeness. `Middleware/PageRequestRevalidationMiddleware.cs` about 139 and `Authorization/MembershipRevalidatingAuthenticationStateProvider.cs` about 311: the row carries operator, IP, `SessionRevalidation`, `trigger: PageRequest`, reason and removed SIDs, but no route or path, so several refused page requests from one operator cannot be told apart by target during an incident review.
**Status**: Closed - merged to master `d58b5d7c`; codex auth-batch-2 clean. Complete once fix/a29g is deleted after its follow-ons land
**Branch**: `fix/a29g` (worktree `ExchangeAdminWeb-wt/a29g`), after `2699dd29` (aud-a29h)
**Commit**: the `fix(aud-a29i)` commit on `fix/a29g` that carries this record
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier=standard pair (grade fallback, owner 2026-08-03) [escalated: T1 Authorization/* paths]; codex-cli 0.163.0; base eb017102 head 2678735e (merged a5d12cc8); 2026-10-23T19:33-19:5xZ; capability proof: AGENTS.md read and git show --stat 2678735e both succeeded

## Evidence

Codex auth-batch-1 finding 1 at `a5d12cc8`.

## Approach (to be written by the implementing agent before code)

- Pass a sanitised request path (`PathBase + Path`, no query string, bounded length) into the page-request audit extras; keep the existing fields. Test pins the field; mutation: the field dropped.

### Implementer's approach (written before code, at `2699dd29`)

The call and the writer, quoted. `Middleware/PageRequestRevalidationMiddleware.cs`:

```csharp
139        MembershipRevalidatingAuthenticationStateProvider.WriteAuditLine(
140            audit, _logger, account, context.Connection.RemoteIpAddress?.ToString() ?? "Unknown", verdict,
141            MembershipRevalidatingAuthenticationStateProvider.TriggerPageRequest);
```

`Authorization/MembershipRevalidatingAuthenticationStateProvider.cs`, the shared writer:

```csharp
316    internal static void WriteAuditLine(
317        AuditService audit, ILogger logger, string account, string ipAddress, MembershipRevalidationResult result, string trigger)
...
321            var extra = new Dictionary<string, object?>
322            {
323                ["reason"] = (result.Reason ?? CircuitRevocationReason.LookupFailed).ToString(),
324                ["trigger"] = trigger,
325            };
326
327            if (result.RemovedGroupSids.Count > 0)
328                extra["removedGroupSids"] = result.RemovedGroupSids.ToArray();
```

The row's `target` is the operator, so nothing names the page. Change:

- **The writer** takes an optional `requestPath` and, when given, writes it as `extra["path"]`. Every
  existing field is written as before; the circuit's revocations pass no path (a circuit has no
  request), so their rows are unchanged. No audit line in the app uses a `path` extra already, and
  `AuditService.MergeExtra` only adds keys, so it cannot displace a base field. Usage telemetry takes
  only category, action and result from an audit call, so the path does not leave the audit trail.
- **The middleware** passes `AuditPath(context.Request)`: `PathBase` plus `Path` - never the query
  string, which is where a page's search terms and anything an operator typed would be -
  percent-encoded by `PathString.ToUriComponent()`, so no character in it (a newline, a quote) can
  break a log line or read as markup, and cut to `MaxAuditPathLength` = 256 characters, the last three
  `...` when cut. In this app a page path is a few dozen characters (`/ExchangeAdminWeb/risky-users`);
  256 holds any real one whole.
- `README.md` "Session Revalidation": the page-request bullet says the line carries the page's path,
  without its query string.

Files (declared): the middleware, the provider, `README.md`,
`ExchangeAdminWeb.Tests/PageRequestRevalidationTests.cs` (a path-base option on the probe host and
two tests), this record. App-wide behaviour, but no version bump here (the supervisor's).

Guard: an end-to-end test over the probe host with a path base, requesting the page with a query
string - the refused row's `path` is the path base plus the path, and the query's value appears
nowhere in the row; and a unit test of `AuditPath` - encoded, bounded, marked, no query.
Mutations: the field dropped in the writer; the path not passed by the middleware; the query string
appended; the bound removed; the encoding removed.

## Files changed

- `Middleware/PageRequestRevalidationMiddleware.cs` - `AuditPath`, `MaxAuditPathLength`, the call
  passes the path; class remarks.
- `Authorization/MembershipRevalidatingAuthenticationStateProvider.cs` - `WriteAuditLine` takes an
  optional `requestPath`, written as `path`.
- `README.md` - the page-request bullet names the field.
- `ExchangeAdminWeb.Tests/PageRequestRevalidationTests.cs` - a `pathBase` option on the probe host
  (`UsePathBase` first, as `Program.cs` does) and the two tests below.
- this record.

## Guard proof

As the approach, no change of scope. Nothing reached a directory.

| Test (`PageRequestRevalidationTests`) | Pins |
| --- | --- |
| `TheRefusalAuditLineNamesThePage_WithoutItsQueryString` | **the finding**, end to end over the probe host with path base `/probe-base`: a refused `GET /probe-base/aud-a29g-probe?lookup=typed-by-the-operator` writes `path` = `/probe-base/aud-a29g-probe`; the query's value appears nowhere in the row; action, trigger, reason, removed SIDs and target are as before |
| `TheAuditPath_IsThePathBaseAndPath_EncodedAndBounded` | `AuditPath`: path base plus path with a query present but not recorded; a newline arrives as `%0A`; a 400-character path comes back exactly `MaxAuditPathLength` long, ending `...`; no `?` |
| every other test in the class and in `CircuitRevalidationTests` (unchanged) | the circuit's rows, which pass no path, and every existing field of the page-request row |

Mutations by `a29i-mutate.ps1` (session scratchpad; the aud-a29g procedure): exact-text replacement
asserted to match once, md5 of original and mutant, `dotnet build ExchangeAdminWeb.Tests.csproj -c
Release`, `PageRequestRevalidationTests` and `CircuitRevalidationTests` (123 cases) run, original
bytes restored with a fresh mtime and the md5 re-taken (asserted equal), rebuilt and run again.
Originals: middleware `7932259151086BD46D8737F59EE214D6`, provider `0142B2A22BA81A02FD54FD470F3CB35B`;
test file `ADD288155FB6A2FB5DCBAF847D90C5B6`. **Every mutant failed; every restore was md5-equal and
passed 123/123.**

| # | Mutation (file) | md5 mutated | Failed with it |
| --- | --- | --- | --- |
| I1 | **the field dropped** in the writer: `extra["path"] = requestPath;` replaced (provider) | `863D84CF5B5FBEAE3B3F8AC2380D2EC8` | `TheRefusalAuditLineNamesThePage_WithoutItsQueryString` |
| I2 | **the field dropped** at the call: `requestPath: null` (middleware) | `FB2F6DF46A50115DEABCC86FAE6A5FEF` | `TheRefusalAuditLineNamesThePage_WithoutItsQueryString` |
| I3 | the query string appended to the path (middleware) | `6C2CF1AFF6AA3536E402CBD6F38A7B01` | both tests |
| I4 | the bound removed: `<= int.MaxValue` (middleware) | `35A0F00418B3ACC879BD4C744B0EEED5` | `TheAuditPath_IsThePathBaseAndPath_EncodedAndBounded` |
| I5 | the encoding removed: `.Value` for `.ToUriComponent()` (middleware) | `D843F96E7C429E6C5234882073443A53` | `TheAuditPath_IsThePathBaseAndPath_EncodedAndBounded` - and, in that one run, `AnAnswerFromAReadThatStartedEarlier_DoesNotReplaceALaterOne`, which I5 cannot reach (see Known gaps) |

Gates, on the final tree:

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; no warning in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m -- xUnit.MaxParallelThreads=4`:
  **6715 passed, 0 failed, 0 skipped, 6715 total** (6 m 36 s). The run before it, with the machine's
  CPU at 100% under two other agents' test runs (worktrees `a70`, `a68-kd2`), failed 3 of 6715 -
  all wall-clock bounds in code this change does not touch:
  `SettingsStoreBusyTests.AdminSettings_ASecretIdCompareThatFindsTheStoreBusy_...` ("waited 14.68
  s"), `SqliteBusyWaitTests.AWriteThatMeetsAHeldLock_FailsBusy_AfterTheDocumentedWait` ("waited 12.98
  s: more than one busy wait of 5 s") and `ExtendedLogServiceTests.Dispose_GivesTheQueueTimeInProportionToItsLength`;
  the three passed rerun alone, and the full rerun above passed whole.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD` (after staging): clean. `tools/Test-AsciiOnly.ps1`: passed.

## Coder dispute

None.

## Known gaps

- **The circuit's rows still carry no page.** A circuit has no request, and which page an inbound
  activity belongs to is not known at the gate (aud-a29d, gap 2); the brief covers page requests.
- **Path segments are recorded as sent.** A page whose route carries an identifier records it; the
  query string, where this app's pages take search input, never is.
- **Not this finding, seen while proving it:** in the I5 mutant run (13 s against the usual 5-7)
  `AnAnswerFromAReadThatStartedEarlier_DoesNotReplaceALaterOne` failed once, in roughly 76 runs of
  it; it did not reproduce in 12 reruns, solo or beside `CircuitRevalidationTests`. That test holds a
  directory read open across other steps, and the harness's lookup timeout is 10 s of real time, so a
  stall that long under machine load turns its early check into `LookupFailed` (or trips its own 10 s
  wait for the read). No production race: `Record` keeps the later-started answer atomically and
  `Forget` removes only the instance it saw. Candidate for its own record: give the two slow-read
  tests a lookup timeout well above any stall.

## Closeout

Pending: review, merge to `master` by the supervisor (app-wide: the base version bump is the
supervisor's, once for the stream).

## Reviewer comments (supervisor, auth-batch-2)

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier=standard pair (grade fallback, owner 2026-08-03) [escalated: T1 Authorization/* paths]; codex-cli 0.163.0; base 2678735e head 0526dc83 (merged d58b5d7c); 2026-10-23 evening; capability proof: AGENTS.md read and git show --stat 0526dc83 both succeeded. Verdict: clean. Codex confirmed the path field on every refusal shape (encoded before the cap, no query), no identity-bearing route segments today, ConcurrentDictionary with exact pair removal so the sweep cannot drop a newer answer, sweep cost bounded to distinct recorded operators after a successful read, RecordedAccountSids internal and test-only, the off-switch wording true in every mode, the startup warning fired once, and the tests reading emitted text rather than the constant. Artefacts: `.agents/review/auth-batch-2.result.txt`, `.stream.log`, `prompt-auth-batch-2.md`.
