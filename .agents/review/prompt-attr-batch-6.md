# Defect review request: settings saves re-check their store, decide per row under the lock, record real before-states, and name the config database (aud-a28j, aud-a28k, aud-a26h)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `e58d8764` or later.
- The change is the branch merged at `6ae1af25`: `6869fd81` (plans), `1c2bb9ff` (aud-a28j, with the
  folded-in partial protected-principals save), `b2afe0e2` (aud-a28k), `3a4d2c96` (aud-a26h),
  `6aaa1133` (comments only), `ade2a1d7` (record SHAs), forked from `374718b3`. Review
  `git diff 374718b3 ade2a1d7`, and read the COMBINED `Components/Pages/AdminSettings.razor`,
  `Components/Pages/ModuleConfig.razor`, `Components/Pages/ExchangeOnlineConfig.razor`,
  `Services/ProtectedPrincipalService.cs` (`ReplaceConfig`), `Services/Storage/ProtectedPrincipalRepository.cs`
  (`Replace`), `Services/ModuleEnablementService.cs` (`SetModulesEnabled`),
  `Services/Storage/ModuleEnablementRepository.cs` (`SetModules`), `Services/SectionAccessService.cs`
  (`SaveEditedAliases`), `Services/Storage/SectionAccessRepository.cs` (`ReplaceAliases`),
  `Services/ModuleConfigService.cs` (`UpdateModuleValues`), `Services/ExtendedLogService.cs` (`SetLevel`),
  `Services/SettingsStoreText.cs` (new), `Services/SettingsSaveAudit.cs`, `README.md` on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a28j.md`, `aud-a28k.md`, `aud-a26h.md`; parents `aud-a28h.md`,
  `aud-a28i.md`, `aud-a28f.md`, `aud-a26f.md`, `aud-a33b.md`. ALREADY admitted and out of scope:
  `aud-a28n` (protected principals, module config and module admins still replaced wholesale from
  the page's state) and `aud-a26k` (a concurrent change equal to the target is refused; two map
  writers have no caller; the directory-read secret reads fail-open; the drain failure uses the
  generic action). Do not re-raise them. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Known
  Failure Classes" (1, 2, 3), Architectural Invariant 2; `docs/ProjectConstitution.md` (fail-closed
  authorization; audit).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 6ae1af25`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. a28j: the `StoreUnreadable` / `StoreCorrupt` refusals on every save of
the three pages (count them against the aud-a28h survey - any save still able to write after a
failed load); the Save button hidden after a failed load AND the handler refusing by itself; the
protected-principals `Replace` under the lock (what it refuses; `EmptyOverNonEmpty` - can a legitimate
"remove every rule" from a page that DID load rules still succeed; the legacy-file-not-imported
refusal); the folded-in partial save (rows saved, secret write throws: the two records and the
message; `UpdateModuleValues` only when changed - a secret cleared to empty). a28k: `SetModulesEnabled`
writing only toggled modules (a toggle to the same value made elsewhere; the descriptor default as
before-state; a module missing a row); `SaveEditedAliases` comparing edited aliases as case-insensitive
sets under the lock (display-name-only changes; the never-configured first save writing the fallback
for every alias - does that change anyone's effective access; `ConcurrentChange` naming each alias);
`SetLevel` storing first then applying (a store failure leaves the old level in effect - is the page's
message right about which level). a26h: `SettingsStoreText` variants (legacy versus database; any
store still naming a retired file; the operator-facing texts unchanged); the drain after the save as
its own warning and record (the activity completed as not succeeded - does that flag the save as
failed anywhere else, for example the progress registry or a notification). Tests passing for the
wrong reason: `SettingsPageHarness` reflection-driven handlers; the `BeforeWrite` hook; the two-store
tests on one temp file.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
