# psl-5 - a failed DACL read hides the next owned group in Self-Service Groups

Status: Fixed on branch; awaiting merge. Commit `<pending>`. Found while fixing psl-2/psl-3.
Severity: LOW (proposed). Reviewer: implementer (worktree subagent), 2026-10-21. Same class as
psl-2/psl-3: one operation's retained PowerShell error records reach a later consumer.
Evidence head: `1633ad02546462b8102518c37db0b9d117d17277`.

## Evidence and consequence

`SelfServiceGroupService.CallerCanManageMembers` (SelfServiceGroups/SelfServiceGroupService.cs)
reads a group's DACL with one `ps.Invoke()` (line 1376). Its throw path is
`catch { ps.Commands.Clear(); return false; }` (1378-1382): commands are cleared, error records
are not. Its consumer for the non-throw path is `if (ps.HadErrors) { ps.Streams.Error.Clear();
return false; }` (1388-1392).

GetOwnedGroupsAsync calls it once per owned group on the same PowerShell (120-127:
`foreach (var group in groups) { ... if (!CallerCanManageMembers(ps, ...)) continue; ...`).
When one group's DACL read leaves an error record and then throws, that group is correctly
excluded, but the record survives. The SDK sets `HadErrors` after an invocation whenever the
stream holds any record, stale ones included (checked against the 7.6 SDK; see psl-2), so the
NEXT group's clean DACL read reports HadErrors and that group is also excluded: a manageable
group silently missing from "my groups", with no message. Only the one following group is hit,
because 1390 then clears the stream.

The producer is realistic, not only a stand-in shape: a script statement that fails with a
statement-terminating error (for example a method call on a null value) writes a record and
continues, and a later `-ErrorAction Stop` failure in the same script then throws with that
record still on the stream (checked against the 7.6 SDK). The other three callers (202, 264,
580) return or throw after a false result, so no later consumer sees the record there. Fail-
closed: nothing is granted, so this is an availability defect, not an authorization one.

Not promoted: a read-only scan also flagged ResolveOwnerDisplay's `SilentlyContinue` lookup
(1471-1473) feeding the same check. The SDK writes no record for a cmdlet error under
SilentlyContinue (checked), so that path does not reach the consumer.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"; same-class consumers found in other
services are fixed as their own commit and record). One commit `fix(self-service-groups): ...
(psl-5)`. Module behaviour: Self-Service Groups.

Scope: `Services/SelfServiceGroups/SelfServiceGroupService.cs`, CallerCanManageMembers only: its
throw path clears the error records with the commands before returning false. The non-throw
HadErrors branch, the fail-closed results, the ACE evaluation and every caller are unchanged.
No production seam: the test reaches the existing private static method by reflection, as
psl-1 did, with local functions standing in for Get-ADGroup and the DACL's GetAccessRules.

Proof: reproduction first: a DACL read that leaves a record and throws (excluded), then a clean
read granting member-write to the caller on the same PowerShell (must be included). Controls:
the failed read still returns false; a record without a throw still excludes its own group and
does not leak. Mutation: the added clear removed at the call site.

## Executed reproduction - 2026-10-21

Branch `worktree-agent-a7f96055b25f98b57`, after psl-4, psl-2 and psl-3; service file unchanged
(MD5 `8677011B6D14ED8C90FCD8CCE2BE0947`). `ExchangeAdminWeb.Tests/SelfServiceGroupDaclLifecycleTests.cs`
reaches the real CallerCanManageMembers by reflection with a local Get-ADGroup whose DACL object's
GetAccessRules (a script method) yields one explicit Allow WriteProperty on `member` for the caller.
Result 1 failed / 2 passed, 0 compiler errors (`out/psl-5-reproduction.log`):

- `CN=broken` (record, then throw) returned false, correctly; the NEXT call, a clean read of
  `CN=next` that grants the caller, also returned false: `Assert.True() Failure`.
- Controls passed: the same clean read alone returns true; a record without a throw (`CN=soft`)
  excludes only its own group, because the HadErrors branch already clears.

## Fix

The throw path now clears the error records with the commands before returning false. Nothing
else changed: the fail-closed results, the HadErrors branch, the ACE projection and evaluation,
and all four callers. No production seam.

## Guard proof

Same session-local driver as psl-4: exact bytes saved, one literal replacement, `dotnet test
ExchangeAdminWeb.slnx -c Release --filter
FullyQualifiedName~SelfServiceGroupDaclLifecycleTests|FullyQualifiedName~SelfServiceGroup|FullyQualifiedName~GroupMembershipAce`
(48 tests), exact restore in `finally`, MD5 re-checked, mtime advanced, restored rerun 48/48.

| Mutation (call site) | Mutant MD5 | Named test failing | Assertion |
|---|---|---|---|
| added clear removed from the catch (exact revert: mutant equals base) | `8677011B6D14ED8C90FCD8CCE2BE0947` | AFailedDaclRead_IsExcluded_AndDoesNotHideTheNextGroup | Assert.True |

Fixed, pre-comment MD5 `585EAB3E3325B3DF16494C80EA42EA94`; 0 `error CS` and both outputs built in
the mutant run. Comments were added after the round; final MD5 `F3161662717A58669134D7E905C9C2BE`.

## Adjacent observation, not fixed (different class)

ADAttributeEditorService.PerformLookup tolerates per-search-base failures (SilentlyContinue, an
empty catch, then `ps.Streams.Error.Clear()` each iteration) but then tests `ps.HadErrors` after
the loop. That flag describes only the LAST invocation and survives `Streams.Error.Clear()`,
while SilentlyContinue writes no record but still sets the flag (checked against the 7.6 SDK with
a built-in cmdlet; assumed, not verified, to hold for the AD cmdlets' same WriteError path). So an error on the last
configured base fails the whole lookup with the generic "AD query failed." even when the user
was found in an earlier base, and the same error on an earlier base is tolerated. Whether a
base's failure should fail the lookup (fail closed: a hidden duplicate could defeat the
ambiguity check) or be tolerated is an owner decision; no stale record is involved, so it is
not this class and no code changed. Also latent only: IExoRecipientCommands' adapter clears
commands, not errors, in `finally`, but PooledExoCommands has no callers.

## Gates - 2026-10-21

- `dotnet build ExchangeAdminWeb.slnx -c Release`: exit 0, 0 errors, 40 existing warnings.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: the first run hit the
  known-flaky `Comms10kRefusalTests.C6ValidationWaitsKeepTheCapturedTicketVisibleAndRejectDelayedInput`
  theory (blame-hang inactivity abort after 4,336 passed / 0 failed). Rerun with
  `--filter "FullyQualifiedName!~C6ValidationWaitsKeepTheCapturedTicketVisibleAndRejectDelayedInput"`:
  exit 0, 4,519 passed / 3 skipped / 0 failed, 4,522 total, 4m59s.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check
  HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` with the new test staged: exit 0.

No PowerShell source changed (no analyzer or Pester run). No AD module import, directory, push or
deployment. No coder self-review verdict is claimed; this record was opened by the implementer, so
an independent review is the supervisor's call. Module behaviour changed: Self-Service Groups (the
supervisor owns its version bump and the index row).
