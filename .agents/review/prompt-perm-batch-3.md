# Defect review request: the permission gate fails closed on an unreadable Exchange Online enablement or config and on an unreadable or unparseable self-grant setting (aud-a28t); the validator tests never share the temp-directory database (aud-a26q)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `04e8ca7c` or later.
- The change is the branch merged at `18c62ce9`: `b18df623` (plans), `53516af0` (aud-a28t), `a7767f0b` (aud-a26q),
  `9b4bf0e6` (comments only), forked from `5d93680b`. Review `git diff 5d93680b 9b4bf0e6`, and read the COMBINED
  `Services/PermissionValidator.cs` (`TryExpandGroupAsync`, the `_initFailed` load catch, `GetPreventSelfGrant`,
  `ValidateSelfGrantAsync`, the corrupt-branch log), `Services/ExoConnectionPool.cs` (`TryGetIsConfigured` beside
  `IsConfigured`), `Services/ModuleEnablementService.cs` (`TryGetModuleEnabled` and its helper; `IsConfigured`'s
  enablement read), `ExchangeAdminWeb.Tests/TestConfigStore.cs` (`Create` refusing the temp directory itself),
  `ExchangeAdminWeb.Tests/SharedTempDatabaseTripwireTests.cs` (the source scan) and
  `ExchangeAdminWeb.Tests/PermissionValidatorTests.cs` (`CreateValidator`, `CreateValidatorWithScriptedResolution`)
  on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a28t.md`, `aud-a26q.md`; parents `aud-a28s.md`, `aud-a28p.md`, `aud-a26n.md`,
  `aud-a28i.md`. ALREADY admitted and out of scope: `aud-a28x` (the pool's own `GetExoConfig`/`BorrowAsync`/`IsConfigured`
  reads fall back to appsettings on a FAILED read), `aud-a28y` (an unreadable config or failed resolver skips the
  self-grant object-id comparison and allows the grant; a non-boolean appsettings `Security:PreventSelfGrant` throws
  from `bool.Parse`), `aud-a26t` (the remedy text's "then reload this page" in a service log; the shared
  `Audit:LogRoot` and undeleted test directories). Do not re-raise them. Repo rules: `AGENTS.md`;
  `.agents/repo-guidance.md` "Known Failure Classes" (1, 2, 3); `docs/ProjectConstitution.md` ("Legacy appsettings
  fallback is allowed only ... when the module-specific config is absent").

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 18c62ce9`. Report whether both succeeded.

## Mandate

Hunt for observable defects. a28t: `TryGetIsConfigured` returns false on an unreadable enablement OR config, and
`TryExpandGroupAsync` throws on false - but a cleanly-read "not configured" also yields false: how does the caller
tell the two apart, and is "keeps the entry a literal" on not-configured the right behaviour when the entry IS a
group (a group entry treated as a user literal is a names-only hole - name whether the aud-a28s/a28p records
accepted it); the `_initFailed` catch now covers the new throw: does anything else in the load now get swallowed
into "Protected-user list is unavailable" that used to surface its own message; `GetPreventSelfGrant` - the
single read, the absent/blank fallback, the unparseable refusal: is the refusal audited and shown, or only
returned; `ModuleEnablementService.TryGetModuleEnabled` - any caller of `IsModuleEnabled` that should now use it
(fail-open enablement reads elsewhere - list them, do not fix); `IsConfigured`'s enablement read still turns a
failure into "disabled": which callers are affected. a26q: the tripwire's source scan - a `GetTempPath()` wrapped
in a helper call or a variable escapes it (name the shape); `TestConfigStore.Create` refusing the temp directory
itself - a path one level down that is still shared across tests (a fixed subfolder name) passes; the four stores
on `testDir` - any store that is still shared through a static. Tests passing for the wrong reason: the executed
reproduction swapped three production files - which tests failed and do they prove the fail-closed path on the
PRODUCTION call site rather than the helper.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
