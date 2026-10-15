# cdr-c4 - bind replacement to the exact approved identities

Status: Implemented on master; compiled guard proof and restored final gate passed.
Independent Astra workflow review is owed; push remains pending under policy ask.
Approved scope: `docs/Comms10kDefenderRemediation-Plan.md`, Revision 6, C4.
Baseline: clean master `b936c604482447c36f93010b6fae135dd71d6df9`.
Origin master verified at `885397981d7550afebf6518ef21b4fc54b062859` on 2026-10-15.

## Evidence and declared files (before implementation)

The page enqueues live parsedEmails and uploadedFileName after awaits, with no member
fingerprint. The processor re-resolves and publishes any complete nonempty set without
comparing identities to validation. It then discards GUIDs and calls the writer with DNs.
Set-ADGroup -Add receives these mutable DNs. ReadBackMembers reads only DNs and Derive
expands expected membership with post-write primary members, permitting unapproved people.
These are verified source flows; no directory call was made.

- `Services/Comms10kMemberSet.cs`: canonical versioned SHA256 fingerprint and frozen
  confirmation value with copied addresses, filename and validated ticket.
- `Services/Jobs/Comms10kReplaceJobPayload.cs`: nullable persisted approved fingerprint.
- `Services/Jobs/Comms10kReplaceProcessor.cs`: semantic fingerprint refusal through the
  C3 audit/result path and paired executable members through immutable dispatch.
- `Components/Pages/Comms10k.razor`: validation snapshot and submission capture before await.
- `Services/Comms10kService.cs`: GUID-bound adds, pre-clear primary identity preflight,
  strict batched identity-aware read-back in the existing runspace.
- `Services/Comms10kReplaceWriter.cs`: GUID sets for write batches and outcome comparison;
  never augment the expected set from an observation.
- `ExchangeAdminWeb.Tests/Comms10kApprovedSetTests.cs`: complete-chain behavioral guards.
- Existing `Comms10kRefusalTests.cs`, `Comms10kReplaceWriterTests.cs`,
  `Comms10kReplaceSequenceTests.cs`, `Comms10kPowerShellLifecycleTests.cs` and
  `Comms10kTargetPinTests.cs`: adapt fixtures to GUID identities and revised primary semantics;
  share the existing local audit/credential/page fixture with the new guards.
- `Modules/ModuleCatalog.cs`: Comms-10k patch bump only.
- `docs/Comms10kDefenderRemediation-Plan.md`, `.agents/state.md`,
  `.agents/review/index.md`, `.agents/token-log.md`, this finding: scoped records and packet.
- ClickGateRegistry / ProgressRegistry only if re-anchoring proves necessary; the playbook
  is read and Comms has no line-keyed ClickGate entries.

## Boundaries and validation limits

No C2 authorization snapshot, C11 count redesign, C5 job visibility, C7 broad reentry/parser
work, C6 ticket UI change, protection checks, locking or rollback. Preserve C1 target GUID,
C9 command cleanup, C10 paired members, 2,000-write batches and one write runspace.
No self-review, other agents/CLI harnesses, branch or hook changes, bypass, push, deployment,
live AD or tenant access. Module-spec version drift remains flagged in state.

GUID-only alternative DN values in the existing Set-ADGroup -Add path follow
[MS-ADTS alternative forms](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adts/92a54869-38d7-4e71-a3be-5f67a0dcdd7e),
[object-reference behavior](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adts/538ec135-1465-4266-bb0a-777924f02846),
and [Set-ADGroup -Add](https://learn.microsoft.com/en-us/powershell/module/activedirectory/set-adgroup?view=windowsserver2025-ps).
The protocol documentation supports the design; installed-cmdlet end-to-end interoperability
is UNVERIFIED. No DN fallback or live probe is authorized.

## Implementation

The canonical fingerprint is `v1:` plus lowercase SHA256 over UTF-8 text: distinct nonempty
GUIDs in lowercase N format, ordinal sort, newline join. Empty sets/identities refuse.
Validation snapshots addresses, filename and trimmed validated ticket before awaiting;
it publishes a copied confirmation only if the upload has not changed during lookup.
Submission captures that approval and its complete-file eligibility before the first await,
and serializes all four matching fields from that captured value.

The nullable payload field is deliberately not JSON-required. Older, malformed and unsupported
fingerprints reach CountRows successfully and then refuse semantically through C3's one-audit,
concrete-result path. Runtime resolution remains mandatory. Only an exact GUID-set fingerprint
match publishes paired executable members; DN renames, ordering and duplicates are immaterial.

The processor keeps pairs through dispatch. The service captures distinct GUIDs before its
credential await, preflights primary membership in the existing write runspace before clear,
and refuses unreadable identities or any primary GUID absent from approval. A primary subset
is allowed. Actual 2,000-member Set-ADGroup -Add batches use GUID-only alternative DNs.
The bulk shape, C1 group GUID, C9 command cleanup and retry semantics remain in use.

Read-back reads the uncapped member attribute and maps every observed DN to GUID using
500-DN Get-ADObject batches in that same runspace. Missing, unexpected, ambiguous or invalid
mapping rows and directory errors fail the observation, producing CouldNotConfirm. Primary
identities are also strict. Outcome comparison uses GUID sets and never augments expected
membership with observed primary members. A concurrent extra primary remains PartlyApplied.
The legacy SucceededWithExceptions enum remains readable for existing results; new replaces
cannot use that exception to approve a different set.

Comms-10k is `1.4.9`; the base app stays `2.27.1`. No ClickGate line entry or ProgressRegistry
adjustment was needed. The focused Comms/ClickGate/progress registry run passed 552 cases
before the final defensive-copy test was added; the final full gate includes that test.

The first focused build hit MSB3027/MSB3021 because stale repo testhost 6240 and test process
8984 held the Release DLL. Their command lines and creation times were inspected; both exited
before the recovery command, so no process was stopped. Initial harness compilation then
exposed a missing progress namespace and init-only/target-typed-new issues, which were fixed.
No compiler-error failure is counted as a mutation proof. No PowerShell source changed.
The first format verification identified xUnit1051 on two new WaitAsync calls; both now pass
TestContext.Current.CancellationToken. The active suite first finished green (3,940 passed /
3 skipped); those three page cases then repeated their intended compiled failures with the
corrected harness before the final restored gate. No analyzer suppression was added.

## Review packet

### Mode

IMPLEMENTATION_REVIEW / REVIEW_ONLY. Independent Astra review is pending;
no self-review was performed.

### Original Request

Owner approved all twenty findings in Revision 6 and sequential fresh Sol 6.1 implementation
at max followed by independent Astra review at max. This slice implements C4 only.

### Approved Plan

`docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, C4, with
`../cdr-c4-design.md` and C2's command-boundary coordination note.

### Commit Range

`b936c604482447c36f93010b6fae135dd71d6df9..HEAD` for this finding's implementation commit.

### Claimed Changes

- Exact approved GUID-set fingerprints survive validation, submission, persistence and runtime
  re-resolution. Frozen inputs prevent pairing one fingerprint with another uploaded list.
- Missing, malformed, unsupported or changed approvals refuse through the existing one-audit
  result path with a specific completion reason.
- Actual adds use immutable GUID-only alternative DN values in the existing batch path.
- Primary preflight refuses extra/unreadable identities; strict batched GUID observation
  decides the outcome without expanding approval after the write.
- Comms-10k patch bump and slice records only.

### Constraints / Out Of Scope

The boundaries above apply. No live directory or installed-cmdlet interoperability claim,
browser rendering claim, deployment, push, hook change, shared behavior or base version bump.
No implementation during review; judge the whole affected workflow, including callers.

### Design Decisions

Member identity is ObjectGUID; DN is descriptive metadata. A versioned hash identifies the
approved set while avoiding persistence of a second large member list. Its input is one
canonical set representation. The nullable persisted field is semantic refusal input, not
a JSON-required field that could bypass the mutation audit during CountRows.

Primary members are a preflight constraint. No primaryGroupID changes or rollback are added.
Post-clear observation is non-atomic; concurrent replaces or AD changes may still mismatch.
The legacy outcome enum remains to read old records, but cannot legitimize unapproved extras.

GUID-only alternative DN modify values retain the measured bulk command shape. Official
protocol/cmdlet documentation supports this choice; installed-cmdlet interoperability has
not been observed. Failure does not trigger a DN fallback or a live test.

### Deferred Work

C11 write-count truth, C5 submitter job visibility, C2 current authorization snapshots and
command-dispatch checks, C7 broad reentry/atomic parsing, C8 downloads, C6 ticket UI and all
Defender findings remain separate slices. C4 captures its own approval before awaits;
C7 must preserve that binding. No locking, protection check or primary-group mutation.

### Review Focus

Read validate -> frozen confirmation -> queued payload -> runtime resolve -> writer -> strict
observation -> audit/completion. Check same-size changed GUIDs, renamed DNs, order/duplicates,
older jobs, submission awaits, actual SDK parameters, pre-clear primary refusal, incomplete
identity mapping, concurrent primary extras and one concrete failure audit/result. Trace the
actual command boundary and C9 cleanup; C2 authorization remains pending by design.

### Verification Run

Compiled mutation and final gate receipts are recorded below. No browser render, directory
query, tenant call, installed AD cmdlet test or deployment. PSScriptAnalyzer/Pester are not
required because no PowerShell source changed.

## Compiled guard proof

All 33 new C4 cases failed an intended assertion against compiled production mutations.
Fourteen groups produced 43 assertion failures, covering those 33 distinct cases and the
existing primary-extra case whose expectation changed. Each probe ran Release solution
tests with `--no-restore`, a focused filter and `xUnit.MaxParallelThreads=4`, without
`--no-build`. TRX inspection required exit 1, the exact expected failure count and an
Assert.* failure message. Build/compiler errors were rejected as evidence.

| Probe | Compiled regression | Intended failures |
|---|---|---|
| M1 | hash keeps duplicates, reverses ordering and permits empty identities | 3 |
| M2 | omit semantic fingerprint-format gate | 5 |
| M3 | invent runtime approval from persisted fingerprint | 3 |
| M4 | refuse an identical GUID set | 3 |
| M5 | omit primary-identity preflight | 5 |
| M6 | supply ordinary DNs to actual member adds | 3 |
| M7 | replace strict observation with a guessed matching set | 5 |
| M8 | compare observed count instead of GUID sets | 1 |
| M9 | add observed primary identities to expected membership | 2 |
| M10 | truncate DN-to-GUID observation after 500 members | 1 |
| M11 | retain a mutable address list in confirmation | 1 |
| M12 | reread confirmation after submission's authorization await | 1 |
| M13 | publish validation after its upload was replaced or mutated | 2 |
| M14 | omit mutation audit from recorded refusals | 8 |

Every probe restored saved fixed bytes in a finally, checked MD5 and SHA256, and advanced
mtime beyond the mutation. The final restored sources matched every corresponding receipt:

| Source | MD5 | SHA256 |
|---|---|---|
| Comms10kMemberSet.cs | `89469CDB3B079A9C498BD8DE4D219231` | `9DAF69BE1A1BB60EC5ACB660AC687D812F53857B91710AD841300CBEBFB0D0F9` |
| Comms10kReplaceProcessor.cs | `E862BEFBF9FF913B55F3C462E5A2BF9A` | `EBAC4A00C701BD0D70EE5DDEB65D43CCAD54ED7866B3AF59587CE185F30251FF` |
| Comms10kService.cs | `C2553671BAF820D4A8E0B3321FC83572` | `87E98AB87456C955DFB761C7ADDACE75A60440130450C5473A207A486B190607` |
| Comms10kReplaceWriter.cs | `08BBDFD63B3A0D35AC0A46AC0A660365` | `493881FE43A8FB1C08D31B4BA05A86DED05C5E35F3867B2877F1B6F2BEC564E1` |
| Comms10k.razor | `DDA72B228B1DC982F277C9FCBC2429C9` | `04D552B1C3C425B3349E5A42AA220285C9FEC895FF3EF88BB98A67DC345A9025` |

Machine-local ignored evidence under `.agents/review/out/` includes `cdr-c4-focused.log`,
its TRX, `cdr-c4-M*.log` and per-probe TRX directories, fixed-byte backups, the mutation
script and `cdr-c4-mutation-receipts.json` with every assertion and mtime receipt. This
finding owns the durable results; ignored logs are supplemental.

## Restored final gate

- `dotnet build ExchangeAdminWeb.slnx -c Release`: exit 0, 41 warnings / 0 errors.
  Two warnings are the local PowerShell fixture's dynamically assigned counters; its
  behavioral assertions establish the actual counts. The other warnings predate C4.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0,
  workspace-loading warning only after correcting the two test cancellation calls.
- `dotnet test ExchangeAdminWeb.slnx -c Release -- xUnit.MaxParallelThreads=4`:
  exit 0, 3,940 passed / 3 skipped / 0 failed (4 m 56 s) on the final corrected harness.
- `git diff --check HEAD`: exit 0 after the final bookkeeping edits.
- `tools/Test-AsciiOnly.ps1`: exit 0 with the new production and test sources staged.

Gate logs are `cdr-c4-build.log`, `cdr-c4-test.log`, `cdr-c4-format.log`,
`cdr-c4-diff.log` and `cdr-c4-ascii.log` under the same ignored evidence directory.

The existing global pre-commit wrapper only chains an executable local pre-commit hook,
which is absent. No hook/trust/configuration change or bypass was performed. The finding
uses a normal configured git commit. No PowerShell source changed; PSScriptAnalyzer and
Pester were not required or run.
