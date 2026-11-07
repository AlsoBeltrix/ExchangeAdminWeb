# rev-b1: forest-wide catalog name lookups have no empty SearchBase, so an ambiguous name can convert into one domain's SID

**Severity**: HIGH - the one-time conversion and the SID binder query the global catalog without `SearchBase`, so the
search covers only the catalog's own domain partition. A module-admin or section-access name grant that exists as a group in
two tree domains is seen once, converted to that domain's SID and activated, where the verb's rule is to refuse ambiguity.
The same omission makes the SID binder miss legitimate targets outside that naming context (fail-closed refusals).
Shares its root with rev-a3.
**Status**: Fixed on branch; awaiting review
**Branch**: fix/rev-abc
**Commit**: eef954d1
**Lines** (master `145f513f`): `Services/InvokingUserIdentifierDirectory.cs:80-85`, `:106-111`; `Services/PowerShellProtectedGroupDirectory.cs:213-218`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review B, range `5c4c2970..82a67e2f`, dispatched 2026-11-06 from master `145f513f`; report `.agents/review/rev-b.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

Explicit empty `SearchBase` on every forest-wide catalog query; keep the domain base for domain-qualified lookups. The
stand-in forest must require the empty base so the existing ambiguity and cross-domain tests bite. Fix together with rev-a3.

## As built

Fixed with rev-a3 and rev-c1 in one commit (`eef954d1`); see `rev-a3.md` for the change, the stand-in and the guard-proof
table. For this finding: `InvokingUserIdentifierDirectory.FindGroups` and `FindUsers` send `-SearchBase ''` at the catalog,
and `PowerShellProtectedGroupDirectory.FindObjectsBySidAtCatalog` (the SID binder) the same. Tests:
`GlobalCatalogSearchBaseTests.TheConversion_RefusesANameThatIsAGroupInTwoTrees_AsAmbiguous` (a name that is a group in
contoso.test and in fabrikam.test is left Ambiguous, never converted) and `TheConversion_FindsAGroupThatOnlyAnotherTreeHolds`;
both fail on the old code.
