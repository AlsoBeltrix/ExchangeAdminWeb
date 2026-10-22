using System.Text.RegularExpressions;
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

    // scdi-2: rooted is not fully qualified. "D:exchangeadmin.db" names a drive but resolves
    // against that drive's current directory, so two processes could open two different files.
    [Theory]
    [InlineData(@"D:exchangeadmin.db")]
    [InlineData(@"C:config\exchangeadmin.db")]
    public void Resolve_DriveRelative_Throws(string configured)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigStorePath.Resolve(configured, Root));

        Assert.Contains("ConfigStore:Path", ex.Message);
        Assert.Contains("absolute local file path", ex.Message);
    }

    // cdt-35: a network path was refused only when it began \\ or //, so \/server/share,
    // /\server\share and \??\UNC\server\share - each a share to Windows - and \??\D:\... all
    // started the app. The one accepted form is the one the ops scripts read: an ASCII drive
    // letter, a colon, then \ or /.
    [Theory]
    [InlineData(@"\/server/share/exchangeadmin.db", "is a network (UNC) path")]
    [InlineData(@"/\server\share\exchangeadmin.db", "is a network (UNC) path")]
    [InlineData(@"\??\D:\shared\exchangeadmin.db", "must be an absolute local file path")]
    [InlineData(@"\??\UNC\server\share\exchangeadmin.db", "must be an absolute local file path")]
    [InlineData(@"\\server\share\exchangeadmin.db", "is a network (UNC) path")]
    [InlineData("//server/share/exchangeadmin.db", "is a network (UNC) path")]
    [InlineData("D:foo", "must be an absolute local file path")]
    [InlineData(@"foo\bar", "must be an absolute local file path")]
    [InlineData(@"\\?\D:\x", "is a network (UNC) path")]
    [InlineData(@"\\?\UNC\server\share\exchangeadmin.db", "is a network (UNC) path")]
    [InlineData(@"\\.\D:\exchangeadmin.db", "is a network (UNC) path")]
    [InlineData("/foo/bar", "must be an absolute local file path")]
    [InlineData("D:", "must be an absolute local file path")]
    [InlineData("\u212A:\\exchangeadmin.db", "must be an absolute local file path")]
    public void Resolve_RefusesEveryFormButADriveLetterPath(string configured, string says)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ConfigStorePath.Resolve(configured, Root));

        Assert.Contains("ConfigStore:Path", ex.Message);
        Assert.Contains(says, ex.Message);
        Assert.Contains($"'{configured}'", ex.Message);
    }

    [Theory]
    [InlineData(@"D:\x\y.db")]
    [InlineData("D:/x/y.db")]
    [InlineData(@"D:\x/y.db")]
    [InlineData("  D:/x/y.db  ")]
    public void Resolve_DriveLetterPath_WithEitherSeparator_ReturnsOneSpelling(string configured)
    {
        var result = ConfigStorePath.Resolve(configured, Root);

        Assert.Equal(@"D:\x\y.db", result.Path);
        Assert.True(result.MustExist);
    }

    // cdt-35: tools/SqliteConfigBackup.psm1 holds the ops scripts' copy of this rule, applied to the
    // ConfigStore:Path every deploy, promotion, cutover and installer run reads. These are its lines,
    // verbatim. ModuleDecision applies the two patterns as PowerShell does, so a change to either
    // copy fails here or in tests/ps/SqliteConfigBackup.Tests.ps1, which pins Resolve's lines.
    private const string ModuleTrim = "$value = $value.Trim()";
    private const string ModuleNetworkTest = @"$Value -match '^[\\/][\\/]'";
    private const string ModuleDriveTest = @"$Value -cnotmatch '^[A-Za-z]:[\\/]'";
    private const string ModuleNormalisation = @"return $value.Replace('/', '\')";
    private const string ModuleTripwire = "TRIPWIRE: this mirrors Services/Storage/ConfigStorePath.cs (Resolve)";

    [Fact]
    public void ModuleRule_IsPinnedInItsSource_AndNamesThisMethod()
    {
        var module = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("tools", "SqliteConfigBackup.psm1"));

        foreach (var line in new[] { ModuleTrim, ModuleNetworkTest, ModuleDriveTest, ModuleNormalisation, ModuleTripwire })
        {
            Assert.True(module.Contains(line, StringComparison.Ordinal),
                $"tools/SqliteConfigBackup.psm1 no longer contains `{line}`. It is the ops scripts' copy of " +
                "ConfigStorePath.Resolve's rule: change Resolve, ModuleDecision here and the module together.");
        }
    }

    // The module's call on one value: its trim, its two pinned patterns (-match is case-insensitive,
    // -cnotmatch case-sensitive), then its separator normalisation. Path is null when it refuses.
    private static (string? Path, string? Refusal) ModuleDecision(string configured)
    {
        var value = configured.Trim();
        if (Regex.IsMatch(value, ModuleNetworkTest.Split('\'')[1], RegexOptions.IgnoreCase))
            return (null, "is a network (UNC) path");
        if (!Regex.IsMatch(value, ModuleDriveTest.Split('\'')[1]))
            return (null, "must be an absolute local file path");
        return (value.Replace('/', '\\'), null);
    }

    // The same values tests/ps/SqliteConfigBackup.Tests.ps1 holds the module to (cdt-17, cdt-30).
    public static TheoryData<string> AgreementValues => new()
    {
        @"D:\shared\config\exchangeadmin.db", @"c:\x\y.db", "D:/shared/x.db", @"D:\shared/x.db", " D:/x.db ", "z:/a",
        @"config\exchangeadmin.db", "D:exchangeadmin.db", "D:", @"\config\exchangeadmin.db", "/config/exchangeadmin.db",
        @"\\server\share\x.db", "//server/share/x.db", @"\\?\D:\x\y.db", @"\\.\D:\x.db", @"1:\x.db", @"DD:\x.db", @":\x.db", "x",
        @"\/server/share/x.db", @"/\server\share\x.db", @"\??\D:\x.db", @"\??\UNC\server\share\x.db", "\u212A:\\x.db",
    };

    [Theory]
    [MemberData(nameof(AgreementValues))]
    public void Resolve_MakesTheModulesCall_OnEveryValue(string configured)
    {
        var (path, refusal) = ModuleDecision(configured);

        if (refusal is null)
        {
            Assert.Equal(path, ConfigStorePath.Resolve(configured, Root).Path);
        }
        else
        {
            var ex = Assert.Throws<InvalidOperationException>(() => ConfigStorePath.Resolve(configured, Root));
            Assert.Contains(refusal, ex.Message);
        }
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
