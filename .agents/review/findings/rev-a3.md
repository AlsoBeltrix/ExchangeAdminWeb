# rev-a3: the global-catalog ancestry lookup has no empty SearchBase, so objects in another forest tree are not found

**Severity**: MEDIUM - fail-closed refusal of permissible writes in a multi-tree forest. `PowerShellProtectedGroupDirectory`
queries the GC with `Get-ADObject -LDAPFilter ... -Server <gc>` and no `SearchBase`; the default base is the GC's own
domain partition, so a target or group in a separate tree is absent from the answer and
`ProtectedGroupMembership` (`:588-590`) throws on the missing entry. Microsoft documents that searching all GC partitions
requires an explicitly empty SearchBase.
**Status**: Admitted; awaiting fix on branch
**Lines** (master `145f513f`): `Services/PowerShellProtectedGroupDirectory.cs:132-141`; `Services/ProtectedGroupMembership.cs:588-590`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review A, range `8557a15a..5c4c2970`, dispatched 2026-11-06 from master `145f513f`; report `.agents/review/rev-a.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

Pass `SearchBase = ""` on the GC branch (and on any other forest-wide GC query in the same file). The stand-in forest in
tests treats a GC endpoint as forest-wide regardless of the base; make the stand-in require the empty base so the test
bites (`AForestLookup_GoesToTheGlobalCatalog_NeverTheLocalDomain` currently checks endpoint and filter only).
