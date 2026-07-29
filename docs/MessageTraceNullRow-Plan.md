# MessageTrace Null Pipeline Row — Plan

Status: Implemented (2026-07-29). Scope: defect fix only, no behavior change beyond
not crashing. Build/format/ASCII/diff-check clean; 830 tests green (827 + 3 new).
Non-vacuity proven per guard: reverting the cloud guard fails 2 tests, reverting the
on-prem guard fails its own test, both with the exact production
`System.NullReferenceException`. Live re-run of the failing prod search NOT yet
performed (needs deploy).
App version at draft: 2.3.30 (unchanged — module-scoped change)
Module: `MessageTrace` (Version `1.2.0` -> `1.2.1`)
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`,
`docs/AdminModuleSpec.md`. On conflict the higher source wins.

## Problem / Goal

A live Exchange Online summary trace fails with
`System.NullReferenceException: Object reference not set to an instance of an object.`
Observed in prod 2026-07-29 (`D:\inetpub\ExchangeAdminWeb\logs\app-20260729.log`,
10:43:08 and 10:58:22). Logged stack frame:

```
at ExchangeAdminWeb.Services.MessageTraceService.<>c__DisplayClass17_0.<GetCloudMessageTraceAsync>b__0(...)
   in D:\source\ExchangeAdminWeb\Services\MessageTraceService.cs:line 386
```

`Services/MessageTraceService.cs:386` is the first statement inside the loop over
the `Get-MessageTraceV2` pipeline output:

```csharp
foreach (var msg in results)                                        // line 384
    var subject = msg.Properties["Subject"]?.Value?.ToString() ?? "";  // line 386
```

The `?.` chain already guards a missing property and a null property value, so the
only remaining null-dereference on that line is **`msg` itself**: the PowerShell
pipeline returned a `Collection<PSObject>` containing a null element and the loop
dereferences it unguarded.

The user-visible symptom is the doubled banner
`Exchange Online trace failed: Object reference not set to an instance of an object.`
The duplication is expected, not a second defect: the inner catch
(`MessageTraceService.cs:423`) and the outer `RunMessageTraceBackendAsync` wrapper
(`:348`) both format the identical `"Exchange Online trace failed: {message}"`
string, and `GetMessageTraceAsync` merges partial errors into `Warnings` (`:27-28`)
before joining them into `Error` (`:41`).

Not a regression from the MT-detail work: `git blame` dates line 386 to `b70b59d`
(2026-06-04). It is data-dependent — the same feature succeeded at 10:58:21 the
same day (detail export emailed) — so only result sets containing a null row crash.

### Scope: the same defect exists in all four mapping loops

`GetPropertyString` (`:634`), `GetPropertyDate` (`:645`), and `GetPropertyLong`
(`:648`) all declare a non-nullable `PSObject obj` and dereference `obj.Properties`
on the first statement. Every loop that feeds them a raw pipeline element therefore
carries the identical crash:

| Line | Loop | Path |
|------|------|------|
| `:384` | `foreach (var msg in results)` | cloud summary — **the observed crash** |
| `:314` | `foreach (var evt in events)` in `MapCloudDetailEvents` | cloud detail |
| `:277` | `foreach (var item in tracking)` in `MapOnPremDetailEvents` | on-prem detail |
| `:501` | `foreach (var item in tracking)` | on-prem summary |

All four are fixed together: they are one defect class, and fixing only the observed
one leaves three identical live crashes reachable from the same page.

## Non-Goals

- Diagnosing **why** EXO emits a null pipeline row. The guard makes the app correct
  regardless of the cause; the upstream question is recorded as an open note, not
  chased here.
- No change to the error-banner duplication (`:348` + `:423`). It is cosmetic and
  out of scope for a defect fix; raise separately if the owner wants it.
- No change to filtering, ordering, truncation, mapping semantics, or the email /
  download / detail behavior.

## Approach

Skip the null element and continue. This matches the file's established fail-soft
posture (a per-row problem degrades that row, never the whole operation) and the
repo's Known Failure Class #2 (aggregate per-item failures; never fail the batch on
one bad row).

Rejected alternative: making the three `GetProperty*` helpers null-tolerant. It
would suppress the symptom at four call sites while leaving each loop body free to
dereference `msg`/`item` directly (`:386`, `:387`, and `:507` already do), so the
crash would remain reachable. Guarding at the loop head is the actual root fix.

## Slices

Single slice — one defect, one commit.

1. **Guard all four mapping loops against a null pipeline element.**
   - `Services/MessageTraceService.cs:384` — add `if (msg is null) continue;` as the
     first statement in the loop body.
   - `:314` (`MapCloudDetailEvents`) — add `if (evt is null) continue;`.
   - `:277` (`MapOnPremDetailEvents`) — add `if (item is null) continue;`.
   - `:501` — add `if (item is null) continue;`.
   - Bump `Modules/ModuleCatalog.cs` `MessageTrace` `Version` `1.2.0` -> `1.2.1`.
     No base app bump (module-scoped fix; Constitution "Deployment And Versioning").

## Tests

Add to `ExchangeAdminWeb.Tests/`. The two `internal static` mappers are directly
unit-testable and need no live pool:

- `MapCloudDetailEvents` with `new PSObject?[] { validEvent, null, validEvent }`:
  asserts no throw and that both valid events are mapped (null skipped, not
  substituted with an empty row).
- `MapOnPremDetailEvents` with a null element among valid tracking rows: same
  assertions, respecting the existing MessageId filter.

The two in-delegate loops (`:384`, `:501`) sit inside `RunPooledQueryAsync` bodies
and are not unit-hostable without a live pool — the file's existing convention
(`docs/ConferenceRooms-BuildingRoomList-Plan.md:133`). They receive the identical
one-line guard, verified by inspection and by the mapper tests covering the shared
defect class.

**Non-vacuity proof (required):** revert each guard, confirm the corresponding test
throws `NullReferenceException`, restore, confirm green. A test that passes with the
guard removed is vacuous and must be replaced.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx` (always target the `.slnx`)
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`
- ASCII gate: `tools/Test-AsciiOnly.ps1` (no non-ASCII in `.cs`)
- Live: re-run the failing prod search after deploy. Automated tests cannot cover a
  live EXO null row; state plainly if the live re-run was not performed.

## Open Questions

- **OQ-1 (non-blocking):** why does `Get-MessageTraceV2` emit a null pipeline row?
  Candidates: an EXO serialization hiccup, or a partial-page result under
  `ResultSize 2000`. Not required for the fix. If the guard starts skipping rows
  frequently, add a `LogWarning` on the skip to quantify it before investigating
  upstream.
