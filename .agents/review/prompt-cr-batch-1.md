# Defect review request: Conference Rooms step naming on the room-list paths, refusal logging, credential-step pin (aud-a2s, aud-a2t, aud-a2u)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `13553dce` or later.
- Three merged branches, all in `Services/ConferenceRoomService.cs` and its tests:
  - aud-a2s: `1e52277d` (plan), `cf9ae39e` (fix), forked from `225e61fc`, merged at `3ff4a6d4`.
  - aud-a2t: `95bfa5ca` (plan), `78889b58` (fix), forked from `53efd9d1`, merged at `932fd294`.
  - aud-a2u: `8233d1ff` (plan), `351f887b` (fix), forked from `af01cd37`, merged at `bf7ba5b4`.
  Review `git show cf9ae39e`, `git show 78889b58`, `git show 351f887b`, and read the COMBINED
  `Services/ConferenceRoomService.cs` on master (`PreflightRoomListAsync`, `AddToRoomListViaAdAsync`,
  `ResolveAdGroupGuid` with its `ref step`, `PreflightSyncedAttributesViaAdAsync`,
  `SetSyncedAttributesViaAdAsync`, `ConfirmDirectoryObject` and its four callers, every `LogWarning`
  added by a2t/a2u).
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a2s.md`, `aud-a2t.md`, `aud-a2u.md`; parents `aud-a2r.md`,
  `aud-a2q.md`, `aud-a2p.md`, `aud-a2n.md`. ALREADY admitted and out of scope: `aud-a2v` (the row's
  own early refusals unlogged; a credential failure names no room; an operator message with a .NET
  parameter suffix; the two preflights' refusal wording differs). Do not re-raise it. Repo rules:
  `AGENTS.md`; `.agents/repo-guidance.md` Architectural Invariant 7 and "Known Failure Classes"
  (1, 2, 3); `docs/ProjectConstitution.md`.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat bf7ba5b4`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. a2s: the `step` tracked by `ref` through `ResolveAdGroupGuid` (a step
label stale after an await or after a refusal return; the catalog and read-back steps that catch
their own errors - do they leave `step` pointing at themselves so a LATER throw is misattributed);
the import catches folded back into the step catch (aud-a2r's import test still meaningful). a2t and
a2u: each refusal logged exactly once - any path where a helper's refusal is logged by two callers
or by none (count the refusal sites against the log lines; the add's read-back is also the
membership read - logged twice?); log lines naming the room and list but never a secret or
credential; the refusals deliberately not logged (the write's unreachable branches; the missing
credential logged by Delinea; the created-list no-Guid throw) - are the reasons sound; the message
rebuilt without " Nothing was written." then returned with it - byte-identical as claimed. Tests
passing for the wrong reason: the capturing logger's matching; the `IncompleteCredentialFor` seam;
the harness knobs that fail only after a `Set-Place` is recorded.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
