# aud-a26c: a failed reversal is shown as "Reversed", and its Undo button disappears

**Severity**: MEDIUM (proposed). The page states a reversal that did not happen and removes the
control that would retry it. Nothing is mis-mutated.
**Status**: Fixed on branch; awaiting merge
**Branch**: `worktree-agent-acad2a289bd002e11`
**Commit**: `<pending>`
**Reviewer**: implementer (found while fixing aud-a28d, 2026-10-22)
**Evidence head**: `ca6575a21e48596848ca9521d4683424f10cf577`

## Evidence

`Components/Pages/AdminEventLog.razor`, LoadEvents, at the evidence head:

```csharp
// Build the set of operation IDs that have been reversed (referenced by a later undo event)
reversedOperationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var evt in allEvents)
{
    if (!string.IsNullOrEmpty(evt.OriginalOperationId))
        reversedOperationIds.Add(evt.OriginalOperationId);
}
```

(675-681.) Every event naming an `originalOperationId` marks that operation reversed, whatever its
result. `IsReversed` (908-909) then puts a "Reversed" badge on the original edit (266-269) and
hides its Undo button (274).

The only producer of such records is `ADAttributeEditorUndoService.LogUndoAudit` - the AD
Attribute Editor is the only `IUndoableModule` - and it writes `originalOperationId` on EVERY
outcome: success (290), a failed save (281) and an exception (315). So an undo whose Set-ADUser
was denied, or whose save was refused by the protection check inside SaveAsync, or that threw,
marks the original edit "Reversed" on the next load. An administrator reading the log is told the
change was retracted when it is still in place - the exact misbelief aud-a26 existed to prevent -
and the button to try again is gone. Within the session the page adds to the set only on success
(1024); the wrong answer arrives on the next load.

aud-a28d, which audits the undo's own refusals, would have made this worse: every refused undo,
a protected target's included, would have marked its edit "Reversed". That is why this record
lands first.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"; further defects found while fixing this
module fixed as their own commit and record). One commit `fix(event-log): ... (aud-a26c)`.
Module behaviour: Admin Event Log, the page that hosts the AD Attribute Editor's undo (the
supervisor owns the version bump; the AD Attribute Editor itself is unchanged by this commit).

Scope: the rule moves into `Services/ReversedOperations.cs`, generic over the entry so it can be
tested - the page's entry record is private, the `AuditFirstMerge` precedent - and changes in one
respect: only a SUCCESSFUL record referencing an operation marks it reversed. The page calls it in
place of the loop. The in-session add on success, the badge, the button condition and every
record's shape are unchanged.

Proof: reproduction first - the helper carries the page's current rule verbatim, the page is wired
to it (no behaviour change), and the new tests fail; then the rule changes. Tests: the rule over
plain entries (success marks; failed, refused and blank do not; case-insensitive), the rule over
REAL undo records written through `AttributeEditorHarness` (a successful reversal marks its edit;
a failed one does not), and a page-wiring scan. Mutations at the call sites: the Success condition
removed; the page's inline loop restored.

## Executed reproduction - 2026-10-22

Branch `worktree-agent-acad2a289bd002e11` after aud-a28c (`df6a378d`). `ReversedOperations` first
held the page's rule verbatim (MD5 `4EB4E513E2E27DB26495ABFD59FBE42F`) and the page was wired to
it, a refactor with no behaviour change. `ExchangeAdminWeb.Tests/ReversedOperationsTests.cs`: 2
failed / 7 passed, 0 compiler errors (`acad2a-a26c-reproduction.log` in the session scratchpad):

- the record a denied Set-ADUser leaves, written by the real undo service and read back as the page
  reads it: `Assert.DoesNotContain() Failure: Item found in set` - the edit was marked reversed;
- a failed plain entry: `Assert.Empty() Failure: Collection was not empty`.

The successful reversal, blank ids, case-insensitivity, a failure followed by a success, and the
wiring scan passed: the refactor itself changed nothing.

## Fix

`Services/ReversedOperations.cs` (new): an operation is reversed when a record names it as its
`originalOperationId` AND that record's result is `Success`. `Components/Pages/AdminEventLog.razor`:
the loop is replaced by `reversedOperationIds = ReversedOperations.From(allEvents, e =>
e.OriginalOperationId, e => e.Result);` (net two lines fewer; the page is `NotYetConverted` in
ClickGateRegistry, so no line keys move). Nothing else on the page changed.

## Guard proof

Same session-local driver as aud-a26b, filter `FullyQualifiedName~ReversedOperationsTests`;
after the round a rebuild and the filter, 9/9 green. Every mutant built with 0 errors.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named test failing |
|---|---|---|---|
| the `Success` condition removed | `ReversedOperations.cs`, `CECE932714F60D51372110A61946387E` | `841E5B08699835DAD5DA6596FEE2CAAB` | `AReversalThatFailed_LeavesItsEditUndoable` (and `AFailedReversal_DoesNotMarkItsOperation`) |
| the page's inline loop restored | `AdminEventLog.razor`, `091926C3D48BE9B8FD14BF5F9CA6DA59` | `878A79886ABF2B9F070E86475986C531` | `ThePageTakesItsMarkersFromTheRule` |

Each restore matched its original MD5. Comments were in place before the round.

## Gates - 2026-10-22

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 40 warnings (incremental), all
  pre-existing.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: exit 0, 4,533
  passed / 3 skipped / 0 failed, 4,536 total, 5m21s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0.
  `git diff --check HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` with the new files staged: exit 0.

No PowerShell source, AD module, directory, push or deployment. No coder self-review verdict is
claimed; the record was opened by the implementer.

## Known gaps

- The page was not rendered (no bUnit); the badge and the hidden button follow from `IsReversed`
  by reading, and the wiring is a source scan.
- Event-log records from before this fix are re-read under the new rule, so a past failed
  reversal stops showing "Reversed" on the next load - the intended correction, but visible.
