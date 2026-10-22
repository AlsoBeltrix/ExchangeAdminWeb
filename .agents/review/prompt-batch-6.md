# Defect review request: room-list membership reads (aud-a2h, aud-a2i) and two deploy fixes (cdt-5, cdt-6)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `bc24d7d1` or later.
- Three merged changes:
  - aud-a2h: `fdf29235`, merged at `a65b0ee2`. Review `git show fdf29235`.
  - aud-a2i: `294e9d46`, merged at `ca54f03e`. Review `git show 294e9d46`.
  - cdt-5 `426e1371` and cdt-6 `2f6e629a` (direct to master). Review `git show 426e1371` and
    `git show 2f6e629a`, and read `deploy.ps1` on master as it stands.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a2h.md`, `aud-a2i.md`, `cdt-5.md`, `cdt-6.md`; parents
  `aud-a2f.md`, `aud-a2g.md`. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md`
  "Architectural Invariants" (3, 4, 5, 7) and "Known Failure Classes" (1, 2, 3);
  `docs/ProjectConstitution.md`; `docs/ConferenceRooms-OnPremRoomListAdd-Plan.md`.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat ca54f03e`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. In a2h: any `Get-DistributionGroupMember` on the room-list path
still without `-ResultSize Unlimited`, and whether the harness would let the parameter be
dropped without a test failing. In a2i: the on-prem membership read via `Get-ADGroup
-Properties member` with `-Server` from the group DN - is the member comparison by identity
(DN/objectGUID/SID) and never display name; does it decide before any Exchange or AD write;
cross-domain members and a member whose DN is in another domain than the group; an empty or
missing `member` attribute versus a read failure (fail closed); any surviving
`Get-ADGroupMember` call anywhere in `Services/` and whether the tripwire test would catch a
new one; environment facts baked into the logic (Invariant 7). In cdt-5 and cdt-6: under
PowerShell 5.1 and 7, is `$runtimeConfigDir` set on every path before the jobs check; does the
outer try/finally remove staging on every failure path and never remove the live publish
folder; does the finally run after a `Write-Fail` throw inside the backup step; any state
write (manifest, marker) reachable on a failure path; `-PlanOnly` behaviour of both; and
tests that pass for the wrong reason.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
