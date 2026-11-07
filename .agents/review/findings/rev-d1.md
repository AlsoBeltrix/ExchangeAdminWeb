# rev-d1: the attribute editor pins a save to whichever DC answered RootDSE, which can be a read-only DC, so every save then fails

**Severity**: MEDIUM - fail-closed outage of a module. sweep-f item 27 pins re-read, write and read-back to the
`dnsHostName` that answered `Get-ADRootDSE` against the domain and returns it unconditionally. Domain discovery can
answer from a read-only domain controller: its RootDSE and reads succeed, the fallback never runs, and `Set-ADUser
-Server <rodc>` fails on every save.
**Status**: Fixed on branch; awaiting review
**Branch**: fix/rev-abc
**Commit**: 86ee0d0b
**Lines** (master `dcf13a16`): `Services/ADAttributeEditorService.cs:1824-1845`, `:1502`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review D, range `fe3b86b8..de53b714`, dispatched 2026-11-06 from master `dcf13a16`; report `.agents/review/rev-d.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

Discover a WRITABLE DC at runtime (`Get-ADDomainController -Discover -Writable`, or RootDSE's `isSynchronized`/`supportedCapabilities`
with the RODC check, whichever the directory layer already has a shape for), then read that server's RootDSE and keep the pin.
Never a configured name. `ADAttributeEditorPinAndSyntaxTests` needs the RODC case (RootDSE succeeds, write refused).

## As built (fix/rev-abc `86ee0d0b`)

Verified real: `PinDomainController` pinned the `dnsHostName` of whatever DC answered `Get-ADRootDSE -Server <domain>`.
- `DiscoverWritableDomainController`: `Get-ADDomainController -Discover -Writable -Service ADWS [-DomainName <the DN's
  domain>]` (the DC locator; no credential, no configured name). The RootDSE is then read on the located host with the save's
  credential, and a RootDSE whose `supportedCapabilities` carries `1.2.840.113556.1.4.1920`
  (LDAP_CAP_ACTIVE_DIRECTORY_PARTIAL_SECRETS_OID: a read-only DC) is never pinned. A discovery that fails falls back to the
  domain's own RootDSE under the same test; anything not pinned stays on the domain name, logged (the old fallback).
- Stand-in (`AttributeEditorHarness`): the locator, `ReadOnlyDcs` (read-only capability, Set-ADUser refused).
- README attribute-editor paragraph. ADAttributeEditor 1.7.9.

## Guard proof

| Test | Fails on the old code (service stashed) |
| --- | --- |
| `ADAttributeEditorPinAndSyntaxTests.TheChain_IsPinnedToAWritableDc_NotTheReadOnlyOneTheDomainNameAnswersFrom` (RootDSE succeeds from an RODC, its write refused) | yes |
| `ADAttributeEditorPinAndSyntaxTests.AReadOnlyDcThatAnswersTheRootDse_IsNeverPinned_WhenNoWritableDcIsDiscovered` | yes |
| `ADAttributeEditorPinAndSyntaxTests.TheChain_IsPinnedToTheDcThatAnsweredTheRootDse_SoALaggingDomainCannotFailALandedSave` (now asserts the locator) | yes |

Assumptions not verified live: `Get-ADDomainController -Discover -Writable -Service ADWS` from the app pool's identity, and
an RODC's RootDSE carrying the partial-secrets capability through ADWS.
