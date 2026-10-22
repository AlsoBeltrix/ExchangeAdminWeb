# Defect review request: nested IIS nodes refuse the mirror (aud-a38i) and the Out of Office schedule zone (aud-a7e)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `9b7428b8` or later.
- The change is the branch merged at `5e9b2c79`: `12f22e87` (plans), `f6b86a46` (aud-a38i,
  PowerShell), `9b3080ee` (aud-a7e, C#), forked from `1049f839`. Review `git diff 1049f839 9b3080ee`,
  and read the COMBINED `tools/AppPoolControl.psm1`, `Components/Pages/OutOfOffice.razor`,
  `Services/OutOfOfficeService.cs`, `Services/EmailService.cs`, `Components/App.razor` on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a38i.md`, `aud-a7e.md`; parents `aud-a38f.md`, `aud-a38g.md`,
  `aud-a7b.md`, `aud-a7d.md`. Two follow-ups are ALREADY admitted from the implementer's hand-back
  and are out of scope: `aud-a7f` (the Check Status tab and the owner notice's Date line are still
  unzoned) and `aud-a38j` (the cutover inherits the nested-node refusal although it never mirrors;
  nodes inside the /XD folders refused; stale plan-line text). Do not re-raise them. Repo rules:
  `AGENTS.md`; `.agents/repo-guidance.md` Architectural Invariants 1, 4, 5, 6 and "Known Failure
  Classes" (1, 2, 4); `docs/ProjectConstitution.md` notification rules.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 5e9b2c79`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. a38i: the nested-node refusal versus the aud-a38f other-pool rule
(order, double-reporting, a node AT the folder under another pool plus nested nodes - one refusal or
two); the "pass returns the entries at the folder" claim; the boundary match for nested detection
(`D:\site\app` versus `D:\site\app2`, trailing separators, case, `/`); the default probe reading
application virtual directories (a vdir whose physical path is inside the folder but belongs to a
site rooted elsewhere); 5.1 constructs; message content (does it tell the operator what to do). a7e:
the browser zone read via JS interop on first render (prerender - does `OnAfterRenderAsync` run; a
circuit reconnect; a browser that returns an alias id like `Asia/Calcutta` or an id .NET cannot
resolve on Windows); `TimeZoneInfo.FindSystemTimeZoneById` with IANA ids on Windows Server 2022
(requires ICU - is that guaranteed on the deploy host, and what happens if not: refusal, which is
the recorded fail-closed, but is the message actionable); the DST-gap refusal and an ambiguous
(repeated) time; `ToUtc` on `DateTimeKind.Unspecified`; the service refusing a Scheduled request
with no zone before borrowing a connection; `-StartTime`/`-EndTime` receiving Kind-Utc values and
the recorded assumption about the Exchange Online module; the notice's `(<zone>, UTC<offset>)`
rendering across the DST boundary; the `ProgressRegistry` `Exempt` entry's justification; the
`ClickGateRegistry` tier-3 claim. Tests passing for the wrong reason: the fake `IJSRuntime`; the
harness's UTC-instant assertions.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
