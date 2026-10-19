# aud-a8: Conference Rooms jobs keep running after the module is disabled

**Severity**: HIGH - a module an administrator has switched off continues writing to the
directory.
**Status**: Verified
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Finding 8 of the 2026-10-16 repository audit, verified against the code before any change.

`Services/Jobs/ConferenceRoomBulkProcessor.ProcessRowAsync` re-checked exactly one thing per
row:

```csharp
var snapshot = JobAuthorizationSnapshot.FromJson(job.AuthSnapshotJson);
var allowed = _sectionAccess.GetGroupsForSection(Section);
if (snapshot is null || !snapshot.IsStillAuthorized(allowed))
```

The section's allowed groups, and nothing else. **Live authorization also rejects a disabled
module** (`Authorization/GroupAuthorizationHandler.cs:55`), and this off-circuit path never
asked. With ExchangeOnline still enabled, a disabled Conference Rooms module keeps changing
rooms - the queued job and every subsequent row.

The same gap existed in Comms-10k and was closed as `aud-c2` on 2026-10-16, which is why this
reuses that shape rather than inventing a second one.

## Predicted observable failure

An administrator disables Conference Rooms - because it is misbehaving, or during an incident,
or to stop exactly this kind of bulk change - and a job already queued carries on applying
room metadata. The module's own page refuses to open while its background worker keeps
writing.

## Approach

Inject `ModuleEnablementService` and refuse before the snapshot check:

```csharp
if (!_enablement.IsModuleEnabled(ModuleName))
{
    Audit(job, actionAudit, target, success: false, ticket, "Conference Rooms is currently disabled.");
    return Failed(target, "Conference Rooms is currently disabled.");
}
```

**Ordered before the authorization check deliberately.** "The module is off" is true regardless
of who submitted the job, and it is a better audit record and a better operator message than
"authorization denied" - which would send someone looking at group membership for a problem
that is a toggle.

`IsModuleEnabled` cascades through `DependsOn`, so disabling the parent ExchangeOnline module
also stops these jobs. That is the existing semantic, inherited rather than invented here.

## Files changed

- `Services/Jobs/ConferenceRoomBulkProcessor.cs`
- `ExchangeAdminWeb.Tests/ConferenceRoomBulkProcessorTests.cs`

## Guard proof

`ADisabledModule_StopsTheJob_AndSaysSo` disables the module, runs a row, and asserts the
outcome is not Success, that the message names the cause, and that **`FinderCalls` is empty** -
nothing reached the directory. The last assertion is the one that matters; a status string can
be right while the write still happened.

Mutation: deleting the enablement check fails it. File restored byte-identical (md5 verified)
and touched.

**The fixture had to change, and that is itself worth recording.** A fresh enablement store
has no row for Conference Rooms, so after this change every existing test in the class failed -
correctly. The fixture now enables the module **and its `DependsOn` parent `ExchangeOnline`**,
because `IsModuleEnabled` cascades. Without the parent the child reads as disabled and the
tests fail for a reason that has nothing to do with what they assert.

## Coder dispute

None.

## Known gaps

The check runs per row, like the authorization check beside it, so a module disabled midway
through a job stops the remaining rows but does not undo the rows already applied. That
matches the existing per-row model and the plan's scope; a job-level abort is a different
piece of work.

Nothing here has run against a real tenant.

## Reviewer comments

Source: the 2026-10-16 audit, finding 8. Verified by the implementer before changing anything.
Independent review of the fix pending.

## Closeout

Fixed directly on `master`. Not pushed.

## Independent review, 2026-10-19 (codex-commercial / gpt-6-astra / max) - no product defect, test gap

The reviewer confirmed the behaviour: the guard precedes authorization, protection and both
mutation branches, and the `DependsOn` cascade is real executable behaviour rather than an
assumption. They checked processor registration, submission, the runner, both service branches
and failure aggregation. No regression and no introduced behaviour change.

**They found the guard proof weaker than it read.** `SaveEnablement` defaults an omitted module
to its catalog default, and `ExchangeOnline` defaults to false - so a save naming only Conference
Rooms switched **both** off. The test would have passed equally against a parent-only check, or
a child-only check that ignored the cascade. It proved "something refuses", not "this module's
effective enablement refuses".

Added, test-only:

| Test | What it pins |
| --- | --- |
| `EitherTheChildOrItsParentBeingOff_StopsTheJob(child: false, parent: true)` | the child alone |
| `EitherTheChildOrItsParentBeingOff_StopsTheJob(child: true, parent: false)` | the cascade |
| `WithBothOn_TheRowStillReachesTheDirectory` | the pair above is not satisfied by refusing everything |

Mutation: narrowing the check to `IsModuleEnabled("ExchangeOnline")` fails the child-only case
and nothing else - the original test passes it unchanged, which is the gap demonstrated rather
than argued. Source restored byte-identical (md5 verified) and touched.

The reviewer's two scope limits are restated rather than fixed, because both were already on
this record and both are per-row by design: a disabled module stops the remaining rows but does
not abort the job or undo rows already applied, and a row already admitted can still begin
writing, since protection resolution and service preflight are awaited after the check. A
job-level abort is different work.

Gate: 4304 passed / 3 known AD skips / 0 failed. No version bump: tests only, no behaviour
change.
