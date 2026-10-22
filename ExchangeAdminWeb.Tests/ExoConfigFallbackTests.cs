using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using ExchangeOnlineConfigPage = ExchangeAdminWeb.Components.Pages.ExchangeOnlineConfig;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a26s: the Exchange Online Config page re-derived the runtime's fallback rule by hand - a key with no row takes its
/// appsettings value, and the certificate the built-in default when appsettings has none - to caption the fields a fallback
/// supplies, and no test asserted that the page and the pool agree. <see cref="ExoConfigFallback"/> owns the rule now: the
/// page calls it, and these cases hold it to what the pool connects under.
/// </summary>
/// <remarks>
/// The pool is not changed by this finding (another agent owns the file; the record names the follow-up), so it still applies
/// its own copy of the rule, and the first theory is what keeps that copy and the function in step. The pool is observed
/// through <see cref="ExoConnectionPool.BorrowAsync(CancellationToken)"/> and <see cref="ExoConnectionPool.IsConfigured"/>, its
/// connect step replaced through its own test seam: an unopened runspace over an empty session state and no connection id, so
/// nothing is opened, invoked or connected. The page is the compiled page through <see cref="SettingsPageHarness"/>.
/// </remarks>
public sealed class ExoConfigFallbackTests : IDisposable
{
    private const string AppIdKey = ExoConnectionPool.ConfigAppIdKey;
    private const string OrganizationKey = ExoConnectionPool.ConfigOrganizationKey;
    private const string CertificateKey = ExoConnectionPool.ConfigCertSubjectKey;

    private readonly SettingsPageHarness _h = new();

    public void Dispose() => _h.Dispose();

    /// <summary>Every source a key's value can come from, for every key.</summary>
    public static TheoryData<string, string> Cases()
    {
        var cases = new TheoryData<string, string>();
        foreach (var key in new[] { AppIdKey, OrganizationKey, CertificateKey })
        {
            foreach (var source in new[] { "stored", "stored-empty", "appsettings", "appsettings-blank", "nothing" })
                cases.Add(key, source);
        }

        return cases;
    }

    // A key's stored and configured values differ from each other and from the built-in default, so the value shows which
    // source the rule took.
    private static string StoredValue(string key) => key switch
    {
        AppIdKey => "11111111-1111-1111-1111-111111111111",
        OrganizationKey => "contoso.onmicrosoft.com",
        _ => "CN=EXO-Stored",
    };

    private static string ConfiguredValue(string key) => key switch
    {
        AppIdKey => "22222222-2222-2222-2222-222222222222",
        OrganizationKey => "fabrikam.onmicrosoft.com",
        _ => "CN=EXO-Configured",
    };

    private static string Setting(string key) => key switch
    {
        AppIdKey => ExoConfigFallback.AppIdSetting,
        OrganizationKey => ExoConfigFallback.OrganizationSetting,
        _ => ExoConfigFallback.CertificateSubjectSetting,
    };

    private static string Field(string key) => key switch
    {
        AppIdKey => "exoAppId",
        OrganizationKey => "exoOrganization",
        _ => "exoCertSubject",
    };

    /// <summary>
    /// Stores the other two keys, so only <paramref name="key"/> can fall back, and gives it the case's row and appsettings
    /// entry. Returns what the store holds for it - null when it has no row.
    /// </summary>
    private string? Arrange(string key, string source)
    {
        var rows = new Dictionary<string, string>();
        foreach (var other in new[] { AppIdKey, OrganizationKey, CertificateKey }.Where(k => k != key))
            rows[other] = StoredValue(other);

        string? stored = source switch
        {
            "stored" => StoredValue(key),
            "stored-empty" => "",
            _ => null,
        };
        if (stored is not null)
            rows[key] = stored;

        new ModuleConfigRepository(TestConfigStore.Create(_h.Root)).SaveModule(ExoConnectionPool.ConfigModuleKey, rows);

        // A stored row - even an empty one - comes with an appsettings value beside it, so a fallback taken over it would show.
        if (source is "stored" or "stored-empty" or "appsettings")
            _h.Config[Setting(key)] = ConfiguredValue(key);
        else if (source == "appsettings-blank")
            _h.Config[Setting(key)] = "";

        return stored;
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheRule_IsWhatThePoolConnectsUnder(string key, string source)
    {
        var stored = Arrange(key, source);
        var expected = ExoConfigFallback.Resolve(_h.Config, key, stored).Value;
        _h.Enablement.SaveEnablement(new Dictionary<string, bool> { [ExoConnectionPool.ConfigModuleKey] = true });
        ExoConnectionConfig? connectedUnder = null;
        using var pool = new ExoConnectionPool(_h.Config, _h.ModuleConfig, _h.Enablement, NullLogger<ExoConnectionPool>.Instance, _h.Trace,
            connect: (config, generation, _) =>
            {
                connectedUnder = config;
                var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.Create());
                var shell = PowerShell.Create();
                shell.Runspace = runspace;
                return new PooledRunspace(runspace, shell, generation);
            });

        if (key != CertificateKey && string.IsNullOrWhiteSpace(expected))
        {
            // A blank app id or organization is the pool's "not configured": it refuses before it would connect.
            Assert.False(pool.IsConfigured);
            await Assert.ThrowsAsync<InvalidOperationException>(() => pool.BorrowAsync(TestContext.Current.CancellationToken));
            Assert.Null(connectedUnder);
            return;
        }

        Assert.True(pool.IsConfigured);
        pool.Return(await pool.BorrowAsync(TestContext.Current.CancellationToken));
        var connected = Assert.NotNull(connectedUnder);
        Assert.Equal(expected, key switch
        {
            AppIdKey => connected.AppId,
            OrganizationKey => connected.Organization,
            _ => connected.CertificateSubject,
        });
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ThePage_ShowsTheRulesValue_WithTheRulesCaption(string key, string source)
    {
        var stored = Arrange(key, source);
        var expected = ExoConfigFallback.Resolve(_h.Config, key, stored);

        var page = _h.OpenExchangeOnlineConfig();

        Assert.Equal(expected.Value, SettingsPageHarness.Field<string>(page, typeof(ExchangeOnlineConfigPage), Field(key)));
        var text = SettingsPageHarness.RenderedText(page);
        if (ExoConfigFallback.Caption(expected) is { } caption)
        {
            Assert.Contains(caption, text, StringComparison.Ordinal);
            Assert.Equal(1, Count(text, "Not set in the database"));
        }
        else
        {
            Assert.DoesNotContain("Not set in the database", text, StringComparison.Ordinal);
        }
    }
}
