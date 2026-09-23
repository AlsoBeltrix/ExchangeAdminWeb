# cpr-11: The audit's explicit nulls were dropped before they reached the log

**Severity**: MEDIUM - AC18 promises an identical key set on every event, so a Splunk search can
tell an explicit null from a missing field. The writer made that promise false for every event.
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: the commit that completes this record

## Evidence

The module built its audit `extra` with explicit nulls, as the plan requires.
`Services/JsonlLogService.cs` then discards them twice over: `WriteToFile` filters
`Where(kv => kv.Value != null)` before serializing, and its `JsonSerializerOptions` carry
`JsonIgnoreCondition.WhenWritingNull` as well.

So a null never reaches the file. The key is simply absent.

## Predicted observable failure

A successful sent reset omits `refusalReason` and `protectedPrincipalServiced`. A reveal omits
`destinationAddress`. Each outcome emits a different key set - which is exactly what AC18 exists
to prevent, because a search then cannot distinguish "this field does not apply here" from "this
field is missing because something is broken".

## Approach

The `"n/a"` sentinel. **The plan pre-authorised this exact fallback:** *"S6 confirms the JSON
writer emits nulls rather than dropping them; if it drops them, the sentinel is the string n/a and
this plan is amended to say so."* It drops them, so the sentinel is in force and the plan is
amended to match.

**Applied in this module rather than by changing the shared writer.** Making `JsonlLogService`
preserve nulls would change the shape of every other module's audit records - a cross-cutting
change with its own blast radius, and not this stream's to make.

## Files changed

- `Components/Pages/CloudPasswordReset.razor` - the sentinel in `AuditReset`.
- `docs/CloudPasswordReset-Plan.md` - rule 4 of the Splunk section, and the affected field types.
- `ExchangeAdminWeb.Tests/CloudPasswordResetWritePathTests.cs` - two guards.

## Guard proof

`The_audit_uses_a_sentinel_because_the_writer_drops_nulls` asserts the constant exists and that no
audit value is a bare null. The second guard,
`The_writer_really_does_drop_nulls_so_the_sentinel_is_load_bearing`, pins the REASON in
`JsonlLogService` rather than the workaround: if that service is ever changed to preserve nulls,
the test fails and whoever changed it can remove the sentinel deliberately, instead of leaving a
defensive string whose cause nobody remembers.

## Coder dispute

None on the defect. The reviewer offered two options - preserve nulls in the writer, or use a
sentinel and amend the plan - and the plan had already chosen the second for this case.

## Known gaps

**No test asserts the EMITTED JSON key sets are identical** across a success, a refusal, a reveal
and a delivery failure, which is what AC18 actually claims. That needs a serialization-level test
driving `AuditService` and reading the file back, which the reviewer also recommended. It is worth
doing and is not done: the guards here prove the module SUPPLIES uniform keys, not that the writer
emits them.

## Reviewer comments

Round 1, Change review over `3b29e5f^..3b29e5f`. Same dispatch as `cpr-8`.

## Closeout

Fix and this record land with cpr-8, cpr-9 and cpr-10 in one commit.
