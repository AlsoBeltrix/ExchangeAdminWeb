# mtps-1: The Access tab tells administrators the split is live while nothing enforces it

**Severity**: MEDIUM - the descriptions rendered beside each grant on the Module Config
Access tab describe the FINAL permission split as current behaviour. Slice 1 is inert by
design, so every runtime check still accepts the parent `MessageTrace` policy. An
administrator reading the page grants on false information. It is MEDIUM rather than HIGH
because it opens no NEW exposure: a group holding `MessageTrace` could run trace search
before slice 1 and can run it after. What is new is the page asserting otherwise.
**Status**: Admitted
**Branch**: - (direct to main)
**Commit**: the commit that completes this record; read it from `git log -1 -- .agents/review/findings/mtps-1.md`

## Evidence

`Modules/ModuleCatalog.cs:279` (as of `9d97e4b`) - the parent `MessageTrace` description
was rewritten to "Open the module and analyse message headers pasted in or uploaded as a
file; the analysis is local and reads nothing from Exchange. Searching message traces needs
the separate Search permission."

`Modules/ModuleCatalog.cs:282-284` - `MessageTraceSearch` is described as the grant for
trace search and the detail exports.

Both sentences reach the Access tab from the descriptor:
`Components/Pages/ModuleConfig.razor:184-187` renders `PermissionDescriptionFor(alias)`
under each grant heading, and `:766-776` is that lookup.

But no runtime check consults the new alias. `Components/Pages/MessageTrace.razor:7`
(`@attribute [Authorize(Policy = "MessageTrace")]`) and `:659` still authorize the parent;
`RunTrace` reaches `MsgTrace.GetMessageTraceAsync` at `:795` and `:807` with no
`MessageTraceSearch` check; `Components/Pages/MessageTraceReports.razor:4` and `:126`
authorize the parent too. Enforcement is slices 3 and 4, which have not been written.

## Predicted observable failure

An administrator opens Module Config -> Message Analysis -> Access, reads that
`MessageTrace` grants header analysis only, and adds a group to it that should NOT have
trace search. Members of that group open the module and run trace searches immediately,
reading any user's senders, recipients, subjects and delivery detail, because every gate
still accepts `MessageTrace`. The page said the grant excluded that; it does not.

The mirror case is milder but also wrong: granting `MessageTraceSearch` changes nobody's
capability today, while the row describes it as the trace-search grant.

**This is not hypothetical.** The owner deployed `9d97e4b` and granted against both aliases
on 2026-09-22 with this copy on screen - `MessageTrace` to `ExchangeWebAdmins` and
`ExchangeWebPerms`, `MessageTraceSearch` to `ExchangeWebAdmins` alone. Read literally, the
page says `ExchangeWebPerms` cannot search traces. It can.

## Approach

Make the copy temporally accurate instead of aspirational, which is what the repo's
user-facing-string rule already requires (owner ruling 2026-09-21: every user-facing string
states what happened and what to do). Both descriptions now say plainly that the split is
declared and NOT yet enforced, what that means for a grant made today, and what to do.

Slice 3 flips them to the final wording in the same commit that makes the gates consult the
new alias, and the plan now carries that as a numbered step rather than an intention.

## Files changed

- `Modules/ModuleCatalog.cs` - both descriptions, module version.
- `ExchangeAdminWeb.Tests/ModuleCatalogTests.cs` - the tripwire test.
- `docs/MessageTracePermissionSplit-Plan.md` - slice 3 gains the flip step.
- `.agents/review/findings/mtps-1.md`, `.agents/review/index.md` - this record.

## Guard proof

`Catalog_MessageTrace_DescriptionsSayTheSplitIsNotEnforcedYet` asserts both descriptions
carry the not-yet-enforced statement. Mutation: restore the `9d97e4b` wording to either
description and the test fails on that description. It is a deliberate tripwire on slice 3 -
when enforcement lands, this test must be rewritten to assert the final wording, and it
fails loudly until someone does, which is the point.

## Coder dispute

None. Confirmed line by line against the four cited files before admitting.

## Known gaps

The test pins the presence of a sentence, not the truth of it. Nothing mechanically ties the
wording to whether a gate consults the alias; that link is the slice 3 step in the plan and a
reviewer's job. Stated rather than papered over.

## Reviewer comments

Round 1, Change review over `9d97e4b^..9d97e4b`:
Reviewer: codex / `@azure-openai-eus2-global/gpt-5.5-dzs` / xhigh / standard, 2026-09-22.
Capability proof passed (read `docs/MessageTracePermissionSplit-Plan.md`; ran
`git diff --stat 9d97e4b^ 9d97e4b`). Verdict **unsound**, one MEDIUM finding, no
CRITICAL and no HIGH. It cleared the code side explicitly: "the new alias is declared in
the catalog and tests, while the page attributes and runtime checks still use MessageTrace",
so the inertness claim slice 1 rests on was confirmed rather than assumed.

Per `.agents/decisions.md` 2026-08-31, reviewer verification rounds are CRITICAL-only and
need an explicit owner go; a MEDIUM closes on the coder-side guard proof above.

## Closeout

Fix and this record land in one commit on master. No branch.
