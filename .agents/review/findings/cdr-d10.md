# cdr-d10 - measure page completeness before discarding malformed entries

Status: Implemented and locally verified on master, 2026-10-16; independent Astra
max workflow review pending.

Approved scope: `docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, D10.
Owner 2026-10-15: "approved. go." Implementation starts on clean master
`82db64bc91b20bdf776e3f216c57aa81df9be76d`; `git ls-remote` confirms origin and
github master both remain `885397981d7550afebf6518ef21b4fc54b062859`.

## Recovery point - 2026-10-16

Owner: "system restarted. please recover and continue." The original D10 agent
did not survive. A fresh Sol 6.1 max implementer is the sole writer and retains
the approved scope and saved implementation. HEAD is still the implementation
base above; both remote master refs were live-verified unchanged on recovery.

The four code/test changes and this untracked finding survived. Production SHA256
`4685053718b04cd764033afa8397fbd7a45fda0aed9c35aa1d68022e44565016`
matches `out/cdr-d10/fixed-service.bin`; test source SHA256
`d210c0df80f62b3f634b7a7355c770251be3a892c74065fb6503ca411f3792d1`
matches the saved `proof.json`. The original `baseline-fixed.log`/TRX records
seven compiled passing cases. No mutation receipt or log exists from before the
restart, and none is counted. The saved `probe.py` has a finally restoration
and checks exact bytes, MD5/SHA256 and newer source mtimes. The recovery plan was
to resume it sequentially after a fresh focused check, retain the original receipt,
then run restored affected tests and one final sequential solution gate before
committing for independent review. Results below complete those recovery steps.
No pre-restart build/test process survived.

## Evidence and declared scope (before code changes)

`TryReadPage` ignores non-object JSON array entries. `FetchPartAsync` compares the
remaining `page.Count` with the requested `$top` when no continuation link exists.
A raw capped response containing malformed entries therefore looks short and can
report Complete before the remaining inventory is fetched.

Capture the raw array length before parsing and use it only for the full/short
completion boundary. Continue to count, deduplicate, filter, export and enforce
the device ceiling using usable objects. Preserve D8's numeric OS-build reader.

The actual fetch has no offset fallback: explicit next links are followed even
for empty usable lists; a full cursor-free response asks the caller to divide the
Last seen range. A full raw page with no usable split timestamp must refuse as
IncompletePaging, preserving existing zero-device refusal semantics. Short raw
pages without a link remain exhaustive, including genuinely empty arrays.

Use existing compiled HTTP/service fixtures for focused regressions, including a
malformed capped root that must partition and return later records, full pages
with zero/few usable rows, continuation and short-page controls. Mutation-prove
every new case through compiled production regressions with standard assertions;
verify exact bytes, MD5/SHA256 and newer restored mtimes before the final gates.

Defender changes 1.6.7 -> 1.6.8 with its existing pinned catalog expectation.
Base remains 2.27.4 and Comms remains 1.4.18: no shared behavior changes. The
known module-spec Version 2.0 / June 25 drift remains flagged.

No D9 export, D4 audit, D2 failure-reason, D7 budget/cancellation, D1/D3/D5,
continuation-host check or deferred psl-2/3/4/cdr-followup-1 work. No page,
authorization, credential, protection, audit, notification, locking, dependency,
live AD/tenant/vault, self-review, subagent, push, deployment, branch/history,
hook or trust changes. D6-local permits correcting stale comments in touched code.
No page edit is expected, so ClickGate re-anchoring is unnecessary.

## Retained implementation and compiled regressions

The recovered implementation is unchanged. `TryReadPage` returns the value array's
raw `GetArrayLength()` separately from the usable object list. `FetchPartAsync`
uses that cardinality only to determine whether a cursor-free response is capped.
Its existing caller then partitions a capped range, or refuses IncompletePaging
with no result rows when no usable timestamp can divide it. Explicit next links
still take precedence, and their own `$top` sets the continuation-page boundary.
All device accounting and ceiling checks still use parsed/deduplicated objects.

The existing HTTP fixture accepts a small page-size override; its inventory
handler can replace the first root entry with null without changing the raw cap.
The new cases execute the actual API/service path against local HTTP responses:

| Runtime cases | Required result |
|---|---|
| Malformed capped root | Partition, read null-lastSeen part once, return all 11 inventory records |
| Capped page, zero / one usable object (2 cases) | IncompletePaging, zero published rows, usable distinct count retained |
| Capped page with cursor, zero / one usable object (2 cases) | Follow the exact link and include the later record |
| Capped continuation with its own smaller top | Use its top and refuse an unsplittable range |
| Raw short page with five non-objects and one usable object | Complete at usable ceiling one; numeric build 22631 retained |

The short-page fixture includes null, Boolean, number, string and array entries.
Existing paging, dedupe, request-budget, filter, ceiling and D8 parsing tests
remain in the restored affected set. Two touched test-header comments now describe
the current local HTTP fixtures and partition/refusal behavior. Defender's catalog
version and its existing pinned test advance to 1.6.8; base remains 2.27.4.

## Compiled mutation proof - completed 2026-10-16

Both the retained pre-restart baseline and the separately named recovery baseline
passed exactly the seven new runtime cases. Recovery touched the unchanged source
and test files to force fresh compilation; the original baseline/proof metadata
was not overwritten. The installed Windows `py` launcher was used after the bare
`python` command resolved to an unavailable Store alias; no test began in that
failed launcher attempt.

Every resumed probe runs `dotnet test ExchangeAdminWeb.slnx -c Release` with the
Malformed-method filter, four xUnit threads and its own new TRX/log. No --no-build
is used. Compiler errors are rejected explicitly. Receipts require exactly the
seven expected cases, exactly the expected failing subset, standard Assert.*
failure messages, and every other case passing.

| Compiled production regression | Assertion failures | Passing controls |
|---|---:|---:|
| Restore original parsed-count completion boundary | 4 | 3 |
| Prematurely terminate a cursor page with few usable rows | 3 | 4 |
| Treat raw top-minus-one as capped | 1 | 6 |
| Ignore the continuation link's own top | 1 | 6 |

The exact failed-name union is all seven baseline cases, with nine failures:

| Case suffix under DefenderEndpointDeviceServiceTests | Failing probes |
|---|---|
| MalformedCappedRootPage_PartitionsAndReturnsTheCompleteInventory | original parsed count |
| MalformedCappedPageWithoutSplitPoint_RefusesInsteadOfCompleting(usableRows: 0) | original parsed count |
| MalformedCappedPageWithoutSplitPoint_RefusesInsteadOfCompleting(usableRows: 1) | original parsed count |
| MalformedCappedPageWithCursor_FollowsItEvenWithFewUsableRows(usableRows: 0) | premature cursor stop |
| MalformedCappedPageWithCursor_FollowsItEvenWithFewUsableRows(usableRows: 1) | premature cursor stop |
| MalformedCappedContinuation_UsesItsOwnTopAndRefusesWhenUnsplittable | original parsed count; premature cursor stop; ignored continuation top |
| MalformedShortPage_CompletesWithOnlyUsableRows | top-minus-one boundary |

Each probe's finally block restores exact fixed bytes, verifies MD5 and SHA256,
and advances the source mtime beyond the mutation. The newly compiled app DLL is
newer than the mutated source and differs from the baseline; its hashes equal the
runtime app copy. Current test-assembly/PDB hashes and assembly/source mtimes are
also recorded. Test source matches the original proof snapshot throughout.

| Source | MD5 | SHA256 |
|---|---|---|
| DefenderEndpointDeviceService.cs | `35bec1e567b4c2cf7e9e98836572339f` | `4685053718b04cd764033afa8397fbd7a45fda0aed9c35aa1d68022e44565016` |
| DefenderEndpointDeviceServiceTests.cs | `587f5466cdc28b550e4fc32d276eda14` | `d210c0df80f62b3f634b7a7355c770251be3a892c74065fb6503ca411f3792d1` |

Ignored machine-local evidence is under `.agents/review/out/cdr-d10/`: original
baseline-fixed.log/TRX and proof.json, separately named recovery-fixed.log/TRX and
recovery.json, four named probe logs/TRX/*-evidence.json receipts, fixed-service.bin,
probe.py and proof-summary.json. No interrupted or nonexistent pre-restart probe
is counted. This finding is the durable record; ignored artifacts are receipts.

## Verification and limits

Restored Defender/AdSite/ModuleCatalog tests passed 299/299. The final Release
solution build passed with 40 existing warnings and zero errors; full solution format
verification passed before the one final full suite: 4,162 passed / 3 skipped /
0 failed. Final diff and staged ASCII guards passed with the new finding staged.
Documentation-only closeout is whitespace-checked again before the normal commit.

No self-review ran. Independent Astra max workflow review follows the verified
commit; D9 is the next approved implementation after that boundary. Browser,
live AD/tenant/vault, physical-location acceptance, push and deployment were not
exercised. The HTTP inventory and device values are synthetic fixtures. No shipped
PowerShell changed, so PSScriptAnalyzer and Pester were not run. The committed-
baseline token meter ran; its Claude-only scope excludes both native Sol sessions,
so no per-slice token usage is attributed.

# Review Packet

## Mode

IMPLEMENTATION_REVIEW

## Original Request

Owner 2026-10-15: "approved. go." for all twenty Revision 6 findings and sequential
fresh Sol 6.1 max implementation with independent Astra max workflow review. Owner
2026-10-16: "system restarted. please recover and continue." This slice recovers
and finishes D10 only: a malformed entry must not make a capped device response
look like an exhaustive short page. Preserve the saved work and prove every new
test against compiled production regressions before the complete local gate.

## Approved Plan

`docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, D10. D6-local permits
stale comments only in touched code. Approval and the scoped model arrangement
are owned by `.agents/decisions.md`. Constitution versioning requires the Defender
module bump to 1.6.8; shared base 2.27.4 and Comms 1.4.18 stay current.

## Commit Range

`82db64bc91b20bdf776e3f216c57aa81df9be76d..HEAD` (single D10 implementation commit).

## Claimed Changes

- Return raw JSON array cardinality separately from parsed device objects.
- Decide cursor-free capped/short completeness using raw cardinality, retaining
  the existing Last seen partition/refusal path and explicit-link handling.
- Add seven compiled service/HTTP runtime cases, all mutation-proven, while keeping
  usable-object counts, dedupe, ceiling and numeric-build parsing contracts.
- Advance Defender to 1.6.8 and its existing pinned catalog test; correct touched
  fixture comments and record recovery/proof/gate evidence.

## Constraints / Out Of Scope

D9 export errors, D4 audit snapshots, D2 shared reasons, D7 budgets/cancellation,
D1/D3 page behavior and D5 performance retain separate approved slices. The carried
continuation-host check, psl-2/3/4 and cdr-followup-1 are deferred. This slice adds
no authorization, credential, protected-principal, audit, notification, locking,
dependency, page/registry, shared-version, PowerShell, hook trust or config change.
Live access, push, deployment, branch/history changes and self-review are outside
this implementation. Module-spec Version 2.0 / June 25 drift remains flagged.

## Design Decisions

- Raw cardinality establishes whether the API response reached its requested cap;
  it never becomes the returned device count or the ceiling metric.
- An explicit next link is followed even when parsing yielded no usable objects;
  that link's top governs its own response boundary.
- A full cursor-free response already belongs to the Last seen partitioner. A
  response with no usable interior split point refuses IncompletePaging and
  publishes zero result rows. No offset fallback is introduced.
- A genuinely short raw response still completes, with only usable objects. The
  existing object parser, including D8's numeric build support, is retained.
- Recovery preserves exact saved production/test bytes, keeps the original 7/7
  receipt and adds separately named fresh compilation/provenance before probes.

## Deferred Work

Independent Astra max review, then D9 and the approved remaining Defender order.
Browser and live location behavior remain unproven; conditional host validation
and unapproved follow-ups retain their separate records and authority boundaries.

## Review Focus

First verify intent and scope. Review the affected HTTP value-array reader,
continuation/request-top handling, partition selection and result publication
workflow, including callers and lifecycle. Check the raw-versus-usable boundary,
zero/few usable objects, short-page and ceiling behavior, null-lastSeen inventory,
dedupe and unsplittable refusal semantics. Reconcile the exact-name mutation union,
restoration and compiled/runtime/test provenance with the actual TRX/log receipts.
Trace the complete workflow as needed; previous clean verdicts are not premises.

## Verification Run

- Retained pre-restart baseline: 7/7 compiled cases passed; source/test hashes match
  its saved proof metadata. No pre-restart mutation attempt is counted.
- Separate recovery baseline: 7/7 passed after touching unchanged sources for fresh
  compilation; runtime app and test-assembly/PDB provenance recorded.
- Four compiled regressions: 9 standard assertion failures, exact union all seven
  new cases; exact-byte/MD5/SHA256/newer-mtime restores and assembly checks passed.
- Restored Defender/AdSite/ModuleCatalog affected set: 299/299 passed.
- Final Release solution build: 40 existing warnings / 0 errors, exit 0; warning
  messages match the preceding D8 final build.
- Full solution format verification: exit 0, completed before the final full suite.
- Single final full Release solution suite: 4,162 passed / 3 skipped / 0 failed,
  with `-- xUnit.MaxParallelThreads=4`; compiled and runtime app SHA256 both
  `aaf1b9b6bbbee1d8d30034d4ac43d475dea3e6761ff1a60c9f48bef8dd71b125`.
- `git diff --check HEAD` and `tools/Test-AsciiOnly.ps1`: both passed, with the new
  finding and changed test source staged. Documentation-only closeout is checked
  again before the normal commit.
- `final-build.json`, `final-format.json`, `final-full-test.json`, `final-diff.json`
  and `final-ascii.json` record exit 0 and sequential timestamps for that exact
  order. `final-affected.json` records the restored 299-case run before the build.
  All source bytes/hashes remain equal to the retained proof snapshots; final
  compiled/runtime app copies match. No extra full-suite or self-review run occurred.
- No browser, live-integration, physical-location, push or deployment verification.
  PSScriptAnalyzer/Pester were not run because no shipped PowerShell changed.
