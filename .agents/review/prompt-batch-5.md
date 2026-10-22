# Defect review request: Conference Rooms follow-ons (aud-a2f, aud-a2g) and the config-path rule (cdt-35, cdt-37)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `ed18c99` or later.
- Two merged branches:
  - Conference Rooms: `587d7d5` (aud-a2f), `f7a364e` (aud-a2g), `f07a6ef` (a2f repair), forked from `86300ae6`, merged at `bdf123f`. Review `git diff 86300ae6 f07a6ef0`.
  - Path rule: `c73e51c` (cdt-35), `cc998bb` (cdt-37), `9c35489` (record fix), forked from `0d040ed2`, merged at `2392030`. Review `git diff 0d040ed2 9c35489b`.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a2f.md` (including the repair and why), `aud-a2g.md`,
  `cdt-35.md`, `cdt-37.md`; parents `aud-a2d.md`, `cdt-30.md`. Repo rules: `AGENTS.md`;
  `.agents/repo-guidance.md` "Architectural Invariants" (1, 2, 6) and "Known Failure Classes"
  (1, 2); `docs/ProjectConstitution.md` audit rules; `docs/ConferenceRooms-OnPremRoomListAdd-Plan.md`.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat bdf123f`. Report whether
both succeeded.

## Mandate

Hunt for observable defects: in a2f, any building row that still writes before the preflight
decides, a preflight that refuses a legitimate row (the repair's already-a-member shortcut:
can it be fooled; the `IsDirSynced` read on the list), the fallback gate, per-row outcome
aggregation; in a2g, an audit row that names a directory object no write produced or omits
one a write did produce, the page and processor paths, exception paths; in cdt-35, any
path spelling the app accepts that the module refuses or the reverse (the two must agree
exactly), the normalisation, an existing deployment whose configured path was accepted before
and is refused now (is that a safe failure); in cdt-37, the installer's call into the module
and its plan-mode output; and tests that pass for the wrong reason.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
