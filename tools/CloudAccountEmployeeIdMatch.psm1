#Requires -Version 7.0
<#
.SYNOPSIS
    Pure helpers for the employeeId coverage survey (docs/CloudPasswordReset-Plan.md, queue item 10).

.DESCRIPTION
    Everything here is a pure function: no directory calls, no Graph calls, no disk. The survey
    script does the I/O and hands the results to these for escaping, routing and classification,
    so the decisions that matter can be tested without a forest.

    Environment neutrality is a repo invariant (.agents/repo-guidance.md, owner ruling
    2026-09-11): nothing here names a domain, host, OU or address, and nothing defaults to one.
    Domain routing is DERIVED from the distinguished name of the object actually found.
#>

Set-StrictMode -Version Latest

function ConvertTo-LdapFilterValue {
    <#
    .SYNOPSIS
        Escape a value for use inside an LDAP filter, per RFC 4515.
    .DESCRIPTION
        employeeId values come from the directory, not from a fixed list, so they can contain
        characters that change the meaning of a filter. An unescaped "*" turns an equality test
        into a wildcard and would report a match for an account that does not have one - a
        false Resolved, which is the single most dangerous wrong answer this survey can give.
        The backslash case must be replaced FIRST or it would double-escape the sequences the
        later replacements introduce.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $Value
    )

    $escaped = $Value.Replace('\', '\5c')
    $escaped = $escaped.Replace('*', '\2a')
    $escaped = $escaped.Replace('(', '\28')
    $escaped = $escaped.Replace(')', '\29')
    $escaped = $escaped.Replace([string][char]0, '\00')
    return $escaped
}

function Get-DomainDnsFromDistinguishedName {
    <#
    .SYNOPSIS
        Derive the DNS domain name of an object from its distinguished name.
    .DESCRIPTION
        A global catalog search can return a user from any domain in the forest, and the
        authoritative read of that user's attributes has to be directed at the domain that owns
        it. That domain is derived here from the object's own DC components rather than named
        anywhere, which is what keeps this survey environment-neutral: it works in whatever
        forest the host belongs to and knows nothing about any particular one.

        Returns $null when the DN carries no DC component, so a caller must decide what to do
        rather than receive a plausible-looking wrong server.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $DistinguishedName
    )

    if ([string]::IsNullOrWhiteSpace($DistinguishedName)) { return $null }

    $parts = [regex]::Matches($DistinguishedName, '(?i)(?:^|,)\s*DC=([^,]+)')
    if ($parts.Count -eq 0) { return $null }

    $labels = foreach ($p in $parts) { $p.Groups[1].Value.Trim() }
    if ($labels | Where-Object { [string]::IsNullOrWhiteSpace($_) }) { return $null }

    return ($labels -join '.')
}

function Get-EmployeeIdMatchOutcome {
    <#
    .SYNOPSIS
        Classify one cloud account into exactly one survey outcome.
    .DESCRIPTION
        This is the whole judgment of the survey, isolated so it can be tested exhaustively.

        Precedence, and why:
        1. Unavailable - a read failed. It is never silently folded into a negative outcome:
           "we looked and there was nothing" and "we could not look" must not be counted
           together, or a broken query reads as a clean answer.
        2. NoEmployeeId - nothing to match on.
        3. Ambiguous - more than one directory user carries this employeeId. This outranks every
           downstream check because the survey does not know WHICH user it would have examined,
           so any statement about mailbox or enablement would be about an arbitrary one.
        4. NoDirectoryMatch - the id is populated but resolves to nobody.
        5. MatchedOwnerDisabled - resolved to a disabled account. Ranked above the mailbox check
           deliberately: a disabled owner invalidates the match whether or not a mailbox exists,
           because nobody would route a password there even if delivery were possible.
        6. MatchedNoMailbox - resolved to a live person with nowhere to deliver.
        7. Resolved.

        Only the summary depends on this precedence. The CSV carries the raw match count,
        mailbox and enabled values per row, so the owner can re-bucket without a re-run.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [AllowEmptyString()]
        [AllowNull()]
        [string] $EmployeeId,

        [int] $MatchCount = 0,

        [bool] $OwnerHasMailbox = $false,

        [bool] $OwnerEnabled = $false,

        [bool] $Unavailable = $false
    )

    if ($Unavailable) { return 'Unavailable' }
    if ([string]::IsNullOrWhiteSpace($EmployeeId)) { return 'NoEmployeeId' }
    if ($MatchCount -gt 1) { return 'Ambiguous' }
    if ($MatchCount -lt 1) { return 'NoDirectoryMatch' }
    if (-not $OwnerEnabled) { return 'MatchedOwnerDisabled' }
    if (-not $OwnerHasMailbox) { return 'MatchedNoMailbox' }
    return 'Resolved'
}

function Get-EmployeeIdOutcomeClasses {
    <#
    .SYNOPSIS
        Every outcome the survey can report, in summary print order.
    .DESCRIPTION
        Declared as one list so a class with zero members still prints as 0 rather than vanishing.
        "No ambiguous accounts" and "ambiguity was never evaluated" must not look the same on a
        page the owner is about to make a 100%-bar decision from.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param()

    return @(
        'Resolved',
        'NoEmployeeId',
        'NoDirectoryMatch',
        'Ambiguous',
        'MatchedOwnerDisabled',
        'MatchedNoMailbox',
        'Unavailable'
    )
}

Export-ModuleMember -Function ConvertTo-LdapFilterValue, Get-DomainDnsFromDistinguishedName, Get-EmployeeIdMatchOutcome, Get-EmployeeIdOutcomeClasses
