using System.Security.Claims;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class ModuleAdminServiceTests
{
    [Fact]
    public void SetAndGet_RoundTripsAdminGroups()
    {
        using var temp = new TempDir();
        var service = CreateService(temp.Path, out _);

        service.SetModuleAdmins("Migration", new[] { "DOMAIN\\Migration-Admins" });

        Assert.Equal(new[] { "DOMAIN\\Migration-Admins" }, service.GetModuleAdmins("Migration"));
    }

    [Fact]
    public void GetModuleAdmins_UnknownModule_ReturnsEmpty_FailOpen()
    {
        using var temp = new TempDir();
        var service = CreateService(temp.Path, out _);

        Assert.Empty(service.GetModuleAdmins("DoesNotExist"));
    }

    [Fact]
    public void SetModuleAdmins_OverwritesWholeList()
    {
        using var temp = new TempDir();
        var service = CreateService(temp.Path, out _);

        service.SetModuleAdmins("Migration", new[] { "A", "B" });
        service.SetModuleAdmins("Migration", new[] { "C" });

        Assert.Equal(new[] { "C" }, service.GetModuleAdmins("Migration"));
    }

    [Fact]
    public void ModuleId_IsCaseInsensitive()
    {
        using var temp = new TempDir();
        var service = CreateService(temp.Path, out _);

        service.SetModuleAdmins("Migration", new[] { "DOMAIN\\X" });

        Assert.Equal(new[] { "DOMAIN\\X" }, service.GetModuleAdmins("migration"));
    }

    // Two domains, same group name, same RID. The whole of A1 is that these must not be
    // interchangeable in the field that decides who can reach a module's configuration.
    private const string HomeOpsSid = "S-1-5-21-1111111111-2222222222-3333333333-1101";
    private const string ForeignOpsSid = "S-1-5-21-1444444444-1555555555-1666666666-1101";

    [Fact]
    public void IsModuleAdmin_MatchesGrantedGroupSid()
    {
        using var temp = new TempDir();
        var service = CreateService(temp.Path, out _);
        service.SetModuleAdmins("Migration", new[] { HomeOpsSid });

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.GroupSid, HomeOpsSid),
        }, "test"));

        Assert.True(service.IsModuleAdmin("Migration", user));
        Assert.False(service.IsModuleAdmin("Migration",
            new ClaimsPrincipal(new ClaimsIdentity(Array.Empty<Claim>(), "test"))));
    }

    [Fact]
    public void IsModuleAdmin_DoesNotAcceptAnotherDomainsSameNamedGroup()
    {
        // Audit finding A1. IsModuleAdmin stripped the domain from the stored grant and then
        // accepted a bare-name match, so a grant for FOREIGN\Ops was satisfied by a member of
        // HOME\Ops. The caller here is in the WRONG domain's group and additionally carries a
        // bare role claim spelled like the granted group - the exact shape that used to pass.
        using var temp = new TempDir();
        var service = CreateService(temp.Path, out _);
        service.SetModuleAdmins("Migration", new[] { ForeignOpsSid });

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.GroupSid, HomeOpsSid),
            new Claim(ClaimTypes.Role, "Ops"),
            new Claim(ClaimTypes.Role, "DOMAIN\\Ops"),
        }, "test"));

        Assert.False(service.IsModuleAdmin("Migration", user));
        Assert.False(service.IsModuleAdminForAny(user));
    }

    [Fact]
    public void IsModuleAdmin_LegacyNameValuedGrantAuthorizesNothing()
    {
        // Owner ruling 2026-10-19: an unmigrated grant is refused, not silently honoured.
        // Review finding sid-1 is why filtering to SIDs is required and exact comparison alone
        // is not enough - a role claim can carry a name, and IsInRole resolves names too.
        using var temp = new TempDir();
        var service = CreateService(temp.Path, out _);
        service.SetModuleAdmins("Migration", new[] { "DOMAIN\\Migration-Admins" });

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Role, "DOMAIN\\Migration-Admins"),
            new Claim(ClaimTypes.Role, "Migration-Admins"),
        }, "test"));

        Assert.False(service.IsModuleAdmin("Migration", user));
    }

    [Fact]
    public void GetUnusableGrants_ReportsOnlyTheRowsThatAuthorizeNothing()
    {
        // This list exists so the refusal above is reportable BEFORE an admin discovers it by
        // losing access to their own module.
        using var temp = new TempDir();
        var service = CreateService(temp.Path, out _);
        service.SetModuleAdmins("Migration", new[] { HomeOpsSid, "DOMAIN\\Legacy-Admins" });
        service.SetModuleAdmins("Comms10k", new[] { ForeignOpsSid });

        var unusable = service.GetUnusableGrants();

        Assert.Equal(new[] { "DOMAIN\\Legacy-Admins" }, unusable["Migration"]);
        Assert.DoesNotContain("Comms10k", unusable.Keys);
    }

    [Fact]
    public void Construction_ImportsLegacyFile_ThenArchives()
    {
        using var temp = new TempDir();
        var configDir = Path.Combine(temp.Path, "config");
        Directory.CreateDirectory(configDir);
        var legacyPath = Path.Combine(configDir, "module-admins.json");
        File.WriteAllText(legacyPath, "{\"Migration\":[\"DOMAIN\\\\Legacy-Admins\"]}");

        var service = CreateService(temp.Path, out _);

        Assert.Equal(new[] { "DOMAIN\\Legacy-Admins" }, service.GetModuleAdmins("Migration"));
        Assert.False(File.Exists(legacyPath));
        Assert.Single(Directory.GetFiles(configDir, "module-admins.json.imported-*"));
    }

    [Fact]
    public void Construction_DbValueWins_OverLegacyFile()
    {
        using var temp = new TempDir();
        var configDir = Path.Combine(temp.Path, "config");
        Directory.CreateDirectory(configDir);

        // Pre-seed the DB with a value for Migration.
        var store = TestConfigStore.Create(temp.Path);
        new ModuleAdminRepository(store).SetForModule("Migration", new[] { "DOMAIN\\Db-Admins" });

        // Legacy file has a different value for the same module.
        File.WriteAllText(Path.Combine(configDir, "module-admins.json"),
            "{\"Migration\":[\"DOMAIN\\\\File-Admins\"]}");

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(temp.Path);
        var service = new ModuleAdminService(env, new ModuleAdminRepository(store),
            Substitute.For<ILogger<ModuleAdminService>>());

        Assert.Equal(new[] { "DOMAIN\\Db-Admins" }, service.GetModuleAdmins("Migration"));
    }

    private static ModuleAdminService CreateService(string contentRoot, out ModuleAdminRepository repository)
    {
        var store = TestConfigStore.Create(contentRoot);
        repository = new ModuleAdminRepository(store);
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(contentRoot);
        return new ModuleAdminService(env, repository, Substitute.For<ILogger<ModuleAdminService>>());
    }
}
