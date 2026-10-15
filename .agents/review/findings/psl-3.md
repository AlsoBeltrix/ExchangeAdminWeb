# psl-3 - DHCP discovery errors survive into a later mutation report

Status: Open follow-up; outside both approved remediation and proposed replay repair.
Severity: MEDIUM. Native Astra read-only audit, 2026-10-15, at
`b936c604482447c36f93010b6fae135dd71d6df9`.

DhcpAuthorizationService.DiscoverDomainController (line 192) clears commands after a failed
discovery attempt but retains errors while trying a fallback. A later authorization or
deauthorization error consumer (102/158) can include those stale discovery errors.
This is retained diagnostic state, not command replay or proof that a successful write fails.

Evidence is control flow only; `../powershell-replay-audit.md` owns the overall inventory.
Before repair, exercise failed discovery, successful fallback and a later distinct failure
with harmless SDK commands; prove only the last operation's diagnostics belong in its report.
Preserve diagnostic consumption before cleanup. No code/tests changed or live service ran.
Next proposed action: scope this independently after the current authorized work.
