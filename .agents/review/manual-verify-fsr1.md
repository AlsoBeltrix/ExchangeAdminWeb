# Owner-run verification prompt: fsr-1

Paste the block below into an interactive codex session (default gpt-5.6-sol), started in
`D:\source\ExchangeAdminWeb`. Paste the JSON it returns back to the coder session.

---

Verify one fixed finding in this repo. Read `.agents/review/findings/fsr-1.md` in full.
Pins: base `3505e6707ccdecf41146913845f1275709ed1532`, head `f6a4eb1` (resolve and echo
the full head SHA). Review `git diff <base>..<head>` - the single fsr-1 fix commit.

Mandate (narrow): confirm the predicted failure is closed and no adjacent regression
exists in the touched surface. Judge by reading: the exact-or-nothing DN branch in
`ResolveGroupForWrite` (no fall-through to the local name loop on a DN miss), the routed
member read in `GetMembersAsync`, the routed write cmdlets and cycle probe, and whether
the Known gaps section (IsDirectMemberOf deliberately un-routed, fail-safe) is honest.

Guard proof, in a DISPOSABLE `git worktree` at the head SHA (never the shared tree):
1. `dotnet test ExchangeAdminWeb.slnx --filter "FullyQualifiedName~GroupSearchForestScope"` - expect pass.
2. Revert the fix in the worktree (`git revert --no-commit f6a4eb1` or checkout the base
   `Services/GroupManagementService.cs`), re-run - expect FAIL.
3. Restore, re-run - expect pass. Remove the worktree.
Set `guard_confirmed` true only if the revert failed the guard and the restore passed it.

Reply with ONLY this JSON:
{"results":[{"id":"fsr-1","verdict":"accepted|reopened|invalid","guard_confirmed":true|false,"comments":["file:line - ..."]}]}
