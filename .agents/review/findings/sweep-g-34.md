# sweep-g-34: the serving-pool check refused every stop on any IIS oddity anywhere on the server (sweep item 34)

**Severity**: MEDIUM - sweep item 34: `Assert-AppPoolServesPath` (`tools/AppPoolControl.psm1`) read every site, application and
virtual directory on the server and refused on any oddity anywhere - another team's application with a missing root virtual
directory, a site name holding both quote characters, a single read error - so every deploy, promotion and install stop was
refused (the cdt-61 class).
**Status**: Merged 59522ca6; reviewed by codex review F 2026-11-09; codex review F 2026-11-09 found a remainder (rev-f2)
**Branch**: `fix/sweep-g` (from master `145f513f`)
**Commit**: `e02c4c0c` (`fix(sweep-g, item 34): ...`) | **Reviewer**: implementer (candidate)

## Approach (as built)

- The default probe reads each site, its application list and each application's virtual directories in their own try. A read
  that fails yields an issue node carrying the path and pool already known for it, and the probe goes on.
- The function refuses, as before, when an issue node could reach the folder - its own path at, inside or above the folder by
  lexical or final path, a path that cannot be placed, or the named pool - and otherwise names it in a warning saying its unread
  children were not seen.
- `Get-Website` failing still refuses. Another node at the folder under another pool still refuses (cdt-61, aud-a38c).
- `Get-WebApplication` in this probe carries `-ErrorAction Stop`. README deploy section.

## Where the code goes beside the fix shape

- The sweep's shape was "read only the IIS nodes whose physical path could reach this folder". The probe still lists every node
  (it must, to know each one's path), but only a node that could reach the folder can refuse; the rest warn. Same outcome.

## Assumptions not verified live

- The read failures are modelled by stand-ins with the Windows PowerShell 5.1 WebAdministration shapes (cdt-61's record); a real
  broken site has not been read. The owner's check: a plan on the host with every other site healthy shows no warning.

## Guard proof (`tests/ps/AppPoolControl.Tests.ps1`, "sweep item 34")

Re-run by the closing agent: `tools/AppPoolControl.psm1` restored to `e02c4c0c^` - 1 passed, 9 failed of 10; restored, all pass.

| Mutation | Failing tests |
|---|---|
| Module at `e02c4c0c^` (any oddity refuses) | the 4 "passes, and warns" rows, the 4 "refuses when the unreadable node ..." rows, cdt-61 with a broken site elsewhere |

## Deploy-time checks

- A plan of each deploy on the host: no warning about unreadable nodes unless IIS really has one; if it does, it names the node
  and the run is not refused.
