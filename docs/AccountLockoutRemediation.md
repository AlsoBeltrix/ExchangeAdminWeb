# Account Lockout Remediation Module

## Purpose

Account Lockout Remediation helps identity and infrastructure administrators find
interactive-session lockout sources and log accounts off from stale Windows sessions.
It is designed for cases where a rotated admin credential keeps being presented by
an active or disconnected desktop/server session.

## Workflows

1. **Find lockout sources** reads recent Security event 4740 records from the PDC
   Emulator or explicitly named domain controllers, then reports the locked account
   and caller computer from each event.
2. **Log off from lockout sources** contacts only implicated source machines and logs
   off sessions for the implicated account on that machine.
3. **Scoped logoff sweep** enumerates enabled AD computers from a supplied search base
   and optional extra computer list, then logs target users off from matching sessions.

All execution supports dry-run mode. Real logoff requires a ticket number and typed
confirmation.

## Required Permissions

- Module access policy: `AccountLockoutRemediation`.
- Mutating policy: `AccountLockoutRemediationLogoff`.
- The module is disabled by default and both permissions fail closed.

## Required Configuration

- `DelineaSecretId`: module-specific AD credential.
- `DefaultThrottleLimit`: optional default WinRM throttle, default `32`.
- `MaxSweepTargets`: optional maximum scoped sweep size, default `10000`; `0` disables
  the module-level limit.

## Credential Requirements

The Delinea credential must be able to:

- Import and use the Active Directory PowerShell module.
- Discover the PDC Emulator and enumerate enabled computers in approved search bases.
- Read Security event 4740 records on the PDC Emulator or supplied domain controllers.
- Connect to target computers over WinRM.
- Query sessions with `quser.exe`.
- Log off sessions with `logoff.exe`.

## Protected Principals

Before any logoff execution, each target account is resolved through
`ProtectedPrincipalService`. Ambiguous, unavailable, check-failed, or protected targets
are skipped fail-closed. Resolved targets are bound to object GUID when available and
re-resolved immediately before execution.

## Auditing And Tracing

The module writes audit records for source lookup, dry-run, successful logoff, failed
logoff, denied logoff, protected-principal block, and partial success. Operation traces
record credential lookup, AD discovery, event-log read, WinRM query, WinRM logoff, and
summary stages. Trace details are sanitized and do not include credential material.

## Limitations

Blank caller-computer events are reported but cannot be fixed by session logoff. Those
usually require remediation of services, scheduled tasks, application pools, mapped
drives, or saved credentials on the source system.

## Manual Validation

1. Configure the module Delinea secret and section access groups in dev.
2. Run source discovery for a known recent lockout and confirm event 4740 parsing.
3. Run dry-run logoff against one known reachable test workstation.
4. Execute logoff against a test account with an active disconnected session.
5. Confirm audit and operation trace records include ticket, actor, target, result, and
   partial failures without raw backend output.
