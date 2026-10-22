# aud-a26p: Settings pages - the Secret ID field is unvalidated, fallback-sourced fields are unmarked, a casing-only edit is not dirty, and parallel PowerShell-session test classes can race on start-up

**Severity**: LOW. (1) The directory-read Secret ID field accepts any text; nothing checks it is a
Secret Server id (an integer) before it is stored, so a typo is found only when the read fails
later. (2) The Exchange Online Config page does not mark which fields show the appsettings fallback
rather than a stored value (Admin Settings now does, aud-a26n). (3) The protected-principals dirty
count compares ignoring case, so an edit that changes only casing does not count as unsaved and the
Save bar does not appear. (4) Test infrastructure: two test classes opening PowerShell sessions at
the same moment can hit PowerShell's start-up race ("An item with the same key has already been
added. Key: Alias"), seen once in `ADAttributeEditorSaveTests` during a filtered run - a flaky
failure source for the full suite.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28p/a26n agent, 2026-10-22, candidates 1, 3, 4 and 6 of its hand-back; its candidate 5, a connection change on one instance not draining the other's pool, is the scdi-1 design - each borrow compares the config generation - and is not admitted)

## Evidence

At the aud-a28p/a26n branch tip `a904c288`: `Components/Pages/AdminSettings.razor` (secret id input;
dirty count), `Components/Pages/ExchangeOnlineConfig.razor` (field rendering), the test classes that
construct `PowerShell` sessions in parallel (xUnit v3 runs classes in parallel by default).

## Approach (to be written by the implementing agent before code)

- Secret ID: validate as a positive integer at save; refuse otherwise, audited (aud-a28h helper),
  naming the field. No guess about what the admin meant.
- Fallback marking: the Exchange Online Config page shows "not set in the database; the configured
  fallback applies" on each such field, as Admin Settings does.
- Dirty count: compare ordinal (casing counts as a change), consistent with what the save writes.
- Test race: either a shared xUnit collection for every class that opens a PowerShell session (so
  they run serially), or a one-time static initialisation guard in the harness; say which and why;
  pin with a test that opens two sessions concurrently through the harness.
- Mutations at the call site: accept any text again; drop the fallback marking; compare
  case-insensitively again; remove the collection attribute from one class.

## Guard proof

Pending.
