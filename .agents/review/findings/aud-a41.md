# aud-a41: The settings store's busy wait is about 35 seconds, not the documented 5, and the settings handlers block their circuit for the whole wait

**Severity**: LOW/MEDIUM - operator experience and a wrong documented fact. `SqliteConnectionFactory`
sets `busy_timeout=5000` but no `Default Timeout`, so Microsoft.Data.Sqlite's 30-second command timeout
wraps the 5-second busy wait and a held write lock makes a save wait about 35 seconds (measured by the
aud-a28v agent: 35.2 s) before it throws SQLITE_BUSY. The factory's doc comment and the aud-a28r record
say 5 seconds. The settings handlers call the store synchronously after `await Task.Yield()`, so the
Blazor circuit is blocked for the whole wait. The aud-a28v test holds a real lock and therefore adds about
35 seconds to the suite.
**Status**: In progress (plan below); approved under the owner's 2026-10-21 "EVERYTHING" go
**Branch**: `worktree-agent-a885c578973c1cad3`
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28v/a26s/a28w agent, 2026-10-22, candidate 2 of its hand-back)

## Evidence

At `a583777d`: `Services/Storage/SqliteConnectionFactory.cs` lines 46-50 (no Default Timeout), line 77
(`busy_timeout=5000`), doc comment lines 54-56; `.agents/review/findings/aud-a28r.md` lines 52 and 168;
`Components/Pages/AdminSettings.razor` around line 1091 (the synchronous store call after `Task.Yield`);
`ExchangeAdminWeb.Tests/SettingsStoreBusyTests.cs` (the 35 s wait).

## Approach (to be written by the implementing agent before code)

- One wait, documented and enforced: set the connection's `Default Timeout` so the total busy wait equals
  the documented value (5 s; or document a deliberately chosen value and make both agree); a test pins the
  measured wait to within a tolerance of the documented one.
- The settings handlers run the store call off the circuit (`Task.Run` or the store's async surface) so
  the page stays responsive and the aud-a28v message appears when the wait ends.
- Correct the factory doc comment and the aud-a28r record's two lines; the aud-a28v busy test shrinks to
  the documented wait.
- Mutations at the call site: the `Default Timeout` removed (the pin fails); the synchronous call restored.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `6972a5c5`. One commit `fix(storage): a held write lock
is waited out once, for the documented 5 seconds, and the settings saves wait for the store off the circuit (aud-a41)`, first on
this branch (then aud-a40, aud-a42). Behaviour: the shared connection factory and the three settings pages (base app version),
and Exchange Online Config (the Exchange Online module's version); the supervisor owns both bumps.

Evidence re-read at `4830230a`, and measured rather than assumed - a throwaway console probe in the scratchpad (never
committed) against Microsoft.Data.Sqlite 10.0.7 / SQLite 3.49.1, the factory's PRAGMA batch applied as the factory applies it,
the lock held by a second unpooled connection's `BEGIN IMMEDIATE`:

- today's factory (no `Default Timeout`): `BEGIN IMMEDIATE` failed SQLITE_BUSY (code 5, extended 5) after 34.16 s (aud-a28v
  measured 35.2 s; aud-a33's mutation run 34 s);
- `Default Timeout` 5, and 1, each with `busy_timeout=5000`: 5.56 to 5.58 s, four runs each - one native busy wait, nothing added;
- the process timer at 1 ms (`timeBeginPeriod(1)`): the native wait itself 5.07 to 5.97 s, `Default Timeout` 5 and 1 alike;
- a 20-million-row recursive CTE (4.83 s) completed under `Default Timeout` 1: the provider's command timeout bounds its busy
  retry, not a statement's run time;
- a lock released after 3 s: the waiting `BEGIN IMMEDIATE` began at 3.07 s (`Default Timeout` 1 or 5); held 4.8 s: began at 5.00 s;
- a factory-style open and read while another connection holds the write lock: 5 ms, reading the committed state (a WAL reader
  never waits for a writer).

So: SQLite's busy handler waits `busy_timeout` (5 s) inside each step; Microsoft.Data.Sqlite re-steps a busy statement after
150 ms while its command timeout (`Default Timeout`, 30 s when unset) has not passed; six rounds make about 35 s.

1. **One wait (`SqliteConnectionFactory`).** A public `BusyTimeout` of 5 s; the PRAGMA is built from it (`busy_timeout=5000`, the
   value unchanged); the connection string sets `Default Timeout` to 1 second - the provider's retry ceiling, below the busy wait,
   so once SQLite's own wait has failed the provider never starts another. Not 5: the native wait's nominal total is exactly
   `busy_timeout`, so with a ceiling equal to it, whether a second 5-second wait starts would rest on Windows' sleeps overshooting
   (they did, every run - 5.07 s at the least - but that is the platform timer, not a guarantee). A busy result SQLite returns
   without waiting is retried for up to 1 s, a retry can still wait once, so the worst case is about 6 s, where a ceiling of 5
   allows 10 or more. The documented wait is 5 s (5.6 s measured on this host: the Windows sleep granularity).
   **No recorded reason for another value** was found. aud-a33's known gap (a long migration could outlast the wait) and the other
   two databases this factory serves were checked: migration steps are DDL, the SID migration's directory lookups run outside the
   lock (aud-a33b), the jobs repository's transactions are single statements, and the usage database's writes are single
   statements - nothing holds a write lock for more than milliseconds. The config, jobs and usage databases and both migrators get
   the documented wait. Architectural Invariant 2: dev and prod wait for each other's writes 5 s, not 35; a settings save meeting
   the other instance's lock for longer is refused (aud-a40 makes every save say so), and a start-up migration meeting the other
   instance's step waits up to 5 s for a step that takes milliseconds.
2. **Off the circuit.** Every settings save's store write - the calls that take the write lock and so can wait - runs through
   `await Task.Run(...)`, so the circuit is free while it waits and the save's message appears when the wait ends: Admin Settings
   `SetModulesEnabled` (SaveEnablement), `ExtendedLog.SetLevel` (SaveLogLevel), `CompareDirectoryReadSecretId`, `ReplaceConfig` and
   `SaveDirectoryReadSecretId` (SaveProtectedPrincipals; `SaveDirectoryReadSecret` becomes async); Exchange Online Config
   `SetModulesEnabled` (SaveModuleStatus) and `SaveChangedValues` (SaveExoConfig); Module Configuration `SaveChangedValues`
   (SaveConfigAsync), `SetModulesEnabled` (SaveModuleEnablementAsync), `SaveEditedAliases` and `SaveGroupDisplayNames`
   (SaveSectionAccessAsync), `ApplyAdminChanges` (SaveModuleAdminsAsync), `SaveAllowlistAndAudit` (SaveAllowlistAsync - the call
   only; `ADAttributeEditorService` is not touched). The reads before them (the corrupt re-checks) stay: measured, a read does not
   wait for a held write lock. The lambdas capture locals only. `ExoPool.DrainPool` is not a store wait and stays (Known gaps).
3. **A save writes what the page held when its first store call began, and its bookkeeping follows what it wrote.** A free
   circuit lets the admin edit while a save waits. So each save takes what it writes, and what its bookkeeping needs, from the page
   before its first store call; afterwards the page's baseline becomes what was saved and the unsaved count is recounted against it
   - the Section Access and Module Admins saves' rule ("the baseline is what was SAVED, so a row added during the lookup still
   counts as an unsaved edit"), already used where they await the directory. An edit made during the wait stays on the page,
   counted unsaved, for the next save. SaveEnablement and SaveModuleEnablementAsync: the baseline takes the toggles written, then the
   count (the reload after them then warns about an edit made during the save, rather than dropping it). SaveLogLevel: the level is
   a local, for the record and the message. SaveProtectedPrincipals: the rules and the secret are taken before the compare (the
   rules' config moves above it); the already-applied record names the entries of what was written; the lists take the rows held
   under the lock (aud-a26n) only when no edit landed during the save; the rules' baseline is what the store now holds; the secret's
   field takes the stored value only while it still holds what was saved; the section is recounted. SaveModuleStatus: the baseline
   takes the toggle written. SaveConfigAsync: the baseline takes the values written. SaveAllowlistAsync (a tally, not a diff): the
   count keeps the edits made during the save. SaveExoConfig, SaveSectionAccessAsync and SaveModuleAdminsAsync already work this way.
   No re-entrancy guard: a second save started while one waits (a double click before the disabled Save renders) is decided by the
   stores under their locks, as two tabs' saves are.
4. **Docs.** The factory's doc comments say what the wait is and why the provider's timeout is set; `ConfigStoreBusy`'s remarks lose
   "about 35 seconds"; the aud-a28v comment in SaveProtectedPrincipals; `SettingsStoreBusyTests`' remarks; aud-a28r.md lines 52 and
   168 corrected in place.
5. **Registries.** ClickGateRegistry: the three pages NotYetConverted, no line keys; nothing re-anchors (the ClickGate tests run
   anyway). ProgressRegistry: SaveExoConfig's pinned calls stay inside its activity; the exempt SaveCurrentTabAsync entries are still
   config-store writes.

Tests:
- New `ExchangeAdminWeb.Tests/SqliteBusyWaitTests.cs`: a write through `SqliteConfigStore` meeting a write lock another connection
  holds fails SQLITE_BUSY after the documented wait - at least 4.5 s and under 9 s (a second wait would pass 10; today's is 34); a
  factory connection runs with `busy_timeout` equal to `BusyTimeout` and a command timeout below it, read from the opened connection
  (deterministic); a lock released after 1 s lets the write through; a statement running past the command timeout completes.
- `SettingsStoreBusyTests`: the aud-a28v case now waits the documented wait, asserted under 9 s.
- New `ExchangeAdminWeb.Tests/SettingsSaveOffCircuitTests.cs` through `SettingsPageHarness`: each save's write held at its store's
  `BeforeWrite` (no lock, no wait); while held, an edit on the page's dispatcher completes at once (the circuit is free); released,
  the save completes, the store holds only what it wrote, and the edit is still on the page and counted unsaved where the page
  counts. One case per save above, the protected-principals save twice (its compare, its rules' write). The harness gains an action
  on the dispatcher, `RunAsync` with arguments, and the allowlist's repository behind a switchable store.

Mutations at the call site: (1) the `Default Timeout` removed - the pin fails; (2) SaveProtectedPrincipals' `ReplaceConfig` back on
the circuit (called inline) - its off-circuit case fails; extra (E1) `Default Timeout` equal to the busy wait (5) - the structural
pin fails; (E2) SaveProtectedPrincipals' rules baseline from the page's lists again - the late edit is counted saved; (E3)
SaveConfigAsync's write back on the circuit; (E4) SaveConfigAsync's baseline from the page's state again.

## Guard proof

Pending.
