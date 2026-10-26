# Defect review request: a deploy over an existing application with no serving appsettings.json is refused; the 8.3 test fails naming its cause (cdt-55, cdt-56)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `84a31608` or later.
- The change is the range merged at `84a31608`: commits `f348fabf` (cdt-55) and `584eb2fc` (cdt-56), on top of `15066df8`.
  Review `git diff 15066df8 584eb2fc -- . ':!.agents'` and read the COMBINED `deploy.ps1` (the new script-level check at
  about 389-399, after the cdt-51 block and before any backup is written; cdt-51's own refusal, whose comment was
  corrected; the -AllowRepoint and -ConfirmFreshInstall paths), `tests/ps/DeployInvariants.Tests.ps1` (the new real-files
  test at about 1644 and the cdt-44, cdt-45 and cdt-51 tests it updated), `tests/ps/AppPoolControl.Tests.ps1` (the 8.3
  flag now asserted rather than branched on; the three short-name sources each recording why they gave nothing),
  `tools/AppPoolControl.psm1`, `tools/JobStateWarning.psm1`.
- You may read any file and run read-only git commands. One command per call. No writes, no builds, no network, no IIS.
- Records: `.agents/review/findings/cdt-55.md`, `cdt-56.md`; parents `cdt-36.md`, `cdt-42.md`..`cdt-51.md`; the
  ops-batch-14 result that raised both is `.agents/review/ops-batch-14.result.txt`. ALREADY admitted and out of scope -
  do not re-raise: `cdt-57` (the installer has cdt-55's shape), `cdt-58` (promote, install and cutover stop a pool
  without naming its other applications), the absence of a native -PlanOnly in deploy.ps1, and the two commit-trailer
  spellings on this branch (a harness attribution change mid-batch, not a defect). Repo rules: `AGENTS.md`;
  `.agents/repo-guidance.md` Architectural Invariants 1, 3, 4, 5, 6 and Known Failure Classes 1, 2;
  `docs/ProjectConstitution.md` Deployment section.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 584eb2fc`. Report whether both succeeded.

## Mandate

Hunt for observable defects. cdt-55: the condition `-not $isUpgrade -and $existingApplication -ne $null -and -not
(Test-Path $servingConfigPath)` - enumerate the real combinations (upgrade; fresh install with no IIS application;
-Force in place; -Force with -AllowRepoint to a new folder; -ConfirmFreshInstall over a half-removed instance) and name
any that is now refused although it is legitimate, or still allowed although it strands a live store; where
`$servingConfigPath` comes from and whether it is the folder IIS serves rather than the run's -PublishPath; its position
relative to EVERY write (backup folder creation, the pre-force appsettings copy, pool and ACL work, publish, stop,
mirror) and relative to cdt-51's refusal, which must still fire first and keep its words; whether the refusal's three
suggested ways out are each actually possible (does restoring a file from `<LogRoot>\ExchangeAdminWeb\ConfigBackups`
work, is a hand-written appsettings naming only ConfigStore:Path accepted by the app and by the deploy's own validator);
and the updated cdt-44, cdt-45 and cdt-51 tests - was any assertion weakened rather than moved. cdt-56: the flag is now
asserted, so on a host with no 8.3 anywhere the suite FAILS rather than skipping - is that the intended trade (CI fails
on skips, so a skip was not available), can `fsutil file setshortname` fail for a reason that is not "8.3 disabled"
(not elevated, file in use, ReFS) and does the message distinguish them, and is the fixture removed on every exit path.
Invariants 5 and 6 across both changed scripts.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.