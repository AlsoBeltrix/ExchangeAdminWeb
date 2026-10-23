# aud-a28x: Exchange Online pool - a failed module-config read falls back to appsettings, so a borrow can connect under the appsettings values

**Severity**: MEDIUM - credential isolation and fail-closed reads. In `ExoConnectionPool.GetExoConfig`,
`BorrowAsync` and `IsConfigured`, a `GetValue` that FAILS reads as null and the appsettings
`ExchangeOnline:*` fallback applies. The Constitution allows the legacy fallback only when the
module-specific config is ABSENT; an unreadable store is not absent. If the store then recovers by the
presence check, a borrow connects under appsettings values (tenant, app ID, certificate) instead of the
module config's. aud-a28t added `TryGetIsConfigured` beside `IsConfigured` for the permission gate; the
pool's own three reads still use the permissive path.
**Status**: Merged `448b02c8` (with the folded-in aud-a26s follow-up); closed on guard proof; codex review pending
**Branch**: `worktree-agent-a4c579a99f70ca768`
**Commit**: `da615264` (fix, tests, record, the folded-in follow-up); master `8151de3e` merged in first (`8ee81571`); plan `7e65ce6a`; comments `5c32669e`
**Reviewer**: implementer (surfaced by the aud-a28t/a26q agent, 2026-10-22, candidate 1 of its hand-back)

## Evidence

At `18c62ce9`: `Services/ExoConnectionPool.cs` `GetExoConfig`, `BorrowAsync`, `IsConfigured` (the
`GetValue` reads and the `??` appsettings fallbacks); `TryGetIsConfigured` (the fail-closed shape);
`docs/ProjectConstitution.md` "Legacy appsettings fallback is allowed only ... when the module-specific
config is absent".

## Approach (to be written by the implementing agent before code)

- The three reads go through `TryGetModuleConfig`; a failed read makes `GetExoConfig` throw a typed
  "Exchange Online settings could not be read: <rows and remedy>" and `BorrowAsync` refuse (no connection
  attempted, one log line); `IsConfigured` keeps its contract for callers that cannot take a throw by
  delegating to `TryGetIsConfigured` and treating unreadable as "not configured AND unreadable" where the
  caller can show it. The fallback applies only to a value that is present-and-null or absent.
- Tests through the pool's connect seam (it throws) and a failing store double; mutations at the call
  site: `GetValue` again at one read; the fallback applied to a failed read again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21). Branch
`worktree-agent-a4c579a99f70ca768` from master `18530b58`: one fix commit `fix(exo-pool): ... (aud-a28x)`, second of three
(after aud-a28y, before aud-a26t), and the comments-only commit after all three. Behaviour: the shared
`ExoConnectionPool` every Exchange Online module borrows from, and the resolver's not-configured refusal (base app
version); no module version moves; the supervisor owns the bump.

Evidence re-read at `18530b58`:
- `ExoConnectionPool.GetExoConfig` 191-208: three `ModuleConfigService.GetValue` reads (193-195; `GetValue` 60-64 ->
  `ReadModuleConfig` 281-293 logs a read failure and returns an empty config, so null), then the presence read and the
  probe (197: `HasModuleConfigFile` -> `ModuleConfigRepository.HasModule` 52-61, which throws; `IsModuleCorrupt` ->
  `TryReadModule` 33-45, false on a failure), then appsettings `ExchangeOnline:AppId` / `Organization` /
  `CertificateSubject` for every null (203-206). A value read that fails while the presence read and the probe answer - a
  confined fault, or a store that recovers between them - is taken for "absent" and gets the appsettings value: possibly
  another tenant, app id or certificate than the module config's.
- `BorrowAsync` 218-326 reads it once (234), before the slot; those values decide the not-configured refusal (236-237),
  the stale-pool check (268) and what the connect is given (305), so the borrow connects under appsettings values.
  `DrainPool` 502-520 reads it (507) to record what the next borrow connects under. `IsConfigured` 153-162 reads it through
  the same chain - and throws when the presence read fails: a bool property that is not exception-safe.
- `TryGetIsConfigured` 175-189 (aud-a28t) is the fail-closed shape: one `TryGetModuleConfig` read, false when it fails,
  appsettings only for a key the rows do not hold.
- `ExchangeIdentityResolver.ResolveRecipientAsync` 47 refuses "Exchange Online is not configured" when `IsConfigured` is
  false. `DrainPool`'s one caller, the Exchange Online Config page's save (`ExchangeOnlineConfig.razor` 554-570, owned
  by another agent), reports any throw as "the connection pool could not be drained" and records it as a failed drain.
- Constitution, Configuration: "If a config store is corrupt or unreadable, fail closed. Do not silently fall back to
  stale defaults." and "Legacy appsettings fallback is allowed only for upgrade compatibility and only when the
  module-specific config is absent." Never Do: "Do not make corrupt config silently fall back to defaults."

1. **One read, and a read that failed is not absent.** `GetExoConfig` reads the ExchangeOnline rows once through
   `ModuleConfigService.TryGetModuleConfig` (aud-a26k). Unreadable -> one error line and a typed `ExoConfigUnreadableException`
   (an `InvalidOperationException`, declared in `ExoConnectionPool.cs`): "Exchange Online settings could not be read: "
   followed by the rows and their fix (`SettingsStoreText.ModuleConfig`, as the Exchange Online Config page names them;
   aud-a26t then gives the service callers a remedy without the page's reload clause). Readable -> every key from that one
   read, appsettings only for a key the rows do not hold, as today; the presence read and the probe go, the one read being
   both. `TryGetIsConfigured` reads AppId and Organization through the same helper, so the fallback rule is in one place.
2. **The borrow refuses.** `BorrowAsync` lets that exception out before it asks for a slot: no slot, nothing connected,
   no appsettings value used.
3. **The drain does not need the read.** `DrainPool` retires the generation and destroys the idle runspaces whether or
   not the rows read. When they cannot be read it records no connected-under config (null, the state before a first
   borrow), so the next borrow records what it reads without draining again, and it logs a warning; it does not throw,
   so a saved configuration is not reported as an undrained pool. Nothing connected before survives either way: the
   generation moved.
4. **`IsConfigured` keeps its contract.** It becomes `TryGetIsConfigured(out var configured) && configured`: a bool
   that never throws, false for rows or an enablement that cannot be read, so callers that cannot show more (the
   recipient autocomplete) keep working. `ExchangeIdentityResolver.ResolveRecipientAsync`, which can, asks
   `TryGetIsConfigured`: unreadable -> "Exchange Online's module enablement or its module_config rows could not be read -
   cannot resolve recipient."; readably not configured -> the existing message.
5. **Registries.** No page, handler or markup changes: ClickGateRegistry and ProgressRegistry untouched.

Tests, new `ExchangeAdminWeb.Tests/ExoConnectionPoolConfigReadTests.cs`: a real pool over a temp database, Exchange Online
enabled on its own store, the ExchangeOnline rows holding one tenant's AppId, Organization and CertificateSubject and
appsettings `ExchangeOnline:*` another's, the rows read through a store that fails a set number of reads after a set
number (aud-a28t's `FailsAfterReadsStore`), and a connect seam that records the config it is given and throws, so no
session can open. (a) The rows unreadable at the borrow's read, from then on or for that read only: the borrow throws
`ExoConfigUnreadableException` naming the rows, the seam never reached, one error logged; (b) the store failing every
read after the borrow's one: the seam is given the rows' values, not appsettings', and the borrow read the rows once;
(c) controls - no ExchangeOnline rows: the seam is given appsettings' values; one key absent: that key from appsettings,
the others from the rows; (d) `IsConfigured` over unreadable rows is false and does not throw, also with every read
failing, where the presence read used to throw; `TryGetIsConfigured` false; (e) `DrainPool` over unreadable rows does not
throw and moves the generation on, and a borrow once the store answers again is given the rows' values; (f) the real
`ExchangeIdentityResolver` over the pool: unreadable rows -> `ResolveRecipientAsync` throws naming the unreadable read,
not "not configured", nothing connected; AppId blank -> "not configured" (control).

Mutations at the call site: (1) `GetExoConfig` reads AppId through `GetValue` again, beside the one read, the appsettings
value standing in for a failed read; (2) `GetExoConfig` takes unreadable rows for absent again (the empty dictionary, so
every value from appsettings). Extras: (E1) `IsConfigured` back on the `GetValue` chain; (E2) `DrainPool` lets the
unreadable read throw; (E3) the resolver asks `IsConfigured` again. No separate pre-fix reproduction: the tests name the
new exception type, so they do not compile against the old pool; mutation 2 is the old fallback-on-failure at its call
site.

## Departures from the plan

- **Item 3 is not done: `DrainPool` keeps its contract.** The plan had the drain retire the generation and not throw when
  the rows cannot be read. `SettingsStoreTextTests.ExchangeOnlineConfig_AFailingDrainAfterASavedConnection_IsItsOwnWarning_NotAFailedSave`
  (aud-a26h, aud-a26k; a file another agent owns) pins the opposite, and it is the honest shape: a drain that cannot read
  the settings throws before it retires anything, and the Exchange Online Config page reports that as its own failed step
  ("Connections opened before the save may still use the previous settings - recycle the application pool", which is then
  true) with a `DrainConnectionPool` Failed record, never as a failed save. Nothing in this finding needs the change:
  `DrainPool` still reads through `GetExoConfig`, which now throws `ExoConfigUnreadableException` where it threw a raw store
  error from its presence read - or, with a confined fault, recorded appsettings values as what the next borrow connects
  under. A connection the failed drain leaves in the pool is retired by the next borrow that reads the rows (scdi-1's
  compare), and a borrow that cannot read them refuses. Test (e) pins that instead, and mutation E2 is the drain swallowing
  the failed read.
- **The folded-in aud-a26s follow-up** (below; the coordinator's direction, master `6972a5c5`). The branch forked from
  `18530b58`, before `Services/ExoConfigFallback.cs` existed, so master `8151de3e` was merged into it first (`8ee81571`;
  `git merge-tree` showed it clean, and it touched none of this branch's files but this record, whose "Folded in" section it
  brought). The pool's three fallback reads call `ExoConfigFallback.Resolve(...).Value`, behind the fail-closed read. Added
  for it: a source check (the pool reads no `ExchangeOnline:*` setting and holds no built-in default itself) and mutation F1.

## Fix

`Services/ExoConnectionPool.cs`:
- New `ExoConfigUnreadableException`, an `InvalidOperationException`.
- `TryReadExoConfig(out ExoConnectionConfig)`: one read of the ExchangeOnline rows through
  `ModuleConfigService.TryGetModuleConfig`, false when it fails; otherwise each key through
  `ExoConfigFallback.Resolve(_config, key, row)` - the row as stored, a blank one included; a key with no row from
  appsettings; the certificate's built-in default after that. A failed read never reaches `Resolve`.
- `GetExoConfig`: that read, or one error line and `ExoConfigUnreadableException("Exchange Online settings could not be
  read: " + the rows and their fix)`. The three `GetValue` reads, the presence read and the corruption probe are gone.
- `IsConfigured` is `TryGetIsConfigured(out var configured) && configured`, and `TryGetIsConfigured` reads through
  `TryReadExoConfig`: a bool that never throws, false for rows or an enablement that cannot be read.
- `BorrowAsync` and `DrainPool`: their code is unchanged; through `GetExoConfig` they throw that exception, the borrow before
  it asks for a slot, the drain before it retires anything.

`Services/ExchangeIdentityResolver.cs`: `ResolveRecipientAsync` asks `TryGetIsConfigured` - unreadable -> "Exchange Online's
module enablement or its module_config rows could not be read - cannot resolve recipient."; readably not configured -> the
existing "Exchange Online is not configured - cannot resolve recipient.".

Tests: new `ExchangeAdminWeb.Tests/ExoConnectionPoolConfigReadTests.cs` (12 cases): (a) unreadable rows refuse the borrow,
from then on and for that read only, nothing connected, one error logged (2); (b) a readable borrow connects under the rows
from one read, every later read failing (1); (c) no rows, and one key absent, take appsettings for what is absent (2); (d)
`IsConfigured` and `TryGetIsConfigured` false over unreadable rows, no throw, true over readable ones (2); (e) `DrainPool`
over unreadable rows throws the typed exception and leaves the generation where it was (2); the source check (1); (f) the
real resolver names the unreadable read, and says "not configured" for a blank AppId (2). Master's
`ExoConfigFallbackTests` (aud-a26s) holds the pool to the rule for every key and source and passes unchanged.

Caller-visible change, by design: while the ExchangeOnline rows cannot be read, every Exchange Online borrow refuses with
"Exchange Online settings could not be read: " and the rows and their fix, logged once per borrow - where a confined fault
used to connect under the appsettings tenant, app id or certificate, and a store-wide one threw a raw store error.
`IsConfigured` is then false (the recipient autocomplete shows nothing) where it used to be true under appsettings values,
or throw; the resolver names the unreadable read; a drain after a save fails with the same text, which the Exchange Online
Config page shows as its failed-drain warning. Over readable rows nothing changes: a key with no row still takes
appsettings, the upgrade fallback the Constitution allows for config that is absent.

## Guard proof

Same session-local driver as aud-a28y (`mutate.ps1`; the fix staged, each mutant restored from the git index and touched),
filter
`FullyQualifiedName~ExoConnection|FullyQualifiedName~ExoConfigFallback|FullyQualifiedName~ExchangeIdentityResolver|FullyQualifiedName~PermissionValidator|FullyQualifiedName~ExchangeServiceBase|FullyQualifiedName~RecipientLookup|FullyQualifiedName~DelegationReport|FullyQualifiedName~Migration|FullyQualifiedName~OutOfOffice|FullyQualifiedName~WriteIdentityTests|FullyQualifiedName~MailboxPermission|FullyQualifiedName~CalendarPermission|FullyQualifiedName~Settings|FullyQualifiedName~ModuleEnablement|FullyQualifiedName~ProtectedPrincipalExclusionsReadTests|FullyQualifiedName~SharedTempDatabaseTripwireTests|FullyQualifiedName~Comms10k|FullyQualifiedName~ConferenceRoom`
(1,928 tests: every class that builds a pool). Fixed-file MD5s: `Services/ExoConnectionPool.cs`
`61DD543084E8406C8A157C8D14A2A0B3`, `Services/ExchangeIdentityResolver.cs` `CC9C9BF662EC2678A160D7FC938A4C78`; every mutant
built with 0 errors and its file was restored to that MD5 each time. After the round, the restored tree rebuilt and the same
filter: 1,928/1,928 green.

| # | Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|---|
| 1 | `TryReadExoConfig`: AppId read through `GetValue` again, before the one read, and fed to the rule - a failed read standing in as absent | `0F60787E4614EAA73B551E7E0838DA65` | `RowsThatCannotBeRead_RefuseTheBorrow_AndNothingComesFromAppsettings(failing: 1)` (the borrow reached the seam under the appsettings AppId), `AReadableBorrow_ConnectsUnderTheRows_FromOneRead` (the second read failed), `IsConfigured_OverRowsThatCannotBeRead_IsFalse_AndDoesNotThrow(failing: 1)`, `DrainPool_OverRowsThatCannotBeRead_FailsBeforeItRetiresAnything(failing: 1)`, `PermissionValidatorConfigReadTests.AStoreThatFailsTheExchangeOnlineRead_RefusesEveryValidation_NotNamesOnly(failing: 1)` |
| 2 | `TryReadExoConfig`: unreadable rows taken for absent again (the failed read's empty dictionary into the rule, so every value from appsettings) | `CD3D0321F72034A93879E5F376CE0ACD` | both rows of `RowsThatCannotBeRead_RefuseTheBorrow_AndNothingComesFromAppsettings` (the borrow reached the seam), of `IsConfigured_OverRowsThatCannotBeRead_IsFalse_AndDoesNotThrow` and of `DrainPool_OverRowsThatCannotBeRead_FailsBeforeItRetiresAnything`; `TheResolver_OverRowsThatCannotBeRead_SaysSo_NotThatExchangeIsNotConfigured`; both rows of `PermissionValidatorConfigReadTests.AStoreThatFailsTheExchangeOnlineRead_RefusesEveryValidation_NotNamesOnly`; `SettingsStoreTextTests.ExchangeOnlineConfig_AFailingDrainAfterASavedConnection_IsItsOwnWarning_NotAFailedSave` - 10 |
| E1 | extra: `IsConfigured` back on fail-open `GetValue` reads | `E2E53F14062390674603CCD03EF59642` | both rows of `IsConfigured_OverRowsThatCannotBeRead_IsFalse_AndDoesNotThrow`; `ThePool_ReadsNoAppsettingsConnectionValueItself_TheOneRuleDoes` |
| E2 | extra, `DrainPool`: the failed read swallowed (`TryReadExoConfig(out var now);`) | `2F9E465CFDA004F0B1C8F36ACDE045E7` | both rows of `DrainPool_OverRowsThatCannotBeRead_FailsBeforeItRetiresAnything`; `SettingsStoreTextTests.ExchangeOnlineConfig_AFailingDrainAfterASavedConnection_IsItsOwnWarning_NotAFailedSave` |
| E3 | extra, `ExchangeIdentityResolver.ResolveRecipientAsync`: asks `IsConfigured` again | `56D14822A26CAED7FCFFE011401B5205` | `TheResolver_OverRowsThatCannotBeRead_SaysSo_NotThatExchangeIsNotConfigured` |
| F1 | folded-in item: the pool's own copy of the certificate fallback back in place of `ExoConfigFallback.Resolve` | `C86B24AA38128750364CA102E55B9AD8` | `ThePool_ReadsNoAppsettingsConnectionValueItself_TheOneRuleDoes` (only: the copy agrees with the rule, so `ExoConfigFallbackTests` passes - which is why the source check is there) |

Mutant E3 is byte for byte `Services/ExchangeIdentityResolver.cs` as aud-a28y committed it (`b5ab1859`). The code comments are
written after the round, in their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; 61 warnings, all pre-existing, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0, 5,942
  passed / 3 skipped (the three known AD skips) / 0 failed, 5,945 total, 5m21s. No hang. (aud-a28y's tree: 5,867; the merge
  of master brought 66, this commit's class 12.)
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

No PowerShell source, AD module, directory, Exchange Online, Delinea, SMTP, push or deployment: the connect seam refuses, and
no case here opens a session. No coder self-review verdict is claimed.

## Known gaps

- `BorrowAsync` still reads the enablement through `IsModuleEnabled`, which takes an unreadable enablement store for every
  module disabled: the borrow refuses, fail-closed, but says Exchange Online is not enabled rather than that the store
  cannot be read. Not this finding's read; handed back.
- A caller that asks `TryGetIsConfigured` and then borrows (the permission validator's group expansion) reads the rows
  twice, as before; the borrow's read is the one that decides what is connected.
- `ExoConfigFallback.cs` and `ExoConfigFallbackTests.cs` (aud-a26s) still say the pool keeps its own copy of the rule; their
  remarks are corrected in this branch's comments commit.
- The failing stores stand in for a store fault, and the connect seam for Exchange; nothing ran against a damaged
  database or a tenant.

## Folded in: point the pool's fallback reads at ExoConfigFallback (aud-a26s follow-up, 2026-10-22) - LOW

aud-a26s moved the fallback rule into `Services/ExoConfigFallback.cs` (`Resolve`/`Caption`) and pinned a test
to the pool's current behaviour; the pool itself was owned by this finding's agent. `ExoConnectionPool.cs`
lines 176-179 (at `a583777d`) become three `ExoConfigFallback.Resolve(...).Value` calls so one function owns
the rule; the fail-closed read this finding adds sits in front of it (a failed read never reaches `Resolve`).

Done in this finding's fix commit, after the branch merged master `8151de3e` (`8ee81571`): `TryReadExoConfig` resolves all
three keys through `Resolve`, and the pool reads no `ExchangeOnline:*` setting itself (source check; mutation F1 above).

## Branch-tip gates - 2026-10-22 (`5c32669e`: the three fixes, the master merge and the comments)

- Comments `5c32669e`: comment lines only - every changed line is a whole-line `//` or `///` comment, the MD5 of each
  touched file's code lines is equal before and after it (session-local `codelines.ps1`, eleven files), and the two pages
  keep 808 and 762 lines.
- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors; 61 warnings, all pre-existing, none in a
  touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0, 5,947
  passed / 3 skipped (the three known AD skips) / 0 failed, 5,950 total, 5m32s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
