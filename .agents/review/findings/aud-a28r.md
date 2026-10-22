# aud-a28r: A Secret ID refused under the lock leaves the protected-principals save partial

**Severity**: LOW/MEDIUM - a partial after a successful write, the aud-a2f class on a settings page.
Since aud-a26n the directory-read Secret ID is compared against the value the page loaded inside
the locked write, but the rules are written FIRST: when the secret compare refuses `ConcurrentChange`,
the rules are already saved and the secret is not. aud-a28j's folded-in part records both outcomes
honestly (a success record for the rules, a refusal for the secret, a message saying which part
landed), so nothing is silent; but the save the admin confirmed as one change lands as half.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28p/a26n agent, 2026-10-22, candidate 2 of its hand-back)

## Evidence

At the aud-a28p/a26n branch tip `a904c288`, `Components/Pages/AdminSettings.razor` `SaveProtectedPrincipals`:
`ReplaceConfig` (rules, under the lock) then the secret's `SaveChangedValues` compare; the refusal
path after the rules' success record.

## Approach (to be written by the implementing agent before code)

- Decide both inside one write where the stores allow it (the rules and the ProtectedPrincipals
  module config are two stores; if one `Write` callback can span both repositories through the shared
  `SqliteConfigStore`, do that), so a secret conflict rolls the rules back too; otherwise run the
  secret's compare (read under the lock, no write) BEFORE the rules write, refuse the whole save on a
  conflict, and write the secret after the rules only when the compare passed - accepting the
  narrower window and saying so in the record.
- Tests through `SettingsPageHarness` with two stores on one temp file: a secret changed elsewhere
  before the save refuses the whole save with nothing written (rules included); a clean save writes
  both. Mutation at the call site: write the rules before the compare again.

## Guard proof

Pending.
