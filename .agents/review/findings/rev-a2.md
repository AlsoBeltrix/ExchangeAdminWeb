# rev-a2: the directory credential cache slides its expiry on every use, so a rotated secret can be served indefinitely

**Severity**: HIGH - availability of every directory-backed operation. `DelineaService` returns the cached credential
before contacting Secret Server and extends the entry's life on each use (`CredentialWindow` from `LastUsed`). After a
password rotation under the same secret ID, a busy instance keeps the old password alive as long as requests arrive
within five minutes of each other; authentication failures downstream cannot evict it (`Forget` is private). Protection
reads and module operations keep failing until a five-minute idle period or a restart. The cache itself stays (decision
2026-11-03, "credential cache stays"; kd-88 addition).
**Status**: Fixed on branch; awaiting review
**Branch**: fix/rev-abc
**Commit**: 8b488bcf
**Lines** (master `145f513f`): `Services/DelineaService.cs:173-174`, `:281-296`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review A, range `8557a15a..5c4c2970`, dispatched 2026-11-06 from master `145f513f`; report `.agents/review/rev-a.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

Absolute expiry measured from retrieval (five minutes, the window the owner accepted), not sliding; and evict the entry
when a consumer reports that the directory rejected the credential (an internal `Invalidate(secretId)` the callers reach on
an authentication failure, followed by one retry with a fresh read). `DelineaCredentialWindowTests.EachUseInsideTheWindow_SlidesItOn`
encodes the sliding behaviour and must change with the fix; add the rotation case.

## As built (fix/rev-abc `8b488bcf`)

Verified real: `Kept` slid `LastUsed` on every use, `Forget` was private.
- `DelineaService`: the kept entry records `Retrieved`; the window is measured from it and a use does not move it.
  `internal void Invalidate(int secretId)` drops the entry (logged, traced). The cache stays.
- `DirectoryCredentialRejection` (new): an ambient watch; the directory reads' choke points report a failed read
  (`PowerShellProtectedGroupDirectory.Run` and its catalog discovery, `ProtectedPrincipalService.InvokeIn` and the group
  check's session, `SelfGrantDirectory.Search`); a rejection is the AD module's credential texts ("rejected the client
  credentials", "unknown user name or bad password", "user name or password is incorrect", "supplied credential is
  invalid", "invalid credentials") or an `InvalidCredentialException` in the chain.
- `ProtectedPrincipalService.WithDirectoryReadCredentialAsync`: the four flows on the directory-read credential (resolution,
  read by SID, Verify's reads, the group check - and since rev-b3 the write-target OU read) run once; when a read inside was
  rejected, the operation's memo (`DirectoryReadOperation.ForgetDirectoryReadSecret`) and the store's entry are dropped and
  the flow runs once more on a fresh fetch. Its second answer stands. Any other failure is not retried.
- Not covered (named): module operations' own credentials through `ModuleCredentialService` - no retry; a rotation there
  is bounded by the absolute five minutes. The self-grant check refuses on a rejected read (fail closed) and is not retried
  itself; in a single change it shares the operation's memo, which the target check's retry has refreshed.

## Guard proof

| Test | Fails on the old code / mutation |
| --- | --- |
| `DelineaCredentialWindowTests.AUseInsideTheWindow_DoesNotSlideIt_TheWindowRunsFromTheRetrieval` (replaces `EachUseInsideTheWindow_SlidesItOn`) | yes (mutation: re-keep on use) |
| `DelineaCredentialWindowTests.ARotatedSecret_IsServedFreshWithinTheWindow_HoweverOftenItIsUsed` | yes |
| `DelineaCredentialWindowTests.AnInvalidatedCredential_IsFetchedAfresh_InsideTheWindow` | yes (mutation: Invalidate no-op) |
| `DirectoryCredentialRejectionTests.ARejectedCredential_IsDroppedAndFetchedAfresh_AndTheCheckAnswersOnTheRetry` | yes (mutation: no retry) |
| `DirectoryCredentialRejectionTests.ACredentialStillRejectedOnTheRetry_FailsClosed_AfterOneRetryOnly` | yes |
| `DirectoryCredentialRejectionTests.AReadThatFailsForAnyOtherReason_IsNotRetried_AndKeepsTheCredential` | passes on both (guards over-application) |
| `DirectoryCredentialRejectionTests.ARejection_IsTheDirectorysRefusalOfTheCredential_AndNothingElse` (7 rows) | new classifier |

Assumption not verified live: the exact text the AD module returns for a rotated password through ADWS.
