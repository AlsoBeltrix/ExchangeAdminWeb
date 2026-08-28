# pgwt-6: A renamed protected target is badged "protects nothing", and re-adding it duplicates the GUID

**Severity**: MEDIUM - the false badge invites an admin to remove protection that is still
effective; string-keyed dedupe then allows two rows for one GUID.
**Status**: In progress
**Branch**: `-` (default-branch mode)
**Commit**: (filled in after commit)

## Evidence

The GUID half of a target row keeps protecting across rename/move by design (T0). The
stale-entry sweep, however, looks up the stored DN and badges NotFound rows "protects
nothing. Remove it" - false for a GUID-matched row. The add path dedupes on the full
`guid|DN` string, so re-adding the renamed group stores a second row sharing the GUID.

## Predicted observable failure

Rename a protected target group: the admin page tells the operator its row protects
nothing and should be removed - removing it un-protects the group. Re-adding first leaves
two rows for one GUID; deleting one later behaves confusingly.

## Approach

Two halves. (1) Target rows leave the stale sweep entirely (the patterns precedent:
entries whose semantics the DN lookup cannot judge are not swept) - a stale DN row still
protects via GUID, so the badge's claim is wrong for this kind by construction. (2) The
add path dedupes targets by parsed GUID: an accepted entry REPLACES any existing row with
the same GUID, refreshing the stored DN - the reviewer's refresh suggestion.

## Files changed

- `Components/Pages/AdminSettings.razor` - sweep exclusion; GUID-keyed replace-on-add
- `ExchangeAdminWeb.Tests/ProtectedGroupWriteTargetTests.cs` - tripwire + validator loop

## Guard proof

- `ProtectedGroupWriteTargetTests::AdminPage_TargetRows_AreNotSwept_AndDedupeByGuid` -
  revert fails, restore passes.

## Coder dispute (if any)

None on substance. Trade recorded: a genuinely deleted group's target row is no longer
badged; it protects nothing and harms nothing, and the drift sweep can flag it later.

## Known gaps

None.

## Reviewer comments

`Reviewer: codex-commercial / gpt-5.6-sol / xhigh / standard` (owner standing dispatch),
generation pass over `8700531..5336072`, verdict `findings` (7), capability_ok true.
Verification round: NOT DISPATCHED - blocked by the workspace-write transport fault
recorded on lst-1.
