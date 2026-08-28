# pgwt-4: An ambiguous typed target name silently protects an arbitrary group

**Severity**: MEDIUM - the intended privileged group stays writable while a same-named
other group appears protected.
**Status**: Verified (owner-run codex verification round, 2026-08-28; accepted, guard confirmed)
**Branch**: `-` (default-branch mode)
**Commit**: `e332c45`

## Evidence

`ADDirectorySearchService.ExecuteValidateExists` requests `ResultSetSize 2` but returns
`FirstOrDefault` with no ambiguity signal - documented as correct for existence checks
("several matches still answer yes"). The GroupTarget flow then persists that arbitrary
match's `objectGUID|DN` (`ProtectedPrincipalEntryValidator.CanonicalValue`). Typed (non-DN)
input on the admin page reaches this path; picker selections return a DN and match exactly
one.

## Predicted observable failure

An admin types a CN shared by two groups; whichever AD returns first is stored. The
intended group remains an unprotected write target.

## Approach

`DirectoryValidationResult` gains `Ambiguous` (default false), set when the existence
lookup returned more than one object. `Decide` refuses a Found+Ambiguous match for the
`GroupTarget` kind only ("matches more than one group - pick it from the suggestions"),
preserving the older lists' existence-only semantics exactly as the reviewer proposed.

## Files changed

- `Services/ADDirectorySearchService.cs` - ambiguity carried on the result
- `Services/ProtectedPrincipalEntryValidator.cs` - GroupTarget refusal
- `ExchangeAdminWeb.Tests/ProtectedGroupWriteTargetTests.cs` - guard

## Guard proof

- `ProtectedGroupWriteTargetTests::Validator_GroupTarget_RefusesAnAmbiguousMatch` - revert
  the fix, it fails; restore, it passes.

## Coder dispute (if any)

None.

## Known gaps

None.

## Reviewer comments

`Reviewer: codex-commercial / gpt-5.6-sol / xhigh / standard` (owner standing dispatch),
generation pass over `8700531..5336072`, verdict `findings` (7), capability_ok true.
**Verification round (OWNER-RUN, 2026-08-28): verdict `accepted`, `guard_confirmed: true`.**
Owner-run interactive codex (default gpt-5.6-sol) - `.agents/review/manual-verify.*`. Full
suite 1833/0/3. Comments: validation reports multi-match lookups
(`ADDirectorySearchService.cs:404`); GroupTarget refuses ambiguous typed input while other
kinds keep existence-only semantics (`ProtectedPrincipalEntryValidator.cs:95`); no adjacent
regression found.
