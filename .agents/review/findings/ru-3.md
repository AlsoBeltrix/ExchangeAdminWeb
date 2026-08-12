# ru-3: The test plan borrowed a private stub and ignored two catalog assertions the new descriptor breaks

**Severity**: MEDIUM -- the named test helper is inaccessible, so the new tests would not
compile; and the S1 commit fails two existing tests that the plan never mentioned.
**Status**: Verified
**Branch**: --
**Commit**: (this commit) -- plan revision

## Evidence

Two separate defects in one finding, both in the plan's test guidance.

1. `docs/RiskyUsersModule-Plan.md` S2 instructed tests to drive the seam "through
   `GraphTokenClientTests.StubHandler`". That type is declared
   `private sealed class StubHandler : HttpMessageHandler` at
   `ExchangeAdminWeb.Tests/GraphTokenClientTests.cs:12`. A private nested type is not
   reachable from another test class.

2. `ExchangeAdminWeb.Tests/ModuleCatalogTests.cs:16` asserts
   `Assert.Equal(25, _catalog.GetAll().Count)` with the comment
   "25 modules (24 operational + 1 config-only)", and `:109` asserts
   `Assert.Equal(34, aliases.Count)`. Adding `RiskyUsers` with one granular permission
   makes those 26 and 36. The plan's S1 listed only "catalog tests" as new work and named
   neither existing assertion.

## Predicted observable failure

1. `RiskyUsersServiceTests` fails to compile with CS0122 (inaccessible due to protection
   level) on first reference to `StubHandler`.
2. The S1 commit -- descriptor only, no other code -- fails
   `dotnet test ExchangeAdminWeb.slnx` on two assertions in a file the implementer had no
   reason to open. Combined with ru-2, the first slice would have failed both build and
   test for two unrelated reasons.

## What

Both halves are the same mistake: the plan asserted things about test infrastructure it
had not read. The stub was named from its shape being right without checking its
accessibility; the catalog counts were missed because the plan reasoned about tests to
ADD and not about tests the change BREAKS.

## Approach

Stub: declare an equivalent private handler inside `RiskyUsersServiceTests`. Explicitly
do NOT promote the existing one to `internal` or hoist it into a shared helper -- that
edits a test file this module does not own, for a second consumer, and the duplication is
about fifteen lines. If a third consumer appears, extracting it is that change's job.

Counts: the update moves into S1, in the same commit as the descriptor, because the S1
commit must be green (ru-2's rule). The stale comment on `:16` is corrected with the
number, since a count comment disagreeing with its assertion misleads the next person to
add a module.

The plan also now records that these two assertions are a deliberate tripwire forcing any
module addition to be a conscious edit, and must not be weakened into a range or a `>=`.
Without that note, the obvious "fix" for a brittle count assertion is to delete its
precision.

## Files changed

- `docs/RiskyUsersModule-Plan.md` -- S1 notes (breaking assertions), S2 test-seam
  paragraph (local stub, and why not to share), S4 catalog-test section and heading

## Guard proof

Not applicable: plan document, plan revision. Both halves are mechanically checkable at
implementation time -- the compiler catches the stub, and `dotnet test` at the S1 commit
catches the counts.

## Coder dispute (if any)

None. Both halves verified by reading the cited lines before admitting:
`GraphTokenClientTests.cs:12` is `private sealed`, and `ModuleCatalogTests.cs:16` / `:109`
carry the literals 25 and 34.

## Known gaps

The alias delta is 2, not 1, because this module registers a granular permission
(`RiskyUsersRemediate`) alongside its main one. If D1 defers remediation and the granular
permission is dropped instead of kept, the alias count becomes 35 -- the implementer must
recount rather than copy 36 from this record.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier`
(grade: fallback -- frontier equals standard on this transport, owner-ruled 2026-08-03)

openreview over `d877294281f694ff3490af9cbedc5a2eb6ca68fa..a2c4c77ad41834b7899edb07e214088c51edd29e`,
verdict `acceptable_with_changes`, `capability_ok: true`, 2026-08-12.
