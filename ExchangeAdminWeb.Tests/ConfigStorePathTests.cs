using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/SharedConfigDb-Plan.md AC1: the config database path comes from ConfigStore:Path when
/// set (absolute, local, must already exist) and from the content root otherwise.
/// </summary>
public class ConfigStorePathTests
{
    private const string Root = @"D:\inetpub\ExchangeAdminWebDev";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_Blank_UsesContentRootDefault(string? configured)
    {
        var result = ConfigStorePath.Resolve(configured, Root);

        Assert.Equal(Path.Combine(Root, "config", "exchangeadmin.db"), result.Path);
    }

    [Fact]
    public void Resolve_Absolute_ReturnsConfigured()
    {
        var result = ConfigStorePath.Resolve(@"D:\inetpub\ExchangeAdminWebShared\config\exchangeadmin.db", Root);

        Assert.Equal(@"D:\inetpub\ExchangeAdminWebShared\config\exchangeadmin.db", result.Path);
    }

    [Fact]
    public void Resolve_Unc_Throws_NamingTheKey()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ConfigStorePath.Resolve(@"\\server\share\exchangeadmin.db", Root));

        Assert.Contains("ConfigStore:Path", ex.Message);
    }

    [Theory]
    [InlineData(@"config\exchangeadmin.db")]
    [InlineData(@"..\shared\exchangeadmin.db")]
    [InlineData(@"\config\exchangeadmin.db")]
    public void Resolve_Relative_Throws(string configured)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigStorePath.Resolve(configured, Root));

        Assert.Contains("ConfigStore:Path", ex.Message);
    }

    [Fact]
    public void Resolve_Configured_MustExist_DefaultMustNot()
    {
        Assert.True(ConfigStorePath.Resolve(@"D:\shared\exchangeadmin.db", Root).MustExist);
        Assert.False(ConfigStorePath.Resolve(null, Root).MustExist);
    }

    [Fact]
    public void Factory_MustExist_MissingFile_Throws_AndCreatesNothing()
    {
        using var temp = new TempDir();
        var missingDir = Path.Combine(temp.Path, "never-created");
        var missingDb = Path.Combine(missingDir, "exchangeadmin.db");

        var factory = new SqliteConnectionFactory(missingDb, mustExist: true);

        Assert.False(Directory.Exists(missingDir), "the factory constructor must not create the directory");

        var ex = Assert.Throws<FileNotFoundException>(() => factory.Open());

        Assert.Contains(missingDb, ex.Message);
        Assert.Contains("ConfigStore:Path", ex.Message);
        Assert.False(File.Exists(missingDb), "a missing shared database must never be created");
        Assert.False(Directory.Exists(missingDir), "Open must not create the directory either");
    }

    [Fact]
    public void Factory_MustExist_ExistingFile_Opens()
    {
        using var temp = new TempDir();
        new ConfigStoreMigrator(new SqliteConnectionFactory(temp.DbPath)).Migrate();

        using var connection = new SqliteConnectionFactory(temp.DbPath, mustExist: true).Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";

        Assert.Equal(ConfigStoreMigrator.TargetVersion, Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public void Factory_Default_CreatesTheFile()
    {
        using var temp = new TempDir();
        var dbPath = Path.Combine(temp.Path, "fresh", "exchangeadmin.db");

        using (new SqliteConnectionFactory(dbPath).Open())
        {
        }

        Assert.True(File.Exists(dbPath));
    }
}
