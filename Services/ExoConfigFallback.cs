namespace ExchangeAdminWeb.Services;

public static class ExoConfigFallback
{
    public const string AppIdSetting = "ExchangeOnline:AppId";
    public const string OrganizationSetting = "ExchangeOnline:Organization";
    public const string CertificateSubjectSetting = "ExchangeOnline:CertificateSubject";
    public const string BuiltInCertificateSubject = "CN=EXO-Automation";

    public static ExoConfigValue Resolve(IConfiguration config, string key, string? stored)
    {
        ArgumentNullException.ThrowIfNull(config);

        var setting = Setting(key);
        if (stored is not null)
            return new ExoConfigValue(stored, ExoConfigSource.Stored, setting);

        var configured = config[setting];
        if (configured is not null)
            return new ExoConfigValue(configured, ExoConfigSource.AppSettings, setting);

        return key == ExoConnectionPool.ConfigCertSubjectKey
            ? new ExoConfigValue(BuiltInCertificateSubject, ExoConfigSource.BuiltInDefault, setting)
            : new ExoConfigValue("", ExoConfigSource.None, setting);
    }

    public static string? Caption(ExoConfigValue value) => value.Source switch
    {
        ExoConfigSource.AppSettings when !string.IsNullOrWhiteSpace(value.Value) =>
            $"Not set in the database; the configured fallback applies: {value.Value} ({value.Setting} in appsettings.json).",
        ExoConfigSource.BuiltInDefault => $"Not set in the database; the built-in default applies: {value.Value}.",
        _ => null,
    };

    private static string Setting(string key) => key switch
    {
        ExoConnectionPool.ConfigAppIdKey => AppIdSetting,
        ExoConnectionPool.ConfigOrganizationKey => OrganizationSetting,
        ExoConnectionPool.ConfigCertSubjectKey => CertificateSubjectSetting,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Not an Exchange Online connection key."),
    };
}

public enum ExoConfigSource
{
    Stored,
    AppSettings,
    BuiltInDefault,
    None,
}

public readonly record struct ExoConfigValue(string Value, ExoConfigSource Source, string Setting);
