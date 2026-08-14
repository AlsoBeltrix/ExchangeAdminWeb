# idm-1: Planned Graph mutation calls cannot produce the distinct failure outcomes the plan's tests require

**Severity**: HIGH - the plan mandates tests and an operator-facing behaviour that the named shared client cannot deliver, so implementation must either fail those tests, silently weaken them, or make an unplanned shared-infrastructure change.
**Status**: Verified
**Branch**: - (default-branch mode)
**Commit**: `<filled in below>`

## Evidence

- `docs/IntuneDeviceManagement-Plan.md` test plan: "Delete / retire / wipe: 204 success; 403;
  404; 5xx - each mapped to a distinct reported outcome, never a blanket success."
- `docs/IntuneDeviceManagement-Plan.md` S4: names `PostNoContentAsync` for retire and wipe.
- `Services/GraphTokenClient.cs:53-61` - `DeleteAsync` returns `bool`. The status code is
  discarded at `:60`.
- `Services/GraphTokenClient.cs:63-77` - `PostNoContentAsync` returns `bool`. Same discard at
  `:76`.
- `Services/GraphTokenClient.cs:104-119` - `PatchWithStatusAsync` already solves exactly this
  problem for PATCH, and its own doc comment says why: "so callers can report WHY a write
  failed ... instead of a bare bool".

The plan also depends on this in a place the reviewer did not name: manual check 12 ("point the
secret at an app registration lacking `PrivilegedOperations.All`; confirm wipe reports a
permission failure rather than a silent success") is unsatisfiable with a bool return. A 403
and a 404 are the same value.

## Predicted observable failure

An implementer following the plan writes `IntuneDeviceServiceTests` cases asserting distinct
outcomes for 403 / 404 / 5xx on delete, retire and wipe. They cannot be written against a
`bool`. The implementer then either (a) deletes the assertions, leaving the module unable to
tell an operator that consent is missing versus that the device is already gone, or (b)
modifies `GraphTokenClient` unplanned - a file two other modules use - with no base app
version decision recorded.

## What

The plan specified module behaviour whose only possible implementation is a change to shared
infrastructure, and then asserted the base app version would not move. Both halves cannot be
true. This is the same shape as `ru-2`: a plan reasoning about the code it will write and not
about the code it will call.

## Approach

Add two purely additive helpers to `Services/GraphTokenClient.cs`, modelled on
`PatchWithStatusAsync` and returning the same
`(bool Ok, HttpStatusCode StatusCode, string? SafeError)` tuple, reusing the existing
`ExtractGraphError` so no token or raw body can escape:

- `DeleteWithStatusAsync(string endpoint)`
- `PostNoContentWithStatusAsync(string endpoint, object? body = null)`

The existing `DeleteAsync` / `PostNoContentAsync` stay, unchanged, with their current callers
untouched - the same back-compat shape `PatchAsync` already has over `PatchWithStatusAsync`
(`GraphTokenClient.cs:121-125`). Additive is deliberate: `ppsvc-1` is this repo's record of a
shared-file change reaching further than its author expected.

Consequence, recorded rather than hidden: `GraphTokenClient` is shared infrastructure, so this
bumps the **base app version**. The plan's claim that a new module leaves it untouched was
correct for a module that only reads; it is not correct for this one.

## Files changed

- `docs/IntuneDeviceManagement-Plan.md` - new slice S0 ahead of S1 for the two helpers and
  their tests; S3/S4 reference the status-returning calls; T7 extended to writes; the
  versioning section corrected; D2's cost framing corrected (see Known gaps).

## Guard proof

Plan-stage finding; no code has been written. The criterion that must bite at implementation is
recorded as AC15: reverting either helper to its bool-returning equivalent must fail the
service tests that distinguish 403 from 404 from 5xx. Stated in the plan so the guard is
written, not remembered.

## Coder dispute (if any)

None. Verified directly against `Services/GraphTokenClient.cs`.

## Known gaps

Folding this in **invalidates the cost argument the plan gave for D2**. D2 offered "no
affected-user email" partly on the grounds that options 2 and 3 would force a base app version
bump by touching `EmailService`. With S0 in the plan the base version bumps regardless, so that
argument no longer distinguishes the options and D2 must be re-put to the owner without it.
Corrected in the same revision.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier`
(grade `fallback`; openreview over `b868e5c..6aef9e3`, verdict `acceptable_with_changes`,
`capability_ok: true`, both SHAs echoed)

Harness: codex-cli 0.147.0, `codex exec`, read-only sandbox. UTC 2026-08-14.

Reviewer's material change: "Add an explicit status-returning Graph mutation path for DELETE
and no-content POST before S3/S4, either by extending GraphTokenClient with
DeleteWithStatusAsync/PostNoContentWithStatusAsync and recording the required base app version
bump, or by reducing the test/UX promise to generic failure messages."

The second alternative was declined. Reducing the promise would put the module in direct
conflict with its own T7 rule (a failed request must not read as a benign outcome) and would
make manual check 12 impossible to run - and check 12 tests the exact misconfiguration this
module's three-permission split exists to make visible.
