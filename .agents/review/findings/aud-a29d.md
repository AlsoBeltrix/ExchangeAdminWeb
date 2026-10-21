# aud-a29d: removing a grant from the store drops it from the compared set, so a page already open through it is never revoked

**Severity**: HIGH - an administrator removes a group from a module's section access (or from
its module admins) while an operator has the module open through that grant; the operator's open
page keeps working, and its read handlers keep returning data, for as long as the tab stays open.
**Status**: Closed on review - codex (a29cdef, 2026-10-21) clean, veto list upheld; live dev check owed before deploy
**Branch**: `aud-a29cde` (worktree `agent-ad94a625885da3a95`), base `9fab2e4`
**Commit**: `80eacbf` (merged)
**Reviewer**: codex / gpt-5.5-dzs / xhigh over 1633ad0..0d30442, 2026-10-21
**Parent**: [aud-a29b](aud-a29b.md)

## Evidence

`Authorization/CircuitMembershipRevalidator.cs` at `9fab2e4`:

```csharp
121        var consulted = _inventory.GetConsultedSids();
122        var tokenGroups = new HashSet<string>(GroupMembershipChecker.ExtractGroupClaims(user), StringComparer.OrdinalIgnoreCase);
123        var held = consulted
124            .Where(tokenGroups.Contains)
...
129        if (held.Count == 0)
130            return MembershipRevalidationResult.Valid("The principal holds no group an authorization gate consults.");
```

`consulted` is the store as it is NOW. The inventory says why, in its remarks
(`Authorization/AuthorizationGroupInventory.cs:11-12`): "one removed is no longer this check's
concern (the gate itself has stopped honouring it)". The gate has stopped honouring it for the
next authorization. A page that already authorized does not authorize again: Risky Users checks
its policy once at initialization (`Components/Pages/RiskyUsers.razor:569`) and its lookup,
list and history handlers (`:628`, `:685`, `:747`) call the service without asking again. So:

1. Operator in group G opens Risky Users; the section grants it to G.
2. An administrator removes G from the section.
3. At the next check G is not in `consulted`, so it is not in `held`; if the operator holds no
   other current grant, the check returns valid **without a directory read**, and the open page
   keeps answering.

Two narrower shapes of the same defect, found while tracing it:

- **The compared set is SIDs, not grants.** If G is removed from Risky Users but still granted
  in another section, G stays in `consulted` and nothing at all is noticed, although the grant the
  open page came through is gone.
- **An unreadable store reads as "nothing granted".** `SectionAccessService.GetSectionAccess`
  and `ModuleAdminService.GetAllGrantedGroups` return an empty map when the store cannot be read
  (right for the gates: no grant denies). Here it empties `consulted`, so every circuit passes
  without a lookup for as long as the store is unreadable - the opposite of fail closed.

## Plan (approved by the owner's EVERYTHING go)

Owner, 2026-10-21 (`.agents/decisions.md`, "Every open review follow-up is approved for
implementation"): "... EVERYTHING." Plan, as briefed by the coordinator and widened to the two
shapes above:

1. **Grants, not SIDs.** The inventory returns each grant as a (source, SID) pair:
   `SectionAccess:<key>` for every `section_access` value (servicer keys included),
   `ModuleAdmins:<module>` for every module-admin grant, `Security:AllowedGroups` and
   `Security:AdminGroups` for the static lists, and - only while section access is unconfigured -
   `SectionAccess:*` for the `Security:AllowedGroups` fallback `GetGroupsForSection` applies then.
   Usable domain SIDs only, as before; compared case-insensitively, never by name.
2. **An unreadable store is a lookup failure.** New `SectionAccessService.TryGetSectionAccess`
   and `ModuleAdminService.TryGetAllGrants` say when the read failed; the inventory throws and the
   check revokes with `LookupFailed`. `GetAllGrantedGroups` (only the inventory used it) goes.
3. **Capture.** The provider (one per circuit) keeps the grants its circuit was confirmed
   through: the first check - the circuit-open check - captures every consulted grant whose SID
   the circuit's token holds; every later valid check adds the grants it confirmed, so a grant
   added later is watched from the first check after it appears. The set only grows.
4. **Each check:** (a) a captured grant no longer in the inventory revokes the circuit with a new
   reason, `GrantRemoved`, audited through the existing `SessionRevalidation` line with the grants'
   SIDs in `removedGroupSids` (no names), without a directory read; (b) the directory comparison
   covers the captured SIDs as well as the SIDs held now, so a captured grant whose group a
   reconnect's token no longer carries is still checked; (c) nothing captured and nothing held is
   still valid without a lookup.
5. The off-switch is unchanged: no capture, no revocation.
6. Tests: a grant removed from the store revokes the open circuit at the next check with no
   directory change; removing a grant the operator never held does not; a grant removed from one
   section revokes though the group stays granted in another; a grant added later is captured and
   its removal seen; a module-admin grant removal revokes; the captured set does not shrink when a
   later token stops carrying a group; the end of the unconfigured fallback revokes the circuits
   that rode it; an unreadable store revokes. Mutation at each call site, md5 before and after.
7. No `.razor` page edits, no version bump.

## Approach

Implemented as the Plan, no change of scope.

- **Grants.** `AuthorizationGrant(Source, Sid)` (equal regardless of case in both parts) and the
  snapshot `AuthorizationInventory(Grants)`. `AuthorizationGroupInventory.Read()` builds it:
  `SectionAccess:<key>` per stored section (servicer keys included), `ModuleAdmins:<module>`,
  `Security:AllowedGroups`, `Security:AdminGroups`, and `SectionAccess:*` for the AllowedGroups
  fallback while section access is unconfigured. Usable SIDs only, names translated as before.
  `GetConsultedSids()` stays, as the SID view of `Read()`.
- **Reads that can fail.** `SectionAccessService.TryGetSectionAccess(out map, out configured)`
  goes through the same read the gates use - `ReadSectionAccess` now wraps
  `ReadSectionAccessReportingFailure`, whose only difference is the third value - and
  `ModuleAdminService.TryGetAllGrants` through the existing `TryReadConfig`. A false makes `Read()`
  throw; the revalidator's existing catch turns it into `LookupFailed`. `GetAllGrantedGroups`
  (aud-a29b's, only the inventory used it) is gone.
- **The check** (`CircuitMembershipRevalidator.RevalidateAsync(user, captured, ct)`): read the
  inventory; captured grants no longer in it -> `GrantRemoved`, `removedGroupSids` = their SIDs,
  no directory read; otherwise compare the SIDs of captured grants plus inventory grants the token
  holds - none: valid without a lookup, as before - with the directory exactly as before
  (disabled, removed group, every failure shape). A valid verdict carries that union as `Grants`.
- **The capture** (`MembershipRevalidatingAuthenticationStateProvider`, one per circuit): a set
  under a lock, passed to every check as a copy, unioned with `Grants` on a valid verdict, never
  reduced. The first check of a circuit is the open check (aud-a29c), so capture starts there.
- `CircuitRevocationReason.GrantRemoved` and its plain message - it says the change was in the
  app's settings, not in Active Directory, and names no group. The audit line is the existing one.
- README "Session Revalidation": the access settings are checked too.

## Files changed

- `Authorization/AuthorizationGroupInventory.cs` - `Read()`, `AuthorizationGrant`,
  `AuthorizationInventory`, the sources.
- `Authorization/CircuitMembershipRevalidator.cs` - the captured grants in, `GrantRemoved`, the
  wider comparison, `MembershipRevalidationResult.Grants`.
- `Authorization/MembershipRevalidatingAuthenticationStateProvider.cs` - the per-circuit capture.
- `Authorization/CircuitRevocation.cs` - `GrantRemoved` and its message.
- `Services/SectionAccessService.cs` - `TryGetSectionAccess`; the read reports its failure.
- `Services/ModuleAdminService.cs` - `TryGetAllGrants` replaces `GetAllGrantedGroups`.
- `ExchangeAdminWeb.Tests/CircuitRevalidationTests.cs` - the tests below; harness `Store`,
  `DropTable`.
- `README.md` - one bullet. `.agents/review/findings/aud-a29b.md` - veto item 7 amended.

## Guard proof

Nothing reached a directory (NSubstitute fake); the stores are the real services over a
throwaway SQLite file, and "unreadable" is a dropped table.

| Test (`CircuitRevalidationTests`) | Pins |
| --- | --- |
| `AGrantRemovedFromTheStore_RevokesTheOpenCircuitWithoutADirectoryChange` | **the finding**: open check captures `SectionAccess:RiskyUsers`; the group is taken out of the section; the next check revokes `GrantRemoved` with the SID in `removedGroupSids`, trigger `Interval`, and no directory read |
| `RemovingAGrantTheOperatorNeverHeld_DoesNotRevoke` | unrelated grant removal does not revoke |
| `AGrantRemovedFromOneSection_RevokesWhileTheGroupStaysGrantedInAnother` | grants are (setting, SID): the group still granted elsewhere does not hide the removal |
| `AGrantAddedWhileTheCircuitIsOpen_IsCapturedSoItsRemovalIsSeen` | growth: a grant added later is captured at the next valid check, and its removal then revokes |
| `AModuleAdminGrantRemoved_RevokesTheOpenCircuit` | module-admin grants are captured and watched the same way |
| `TheCapturedGrantsNeverShrink_AndStayComparedWhenALaterTokenStopsCarryingTheGroup` | the compared set never shrinks: a reconnect token without the group still gets the directory read (2 reads) and keeps the grant captured; when the directory drops the group, `MembershipChanged` |
| `ConfiguringSectionAccessForTheFirstTime_RevokesTheCircuitsThatRodeTheAllowedGroupsFallback` | the fallback is a grant of its own (`SectionAccess:*`), and the first save of section access ends it |
| `AGrantStoreThatCannotBeRead_IsALookupFailure` (`section_access`, `module_admins`) | an unreadable store revokes with `LookupFailed` ("could not be read"), even for a circuit with nothing captured |
| `EveryReasonHasItsOwnPlainMessage` (unchanged) | covers the new reason |

Every pre-existing test in both classes still passes.

Same mutation script and procedure as aud-a29c (exact-text replacement asserted to match once,
md5, `dotnet build ExchangeAdminWeb.Tests.csproj -c Release`, the `CircuitRevalidationTests` class
run, restore, touch, md5 re-taken). Every restore was md5-equal; the restored tree rebuilt and both
classes passed 108/108. Pre-comment md5s: revalidator `9641E6D5A096CC92AFD1C220035855D8`, provider
`A4724803A7A1F642CA6B59B8079BAB6D`, inventory `FD7D86CBC28E3D242C85F6C5CC6E973C`,
`SectionAccessService` `2BC9FF892D8762D4A6A66FA58882FD57`, `ModuleAdminService`
`F46DC6EFC613E9391CF72B806FF5AC8D`, revocation `EEEE66BC85E9599706C7643FD5281E52`.

| # | Mutation at the call site (file) | md5 mutated | Named tests that failed |
| --- | --- | --- | --- |
| D1 | `withdrawn.Count > 0` -> `< 0` (revalidator) | `821BF3E3438D744602E516301374F243` | `AGrantRemovedFromTheStore_...`, `AModuleAdminGrantRemoved_...`, `ConfiguringSectionAccessForTheFirstTime_...`, `AGrantRemovedFromOneSection_...`, `AGrantAddedWhileTheCircuitIsOpen_...` (5) |
| D2 | the comparison starts from nothing instead of the captured grants (revalidator) | `6EB54BD9B3DA95D3DF22F1015D764B4C` | `TheCapturedGrantsNeverShrink_...` |
| D3 | a valid verdict's grants are not added to the capture (provider) | `B33B6B607C82E0AF668CCEE866E19204` | the five of D1 and `TheCapturedGrantsNeverShrink_...` (6) |
| D4 | the provider passes an empty capture (provider) | `353D61A5D57277886DD43A74D215217A` | the same six |
| D5 | the source loses its section key - grants by SID only (inventory) | `02E4956E839245EE971CB2E331660924` | `AGrantRemovedFromOneSection_...`, `TheCapturedGrantsNeverShrink_...`, `AGrantAddedWhileTheCircuitIsOpen_...` (3) |
| D6 | no `SectionAccess:*` grant while unconfigured (inventory) | `6735756F3953990A826BC86A1F75E6D1` | `ConfiguringSectionAccessForTheFirstTime_...` |
| D7 | an unreadable section-access store does not throw (inventory) | `745C2A45A5BDA9642DB99A80C5DE8E27` | `AGrantStoreThatCannotBeRead_IsALookupFailure(section_access)` |
| D8 | an unreadable module-admin store does not throw (inventory) | `2D321AA435C314724DAE09FFBB031C0A` | `AGrantStoreThatCannotBeRead_IsALookupFailure(module_admins)` |
| D9 | the section-access read reports an unreadable store as readable (`SectionAccessService`) | `D7DDA2A5A990D05246EA5388FA2DF3D9` | `AGrantStoreThatCannotBeRead_IsALookupFailure(section_access)` |
| D10 | `TryGetAllGrants` always reports readable (`ModuleAdminService`) | `9A8632205C78DC6A1E48C282386DAF97` | `AGrantStoreThatCannotBeRead_IsALookupFailure(module_admins)` |
| D11 | `GrantRemoved` loses its own message (revocation) | `4F42311098210CDBA5749B4FC59B391E` | `EveryReasonHasItsOwnPlainMessage` |
| D12 | the removed grants' SIDs left out of the verdict (revalidator) | `5549381920D4C2C016933E49AD15A251` | `AGrantRemovedFromTheStore_...`, `AGrantAddedWhileTheCircuitIsOpen_...` |

## Gates

On the final (commented) tree, on top of aud-a29c:

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; no warning in any file this change
  touches.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: **4630 passed,
  3 skipped (the known AD skips), 0 failed, 4633 total**; the hang collector reported "All tests
  finished running".
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD` (after staging) and `tools/Test-AsciiOnly.ps1`: see the commit.

Not run: anything against a live directory (by instruction).

## Known gaps

- **Any removed grant ends the session, used or not.** Which grant an open page came through is
  not known - pages are interactive islands that authorized once - so the circuit is held to every
  grant its groups had. Taking a group out of one module's section access ends the open sessions
  of everyone in that group, even those using another module; they reload and carry on with what
  they still have.
- **A grant that comes and goes between two checks.** A grant added after a circuit opened is
  captured at the next check (within one interval). If it is removed again before then, and in
  between the operator opened a page through it - an enhanced navigation adds the page to the
  circuit already open - this circuit never captured it and never sees the removal. The same is
  true of a grant removed in the second between a page request and its circuit's open check.
  Closing it means capturing when a page is added to a circuit, which the gate cannot see today
  (an inbound activity does not say what it is).
- **An unreadable store ends sessions.** Every circuit holding a consulted group fails its next
  check while the config store cannot be read - fail closed, as the gates themselves deny then.
- **The first section-access save on an unconfigured store** ends, once, every session that was
  riding the `Security:AllowedGroups` fallback.

## Choices the owner can veto

1. **Grants are (setting, SID) pairs**, so a group removed from one section while it stays in
   another still counts as a removal.
2. **Any captured grant that disappears revokes**, even when the operator still holds other
   grants, for the reason in the first gap.
3. **`GrantRemoved` needs no directory read** - the store is the evidence; the audit line lists the
   removed grants' SIDs (not their settings) in `removedGroupSids`, like `MembershipChanged`.
4. **An unreadable store is `LookupFailed`**, the same as an unreachable directory.

## Closeout

Fixed on branch `aud-a29cde` under the owner's 2026-10-21 go. Pending: codex review of the
commit, merge to `master` by the supervisor (with the base version bump and the index row), and
a live dev check: with a page open through a section grant, remove the group from that section
and confirm the tab ends within one interval with one `GrantRemoved` line.
