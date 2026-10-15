# cdr-c7 - shared busy ownership and atomic CSV publication

Status: Implemented and verified locally; independent Astra max workflow review pending.
Push remains pending under policy ask.
Approved scope: `docs/Comms10kDefenderRemediation-Plan.md`, Revision 6, C7.
Baseline: clean master `097cfd1e74d4622c016d65308d1d71f7c8722fff`, base 2.27.3,
Comms-10k 1.4.15. Both origin and github master verified live at
`885397981d7550afebf6518ef21b4fc54b062859` on 2026-10-15. Work remains local.

## Evidence and declared scope (before code changes)

HandleFileUpload resets shared upload state, never raises isLoading and appends parsed
rows directly across CSV reads. A later parse failure therefore leaves a valid prefix
available to Validate. None of the six handlers that owns isLoading refuses busy entry;
an overlapping preview/export can release another handler's busy ownership. The first
query row is trimmed before storage, losing whitespace in its rejection while duplicate
rejections retain raw text. C3/C4 already snapshot validation inputs and submission
approval/completeness before awaits; preserve that work rather than replace it.

Declared files:

- `Components/Pages/Comms10k.razor`: first-statement busy refusal in HandleFileUpload,
  ValidateEmails, ExecuteReplace, LoadPreview, DownloadFull and DownloadRejections;
  upload owns the flag until completion, parses into locals, and publishes the entire
  successful upload together; preserve the raw first-row text separately from query
  addresses, retaining trimmed case-insensitive matching and duplicate semantics.
- `Modules/ModuleCatalog.cs`: Comms patch only; no shared behavior change intended.
- `ExchangeAdminWeb.Tests/Comms10kBusyStateTests.cs`: compiled page behavioral guards
  with controlled browser-file reads, awaits and local dependencies. Cover each owner,
  overlapping operations, upload failure/reentry, frozen validation/submission inputs,
  full-resolution refusal and original rejection text with unchanged lookup semantics.
- Existing Comms rejection-report assertions/fixture seams only where the local parser
  contract requires adaptation; no weakening or exemptions of real guards.
- ClickGateRegistry/ProgressRegistry only as established by the re-anchor playbook and
  the page's actual handlers/call identities.
- This finding, review index, approved plan, current state and token log: scope, behavioral
  mutation map, exact-byte/hash/mtime restoration, final gates and Constitution packet.

## Boundaries

Retain the C4 frozen GUID fingerprint and validated ticket; direct submission still
requires successful complete resolution, zero combined rejections and a nonempty approved
set. Retain C5 jobsBusy, cache generation and full-owner handling separately from isLoading.
Retain C2 current-principal/current-groups snapshot, dispatch enforcement and explicit
refusal actor. C8 safe audit/formula export and C6 ticket-display work remain separate.
No live AD/tenant/vault calls, deployment, push, branch/history/hook/trust changes,
protection check, locking, broad cleanup, self-review or subagents. psl-2/3/4 and
cdr-followup-1 remain unapproved. Module-spec 2.0/June 25 versus app 2.27.3 drift remains
flagged; Constitution/current code govern.

## Implementation

Every isLoading owner now returns as its first statement if another operation owns the
flag. Upload raises the flag before invalidating the previous file and approval, then
owns it through finally. Rows, pre-lookup rejections, duplicate rows and the first raw
text for each query address are collected locally. One tuple assignment publishes all
four collections and the filename after the final successful CSV read. A parse/stream
error leaves empty upload state and no approval, so its valid prefix cannot be validated
or submitted, including through direct event invocation.

The query list remains trimmed and deduplicated with OrdinalIgnoreCase. AllRejections
maps the resolver's normalized first-row address back to the original field text; the
existing duplicate lookup still compares normalized addresses, then reports each raw
duplicate only if that address failed resolution. Resolved duplicates remain silent.
This changes reporting, not lookup/matching semantics or the approved member set.

C4's pre-await validation input copies, source identity check and immutable confirmation
are retained. ExecuteReplace still captures that approval and complete-resolution
decision before its first await, uses its validated ticket and GUID fingerprint, and
requires successful resolution, nonempty members and zero combined rejections. C2's
current actor/group capture and dispatch gates and C5's separate jobsBusy/cache generation
are unchanged. Comms-10k is 1.4.16; the base remains 2.27.3.

The re-anchor playbook compared committed and working page lines with SequenceMatcher
equal opcodes: 1,189 old lines, 1,214 new lines, 1,175 equal lines. The only Comms entry in
ClickGateRegistry is its pre-existing tier-3 exclusion, so there are no line-keyed entries
to move. Existing ProgressRegistry handler/call identities remain; its upload rationale
now describes the actual busy guard, local publication and local completion count.

## Behavioral regression coverage

27 new cases invoke the compiled page with local controlled continuations. They exercise
real CsvHelper parsing, the existing local service/dummy-credential HTTP fixture, the real
temporary SQLite queue with its pump disabled, captured JS transfers, and real optional
ticket validation over a local HttpMessageHandler. Existing test-only directory seams now
return safe local read rows and capture query inputs. No socket, vault, AD or tenant call
was made. These are behavioral assertions, not source-shape substitutes.

| Case family | Cases | Behavior |
|---|---|---|
| EachBusyOwner | 6 | Suspend each actual owner, invoke it again directly, require immediate refusal, unchanged state/approval/filename/activity/transfers and no duplicate enqueue |
| UploadPublishes | 1 | Suspend after real rows were parsed; require all collections unpublished, second upload unopened and refused before resets, Validate/Replace refused, then publish the full first file |
| FailedParse | 2 | A CSV syntax error or browser-stream IOException after a valid prefix leaves no usable rows, rejections, filename or approval; direct Validate sees no addresses and Replace cannot enqueue |
| ReadsCannotRelease | 6 | Preview/full export/rejection export each refuse during suspended Validate and Submit and cannot lower their busy flag or perform reads/transfers |
| ValidationCapturesInputsBefore | 3 | Change ticket, addresses or filename at the first yield; lookup and validated ticket remain frozen, and changed upload identity cannot be approved |
| ValidationKeepsCaptured | 1 | Change addresses/ticket while optional ticket validation awaits; resolver still receives the captured addresses and ticket retains its original validated value |
| SubmissionKeeps | 2 | Change approval, emails, filename, ticket and resolution at the first yield or auth await; job carries the original approved payload, fingerprint, ticket and current authorized owner/snapshot |
| DirectSubmission | 4 | Seed a valid approval, then fail resolution, empty its members, add a resolver rejection or add a parse rejection; direct handler refuses enqueue and audits/notifies once |
| RawRejectedRows | 2 | Email/UPN header variants retain whitespace/case for first and duplicate misses through the downloaded CSV, while queries stay trimmed/deduplicated and accepted duplicates stay silent |

The initial and formatter-adjusted focused runs passed 27/27. The first two compiled
original-page probes did produce the intended 17 failures, but the accounting checker
rejected custom Assert.True messages. Stack attribution pointed at an earlier local
declaration, so exact assertion-line matching was unsuitable. Those probes are excluded
from counted proof. The two custom
messages were removed, a Release solution Rebuild completed, and a fresh 27/27 baseline
passed. The portable PDB's embedded test-document SHA256 equals the current test source
(`05B920E898EBBFC0C145DBE2013B3AD985BDFA3238A6C0B526BB1FFDE99F4870`). The subsequent
proof accepts only explicit Assert.* failures with no compiler/build errors.

## Compiled mutation proof

All 27 new cases failed intended explicit Assert.* assertions across ten accepted
sequential compiled production regressions, producing 48 failures. Each probe ran
`dotnet test ExchangeAdminWeb.slnx -c Release --no-restore --filter ...` with
`xUnit.MaxParallelThreads=4`; none used --no-build. Compiler/build errors, non-assertion
failures, unexpected counts and stale TRX files were rejected. The union of exact failed
case names is the 27-case passing baseline, with no missing or extra name.

| Probe | Compiled production regression | Assertion failures / executed |
|---|---|---|
| M1 | Restore the entire committed original page | 17 / 27 |
| M2 | Remove all six busy-entry refusals | 13 / 13 |
| M3 | Move upload's busy refusal below the shared resets | 2 / 7 |
| M4 | Publish the local collections before any CSV read | 3 / 3 |
| M5 | Report the normalized first miss without its original text | 2 / 2 |
| M6 | Capture validation inputs after the first yield | 3 / 3 |
| M7 | Read live addresses and ticket after ticket-validation await | 1 / 1 |
| M8 | Capture submission approval/completeness/ticket after the first yield | 1 / 2 |
| M9 | Refresh submission approval/completeness/ticket after authorization awaits | 2 / 2 |
| M10 | Treat an existing approval as sufficient without resolution completeness | 4 / 4 |

Per-case union (names are in Comms10kBusyStateTests.cs):

| Case family / partition | Cases | Proving probes |
|---|---|---|
| EachBusyOwner: upload, validate, submit, preview, full export, rejected export | 6 | M1, M2; upload also M3 |
| UploadPublishes: suspended rows, second upload before reset | 1 | M1, M2, M3, M4 |
| FailedParse: CSV syntax / browser-stream IOException | 2 | M1, M4 |
| ReadsCannotRelease: Validate/Submit x Preview/Full/Rejections | 6 | M1, M2 |
| ValidationCapturesInputsBefore: ticket / addresses / filename | 3 | M6 |
| ValidationKeepsCaptured: addresses/ticket during ticket validation | 1 | M7 |
| SubmissionKeeps: first yield / auth await | 2 | M9 for both; M8 for first yield |
| DirectSubmission: failed / empty / lookup rejection / parse rejection despite approval | 4 | M10 |
| RawRejectedRows: Email / UserPrincipalName headers | 2 | M1, M5 |

Every probe restores saved exact bytes in finally, verifies both hashes and advances the
source mtime beyond its mutated value. Final fixed page bytes:

| Source | MD5 | SHA256 |
|---|---|---|
| Comms10k.razor | `F084A0CC3579040937E24BE658A5E625` | `0B65C64D742B126CEB5AB710C0AD451593386148B26E4E609D13E684CD90A1DF` |

Ignored machine-local supplements: `.agents/review/out/cdr-c7-mutations/receipts.json`
contains exact case names, assertion messages, counts, hashes and restoration mtimes;
the ten accepted TRX/log files sit beside it. Its script is `cdr-c7-mutations.py` one
directory above. M1's accepted receipt uses `M1-original-page-standard-asserts`; the two
earlier excluded original-page runs are not part of the counts. Passing current-source
baseline: `cdr-c7-standard-baseline/cdr-c7-standard-baseline.trx`. PDB checksum receipts
are `cdr-c7-pdb-proof.json` and `cdr-c7-pdb-proof-after-mutations.json`; re-anchor mapping
is `cdr-c7-reanchor.json`. This record owns the durable evidence.

## Final verification

- Restored focused Comms/ClickGate/Progress run: 784 passed / 0 failed / 0 skipped.
- Final `dotnet build ExchangeAdminWeb.slnx -c Release`: exit 0, 40 warnings / 0 errors.
  All 40 are the identical NuGet advisories emitted by the preceding C2 correction's
  57-warning build (40 NuGet + 17 existing C#). This final build was incremental after
  the restored focused compilation, so it emitted no C# recompile diagnostics.
  The explicit proof Rebuild emitted 18 existing C# diagnostics: the 16 remaining app
  diagnostics and two unchanged C4 test-field CS0649 warnings. C7 removes the old
  Comms nullable-flow warning caused by e.File?.Name. No new C7 diagnostic or dependency
  change. `cdr-c7-warning-comparison.json` owns the machine-local classification receipt.
- Full `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0,
  existing workspace-loading warning only; completed before the full suite.
- `dotnet test ExchangeAdminWeb.slnx -c Release -- xUnit.MaxParallelThreads=4`: exit 0,
  4,121 passed / 3 skipped / 0 failed (4,124 total), 4 m 54 s. Final TRX confirms all 27
  current C7 cases passed. This was the slice's only full-suite run; no full baseline ran.
- `git diff --check HEAD`: exit 0; repeated after final bookkeeping/staging.
- `tools/Test-AsciiOnly.ps1`: exit 0 with both new files staged, repeated after final
  staging so the new .cs source is included in its tracked-file scan.

The page still matches the mutation receipt's exact MD5/SHA256 after the final build,
format verification and full suite. The final portable PDB document checksum still equals
the current test source. Logs use `cdr-c7-build`, `cdr-c7-format`, `cdr-c7-test`,
`cdr-c7-diff` and `cdr-c7-ascii` under `.agents/review/out/`; full TRX is
`cdr-c7-final/cdr-c7-final.trx`. `cdr-c7-final-evidence.json` checks the complete case union,
restoration mtimes, hashes and final outcomes. The adapter's summary notExecuted counter
is zero; actual skipped rows are three NotExecuted results and total minus executed is
three. The supplemental checker was corrected to assert those actual rows/counts.

Browser rendering and installed cmdlet interoperability remain unverified. PowerShell
lint/Pester are not required because this slice changes no shipped PowerShell file.

The committed-baseline token meter ran successfully. Its scope is Claude Code transcripts
and excludes this native Sol implementation, so no slice usage or cost is attributed.

# Review Packet

## Mode

IMPLEMENTATION_REVIEW

## Original Request

Implement approved C7: all six shared-busy handlers must refuse reentry before any reset,
await or flag release; upload must own the flag, publish only a complete local parse,
retain original rejected-row text with unchanged matching, and preserve frozen complete
validation/submission inputs and all earlier C2/C3/C4/C5 contracts. Add compiled behavioral
regressions and prove every new case against compiled regressions, then restore exactly,
run the ordered local gate, record and commit this one finding. Independent Astra max
workflow review follows the implementation commit.

## Approved Plan

`docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, C7.
Owner 2026-10-15: "approved. go." The plan's execution sequence and model assignment
are recorded in `.agents/decisions.md`.

## Commit Range

`097cfd1e74d4622c016d65308d1d71f7c8722fff..HEAD` (the implementation commit carrying this packet).

## Claimed Changes

- First-statement busy refusal in HandleFileUpload, ValidateEmails, ExecuteReplace,
  LoadPreview, DownloadFull and DownloadRejections.
- Upload busy ownership, invalidation of the previous approval/file, local parsing and
  one successful publication of query/rejection/duplicate/raw collections plus filename.
- Original first-row rejection text and raw duplicates, with trimmed normalized lookup
  and case-insensitive duplicate matching retained.
- 27 compiled behavioral guards; all new cases mutation-proven with an exact-case union.
- Comms-10k patch 1.4.16; base remains 2.27.3. Records and ProgressRegistry rationale updated.

## Constraints / Out Of Scope

No C8 audit/formula export or C6 ticket-display change, service/worker/writer refactor,
protection check, locking, broad cleanup, live directory/tenant/vault probe, browser,
deployment, push, branch/history/hook/trust change, self-review or subagents. No changes
to jobsBusy/full-owner cache generations or current-principal/dispatch authorization.
Known module-spec header drift remains flagged.

## Design Decisions

- Keep query strings normalized for the existing resolver, approval and queued payload;
  a separate local map recovers only the first raw CSV field when reporting a rejection.
- Reset published upload state at the start to invalidate a previous approval, then
  publish the new file's full collections together only on parse success.
- Preserve the existing C4 snapshots/source identity refusal and C2/C5 authorization/cache
  code; the busy refusals are the required new server-side concurrency boundary.
- Keep export construction and auditing unchanged for the separately approved C8 slice.

## Deferred Work

C8 exports and C6 ticket display are next in the approved Part 1 sequence; Defender work
follows its plan order, with D2 before D7. psl-2/3/4 and cdr-followup-1 remain unapproved.
Browser rendering, hosted-circuit interaction and installed AD cmdlet interoperability
remain unverified. Remote work remains unpushed under policy ask.

## Review Focus

Review the complete affected page workflow: direct invocation of each owner, refused
upload before resets, no parsed prefix or stale approval after failure, full collection
publication, raw text and duplicate matching across lookup/download, and the preserved
validation/submission snapshots and direct completeness gates. Include the interaction
with C2 authorization and C5 cached job ownership. Check the actual exception/await paths
and the meaning of assertions; do not rely on the coder's mutation count alone.

## Verification Run

Restored focused 784/784 passed; ten accepted compiled regression groups produced 48
Assert.* failures covering every one of the 27 new cases. Exact bytes/MD5/SHA256/advanced
mtimes verified after each restore, and the test PDB document checksum matches the current
test source. Final Release build, full format verification before full tests, 4,121 passed /
3 skipped / 0 failed, final diff and staged ASCII all passed. No browser, installed-cmdlet
interoperability, live AD/tenant/vault probe, push or deployment was run. No self-review ran.
