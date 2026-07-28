# mt-detail-slice4: message-trace detail export email + zip attachment (slice 4)

**Severity**: n/a — slice-landing review of a new email method + attachment path + its tests
**Status**: Accepted (round 1, 2026-07-27) — verified, slice 4 complete
**Branch**: `master` (committed directly per repo policy; no per-finding branch)
**Commit**: `2575467` (slice 4), base `09a9605`

## Evidence
`Services/EmailService.cs` — new `SendMessageTraceResultAsync` (virtual, fail-soft),
new `ResolveMessageTraceRecipients` (internal static), and an optional MimeKit
`BodyBuilder.Attachments.Add` path folded into the private `SendEmailAsync` /
`SendEmailOrThrowAsync`. `ExchangeAdminWeb.Tests/EmailServiceMessageTraceTests.cs`
— 8 tests on the resolver.

## Predicted observable failure
Without a fixed recipient rule the detail export could be sent to an
operator-typed address (data exfiltration), or duplicate/blank recipients could
leak the file to the same mailbox multiple times / fail SMTP. The core guard:
`ResolveRecipients_DeduplicatesCaseInsensitively` (a user who is also an admin
appears once) and the recipient set derives ONLY from the authenticated identity
+ admin config. Guards: `dotnet build ExchangeAdminWeb.slnx -c Release` (0
errors), `dotnet test ExchangeAdminWeb.slnx --filter
FullyQualifiedName~EmailServiceMessageTraceTests` (8/8).

## What
Fourth slice of the Message Analysis detail work stream (plan task 4). Adds the
off-circuit email path that delivers the per-message delivery-detail export as a
zip attachment. The recipient set is the logged-in user plus the configured
admins only — never an operator-typed address — enforcing the owner exfiltration
rule. Send is fail-soft so a delivery failure never throws into the bulk-job
result.

## Approach
`SendMessageTraceResultAsync(userEmail, zipBytes, zipFileName, messageCount,
ticket, performedBy)` resolves recipients via `ResolveMessageTraceRecipients`,
builds an HTML summary body, and sends one message per recipient with the zip
attached. `ResolveMessageTraceRecipients` (internal static, exposed to the test
project via the existing `InternalsVisibleTo`) is the SMTP-free unit: it unions
the user address and the comma-split admin config, trims, drops blanks, and
de-duplicates case-insensitively. The private send path gains optional
`attachmentBytes`/`attachmentFileName` params that, when present, call
`BodyBuilder.Attachments.Add`. Mirrors the existing `Send*NotificationAsync`
family; `virtual` for test-seaming; failure logged, never thrown.

## Files changed
- `Services/EmailService.cs` — new email method + recipient resolver + optional
  attachment path (modified).
- `ExchangeAdminWeb.Tests/EmailServiceMessageTraceTests.cs` — 8 tests (new file).

## Guard proof
Removing `.Distinct(StringComparer.OrdinalIgnoreCase)` from
`ResolveMessageTraceRecipients` makes exactly
`ResolveRecipients_DeduplicatesCaseInsensitively` FAIL (1 failed / 7 passed);
restore -> 8 pass. Build 0 errors, added lines ASCII-clean (the file has
pre-existing non-ASCII in unrelated notification bodies), `dotnet format` no
changes, full suite 801/801.

## Coder dispute (if any)
None.

## Known gaps
The bulk-job processor that will call `SendMessageTraceResultAsync` (zip assembly
+ save-to-log-path + invocation) does not exist yet (slice 5); this slice is the
email method + attachment path only. The SMTP send itself is not unit-tested
(requires a live/faked SMTP server); only the recipient resolver — the security-
relevant, deterministic unit — is covered. Live email/zip delivery is a
manual-validation-on-dev standing gap.

## Reviewer comments

**Round 1 (2026-07-27): ACCEPTED.** Verdict JSON:
`{"verdict":"accepted","guard_confirmed":true,"capability_ok":true,"reviewed_sha":"25754670e0bd0fa5b64a9779fd768c024b2bc6a9","base_sha":"09a960555d206db3968b03baaf3348ea64e1da1d","comments":[]}`.
SHAs match dispatch (head `2575467`, base `09a9605`); `guard_confirmed` and
`capability_ok` both literally true; no comments. The reviewer confirmed the
exfiltration rule (recipients derive only from `userEmail` + `_adminEmail`, no
operator-typed path), the resolver's trim/blank/dedup/fallback/empty behavior,
fail-soft send through the catching `SendEmailAsync`, and the guarded attachment
path. Guard proof (in the reviewer's own isolated tree): removing
`.Distinct(StringComparer.OrdinalIgnoreCase)` -> `EmailServiceMessageTraceTests`
FAIL; restore -> PASS. Capability build `dotnet build ExchangeAdminWeb.slnx -c
Release` EXIT=0. The reviewer hit the known `%TEMP%` MSB5021 / solution-restore
environment class and worked around it (`UseSharedCompilation=false`,
`MSBUILDDISABLENODEREUSE=1`, project-level `--no-restore`) -- environment
artifact, not a code defect.

Reviewer: codex CLI (codex-cli 0.145.0, `codex exec`), model
`@azure-openai-eus2-global/gpt-5.5-dzs`, reasoning effort xhigh, standard tier.
