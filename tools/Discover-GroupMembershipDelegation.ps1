<#
.SYNOPSIS
  READ-ONLY discovery: who can edit the membership of groups in a given OU, and HOW.

.DESCRIPTION
  For the self-service group feature (GM-3) we need to know whether "people who can edit a
  group" is captured by the managedBy manager field alone, or whether L1/L2 get edit rights
  some other way (OU-level delegation, a direct ACE, or membership in a delegated helpdesk
  group). This script reads each group's security descriptor and reports every Allow entry
  that grants membership-edit capability, classifying:

    - the RIGHT (GenericAll / GenericWrite / WriteAllProps / WriteMember / SelfMembership*)
    - whether it is INHERITED (OU delegation) or set DIRECTLY on the group
    - whether the trustee is a USER or a GROUP (a group = delegated-via-group, e.g. Helpdesk)
    - whether the trustee IS the group's managedBy manager

  * SelfMembership only lets the trustee add/remove THEMSELVES - it is NOT enough to manage
    other members, and is reported separately so it is not mistaken for full edit rights.

  It makes NO changes. It only reads. Run it as a user that can read group ACLs (any
  authenticated domain user usually can).

.NOTES
  Output: a per-ACE CSV per OU (GroupMembershipDelegation-<n>.csv in the current directory)
  plus a summary printed to the console that answers the A-vs-B scoping question directly.
#>

#Requires -Modules ActiveDirectory
[CmdletBinding()]
param(
    [string[]] $SearchBase = @(
        'OU=Groups,OU=NWD,OU=AMER,DC=ad,DC=analog,DC=com',
        'OU=SYNC,OU=Distribution Lists,OU=Recipients,OU=Analog,OU=Exchange,DC=ad,DC=analog,DC=com'
    ),
    # 0 = scan every group in each OU; a positive number samples that many per OU (faster first look).
    [int] $SampleSize = 0
)

$ErrorActionPreference = 'Stop'
Import-Module ActiveDirectory -ErrorAction Stop

$adr = [System.DirectoryServices.ActiveDirectoryRights]

Write-Host "Building schema/extended-rights GUID map..." -ForegroundColor Cyan
$rootDSE  = Get-ADRootDSE
$schemaNC = $rootDSE.schemaNamingContext
$configNC = $rootDSE.configurationNamingContext

# GUID -> friendly name, so an ACE's ObjectType (a raw GUID) reads as e.g. "member".
$guidMap = @{}
Get-ADObject -SearchBase $schemaNC -LDAPFilter '(schemaIDGUID=*)' -Properties lDAPDisplayName, schemaIDGUID |
    ForEach-Object { $guidMap[([Guid]$_.schemaIDGUID).Guid.ToLower()] = $_.lDAPDisplayName }
Get-ADObject -SearchBase "CN=Extended-Rights,$configNC" -LDAPFilter '(rightsGuid=*)' -Properties displayName, rightsGuid |
    ForEach-Object { $guidMap[$_.rightsGuid.ToLower()] = $_.displayName }

# Caches so we resolve each trustee SID / manager DN at most once.
$sidInfoCache = @{}   # sid string -> [pscustomobject]@{ Name; Class }
$dnSidCache   = @{}   # DN -> sid string (for managedBy)

function Resolve-SidInfo([string] $sid) {
    if ($sidInfoCache.ContainsKey($sid)) { return $sidInfoCache[$sid] }
    $name  = $sid
    $class = 'unknown'
    try {
        $obj = Get-ADObject -LDAPFilter "(objectSid=$sid)" -Properties objectClass, name -ErrorAction Stop | Select-Object -First 1
        if ($obj) { $name = $obj.name; $class = ($obj.objectClass | Select-Object -Last 1) }
    } catch { }
    if ($class -eq 'unknown') {
        # Well-known / builtin SIDs (SYSTEM, Domain Admins, etc.) - name only, no domain object.
        try { $name = ([System.Security.Principal.SecurityIdentifier]$sid).Translate([System.Security.Principal.NTAccount]).Value } catch { }
        $class = 'builtin-or-unresolved'
    }
    $info = [pscustomobject]@{ Name = $name; Class = $class }
    $sidInfoCache[$sid] = $info
    return $info
}

function Resolve-DnSid([string] $dn) {
    if ([string]::IsNullOrWhiteSpace($dn)) { return $null }
    if ($dnSidCache.ContainsKey($dn)) { return $dnSidCache[$dn] }
    $sid = $null
    try { $sid = (Get-ADObject -Identity $dn -Properties objectSid -ErrorAction Stop).objectSid.Value } catch { }
    $dnSidCache[$dn] = $sid
    return $sid
}

function Get-MembershipRightKind($ace) {
    # Returns a classification string, or $null if this ACE does not grant membership editing.
    $ot = ''
    if ($ace.ObjectType) { $ot = $ace.ObjectType.Guid.ToLower() }
    $otName = $guidMap[$ot]
    $allProps = ($ot -eq '' -or $ot -eq '00000000-0000-0000-0000-000000000000')

    if ($ace.ActiveDirectoryRights.HasFlag($adr::GenericAll))                                   { return 'GenericAll' }
    if ($ace.ActiveDirectoryRights.HasFlag($adr::GenericWrite))                                 { return 'GenericWrite' }
    if ($ace.ActiveDirectoryRights.HasFlag($adr::WriteProperty) -and $allProps)                 { return 'WriteAllProps' }
    if ($ace.ActiveDirectoryRights.HasFlag($adr::WriteProperty) -and $otName -eq 'member')      { return 'WriteMember' }
    if ($ace.ActiveDirectoryRights.HasFlag($adr::Self)          -and $otName -eq 'Self-Membership') { return 'SelfMembership' }
    return $null
}

$allRows = New-Object System.Collections.Generic.List[object]
$ouIndex = 0

foreach ($ou in $SearchBase) {
    $ouIndex++
    Write-Host "`nScanning OU [$ouIndex/$($SearchBase.Count)]: $ou" -ForegroundColor Cyan

    $getParams = @{ SearchBase = $ou; Filter = '*'; Properties = @('managedBy') }
    $groups = Get-ADGroup @getParams
    if ($SampleSize -gt 0) { $groups = $groups | Select-Object -First $SampleSize }

    $total = @($groups).Count
    Write-Host "  $total group(s) to inspect." -ForegroundColor DarkGray
    $i = 0
    $diagShown = $false

    foreach ($grp in $groups) {
        $i++
        if ($i % 100 -eq 0) { Write-Host "  ...$i/$total" -ForegroundColor DarkGray }

        $managerSid = Resolve-DnSid $grp.managedBy
        # Read the DACL via the AD: drive - more reliable than Get-ADGroup -Properties
        # nTSecurityDescriptor, which can return the descriptor with an empty .Access.
        $sd = $null
        try { $sd = Get-Acl -Path ("AD:\" + $grp.DistinguishedName) -ErrorAction Stop } catch { }

        if (-not $diagShown) {
            $accessCount = if ($sd) { @($sd.Access).Count } else { 'NULL descriptor' }
            Write-Host "  [diag] first group '$($grp.Name)': ACE count = $accessCount" -ForegroundColor Magenta
            $diagShown = $true
        }
        if (-not $sd) { continue }

        foreach ($ace in $sd.Access) {
            if ($ace.AccessControlType -ne 'Allow') { continue }
            $kind = Get-MembershipRightKind $ace
            if (-not $kind) { continue }

            $sid = $null
            try { $sid = $ace.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value } catch { }
            if (-not $sid) { $sid = $ace.IdentityReference.Value }

            $info = Resolve-SidInfo $sid

            $allRows.Add([pscustomobject]@{
                OU          = $ou
                Group       = $grp.Name
                Trustee     = $info.Name
                TrusteeType = $info.Class
                RightKind   = $kind
                Inherited   = [bool]$ace.IsInherited
                IsManager   = ($managerSid -and $sid -eq $managerSid)
                TrusteeSid  = $sid
            })
        }
    }

    $csv = Join-Path (Get-Location) "GroupMembershipDelegation-$ouIndex.csv"
    $allRows | Where-Object OU -eq $ou | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8
    Write-Host "  Detail written: $csv" -ForegroundColor Green
}

# ---------------- Summary: answers the A-vs-B scoping question ----------------
Write-Host "`n==================== SUMMARY ====================" -ForegroundColor Yellow

# Full edit = can manage OTHER members (everything except SelfMembership).
$fullEdit = $allRows | Where-Object RightKind -ne 'SelfMembership'

Write-Host "`nMembership-edit entries by shape (full-edit only, excludes self-only):"
$fullEdit |
    Group-Object Inherited, @{e={$_.TrusteeType}} |
    Sort-Object Count -Descending |
    Format-Table @{n='Inherited';e={$_.Values[0]}}, @{n='TrusteeType';e={$_.Values[1]}}, Count -AutoSize | Out-Host

Write-Host "Full-edit entries where the trustee is NOT the named manager:"
$notManager = $fullEdit | Where-Object { -not $_.IsManager }
Write-Host ("  {0} of {1} full-edit entries are held by someone OTHER than the managedBy manager." -f @($notManager).Count, @($fullEdit).Count)
Write-Host ("  ...of those, {0} are INHERITED (OU delegation) and {1} are DIRECT on the group." -f `
    @($notManager | Where-Object Inherited).Count, @($notManager | Where-Object { -not $_.Inherited }).Count)

Write-Host "`nTop trustees who can edit membership WITHOUT being the named manager (this reveals L1/L2):"
$notManager |
    Group-Object Trustee, TrusteeType |
    Sort-Object Count -Descending |
    Select-Object -First 20 |
    Format-Table @{n='Trustee';e={$_.Values[0]}}, @{n='Type';e={$_.Values[1]}}, @{n='Groups';e={$_.Count}} -AutoSize | Out-Host

Write-Host "Self-only entries (SelfMembership - NOT sufficient to manage others, informational):"
Write-Host ("  {0} entries." -f @($allRows | Where-Object RightKind -eq 'SelfMembership').Count)

Write-Host "`nInterpretation:" -ForegroundColor Yellow
Write-Host "  - If nearly all full-edit is held by the named manager -> manager-only scope (A) is faithful."
Write-Host "  - If a lot is held by non-managers via INHERITED entries or by GROUP trustees (helpdesk"
Write-Host "    groups) -> those users are NOT in the manager list, so scope (B) is needed to show"
Write-Host "    their groups. The top-trustees table names exactly who/what those are."
