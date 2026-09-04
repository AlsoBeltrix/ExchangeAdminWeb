# scd-4: Installer guidance risked leaving the per-instance jobs database unwritable

**Severity**: MEDIUM - a fresh install with `-ConfigStorePath` following the draft's
"ACL that directory instead of `config\`" wording would leave the app pool without
Modify on the local `config\`, where the per-instance jobs database must be created.
**Status**: Verified (plan revised; docs-only - no code exists yet)
**Branch**: -
**Commit**: lands in the same commit as this record and the plan revision; read it
from `git log -1 -- .agents/review/findings/scd-4.md`.

## Evidence

`docs/SharedConfigDb-Plan.md` at `8bb46a4`, AC9: "ACLs that directory for the app pool
identity instead of (in addition to) `config\`" - ambiguous, and the "instead of"
reading is the dangerous one. `Program.cs:68` puts `exchangeadmin-jobs.db` under
`<ContentRoot>\config`; `tools/Install-ExchangeAdminWeb.ps1:559` grants the local
`config\` ACL today and `:607-616` writes first-run seed files there. Also
`tools/deploy-pipeline.ps1` help and final messages describe config promotion.

## Predicted observable failure

Fresh install with the shared path: the first bulk job submission fails to create
`exchangeadmin-jobs.db` (access denied) with the config store working normally, which
points the operator at the wrong subsystem.

## What

Plan defect: an ambiguous sentence in an acceptance criterion, plus a script whose
messages the plan forgot.

## Approach

Plan revised: AC9 and a new settled item state that the local `config\` is ALWAYS
created and ACLed and the shared directory is ACLed in addition; AC6 and the S3 sketch
add `deploy-pipeline.ps1` message updates with a Pester source guard.

## Files changed

- `docs/SharedConfigDb-Plan.md` - section 1, AC6, AC9, section 6 (S3 sketch), section
  8, section 10.

## Guard proof

Docs-only. The AC9 Pester row and the `deploy-pipeline.ps1` guard bite at S3/S4.
`git diff --check` clean on the fold commit.

## Coder dispute (if any)

None.

## Known gaps

None.

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier
  (grade fallback; same dispatch as scd-1)
Harness: codex-cli 0.152.1. Reviewed SHA `8bb46a4`, base `e36e798`, capability_ok
true, verdict `acceptable_with_changes` (material change 4). Dispatched 2026-09-04;
envelope at `.agents/review/scd.result.json`.
