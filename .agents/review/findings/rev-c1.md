# rev-c1: Verify and enforcement query the global catalog without an empty SearchBase, so an existing group in another tree reads as Missing and an Ignore then bypasses it

**Severity**: HIGH - fail-open through Ignore under the same root as rev-a3 and rev-b1. The Verify adapter and the
enforcement `FindGroups` query the GC with `-Server <gc>` and no `SearchBase`, so a protected domain-local group in a
separate tree domain is not returned. Verify reports it Missing; an operator Ignores it; enforcement's lookup returns zero
for the same reason, and because the entry is an ignored SID row with a confirmed-zero result, it is skipped. A member whose
token does not carry that domain-local SID then passes. Before the ignore list, the zero result failed closed.
**Status**: Admitted; awaiting fix on branch
**Lines** (master `14a94b50`): `Services/PowerShellProtectedGroupDirectory.cs:97-102`, `:244-249`; `Services/ProtectedGroupMembership.cs:360-366`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review C, range `82a67e2f..fe3b86b8`, dispatched 2026-11-06 from master `14a94b50`; report `.agents/review/rev-c.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

Explicit empty `SearchBase` on every GC query in Verify and enforcement (fix with rev-a3 and rev-b1 as one change to the
directory layer). `FakeForest` and the PowerShell stand-in must require the empty base so the restored-group and
other-domain tests bite.
