#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.5' }

<#
Behavioural tests for tools/CloudAccountOwnerDerivation.psm1 and the plan mode of
tools/Get-CloudAccountOwnerCoverage.ps1 (docs/CloudPasswordReset-Plan.md, S0).

Nothing here needs Active Directory, Graph, a network or elevation. The derivation module is
pure string-and-rules work by design, precisely so the rules that decide where a password gets
mailed can be tested somewhere other than production. The plan-mode test proves the survey
script parses and describes itself without querying or writing anything.

REBUILT 2026-09-11 alongside the module, for the three-source agreement design. The suite now
has to hold two rules apart that look alike and are opposites:

  an IDENTIFIER arm (the employee id) matching more than one user is DISCARDED as non-evidence,
  because a value many people share was never an identifier; the other sources still decide.

  a PERSON arm (display name, UPN key) matching more than one user REFUSES, because two people
  sharing a display name are two real candidate people and picking either is the exact error
  this design exists to prevent.

Getting those the same way round is the most likely way for a future edit to quietly break the
safety property, so each has a test that fails if it is given the other's treatment.

Fixture names are deliberately generic. Test fixtures are exempt from the environment-neutrality
invariant, but nothing here needs a real domain to make its point.
#>

<#
CALLING CONVENTION, and the reason every array assertion below assigns first.

Get-CloudAccountOwnerCandidate, Get-CloudAccountOwnerDisplayNameCandidate and
Get-CloudAccountOwnerSourceMatch all "return ,$array". The unary comma stops PowerShell
unrolling a one-element result to a bare string, which would make $c[0] a CHARACTER. The cost
is that the array reaches the caller as ONE pipeline object, so @(f) and "f | Should ..." wrap
it again and assert against a one-element array holding the real one - which passes or fails
for reasons that have nothing to do with the rule under test.

Assign, then assert:  $s = Get-...; $s | Should -Contain 'UpnKey'
Or index the call:    (Get-...).Count | Should -Be 0
Never:                @(Get-...) | Should -HaveCount 0
#>

BeforeAll {
    $script:ModulePath =(Resolve-Path (Join-Path $PSScriptRoot '..' '..' 'tools' 'CloudAccountOwnerDerivation.psm1')).Path
    $script:ScriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' 'tools' 'Get-CloudAccountOwnerCoverage.ps1')).Path
    Import-Module $script:ModulePath -Force

    # One directory row as Get-AdOwnerMatch hands it to the resolver, with every field the
    # resolver reads. Tests override only what they mean to vary, so a test that says "no mail"
    # is visibly only about mail.
    function New-Row {
        param(
            [string] $DistinguishedName = 'CN=John Smith,OU=Users,DC=ad,DC=example,DC=com',
            [string] $SamAccountName = 'jsmith',
            $Email = 'john.smith@example.com',
            $Enabled = $true,
            $GivenName = 'John',
            $Surname = 'Smith',
            $DisplayName = 'John Smith',
            $MatchedDomain = 'ad.example.com',
            [string[]] $Sources = @('UpnKey')
        )
        [pscustomobject]@{
            DistinguishedName = $DistinguishedName; SamAccountName = $SamAccountName
            Email = $Email; Enabled = $Enabled; GivenName = $GivenName; Surname = $Surname
            DisplayName = $DisplayName; MatchedDomain = $MatchedDomain; Sources = $Sources
        }
    }

    # One on-premises user as Get-ADUser returns it, for the source-attribution tests.
    function New-AdFixture {
        param(
            $DisplayName = 'John Smith',
            $SamAccountName = 'jsmith',
            $UserPrincipalName = 'john.smith@ad.example.com',
            $ProxyAddresses = @('SMTP:john.smith@example.com', 'smtp:jsmith@example.com'),
            $EmployeeID = $null,
            $EmployeeNumber = $null,
            $ExtensionAttribute1 = $null
        )
        [pscustomobject]@{
            DisplayName = $DisplayName; SamAccountName = $SamAccountName
            UserPrincipalName = $UserPrincipalName; ProxyAddresses = $ProxyAddresses
            EmployeeID = $EmployeeID; EmployeeNumber = $EmployeeNumber
            ExtensionAttribute1 = $ExtensionAttribute1
        }
    }
}

Describe 'Get-CloudAccountOwnerCandidate' {

    It 'strips a trailing -CLD and keeps the unchanged local part, in that order' {
        $c = Get-CloudAccountOwnerCandidate -UserPrincipalName 'jsmith-CLD@example.onmicrosoft.com'
        $c | Should -HaveCount 2
        $c[0] | Should -BeExactly 'jsmith'
        $c[1] | Should -BeExactly 'jsmith-CLD'
    }

    It 'strips a trailing _CLD as well' {
        $c = Get-CloudAccountOwnerCandidate -UserPrincipalName 'first.last_CLD@example.onmicrosoft.com'
        $c[0] | Should -BeExactly 'first.last'
        $c[1] | Should -BeExactly 'first.last_CLD'
    }

    It 'strips the suffix case-insensitively' {
        (Get-CloudAccountOwnerCandidate -UserPrincipalName 'jsmith-cld@example.onmicrosoft.com')[0] |
            Should -BeExactly 'jsmith'
    }

    It 'yields a single candidate for a UPN with no CLD suffix' {
        $c = Get-CloudAccountOwnerCandidate -UserPrincipalName 'first.last@example.onmicrosoft.com'
        $c | Should -HaveCount 1
        $c[0] | Should -BeExactly 'first.last'
    }

    It 'does not cut a key that merely contains cld' {
        # cldelgado is a person, not a suffix. Cutting this would look up an unrelated account.
        $c = Get-CloudAccountOwnerCandidate -UserPrincipalName 'cldelgado@example.onmicrosoft.com'
        $c | Should -HaveCount 1
        $c[0] | Should -BeExactly 'cldelgado'
    }

    It 'does not cut a mid-string CLD' {
        $c = Get-CloudAccountOwnerCandidate -UserPrincipalName 'a-CLD-b@example.onmicrosoft.com'
        $c | Should -HaveCount 1
        $c[0] | Should -BeExactly 'a-CLD-b'
    }

    It 'does not produce a blank candidate when the UPN is only a suffix' {
        # "-CLD@..." strips to an empty key, which must not become a wildcard lookup.
        Get-CloudAccountOwnerCandidate -UserPrincipalName '-CLD@example.onmicrosoft.com' |
            Should -HaveCount 1
        (Get-CloudAccountOwnerCandidate -UserPrincipalName '-CLD@example.onmicrosoft.com')[0] |
            Should -BeExactly '-CLD'
    }

    It 'returns an empty array for blank, whitespace and null input' {
        # Assign, never @(f) or f | ... - see the calling-convention note in BeforeAll.
        (Get-CloudAccountOwnerCandidate -UserPrincipalName '').Count | Should -Be 0
        (Get-CloudAccountOwnerCandidate -UserPrincipalName '   ').Count | Should -Be 0
        (Get-CloudAccountOwnerCandidate -UserPrincipalName $null).Count | Should -Be 0
    }

    It 'returns a real empty array, not a null that @() turns into one null element' {
        # ,@() and not @(): a bare empty return pipes nothing, the caller gets $null, and the
        # caller's own @($x) then produces a ONE-element array holding $null. That element goes
        # on to be stringified into a blank LDAP term.
        $empty = Get-CloudAccountOwnerCandidate -UserPrincipalName ''
        $null -eq $empty | Should -BeFalse
        @($empty).Count | Should -Be 0
    }

    It 'returns an empty array when the UPN has no local part' {
        (Get-CloudAccountOwnerCandidate -UserPrincipalName '@example.onmicrosoft.com').Count | Should -Be 0
    }

    It 'handles a bare key with no domain' {
        $c = Get-CloudAccountOwnerCandidate -UserPrincipalName 'jsmith-CLD'
        $c | Should -HaveCount 2
        $c[0] | Should -BeExactly 'jsmith'
    }
}

Describe 'Get-CloudAccountOwnerDisplayNameCandidate' {

    It 'strips a trailing -CLD and keeps the original, in that order' {
        $c = Get-CloudAccountOwnerDisplayNameCandidate -DisplayName 'John Smith-CLD'
        $c | Should -HaveCount 2
        $c[0] | Should -BeExactly 'John Smith'
        $c[1] | Should -BeExactly 'John Smith-CLD'
    }

    It 'tolerates whitespace around the separator, because a display name is typed prose' {
        (Get-CloudAccountOwnerDisplayNameCandidate -DisplayName 'Smith, John - CLD')[0] |
            Should -BeExactly 'Smith, John'
    }

    It 'yields one candidate for a display name with no suffix' {
        $c = Get-CloudAccountOwnerDisplayNameCandidate -DisplayName 'John Smith'
        $c | Should -HaveCount 1
    }

    It 'returns an empty array for blank and null input' {
        (Get-CloudAccountOwnerDisplayNameCandidate -DisplayName '').Count | Should -Be 0
        (Get-CloudAccountOwnerDisplayNameCandidate -DisplayName $null).Count | Should -Be 0
    }
}

Describe 'ConvertTo-LdapEscapedValue' {

    It 'escapes the metacharacters that would change the filter meaning' {
        ConvertTo-LdapEscapedValue -Value 'a*b' | Should -BeExactly 'a\2ab'
        ConvertTo-LdapEscapedValue -Value 'a(b)' | Should -BeExactly 'a\28b\29'
        ConvertTo-LdapEscapedValue -Value 'a\b' | Should -BeExactly 'a\5cb'
    }

    It 'escapes the backslash first, so it does not re-escape its own output' {
        # If '(' were handled first, its \28 would then become \5c28 and the filter would be
        # looking for a literal backslash.
        ConvertTo-LdapEscapedValue -Value '\(' | Should -BeExactly '\5c\28'
    }

    It 'returns empty for null' {
        ConvertTo-LdapEscapedValue -Value $null | Should -BeExactly ''
    }
}

Describe 'New-CloudAccountOwnerFilter' {

    BeforeAll {
        $script:FullSet = New-CloudAccountOwnerTermSet `
            -UserPrincipalName 'jsmith-CLD@example.onmicrosoft.com' `
            -DisplayName 'John Smith-CLD' `
            -EmployeeId '00123'
        $script:FullFilter = New-CloudAccountOwnerFilter -TermSet $script:FullSet
    }

    It 'carries every source in ONE filter, so a lookup costs one query per domain' {
        # Owner challenge 2026-09-11: "how will that translate to run-time password changes?
        # it's already too slow." One OR'd filter is the answer; a per-source loop is not.
        $script:FullFilter | Should -Match '^\(&\(objectClass=user\)\(\|'
        $script:FullFilter | Should -Match '\(displayName=John Smith\)'
        $script:FullFilter | Should -Match '\(sAMAccountName=jsmith\)'
        $script:FullFilter | Should -Match '\(employeeID=00123\)'
        $script:FullFilter | Should -Match '\(employeeNumber=00123\)'
        $script:FullFilter | Should -Match '\(extensionAttribute1=00123\)'
    }

    It 'compares the UPN LOCAL PART to a local part, wildcarding only the domain' {
        # This is the defect that voided the first survey run. The old filter compared a local
        # part against a WHOLE userPrincipalName, so an account whose on-premises UPN is
        # first.last@ but whose sAMAccountName is flast could never match.
        $script:FullFilter | Should -Match '\(userPrincipalName=jsmith@\*\)'
        $script:FullFilter | Should -Match '\(proxyAddresses=smtp:jsmith@\*\)'
    }

    It 'never wildcards the left of the @, which would reintroduce the substring hazard' {
        # ADDirectorySearchService.Search matches jdoe against jdoe2. Owner derivation must not.
        $script:FullFilter | Should -Not -Match '=\*'
    }

    It 'has no mail arm comparing a local part to a whole address' {
        $script:FullFilter | Should -Not -Match '\(mail='
    }

    It 'has no manager, otherMails or employeeType arm' {
        # All three were struck on owner rulings 2026-09-11. manager names the owner's MANAGER;
        # otherMails is Entra's SSPR recovery address, not the mailbox SMTP; employeeType holds
        # a worker-class code, so an arm on it matches an entire category of people at once. In
        # an agreement design a wrong arm manufactures corroboration, which is worse than a
        # missing one.
        $script:FullFilter | Should -Not -Match 'manager'
        $script:FullFilter | Should -Not -Match 'otherMails'
        $script:FullFilter | Should -Not -Match 'employeeType'
    }

    It 'escapes a key before appending the domain wildcard, not after' {
        # The escaper turns * into \2a. Escaping the assembled "key@*" would kill the wildcard
        # the arm depends on.
        $set = New-CloudAccountOwnerTermSet -UserPrincipalName 'a*b@example.com' -DisplayName '' -EmployeeId ''
        $f = New-CloudAccountOwnerFilter -TermSet $set
        $f | Should -Match '\(userPrincipalName=a\\2ab@\*\)'
    }

    It 'omits the id arms entirely when employeeId is blank' {
        $set = New-CloudAccountOwnerTermSet -UserPrincipalName 'jsmith@example.com' -DisplayName 'John Smith' -EmployeeId ''
        $f = New-CloudAccountOwnerFilter -TermSet $set
        $f | Should -Not -Match 'employee'
    }

    It 'returns null rather than an empty filter when no source has anything to ask' {
        # An empty OR would match the entire directory. Null forces the caller to issue no query.
        $set = New-CloudAccountOwnerTermSet -UserPrincipalName '' -DisplayName '' -EmployeeId ''
        New-CloudAccountOwnerFilter -TermSet $set | Should -BeNullOrEmpty
    }
}

Describe 'Get-CloudAccountOwnerSourceMatch' {

    It 'matches the UPN local part against the on-premises UPN local part' {
        # The owner's own account, which the void first run failed to match: cloud key
        # "john.smith", on-premises UPN "john.smith@<domain>", sAMAccountName "jsmith".
        $set = New-CloudAccountOwnerTermSet -UserPrincipalName 'john.smith@example.onmicrosoft.com' -DisplayName '' -EmployeeId ''
        $u = New-AdFixture -SamAccountName 'jsmith' -UserPrincipalName 'john.smith@ad.example.com' -ProxyAddresses @()
        $s = Get-CloudAccountOwnerSourceMatch -TermSet $set -DirectoryUser $u
        $s | Should -Contain 'UpnKey'
    }

    It 'matches the key against sAMAccountName' {
        $set = New-CloudAccountOwnerTermSet -UserPrincipalName 'jsmith-CLD@example.onmicrosoft.com' -DisplayName '' -EmployeeId ''
        $u = New-AdFixture -SamAccountName 'jsmith' -UserPrincipalName 'john.smith@ad.example.com' -ProxyAddresses @()
        $s = Get-CloudAccountOwnerSourceMatch -TermSet $set -DirectoryUser $u
        $s | Should -Contain 'UpnKey'
    }

    It 'matches the key against an smtp alias local part' {
        $set = New-CloudAccountOwnerTermSet -UserPrincipalName 'jaysmith@example.onmicrosoft.com' -DisplayName '' -EmployeeId ''
        $u = New-AdFixture -SamAccountName 'jsmith' -UserPrincipalName 'john.smith@ad.example.com' `
            -ProxyAddresses @('SMTP:jaysmith@example.com')
        $s = Get-CloudAccountOwnerSourceMatch -TermSet $set -DirectoryUser $u
        $s | Should -Contain 'UpnKey'
    }

    It 'ignores a non-smtp proxy address' {
        $set = New-CloudAccountOwnerTermSet -UserPrincipalName 'jsmith@example.onmicrosoft.com' -DisplayName '' -EmployeeId ''
        $u = New-AdFixture -SamAccountName 'other' -UserPrincipalName 'other@ad.example.com' `
            -ProxyAddresses @('x500:/o=x/cn=jsmith@example.com', 'sip:jsmith@example.com')
        (Get-CloudAccountOwnerSourceMatch -TermSet $set -DirectoryUser $u).Count | Should -Be 0
    }

    It 'matches the employee id against any of the three on-premises attributes' {
        $set = New-CloudAccountOwnerTermSet -UserPrincipalName 'x@example.com' -DisplayName '' -EmployeeId '00123'

        foreach ($attr in 'EmployeeID', 'EmployeeNumber', 'ExtensionAttribute1') {
            $u = New-AdFixture -SamAccountName 'other' -UserPrincipalName 'other@ad.example.com' -ProxyAddresses @()
            $u.$attr = '00123'
            $s = Get-CloudAccountOwnerSourceMatch -TermSet $set -DirectoryUser $u
            $s | Should -Contain 'Id' -Because "$attr should satisfy source 1"
        }
    }

    It 'matches the display name with the CLD suffix stripped' {
        $set = New-CloudAccountOwnerTermSet -UserPrincipalName 'x@example.com' -DisplayName 'John Smith-CLD' -EmployeeId ''
        $u = New-AdFixture -DisplayName 'John Smith' -SamAccountName 'other' -UserPrincipalName 'other@ad.example.com' -ProxyAddresses @()
        $s = Get-CloudAccountOwnerSourceMatch -TermSet $set -DirectoryUser $u
        $s | Should -Contain 'DisplayName'
    }

    It 'reports every source a single row satisfies' {
        $set = New-CloudAccountOwnerTermSet -UserPrincipalName 'jsmith-CLD@example.onmicrosoft.com' `
            -DisplayName 'John Smith-CLD' -EmployeeId '00123'
        $u = New-AdFixture -EmployeeID '00123'
        $s = Get-CloudAccountOwnerSourceMatch -TermSet $set -DirectoryUser $u
        $s | Should -Contain 'Id'
        $s | Should -Contain 'DisplayName'
        $s | Should -Contain 'UpnKey'
    }

    It 'returns nothing for a row that satisfies no source' {
        $set = New-CloudAccountOwnerTermSet -UserPrincipalName 'jsmith@example.com' -DisplayName 'John Smith' -EmployeeId '00123'
        $u = New-AdFixture -DisplayName 'Someone Else' -SamAccountName 'other' `
            -UserPrincipalName 'other@ad.example.com' -ProxyAddresses @() -EmployeeID '99999'
        (Get-CloudAccountOwnerSourceMatch -TermSet $set -DirectoryUser $u).Count | Should -Be 0
    }

    It 'does not throw on a row missing the optional attributes entirely' {
        # Set-StrictMode makes a bare read of an absent property throw, and AD omits attributes
        # that were never set.
        $set = New-CloudAccountOwnerTermSet -UserPrincipalName 'jsmith@example.com' -DisplayName 'John Smith' -EmployeeId '00123'
        $bare = [pscustomobject]@{ SamAccountName = 'jsmith' }
        { Get-CloudAccountOwnerSourceMatch -TermSet $set -DirectoryUser $bare } | Should -Not -Throw
        $s = Get-CloudAccountOwnerSourceMatch -TermSet $set -DirectoryUser $bare
        $s | Should -Contain 'UpnKey'
    }

    It 'returns nothing for a null row' {
        $set = New-CloudAccountOwnerTermSet -UserPrincipalName 'jsmith@example.com' -DisplayName '' -EmployeeId ''
        (Get-CloudAccountOwnerSourceMatch -TermSet $set -DirectoryUser $null).Count | Should -Be 0
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
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @(New-Row) -CloudDisplayName 'John Smith (Cloud)'
            $r.Outcome | Should -BeExactly 'Resolved'
            $r.OwnerEmail | Should -BeExactly 'john.smith@example.com'
            $r.OwnerSamAccountName | Should -BeExactly 'jsmith'
        }

        It 'reports UnresolvedNoMatch when the pooled query returned nothing' {
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @() -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'UnresolvedNoMatch'
            $r.OwnerEmail | Should -BeNullOrEmpty
        }

        It 'reports UnresolvedNoMailbox for a match with a blank mail attribute' {
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @(New-Row -Email '') -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'UnresolvedNoMailbox'
        }

        It 'reports UnresolvedNoMailbox for a null mail attribute' {
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @(New-Row -Email $null) -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'UnresolvedNoMailbox'
        }

        It 'reports UnresolvedNameMismatch when corroboration fails' {
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @(New-Row) -CloudDisplayName 'Backup Service Account'
            $r.Outcome | Should -BeExactly 'UnresolvedNameMismatch'
        }

        It 'reports UnresolvedOwnerDisabled for a disabled match, before the mailbox and name checks' {
            # A leaver whose mailbox still accepts mail is the case that makes this check first.
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @(New-Row -Enabled $false -Email '') -CloudDisplayName 'Nobody At All'
            $r.Outcome | Should -BeExactly 'UnresolvedOwnerDisabled'
        }

        It 'treats two rows for the SAME user, seen through two arms, as one match' {
            $a = New-Row -Sources @('DisplayName')
            $b = New-Row -Sources @('UpnKey')
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @($a, $b) -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'Resolved'
        }

        It 'matches the same user case-insensitively across rows' {
            $a = New-Row -DistinguishedName 'CN=John Smith,DC=ad,DC=example,DC=com' -Sources @('DisplayName')
            $b = New-Row -DistinguishedName 'cn=john smith,dc=ad,dc=example,dc=com' -Sources @('UpnKey')
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @($a, $b) -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'Resolved'
        }

        It 'records which sources answered' {
            $a = New-Row -Sources @('Id')
            $b = New-Row -Sources @('UpnKey')
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @($a, $b) -CloudDisplayName 'John Smith'
            $r.AnsweringSources | Should -BeExactly 'Id+UpnKey'
        }
    }

    Context 'an identifier arm that matches many is not an identifier' {

        It 'discards a multi-match id arm as non-evidence rather than refusing' {
            # A value hundreds of accounts share - a placeholder, a default, a repeated entry -
            # was never an identifier. Refusing on it would take out every account carrying it,
            # including ones the display name answers perfectly well. The search result is the
            # test; no sentinel list and no environment fact is needed.
            $decoy1 = New-Row -DistinguishedName 'CN=Placeholder A,DC=ad,DC=example,DC=com' -Sources @('Id')
            $decoy2 = New-Row -DistinguishedName 'CN=Placeholder B,DC=ad,DC=example,DC=com' -Sources @('Id')
            $real = New-Row -Sources @('DisplayName')

            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @($decoy1, $decoy2, $real) -CloudDisplayName 'John Smith'

            $r.Outcome | Should -BeExactly 'Resolved'
            $r.IdArmDiscarded | Should -BeTrue
            $r.AnsweringSources | Should -BeExactly 'DisplayName'
            $r.OwnerSamAccountName | Should -BeExactly 'jsmith'
        }

        It 'still reports UnresolvedNoMatch when the discarded id arm was the only source' {
            $d1 = New-Row -DistinguishedName 'CN=A,DC=ad,DC=example,DC=com' -Sources @('Id')
            $d2 = New-Row -DistinguishedName 'CN=B,DC=ad,DC=example,DC=com' -Sources @('Id')
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @($d1, $d2) -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'UnresolvedNoMatch'
            $r.IdArmDiscarded | Should -BeTrue
        }

        It 'keeps a single-match id arm as evidence' {
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @(New-Row -Sources @('Id')) -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'Resolved'
            $r.IdArmDiscarded | Should -BeFalse
        }
    }

    Context 'a person arm that matches many is two real people' {

        It 'refuses when the display name arm matched two distinct users' {
            # The opposite treatment to the id arm above, deliberately. Two people sharing a
            # display name are two candidate PEOPLE, and picking either is the error this whole
            # design exists to prevent.
            $a = New-Row -DistinguishedName 'CN=John Smith,OU=A,DC=ad,DC=example,DC=com' -Sources @('DisplayName')
            $b = New-Row -DistinguishedName 'CN=John Smith,OU=B,DC=ad,DC=example,DC=com' -Sources @('DisplayName')
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @($a, $b) -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'AmbiguousWithinSource'
            $r.OwnerEmail | Should -BeNullOrEmpty
        }

        It 'refuses when the UPN key arm matched two distinct users' {
            $a = New-Row -DistinguishedName 'CN=John Smith,DC=ad,DC=example,DC=com' -Sources @('UpnKey')
            $b = New-Row -DistinguishedName 'CN=John Smith,DC=other,DC=example,DC=com' -Sources @('UpnKey')
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @($a, $b) -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'AmbiguousWithinSource'
        }

        It 'refuses to merge two rows that carry no distinguished name' {
            $a = New-Row -DistinguishedName '' -Sources @('UpnKey')
            $b = New-Row -DistinguishedName '' -Sources @('UpnKey')
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @($a, $b) -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'AmbiguousWithinSource'
        }
    }

    Context 'sources that disagree' {

        It 'refuses, and says disagreement rather than ambiguity, when two sources name two people' {
            $byId = New-Row -DistinguishedName 'CN=John Smith,DC=ad,DC=example,DC=com' -Sources @('Id')
            $byName = New-Row -DistinguishedName 'CN=Jane Smith,DC=ad,DC=example,DC=com' `
                -SamAccountName 'jasmith' -GivenName 'Jane' -Sources @('DisplayName')
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @($byId, $byName) -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'AmbiguousSourceDisagreement'
            $r.OwnerEmail | Should -BeNullOrEmpty
        }

        It 'resolves when every answering source names the same person' {
            $a = New-Row -Sources @('Id')
            $b = New-Row -Sources @('DisplayName')
            $c = New-Row -Sources @('UpnKey')
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @($a, $b, $c) -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'Resolved'
            $r.AnsweringSources | Should -BeExactly 'Id+DisplayName+UpnKey'
        }
    }

    Context 'Unavailable is not absence' {

        It 'refuses when the lookup did not complete' {
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @() -CloudDisplayName 'John Smith' -LookupUnavailable
            $r.Outcome | Should -BeExactly 'Unavailable'
        }

        It 'lets Unavailable beat a clean single match' {
            # The match may be the wrong person; the domain that did not answer could hold the
            # right one, or a second one. A partial answer is not an answer.
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @(New-Row) -CloudDisplayName 'John Smith' -LookupUnavailable
            $r.Outcome | Should -BeExactly 'Unavailable'
            $r.OwnerEmail | Should -BeNullOrEmpty
        }

        It 'carries the reason through so the CSV says which domain failed' {
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @() -CloudDisplayName 'x' `
                -LookupUnavailable -UnavailableReason 'ad.example.com : server down'
            $r.Reason | Should -BeExactly 'ad.example.com : server down'
        }
    }

    Context 'defensive input' {

        It 'ignores null elements in the row array' {
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @($null, (New-Row)) -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'Resolved'
        }

        It 'reports UnresolvedNoMatch for a null row array' {
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser $null -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'UnresolvedNoMatch'
        }

        It 'counts a returned row that satisfies no source instead of silently dropping it' {
            # A row the filter matched and the in-memory comparison rejects means the two have
            # drifted apart, which is a defect worth surfacing rather than absorbing.
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @((New-Row -Sources @())) -CloudDisplayName 'John Smith'
            $r.Outcome | Should -BeExactly 'UnresolvedNoMatch'
            $r.UnattributedRows | Should -Be 1
        }

        It 'always returns every field, populated or null' {
            $r = Resolve-CloudAccountOwnerOutcome -MatchedUser @() -CloudDisplayName 'x'
            $names = $r.PSObject.Properties.Name
            foreach ($f in 'Outcome', 'Reason', 'AnsweringSources', 'SourceDetail', 'IdArmDiscarded',
                'UnattributedRows', 'OwnerSamAccountName', 'OwnerEmail', 'OwnerDistinguishedName',
                'OwnerEnabled', 'OwnerDisplayName', 'OwnerMatchedDomain') {
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

    It 'describes the scoping step, because the denominator is half the answer' {
        $out = & $script:ScriptPath -PlanOnly 6>&1 | Out-String
        $out | Should -Match 'privileged principals'
        $out | Should -Match 'PIM'
    }

    It 'never wraps Get-CloudAccountOwnerCandidate in @(), which nests the array' {
        # The function returns ,$array. @(that) is a one-element array CONTAINING the array,
        # which stringifies into one space-joined LDAP key that matches nobody - a silent 0%
        # coverage result that looks like a finding rather than a bug. Unwrap by assignment.
        $text = Get-Content -LiteralPath $script:ScriptPath -Raw
        $text | Should -Not -Match '@\(\s*Get-CloudAccountOwnerCandidate'
    }

    It 'exports every module function the survey script actually calls' {
        # The failure this catches is invisible to every other test here. Invoke-PlanOrAction
        # never runs its scriptblock in plan mode, so a script that calls an UNEXPORTED module
        # helper parses clean, plans clean, and dies on the first real account. Get-ObjectPropertyValue
        # was exactly that: written as an internal helper, then called from three places in the
        # survey script. Command names come from the AST, so a mention in a comment does not count.
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:ScriptPath, [ref]$null, [ref]$null)
        $called = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true) |
                ForEach-Object { $_.GetCommandName() } | Where-Object { $_ })

        $moduleText = Get-Content -LiteralPath $script:ModulePath -Raw
        $defined = @([regex]::Matches($moduleText, '(?m)^function\s+([A-Za-z]+-[A-Za-z0-9]+)') |
                ForEach-Object { $_.Groups[1].Value })
        $exported = @((Get-Module -Name 'CloudAccountOwnerDerivation').ExportedFunctions.Keys)

        $defined | Should -Not -BeNullOrEmpty -Because 'the regex must actually find the module functions'
        $missing = @($defined | Where-Object { $called -contains $_ -and $exported -notcontains $_ })
        ($missing -join ', ') | Should -BeExactly ''
    }

    It 'caps every directory query, so a non-selective filter cannot silently truncate' {
        $text = Get-Content -LiteralPath $script:ScriptPath -Raw
        $text | Should -Match 'Get-ADUser[^\r\n]*-ResultSetSize'
    }

    It 'writes no CSV in plan mode' {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("cpr_cov_" + [guid]::NewGuid().ToString('N') + ".csv")
        & $script:ScriptPath -PlanOnly -CsvPath $tmp 6>&1 | Out-Null
        Test-Path -LiteralPath $tmp | Should -BeFalse
    }
}
