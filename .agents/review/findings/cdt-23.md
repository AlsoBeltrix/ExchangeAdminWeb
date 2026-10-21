# cdt-23: a failed installer run leaves its staging publish behind

**Severity**: LOW. The installer publishes the build to `<PublishPath>.staging.<timestamp>` and
removes that folder only after a successful mirror. Every failure after the publish - the publish
itself, the payload check (cdt-2), the pre-upgrade backup refusing or failing, the pool not
stopping, the mirror failing (cdt-18) - leaves it next to the publish folder: a full copy of the
build (on the order of 100 MB) per failed run, each with a new timestamp, and with it whatever
`appsettings.json` the source tree carries, which `dotnet publish` copies (the checkout's own file,
when one exists, can hold real values). The installer twin of cdt-6 in the ADI deploy script.
Found while fixing cdt-18.
**Status**: Fixed on branch; awaiting merge
**Branch**: `cdt-11-14-installer-deploy` (worktree `agent-a1b04b356d91eeed2`)
**Commit**: `<pending>`
**Reviewer**: implementer (found by the cdt-11..19 agent; not yet confirmed by the supervisor)

## Evidence

`tools/Install-ExchangeAdminWeb.ps1` at `9bcb293`. The publish (962) and the only removal of its
output, which only a run that got past the mirror reaches:

```
962: Invoke-PlanOrAction "dotnet publish to staging: $stagingPath" {
...
1087: if (-not $PlanOnly) {
1088:     Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction SilentlyContinue
1089: }
```

Between them: `Write-Fail` on a failed `dotnet publish` (inside the publish action), the
action-mode `Resolve-ConfigDbTool` (throws on a missing or too-old binary), the backup (its refusals
are `Write-Fail`; `Backup-SqliteConfigDb` throws on a tool or integrity failure),
`Stop-AppPoolVerified` (throws if the pool will not stop), and the mirror's `catch`, which since
cdt-18 rethrows with `Write-Fail` and says "The published build is left in <staging>". Each is a
terminating error under the script's `Stop`, so none reaches line 1087. No `trap` or outer
`finally` exists.

**Mechanism, reproduced** under Windows PowerShell 5.1.20348. Scratch harness: the installer's real
region from the publish step to the restart check, lifted from the `9bcb293` file by the 5.1 parser
and run under `Stop` with only `dotnet`, the db tool resolver, the IIS pool calls, the robocopy
runner and the manifest writer stubbed (the stub `dotnet` creates the staging folder with an
`appsettings.json` in it):

| Case | What the caller saw | Staging left behind |
| --- | --- | --- |
| `dotnet publish` exits 1 | "dotnet publish failed with exit code 1" | True |
| the db tool is missing from the build | "The config DB tool is missing: ..." | True |
| the pool will not stop | "App pool is still 'Started' after 60 seconds." | True |
| the mirror fails | "robocopy failed with exit code 8 during: Deploy application files" | True |
| everything succeeds | completed | False |

## Plan (approved by the owner's EVERYTHING go)

1. One outer `try { ... } finally { if (-not $PlanOnly) { Remove-Item -LiteralPath $stagingPath
   -Recurse -Force -ErrorAction SilentlyContinue } }` from the publish step to the end of the deploy:
   the publish, the payload check, the backup decision, the stop, the mirror (cdt-18's flags, `try`,
   `catch` and `finally`), the manifest and the restart check. The region is re-indented one level;
   `git diff -w` shows the real change. The removal after the mirror moves into the `finally` and
   goes from the body.
2. cdt-18's mirror-failure message says the staging publish is removed when the installer exits,
   instead of "is left in".
3. The manifest is now written before staging is removed rather than after; it reads the publish
   folder and git, not staging, so the order does not matter.
4. cdt-18's tests looked for the mirror `try` among the script's top-level statements; they now find
   it wherever it is and read its siblings from the block that holds it.

Assumptions, recorded: plan mode removes nothing (it creates nothing, and the removal was already
guarded on `-not $PlanOnly`); `-ErrorAction SilentlyContinue` keeps a removal failure from replacing
the error that is propagating, as in cdt-6.

Constraints: as cdt-11 (standalone, plan mode, 5.1, ASCII, no version bump). Files:
`tools/Install-ExchangeAdminWeb.ps1`, `tests/ps/InstallerConfigBackup.Tests.ps1` (the new tests, and
cdt-18's two tests adapted to the new nesting), this record.

Guards: an AST test, the shape of cdt-6's: every command from the publish to the restart check lies
in the BODY of a try whose `finally` removes `$stagingPath`, only when not planning; and a
behavioural test running that region (lifted by AST) with the external tools stubbed: a failed
publish, a missing db tool, a pool that will not stop and a failed mirror each leave no staging
folder and still surface their own error; success leaves none. Mutations: the outer `finally` emptied;
the outer `try` closed before the stop (the stop and the mirror uncovered); the `9bcb293` shape. (The
`-not $PlanOnly` guard on the removal is kept as it was and not mutated: a plan creates no staging,
so removing it in a plan would be invisible as well as harmless.)

## Approach

As planned. In `tools/Install-ExchangeAdminWeb.ps1` after the fix: a comment (962-965), then the
outer `try {` (966) whose body is the former top-level region from the publish to the restart check,
indented one level (967-1097), and `} finally { if (-not $PlanOnly) { Remove-Item -LiteralPath
$stagingPath ... } }` (1098-1102). The old post-mirror removal is gone from the body; the manifest
call and the restart check now sit inside the `try`. `git diff -w` of the change is seven lines in
and five out. cdt-18's mirror-failure message (1074) now ends "The staging publish <path> is
removed when the installer exits; fix the cause and re-run the installer ...". cdt-18's two tests
locate the mirror's `try` with `Find` (the try whose body holds the mirror call) and read its
siblings from `.Parent.Statements` instead of the script's top-level statements; nothing else in
them changed, and both still pass.

The same 5.1 harness as in Evidence, against the fixed file:

| Case | What the caller saw | Staging left behind |
| --- | --- | --- |
| `dotnet publish` exits 1 | "dotnet publish failed with exit code 1" | False |
| the db tool is missing from the build | "The config DB tool is missing: ..." | False |
| the pool will not stop | "App pool is still 'Started' after 60 seconds." | False |
| the mirror fails | "robocopy failed with exit code 8 during: Deploy application files" | False |
| everything succeeds | completed | False |

## Guard proof

| Test (`tests/ps/InstallerConfigBackup.Tests.ps1`, Describe `cdt-23: ...`) | Pins |
| --- | --- |
| `covers every command from the publish to the restart check with a finally that removes staging (AST)` | Exactly one `try` whose `finally` removes `$stagingPath`, the finally being only `if (-not $PlanOnly) { Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction SilentlyContinue }`; that is the only removal of `$stagingPath` in the script; every command from the publish step to the end of the restart check lies inside that try's BODY, and the covered set includes `Resolve-ConfigDbTool`, `Backup-SqliteConfigDb`, `Stop-AppPoolVerified`, `Invoke-RobocopyChecked`, `Start-AppPoolVerified` and `Write-DeploymentManifest` |
| `runs the real region: a failed publish, a missing db tool, a pool that will not stop and a failed mirror each leave no staging, and keep their own error` | Lifts the `try` that holds the publish (found by the publish, not by its finally) and runs it under `Stop` with the script's own helpers and only the external tools stubbed: each failure leaves no staging folder and throws its own message (the mirror's ending "staging publish ... is removed when the installer exits"); success leaves none |
| cdt-18's two tests (adapted: the mirror's `try` found by `Find`, its siblings from its parent block) | Unchanged assertions, still green |

Mutations on `tools/Install-ExchangeAdminWeb.ps1` (fixed md5 `5B1CC7AB68E1409A8883291A5ACCF261`,
pre-comment; md5s of the content as committed, LF), each applied by a script that fails unless
every edit matches exactly once, run against `InstallerConfigBackup.Tests.ps1` and
`DeployInvariants.Tests.ps1`, then the original bytes restored, touched, md5 re-confirmed; the
restored file re-run green (141 passed). Post-comment md5 `A6EC5B72F279C8EE2FB351E2DA49765E` (141
passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| B1 the outer `finally` emptied (`$null = 'nothing removed'`) | `53C7B03EB631B9F485FC8E73E85EB650` | both (AST: "Expected 1, because one try owns the staging folder, but got 0"; behavioural: "Expected $false, because a run that fails (PublishFails) must not leave its staging publish behind, but got $true") |
| B2 the outer `try` opened only at the stop (publish, payload check and backup outside it) | `D2A568AC78FDACD1881E756AB2B9DB7F` | both (AST: "... but got @('line 962: Invoke-PlanOrAction', 'line 963: Push-Location', 'line 965: dotnet', ..."; behavioural: "Expected a value, because the publish sits in a try, so its finally can remove staging") |
| B3 pre-fix shape (`9bcb293`) | `624AA01641C4FC783DC9652CF904FA6F` | both (as B1 and B2) |

The behavioural test first found its region by the `finally`'s content, so B1 broke the lookup and
the test failed for the wrong reason (nothing ran, so no error was thrown). It now finds the region
by the publish it holds and asserts it exists; re-run, B1 fails on the staging assertion itself.
