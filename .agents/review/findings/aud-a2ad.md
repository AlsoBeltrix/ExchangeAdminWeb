# aud-a2ad: restore Room Finder's creation of a building's room list, with the protected-target check kept before the add

**Severity**: HIGH-class by provenance - this row reverses the shape aud-a2ab chose and must not reopen the hole it
closed any wider than the owner has ruled. Owner ruling 2026-10-26 (`.agents/decisions.md`): "functionality CANNOT
change ... resolve this without removing anything or causing undue extra work." Room Finder apply creates a building's
room list again when none exists (the behaviour before `3bfdc1af`), and kd-66's preview wording goes back to saying
the list will be created. The created list is checked against Protected Group Targets BEFORE the room is added to it
(the post-creation re-check aud-a2e introduced, which aud-a2ab kept as "the add's own guard"); a refused add leaves the
new, empty list and the room's metadata as written and says so. That ordering is a scoped, owner-ruled exception,
recorded beside Self-Service Groups and Comms-10k, pinned by a tripwire test, and stated in the Constitution's list
of exceptions by the implementing agent (a docs edit, with a pointer to the ruling).
**Status**: Open - approved by owner ruling 2026-10-26; the next serial slice after cons-7
**Branch**: -
**Commit**: -
**Reviewer**: owner ruling

## Evidence

`Services/ConferenceRoomService.cs` as of `3bfdc1af`: `PreflightRoomListAsync` refuses when no list exists
(aud-a2ab); the creation path it bypassed; `ConferenceRoomListWriteTargetTests` about 406 asserting refusal before
`Set-Place`; `Components/Pages/ConferenceRooms.razor` about 204 (kd-66's preview wording).

## Approach (to be written by the implementing agent before code)

- Restore creation on the missing-list path exactly as it was before aud-a2ab (`git show ebd66b91:Services/ConferenceRoomService.cs`
  is the last shape with creation AND aud-a2e's post-creation check); keep aud-a2ac's ambiguous-lookup refusal (an
  AMBIGUOUS or FAILED lookup still refuses before any write - only a confirmed absence proceeds to create).
- The post-creation check before the add stays and is pinned: tripwire test asserting that a protected created list
  is refused at the add, with the list created and the metadata written, and that the audit row says both.
- kd-66: the preview says the list will be created, as before.
- Constitution: add the exception to the list the Self-Service Groups and Comms-10k exceptions are in, one sentence,
  pointing at the 2026-10-26 ruling. `.agents/repo-guidance.md` Known Failure Class 3 lists the exceptions too; add
  it there.
- Mutations: the post-creation check removed (the tripwire must fail); creation removed again (the restore test must fail).

## Guard proof

Pending.