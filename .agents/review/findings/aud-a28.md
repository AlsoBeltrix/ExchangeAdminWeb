# aud-a28: a refused attempt left no durable audit at all

**Severity**: MEDIUM. Nothing is mis-mutated; what is lost is the record that someone tried.
The attempts that matter most here - an edit or an emergency disable aimed at a protected
principal - are exactly the ones that refused early and so recorded nothing.
**Status**: Verified and fixed; review pending
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Finding 28 of the 2026-10-16 audit, verified against the code.

`EmergencyDisableService.DisableAsync` has eight early returns - missing ticket, a failed
protected-principal check, a protected target with no servicer authority, unavailable AD or
Graph credentials, an unreadable AD or Entra pre-state, an unpersistable snapshot - and every
one of them returned above `LogAudit`. `ADAttributeEditorService.SaveAsync` has seven of the
same shape: corrupt allowlist, failed validation, a denylisted attribute, a failed or
blocking protection check, missing credentials, a busy throttle.

Each refusal did call `_operationTrace.Step(...)`. The trace is diagnostic - different store,
different retention, different readers. "Who tried to emergency-disable this account and was
stopped?" is asked of the audit log, and the audit log did not know.

## Predicted observable failure

An operator attempts an emergency disable against a protected principal and is blocked. Later
someone asks who tried. The audit log is empty for that target; the only evidence is in a
diagnostic trace that is not retained for the same period and is not the record anyone
consults.

## Approach

A `LogRefusal` helper in each service, called immediately before every refusal return. It
writes a module action with `success: false`, the reason the operator was given, the step
that refused (`refusedAt`), and `mutationAttempted = false`.

`mutationAttempted` is the point of the record. Without it a refusal row is indistinguishable
from a failed write, and those are materially different events: one changed nothing, the
other may have changed something and then failed.

No refusal message or control flow changed - the operator sees exactly what they saw before.

## Files changed

- `Services/EmergencyDisableService.cs`
- `Services/ADAttributeEditorService.cs`
- `ExchangeAdminWeb.Tests/RefusalAuditTests.cs`
- `Modules/ModuleCatalog.cs` - EmergencyDisable 1.3.2 -> 1.4.0, ADAttributeEditor 1.5.1 ->
  1.6.0. Module-scoped behaviour; no shared code touched, so no base app version bump.

## Guard proof

Source scans, and the limit is declared in the test file: both services reach a live
directory or Delinea before most of these refusals, so nothing here drives them end to end.
What they pin is that no refusal path returns without recording first.

| Test | What it pins |
| --- | --- |
| `EveryEmergencyDisableRefusal_WritesAnAuditRecord` | refusal returns and `LogRefusal` calls are equal in number, >= 8 |
| `EveryAttributeSaveRefusal_WritesAnAuditRecord` | the same for `SaveAsync`, >= 7 |
| `ARefusalRecordSaysNoMutationWasAttempted` | the flag that distinguishes a refusal from a failed write |
| `ARefusalLogSitsInsideABracedBlock` | the call is not the lone statement of a brace-less `if` |

| Mutation | Result |
| --- | --- |
| one `LogRefusal` call deleted | FAIL - `EveryAttributeSaveRefusal_WritesAnAuditRecord`, counts differ |
| braces removed at one site | FAIL - `ARefusalLogSitsInsideABracedBlock`, naming line 528 |

Source restored byte-identical (md5 verified), touched, rebuilt.

Gate: Release 0 errors; suite 4399 passed / 3 known AD skips / 0 failed; format and
`git diff --check` clean.

## The fourth test exists because the first attempt shipped the bug it catches

The first version of this fix inserted each `LogRefusal` call above a return that a
brace-less `if` had governed alone:

```csharp
if (allowlist == null)
    LogRefusal(target, performedBy, ip, ticket, "...");
    return new(false, "Attribute allowlist configuration is corrupt.", null);
```

The `if` then guarded only the logging, and the refusal return became **unconditional**:
`SaveAsync` would have failed every call, in all seven places, reporting a corrupt allowlist.

The three counting guards passed on that code. Both the call and the return were present, so
the totals matched. `dotnet format` caught it - as four WHITESPACE errors, because the
indentation no longer described the control flow - and the compiler did not reject it.

This is the known failure class "a source scan passes for the wrong reason", recorded in
`.agents/state.md`, in a new form: counting proves a line exists, not that it is reachable on
the path it was written for. `ARefusalLogSitsInsideABracedBlock` pins the shape instead, and
it fails on the original bug.

## Known gaps

- **Source scans, not behaviour.** No test drives a real refusal through to a real audit
  store; the services need a live directory or Delinea to get that far. A refactor that keeps
  the call sites but breaks `AuditService` would pass these.
- `SaveAsync` has no happy-path behavioural coverage at all. That is why a version of it that
  failed every call still passed 4397 tests. Out of scope here; worth its own entry.
- Only these two services were swept. Other modules may refuse without auditing; the audit
  finding named these two and that is what was fixed.

## Closeout

Fixed directly on `master`. Independent review pending.
