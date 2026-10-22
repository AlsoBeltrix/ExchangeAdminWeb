# Defect review request: a busy settings store at the Secret ID compare is a named refusal (aud-a28v); one fallback-rule function pinned to the pool, digits-only Secret ID, ordinal ChangedEntries (aud-a26s); PowerShell warmed once at start-up and the test fixture keeps its failure (aud-a28w)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `6972a5c5` or later.
- The change is the branch merged at `a583777d`: `40026345` (plans), `65e8644b` (aud-a28v), `63f43eb5` (aud-a26s),
  `9c554a5a` (aud-a28w), `082a2b51` and `c7b61943` (comments only), `6493c327` (record SHAs), forked from `e9886df7`.
  Review `git diff e9886df7 6493c327`, and read the COMBINED `Services/Storage/ConfigStoreBusy.cs`,
  `Components/Pages/AdminSettings.razor` (`SaveProtectedPrincipals`: the compare, the busy catch, the refusal record,
  the message; `IsSecretId` use; `ChangedEntries`), `Services/ProtectedPrincipalService.cs` (`IsSecretId`),
  `Services/ExoConfigFallback.cs`, `Components/Pages/ExchangeOnlineConfig.razor` (the captions), `Services/PowerShellWarmUp.cs`,
  `Program.cs` (the warm-up call after `builder.Build()`), `ExchangeAdminWeb.Tests/PowerShellStartup.cs` (the fixture,
  `Outcome`), and the tests `SettingsStoreBusyTests.cs`, `ExoConfigFallbackTests.cs`, `PowerShellWarmUpTests.cs` on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a28v.md`, `aud-a26s.md`, `aud-a28w.md`; parents `aud-a28r.md`, `aud-a26p.md`,
  `aud-a28j.md`. ALREADY admitted and out of scope: `aud-a40` (every OTHER settings save still reports a busy store as
  "Failed to save: SQLite Error 5"), `aud-a41` (the busy wait is ~35 s not the documented 5 because no `Default
  Timeout` is set; the handlers block the circuit for the wait; the aud-a28v test waits 35 s), `aud-a28x` (the pool's
  own fallback reads on a FAILED config read; pointing the pool at `ExoConfigFallback`). Do not re-raise them. Repo
  rules: `AGENTS.md`; `.agents/repo-guidance.md` "Known Failure Classes" (1, 2, 3); `docs/ProjectConstitution.md`;
  `.agents/decisions.md` 2026-06-17 and 2026-07-02 item 1 (no background workers; one-shot start-up calls).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat a583777d`. Report whether both succeeded.

## Mandate

Hunt for observable defects. a28v: `ConfigStoreBusy.IsBusy` matches primary code 5 only - SQLITE_LOCKED (6) and the
extended busy codes (261 BUSY_RECOVERY, 517 BUSY_SNAPSHOT): does `SqliteErrorCode` carry the primary or the extended
code, and would a real busy under WAL read as 5; the busy catch's placement - is it reachable only around the compare,
and does the refusal record carry the same fields as the other refusals; the message tells the operator the changes
"are still on this page" - true after the exception path (the form state untouched). a26s: `ExoConfigFallback.Resolve`
vs what the pool connects under - the test asserts through `BorrowAsync`/`IsConfigured` with the connect seam: does
it reach the three pool reads or stop before them; `IsSecretId` digits-only with no leading zero - does "0" (a single
zero) have a meaning the lookups accepted, and is the stored value's own read still tolerant of old values; the
captions for a stored EMPTY string vs null. a28w: `PowerShellWarmUp.Start` on the thread pool, not awaited, called
after `Build()` before `Run()` - what happens on a host that is shutting down before the warm-up finishes (an
unobserved task exception, a runspace left open); three attempts "never throws" - the log line on total failure
names the shape and the last exception; the bare `PowerShell.Create()` sites the agent found - does warming
`CreateDefault` warm what they lazily open; is the warm-up a "background worker" under the 2026-06-17 decision
(one-shot vs recurring - judge from the decision's text). Tests passing for the wrong reason: the
`CircuitRevalidationTests` text scan on `Program.cs` the comment tripped; the 35 s lock test (does it hold the lock
on the SAME file the save opens).

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
