# aud-a38j: The cutover inherits the nested-node refusal although it never mirrors, and the plan lines still describe the old rule

**Severity**: LOW - two over-refusals and one stale description. `Move-ConfigDbToShared.ps1`
only stops pools and copies databases; it calls `Assert-AppPoolServesPath` (aud-a38g) and since
aud-a38i refuses any IIS node nested inside either instance's folder, although it would not
replace or purge those nodes' files. Deploy and promote also refuse nested nodes that sit inside
the `/XD` excluded folders (`logs`, `config`), which the mirror leaves alone. And the plan-mode
lines and comments in promote and the cutover ("refuse unless IIS serves that folder from this
pool and no other") describe aud-a38f's rule, not aud-a38i's; tests pin that text.
**Status**: Merged to master (`f18431d0`); closed on guard proof; codex review pending
**Branch**: `worktree-agent-aeb07a6371bf7b965` (forked at `36fb150d`)
**Commit**: `40e578f0` (merged `f18431d0`)
**Reviewer**: implementer (surfaced by the aud-a38i/a7e agent, 2026-10-22, candidates 3 and 4 of its hand-back)

## Evidence

At the aud-a38i branch tip `f6b86a46`, `tools/AppPoolControl.psm1`, `Assert-AppPoolServesPath`:
one rule for all five callers (promote `:240`, deploy `:616` and `:929`, installer `:1090`, cutover
`:231`); no parameter distinguishes a caller that mirrors from one that does not, nor the mirror's
excluded subfolders.

## Approach (to be written by the implementing agent before code)

- Give `Assert-AppPoolServesPath` an explicit, mandatory choice for the nested-node rule - a
  parameter such as `-NestedNodes Refuse|Allow` with NO default, so every caller states what it
  does: deploy, promote and the installer pass `Refuse` (they mirror); the cutover passes `Allow`
  with a comment saying it copies databases only. The aud-a38f other-pool refusal and the
  "something must be AT the folder" rule apply to every caller regardless.
- Optionally `-ExcludedSubfolders` naming the mirror's `/XD` folders so nodes strictly inside them
  are not refused; take it only if the robocopy exclusion list is already a single shared value
  the module can be handed (one truth, pointers over copies); otherwise leave the over-refusal and
  record why.
- Update the plan-mode lines and comments in promote and the cutover to describe the rule each
  caller applies; update the Pester pins that hold that text.
- Pester: the cutover passes with a nested node under another pool? NO - the other-pool rule still
  refuses; the cutover passes with a nested node under the same pool; deploy and promote refuse it;
  a caller omitting the parameter fails binding. Mutation at the call site: the cutover passes
  `Refuse`; the parameter given a default. 5.1 `ParseFile` zero errors on every changed script and
  the module; the installer must never contain the string `deploy.ps1`; `Import-Module Pester
  -RequiredVersion 6.2.0` before the analyzer.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); this record was admitted under it.
Ops-script-only: no version bump (the `aud-a34`/`a35`/`a36`/`a38`/`a38c`/`a38f`/`a38i` precedent).
Written before the code.

1. **`tools/AppPoolControl.psm1`, `Assert-AppPoolServesPath -NestedNodes Refuse|Allow`.** Mandatory,
   `ValidateSet('Refuse', 'Allow')`, no default: a caller that omits it fails binding before IIS is
   read, so every caller states the rule it needs.
   - `Refuse` - the `aud-a38i` rule, unchanged: the folder's own nodes run under the named pool, and
     any node rooted strictly inside the folder is refused whatever its pool.
   - `Allow` - the `aud-a38f` rule: every node at or inside the folder runs under the named pool, so
     a nested node under the named pool passes and one under another pool is refused.
   - Both: unreadable IIS refuses; nothing AT the folder refuses; the folder's own nodes under
     another pool refuse. Every `aud-a38f` mechanism is kept (boundary match, virtual-directory read,
     missed-read guard). A pass returns what IIS serves at or inside the folder (under `Refuse` that
     can only be what is at it). Under `Allow` the other-pool refusal says "at or under" and drops
     the mirror clause ("while its files are replaced"); under `Refuse` every message is unchanged.
2. **Callers.** `deploy.ps1` (upgrade `:616`, `-Force` `:929`), `tools/promote-dev-to-prod.ps1`
   (`:240`, `Stop-AppPoolChecked`) and `tools/Install-ExchangeAdminWeb.ps1` (`:1090`) pass
   `-NestedNodes Refuse`: each then mirrors the folder with `robocopy /MIR`.
   `tools/Move-ConfigDbToShared.ps1` (`:231`, `Assert-PoolServesInstance`) passes
   `-NestedNodes Allow`, with a comment saying why: it stops pools, copies databases (backups out to
   `-BackupRoot`, dev's database to the shared path, which must lie outside both folders) and
   rewrites each instance's `appsettings.json`, and never mirrors - nothing of a nested node is
   replaced or purged, and stopping the named pool stops a same-pool node with it. A nested node
   under another pool keeps a worker serving from inside the instance's folder while its database
   is copied, so it is still refused. Every call stays `$null = ...`, inside no catch that swallows.
3. **Text.** The plan-mode lines and comments in promote and the cutover, the installer's step
   description (same `aud-a38f` wording, "and no other"), and the call-site comments in `deploy.ps1`
   and the installer describe the rule each caller applies. Pure ASCII, nothing PS7-only
   (Invariant 6).
4. **`-ExcludedSubfolders`: not taken.** The mirror's exclusion list is not one shared value: it is
   a literal in each robocopy argument list - `deploy.ps1` twice, the promotion's mirror and its
   rollback, the installer - and each copy is pinned by its own test. Handing the module the list
   would make a sixth copy, which is what the condition (one truth, pointers over copies) rules
   out. The over-refusal of a node inside an excluded folder stays, recorded under Known gaps.
5. **Tests.**
   - `tests/ps/AppPoolControl.Tests.ps1`: every existing call passes `-NestedNodes Refuse` (they pin
     the mirroring callers' rule). A new `aud-a38j` context: omitting the parameter fails binding,
     in a hostless runspace (where a missing mandatory parameter is an error, not a prompt), and the
     probe is never asked; the declaration is mandatory, exactly the two values, no default (AST);
     `Allow` passes an application and a virtual directory nested under the named pool and returns
     them; `Allow` still refuses a nested node under another pool, a folder another pool serves,
     nothing at the folder, and unreadable IIS; through the default probe, `Allow` passes an
     application's own virtual directory inside the folder. The promotion's lifted stop step, over
     the real module, refuses a same-pool nested node before anything is stopped. The plan-line pin
     becomes exact; the call-text pin includes `-NestedNodes Refuse`.
   - `tests/ps/DeployInvariants.Tests.ps1`: the cdt-28 call pin includes `-NestedNodes Refuse`; its
     stub records the value it is handed; a new test runs each branch's real region over the real
     module: a same-pool nested node is refused before the stop.
   - `tests/ps/InstallerConfigBackup.Tests.ps1`: the cdt-32 step-text and call pins; the stub records
     the value.
   - `tests/ps/MoveConfigDbToShared.Tests.ps1`: the call pin (`-NestedNodes Allow`) and the plan-line
     pins; the lifted wrapper over the real module passes a same-pool nested node and still refuses
     an other-pool one.
6. **Mutations at the call site**, each with md5 before and after, the failing test named, restore,
   md5 re-confirmed, touch, rerun green: the cutover passes `Refuse`; the parameter is given a
   default (made optional with one, and - inert at run time - added while kept mandatory). Also: the
   module ignores `-NestedNodes` (either way); `Allow` narrowed to the folder's own nodes; promote
   and deploy pass `Allow`; the cutover's plan line reverted.
7. **Gates**: Pester 6.2.0 imported before PSScriptAnalyzer; `-Severity Error` per changed file;
   `Invoke-Pester tests/ps`; Windows PowerShell 5.1 `ParseFile` on the module and the five calling
   scripts; the installer never contains `deploy.ps1`; `git diff --check HEAD`; ASCII lint.

## Guard proof

Built as the Plan. `tools/AppPoolControl.psm1`, `Assert-AppPoolServesPath` takes `-NestedNodes`
(`[Parameter(Mandatory = $true)]`, `[ValidateSet('Refuse', 'Allow')]`, no default). The refusals in
order: IIS unreadable; nothing AT the folder; then the pool check over `$serving` (the folder's own
nodes) under `Refuse` or `$inside` (everything at or inside it) under `Allow`; then, under `Refuse`
only, the `aud-a38i` refusal of anything strictly inside. A pass returns `$inside` (under `Refuse`
that can only be what is at the folder). Every `Refuse` message is the `aud-a38i` one unchanged;
under `Allow` the pool refusal reads "Files at or under ..." and has no "while its files are
replaced".

**Callers** - each `$null = ...`, the result discarded, inside no catch that swallows it (the
cutover's wrapper rethrows through `Write-Fail`, as since `aud-a38g`):

| Call site | Rule | Plan-mode text |
| --- | --- | --- |
| `deploy.ps1:618` (upgrade), `:932` (`-Force`) | `-NestedNodes Refuse` | none (deploy.ps1 has no plan mode); comments state the rule |
| `tools/promote-dev-to-prod.ps1:243` (`Stop-AppPoolChecked`) | `-NestedNodes Refuse` | `:227` "... -NestedNodes Refuse (refuse unless IIS serves that folder from this pool and no other, and nothing else is rooted inside it: the mirror replaces the whole folder)" |
| `tools/Install-ExchangeAdminWeb.ps1:1091` (its confirm step) | `-NestedNodes Refuse` | the step: "Confirm that IIS serves $PublishPath from app pool $AppPoolName and no other, with nothing else rooted inside it (the mirror replaces the whole folder)" |
| `tools/Move-ConfigDbToShared.ps1:238` (`Assert-PoolServesInstance`) | `-NestedNodes Allow`, with the why | `:716`/`:717` "... -NestedNodes Allow (refuse unless IIS serves that folder, and everything rooted inside it, from this pool and no other)" |

| Test | Pins |
| --- | --- |
| `MoveConfigDbToShared.Tests.ps1` `aud-a38g` / `aud-a38j: passes a folder with a node rooted inside it under the same pool` | **the finding**: the cutover's lifted wrapper over the real module passes a same-pool nested node (its probe was asked) |
| `... / aud-a38j: still refuses a node rooted inside the folder under another pool` | the other-pool rule still applies to the cutover |
| `... / asks the module from one place, stating that nodes nested under the same pool are allowed (aud-a38j)` (changed) | AST: the one call is exactly `... -NestedNodes Allow` |
| `... / prints the check for both instances in plan mode ...` (changed) | the two plan lines, exact |
| `AppPoolControl.Tests.ps1` `aud-a38j` / `fails binding when a caller does not say, and never reads IIS` | a hostless runspace (a missing mandatory parameter is an error there, not a prompt): `MissingMandatoryParameter`, and the probe count stays 0 |
| `... / declares -NestedNodes mandatory, limited to Refuse and Allow, with no default` | the declaration, from command metadata and the parameter's AST |
| `... / with Allow, passes nodes nested inside the folder under the named pool, and returns them with the folder's own` | an application (mixed spelling) and a virtual directory under the named pool; a site root above and a prefix sibling ignored |
| `... / with Allow, still refuses a node nested inside the folder under another pool, and blames only it`; `... a folder another pool serves`; `... nothing is at the folder itself`; `... IIS cannot be read` | the rules every caller keeps |
| `aud-a38f: the default probe ...` / `aud-a38j: with Allow, passes an application's own virtual directory inside the folder, under that application's pool` | the same through the default probe's virtual-directory read |
| `aud-a38c` / `aud-a38j: refuses a prod folder with a node rooted inside it under the same pool, before stopping anything` | the promotion's lifted stop step over the real module: refused, no exists probe, no stop |
| `aud-a38c` / `in a dry run prints the check ...` (changed: exact line), `checks the folder the mirror replaces ...` (changed: `-NestedNodes Refuse`) | promote's plan text and call |
| `DeployInvariants.Tests.ps1` / `runs each branch's real region over the real module: a node rooted inside the folder under the same pool is refused before the stop (aud-a38j)` | both deploy branches: lifted region, real module, injected probe - refused after the probe, before the stop; alone, it stops |
| `... cdt-28 (AST)` and `... cdt-28` behavioural (changed) | the call text includes `-NestedNodes Refuse`; the stub records `Refuse` |
| `InstallerConfigBackup.Tests.ps1` `cdt-32` AST and behavioural (changed) | the step text and call; the stub records `Refuse` |
| every other `Assert-AppPoolServesPath` test (changed) | now passes `-NestedNodes Refuse` - they pin the mirroring callers' rule, unchanged |

**Against the unfixed code** - the module and its five callers checked out at `HEAD`, the tests as
changed: 352 tests in the four files, 37 failed - every test that states `-NestedNodes` fails binding
against the old module, and the finding test fails with the `aud-a38i` refusal. The five files were
restored from saved copies, touched, md5s re-confirmed.

Mutations at the call site, each by exact-text replacement asserted to match once, md5 recorded, the
named test files run in a fresh pwsh (Pester 6.2.0), the file restored from its saved bytes, touched,
md5 re-confirmed equal, and the files re-run green. First round on the pre-comment bytes (module
`cc7e482e5bbd937c8cc516fd96ccbafa`, cutover `d70b1b2782dd07c1eaf82df788083178`, promote
`0ab98a7ed7edf5a38cdfc765b2cacf39`, deploy `7272f833dc51201e90d3cbf7dd1d8c78`, installer
`30160f8c726dec8096d16e366e1817af`); re-run on the final bytes with the same results - the md5s below
are the re-run's. CRLF working-tree bytes (`.ps1` is `eol=crlf`; `.psm1` checks out CRLF).

| Mutation | File (final md5) | md5 after | Failed |
| --- | --- | --- | --- |
| J1: the cutover passes `Refuse` | cutover `169fbab7adb11db13a521744f1921da3` | `660bb5481d455668ac01391b5ea62d0d` | 2: `aud-a38j: passes a folder with a node rooted inside it under the same pool` (the `aud-a38i` refusal); `asks the module from one place ...` |
| J2a: the parameter given a default - made optional, `= 'Refuse'` | module `24d583c476ed77b2cba6c9251fae12f2` | `44b9efe7be4b96f163eb2681561a9ad7` | 2: `fails binding when a caller does not say ...` (no binding error); `declares -NestedNodes mandatory ...` |
| J2b: a default added, still mandatory (inert at run time) | module | `72239cb1cf1f60c6892fb76117865e77` | 1: `declares -NestedNodes mandatory ...` (the AST's default) |
| J3: nested nodes refused whatever the caller states | module | `7d256f1bfdcb1448fbed1e84e95d62b4` | 3: both module `Allow` passes and the cutover's same-pool pass |
| J4: nested nodes never refused | module | `cb68523945682157179b3b3b8737e632` | 9: every `aud-a38i` refusal, the `aud-a38f` nested other-pool refusals under `Refuse`, the promotion's and deploy's same-pool tests |
| J5: `Allow` holds only the folder's own nodes to the pool | module | `d014bebcf57b53508e33922a934b1cb5` | 2: module `with Allow, still refuses a node nested ... under another pool`; the cutover's other-pool test |
| J6: the promotion passes `Allow` | promote `c871c5d28e2bdf9b0a5544473a252670` | `45ff40dcbbc255bd7402ccfc3fe657d9` | 2: `aud-a38j: refuses a prod folder with a node rooted inside it ...`; the call-text pin |
| J7: deploy.ps1's upgrade branch passes `Allow` | deploy `76ac332438695017f2802d160451fe02` | `14b345dde0775057f612de4dca6527d1` | 3: cdt-28 AST, cdt-28 behavioural, the real-module test |
| J8: the installer passes `Allow` | installer `8639e2a1fa8061c184feee21ed149a43` | `719f9ad89b63d4eac9bf7f53059cf058` | 2: cdt-32 AST and behavioural |
| J9: the cutover's dev plan line back to the `aud-a38f` wording | cutover | `88b5979bfbcf347393948e82e0843af0` | 1: `prints the check for both instances in plan mode ...` |
| J10: the promotion's plan line back to the `aud-a38f` wording | promote | `e2f456734b4f92e5b42ee82485915ec0` | 1: `in a dry run prints the check and reads nothing from IIS` |

J1 and J2 are the two the Approach names. Comments and help text were written after the first round.
Final test-file md5s: `AppPoolControl.Tests.ps1` `31cb712f4d9e0349fa4ed70779b03048`,
`DeployInvariants.Tests.ps1` `e97a2934ee255a273963a93eb620003b`, `InstallerConfigBackup.Tests.ps1`
`d4238dacf73fafb750e2a038204ba02e`, `MoveConfigDbToShared.Tests.ps1`
`a63984ada0f24fdc5a3d8003fd95a348`.

**`-ExcludedSubfolders`: not taken.** The mirror's exclusion is a literal in each of five robocopy
argument lists - `deploy.ps1:648` and `:963`, the promotion's mirror `:389` and rollback `:418`, the
installer `:1119` - the two deploy copies and the rollback each pinned by their own test. There is no
one value to hand over, so taking the parameter would have made a sixth copy. Also checked locally on
temp folders: `robocopy /MIR /XD logs config` leaves a destination `wwwroot\config\` alone too - a
bare `/XD` name matches at any depth - so "strictly inside `<folder>\logs` or `<folder>\config`"
would be a second, narrower definition of the exclusion as well as a copy of it.

## Gates

One pwsh 7.6.6 session, `Import-Module Pester -RequiredVersion 6.2.0` before `PSScriptAnalyzer`
1.25.0:

- `Invoke-ScriptAnalyzer -Severity Error`, one `-Path` per changed file: `tools/AppPoolControl.psm1`,
  `deploy.ps1`, `tools/promote-dev-to-prod.ps1`, `tools/Install-ExchangeAdminWeb.ps1`,
  `tools/Move-ConfigDbToShared.ps1`, and the four test files - 0 errors each.
- `Invoke-Pester tests/ps`: 464 passed, 0 failed, 0 skipped, 0 not run (452 before; 12 new).
- Windows PowerShell 5.1.20348.5756 `Parser::ParseFile`: the module, the four calling scripts and the
  four test files - 0 errors each. Under 5.1 the module imports, exports its four functions, refuses a
  same-pool nested node under `Refuse`, passes it under `Allow`, and refuses an other-pool one under
  `Allow`.
- The installer contains no `deploy.ps1` string (0 occurrences).
- `git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` passed after `git add`.

## Files changed

- `tools/AppPoolControl.psm1` - `-NestedNodes`, the two rules, help text and comments.
- `deploy.ps1`, `tools/promote-dev-to-prod.ps1`, `tools/Install-ExchangeAdminWeb.ps1`,
  `tools/Move-ConfigDbToShared.ps1` - each call states its rule; the plan lines and step text; comments.
- `tests/ps/AppPoolControl.Tests.ps1`, `tests/ps/DeployInvariants.Tests.ps1`,
  `tests/ps/InstallerConfigBackup.Tests.ps1`, `tests/ps/MoveConfigDbToShared.Tests.ps1`.
- This record.

## Known gaps

- **Over-refusal kept for nodes inside the mirror's excluded folders** (`-ExcludedSubfolders` not taken,
  above). A node rooted inside `<folder>\logs` or `<folder>\config` is still refused by deploy, the
  promotion and the installer, though the mirror would leave it alone. Lifting it needs the exclusion
  list made one shared value first, carrying robocopy's any-depth name match; an owner decision.
- **The default probe has never run against real IIS** (`aud-a38c`/`a38f`/`a38i`, unchanged). Not run.
- The "nothing at the folder" refusal still ends "Refusing to replace files that may still be served."
  for every caller, the cutover included (since `aud-a38g`); the cutover's wrapper says what the
  cutover does instead. Not changed.
- No deploy, promotion, install or cutover was run and no IIS was touched; plan modes and lifted
  regions only, through the injected probe.

## Closeout

Fixed on branch; not merged, not pushed. No version bump (ops scripts).
