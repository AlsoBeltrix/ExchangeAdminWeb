# cons-5: a second save while one is running is dropped with no word to the operator

**Severity**: LOW - no wrong write; an operator action disappears. `Components/Pages/ModuleConfig.razor` about 880,
`Components/Pages/AdminSettings.razor` about 392 and `Components/Pages/ExchangeOnlineConfig.razor` about 370: the
aud-a67 (4)/(13) guard returns without a banner, a status line or a refusal record, so a second save - including one for
a different dirty setting on the same page instance - is silently ignored while its setting stays dirty.
**Status**: Merged to master `4afa745d`; codex defect review settings-batch-9 in flight
**Branch**: `fix/a64-a67` (from `eb017102`)
**Commit**: see below
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base 620e9d5a head f08a664a (merged fe67668d, with d0fc4c70); 2026-10-26; capability proof succeeded (AGENTS.md read, git show --stat f08a664a)

## Evidence

Codex settings-batch-8 finding 2 at `fe67668d`.

## Approach

- Keep the one-save guard and show a non-success status ("A save is already running; wait for it to finish.") without
  clearing the dirty state; the three pages are line-keyed, so edit in place or re-anchor. Test per page; mutation: the
  status dropped.


### Implementation notes (fix/a64-a67, 2026-10-26), at `64365a78`

The aud-a67 (4) guard returned bare. Each of the three now sets a non-success status and returns, clearing nothing and
recording nothing: no save was attempted, and the one that is running writes its own record. One constant,
`ModuleConfig.SaveAlreadyRunning`, is what all three show, so the three pages cannot drift apart.

The pages differ in what "the press was not taken" means, and the case asserts each on its own terms: Admin Settings
and Module Configuration keep their unsaved count, and Exchange Online Config - which has no dirty state at all, its
fields being the state - keeps the values typed into them.

## Files changed

- `Components/Pages/ModuleConfig.razor`, `Components/Pages/AdminSettings.razor`,
  `Components/Pages/ExchangeOnlineConfig.razor`; `ExchangeAdminWeb.Tests/SettingsSaveOffCircuitTests.cs` (aud-a67 (4)'s
  theory, one row per page, extended rather than duplicated).

## Guard proof

Driver and filter as aud-a67's (511 tests). Each page's MD5 matched after its row; 511/511 green after each restore.
Each mutant is that page's guard as it was at `64365a78`, so each failure is the finding's reproduction.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| P1 `ModuleConfig.SaveCurrentTabAsync`'s refusal silent again | `31e8b9f3a56adfb1e45ff6a00cdad730` | 1 of 511: `ASecondSavePressedWhileTheFirstRuns_...(page: "Module Configuration")` - nothing shown where the refusal was expected |
| P2 `AdminSettings.SaveCurrentTabAsync`'s refusal silent again | `ee4a36d41a9bba251de02b14af523e21` | 1 of 511: the same case for "Admin Settings" |
| P3 `ExchangeOnlineConfig.SaveExoConfig`'s refusal silent again | `4fce8838e5a07129617e1d9f680c33e4` | 1 of 511: the same case for "Exchange Online Config" |

## Known gaps

- The status is a page banner, not an audit record, and deliberately: nothing was attempted, so there is nothing to
  record. An operator reviewing the log sees only the save that ran.
- `SaveModuleStatus` on Exchange Online Config shares the constant and the shape, but the case drives the connection
  save on that page; the status handler's guard is covered by aud-a67 (4)'s write-count assertion only.
- No page was rendered in a browser; the banner is asserted as the field the markup binds.

## Branch-tip gates - 2026-10-26 (`fix/a64-a67`, the cons-2/3/4/5 round)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 19 warnings - the same count as the fork, none in a
  touched file.
- `dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4`: 6,745 passed / 0 failed / 0 skipped, 4m40s. The
  previous tip of this branch was 6,742; these four records add 3 cases net (cons-4 adds three and retargets one,
  cons-5 extends an existing theory rather than adding cases, cons-2 and cons-3 add none).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `pwsh -NoProfile -File tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
- Not run: `Invoke-ScriptAnalyzer` and `Invoke-Pester tests/ps` - no PowerShell file changed. No directory, Exchange
  Online, Graph, DHCP, IIS, Delinea or SMTP call; nothing deployed.
