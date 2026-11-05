# dov-v1: Verify stored directory objects - engine and the Protected Principals pane (slice V1)

**Severity**: MEDIUM - feature slice. The Protected Principals pane had no way to say which stored entries no longer match
the directory except a page-load sweep that looked at legacy rows only and a count banner; a deleted protected group kept
refusing writes with nothing on the page naming the cure (decision 2026-11-03's open question).
Plan `docs/DirectoryObjectVerify-Plan.md` (Approved 2026-11-04, three codex-commercial rounds, section G), section E "V1";
decision `.agents/decisions.md` 2026-11-04.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/dov-v1` (worktree `D:\source\ExchangeAdminWeb-wt\dov-v1`, from master `6062d6f5`)
**Commit**: see `git log 6062d6f5..fix/dov-v1` (`fix(dov-v1): ...`, `record(dov-v1): ...`) | **Reviewer**: implementer (candidate)

## Approach (as built), per plan point

1. **Engine** (`Services/DirectoryObjectVerifier.cs`, new). `DirectoryObjectVerifier.VerifyAsync(items[, ignores], ct)` ->
   `VerifyReport`. `VerifyItem(Store, RowKey, Kind, Identifier, Label, StoredValue, SnapshotVersion)`; findings
   `Verified / Missing / LabelDrift(current) / NotConverted(resolution) / Ambiguous(matches) / WrongClass(actual) /
   Unreadable(reason) / Ignored(underlying)`; typed lookup answers `VerifyLookupResult.Found(objects)` (empty = confirmed
   absence, the only road to Missing) and `.Unreadable(reason)`. One `DirectoryReadOperation.BeginCheck`
   ("Directory object verify") per press; every AD read in ONE session of the protection service's warm pool with the
   directory-read credential (`ProtectedPrincipalService.RunVerifyReadsAsync`: one fetch, the throttle, the 60 s
   credential-failure window, a session a failed read may have broken is failed). Identifier lookups at the global catalog in
   chunks of 50 (`(|(objectSid=..)..)`, `(|(objectGUID=\xx..)..)`, attributes as B.1), DN reads per domain; the forest check
   on every stored DN, once per domain, through `ADDirectorySearchService.ForestDomainFromCrossRefs` (the OU validation's
   decision, extracted so both use it): a domain the forest's crossRefs lack is `Unreadable("domain is not in this forest")`;
   a crossRef read that fails is unreadable too. Any read that throws makes its batch unreadable; an adapter is never shown an
   unreadable answer. Nothing named or defaulted (Invariant 7).
2. **A1/A2 adapter** (`Services/ProtectedPrincipalVerifyAdapter.cs`). Items from the rules (identifier rows, targets, unpaired
   legacy rows; companions are not items). B.2: user canonical label UPN, else mail, else none (never drift); group / OU /
   target DN; classes user|inetOrgPerson, group, organizationalUnit. Legacy rows: `ProtectedPrincipalIdentifierResolver.Resolve`
   in the press's session (after the forest check on a DN-shaped value) -> Convertible / None / Several / Unreadable.
3. **Ignore list** (`Services/VerifyIgnoreList.cs`): `app_setting` `verify_ignores`, JSON array of
   `{store,rowKey,identifierOrText,finding,observed,by,at}`; a value that does not parse, or any incomplete entry, is an empty
   list + one Warning. `AppSettingRepository.ReplaceIfEqual(connection, transaction, key, expected, new)` (compare-and-swap in
   the caller's transaction; `ReadValue` beside it). `ProtectedPrincipalRepository` primitives, each ONE write transaction that
   refuses unless the rules are still the item's version and the row is still stored exactly as read:
   `RemoveWithCompanionAndIgnores`, `ReplaceWithCompanionAndIgnores`, `RewriteLabel(kind, identifier, newLabel)` (row label and
   companion), `AddVerifyIgnore` (row re-checked under the lock, R2), `ConvertRow` (through
   `ConfigDbIdentifiers.ConvertProtectedRow`). Remove, Replace and Ignore compare-and-swap the list against the value read
   beside the item and prune the entries of rows the write no longer holds; the actions re-read the list and retry ONCE, and
   a second mismatch is refused. The whole-store `Replace` (the pane's ordinary Remove + Save) prunes in its transaction too.
   `IConfigStore.ReadInTransaction` (default: through `Read`; `SqliteConfigStore`: one deferred `BEGIN ... COMMIT`);
   `ProtectedPrincipalRepository.TryRead(out data, out configured, out verifyIgnores)` reads rules, marker and list in one read
   transaction and `LoadEffectiveConfig` caches the parsed list with the rules (`LoadEffectiveConfigAndIgnores`). V1 does NOT
   change `ProtectedGroupMembership.Evaluate` (B.4 is V2). `db identifier-status` ends with the list (nothing when empty).
4. **Page** (`Components/Pages/AdminSettings.razor`). Verify button (`btn btn-outline-success`, "Verifying...", disabled while
   the pane is dirty, a save or an Add is pending, or an action runs); `Progress.Begin(..., ActivitySize.Items(n))`; badges
   verified / missing / name changed / not converted / several objects / wrong type / could not verify / ignored with hover
   help; summary "Verified N entries at <time>: M findings, K ignored, J could not be verified."; `verify_last` (store-keyed
   map, plain write, display-only) shown as "Last verified ... by ..." before a press. A press shows the rows it verified (the
   pane has no unsaved edits, so nothing is lost). Removed: `StaleBadge`, `StaleTip`, `UnconvertedCount`,
   `SweepExistingEntriesAsync`, `ppStaleEntries`, `ppSweepDone`, `OnAfterRenderAsync` and the "not converted" banner; and
   `ProtectedPrincipalEntryValidator.ShouldFlagAsStale`, whose only caller was the sweep (with its four tests). ClickGate:
   `ClickGateRegistry` has no line entries for AdminSettings.razor (re-checked: "tier 2, approved 2026-10-12, not started").
5. **Actions** (`Services/ProtectedPrincipalVerifyActions.cs`): Rename (LabelDrift), Replace (picker; validated as an Add),
   Convert (NotConverted convertible), Remove now, Ignore (identifier rows' Missing / LabelDrift / WrongClass / Ambiguous only).
   Each re-authorizes the `AdminSettings` policy with the admin's current principal, refuses an action the finding does not
   offer (`NotOffered`), and audits: success `LogSettingsChange(user, ip, "Verify:ProtectedPrincipals", before, after,
   {action, finding, observed, row[, replacement]})`; refusal `LogSettingsSaveRefused` (refusedAt = Authorization /
   NotOffered / VersionChanged / RowChanged / IgnoresChanged / IgnoresUnreadable / StoreUnreadable); failure
   `LogSettingsSaveException`. A press writes one success row with its counts. After a landed action the page shows the rows the
   write left and rebases the other findings on its version (`ProtectedPrincipalVerifyActions.Rebase`).
6. **Help**: pane note, button tip, every badge's and action's hover; README Protected Users paragraph rewritten (Verify) and
   the "not converted" Troubleshooting entry gains the Verify route; `docs/ConfigDbOperations.md` notes the status printout.
   The "Protected-principal check was incomplete" Troubleshooting entry is unchanged (V2).
7. **Versions**: AdminSettings 1.2.6 -> 1.3.0. Base 2.36.0 unchanged.

## Where the code goes beside the plan's text

- **The forest check marks an identifier row unreadable even when the catalog would have found it.** B.1 says the check
  applies "to every DN"; it is applied to every stored DN before any lookup, so a row whose stored DN names a domain outside the
  forest is "could not verify" whatever the catalog holds. Never Missing or Verified; the cost is that such a row is offered
  Remove only. A stored DN with no DC components is not checked for identifier rows (the catalog answers by identifier).
- **A user row's label is an address, so its forest check has no DN to read**: a `user_sid` the catalog does not hold is
  Missing. Assumption, unverified against AD: a SID of another forest's user would read as missing here. Its ignore is
  display-only (B.4), so nothing enforces on it.
- **Rows stored by name get NotConverted(unreadable)** for a DN outside the forest (not plain Unreadable), so every A2 row stays
  NotConverted as A2 says; its only action is Remove.
- **An Ignored finding offers Replace and Remove** (the exec summary's "undone by removing or re-picking the entry").
- **`verify_last` is a map keyed by store** in the one key, so V3/V4 panes share it; written by an in-transaction read and
  upsert (no compare against a caller's value - display-only), not literally `AppSettingRepository.Set`.
- **`ConfigDbIdentifiers.ConvertModuleAdminGrant`** was extracted beside `ConvertProtectedRow` (plan C.2 names both); V3 uses it.
- **`docs/ConfigDbOperations.md`** got one sentence on the status printout (plan D, which lists the run-book under V2) because
  the printout itself ships in V1.

## Guard proof

See "Non-vacuity" below: each mutation applied, the solution built, the named tests run and seen to fail, the file restored.

## Gates

At the tip, from the worktree root; see the hand-back for the summary lines. Not run: any live database, directory or
Exchange; the PowerShell session's batched reads (`PowerShellProtectedGroupDirectory.FindVerifyObjects`,
`ForestDomainServer`) against a real or stand-in forest - the engine is proven over `IVerifyDirectory` stand-ins and the
filters by their shape.

## Left for later

- V2: B.4 (an ignored missing `group_sid` stops refusing only on a confirmed zero), banner text, corrupt-list tests per path.
- V3/V4: further adapters on `IVerifyStoreAdapter` (no engine change).
