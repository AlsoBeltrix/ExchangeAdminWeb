# aud-a1b: Group-SID entry validates the shape, not the object

**Severity**: MEDIUM - an authorization grant can be saved that silently grants nothing, and
the operator is told the opposite.
**Status**: Closed - codex group review (authz-batch, 2026-10-21) raised no defect in this change
**Branch**: `worktree-agent-acf74809ad7ee3f7d`
**Commit**: `0ad4ee3` (merged)

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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("ALL the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21). The
option taken is **resolve on save**, applied to both controls together, as the record requires.

**Mechanism.**

1. New `ADDirectorySearchService.ValidateGroupSid` - the app's existing directory abstraction for
   admin input validation, already injected into the page as `ADSearch`. It looks the SID up
   across the forest through the global catalog (a grant may name another domain's group) and
   reports Found with the object's class, NotFound, or Unavailable. A global catalog that cannot
   be resolved is Unavailable, never a local-domain search: a foreign group would read as
   missing.
2. New pure `Authorization/GroupGrantValidation`: decides each NEW grant - not a usable group SID,
   no such object, an object that is not a group, or directory unavailable - and words the
   refusal. Rows already stored are not re-checked, so an outage never blocks saving a removal.
3. `SaveSectionAccessAsync` and `SaveModuleAdminsAsync` snapshot their lists, confirm every grant
   not in the loaded baseline through one new reported step (`ConfirmNewGroupGrantsAsync`, with its
   own progress activity), and refuse the whole save when any is refused. They persist the
   SNAPSHOT, so a row added while the lookup is in flight cannot ride an unchecked save.
4. Not on the authorization path: nothing reads the directory when a grant is evaluated.
5. The add-time message - "typed text is not accepted here" - is replaced by one that says what is
   true: a pasted group SID is accepted, and is confirmed against the directory on save.

**Files.** `Services/ADDirectorySearchService.cs`, `Authorization/GroupGrantValidation.cs` (new),
`Components/Pages/ModuleConfig.razor` (not ClickGate-registered - "tier 2, not approved"),
`ExchangeAdminWeb.Tests/ProgressRegistry.cs` (the new reported step), tests in a new
`GroupGrantValidationTests.cs`.

**Tests.** Every refusal reason and the accept case on the pure decision; the forest-wide SID
filter; code-view tripwires that both saves confirm before they persist, persist the snapshot,
and that the lookup never falls back to the local domain.

**Mutation, at the call sites.** Remove the confirmation from each save (its tripwire fails);
accept a found object that is not a group (the decision test fails); let the lookup search the
local domain when no catalog resolves (its tripwire fails).

## Files involved

- `Components/Pages/ModuleConfig.razor` - `AddModuleAdmin`, `AddAccessGroup`
- `Authorization/SectionAccessGroupIdentity.cs` - `IsUsableGroupSid`
- `Services/ModuleAdminService.cs` - `TryGetUnusableGrants`, which inherits the same shape-only
  judgement

## Interim mitigation, already landed

`aud-a1`'s repair commit corrects the false comment on `AddModuleAdmin` so the code no longer
claims a guarantee it does not provide. The operator-facing message was left unchanged and
misleading - changing it is part of this finding, not a separate cosmetic fix, because the
right wording depends on which approach above is taken. With resolve-on-save chosen it now
says a pasted group SID is accepted and is confirmed against Active Directory on save.

## Guard proof

`ExchangeAdminWeb.Tests/GroupGrantValidationTests.cs`:

| Test | What it pins |
| --- | --- |
| `AGroupTheDirectoryHolds_MayBeSaved` | the accept case, so the refusals cannot be passing by refusing everything |
| `AUsersOrComputersSid_IsRefused` (3) | the finding's first case: a well-shaped SID that belongs to a user or computer |
| `ASidThatResolvesToNothing_IsRefused` | the second: a SID that resolves to nothing |
| `ADirectoryThatCannotBeAsked_IsRefused_AsAnOutageNotATypo` | fail closed, with its own reason |
| `Refusals_AskTheDirectoryAboutEachNewGrant_AndReportEveryRefusal`, `AValueThatIsNotAUsableGroupSid_IsRefusedWithoutALookup` (4), `NoNewGrants_AskNothing`, `TheRefusalNamesEveryRefusedGrant` | the save-time orchestration |
| `BuildSidFilter_KeysOnObjectSid` | the lookup's shape |
| `TheAccessSave_ConfirmsNewGrantsBeforeItPersists_AndPersistsTheSnapshot`, `TheModuleAdminSave_ConfirmsNewGrantsBeforeItPersists_AndPersistsTheSnapshot` | code-view tripwires: BOTH controls confirm before the store write, and persist what was confirmed |
| `TheConfirmation_AsksTheDirectoryForEachGrant`, `TheSidLookup_NeverFallsBackToTheLocalDomain` | code-view tripwires over the parts that need a circuit or a DC |
| `TheOldFalseMessage_IsGone` | consequence 2: the message that said typed text is refused |

`ProgressRegistry`: `ConfirmNewGroupGrantsAsync` is a new Reported step (activity opened before
the `Task.Run`, `Items(count)`), and the `SaveCurrentTabAsync` exemption's reason now says the
two saves reach it - the registry's own tests hold the activity to the call it covers.

Mutations, each run this session; every file restored byte-identical, touched and rebuilt green:

| Mutation | File md5 (before = restored) | Mutated md5 | Named test | Result |
| --- | --- | --- | --- | --- |
| module-admin save skips the confirmation (call site) | `ModuleConfig.razor` `51fb96723c19b7ea3956ea45be3b2283` | `78662dbe8fb6150aea409cff6c4e428e` | `TheModuleAdminSave_ConfirmsNewGrantsBeforeItPersists_AndPersistsTheSnapshot` | FAIL |
| access save skips the confirmation (call site) | `ModuleConfig.razor` `51fb96723c19b7ea3956ea45be3b2283` | `964175cb0e67229979f985837038d8de` | `TheAccessSave_ConfirmsNewGrantsBeforeItPersists_AndPersistsTheSnapshot` | FAIL |
| a found object that is not a group is accepted | `GroupGrantValidation.cs` `114e387edbbd128a49f7e1401b3225a4` | `42658a6df9474c51d7dab12116cb4b15` | `AUsersOrComputersSid_IsRefused` (all 3) | FAIL |
| the SID lookup loses its no-catalog guard | `ADDirectorySearchService.cs` `ae73f444f5a706eb481de37818accfd0` | `5cbba2e4270db4be70869bdd83ade666` | `TheSidLookup_NeverFallsBackToTheLocalDomain` | FAIL |

## Known gaps

**Not verified against a live directory; probing AD is forbidden without a direct owner go.**
That a global-catalog search by `objectSid` returns the object and its class for every domain of
the forest is documented behaviour; the picker's GC search is measured live, this query is not.
The page wiring is pinned by tripwire only - there is no bUnit harness.

**Rows saved before this fix are not re-checked**, and `ModuleAdminService.TryGetUnusableGrants`
still judges shape: it is the store-only inventory, and making it read the directory would put a
directory dependency on a report nothing in the UI shows yet (`aud-a1`'s Known gaps). A stored
user's SID, or a deleted group's, still authorizes nobody and still reads as usable there.

**Behaviour changes:** saving the Access or Admins section of a module's configuration now reads
the directory once per NEW grant, under the app pool's identity (`.agents/decisions.md`
2026-07-31, whose environment condition this inherits), and refuses the save - naming each entry
- when any is not a group, does not exist, or cannot be confirmed. A save that only removes
grants, or changes nothing in those lists, does not read the directory.

## Reviewer comments

`Reviewer: codex-commercial / gpt-6-astra / max`, reviewing `5059c6f`. Finding 3 of 4.
Raw output: `.agents/review/out/aud-a1.txt` (machine-local, gitignored).

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent on
`worktree-agent-acf74809ad7ee3f7d`. Independent review of the fix pending.

## Closeout

Fixed on branch, not merged, not pushed. The module-configuration page is shared admin
infrastructure (`ModuleConfig.razor`, `ADDirectorySearchService`), so the base app version moves
at merge; no module version.
