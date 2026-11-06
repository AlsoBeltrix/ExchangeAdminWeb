# rev-a2: the directory credential cache slides its expiry on every use, so a rotated secret can be served indefinitely

**Severity**: HIGH - availability of every directory-backed operation. `DelineaService` returns the cached credential
before contacting Secret Server and extends the entry's life on each use (`CredentialWindow` from `LastUsed`). After a
password rotation under the same secret ID, a busy instance keeps the old password alive as long as requests arrive
within five minutes of each other; authentication failures downstream cannot evict it (`Forget` is private). Protection
reads and module operations keep failing until a five-minute idle period or a restart. The cache itself stays (decision
2026-11-03, "credential cache stays"; kd-88 addition).
**Status**: Admitted; awaiting fix on branch
**Lines** (master `145f513f`): `Services/DelineaService.cs:173-174`, `:281-296`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review A, range `8557a15a..5c4c2970`, dispatched 2026-11-06 from master `145f513f`; report `.agents/review/rev-a.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

Absolute expiry measured from retrieval (five minutes, the window the owner accepted), not sliding; and evict the entry
when a consumer reports that the directory rejected the credential (an internal `Invalidate(secretId)` the callers reach on
an authentication failure, followed by one retry with a fresh read). `DelineaCredentialWindowTests.EachUseInsideTheWindow_SlidesItOn`
encodes the sliding behaviour and must change with the fix; add the rotation case.
