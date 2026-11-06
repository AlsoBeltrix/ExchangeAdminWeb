# rev-d1: the attribute editor pins a save to whichever DC answered RootDSE, which can be a read-only DC, so every save then fails

**Severity**: MEDIUM - fail-closed outage of a module. sweep-f item 27 pins re-read, write and read-back to the
`dnsHostName` that answered `Get-ADRootDSE` against the domain and returns it unconditionally. Domain discovery can
answer from a read-only domain controller: its RootDSE and reads succeed, the fallback never runs, and `Set-ADUser
-Server <rodc>` fails on every save.
**Status**: Admitted; awaiting fix on branch
**Lines** (master `dcf13a16`): `Services/ADAttributeEditorService.cs:1824-1845`, `:1502`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review D, range `fe3b86b8..de53b714`, dispatched 2026-11-06 from master `dcf13a16`; report `.agents/review/rev-d.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

Discover a WRITABLE DC at runtime (`Get-ADDomainController -Discover -Writable`, or RootDSE's `isSynchronized`/`supportedCapabilities`
with the RODC check, whichever the directory layer already has a shape for), then read that server's RootDSE and keep the pin.
Never a configured name. `ADAttributeEditorPinAndSyntaxTests` needs the RODC case (RootDSE succeeds, write refused).
