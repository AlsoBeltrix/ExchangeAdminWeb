# aud-a29h: the revalidation off-switch warning says a removed operator keeps access "until it is reloaded", which is not what happens

**Severity**: LOW - wording that misleads during an incident. `Authorization/MembershipRevalidatingAuthenticationStateProvider.cs` about 238-240 and 247-249, and `README.md` about 873, say that with the off-switch set a removed operator keeps access "until it is reloaded". Under Kerberos a reload keeps the stale access until the ticket renews, and with the switch off page requests are not checked either (aud-a29g). Someone switching revalidation off during an incident would wrongly expect a reload to cut a removed operator off.
**Status**: Verified on branch; awaiting merge - admitted 2026-10-23 evening from the aud-a29g agent's hand-back; inside the released scope
**Branch**: `fix/a29g` (worktree `ExchangeAdminWeb-wt/a29g`), continuing from `2678735e`
**Commit**: the `fix(aud-a29h)` commit on `fix/a29g` that carries this record
**Reviewer**: implementer (aud-a29g agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `a5d12cc8`, lines as above. Trigger: the operator reads the startup warning or README while deciding to flip the switch. Predicted failure: a wrong operational expectation; no code path changes.

## Approach

- Reword both the warning and the README sentence to say what is true: with the switch off, nothing re-checks group membership - not a reload, not a page request - until the operator's Kerberos ticket is renewed or the app restarts. A test pins the warning text as the aud-a29b tests pin its presence.
- Mutation: the old wording restored.

### Implementer's approach (written before code, at `2678735e`)

The wording, quoted. `Authorization/MembershipRevalidatingAuthenticationStateProvider.cs`, the
startup warning and the audit line's detail:

```csharp
238        logger.LogWarning(
239            "Revocation revalidation is disabled by configuration ({Key} = false): an operator removed "
240            + "from an AD group, or disabled, keeps their access on any tab already open until it is reloaded",
...
248                errorDetail: "Revocation revalidation is disabled by configuration. An operator removed from "
249                    + "an AD group, or disabled, keeps their access on any tab already open until it is reloaded.",
```

`README.md`, the off-switch bullet:

```markdown
872  - **Off-switch:** `RevalidationEnabled` is `true` when absent. Setting it to `false` (and
873    restarting) stops every re-check and every revocation - removed or disabled operators keep their
874    open tabs until they reload - so use it only while a wrong revocation is being fixed. It is never
```

No other shipped text carries the claim (searched `.cs`, `.razor`, `.md`, `.json`, `.sample`, `.ps1`;
the remaining hits are history in older review records).

What is true with the switch off: `ValidateAsync` returns valid before any lookup, the gate holds
nothing, and the page-request check (aud-a29g) calls `next` before reading anything - so nothing
re-checks group membership against the directory, for an open tab, a reload or a page request. What
the operator holds is what their Windows sign-in carries: under Kerberos a reload, or any new page,
brings back the ticket's groups until the ticket is renewed or expires, and a tab already open keeps
what it initialized with until its circuit ends - the tab closed, or the app restarted.

One refinement of the brief's sentence, stated rather than silently taken: "until ... the app
restarts" is true of a tab that is already open (a restart ends its circuit), not of the operator's
access as a whole - after a restart with the switch still off, a reload still brings back the
ticket's groups. So the new text names both ends, each for what it ends. New text, one constant
used by both lines so they cannot drift apart (`DisabledConsequence`):

> Nothing re-checks an operator's group membership against the directory - not an open tab, not a
> reload, not a page request. An operator removed from an AD group, or disabled, keeps the access
> their Windows sign-in carries: under Kerberos every reload and page request brings back the same
> groups until their ticket is renewed or expires, and a tab already open keeps its access until it
> is closed or the app restarts.

The warning is "Revocation revalidation is disabled by configuration ({Key} = false). " followed by
that; the audit detail is "Revocation revalidation is disabled by configuration. " followed by that.
Both keep the phrases the existing aud-a29b test checks. The README bullet says the same in prose.

Files (declared): `Authorization/MembershipRevalidatingAuthenticationStateProvider.cs` (the constant,
the two lines), `README.md` (the bullet), `ExchangeAdminWeb.Tests/CircuitRevalidationTests.cs` (two
tests, beside the off-switch tests), this record. No behaviour changes, so no version bump either
way; nothing else touched.

Guard: `DisabledRevalidation_SaysThatNothingReChecksMembership_NotEvenAReload` pins both texts word
for word with the sentence written out in the test (not read from the constant, so a change to the
constant cannot pass it); `Readme_SaysTheOffSwitchLeavesEveryReloadAndPageRequestUnchecked` pins the
README the way `SettingsStoreTextTests` pins its README text (the old phrase absent, the new one
present, whitespace collapsed). Mutations: the provider file restored to its `2678735e` wording, and
the README restored to its `2678735e` wording, each run against its test.

## Files changed

- `Authorization/MembershipRevalidatingAuthenticationStateProvider.cs` - `DisabledConsequence`; the
  warning and the audit detail use it.
- `README.md` - the off-switch bullet.
- `ExchangeAdminWeb.Tests/CircuitRevalidationTests.cs` - the two tests below.
- this record.

## Guard proof

As the approach, no change of scope. Nothing reached a directory or a deployed configuration.

| Test (`CircuitRevalidationTests`) | Pins |
| --- | --- |
| `DisabledRevalidation_SaysThatNothingReChecksMembership_NotEvenAReload` | **the finding**: the startup warning and the audit line's detail, each word for word against the sentence written out in the test |
| `Readme_SaysTheOffSwitchLeavesEveryReloadAndPageRequestUnchecked` | the README bullet: "until they reload" is gone, the new sentence is there (whitespace collapsed) |
| `DisabledRevalidation_IsReportedAtStartupWithOneWarningAndOneAuditLine` (unchanged) | still one warning naming the key, one audit line with every field |

Mutations, "the old wording restored": the file put back to its exact bytes at `2678735e` (`git
cat-file blob`; both files are `eol=lf`, so that is what a checkout gives), md5 taken, `dotnet build
ExchangeAdminWeb.Tests.csproj -c Release`, `CircuitRevalidationTests` and `PageRequestRevalidationTests`
(121 cases) run; then the final bytes restored, a fresh mtime set, the md5 re-taken (asserted equal),
rebuilt and run again. Script `a29h-mutate.ps1` (session scratchpad).

| # | Mutation (file) | md5 final / mutant | Failed with it | Restored |
| --- | --- | --- | --- | --- |
| H1 | the warning and audit wording as at `2678735e` (provider) | `1F052FE689C38DE538AB45B30A646BF4` / `FA3BFCF9EB51F998F40925CC1210EBEC` | `DisabledRevalidation_SaysThatNothingReChecksMembership_NotEvenAReload` (1 of 121) | md5-equal, 121/121 |
| H2 | the off-switch bullet as at `2678735e` (`README.md`) | `AD6762C2840FF03E5C1E1D7280290E5A` / `76C5C37255B741BBC7589173A2685762` | `Readme_SaysTheOffSwitchLeavesEveryReloadAndPageRequestUnchecked` (1 of 121) | md5-equal, 121/121 |

Test file final md5 `86C72800269F180C0B87B24125328A65`.

Gates, on the final tree:

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; no warning in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m -- xUnit.MaxParallelThreads=4`:
  **6713 passed, 0 failed, 0 skipped, 6713 total** (5 m 49 s); nothing hung.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD` (after staging): clean. `tools/Test-AsciiOnly.ps1`: passed.

## Coder dispute

None. One refinement of the brief's sentence, recorded in the approach: "or the app restarts" is said
of a tab already open, not of the operator's access as a whole.

## Known gaps

- **Kerberos only, by design of the sentence.** Under NTLM a new connection is authenticated against
  a domain controller and so carries current groups; the text makes no claim about NTLM.
- **Not seen on a deployed instance.** The warning appears only when the switch is set, which
  nothing here did outside the tests. Wording only: no code path changes.
- **The appsettings sample note** ("false stops every re-check and revocation until it is set back")
  was already true and is unchanged.

## Closeout

Pending: review, and merge to `master` by the supervisor. No version bump: no behaviour changes.