# cons-3: the ConfigDbToolInApp plan still claims a five-second db CLI wait

**Severity**: LOW - an operator document now imprecise. `docs/ConfigDbToolInApp-Plan.md` says the `db` command-line
mode waits 5 s on a held database. aud-a67 (2) brought it down from a measured 30.5 s to a measured 7.8-9.4 s, and the
residue is structural: `busy_timeout` is a PRAGMA settable only after opening, so the open and the statement each get
one wait. The plan reads Implemented, so this is a correction, not a relabel.
**Status**: Merged to master `4afa745d`; codex defect review settings-batch-9 in flight
**Branch**: `fix/a64-a67` (from `eb017102`)
**Commit**: see below
**Reviewer**: implementer (aud-a64/a67 agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `d0fc4c70`; the measurement is in the aud-a67 record (Release executable against a database another process holds
exclusively; note a plain `BEGIN IMMEDIATE` takes RESERVED and does not block readers, so the fixture needs a journal
spill to take EXCLUSIVE).

## Approach

- State the measured figure and why it is about twice the configured timeout; no behaviour change; doc check only.

### What changed (fix/a64-a67, 2026-10-26), at `e61411b4`

`docs/ConfigDbToolInApp-Plan.md`, two edits, no behaviour:

- the verb table's `backup` row no longer says `PRAGMA busy_timeout=5000` as if that were the wait; it names the one
  busy wait as both the `busy_timeout` and the command timeout, and points at the paragraph below it;
- a new paragraph under the table gives the measured figures - 30.5 s before aud-a67 item 2, 7.8 to 9.4 s after - and
  why the remainder is about twice the configured timeout rather than once: `busy_timeout` is a PRAGMA, settable only
  on an open connection, so the open gets the provider's retry (bounded by the command timeout) and the statement
  after it gets SQLite's own wait. It also records that the command timeout bounds the retry only, not how long a
  statement may run, so a long `VACUUM INTO` is not cut short, and it keeps the fixture note - a plain
  `BEGIN IMMEDIATE` takes RESERVED, which a reader passes, so the measurement needs a journal spill to take EXCLUSIVE.

The plan reads `Implemented`, so this is a correction to what the shipped tool does, not a relabel; its `Status` line
is untouched.

## Files changed

- `docs/ConfigDbToolInApp-Plan.md`.

## Guard proof

Not applicable (docs only). `git diff --check` and the ASCII lint are in the branch-tip gates; the figures are the
ones measured for aud-a67 item 2 and recorded there with their method.

## Known gaps

- The numbers are this host, this build, this fixture. They are the right order of magnitude for the claim the plan
  makes, not a specification: nothing asserts them, and a different disk or a busier host will move them.
- `tools/SqliteConfigBackup.psm1` and the deploy, promote and cutover scripts that call the CLI say nothing about a
  wait, so none of them needed a change; they were read, not run.

## Branch-tip gates - 2026-10-26 (`fix/a64-a67`, the cons-2/3/4/5 round)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 19 warnings - the same count as the fork, none in a
  touched file.
- `dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4`: 6,745 passed / 0 failed / 0 skipped, 4m40s. The
  previous tip of this branch was 6,742; these four records add 3 cases net (cons-4 adds three and retargets one,
  cons-5 extends an existing theory rather than adding cases, cons-2 and cons-3 add none).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `pwsh -NoProfile -File tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
- Not run: `Invoke-ScriptAnalyzer` and `Invoke-Pester tests/ps` - no PowerShell file changed. No directory, Exchange
  Online, Graph, DHCP, IIS, Delinea or SMTP call; nothing deployed.
