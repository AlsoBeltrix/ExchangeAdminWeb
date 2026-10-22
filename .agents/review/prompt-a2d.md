# Defect review request: aud-a2d - every Exchange write receives the validated identity

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`.
- The change is `git diff 3562cf08 7ae55b9a` (commits `3dcb15e4`, `fb449b5e`, `6241bb20`,
  `73787287`, `404c856a`, `32a09ace`, `7ae55b9a`), merged into master after `2478ede`.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Record: `.agents/review/findings/aud-a2d.md` (plan, the write call-site table before -> after,
  what was deliberately left on the typed string, 74 mutations, assumptions that could not be
  checked without live calls). Parents: `aud-a2b.md`, `aud-a2c.md`. Repo rules: `AGENTS.md`;
  `.agents/repo-guidance.md` "Known Failure Classes" (2: success aggregation; 3: fail-closed
  authorization) and the ClickGateRegistry rule under Verification; `docs/ProjectConstitution.md`
  Protected Principals and audit.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 404c856a`. Report whether
both succeeded.

## Mandate

HIGH-stakes authorization plumbing across four modules. Hunt for observable defects: any
Exchange or AD write in Mailbox Permissions, Calendar Permissions, Out of Office or Conference
Rooms (pages, services, CSV/bulk paths, job processors) that still receives the operator's
typed string or a re-resolution of it as the TARGET; a `ValidatedMailbox` that can be built
without a check; the read-back-before-write (can it pass for a different mailbox; what if
Exchange returns two); the central-vs-legacy mismatch rule; `TargetValidation` allow paths that
lack a mailbox; the Conference Rooms gate's NotFound handling and the AD objectGUID binding for
City/State/Country and room-list writes; cloud-only rooms; audit rows (`writtenIdentity`,
`writtenDirectoryObject`) versus what was actually written; the pages' line counts (the
ClickGateRegistry keys are line-based: confirm no registered page changed length);
behaviour changes the record lists (judge whether any is wrong rather than merely strict);
the GUID-as-identity assumptions for `Add-/Remove-RecipientPermission`, `Set-Place`,
`Add-DistributionGroupMember -Member`, piped `Add-/Remove-ADPermission` (say what you know
about each); and tests that pass for the wrong reason.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
