# aud-a29f: the protected-principal servicer gate grants through stored values no other gate honours, and revalidation never checks them

**Severity**: MEDIUM - it needs a value in a `ProtectedServicer:` key that is not a usable group
SID (a name left by a halted or deferred SID migration, or a well-known SID written to the store
directly). When there is one, it authorizes the strongest bypass in the app - acting on protected
principals - by group NAME, and circuit revalidation never looks at it.
**Status**: Merged to master; codex group review pending; live dev check owed
**Branch**: `aud-a29cde` (worktree `agent-ad94a625885da3a95`), base `9fab2e4`
**Commit**: `9916b0e` (merged)
**Reviewer**: implementer (found while fixing aud-a29e, 2026-10-21)
**Parent**: [aud-a29e](aud-a29e.md), [aud-a29b](aud-a29b.md)

## Evidence

`Services/ProtectedPrincipalServicerService.cs` at `fe0a9ab`, `Evaluate`:

```csharp
104            var groups = _sectionAccess.GetGroupsForSection(key);
...
111            foreach (var group in groups)
112            {
113                if (string.IsNullOrWhiteSpace(group))
114                    continue;
115
116                // IsInRole against a stored SID, matching how every other section-access check
117                // compares. The Windows token carries group SIDs, so this needs no directory call.
118                if (user.IsInRole(group))
```

The comment assumes every stored value is a SID; nothing here makes it so. `IsInRole` resolves
NAMES as well as SIDs (`GroupMembershipChecker` remarks: "measured, `IsInRole("Domain Users")`
is true"), and a SID-shaped value is matched against the token's group claims directly. Every other
gate over these stores filters to usable group SIDs first, for exactly that reason (review finding
sid-1): `GroupAuthorizationHandler` (dynamic path, `UsableSidsOnly`), `ModuleAdminService.IsModuleAdmin`,
`JobAuthorizationSnapshot.Capture`. Names can be in the store: the SID migration leaves every row
as it was when it halts or the directory is unavailable (`SectionAccessSidMigration`,
"`Halted`: ... NOTHING was written; the store still holds names").

Two consequences:

1. **Name-based authorization of servicing.** In that state, a module's ordinary policy ignores a
   name-valued grant, but its servicer key still honours one - so two same-named groups in
   different domains both service protected principals, the ambiguity sid-1 removed everywhere
   else. A well-known SID stored there (`S-1-1-0`) would make everyone a servicer.
2. **Never revalidated.** `AuthorizationGroupInventory` reads stored values through
   `UsableSidsOnly` "which is how the gates read them" - true of every gate but this one. A
   servicer authorized through such a value holds nothing the revalidation compares, so removing
   them from the group in Active Directory never ends their session, the gap aud-a29e closed for
   the static lists.

## Plan (approved by the owner's EVERYTHING go)

Owner, 2026-10-21 (`.agents/decisions.md`, "Every open review follow-up is approved for
implementation"), and the coordinator's brief: "If you find a further hole in this code, fix it as
its own commit and record (aud-a29f ...)". Plan:

1. `Evaluate` considers only usable group SIDs - `GroupMembershipChecker.UsableSidsOnly(groups)`,
   the filter every other gate over `section_access` applies - so the servicer gate, the module
   gates and the revalidation inventory all read the store the same way. Values it skips are
   logged as a warning (count only), as the policy handler does.
2. Fail closed: a key holding only names or well-known SIDs denies, like an empty key.
3. Tests (`ProtectedPrincipalServicerAdminUiTests`, which already drives `Evaluate` over a real
   store): a name-valued servicer grant no longer authorizes; a well-known SID no longer authorizes;
   a usable domain SID still does, and is the group reported for the audit record.
4. Mutation at the call site, md5 before and after.
5. No page edits, no version bump (supervisor's at merge).

## Approach

Implemented as the Plan, no change of scope. `Evaluate` iterates
`GroupMembershipChecker.UsableSidsOnly(groups)` instead of the raw values (the blank-value check
it had is subsumed: a blank is not a usable SID), and logs one warning with the count when it
skipped any. The decision, the audit group it reports and every other path are unchanged.

## Files changed

- `Services/ProtectedPrincipalServicerService.cs` - the filter, the warning, the remarks.
- `ExchangeAdminWeb.Tests/ProtectedPrincipalServicerAdminUiTests.cs` - the two tests below and
  their helpers.
- `ExchangeAdminWeb.Tests/CircuitRevalidationTests.cs` - `AServicerGrant_IsConsultedLikeAnyOther`.

## Guard proof

| Test | Pins |
| --- | --- |
| `ProtectedPrincipalServicerAdminUiTests.AServicerValueThatIsNotAUsableGroupSid_DoesNotAuthorizeServicing` (a name, `S-1-1-0`, `S-1-5-32-544`) | **the finding**: each stored value matches the principal through `IsInRole` (asserted first, so the test cannot pass vacuously) and no longer authorizes servicing |
| `ProtectedPrincipalServicerAdminUiTests.AServicerValueThatIsAUsableGroupSid_StillAuthorizesServicing` | a usable domain SID beside a name still authorizes, and is the group reported for the audit record |
| `CircuitRevalidationTests.AServicerGrant_IsConsultedLikeAnyOther` | the other half: a servicer grant is compared by revalidation like any section grant, so what the gate now honours is what is re-checked |

Every pre-existing servicer test (each module's `*ServicerTests`, the protection gates) still
passes.

Mutation by the same script and procedure as aud-a29c/d/e. `ProtectedPrincipalServicerService.cs`
pre-comment md5 `8B589B104A12DF5E2A20C2B178E3461B`; restored md5-equal; the restored tree rebuilt
and passed.

| # | Mutation at the call site | md5 mutated | Named test that failed |
| --- | --- | --- | --- |
| F1 | `foreach (var group in usable)` -> `foreach (var group in groups)` - the raw values again | `94A4B45DD549D911F5B6F6108CAE12CA` | `AServicerValueThatIsNotAUsableGroupSid_DoesNotAuthorizeServicing` (all 3 cases) |

## Gates

On the final (commented) tree, on top of aud-a29c, aud-a29d and aud-a29e:

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; no warning in any file this change
  touches.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: **4647 passed,
  3 skipped (the known AD skips), 0 failed, 4650 total**; the hang collector reported "All tests
  finished running".
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD` (after staging) and `tools/Test-AsciiOnly.ps1`: see the commit.

## Known gaps

- **A servicer grant stored as a name stops working** where one exists - that is the fix, and it
  is the same rule the module's own policy already applies to that row. Where the SID migration has
  completed there are none; where it halted, the servicer grant now fails closed with the rest of
  the module's name-valued grants until the migration's reported rows are corrected, and the warning
  in the log says how many values were skipped.

## Choices the owner can veto

1. **Filter in the gate** (the servicer gate reads the store as SIDs only, like the others) rather
   than teach revalidation to translate stored names - which would have kept name-based servicing,
   the ambiguity sid-1 removed everywhere else.

## Closeout

Fixed on branch `aud-a29cde`. Pending: codex review of the commit, the index row (new finding,
found by the implementer), merge to `master` by the supervisor.
