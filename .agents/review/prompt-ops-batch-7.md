# Defect review request: callers state the nested-node rule (aud-a38j); Check Status and the Date line name their zone (aud-a7f); a repeated DST hour is refused (aud-a7g)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `7fad8df4` or later.
- The change is the branch merged at `f18431d0`: `f404724c` (plans), `40e578f0` (aud-a38j, PowerShell),
  `58ccceac` (aud-a7f, C#), `1e0ef195` (a7g record export + plan), `9f3f8e39` (aud-a7g, C#), forked
  from `36fb150d`. Review `git diff 36fb150d 9f3f8e39`, and read the COMBINED
  `tools/AppPoolControl.psm1`, `tools/Move-ConfigDbToShared.ps1`, `tools/promote-dev-to-prod.ps1`,
  `deploy.ps1`, `tools/Install-ExchangeAdminWeb.ps1`, `Models/LookupModels.cs`
  (`ToUtc`, `NoInstantReason`, `ShowReported`, `OffsetAt`), `Components/Pages/OutOfOffice.razor`
  (`ReadScheduleZoneAsync`, `ValidateOofSchedule`, the Check Status rendering),
  `Services/OutOfOfficeService.cs`, `Services/EmailService.cs` on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a38j.md`, `aud-a7f.md`, `aud-a7g.md`; parents `aud-a38i.md`,
  `aud-a38g.md`, `aud-a38f.md`, `aud-a7e.md`, `aud-a7d.md`. Already admitted and out of scope:
  `aud-a38k` (the "nothing at the folder" refusal tells the cutover it is refusing to replace files it
  never replaces) and `aud-a7h` (`GetOutOfOfficeAsync` reads `StartTime`/`EndTime` with `as DateTime?`
  so another type silently drops the schedule; the a7g refusal says "hour" for any repeat amount). Do
  not re-raise them. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` Architectural Invariants 1,
  4, 5, 6 and "Known Failure Classes" (1, 2, 4); `docs/ProjectConstitution.md`.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat f18431d0`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. a38j: the mandatory `-NestedNodes` parameter - every call site passes
it (AST, all five); `Allow` semantics for the cutover (a nested node under ANOTHER pool still refused -
is that the right rule for a script that only stops pools and copies databases, or does it block a
legitimate cutover); the `Refuse` path unchanged from aud-a38i; the return value under each mode;
5.1 compatibility of `ValidateSet` + Mandatory in a hostless runspace (binding error, not a prompt,
when a script omits it); the updated plan-line text versus what each caller does; the recorded
decision not to take `-ExcludedSubfolders`. a7f: `ShowReported` for `Kind=Local` (the server's zone -
converted with `ToUniversalTime`; correct on a server whose zone differs from the browser's);
`Unspecified` shown as reported with no zone (is "time zone not reported" actionable); the UTC
fallback when no browser zone; the notice Date line in UTC (reader is the mailbox owner). a7g:
`IsAmbiguousTime` at page and service; the end time; a zone with a non-hour repeat; the two
zone-resolution messages (does the resolution message mislead when the id is valid but ICU lacks it);
the recorded assumption about `Get-MailboxAutoReplyConfiguration` kinds. Tests passing for the wrong
reason: the harness stub returning Kind-Utc times only; the fake `IJSRuntime`; the Pester hostless
runspace binding test.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
