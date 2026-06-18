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

    [Fact]
    public void IsModuleAdmin_MatchesRoleClaim()
    {
        using var temp = new TempDir();
        var service = CreateService(temp.Path, out _);
        service.SetModuleAdmins("Migration", new[] { "DOMAIN\\Migration-Admins" });

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Role, "Migration-Admins"),
        }, "test"));

        Assert.True(service.IsModuleAdmin("Migration", user));
        Assert.False(service.IsModuleAdmin("Migration",
            new ClaimsPrincipal(new ClaimsIdentity(Array.Empty<Claim>(), "test"))));
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
