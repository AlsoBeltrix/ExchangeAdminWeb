using System.Management.Automation;
using System.Security;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/DirectoryObjectVerify-Plan.md A5, B.2 (slice V4): Verify of Migration's Excluded AD Groups through the module's own
/// per-domain lookup, <see cref="MigrationService.TryResolveExcludedGroup"/>, which the eligibility check now calls through its
/// thin thrower - and the module's own check, over the same stand-in forest, answering exactly as before for the same value.
/// </summary>
/// <remarks>
/// The real engine and adapter, reading in a warm session of <see cref="MigrationStubExchange"/>'s Active Directory stand-in -
/// the pool the module's check is lent - through the real <see cref="VerifySessionDirectory"/> and
/// <see cref="PowerShellProtectedGroupDirectory"/>. A forest of two domains, contoso.test (the host's) and fabrikam.test. No
/// directory, no Exchange.
/// </remarks>
public sealed class ModuleConfigVerifyExcludedGroupTests : IDisposable
{
    private const string Host = "contoso.test";
    private const string Second = "fabrikam.test";
    private const string Address = "bob@fabrikam.test";
    private const string SalesDn = "CN=Sales,OU=Groups,DC=contoso,DC=test";
    private const string SecondSalesDn = "CN=Sales,OU=Groups,DC=fabrikam,DC=test";
    private const string StaffDn = "CN=Staff,OU=Groups,DC=fabrikam,DC=test";
    private const string SalesGuid = "5a1e5a1e-0000-4000-8000-00000000a5a5";
    private const string Incomplete = "Could not be determined: the excluded-group check could not be completed: ";

    private readonly MigrationStubExchange _exchange = new();

    public ModuleConfigVerifyExcludedGroupTests() => _exchange.ForestDomains.Add(Second);

    public void Dispose() => _exchange.Dispose();

    /// <summary>The press's directory: a lease of the stand-in's warm sessions, as the module's check is lent one.</summary>
    private sealed class SessionAccess(DirectorySessionPool pool) : IVerifyDirectoryAccess
    {
        public Task<VerifyDirectoryRun<T>> RunAsync<T>(Func<IVerifyDirectory, T> reads, CancellationToken cancellationToken)
        {
            using var session = pool.Lease(TimeSpan.FromSeconds(30));
            var directory = new VerifySessionDirectory(new PowerShellProtectedGroupDirectory(
                session.PowerShell, new PSCredential("svc-verify", new SecureString())));
            return Task.FromResult(VerifyDirectoryRun<T>.Ran(reads(directory)));
        }
    }

    private async Task<IReadOnlyDictionary<string, VerifyFinding>> VerifyAsync(string value)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ExcludedADGroups"] = value };
        var engine = new DirectoryObjectVerifier(new SessionAccess(_exchange.DirectorySessions), [new ModuleConfigVerifyAdapter()],
            NullLogger<DirectoryObjectVerifier>.Instance);
        var report = await engine.VerifyAsync(ModuleConfigVerifyAdapter.ItemsFrom("Migration", values, "v"), CancellationToken.None);
        return report.Results.ToDictionary(r => r.Item.StoredValue, r => r.Finding, StringComparer.OrdinalIgnoreCase);
    }

    private Task<MigrationEligibilityResult> Check() =>
        _exchange.Service().CheckMigrationEligibilityAsync(Address, MigrationDirection.ToCloud);

    [Fact]
    public async Task AGuidEntry_Verifies_ThroughTheModulesOwnLookupInEachDomain()
    {
        _exchange.DirectoryGroups[SalesGuid] = SalesDn;

        var findings = await VerifyAsync($" {SalesGuid} ");

        Assert.Equal(VerifyFindingKind.Verified, Assert.Single(findings).Value.Kind);
        Assert.Equal([$"Get-ADGroup -Server {Host} {SalesGuid}", $"Get-ADGroup -Server {Second} {SalesGuid}"], _exchange.DirectoryCallList());
    }

    [Fact]
    public async Task AValueNoDomainHolds_IsMissing_NotUnreadable_AndTheModulesOwnCheckStillThrowsForIt()
    {
        var findings = await VerifyAsync("Sales");

        Assert.Equal(VerifyFindingKind.Missing, findings["Sales"].Kind);

        // The module, unchanged: the same value refuses its check, in the same words as before the extraction.
        _exchange.ExcludeGroups("Sales");
        _exchange.DirectoryUsers[Address] = Users(DirectoryUserIn(Second, Address, StaffDn));
        var result = await Check();
        Assert.Equal(MigrationStatus.Undetermined, result.Status);
        Assert.Equal(
            [Incomplete + "the excluded group 'Sales' could not be resolved in any domain of the forest ("
                + $"{Host}: Cannot find an object with identity: 'Sales' under: 'DC=contoso,DC=test'; "
                + $"{Second}: Cannot find an object with identity: 'Sales' under: 'DC=fabrikam,DC=test')"],
            result.IneligibilityReasons);
    }

    [Fact]
    public async Task AValueSeveralDomainsHold_IsSeveralObjects_AndTheModuleStillExcludesAMemberOfEach()
    {
        _exchange.DirectoryGroups["Sales"] = new[] { SalesDn, SecondSalesDn };

        var finding = (await VerifyAsync("Sales"))["Sales"];

        Assert.Equal(VerifyFindingKind.Ambiguous, finding.Kind);
        Assert.Equal($"{Host}: {SalesDn}; {Second}: {SecondSalesDn}", finding.Detail);

        _exchange.ExcludeGroups("Sales");
        foreach (var memberOf in new[] { SalesDn, SecondSalesDn })
        {
            _exchange.DirectoryUsers[Address] = Users(DirectoryUserIn(Second, Address, memberOf));
            var result = await Check();
            Assert.Equal(MigrationStatus.Ineligible, result.Status);
            Assert.StartsWith("Member of excluded group: Sales (", Assert.Single(result.IneligibilityReasons), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ADomainThatCannotBeAsked_IsUnreadable_NeverMissing_AndOnlyForThatEntry()
    {
        _exchange.DirectoryGroups["Staff"] = StaffDn;
        _exchange.Failures[$"Get-ADGroup Sales @{Second}"] = "the server is not operational";

        var findings = await VerifyAsync("Sales, Staff");

        Assert.Equal(VerifyFindingKind.Unreadable, findings["Sales"].Kind);
        Assert.Contains("the server is not operational", findings["Sales"].Detail, StringComparison.Ordinal);
        Assert.Equal(VerifyFindingKind.Verified, findings["Staff"].Kind);
    }

    [Fact]
    public async Task EachCommaEntry_IsItsOwnItem_SplitAsTheModuleSplitsIt()
    {
        _exchange.DirectoryGroups["Sales"] = SalesDn;

        var findings = await VerifyAsync("Sales,,  Nobody ,");

        Assert.Equal(["Nobody", "Sales"], findings.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((VerifyFindingKind.Verified, VerifyFindingKind.Missing), (findings["Sales"].Kind, findings["Nobody"].Kind));
    }
}
