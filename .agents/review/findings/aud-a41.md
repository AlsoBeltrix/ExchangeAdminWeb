# aud-a41: The settings store's busy wait is about 35 seconds, not the documented 5, and the settings handlers block their circuit for the whole wait

**Severity**: LOW/MEDIUM - operator experience and a wrong documented fact. `SqliteConnectionFactory`
sets `busy_timeout=5000` but no `Default Timeout`, so Microsoft.Data.Sqlite's 30-second command timeout
wraps the 5-second busy wait and a held write lock makes a save wait about 35 seconds (measured by the
aud-a28v agent: 35.2 s) before it throws SQLITE_BUSY. The factory's doc comment and the aud-a28r record
say 5 seconds. The settings handlers call the store synchronously after `await Task.Yield()`, so the
Blazor circuit is blocked for the whole wait. The aud-a28v test holds a real lock and therefore adds about
35 seconds to the suite.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
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

## Guard proof

Pending.
