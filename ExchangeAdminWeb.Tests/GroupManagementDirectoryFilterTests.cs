using System.Collections;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// What Group Management asks the directory for when it searches for groups and when it resolves a
/// group it was given by name: the text an operator typed is a value in an LDAP filter, matched
/// literally whatever characters it holds.
/// </summary>
/// <remarks>
/// Behaviour, not text alone: the real <see cref="GroupManagementService.SearchGroupsAsync"/> and
/// <see cref="GroupManagementService.GetMembersAsync"/> over a local session (the
/// <c>CreateDirectoryRunspace</c> seam) whose Get-ADGroup notes its parameters and answers with the
/// stand-in groups its -LDAPFilter matches, read as a directory reads one
/// (<see cref="StandInLdapFilter"/>). A search by -Filter, or a filter no directory would accept,
/// is an error there.
/// </remarks>
public sealed class GroupManagementDirectoryFilterTests
{
    // The directory: each group's name, logon name and address.
    private static readonly (string Name, string Sam, string Mail)[] Directory =
    [
        ("Finance Team", "fin-team", "finance@contoso.test"),
        ("Payroll", "payroll", "pay.finance@contoso.test"),
        ("Facilities", "facilities", "facilities@contoso.test"),
        ("O'Brien Team", "obrien", "obrien@contoso.test"),
        ("Pay$roll Admins", "pay-admins", "payadmins@contoso.test"),
        (@"Ops\Night", "ops-night", "opsnight@contoso.test"),
        ("Ops (Day)", "ops-day", "opsday@contoso.test"),
        ("Ops*Star", "ops-star", "opsstar@contoso.test"),
        ("OpsAllStar", "ops-allstar", "opsallstar@contoso.test"),
    ];

    private static string Dn(string name) => $"CN={name},OU=Groups,DC=contoso,DC=test";

    private sealed class StandInGroups(Func<Runspace> open)
        : GroupManagementService(null!, null!, null!, null!, NullLogger<GroupManagementService>.Instance)
    {
        internal override Runspace CreateDirectoryRunspace() => open();

        internal override Task<(string username, string password, string domain)?> GetCredentialsAsync(string purpose) =>
            Task.FromResult<(string username, string password, string domain)?>(("svc-groups", "not-a-secret", "CONTOSO"));

        internal override Task<ModuleCredentialLookup> LookupCredentialsAsync(string purpose) =>
            Task.FromResult(new ModuleCredentialLookup(("svc-groups", "not-a-secret", "CONTOSO"), default));
    }

    /// <summary>
    /// A local session: Import-Module does nothing, the forest names one catalog, and Get-ADGroup notes the
    /// parameters it was given in <paramref name="calls"/>. A read by -Identity answers nothing; a search
    /// answers with the groups its -LDAPFilter matches.
    /// </summary>
    private static Runspace DirectorySession(List<Hashtable> calls)
    {
        var groups = Directory.Select(g => new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = g.Name,
            ["sAMAccountName"] = g.Sam,
            ["mail"] = g.Mail,
            ["distinguishedName"] = Dn(g.Name),
        }).ToArray();

        var state = InitialSessionState.CreateDefault();
        state.Variables.Add(new SessionStateVariableEntry("PSModuleAutoloadingPreference", "None", "no autoloading"));
        state.Variables.Add(new SessionStateVariableEntry("GroupCalls", calls, "the Get-ADGroup calls the session took"));
        state.Variables.Add(new SessionStateVariableEntry("Groups", groups, "the groups the directory holds"));
        state.Commands.Add(new SessionStateFunctionEntry("Import-Module", "[CmdletBinding()] param([Parameter(Position = 0)] $Name)"));
        state.Commands.Add(new SessionStateFunctionEntry("Get-ADForest",
            "[CmdletBinding()] param($Credential) [pscustomobject]@{ GlobalCatalogs = @('gc.contoso.test') }"));
        state.Commands.Add(new SessionStateFunctionEntry("Get-ADGroup", """
            [CmdletBinding()] param($Filter, $LDAPFilter, $Identity, $Properties, $Credential, $Server, $ResultSetSize)
            $call = @{}
            foreach ($key in $PSBoundParameters.Keys) { $call[$key] = $PSBoundParameters[$key] }
            $global:GroupCalls.Add($call)
            if ($PSBoundParameters.ContainsKey('Identity')) { return }
            if ($PSBoundParameters.ContainsKey('Filter')) { Write-Error "stand-in: a group search by -Filter ('$Filter') is not evaluated."; return }
            $clauses = [ExchangeAdminWeb.Tests.StandInLdapFilter]::Read("$LDAPFilter")
            if ($null -eq $clauses) { Write-Error "stand-in: cannot read the filter '$LDAPFilter'."; return }
            foreach ($group in $global:Groups) {
                $matched = $false
                foreach ($clause in $clauses) { if ("$($group[$clause[0]])" -imatch $clause[1]) { $matched = $true } }
                if ($matched) {
                    [pscustomobject]@{
                        Name = $group['name']; SamAccountName = $group['sAMAccountName']; Mail = $group['mail']
                        DistinguishedName = $group['distinguishedName']
                        GroupCategory = 'Security'; GroupScope = 'Global'; Description = ''; ManagedBy = ''
                    }
                }
            }
            """));
        return RunspaceFactory.CreateRunspace(state);
    }

    private static string? SearchFilter(Hashtable call) => call["LDAPFilter"]?.ToString();

    // ---- the search ------------------------------------------------------------------------------

    [Fact]
    public async Task ASearch_FindsEveryGroupWhoseNameLogonNameOrAddressContainsTheTerm()
    {
        var calls = new List<Hashtable>();
        var groups = new StandInGroups(() => DirectorySession(calls));

        var found = await groups.SearchGroupsAsync("fin");

        // By name and logon name (Finance Team), and by address alone (Payroll).
        Assert.Equal(["Finance Team", "Payroll"], found.Select(g => g.Name).Order());
        var call = Assert.Single(calls);
        Assert.Equal("(|(name=*fin*)(sAMAccountName=*fin*)(mail=*fin*))", SearchFilter(call));
        Assert.False(call.ContainsKey("Filter"));
    }

    [Theory]
    [InlineData("O'Brien", "(|(name=*O'Brien*)(sAMAccountName=*O'Brien*)(mail=*O'Brien*))", "O'Brien Team")]
    [InlineData("Pay$roll", "(|(name=*Pay$roll*)(sAMAccountName=*Pay$roll*)(mail=*Pay$roll*))", "Pay$roll Admins")]
    [InlineData(@"Ops\Night", @"(|(name=*Ops\5cNight*)(sAMAccountName=*Ops\5cNight*)(mail=*Ops\5cNight*))", @"Ops\Night")]
    [InlineData("(Day)", @"(|(name=*\28Day\29*)(sAMAccountName=*\28Day\29*)(mail=*\28Day\29*))", "Ops (Day)")]
    [InlineData("Ops*Star", @"(|(name=*Ops\2aStar*)(sAMAccountName=*Ops\2aStar*)(mail=*Ops\2aStar*))", "Ops*Star")]
    [InlineData("Ops\0Star", @"(|(name=*Ops\00Star*)(sAMAccountName=*Ops\00Star*)(mail=*Ops\00Star*))", null)]
    [InlineData("*", @"(|(name=*\2a*)(sAMAccountName=*\2a*)(mail=*\2a*))", "Ops*Star")]
    [InlineData(")(name=*", @"(|(name=*\29\28name=\2a*)(sAMAccountName=*\29\28name=\2a*)(mail=*\29\28name=\2a*))", null)]
    public async Task ASearchTermWithACharacterAFilterGivesMeaningTo_IsMatchedLiterally(string term, string filter, string? onlyMatch)
    {
        var calls = new List<Hashtable>();
        var groups = new StandInGroups(() => DirectorySession(calls));

        var found = await groups.SearchGroupsAsync(term);

        Assert.Equal(onlyMatch is null ? [] : new[] { onlyMatch }, found.Select(g => g.Name));
        Assert.Equal(filter, SearchFilter(Assert.Single(calls)));
    }

    // ---- a group given by name ---------------------------------------------------------------------

    // The group's members are read from the group the name resolved to: the read by -Identity that follows
    // the search names it. The stand-in answers that read with nothing, which ends the listing there.
    private static async Task<(string? ResolvedDn, List<string?> Filters)> ResolveByName(string identity, string? logonName)
    {
        var calls = new List<Hashtable>();
        var groups = new StandInGroups(() => DirectorySession(calls));

        await Record.ExceptionAsync(() => groups.GetMembersAsync(identity, actingUser: null, logonName));

        var read = calls.SingleOrDefault(c => c.ContainsKey("Identity"));
        var searches = calls.Where(c => !c.ContainsKey("Identity")).ToList();
        Assert.All(searches, c => Assert.False(c.ContainsKey("Filter")));
        return (read?["Identity"]?.ToString(), searches.Select(SearchFilter).ToList());
    }

    [Theory]
    [InlineData("fin-team", "Finance Team")]
    [InlineData("Finance Team", "Finance Team")]
    [InlineData("FINANCE@contoso.test", "Finance Team")]
    public async Task AGroupGivenByItsLogonNameItsNameOrItsAddress_IsTheOneRead(string identity, string name)
    {
        var (resolvedDn, filters) = await ResolveByName(identity, logonName: null);

        Assert.Equal(Dn(name), resolvedDn);
        Assert.Equal([$"(|(sAMAccountName={identity})(name={identity})(mail={identity}))"], filters);
    }

    [Fact]
    public async Task TheLogonNameIsTriedBeforeTheIdentity_AndAPartOfANameResolvesNothing()
    {
        // "fin" is part of two groups' names and all of none: exact match only, so the identity is tried next.
        var (resolvedDn, filters) = await ResolveByName("Payroll", logonName: "fin");

        Assert.Equal(Dn("Payroll"), resolvedDn);
        Assert.Equal(["(|(sAMAccountName=fin)(name=fin)(mail=fin))", "(|(sAMAccountName=Payroll)(name=Payroll)(mail=Payroll))"], filters);
    }

    [Theory]
    [InlineData("O'Brien Team", "(|(sAMAccountName=O'Brien Team)(name=O'Brien Team)(mail=O'Brien Team))", "O'Brien Team")]
    [InlineData("Pay$roll Admins", "(|(sAMAccountName=Pay$roll Admins)(name=Pay$roll Admins)(mail=Pay$roll Admins))", "Pay$roll Admins")]
    [InlineData(@"Ops\Night", @"(|(sAMAccountName=Ops\5cNight)(name=Ops\5cNight)(mail=Ops\5cNight))", @"Ops\Night")]
    [InlineData("Ops (Day)", @"(|(sAMAccountName=Ops \28Day\29)(name=Ops \28Day\29)(mail=Ops \28Day\29))", "Ops (Day)")]
    [InlineData("Ops*Star", @"(|(sAMAccountName=Ops\2aStar)(name=Ops\2aStar)(mail=Ops\2aStar))", "Ops*Star")]
    [InlineData("Ops*", @"(|(sAMAccountName=Ops\2a)(name=Ops\2a)(mail=Ops\2a))", null)]
    [InlineData("Ops\0Star", @"(|(sAMAccountName=Ops\00Star)(name=Ops\00Star)(mail=Ops\00Star))", null)]
    [InlineData("x)(name=Payroll", @"(|(sAMAccountName=x\29\28name=Payroll)(name=x\29\28name=Payroll)(mail=x\29\28name=Payroll))", null)]
    public async Task AGroupNameWithACharacterAFilterGivesMeaningTo_ResolvesOnlyTheGroupNamedExactlyThat(string identity, string filter, string? name)
    {
        var (resolvedDn, filters) = await ResolveByName(identity, logonName: null);

        Assert.Equal(name is null ? null : Dn(name), resolvedDn);
        Assert.Equal([filter], filters);
    }
}
