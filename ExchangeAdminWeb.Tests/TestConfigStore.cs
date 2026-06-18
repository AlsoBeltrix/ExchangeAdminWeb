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
        var dbPath = Path.Combine(dir, "exchangeadmin.db");
        var factory = new SqliteConnectionFactory(dbPath);
        new ConfigStoreMigrator(factory).Migrate();
        return new SqliteConfigStore(factory);
    }

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
