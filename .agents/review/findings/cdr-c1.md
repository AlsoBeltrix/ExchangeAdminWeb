# cdr-c1 - the broadcast target can be reconfigured or retargeted by its DN

Status: Implemented and locally verified on master; independent Astra workflow review pending.
Push awaits separate authority.

## Evidence and approved scope

Verified against clean master `75b2d98` before edits. Both remotes report `8853979`.
`Comms10kService.TargetGroup` reads `Comms10k/TargetGroupName`; preview and initial
replace lookup use that name. Clear, add and read-back address `target.DistinguishedName`
even after the pre-write GUID re-read. A GUID lookup alone therefore leaves the writes
bound to a mutable name/path. Existing re-read and membership queries also accept the
first result without proving it is exactly the approved object.

Approved Revision 6 C1 and the 2026-10-15 owner decision pin this module to ObjectGUID
`87663f48-10a7-44b3-8da3-0a7e4cc3de0b`. The environment-neutrality waiver covers that
constant only. Remove the config/UI setting; retain obsolete database rows. Every group
lookup, mutation and read-back must use the pinned GUID and fail closed on an unresolved,
multiple or mismatched result. Member DNs remain member identities; C4 is separate.

## Declared files (before implementation)

- `Services/Comms10kService.cs` - pinned target, exact lookup validation and GUID wiring.
- `Components/Pages/Comms10k.razor` - fixed-target/configuration wording at the same line count.
- `Modules/ModuleCatalog.cs` - remove the target field; Comms-10k patch version only.
- `ExchangeAdminWeb.Tests/Comms10kTargetPinTests.cs` - actual SDK argument/refusal guards.
- `ExchangeAdminWeb.Tests/Comms10kPowerShellLifecycleTests.cs` - provide the pinned identity
  in the existing C9 stand-in's results; preserve lifecycle coverage.
- `ExchangeAdminWeb.Tests/Comms10kReplaceSequenceTests.cs` - correct the affected DN-binding
  explanation and retain its sequence guards.
- `docs/Comms10kDefenderRemediation-Plan.md` - C1 implementation row only.
- `.agents/state.md` - current introduction only.
- `.agents/review/index.md` - C1 row only.
- `.agents/token-log.md` - one concise appended receipt.
- `.agents/review/findings/cdr-c1.md` - this evidence, guard proof and review packet.

Page line numbers will be preserved. If implementation needs to move lines, follow
`.agents/playbooks/clickgate-reanchor.md` and extend the scope to the affected ClickGate
and Progress registry entries before changing them.

## Boundaries

Keep the C9 invocation cleanup, protection exemption and no-locking decisions. No other
finding, shared behavior, schema migration, live AD/tenant access, deployment, push,
self-review, subagent dispatch or external Codex launch is included. The module-spec
version drift is already recorded in state. The C9 verified gate is the baseline;
run the complete per-slice gate after final semantic edits and mutation restoration.

## Implementation

The service's target is the approved GUID. Configuration now requires only the module's AD
credential; no target-name config read or catalog field remains. The page identifies the fixed
Comms-10k target and points an unconfigured module at its AD credential setting. No config
database/schema code changed, and obsolete target rows are untouched.

Initial lookup and pre-write re-read share the GUID lookup. Preview, export and read-back
require exactly one result, its ObjectGUID equal to the approved constant, and its DN present.
Clear, each add and every group read carry the GUID as Identity. A different supplied target
GUID is rejected before command construction. The returned target DN is descriptive; member
DNs, primary-group union, C9 command cleanup, retry behavior, protection exemption and accepted
absence of locking remain unchanged. Comms-10k version `1.4.5` -> `1.4.6`; base app unchanged.

The real preview command body is a module-local static operation so local SDK tests can
exercise it without loading AD. The existing C9 fixture now returns the pinned ObjectGUID.
The page has 734 lines before and after; only lines 37 and 43 changed, so ClickGate and Progress
positions do not move and no registry edit/re-anchor is needed.

## Guard proof

The initial compiled Comms-10k run passed 137/137 tests, including all 31 new target-pin cases
and the 14 existing C9 lifecycle cases. Local functions named `Get-ADGroup`, `Set-ADGroup` and
`Get-ADObject` capture the actual service arguments; there is no AD module import or network
access. The DN supplied to write methods differs from the returned group's moved DN, and both
differ from the approved GUID. Any DN-addressed target command fails the argument assertions.

All new cases were mutation-proven after successful compilation of both projects. Each log
contains the app/test build outputs and zero `error CS` matches; no `--no-build` was used.

| Probe | Mutation | Assertion failures |
|---|---|---|
| M1 | Restore DN Identity arguments for lookup, preview, clear, add and read-back; restore the old target-name getter/configured expression | 5 `Assert.Equal` failures plus 1 `Assert.DoesNotContain` failure, 6/6 |
| M2 | Bypass exact-result and error validation | 21/21 `Assert.Throws` failures across lookup, preview and read-back: missing, multiple, wrong/missing/empty GUID, missing DN and reported error |
| M3 | Accept any supplied target GUID | 4/4 `Assert.Throws` failures for lookup, clear, add and read-back |

Each mutation exited 1 for assertion failures. M2's two missing-membership cases produced an
incorrect exception type after bypassing validation; the other 19 produced no exception.
The fixed service MD5 before and after every restore was
`EB8404D434E3CF7A8ED16300F87EF08C`. Mutated MD5s were M1
`8405E48D5D4EB9CAD4153915EB2120F2`, M2 `E427B568EDA897298C2CB721E97B3438`,
M3 `9B9453185D1BD3C5C33EE2CEAF5FF4D9`. Each restore wrote the original bytes, checked MD5
and advanced mtime. The final restored Release build therefore compiled the fixed service.
Machine-local logs: `.agents/review/out/cdr-c1-{targeted,mutation-wiring,mutation-lookup,
mutation-unpinned}.log`.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`: exit 0, 0 errors, 39 existing warnings.
- `dotnet test ExchangeAdminWeb.slnx -c Release -- xUnit.MaxParallelThreads=4`: exit 0,
  3,875 passed / 3 skipped / 0 failed, 3,878 total, 4m58s. One full-suite run after the final
  semantic edits and mutation restoration; normal compilation, zero `error CS` matches.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0;
  workspace-loading warnings only, no format repair required.
- `git diff --check HEAD` and `tools/Test-AsciiOnly.ps1`: exit 0. The new test file was
  staged before ASCII lint. Source edits are LF/ASCII.
- No PowerShell source changed, so PSScriptAnalyzer/Pester were not applicable.

No live-directory or browser acceptance ran. Automated local functions cannot prove the GUID
resolves in the real forest or that the page renders on dev. The plan's dev check (resolve the
fixed group and list members) remains pending separately from independent workflow review.

## Review packet

### Mode

IMPLEMENTATION_REVIEW

### Original request

Owner: "approved. go." to Revision 6's twenty findings and sequential fresh Sol 6.1 max
implementation with independent Astra max workflow review. This slice implements C1 only:
pin the target and every target-group operation to the approved ObjectGUID, remove the target
setting and refuse an unresolved or mismatched target.

### Approved plan

`docs/Comms10kDefenderRemediation-Plan.md`, approved Revision 6, C1.

### Commit range

`75b2d9848de28f30eb3a74124ffbb99846a497f1..HEAD` at this finding's implementation commit;
the coordinating parent supplies the resolved head.

### Claimed changes

- The target is fixed to the approved ObjectGUID; every target Identity uses that GUID.
- Group reads require exactly one matching object. A supplied non-pinned identity is rejected
  before constructing a command; lookup failure remains before the replace's point of no return.
- Target config/UI field removed, obsolete database rows retained, credential guidance updated.
- Actual SDK argument/refusal guards added; C9 fixture and DN-binding explanation updated.
- Comms-10k patch version `1.4.6`; no shared/app version change.

### Constraints / out of scope

No other finding, config migration, live directory/tenant access, deployment, push, protection
or locking change. The single-constant environment-neutrality waiver is explicit owner policy.

### Design decisions

Keep member DNs and the existing primary-group union. Target identity and the future C4 member
fingerprint solve different problems. Keep the resolved DN only as descriptive target metadata;
never use it to address a target command. Reuse the C9 lifecycle helper for every invocation.

### Deferred work

Independent Astra workflow review is pending. C10 is the next implementation slice after that
review; all later findings remain in the approved plan. The live dev group-pin acceptance was
not run and requires separate authority.

### Review focus

Review the complete affected operator/job workflow, its callers and lifecycle against the
approved intent, including target identity from configuration through initial reads, re-read,
clear/add retries and read-back. Assess the actual SDK coverage and fail-closed paths. No coder
self-review was performed.

### Verification run

All required local gates and 31 new cases' mutation proofs passed as recorded above.
