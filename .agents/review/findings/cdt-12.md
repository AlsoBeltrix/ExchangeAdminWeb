# cdt-12: a -Force deploy never checks the live config database after it restarts the app

**Severity**: MEDIUM. The upgrade path of `deploy.ps1` ends with a post-deploy check of the live
config database - present, and passing `PRAGMA integrity_check` - and WARNS, naming the
pre-deploy backup, when it is not. The `-Force` path has no such check, although it is the path
that regenerates `appsettings.json` and so the one most able to leave the app pointed at a
different, missing or damaged database (cdt-8 was exactly that). A `-Force` run ends on "Deploy
complete (INSTALL)." with nothing having looked at the database the restarted app will open.
**Status**: Merged to master; codex group review pending
**Branch**: `cdt-11-14-installer-deploy` (worktree `agent-a1b04b356d91eeed2`)
**Commit**: `4c0364e` (merged)
**Reviewer**: implementer (surfaced by the cdt-8/9/10 agent)

## Evidence

`deploy.ps1` at `5784583` (unchanged since `27f8167`). The upgrade branch, after its inner
`finally` restarts the pool:

```
694:         $liveDb = Resolve-ConfigDbPath -PublishPath $PublishPath
695:         if (Test-Path -LiteralPath $liveDb) {
696:             try {
697:                 Test-SqliteConfigDbIntegrity -DbPath $liveDb -AppPath $PublishPath | Out-Null
698:                 Write-Success "Config DB verified: integrity_check passed on the live store at $liveDb"
699:             } catch {
700:                 Write-Warn "POST-DEPLOY CHECK: live config DB integrity check FAILED: ...
701:             }
702:         } elseif ($configStorePathSetting) {
703:             Write-Warn "POST-DEPLOY CHECK: ConfigStore:Path names $liveDb but it does not exist after deploy. ...
704:         } else {
705:             Write-Warn "POST-DEPLOY CHECK: no config DB at $liveDb after deploy. ...
706:         }
```

The fresh-install branch (lines 708-920), which every `-Force` run takes, ends with the inner
`try`/`finally` that mirrors, regenerates `appsettings.json` and restarts the pool:

```
909:         } finally {
910:             Write-Step "Starting app pool"
911:             Start-AppPoolWithRetry -Name $AppPoolName
...
918:             Remove-Item $StagingPath -Recurse -Force -ErrorAction SilentlyContinue
919:         }
920:     }
```

and contains no `Test-SqliteConfigDbIntegrity` and no `Resolve-ConfigDbPath` after line 724
(the pre-change resolve cdt-9 added). The next output is line 948, "Deploy complete ($mode).".

**Mechanism: no error at all** - there is no check to fail. Whatever the regenerated file names,
present or not, intact or not, the run reports success.

Also in the upgrade branch's comment, a stated reason that is false:

```
686: # above (it changes by design across the pool restart via WAL checkpoint + startup seeding),
687: # so verify it properly here instead: the app pool was started above, so the schema has been
688: # migrated/seeded.
```

Starting an app pool does not initialise the application: IIS starts an ASP.NET Core app on the
first request, and this site refuses an anonymous one before the app is asked to start
(`aud-a40`, independently confirmed). So the check verifies the database the new build is about
to open, not the result of its migrations. The check is still worth having - presence, and a
store that is intact after the deploy - but not for the reason the comment gives.

## Plan (approved by the owner's EVERYTHING go)

1. In the fresh-install branch, after the inner `try`/`finally` (pool restarted, manifest written,
   staging removed), the upgrade path's check with the same posture: `$liveDb =
   Resolve-ConfigDbPath -PublishPath $PublishPath`, re-resolved from the regenerated
   `appsettings.json` - which carries the kept `ConfigStore:Path` since cdt-8 - so it checks the
   file the app will open; `Test-SqliteConfigDbIntegrity -DbPath $liveDb -AppPath $PublishPath`
   (staging is gone; the publish folder holds the same build); a failure WARNS and names the
   pre-deploy backup; a configured path that is missing WARNS; no key and no database WARNS with
   the upgrade path's text. Same three messages, so an operator reads one vocabulary.
2. Like the upgrade check it is reached only on success: a throw in the inner `try` propagates
   past it. It is unconditional within the branch, so a genuine fresh install runs it too and gets
   the existing "no config DB ... it will be created on first app start" warning, which is
   accurate there (the app creates it on its first request).
3. Correct the upgrade check's comment (plan step 1's comment says the same): the pool restart
   does not start the application, so the check verifies what the new build will open.

Assumptions, recorded: warn, not throw, exactly as the upgrade path and as briefed - the files are
already deployed and the pool is running; the verified pre-deploy backup (cdt-9) is the rollback.
Not changed: the upgrade check itself; the drift inventory (cdt-13).

Constraints: no plan mode in `deploy.ps1` (`tools/deploy-pipeline.ps1 -PlanOnly` skips it), so
Invariant 4 adds no plan line; nothing prompts. Pure ASCII, no BOM, LF in the index, no ternary,
no `??`, no PS7-only parameters; parsed under Windows PowerShell 5.1. The cdt-6 rule holds: the
new commands sit in the outer `try` body whose `finally` removes staging. No version bump:
ops-script-only.

Files: `deploy.ps1`, `tests/ps/DeployInvariants.Tests.ps1`, this record.

Guard: an AST test on the fresh-install branch - exactly one `$liveDb = Resolve-ConfigDbPath
-PublishPath $PublishPath`, unconditional, after the `try` whose `finally` restarts the pool;
exactly one `Test-SqliteConfigDbIntegrity -DbPath $liveDb -AppPath $PublishPath` after it, whose
only conditional ancestor is `if (Test-Path -LiteralPath $liveDb)`, inside a `try` whose single
catch warns and neither throws nor calls `Write-Fail`; the `elseif ($configStorePathSetting)` and
`else` branches warn. Mutations: the block removed; moved before the pool restart; the catch
throwing; `-AppPath $StagingPath`; the block made conditional; the `5784583` shape.

## Approach

As planned. In `deploy.ps1` after the fix:

- Lines 923-948, the end of the fresh-install branch, after the inner `try`/`finally` (pool
  restart at 913): a comment, `$liveDb = Resolve-ConfigDbPath -PublishPath $PublishPath` (936),
  and the upgrade path's three-way check, `Test-SqliteConfigDbIntegrity -DbPath $liveDb -AppPath
  $PublishPath` at 939, with the same three warnings.
- Lines 684-690: the upgrade check's comment no longer claims the pool restart has migrated the
  schema.

Order in the `-Force` path after the fix, unchanged up to the restart: the read, the resolve and
the refusal, the verified backup, the IIS web application, the job check, the pool binding check,
the stop, the mirror, the regenerated `appsettings.json`, the restart, manifest and staging
removal (inner `finally`, 911-921), then the live check (936-948), then the outer `finally`
(950). The new statements are in the outer `try` body, so the cdt-6 test still covers them.

Not reproduced on a host: `deploy.ps1` needs elevation, IIS and the WebAdministration provider,
and this was not run. The check's commands are the upgrade path's, verbatim; the module function
it calls is covered behaviourally by `SqliteConfigBackup.Tests.ps1`.

## Guard proof

| Test (`tests/ps/DeployInvariants.Tests.ps1`) | Pins |
| --- | --- |
| `checks the live config database after a -Force deploy restarts the pool, and only warns (cdt-12)` | Parses the AST of the fresh-install branch: one `try` whose `finally` calls `Start-AppPoolWithRetry`; exactly one `$liveDb = Resolve-ConfigDbPath -PublishPath $PublishPath`, with no conditional ancestor in the branch, starting after that `try` ends; exactly one `Test-SqliteConfigDbIntegrity` with `-DbPath $liveDb -AppPath $PublishPath`, after the resolve, whose only conditional ancestor is `if (Test-Path -LiteralPath $liveDb)`; the `try` around it has one catch whose first statement is the `Write-Warn "POST-DEPLOY CHECK: live config DB integrity check FAILED...` and which contains no `throw` and no `Write-Fail`; the `elseif ($configStorePathSetting)` and `else` branches each start with their `Write-Warn "POST-DEPLOY CHECK: ...` |
| `runs a post-deploy live DB integrity check (DB is excluded from file drift by design)` and `runs the db tool from the binary it is about to deploy ...` (existing) | Still green: the upgrade check is unchanged apart from its comment |
| `removes the staging publish on every failure after it, ... (cdt-6)` (existing) | Still green: the new commands sit in the outer `try` body |

Mutations on `deploy.ps1` (fixed md5 `70661F7078BACA0242381A19E5B92309`, pre-comment; md5s of the
content as committed, LF), each applied by a script that fails unless every edit matches exactly
once, run against the whole `DeployInvariants.Tests.ps1`, then the original bytes restored,
touched, md5 re-confirmed; the restored file re-run green (94 passed). Post-comment md5
`4CDA558BBAD34827D70C4A030A9AEDE2` (94 passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| R1 the block removed | `78DABC0808F20A330D7F45D7423E911B` | the cdt-12 test ("Expected 1, because the -Force path must resolve the database it checks, but got 0") |
| R2 the block moved into the inner `try`, before the pool restart | `69EC1C88257010CCC6646E8AD7CAD7A6` | the cdt-12 test ("Expected the actual value to be greater than 52423, because the check runs after the pool restart, but got 50762") |
| R3 the catch throws instead of warning | `E509AACDB00869611C8BFB7435734DBB` | the cdt-12 test ("Expected like wildcard 'Write-Warn "POST-DEPLOY CHECK: live config DB integrity check FAILED*' to match 'throw'") |
| R4 `-AppPath $StagingPath` | `AB3DE76C62F56E4866400CBF2DC92425` | the cdt-12 test ("Expected: '$PublishPath' But was: '$StagingPath'") |
| R5 the block wrapped in `if ($Force -and -not (Test-Path $configPath))`, never true there | `C4EED79756F69EA3D6252B898E6ADBF3` | the cdt-12 test ("Expected $null or empty, because every -Force run that gets this far is checked, but got if ($Force -and -not (Test-Path $configPath)) ...") |
| R6 pre-fix shape (`5784583`) | `F8F2A93158CB6F0BA19FC2A3447A79BD` | the cdt-12 test ("Expected 1 ... but got 0") |
