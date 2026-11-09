# rev-a3: the global-catalog ancestry lookup has no empty SearchBase, so objects in another forest tree are not found

**Severity**: MEDIUM - fail-closed refusal of permissible writes in a multi-tree forest. `PowerShellProtectedGroupDirectory`
queries the GC with `Get-ADObject -LDAPFilter ... -Server <gc>` and no `SearchBase`; the default base is the GC's own
domain partition, so a target or group in a separate tree is absent from the answer and
`ProtectedGroupMembership` (`:588-590`) throws on the missing entry. Microsoft documents that searching all GC partitions
requires an explicitly empty SearchBase.
**Status**: Merged 78c03e49; closed by codex review E 2026-11-09 (CLOSED, `.agents/review/rev-e.result.txt`)
**Branch**: fix/rev-abc
**Commit**: eef954d1
**Lines** (master `145f513f`): `Services/PowerShellProtectedGroupDirectory.cs:132-141`; `Services/ProtectedGroupMembership.cs:588-590`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review A, range `8557a15a..5c4c2970`, dispatched 2026-11-06 from master `145f513f`; report `.agents/review/rev-a.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

Pass `SearchBase = ""` on the GC branch (and on any other forest-wide GC query in the same file). The stand-in forest in
tests treats a GC endpoint as forest-wide regardless of the base; make the stand-in require the empty base so the test
bites (`AForestLookup_GoesToTheGlobalCatalog_NeverTheLocalDomain` currently checks endpoint and filter only).

## As built (fix/rev-abc `eef954d1`, one commit with rev-b1 and rev-c1)

Verified real against the code: every forest-wide read passed `-Server <gc>:3268` with no `-SearchBase`, and the AD cmdlets
default the base to the server's default naming context. Note: `ADDirectorySearchService`'s remarks record a live
measurement on this forest (18 ANALOG + 7 WINROOT for one term) from a catalog search without a base, so the defect may not
show on ADI's forest; under repo-guidance Invariant 7 the safety argument cannot rest on that, and Microsoft documents the
empty base as the all-partitions search. Not verified live.

- `PowerShellProtectedGroupDirectory.RunAt(command, server, ...)`: `-Server <server>`, or when null `-Server <gc>` and
  `-SearchBase ''`. Used by `FindGroups`, `ReadMemberOf` (the ancestry read), `FindObjectDnsBySid`,
  `FindObjectsBySidAtCatalog` (the SID binder), `FindVerifyObjects` and `FindUsers`. Domain-qualified reads are unchanged.
- `InvokingUserIdentifierDirectory.RunAt`, the same, for the conversion verb's `FindGroups` and `FindUsers` (rev-b1).
- Departure from the fix shape: `SelfGrantDirectory.Search` (the self-grant check's three catalog searches) gets the empty
  base too - the same root cause, and it is fail-open there (a grantee in another tree reads as no row). Its
  `GlobalCatalog(ps)` call does not match the brief's grep.
- `FakeForest` is the `IProtectedGroupDirectory` seam (server null = forest); a search base does not exist at that level, so
  the requirement lives in the PowerShell stand-in: `SelfGrantDirectoryStandIn` now answers a catalog search with no base
  from the catalog host's own domain (contoso.test) only, and refuses an empty base sent to a domain. fabrikam.test is the
  second tree.

## Guard proof

| Test | Fails on the old code | Note |
| --- | --- | --- |
| `GlobalCatalogSearchBaseTests.AGroupInAnotherTree_IsFoundByEveryForestWideRead` | yes | all six forest-wide reads |
| `GlobalCatalogSearchBaseTests.TheConversion_RefusesANameThatIsAGroupInTwoTrees_AsAmbiguous` | yes | rev-b1 |
| `GlobalCatalogSearchBaseTests.TheConversion_FindsAGroupThatOnlyAnotherTreeHolds` | yes | rev-b1 |
| `GlobalCatalogSearchBaseTests.ADomainQualifiedRead_KeepsItsDomainsOwnBase` | passes on old code; fails with the empty base sent always (mutation `server is null \|\| true`) | guards over-application |
| 12 existing `PermissionValidatorSelfGrantTests` (other-domain cases) | yes, once the stand-in requires the base | self-grant departure |
| `ProtectedGroupMembershipTests.AForestLookup_GoesToTheGlobalCatalog_NeverTheLocalDomain`, `EveryRead_CarriesItsCredential_AndNoFormWithoutOneRemains` | updated tripwires | |

Proof: `git diff -- Services` reverted with `git apply -R`, build, the tests above failed (15), patch re-applied, all pass.
