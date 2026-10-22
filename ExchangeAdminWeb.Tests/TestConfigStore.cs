using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Builds a real SQLite-backed <see cref="IConfigStore"/> and repositories over a throwaway
/// database file, for tests of services that now depend on the config store. Using the real
/// store (not a mock) keeps the parity tests honest about SQL/transaction behavior.
/// </summary>
internal static class TestConfigStore
{
    /// <summary>Creates a migrated config store over a fresh DB file under <paramref name="dir"/>.</summary>
    public static SqliteConfigStore Create(string dir)
    {
        if (IsTheTempDirectoryItself(dir))
            throw new InvalidOperationException(
                $"'{dir}' is the temp directory itself: a config database there is one file every test and every run shares. Give the test a directory of its own under it.");

        var dbPath = Path.Combine(dir, "exchangeadmin.db");
        var factory = new SqliteConnectionFactory(dbPath);
        new ConfigStoreMigrator(factory).Migrate();
        return new SqliteConfigStore(factory);
    }

    private static bool IsTheTempDirectoryItself(string dir) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Creates an <see cref="AppSettingRepository"/> over a fresh DB under <paramref name="dir"/>.</summary>
    public static AppSettingRepository CreateAppSettings(string dir) => new(Create(dir));

    /// <summary>Creates a <see cref="ModuleConfigRepository"/> over a fresh DB under <paramref name="dir"/>.</summary>
    public static ModuleConfigRepository CreateModuleConfig(string dir) => new(Create(dir));

    /// <summary>Creates a <see cref="ModuleEnablementRepository"/> over a fresh DB under <paramref name="dir"/>.</summary>
    public static ModuleEnablementRepository CreateModuleEnablement(string dir) => new(Create(dir));

    /// <summary>Creates a <see cref="ProtectedPrincipalRepository"/> over a fresh DB under <paramref name="dir"/>.</summary>
    public static ProtectedPrincipalRepository CreateProtectedPrincipal(string dir) => new(Create(dir));

    /// <summary>Creates an <see cref="AttributeEditorRepository"/> over a fresh DB under <paramref name="dir"/>.</summary>
    public static AttributeEditorRepository CreateAttributeEditor(string dir) => new(Create(dir));
}
