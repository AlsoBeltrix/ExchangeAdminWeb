using System.Security.Claims;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.SelfServiceGroups;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a2y (2): a Group Management or Self-Service Groups refusal for want of the module's AD credential says why there is
/// none.
/// </summary>
/// <remarks>
/// Seven paths refused "AD credentials unavailable. Check the DelineaSecretId configuration for &lt;module&gt;." whatever the
/// lookup's reason - also when the ID was set and Delinea returned no credential for it, or the module's configuration could
/// not be read - so the operator was sent to fix an ID that was right (aud-a2x's defect, in these two modules). Each path is
/// driven for real through a real <see cref="ModuleCredentialService"/> - aud-a2x's <see cref="OfflineCredentials"/>, whose
/// Delinea service has no bootstrap credential and whose HTTP client counts and fails any request - over a real module
/// configuration store, so the words come from what the lookup found. No credential is ever returned, so no path here gets
/// as far as a directory session.
/// </remarks>
public sealed class GroupCredentialRefusalTests : IDisposable
{
    private const string CallerSid = "S-1-5-21-1000000000-1000000000-1000000000-1105";
    private const string GroupGuid = "6f1c2c7e-3b9a-4d55-9a51-1c2f0e4b7a10";
    private const string MemberGuid = "3c6e2a1f-9b7d-4e2a-8f51-0d4c1b2a7e90";

    private const string NoSecretId = "no secret ID";
    private const string NotReturned = "a secret ID Delinea returns no credential for";
    private const string Unreadable = "an unreadable module configuration";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eaw-a2y-" + Guid.NewGuid().ToString("N"));

    public GroupCredentialRefusalTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // Every path that refused with the one text, each with what its module's configuration says.
    public static TheoryData<string, string> Paths()
    {
        var data = new TheoryData<string, string>();
        foreach (var path in new[]
                 {
                     "owned-groups list", "single-group search", "member listing", "typed membership change",
                     "listed-member removal", "bulk member resolution", "group search",
                 })
        {
            foreach (var configured in new[] { NoSecretId, NotReturned, Unreadable })
                data.Add(path, configured);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task ARefusalForWantOfTheCredential_SaysWhyThereIsNone(string path, string configured)
    {
        // Refused in the path's own shape - a throw, a failed result, every line not attempted - in the words for what the
        // lookup found; "check the DelineaSecretId configuration" only where none is set. Nothing is sent to Delinea.
        var module = path == "group search" ? "GroupManagement" : "SelfServiceGroups";
        using var offline = CreateOffline(module, configured);

        var refusal = await Refuse(path, offline.Service, offline.ModuleConfig);

        Assert.Equal(Expected(module, configured), refusal);
        Assert.StartsWith("AD credentials unavailable.", refusal, StringComparison.Ordinal);
        Assert.Equal(configured == NoSecretId, refusal.Contains("Check the DelineaSecretId configuration", StringComparison.Ordinal));
        Assert.Equal(0, offline.Credentials.RequestsSent);
    }

    [Theory]
    [InlineData(ModuleCredentialFailure.SecretIdNotSet)]
    [InlineData(ModuleCredentialFailure.None)]
    [InlineData(ModuleCredentialFailure.NotReturned)]
    [InlineData(ModuleCredentialFailure.ConfigUnreadable)]
    public void TheSharedRefusal_ChoosesItsWordsFromTheFailure(ModuleCredentialFailure failure)
    {
        // No secret ID keeps today's text to the byte; None - which no lookup returns beside no credential - is read as that.
        var expected = failure switch
        {
            ModuleCredentialFailure.NotReturned =>
                "AD credentials unavailable. The DelineaSecretId for Mod is set, but Delinea returned no credential for it (the application log has its reason).",
            ModuleCredentialFailure.ConfigUnreadable =>
                "AD credentials unavailable. The Mod module configuration could not be read, so neither could its DelineaSecretId (the application log records it).",
            _ => "AD credentials unavailable. Check the DelineaSecretId configuration for Mod.",
        };

        Assert.Equal(expected, GroupManagementService.CredentialUnavailableRefusal("Mod", failure));
    }

    private static string Expected(string module, string configured) => configured switch
    {
        NoSecretId => $"AD credentials unavailable. Check the DelineaSecretId configuration for {module}.",
        NotReturned =>
            $"AD credentials unavailable. The DelineaSecretId for {module} is set, but Delinea returned no credential for it (the application log has its reason).",
        _ => $"AD credentials unavailable. The {module} module configuration could not be read, so neither could its DelineaSecretId (the application log records it).",
    };

    // The path's refusal text, from whichever shape the path refuses in.
    private static async Task<string> Refuse(string path, ModuleCredentialService credentials, ModuleConfigService moduleConfig)
    {
        var selfService = new SelfServiceGroupService(credentials, null!, null!, NullLogger<SelfServiceGroupService>.Instance);
        switch (path)
        {
            case "owned-groups list":
                return (await Assert.ThrowsAsync<InvalidOperationException>(() => selfService.GetOwnedGroupsAsync(CallerSid, [], []))).Message;
            case "single-group search":
                return (await Assert.ThrowsAsync<InvalidOperationException>(
                    () => selfService.SearchManageableGroupAsync(CallerSid, [], [], "Finance Approvers"))).Message;
            case "member listing":
                return (await Assert.ThrowsAsync<InvalidOperationException>(
                    () => selfService.GetGroupMembersAsync(CallerSid, [], [], GroupGuid))).Message;
            case "typed membership change":
                var added = await selfService.ChangeMemberAsync(CallerSid, GroupGuid, "alex.doe@example.test", MembershipOperation.Add, Operator());
                Assert.False(added.Result.Success);
                Assert.False(added.MembershipChanged);
                return added.Result.Message;
            case "listed-member removal":
                var removed = await selfService.RemoveListedMemberAsync(CallerSid, GroupGuid, MemberGuid, Operator());
                Assert.False(removed.Result.Success);
                Assert.False(removed.MembershipChanged);
                return removed.Result.Message;
            case "bulk member resolution":
                var lines = await selfService.ResolveBatchAsync(CallerSid, [new BulkIdentityList.Line(1, "alex.doe"), new BulkIdentityList.Line(2, "sam.roe")]);
                Assert.Equal(2, lines.Count);
                Assert.All(lines, l => Assert.Equal(BulkIdentityList.Status.NotAttempted, l.Status));
                var reason = Assert.Single(lines.Select(l => l.Reason).Distinct());
                // GroupManagementService.NotAttempted's prefix, on every line it marks.
                Assert.StartsWith("Not attempted - ", reason, StringComparison.Ordinal);
                return reason["Not attempted - ".Length..];
            case "group search":
                var groups = new GroupManagementService(moduleConfig, credentials, null!, null!, NullLogger<GroupManagementService>.Instance);
                return (await Assert.ThrowsAsync<InvalidOperationException>(() => groups.SearchGroupsAsync("Finance"))).Message;
            default:
                throw new ArgumentOutOfRangeException(nameof(path), path, null);
        }
    }

    private static ClaimsPrincipal Operator() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, @"EXAMPLE\operator")], "test"));

    private OfflineLookup CreateOffline(string module, string configured)
    {
        var environment = ModuleCredentialLookupTests.Environment(_dir);
        var moduleConfig = configured == Unreadable
            ? new ModuleConfigService(new ModuleCatalog(), environment, new ModuleConfigRepository(new UnreadableModuleConfigStore()),
                NullLogger<ModuleConfigService>.Instance)
            : new ModuleConfigService(new ModuleCatalog(), environment, TestConfigStore.CreateModuleConfig(_dir),
                NullLogger<ModuleConfigService>.Instance);
        if (configured == NotReturned)
            moduleConfig.SaveModuleConfig(module, new Dictionary<string, string> { ["DelineaSecretId"] = "4242" });
        return new OfflineLookup(moduleConfig, new OfflineCredentials(_dir, ModuleCredentialLookupTests.Config(_dir), moduleConfig));
    }

    private sealed record OfflineLookup(ModuleConfigService ModuleConfig, OfflineCredentials Credentials) : IDisposable
    {
        public ModuleCredentialService Service => Credentials.Service;

        public void Dispose() => Credentials.Dispose();
    }
}
