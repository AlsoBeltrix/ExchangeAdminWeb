# aud-a29j: the recorded-answer cache keeps expired entries for operators who never return

**Severity**: LOW - unbounded retention by distinct SID. `Authorization/CircuitMembershipRevalidator.cs` about 93: `_answers` is a singleton dictionary keyed by SID; expired entries are removed only when `RecordedAnswerFor` runs for that SID or a failed read calls `Forget`. Over a long app-pool lifetime, stale membership sets for operators who never return stay in memory past the interval. Bounded by the real operator population, but not interval-bounded as the record describes the cache.
**Status**: Merged to master `d58b5d7c`; codex defect review auth-batch-2 in flight
**Branch**: `fix/a29g` (worktree `ExchangeAdminWeb-wt/a29g`), after `14079ee5` (aud-a29i)
**Commit**: the `fix(aud-a29j)` commit on `fix/a29g` that carries this record
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier=standard pair (grade fallback, owner 2026-08-03) [escalated: T1 Authorization/* paths]; codex-cli 0.163.0; base eb017102 head 2678735e (merged a5d12cc8); 2026-10-23T19:33-19:5xZ; capability proof: AGENTS.md read and git show --stat 2678735e both succeeded

## Evidence

Codex auth-batch-1 finding 2 at `a5d12cc8`.

## Approach (to be written by the implementing agent before code)

- Sweep expired entries opportunistically on record/read (cheap, no new dependency), or move to `IMemoryCache` with absolute expiration and a size limit. Test: an expired entry for SID A is gone after a record for SID B; mutation: the sweep removed.

### Implementer's approach (written before code, at `14079ee5`)

`Authorization/CircuitMembershipRevalidator.cs`, quoted:

```csharp
 90    // The directory's latest answer per account SID, and when its read started (aud-a29g). Facts about
 91    // the account, never verdicts: a page request still recomputes its verdict from the settings as they
 92    // are now and from its own token. One entry per operator, replaced by every later read.
 93    private readonly ConcurrentDictionary<string, RecordedAnswer> _answers = new(StringComparer.OrdinalIgnoreCase);
...
310    private void Record(string userSid, OperatorDirectoryMembership membership, long started)
311    {
312        var answer = new RecordedAnswer(membership, started);
313        _answers.AddOrUpdate(userSid, answer, (_, existing) => existing.Started > started ? existing : answer);
314    }
...
323    private OperatorDirectoryMembership? RecordedAnswerFor(string userSid)
...
328        if (Clock.GetElapsedTime(answer.Started) < _options.Interval)
329            return answer.Membership;
330
331        _answers.TryRemove(new KeyValuePair<string, RecordedAnswer>(userSid, answer));
```

As the finding says, an entry leaves only when its own SID is looked up again (331) or a read for it
fails (`Forget`); one for an operator who never returns stays for the app pool's life.

Chosen: **the sweep, on record.** After `Record` stores an answer, one pass over the dictionary removes
every entry whose read started an interval or more ago - by `TryRemove(KeyValuePair)`, the same
instance-guarded removal 331 uses, so a newer answer that replaced an expired one in the meantime is
never removed. Record is where the dictionary grows, and it runs only after a directory read, so a
pass over the operators (hundreds at most) is nothing beside the read it follows; the page-request
path that reads the cache on every page stays a single lookup. Retention becomes: at most one
interval after the read started, plus the time until the next successful directory read by anyone -
which, in an app with any traffic, is soon; an idle app reads nothing and adds nothing. Not
`IMemoryCache`: a new dependency, and a size limit is a second knob whose eviction could drop a live
answer (harmless - one extra read - but nobody asked for it).

Observing retention needs a seam: an expired answer and an absent one behave the same (neither
confirms anything), so the test reads `RecordedAccountSids`, a new internal snapshot of the keys.

Files (declared): `Authorization/CircuitMembershipRevalidator.cs` (the sweep, the snapshot, the
comment at 90-92), `ExchangeAdminWeb.Tests/PageRequestRevalidationTests.cs` (a second operator and
two tests), this record. No behaviour visible to an operator, no README change, no version bump here.

Guard: an expired entry for operator A is gone after a record for operator B; an entry for A still
inside the interval survives B's record. Mutations: the sweep removed; the sweep removing live
entries too.

## Files changed

- `Authorization/CircuitMembershipRevalidator.cs` - the sweep in `Record`, `RecordedAccountSids`, the
  field comment and the class remarks.
- `ExchangeAdminWeb.Tests/PageRequestRevalidationTests.cs` - a second operator (`Colleague`, its own
  account SID) and the two tests below.
- this record.

## Guard proof

As the approach, no change of scope. Nothing reached a directory; time is the manual clock.

| Test (`PageRequestRevalidationTests`) | Pins |
| --- | --- |
| `AnExpiredAnswer_IsSweptOutWhenAnotherAccountsAnswerIsRecorded` | **the finding**: the operator's answer is held; one interval after its read started, a colleague's answer is recorded, and only the colleague's is held |
| `AnAnswerStillInsideTheInterval_SurvivesTheSweep` | the sweep takes only what has expired: one second short of the interval both are held, and the operator's still confirms their next page with no read |
| every other recorded-answer test (unchanged) | rules 1-5 of aud-a29g still hold with the sweep in place |

Mutations by `a29j-mutate.ps1` (session scratchpad; the aud-a29g procedure), run against
`PageRequestRevalidationTests` and `CircuitRevalidationTests` (125 cases). Original revalidator
`9CA0366C1609EFD10FB2383A9C618524`; test file `6F43E13405474B7DDE702C4634EEF37C`. **Both mutants
failed; both restores were md5-equal and passed 125/125.**

| # | Mutation (revalidator) | md5 mutated | Failed with it |
| --- | --- | --- | --- |
| J1 | **the sweep removed**: `_answers.TryRemove(entry);` replaced | `86174223DDC8E9F786CEA4AD193980BD` | `AnExpiredAnswer_IsSweptOutWhenAnotherAccountsAnswerIsRecorded` |
| J2 | the sweep removes live answers too: `>= TimeSpan.Zero` | `BEE026BC0F6D1706756158CE84D2A1FF` | `AnAnswerStillInsideTheInterval_SurvivesTheSweep`, `AnExpiredAnswer_...`, `TheRecordedAnswerIsReusedInsideTheInterval`, `AReadByACircuitCheck_...`, `ARecordedAnswerNeverRefuses_...`, `AFailureOfAReadThatStartedEarlier_...` (6) |

Gates, on the final tree (md5s as above):

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; no warning in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m -- xUnit.MaxParallelThreads=4`:
  **6717 passed, 0 failed, 0 skipped, 6717 total** (7 m 6 s); nothing hung.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD` (after staging): clean. `tools/Test-AsciiOnly.ps1`: passed.

## Coder dispute

None.

## Known gaps

- **Retention is the interval plus the wait for the next read, not the interval exactly.** Nothing
  runs on a timer (the 2026-06-17 no-unattended-worker posture holds); an idle app keeps its last
  answers until something is read again, and it adds none meanwhile.
- **The sweep is a pass over every held answer on each successful read.** At this app's operator
  count that is a few hundred entries at most, after a directory read that costs far more; not
  measured.

## Closeout

Pending: review, merge to `master` by the supervisor.