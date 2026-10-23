# Defect review request: one documented 5 s busy wait and settings saves off the circuit (aud-a41); every settings save turns a busy store into a named refusal through one helper (aud-a40); the Program.cs source guards read code not comments (aud-a42)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `bba6bb2b` or later.
- The change is the branch merged at `86df002a`: `6db3a417` (plans), `bcef5fec` (aud-a41), `00ab79a9` (aud-a40),
  `086501ad` (aud-a42), `f219f2e8` (comments only), `c4c3f83b` (record SHAs), forked from `4830230a` (far behind master
  at merge; the merge-tree was clean). Review `git diff 4830230a c4c3f83b -- . ':!.agents'`, and read the COMBINED
  `Services/Storage/SqliteConnectionFactory.cs` (`BusyTimeout`, `DefaultTimeout` set per connection, the PRAGMA),
  `Services/SettingsSaveAudit.cs` (`LogSettingsSaveException`, `StoreBusyMessage`, `StoreBusyCause`),
  `Components/Pages/AdminSettings.razor`, `Components/Pages/ExchangeOnlineConfig.razor`, `Components/Pages/ModuleConfig.razor`
  (every save: the values captured before the first store call, the `Task.Run` write, the baseline-from-what-was-written
  recount, the in-flight save wait on Module Configuration, every catch through the helper), and the tests
  `SqliteBusyWaitTests.cs`, `SettingsSaveOffCircuitTests.cs`, `SettingsStoreBusyTests.cs`, `SettingsPageHarness.cs`,
  `CircuitRevalidationTests.cs` (`ProgramCode()`) on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a41.md` (its departures: `DefaultTimeout` 5 not 1, measured; set per connection
  not in the string; the Module Configuration in-flight wait), `aud-a40.md` (its save survey table), `aud-a42.md` (its
  scan survey table); parents `aud-a28v.md`, `aud-a28r.md`, `aud-a26p.md`, `aud-a28j.md`. ALREADY admitted and out of
  scope: `aud-a66` (one module's admin-group or section-access diff applied to another module when a second module is
  opened during the directory lookup), `aud-a67` (`dirty` carried across modules; the db CLI's 35 s wait; the drain on
  the circuit; no re-entrancy guard; the stale comment; the allowlist save's busy message). Do not re-raise them. Repo
  rules: `AGENTS.md`; `.agents/repo-guidance.md` "Known Failure Classes" (1, 2, 3) and Architectural Invariant 2 (one
  shared config database; dev and prod); `docs/ProjectConstitution.md`.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 86df002a`. Report whether both succeeded.

## Mandate

Hunt for observable defects. a41: `DefaultTimeout` 5 s applied to EVERY connection the factory opens - the config
migrators run DDL at start-up: a migration step that legitimately runs longer than 5 s (a table rebuild on a large
usage database) now fails where it did not before; the jobs database's long transactions, if any; the record says
nothing holds a lock for more than milliseconds - verify for the usage database's retention sweep and the jobs
repository; `Task.Run` for the store write on a Blazor Server circuit - the lambda captures locals only (the record
says so): check each lambda for a captured page field or `this` (a race with the free circuit); the recount after
the save - a save that fails leaves the baseline where; the Module Configuration in-flight wait - a save that never
completes (the 5 s busy then the refusal - fine; a hung store) blocks every later navigation on that page instance:
is the wait bounded; `SettingsPageHarness.OnDispatcherAsync` and the off-circuit tests starting saves from another
thread - do they test the production dispatcher path or bypass it. a40: the helper's busy decision for the
two-store protected-principals save - the secret's busy after the rules saved is "refused under the secret's own
section": is `mutationAttempted: false` right when the RULES were written in the same operator action (the record
argues the secret's write was not made; confirm the rules' success record exists so the audit reconciles);
`LogSettingsSaveException` "never throws" - trace both record paths; the tripwire requiring the helper in every
listed catch - a catch added later outside the list escapes it (name the shape). a42: `RazorSyntax.CodeView`
blanking string LITERALS too - a Program.cs check that must find text inside a literal (a route string, a policy
name) now cannot: the record says none of these patterns is in a literal - verify for the Defender registration
and the db-mode dispatch checks. Tests passing for the wrong reason: `SqliteBusyWaitTests`' 4.5-9 s window on a
slow CI runner (a false pass if the retry adds exactly one more wait = ~10 s is excluded, but a false FAIL on a
runner where the native wait overshoots 9 s is a flake - name it); the busy tests holding a raw `BEGIN IMMEDIATE` on
the harness file - same journal mode as production (WAL)?

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
