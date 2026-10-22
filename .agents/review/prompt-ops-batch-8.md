# Defect review request: the cutover's refusal wording (aud-a38k) and the typed read of a reported schedule (aud-a7h)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `f367b2b3` or later.
- The change is the branch merged at `0187257e`: `95202167` (plans), `90f73fac` (aud-a38k, PowerShell),
  `ed505ee1` (aud-a7h, C#), forked from `4a8021a8`. Review `git diff 4a8021a8 ed505ee1`, and read the
  COMBINED `tools/AppPoolControl.psm1` (`Assert-AppPoolServesPath`, the `-NestedNodes` wording block),
  `Models/LookupModels.cs` (`ReportedTime`, `ReportedTime.Read`, `ShowReported`, `NoInstantReason`),
  `Services/OutOfOfficeService.cs` (`GetOutOfOfficeAsync`), `Components/Pages/OutOfOffice.razor`
  (the Check Status schedule line) on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a38k.md`, `aud-a7h.md`; parents `aud-a38j.md`, `aud-a38i.md`,
  `aud-a7g.md`, `aud-a7f.md`. ALREADY admitted and out of scope: `aud-a7i` (the unreadable marker
  names the type but not the text; an end-only schedule shows no line) and `aud-a23d` (the same
  `as DateTime?` reads in Migration and recipient lookup). Do not re-raise them. Repo rules:
  `AGENTS.md`; `.agents/repo-guidance.md` Architectural Invariants 1, 4, 5, 6 and "Known Failure
  Classes" (1, 2, 4).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 0187257e`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. a38k: the `Refuse` messages byte-identical to before (the promote,
deploy and installer pins); the `Allow` messages - accurate for a caller that stops pools and copies
databases; 5.1 compatibility of the wording block; any caller whose message changed that should not
have. a7h: `ReportedTime.Read` - a `PSObject` wrapping a `PSObject`; a `DateTimeOffset` at a non-zero
offset (instant preserved); the ISO 8601 grammar (fractional seconds, `T` versus space, a date-only
string, lowercase `z`); `AdjustToUniversal` with `Unspecified` input; the "first type name" of a
deserialized object (is `Deserialized.System.DateTime` read or marked unreadable - which is right);
the page gating the line on a reported start; `OutOfOfficeResult` consumers other than the page
(any that still expect `DateTime?`); the 30-minute-repeat `TimeZoneInfo` test's adjustment rule
validity. Tests passing for the wrong reason: the harness stub returning what each test sets.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
