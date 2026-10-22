# aud-a38k: The "nothing at the folder" refusal tells the cutover it is refusing to replace files it never replaces

**Severity**: LOW - wording only. `Assert-AppPoolServesPath`'s refusal when IIS serves nothing at
the folder says "Refusing to replace files that may still be served" to every caller, the cutover
included; `Move-ConfigDbToShared.ps1` replaces no files, so the operator reads a reason that does
not describe what was refused. Dates from aud-a38g; the aud-a38j `-NestedNodes Allow` path kept the
text.
**Status**: Fixed on branch; closed on guard proof; review pending
**Branch**: `worktree-agent-a6125ee40db9873ac` (forked at `4a8021a8`)
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a38j/a7f/a7g agent, 2026-10-22, candidate 1 of its hand-back)

## Evidence

At `f18431d0`, `tools/AppPoolControl.psm1`, `Assert-AppPoolServesPath`, the nothing-at-the-folder
refusal text; `tools/Move-ConfigDbToShared.ps1` `Assert-PoolServesInstance` wraps it with
`Write-Fail` and appends its own "Nothing has been stopped or copied." but the module's clause
about replacing files is still inside the message.

## Approach (to be written by the implementing agent before code)

- Word the module's refusals by the rule, not the caller's action: under `-NestedNodes Refuse` the
  mirror clause stays (those callers mirror); under `Allow` the refusal says the named pool does not
  serve the folder and nothing will be stopped, with no mention of replacing files. Pester: the
  cutover's lifted wrapper over the real module produces a message without the mirror clause; the
  promote/deploy messages keep it. 5.1 `ParseFile` zero errors; pure ASCII; `Import-Module Pester
  -RequiredVersion 6.2.0` before the analyzer. Mutation at the call site: the `Allow` message gets
  the mirror clause back.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); this record was admitted under it.
Ops-script-only: no version bump (the `aud-a34`/`a35`/`a36`/`a38`/`a38c`/`a38f`/`a38i`/`a38j`
precedent). Written before the code.

1. **`tools/AppPoolControl.psm1`, `Assert-AppPoolServesPath`: the wording chosen by the rule, in one
   place.** The block that already picks `$held`, `$scope` and `$consequence` by `-NestedNodes` moves
   above the nothing-at-the-folder check and also picks how that refusal ends.
   - `Refuse` (deploy, the promotion, the installer - each then mirrors the folder): every message
     byte-identical. Nothing at the folder ends "Refusing to replace files that may still be served.";
     the other-pool refusal keeps "would leave that application running while its files are replaced".
   - `Allow` (the cutover - it stops pools and copies databases, and replaces no file): nothing at the
     folder ends "IIS does not show '<pool>' serving '<folder>', so nothing will be stopped."; the
     other-pool refusal ("Files at or under '<folder>' are served by an app pool other than '<pool>'")
     reads "Stopping '<pool>' would leave that application running, so nothing will be stopped." No
     mention of replacing files in either.
   - Unchanged under both: the IIS-unreadable refusal (it names no files) and the `Refuse`-only nested
     refusal (it exists only for a caller that mirrors).
2. **Callers unchanged.** The five calls already state their rule (`aud-a38j`); the cutover's
   `Assert-PoolServesInstance` keeps its own frame ("... Nothing has been stopped or copied.").
3. **Tests**, each over the real module through its `-ApplicationsProbe`, each asserting its probe was
   asked where the caller is lifted:
   - `tests/ps/MoveConfigDbToShared.Tests.ps1` (`aud-a38g` Describe, the lifted wrapper): **the
     finding** - nothing at the folder: the refusal says nothing of replacing (`*replac*` absent) and
     says IIS does not show the named pool serving the folder, so nothing will be stopped; a folder
     another pool serves: the same.
   - `tests/ps/AppPoolControl.Tests.ps1`: the promotion's lifted stop step (`aud-a38c` context):
     nothing at the prod folder keeps "Refusing to replace files that may still be served."; another
     pool keeps "while its files are replaced"; neither reaches the stop. A new `aud-a38k` context on
     the module: the nothing-at-the-folder and other-pool messages exact under each rule; the
     unreadable refusal identical under both.
   - `tests/ps/DeployInvariants.Tests.ps1`: each deploy branch's real region over the real module:
     nothing at the folder, and a folder another pool also serves, each refused in the mirror's terms
     before the stop.
4. **Mutations at the call site**, each by exact-text replacement asserted to match once, md5 before
   and after, failing tests named, restored, md5 re-confirmed, touched, rerun green:
   - A1 (the one the Approach names): the `Allow` nothing-at-the-folder refusal gets the mirror clause
     back.
   - A2: the `Allow` other-pool refusal gets "while its files are replaced" back.
   - A3: the `Refuse` refusals take the `Allow` wording - the promotion's and deploy's pins must fail.
5. **Gates**: pure ASCII; Windows PowerShell 5.1 `Parser::ParseFile` zero errors on the module, its
   four calling scripts (five call sites) and the changed test files; `Import-Module Pester
   -RequiredVersion 6.2.0` before `Invoke-ScriptAnalyzer`, `-Severity Error` zero on each changed file
   (one `-Path` per call); `Invoke-Pester tests/ps` 0 failed 0 skipped; the installer contains no
   `deploy.ps1` string; `git diff --check HEAD`; `tools/Test-AsciiOnly.ps1` after `git add -A`.
   Comments and help text written after the mutation round.

## Guard proof

Built as the Plan. `tools/AppPoolControl.psm1`, `Assert-AppPoolServesPath`: the `-NestedNodes` block
now sits above the nothing-at-the-folder check and picks, with `$held`/`$scope`, how both refusals end
(`$consequence` for the other-pool one, `$unserved` for the nothing-at-the-folder one). The callers and
their call text are unchanged.

| Refusal | `Refuse` (deploy, promotion, installer) - unchanged | `Allow` (the cutover) |
| --- | --- | --- |
| nothing at the folder | "... `<reported>`. Refusing to replace files that may still be served." | "... `<reported>`. IIS does not show '`<pool>`' serving '`<folder>`', so nothing will be stopped." |
| the folder's nodes under another pool | "Files at '`<folder>`' ... Stopping '`<pool>`' would leave that application running while its files are replaced. Name the pool ..." | "Files at or under '`<folder>`' ... Stopping '`<pool>`' would leave that application running, so nothing will be stopped. Name the pool ..." |
| IIS unreadable | unchanged, the same under both | |
| nested node (`Refuse` only) | unchanged | - |

Under Windows PowerShell 5.1 the module imports, exports its four functions, and produces the four
messages above for the two refusals under each rule.

| Test | Pins |
| --- | --- |
| `MoveConfigDbToShared.Tests.ps1` `aud-a38g` / `aud-a38k: refuses a folder nothing in IIS serves without a word about replacing files, and says nothing will be stopped` | **the finding**: the cutover's lifted wrapper over the real module (its probe asked): no `*replac*` anywhere in the message, "IIS does not show 'aud-a38g-dev' serving '<folder>', so nothing will be stopped.", still ending "Nothing has been stopped or copied." |
| `... / aud-a38k: refuses a folder another pool serves without a word about replacing files, and says nothing will be stopped` | the cutover's other-pool refusal: "... would leave that application running, so nothing will be stopped.", no `*replac*` |
| `AppPoolControl.Tests.ps1` `aud-a38c` / `aud-a38k: refuses a prod folder nothing in IIS serves in the mirror's terms, before stopping anything` | the promotion's lifted stop step over the real module keeps "Refusing to replace files that may still be served."; no exists probe, no stop |
| `... / aud-a38k: refuses a prod folder another pool serves in the mirror's terms, before stopping anything` | the promotion keeps "while its files are replaced." |
| `Assert-AppPoolServesPath` / `aud-a38k: each refusal ends in the terms of the rule the caller named` (5 tests) | the module: both refusals exact under each rule; the unreadable refusal exact and identical under both |
| `DeployInvariants.Tests.ps1` / `runs each branch's real region over the real module: a folder the named pool is not shown to serve alone is refused in the mirror's terms, before the stop (aud-a38k)` | both deploy branches, lifted region over the real module: nothing at the folder, and another pool at it, each refused after the probe and before the stop, in the mirror's terms |

**Against the unfixed code** - the module checked out at `HEAD` (CRLF md5
`24d583c476ed77b2cba6c9251fae12f2`, the `aud-a38j` final), the tests as changed: 362 tests in the four
files, 4 failed - both cutover tests (the finding's message still ending "Refusing to replace files that
may still be served."; the other-pool one with no "nothing will be stopped") and the two module `Allow`
tests. The `Refuse` pins pass there, as they must: that wording is unchanged. Restored from the saved
bytes, touched, md5 re-confirmed.

Mutations at the call site, each by exact-text replacement asserted to match once, md5 recorded, the four
test files (`AppPoolControl`, `MoveConfigDbToShared`, `DeployInvariants`, `InstallerConfigBackup`) run in
a fresh pwsh (Pester 6.2.0), the module restored from its saved bytes, touched, md5 re-confirmed equal,
and the files re-run green (362/362). First round on the pre-comment module (`edae0f142037edc9b72e9390f6e36dcf`),
re-run on the final module (`4533af5668608b53304fbcac4adec6ef`) with the same results - the md5s below
are the re-run's. CRLF working-tree bytes (`.psm1` checks out CRLF; `.ps1` is `eol=crlf`).

| Mutation | md5 after | Failed |
| --- | --- | --- |
| A1: the `Allow` nothing-at-the-folder refusal gets the mirror clause back (`$unserved = 'Refusing to replace files that may still be served.'`) | `44c5f7efd033362139a518538b47baeb` | 2: the cutover's `aud-a38k: refuses a folder nothing in IIS serves ...` (the finding); the module's `with Allow, refuses a folder nothing serves ...` |
| A2: the `Allow` other-pool refusal gets "while its files are replaced" back | `15340268881f7f18cc0e84942e6009a6` | 2: the cutover's `aud-a38k: refuses a folder another pool serves ...`; the module's `with Allow, refuses a folder another pool serves ...` |
| A3a: the `Refuse` nothing-at-the-folder refusal takes the `Allow` wording | `a4ea216f9e3b01c126efc0e3ff2f8138` | 3: the promotion's `aud-a38k: refuses a prod folder nothing in IIS serves ...`; the module's `with Refuse, refuses a folder nothing serves ...`; deploy's `aud-a38k` real-region test |
| A3b: the `Refuse` other-pool refusal takes the `Allow` wording | `de6f7c5b02ebe3edd157635b22dba8a8` | 3: the promotion's `aud-a38k: refuses a prod folder another pool serves ...`; the module's `with Refuse, refuses a folder another pool serves ...`; deploy's `aud-a38k` real-region test |

A1 is the one the Approach names. Comments and help text were written after the first round. Final
test-file md5s (CRLF): `AppPoolControl.Tests.ps1` `e0abec7017a596f46953206408d4bc73`,
`MoveConfigDbToShared.Tests.ps1` `f777e66a5ff84a5d7b7cfac0a2f9a28b`, `DeployInvariants.Tests.ps1`
`ed34ea6e9f557f159ccb3bdfc19f59de`.

## Gates

One pwsh 7.6.6 session per run, `Import-Module Pester -RequiredVersion 6.2.0` before `PSScriptAnalyzer`
1.25.0:

- `Invoke-ScriptAnalyzer -Severity Error`, one `-Path` per changed file: `tools/AppPoolControl.psm1`,
  `tests/ps/AppPoolControl.Tests.ps1`, `tests/ps/MoveConfigDbToShared.Tests.ps1`,
  `tests/ps/DeployInvariants.Tests.ps1` - 0 errors each. All severities: 7, 2, 17 and 16, the same as
  the `HEAD` copies.
- `Invoke-Pester tests/ps`: 474 passed, 0 failed, 0 skipped, 0 not run (464 before; 10 new).
- Windows PowerShell 5.1.20348.5756 `Parser::ParseFile`: the module, `deploy.ps1`,
  `tools/promote-dev-to-prod.ps1`, `tools/Install-ExchangeAdminWeb.ps1`, `tools/Move-ConfigDbToShared.ps1`
  (the five call sites) and the three changed test files - 0 errors each.
- The installer contains no `deploy.ps1` string (0 occurrences).
- Pure ASCII (every changed file, 0 bytes above 0x7F); `git diff --check HEAD` clean;
  `tools/Test-AsciiOnly.ps1` passed after `git add -A`.

## Files changed

- `tools/AppPoolControl.psm1` - the per-rule wording of the two refusals; help text and comments.
- `tests/ps/MoveConfigDbToShared.Tests.ps1`, `tests/ps/AppPoolControl.Tests.ps1`,
  `tests/ps/DeployInvariants.Tests.ps1` - the tests above.
- This record.

## Known gaps

- **The cutover's frame still says it twice**: under `Allow` the module ends "so nothing will be
  stopped" and the cutover's wrapper then adds "Nothing has been stopped or copied." Both true; the
  wrapper's sentence also covers the copy, so neither was dropped.
- **No IIS was touched**; every refusal was driven through the module's injected probe. The default
  probe has never run against real IIS (`aud-a38c`/`a38f`/`a38i`/`a38j`, unchanged).
- The installer's own path (`-NestedNodes Refuse`) is covered by the module's `Refuse` pins; it has no
  real-module test of its own (its cdt-32 tests use a stand-in), as before.

## Closeout

Fixed on branch; not merged, not pushed. No version bump (ops scripts).
