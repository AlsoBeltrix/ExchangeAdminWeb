# aud-a29e: static groups that can authorize but cannot be revalidated are skipped silently

**Severity**: MEDIUM - it needs a particular, accepted configuration shape, but what it leaves
unrevalidated is administrative access: a circuit whose only grant is such a group is never
re-checked, not even for a disabled account.
**Status**: Closed on review - codex (a29cdef, 2026-10-21) clean, veto list upheld; live dev check owed before deploy
**Branch**: `aud-a29cde` (worktree `agent-ad94a625885da3a95`), base `9fab2e4`
**Commit**: `fe0a9ab` (merged)
**Reviewer**: codex / gpt-5.5-dzs / xhigh over 1633ad0..0d30442, 2026-10-21
**Parent**: [aud-a29b](aud-a29b.md)

## Evidence

The inventory drops static entries that are not usable domain SIDs
(`Authorization/AuthorizationGroupInventory.cs` at `9fab2e4`):

```csharp
68            // SID-shaped but not a usable group (a well-known or BUILTIN SID): nothing to translate.
69            if (value.TrimStart().StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
70                continue;
71
72            var translated = _directory.TranslateGroupName(value);
73            if (SectionAccessGroupIdentity.IsUsableGroupSid(translated))
74                sids.Add(translated!);
```

The policy handler does not filter the static lists (`Authorization/GroupAuthorizationHandler.cs`):

```csharp
101        var usableGroups = requirement.ResolveDynamically
102            ? GroupMembershipChecker.UsableSidsOnly(groups)
103            : groups.Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
...
117        if (GroupMembershipChecker.IsMemberOfAny(groupClaims, usableGroups))
...
129            if (user.IsInRole(allowedGroup))
```

So `Security:AdminGroups = ["BUILTIN\\Administrators"]` (or `"S-1-5-32-544"`) authorizes
every Admin Settings policy for a token that carries S-1-5-32-544 - `IsInRole` translates the
name to that SID and tests the token, and the claims match compares the SID string directly -
while the inventory drops it: `IsUsableGroupSid` refuses well-known and BUILTIN SIDs because
`tokenGroups` read from the directory cannot attest to them (the server adds local aliases to
the token itself). A circuit holding only that grant has nothing in `held`, so revalidation
returns valid without a lookup at every check - not even a disabled account ends it.

## Plan (approved by the owner's EVERYTHING go)

Owner, 2026-10-21 (`.agents/decisions.md`, "Every open review follow-up is approved for
implementation"): "... EVERYTHING." Plan, as briefed by the coordinator:

1. **Refuse at startup.** When revalidation is enabled, every `Security:AllowedGroups` and
   `Security:AdminGroups` entry is classified before the app is built: a usable domain group SID,
   or a name that translates (the LSA translation `IsInRole` uses) to one, passes; a SID, or a
   name translating to a SID, that can be parsed but is not a usable domain group (BUILTIN,
   well-known, a bare domain SID) refuses startup with a message naming the key, the entry, what
   it resolves to and the remedy - use a domain group, or switch revalidation off with
   `Authentication:RevalidationEnabled = false`. A name that maps to nothing grants nothing and
   passes. With the switch off, nothing is checked.
2. **No silent exemption at run time either.** A translation that fails at startup cannot be
   classified there; startup logs it and continues, because the run-time check already fails
   closed on a translation failure. If such an entry (or any static entry, should a name come to
   translate differently) resolves to a non-domain SID at run time, the inventory no longer drops
   it: it is reported as not revalidatable, and a circuit whose token carries that SID is revoked
   (`LookupFailed`, audited, the entry's key in the detail - no group name).
3. **README** beside the switch: the rule, the refused shapes, the remedy.
4. Tests: such a configuration fails startup validation and the message names the entry; a
   domain-SID configuration passes; the disabled-switch path still starts (and translates
   nothing); a translation failure at startup is reported, not fatal; at run time a token carrying
   a non-revalidatable static SID is revoked and one that does not is unaffected; a Program.cs
   tripwire for the startup call. Mutation at each call site, md5 before and after.
5. No `.razor` page edits, no version bump.

## Approach

Implemented as the Plan, no change of scope.

- **Startup.** `CircuitRevalidationSettings.EnsureStaticGroupsCanBeRevalidated(enabled,
  allowedGroups, adminGroups, translateName)`: off -> nothing translated, nothing refused. On ->
  each non-blank entry, trimmed: SID-shaped and unparseable -> passes (grants nothing); SID-shaped
  and parseable -> that SID; a name -> `translateName` (null passes - grants nothing; a
  `DirectoryUnavailableException` becomes a returned warning). The SID then passes only if
  `SectionAccessGroupIdentity.SidRejectionReason` is null; otherwise `InvalidOperationException`:
  `Security:AdminGroups contains 'BUILTIN\Administrators', which resolves to S-1-5-32-544: a
  well-known SID, which grants more than a specific group. Windows can grant access through it, but
  session revalidation cannot re-check it against Active Directory. Use a domain group instead, or
  set Authentication:RevalidationEnabled to false to switch revalidation off (README 'Session
  Revalidation').`
- **`Program.cs`** calls it right after the switch is resolved, inside the existing block whose
  `InvalidOperationException` is logged `Fatal` and rethrown, before `builder.Build()`, with
  `allowedGroups`/`adminGroups` (the same configuration values the policies are built from) and the
  production LSA translation (`new OperatorMembershipDirectory().TranslateGroupName`, the
  translation `IsInRole` makes). Warnings go to the log.
- **Run time.** `AuthorizationGroupInventory.ResolveStaticGroups` no longer drops what it cannot
  use: a static value that resolves to a SID that is not a usable domain group (a SID literal, or a
  name translating to one) goes to the new `AuthorizationInventory.Unrevalidatable` as
  (setting, SID). The revalidator, after the grant-removal check, revokes with `LookupFailed` when
  the token carries any of them - before any directory read; the detail names the setting
  (`Security:AdminGroups`), never the group. A token that does not carry it is unaffected.
- README "Session Revalidation": the rule, beside the off-switch.

## Files changed

- `Authorization/CircuitRevalidationSettings.cs` - `EnsureStaticGroupsCanBeRevalidated`.
- `Program.cs` - the startup call (inside the existing fatal block).
- `Authorization/AuthorizationGroupInventory.cs` - `Unrevalidatable`; static values resolved
  rather than dropped.
- `Authorization/CircuitMembershipRevalidator.cs` - the fail-closed check.
- `ExchangeAdminWeb.Tests/CircuitRevalidationTests.cs` - the tests below.
- `README.md` - one bullet. `.agents/review/findings/aud-a29b.md` - veto item 7 amended again.

## Guard proof

| Test (`CircuitRevalidationTests`) | Pins |
| --- | --- |
| `AStaticGroupThatCannotBeRevalidated_RefusesStartupNamingTheEntry` (5 cases: `S-1-5-32-544`, `BUILTIN\Administrators` -> S-1-5-32-544, `Everyone` -> S-1-1-0, `S-1-5-11`, a bare domain SID) | **the finding**: each refuses startup; the message names the setting and the entry and gives the remedy (a domain group, or the off-switch key) |
| `DomainStaticGroups_PassStartupValidation` | a domain SID, names translating to domain SIDs, an unmapped name, a blank and an unparseable SID-shaped value all pass - no refusal, no warning |
| `TheDisabledSwitch_SkipsStaticGroupValidation` | the disabled-switch path still starts: nothing refused, nothing translated |
| `AStaticNameThatCannotBeTranslatedAtStartup_IsReportedNotFatal` | a failed translation is one warning naming the entry, not a refusal |
| `ProgramCs_RefusesStaticGroupsThatCannotBeRevalidatedBeforeTheAppIsBuilt` | the call is in `Program.cs` with these arguments, after the switch is resolved, inside the block that logs `Fatal` and rethrows, before `builder.Build()` |
| `AStaticGroupThatCannotBeRevalidated_FailsClosedForATokenThatCarriesIt` (SID literal; name translating to it) | run time, no silent exemption: a token carrying it is revoked `LookupFailed` without a directory read; the audit detail names `Security:AdminGroups` and not the group |
| `AStaticGroupThatCannotBeRevalidated_LeavesATokenWithoutItAlone` | a token that does not carry it is checked as before and stays valid |
| `TheInventoryHoldsOnlyUsableDomainSids` (extended) | the consulted SIDs are unchanged; the two static non-domain SIDs are now in `Unrevalidatable` (and the stored ones, which grant nothing, are not) |

Every pre-existing test in both classes still passes.

Same mutation script and procedure as aud-a29c and aud-a29d; every restore md5-equal; the
restored tree rebuilt and both classes passed 120/120. Pre-comment md5s: settings
`CE1E917EF0C06B6F40ED6D8605CC7214`, `Program.cs` `4EAA1780828DCF75A8C9EB46D7DE8A44`, inventory
`04EC8B686F4E596AE544794D6D84B67D`, revalidator `65C9A630555A72F6FB890C38AA039466`.

| # | Mutation at the call site (file) | md5 mutated | Named tests that failed |
| --- | --- | --- | --- |
| E1 | the refusal never fires: `rejection is null \|\| rejection is not null` (settings) | `EA87D439256ED90E7058A4B1B5B0AF07` | `AStaticGroupThatCannotBeRevalidated_RefusesStartupNamingTheEntry` (all 5 cases) |
| E2 | the off-switch no longer skips the check (settings) | `1C8954C6F5CD6C055E4E36281962A374` | `TheDisabledSwitch_SkipsStaticGroupValidation` |
| E3 | names not translated (settings) | `A1A6E216E393858EF1CD81CC98467028` | the `BUILTIN\Administrators` and `Everyone` cases, `AStaticNameThatCannotBeTranslatedAtStartup_IsReportedNotFatal` (3) |
| E4 | a failed translation is fatal instead of a warning (settings) | `15F141F3E32148A1476658B483D16AE3` | `AStaticNameThatCannotBeTranslatedAtStartup_IsReportedNotFatal` |
| E5 | an unparseable SID-shaped value is refused too (settings) | `09562A75083F39719568D46B29EA66BF` | `DomainStaticGroups_PassStartupValidation` |
| E6 | the startup call removed (`Program.cs`) | `E06CA2FB8B9EE8DB0C43E11D5BBE56CE` | `ProgramCs_RefusesStaticGroupsThatCannotBeRevalidatedBeforeTheAppIsBuilt` |
| E7 | non-domain static SIDs dropped again (inventory) | `BB10C28639DBA4A4F12EA0A3B2374064` | `AStaticGroupThatCannotBeRevalidated_FailsClosedForATokenThatCarriesIt` (both), `TheInventoryHoldsOnlyUsableDomainSids` (3) |
| E8 | the revalidator ignores them (revalidator) | `C163E7693CCA9B24430FB01D582C0D3C` | `AStaticGroupThatCannotBeRevalidated_FailsClosedForATokenThatCarriesIt` (both) |

E6 is a source tripwire: it proves the call is present and placed; that the call refuses is
proved by the settings tests.

## Gates

On the final (commented) tree, on top of aud-a29c and aud-a29d:

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; no warning in any file this change
  touches.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: **4642 passed,
  3 skipped (the known AD skips), 0 failed, 4645 total**; the hang collector reported "All tests
  finished running".
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD` (after staging) and `tools/Test-AsciiOnly.ps1`: see the commit.

Not run: anything against a live directory or a deployed `appsettings.json` (by instruction).

## Known gaps

- **Not run against a real `Security:*` configuration.** Before this is deployed, the instances'
  `Security:AllowedGroups` and `Security:AdminGroups` should be checked for built-in or well-known
  entries: with revalidation on, such an entry now stops the app at startup (the error names it).
  The refusal is the point; a surprise at deploy time is not.
- **A local group of the web server is not detected.** A server-local group's SID has the shape
  of a domain account SID (`S-1-5-21-<machine>-<rid>`), which `IsUsableGroupSid` cannot tell from
  a directory group, so it is consulted like one. `tokenGroups` from the directory does not carry
  it, so a session holding it is ended at its first check (`MembershipChanged`) - closed and loud,
  not silent, but not refused at startup either. Telling the two apart needs the machine's own
  account-domain SID.
- **Stored values are out of scope here**: a well-known SID or a name in `section_access` or the
  module-admin store grants nothing through the gates that read those stores as SIDs only. One
  gate did not - the protected-principal servicer gate - which is aud-a29f, the next commit on
  this branch.

## Choices the owner can veto

1. **Refuse startup** rather than revoke at run time: a misconfiguration is named once, before
   anything is served, instead of ending the affected operators' sessions at every check.
2. **A failed translation at startup warns, not refuses** - the run-time check fails closed on it
   anyway, and refusing would make an unreachable trust at startup an outage.
3. **The run-time backstop revokes as `LookupFailed`** (no new reason), naming the setting in the
   audit detail.
4. **Unparseable SID-shaped entries and names that map to nothing pass**, because they grant
   nothing.

## Closeout

Fixed on branch `aud-a29cde` under the owner's 2026-10-21 go. Pending: codex review of the
commit, merge to `master` by the supervisor (with the base version bump and the index row), and
the configuration check in the first known gap before the next deploy.
