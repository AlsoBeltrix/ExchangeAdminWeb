# psl-2 - licensing rows can report an earlier row's PowerShell error

Status: Open follow-up; outside both approved remediation and proposed replay repair.
Severity: MEDIUM. Native Astra read-only audit, 2026-10-15, at
`b936c604482447c36f93010b6fae135dd71d6df9`.

LicensingUpdatesService.ApplyChanges (line 368) clears commands after a row failure but
retains error records before continuing. ResolveUsers (265) does likewise. If a later write
returns non-terminating errors, its consumer at line 359 can select an earlier row's error.
This is diagnostic attribution across rows, not command replay. The audit does not establish
that a subsequent successful write is reported as failed.

Evidence is control flow only; `../powershell-replay-audit.md` owns the overall inventory.
Before repair, add an actual-SDK multi-row failure reproduction with distinct error messages,
prove the later result carries the wrong row's message, and define error cleanup after each
operation has consumed its own diagnostics. No code or tests changed; no live service ran.
Next proposed action: scope this independently after the current authorized work.
