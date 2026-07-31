# ppv-4: Live AD tests still report green when the directory is unreachable

**Severity**: LOW — no production behavior is affected, but on CI these tests are
indistinguishable from real coverage, which is the exact trap this work stream already
tripped over twice.
**Status**: Verified
**Branch**: default-branch mode (one commit per finding, per repo policy)
**Commit**: (filled in after commit)

## Evidence

`ExchangeAdminWeb.Tests/ADDirectoryLiveTests.cs` uses a bare early return at
`:54`, `:74`, `:86`, `:105`, `:116`:

```csharp
if (!Reachable(svc)) return;
```

The *fixture-missing* case in the same file was already converted to
`Assert.SkipWhen` (`:57`, `:90`) after a probe caught it passing green with the mapping
code broken. The *dependency-missing* case was left as an early return — the same defect
one level up, in the file whose own doc comment warns about it.

## Predicted observable failure

On CI (`windows-latest`, no RSAT) all five tests report **passed**, not skipped, even with
the OU mapping or the live `ValidateExists` path deliberately broken. Anyone reading the
CI summary sees five green tests that asserted nothing. The failure is to the reader, not
the runtime — a false confidence signal in the one suite whose stated purpose is catching
what unit tests cannot.

Catchable by running the suite on a host without the ActiveDirectory module and observing
"passed" rather than "skipped".

## What

`Assert.SkipWhen` was applied to one of the two skip conditions in this file and not the
other, in the same commit.

## Approach

Replace each `if (!Reachable(svc)) return;` with
`Assert.SkipWhen(!Reachable(svc), "Active Directory is not reachable from this host.")`.
Mechanical, no logic change; the result becomes a visible skip.

## Files changed

- `ExchangeAdminWeb.Tests/ADDirectoryLiveTests.cs` — five call sites, plus the class doc
  comment that currently describes the early-return behavior.

## Guard proof

Self-proving, and demonstrated rather than argued. Forcing `Reachable` to `false`
(simulating a host with no AD) turns the summary from

```
Passed!  - Failed: 0, Passed: 5, Skipped: 0, Total: 5
```

into

```
Skipped! - Failed: 0, Passed: 0, Skipped: 5, Total: 5
```

Before the fix the same simulation still reported **5 passed** — the defect exactly.
Restored: 5/5 run and pass on this dev box, where RSAT is installed.

## Coder dispute (if any)

None. Accepted as written; the reviewer caught an inconsistency I introduced in the same
commit that fixed its sibling.

## Known gaps

The pre-existing `ADDirectorySearchServiceTests` (`:100`, `:111`, `:121`) has the same
pattern and is **out of scope** for this finding — it predates this range. Recorded in
`.agents/state.md` as a known trap; worth a separate sweep, not a silent expansion of this
fix.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard`
Harness: codex-cli 0.146.0 (`codex exec --json`, `-s read-only`).
Reviewed SHA: `521bb6e62741c7433a827079d1c53eef0b3b4fec`
Base SHA: `10d159363eeed955d825bf304a143594686b034b`
`capability_ok`: true. Verdict: **findings** (4). 2026-07-31 UTC.

Reviewer's better_approach, adopted verbatim: "Replace the early returns with
Assert.SkipWhen(!Reachable(svc), \"Active Directory is not reachable\") or an equivalent
explicit skip so the result is visible as skipped, not passed."
