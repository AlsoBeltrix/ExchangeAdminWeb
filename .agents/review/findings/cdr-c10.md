# cdr-c10 - repeated directory rows manufacture address ambiguity

Status: Implemented and locally verified on master; independently unreviewed.
Push awaits separate authority.

## Evidence and approved scope

Verified before edits against clean master `f6f87a5`. Both `origin` and `github`
report `8853979`. `QueryBatchCandidates` pools every returned row; `Resolve`
scans all candidates for every address and counts rows, not distinct people.
One person with UPN and mail in different 500-address batches is returned twice,
so both addresses are rejected as ambiguous. The existing repeated-person test
provides only one row and does not reproduce that path.

Approved Revision 6 C10 requires immutable-identity deduplication before ambiguity
decisions and an address-to-distinct-identity index. Preserve genuine ambiguity,
input order and every original rejection, including repeated rejected addresses.
Coordinate the paired GUID/DN shape with `../cdr-c4-design.md`; fingerprinting,
member-write identity and exact-set confirmation remain C4 work.

## Declared files (before implementation)

- `Services/Comms10kAddressResolver.cs` - GUID-bearing candidates, paired resolved
  members, indexed distinct-identity assignment and invalid-identity refusal.
- `Services/Comms10kService.cs` - request/read objectGUID, reject unusable directory
  identities, pass paired resolved members, and expose the existing batch loop to
  harmless real-SDK tests without importing AD.
- `ExchangeAdminWeb.Tests/Comms10kAddressIdentityTests.cs` - cross-batch workflow,
  genuine ambiguity, identity pairing and invalid-identity regression guards.
- `ExchangeAdminWeb.Tests/Comms10kAddressResolverTests.cs` - give existing fixtures
  immutable identities; retain their existing behavioral assertions.
- `ExchangeAdminWeb.Tests/Comms10kRejectionReportTests.cs` - give existing fixtures
  immutable identities; retain rejection/duplicate semantics.
- `Modules/ModuleCatalog.cs` - Comms-10k patch version only.
- `docs/Comms10kDefenderRemediation-Plan.md` - C10 implementation row only.
- `.agents/state.md` - current remediation introduction only.
- `.agents/review/index.md` - C10 row only.
- `.agents/token-log.md` - one concise receipt.
- `.agents/review/findings/cdr-c10.md` - this record and review packet.

## Boundaries

No page or registry edit, shared/app version change, other finding, protection
change, fingerprint/write redesign, self-review, subagent/harness launch, branch
change, hook/trust adjustment, live AD/tenant call, deployment or push. The stale
module-spec version is already flagged in state. C1/C9's passing gates are the
baseline; the complete per-slice gate follows final code and mutation restoration.

## Implementation

`Candidate` now requires an ObjectGuid. `ReadAddressCandidates` is the existing
batch loop extracted after runspace/import/credential setup; it requests
objectGUID and throws for missing, malformed or empty values before any partial
candidate set can return. No extra directory query, server or result cap is added.

The resolver builds one case-insensitive address index. Each address maps to its
distinct GUIDs; a row whose UPN equals its mail, or whose identity is returned by
two queries, counts once. Every returned address key is indexed, including keys
that changed between queries. The first row for an address/GUID supplies its DN.
Resolution still walks the original inputs, preserving exact rejected text and
one rejection per rejected input. Resolved output deduplicates by GUID, so a moved
person does not become a second member. Empty candidate GUIDs are refused even
when the pure resolver is invoked without the directory parser.

`ResolvedMember(ObjectGuid, DistinguishedName)` is carried as the sole paired
member value through `Outcome.ResolvedMembers` and
`Comms10kResolveResult.ResolvedMembers`. Both existing `ResolvedDns` getters derive
a fresh DN list from that value. C3 can continue using the derived count/list;
C4 must use the paired members for confirmation and member-write identities.
The job/page and actual DN-addressed member write are unchanged in this slice.
Comms-10k is `1.4.7`; base app version remains `2.27.1`.

## Regression and compiled mutation proof

The 146 Comms-10k cases passed after a successful Release compile, including nine
new identity cases. The cross-batch guard uses the real SDK batch helper and
harmless local `Get-ADObject` stand-ins: 501 addresses issue two queries, one GUID
appears in both results, and all 500 people resolve with no rejections. The harness
also requires objectGUID in the actual requested properties. No AD module or
directory is loaded/contacted.

| Compiled mutation | Assertion failures | What it proves |
|---|---|---|
| Restore the pre-fix per-address candidate scan, row-count ambiguity and DN-based output deduplication, adapted only to compile with the paired member type | 6 failed / 3 passed | Two real batches; all returned address keys and moved-DN GUID deduplication; one candidate pass; empty-GUID refusal; both repeated-row/input-semantics theory cases |
| Bypass only the query's unusable-GUID throw while still parsing the value | 3 failed / 6 passed | Missing, malformed and empty GUID rows cannot return a partial candidate set |
| Replace the index's GUID key with Guid.Empty | 2 failed / 0 passed, targeted | Distinct people remain ambiguous, including different GUIDs sharing a DN |

Every probe used `dotnet test ExchangeAdminWeb.slnx -c Release` with a focused
filter and `xUnit.MaxParallelThreads=4`; none used `--no-build`. All compiled and
ran tests, exited 1 for assertion failures, and had zero `error CS`/build errors.
Each altered source was restored from its fixed byte copy in a `finally`, hash
checked and touched before the next probe/build:

| Source | Fixed/restored MD5 | Fixed/restored SHA256 | mtime restoration UTC |
|---|---|---|---|
| `Services/Comms10kAddressResolver.cs` | `B8979B1AC56492343B2FD7C28C7572A1` | `4137AA5A46EC6D8E25669851687EB1273CDED3BC241543D7EF50633AB4E330A8` | `15:29:48` -> `15:31:29`, then `15:31:29` -> `15:32:50` |
| `Services/Comms10kService.cs` | `57A87CE350B0D0698361CB6AD0ADDCE8` | `6C50D20DCE0F121D75E0AB789A9F34BB68D7AE1D0E1803A49C8351B1D7F72C90` | `15:29:48` -> `15:32:17` |

Machine-local logs are `.agents/review/out/cdr-c10-targeted.log` and
`cdr-c10-mutation-{resolver,query,distinct}.log`. This record owns the durable
results; those ignored files are optional supporting artifacts.

## Final gate

All checks ran after the final semantic code and byte-identical mutation restores:

- `dotnet build ExchangeAdminWeb.slnx -c Release`: exit 0, 39 warnings / 0 errors.
- `dotnet test ExchangeAdminWeb.slnx -c Release -- xUnit.MaxParallelThreads=4`:
  exit 0, 3,884 passed / 3 skipped / 0 failed, 4 m 59 s.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0.
  The tool reported workspace-loading warnings, with no formatting errors.
- `git diff --check HEAD`: exit 0.
- `tools/Test-AsciiOnly.ps1`: exit 0; new test source was staged first.

Final build/test/format logs use the same machine-local `cdr-c10-` prefix.

## Review Packet

### Mode

IMPLEMENTATION_REVIEW / REVIEW_ONLY. Independent Astra review is pending.

### Original Request

Owner approved all twenty findings in Revision 6: "approved. go." Implement C10
as one sequential Sol 6.1 slice, then independently review the complete affected
workflow with Astra at max before proceeding to the next finding.

### Approved Plan

`docs/Comms10kDefenderRemediation-Plan.md`, approved Revision 6, C10.

### Commit Range

`f6f87a5edf3d76e3740ffe424d28dc5d6fa1d5a0..HEAD` (the C10 commit containing this record).

### Claimed Changes

- Address assignment indexes distinct GUIDs instead of scanning/counting rows.
- Query identity parsing refuses unusable GUIDs; resolved GUID/DN values stay paired.
- Regression coverage includes the actual two-batch query path and per-input reporting.
- Comms-10k patch bump and slice records only.

### Constraints / Out Of Scope

No live AD/tenant call, push, deployment, self-review, shared behavior, page/registry
change, protection check, or other remediation finding. Do not implement during
review. C3/C4 own refusal/fingerprinting/member-write identity changes.

### Design Decisions

GUID, not DN or address, defines one person. Every returned key is retained;
deduplicating the whole pool before indexing would discard keys changed between
batches. The first match per address/GUID supplies metadata, and original input
order selects the member's first resolved metadata. Derived DN lists preserve the
current callers while avoiding separate mutable GUID and DN lists.

### Deferred Work

C3 partial-resolution refusal and its audit; C4 approved-set fingerprint and
immutable member-write/read-back identity; the remaining plan findings. The current
write still uses member DNs. Module-spec version drift remains recorded in state.

### Review Focus

Read the complete flow from query construction/parsing through address assignment,
`ResolveEmailsAsync`, page validation/count/rejection consumption and background
re-resolution into the existing replace path. Judge C10 scope and the paired-member
contract, cross-batch keys, genuine ambiguity, order and duplicate/error semantics.
Do not infer correctness from earlier slices' review outcomes or just the diff.

### Verification Run

See regression/mutation proof and final gate above. No page render, real directory
query or live membership replace was run. No PowerShell source changed, so
PSScriptAnalyzer/Pester are not required for this slice.
