# Defect review request: a permission write that fails before its command was submitted is filed Failed with nothing written; Undetermined is reserved for a failure after the command was sent (aud-a76)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `03261f7c` or later.
- The change is the branch merged at `03261f7c`: `537ccd2c` (plan), `7ef600eb` (fix, tests, record), `8f3a441b` (record),
  forked from `a1eb8a3a`. Review `git diff a1eb8a3a 8f3a441b -- . ':!.agents'`, and read the COMBINED
  `Services/PermissionWriteSubmission.cs` (new: `PermissionWriteNotSubmittedException`, the per-call tracker with
  `Submitting()` and `TrackAsync`), `Services/PermissionChangeFailure.cs` (`TheConnection` stage, `StageOf(step, ex)`, the
  banner reading the wrapped cause), `Services/MailboxPermissionService.cs` and `Services/CalendarPermissionService.cs` (the
  eight write methods: where `Submitting()` sits relative to the first write command), `Components/Pages/MailboxPermissions.razor`
  528 and 744, `Components/Pages/CalendarPermissions.razor` 505 and 687 (`step = StageOf(step, ex)` first in each catch), and
  the tests `PermissionWriteSubmissionTests.cs`, `PermissionPagesSelfGrantTests.cs` on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command per
  call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a76.md`; parents `aud-a73.md`, `aud-a75.md`, `aud-a71.md`, `aud-a59.md`, `aud-a53.md`.
  ALREADY admitted and out of scope: `aud-a70` items 14-17 (a mid-command session drop filed Failed through the per-right
  results; no notification for an Undetermined change; the retried Calendar removal filed Undetermined; the pool's
  "stopped" message line break). Do not re-raise them. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Known Failure
  Classes" (1, 2, 3); `docs/ProjectConstitution.md` (audit rules).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 03261f7c`. Report whether both succeeded.

## Mandate

Hunt for observable defects. The mark placement in each of the eight writes: is `Submitting()` called strictly before the
first command that can mutate, and after every step that cannot (the bind and recipient check, `ConnectOnPrem`, the
session read) - name any write where a mutating command precedes the mark or a non-mutating step follows it; the
tracker "never cleared" per call - a write method that loops over several rights marks once at the first: a second
right's connect failure then goes out unwrapped (Undetermined for the right, which is right); `TrackAsync` wrapping only
a throw BEFORE the mark - an exception thrown by `Submitting()` itself or by the tracker; the bulk loops reading
`ex.Message` - unchanged text (the wrapper keeps the cause's message) - confirm for the `InnerException`-less case;
`StageOf(step, ex)` - a `PermissionWriteNotSubmittedException` arriving at `AfterTheWrite` (impossible?) - what stage;
the banner "Nothing was written: the connection to Exchange failed: <cause>" for a pool refusal that is not a
connection failure (module disabled, settings unreadable) - is "the connection to Exchange failed" the right phrase for
"Exchange Online is disabled"; the audit row for the pre-send failure: `Failed`, `mutationAttempted`? (the refusal
convention) - name whether the row distinguishes "nothing sent" from "sent and failed" beyond the text. Tests passing
for the wrong reason: `PermissionPagesSelfGrantTests`' refusal theory driving the pool's refusals through a stand-in pool
- does each refusal reach the page through the same exception type the production pool throws; the retired tests - was
any assertion lost that still mattered.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.
