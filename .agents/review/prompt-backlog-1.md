# Independent review request: unreviewed audit remediations (batch 1 of the backlog)

You are reviewing, not implementing. **Do not modify any file.** Report findings only.

## Context

Repo: `D:\source\ExchangeAdminWeb` (ASP.NET Core 10 Blazor Server; Exchange Online,
Microsoft Graph, on-prem Active Directory administration).

A long remediation stream landed these commits on `master` **without independent review**,
because the agent wrongly believed no review harness was available. They are unpushed. Your
review is the first anyone has given them.

Review this range:

```
git --no-pager log --oneline 30e893b..HEAD
git --no-pager diff 30e893b..HEAD --stat
```

Concentrate on these four, which are the highest-risk of the batch. Read each commit's full
diff and the matching record under `.agents/review/findings/`:

| Finding | Commit subject | Record |
| --- | --- | --- |
| aud-a28 | `fix(audit): record the attempts that were refused` | `aud-a28.md` |
| aud-a27 | `fix(emergency-disable): notify the security team, and say so only when true` | `aud-a27.md` |
| aud-a23 | `fix(mailbox-permissions): notify on what applied, not on overall success` | `aud-a23.md` |
| aud-a24 | `fix(bulk-permissions): notify the people a bulk run actually affected` | `aud-a24.md` |

## What to check against

- `docs/ProjectConstitution.md` - highest engineering authority.
- `.agents/repo-guidance.md`, especially the four **Known Failure Classes**: side-effect
  ordering vs try/catch/finally; success aggregation over N items; fail-closed
  authorization; stale references.
- Owner ruling, verbatim, that governs a23/a24: *"it ABSOLUTELY needs to email users when
  mailbox or calendar permissions are changed. NOT for group membership changes."*

## Specific things to attack

1. **aud-a28** adds a `LogRefusal` call before every refusal return in
   `EmergencyDisableService.DisableAsync` and `ADAttributeEditorService.SaveAsync`. An
   earlier draft of this fix put the call under a brace-less `if`, making the refusal return
   unconditional - caught by `dotnet format`, not by the tests. **Verify no refusal path is
   unreachable, mis-gated, or double-logged**, and that no refusal was missed.
2. **aud-a27** routes the emergency-disable notice to the module's `NotifySecurityTeam`
   address instead of the global admin list, and carries a `SecurityNotificationOutcome`
   back to the page. Check: is the outcome accurate on every path? Can the page still claim
   a notification that did not happen? Is the `EmergencyDisable_NotificationGap` audit
   written whenever it should be?
3. **aud-a23/a24** add `PermissionResult.AppliedRights` and notify on what actually applied.
   Check the partial-result logic hard: a grant where FullAccess lands and SendAs fails must
   notify about FullAccess only. Does any path announce a right that was not applied, or
   stay silent about one that was? Does the bulk path send exactly one notice per affected
   row and none for refused rows?
4. **Cross-cutting**: these four changed shared code - `Services/EmailService.cs`,
   `Models/PermissionResult.cs`. Did any change alter behaviour for an existing caller that
   was not part of the finding? `EmailService.SendUserNotificationAsync` and
   `SendOwnerNotificationAsync` were made `virtual`; `SendAdminNotificationAsync` was
   refactored to delegate to a new `SendModuleNotificationAsync`.
5. **The tests.** Every one of these findings is covered by source-scan guards. Tell me
   which would pass against broken code. This stream has already shipped three guards that
   asserted a string while the branch around it was dead - look for that shape specifically.

## Output

A list of findings: severity, file and line, what is wrong, and the concrete failure it
produces. Say plainly if a record's claim is false. If a category is clean, say so.
