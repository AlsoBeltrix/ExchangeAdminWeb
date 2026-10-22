# aud-a38k: The "nothing at the folder" refusal tells the cutover it is refusing to replace files it never replaces

**Severity**: LOW - wording only. `Assert-AppPoolServesPath`'s refusal when IIS serves nothing at
the folder says "Refusing to replace files that may still be served" to every caller, the cutover
included; `Move-ConfigDbToShared.ps1` replaces no files, so the operator reads a reason that does
not describe what was refused. Dates from aud-a38g; the aud-a38j `-NestedNodes Allow` path kept the
text.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; plan written, in progress
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

Pending.
