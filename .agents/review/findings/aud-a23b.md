# aud-a23b: the on-premises path still reads applied rights out of Detail

**Severity**: LOW. Correct today. It is a convention with one remaining consumer and a sharp
edge that `aud-a23` had to route around.
**Status**: Fixed on branch; awaiting merge
**Branch**: `worktree-agent-a6bb7956d82362f14`
**Commit**: `<pending>`
**Reviewer**: implementer (filed by the `aud-a23` implementer)

## Evidence

`Components/Pages/MailboxPermissions.razor`, the `ExecuteOnPrem` path:

```csharp
var succeeded = opResult.Detail?.Split(',', StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
```

`Detail` means different things on different paths. On a partial result it is the applied
rights. On a **successful cloud grant** it is the OWA link text. This parse is safe only
because `AddMailboxPermissionsOnPremAsync` never returns a cloud result, where `onPrem: true`
suppresses the link.

`aud-a23` added `PermissionResult.AppliedRights`, which says the same thing unambiguously and
is populated on the on-premises paths too. This site is now the only consumer of the old
convention.

## Predicted observable failure

Not reachable as written. It becomes reachable if the on-premises and cloud paths are ever
merged, if `ForGrant` gains a link on the on-premises branch, or if someone copies this line
to a cloud call site - which `aud-a23` nearly did, and which would put a URL in the
permission-type line of a user notification.

## Approach (proposed, not approved)

Replace the split with `opResult.AppliedRights ?? []` and delete the `succeeded` local. The
two per-right audit calls above it use `succeeded.Contains(...)`, which reads the same from
the list. Three lines, and the page must stay at its `ExpectedLineCount` of 808 or be
re-anchored per `.agents/playbooks/clickgate-reanchor.md`.

## Plan (approved by the owner's EVERYTHING go)

Owner, 2026-10-21: "fix all the bugs that you know about and any more you find while doing
so" and "... EVERYTHING." (`.agents/decisions.md`, 2026-10-21 "Every open review follow-up
is approved for implementation"). That approves
this follow-up, which was recorded "not approved". The plan is the proposed approach, plus the
survey this record's known gap asked for:

1. **Survey first.** Every production read of a `Detail` member - `Authorization`,
   `Components`, `Middleware`, `Models`, `Modules`, `Services` and the root `.cs` files.
2. **`ExecuteOnPrem` reads `AppliedRights`.** `var applied = opResult.AppliedRights ?? [];`
   replaces the split, and every reader of the old local reads `applied`: the two per-right
   audit flags, the `_PARTIAL` marker on the admin notification, the notification gate and the
   rights the notice names. The gate keeps its `opResult.Success ||` clause and the notice
   keeps its `permType` fallback, so the only thing that changes is where the rights come from.
3. **Behaviour-neutral today, by construction.** For every result `MailboxPermissionOutcome`
   produces on the on-premises paths the two sources agree: a partial carries the landed right
   in both; a full success has `Detail` null and `AppliedRights` = every right, in request
   order, so the per-right flags (already true through `opResult.Success`) and the notified
   rights (`permType` before, the same names joined the same way now) do not move; a total
   failure carries neither. The credentials-unavailable early return is a `Fail` with neither.
4. **The page stays at exactly 808 lines** - one statement replaced, and the comment added
   after the mutation round takes the blank line under it - so no `ClickGateRegistry` line key
   moves and `ExpectedLineCount` is untouched.
5. **Guards** in `ExchangeAdminWeb.Tests/MailboxPartialNotificationTests.cs`, beside the
   `aud-a23` page scan: (a) a method-scoped scan of `ExecuteOnPrem` - a scan for the same
   reason as `aud-a23`'s: the handler needs a live Exchange connection - pinning that it takes
   `opResult.AppliedRights`, that every rights decision reads `applied`, and that its code
   (comments and literals blanked) never touches `.Detail`; (b) a tripwire over every
   production `.cs` and `.razor` file (comments blanked) refusing a `.Detail` member that is
   split, searched, sliced, case-folded or compared as text, coalesced to a string and then
   any of those, or handed to `Regex` or `string.Equals`/`Compare`.
6. **Module-scoped**: Mailbox Permissions. The version bump is the supervisor's. Nothing
   shared changes - `PermissionResult` is untouched.

Mutations planned, each at the call site: the old split reinstated; one per-right audit flag
read back from `opResult.Detail?.Contains(...)` with `applied` kept for everything else; the
split re-spelled as `(opResult.Detail ?? "").Split(',')`.

## Approach

As planned.

**The survey** (at `ca6575a`, every `.Detail` member read in the production trees named in
plan step 1). `Components/Pages/MailboxPermissions.razor:712` was the only parse. Every other
`PermissionResult.Detail` read displays it - `@result.Detail` on `MailboxPermissions`,
`CalendarPermissions`, `BlockedSenders` and three places on `Migration` - or copies it
(`Services/MigrationService.cs:563`, the protected-exclusion re-wrap). The remaining hits are
other types' `Detail` (progress activities, message-trace events, emergency-disable steps,
lockout rows, MFA methods, usage events, the true-last-logon answer, and the
`Comms10kMemberResolver.Detail` record type), and none of those is parsed either.

**The fix**, `ExecuteOnPrem` in `Components/Pages/MailboxPermissions.razor`: line 713
`var applied = opResult.AppliedRights ?? [];` (the same expression `SubmitSingle` uses at
515), with a one-line comment at 712 above it; `applied.Contains("FullAccess")` (716),
`applied.Contains("SendAs")` (722), `applied.Count > 0` for the `_PARTIAL` marker (727) and the
gate (732), and `applied.Count > 0 ? applied : new[] { permType }` for the notified rights
(735). The file is still 808 lines: the comment took the blank line that separated the local
from the first `if`.

## Files changed

- `Components/Pages/MailboxPermissions.razor` - `ExecuteOnPrem` only, same line count.
- `ExchangeAdminWeb.Tests/MailboxPartialNotificationTests.cs` - two tests and the tripwire's
  patterns, samples and file walk.
- This record.

## Guard proof

| Test (`ExchangeAdminWeb.Tests/MailboxPartialNotificationTests.cs`) | Pins |
| --- | --- |
| `TheOnPremPathReadsAppliedRights_NeverDetail` | Scoped to the `ExecuteOnPrem` body (`AuditCategoryFilingTests.MethodBody`): it takes `var applied = opResult.AppliedRights ?? [];`; both per-right audit flags are `opResult.Success \|\| applied.Contains(...)`; the `_PARTIAL` marker and the notification gate test `applied.Count > 0`; the notice joins `applied` (falling back to `permType`); and the method's code view (`RazorSyntax.CodeView`, comments and literals blanked) matches no `\.\s*Detail\b` at all |
| `NoProductionCodeParsesADetailMember` | Every `.cs` and `.razor` in the repo root and in `Authorization`, `Components`, `Middleware`, `Models`, `Modules`, `Services`, comments blanked (`RazorSyntax.CommentsBlanked`), against four patterns: a text method on `.Detail` (`Split`, `Contains`, `IndexOf`, `StartsWith`, `Substring`, case-folding, `Equals` and the rest), the same after `?? ""` / `?? string.Empty`, `.Detail` handed to `Regex.*` or `string.Equals`/`Compare`, and `.Detail` compared with a string literal on either side. Not vacuous: it asserts the walk reached `MailboxPermissions.razor` and `MailboxPermissionOutcome.cs`, that each of nine parse samples is refused, and that five reads the app really makes (`@result.Detail`, `string.IsNullOrEmpty(result.Detail)`, `step.Detail != null`, `Detail = result.Detail,`, `string.IsNullOrWhiteSpace(last.Detail) ? last.Detail : ...`) are allowed. Its failure names file and line |

Mutations on `Components/Pages/MailboxPermissions.razor`, fixed md5
`A183A95E490F7A95AA58EFF3F598E112` (pre-comment; LF in the working tree and the index, so the
md5s are of the content as committed). Each was applied by exact-text replacement asserted to
match once (M1 by whole-file replacement), confirmed by md5, built in Release and run against
the whole class, then the fixed bytes were restored, touched and md5-confirmed. Every mutation
failed exactly the two new tests; the other ten in the class passed under each.

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| M1: the pre-fix shape (the base file, `ca6575a`): the `Detail` split and every `succeeded` read | `9B2126D2D0C3A42355AA6A74C7F0DA54` | both: the scan ("Sub-string not found" on `var applied = opResult.AppliedRights ?? [];`), the tripwire ("Parsed here: Components\Pages\MailboxPermissions.razor:712") |
| M2: `applied` kept for everything except the FullAccess audit flag, which reads `opResult.Detail?.Contains("FullAccess") ?? false` | `AF2822D18548B1BE71B57CF5DE8C137E` | both: the scan (the FullAccess flag string not found), the tripwire ("...:716") |
| M3: the split re-spelled, `var applied = (IReadOnlyList<string>)(opResult.Detail ?? "").Split(',', StringSplitOptions.TrimEntries);` | `620AE8C0070FDD83B9606FB07A964DEC` | both: the scan, the tripwire ("...:712", through its coalesced-string pattern) |

Post-comment md5 `13462BF905F97D63DA2DC675F754813F`.

## Gates

On the final code, base `ca6575a`:

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: 4492 passed, 3
  skipped (the three known `ADDirectorySearchServiceTests` AD skips), 0 failed, 5 m 7 s; no
  hang. Baseline at `ca6575a`: 4490 / 3 / 0 - the difference is the two tests above.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD`: clean. `tools/Test-AsciiOnly.ps1` after `git add`: passed.

## Known gaps

- **No Exchange was reached.** The handler cannot be driven without a live on-premises
  connection, so the wiring is a source scan, as `aud-a23`'s is; the values it reads are the
  ones `MailboxPermissionOutcome` is already tested to produce (`AFullGrant_NamesEveryRight`
  and the partial cases above).
- **The tripwire is text, not data flow.** It catches a parse written on the member access
  itself. `var d = opResult.Detail; d.Split(',')` - the value moved into a local first - would
  pass it. Nothing in the app does that today (the survey above), and the consumer scan forbids
  any `.Detail` read inside `ExecuteOnPrem` outright.
- **It is deliberately broader than `PermissionResult`.** Any type's `.Detail` that is parsed
  trips it. None is today; a future one that must be is to be argued for in the test, by name.
- Observed, not changed: `MigrationService`'s protected-exclusion re-wrap (`:557-566`) builds a
  new `PermissionResult` without `AppliedRights` or `Changed`. Harmless today - no migration
  result carries either (`Changed` defaults to true and migration never returns `NoChange`) -
  but it is the same field-by-field-rebuild shape that `WithServicedNote` had to be fixed for
  in `aud-a23`.
