# Conference Rooms — add a room to an on-prem-synced room list

Status: Approved (owner "go", 2026-07-01) — In progress
Module: `ConferenceRooms` (`Modules/ModuleCatalog.cs`, currently `2.0.12`)
App version: unchanged (module-scoped behavior change)
Owner: Michael

## Problem

Room Finder apply fails on its last step ("Add to room list") for rooms whose target
room list is an **on-prem-mastered, directory-synced distribution group**. Exchange
Online refuses `Add-DistributionGroupMember` against a synced group with:

> The operation ... failed because it's out of the current user's write scope. The
> action 'Add-DistributionGroupMember' ... can't be performed on the object ...
> because the object is being synchronized from your on-premises organization. This
> action should be performed on the object in your on-premises organization.

Observed 2026-07-01 (stakeholder screenshot, "San Jose Conference Rooms"): rooms that
were already members returned OK ("Already in ..."); rooms still needing the add came
back PARTIAL because the cloud add is impossible for a synced group. The rooms being
newly created is a red herring — the blocker is the *list's* mastering, not the room's
age.

## Scope clarification (owner, 2026-07-01) — NOT a reversal of the 2026-06-18 rule

- **Room lists this app CREATES stay cloud-only.** `New-DistributionGroup -RoomList`
  with no `-OrganizationalUnit`, created in EXO's default location. Unchanged. The
  2026-06-18 decision stands.
- **Room lists that ALREADY EXIST can be either** cloud-only or on-prem-synced. The
  app must add a room to whichever kind the target list turns out to be. This plan adds
  handling for the existing-on-prem-synced case only. No `.agents/decisions.md`
  reversal; at most a one-line clarification that existing-list adds cover both
  masterings.

## Approach — try cloud, fall back to on-prem (auto-detect, per row, no mode switch)

Extend the existing, proven pattern the module already uses for City/State/Country
(cloud write rejected as synced → write on-prem via a direct AD runspace → syncs up in
~30 min). The room-list step currently only ever tries the cloud and gives up; this
adds the on-prem fallback as step 2 of that same idea.

In `AddToRoomListAsync` (`Services/ConferenceRoomService.cs`), when the cloud
`Add-DistributionGroupMember` fails:

1. **Cloud add first (unchanged).** If the list is a genuine cloud-only object, this
   succeeds — done, same as today.
2. **Classify the failure.** Reuse the existing pure helper
   `IsOnPremMasteredWriteError(message)` (lines ~210–216) — it already recognizes the
   exact "out of the current user's write scope" / "being synchronized from your
   on-premises organization" wording. Only on a match do we fall back.
   - `NonRoomMailboxAddToRoomList` / "isn't a room mailbox" keeps its existing
     AD-attribute-fix handling (unchanged).
   - Any OTHER error (permission, transient, etc.) surfaces as-is — no fallback, no
     masking.
3. **On-prem fallback:** a new private method (working name
   `AddToRoomListViaAdAsync(roomEmail, roomListName)`) that mirrors
   `SetSyncedAttributesViaAdAsync`'s runspace pattern (lines ~1267–1327):
   - Get the module AD credential via `GetModuleCredentialsAsync`; if absent, return a
     clear "on-prem AD credential not configured" message (same wording style as the
     Set-ADUser path). Fail closed.
   - Open a default runspace, `Import-Module ActiveDirectory`.
   - Resolve the **room's** AD object to an immutable ObjectGUID via the existing
     `ResolveAdObjectGuid` helper (unique-match-or-refuse; reused so the two paths can
     never disagree about which object).
   - Resolve the **group's** AD object. The synced room list exists on-prem as a group.
     Resolve it fail-closed to exactly one object (by mail/displayName; refuse on
     not-found or ambiguous, same posture as the room resolve). Exact resolution
     strategy to be finalized against a real synced list during dev testing (candidate:
     `Get-ADGroup -Filter` on mail then displayName).
   - **Idempotency:** check current membership first (or tolerate AD's "already a
     member" error) so a re-run is a no-op success, matching the cloud path's
     already-member behavior.
   - `Add-ADGroupMember -Identity <groupGuid> -Members <roomGuid> -Credential ...
     -ErrorAction Stop`. On `HadErrors`, return the first error.
4. **Reporting.** On on-prem-fallback success, the step and row message say the room was
   added on-prem and will appear in Room Finder after the next directory sync (~30 min)
   — consistent with the City/State/Country note. On a cloud-only list, the message
   stays "Added to room list '<name>'." The row is only PARTIAL/failed if BOTH the cloud
   add is rejected-as-synced AND the on-prem add fails.

## Idempotency and re-run safety

Both paths already tolerate re-runs (cloud path checks `Get-DistributionGroupMember`
first). The on-prem path must do the same. Re-running any row stays safe.

## Notifications / audit (already in place, confirm unchanged)

`ConferenceRooms 2.0.12` already sends an admin notification per write apply and audits
each apply. The fallback is still one write apply, so no new notification/audit wiring —
just confirm the on-prem-add path is inside the existing audited apply and its outcome
(cloud vs on-prem, success/partial) is captured in the step detail.

## Protected principals (unchanged)

The write target here is the **room mailbox** being added; room mailboxes are not
protected principals, and ConferenceRooms already routes its mutations through the
protected-principal check (2026-06-29 sweep, one of the 12 gated modules). No change to
gating. We are not mutating the group's other members.

## Hard dependency the code cannot satisfy — VERIFY IN DEV

The module's on-prem AD service account is proven to have **Set-ADUser** (attribute
write) rights. **Add-ADGroupMember requires separate "write members" rights** on the
target room-list groups. If the account lacks that delegation in on-prem AD, the on-prem
add fails and the row is PARTIAL with a clear permission error — this is an AD
delegation change on the ops side, not a code fix. Confirm this delegation exists (or
grant it) as part of dev validation. The plan does not assume it.

## Tests

- Pure/static coverage: `IsOnPremMasteredWriteError` classification is already tested;
  add cases pinning the exact stakeholder error string maps to `true` (fallback
  triggers) and that unrelated errors map to `false` (no fallback).
- Factor the fallback DECISION and message construction into pure/testable units where
  possible (e.g. "given cloud-add failed with error X, do we fall back? what message?")
  and cover: rejected-as-synced → fallback path + sync note; other error → surfaced
  as-is; already-member → success no-op.
- The live `Add-ADGroupMember` runspace call cannot be unit-tested (same accepted limit
  as the existing Set-ADUser path); it is covered by the module's Manual Validation
  steps below.
- Prove any new test non-vacuous (revert the production change, confirm it fails,
  restore).

## Manual validation (dev)

1. Existing **on-prem-synced** room list (reproduce "San Jose Conference Rooms" case):
   apply a room whose target list is synced → cloud add rejected → on-prem add succeeds
   → row OK with the ~30-min sync note → membership visible on-prem immediately and in
   EXO after sync.
2. Re-run the same row → already-member no-op success (both directions).
3. Existing/created **cloud-only** room list → cloud add succeeds directly, no fallback,
   original message. (Confirms no regression to the cloud path.)
4. On-prem add attempted with the AD account lacking write-members rights → row PARTIAL
   with a clear permission error, other steps intact. (Confirms fail-closed + honest
   reporting.)

## Versioning

`ConferenceRooms` `2.0.12` → `2.1.0` (new capability: on-prem room-list membership
fallback). App `<VersionPrefix>` unchanged (module-scoped). Bump on the commit that
lands the behavior.

## Out of scope

- No change to list CREATION (stays cloud-only).
- No change to City/State/Country, timezone, Set-Place, or Set-RemoteMailbox paths.
- No new PAM/credential backend; reuses the existing module AD credential.
