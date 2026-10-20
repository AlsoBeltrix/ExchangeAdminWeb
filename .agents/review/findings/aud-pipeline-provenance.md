# aud-pipeline-provenance: the stale-build check announced a match it never made

**Severity**: HIGH for what it guards, LOW in likelihood. It is the only thing standing
between a stale or unknown build and production, and when it could not do its job it said
it had.
**Status**: Verified, fixed, independently reviewed, review findings remediated
**Branch**: -- (direct-to-main)
**Commit**: `6c1b827`

## Evidence

From the **lower-priority Wave 4** list of the 2026-10-16 audit
(`docs/RepoAudit-Remediation-Plan.md`): *"`tools/deploy-pipeline.ps1:124` - provenance check
fails open; a missing commit id or a Git failure still yields 'matches source HEAD'."*
Verified against the code.

```powershell
$currentCommit = try { (git -C $repoRoot rev-parse HEAD 2>$null) } catch { $null }
$currentDirty  = try { [bool](git -C $repoRoot status --porcelain 2>$null) } catch { $false }
...
if ($currentCommit -and $manifest.gitCommit -and $currentCommit -ne $manifest.gitCommit) {
    throw "Dev is stale: ..."
}
Write-Ok "Dev commit matches source HEAD"
```

Three separate problems, compounding:

1. **The comparison is conditional and the success message is not.** If `$currentCommit` is
   empty, or the manifest carries no `gitCommit`, the `if` is skipped - and the next line
   prints "Dev commit matches source HEAD" regardless. A positive claim, with no comparison
   behind it, on the check guarding production.
2. **`try/catch` around `git` catches nothing.** `git` is a native executable; a failure
   sets `$LASTEXITCODE` and writes to stderr, it does not raise a PowerShell exception. With
   `2>$null` swallowing stderr, a failed probe produced an empty string and fell straight
   into case 1.
3. **`$currentDirty` is `$false` both when the tree is clean and when git failed**, so the
   "source has drifted since the dev build" warning silently never fires - and its absence
   reads as "clean".

## Predicted observable failure

A promotion is run where `git` is not on PATH, or `$repoRoot` is not a repository, or the
manifest was written by a build that could not resolve a commit. The pipeline prints "Dev
commit matches source HEAD" and promotes whatever happens to be sitting in the dev folder -
possibly built days earlier, from code nobody can now identify. The operator has been told
the opposite.

## Approach

**Probe on the exit code, not on an exception that cannot happen.** `$commitProbeOk` is
`$LASTEXITCODE -eq 0` plus a non-empty answer; `$statusProbeOk` likewise.

**Each way of not knowing is its own branch, and none of them reports a match.** A failed
git probe and a manifest with no `gitCommit` are distinguished, because they send the
operator to different places - one is a tooling problem on the deploy host, the other means
the dev build itself is unidentifiable.

**Unverifiable provenance refuses the promotion** unless `-AllowUnverifiedProvenance` is
passed, modelled on the existing `-AllowDirty`. A check that cannot run has not passed.

**The success line moved into an `else`**, so it is now impossible to reach without a
comparison having happened.

**A failed status probe warns explicitly** that drift was not checked, rather than being
indistinguishable from a clean tree.

## Files changed

- `tools/deploy-pipeline.ps1`
- `tests/ps/DeployInvariants.Tests.ps1`

No version bump; ops-script-only.

## Guard proof

Source guards in the suite that already owns this script. The `-Prod` branch needs
administrator rights, two IIS instances and a real dev deployment to execute.

| Test | What it pins |
| --- | --- |
| `never reports a commit match it did not make (provenance fails closed)` | **the finding** - matched as `} else {` plus the message, not the message alone |
| `treats an unusable git probe as unverified, not as verified` | the exit-code check, and that the refusal exists |
| `names the two distinct ways provenance can be unknown` | the operator can tell which happened |
| `makes promoting without verified provenance an explicit switch` | the parameter and its use |
| `does not let a failed status probe read as a clean tree` | the drift warning's third state |

| Mutation | Result |
| --- | --- |
| the success line pulled back out of its `else` | FAIL - the provenance guard |
| `$commitProbeOk` reverted to an emptiness test | FAIL - the probe guard |
| the failed-status-probe branch disabled | FAIL - the drift guard |

Each confirmed applied before its run; source restored byte-identical (md5).

The first guard is deliberately written as condition-plus-statement. Asserting only the
message text would pass while the branch above it was dead - the exact mistake made on
`aud-a38` and caught there by a mutation.

Gate: ASCII lint passed; `Invoke-Pester tests/ps` 226 passed / 0 failed; PSScriptAnalyzer 0
errors; the script parses clean. No .NET run - no C# or Razor changed.

## Known gaps

- **No promotion was run.** The branches are read, not executed; `-Prod` needs admin, IIS
  and a real dev deployment.
- **No test drives the git probe itself.** That `git rev-parse` sets `$LASTEXITCODE`
  non-zero outside a repository is taken from how git behaves, not demonstrated here. The
  logic is inline in the `-Prod` branch; extracting it to make it callable was judged out of
  proportion for a lower-priority item, and that judgement is worth a second opinion.
- **`-AllowUnverifiedProvenance` has no audit trail.** It warns and proceeds; nothing
  records afterwards that a promotion went out unverified.
- **The manifest is trusted once it has a commit.** Nothing checks that the DLL in the dev
  folder was actually produced by that commit - only that the manifest says so, and the
  manifest is written by the same pipeline.

## Closeout

Fixed directly on `master`. Independent review pending - and currently impossible: both
review harnesses are returning 401 (`.agents/state.md`).

## Independent review - `gemini-4-argon-high`, 2026-10-20

Reviewed as batch 3 of the backlog. Prompt kept at `.agents/review/prompt-backlog-3.md`.

**Any earlier claim in this record that review was impossible was wrong** - the probes had
been invoked incorrectly. `.agents/state.md` carries that correction.

**Two MEDIUM defects in my own fix.** Removing the `try/catch` was right about native exit
codes and wrong about a missing executable: `git` absent raises `CommandNotFoundException`
before any exit code exists, and with `$ErrorActionPreference = "Stop"` that killed the run
instead of reporting unverifiable provenance - so `-AllowUnverifiedProvenance`, the escape
hatch, could not be used on a host without git. Both probes now catch that separately.

And a manifest carrying `"gitCommit": "unknown"` - which is what the writer records when it
cannot resolve HEAD - passed the "is it empty" test, then crashed on `Substring(0, 8)` with
`ArgumentOutOfRangeException`. It is now treated as unverifiable, and both shortenings are
length-guarded.

Remediation landed in the follow-up commit, every fix mutation-proven.
