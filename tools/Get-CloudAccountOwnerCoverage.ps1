#Requires -Version 7.0
<#
.SYNOPSIS
    READ-ONLY survey: how many IN-SCOPE Entra cloud-only accounts can have their on-premises
    owner derived reliably enough to email a new password to?

.DESCRIPTION
    S0 of docs/CloudPasswordReset-Plan.md, and a hard gate on the rest of it. The Cloud Password
    Reset module never shows the operator the password; it mails it to the account's owner at
    their on-premises mailbox, and the owner is DERIVED at each reset because the owner rejected
    a stored mapping ("cannot store it... we're not going to create an instantly stale map").
    The whole design therefore stands or falls on one number nobody has measured: what fraction
    of the real population actually resolves.

    REBUILT 2026-09-11 after the first run returned a void 9.6%. Two defects made that number
    meaningless, and both are fixed here:

    1. WRONG DENOMINATOR. It surveyed every cloud-only account in the tenant. The owner: "we
       also have customer accounts in entra, and those are out of scope... the only things
       in-scope here are CLD admin accounts that need to be reset. customer accounts, conf
       rooms, etc., are not in-scope." Scope is now taken from what an account CAN DO, not from
       what it is called: a cloud-only account holding a directory role, eligible for one
       through PIM, or sitting in a role-assignable group. Naming conventions drifted over the
       years and cannot define the population; role-holding is a fact the directory answers.
       -Scope AllCloudOnly restores the old denominator for comparison.

    2. WRONG COMPARISON. It derived one key from the UPN local part and compared it against
       WHOLE userPrincipalName and mail values, so it could only match when the local part
       happened to equal the sAMAccountName. The owner's own account proved it: cloud key
       "michael.coelho", on-premises UPN "michael.coelho@<domain>", sAMAccountName "mcoelho" -
       a match that any correct comparison finds and this one could not. The derivation is now
       three sources that must AGREE (employee id, display name, UPN local part compared
       LOCAL-PART-TO-LOCAL-PART), and the survey reports which source answered rather than one
       flat percentage.

    IT MAKES NO CHANGES. It reads Graph and AD and writes one CSV to disk. It never touches a
    password, never calls PATCH, and needs no elevation.

.PARAMETER PlanOnly
    Describe every step without running any of it. Nothing is queried and no file is written.

.PARAMETER Scope
    AdminOnly (default) surveys only cloud-only accounts that hold a directory role, are
    PIM-eligible for one, or belong to a role-assignable group. That is the population the
    module exists to serve. AllCloudOnly surveys every cloud-only non-guest account, which is
    what the void first run measured; useful only for comparing the two denominators.

.PARAMETER SampleSize
    0 (default) surveys every in-scope account. A positive number surveys that many, for a
    faster first look. Sampling gives an estimate, not the answer the gate needs.

.PARAMETER CsvPath
    Per-account detail output. Defaults to CloudAccountOwnerCoverage-<yyyyMMdd-HHmmss>.csv in
    the current directory.

.PARAMETER SamplesPerClass
    How many example accounts to print per failure class. Default 5. Only the cloud UPN is
    printed; owner mail addresses stay in the CSV.

.PARAMETER SearchDomain
    The domains to search for owners, by DNS name. Required for a real run. These stand in for
    the module's operator-chosen Search Domains setting, so the survey measures the scope that
    will actually ship. Pass only domains whose accounts sync to Entra, or that you otherwise
    want searched - not every domain a trust makes reachable.

.PARAMETER ListDomains
    List the domains available to this host (forest domains plus trusted domains) and exit. Use
    this to decide what to pass to -SearchDomain. Queries the directory; writes nothing.

.NOTES
    Scope is chosen, not assumed. ADDirectorySearchService.ValidateExists issues its USER query
    WITHOUT a -Server, so today it binds whichever single domain the app host happens to be
    joined to (the -Server routing at ADDirectorySearchService.cs:355 is scoped to the Group
    kind). That is an accident of deployment. A forest-wide sweep is not the answer either: the
    estate has domains and trusts with no relationship to Entra, so searching everything queries
    irrelevant directories and manufactures collisions that are not real ones. The settled
    design, and what this survey reproduces, is an operator-chosen set of domains - see
    .agents/decisions.md 2026-09-11 "Searched domains are an operator setting".

    ONE query per checked domain, not one per source. Every arm of every source goes into a
    single OR'd exact-match filter (New-CloudAccountOwnerFilter), and which source answered is
    then worked out in memory from attributes the returned rows already carry. That is roughly
    half the query cost of the superseded design, which issued one lookup per candidate key.
    The survey walks its domains sequentially because it is measuring coverage, not latency; the
    C# resolver in S1 issues them concurrently, which is where the plan's wall-clock claim
    lives. Do not read this loop as evidence about run-time speed either way.

    If ANY checked domain cannot be reached, the account is Unavailable rather than NotFound -
    an unreachable domain could be holding the second match, so absence cannot be concluded from
    it. Same rule if a domain's result set comes back at the cap: a truncated answer cannot
    prove uniqueness.

    Output: the CSV, plus a console summary answering the gate question directly.
#>
[CmdletBinding()]
param(
    [switch] $PlanOnly,
    [ValidateSet('AdminOnly', 'AllCloudOnly')]
    [string] $Scope = 'AdminOnly',
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
        Discovered from the directory at run time - no name is hard-coded, defaulted or typed.
        This is the same enumeration the module's Search Domains setting will offer as
        checkboxes.
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
#
# The two ambiguity classes are deliberately separate. AmbiguousWithinSource means one source
# named two people; AmbiguousSourceDisagreement means two sources each named a different person.
# Those are different defects with different fixes, and the owner is choosing a design on these
# numbers, so folding them together would hide the thing worth knowing.
$OutcomeClasses = @(
    'Resolved',
    'UnresolvedNoMatch',
    'UnresolvedNoMailbox',
    'UnresolvedNameMismatch',
    'UnresolvedOwnerDisabled',
    'AmbiguousWithinSource',
    'AmbiguousSourceDisagreement',
    'Unavailable'
)

# A domain returning this many rows is treated as truncated, and a truncated answer cannot prove
# uniqueness. Set well above any plausible legitimate match count so it only fires on a
# genuinely non-selective filter (an employee id shared by hundreds of accounts, say), where the
# right answer is to say so rather than to pick from the first page.
$ResultCap = 100

$script:CloudAccounts = @()
$script:Rows = @()
$script:Domains = @()
$script:PrivilegedReason = @{}
$script:PimReadable = $true
$script:PimNote = $null

# ---------------------------------------------------------------------------------------------
# Step 1 - Graph, read-only.
# ---------------------------------------------------------------------------------------------

Invoke-PlanOrAction "Use the existing Microsoft Graph connection, or sign in read-only (User.Read.All, RoleManagement.Read.Directory, Group.Read.All)" {
    if (-not (Get-Module -ListAvailable -Name Microsoft.Graph.Users)) {
        Write-Fail "The Microsoft.Graph.Users module is not installed. Install-Module Microsoft.Graph -Scope CurrentUser"
    }
    Import-Module Microsoft.Graph.Users

    # An existing session is reused rather than replaced: the estate connects app-only through
    # its own tooling, and re-running Connect-MgGraph here would tear that down for a delegated
    # sign-in this survey does not need. Read scopes are all it uses either way.
    $ctx = Get-MgContext
    if (-not $ctx) {
        Connect-MgGraph -Scopes 'User.Read.All', 'RoleManagement.Read.Directory', 'Group.Read.All' -NoWelcome
        $ctx = Get-MgContext
    }
    if (-not $ctx) { Write-Fail "No Microsoft Graph context, and Connect-MgGraph returned none." }

    $who = if ($ctx.Account) { $ctx.Account } else { "app $($ctx.ClientId) ($($ctx.AuthType))" }
    Write-Ok "Graph tenant $($ctx.TenantId) as $who"
}

function Get-GraphCollection {
    <#
        Page a Graph collection through Invoke-MgGraphRequest rather than a typed cmdlet, so the
        scoping step depends only on Microsoft.Graph.Authentication. The role and PIM cmdlets
        live in modules an estate may not have installed, and a missing module would fail the
        survey for a reason that has nothing to do with what it is measuring.
    #>
    param(
        [Parameter(Mandatory)][string] $Uri,
        [hashtable] $Headers
    )

    $items = [System.Collections.Generic.List[object]]::new()
    $next = $Uri

    while ($next) {
        $page = if ($Headers) {
            Invoke-MgGraphRequest -Method GET -Uri $next -Headers $Headers -OutputType PSObject -ErrorAction Stop
        }
        else {
            Invoke-MgGraphRequest -Method GET -Uri $next -OutputType PSObject -ErrorAction Stop
        }

        foreach ($i in @(Get-ObjectPropertyValue -InputObject $page -Name 'value')) {
            if ($null -ne $i) { $items.Add($i) }
        }

        $next = [string](Get-ObjectPropertyValue -InputObject $page -Name '@odata.nextLink')
        if ([string]::IsNullOrWhiteSpace($next)) { $next = $null }
    }

    return , $items.ToArray()
}

function Add-PrivilegedPrincipal {
    param([Parameter(Mandatory)][AllowNull()][object] $Id, [Parameter(Mandatory)][string] $Reason)

    $key = [string]$Id
    if ([string]::IsNullOrWhiteSpace($key)) { return }

    if (-not $script:PrivilegedReason.ContainsKey($key)) {
        $script:PrivilegedReason[$key] = [System.Collections.Generic.List[string]]::new()
    }
    if (-not $script:PrivilegedReason[$key].Contains($Reason)) {
        $script:PrivilegedReason[$key].Add($Reason)
    }
}

Invoke-PlanOrAction "Identify the privileged principals that define scope: active directory roles, role-assignable groups, PIM eligibility" {
    if ($Scope -ne 'AdminOnly') {
        Write-Warn "-Scope AllCloudOnly: skipping the privilege query. This reproduces the void first run's denominator, which includes customer accounts, conference rooms and service principals' user objects."
        return
    }

    # --- Active role assignments. -------------------------------------------------------------
    # /directoryRoles returns only ACTIVATED roles, which is the correct set: a role that has
    # never been activated in this tenant has no members to find.
    $roleCount = 0
    $roles = Get-GraphCollection -Uri '/v1.0/directoryRoles?$select=id,displayName'
    foreach ($role in $roles) {
        $roleId = [string](Get-ObjectPropertyValue -InputObject $role -Name 'id')
        $roleName = [string](Get-ObjectPropertyValue -InputObject $role -Name 'displayName')
        if ([string]::IsNullOrWhiteSpace($roleId)) { continue }

        # The microsoft.graph.user cast is what makes this a USER list. Without it /members
        # returns service principals and groups too, and counting those as in-scope accounts
        # would inflate the denominator with things that have no owner to mail.
        $members = Get-GraphCollection -Uri "/v1.0/directoryRoles/$roleId/members/microsoft.graph.user?`$select=id"
        foreach ($m in $members) {
            Add-PrivilegedPrincipal -Id (Get-ObjectPropertyValue -InputObject $m -Name 'id') -Reason "ActiveRole:$roleName"
        }
        $roleCount++
    }
    Write-Ok "$roleCount activated directory roles enumerated"

    # --- Role-assignable groups. --------------------------------------------------------------
    # Membership of one of these is a standing grant of whatever roles the group holds, so its
    # members are in scope whether or not they currently hold a role directly.
    $groupCount = 0
    try {
        $groups = Get-GraphCollection `
            -Uri '/v1.0/groups?$filter=isAssignableToRole eq true&$select=id,displayName&$count=true' `
            -Headers @{ ConsistencyLevel = 'eventual' }

        foreach ($g in $groups) {
            $gid = [string](Get-ObjectPropertyValue -InputObject $g -Name 'id')
            $gname = [string](Get-ObjectPropertyValue -InputObject $g -Name 'displayName')
            if ([string]::IsNullOrWhiteSpace($gid)) { continue }

            $gm = Get-GraphCollection -Uri "/v1.0/groups/$gid/transitiveMembers/microsoft.graph.user?`$select=id"
            foreach ($m in $gm) {
                Add-PrivilegedPrincipal -Id (Get-ObjectPropertyValue -InputObject $m -Name 'id') -Reason "RoleAssignableGroup:$gname"
            }
            $groupCount++
        }
        Write-Ok "$groupCount role-assignable groups enumerated"
    }
    catch {
        # Fail loudly and keep going, but record it: a group set that could not be read is
        # accounts missing from the denominator, and the summary must say so on its face.
        $script:PimReadable = $false
        $script:PimNote = "Role-assignable groups could not be read: $($_.Exception.Message)"
        Write-Warn $script:PimNote
    }

    # --- PIM eligibility. ---------------------------------------------------------------------
    # An eligible-only admin holds no role right now, so transitiveMemberOf and /directoryRoles
    # both miss them entirely - they are exactly the population a naive scoping query loses.
    # This read needs RoleEligibilitySchedule.Read.Directory and Entra ID P2; where either is
    # absent the survey reports active-only and says so, rather than quietly undercounting.
    try {
        $elig = Get-GraphCollection -Uri '/v1.0/roleManagement/directory/roleEligibilitySchedules?$select=principalId'
        foreach ($e in $elig) {
            Add-PrivilegedPrincipal -Id (Get-ObjectPropertyValue -InputObject $e -Name 'principalId') -Reason 'PimEligible'
        }
        Write-Ok "$($elig.Count) PIM role-eligibility schedules read"
    }
    catch {
        $script:PimReadable = $false
        $script:PimNote = "PIM role eligibility could not be read ($($_.Exception.Message)). Active assignments only; eligible-only admin accounts are UNDERCOUNTED in every number below."
        Write-Warn $script:PimNote
    }

    Write-Ok "$($script:PrivilegedReason.Count) distinct privileged principals (all identity types, before the cloud-only filter)"
}

Invoke-PlanOrAction "Enumerate every cloud-only user in the tenant (onPremisesSyncEnabled not true, userType Member) and keep the in-scope ones" {
    # employeeId is source 1. It is read here so the derivation can use it without a second
    # per-account Graph call; a blank one simply means source 1 stays silent for that account.
    $props = @('Id', 'DisplayName', 'UserPrincipalName', 'AccountEnabled', 'OnPremisesSyncEnabled', 'UserType', 'EmployeeId')

    # Filtered client-side, not server-side, on purpose: onPremisesSyncEnabled is NULL rather
    # than false for a cloud-only account, and a Graph $filter on a null-valued property is the
    # kind of thing that silently returns an empty page. Fail-closed reading: only a definite
    # $true counts as synced, so an unreadable value lands in the survey rather than being
    # dropped from it.
    $all = Get-MgUser -All -Property $props -ConsistencyLevel eventual | Select-Object $props

    $cloud = @($all | Where-Object {
            $_.OnPremisesSyncEnabled -ne $true -and $_.UserType -ne 'Guest'
        })

    $tenantCloudCount = $cloud.Count

    if ($Scope -eq 'AdminOnly') {
        # Scope by what the account CAN DO. The owner ruled out scoping by name: "these accounts
        # were created at different times with different naming conventions", so any -CLD or
        # admin- pattern would both miss real admin accounts and sweep in customer accounts that
        # happen to match. Role-holding is a fact the directory answers.
        $cloud = @($cloud | Where-Object { $script:PrivilegedReason.ContainsKey([string]$_.Id) })
    }

    if ($SampleSize -gt 0 -and $cloud.Count -gt $SampleSize) {
        Write-Warn "Sampling $SampleSize of $($cloud.Count) in-scope accounts. This is an estimate; the gate needs a full run."
        $cloud = @($cloud | Get-Random -Count $SampleSize)
    }

    $script:CloudAccounts = $cloud
    Write-Ok "$($all.Count) users in tenant, $tenantCloudCount cloud-only non-guest, $($cloud.Count) in scope ($Scope)"
}

# ---------------------------------------------------------------------------------------------
# Step 2 - Active Directory, read-only, ONE pooled lookup per domain per account.
# ---------------------------------------------------------------------------------------------

Invoke-PlanOrAction "Load the ActiveDirectory module and verify every requested search domain answers" {
    if (-not (Get-Module -ListAvailable -Name ActiveDirectory)) {
        Write-Fail "The ActiveDirectory module is not installed. Add RSAT-AD-PowerShell, or run this from a host that has it."
    }
    Import-Module ActiveDirectory

    # Checked up front rather than per-account: a domain that cannot be reached would turn every
    # single account Unavailable, and finding that out after several hundred lookups wastes the
    # run.
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

# The attributes every source needs. proxyAddresses carries the primary SMTP in a shape whose
# local part can be compared against the cloud key; the three id attributes are OR'd because
# which one is authoritative is an environment fact this code may not assume. employeeType is
# deliberately absent - it holds a worker-class code (CWK, contingent worker), which describes a
# CATEGORY of people, so an arm on it would match everyone in that class at once.
$AdProperties = @(
    'DisplayName', 'DistinguishedName', 'SamAccountName', 'UserPrincipalName', 'mail',
    'GivenName', 'Surname', 'Enabled', 'proxyAddresses',
    'employeeID', 'employeeNumber', 'extensionAttribute1'
)

function Get-AdOwnerMatch {
    <#
        One pooled exact-match lookup per checked domain, using the single OR'd filter that
        carries every source's arms (New-CloudAccountOwnerFilter). Every returned row is then
        attributed to the sources it satisfies, in memory, from attributes the row already
        carries - so agreement costs no query time at all.

        Two rules do the safety work here, and both survived the rebuild unchanged in intent:

        A thrown lookup is Unavailable, never NotFound. An exception means the question never
        reached the directory, which is not evidence the user is absent - the single most
        important distinction in this whole survey, and the one a naive try/catch gets wrong.

        And one bad domain poisons the whole account, not just its own result. Concluding "only
        one match" while a checked domain stayed silent is exactly the wrong answer: the silent
        domain is where the second match would have been. A truncated result set is treated the
        same way, because a capped answer cannot prove uniqueness either.
    #>
    param([Parameter(Mandatory)][psobject] $TermSet, [Parameter(Mandatory)][string[]] $Domain)

    $filter = New-CloudAccountOwnerFilter -TermSet $TermSet

    if ([string]::IsNullOrWhiteSpace($filter)) {
        # No source had anything to ask. Returning an empty filter here would match the entire
        # directory, so the only correct move is to issue no query at all.
        return [pscustomobject]@{
            Rows = @(); Unavailable = $false; Error = $null
            MatchedDomains = @(); Filter = $null
        }
    }

    $rows = [System.Collections.Generic.List[object]]::new()
    $matchedIn = [System.Collections.Generic.List[string]]::new()

    foreach ($d in $Domain) {
        try {
            $found = @(Get-ADUser -Server $d -LDAPFilter $filter -ResultSetSize $ResultCap -Properties $AdProperties -ErrorAction Stop)
        }
        catch {
            return [pscustomobject]@{
                Rows = @(); Unavailable = $true; Error = "$d : $($_.Exception.Message)"
                MatchedDomains = @(); Filter = $filter
            }
        }

        if ($found.Count -ge $ResultCap) {
            return [pscustomobject]@{
                Rows           = @(); Unavailable = $true
                Error          = "$d : result set hit the $ResultCap-row cap; a truncated answer cannot prove uniqueness."
                MatchedDomains = @(); Filter = $filter
            }
        }

        foreach ($u in $found) {
            $rows.Add([pscustomobject]@{
                    DistinguishedName = (Get-ObjectPropertyValue -InputObject $u -Name 'DistinguishedName')
                    SamAccountName    = (Get-ObjectPropertyValue -InputObject $u -Name 'SamAccountName')
                    Email             = (Get-ObjectPropertyValue -InputObject $u -Name 'mail')
                    Enabled           = (Get-ObjectPropertyValue -InputObject $u -Name 'Enabled')
                    GivenName         = (Get-ObjectPropertyValue -InputObject $u -Name 'GivenName')
                    Surname           = (Get-ObjectPropertyValue -InputObject $u -Name 'Surname')
                    DisplayName       = (Get-ObjectPropertyValue -InputObject $u -Name 'DisplayName')
                    MatchedDomain     = $d
                    Sources           = (Get-CloudAccountOwnerSourceMatch -TermSet $TermSet -DirectoryUser $u)
                })
        }

        if ($found.Count -gt 0) { $matchedIn.Add($d) }
    }

    return [pscustomobject]@{
        Rows           = $rows.ToArray()
        Unavailable    = $false
        Error          = $null
        MatchedDomains = @($matchedIn | Sort-Object -Unique)
        Filter         = $filter
    }
}

Invoke-PlanOrAction "Derive candidates from all three sources and resolve an owner for each in-scope account (read-only AD lookups)" {
    $rows = [System.Collections.Generic.List[object]]::new()
    $i = 0
    $total = $script:CloudAccounts.Count

    foreach ($acct in $script:CloudAccounts) {
        $i++
        if ($total -gt 0 -and ($i % 25 -eq 0 -or $i -eq $total)) {
            Write-Progress -Activity 'Resolving owners' -Status "$i / $total" -PercentComplete ([int](100 * $i / $total))
        }

        $termSet = New-CloudAccountOwnerTermSet `
            -UserPrincipalName $acct.UserPrincipalName `
            -DisplayName $acct.DisplayName `
            -EmployeeId ([string](Get-ObjectPropertyValue -InputObject $acct -Name 'EmployeeId'))

        $match = Get-AdOwnerMatch -TermSet $termSet -Domain $script:Domains

        $outcome = Resolve-CloudAccountOwnerOutcome `
            -MatchedUser $match.Rows `
            -CloudDisplayName $acct.DisplayName `
            -LookupUnavailable:$match.Unavailable `
            -UnavailableReason $match.Error

        $reasonList = if ($script:PrivilegedReason.ContainsKey([string]$acct.Id)) {
            ($script:PrivilegedReason[[string]$acct.Id] -join ' | ')
        }
        else { '' }

        $rows.Add([pscustomobject]@{
                CloudUserPrincipalName = $acct.UserPrincipalName
                CloudDisplayName       = $acct.DisplayName
                CloudObjectId          = $acct.Id
                CloudAccountEnabled    = $acct.AccountEnabled
                CloudEmployeeId        = (Get-ObjectPropertyValue -InputObject $acct -Name 'EmployeeId')
                InScopeReason          = $reasonList
                TermKeys               = (@($termSet.Keys) -join ' | ')
                TermDisplayNames       = (@($termSet.DisplayNames) -join ' | ')
                DirectoryRowsReturned  = @($match.Rows).Count
                Outcome                = $outcome.Outcome
                Reason                 = $outcome.Reason
                AnsweringSources       = $outcome.AnsweringSources
                SourceDetail           = $outcome.SourceDetail
                IdArmDiscarded         = $outcome.IdArmDiscarded
                UnattributedRows       = $outcome.UnattributedRows
                OwnerSamAccountName    = $outcome.OwnerSamAccountName
                OwnerEmail             = $outcome.OwnerEmail
                OwnerEnabled           = $outcome.OwnerEnabled
                OwnerDistinguishedName = $outcome.OwnerDistinguishedName
                OwnerMatchedDomain     = $outcome.OwnerMatchedDomain
                SearchedDomains        = ($script:Domains -join ' | ')
                MatchedDomains         = (@($match.MatchedDomains) -join ' | ')
                LookupError            = $match.Error
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
        Write-Warn "No in-scope accounts were surveyed. The gate cannot be answered from this run."
        if ($Scope -eq 'AdminOnly') {
            Write-Warn "Scope was AdminOnly. If the privilege queries returned nothing, check the app registration's granted roles before concluding the population is empty."
        }
        return
    }

    Write-Host ""
    Write-Host "Cloud Password Reset - owner coverage (scope: $Scope)" -ForegroundColor White
    Write-Host ("=" * 62)
    Write-Host ("{0,-30} {1,7} {2,8}" -f 'Outcome', 'Count', 'Percent')

    foreach ($class in $OutcomeClasses) {
        $n = @($script:Rows | Where-Object { $_.Outcome -eq $class }).Count
        $pct = 100.0 * $n / $total
        $color = if ($class -eq 'Resolved') { 'Green' } elseif ($n -eq 0) { 'DarkGray' } else { 'Yellow' }
        Write-Host ("{0,-30} {1,7} {2,7:N1}%" -f $class, $n, $pct) -ForegroundColor $color
    }

    Write-Host ("=" * 62)
    Write-Host ("{0,-30} {1,7}" -f 'Total', $total)

    $resolved = @($script:Rows | Where-Object { $_.Outcome -eq 'Resolved' })
    Write-Host ""
    Write-Host ("Resolved rate: {0:N1}%  ({1} of {2})" -f (100.0 * $resolved.Count / $total), $resolved.Count, $total) -ForegroundColor White
    Write-Host "The owner sets the threshold. A low rate makes the reveal permission the normal path,"
    Write-Host "which means the design is wrong and the plan is replaced rather than amended."

    # --- Per source, not one percentage. ------------------------------------------------------
    # The whole point of the rebuild is that three sources vote. A flat coverage number hides
    # which of them is actually carrying the result, and therefore hides whether dropping one
    # would cost anything. Two views: which sources answered on the accounts that RESOLVED, and
    # what each source could have answered across every surveyed account.
    Write-Host ""
    Write-Host "Answering sources on resolved accounts" -ForegroundColor White
    Write-Host ("-" * 62)
    if ($resolved.Count -eq 0) {
        Write-Host "  (none resolved)" -ForegroundColor DarkGray
    }
    else {
        $byCombo = $resolved | Group-Object -Property AnsweringSources | Sort-Object -Property Count -Descending
        foreach ($g in $byCombo) {
            $label = if ([string]::IsNullOrWhiteSpace($g.Name)) { '(none)' } else { $g.Name }
            Write-Host ("  {0,-34} {1,6} {2,7:N1}%" -f $label, $g.Count, (100.0 * $g.Count / $resolved.Count))
        }
        Write-Host ""
        foreach ($s in 'Id', 'DisplayName', 'UpnKey') {
            $n = @($resolved | Where-Object { $_.AnsweringSources -like "*$s*" }).Count
            Write-Host ("  {0,-34} {1,6} {2,7:N1}% of resolved" -f "$s answered", $n, (100.0 * $n / $resolved.Count))
        }
        $soleUpn = @($resolved | Where-Object { $_.AnsweringSources -eq 'UpnKey' }).Count
        Write-Host ""
        Write-Host ("  {0} resolved on the UPN key ALONE, with no corroborating source." -f $soleUpn) -ForegroundColor $(if ($soleUpn -gt 0) { 'Yellow' } else { 'DarkGray' })
        Write-Host "  Those are the ones the name-corroboration floor is the only thing standing behind." -ForegroundColor DarkGray
    }

    # --- Source health across everything surveyed. --------------------------------------------
    $withEmpId = @($script:Rows | Where-Object { -not [string]::IsNullOrWhiteSpace($_.CloudEmployeeId) }).Count
    $idDiscarded = @($script:Rows | Where-Object { $_.IdArmDiscarded -eq $true }).Count
    $unattributed = @($script:Rows | Where-Object { [int]$_.UnattributedRows -gt 0 }).Count
    $disagree = @($script:Rows | Where-Object { $_.Outcome -eq 'AmbiguousSourceDisagreement' }).Count

    Write-Host ""
    Write-Host "Source health across all $total surveyed accounts" -ForegroundColor White
    Write-Host ("-" * 62)
    Write-Host ("  {0,-46} {1,6} {2,6:N1}%" -f 'employeeId populated on the cloud account', $withEmpId, (100.0 * $withEmpId / $total))
    Write-Host ("  {0,-46} {1,6}" -f 'id arm discarded as non-unique (not an id)', $idDiscarded)
    Write-Host ("  {0,-46} {1,6}" -f 'accounts with rows matching no source on re-read', $unattributed)
    Write-Host ("  {0,-46} {1,6}" -f 'sources named DIFFERENT people (disagreement)', $disagree)
    if ($unattributed -gt 0) {
        Write-Warn "Rows the filter matched but the in-memory comparison rejected mean the two have drifted apart. Investigate before trusting any number above."
    }
    if ($disagree -gt 0) {
        Write-Warn "Source disagreement is the class that decides whether agreement is the right mechanism. Read those rows in the CSV."
    }

    if ($SamplesPerClass -gt 0) {
        foreach ($class in $OutcomeClasses) {
            if ($class -eq 'Resolved') { continue }
            $examples = @($script:Rows | Where-Object { $_.Outcome -eq $class } | Select-Object -First $SamplesPerClass)
            if ($examples.Count -eq 0) { continue }
            Write-Host ""
            Write-Host "$class - first $($examples.Count):" -ForegroundColor Yellow
            foreach ($e in $examples) {
                Write-Host ("  {0}  [{1}] {2}" -f $e.CloudUserPrincipalName, $e.SourceDetail, $e.Reason)
            }
        }
    }

    Write-Host ""
    Write-Host "Searched domains: $($script:Domains -join ', ')" -ForegroundColor White
    Write-Host "Every number above describes THIS set. A domain you did not check was never asked." -ForegroundColor DarkGray

    if ($Scope -eq 'AdminOnly' -and -not $script:PimReadable) {
        Write-Host ""
        Write-Warn "SCOPE IS INCOMPLETE. $($script:PimNote)"
        Write-Warn "Treat the denominator as a floor, not a count."
    }

    $multi = @($script:Rows | Where-Object { $_.MatchedDomains -and $_.MatchedDomains -match '\|' })
    if ($multi.Count -gt 0) {
        Write-Host ""
        Write-Warn "$($multi.Count) accounts matched in more than one domain (see MatchedDomains). Distinctness is by distinguishedName, so the same person seen twice through a trust is already one match; these are worth a look for real name clashes."
    }

    Write-Host ""
    Write-Host "Detail: $CsvPath"
}

if ($PlanOnly) {
    Write-Host ""
    Write-Plan "Plan mode: nothing was queried and no file was written."
}
