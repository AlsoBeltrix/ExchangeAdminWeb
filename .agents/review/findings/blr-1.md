# blr-1: a pasted 48-digit recovery password is written to the audit log as the search target

**Severity**: HIGH — the audit log is a durable, separately-stored, widely-readable
record, and this writes a full disk-decryption key into it. The module's own
documented rule is that recovery keys never reach an audit record, a log line, or
an error message.
**Status**: Verified
**Branch**: — (default-branch mode)
**Commit**: `bd85e2e`

## Evidence

- `Components/Pages/BitLockerRecovery.razor:270` — `target` is set to the **raw
  contents of the recovery-screen box**, whatever the operator pasted.
- `Components/Pages/BitLockerRecovery.razor:297` — that raw string is passed as the
  `target` argument to `Audit.LogLookupAction`.
- `Services/AuditService.cs:206` — `LogLookupAction` writes `target` verbatim into
  the audit event, which is then persisted.

Triggering condition: the module explicitly invites this input. `docs/BitLockerRecovery.md`
tells operators they may search by "48-digit recovery key", the page placeholder reads
"Key ID or 48-digit recovery key", and `BitLockerRecoveryIdentifierParser` has a
dedicated `RecoveryPassword` branch. So the failing path is the documented happy path,
not an edge case — an operator with the caller reading their screen aloud pastes the
recovery key to find the machine.

## Predicted observable failure

Search by pasted recovery password, then read the audit log: the full 48-digit key
is present in cleartext in the `target` field. Anyone entitled to read audit records
— a broader group than those entitled to reveal a key — can extract a working
disk-decryption key without ever triggering the audited `RevealRecoveryKey` event
that exists to record exactly that disclosure.

The irony is the point: the reveal path was carefully built to keep the key out of
the audit record, and the search path writes it there in cleartext.

## What

The page audits the operator's raw input as the lookup target. For a computer-name
search or a key ID that is correct and useful. For a recovery password it silently
converts the audit log into a key store.

## Approach

Fix at the page, not in `AuditService`: the host audit signature is shared by many
modules and is not this module's to change (the same reasoning the integration plan
applied to the degraded-search note).

`BitLockerRecovery.razor` now parses the identifier **before** auditing, via the
existing `BitLockerRecoveryIdentifierParser`, and substitutes a non-secret descriptor
when the parsed kind is `RecoveryPassword`. A key ID is still audited verbatim — it
is an identifier, not a secret, and it is what makes the record useful. The parse is
the same one the service performs, so the two cannot disagree about what was entered.

`AuditSearchTarget` is `internal static` so the redaction is testable without a
component host (this repo has no bUnit harness; the same device is used by
`MessageTraceExportListing` and `ADIdentityAutocomplete`).

## Files changed

- `Components/Pages/BitLockerRecovery.razor:270-284` — audit target derived through
  `AuditSearchTarget` instead of the raw box contents.
- `Components/Pages/BitLockerRecovery.razor:349-372` — new `AuditSearchTarget` helper.
- `ExchangeAdminWeb.Tests/BitLockerRecoveryTests.cs` — 6 tests.

## Guard proof

`ExchangeAdminWeb.Tests/BitLockerRecoveryTests.cs::AuditTarget_RedactsAPastedRecoveryPassword`
and the five beside it. Reverting `AuditSearchTarget` to the raw `target` makes the
redaction tests FAIL; restoring makes them PASS.

`AuditTarget_RedactsARecoveryPasswordEvenWithSurroundingText` is the load-bearing one:
the parser matches a recovery password anywhere inside the input, so redacting only
when the whole box is 48 digits would leak "Recovery key: 111111-...-888888".

## Coder dispute (if any)

None. Verified against the code before fixing.

## Known gaps

A malformed paste that the parser does **not** recognise as a recovery password
(e.g. wrong group count) is still audited verbatim. That is deliberate: an
unparseable string is not a key, the audit record would otherwise lose all
diagnostic value, and the service refuses such input anyway. The reviewer is invited
to grade this explicitly.

## Reviewer comments

To be filled by the verification dispatch.
