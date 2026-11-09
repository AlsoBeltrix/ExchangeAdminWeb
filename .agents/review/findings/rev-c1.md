# rev-c1: Verify and enforcement query the global catalog without an empty SearchBase, so an existing group in another tree reads as Missing and an Ignore then bypasses it

**Severity**: HIGH - fail-open through Ignore under the same root as rev-a3 and rev-b1. The Verify adapter and the
enforcement `FindGroups` query the GC with `-Server <gc>` and no `SearchBase`, so a protected domain-local group in a
separate tree domain is not returned. Verify reports it Missing; an operator Ignores it; enforcement's lookup returns zero
for the same reason, and because the entry is an ignored SID row with a confirmed-zero result, it is skipped. A member whose
token does not carry that domain-local SID then passes. Before the ignore list, the zero result failed closed.
**Status**: Merged 78c03e49; closed by codex review E 2026-11-09 (CLOSED, `.agents/review/rev-e.result.txt`)
**Branch**: fix/rev-abc
**Commit**: eef954d1
**Lines** (master `14a94b50`): `Services/PowerShellProtectedGroupDirectory.cs:97-102`, `:244-249`; `Services/ProtectedGroupMembership.cs:360-366`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review C, range `82a67e2f..fe3b86b8`, dispatched 2026-11-06 from master `14a94b50`; report `.agents/review/rev-c.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

Explicit empty `SearchBase` on every GC query in Verify and enforcement (fix with rev-a3 and rev-b1 as one change to the
directory layer). `FakeForest` and the PowerShell stand-in must require the empty base so the restored-group and
other-domain tests bite.

## As built

Fixed with rev-a3 and rev-b1 in one commit (`eef954d1`); see `rev-a3.md`. For this finding: `FindVerifyObjects(filter,
null)` (Verify's reads by SID and GUID at the catalog) and `FindGroups(Sid, sid, null)` (enforcement's lookup of a
`group_sid` entry, `ProtectedGroupMembership.Candidates`) now search every partition, so a domain-local group in another tree
is found - Verify does not report it Missing, and enforcement evaluates it rather than reading the ignored entry's
confirmed zero. Test: `GlobalCatalogSearchBaseTests.AGroupInAnotherTree_IsFoundByEveryForestWideRead` (a domain-local group in
fabrikam.test by SID at the catalog, through both reads); fails on the old code. The restored-group and ignore tests
(`ProtectedGroupIgnoreTests`) run over `FakeForest`, whose seam has no base (see rev-a3).
