# cdr-followup-2: the validation step reports distinct members against the address count

**Severity**: LOW. The `cdr-followup-1` wording defect, at the two other places the same
resolve is summarised to an operator. No count is wrong; the pairing of them is.
**Status**: Merged to master; codex group review pending
**Branch**: `worktree-agent-a6bb7956d82362f14`
**Commit**: `e03e7c8` (merged)
**Reviewer**: implementer (found while fixing `cdr-followup-1`)
**Module**: Comms-10k

## Evidence

At `ca6575a`:

1. **The validation banner**, `Components/Pages/Comms10k.razor:239`, rendered when the
   resolve succeeded:

   ```razor
   <strong>@resolveResult.ResolvedDns.Count</strong> of @parsedEmails.Count email(s) matched in AD.
   ```

   `parsedEmails` is the upload, de-duplicated case-insensitively by the CSV parse (the `seen`
   set in `HandleFileUpload`), so one person's UPN and primary address are two entries.
   `ResolvedDns` holds one entry per distinct GUID (`Comms10kAddressResolver.Resolve`, the
   `resolvedSet` check). Two aliases of one person, nothing rejected: "1 of 2 email(s) matched
   in AD.", beside "Replace All Members (1 users)" and no rejection listed.

2. **The service's resolve message**, `Services/Comms10kService.cs:279`:

   ```csharp
   Message = $"{outcome.ResolvedDns.Count} resolved, {outcome.Rejections.Count} rejected."
   ```

   Members "resolved" beside addresses "rejected": an upload of two aliases and one unknown
   address reads "1 resolved, 1 rejected." - three rows reported as two. It reaches people
   twice: as the page's failure banner when validation finds a rejection (`ValidateEmails`,
   `result = new Comms10kUpdateResult { Success = false, Message = resolveResult.Message }`,
   rendered by `@result.Message`); and inside the job's refusal reason when the run-time
   resolve finds one (`Comms10kReplaceProcessor.ResolveAsync`: "Address validation rejected N
   address(es). Fix the file and re-upload. {resolve.Message}"), which is the persisted row
   message, the audit row's error and the admin notification's detail.

Seen and NOT a defect: `ValidateEmails`' progress activity reports `ResolvedDns.Count` as done
of `ActivitySize.Items(emails.Length)` and completes with "N of M matched" - the same pairing,
but `ActivityProgressService.Finish` drops an activity without rendering its message ("nothing
renders them, because the frame is live only"), and the Report and the Complete run back to
back with no await between them, so the frame never shows either. Left as written.

## Predicted observable failure

An operator uploads a list on which one person appears under both their UPN and their primary
address. Validate says "Validated; not applied yet. 1 of 2 email(s) matched in AD." with no
rejection listed and the Replace button live. The operator goes looking for an address that did
not match - there is none - or reads it as one person missing and stops. With a genuine miss
in the same file the failure banner says "1 resolved, 1 rejected." for three uploaded rows, and
a job whose run-time resolve finds that miss records the same sentence in its row, its audit
and its notification.

## Plan (approved by the owner's EVERYTHING go)

Owner, 2026-10-21: "fix all the bugs that you know about and any more you find while doing
so" and "... EVERYTHING." (`.agents/decisions.md`, 2026-10-21 "Every open review follow-up
is approved for implementation"). Found while doing `cdr-followup-1`, under the same go.

1. **Both sites use `cdr-followup-1`'s `Comms10kAddressResolver.DescribeResolved`**, so the
   stage message, the banner and the resolve message say one thing in one wording.
2. **The banner**: `@(Comms10kAddressResolver.DescribeResolved(parsedEmails.Count -
   resolveResult.Rejections.Count, resolveResult.ResolvedMembers.Count)).` - WITHOUT the
   rejected clause. The banner's own rejection line counts `AllRejections`, which also holds the
   rows the CSV parse rejected before any lookup, so a second and different "rejected" number
   beside it would contradict it. One line for one line, so the page keeps its 1233 lines.
3. **The service**: `DescribeResolved(emails.Count - outcome.Rejections.Count,
   outcome.ResolvedMembers.Count, outcome.Rejections.Count) + "."` - WITH the clause; it is
   the only count of rejections in that message. `Resolve` walks every input address and gives
   each exactly one of a rejection or a resolution, so the subtraction is the resolved-address
   count.
4. **Guards**, beside `cdr-followup-1`'s in the `Comms10kRefusalTests` partial class: the page's
   real `ValidateEmails` driven with the real service resolution over the fixture's directory,
   then the page's own render tree built and its text read (the `Comms10kTicketDisplayTests`
   precedent) - the success banner for two aliases, and the failure banner for two aliases and
   one unknown address; and the service's message for the latter, directly.
5. **Module**: Comms-10k; the version bump is the supervisor's. Nothing shared changes.
6. `docs/Comms10kReplaceUx-Plan.md` (Approved, 2026-06-29) item 1 quotes the old banner as an
   "e.g."; left as written - an example in a plan, not a contract the page must keep.

Mutations planned: the banner restored; the service message restored; the service's member
count passed as its address count.

## Approach

As planned.

- `Components/Pages/Comms10k.razor:239`: `@(Comms10kAddressResolver.DescribeResolved(...)).`
  replaces the `<strong>N</strong> of M email(s) matched in AD.` line - one line for one, the
  page still 1233 lines; no comment added to the markup, so the count holds.
- `Services/Comms10kService.cs`, `ResolveEmailsAsync`: the `Message` of the result built after
  `Resolve`, with a comment above it. The early returns ("Module not configured.", "AD
  credentials unavailable.", the lookup failure) are not summaries of a resolve and keep their
  text.

For two aliases of one person the banner now reads "Validated; not applied yet. 2 addresses
resolved to 1 distinct member. The distribution list has not changed."; for two aliases and an
unknown address the failure banner, and a job's refusal reason, carry "2 addresses resolved to
1 distinct member; 1 rejected."

## Files changed

- `Components/Pages/Comms10k.razor` - the banner line.
- `Services/Comms10kService.cs` - the resolve message.
- `ExchangeAdminWeb.Tests/Comms10kResolveStageMessageTests.cs` - three tests and a render-text
  helper, beside `cdr-followup-1`'s test.
- This record.

## Guard proof

| Test (`ExchangeAdminWeb.Tests/Comms10kResolveStageMessageTests.cs`, `Comms10kRefusalTests`) | Pins |
| --- | --- |
| `TheValidationBannerNamesAddressesAndDistinctMembersSeparately` | **The banner.** The page built by the C6/C7 harness; `parsedEmails` = an address and its alias, which the fixture's directory returns as ONE candidate (UPN and mail); the real `ValidateEmails` invoked, through the real `Comms10kService.ResolveEmailsAsync`. Then the page's compiled `BuildRenderTree` is run and its text and markup frames read: replace is allowed (`CanReplace`), the banner says "Validated; not applied yet." and "2 addresses resolved to 1 distinct member.", and "matched in AD" appears nowhere |
| `TheValidationFailureNamesAddressesAndDistinctMembersSeparately` | **The failure banner**, same harness, plus one address the directory does not know: replace refused, and the rendered page carries "2 addresses resolved to 1 distinct member; 1 rejected." and not "1 resolved, 1 rejected" |
| `TheServiceResolveMessageNamesAddressesAndDistinctMembersSeparately` | **The service message at its source** - which is also the tail of a job's refusal reason - for the same three addresses: not a success, the one rejection is the unknown address, and the message is exactly "2 addresses resolved to 1 distinct member; 1 rejected." |

Mutations, each at the call site, fixed md5s `Comms10k.razor` `63FE09E30F1170FA3F28202D5B2D0D0C`
and `Comms10kService.cs` `5CF26C1A2D3A2B8B16700CFABD14EA5E` (pre-comment; LF in the working
tree and the index). Each applied by exact-text replacement asserted to match once, confirmed
by md5, built in Release and run against every Comms-10k test (404), then the fixed bytes
restored, touched and md5-confirmed.

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| M1: the banner restored, `<strong>@resolveResult.ResolvedDns.Count</strong> of @parsedEmails.Count email(s) matched in AD.` (the mutant is byte-identical to `ca6575a`'s page) | `12081992FA5E4183788D6118F8091572` | 1 of 404: `TheValidationBannerNamesAddressesAndDistinctMembersSeparately` (sub-string not found) |
| M2: the service message restored, `$"{outcome.ResolvedDns.Count} resolved, {outcome.Rejections.Count} rejected."` (byte-identical to `ca6575a`'s service) | `A077BEFED3DE001B47E07AF50ED1FA58` | 2 of 404: `TheServiceResolveMessageNamesAddressesAndDistinctMembersSeparately` (actual "1 resolved, 1 rejected."); `TheValidationFailureNamesAddressesAndDistinctMembersSeparately` |
| M3: the service's member count passed as its address count | `4BFC05601C7BE20E613B31CB6ABC41DF` | 2 of 404: the same two (actual "1 address resolved to 1 distinct member; 1 rejected.") |

Post-comment md5: `Comms10kService.cs` `7230BF453B2F591DA5325A76F45030EA`; the page is unchanged
from its fixed bytes.

## Gates

On the final code, on top of `aud-a38e` (`faead5b`):

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: 4502 passed, 3
  skipped (the known `ADDirectorySearchServiceTests` AD skips), 0 failed, 5 m 16 s; no hang.
  The difference from `cdr-followup-1`'s 4499 is the three tests above.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD`: clean. `tools/Test-AsciiOnly.ps1` after `git add`: passed.

## Known gaps

- **The banner test reads a render TREE, not a browser.** `BuildRenderTree` is the page's
  compiled markup, run for real, but no renderer or browser is involved - the same footing as
  `Comms10kTicketDisplayTests`.
- **Messages already persisted keep the old wording**: job rows and audit rows written before
  this change, as in `cdr-followup-1`.
- The progress activity's "N of M matched" detail is the same pairing and is left as written,
  because it is never rendered (Evidence, last paragraph). If the frame ever starts showing an
  activity's completion message, that line becomes this finding's third site.
