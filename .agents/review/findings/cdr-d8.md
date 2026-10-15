# cdr-d8 - preserve ordinary OS build and escaped AD site values

Status: Implemented and verified on master; independent Astra max review pending. Push pending.

Approved scope: `docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, D8.
Owner 2026-10-15: "approved. go." Implementation begins on clean master
`d9c71f655963d99e309fe6ed004f58009ee01d7c`; origin and github master were checked
with `git ls-remote` and both remain `885397981d7550afebf6518ef21b4fc54b062859`.

## Evidence and declared scope (before code changes)

`DefenderEndpointDeviceService.ParseDevice` reads osBuild through GetString, which
accepts only JSON strings. Numeric machine builds therefore become empty in the
returned row, its detail view and the existing CSV projector. Accept numbers for
this field while preserving legitimate strings and existing case-insensitive lookup.

`AdSiteDirectory.SiteNameFromDn` splits at the first comma, including escaped
commas inside the site's CN. Read the complete first RDN value, including escaped
characters and UTF-8 hex escapes, through a bounded local parser. The existing
ProtectedPrincipalService CN reader does not decode escapes or honor even
backslashes; EmergencyDisableService's sound separator scan only returns a parent.
Neither is a complete reusable site-name reader. No general DN refactor is planned.

Add minimal compiled HTTP/directory-stub regressions through the real service,
actual page filter and CSV projector. Preserve ordinary existing inputs. Prove
every new case using compiled production regressions and standard Assert failures,
then exact-byte/MD5/SHA256 restoration with newer mtimes and final sequential gates.

Defender changes 1.6.6 -> 1.6.7. Shared AdSiteDirectory behavior independently
requires base VersionPrefix/AssemblyVersion/FileVersion 2.27.3 -> 2.27.4 under
the Constitution. Comms remains 1.4.18; unrelated module versions stay unchanged.
Known module-spec Version 2.0 / June 25 versus base-app drift remains flagged.

D10 pagination, D9 export failure, D4 audit snapshots, D2 shared failure reasons,
D7 deadline/cancellation, D1/D3/D5 and deferred psl-2/3/4/cdr-followup-1 remain
outside scope. D6-local permits stale-comment correction only in touched code.
No page, authorization, credential, protection, audit, notification, locking or
serialization changes; no new dependency, live AD/tenant/vault probe, real subnet
claim, self-review, subagent, push, deployment, branch/history operation or hook,
trust/config change. No page edits means no ClickGate re-anchor is needed.

## Implementation

Only osBuild uses the new string-or-number reader. Numeric JSON values use their
invariant JSON representation, without an integer narrowing or locale-dependent
conversion. Strings retain their exact existing value. Missing/other kinds still
produce an empty cell, and the existing case-insensitive property lookup remains.
The returned DefenderDevice row already supplies the detail binding and BuildCsv,
so neither the page nor its projector needed a shipped edit.

The site reader consumes escaped characters together, distinguishing an escaped
comma inside the value from the delimiter after an escaped backslash. Consecutive
hex escapes are decoded together as UTF-8 bytes. Existing ordinary name trimming
and empty/missing-name behavior remain. The result passes through the existing
subnet map, Site assignment, case-insensitive page filter and CSV quoting.

Defender is 1.6.7 and the shared base is 2.27.4, with all three csproj version fields
aligned. Comms stays 1.4.18. D6-local stale comments in the two touched services now
describe Site failure isolation and batched posture/observer requests. The true
cross-instance Comms overlap warnings are untouched. No Razor file is changed in
the final implementation, so ClickGate and Progress registry edits are unnecessary.

## Compiled behavioral coverage

`ExchangeAdminWeb.Tests/DefenderOrdinaryDataTests.cs` adds seven compiled cases.
Local HTTP handlers provide token/inventory JSON; NSubstitute directory commands
provide RootDSE and subnet results. No live client, runspace or directory is used.

- Three cases read numeric 22631, numeric zero under mixed-case OsBuild, and a
  legitimate string build 19045.1234 through ListDevicesAsync. Each checks the
  returned row and reads the actual BuildCsv output using CsvHelper by column name.
- Four cases read a literal escaped comma, hex-escaped comma, escaped UTF-8 plus
  comma, and a terminal escaped backslash before the real RDN separator. Each
  checks the complete mapped human site name, then invokes the compiled page's
  FilteredDevices and ExportableDevices properties with the applied Site filter.
  The filtered CSV contains the exact complete Site value and matching device.
  Each case also preserves an ordinary London site, a device with no matching
  subnet, the ordinary London filter and an empty nonmatching filter result.

The accepted compiled baseline passed 7/7. The scoped formatter ran before the
proof snapshots. This is compiled service/filter/projector evidence; no browser
or rendering-host behavior is claimed.

The first restored affected run passed 233 cases and failed the existing catalog
version assertion, which still expected 1.6.6. Its expectation is aligned with
the required 1.6.7 module bump; no guard was bypassed or disabled. The restored
affected set is rerun after that fixture correction.

## Compiled mutation proof

Every probe runs `dotnet test ExchangeAdminWeb.slnx -c Release` with the new-class
filter, four xUnit threads and fresh TRX output. There is no --no-build use and
compiler errors are explicitly rejected. The checker requires precisely the
expected seven runtime cases, the expected failing subset, standard Assert.*
failure messages, and no additional failures. For every probe, the compiled app
mtime exceeds the changed production source mtime and the runtime app DLL hashes
equal the newly compiled app DLL hashes.

| Production regression | Assertion failures | Passing controls |
|---|---:|---:|
| Restore the original string-only osBuild read | 2 | 5 |
| Restore the original first-comma site split | 4 | 3 |
| Remove legitimate string-build support | 1 | 6 |
| Emit blank OsBuild in the actual CSV projector | 3 | 4 |
| Ignore the applied Site filter in the compiled page | 4 | 3 |

The 14 standard assertion failures cover exactly all seven new cases:

| Exact case inputs | Probes that failed it |
|---|---|
| osBuild, numeric 22631 | string-only reader; blank build CSV |
| OsBuild, numeric 0 | string-only reader; blank build CSV |
| osBuild, string 19045.1234 | removed string support; blank build CSV |
| `New York\, Building A` | original site split; ignored Site filter |
| `New York\2C Building A` | original site split; ignored Site filter |
| `Montr\C3\A9al\, Building A` | original site split; ignored Site filter |
| `Branch\\` | original site split; ignored Site filter |

Each changed source is restored to exact fixed bytes, verified independently by
MD5 and SHA256, and given an mtime newer than its mutation. Fixed source hashes:

| File | MD5 | SHA256 |
|---|---|---|
| DefenderEndpointDeviceService.cs | `3f0eb75ef211d0c9822d054ef6dc87bd` | `cf2eb187cbe06cffe2b8478383c7780578142550614f69ca4dd32c154fc07c2f` |
| AdSiteDirectory.cs | `63fcd8eca72aa040607d46d73166464d` | `c1c6f7c0a2ad43979ee646f427c4ae9f5eb438416eeb13df6d994a4982cb9cd6` |
| DefenderEndpointDevices.razor (unchanged) | `f69646cd1c5de522196144298795b96f` | `d3f6a3a12bb2d0e4e95719e171e6090bfd95ba7c4b5487066bebf97b3dd13983` |

New test SHA256: `9d90952d59f9779d14fe3a1366e95681233ffe654e8b63e042ef9f9b7941ce85`.
Machine-local evidence is under `.agents/review/out/cdr-d8/`: probe.py, proof.json,
five named *-evidence.json receipts, proof-summary.json, exact fixed-source binary
copies and fresh TRX/log directories. These are ignored evidence, not durable status.

## Limits and remaining work

No self-review ran. Independent Astra max review is pending. D10 is the next
approved implementation after review. No browser, live AD/tenant/vault probe,
physical-location acceptance, deployment or push was exercised. Directory/IP
values in the tests are synthetic fixtures, not measured subnet or site claims.
PowerShell lint and Pester are unnecessary because no shipped PowerShell changed.
The committed-baseline token meter ran; its Claude-only scope excludes this native
Sol implementation, so no per-slice token usage is attributed.

# Review Packet

## Mode

IMPLEMENTATION_REVIEW

## Original Request

Owner 2026-10-15 approved all twenty Revision 6 findings with "approved. go."
This single slice implements D8 only: preserve normal numeric osBuild values to
the returned row/detail data and CSV, and preserve a site's escaped-comma CN
through reading, mapping and the Site filter. Use sequential fresh Sol 6.1 max
implementation and independent Astra max review, one finding per commit.

## Approved Plan

`docs/Comms10kDefenderRemediation-Plan.md`, APPROVED Revision 6, D8; D6-local allows
stale comments only in touched code. `.agents/decisions.md` owns approval and model
arrangement. Constitution versioning rules independently require Defender 1.6.7
and base 2.27.4 for the shared directory behavior change.

## Commit Range

`d9c71f655963d99e309fe6ed004f58009ee01d7c..HEAD` (the single D8 implementation commit).

## Claimed Changes

- Accept numeric and legitimate string osBuild values using the existing property lookup.
- Decode the site's complete first RDN value with escaped separators/backslashes and UTF-8.
- Add seven compiled row/mapping/filter/CSV cases, with mutation proof for all seven.
- Bump Defender and the shared base independently; keep Comms 1.4.18 unchanged.
- Correct stale Site-failure/batching comments in touched services and update slice records.

## Constraints / Out Of Scope

No D10 pagination, D9 export-failure, D4 audit-snapshot, D2 failure-reason, D7
deadline/cancellation, D1/D3/D5 implementation, broad DN refactor, new dependency,
authorization, credential, audit, notification, protection, locking or serialization
change. No shipped Razor or PowerShell changes. No psl-2/3/4 or cdr-followup-1 work.
No self-review, subagent, live probe, push, deployment, branch/history, hook trust
or config change is authorized in this slice. Existing cross-instance Comms overlap
warnings remain load-bearing. Module-spec Version 2.0 / June 25 drift is still flagged.

## Design Decisions

- Numeric builds retain the JSON number representation without precision loss or locale
  conversion; only osBuild gains numeric acceptance. Other string fields retain their contract.
- The existing CN extractor leaves escapes and mishandles even backslashes; the parent-DN
  helper only locates a parent. A local first-value reader avoids changing those other callers.
- Consecutive hex octets decode together, so UTF-8 names survive rather than mojibake.
- Existing row/detail/filter/projector code already consumes these values; no shipped page
  change or ClickGate/Progress re-anchor is required. The tests invoke its compiled properties.

## Deferred Work

Independent Astra max workflow review, followed by approved D10 and the remaining
Defender sequence. Live location acceptance and browser use remain unproven;
adjacent findings and unapproved follow-ups retain their separate scopes.

## Review Focus

Review intent and the affected API-row/detail-data/CSV path plus directory RDN,
subnet mapping, compiled Site filtering and filtered export. Check escaped separator
boundaries, UTF-8 decoding, numeric zero/casing/string compatibility, existing
blank/unavailable semantics, version scope and actual mutation/gate receipts.
Trace callers and lifecycle as needed for the workflow; do not limit review to
the changed lines. No earlier clean verdict is a premise for this review.

## Verification Run

- Accepted compiled baseline: 7/7 passed.
- Five compiled production probes: 14 standard assertion failures, exact union all seven
  new cases; fresh runtime assembly provenance and byte/MD5/SHA256/newer-mtime restores passed.
- Restored Defender/AdSite affected workflow: 234/234 passed after aligning the existing
  catalog-version assertion with Defender 1.6.7. The first affected attempt had 233 passed
  and that one stale-version assertion failure; no production correction was needed.
- Final Release solution build: 40 existing warnings / 0 errors, exit 0.
- Full solution format verification: exit 0, completed before the single final full suite.
- Single final full Release solution suite: 4,155 passed / 3 skipped / 0 failed, using
  `-- xUnit.MaxParallelThreads=4`; runtime and compiled app SHA256 both
  `ffab897ed1318180916e0cbd3a38290e785b1620137466295104a2eb3ebb1844`.
- `git diff --check HEAD` and `tools/Test-AsciiOnly.ps1`: both passed, with the new test
  file staged for ASCII scanning. Documentation-only closeout is whitespace-checked again.
- `final-build.json`, `final-format.json`, `final-full-test.json`, `final-diff.json`
  and `final-ascii.json` record sequential timestamps and exit 0 for the required order.
  Final source hashes still equal all fixed proof snapshots; the new test source also
  remains exact. No shipped PowerShell changed, so no PSScriptAnalyzer/Pester run.
- No browser, live integration, physical-location, deployment or push verification ran.
