# Defect review request: Conference Rooms row-refusal and credential logging, country message, refusal wording (aud-a2v)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `e0feaa39` or later.
- The change is the branch merged at `6ac190f7`: `1d6d2c2d` (plan), `937c2599` (fix, tests, comments, record), forked
  from `dc0555e2`. Review `git diff dc0555e2 937c2599`, and read the COMBINED `Services/ConferenceRoomService.cs`
  (`SetRoomMetadataAndListAsync`, the four credential lookups, `BuildCountryAttributes`, the City/State/Country
  preflight, the room-list preflight, `ValidateRoomRecipientAsync`, `SetRoomTypeAsync` for contrast) and
  `ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs` (the `Service` seam, `NoCredentialFor`) on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a2v.md`; parents `aud-a2t.md`, `aud-a2u.md`, `aud-a2j.md`, `aud-a2k.md`,
  `aud-a2l.md`. ALREADY admitted and out of scope: `aud-a2w` (the Room Type path logs none of its refusals; a
  Finder-row room read that throws is not caught in the service; the row's time-zone and recipient refusals do not
  end "Nothing was written."), `aud-a2x` (every credential refusal says "set the AD Delinea Secret ID" even when the
  ID is set and the lookup failed). Do not re-raise them. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md`
  "Known Failure Classes" (1, 2, 3) and Architectural Invariant 7 (environment neutrality);
  `docs/ProjectConstitution.md`.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 6ac190f7`. Report whether both succeeded.

## Mandate

Hunt for observable defects. The new Warning logs: are any emitted twice for one refusal (the row and a shared
helper both logging); does any log line carry a value that should not be logged (a secret field, a credential);
is the log at the row's return reachable on every refusal the record lists. The credential lines: the four
purposes named correctly; a refusal path where the credential is missing but the lookup is not reached. The
country message: `ArgumentException` without `nameof` - any catch that matched on `ParamName` or on the suffix
text; the "has no display name" throw's reachability claim (Western Sahara on .NET 10.0.12) - is it a runtime fact
the code now depends on, or a test fixture only (Invariant 7). The suffix rule: every City/State/Country early
refusal now ends " Nothing was written." - does any of those refusals occur AFTER a write began (which would make
the suffix false); does the room-list preflight still end its refusals the same way. The changed aud-a2u
assertion ("append nothing" dropped for two cases): is anything aud-a2u guarded now unguarded. Tests passing for
the wrong reason: the `NoCredentialFor` seam recording purposes; the `Service` stand-in's refusal paths vs the
production path.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
