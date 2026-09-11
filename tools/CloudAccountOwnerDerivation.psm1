<#
.SYNOPSIS
    Pure derivation logic shared by the Cloud Password Reset owner-coverage survey.

.DESCRIPTION
    docs/CloudPasswordReset-Plan.md, "The derivation, computed fresh on every reset". A
    cloud-only Entra account has no reliable link to its on-premises owner, so the owner is
    DERIVED from the UPN local part at each attempt and never stored. These three functions are
    the string-and-rules half of that derivation, kept out of the survey script so they can be
    tested without a directory (tests/ps/CloudAccountOwnerDerivation.Tests.ps1).

    They are the model for Services/CloudAccountOwnerResolver.cs in S1. Where the two differ,
    the C# is authoritative and this file should be corrected, not the other way round: the
    survey exists to predict what the module will do, so a divergence makes its numbers a lie.

    Nothing here touches AD, Graph, the network or the filesystem.
#>

Set-StrictMode -Version Latest

<#
.SYNOPSIS
    Candidate on-premises keys for a cloud account's UPN, in priority order.

.DESCRIPTION
    The owner named four shapes the tenant actually contains, because the convention drifted:
    "<samaccountname>-CLD@", "first.last@", "samaccountname@" and "first.last_CLD@". Two
    candidates cover all four:

      1. the local part with a trailing -CLD or _CLD removed, case-insensitively
      2. the local part unchanged

    The list is de-duplicated case-insensitively, so an account with no suffix yields one
    candidate rather than the same string twice. Order is preserved: the suffix-stripped form
    is the current convention and is tried first.

    A blank or malformed UPN yields an empty array. That is not an error here; the caller
    reports it as "no candidates" and refuses, which is the same outcome as a lookup that finds
    nothing.
#>
function Get-CloudAccountOwnerCandidate {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [AllowNull()]
        [string] $UserPrincipalName
    )

    if ([string]::IsNullOrWhiteSpace($UserPrincipalName)) { return @() }

    $trimmed = $UserPrincipalName.Trim()
    $at = $trimmed.IndexOf('@')
    $localPart = if ($at -ge 0) { $trimmed.Substring(0, $at) } else { $trimmed }
    if ([string]::IsNullOrWhiteSpace($localPart)) { return @() }

    $candidates = [System.Collections.Generic.List[string]]::new()

    # Trailing -CLD / _CLD only. A key that merely CONTAINS "cld" (cldelgado) must not be cut.
    if ($localPart -match '^(.*?)[-_]CLD$') {
        $stripped = $Matches[1]
        if (-not [string]::IsNullOrWhiteSpace($stripped)) { $candidates.Add($stripped) }
    }

    $candidates.Add($localPart)

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $ordered = [System.Collections.Generic.List[string]]::new()
    foreach ($c in $candidates) {
        if ($seen.Add($c)) { $ordered.Add($c) }
    }

    # Unary comma: without it PowerShell unrolls a one-element array to a bare string on return,
    # so a caller that indexes the result gets a CHARACTER instead of a candidate key.
    return ,$ordered.ToArray()
}

<#
.SYNOPSIS
    Does this AD user's name corroborate the cloud account's display name?

.DESCRIPTION
    A sAMAccountName can collide across the searched domains, so an exact-match lookup can
    return a plausible-looking WRONG person.
    Corroboration is the check: the AD user's given name and surname must BOTH appear in the
    cloud account's Graph displayName, case-insensitively and in any order. That tolerates
    "Smith, John" against "John Smith (Cloud Admin)" and rejects an unrelated jsmith.

    Fail-closed: a blank given name, a blank surname or a blank display name returns false. An
    absent name is not a passing name, and there is nothing to corroborate against.

    The exact tolerance is the open owner decision D3, answerable only once this survey reports
    how large the mismatch class really is. This implementation is the strict form D3 is
    measured against, not a settled answer.
#>
function Test-CloudAccountOwnerNameMatch {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [AllowEmptyString()][AllowNull()][string] $GivenName,
        [AllowEmptyString()][AllowNull()][string] $Surname,
        [AllowEmptyString()][AllowNull()][string] $CloudDisplayName
    )

    if ([string]::IsNullOrWhiteSpace($GivenName)) { return $false }
    if ([string]::IsNullOrWhiteSpace($Surname)) { return $false }
    if ([string]::IsNullOrWhiteSpace($CloudDisplayName)) { return $false }

    $cmp = [System.StringComparison]::OrdinalIgnoreCase

    if ($CloudDisplayName.IndexOf($GivenName.Trim(), $cmp) -lt 0) { return $false }
    if ($CloudDisplayName.IndexOf($Surname.Trim(), $cmp) -lt 0) { return $false }

    return $true
}

<#
.SYNOPSIS
    Collapse per-candidate lookup results into one fail-closed owner outcome.

.DESCRIPTION
    Implements the resolution table in docs/CloudPasswordReset-Plan.md, in this precedence:

      1. ANY candidate Unavailable  -> Unavailable. The lookup never ran. That is not an
         absence, and no higher permission may paper over it.
      2. ANY candidate Ambiguous, or two or more DISTINCT users across candidates -> Ambiguous.
         Never pick one. Two candidates resolving to the SAME user is one match, not two;
         distinctness is by distinguishedName, case-insensitively.
      3. No candidates, or none found -> UnresolvedNoMatch.
      4. Exactly one distinct user, checked in this order:
           disabled          -> UnresolvedOwnerDisabled
           blank mail        -> UnresolvedNoMailbox
           name mismatch     -> UnresolvedNameMismatch
           otherwise         -> Resolved

    The disabled check is FIRST and is deliberately its own outcome rather than folded into
    "unresolved". The plan names Enabled as needed for the leaver rule but never states the
    rule's shape, so the survey measures the class separately and the owner rules on it with a
    number in hand. A leaver's mailbox may still accept mail, which is exactly why a disabled
    owner must not silently pass as resolved.

    Each element of -CandidateResult is expected to carry: Candidate, Status (Found / NotFound /
    Ambiguous / Unavailable), DistinguishedName, SamAccountName, Email, Enabled, GivenName,
    Surname. An unrecognised Status is treated as Unavailable, because an unknown lookup state
    is not evidence of absence.
#>
function Resolve-CloudAccountOwnerOutcome {
    [CmdletBinding()]
    [OutputType([psobject])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowNull()]
        [object[]] $CandidateResult,

        [AllowEmptyString()][AllowNull()]
        [string] $CloudDisplayName
    )

    function New-OwnerOutcomeResult {
        param([string]$Outcome, [string]$Reason, $Owner)
        [pscustomobject]@{
            Outcome                = $Outcome
            Reason                 = $Reason
            OwnerSamAccountName    = if ($null -ne $Owner) { $Owner.SamAccountName } else { $null }
            OwnerEmail             = if ($null -ne $Owner) { $Owner.Email } else { $null }
            OwnerDistinguishedName = if ($null -ne $Owner) { $Owner.DistinguishedName } else { $null }
            OwnerEnabled           = if ($null -ne $Owner) { $Owner.Enabled } else { $null }
        }
    }

    $results = @()
    if ($null -ne $CandidateResult) { $results = @($CandidateResult | Where-Object { $null -ne $_ }) }

    if ($results.Count -eq 0) {
        return New-OwnerOutcomeResult -Outcome 'UnresolvedNoMatch' -Reason 'No candidate keys could be derived from the UPN.' -Owner $null
    }

    $known = @('Found', 'NotFound', 'Ambiguous', 'Unavailable')
    $statuses = foreach ($r in $results) {
        $s = [string]$r.Status
        if ($known -contains $s) { $s } else { 'Unavailable' }
    }

    if ($statuses -contains 'Unavailable') {
        return New-OwnerOutcomeResult -Outcome 'Unavailable' -Reason 'At least one candidate lookup did not complete; absence was never established.' -Owner $null
    }

    if ($statuses -contains 'Ambiguous') {
        return New-OwnerOutcomeResult -Outcome 'Ambiguous' -Reason 'A candidate matched more than one directory object.' -Owner $null
    }

    $found = @($results | Where-Object { [string]$_.Status -eq 'Found' })
    if ($found.Count -eq 0) {
        return New-OwnerOutcomeResult -Outcome 'UnresolvedNoMatch' -Reason 'No candidate matched a directory user.' -Owner $null
    }

    $distinct = [ordered]@{}
    foreach ($f in $found) {
        $dn = [string]$f.DistinguishedName
        # A found row with no DN cannot be proven identical to any other, so it counts as its
        # own user. That pushes an unidentifiable pair to Ambiguous rather than merging them.
        $key = if ([string]::IsNullOrWhiteSpace($dn)) { 'nodn:' + [guid]::NewGuid().ToString('N') } else { $dn.Trim().ToLowerInvariant() }
        if (-not $distinct.Contains($key)) { $distinct[$key] = $f }
    }

    if ($distinct.Count -gt 1) {
        return New-OwnerOutcomeResult -Outcome 'Ambiguous' -Reason "Candidates resolved to $($distinct.Count) distinct directory users." -Owner $null
    }

    $owner = @($distinct.Values)[0]

    if ($owner.Enabled -eq $false) {
        return New-OwnerOutcomeResult -Outcome 'UnresolvedOwnerDisabled' -Reason 'The single matching directory user is disabled (probable leaver).' -Owner $owner
    }

    if ([string]::IsNullOrWhiteSpace([string]$owner.Email)) {
        return New-OwnerOutcomeResult -Outcome 'UnresolvedNoMailbox' -Reason 'The single matching directory user has no mail address.' -Owner $owner
    }

    if (-not (Test-CloudAccountOwnerNameMatch -GivenName $owner.GivenName -Surname $owner.Surname -CloudDisplayName $CloudDisplayName)) {
        return New-OwnerOutcomeResult -Outcome 'UnresolvedNameMismatch' -Reason 'Given name and surname do not both appear in the cloud display name.' -Owner $owner
    }

    return New-OwnerOutcomeResult -Outcome 'Resolved' -Reason 'Exactly one enabled, mail-enabled directory user, corroborated by name.' -Owner $owner
}

Export-ModuleMember -Function Get-CloudAccountOwnerCandidate, Test-CloudAccountOwnerNameMatch, Resolve-CloudAccountOwnerOutcome
