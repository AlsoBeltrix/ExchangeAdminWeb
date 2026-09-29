# Playbook: re-anchoring ClickGateRegistry after a page moves

`ExchangeAdminWeb.Tests/ClickGateRegistry.cs` keys every entry by LINE NUMBER in the
page. Any edit that adds or removes lines above a registered control invalidates
every entry below it, and `ExpectedLineCount` on top of that.

Earlier slices avoided this by keeping edits below the registered lines (see
`.agents/token-log.md`, Defender S4). That works until it does not - a change to a
table header or an action bar is above almost everything.

## The procedure that works

1. **Make the page edit first.** Do not try to keep the registry in step as you go.
2. **Map old line -> new line by diffing the committed page against the working copy**
   (`git show HEAD:<page>` vs the file, `difflib.SequenceMatcher`, take the `equal`
   opcodes). Rewrite each entry's line from that map. This is exact where arithmetic
   is not: a change with several insertion points has a different cumulative offset in
   each region, and a single "+N" is wrong for most of them.
3. **Fix the unmapped ones by hand.** A line you EDITED is not `equal`, so it never
   maps - the entry for a control whose own tag you touched comes back unmapped. That
   is correct behaviour from the mapper, not a bug; it is telling you it cannot know.
4. **Run the ClickGate tests and read the reported positions.** Every failure names
   the actual line ("no clickable button at line N", "these ... are not in the
   registry: <page>:M"). For a small change this step alone is faster than mapping.
5. **`ExpectedLineCount` is a line count, not an index.** Splitting on newline
   overcounts by one when the file ends with a newline.

## Traps

- **`GatedTwinButtonLine` is a second line-number field** and the entry regex will not
  catch it. It moves with its button.
- **Snippet-nearest matching picks the wrong duplicate.** Choosing the candidate line
  closest to the stale number looks reasonable and silently mis-anchors entries whose
  snippet appears more than once - it moved three entries to the wrong rows here on
  2026-09-29 before the diff-based map replaced it.
- **A new control needs an entry, not just a shift.** A non-button click target
  (`@onclick` on a div) needs a `NonButtonTarget` with a declared `RefusalMechanism`;
  an input/select needs a `DomSyncedControl` or an `UngatedDomSyncedControl` with a
  written reason.
- **An orphaned test process locks the build.** `MSB3027 / MSB3021 ... being used by
  another process` reads like a build break and is not. Kill `testhost` AND
  `ExchangeAdminWeb.Tests` (and `vstest.console`) - killing only `testhost` misses it,
  which cost a confusing round here on 2026-09-29.

## Verify

`dotnet test ExchangeAdminWeb.slnx --filter "FullyQualifiedName~ClickGate"` must be
green before the slice is done, then the full suite. A registry that points at the
wrong lines still PASSES some of its tests, so a partial green is not done.
