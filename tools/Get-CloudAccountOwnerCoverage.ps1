#Requires -Version 7.0
<#
.SYNOPSIS
    READ-ONLY survey: how many Entra cloud-only accounts can have their on-premises owner
    derived reliably enough to email a new password to?

.DESCRIPTION
    S0 of docs/CloudPasswordReset-Plan.md, and a hard gate on the rest of it. The Cloud Password
    Reset module never shows the operator the password; it mails it to the account's owner at
    their on-premises mailbox, and the owner is DERIVED from the cloud UPN at each reset because
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

.PARAMETER SearchDomain
    The domains to search for owners, by DNS name. Required for a real run. These stand in for the
    module's operator-chosen Search Domains setting, so the survey measures the scope that will
    actually ship. Pass only domains whose accounts sync to Entra, or that you otherwise want
    searched - not every domain a trust makes reachable.

.PARAMETER ListDomains
    List the domains available to this host (forest domains plus trusted domains) and exit. Use
    this to decide what to pass to -SearchDomain. Queries the directory; writes nothing.

.NOTES
    Scope is chosen, not assumed. ADDirectorySearchService.ValidateExists issues its USER query
    WITHOUT a -Server, so today it binds whichever single domain the app host happens to be joined
    to (the -Server routing at ADDirectorySearchService.cs:355 is scoped to the Group kind). That
    is an accident of deployment. A forest-wide sweep is not the answer either: the estate has
    domains and trusts with no relationship to Entra, so searching everything queries irrelevant
    directories and manufactures collisions that are not real ones. The settled design, and what
    this survey reproduces, is an operator-chosen set of domains - see .agents/decisions.md
    2026-09-11 "Searched domains are an operator setting".

    Every checked domain is searched for every candidate key, and matches are pooled across the
    set: two or more distinct users anywhere in that set is Ambiguous and resolves to nothing. If
    ANY checked domain cannot be reached, the key is Unavailable rather than NotFound - an
    unreachable domain could be holding the second match, so absence cannot be concluded from it.

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
    [string[]] $SearchDomain,
    [switch] $ListDomains
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

function Get-AvailableDomain {
    <#
        Every domain this host could search: the forest's own domains plus every trusted domain.
        Discovered from the directory at run time - no name is hard-coded, defaulted or typed. This
        is the same enumeration the module's Search Domains setting will offer as checkboxes.
    #>
    Import-Module ActiveDirectory -ErrorAction Stop

    $names = [System.Collections.Generic.List[string]]::new()

    try {
        $forest = Get-ADForest -ErrorAction Stop
        foreach ($d in $forest.Domains) { $names.Add([string]$d) }
    }
    catch {
        Write-Warn "Could not enumerate forest domains: $($_.Exception.Message)"
    }

    try {
        foreach ($t in (Get-ADTrust -Filter * -ErrorAction Stop)) {
            if ($t.Name) { $names.Add([string]$t.Name) }
        }
    }
    catch {
        Write-Warn "Could not enumerate trusts: $($_.Exception.Message)"
    }

    return , @($names | Sort-Object -Unique)
}

if ($ListDomains) {
    if (-not (Get-Module -ListAvailable -Name ActiveDirectory)) {
        Write-Fail "The ActiveDirectory module is not installed. Add RSAT-AD-PowerShell, or run this from a host that has it."
    }
    $available = Get-AvailableDomain
    Write-Host ""
    Write-Host "Domains available to this host:" -ForegroundColor White
    foreach ($d in $available) { Write-Host "  $d" }
    Write-Host ""
    Write-Host "Pass the ones whose accounts sync to Entra, or that you otherwise want searched:"
    Write-Host "  -SearchDomain " -NoNewline
    Write-Host (($available | Select-Object -First 2) -join ',') -ForegroundColor DarkGray
    return
}

if (-not $PlanOnly -and (-not $SearchDomain -or $SearchDomain.Count -eq 0)) {
    Write-Fail "-SearchDomain is required. Run with -ListDomains to see what this host can search, then pass the domains you want. Nothing is assumed."
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
$script:Domains = @()

# ---------------------------------------------------------------------------------------------
# Step 1 - Graph, read-only.
# ---------------------------------------------------------------------------------------------

Invoke-PlanOrAction "Use the existing Microsoft Graph connection, or sign in read-only (User.Read.All)" {
    if (-not (Get-Module -ListAvailable -Name Microsoft.Graph.Users)) {
        Write-Fail "The Microsoft.Graph.Users module is not installed. Install-Module Microsoft.Graph -Scope CurrentUser"
    }
    Import-Module Microsoft.Graph.Users

    # An existing session is reused rather than replaced: the estate connects app-only through its
    # own tooling, and re-running Connect-MgGraph here would tear that down for a delegated sign-in
    # this survey does not need. Read scopes are all it uses either way.
    $ctx = Get-MgContext
    if (-not $ctx) {
        Connect-MgGraph -Scopes 'User.Read.All' -NoWelcome
        $ctx = Get-MgContext
    }
    if (-not $ctx) { Write-Fail "No Microsoft Graph context, and Connect-MgGraph returned none." }

    $who = if ($ctx.Account) { $ctx.Account } else { "app $($ctx.ClientId) ($($ctx.AuthType))" }
    Write-Ok "Graph tenant $($ctx.TenantId) as $who"
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

Invoke-PlanOrAction "Load the ActiveDirectory module and verify every requested search domain answers" {
    if (-not (Get-Module -ListAvailable -Name ActiveDirectory)) {
        Write-Fail "The ActiveDirectory module is not installed. Add RSAT-AD-PowerShell, or run this from a host that has it."
    }
    Import-Module ActiveDirectory

    # Checked up front rather than per-account: a domain that cannot be reached would turn every
    # single key Unavailable, and finding that out after several hundred lookups wastes the run.
    $ok = [System.Collections.Generic.List[string]]::new()
    foreach ($d in $SearchDomain) {
        try {
            $info = Get-ADDomain -Server $d -ErrorAction Stop
            $ok.Add([string]$info.DNSRoot)
            Write-Ok "$($info.DNSRoot) reachable"
        }
        catch {
            Write-Fail "Search domain '$d' did not answer: $($_.Exception.Message). Fix it or drop it from -SearchDomain; the survey will not silently skip a domain."
        }
    }

    $script:Domains = @($ok | Sort-Object -Unique)
    Write-Ok "Searching $($script:Domains.Count) domain(s): $($script:Domains -join ', ')"
}

function Get-AdCandidateResult {
    <#
        One candidate key, looked up the way the module will: exact-match LDAP filter over
        userPrincipalName / mail / sAMAccountName (ADDirectorySearchService.BuildExactMatchFilter,
        :432), ResultSetSize 2 per domain so "more than one" is decisive, across EVERY checked
        domain with matches pooled.

        Two rules do the safety work here:

        A thrown lookup is Unavailable, never NotFound. An exception means the question never
        reached the directory, which is not evidence the user is absent - the single most
        important distinction in this whole survey, and the one a naive try/catch gets wrong.

        And one unreachable domain poisons the whole key, not just its own result. Concluding
        "only one match" while a checked domain stayed silent is exactly the wrong answer: the
        silent domain is where the second match would have been.
    #>
    param([string] $Candidate, [string[]] $Domain)

    $escaped = $Candidate -replace '\\', '\5c' -replace '\*', '\2a' -replace '\(', '\28' -replace '\)', '\29' -replace "`0", '\00'
    $filter = "(|(userPrincipalName=$escaped)(mail=$escaped)(sAMAccountName=$escaped))"
    $props = @('DisplayName', 'DistinguishedName', 'SamAccountName', 'UserPrincipalName', 'mail', 'GivenName', 'Surname', 'Enabled')

    $hits = [System.Collections.Generic.List[object]]::new()
    $matchedIn = [System.Collections.Generic.List[string]]::new()

    foreach ($d in $Domain) {
        try {
            $found = @(Get-ADUser -Server $d -LDAPFilter $filter -ResultSetSize 2 -Properties $props -ErrorAction Stop)
        }
        catch {
            return [pscustomobject]@{
                Candidate = $Candidate; Status = 'Unavailable'; DistinguishedName = $null
                SamAccountName = $null; Email = $null; Enabled = $null; GivenName = $null; Surname = $null
                MatchedDomains = $null; Error = "$d : $($_.Exception.Message)"
            }
        }

        foreach ($u in $found) { $hits.Add($u) }
        if ($found.Count -gt 0) { $matchedIn.Add($d) }
    }

    # Distinctness by DN, so the same user seen twice through a trust is one user, not a collision.
    $distinct = @($hits | Sort-Object -Property DistinguishedName -Unique)
    $domains = ($matchedIn | Sort-Object -Unique) -join ' | '

    if ($distinct.Count -eq 0) {
        return [pscustomobject]@{
            Candidate = $Candidate; Status = 'NotFound'; DistinguishedName = $null
            SamAccountName = $null; Email = $null; Enabled = $null; GivenName = $null; Surname = $null
            MatchedDomains = $null; Error = $null
        }
    }

    if ($distinct.Count -gt 1) {
        return [pscustomobject]@{
            Candidate = $Candidate; Status = 'Ambiguous'; DistinguishedName = $null
            SamAccountName = $null; Email = $null; Enabled = $null; GivenName = $null; Surname = $null
            MatchedDomains = $domains; Error = $null
        }
    }

    $u = $distinct[0]
    return [pscustomobject]@{
        Candidate         = $Candidate
        Status            = 'Found'
        DistinguishedName = $u.DistinguishedName
        SamAccountName    = $u.SamAccountName
        Email             = $u.mail
        Enabled           = $u.Enabled
        GivenName         = $u.GivenName
        Surname           = $u.Surname
        MatchedDomains    = $domains
        Error             = $null
    }
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
        $results = @(foreach ($c in $candidates) { Get-AdCandidateResult -Candidate $c -Domain $script:Domains })

        $outcome = Resolve-CloudAccountOwnerOutcome -CandidateResult $results -CloudDisplayName $acct.DisplayName

        $matchedDomains = (($results | Where-Object { $_.MatchedDomains } | ForEach-Object { $_.MatchedDomains -split ' \| ' }) | Sort-Object -Unique) -join ' | '

        # Set-StrictMode makes a property read on an empty pipeline result throw, so hold the
        # object first and only reach into it when there actually is one.
        $errored = @($results | Where-Object { $_.Error })
        $lookupError = if ($errored.Count -gt 0) { $errored[0].Error } else { $null }

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
            SearchedDomains        = ($script:Domains -join ' | ')
            MatchedDomains         = $matchedDomains
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

    Write-Host ""
    Write-Host "Searched domains: $($script:Domains -join ', ')" -ForegroundColor White
    Write-Host "Every number above describes THIS set. A domain you did not check was never asked." -ForegroundColor DarkGray

    $ambiguous = @($script:Rows | Where-Object { $_.CandidateStatuses -like '*=Ambiguous*' })
    $multi = @($script:Rows | Where-Object { $_.MatchedDomains -and $_.MatchedDomains -match '\|' })

    Write-Host ""
    if ($ambiguous.Count -eq 0) {
        Write-Ok "Collisions across the searched set: none. No candidate key matched two distinct users."
    }
    else {
        Write-Warn "Collisions across the searched set: $($ambiguous.Count) accounts whose candidate key matched two distinct users. Those resolve to nothing and refuse."
    }

    if ($multi.Count -gt 0) {
        Write-Warn "$($multi.Count) accounts matched in more than one domain (see MatchedDomains). Check whether those are the same person seen twice or a real name clash."
    }

    Write-Host ""
    Write-Host "Detail: $CsvPath"
}

if ($PlanOnly) {
    Write-Host ""
    Write-Plan "Plan mode: nothing was queried and no file was written."
}
