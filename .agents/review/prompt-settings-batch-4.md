# Defect review request: protected-principals save compares before any write (aud-a28r), Secret ID validation, EXO fallback marking and case-sensitive dirty count (aud-a26p), the a28p/a26n leftovers (aud-a28s)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `d3f242a3` or later.
- Two merged branches. (A) `25071281`: `c8deba78` (plans), `dbcac40f` (aud-a28r), `ec2e09c3` (aud-a26p), `07ef4e8e`
  (comments only), `3e8ce52c` (record SHAs), forked from `55a111e9`: review `git diff 55a111e9 3e8ce52c`. (B) `0bcb92c0`
  (aud-a28s), its commits listed in `.agents/review/findings/aud-a28s.md`: review the diff that record names.
- Read the COMBINED `Services/ModuleConfigService.cs` (`CompareChangedValues`, `SaveChangedValues`, the shared private
  decision, the write lock), `Services/ProtectedPrincipalService.cs` (`CompareDirectoryReadSecretId`, `ReplaceConfig`),
  `Components/Pages/AdminSettings.razor` (the save path: compare, rules write, secret write, the refusal record, the
  Secret ID check, the dirty count), `Components/Pages/ExchangeOnlineConfig.razor` (the fallback marking),
  `Services/ExoConnectionPool.cs` (`GetExoConfig`), `ExchangeAdminWeb.Tests/PowerShellStartup.cs` (the assembly
  fixture) and `ExchangeAdminWeb.Tests/SettingsSaveStoreCheckTests.cs` (the aud-a28j case's changed failure point) on
  master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a28r.md`, `aud-a26p.md`, `aud-a28s.md`; parents `aud-a28p.md`, `aud-a26n.md`,
  `aud-a28j.md`, `aud-a28i.md`, `aud-a28h.md`. ALREADY admitted and out of scope: `aud-a28v` (a compare that waits out
  the 5-second busy timeout fails whole with a generic "Failed to save"), `aud-a28w` (the app's first PowerShell
  sessions could hit the start-up race the suite guards against), `aud-a26s` (the EXO page repeats the pool's
  fallback rule untested; the Secret ID check accepts "+42"/"0042"; `ChangedEntries` ignores case). Do not re-raise
  them. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Known Failure Classes" (1, 2, 3); `docs/ProjectConstitution.md`.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 25071281`. Report whether both succeeded.

## Mandate

Hunt for observable defects. a28r: the compare and the save share one private decision - can they still disagree
(the compare reads under the lock and releases it; the rules write and the secret write follow; name the window and
whether the record's "remaining window" statement is accurate and complete); does a compare refusal leave exactly one
refusal record with `mutationAttempted: false`; does an unreadable-secret-rows compare refuse (fail closed) rather
than treat the row as absent; the `ConfigSaved` event not raised by the compare (any listener that expected it);
re-entrancy - the compare takes the store's write lock from inside the page's own save path: is any other lock held
at that moment (deadlock order). a26p: the Secret ID check - blank clears, a non-numeric is refused without echo;
does the refusal reach the audit; is the check applied on the ONLY path that stores the ID; the EXO fallback marking -
is the text ever shown for a field that has a stored row (a row with an empty value); the case-sensitive dirty count -
does the save then write BOTH casings or replace; aud-a28j's test moved failure point - does the scenario it covers
still exist in production in that shape. a28s: whatever that record names, check its tests bite on the production
path. The start-up guard: an assembly fixture that "never throws" - can it hide a real start-up failure so that the
first test to open a session fails with a less useful error; does it open any live connection (it must not).
Tests passing for the wrong reason: the settings harness's store double; the `SettingsWholeProtectedSaveTests`
sequencing assertions.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
