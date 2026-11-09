<#
.SYNOPSIS
    Who besides the administrators can change the code in an app folder that an elevated script
    is about to run (live-risk sweep item 12, sweep-g).

.DESCRIPTION
    The ops scripts run `<ProgramFiles>\dotnet\dotnet.exe <AppPath>\ExchangeAdminWeb.dll db ...`
    elevated (docs/ConfigDbToolInApp-Plan.md). The .NET host is taken from Program Files, which only
    administrators can write. The assembly and everything it loads - its dependencies, its
    deps.json and runtimeconfig.json, the native SQLite library under runtimes\ - comes from the app
    folder, and an elevated script that runs code from a folder another principal can write has
    handed that principal administrator rights: the aud-sqlite3-path hole, moved from sqlite3.exe
    to the app folder. Whether a folder is safe is not assumed from where it is or from how a host
    is usually set up (Architectural Invariant 7); it is read here, at run time, every time.

    A folder is safe when nothing in it - the folder itself and every file and folder under it,
    except the config\ and logs\ folders at its top, which hold runtime data the app writes and
    no code the tool loads - is owned by, or grants any of the rights that change content, to a
    principal other than:
      - BUILTIN\Administrators (S-1-5-32-544),
      - NT AUTHORITY\SYSTEM (S-1-5-18),
      - NT SERVICE\TrustedInstaller (S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464),
      - the account running this script, which is the principal the tool runs as anyway.
    Any other principal - Authenticated Users, Users, Everyone, INTERACTIVE, the app pool's own
    identity, a service account - may read and execute, never write.

    The rights that change content: write data / create files, append data / create folders,
    delete child, delete, change permissions, take ownership, and the generic all / generic write
    bits an inheritable entry can carry. An owner can always rewrite the permissions, so an owner
    outside the list counts too. Deny entries are ignored (they only take rights away), and so are
    entries that do not apply to the object they sit on (inherit-only) and the CREATOR OWNER /
    CREATOR GROUP placeholders, which grant nothing until a new object is created - and creating
    one needs a write right that is checked itself.

    The folders ABOVE the app folder are read too, every one up to the root of the volume
    (finding rev-f1; on a network share the root is the share itself, \\server\share, which is
    read, and the walk stops there - finding rev-g1): a principal who can delete or rename what
    is in a parent folder can put another folder in the app folder's place between this check and the run, and the elevated
    tool loads whatever is there then. A parent is safe when it is owned by the trusted set and
    no other principal holds, on the parent itself (inherited or not, inheritable or not), a
    right that can replace what is below it: delete child, delete, change permissions, take
    ownership or generic all. Write data and append data on a parent only add new entries next
    to the app folder, so they are not findings there. The trusted set is the same as for the
    app folder: the app pool's identity is not trusted with these rights on a parent either,
    because a pool identity that can replace the folder can change the code the elevated tool
    runs. A parent whose permissions or owner cannot be read is refused, naming it. For a
    folder not created yet, the folders above it that exist are read the same way.

    Not checked: where a junction or symbolic link in the path leads. The folders read are the
    ones the path names.

    Pure ASCII and Windows PowerShell 5.1-compatible: deploy.ps1 imports this (through
    SqliteConfigBackup.psm1) under 5.1.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# WriteData 0x2, AppendData 0x4, DeleteSubdirectoriesAndFiles 0x40, Delete 0x10000,
# ChangePermissions 0x40000, TakeOwnership 0x80000, GENERIC_ALL 0x10000000, GENERIC_WRITE 0x40000000.
$script:ContentChangingRights = 0x2 -bor 0x4 -bor 0x40 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000

# The rights on a folder ABOVE the app folder that can replace what is below it (rev-f1):
# DeleteSubdirectoriesAndFiles 0x40, Delete 0x10000 (rename the parent itself away),
# ChangePermissions 0x40000, TakeOwnership 0x80000, GENERIC_ALL 0x10000000.
$script:ReplacementRights = 0x40 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000

# The runtime-data folders at the top of an app folder: written by the app pool's identity on
# purpose, and holding no code the db tool loads.
$script:RuntimeDataFolders = @('config', 'logs')

function Get-ToolFolderTrustedSid {
    <#
    .SYNOPSIS
        The SIDs that may own, or hold a content-changing right on, an app folder an elevated
        script runs code from.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param()

    $current = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    return @(
        'S-1-5-32-544',
        'S-1-5-18',
        'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464',
        $current
    ) | Select-Object -Unique
}

function ConvertTo-PrincipalName {
    # Private. The account name for a SID when it translates, else the SID itself.
    param([System.Security.Principal.SecurityIdentifier]$Sid)

    try {
        return $Sid.Translate([System.Security.Principal.NTAccount]).Value
    } catch {
        return $Sid.Value
    }
}

function ConvertTo-RightsText {
    # Private (rev-i1). The rights in an access mask as words. FileSystemRights has no names for the
    # generic bits an inheritable entry can carry (an inherit-only entry often holds them), so its
    # ToString() prints such a mask as a bare number (-536805376 for GENERIC_READ | GENERIC_WRITE |
    # GENERIC_EXECUTE | DELETE). The generic bits are named here, then the named FileSystemRights of
    # the bits that remain; a remainder with no name is shown in hex. Wording only: what a mask
    # counts as is decided from the mask itself, never from this text.
    param([Parameter(Mandatory)][int64]$Mask)

    $names = [System.Collections.Generic.List[string]]::new()
    foreach ($generic in @(
            @{ Bit = 0x80000000; Name = 'GenericRead' },
            @{ Bit = 0x40000000; Name = 'GenericWrite' },
            @{ Bit = 0x20000000; Name = 'GenericExecute' },
            @{ Bit = 0x10000000; Name = 'GenericAll' })) {
        if (($Mask -band [int64]$generic.Bit) -ne 0) { $names.Add($generic.Name) }
    }
    $rest = $Mask -band 0x0FFFFFFF
    if ($rest -ne 0) {
        $named = [System.Enum]::ToObject([System.Security.AccessControl.FileSystemRights], [int]$rest).ToString()
        if ($named -match '^-?\d+$') { $named = '0x{0:X}' -f $rest }
        $names.Add($named)
    }
    return $names -join ', '
}

function Get-ObjectWriteExposure {
    # Private. The owner and every allow entry on one file or folder that the trusted set does not
    # cover. -InheritableOnly evaluates what a NEW child of this folder would inherit instead of
    # what applies to the folder itself (the plan-mode answer for a folder not created yet).
    param(
        [Parameter(Mandatory)][string]$LiteralPath,
        [Parameter(Mandatory)][string[]]$TrustedSid,
        [switch]$InheritableOnly
    )

    $acl = Get-Acl -LiteralPath $LiteralPath -ErrorAction Stop

    if (-not $InheritableOnly) {
        $owner = $acl.GetOwner([System.Security.Principal.SecurityIdentifier])
        if ($null -eq $owner) {
            throw "The owner of $LiteralPath cannot be read."
        }
        if ($TrustedSid -notcontains $owner.Value) {
            [pscustomobject]@{
                Path      = $LiteralPath
                Principal = ConvertTo-PrincipalName $owner
                Sid       = $owner.Value
                Rights    = 'owner (can change the permissions at any time)'
                Inherited = $false
                Kind      = 'Owner'
            }
        }
    }

    foreach ($rule in @($acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))) {
        if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) { continue }
        $sid = [string]$rule.IdentityReference.Value
        if ($sid -eq 'S-1-3-0' -or $sid -eq 'S-1-3-1') { continue }
        if ($TrustedSid -contains $sid) { continue }

        $inheritOnly = ($rule.PropagationFlags -band [System.Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0
        if ($InheritableOnly) {
            if ($rule.InheritanceFlags -eq [System.Security.AccessControl.InheritanceFlags]::None) { continue }
        } elseif ($inheritOnly) {
            continue
        }

        $mask = [int64][int]$rule.FileSystemRights
        if ($mask -lt 0) { $mask += 4294967296 }
        if (($mask -band $script:ContentChangingRights) -eq 0) { continue }

        [pscustomobject]@{
            Path      = $LiteralPath
            Principal = ConvertTo-PrincipalName $rule.IdentityReference
            Sid       = $sid
            Rights    = ConvertTo-RightsText -Mask $mask
            Inherited = [bool]$rule.IsInherited
            Kind      = 'Grant'
        }
    }
}

function Get-AclWalkParent {
    # Private (rev-g1). The folder above $Path for the parent walk, or nothing when $Path is the
    # root of its volume. For a path on a network share the root is the share (\\server\share):
    # Split-Path -Parent answers \\server for it, which is not a folder and has no permissions to
    # read, so the walk must stop at the share instead of asking for it.
    param([Parameter(Mandatory)][string]$Path)

    $root = [System.IO.Path]::GetPathRoot($Path)
    if ($root -and $Path.TrimEnd('\', '/').Equals($root.TrimEnd('\', '/'), [System.StringComparison]::OrdinalIgnoreCase)) {
        return $null
    }
    $parent = Split-Path -Parent $Path
    if ($parent) { $parent }
}

function Get-ParentChainExposure {
    # Private (rev-f1). The owner and every allow entry on $FirstParent and on each folder above it,
    # up to the root of the volume (the share itself on a network path; rev-g1), that lets a
    # principal outside the trusted set replace what is below it. $Below is the folder being
    # judged, named in the error when a parent cannot be read.
    param(
        [Parameter(Mandatory)][string]$FirstParent,
        [Parameter(Mandatory)][string]$Below,
        [Parameter(Mandatory)][string[]]$TrustedSid
    )

    $parent = $FirstParent
    while ($parent) {
        try {
            $acl = Get-Acl -LiteralPath $parent -ErrorAction Stop
            $owner = $acl.GetOwner([System.Security.Principal.SecurityIdentifier])
            $rules = @($acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))
        } catch {
            throw "The permissions of $parent, a folder above $Below, cannot be read ($($_.Exception.Message)); whoever can replace a folder above the app folder can replace the code in it."
        }
        if ($null -eq $owner) {
            throw "The owner of $parent, a folder above $Below, cannot be read; whoever owns a folder above the app folder can replace the code in it."
        }
        if ($TrustedSid -notcontains $owner.Value) {
            [pscustomobject]@{
                Path      = $parent
                Principal = ConvertTo-PrincipalName $owner
                Sid       = $owner.Value
                Rights    = 'owner (can change the permissions at any time)'
                Inherited = $false
                Kind      = 'ParentOwner'
            }
        }
        foreach ($rule in $rules) {
            if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) { continue }
            $sid = [string]$rule.IdentityReference.Value
            if ($sid -eq 'S-1-3-0' -or $sid -eq 'S-1-3-1') { continue }
            if ($TrustedSid -contains $sid) { continue }
            # An inherit-only entry does not apply to the parent itself; what it passes down to a
            # folder below is read on that folder.
            if (($rule.PropagationFlags -band [System.Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0) { continue }

            $mask = [int64][int]$rule.FileSystemRights
            if ($mask -lt 0) { $mask += 4294967296 }
            if (($mask -band $script:ReplacementRights) -eq 0) { continue }

            [pscustomobject]@{
                Path      = $parent
                Principal = ConvertTo-PrincipalName $rule.IdentityReference
                Sid       = $sid
                Rights    = ConvertTo-RightsText -Mask $mask
                Inherited = [bool]$rule.IsInherited
                Kind      = 'ParentGrant'
            }
        }
        $parent = Get-AclWalkParent -Path $parent
    }
}

function Get-ToolFolderWriteExposure {
    <#
    .SYNOPSIS
        Every owner and every allow entry in an app folder that lets a principal other than the
        administrators, SYSTEM, TrustedInstaller or the current account change its code.

    .DESCRIPTION
        Reads every folder above it up to the root of the volume (rev-f1), then the folder, then
        every file and folder under it except the config\ and logs\ folders at its top (see the
        module description). Each finding is an object: Path, Principal, Sid, Rights, Inherited,
        Kind ('Owner' or 'Grant' in the folder; 'ParentOwner' or 'ParentGrant' above it). Nothing
        returned means the folder is safe.

        THROWS when the folder is not there or anything in it cannot be read: a folder whose
        permissions cannot be shown is not a folder shown to be safe, so a caller refuses then too.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "The folder $Path does not exist, so its permissions cannot be read."
    }

    $trusted = Get-ToolFolderTrustedSid
    $root = (Get-Item -LiteralPath $Path -Force -ErrorAction Stop).FullName.TrimEnd('\')
    $skipped = @($script:RuntimeDataFolders | ForEach-Object { Join-Path $root $_ })

    $above = Get-AclWalkParent -Path $root
    if ($above) {
        Get-ParentChainExposure -FirstParent $above -Below $root -TrustedSid $trusted
    }
    Get-ObjectWriteExposure -LiteralPath $root -TrustedSid $trusted
    foreach ($item in @(Get-ChildItem -LiteralPath $root -Recurse -Force -ErrorAction Stop)) {
        $full = $item.FullName
        $inRuntimeData = $false
        foreach ($folder in $skipped) {
            if ($full.Equals($folder, [System.StringComparison]::OrdinalIgnoreCase) -or
                $full.StartsWith($folder + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
                $inRuntimeData = $true
                break
            }
        }
        if ($inRuntimeData) { continue }
        Get-ObjectWriteExposure -LiteralPath $full -TrustedSid $trusted
    }
}

function Get-ToolFolderInheritedWriteExposure {
    <#
    .SYNOPSIS
        For a folder that does not exist yet: what it would inherit from the nearest existing
        folder above it that lets another principal change its code.

    .DESCRIPTION
        The plan-mode answer for a folder a run creates (a staging publish). Reads the nearest
        existing ancestor and returns, in the shape Get-ToolFolderWriteExposure returns, the owner
        and the entries on that ancestor and every folder above it that could replace the new
        folder (rev-f1), then the ancestor's inheritable allow entries that the trusted set does not
        cover. The new folder's owner is whoever creates it, so no owner finding is made for it.
        Throws when no ancestor exists or any of them cannot be read.

        The two reads answer different questions - can the principal replace the new folder, and
        would it hold a write right inside it - and an entry that applies to the ancestor and is
        inherited too answers yes to both. Such an entry is returned once (rev-i2): the ancestor's
        ParentGrant, with AlsoInherited = $true, rather than a ParentGrant and a Grant naming the
        same principal, folder and rights. Every other finding of either read is returned as is.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $ancestor = Get-AclWalkParent -Path $Path
    while ($ancestor -and -not (Test-Path -LiteralPath $ancestor -PathType Container)) {
        $ancestor = Get-AclWalkParent -Path $ancestor
    }
    if (-not $ancestor) {
        throw "No folder above $Path exists, so what it would inherit cannot be read."
    }
    $trusted = Get-ToolFolderTrustedSid
    $replacing = @(Get-ParentChainExposure -FirstParent $ancestor -Below $Path -TrustedSid $trusted)
    $inheritedOnly = [System.Collections.Generic.List[object]]::new()
    foreach ($grant in @(Get-ObjectWriteExposure -LiteralPath $ancestor -TrustedSid $trusted -InheritableOnly)) {
        $same = @($replacing | Where-Object {
                $_.Kind -eq 'ParentGrant' -and $_.Path -eq $grant.Path -and $_.Sid -eq $grant.Sid -and
                $_.Rights -eq $grant.Rights -and $_.Inherited -eq $grant.Inherited
            })
        if ($same.Count -gt 0) {
            $same[0] | Add-Member -NotePropertyName AlsoInherited -NotePropertyValue $true -Force
        } else {
            $inheritedOnly.Add($grant)
        }
    }
    $replacing
    $inheritedOnly
}

function Format-ToolFolderWriteExposure {
    <#
    .SYNOPSIS
        One line naming the findings: the first five, and how many more.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([object[]]$Exposure)

    $all = @($Exposure)
    $shown = @($all | Select-Object -First 5 | ForEach-Object {
            $how = if ($_.Kind -eq 'Owner' -or $_.Kind -eq 'ParentOwner') { 'is the owner of' } elseif ($_.Inherited) { "holds $($_.Rights) (inherited) on" } else { "holds $($_.Rights) on" }
            $where = if ($_.Kind -eq 'ParentOwner' -or $_.Kind -eq 'ParentGrant') { ', a folder above the app folder, so can replace it' } else { '' }
            if ($_.PSObject.Properties['AlsoInherited'] -and $_.AlsoInherited) { $where += ', and the new folder would inherit it' }
            "$($_.Principal) $how $($_.Path)$where"
        })
    $text = $shown -join '; '
    if ($all.Count -gt 5) { $text += "; and $($all.Count - 5) more" }
    return $text
}

Export-ModuleMember -Function Get-ToolFolderTrustedSid, Get-ToolFolderWriteExposure, Get-ToolFolderInheritedWriteExposure, Format-ToolFolderWriteExposure
