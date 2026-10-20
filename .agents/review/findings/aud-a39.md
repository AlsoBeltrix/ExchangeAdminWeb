# aud-a39: the cutover accepted a shared database that a deploy would delete

**Severity**: HIGH. The loss is the authorization store for every instance, destroyed by a
routine deploy weeks after the cutover that caused it.
**Status**: Verified, fixed, independently reviewed, review findings remediated
**Branch**: -- (direct-to-main)
**Commit**: `ba0d418`

## Evidence

Finding 39 of the 2026-10-16 audit, verified against the code.

`tools/Move-ConfigDbToShared.ps1` validated `-SharedDbPath` through
`Assert-LocalAbsolutePath`, which rejects two things:

- a UNC path, because SQLite locking is unreliable over a share;
- a drive-relative path like `D:file.db`, which resolves against that drive's current
  directory (an earlier review finding, `scdi-2`).

It did not reject a path **inside an instance's publish folder**. So
`-SharedDbPath D:\inetpub\ExchangeAdminWebDev\shared\exchangeadmin.db` was accepted, written
into both instances' `appsettings.json` as `ConfigStore:Path`, and left there.

Deploys mirror the publish tree:

```
robocopy $StagingPath $PublishPath /MIR /XF appsettings*.json /XD logs config
```

`/MIR` deletes anything in the destination that is not in the source. `shared\` is not in
the source. The next deploy of that instance deletes the shared database - and with it the
module enablement, section-access authorization, protected principals and attribute
allowlist for **both** instances.

`config\` is excluded, so a path there survives `/MIR` today. That is not a reason to allow
it: the directory then belongs to one instance's lifecycle, the other instance depends on a
folder that an uninstall or clean reinstall removes, and the protection rests on a deploy
script's exclusion list rather than on anything structural. The architectural invariant
says the shared file lives outside both publish folders; this now enforces that rather than
trusting it.

## Predicted observable failure

An operator runs the cutover with the shared database under the dev publish folder. It
works - both instances start, configuration is shared, everything is correct. Weeks later a
routine dev deploy mirrors the tree and deletes the file. Both app pools now refuse to
start, because a configured `ConfigStore:Path` is opened without create. Nothing connects
the outage to a cutover parameter chosen weeks earlier.

## Approach

A precondition that refuses a `-SharedDbPath` lying inside either publish tree, with a
message naming which tree, why a publish folder cannot hold shared state, and a path shape
that would work.

`Test-PathIsInside` is textual and case-insensitive, matching how the script already
compares the two publish paths to each other. The trailing separator appended to the
directory is load-bearing: without it, `D:\inetpub\App` would be judged to contain
`D:\inetpub\AppShared`, and the guard would refuse a perfectly good sibling.

Placed with the other read-only preconditions, so it fires before any backup directory is
created or any file is touched.

## Files changed

- `tools/Move-ConfigDbToShared.ps1` - `Test-PathIsInside`, the precondition
- `tests/ps/MoveConfigDbToShared.Tests.ps1`

No version bump; ops-script-only.

## Guard proof

Behavioural: this suite invokes the real script against fake instance trees in a temp
directory, so the refusal is driven end to end rather than read.

| Test | What it pins |
| --- | --- |
| `refuses a path inside the dev publish folder` | **the finding** |
| `refuses a path inside the prod publish folder` | the other tree |
| `refuses an instance's own config folder, which deploy exclusions only happen to spare` | the deliberate strictness, with the reason in the test name |
| `explains why, so the operator can choose a path rather than guess` | the message names `/MIR`, not just "invalid" |
| `refuses before doing any work at all` | no backup root, no database left behind |
| `still accepts a path outside both trees` | a fix that refused everything would pass all of the above |
| `does not mistake a sibling folder with a shared prefix for a containing one` | `...\Dev` must not match `...\DevShared` |

| Mutation | Result |
| --- | --- |
| the guard condition replaced with `if ($false)` | FAIL - 4 tests |
| the trailing separator dropped from `Test-PathIsInside` | FAIL - the sibling-prefix test, and only that one |

The second mutation is the one worth noting: it fails exactly one test, and that test exists
only because the prefix trap is easy to write and invisible until someone names a folder
`...Shared`. Source restored byte-identical (md5 verified).

**A first draft of these tests failed for the wrong reason** and was rewritten. They
captured output with `*>&1 | Out-String` and asserted on the text, but `Write-Fail` throws,
and a terminating exception is not captured that way - so all four refusal tests errored
rather than asserting. They now use the `Should -Throw` idiom the suite already uses for the
UNC and drive-relative cases.

Gate: ASCII lint passed; `Invoke-Pester tests/ps` 206 passed / 0 failed; PSScriptAnalyzer 0
errors; the script parses clean. No .NET run - no C# or Razor changed.

## Known gaps

- **The check is textual**, not filesystem-resolved. A junction, a symlink or a `subst`
  drive pointing into a publish tree would pass. Resolving would mean touching the
  filesystem in a read-only precondition and would still not catch a link created later;
  the textual check matches how the script already compares its two publish paths.
- **Only the two publish trees are checked.** A shared database placed somewhere else that
  a different tool mirrors - a backup root, a staging folder - is still accepted.
- **Existing deployments are not audited.** This stops a bad path being chosen; it does
  nothing about one already written into an `appsettings.json`. Whether any instance is
  currently in that state was not checked, and checking it needs access to the hosts.
- The message recommends `D:\inetpub\ExchangeAdminWebShared\config\exchangeadmin.db` as an
  example shape; it is illustrative, not a convention this repo enforces anywhere.

## Closeout

Fixed directly on `master`. Independent review pending - and currently impossible: both
review harnesses are returning 401 (`.agents/state.md`).

## Independent review - `gemini-4-argon-high`, 2026-10-20

Reviewed as batch 3 of the backlog. Prompt kept at `.agents/review/prompt-backlog-3.md`.

**Any earlier claim in this record that review was impossible was wrong** - the probes had
been invoked incorrectly. `.agents/state.md` carries that correction.

**One MEDIUM defect, and a false justification of mine.** `Test-PathIsInside` compared raw
strings, so `..`, `.`, forward slashes and doubled separators walked straight past it:
`D:\inetpub\App\..\App\x` is inside the tree on disk but does not start with
`D:\inetpub\App\` as text. Both sides are now canonicalised with
`[System.IO.Path]::GetFullPath`.

**The record claimed resolving "would mean touching the filesystem in a read-only
precondition". That was false** - `GetFullPath` is purely in-memory, the installer already
uses it, and the same precondition block already calls `Test-Path`. The real limit is
narrower and is recorded as such: `GetFullPath` does not resolve junctions or `subst`
drives.

Remediation landed in the follow-up commit, every fix mutation-proven.
