# idm-2: The wipe request shape is an unverified claim stated as fact

**Severity**: HIGH - the most destructive action in the module is specified by an assumption about Graph's default behaviour that nothing in the plan or the repo establishes.
**Status**: Verified
**Branch**: - (default-branch mode)
**Commit**: `<filled in below>`

## Evidence

- `docs/IntuneDeviceManagement-Plan.md`, Out of scope: "This module sends **no body**, which is
  a full factory reset."
- Microsoft Learn, wipe action, v1.0 (read 2026-08-14): "In the request body, supply JSON
  representation of the parameters", followed by a table of `keepEnrollmentData`,
  `keepUserData`, `macOsUnlockCode`, `obliterationBehavior`, `persistEsimDataPlan`.
- Contrast with the retire action page, same read: "Do not supply a request body for this
  method." The two Intune action pages say **different** things, and the plan treated them the
  same.

Nothing in the plan, the repo, or the Learn page establishes what Graph does with an absent
body on `wipe`. The claim "which is a full factory reset" is an inference presented as a
verified fact, in a document whose own Verified API surface section says everything in it was
checked against Learn.

## Predicted observable failure

Two candidates, and which one fires is exactly what is unknown:

1. Graph rejects the bodyless POST and the first live wipe fails with a 400. Recoverable, but
   discovered on a real device during a real decommission.
2. Graph accepts it and applies defaults that are **not** a full factory reset - `keepUserData`
   defaulting true on some platform would leave user data on a machine an operator was told had
   been wiped. This is the worse outcome and it is silent.

Either way there is no test that pins the intended semantics, because "send nothing" cannot be
asserted to mean anything in particular.

## What

The plan specified the destructive edge of the most destructive action by omission. An explicit
body is both testable and self-documenting; an absent one encodes intent nowhere.

## Approach

Send an explicit body on wipe with `keepEnrollmentData: false` and `keepUserData: false`, the
two flags that determine whether the reset is full. The remaining optional parameters are
deliberately omitted and each is named in the plan with the reason (`macOsUnlockCode` is a
per-device operator input this module does not collect; `obliterationBehavior` and
`persistEsimDataPlan` are platform-specific and belong with the wipe-options work already listed
as out of scope).

The service test asserts the serialized body, so the intent is pinned by an assertion rather
than by a sentence. S1's live-verification note additionally records what the tenant actually
does with the explicit body.

## Files changed

- `docs/IntuneDeviceManagement-Plan.md` - Out of scope entry rewritten; S4 specifies the body;
  the verified-API table annotated to distinguish retire (no body) from wipe (body); test plan
  gains the body assertion; AC16 added.

## Guard proof

Plan-stage finding; no code written. AC16 is the criterion: a service test asserts the exact
JSON sent to `/wipe`, and changing either flag to `true` must fail it. Recorded in the plan so
the assertion is written rather than assumed.

## Coder dispute (if any)

Partial, and recorded because the severity rests on it. The reviewer asserted the bodyless call
is "likely wrong" for Graph. That is **not established** - the Learn page does not mark the body
required, and an empty POST may well be accepted. I admitted the finding on the narrower and
solid ground: the plan asserted a *semantic* ("which is a full factory reset") that it had not
verified, for the one action that destroys a machine. The remedy is the same either way, and it
removes the dependency on the unknown rather than resolving it.

## Known gaps

Whether Graph accepts a bodyless wipe remains unknown and this fix makes it irrelevant rather
than answering it. If a future change wants the bodyless form back, it needs a live test, not a
reading of the docs.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier`
(grade `fallback`; openreview over `b868e5c..6aef9e3`, verdict `acceptable_with_changes`,
`capability_ok: true`, both SHAs echoed)

Harness: codex-cli 0.147.0, `codex exec`, read-only sandbox. UTC 2026-08-14.

Reviewer's material change: "Change the wipe design from 'no body' to an explicit, tested wipe
request body that represents the intended full factory reset semantics, or prove in live S1
notes that Graph accepts an empty body for this tenant/API version."

Adopted in its first form. The second - prove the empty body works - would establish that the
call succeeds without establishing what it does, which is the half that matters.
