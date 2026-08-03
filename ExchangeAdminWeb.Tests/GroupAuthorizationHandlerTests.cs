using System.Security.Claims;
using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Direct tests for the single gate every privileged policy funnels through.
/// Uses the real SectionAccessService / ModuleEnablementService over a temp
/// config directory (same approach as SectionAccessServiceTests) so the
/// fail-closed semantics under test are the production ones.
/// </summary>
public class GroupAuthorizationHandlerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configDir;
    private readonly SqliteConfigStore _store;
    private readonly ModuleEnablementRepository _enablementRepo;

    public GroupAuthorizationHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"gah-test-{Guid.NewGuid():N}");
        _configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(_configDir);
        _store = TestConfigStore.Create(_tempDir);
        _enablementRepo = new ModuleEnablementRepository(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private GroupAuthorizationHandler CreateHandler()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AllowedGroups:0"] = "AllUsersGroup",
                ["Security:AdminGroups:0"] = "AdminGroup"
            })
            .Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);

        var catalog = new ModuleCatalog();
        var sectionAccess = new SectionAccessService(config, Substitute.For<ILogger<SectionAccessService>>(), env, catalog, new SectionAccessRepository(_store));
        var moduleConfig = new ModuleConfigService(catalog, env, TestConfigStore.CreateModuleConfig(_tempDir), Substitute.For<ILogger<ModuleConfigService>>());
        var enablement = new ModuleEnablementService(catalog, env, moduleConfig, new ModuleEnablementRepository(_store), config, Substitute.For<ILogger<ModuleEnablementService>>());

        return new GroupAuthorizationHandler(
            Substitute.For<ILogger<GroupAuthorizationHandler>>(), sectionAccess, catalog, enablement);
    }

    private void WriteSectionAccess(Dictionary<string, string[]> sections)
    {
        // Section access now lives in the DB; seed the shared store the handler's service reads.
        new SectionAccessRepository(_store).SaveAll(sections);
    }

    private void WriteEnablement(Dictionary<string, bool> state)
    {
        // Enablement now lives in the DB; seed the shared store the handler's service reads.
        _enablementRepo.SaveAll(state);
    }

    // Real group SIDs from this deployment, so the fixtures are the shape production stores.
    private const string DomainSid = "S-1-5-21-8915387-325452579-1788637320";
    private const string ExchangeAdminsSid = DomainSid + "-677335";
    private const string OtherGroupSid = DomainSid + "-586078";

    private static ClaimsPrincipal MakeUser(params string[] groups)
    {
        // GroupSid, not Role: that is what Negotiate actually populates (0 of 1687 prod
        // authorizations came through Role). The handler reads both.
        var claims = new List<Claim> { new(ClaimTypes.Name, "CONTOSO\\tester") };
        claims.AddRange(groups.Select(g => new Claim(ClaimTypes.GroupSid, g)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static async Task<AuthorizationHandlerContext> HandleAsync(
        GroupAuthorizationHandler handler, GroupAuthorizationRequirement requirement, ClaimsPrincipal user)
    {
        var context = new AuthorizationHandlerContext([requirement], user, null);
        await handler.HandleAsync(context);
        return context;
    }

    [Fact]
    public async Task DynamicSection_NoGroupsConfigured_Denies()
    {
        // Fail-closed section with no section-access source at all: deny.
        var handler = CreateHandler();
        var requirement = new GroupAuthorizationRequirement("MailboxPermissions", dynamic: true);

        var context = await HandleAsync(handler, requirement, MakeUser(ExchangeAdminsSid));

        Assert.False(context.HasSucceeded);
        Assert.True(context.HasFailed);
    }

    [Fact]
    public async Task DynamicSection_DisabledModule_DeniesEvenForGroupMember()
    {
        var handler = CreateHandler();
        WriteSectionAccess(new() { ["MailboxPermissions"] = [ExchangeAdminsSid] });
        WriteEnablement(new() { ["ExchangeOnline"] = true, ["MailboxPermissions"] = false });
        var requirement = new GroupAuthorizationRequirement("MailboxPermissions", dynamic: true);

        var context = await HandleAsync(handler, requirement, MakeUser(ExchangeAdminsSid));

        Assert.False(context.HasSucceeded);
        Assert.True(context.HasFailed);
    }

    [Fact]
    public async Task DynamicSection_EnabledModuleAndGroupMember_Succeeds()
    {
        var handler = CreateHandler();
        WriteSectionAccess(new() { ["MailboxPermissions"] = [ExchangeAdminsSid] });
        WriteEnablement(new() { ["ExchangeOnline"] = true, ["MailboxPermissions"] = true });
        var requirement = new GroupAuthorizationRequirement("MailboxPermissions", dynamic: true);

        var context = await HandleAsync(handler, requirement, MakeUser(ExchangeAdminsSid));

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task DynamicGroups_UnmigratedNameValue_DeniesEveryone()
    {
        // Review finding sid-1. A section whose rows are still names must authorize NOBODY, even
        // the very people the name denotes. Exact comparison alone does not achieve this: a role
        // claim can carry a name, and IsInRole resolves names as well as SIDs, so without the
        // filter this user would be let in on an ambiguous identifier.
        var handler = CreateHandler();
        WriteSectionAccess(new() { ["MailboxPermissions"] = ["ExchangeAdmins"] });
        WriteEnablement(new() { ["ExchangeOnline"] = true, ["MailboxPermissions"] = true });
        var requirement = new GroupAuthorizationRequirement("MailboxPermissions", dynamic: true);

        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "CONTOSO\\tester"), new Claim(ClaimTypes.Role, "ExchangeAdmins")],
            "TestAuth"));
        var context = await HandleAsync(handler, requirement, user);

        Assert.False(context.HasSucceeded);
        Assert.True(context.HasFailed);
    }

    [Fact]
    public async Task DynamicGroups_PartiallyMigratedSection_StillHonoursTheMigratedRows()
    {
        // Dropping unmigrated rows must not be all-or-nothing: a section mid-migration keeps
        // working for the groups that did convert.
        var handler = CreateHandler();
        WriteSectionAccess(new() { ["MailboxPermissions"] = ["LegacyName", ExchangeAdminsSid] });
        WriteEnablement(new() { ["ExchangeOnline"] = true, ["MailboxPermissions"] = true });
        var requirement = new GroupAuthorizationRequirement("MailboxPermissions", dynamic: true);

        var context = await HandleAsync(handler, requirement, MakeUser(ExchangeAdminsSid));

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task StaticGroups_DomainQualifiedConfigEntry_NoLongerMatchesBareRoleClaim()
    {
        // Asserted True until 2026-08-03: the handler stripped "CONTOSO\" and matched any bare
        // "Admins" claim. That normalization WAS the defect - it made CONTOSO\Admins and a
        // different trusted domain's Admins indistinguishable in the field that decides who
        // reaches a privileged module. Stored values are SIDs now, which are self-qualifying, so
        // the comparison is exact and this must NOT match.
        // See docs/SectionAccessSidStorage-Plan.md.
        var handler = CreateHandler();
        var requirement = new GroupAuthorizationRequirement(["CONTOSO\\Admins"]);

        var context = await HandleAsync(handler, requirement, MakeUser("Admins"));

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task StaticGroups_MatchIsCaseInsensitive()
    {
        var handler = CreateHandler();
        var requirement = new GroupAuthorizationRequirement([ExchangeAdminsSid.ToLowerInvariant()]);

        var context = await HandleAsync(handler, requirement, MakeUser(ExchangeAdminsSid));

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task StaticGroups_NonMember_Denies()
    {
        var handler = CreateHandler();
        var requirement = new GroupAuthorizationRequirement([ExchangeAdminsSid]);

        var context = await HandleAsync(handler, requirement, MakeUser(OtherGroupSid));

        Assert.False(context.HasSucceeded);
        Assert.True(context.HasFailed);
    }

    [Fact]
    public async Task StaticGroups_EmptyList_Denies()
    {
        // Empty Security:AllowedGroups must deny everyone, never allow-all.
        var handler = CreateHandler();
        var requirement = new GroupAuthorizationRequirement(Array.Empty<string>());

        var context = await HandleAsync(handler, requirement, MakeUser(ExchangeAdminsSid));

        Assert.False(context.HasSucceeded);
        Assert.True(context.HasFailed);
    }
}
