#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.5' }

<#
Behavioural tests for tools/CloudAccountOwnerDerivation.psm1 and the plan mode of
tools/Get-CloudAccountOwnerCoverage.ps1 (docs/CloudPasswordReset-Plan.md, S0).

Nothing here needs Active Directory, Graph, a network or elevation. The derivation module is
pure string-and-rules work by design, precisely so the rules that decide where a password gets
mailed can be tested somewhere other than production. The plan-mode test proves the survey
script parses and describes itself without querying or writing anything.
#>

BeforeAll {
    $script:ModulePath = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' 'tools' 'CloudAccountOwnerDerivation.psm1')).Path
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' 'tools' 'Get-CloudAccountOwnerCoverage.ps1')).Path
    Import-Module $script:ModulePath -Force

    # One found candidate, with every field the resolver reads. Tests override what they mean to
    # vary, so a test that says "no mail" is visibly only about mail.
    function New-Candidate {
        param(
            [string] $Candidate = 'jsmith',
            [string] $Status = 'Found',
            [string] $DistinguishedName = 'CN=John Smith,OU=Users,DC=ad,DC=analog,DC=com',
            [string] $SamAccountName = 'jsmith',
            $Email = 'john.smith@analog.com',
            $Enabled = $true,
            $GivenName = 'John',
            $Surname = 'Smith'
        )
        [pscustomobject]@{
            Candidate = $Candidate; Status = $Status; DistinguishedName = $DistinguishedName
            SamAccountName = $SamAccountName; Email = $Email; Enabled = $Enabled
            GivenName = $GivenName; Surname = $Surname; Error = $null
        }
    }
}

Describe 'Get-CloudAccountOwnerCandidate' {

    It 'strips a trailing -CLD and keeps the unchanged local part, in that order' {
        $c = Get-CloudAccountOwnerCandidate -UserPrincipalName 'jsmith-CLD@analog.onmicrosoft.com'
        $c | Should -HaveCount 2
        $c[0] | Should -BeExactly 'jsmith'
        $c[1] | Should -BeExactly 'jsmith-CLD'
    }

    It 'strips a trailing _CLD as well' {
        $c = Get-CloudAccountOwnerCandidate -UserPrincipalName 'first.last_CLD@analog.onmicrosoft.com'
        $c[0] | Should -BeExactly 'first.last'
        $c[1] | Should -BeExactly 'first.last_CLD'
    }

    It 'strips the suffix case-insensitively' {
        (Get-CloudAccountOwnerCandidate -UserPrincipalName 'jsmith-cld@analog.onmicrosoft.com')[0] |
            Should -BeExactly 'jsmith'
    }

    It 'yields a single candidate for a UPN with no CLD suffix' {
        $c = Get-CloudAccountOwnerCandidate -UserPrincipalName 'first.last@analog.onmicrosoft.com'
        $c | Should -HaveCount 1
        $c[0] | Should -BeExactly 'first.last'
    }

    It 'does not cut a key that merely contains cld' {
        # cldelgado is a person, not a suffix. Cutting this would look up an unrelated account.
        $c = Get-CloudAccountOwnerCandidate -UserPrincipalName 'cldelgado@analog.onmicrosoft.com'
        $c | Should -HaveCount 1
        $c[0] | Should -BeExactly 'cldelgado'
    }

    It 'does not cut a mid-string CLD' {
        $c = Get-CloudAccountOwnerCandidate -UserPrincipalName 'a-CLD-b@analog.onmicrosoft.com'
        $c | Should -HaveCount 1
        $c[0] | Should -BeExactly 'a-CLD-b'
    }

    It 'does not produce a blank candidate when the UPN is only a suffix' {
        # "-CLD@..." strips to an empty key, which must not become a wildcard lookup.
        Get-CloudAccountOwnerCandidate -UserPrincipalName '-CLD@analog.onmicrosoft.com' |
            Should -HaveCount 1
        (Get-CloudAccountOwnerCandidate -UserPrincipalName '-CLD@analog.onmicrosoft.com')[0] |
            Should -BeExactly '-CLD'
    }

    It 'returns an empty array for blank, whitespace and null input' {
        @(Get-CloudAccountOwnerCandidate -UserPrincipalName '') | Should -HaveCount 0
        @(Get-CloudAccountOwnerCandidate -UserPrincipalName '   ') | Should -HaveCount 0
        @(Get-CloudAccountOwnerCandidate -UserPrincipalName $null) | Should -HaveCount 0
    }

    It 'returns an empty array when the UPN has no local part' {
        @(Get-CloudAccountOwnerCandidate -UserPrincipalName '@analog.onmicrosoft.com') | Should -HaveCount 0
    }

    It 'handles a bare key with no domain' {
        $c = Get-CloudAccountOwnerCandidate -UserPrincipalName 'jsmith-CLD'
        $c | Should -HaveCount 2
        $c[0] | Should -BeExactly 'jsmith'
    }
}

Describe 'Test-CloudAccountOwnerNameMatch' {

    It 'accepts both names present in order' {
        Test-CloudAccountOwnerNameMatch -GivenName 'John' -Surname 'Smith' -CloudDisplayName 'John Smith (Cloud Admin)' |
            Should -BeTrue
    }

    It 'accepts both names present in any order' {
        Test-CloudAccountOwnerNameMatch -GivenName 'John' -Surname 'Smith' -CloudDisplayName 'Smith, John' |
            Should -BeTrue
    }

    It 'is case-insensitive' {
        Test-CloudAccountOwnerNameMatch -GivenName 'john' -Surname 'SMITH' -CloudDisplayName 'John Smith' |
            Should -BeTrue
    }

    It 'rejects when only one name appears' {
        Test-CloudAccountOwnerNameMatch -GivenName 'Jane' -Surname 'Smith' -CloudDisplayName 'John Smith' |
            Should -BeFalse
    }

    It 'rejects an unrelated display name' {
        Test-CloudAccountOwnerNameMatch -GivenName 'John' -Surname 'Smith' -CloudDisplayName 'Service Account 12' |
            Should -BeFalse
    }

    It 'fails closed on a blank given name, surname or display name' {
        Test-CloudAccountOwnerNameMatch -GivenName '' -Surname 'Smith' -CloudDisplayName 'John Smith' | Should -BeFalse
        Test-CloudAccountOwnerNameMatch -GivenName 'John' -Surname '' -CloudDisplayName 'John Smith' | Should -BeFalse
        Test-CloudAccountOwnerNameMatch -GivenName 'John' -Surname 'Smith' -CloudDisplayName '' | Should -BeFalse
    }

    It 'fails closed on nulls' {
        Test-CloudAccountOwnerNameMatch -GivenName $null -Surname $null -CloudDisplayName $null | Should -BeFalse
    }
}

Describe 'Resolve-CloudAccountOwnerOutcome' {

    Context 'the resolution table' {

        It 'resolves exactly one enabled, mail-enabled, corroborated user' {
            $r = Resolve-CloudAccountOwnerOutcome -CandidateResult @(New-Candidate) -CloudDisplayName 'John Smith (Cloud)'
            $r.Outcome | Should -BeExactly 'Resolved'
            $r.OwnerEmail | Should -BeExactly 'john.smith@analog.com'
            $r.OwnerSamAccountName | Should -BeExactly 'jsmith'
        }

        It 'reports UnresolvedNoMatch when no candidate matches' {
            $r = Resolve-CloudAccountOwnerOutcome -CandidateResult @(New-Candidate -Status 'NotFound') -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'UnresolvedNoMatch'
            $r.OwnerEmail | Should -BeNullOrEmpty
        }

        It 'reports UnresolvedNoMatch when there are no candidates at all' {
            $r = Resolve-CloudAccountOwnerOutcome -CandidateResult @() -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'UnresolvedNoMatch'
        }

        It 'reports UnresolvedNoMailbox for a match with a blank mail attribute' {
            $r = Resolve-CloudAccountOwnerOutcome -CandidateResult @(New-Candidate -Email '') -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'UnresolvedNoMailbox'
        }

        It 'reports UnresolvedNoMailbox for a null mail attribute' {
            $r = Resolve-CloudAccountOwnerOutcome -CandidateResult @(New-Candidate -Email $null) -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'UnresolvedNoMailbox'
        }

        It 'reports UnresolvedNameMismatch when corroboration fails' {
            $r = Resolve-CloudAccountOwnerOutcome -CandidateResult @(New-Candidate) -CloudDisplayName 'Backup Service Account'
            $r.Outcome | Should -BeExactly 'UnresolvedNameMismatch'
        }

        It 'reports UnresolvedOwnerDisabled for a disabled match, before the mailbox and name checks' {
            # A leaver whose mailbox still accepts mail is the case that makes this check first.
            $r = Resolve-CloudAccountOwnerOutcome -CandidateResult @(New-Candidate -Enabled $false) -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'UnresolvedOwnerDisabled'
        }

        It 'reports Ambiguous when a single candidate matched more than one object' {
            $r = Resolve-CloudAccountOwnerOutcome -CandidateResult @(New-Candidate -Status 'Ambiguous') -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'Ambiguous'
            $r.OwnerEmail | Should -BeNullOrEmpty
        }

        It 'never picks one when two candidates resolve to two different users' {
            $a = New-Candidate -Candidate 'jsmith' -DistinguishedName 'CN=John Smith,DC=ad,DC=analog,DC=com'
            $b = New-Candidate -Candidate 'jsmith-CLD' -DistinguishedName 'CN=Jane Smith,DC=winroot,DC=analog,DC=com'
            $r = Resolve-CloudAccountOwnerOutcome -CandidateResult @($a, $b) -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'Ambiguous'
            $r.OwnerEmail | Should -BeNullOrEmpty
        }

        It 'treats two candidates resolving to the SAME user as one match' {
            $a = New-Candidate -Candidate 'jsmith'
            $b = New-Candidate -Candidate 'jsmith-CLD'
            $r = Resolve-CloudAccountOwnerOutcome -CandidateResult @($a, $b) -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'Resolved'
        }

        It 'matches the same user case-insensitively across candidates' {
            $a = New-Candidate -DistinguishedName 'CN=John Smith,DC=ad,DC=analog,DC=com'
            $b = New-Candidate -Candidate 'jsmith-CLD' -DistinguishedName 'cn=john smith,dc=ad,dc=analog,dc=com'
            $r = Resolve-CloudAccountOwnerOutcome -CandidateResult @($a, $b) -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'Resolved'
        }
    }

    Context 'Unavailable is not absence' {

        It 'refuses when any candidate lookup did not complete' {
            $r = Resolve-CloudAccountOwnerOutcome -CandidateResult @(New-Candidate -Status 'Unavailable') -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'Unavailable'
        }

        It 'lets Unavailable beat a successful match on another candidate' {
            # The successful match may be the wrong person; the failed lookup could have found the
            # right one. A partial answer is not an answer.
            $ok = New-Candidate -Candidate 'jsmith'
            $bad = New-Candidate -Candidate 'jsmith-CLD' -Status 'Unavailable'
            $r = Resolve-CloudAccountOwnerOutcome -CandidateResult @($ok, $bad) -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'Unavailable'
        }

        It 'lets Unavailable beat Ambiguous' {
            $amb = New-Candidate -Status 'Ambiguous'
            $bad = New-Candidate -Candidate 'jsmith-CLD' -Status 'Unavailable'
            $r = Resolve-CloudAccountOwnerOutcome -CandidateResult @($amb, $bad) -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'Unavailable'
        }

        It 'treats an unrecognised status as Unavailable, not as absence' {
            $r = Resolve-CloudAccountOwnerOutcome -CandidateResult @(New-Candidate -Status 'Weird') -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'Unavailable'
        }

        It 'treats a null status as Unavailable' {
            $c = New-Candidate
            $c.Status = $null
            $r = Resolve-CloudAccountOwnerOutcome -CandidateResult @($c) -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'Unavailable'
        }
    }

    Context 'defensive input' {

        It 'ignores null elements in the result array' {
            $r = Resolve-CloudAccountOwnerOutcome -CandidateResult @($null, (New-Candidate)) -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'Resolved'
        }

        It 'reports UnresolvedNoMatch for a null result array' {
            $r = Resolve-CloudAccountOwnerOutcome -CandidateResult $null -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'UnresolvedNoMatch'
        }

        It 'refuses to merge two found rows that carry no distinguished name' {
            $a = New-Candidate -Candidate 'jsmith' -DistinguishedName ''
            $b = New-Candidate -Candidate 'jsmith-CLD' -DistinguishedName ''
            $r = Resolve-CloudAccountOwnerOutcome -CandidateResult @($a, $b) -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'Ambiguous'
        }

        It 'always returns every field, populated or null' {
            $r = Resolve-CloudAccountOwnerOutcome -CandidateResult @(New-Candidate -Status 'NotFound') -CloudDisplayName 'x'
            $names = $r.PSObject.Properties.Name
            foreach ($f in 'Outcome', 'Reason', 'OwnerSamAccountName', 'OwnerEmail', 'OwnerDistinguishedName', 'OwnerEnabled') {
                $names | Should -Contain $f
            }
        }
    }
}

Describe 'Get-CloudAccountOwnerCoverage.ps1 plan mode' {

    It 'parses without error' {
        $errors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($script:ScriptPath, [ref]$null, [ref]$errors) | Out-Null
        $errors | Should -BeNullOrEmpty
    }

    It 'describes every step and queries nothing' {
        $out = & $script:ScriptPath -PlanOnly 6>&1 | Out-String
        $out | Should -Match 'PLAN'
        $out | Should -Match 'Microsoft Graph'
        $out | Should -Match 'Enumerate every cloud-only user'
        $out | Should -Match 'verify every requested search domain'
        $out | Should -Match 'Derive candidates'
        $out | Should -Match 'nothing was queried and no file was written'
    }

    It 'never wraps Get-CloudAccountOwnerCandidate in @(), which nests the array' {
        # The function returns ,$array. @(that) is a one-element array CONTAINING the array,
        # which stringifies into one space-joined LDAP key that matches nobody - a silent 0%
        # coverage result that looks like a finding rather than a bug. Unwrap by assignment.
        $text = Get-Content -LiteralPath $script:ScriptPath -Raw
        $text | Should -Not -Match '@\(\s*Get-CloudAccountOwnerCandidate'
    }

    It 'writes no CSV in plan mode' {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("cpr_cov_" + [guid]::NewGuid().ToString('N') + ".csv")
        & $script:ScriptPath -PlanOnly -CsvPath $tmp 6>&1 | Out-Null
        Test-Path -LiteralPath $tmp | Should -BeFalse
    }
}
