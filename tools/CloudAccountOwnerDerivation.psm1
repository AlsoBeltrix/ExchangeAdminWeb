<#
.SYNOPSIS
    Pure derivation logic shared by the Cloud Password Reset owner-coverage survey.

.DESCRIPTION
    docs/CloudPasswordReset-Plan.md, "The derivation, computed fresh on every reset". A
    cloud-only Entra account has no reliable link to its on-premises owner, so the owner is
    DERIVED at each attempt and never stored. These functions are the string-and-rules half of
    that derivation, kept out of the survey script so the rules that decide where a password
    gets mailed can be tested without a directory
    (tests/ps/CloudAccountOwnerDerivation.Tests.ps1).

    REBUILT 2026-09-11. The superseded version derived the owner from the UPN local part alone
    and compared that key against WHOLE userPrincipalName and mail values, so it could only
    match when the local part happened to equal the sAMAccountName. Every account provisioned
    in the "first.last@" era was structurally unmatchable. The replacement is three evidence
    sources that must AGREE:

      Id          - the cloud account's employeeId, matched against the three on-premises
                    attributes that carry an employee identifier (employeeID, employeeNumber,
                    extensionAttribute1), OR'd. Which one is authoritative is an environment
                    fact this code may not assume.
      DisplayName - the cloud display name with a trailing -CLD/_CLD stripped, exact against
                    on-premises displayName.
      UpnKey      - the UPN local part, exact against the on-premises UPN LOCAL PART
                    (userPrincipalName=<key>@*), the smtp alias local part
                    (proxyAddresses=smtp:<key>@*), and sAMAccountName.

    Every source that can answer, answers. One distinct directory user across them resolves;
    two sources naming two different people refuses; a lookup that FAILED is Unavailable and
    poisons the whole answer, because a failed lookup is not an absence.

    manager and otherMails are NOT sources. Both were struck on owner rulings 2026-09-11:
    manager names the owner's MANAGER, and in a design that treats convergence as proof a wrong
    arm manufactures corroboration; otherMails is Entra's SSPR recovery address, not the mailbox
    SMTP, and is empty in this tenant. employeeType is not a fourth id attribute either - it
    holds a worker-class code describing a category of people, so an arm on it would match
    everyone in that class at once. Do not reintroduce any of the three.

    Cost is one OR'd query per checked domain, not one per source: New-CloudAccountOwnerFilter
    puts every arm into a single filter, and Get-CloudAccountOwnerSourceMatch works out which
    source answered from attributes the row already carries. Agreement costs zero query time.

    These functions are the model for Services/CloudAccountOwnerResolver.cs in S1. Where the two
    differ, the C# is authoritative and this file should be corrected, not the other way round:
    the survey exists to predict what the module will do, so a divergence makes its numbers a
    lie.

    Nothing here touches AD, Graph, the network or the filesystem.
#>

Set-StrictMode -Version Latest

<#
.SYNOPSIS
    Escape a value for literal use inside an LDAP filter (RFC 4515).

.DESCRIPTION
    Backslash FIRST, or the escapes introduced by the later replacements get escaped again.
    Exposed rather than kept private because every filter arm in this module depends on it and
    an unescaped '*' turns an exact-match arm into a wildcard that matches the wrong person.
#>
function ConvertTo-LdapEscapedValue {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [AllowEmptyString()][AllowNull()]
        [string] $Value
    )

    if ($null -eq $Value) { return '' }

    return ($Value -replace '\\', '\5c' -replace '\*', '\2a' -replace '\(', '\28' -replace '\)', '\29' -replace "`0", '\00')
}

<#
.SYNOPSIS
    Candidate on-premises keys for a cloud account's UPN, in priority order. Source 3.

.DESCRIPTION
    The owner named four shapes the tenant actually contains, because the convention drifted:
    "<samaccountname>-CLD@", "first.last@", "samaccountname@" and "first.last_CLD@". Two
    candidates cover all four:

      1. the local part with a trailing -CLD or _CLD removed, case-insensitively
      2. the local part unchanged

    The list is de-duplicated case-insensitively, so an account with no suffix yields one
    candidate rather than the same string twice. Order is preserved: the suffix-stripped form
    is the current convention and is tried first.

    The suffix strip is a string operation on a value the tenant supplies. It is not the
    EVIDENCE, and the design no longer rests on it being a reliable convention - that is what
    sources 1 and 2 are for.

    A blank or malformed UPN yields an empty array. That is not an error here; the caller
    reports it as "no candidates" and the source stays silent, which is not the same as a veto.
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

    # ,@() and not @(): a bare empty array returned from a function pipes nothing at all, so the
    # caller's variable lands as $null and every later @($x) turns it into a one-element array
    # holding $null. The unary comma wraps it, the wrapper is enumerated on output, and the caller
    # gets a real empty array. Same mechanism as the ,$ordered.ToArray() below, for the same reason.
    if ([string]::IsNullOrWhiteSpace($UserPrincipalName)) { return ,([string[]]@()) }

    $trimmed = $UserPrincipalName.Trim()
    $at = $trimmed.IndexOf('@')
    $localPart = if ($at -ge 0) { $trimmed.Substring(0, $at) } else { $trimmed }
    if ([string]::IsNullOrWhiteSpace($localPart)) { return ,([string[]]@()) }

    $candidates = [System.Collections.Generic.List[string]]::new()

    # Trailing -CLD / _CLD only. A key that merely CONTAINS "cld" (cldelgado) must not be cut.
    if ($localPart -match '^(.*?)[-_]CLD$') {
        $stripped = $Matches[1]
        if (-not [string]::IsNullOrWhiteSpace($stripped)) { $candidates.Add($stripped) }
    }

    $candidates.Add($localPart)

    # Unary comma: without it PowerShell unrolls a one-element array to a bare string on return,
    # so a caller that indexes the result gets a CHARACTER instead of a candidate key.
    return ,(Get-DistinctOrdinalIgnoreCase -Value $candidates)
}

<#
.SYNOPSIS
    Candidate on-premises display names for a cloud account. Source 2.

.DESCRIPTION
    A display name describes the person; the UPN describes the provisioning convention in force
    the day the account was made. Only one of those two survives a convention change, which is
    why this source outranks the UPN.

    Same strip as the UPN keys - a trailing -CLD/_CLD - but tolerant of whitespace around the
    separator, because a display name is prose typed by a human and "Smith, John - CLD" is the
    same shape as "jsmith-CLD". The unstripped form is also kept, de-duplicated, so an account
    whose display name carries no suffix still yields exactly one candidate.

    Blank input yields an empty array: the source is silent, not a veto.
#>
function Get-CloudAccountOwnerDisplayNameCandidate {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [AllowNull()]
        [string] $DisplayName
    )

    if ([string]::IsNullOrWhiteSpace($DisplayName)) { return ,([string[]]@()) }

    $trimmed = $DisplayName.Trim()
    $candidates = [System.Collections.Generic.List[string]]::new()

    if ($trimmed -match '^(.*?)\s*[-_]\s*CLD$') {
        $stripped = $Matches[1].Trim()
        if (-not [string]::IsNullOrWhiteSpace($stripped)) { $candidates.Add($stripped) }
    }

    $candidates.Add($trimmed)

    return ,(Get-DistinctOrdinalIgnoreCase -Value $candidates)
}

<#
.SYNOPSIS
    De-duplicate a string list case-insensitively, preserving first-seen order.
#>
function Get-DistinctOrdinalIgnoreCase {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [AllowEmptyCollection()][AllowNull()]
        [object[]] $Value
    )

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $ordered = [System.Collections.Generic.List[string]]::new()

    if ($null -ne $Value) {
        foreach ($v in $Value) {
            if ($null -eq $v) { continue }
            $s = [string]$v
            if ([string]::IsNullOrWhiteSpace($s)) { continue }
            if ($seen.Add($s)) { $ordered.Add($s) }
        }
    }

    return ,$ordered.ToArray()
}

<#
.SYNOPSIS
    Everything the three sources know about one cloud account, in one object.

.DESCRIPTION
    Built once per account and handed to both the filter builder and the source attributor, so
    the terms that go into the query are provably the same terms the in-memory comparison uses.
    Splitting those two would let the filter ask for one thing and the attribution check another,
    which is the failure the plan calls "inert in the quiet way": the lookup runs, returns
    nothing, and reports a clean NotFound.
#>
function New-CloudAccountOwnerTermSet {
    [CmdletBinding()]
    [OutputType([psobject])]
    param(
        [AllowEmptyString()][AllowNull()][string] $UserPrincipalName,
        [AllowEmptyString()][AllowNull()][string] $DisplayName,
        [AllowEmptyString()][AllowNull()][string] $EmployeeId
    )

    $keySet = Get-CloudAccountOwnerCandidate -UserPrincipalName $UserPrincipalName
    $nameSet = Get-CloudAccountOwnerDisplayNameCandidate -DisplayName $DisplayName

    $id = if ([string]::IsNullOrWhiteSpace($EmployeeId)) { $null } else { $EmployeeId.Trim() }

    return [pscustomobject]@{
        EmployeeId   = $id
        DisplayNames = $nameSet
        Keys         = $keySet
    }
}

<#
.SYNOPSIS
    ONE OR'd exact-match LDAP filter carrying every source's arms.

.DESCRIPTION
    Owner 2026-09-11: "how will that translate to run-time password changes? it's already too
    slow." The answer is this function. Every arm of every source goes into a single filter, so
    a reset costs one query per checked domain instead of one per candidate key per domain -
    roughly half what the superseded design cost, and the agreement rule on top of it is free
    because it is computed from attributes the returned rows already carry.

        (&(objectClass=user)(|
            (displayName=<name>)
            (userPrincipalName=<key>@*)
            (proxyAddresses=smtp:<key>@*)
            (sAMAccountName=<key>)
            (employeeID=<id>)(employeeNumber=<id>)(extensionAttribute1=<id>)
        ))

    The two wildcard arms are exact to the LEFT of the '@' and wildcard only across the domain.
    That is the whole point of source 3's rebuild - comparing a local part against a local part
    instead of against a whole UPN - and it does NOT reintroduce the substring hazard that rules
    out ADDirectorySearchService.Search, where 'jdoe' also matches 'jdoe2'.

    There is deliberately no (mail=...) arm. The old filter had one, comparing a local part
    against a whole address, which could never match. proxyAddresses carries the primary SMTP
    anyway and carries it in a shape that can be compared correctly.

    Returns $null when no source has anything to ask. A caller must treat that as "no query" and
    never as an empty filter, which would match the entire directory.
#>
function New-CloudAccountOwnerFilter {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [psobject] $TermSet
    )

    $arms = [System.Collections.Generic.List[string]]::new()

    foreach ($name in @($TermSet.DisplayNames)) {
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        $arms.Add("(displayName=$(ConvertTo-LdapEscapedValue -Value $name))")
    }

    foreach ($key in @($TermSet.Keys)) {
        if ([string]::IsNullOrWhiteSpace($key)) { continue }
        $e = ConvertTo-LdapEscapedValue -Value $key
        $arms.Add("(userPrincipalName=$e@*)")
        $arms.Add("(proxyAddresses=smtp:$e@*)")
        $arms.Add("(sAMAccountName=$e)")
    }

    if (-not [string]::IsNullOrWhiteSpace($TermSet.EmployeeId)) {
        $e = ConvertTo-LdapEscapedValue -Value $TermSet.EmployeeId
        $arms.Add("(employeeID=$e)")
        $arms.Add("(employeeNumber=$e)")
        $arms.Add("(extensionAttribute1=$e)")
    }

    if ($arms.Count -eq 0) { return $null }

    return "(&(objectClass=user)(|$([string]::Join('', $arms))))"
}

<#
.SYNOPSIS
    Which sources does this directory row satisfy? Computed in memory, no query.

.DESCRIPTION
    The pooled query returns every row any arm matched. This decides which source each row is
    evidence FOR, from attributes the row already carries, so agreement costs nothing.

    Returns any of 'Id', 'DisplayName', 'UpnKey' - an empty array means the row came back from
    the directory but satisfies no source on a strict re-read. That should be rare and is
    counted rather than silently dropped, because a row the filter matched and the comparison
    rejects means the two have drifted apart.

    -DirectoryUser is expected to carry DisplayName, SamAccountName, UserPrincipalName,
    ProxyAddresses, EmployeeID, EmployeeNumber and ExtensionAttribute1. A missing property is
    read as absent, not as an error: under Set-StrictMode a bare property read on an object that
    lacks it throws, so every read here goes through the PSObject property bag.
#>
function Get-CloudAccountOwnerSourceMatch {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)][psobject] $TermSet,
        [Parameter(Mandatory)][AllowNull()][psobject] $DirectoryUser
    )

    $sources = [System.Collections.Generic.List[string]]::new()
    if ($null -eq $DirectoryUser) { return ,$sources.ToArray() }

    $cmp = [System.StringComparer]::OrdinalIgnoreCase

    # --- Source 1: the employee id, against all three on-premises attributes, OR'd. -----------
    if (-not [string]::IsNullOrWhiteSpace($TermSet.EmployeeId)) {
        foreach ($attr in 'EmployeeID', 'EmployeeNumber', 'ExtensionAttribute1') {
            $v = Get-ObjectPropertyValue -InputObject $DirectoryUser -Name $attr
            if ($null -eq $v) { continue }
            if ($cmp.Equals([string]$v.ToString().Trim(), $TermSet.EmployeeId)) {
                $sources.Add('Id')
                break
            }
        }
    }

    # --- Source 2: display name, exact. -------------------------------------------------------
    $adName = Get-ObjectPropertyValue -InputObject $DirectoryUser -Name 'DisplayName'
    if (-not [string]::IsNullOrWhiteSpace($adName)) {
        foreach ($name in @($TermSet.DisplayNames)) {
            if ($cmp.Equals([string]$adName.ToString().Trim(), [string]$name)) {
                $sources.Add('DisplayName')
                break
            }
        }
    }

    # --- Source 3: UPN local part, smtp alias local part, sAMAccountName. ---------------------
    $keys = @($TermSet.Keys)
    if ($keys.Count -gt 0) {
        $rowKeys = [System.Collections.Generic.List[string]]::new()

        $sam = Get-ObjectPropertyValue -InputObject $DirectoryUser -Name 'SamAccountName'
        if (-not [string]::IsNullOrWhiteSpace($sam)) { $rowKeys.Add([string]$sam.ToString().Trim()) }

        $upn = Get-ObjectPropertyValue -InputObject $DirectoryUser -Name 'UserPrincipalName'
        $upnLocal = Get-AddressLocalPart -Address $upn
        if ($upnLocal) { $rowKeys.Add($upnLocal) }

        $proxies = Get-ObjectPropertyValue -InputObject $DirectoryUser -Name 'ProxyAddresses'
        foreach ($p in @($proxies)) {
            if ([string]::IsNullOrWhiteSpace($p)) { continue }
            $s = [string]$p.ToString().Trim()
            if (-not $s.StartsWith('smtp:', [System.StringComparison]::OrdinalIgnoreCase)) { continue }
            $local = Get-AddressLocalPart -Address $s.Substring(5)
            if ($local) { $rowKeys.Add($local) }
        }

        foreach ($rk in $rowKeys) {
            $hit = $false
            foreach ($key in $keys) {
                if ($cmp.Equals($rk, [string]$key)) { $hit = $true; break }
            }
            if ($hit) { $sources.Add('UpnKey'); break }
        }
    }

    return ,$sources.ToArray()
}

<#
.SYNOPSIS
    The part of an address left of the '@', or $null if there is none.
#>
function Get-AddressLocalPart {
    [CmdletBinding()]
    [OutputType([string])]
    param([AllowEmptyString()][AllowNull()][string] $Address)

    if ([string]::IsNullOrWhiteSpace($Address)) { return $null }
    $t = $Address.Trim()
    $at = $t.IndexOf('@')
    $local = if ($at -ge 0) { $t.Substring(0, $at) } else { $t }
    if ([string]::IsNullOrWhiteSpace($local)) { return $null }
    return $local
}

<#
.SYNOPSIS
    Read a property that may not exist, without tripping Set-StrictMode.
#>
function Get-ObjectPropertyValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowNull()][psobject] $InputObject,
        [Parameter(Mandatory)][string] $Name
    )

    if ($null -eq $InputObject) { return $null }
    $p = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $p) { return $null }
    return $p.Value
}

<#
.SYNOPSIS
    Does this AD user's name corroborate the cloud account's display name?

.DESCRIPTION
    A sAMAccountName can collide across the searched domains, so an exact-match lookup can
    return a plausible-looking WRONG person. Corroboration is the check: the AD user's given
    name and surname must BOTH appear in the cloud account's Graph displayName,
    case-insensitively and in any order. That tolerates "Smith, John" against "John Smith (Cloud
    Admin)" and rejects an unrelated jsmith.

    Fail-closed: a blank given name, a blank surname or a blank display name returns false. An
    absent name is not a passing name, and there is nothing to corroborate against.

    Under the rebuilt derivation this is the FLOOR rather than the mechanism. An answer found by
    source 1 or source 2 is corroborated by construction, so this check only has real work to do
    on a source-3-only answer. It still runs on every resolution, and the survey records which
    sources answered alongside the outcome, so the owner can see with a number whether a strict
    name test is costing correct source-1 answers to nicknames rather than catching errors.

    The exact tolerance is the open owner decision D3, answerable only once the survey reports
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
    Collapse the pooled directory rows into one fail-closed owner outcome, by agreement.

.DESCRIPTION
    Implements the resolution table in docs/CloudPasswordReset-Plan.md, in this precedence:

      1. -LookupUnavailable            -> Unavailable. A lookup that did not complete is not an
         absence, and no higher permission may paper over it. This beats every other outcome,
         including a clean single match, because the domain that stayed silent is exactly where
         a second match would have been.

      2. An 'Id' arm matching MORE THAN ONE distinct user -> that arm is DISCARDED as
         non-evidence and the remaining sources decide. It is NOT escalated to ambiguous. An
         identifier that matches many people has proved it is not an identifier - a placeholder,
         a default, a repeated value - and refusing on it would take out every account carrying
         it, including ones display name could have answered perfectly well. The search result is
         the test, so no sentinel list and no environment fact is needed.

      3. No source answered -> UnresolvedNoMatch.

      4. A PERSON arm (DisplayName, UpnKey) matching more than one distinct user ->
         AmbiguousWithinSource. This is deliberately the opposite treatment to rule 2: two people
         sharing a display name are two real candidate people, and picking either is the exact
         error this whole design exists to prevent.

      5. Answering sources naming DIFFERENT people -> AmbiguousSourceDisagreement. Reported as
         its own class rather than folded into ambiguity, because "the sources disagree" and "one
         source is internally ambiguous" are different defects and the owner is choosing a design
         on these numbers.

      6. Exactly one distinct user across every answering source, checked in this order:
           disabled          -> UnresolvedOwnerDisabled
           blank mail        -> UnresolvedNoMailbox
           name mismatch     -> UnresolvedNameMismatch
           otherwise         -> Resolved

    The disabled check is FIRST and is deliberately its own outcome rather than folded into
    "unresolved". The plan names Enabled as needed for the leaver rule but never states the
    rule's shape, so the survey measures the class separately and the owner rules on it with a
    number in hand. A leaver's mailbox may still accept mail, which is exactly why a disabled
    owner must not silently pass as resolved.

    Each element of -MatchedUser is expected to carry: DistinguishedName, SamAccountName, Email,
    Enabled, GivenName, Surname, DisplayName, MatchedDomain, and Sources (the string array from
    Get-CloudAccountOwnerSourceMatch). A row with no DN cannot be proven identical to any other,
    so it counts as its own user - which pushes an unidentifiable pair to ambiguous rather than
    quietly merging them.
#>
function Resolve-CloudAccountOwnerOutcome {
    [CmdletBinding()]
    [OutputType([psobject])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [AllowNull()]
        [object[]] $MatchedUser,

        [AllowEmptyString()][AllowNull()]
        [string] $CloudDisplayName,

        [switch] $LookupUnavailable,

        [AllowEmptyString()][AllowNull()]
        [string] $UnavailableReason
    )

    $sourceOrder = @('Id', 'DisplayName', 'UpnKey')

    function New-OwnerOutcomeResult {
        param(
            [string] $Outcome,
            [string] $Reason,
            $Owner,
            [string[]] $Answered = @(),
            [string] $Detail = '',
            [bool] $IdDiscarded = $false,
            [int] $Unattributed = 0
        )
        [pscustomobject]@{
            Outcome                = $Outcome
            Reason                 = $Reason
            AnsweringSources       = ($Answered -join '+')
            SourceDetail           = $Detail
            IdArmDiscarded         = $IdDiscarded
            UnattributedRows       = $Unattributed
            OwnerSamAccountName    = (Get-ObjectPropertyValue -InputObject $Owner -Name 'SamAccountName')
            OwnerEmail             = (Get-ObjectPropertyValue -InputObject $Owner -Name 'Email')
            OwnerDistinguishedName = (Get-ObjectPropertyValue -InputObject $Owner -Name 'DistinguishedName')
            OwnerEnabled           = (Get-ObjectPropertyValue -InputObject $Owner -Name 'Enabled')
            OwnerDisplayName       = (Get-ObjectPropertyValue -InputObject $Owner -Name 'DisplayName')
            OwnerMatchedDomain     = (Get-ObjectPropertyValue -InputObject $Owner -Name 'MatchedDomain')
        }
    }

    if ($LookupUnavailable) {
        $why = if ([string]::IsNullOrWhiteSpace($UnavailableReason)) {
            'At least one checked domain did not answer; absence was never established.'
        }
        else { $UnavailableReason }
        return New-OwnerOutcomeResult -Outcome 'Unavailable' -Reason $why -Owner $null
    }

    $rows = @()
    if ($null -ne $MatchedUser) { $rows = @($MatchedUser | Where-Object { $null -ne $_ }) }

    # Distinctness is by distinguishedName everywhere in this derivation, so the same person
    # seen through two arms, or through two domains across a trust, is one user and not a
    # collision. A row with no DN gets a unique key instead of an empty one, so two of them
    # cannot merge into a false single match.
    $keyed = [System.Collections.Generic.List[object]]::new()
    foreach ($r in $rows) {
        $dn = [string](Get-ObjectPropertyValue -InputObject $r -Name 'DistinguishedName')
        $key = if ([string]::IsNullOrWhiteSpace($dn)) {
            'nodn:' + [guid]::NewGuid().ToString('N')
        }
        else { $dn.Trim().ToLowerInvariant() }

        $srcRaw = Get-ObjectPropertyValue -InputObject $r -Name 'Sources'
        $src = @()
        if ($null -ne $srcRaw) { $src = @($srcRaw | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ }) }

        $keyed.Add([pscustomobject]@{ Key = $key; Row = $r; Sources = $src })
    }

    $unattributed = @($keyed | Where-Object { $_.Sources.Count -eq 0 }).Count

    # Distinct users per source, first row seen wins as the representative.
    $bySource = [ordered]@{}
    foreach ($s in $sourceOrder) { $bySource[$s] = [ordered]@{} }
    foreach ($k in $keyed) {
        foreach ($s in $k.Sources) {
            if (-not $bySource.Contains($s)) { continue }
            if (-not $bySource[$s].Contains($k.Key)) { $bySource[$s][$k.Key] = $k.Row }
        }
    }

    # Rule 2. A non-unique id value is not an id. Discard the arm; do not refuse the reset.
    $idDiscarded = $false
    if ($bySource['Id'].Count -gt 1) {
        $idDiscarded = $true
        $bySource['Id'] = [ordered]@{}
    }

    $answered = @($sourceOrder | Where-Object { $bySource[$_].Count -gt 0 })
    $detail = (($sourceOrder | ForEach-Object { "$_=$($bySource[$_].Count)" }) -join ' ')
    if ($idDiscarded) { $detail = "$detail Id:discarded-nonunique" }

    if ($answered.Count -eq 0) {
        $why = if ($idDiscarded) {
            'No source answered. The id arm matched many users and was discarded as non-evidence.'
        }
        else { 'No source matched a directory user.' }
        return New-OwnerOutcomeResult -Outcome 'UnresolvedNoMatch' -Reason $why -Owner $null `
            -Answered @() -Detail $detail -IdDiscarded $idDiscarded -Unattributed $unattributed
    }

    # Rule 4. A person arm matching two people is two real candidate people.
    foreach ($s in @('DisplayName', 'UpnKey')) {
        if ($bySource[$s].Count -gt 1) {
            return New-OwnerOutcomeResult -Outcome 'AmbiguousWithinSource' `
                -Reason "Source $s matched $($bySource[$s].Count) distinct directory users." -Owner $null `
                -Answered $answered -Detail $detail -IdDiscarded $idDiscarded -Unattributed $unattributed
        }
    }

    # Rule 5. Agreement is the mechanism: the answering sources must converge on one person.
    $union = [ordered]@{}
    foreach ($s in $answered) {
        foreach ($k in $bySource[$s].Keys) {
            if (-not $union.Contains($k)) { $union[$k] = $bySource[$s][$k] }
        }
    }

    if ($union.Count -gt 1) {
        return New-OwnerOutcomeResult -Outcome 'AmbiguousSourceDisagreement' `
            -Reason "Answering sources ($($answered -join '+')) named $($union.Count) different people." -Owner $null `
            -Answered $answered -Detail $detail -IdDiscarded $idDiscarded -Unattributed $unattributed
    }

    $owner = @($union.Values)[0]

    if ((Get-ObjectPropertyValue -InputObject $owner -Name 'Enabled') -eq $false) {
        return New-OwnerOutcomeResult -Outcome 'UnresolvedOwnerDisabled' `
            -Reason 'The single agreed directory user is disabled (probable leaver).' -Owner $owner `
            -Answered $answered -Detail $detail -IdDiscarded $idDiscarded -Unattributed $unattributed
    }

    if ([string]::IsNullOrWhiteSpace([string](Get-ObjectPropertyValue -InputObject $owner -Name 'Email'))) {
        return New-OwnerOutcomeResult -Outcome 'UnresolvedNoMailbox' `
            -Reason 'The single agreed directory user has no mail address.' -Owner $owner `
            -Answered $answered -Detail $detail -IdDiscarded $idDiscarded -Unattributed $unattributed
    }

    $given = [string](Get-ObjectPropertyValue -InputObject $owner -Name 'GivenName')
    $surname = [string](Get-ObjectPropertyValue -InputObject $owner -Name 'Surname')
    if (-not (Test-CloudAccountOwnerNameMatch -GivenName $given -Surname $surname -CloudDisplayName $CloudDisplayName)) {
        return New-OwnerOutcomeResult -Outcome 'UnresolvedNameMismatch' `
            -Reason 'Given name and surname do not both appear in the cloud display name.' -Owner $owner `
            -Answered $answered -Detail $detail -IdDiscarded $idDiscarded -Unattributed $unattributed
    }

    return New-OwnerOutcomeResult -Outcome 'Resolved' `
        -Reason "Sources $($answered -join '+') agreed on one enabled, mail-enabled directory user." -Owner $owner `
        -Answered $answered -Detail $detail -IdDiscarded $idDiscarded -Unattributed $unattributed
}

# Get-ObjectPropertyValue is exported even though it is a helper: the survey script calls it on
# Graph and AD objects that omit unset properties, and Set-StrictMode turns a bare read of a
# missing property into a throw. Get-DistinctOrdinalIgnoreCase and Get-AddressLocalPart stay
# internal because nothing outside the module needs them.
Export-ModuleMember -Function `
    ConvertTo-LdapEscapedValue, `
    Get-CloudAccountOwnerCandidate, `
    Get-CloudAccountOwnerDisplayNameCandidate, `
    New-CloudAccountOwnerTermSet, `
    New-CloudAccountOwnerFilter, `
    Get-CloudAccountOwnerSourceMatch, `
    Test-CloudAccountOwnerNameMatch, `
    Resolve-CloudAccountOwnerOutcome, `
    Get-ObjectPropertyValue
