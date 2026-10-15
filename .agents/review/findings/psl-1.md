# psl-1 - an optional Exchange command survives failure and replays

Status: Open; implementation proposal awaiting owner approval.
Severity: HIGH. Native Astra read-only audit, 2026-10-15, at
`b936c604482447c36f93010b6fae135dd71d6df9`.

## Evidence

ExchangeServiceBase.InvokeOptional (lines 268-273) calls ps.Invoke before clearing commands
and errors. A terminating exception skips both clears. ConferenceRoomService.SetCalendarPermission
uses it for Add-MailboxFolderPermission (line 1208), catches failure (1223), and returns to
SetCalendarPermissionsForType's next permission (1149 onward), reusing the same object.
The next invocation can execute the failed mutation again, including when its first call
changed permissions before throwing. The last permission can leak into timezone commands
(line 832). This is a demonstrated control-flow path, not an executed fault-injection test.

The same root also affects SetRoomTypeAsync's Get-Recipient (705): catch at 709 swallows
failure, then permission removal (724) or calendar processing (768) reuses the instance.
That replays a failed read inside later work; it is not a second queued-mutation finding.

The complete ownership inventory and limits are in `../powershell-replay-audit.md`.
The twenty existing remediation findings remain independently authorized; this extension
is not authorized by their approval.

## Proposed repair and proof

`docs/PowerShellCommandLifecycle-Plan.md` owns scope, preserved error/tracker/retry contracts,
harmless SDK tests, versions and gates. Repair the one shared helper; do not mechanically
convert disposal-safe sites. Exercise the actual Conference Rooms sequence, including a
simulated mutation effect followed by a throw, and prove the old helper produces duplicate
calls. Preserve the original exception and RunAsync diagnostic context while cleaning up.

No code changed, no reproduction executed, no independent implementation review owed yet.
Next: owner approval of the concrete separate plan, then one Sol implementation slice and
independent Astra workflow review at the next safe committed boundary.
