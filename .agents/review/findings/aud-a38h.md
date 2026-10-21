# aud-a38h (committed as aud-a38f in `3dd3ca5`; renamed because codex independently assigned aud-a38f to the nested-IIS finding): the cutover reports a pool started that IIS only agreed to start

**Severity**: MEDIUM. The `aud-a38` defect - "IIS accepting a start request is not the pool
running" - in the cutover, and its worst case is on the failure path: a cutover that changed
nothing restarts both pools "so the outage ends here", and says it did, whether or not either
pool came back.
**Status**: Merged to master; codex group review pending
**Branch**: `worktree-agent-a6bb7956d82362f14`
**Commit**: `3dd3ca5` (merged)
**Reviewer**: implementer (found while fixing `aud-a38e`)

## Evidence

`tools/Move-ConfigDbToShared.ps1` at `ca6575a`, `Start-AppPoolChecked` (lines 219-239):

```powershell
    Invoke-PlanOrAction "Start app pool $Name" {
        $started = $false
        for ($i = 1; $i -le 5; $i++) {
            try {
                $state = (Get-ItemProperty "IIS:\AppPools\$Name").State
                if ($state -eq 'Started') { $started = $true; break }
                Start-WebAppPool -Name $Name -ErrorAction Stop
                $started = $true
                break
            } catch { if ($i -eq 5) { throw }; Start-Sleep -Seconds 3 }
        }
        if (-not $started) { Write-Fail "App pool $Name did not start." }
        Write-Ok "App pool $Name started"
    }
```

`$started` is set the moment `Start-WebAppPool` returns, which is when IIS has accepted the
request. Nothing asks IIS afterwards. A pool whose identity's password has expired, whose
runtime is missing, or that rapid-fail protection stops again, is left Stopped with no
exception raised (the `aud-a38` record's list, `tools/AppPoolControl.psm1`
`Start-AppPoolVerified`'s reason for existing), and this prints "App pool X started".

Two callers:

- **Step 7** (lines 758-759), inside the cutover's `try`. Here step 8 catches it later: no
  "Config store schema ready" line within 90 s. But it is diagnosed as "Nothing answered for
  this instance on localhost ... Check the site bindings", after "OK App pool X started" - the
  transcript points away from the pool.
- **The catch**, when nothing was repointed yet (lines 772-781): "Both instances are unchanged;
  restarting their app pools so the outage ends here", then `Start-AppPoolChecked` for each pool
  inside a `try` whose catch warns "Could not restart app pool ... Start it by hand". That
  warning can only fire on a throw, and a pool that IIS accepted the start for and then left
  stopped does not throw. Nothing later checks. The run ends on the original error with the
  operator told the instances were restarted.

**Mechanism: no error at all**, on both paths - the start request succeeds, and nothing
compares the pool's state with what the message says.

## Predicted observable failure

A cutover fails at step 3 (the dev backup fails verification). The catch restarts both pools.
Prod's service account password was rotated last week and its pool has been running on a
cached logon since; the restart cannot log on, and IIS leaves the pool stopped. The transcript
says "restarting their app pools so the outage ends here", "OK App pool ExchangeAdminWeb
started", and "Nothing else was changed". Prod is down, and the operator, told it was
restarted, is looking at the backup error.

## Plan (approved by the owner's EVERYTHING go)

Owner, 2026-10-21: "fix all the bugs that you know about and any more you find while doing
so" and "... EVERYTHING." (`.agents/decisions.md`, 2026-10-21 "Every open review follow-up
is approved for implementation"). Found while fixing `aud-a38e` in the same script.

1. **`Start-AppPoolChecked` becomes the verified start**: inside the same plan-or-act wrapper,
   `$null = Start-AppPoolVerified -Name $Name` (the module the script imports since `aud-a38e`)
   then "App pool X confirmed started". The module issues the start, polls IIS until the pool
   reports Started (60 s), and throws if it never does, if its state cannot be read, or if the
   pool does not exist - an absent pool is an error on this side, as `aud-a38` set it.
2. **Both callers keep their shape.** Step 7's throw goes to the cutover's catch, which (both
   instances repointed by then) prints the restore commands, now with an accurate reason instead
   of a log-line timeout 90 s later. The failure path's per-pool `try` now sees a pool that did
   not come back and prints its "Could not restart ... Start it by hand" warning, with the
   module's reason.
3. **The five-attempt retry goes.** It retried a start that THREW; the verified start captures
   that throw and decides by the state it then polls for, so a retry of the request is not what
   makes it correct. Consistent with the promotion's `Start-AppPoolChecked` (`aud-a38`).
4. **Plan mode** prints a plan line naming the confirmation and starts nothing. The existing
   plan-mode test's prod-start assertion is anchored on the end of the old line, so it moves to
   the new one.
5. **Guards** in `tests/ps/MoveConfigDbToShared.Tests.ps1`, beside `aud-a38e`'s and in its
   shape: the lifted start step driven through the module's injectable probes (confirmed
   Started; left Stopped after the start - the finding; absent; plan mode), and AST (no
   `Start-WebAppPool` command anywhere; exactly one `Start-AppPoolVerified -Name $Name`, inside
   the action block plan mode never runs).
6. **No version bump**: ops script only. Pure ASCII, CRLF in the working tree, 5.1-parsed.

Mutations planned at the call site: the pre-fix start restored; the verified start's answer
replaced by an unverified `Start-WebAppPool`; the verified start moved out of the plan-mode
wrapper.

## Approach

As planned. `tools/Move-ConfigDbToShared.ps1` after the fix, lines 235-248: `Start-AppPoolChecked`
is the plan line ("Start app pool X and confirm IIS reports it Started"), the comment,
`$null = Start-AppPoolVerified -Name $Name` and "App pool X confirmed started". Its callers
are untouched: step 7 (767-768) and the failure path's per-pool restart (785, inside its `try`
whose catch warns). `tests/ps/MoveConfigDbToShared.Tests.ps1`: the existing plan-mode
assertion for prod's start line moves to the new wording, and the `aud-a38f` block.

## Files changed

- `tools/Move-ConfigDbToShared.ps1` - `Start-AppPoolChecked`.
- `tests/ps/MoveConfigDbToShared.Tests.ps1` - one existing assertion re-worded, the `aud-a38f`
  block.
- This record.

## Guard proof

The start step is driven the way `aud-a38e` drives the stop step: lifted out of the parsed
file unchanged, with the real `Start-AppPoolVerified` underneath and its probes injected
through `$PSDefaultParameterValues`, each wrapped in a block that returns it and each test
asserting its own probe was asked. `Write-Plan` and `Start-WebAppPool` are recording stand-ins,
and `Get-ItemProperty` is mocked to answer `'Stopped'`, so the pre-fix start pasted back reaches
no IIS either.

| Test (`tests/ps/MoveConfigDbToShared.Tests.ps1`, `aud-a38f: ...`) | Pins |
| --- | --- |
| `starts the pool through the verified start and reports it only when IIS confirms Started` | the start goes through `Start-AppPoolVerified` (its exists probe and start action used); the bare cmdlet is not called |
| `refuses a pool that IIS leaves stopped after accepting the start` | **the finding**: the start is issued, the state probe answers Stopped, and the step throws "is 'Stopped' after 0 seconds, not Started" instead of reporting it started |
| `refuses an absent pool, which cannot be started` | an absent pool throws before any start is issued |
| `in plan mode prints one plan line and touches no pool` | no probe, no start, no IIS read; exactly the one plan line |
| `calls Start-WebAppPool nowhere in the cutover` | AST: zero `Start-WebAppPool` commands (the restore instructions and the restart warning print it as text) |
| `starts through Start-AppPoolVerified once, inside the action plan mode never runs` | AST: exactly one call, `Start-AppPoolVerified -Name $Name`, inside the script block handed to the one `Invoke-PlanOrAction` in the start step |
| `Move-ConfigDbToShared.ps1 plan mode` / `prints all eight steps and changes nothing` (existing, one assertion re-worded) | prod's start plan line is exactly the new one |

Mutations on `tools/Move-ConfigDbToShared.ps1`, fixed md5 `28B1C5759E598D316018EE08CF67FF63`
(pre-comment; working-tree bytes, CRLF; the index holds LF). Each applied by exact-text
replacement asserted to match once, confirmed by md5, run against `MoveConfigDbToShared.Tests.ps1`
and `DeployInvariants.Tests.ps1` (160 tests), then the fixed bytes restored, touched and
md5-confirmed. The final, commented file ran green in the full suite (Gates).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| M1: the pre-fix start restored - state read, `Start-WebAppPool`, `$started = $true`, the retry loop, the old plan line (the mutant is byte-identical to `aud-a38e`'s committed file, `faead5b`) | `A5F9840EE38609E3E67DB09E385ACEBC` | 7: all six `aud-a38f` tests; `prints all eight steps and changes nothing` |
| M2, present but unverified: the new plan line and message kept, the body an unconfirmed `Start-WebAppPool -Name $Name -ErrorAction Stop` | `7A3C3E6106BEC8A196B4AC182E12DC9D` | 5: the three apply-path behavioural tests; `calls Start-WebAppPool nowhere`; `starts through Start-AppPoolVerified once` |
| M3: the verified start moved out of the plan-mode wrapper, above `Invoke-PlanOrAction` | `1FE642B54D73ED6E2A7A8C4030CF85A5` | 6: `in plan mode prints one plan line and touches no pool`; `starts through Start-AppPoolVerified once, inside the action ...`; and four existing plan-mode tests that run the whole script (`prints all eight steps`, `is plan mode by default`, and A39's `still accepts a path outside both trees` and `does not mistake a sibling folder ...`), because a plan run now asked IIS for a pool and refused it as absent |

Post-comment md5 `EBF1706FD93D785469DFA52FE89125B0`; the test file `94056E91956FB04BAA4A4B53E4F70BB6`.

## Gates

One pwsh 7.6.6 session, Pester 6.2.0 imported before the analyzer, on the final code (on top
of `cdr-followup-2`, `e03e7c8`):

- `Invoke-ScriptAnalyzer -Path . -Recurse`: 0 errors. 327 warnings, 36 under the untracked
  `bin\`; the two this change adds are `PSUseShouldProcessForStateChangingFunctions` on the
  test stand-ins `Start-WebAppPool` and `New-StartProbe`, as `aud-a38e`'s two.
- `Invoke-Pester tests/ps`: 293 passed, 0 failed, 0 skipped, 0 not run (6 of them new).
- Windows PowerShell 5.1.20348.5756 `Parser::ParseFile`: `tools/Move-ConfigDbToShared.ps1`,
  `tools/AppPoolControl.psm1`, `tests/ps/MoveConfigDbToShared.Tests.ps1` - 0 errors each.
- `tools/Test-AsciiOnly.ps1` after `git add`: passed. `git diff --check HEAD`: clean.
- No .NET build or test for its own sake - no C# or Razor changed in this commit.

## Known gaps

- **No cutover was run and no IIS was touched**, as for `aud-a38e`.
- **The retry of a throwing start is gone.** One case it covered: a pool still draining after a
  stop that timed out, on the failure path. IIS refuses a start while a pool is Stopping; the
  old loop re-issued the start up to five times over about twelve seconds, and the verified
  start issues it once and then waits out its 60 s for Started. If the drain ends inside those
  twelve seconds the old shape restarted the pool and this does not - it warns "Could not
  restart ... Start it by hand", which is now true rather than optimistic. Promotion's start has
  the same single-request shape (`aud-a38`).
- **Step 8 still does the end-to-end check.** A pool can report Started and the application
  inside it still fail to open the shared database; "Config store schema ready" remains the
  proof of that, unchanged.
