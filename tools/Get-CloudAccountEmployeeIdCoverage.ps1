#Requires -Version 7.0
<#
.SYNOPSIS
    READ-ONLY survey: for how many Entra cloud-only accounts does employeeId resolve to exactly
    one live directory user with a mailbox to send a new password to?

.DESCRIPTION
    The gate on queue item 10 and on the revision of docs/CloudPasswordReset-Plan.md.

    On 2026-09-11 a survey measured employeeId populated on 0 of 172 cloud accounts, the owner
    refused to stamp them, and matching was ruled off the table: "if we cannot get a 100%
    working match, then matching is off the table". The design that survived puts the
    destination address in the operator's hands. On 2026-09-22 the owner stamped the in-scope
    accounts and asked for matching back. This measures whether the stamping actually produced a
    usable match, which is a different question from whether the field is filled in.

    Four ways a stamped account still fails, all counted separately here:
      - the id is blank after all                           -> NoEmployeeId
      - it is populated but resolves to nobody              -> NoDirectoryMatch
      - it resolves to MORE THAN ONE user                   -> Ambiguous
      - it resolves to one user with no mailbox             -> MatchedNoMailbox
    Plus MatchedOwnerDisabled, and Unavailable for reads that failed. A read failure is never
    folded into a negative outcome: "nothing there" and "could not look" must not be counted
    together.

    Ambiguous is the outcome to read first. A duplicate employeeId means the module would pick
    an owner it cannot justify, and mailing a password on that guess is worse than not matching
    at all.

    IT MAKES NO CHANGES. It reads Graph and Active Directory and writes one CSV. It never sets a
    password, never calls PATCH, and needs no elevation.

.PARAMETER PlanOnly
    Describe every step without running any of it. Nothing is queried and no file is written.

.PARAMETER CsvPath
    Per-account detail output. Defaults to CloudAccountEmployeeIdCoverage-<yyyyMMdd-HHmmss>.csv
    in the current directory.

.PARAMETER SampleSize
    0 (default) surveys every cloud-only account. A positive number surveys that many, for a
    faster first look. A sample gives an estimate; a 100% bar needs the full run.

.PARAMETER SamplesPerClass
    How many example accounts to print per outcome class. Default 5. Only the cloud UPN is
    printed; owner mail addresses stay in the CSV.

.PARAMETER ConnectionModulePath
    The M365Connections module that owns Graph and AD authentication here. Defaults to the
    'm365-connections-module:' entry in .agents/machines.md - machine-specific, so never
    hardcoded in this script.

.NOTES
    CONNECTING: this script does NOT call Connect-MgGraph itself and must not be changed to.
    Owner, 2026-09-22: "I don't log in to graph like this. I use the connection module's
    GraphConnect." That function authenticates the existing app registration through its Delinea
    credential helper, APP-ONLY, so there are no delegated scopes to request - asking for them
    would be both wrong and a prompt the operator should never see. AD comes from the same
    module's ADImport. Connect-AllM365Services is deliberately not used: it also runs module
    updates and opens other service connections, which this read-only survey has no business
    doing.

    Environment neutrality (.agents/repo-guidance.md, owner ruling 2026-09-11): no domain, host,
    OU, group or address is named or defaulted anywhere in this script. The forest is discovered
    from the host's own membership via Get-ADForest, and each matched user is re-read from the
    domain derived from its own distinguished name.

    If the forest cannot be resolved the survey FAILS rather than quietly falling back to the
    local domain. A local-domain-only answer cannot see a duplicate employeeId in another domain,
    so it would under-report Ambiguous - the one outcome whose whole purpose is to look across
    domains - and a narrower answer would read as a cleaner one against a bar set at 100%.

    The CSV carries the raw match count, mailbox and enabled values per row, so the owner can
    re-bucket the summary without a second run.
#>
[CmdletBinding()]
param(
    [switch] $PlanOnly,
    [string] $CsvPath,
    [ValidateRange(0, [int]::MaxValue)]
    [int] $SampleSize = 0,
    [ValidateRange(0, 100)]
    [int] $SamplesPerClass = 5,
    [string] $ConnectionModulePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'CloudAccountEmployeeIdMatch.psm1') -Force

function Write-Step { param([string]$Message) Write-Host ">>> $Message" -ForegroundColor Cyan }
function Write-Ok { param([string]$Message) Write-Host " OK  $Message" -ForegroundColor Green }
function Write-Warn { param([string]$Message) Write-Host "  !  $Message" -ForegroundColor Yellow }
function Write-Plan { param([string]$Message) Write-Host "PLAN $Message" -ForegroundColor DarkGray }
function Write-Fail { param([string]$Message) Write-Host "  X  $Message" -ForegroundColor Red; throw $Message }

function Invoke-PlanOrAction {
    param([string]$Description, [scriptblock]$Action)

    if ($PlanOnly) {
        Write-Plan $Description
        return
    }

    Write-Step $Description
    & $Action
}

if (-not $CsvPath) {
    $CsvPath = Join-Path (Get-Location).Path ("CloudAccountEmployeeIdCoverage-{0}.csv" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}

# Resolved the same way tools/Get-TokenUsage.ps1 resolves transcript-root: the path is a fact
# about this machine, so it lives in .agents/machines.md and not in the script.
if (-not $ConnectionModulePath) {
    $machinesFile = Join-Path -Path (Split-Path -Parent $PSScriptRoot) -ChildPath '.agents/machines.md'
    if (-not (Test-Path -LiteralPath $machinesFile)) {
        Write-Fail "No -ConnectionModulePath given and machines file not found: $machinesFile"
    }
    $entry = Select-String -LiteralPath $machinesFile -Pattern 'm365-connections-module:\s*`([^`]+)`' |
        Select-Object -First 1
    if (-not $entry) {
        Write-Fail "No -ConnectionModulePath given and no 'm365-connections-module:' entry in $machinesFile. Add one, or pass -ConnectionModulePath."
    }
    $ConnectionModulePath = $entry.Matches[0].Groups[1].Value
}

$script:CloudAccounts = @()
$script:Rows = @()
$script:GlobalCatalog = $null

# -------------------------------------------------------------------------------------------
# Step 1 - Graph, read-only. Enumerate the population and read employeeId.
# -------------------------------------------------------------------------------------------

Invoke-PlanOrAction "Connect to Microsoft Graph via $ConnectionModulePath (GraphConnect, app-only)" {
    if (-not (Test-Path -LiteralPath $ConnectionModulePath)) {
        Write-Fail "The M365Connections module is not at $ConnectionModulePath. Pass -ConnectionModulePath, or correct the 'm365-connections-module:' entry in .agents/machines.md."
    }
    Import-Module $ConnectionModulePath -Force

    if (-not (Get-Command -Name GraphConnect -ErrorAction SilentlyContinue)) {
        Write-Fail "$ConnectionModulePath does not export GraphConnect."
    }
    if (-not (Get-Module -ListAvailable -Name Microsoft.Graph.Users)) {
        Write-Fail "The Microsoft.Graph.Users module is not installed. Install-Module Microsoft.Graph -Scope CurrentUser"
    }
    Import-Module Microsoft.Graph.Users

    # No -Scopes: GraphConnect is app-only through the app registration's Delinea credential.
    GraphConnect

    $ctx = Get-MgContext
    if (-not $ctx) { Write-Fail "GraphConnect returned no Graph context." }
    Write-Ok "Graph tenant $($ctx.TenantId), app $($ctx.ClientId), auth $($ctx.AuthType)"
}

Invoke-PlanOrAction "Enumerate every cloud-only member account and read its employeeId" {
    $props = @('Id', 'DisplayName', 'UserPrincipalName', 'AccountEnabled', 'OnPremisesSyncEnabled', 'UserType', 'EmployeeId')

    # Filtered client-side on purpose: onPremisesSyncEnabled is NULL rather than false for a
    # cloud-only account, and a Graph filter over a null-valued property is exactly the shape
    # that silently returns an empty page. Read fail-closed - only a definite $true counts as
    # synced, so an unreadable value stays IN the survey rather than disappearing from it.
    $all = Get-MgUser -All -Property $props -ConsistencyLevel eventual | Select-Object $props

    $cloud = @($all | Where-Object {
            $_.OnPremisesSyncEnabled -ne $true -and $_.UserType -ne 'Guest'
        })

    if ($SampleSize -gt 0 -and $cloud.Count -gt $SampleSize) {
        Write-Warn "Sampling $SampleSize of $($cloud.Count) cloud-only accounts. This is an estimate; the 100% bar needs a full run."
        $cloud = @($cloud | Get-Random -Count $SampleSize)
    }

    $script:CloudAccounts = $cloud
    $stamped = @($cloud | Where-Object { -not [string]::IsNullOrWhiteSpace($_.EmployeeId) }).Count
    Write-Ok "$($all.Count) users in tenant, $($cloud.Count) cloud-only non-guest accounts, $stamped carrying an employeeId"
}

# -------------------------------------------------------------------------------------------
# Step 2 - Active Directory, read-only, forest-wide.
# -------------------------------------------------------------------------------------------

Invoke-PlanOrAction "Load Active Directory via the same module (ADImport) and discover this host's forest global catalog" {
    if (-not (Get-Module -ListAvailable -Name ActiveDirectory)) {
        Write-Fail "The ActiveDirectory module is not installed. Add RSAT-AD-PowerShell, or run this from a host that has it."
    }

    # ADImport rather than a bare Import-Module, for the same reason as GraphConnect: this repo
    # does not open its own connections when the connection module owns them.
    if (Get-Command -Name ADImport -ErrorAction SilentlyContinue) {
        ADImport
    }
    else {
        Write-Fail "$ConnectionModulePath does not export ADImport."
    }

    # Deliberately fail-CLOSED, unlike the app's own fail-soft global catalog resolution. See
    # the note in the help block: a local-domain answer under-reports Ambiguous.
    try {
        $forest = Get-ADForest
    }
    catch {
        Write-Fail "Could not resolve the forest ($($_.Exception.Message)). This survey needs a forest-wide search to count duplicate employeeIds; a local-domain answer would under-report them."
    }

    $script:GlobalCatalog = "$($forest.Name):3268"
    Write-Ok "Global catalog $($script:GlobalCatalog), $($forest.Domains.Count) domain(s) in scope"
}

Invoke-PlanOrAction "Resolve each employeeId across the forest and classify the outcome" {
    $i = 0
    foreach ($account in $script:CloudAccounts) {
        $i++
        if ($i % 50 -eq 0) { Write-Host "    ... $i of $($script:CloudAccounts.Count)" -ForegroundColor DarkGray }

        $employeeId = $account.EmployeeId
        $matchCount = 0
        $ownerDn = ''
        $ownerMail = ''
        $ownerEnabled = $false
        $unavailable = $false
        $note = ''

        if (-not [string]::IsNullOrWhiteSpace($employeeId)) {
            try {
                $filterValue = ConvertTo-LdapFilterValue -Value $employeeId
                $hits = @(Get-ADUser -Server $script:GlobalCatalog -LDAPFilter "(employeeID=$filterValue)" -Properties employeeID)
                $matchCount = $hits.Count

                if ($matchCount -eq 1) {
                    # Re-read from the domain that owns the object. The global catalog holds a
                    # partial attribute set, so mail or Enabled can be absent there and read as
                    # a failure that is really an artefact of where we looked.
                    $ownerDn = $hits[0].DistinguishedName
                    $domain = Get-DomainDnsFromDistinguishedName -DistinguishedName $ownerDn
                    if (-not $domain) {
                        $unavailable = $true
                        $note = "Matched object has no DC component in its DN; cannot route an authoritative read."
                    }
                    else {
                        $full = Get-ADUser -Server $domain -Identity $ownerDn -Properties mail, Enabled
                        $ownerMail = if ($full.mail) { [string]$full.mail } else { '' }
                        $ownerEnabled = [bool]$full.Enabled
                    }
                }
            }
            catch {
                $unavailable = $true
                $note = $_.Exception.Message
            }
        }

        $outcome = Get-EmployeeIdMatchOutcome `
            -EmployeeId $employeeId `
            -MatchCount $matchCount `
            -OwnerHasMailbox (-not [string]::IsNullOrWhiteSpace($ownerMail)) `
            -OwnerEnabled $ownerEnabled `
            -Unavailable $unavailable

        $script:Rows += [pscustomobject]@{
            CloudUpn         = $account.UserPrincipalName
            CloudDisplayName = $account.DisplayName
            CloudEnabled     = $account.AccountEnabled
            EmployeeId       = $employeeId
            MatchCount       = $matchCount
            OwnerDn          = $ownerDn
            OwnerMail        = $ownerMail
            OwnerEnabled     = $ownerEnabled
            Outcome          = $outcome
            Note             = $note
        }
    }

    Write-Ok "$($script:Rows.Count) accounts classified"
}

# -------------------------------------------------------------------------------------------
# Step 3 - Report.
# -------------------------------------------------------------------------------------------

Invoke-PlanOrAction "Write the per-account CSV to $CsvPath" {
    $script:Rows | Export-Csv -Path $CsvPath -NoTypeInformation -Encoding UTF8
    Write-Ok "Wrote $($script:Rows.Count) rows to $CsvPath"
}

Invoke-PlanOrAction "Print the coverage summary" {
    $total = $script:Rows.Count
    if ($total -eq 0) { Write-Fail "No accounts were surveyed, so there is no coverage to report." }

    Write-Host ""
    Write-Host "employeeId coverage, $total cloud-only accounts" -ForegroundColor White
    Write-Host ""

    foreach ($class in (Get-EmployeeIdOutcomeClasses)) {
        $rows = @($script:Rows | Where-Object { $_.Outcome -eq $class })
        $pct = [math]::Round(100.0 * $rows.Count / $total, 1)
        Write-Host ("  {0,-22} {1,6}  {2,5}%" -f $class, $rows.Count, $pct)

        if ($rows.Count -gt 0 -and $class -ne 'Resolved' -and $SamplesPerClass -gt 0) {
            foreach ($sample in ($rows | Select-Object -First $SamplesPerClass)) {
                Write-Host ("        {0}" -f $sample.CloudUpn) -ForegroundColor DarkGray
            }
        }
    }

    $resolved = @($script:Rows | Where-Object { $_.Outcome -eq 'Resolved' }).Count
    $rate = [math]::Round(100.0 * $resolved / $total, 1)

    Write-Host ""
    if ($resolved -eq $total) {
        Write-Ok "100% of surveyed accounts resolve to exactly one live mailbox. The bar is met on this population."
    }
    else {
        Write-Warn "$rate% resolve. The owner's bar is 100%, so this is a decision for the owner, not a gap to route around with a fallback."
    }
    Write-Host ""
    Write-Host "Full detail, including the raw match count per account, is in $CsvPath" -ForegroundColor White
}
