using System.Reflection;
using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using ExchangeOnlineConfigPage = ExchangeAdminWeb.Components.Pages.ExchangeOnlineConfig;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-72 on Exchange Online Config: both saves read what they write - the module toggle, the three connection fields -
/// after their yield and the authorization re-check. The toggle reaches the browser's disabled state a round trip late
/// and the connection fields are never disabled, so a click or a keystroke landing there was saved instead of what was
/// on screen at the click. Both now read it above the yield.
/// </summary>
/// <remarks>
/// The compiled page through <see cref="SettingsPageHarness"/>, its handlers on the harness renderer's dispatcher. The
/// change is applied in the same dispatcher work item that starts the save, once the save has yielded: the save's
/// continuation is queued behind that work item, so the change lands in the yield, as a queued change event would.
/// </remarks>
public sealed class ExchangeOnlineConfigSubmittedSaveTests : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string AppId = "6f9619ff-8b86-d011-b42d-00c04fc964ff";

    private readonly SettingsPageHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task AToggleClickLandingInTheStatusSavesYield_SavesTheToggleSubmitted()
    {
        new ModuleEnablementRepository(TestConfigStore.Create(_h.Root))
            .SaveAll(new Dictionary<string, bool> { ["ExchangeOnline"] = true });
        var page = _h.OpenExchangeOnlineConfig();
        SetField(page, "moduleEnabled", false);

        var save = await StartWithChangeInTheYield(page, "SaveModuleStatus", () => SetField(page, "moduleEnabled", true));
        await save;

        Assert.True(new ModuleEnablementRepository(TestConfigStore.Create(_h.Root)).TryGetAll(out var stored));
        Assert.False(stored["ExchangeOnline"]);
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Module:ExchangeOnline", Text(record, "section"));
        Assert.Equal("Success", Text(record, "result"));
        Assert.False(SettingsPageHarness.Field<bool>(page, typeof(ExchangeOnlineConfigPage), "moduleEnabledBaseline"));
    }

    [Fact]
    public async Task AKeystrokeLandingInTheConnectionSavesYield_SavesTheFieldsSubmitted()
    {
        new ModuleConfigRepository(TestConfigStore.Create(_h.Root)).SaveModule(ExoConnectionPool.ConfigModuleKey,
            new Dictionary<string, string>
            {
                [ExoConnectionPool.ConfigAppIdKey] = AppId,
                [ExoConnectionPool.ConfigOrganizationKey] = "contoso.onmicrosoft.com",
                [ExoConnectionPool.ConfigCertSubjectKey] = "CN=EXO-Automation",
            });
        var page = _h.OpenExchangeOnlineConfig();
        SetField(page, "exoOrganization", "fabrikam.onmicrosoft.com");

        var save = await StartWithChangeInTheYield(page, "SaveExoConfig",
            () => SetField(page, "exoOrganization", "northwind.onmicrosoft.com"));
        await save;

        Assert.True(new ModuleConfigRepository(TestConfigStore.Create(_h.Root))
            .TryReadModule(ExoConnectionPool.ConfigModuleKey, out var stored));
        Assert.Equal("fabrikam.onmicrosoft.com", stored[ExoConnectionPool.ConfigOrganizationKey]);
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Success", Text(record, "result"));
        Assert.Equal(["Org=fabrikam.onmicrosoft.com"], Names(record, "added"));
    }

    /// <summary>
    /// Starts <paramref name="method"/> on the dispatcher and applies <paramref name="change"/> in the same work item,
    /// once it has yielded. Returns the save, still running.
    /// </summary>
    private async Task<Task> StartWithChangeInTheYield(ExchangeOnlineConfigPage page, string method, Action change)
    {
        var info = typeof(ExchangeOnlineConfigPage).GetMethod(method, PrivateInstance)
            ?? throw new InvalidOperationException($"{method} not found");
        var running = Task.CompletedTask;
        await _h.OnDispatcherAsync(() =>
        {
            running = (Task)info.Invoke(page, null)!;
            Assert.False(running.IsCompleted, $"{method} finished without yielding, so nothing could change under it.");
            change();
        });
        return running;
    }

    private static void SetField(ExchangeOnlineConfigPage page, string field, object? value) =>
        SettingsPageHarness.SetField(page, typeof(ExchangeOnlineConfigPage), field, value);

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    private static string[] Names(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(v => v.GetString() ?? "").ToArray()
            : [];
}
