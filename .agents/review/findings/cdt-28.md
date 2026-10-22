# cdt-28: deploy.ps1 confirms the alias's pool, not that nothing else serves the folder it mirrors

**Severity**: HIGH. Before stopping the pool and mirroring, both `deploy.ps1` branches ask IIS
which pool serves the one application `$ParentSite/$AppAlias`, and refuse unless it is
`-AppPoolName` (review of aud-a35, cdt-19). Nothing asks whether any OTHER site or application has
its files in `$PublishPath`. One that does, under a different pool, is never looked at: the run
stops `-AppPoolName`, confirms it stopped, and `robocopy /MIR` replaces files that the other pool is
still serving - the live-file replacement `Assert-AppPoolServesPath` (aud-a38c,
`tools/AppPoolControl.psm1`) was written to close, and which the promotion script already calls
before its stop. From the codex review of the cdt-11..cdt-27 merge (ops-batch-3, its finding 1).
**Status**: Merged to master; codex group review pending
**Branch**: `worktree-agent-a80bd818fc511a34f` (worktree `agent-a80bd818fc511a34f`)
**Commit**: `7548b56` (merged)
**Reviewer**: codex / gpt-5.5-dzs / xhigh, ops-batch-3, 2026-10-21

## Evidence

`deploy.ps1` at `2478ede`, the upgrade branch (the fresh-install branch, which every `-Force` run
takes, has the same lines at 893-915):

```
600:         $boundPool = try { (Get-WebApplication -Name $AppAlias -Site $ParentSite -ErrorAction Stop).applicationPool } catch { $null }
...
604:         if (-not $boundPool) {
605:             Write-Fail "Cannot read which app pool serves IIS application $ParentSite/$AppAlias ...
...
607:         if ($boundPool -ne $AppPoolName) {
608:             Write-Fail "IIS application $ParentSite/$AppAlias runs under app pool '$boundPool', not '$AppPoolName'. ...
...
615:         $poolState = Stop-AppPoolVerified -Name $AppPoolName
...
619:         if ($poolState -ne 'Stopped') {
...
641:             & robocopy @robocopyArgs
```

The one IIS question is about one node. `Assert-AppPoolServesPath` (exported by
`tools/AppPoolControl.psm1`, which `deploy.ps1` already imports at line 194 for
`Stop-AppPoolVerified`) asks IIS for every site root and application, keeps those whose files are
at the folder, and throws unless there is at least one and every one runs under the named pool.
The promotion script calls it right before its verified stop
(`tools/promote-dev-to-prod.ps1:240`, `$null = Assert-AppPoolServesPath -Name $Name -PhysicalPath
$PublishPath`); `deploy.ps1` never calls it. The upgrade branch does not even re-point the alias at
`$PublishPath` (line 430 sets the folder only when it creates the application), so the alias check
does not show that the alias serves the folder being mirrored either.

**Mechanism: no error at all**, reproduced under Windows PowerShell 5.1.20348 (deploy's runtime).
Scratch harness: each branch's real region, from the `$boundPool` lookup through the check on the
stop result, lifted from the `2478ede` file by the 5.1 parser and run under `Stop`, with IIS
stubbed as one picture - `Example Site/ExampleApp` bound to `ExamplePool` with its files at
`D:\example\publish`, and a second site's application, `Other Site/Copy`, with its files at the
same folder under `OtherPool` - and every IIS read logged:

| Branch | IIS picture | Result | IIS reads made |
| --- | --- | --- | --- |
| upgrade | the folder is also served by `Other Site/Copy` under `OtherPool` | reached the mirror | `Get-WebApplication(Name=ExampleApp;Site=Example Site)`, `Stop-AppPoolVerified(ExamplePool)` |
| `-Force` | the same | reached the mirror | the same |
| upgrade, `-Force` | only the alias serves the folder | reached the mirror | the same |

The other application is never read, so the run cannot tell the two pictures apart.

## Plan (approved by the owner's EVERYTHING go)

In both branches, the same change:

1. Immediately before `$poolState = Stop-AppPoolVerified -Name $AppPoolName`, after the alias
   checks: `$null = Assert-AppPoolServesPath -Name $AppPoolName -PhysicalPath $PublishPath` - the
   promotion script's parameter shape, its result discarded as the promotion script discards it.
   It throws when IIS cannot be read, when no site or application has its files at
   `$PublishPath`, and when any that does runs under another pool. Nothing catches it: the only
   `try` around the branch is the outer one, which has a `finally` (staging removal) and no
   `catch`, so the run ends there, before the stop and the mirror - fail closed, as the module does.
2. The alias checks stay. They catch a different mistake (a mistyped `-AppPoolName` or
   `-AppAlias`, the alias bound to another pool) with their own message, and they run first.
3. `tools/AppPoolControl.psm1` is not edited (another stream is changing its internals for
   aud-a38f); the call passes only `-Name` and `-PhysicalPath`.

Assumptions, recorded:

- The folder is compared as the module compares it (environment variables expanded, made
  absolute, trailing separators dropped, case-insensitive equality). An IIS configuration that
  spells the folder another way - a junction, an 8.3 short name - is now refused, with what IIS
  reported in the message. Fail closed; no host's IIS configuration was read for this.
- An upgrade whose `-PublishPath` is not the folder the alias serves - which this branch never
  re-points - is now refused ("No IIS site or application has its files at ...") instead of
  mirroring into a folder nothing serves and reporting success.
- The refusal comes after the IIS configuration changes made earlier in the run (pool settings,
  ACLs, authentication) and before anything is stopped or mirrored, like every other refusal at
  this point; "Nothing was deployed" holds.

Constraints: no plan mode in `deploy.ps1`; pure ASCII, no BOM, LF in the index, no ternary, no
`??`, no PS7-only parameters; parsed under Windows PowerShell 5.1. No version bump: ops-script-only.

Files: `deploy.ps1`, `tests/ps/DeployInvariants.Tests.ps1` (two new tests; the cdt-19 behavioural
test, whose lifted region now contains the call, gets a pass-through stub for it), this record.

Guards: an AST test per branch - the statement right before the verified stop is exactly the
call, as a statement of the branch itself (nothing conditional or catching in between), after the
alias checks; exactly two calls in the script, and no `try` with a `catch` around either. And a
behavioural test running each branch's real region with the IIS calls stubbed: a folder also
served by another pool is refused after the alias lookup and the folder check, before any stop;
an alias bound elsewhere is refused before the folder check; only both passing reach the stop.
Mutations: the upgrade call removed; the `-Force` call removed; the call moved after the stop
check; the call swallowed by a `try`/`catch`.

## Approach

As planned, the same change in both branches of `deploy.ps1`:

- Upgrade: a comment at 611-615 and `$null = Assert-AppPoolServesPath -Name $AppPoolName
  -PhysicalPath $PublishPath` at 616, after the alias checks (600-609) and before the A35 comment
  and `$poolState = Stop-AppPoolVerified -Name $AppPoolName` (622).
- Fresh install / `-Force`: the same at 911-916, stop at 922.

The same 5.1 harness as in Evidence, against the fixed file, now with the module's real
`Assert-AppPoolServesPath` (run inside the module, its IIS probe fed the same stubbed picture):

| Branch | IIS picture | Result | IIS reads made |
| --- | --- | --- | --- |
| upgrade, `-Force` | the folder is also served by `Other Site/Copy` under `OtherPool` | refused: "The files at 'D:\example\publish' are served by an app pool other than 'ExamplePool': Other Site/Copy runs under 'OtherPool'. Stopping 'ExamplePool' would leave that application running while its files are replaced. Name the pool that serves them, or fix the binding." | the alias lookup, then every site and application; no stop |
| upgrade, `-Force` | only the alias serves the folder | reached the mirror | the alias lookup, every site and application, then `Stop-AppPoolVerified(ExamplePool)` |

## Guard proof

| Test (`tests/ps/DeployInvariants.Tests.ps1`) | Pins |
| --- | --- |
| `asks IIS for every site and application serving the folder right before each verified stop, and nothing catches its refusal (cdt-28, AST)` | For the upgrade and the fresh-install branch alike: the statement right before `$poolState = Stop-AppPoolVerified -Name $AppPoolName` is exactly `$null = Assert-AppPoolServesPath -Name $AppPoolName -PhysicalPath $PublishPath`, as a statement of the branch's own block, and it comes after the two alias checks; the script calls `Assert-AppPoolServesPath` exactly twice, and no `try` around either call has a `catch` |
| `runs each branch's real region: a folder also served by another pool is refused before anything is stopped (cdt-28)` | Lifts each branch's region (the alias lookup through the stop-result check) by AST and runs it under `Stop` with `Get-WebApplication`, `Assert-AppPoolServesPath` and `Stop-AppPoolVerified` stubbed: a folder another pool also serves calls `lookup,serves(ExamplePool|D:\example\publish)` and throws the module's "served by an app pool other than 'ExamplePool'" refusal; an alias bound to another pool calls only `lookup` (the alias check still runs first); only both passing reach `stop(ExamplePool)`, and the check's result is not written to the output |
| `runs each branch's real binding-and-stop region ... (cdt-19)` (existing, one stub added) | Its lifted region now contains the call; a pass-through `Assert-AppPoolServesPath` stub that records nothing keeps its call sequences as they were |

Mutations on `deploy.ps1` (fixed md5 `D988F5F686F4E470BA9F019094FDA783`, pre-comment; md5s of the
content as committed, LF), each applied by a script that fails unless every edit matches exactly
once (an edit targeting one branch is anchored on that branch's unique `Write-Step` line), run
against the whole `DeployInvariants.Tests.ps1`, then the original bytes restored, touched, md5
re-confirmed, and the suite rerun green (107 passed) each time. Post-comment md5
`96CE0EF9E725956C25D082F664C6F0AE` (107 passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| R1 upgrade: the call removed | `E31B149771187DD6767AE15566091798` | both cdt-28 tests (AST: "Expected: '$null = Assert-AppPoolServesPath -Name $AppPoolName -PhysicalPath $PublishPath' But was: 'if ($boundPool -ne $AppPoolName) {..."; behavioural: "Expected: 'lookup,serves(ExamplePool\|D:\example\publish)' But was: 'lookup,stop(ExamplePool)'" - the pool was stopped with the folder served elsewhere) |
| R2 `-Force`: the call removed | `E72A80D1904B1D17FA767098E51DB1BF` | the same two, for `-Force` |
| R3 upgrade: the call moved after the stop and its check | `7481AD5D53419ECFDB1B81345B1AFCAC` | the same two (the statement before the stop is the alias check again; the stop ran first) |
| R4 `-Force`: the call wrapped in a `try` whose `catch` only warns | `0AEEC5530FBF2EC6F49E6431EEC5B4EE` | both (AST: "But was: 'try { $null = Assert-AppPoolServesPath ... } catch { Write..."; behavioural: "But was: 'lookup,serves(ExamplePool\|D:\example\publish),stop(ExamplePool)'" - the refusal was swallowed and the pool stopped) |
| R5 pre-fix shape (`2478ede`, byte-identical: the same md5 as that commit's file) | `01398B82E8BDEC052F691026C8999508` | both, as R1 |

Not run: a real IIS deploy (needs elevation and a host). The module's own decision is covered by
its tests (`tests/ps/AppPoolControl.Tests.ps1`, Describe `Assert-AppPoolServesPath`); here it is
stubbed, and IIS itself stays outside every test.
