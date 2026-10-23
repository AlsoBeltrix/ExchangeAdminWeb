# aud-a58: The Home page parses a configuration boolean during render, so a non-Boolean value breaks the Home page for every user

**Severity**: MEDIUM - availability. `Home.razor` runs `bool.Parse(Config["Email:NotifyUsersOnPermissionGrant"]
?? "false")` during render; a value such as "yes" or "1" throws `FormatException` and the Home page fails
to render for every user until the setting is corrected (the aud-a28y/a43 `bool.Parse` class, on the
landing page).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a51/a48/a49 agent, 2026-10-22, candidate 6 of its hand-back)

## Evidence

At `81809833`: `Components/Pages/Home.razor` line 39; `git grep -n "bool.Parse"` across `Components/` and
`Services/` for the same shape elsewhere (list every site in the record; fix each one the same way).

## Approach (to be written by the implementing agent before code)

- `bool.TryParse` with the default on an unparseable value and one warning naming the setting and the value,
  once per process per value (the aud-a43 shape); the page renders. Apply the same to every other
  `bool.Parse` on a configuration value the grep finds.
- Tests: a render test (the page's reflection driver or `BuildRenderTree`) with "yes"; a source check that
  no `bool.Parse(Config[` remains in pages or services. Mutation at the call site: `bool.Parse` again.

## Guard proof

Pending.
