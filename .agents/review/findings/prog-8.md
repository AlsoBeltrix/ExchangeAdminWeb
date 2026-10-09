# prog-8: A third guard anchored on prose, found the same way as the first two

**Severity**: LOW - no operator-facing failure. It blocks correct changes with a false red.
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: see `.agents/review/index.md`

## Evidence

`ExchangeAdminWeb.Tests/ProtectedGroupWriteTargetTests.cs:417` locates
`SweepExistingEntriesAsync` by raw page text, `:419` bounds it by the next method signature,
and `:421` asserts:

```csharp
Assert.DoesNotContain("ppTargets", page[sweepStart..sweepEnd], StringComparison.Ordinal);
```

That slice is **not** passed through `ProgressScan.CodeView` or any comment stripper.

**The S4 agent avoided it by accident of wording.** `Components/Pages/AdminSettings.razor:778-782`
spells its comment `Group TARGETS` rather than `ppTargets`; the executable sweep correctly
omits the token either way. Codex confirmed the guard would have fired on the comment.

Predicted observable failure: a correct future change that adds an explanatory comment or a
string literal mentioning `ppTargets` inside `SweepExistingEntriesAsync` fails
`AdminPage_TargetRows_AreNotSwept_AndDedupeByGuid` while target rows are still not swept. CI
blocked on prose.

## The pattern this completes

**Third guard in this repo found anchored on raw text rather than code**, and the third found
the same way - by someone tripping it or nearly tripping it, never by reading it:

| Finding | Guard | How it surfaced |
|---|---|---|
| `prog-6` | `IntuneDevicesPageTests` / `RiskyUsersPageTests`, `LastIndexOf("finally")` | an agent's own comment contained the word |
| `prog-3` | `ProgressRegistryTests`, covered-call lookup | a reviewer constructed a counterfeit literal |
| `prog-8` | `ProtectedGroupWriteTargetTests`, `DoesNotContain("ppTargets")` | an agent avoided it by chance of wording |

The repo now has TWO working comment-strippers - `ClickGateSource.BlankComments` and
`ProgressScan.CodeView` (the stricter, which also blanks literals; prog-6 chose it after
finding `BlankComments` refuses to blank a `//` on a line where a quote opens earlier). The
fix is always to use one of them.

**Worth a sweep rather than a third one-off fix:** any test slicing raw page or method text
and asserting on a token is this defect. That sweep is not in any approved plan.

## Approach

Taken, as recorded: both slice anchors and the `DoesNotContain` now read
`ProgressScan.CodeView(page)`, which blanks comments AND string literals in one lexical pass
and preserves every offset. The intent is unchanged and is now what the assertion actually
says - the sweep must not READ `ppTargets`.

`ProgressScan.CodeView` was chosen over `ClickGateSource.BlankComments` for the reason
`a3da1e3` recorded on `prog-6`: `BlankComments` refuses to blank a `//` on any line where a
quote opens earlier, and it leaves string literals alone, so a literal naming `ppTargets`
would still trip the negative assertion.

Two assertions in the same test are split rather than converted wholesale, because they are
not the same kind of claim:

- `Assert.Contains("objectKind == \"GroupTarget\"", ...)` keeps reading the RAW page. Its
  needle IS a string literal; the code view blanks it, so a code-viewed slice could never
  match. Offsets are preserved, so the raw page is sliced at the code-view index.
- `Assert.Contains("RemoveAll", ...)` moved to the code view. A bare identifier in a positive
  assertion is the same defect wearing the other hat: a comment naming `RemoveAll` would
  satisfy it with the dedupe deleted.

The `ProgressScan` lexer is REFERENCED across classes, not relocated. The shared-tool question
is argued in the `prog-9`-era sweep commit and in `prog-4`: lifting a lexer that carries two
open findings into a shared home widens those findings and touches `ProgressRegistryTests.cs`,
which `prog-4` has kept off-limits.

## Files changed

- `ExchangeAdminWeb.Tests/ProtectedGroupWriteTargetTests.cs` -
  `AdminPage_TargetRows_AreNotSwept_AndDedupeByGuid` only. Test project only; nothing shipped
  changed, so no version bump.

## Guard proof

The mutation the finding predicts, with the sweep's CODE correct throughout: the comment
inside `SweepExistingEntriesAsync` reworded from `Group TARGETS` to `The ppTargets rows`,
line-neutral, so the page stayed at 999 lines.

| Run | Test file | Page | Result |
|---|---|---|---|
| 1 | at `b24cf56` (pre-fix) | comment mutated | **1 failed / 30 passed** - `Assert.DoesNotContain() Failure: Sub-string found`, `ProtectedGroupWriteTargetTests.cs:421`, quoting the comment text |
| 2 | fixed | SAME mutation, byte-identical | **0 failed / 31 passed** |

Only the test file differs between the two runs, so the code view is what turns it green.

**And the negative control, which is the half that proves the fix did not simply defang the
guard:** with the comment restored, a REAL read added inside the sweep -
`targets.AddRange(ppTargets.Select(t => (t, "Group")));` appended to the `ppOus` line - the
FIXED test fails, 1 failed / 0 passed, same assertion and same message. The guard still
catches the defect it exists for.

Worth recording because it cost a run: the first attempt at that control used `t.Dn` and did
not compile (`ppTargets` holds strings). A probe that does not compile is neither a pass nor a
fail - it is no evidence - and the same trap is logged against `8b56902`. Re-anchored and
re-run.

Page restored byte-identical by md5 (`7b07fe69b8a715cfdfab406980295846`) and touched so
MSBuild rebuilt, after each mutation.

## The sweep this finding called for - done, in the commit after the fix

"Worth a sweep rather than a third one-off fix" was the finding's own instruction. The test
project was surveyed end to end: **63 files read raw source, 129 of their expressions slice
it**. The sweep is scoped to the shape the three precedents actually share - a slice whose
ANCHOR or whose NEEDLE is a token that can occur in prose, which is the false-RED class this
finding's severity line describes.

**Already defended, found by reading rather than assumed:**

- `ClickGateSource.Text` is comment-blanked at construction (`ClickGateSource.cs:24`), so
  every slice in `ClickGateTests` and `ClickGateStuckFlagTests` - including
  `IndexOf("try")`, `IndexOf("await ")` and the `LastIndexOf` anchors - already reads blanked
  text. It keeps literals on purpose: handler names live inside quoted markup attributes, the
  same asymmetry `ProgressScan` draws between its two views.
- Seven classes strip comments in their own helper before slicing: `AppLayoutCssTests`,
  `Comms10kReplaceSequenceTests`, `GlobalProgressWiringTests`, `InFlightWorkGuardTests`,
  `MigrationBatchTickBoxTests`, `ServiceHealthPageTests`, `UiThemeCssTests`.
  `RiskyUsersPageTests.SetPageBody` strips for the `SetPage` body specifically, and
  `MigrationStatusPageTests` already had `StripLineComments` / `StripRazorComments` on its
  markup censuses.
- `EventSubscriptionLifetimeTests` uses `ProgressScan.StripComments` and is out of scope under
  open finding `leak-1`.

**Converted - nineteen assertion sites across six classes**, two of them shared `WriteIndex`
helpers that between them anchor every write-ordering assertion in their class:

| Class | What was anchored on prose |
|---|---|
| `IntuneDevicesPageTests` | `WriteIndex` helper; `else` branch bound + `DoesNotContain("Refuse(")`; the nine-`return;` census + per-exit `Refuse(`; the forward `finally` anchor `a3da1e3` left standing; `DoesNotContain("device.AzureADDeviceId")`; `DoesNotContain("throw")` twice |
| `RiskyUsersPageTests` | `WriteIndex` helper; the nine-`return;` census; `DoesNotContain("SendAdminNotificationAsync")` |
| `MigrationStatusPageTests` | `IndexOf("return;")`; `IndexOf("await ")`; `LastIndexOf("await ")` |
| `GroupBulkActionsWiringTests` | four snapshot-before-await tests: `IndexOf("await ")` plus `DoesNotContain("selected.")` |
| `UsageTrackerWiringTests` | the `else` anchor bounding the range-stale assertion |
| `GroupMemberNestingProtectionTests` | the `else` anchor bounding the Group row branch |

**`ClickGateSource.BlankComments` was the right tool exactly once.** The
`GroupMemberNestingProtectionTests` branch is MARKUP - a code view blanks the quoted attribute
values the test anchors and asserts on. Everything else converted is C#, where `CodeView` is
correct and stricter.

**Left raw deliberately, with the reason in each case:**

- **Needle IS a string literal from the source** - log messages, audit action names, UI text
  (`"no principal to protect"`, `"could not be removed"`, `"The action itself completed."`,
  `"RiskyUsers_Lookup"`). A code view blanks it, so these must read raw. Where such an
  assertion sat beside a code one, the two were SPLIT rather than both moved.
- **Anchor is a full member signature** - `"public async Task<GroupMemberList> GetMembersAsync("`,
  `"private async Task DownloadCsvAsync()"` and the like, the shape every method-bounding
  helper in a dozen classes uses. A declaration carrying modifiers, return type and open paren
  is not a phrase anyone writes in prose.
- **Needle is a distinctive multi-token fragment** - `AddCommand("Get-ADGroupMember")`,
  `objectKind == "Group" && normalized.Contains('=')`, `if (resolvedMember.IsGroup)`. Most
  carry a literal and so must stay raw regardless; none is reproduced verbatim by prose.
- **The assertion is about markup or CSS** - the `<thead>` slices in
  `MigrationStatusPageTests`, the `<li>` checks in `GroupBulkActionsWiringTests`, the
  declaration blocks in `AppLayoutCssTests`, `UiThemeCssTests` and `SidebarScrollCssTests`. A
  code view would be wrong, and these are already comment-stripped where it matters.
- **Positive `Assert.Contains` over a WHOLE file rather than a slice** - e.g.
  `ProtectedGroupWriteTargetTests.AdminPage_WiresTheTargetList`. This is the weaker false-GREEN
  variant, not this finding's shape, and its needles are markup attributes anyway. Surveyed and
  named rather than silently skipped.

**Sweep guard proof: a three-way differential, each mutation line-neutral and the pages'
CODE correct throughout**, chosen to cover the three distinct mechanisms rather than one per
site:

| Mutation | Mechanism it exercises |
|---|---|
| `IntuneDevices.razor` - `// it must never throw` appended to the return inside `RemoveEntraObjectAsync` | negative assertion over a code slice |
| `Migration.razor` - a comment inside `FetchUserReport` reworded to contain `await ` between the guard and the write | BACKWARD anchor (`LastIndexOf`) slipping in the failing direction |
| `SelfServiceGroups.razor` - `@* the else arm is below *@` appended to the Group branch's opening brace | FORWARD anchor slipping, over markup, via `BlankComments` |

The "before" run used a scratch `git worktree` detached at the fix commit `53fcb5d`, so no
uncommitted work was ever discarded; the three mutated pages were md5-verified
byte-identical between the two trees.

- Pre-sweep test files + mutated pages: **3 failed / 368 passed**, naming exactly
  `IntuneDevices_ExecuteAction_EntraHalfNeverThrowsIntoTheIntuneAuditPath`,
  `NoReportTextIsWrittenByASupersededFetch` and
  `Page_RemovesByGuid_AndGroupRowsOnlyEnterThePendingState` - and nothing else.
- Swept test files + the byte-identical mutations: **0 failed / 371 passed**.

Only the test files differ between the runs, so the code view is what turns them green.
HONESTLY: thirteen of the nineteen sites are not individually probed - they are the same three
mechanisms on different methods, and a fourth copy of a result is not a fourth result. Pages
restored byte-identical by md5 and touched; the probe worktree removed.

## Reviewer comments

Raised during the review of `ee1c0f9..6a561df` (slice S4), which was otherwise **sound**:
the AdminSettings dispatchers are thin and reach registered reporting methods, the
AdminBulkJobs handlers were genuinely synchronous before and now yield after `Begin`, the
`JobChanged` split preserves the same reloads without strobing, and `NotFound` versus
`Unavailable` is drawn at the right layer.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard.
Verification was no-build: 80/80 on the three filtered classes, `git diff --check` clean.
Raw output: `.agents/review/prog-s4.result.json`.

## Closeout

Fixed on main. Release build 0 errors; `dotnet test ExchangeAdminWeb.slnx` 3674 passed, 0
failed, 3 skipped (unchanged - no test added, one re-anchored); `dotnet format
--verify-no-changes` exit 0; `git diff --check HEAD` clean; every added line ASCII.

The sweep this finding calls for landed in the commit immediately after the fix, as the
finding's "worth a sweep rather than a third one-off fix" asks. Its survey, conversions,
deliberate exclusions and three-way differential probe are in the section above. Release
build 0 errors; `dotnet test ExchangeAdminWeb.slnx` 3674 passed, 0 failed, 3 skipped after
the sweep too; `dotnet format --verify-no-changes` exit 0; `git diff --check HEAD` clean;
every added line ASCII.

**Not closed by the sweep, and named so nobody reads it as closed:** the false-GREEN class -
a positive `Assert.Contains` satisfied by a comment - is surveyed but not converted. It is a
larger and weaker class than this finding's, and converting it means separating
literal-needle from code-needle assertions at roughly sixty more sites. If the owner wants
it, it is a work stream, not a sweep.
