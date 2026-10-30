# cdt-62: the installer casts the pool's identityType to an integer, the exact failure of the 2026-09-08 cutover

**Severity**: HIGH when run - sweep item 11. `tools/Install-ExchangeAdminWeb.ps1` about 920-921: `[int]$existingProcessModel.identityType`;
the cutover failed on this cast ("Cannot convert value SpecificUser to type System.Int32", commit 7ba6b255) while deploy.ps1
matches the text (about 502-504). Not on the pipeline's path; any elevated plan or apply over an existing pool throws before
changing anything. No test covers it.
**Status**: Open - admitted 2026-10-30 from the sweep
**Branch**: - | **Commit**: - | **Reviewer**: implementer (candidate)

## Approach

- Match the text as deploy.ps1 does, then map to 3 or 4; Pester with a stand-in returning "SpecificUser"; mutation: the cast
  restored. Parse under 5.1.