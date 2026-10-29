# cdt-61: the pre-stop serving-pool check reads an IIS property Windows PowerShell 5.1 does not expose, so every deploy refuses before the stop

**Severity**: HIGH - deploys are blocked. On 2026-10-29 `tools/deploy-pipeline.ps1 -Dev` refused at "Stopping app pool":
`Assert-AppPoolServesPath` (`tools/AppPoolControl.psm1` about 379-510, aud-a38f of 2026-10-22, never run live before) reads
`$site.ItemXPath` / `$app.ItemXPath` in its `ApplicationsProbe`; under Windows PowerShell 5.1 on the IIS host that property
is not on the `Get-Website` objects ("The property 'ItemXPath' cannot be found on this object"), the module's strict mode
throws, and the catch at about 505 refuses the run. The earlier cdt-42 check passed because it reads `Get-WebApplication`
directly. Second question: the cdt-54 sharing report just above printed "serves no IIS application other than this
instance ... stopping it stops nothing else" - if it shares the probe, it must have swallowed the same failure into a
reassurance, which is a defect of its own (a read that failed must warn, never reassure).
**Status**: Fixed on branch, verified; closes on guard proof. One defect, not two: the sharing report has a read of its
own, which works under 5.1, and its reassurance was true on this host. Not merged. The live elevated check was NOT run
(this session is not elevated and WebAdministration will not import without elevation); the owner's re-run of the dev
deploy is the fixed check's first live run
**Branch**: `fix/cdt61` (worktree `D:\source\ExchangeAdminWeb-wt\cdt61`), from master `8d3cbad8`
**Commit**: fix `bc549d68`; this record and its index row in the `docs(cdt-61)` commit that adds this line
**Reviewer**: implementer (candidate)

## Evidence

The owner's pasted deploy output, 2026-10-29 13:40; `tools/AppPoolControl.psm1` 440-460 (the probe) and 505 (the catch).

At `8d3cbad8` the probe is 450-469: 453 builds the site root's owner from `"$($site.ItemXPath)/application[@path='/']"`,
456 takes `$app.ItemXPath`, 459 reads `Get-WebConfiguration -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter
"$($owner.ItemXPath)/virtualDirectory"`. 453 runs before `Get-WebApplication` is called, so the first site's missing
property ends the probe; the catch at 502-506 turns it into "Cannot read the IIS sites and applications, so it cannot be
confirmed that app pool ... is what serves ...: The property 'ItemXPath' cannot be found on this object."

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

## Reproduction and property inventory

**The live reads could not be made.** This session is not elevated, and `Import-Module WebAdministration` refuses in both
shells: Windows PowerShell 5.1.20348.5756 "Process should have elevated status to access IIS configuration data.";
pwsh 7.6.9 "Failure from remote command: Import-Module -Name
'C:\Windows\system32\WindowsPowerShell\v1.0\Modules\WebAdministration\WebAdministration.psd1': Process should have
elevated status to access IIS configuration data." Elevation was not sought or worked around. So no `Get-Member` was run.
The inventory below is read from WebAdministration itself on this host instead: its type data
(`iisprovider.types.ps1xml`), the IIS schema (`IIS_schema.xml`, `rscaext.xml`, `Ftp_schema.xml`) and the IL of
`Microsoft.IIS.PowerShell.Provider.dll` / `Microsoft.IIS.PowerShell.Framework.dll` 10.0.20348.1 (GAC), whose method bodies
a scratch script listed by reflection without invoking anything (`bin/cdt61/ilscan.ps1`, `ildump.ps1`; git-ignored).

| Cmdlet | What it writes (IL) | Properties | ItemXPath |
| --- | --- | --- | --- |
| `Get-Website` | `GetWebsiteCommand.WriteSite`: `ConfigurationElement.ToPSObject` of each site, then five `PSNoteProperty`s from the root application and its root virtual directory | the site's attributes `name`, `id`, `serverAutoStart`, `state` (from `rscaext.xml`); its child elements `bindings`, `limits`, `logFile`, `traceFailedRequestsLogging`, `hsts`, `applicationDefaults`, `virtualDirectoryDefaults`, `ftpServer`; `Collection` (its applications); then `applicationPool`, `enabledProtocols`, `physicalPath`, `userName`, `password`; methods `Start`, `Stop` | **no** - `ToPSObject` adds none and `WriteSite` adds none |
| `Get-WebApplication` | `GetWebApplicationCommand.ProcessRecord`: runs `get-webconfiguration -filter /system.applicationHost/sites/site/application -pspath MACHINE/WEBROOT/APPHOST` in a nested pipeline, skips a path that is empty or `/`, matches `-Name` and `-Site` as IgnoreCase wildcards, writes those objects | attributes `path`, `applicationPool`, `enabledProtocols`, `serviceAutoStartEnabled`, `serviceAutoStartProvider`, `preloadEnabled`; `virtualDirectoryDefaults`; `Collection` (its virtual directories); Get-WebConfiguration's `PSPath`, `Location`, `ConfigurationPathType`, `ItemXPath`, `Metadata`; the type data's ScriptProperty `PhysicalPath` (`$this.ItemXPath + "/virtualDirectory[@path='/']/@physicalPath"`) | **yes** (Get-WebConfiguration's) |
| `Get-WebVirtualDirectory` | `GetWebVDirCommand.ProcessRecord`: runs `get-webconfiguration -filter /system.applicationHost/sites/site/application/virtualDirectory`, skips a path that is empty or `/` (IL_00B4-IL_00C9), matches the directory's path, its application's path and its site's name as IgnoreCase wildcards, writes those objects | `path`, `physicalPath`, `userName`, `password`, `logonMethod`, `allowSubDirConfig`; the same five | yes; **never a root virtual directory** |
| `Get-WebConfiguration` | `GetConfigurationCommand.WriteObject`: `ToPSObject`, then `PSPath`, `Location`, `ConfigurationPathType`, `ItemXPath`, `Metadata` | the element's attributes, child elements and `Collection`, and those five | yes |

The literal `"ItemXPath"` occurs in the two assemblies only in `NamespaceNode.ToPSObject` (provider items, `Get-Item
IIS:\...`), the configuration cmdlets (`Get-WebConfiguration`, `Select-`, `Get-WebConfigurationProperty`, the lock cmdlets)
and four runtime/binding commands - never in `GetWebsiteCommand` or `ConfigurationElement.ToPSObject`. That is the failure:
a site has no ItemXPath, an application has one. The owner's run agrees on both halves: `$site.ItemXPath` threw; and
(inference from the report's control flow) the sharing report, which reads every application's type-data `PhysicalPath`
(built on `$this.ItemXPath`), resolved the dev instance's folder, or it would have listed the instance as "another
application with no folder" and warned.

**Under PowerShell 7** WebAdministration's manifest declares `CompatiblePSEditions = @('Desktop')`, and pwsh loads it through
the Windows PowerShell compatibility session (the import error above came back as a remote command's). So under 7 these
cmdlets run in a 5.1 process and their objects arrive deserialized. Inference, not observed: the same property names cross
as values, so a site has no ItemXPath under 7 either.

**`Get-WebVirtualDirectory -Site 'Default Web Site' -Application '/ExchangeAdminWebDev'` on this host:** nothing. The host's
`applicationHost.config` (read directly, structure only - `BUILTIN\Users` has read on it; `bin/cdt61/config-tree.ps1`) gives
every application only its root virtual directory, and the cmdlet never lists a root one. Its answer here is the empty list a
missed read also gives, so aud-a38f's guard cannot be kept on it, and its wildcard match of the site name can miss a site
outright. Not used.

**This host's IIS tree**, from the same read: site `Default Web Site` (root under `DefaultAppPool`, and five applications,
each under its own pool, among them `/ExchangeAdminWebDev` under `ExchangeAdminWebDev` at `D:\inetpub\ExchangeAdminWebDev` and
`/ExchangeAdminWeb` under `ExchangeAdminWeb` at `D:\inetpub\ExchangeAdminWeb`) and one other site (root and one application,
under one pool of its own). No virtual directory besides the roots; no name holding a quote or a wildcard character.

**Reproduction by replay, not a live read** (`bin/cdt61/replay-51.ps1`): stand-ins for `Get-Website`, `Get-WebApplication` and
`Get-WebConfiguration` in the module's own scope, shaped as the IL builds them and answering from this host's
`applicationHost.config` (Get-WebConfiguration's filter evaluated as XPath over the file).

| Shell | Module | `Assert-AppPoolServesPath -Name 'ExchangeAdminWebDev' -PhysicalPath 'D:\inetpub\ExchangeAdminWebDev' -NestedNodes Refuse` | `Get-AppPoolOtherApplication`, same pool and folder |
| --- | --- | --- | --- |
| 5.1.20348.5756 | `8d3cbad8` (md5 `D3F9F4BCA7B533758F7A6F0B9F7EBB1E`) | threw "Cannot read the IIS sites and applications, so it cannot be confirmed that app pool 'ExchangeAdminWebDev' is what serves 'D:\inetpub\ExchangeAdminWebDev': The property 'ItemXPath' cannot be found on this object. Verify that the property exists." - no Get-WebConfiguration call made | returned nothing (six type-data `PhysicalPath` reads) |
| 5.1.20348.5756 | `bc549d68` (md5 `552E277069F421A08ADAB470E8A64326`) | **returned** one node: `Default Web Site/ExchangeAdminWebDev` under `ExchangeAdminWebDev` at `D:\inetpub\ExchangeAdminWebDev`, after eight name-built filters each found a root virtual directory | returned nothing |
| 7.6.9 | `8d3cbad8` / `bc549d68` | the same throw / the same one node | - |
| 5.1.20348.5756 | `bc549d68`, prod: `ExchangeAdminWeb` at `D:\inetpub\ExchangeAdminWeb` | returned one node, `Default Web Site/ExchangeAdminWeb` (the dev sibling sharing the prefix is not inside) | returned nothing |

The unfixed rows are the owner's two lines, in the order he saw them.

## The fix, as built (`bc549d68`)

- `tools/AppPoolControl.psm1` 379-389: `ConvertTo-IisXPathLiteral`, private. A name IIS reported as an XPath literal: single
  quotes; double quotes when it holds a single quote; a throw when it holds both (XPath 1.0 has no escape), which is a
  refusal.
- 453-489, the default probe: the owners carry no ItemXPath (471, 474); the site's XPath is built from its name (476),
  each owner's filter from that and the application's path (478); the read passes `-ErrorAction Stop` (479), so a read that
  reports an error refuses even when it also answered; aud-a38f's root-virtual-directory guard is unchanged (480-482). The
  comment (453-467) says why the filter is built and why Get-WebVirtualDirectory is not used.
- Nothing else changed: `Get-Website` and `Get-WebApplication` are read as before, every refusal message is unchanged, the
  five callers are unchanged. Pure ASCII (Invariant 6); no IIS name in the source (Invariant 7). No version bump: ops
  scripts.

## The sharing report: one defect, not two

`Get-AppPoolOtherApplication` does not share the probe. Its own default probe (`tools/AppPoolControl.psm1` 632-639 at
`bc549d68`, 612-619 at `8d3cbad8`) reads `Get-Website` (`name`, `applicationPool`, `physicalPath`) and `Get-WebApplication
-Site` (`path`, `applicationPool`, `PhysicalPath`) - no ItemXPath and no `Get-WebConfiguration` of its own. Every one of
those properties is on the 5.1 objects (the table above). A probe that throws is rethrown by its catch (645-649; 625-629 at
`8d3cbad8`), and deploy.ps1's `Write-AppPoolSharingReport` turns a throw into the warning (`deploy.ps1` 279-283: "Stopping app
pool ... may stop other IIS applications too, and their active bulk jobs are not read: ...") and prints "stopping it stops
nothing else" (286) only after a call that returned an empty list. So the reassurance the owner saw came from a read that
worked, and it was true: on this host `ExchangeAdminWebDev` serves only `Default Web Site/ExchangeAdminWebDev` (the replay
above, and the configuration read). The existing Pester test `cdt-54 ... / the default probe / reads every site root and
application, and no virtual directory` (`tests/ps/AppPoolControl.Tests.ps1` 1665) already drives that probe with stand-ins
that carry no ItemXPath and a `Get-WebConfiguration` that throws if called, so a regression to either read there fails it. No
second commit.

## Guard proof

| Test (`tests/ps/AppPoolControl.Tests.ps1`, `Assert-AppPoolServesPath` / `aud-a38f: the default probe reads virtual directories too`) | Pins |
| --- | --- |
| (1219) `cdt-61: reads each application's virtual directories by a filter built from the site's name and the application's path, for a site with no ItemXPath` | **the finding**: the stand-in site has no ItemXPath (asserted); the check passes, returning `Web/App|ProdPool|D:\sites\App`; the filters asked are exactly the site root's application's and then the application's, each built from the names |
| (1236) `cdt-61: writes a name holding a single quote in double quotes, and refuses one holding both quote characters` | `Bob's Web` is named in double quotes and passes; a name with both quotes refuses ("holds both quote characters") with no filter sent |
| (1250) `cdt-61: refuses when the virtual-directory read reports an error, though it also listed a root virtual directory` | a read that writes an error and a root virtual directory refuses, naming the error (`-ErrorAction Stop`) |
| the five aud-a38f / a38i / a38j default-probe tests (1260-1312) | unchanged expectations, now over stand-ins with the 5.1 shapes: a site with no ItemXPath, an application with one (what Get-WebConfiguration returns), filters built from the names |

The Context's stand-ins changed with it: `Get-Website` returns a site with no ItemXPath, named by the test (default `Web`);
`Get-WebApplication` keeps an ItemXPath, as 5.1's objects have one; `Get-WebConfiguration` is an advanced function (so it
honours `-ErrorAction`), records each filter, and can write an error before answering.

**Against the unfixed module** (`8d3cbad8`'s bytes in place, the new tests): 98 passed, 8 failed - every default-probe test,
each on "The property 'ItemXPath' cannot be found on this object". Restored, md5 re-confirmed.

Mutations of `tools/AppPoolControl.psm1` at its final bytes (md5 `552E277069F421A08ADAB470E8A64326`; test file md5
`B92DD36349FAFE8ACD1C928ECBB09E98`, CRLF working-tree bytes), by `bin/cdt61/Invoke-Mutation.ps1`: each exact-text edit
asserted to match once, the mutant's md5 recorded, `AppPoolControl.Tests.ps1` run in a fresh pwsh (Pester 6.2.0), the
original bytes restored, the mtime refreshed, the md5 re-checked equal; afterwards the file ran 106 of 106.

| Mutation | md5 after | Failed (of 106) |
| --- | --- | --- |
| M1: the ItemXPath read restored - 471, 474 and 478 as at `8d3cbad8` (the record's mutation) | `BBB98233F4119366F7A78405EB3C3149` | 8: every default-probe test, each seeing the throw "Cannot read the IIS sites and applications ...: The property 'ItemXPath' cannot be found on this object" |
| M2: `-ErrorAction Stop` dropped from the read | `7F422127CE6CE03D367D63CADD640988` | 1: (1250), no exception thrown |
| M3: every name single-quoted | `75F1BFE909D2E5044B6D795DAFC14FCC` | 1: (1236), the `Bob's Web` filter missed and refused |
| M4: the root-virtual-directory guard removed, under the new filters | `EC73559E038A1D2022F7279EA72FAA56` | 1: `refuses when an application's virtual directories cannot be shown to have been read` (1303), no exception thrown |

## Gates

On `bc549d68`, pwsh 7.6.9, Pester 6.2.0 imported before PSScriptAnalyzer 1.25.0:

- `Invoke-ScriptAnalyzer -Path . -Recurse -Severity Error`: 0. At all severities the two changed files carry exactly what
  they carried at `8d3cbad8` (module: 3 PSProvideCommentHelp, 4 PSUseOutputTypeCorrectly, 2
  PSUseShouldProcessForStateChangingFunctions; test file: 2 PSUseShouldProcessForStateChangingFunctions).
- `Invoke-Pester tests/ps`: 534 passed, 0 failed, 0 skipped (`8d3cbad8`: 531; +3).
- `pwsh -NoProfile -File tools/Test-AsciiOnly.ps1`: passed. `git diff --check HEAD`: clean. Both files stay CRLF.
- Windows PowerShell 5.1.20348.5756 `Parser::ParseFile`: the module and the test file, 0 errors each. Under 5.1 the module
  imports and exports the same seven functions, the helper private; it writes `'Default Web Site'`, `'/ExchangeAdminWebDev'`
  and `"Bob's Web"`, and throws on a name with both quotes.
- No .NET build or test: no C# or Razor changed (and the brief forbids it).

## Known gaps

- **The live check was not run.** The session is not elevated, so neither the read-only `Get-Member` inventory nor the
  read-only call of the fixed check against real IIS was possible; the inventory is the module's IL, the call a replay over
  this host's configuration. The filters use only `@name` and `@path` equality predicates, the kind the type data's
  `PhysicalPath` already sent through the same engine in the owner's run (there the site's also carries `and @id`); a filter
  IIS rejected or matched to nothing would refuse, not pass. The first live run is the owner's re-run of the
  dev deploy; for a read-only look first, from an elevated Windows PowerShell 5.1 in the repo:
  `Import-Module WebAdministration; Import-Module .\tools\AppPoolControl.psm1 -Force; Assert-AppPoolServesPath -Name
  'ExchangeAdminWebDev' -PhysicalPath 'D:\inetpub\ExchangeAdminWebDev' -NestedNodes Refuse; Get-AppPoolOtherApplication
  -Name 'ExchangeAdminWebDev' -PhysicalPath 'D:\inetpub\ExchangeAdminWebDev'` (stops nothing). Expected: the one node, then
  nothing.
- Whether IIS's native query processor takes a double-quoted literal is unverified. It matters only for a name holding an
  apostrophe (none on this host) and fails closed either way.

Candidates, found and not fixed (outside this record):

- **`Get-WebApplication -Site` matches the site name as a wildcard** (IgnoreCase `WildcardPattern`, IL above). In both probes, a
  site whose name holds `[`/`]` would have its applications left out silently, if IIS accepts such a name: in
  `Assert-AppPoolServesPath` a nested application under another pool could then pass the check; in
  `Get-AppPoolOtherApplication` the pool's applications on that site would be missing from the report, and it could print
  "stops nothing else". No such name on this host. A fix would read applications by a name-built filter, as the virtual
  directories now are.
- **The probes' `Get-Website` and `Get-WebApplication` calls carry no `-ErrorAction Stop`.** A module function sees the
  global `$ErrorActionPreference`, not deploy.ps1's script-scoped "Stop", so a non-terminating error from either (the nested
  pipeline's errors are relayed through `Utility.InvokePipeShowError`) would leave a partial listing rather than a refusal
  or the report's warning. Inferred from the IL; not observed.

## Coder dispute

None.

## Reviewer comments

None yet.

## Closeout

Fixed on branch `fix/cdt61`; not merged, not pushed. No version bump (ops scripts). Pending: the owner's dev deploy re-run
(the first live run), merge into `master`, branch deletion.
