using System.Collections;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a52: the members of a group only Exchange Online holds, as the self-grant check asks for them - the reader over a
/// local runspace whose Exchange cmdlets are advanced functions (an unexpected parameter is a binding error), and the
/// resolver that runs it through the pool. Nothing here reaches Exchange.
/// </summary>
public sealed class ExchangeGroupMembersTests : IDisposable
{
    private readonly Hashtable _groups = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));
    private readonly ArrayList _calls = ArrayList.Synchronized(new ArrayList());
    private readonly Hashtable _failures = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));
    private readonly Runspace _runspace;
    private readonly PowerShell _ps;
    private readonly ExchangeWriteHarness _h = new();

    private const string Stubs = """
        function global:Get-Recipient {
            [CmdletBinding()] param($Identity)
            [void]$StandInCalls.Add("Get-Recipient $Identity")
            $group = $StandInGroups["$Identity"]
            if ($null -eq $group) { Write-Error "The operation couldn't be performed because object '$Identity' couldn't be found on 'stand-in'."; return }
            [pscustomobject]@{ PrimarySmtpAddress = $group.Address; RecipientType = $group.Type; RecipientTypeDetails = $group.Kind; IsDirSynced = $false }
        }
        function global:Get-DistributionGroupMember {
            [CmdletBinding()] param($Identity, $ResultSize)
            [void]$StandInCalls.Add("Get-DistributionGroupMember $Identity $ResultSize")
            if ($StandInFailures.ContainsKey("$Identity")) { Write-Error $StandInFailures["$Identity"]; return }
            foreach ($member in $StandInGroups["$Identity"].Members) { [pscustomobject]$member }
        }
        function global:Get-UnifiedGroupLinks {
            [CmdletBinding()] param($Identity, $LinkType, $ResultSize)
            [void]$StandInCalls.Add("Get-UnifiedGroupLinks $Identity $LinkType $ResultSize")
            if ($StandInFailures.ContainsKey("$Identity")) { Write-Error $StandInFailures["$Identity"]; return }
            foreach ($member in $StandInGroups["$Identity"].Members) { [pscustomobject]$member }
        }
        function global:Get-DynamicDistributionGroupMember {
            [CmdletBinding()] param($Identity, $ResultSize)
            [void]$StandInCalls.Add("Get-DynamicDistributionGroupMember $Identity $ResultSize")
            if ($StandInFailures.ContainsKey("$Identity")) { Write-Error $StandInFailures["$Identity"]; return }
            foreach ($member in $StandInGroups["$Identity"].Members) { [pscustomobject]$member }
        }
        """;

    public ExchangeGroupMembersTests()
    {
        _runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
        _runspace.Open();
        _runspace.SessionStateProxy.SetVariable("StandInGroups", _groups);
        _runspace.SessionStateProxy.SetVariable("StandInCalls", _calls);
        _runspace.SessionStateProxy.SetVariable("StandInFailures", _failures);
        using (var init = PowerShell.Create())
        {
            init.Runspace = _runspace;
            init.AddScript(Stubs).Invoke();
        }
        _ps = PowerShell.Create();
        _ps.Runspace = _runspace;
    }

    public void Dispose()
    {
        _ps.Dispose();
        _runspace.Dispose();
        _h.Dispose();
    }

    private const string Dl = "MailUniversalDistributionGroup";
    private const string Security = "MailUniversalSecurityGroup";
    private const string Microsoft365 = "GroupMailbox";

    // aud-a60. The RecipientType Exchange reports for a Microsoft 365 group: no group in it - only its RecipientTypeDetails,
    // GroupMailbox, says it is one. These stubs gave it a distribution group's type, which hid that the check read the type
    // alone.
    private const string Microsoft365Type = "UserMailbox";

    private void Group(string address, string? kind, params Hashtable[] members) => _groups[address] = new Hashtable
    {
        ["Address"] = address,
        ["Kind"] = kind,
        ["Type"] = kind == Microsoft365 ? Microsoft365Type : kind,
        ["Members"] = members,
    };

    private static Hashtable User(string address) => new()
    {
        ["PrimarySmtpAddress"] = address,
        ["ExternalDirectoryObjectId"] = "ext-" + address,
        ["RecipientType"] = "UserMailbox",
        ["RecipientTypeDetails"] = "UserMailbox",
        ["IsDirSynced"] = false,
    };

    private static Hashtable Nested(string? address, string kind, bool? synced)
    {
        var row = new Hashtable { ["RecipientType"] = kind == Microsoft365 ? Microsoft365Type : kind, ["RecipientTypeDetails"] = kind };
        if (address is not null)
            row["PrimarySmtpAddress"] = address;
        if (synced is not null)
            row["IsDirSynced"] = synced.Value;
        return row;
    }

    /// <summary>What the resolver's pooled invoke does: errors throw, the pipeline is cleared.</summary>
    private static Collection<PSObject> Invoke(PowerShell ps)
    {
        try
        {
            var rows = ps.Invoke();
            if (ps.HadErrors)
                throw new InvalidOperationException(ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "error");
            return rows;
        }
        finally
        {
            ps.Commands.Clear();
            ps.Streams.Error.Clear();
        }
    }

    private IReadOnlyList<ExchangeGroupMember> Read(string group) => ExchangeGroupMembers.Read(_ps, group, Invoke);

    private IReadOnlyList<string> Calls => _calls.Cast<string>().ToList();

    [Fact]
    public void TheMembersOfADistributionOrSecurityGroup_AreReadWhole()
    {
        Group("team@contoso.test", Security, User("a@contoso.test"), User("b@contoso.test"));

        var members = Read("team@contoso.test");

        Assert.Equal(["a@contoso.test", "b@contoso.test"], members.Select(member => member.PrimarySmtpAddress));
        Assert.Equal("ext-a@contoso.test", members[0].ExternalDirectoryObjectId);
        Assert.False(members[0].IsGroup);
        Assert.Equal(["Get-Recipient team@contoso.test", "Get-DistributionGroupMember team@contoso.test Unlimited"], Calls);
    }

    [Fact]
    public void TheMembersOfAMicrosoft365Group_AreItsMemberLinks()
    {
        Group("m365@contoso.test", Microsoft365, User("a@contoso.test"));

        var members = Read("m365@contoso.test");

        Assert.Equal("a@contoso.test", Assert.Single(members).PrimarySmtpAddress);
        Assert.Equal(["Get-Recipient m365@contoso.test", "Get-UnifiedGroupLinks m365@contoso.test Members Unlimited"], Calls);
    }

    [Fact]
    public void ACloudGroupNestedInIt_IsReadToo()
    {
        Group("outer@contoso.test", Dl, User("a@contoso.test"), Nested("inner@contoso.test", Security, synced: false));
        Group("inner@contoso.test", Security, User("b@contoso.test"), Nested("m365@contoso.test", Microsoft365, synced: false));
        Group("m365@contoso.test", Microsoft365, User("c@contoso.test"));

        var members = Read("outer@contoso.test");

        Assert.Equal(["a@contoso.test", "inner@contoso.test", "b@contoso.test", "m365@contoso.test", "c@contoso.test"],
            members.Select(member => member.PrimarySmtpAddress));
        Assert.Contains("Get-DistributionGroupMember inner@contoso.test Unlimited", Calls);
        Assert.Contains("Get-UnifiedGroupLinks m365@contoso.test Members Unlimited", Calls);
    }

    [Fact]
    public void AMicrosoft365GroupNestedInIt_IsAGroup_ByItsDetails_AndIsRead()
    {
        // aud-a60. Exchange reports a Microsoft 365 group with RecipientType UserMailbox; only RecipientTypeDetails names it a
        // group. Read by its type alone it was a person: its members were never read, and the self-grant check compared the
        // group itself with the operator.
        Group("outer@contoso.test", Dl, Nested("m365@contoso.test", Microsoft365, synced: false));
        Group("m365@contoso.test", Microsoft365, User("inside@contoso.test"));

        var members = Read("outer@contoso.test");

        var nested = Assert.Single(members, member => member.PrimarySmtpAddress == "m365@contoso.test");
        Assert.Equal(Microsoft365Type, nested.RecipientType);
        Assert.True(nested.IsGroup);
        Assert.Contains(members, member => member.PrimarySmtpAddress == "inside@contoso.test");
        Assert.Contains("Get-UnifiedGroupLinks m365@contoso.test Members Unlimited", Calls);
    }

    // ---- aud-a72: a dynamic distribution group --------------------------------------------------------------------

    private const string Dynamic = "DynamicDistributionGroup";

    [Fact]
    public void TheMembersOfADynamicDistributionGroup_AreTheOnesExchangeCalculated()
    {
        // Classified a group (aud-a60) and read by no cmdlet, so every grant to one was refused as incomplete. Its members are
        // the ones Exchange calculated from its filter - nothing here evaluates the filter.
        Group("dynamic@contoso.test", Dynamic, User("a@contoso.test"), User("b@contoso.test"));

        var members = Read("dynamic@contoso.test");

        Assert.Equal(["a@contoso.test", "b@contoso.test"], members.Select(member => member.PrimarySmtpAddress));
        Assert.False(members[0].IsGroup);
        Assert.Equal(["Get-Recipient dynamic@contoso.test", "Get-DynamicDistributionGroupMember dynamic@contoso.test Unlimited"], Calls);
    }

    [Fact]
    public void ACloudGroupInADynamicGroup_AndADynamicGroupInACloudGroup_AreEachReadInTurn()
    {
        // The same rules as any group's members: a cloud-only group among them is read once, by its address.
        Group("outer@contoso.test", Dl, Nested("dynamic@contoso.test", Dynamic, synced: false));
        Group("dynamic@contoso.test", Dynamic, User("a@contoso.test"), Nested("inner@contoso.test", Security, synced: false));
        Group("inner@contoso.test", Security, User("b@contoso.test"));

        var members = Read("outer@contoso.test");

        Assert.Equal(["dynamic@contoso.test", "a@contoso.test", "inner@contoso.test", "b@contoso.test"],
            members.Select(member => member.PrimarySmtpAddress));
        Assert.Equal(["Get-Recipient outer@contoso.test", "Get-DistributionGroupMember outer@contoso.test Unlimited",
            "Get-DynamicDistributionGroupMember dynamic@contoso.test Unlimited", "Get-DistributionGroupMember inner@contoso.test Unlimited"], Calls);
    }

    [Theory]
    [InlineData("UserMailbox", "GroupMailbox", true)]
    [InlineData(Dl, "GroupMailbox", true)]
    [InlineData(null, "GroupMailbox", true)]
    [InlineData(Dl, Dl, true)]
    [InlineData(Security, Security, true)]
    [InlineData("MailNonUniversalGroup", "MailNonUniversalGroup", true)]
    [InlineData("DynamicDistributionGroup", "DynamicDistributionGroup", true)]
    [InlineData(Dl, "RoomList", true)]
    [InlineData(null, "RoomList", true)]
    [InlineData("UserMailbox", "UserMailbox", false)]
    [InlineData("UserMailbox", "SharedMailbox", false)]
    [InlineData("MailUser", "GuestMailUser", false)]
    [InlineData("MailContact", "MailContact", false)]
    [InlineData(null, null, false)]
    public void ARecipientIsAGroup_WhenEitherKindExchangeGaveNamesOne(string? type, string? details, bool group)
    {
        // aud-a60. One rule for a grantee and for a member: a person read as a group is refused as incomplete (its membership
        // cannot be read), a group read as a person is allowed - so the rule reads either kind. Rows 1-3 are a Microsoft 365
        // group as Get-Recipient, Get-UnifiedGroup and a row without a type report it.
        Assert.Equal(group, ExchangeRecipientKinds.NamesAGroup(type, details));
        Assert.Equal(group, new ExchangeGroupMember("x@contoso.test", null, type, details, false).IsGroup);
        Assert.Equal(group, new ResolvedRecipient("x@contoso.test", null, type, false, RecipientTypeDetails: details).IsGroup);
    }

    [Fact]
    public void AGroupNestedInItsOwnMember_IsReadOnce()
    {
        Group("a-team@contoso.test", Dl, Nested("b-team@contoso.test", Dl, synced: false));
        Group("b-team@contoso.test", Dl, Nested("a-team@contoso.test", Dl, synced: false), User("x@contoso.test"));

        var members = Read("a-team@contoso.test");

        Assert.Contains(members, member => member.PrimarySmtpAddress == "x@contoso.test");
        Assert.Single(Calls, call => call.StartsWith("Get-DistributionGroupMember a-team@", StringComparison.Ordinal));
        Assert.Single(Calls, call => call.StartsWith("Get-DistributionGroupMember b-team@", StringComparison.Ordinal));
    }

    [Fact]
    public void ASyncedGroupNestedInIt_IsListed_AndNotRead()
    {
        Group("outer@contoso.test", Dl, Nested("synced@contoso.test", Security, synced: true));
        Group("synced@contoso.test", Security, User("hidden@contoso.test"));

        var members = Read("outer@contoso.test");

        var synced = Assert.Single(members);
        Assert.True(synced.IsGroup);
        Assert.True(synced.IsDirSynced);
        Assert.DoesNotContain(Calls, call => call.Contains("synced@contoso.test", StringComparison.Ordinal));
    }

    // aud-a61: a nesting past the bound is its own case below, with its own exception and text. aud-a72: a dynamic group is
    // read now, so its row is a group of a kind no cmdlet reads; and a dynamic group's read fails when it fails, and when it
    // reports no member.
    public static TheoryData<string> Failures =>
    [
        "not found", "no kind", "a group of a kind no cmdlet reads", "a nested group without an address",
        "a nested group without a sync state", "a member read that fails", "a dynamic group that reports no member",
        "a dynamic group whose read fails",
    ];

    [Theory]
    [MemberData(nameof(Failures))]
    public void AMembershipThatCannotBeReadWhole_Throws(string shape)
    {
        // The self-grant check reads a throw as a check that could not be completed; nothing here may answer short.
        const string Root = "team@contoso.test";
        switch (shape)
        {
            case "not found":
                break;
            case "no kind":
                Group(Root, null, User("a@contoso.test"));
                break;
            case "a group of a kind no cmdlet reads":
                Group(Root, "UniversalSecurityGroup", User("a@contoso.test"));
                break;
            case "a dynamic group that reports no member":
                Group(Root, Dynamic);
                break;
            case "a dynamic group whose read fails":
                Group(Root, Dynamic, User("a@contoso.test"));
                _failures[Root] = "The operation couldn't be performed because the server is busy.";
                break;
            case "a nested group without an address":
                Group(Root, Dl, Nested(null, Security, synced: false));
                break;
            case "a nested group without a sync state":
                Group(Root, Dl, Nested("inner@contoso.test", Security, synced: null));
                Group("inner@contoso.test", Security, User("a@contoso.test"));
                break;
            default:
                Group(Root, Dl, User("a@contoso.test"));
                _failures[Root] = "The operation couldn't be performed because the server is busy.";
                break;
        }

        Assert.ThrowsAny<Exception>(() => Read(Root));
    }

    [Fact]
    public void ADynamicGroupThatReportsNoMember_ThrowsNamingIt_AtTheRootOrNested()
    {
        // aud-a70 (11). Still no answer - an empty calculated membership cannot be told from one not yet calculated - but the
        // throw names the group read empty, so the check can say which: the grantee itself, or a group nested in it.
        const string Root = "team@contoso.test";
        const string Inner = "inner-dynamic@contoso.test";
        Group(Root, Dynamic);
        Group("outer@contoso.test", Dl, User("a@contoso.test"), Nested(Inner, Dynamic, synced: false));
        Group(Inner, Dynamic);

        Assert.Equal(Root, Assert.Throws<ExchangeDynamicGroupEmptyException>(() => Read(Root)).Group);
        Assert.Equal(Inner, Assert.Throws<ExchangeDynamicGroupEmptyException>(() => Read("outer@contoso.test")).Group);
    }

    /// <summary>A chain of <paramref name="nested"/> cloud groups below group0, the last holding a@contoso.test.</summary>
    private void Chain(int nested)
    {
        for (var i = 0; i < nested; i++)
            Group($"group{i}@contoso.test", Dl, Nested($"group{i + 1}@contoso.test", Dl, synced: false));
        Group($"group{nested}@contoso.test", Dl, User("a@contoso.test"));
    }

    [Fact]
    public void ANestingPastTheBound_Throws_NamingTheBound()
    {
        // aud-a61. One more nested group than the bound: the read stops when it finds it, unread, and says why - the
        // self-grant check refuses on it as a check it could not complete, never as "not a member".
        Chain(ExchangeGroupMembers.MaxNestedGroups + 1);

        var thrown = Assert.Throws<ExchangeGroupNestingBoundException>(() => Read("group0@contoso.test"));

        Assert.Equal(ExchangeGroupMembers.MaxNestedGroups, thrown.Bound);
        Assert.Equal("group0@contoso.test", thrown.Group);
        Assert.Equal($"'group0@contoso.test' holds more than {ExchangeGroupMembers.MaxNestedGroups} nested groups", thrown.Message);
        Assert.Equal(ExchangeGroupMembers.MaxNestedGroups + 1,
            Calls.Count(call => call.StartsWith("Get-DistributionGroupMember ", StringComparison.Ordinal)));
    }

    [Fact]
    public void AsManyNestedGroupsAsTheBound_AreReadWhole()
    {
        // The control for the bound: the group and every group nested under it, one read each.
        Chain(ExchangeGroupMembers.MaxNestedGroups);

        var members = Read("group0@contoso.test");

        Assert.Contains(members, member => member.PrimarySmtpAddress == "a@contoso.test");
        Assert.Equal(ExchangeGroupMembers.MaxNestedGroups + 1,
            Calls.Count(call => call.StartsWith("Get-DistributionGroupMember ", StringComparison.Ordinal)));
    }

    [Fact]
    public void AHundredNestedGroups_PastTheOldBound_AreReadWhole()
    {
        // aud-a52's bound refused anything nesting 50 groups; a membership of a hundred cloud groups is read and answered.
        Chain(100);

        var members = Read("group0@contoso.test");

        Assert.Contains(members, member => member.PrimarySmtpAddress == "a@contoso.test");
    }

    // ---- the resolver ------------------------------------------------------------------------------

    private ExchangeIdentityResolver ResolverOver(ExoConnectionPool pool) =>
        new(pool, null!, NullLogger<ExchangeIdentityResolver>.Instance, _h.Config);

    [Fact]
    public async Task TheResolver_ReadsTheMembers_ThroughThePool()
    {
        _h.AddMailbox("team@contoso.test", Guid.NewGuid(), recipientTypeDetails: Security);
        _h.ListMembers.Add("a@contoso.test");
        _h.ListMembers.Add("b@contoso.test");

        var members = await ResolverOver(_h.Pool).ReadGroupMembersAsync("team@contoso.test");

        Assert.Equal(["a@contoso.test", "b@contoso.test"], members.Select(member => member.PrimarySmtpAddress));
        Assert.Equal("Unlimited", ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Get-DistributionGroupMember")), "ResultSize")?.ToString());
        Assert.Equal(1, _h.Connects);
    }

    [Fact]
    public async Task TheResolver_ReadsADynamicGroupsMembers_ThroughThePool()
    {
        // aud-a72: through the pool as any group's read, by the dynamic group's own cmdlet.
        _h.AddMailbox("dynamic@contoso.test", Guid.NewGuid(), recipientTypeDetails: Dynamic);
        _h.ListMembers.Add("a@contoso.test");

        var members = await ResolverOver(_h.Pool).ReadGroupMembersAsync("dynamic@contoso.test");

        Assert.Equal("a@contoso.test", Assert.Single(members).PrimarySmtpAddress);
        Assert.Equal("Unlimited", ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Get-DynamicDistributionGroupMember")), "ResultSize")?.ToString());
        Assert.Empty(_h.CallsTo("Get-DistributionGroupMember"));
        Assert.Equal(1, _h.Connects);
    }

    [Fact]
    public async Task TheResolver_OverAnExchangeOnlineThatIsNotConfigured_Throws_AndConnectsNothing()
    {
        _h.Enablement.SetModulesEnabled(new Dictionary<string, bool> { [ExoConnectionPool.ConfigModuleKey] = false }, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ResolverOver(_h.Pool).ReadGroupMembersAsync("team@contoso.test"));

        Assert.Equal(0, _h.Connects);
    }

    [Fact]
    public async Task TheResolver_OverExchangeOnlineRowsThatCannotBeRead_Throws_AndConnectsNothing()
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_h.Dir);
        var unreadable = new UnreadableStore(TestConfigStore.Create(_h.Dir));
        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env, new ModuleConfigRepository(unreadable),
            NullLogger<ModuleConfigService>.Instance);
        var enablement = new ModuleEnablementService(new ModuleCatalog(), env, moduleConfig,
            new ModuleEnablementRepository(TestConfigStore.Create(_h.Dir)), _h.Config, NullLogger<ModuleEnablementService>.Instance);
        var connects = 0;
        using var pool = new ExoConnectionPool(_h.Config, moduleConfig, enablement, NullLogger<ExoConnectionPool>.Instance, _h.Trace,
            connect: (_, _, _) =>
            {
                Interlocked.Increment(ref connects);
                throw new InvalidOperationException("this test opens no Exchange session");
            });

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => ResolverOver(pool).ReadGroupMembersAsync("team@contoso.test"));

        Assert.Contains("could not be read", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(0, connects);
    }

    /// <summary>A config store whose every read fails; writes pass.</summary>
    private sealed class UnreadableStore(IConfigStore inner) : IConfigStore
    {
        public long GetChangeToken() => inner.GetChangeToken();

        public T Read<T>(Func<SqliteConnection, T> read) => throw new InvalidOperationException("config store unreadable");

        public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write) => inner.Write(write);

        public void Write(Action<SqliteConnection, SqliteTransaction> write) => inner.Write(write);
    }
}
