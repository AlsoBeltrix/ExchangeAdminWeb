# cdt-61: the pre-stop serving-pool check reads an IIS property Windows PowerShell 5.1 does not expose, so every deploy refuses before the stop

**Severity**: HIGH - deploys are blocked. On 2026-10-29 `tools/deploy-pipeline.ps1 -Dev` refused at "Stopping app pool":
`Assert-AppPoolServesPath` (`tools/AppPoolControl.psm1` about 379-510, aud-a38f of 2026-10-22, never run live before) reads
`$site.ItemXPath` / `$app.ItemXPath` in its `ApplicationsProbe`; under Windows PowerShell 5.1 on the IIS host that property
is not on the `Get-Website` objects ("The property 'ItemXPath' cannot be found on this object"), the module's strict mode
throws, and the catch at about 505 refuses the run. The earlier cdt-42 check passed because it reads `Get-WebApplication`
directly. Second question: the cdt-54 sharing report just above printed "serves no IIS application other than this
instance ... stopping it stops nothing else" - if it shares the probe, it must have swallowed the same failure into a
reassurance, which is a defect of its own (a read that failed must warn, never reassure).
**Status**: Open - admitted 2026-10-29 from the owner's dev deploy output; blocks the deploy
**Branch**: -
**Commit**: -
**Reviewer**: implementer (candidate)

## Evidence

The owner's pasted deploy output, 2026-10-29 13:40; `tools/AppPoolControl.psm1` 440-460 (the probe) and 505 (the catch).

## Approach

- Reproduce read-only under `powershell.exe` 5.1 on this host: `Import-Module WebAdministration; Get-Website | Get-Member`
  and `Get-WebApplication -Site 'Default Web Site' | Get-Member` - record whether ItemXPath exists and what does. Read the
  virtual directories another way that 5.1 supports (for example `Get-WebConfiguration -Filter
  "/system.applicationHost/sites/site[@name='X']/application[@path='Y']/virtualDirectory"` built from the names, or
  `Get-WebVirtualDirectory -Site -Application`), keeping aud-a38f's rule that a listing with no root virtual directory is a
  failed read. Make the probe work under both 5.1 and 7; Pester with a stand-in that lacks ItemXPath; mutation: the
  property read restored.
- Decide the sharing-report question by reading `Get-AppPoolOtherApplication`: if it swallowed the probe failure, it must
  warn "could not be read" and never print "stops nothing else"; Pester; mutation.
- Then the owner re-runs the deploy. Gates: PSScriptAnalyzer (0 errors), Pester, ASCII lint, `git diff --check`, parse
  under 5.1.

## Guard proof

Pending.