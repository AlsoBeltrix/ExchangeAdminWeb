# Independent review request: audit finding A25

You are reviewing, not implementing. **Do not modify any file.** Report findings only.

## Context

Repo: `D:\source\ExchangeAdminWeb` (ASP.NET Core 10 Blazor Server, Exchange/Graph/AD admin).
The change under review is **uncommitted** - review the working tree against `HEAD`:

```
git --no-pager diff HEAD -- Components/Pages/GroupManagement.razor Services/GroupManagementService.cs Modules/ModuleCatalog.cs ExchangeAdminWeb.Tests/ClickGateRegistry.cs
```

plus the new untracked file `ExchangeAdminWeb.Tests/GroupManagerNotificationTests.cs`.

## What the change is meant to do

Audit finding A25: security-group membership changes notified administrators only.

The owner ruled this personally on 2026-10-19, verbatim:

> "it ABSOLUTELY needs to email users when mailbox or calendar permissions are changed. NOT
> for group membership changes. Email the manager of the group with a summary of the
> changes, but do not email every user in the group."

So the intended behaviour is:

1. A security-group membership change emails the group's `managedBy` MANAGER a summary.
2. Members are **never** emailed. This is an explicit refusal, not an oversight.
3. **One message per operation.** A batch of 50 removals sends ONE summary, not 50 messages.
4. A group with no manager has **no recipient**: record that in the audit log. Do NOT fall
   back to the admin address and do NOT fall back to emailing members.

## Authority and invariants to check against

- `docs/ProjectConstitution.md` is the highest engineering authority.
- `.agents/repo-guidance.md` lists "Known Failure Classes" - check the diff against all four,
  especially #2 (loops must aggregate per-item failures, never blanket success) and #3
  (fail-closed authorization).
- `Components/Pages/GroupManagement.razor` is registered in
  `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` with an `ExpectedLineCount`. I changed it
  from 1050 to 1125 and claim no line-keyed entry moved, because every registered control is
  at lines 40-315 and all my edits are at 585+. **Verify that claim.**

## Specific things I want attacked

1. **Fan-out.** Prove or disprove that a 50-row bulk paste sends exactly one manager email.
   The per-row helpers `AddOneAsync` / `RemoveOneAsync` take a `sendAdminEmail` flag that is
   `false` for batch rows; I reused it as the "standalone operation" gate. Is that sound, or
   is there a path where a batch row notifies?
2. **The no-manager path.** Does anything reach a send when `GetGroupManagerAsync` returns
   null? Is the audit record actually written on every one of those paths?
3. **`GetGroupManagerAsync` itself.** It uses `Get-ADObject -Identity <DN>` with the module
   credential. Is the DN handling safe (injection, cross-domain, a DN that names a group or
   a contact rather than a user)? Does it fail soft in every case?
4. **Partial batches.** I notify when `summary.Done > 0`, including partial failures. Is the
   summary text I pass actually accurate about what landed?
5. **My tests.** They are source scans, declared as such. Tell me which of them would pass
   against broken code. I have already been bitten three times this session by guards that
   asserted a string while the branch around it was dead - look specifically for that shape.

## Output

A list of findings, each with: severity, the file and line, what is wrong, and the concrete
failure it produces. If a claim of mine above is false, say so plainly. If you find nothing
in a category, say that too.
