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
