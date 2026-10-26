# Defect review request: the 2026-10-26 evening consolidation - fourteen findings merged from six branches after the second interruption

You are reviewing, not implementing. Do not modify any file.

## Why this review exists

Six implementation agents were cut off mid-slice a second time (a gateway key usage cap). The supervisor merged every
slice each had COMMITTED, resolved the conflicts by hand, and re-ran every gate green. None of this range has had a
codex review of its own. As in the first consolidation, the highest-value target is INTERACTION between slices written
in parallel against different bases, and the supervisor's own conflict resolutions.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `91ebed48` or later.
- The consolidation is `162cb44d..b482db4c`. Review `git diff 162cb44d b482db4c -- . ':!.agents'` and read the COMBINED
  files. What each merge carried:
  - `b8505b4c` kd-69: a cancel arriving during the configuration read ends the credential lookup too
    (`Services/ModuleCredentialService.cs`).
  - `2782fb17` kd-62, kd-63, kd-64, kd-65: the removal's reason comes back with its bool and every path audits; the
    fallback category is offered in the filter; the ambiguity case proves its command ran; Event Log 1.5.0
    (`Components/Pages/AdminBulkJobs.razor`, `Components/Pages/AdminEventLog.razor`, the ValidateExists tests).
  - `ea4c8c94` kd-66: the Room Finder preview says the room list does not exist and the row will be refused.
  - `78bcde04` kd-45, kd-46, kd-48, kd-49: ticket presence means at least one VISIBLE character - no control, no Unicode
    format character, no unpaired surrogate (`Services/TicketPresence.cs`); Risky Users, Intune Devices, Message Trace,
    Migration and Emergency Disable judge presence by it; MFA Reset reads its inputs at the click; DHCP deauthorize keeps
    its ticket and says nothing was attempted.
  - `aa932d14` kd-18 and kd-40 for CALENDAR Permissions, kd-43, kd-44: both permission pages now require a ticket and
    audit their refusals; a cloud Calendar Set whose session drops is not followed by an Add on it; an unconfirmed
    change tells the affected user and owner (`Components/Pages/CalendarPermissions.razor`,
    `Services/CalendarPermissionService.cs`, `Services/PermissionChangeNotifier.cs`).
  - `b482db4c` cdt-57: the installer refuses to deploy over an application whose folder has no appsettings.json
    (`tools/Install-ExchangeAdminWeb.ps1`).
- **The supervisor's four judged resolutions, all in scope:**
  1. `Services/TicketPresence.cs` and its tests: two branches carried different versions of the shared rule. Master's
     kd-48 version (visible-character test) was kept and the aud-a70 branch's kd-24 version (control characters only)
     discarded. Check every caller against the rule that survived.
  2. `Modules/ModuleCatalog.cs`: five module entries where parallel branches each bumped the same module. Both note sets
     were kept and the versions renumbered - Migration 1.26.0, Group Management 2.16.0, Conference Rooms 2.19.0,
     Emergency Disable 1.7.0, DHCP Authorization 1.9.0.
  3. `ExchangeAdminWeb.Tests/EmergencyDisableServiceTests.cs`: its catalog assertion was reconciled to 1.7.0 with a note.
  4. Event Log 1.5.0 keeping master's aud-a64 note alongside the new filter note.
- You may read any file and run read-only git commands. One command per call. No writes, no builds, no network.
- Out of scope, do not re-raise: anything already an open row in `.agents/review/index.md` (read it - `cons-7`, `kd-68`,
  `kd-67`, `kd-50`, `kd-51`, `kd-57`, `kd-58`, `kd-59`, `kd-60`, `cdt-58`, `kd-38`, `kd-39`, `kd-41` are all open and
  assigned); aud-a67 (12); the deliberate removal of room-list creation.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat b482db4c`. Report whether both succeeded.

## Mandate

1. **The surviving ticket rule.** It now refuses any string without a visible character, including combining marks,
   unassigned and private-use code points. Name any legitimate ticket format that is now refused, any caller that
   validates differently from the others, and any test that was written against the control-characters-only rule and
   still passes because its fixture happens to satisfy both.
2. **The two permission pages, now symmetric.** Mailbox landed in the morning consolidation and Calendar this evening.
   Compare them: the same ticket gate, the same refusal audit shape, the same Undetermined handling, the same
   notification behaviour - name any divergence, and anything in the shared services called with different expectations.
3. **kd-44's new affected-user notification.** It fires when an outcome could not be confirmed. Check it cannot fire for
   a write that certainly did nothing, that it says nothing a recipient should not be told, and that it is sent once.
4. **kd-62's reason-with-bool.** The service now returns the reason alongside the bool. Check every caller of that
   method, that no path writes two rows or none, and that a reason the service cannot determine is still audited.
5. **The catalog.** Every entry has exactly one `Version`; each note's number matches the `Version` below it; no note
   was lost in the renumbering; and no module whose behaviour changed in this range is still on its old number.
6. **Tests that now pass or fail for the wrong reason** - the same class as the kd-7/kd-52 collision the first
   consolidation produced.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.