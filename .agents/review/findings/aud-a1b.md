# aud-a1b: Group-SID entry validates the shape, not the object

**Severity**: MEDIUM - an authorization grant can be saved that silently grants nothing, and
the operator is told the opposite.
**Status**: Open - not approved for implementation
**Branch**: -
**Commit**: -

## Evidence

Raised by codex-commercial / `gpt-6-astra` / max while reviewing `5059c6f` (`aud-a1`).

`Components/Pages/ModuleConfig.razor` validates a group entry with
`SectionAccessGroupIdentity.IsUsableGroupSid(input)`. That tests the **shape** of a SID. It
cannot tell a group's SID from a user's, or from one that resolves to nothing at all. The
autocomplete forwards typed input straight through, so a global admin can type a canonical SID
and save it without selecting anything - including while Active Directory is unreachable.

Two consequences:

1. The stored grant authorizes nobody, and nothing says so. `GetUnusableGrants` judges shape
   too, so the row passes that inventory as well.
2. The operator message - *"Pick a group from the suggestion list - typed text is not accepted
   here"* - is false. Typed text that happens to be SID-shaped is accepted.

**This is not new to `aud-a1`.** `AddAccessGroup` has used the identical guard for section
access and servicer grants since `docs/SectionAccessSidStorage-Plan.md`. `aud-a1` extended the
same pattern to module admins, which is how it surfaced.

## Predicted observable failure

An admin pastes a SID from a ticket - a user's, or a group that has since been deleted - into
the module-admin or section-access box. It saves without complaint. Nobody gains access, the
grant list looks populated, and the unusable-grant report says everything is fine. The failure
appears later as "why can't my team open this module", with no signal pointing at the row.

## Approach - not yet designed

The obvious options, neither chosen:

- **Require selection.** `OnResultSelected` already fires with the resolved object, so the page
  could accept only SIDs it saw come back from a search this session. Cheap, but it breaks
  pasting a known-good SID and would need a deliberate escape hatch.
- **Resolve on save.** Look the SID up and confirm it is a group before storing. Correct, but
  it puts a directory call on the save path and needs a fail-closed answer for "AD unreachable"
  - which is exactly when an admin is most likely to be fixing access.

**Whichever is chosen must be applied to `AddAccessGroup` and `AddModuleAdmin` together.**
Fixing one leaves the other with the same hole and two adjacent controls that behave
differently, which is worse than the current consistent weakness.

## Files involved

- `Components/Pages/ModuleConfig.razor` - `AddModuleAdmin`, `AddAccessGroup`
- `Authorization/SectionAccessGroupIdentity.cs` - `IsUsableGroupSid`
- `Services/ModuleAdminService.cs` - `TryGetUnusableGrants`, which inherits the same shape-only
  judgement

## Interim mitigation, already landed

`aud-a1`'s repair commit corrects the false comment on `AddModuleAdmin` so the code no longer
claims a guarantee it does not provide. The operator-facing message is unchanged and is still
misleading - changing it is part of this finding, not a separate cosmetic fix, because the
right wording depends on which approach above is taken.

## Known gaps

Not verified against a live directory: whether `IsUsableGroupSid` would accept a well-formed
SID from a foreign forest, and what the save path does with one. Probing AD is forbidden
without a direct owner go.

## Reviewer comments

`Reviewer: codex-commercial / gpt-6-astra / max`, reviewing `5059c6f`. Finding 3 of 4.
Raw output: `.agents/review/out/aud-a1.txt` (machine-local, gitignored).

## Closeout

Pending owner approval. Recorded under the 2026-10-19 ruling that everything needing
remediation gets planned and fixed; it is not in a wave yet because the approach is undecided.
