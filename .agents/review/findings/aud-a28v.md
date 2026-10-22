# aud-a28v: A protected-principals save whose compare waits out the store's busy timeout fails whole with a generic "Failed to save"

**Severity**: LOW - operator guidance. Since aud-a28r the save compares the Secret ID under the
module-config store's write lock before any write. A database whose lock is held for more than the
5-second busy timeout now fails the whole save up front (correct: nothing written), but the operator
sees a generic "Failed to save" with no hint that the store was busy and a retry would succeed.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28r/a26p agent, 2026-10-22, candidate 5 of its hand-back)

## Evidence

At `25071281`: `Services/ModuleConfigService.cs` `CompareChangedValues` (the write lock it takes);
`Services/ProtectedPrincipalService.cs` `CompareDirectoryReadSecretId`; `Components/Pages/AdminSettings.razor`
(the save's catch and its message; NotYetConverted for `ClickGateRegistry`).

## Approach (to be written by the implementing agent before code)

- The compare's busy failure is distinguishable (the SQLite busy result or a typed outcome) and the page's
  message names it: "the settings store was busy; nothing was written; try again".
- One refusal record as today. Test through the settings harness with a held write lock; mutation at the
  call site: the generic message again.

## Guard proof

Pending.
