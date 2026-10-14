# mde-1: A missing AD module removes the whole device inventory to report a Site problem

**Severity**: HIGH - it is the exact regression the mechanism under review exists to prevent.
A directory read that cannot run at all escapes the catch, reaches the page's handler, and
replaces a working 26,000-device report with an error. The Site column was built so a failed
forest read costs one column; this made it cost everything.
**Status**: Verified
**Branch**: -- (direct-to-main)
**Commit**: `27beb85`

## Evidence

`Services/DefenderEndpointDeviceService.cs:1176` (pre-fix) caught exactly three types around
`_siteDirectory.ReadSubnets()`:

```csharp
catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or TimeoutException)
```

The production path does not stay inside that list. `Services/PowerShellDirectoryCommands.cs:103-110`
opens a runspace and imports the ActiveDirectory module, and the interface contract at
`Authorization/ISectionAccessDirectoryCommands.cs:82-86` states that a failure to RUN at all
still throws. The three that escape:

- `CommandNotFoundException` - RSAT / the AD module is not installed on the host;
- the runspace failing to open at all;
- a domain/invocation failure surfacing as neither of the three listed types.

Any of them propagates past this catch to the page catch at
`Components/Pages/DefenderEndpointDevices.razor:886-892`, which renders a page-level error and
discards the device list that had already been fetched successfully.

## Predicted observable failure

On a host without the AD module - which is every host that has not had RSAT added, and the
Site feature's whole point is that it degrades - an operator searching Defender devices gets an
error page instead of the inventory. The posture data, the observer column and the CSV export
are all lost to report that one column could not be filled.

## Approach

Catch everything around that one call. This is not a weakening, and the reason is structural
rather than a judgement call: the `try` wraps a SINGLE call that is entirely directory work,
with none of this module's own code inside it, and the subnet-to-site matching that follows sits
outside the `try`. There is no bug of ours for a broad catch to swallow. That is what makes it
different from the hunting side's deliberately narrow filter, which does wrap this module's own
parsing and must stay narrow.

## Files changed

- `Services/DefenderEndpointDeviceService.cs`
- `ExchangeAdminWeb.Tests/AdSiteDirectoryTests.cs`

## Guard proof

Pinned as BEHAVIOUR, not as a source-level assertion about the catch clause - an assertion that
reads the catch would pass against a catch that does the wrong thing. Seven exception types the
runspace path can actually raise, each asserting the device inventory survives and only Site is
marked `(unavailable)`.

| Test | Mutation | Result |
| --- | --- | --- |
| The seven-type theory in `AdSiteDirectoryTests.cs` | restore the three-type `when` filter | FAIL on three cases, `CommandNotFoundException` among them |

Full suite at the fix: **3,820 passed / 0 failed / 3 skipped**; build 0 errors; format clean;
`git diff --check` clean; ASCII lint passed.

## Coder dispute

None. The finding is correct, and the aggravating fact is recorded in `.agents/state.md`: this
defect sat inside the mechanism built to prevent exactly it, written and self-verified by the
coder without noticing.

## Known gaps

The forest read has still never run in this environment. The guard proves the degradation path
is reachable and survivable; it does not prove `AdSiteDirectory` returns correct subnets here.
That remains one of the three unproven environmental facts in `.agents/state.md`, and probing
Active Directory to settle it is forbidden without a direct owner go.

## Reviewer comments

`Reviewer: codex / gpt-5.5 / xhigh`
Range: `96cdb89..HEAD` (the Defender location session). Capability proof in the envelope: read
`Services/AdSiteSubnetMap.cs`, ran `git diff --stat 96cdb89..HEAD` (34 files, 3,972 insertions).
Verdict: **findings** (3) - this one, `mde-2` and `mde-3`.
Raw output: `.agents/review/out/session-review.txt`.

No verification round was dispatched: the finding is HIGH, not CRITICAL, and
`.agents/decisions.md` 2026-08-31 closes everything below CRITICAL on the coder-side guard
proof.

## Closeout

Fixed directly on `master` in `27beb85`. Round 1 was not clean, so the stream continued to
round 2 (`mde-4`, `mde-5`). Not yet pushed - push policy is ask.
