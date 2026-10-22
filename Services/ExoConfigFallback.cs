namespace ExchangeAdminWeb.Services;

/// <summary>
/// The Exchange Online connection's fallback rule, in one place (aud-a26s): the value a connection key takes, and where it
/// comes from, given what the module-config store holds for it.
/// </summary>
/// <remarks>
/// <para>A stored value is used as it is - an empty one included, which does not fall back. A key with no row takes its
/// appsettings value (<c>ExchangeOnline:AppId</c>, <c>:Organization</c>, <c>:CertificateSubject</c>) when there is one, a blank
/// one included; the certificate with no appsettings entry takes the built-in <c>CN=EXO-Automation</c>; anything else is
/// empty. That is the rule ExoConnectionPool.GetExoConfig applies.</para>
/// <para>The Exchange Online Config page used to restate it by hand to caption the fields a fallback supplies, and nothing
/// kept the two in step. The page calls this now, and ExoConfigFallbackTests holds it to what the pool connects under for
/// every key and source. The pool is still to call it too (left for a follow-up in the aud-a26s record): until then it keeps
/// its own copy, and that test is what keeps the two together.</para>
/// </remarks>
public static class ExoConfigFallback
{
    // Each named once, for the value the rule reads and the caption that names it.
    public const string AppIdSetting = "ExchangeOnline:AppId";
    public const string OrganizationSetting = "ExchangeOnline:Organization";
    public const string CertificateSubjectSetting = "ExchangeOnline:CertificateSubject";
    public const string BuiltInCertificateSubject = "CN=EXO-Automation";

    /// <summary>
    /// The value <paramref name="key"/> takes, given what the store holds for it (<paramref name="stored"/>: null when it has
    /// no row), and its source. Throws for a key that is not one of the three connection keys.
    /// </summary>
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

    /// <summary>
    /// The caption under a field whose value a fallback supplies (aud-a26p's wording); null when the value is stored or
    /// nothing supplies it. A blank appsettings value supplies nothing: the field is blank, and so is the runtime's value.
    /// </summary>
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

/// <summary>Where an Exchange Online connection value came from.</summary>
public enum ExoConfigSource
{
    /// <summary>The store's row, an empty one included.</summary>
    Stored,

    /// <summary>The appsettings value, a blank one included.</summary>
    AppSettings,

    /// <summary>The certificate's built-in default.</summary>
    BuiltInDefault,

    /// <summary>Nothing: the value is empty.</summary>
    None,
}

/// <summary>A connection value, where it came from, and the appsettings setting that supplies it when the store has no row.</summary>
public readonly record struct ExoConfigValue(string Value, ExoConfigSource Source, string Setting);
