#Requires -Version 7.0
<#
.SYNOPSIS
    READ-ONLY survey: how many Entra cloud-only accounts can have their on-premises owner
    derived reliably enough to email a new password to?

.DESCRIPTION
    S0 of docs/CloudPasswordReset-Plan.md, and a hard gate on the rest of it. The Cloud Password
    Reset module never shows the operator the password; it mails it to the account's owner at
    their @analog.com mailbox, and the owner is DERIVED from the cloud UPN at each reset because
    the owner rejected a stored mapping ("cannot store it... we're not going to create an
    instantly stale map"). The whole design therefore stands or falls on one number nobody has
    measured: what fraction of the real population actually resolves.

    This script measures it. For every cloud-only user in the tenant it derives the candidate
    on-premises keys, looks each one up in Active Directory the same way the module will, applies
    the same fail-closed rules, and reports the outcome distribution with samples of each failure
    class.

    A high resolved rate makes the reveal permission a rare exception and the design sound. A low
    one makes the exception path the normal path and the design wrong, at which point the plan is
    replaced rather than amended. The owner sets the threshold after seeing the numbers.

    IT MAKES NO CHANGES. It reads Graph and AD and writes one CSV to disk. It never touches a
    password, never calls PATCH, and needs no elevation.

.PARAMETER PlanOnly
    Describe every step without running any of it. Nothing is queried and no file is written.

.PARAMETER SampleSize
    0 (default) surveys every cloud-only account. A positive number surveys that many, for a
    faster first look. Sampling gives an estimate, not the answer the gate needs.

.PARAMETER CsvPath
    Per-account detail output. Defaults to CloudAccountOwnerCoverage-<yyyyMMdd-HHmmss>.csv in the
    current directory.

.PARAMETER SamplesPerClass
    How many example accounts to print per failure class. Default 5. Only the cloud UPN is
    printed; owner mail addresses stay in the CSV.

.PARAMETER SkipForestCheck
    Skip the global-catalog cross-domain probe described below.

.NOTES
    Fidelity to the module, and one thing this survey deliberately measures that the module does
    not do: ADDirectorySearchService.ValidateExists issues its USER query WITHOUT a -Server, so it
    binds the LOCAL domain only (the -Server routing at ADDirectorySearchService.cs:355 is scoped
    to the Group kind). The plan's corroboration rule exists to catch a sAMAccountName collision
    across ad.analog.com and winroot.analog.com, but a local-domain query cannot SEE the other
    domain's user, so it would not report Ambiguous for one. The primary outcome column below
    reproduces the module's local-domain behaviour exactly. A second column, ForestMatchCount,
    additionally probes the global catalog (port 3268, read-only) to count how many users the
    forest really holds for that key. If that column is above 1 anywhere, the collision risk is
    real and unseen by the module, and it is a finding for the owner, not something this script
    decides. Use -SkipForestCheck to turn the probe off.

    Output: the CSV, plus a console summary answering the gate question directly.
#>
[CmdletBinding()]
param(
    [switch] $PlanOnly,
    [ValidateRange(0, [int]::MaxValue)]
    [int] $SampleSize = 0,
    [string] $CsvPath,
    [ValidateRange(0, 100)]
    [int] $SamplesPerClass = 5,
    [switch] $SkipForestCheck
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'CloudAccountOwnerDerivation.psm1') -Force

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
    $CsvPath = Join-Path (Get-Location).Path ("CloudAccountOwnerCoverage-{0}.csv" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}

# Every outcome this survey can report, in the order the summary prints them. Declared here so
# a class with zero members still shows as 0 rather than vanishing from the report - "no
# ambiguous accounts" and "ambiguity was never evaluated" must not look the same.
$OutcomeClasses = @(
    'Resolved',
    'UnresolvedNoMatch',
    'UnresolvedNoMailbox',
    'UnresolvedNameMismatch',
    'UnresolvedOwnerDisabled',
    'Ambiguous',
    'Unavailable'
)

$script:CloudAccounts = @()
$script:Rows = @()
$script:GlobalCatalog = $null

# ---------------------------------------------------------------------------------------------
# Step 1 - Graph, read-only.
# ---------------------------------------------------------------------------------------------

Invoke-PlanOrAction "Connect to Microsoft Graph as the signed-in admin (delegated User.Read.All, read-only)" {
    if (-not (Get-Module -ListAvailable -Name Microsoft.Graph.Users)) {
        Write-Fail "The Microsoft.Graph.Users module is not installed. Install-Module Microsoft.Graph -Scope CurrentUser"
    }
    Import-Module Microsoft.Graph.Users
    Connect-MgGraph -Scopes 'User.Read.All' -NoWelcome
    $ctx = Get-MgContext
    if (-not $ctx) { Write-Fail "Connect-MgGraph returned no context." }
    Write-Ok "Graph tenant $($ctx.TenantId) as $($ctx.Account)"
}

Invoke-PlanOrAction "Enumerate every cloud-only user in the tenant (onPremisesSyncEnabled not true, userType Member)" {
    $props = @('Id', 'DisplayName', 'UserPrincipalName', 'AccountEnabled', 'OnPremisesSyncEnabled', 'UserType')

    # Filtered client-side, not server-side, on purpose: onPremisesSyncEnabled is NULL rather than
    # false for a cloud-only account, and a Graph $filter on a null-valued property is the kind of
    # thing that silently returns an empty page. Fail-closed reading: only a definite $true counts
    # as synced, so an unreadable value lands in the survey rather than being dropped from it.
    $all = Get-MgUser -All -Property $props -ConsistencyLevel eventual | Select-Object $props

    $cloud = @($all | Where-Object {
        $_.OnPremisesSyncEnabled -ne $true -and $_.UserType -ne 'Guest'
    })

    if ($SampleSize -gt 0 -and $cloud.Count -gt $SampleSize) {
        Write-Warn "Sampling $SampleSize of $($cloud.Count) cloud-only accounts. This is an estimate; the gate needs a full run."
        $cloud = @($cloud | Get-Random -Count $SampleSize)
    }

    $script:CloudAccounts = $cloud
    Write-Ok "$($all.Count) users in tenant, $($cloud.Count) cloud-only non-guest accounts to survey"
}

# ---------------------------------------------------------------------------------------------
# Step 2 - Active Directory, read-only, one lookup per candidate key.
# ---------------------------------------------------------------------------------------------

Invoke-PlanOrAction "Load the ActiveDirectory module and resolve the forest global catalog" {
    if (-not (Get-Module -ListAvailable -Name ActiveDirectory)) {
        Write-Fail "The ActiveDirectory module is not installed. Add RSAT-AD-PowerShell, or run this from a host that has it."
    }
    Import-Module ActiveDirectory

    if ($SkipForestCheck) {
        Write-Warn "Forest cross-domain probe skipped by request; ForestMatchCount will be blank."
    }
    else {
        # Fail-soft, exactly as ADDirectorySearchService.ResolveGlobalCatalog does: a forest that
        # cannot be reached degrades the extra column, it does not fail the survey.
        try {
            $forest = Get-ADForest
            $script:GlobalCatalog = "$($forest.Name):3268"
            Write-Ok "Global catalog $($script:GlobalCatalog), domains: $($forest.Domains -join ', ')"
        }
        catch {
            Write-Warn "Could not resolve the forest global catalog ($($_.Exception.Message)). ForestMatchCount will be blank."
            $script:GlobalCatalog = $null
        }
    }
}

function Get-AdCandidateResult {
    <#
        One candidate key, looked up the way ValidateExists does it: exact-match LDAP filter over
        userPrincipalName / mail / sAMAccountName (ADDirectorySearchService.BuildExactMatchFilter,
        :432), ResultSetSize 2 so "more than one" is decisive, local domain only.

        A thrown lookup is Unavailable, never NotFound. An exception means the question never
        reached the directory, which is not evidence the user is absent - the single most
        important distinction in this whole survey, and the one a naive try/catch gets wrong.
    #>
    param([string] $Candidate)

    $escaped = $Candidate -replace '\\', '\5c' -replace '\*', '\2a' -replace '\(', '\28' -replace '\)', '\29' -replace "`0", '\00'
    $filter = "(|(userPrincipalName=$escaped)(mail=$escaped)(sAMAccountName=$escaped))"

    try {
        $found = @(Get-ADUser -LDAPFilter $filter -ResultSetSize 2 -Properties DisplayName, DistinguishedName, SamAccountName, UserPrincipalName, mail, GivenName, Surname, Enabled -ErrorAction Stop)
    }
    catch {
        return [pscustomobject]@{
            Candidate = $Candidate; Status = 'Unavailable'; DistinguishedName = $null
            SamAccountName = $null; Email = $null; Enabled = $null; GivenName = $null; Surname = $null
            Error = $_.Exception.Message
        }
    }

    if ($found.Count -eq 0) {
        return [pscustomobject]@{
            Candidate = $Candidate; Status = 'NotFound'; DistinguishedName = $null
            SamAccountName = $null; Email = $null; Enabled = $null; GivenName = $null; Surname = $null
            Error = $null
        }
    }

    if ($found.Count -gt 1) {
        return [pscustomobject]@{
            Candidate = $Candidate; Status = 'Ambiguous'; DistinguishedName = $null
            SamAccountName = $null; Email = $null; Enabled = $null; GivenName = $null; Surname = $null
            Error = $null
        }
    }

    $u = $found[0]
    return [pscustomobject]@{
        Candidate         = $Candidate
        Status            = 'Found'
        DistinguishedName = $u.DistinguishedName
        SamAccountName    = $u.SamAccountName
        Email             = $u.mail
        Enabled           = $u.Enabled
        GivenName         = $u.GivenName
        Surname           = $u.Surname
        Error             = $null
    }
}

function Get-ForestMatchCount {
    <#
        How many users the WHOLE forest holds for this key, via the global catalog. Reported, never
        acted on: the module cannot see this, and the point of the column is to show the owner
        whether the collision the corroboration rule guards against actually exists here.
        Returns $null when the probe is off or failed, which the CSV renders as blank - blank means
        "not measured", 0 means "measured, none".
    #>
    param([string[]] $Candidate)

    if (-not $script:GlobalCatalog) { return $null }

    $total = 0
    foreach ($c in $Candidate) {
        $escaped = $c -replace '\\', '\5c' -replace '\*', '\2a' -replace '\(', '\28' -replace '\)', '\29'
        try {
            $hits = @(Get-ADUser -Server $script:GlobalCatalog -LDAPFilter "(|(userPrincipalName=$escaped)(mail=$escaped)(sAMAccountName=$escaped))" -ResultSetSize 10 -ErrorAction Stop)
            $total += $hits.Count
        }
        catch {
            return $null
        }
    }
    return $total
}

Invoke-PlanOrAction "Derive candidates and resolve an owner for each cloud-only account (read-only AD lookups)" {
    $rows = [System.Collections.Generic.List[object]]::new()
    $i = 0
    $total = $script:CloudAccounts.Count

    foreach ($acct in $script:CloudAccounts) {
        $i++
        if ($total -gt 0 -and ($i % 25 -eq 0 -or $i -eq $total)) {
            Write-Progress -Activity 'Resolving owners' -Status "$i / $total" -PercentComplete ([int](100 * $i / $total))
        }

        $candidates = @(Get-CloudAccountOwnerCandidate -UserPrincipalName $acct.UserPrincipalName)
        $results = @(foreach ($c in $candidates) { Get-AdCandidateResult -Candidate $c })

        $outcome = Resolve-CloudAccountOwnerOutcome -CandidateResult $results -CloudDisplayName $acct.DisplayName

        $forestCount = $null
        if (-not $SkipForestCheck -and $candidates.Count -gt 0) {
            $forestCount = Get-ForestMatchCount -Candidate $candidates
        }

        $lookupError = ($results | Where-Object { $_.Error } | Select-Object -First 1).Error

        $rows.Add([pscustomobject]@{
            CloudUserPrincipalName = $acct.UserPrincipalName
            CloudDisplayName       = $acct.DisplayName
            CloudObjectId          = $acct.Id
            CloudAccountEnabled    = $acct.AccountEnabled
            Candidates             = ($candidates -join ' | ')
            CandidateStatuses      = (($results | ForEach-Object { "$($_.Candidate)=$($_.Status)" }) -join ' | ')
            Outcome                = $outcome.Outcome
            Reason                 = $outcome.Reason
            OwnerSamAccountName    = $outcome.OwnerSamAccountName
            OwnerEmail             = $outcome.OwnerEmail
            OwnerEnabled           = $outcome.OwnerEnabled
            OwnerDistinguishedName = $outcome.OwnerDistinguishedName
            ForestMatchCount       = $forestCount
            LookupError            = $lookupError
        })
    }

    Write-Progress -Activity 'Resolving owners' -Completed
    $script:Rows = $rows.ToArray()
    Write-Ok "$($script:Rows.Count) accounts surveyed"
}

# ---------------------------------------------------------------------------------------------
# Step 3 - report.
# ---------------------------------------------------------------------------------------------

Invoke-PlanOrAction "Write per-account detail to $CsvPath" {
    $script:Rows | Export-Csv -LiteralPath $CsvPath -NoTypeInformation -Encoding UTF8
    Write-Ok "CSV written: $CsvPath"
}

Invoke-PlanOrAction "Print the coverage summary that answers the S0 gate" {
    $total = $script:Rows.Count
    if ($total -eq 0) {
        Write-Warn "No cloud-only accounts were surveyed. The gate cannot be answered from this run."
        return
    }

    Write-Host ""
    Write-Host "Cloud Password Reset - owner coverage" -ForegroundColor White
    Write-Host ("=" * 60)
    Write-Host ("{0,-26} {1,7} {2,8}" -f 'Outcome', 'Count', 'Percent')

    foreach ($class in $OutcomeClasses) {
        $n = @($script:Rows | Where-Object { $_.Outcome -eq $class }).Count
        $pct = if ($total -gt 0) { 100.0 * $n / $total } else { 0 }
        $color = if ($class -eq 'Resolved') { 'Green' } elseif ($n -eq 0) { 'DarkGray' } else { 'Yellow' }
        Write-Host ("{0,-26} {1,7} {2,7:N1}%" -f $class, $n, $pct) -ForegroundColor $color
    }

    Write-Host ("=" * 60)
    Write-Host ("{0,-26} {1,7}" -f 'Total', $total)

    $resolved = @($script:Rows | Where-Object { $_.Outcome -eq 'Resolved' }).Count
    Write-Host ""
    Write-Host ("Resolved rate: {0:N1}%  ({1} of {2})" -f (100.0 * $resolved / $total), $resolved, $total) -ForegroundColor White
    Write-Host "The owner sets the threshold. A low rate makes the reveal permission the normal path,"
    Write-Host "which means the design is wrong and the plan is replaced rather than amended."

    if ($SamplesPerClass -gt 0) {
        foreach ($class in $OutcomeClasses) {
            if ($class -eq 'Resolved') { continue }
            $examples = @($script:Rows | Where-Object { $_.Outcome -eq $class } | Select-Object -First $SamplesPerClass)
            if ($examples.Count -eq 0) { continue }
            Write-Host ""
            Write-Host "$class - first $($examples.Count):" -ForegroundColor Yellow
            foreach ($e in $examples) {
                Write-Host ("  {0}  [{1}]" -f $e.CloudUserPrincipalName, $e.Reason)
            }
        }
    }

    $collisions = @($script:Rows | Where-Object { $null -ne $_.ForestMatchCount -and $_.ForestMatchCount -gt 1 })
    Write-Host ""
    if ($SkipForestCheck -or -not $script:GlobalCatalog) {
        Write-Warn "Forest cross-domain collisions: NOT MEASURED. Blank is not zero."
    }
    elseif ($collisions.Count -eq 0) {
        Write-Ok "Forest cross-domain collisions: none. No candidate key matches more than one forest user."
    }
    else {
        Write-Warn "Forest cross-domain collisions: $($collisions.Count) accounts whose candidate key matches more than one user forest-wide."
        Write-Warn "The module's USER lookup binds the local domain only, so it cannot see these and will NOT report them as ambiguous."
        Write-Warn "Corroboration is the only thing standing between those and a password mailed to the wrong person. Raise with the owner."
    }

    Write-Host ""
    Write-Host "Detail: $CsvPath"
}

if ($PlanOnly) {
    Write-Host ""
    Write-Plan "Plan mode: nothing was queried and no file was written."
}
