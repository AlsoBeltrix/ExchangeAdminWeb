using System.Reflection;
using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using AdminSettingsPage = ExchangeAdminWeb.Components.Pages.AdminSettings;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-72 on Admin Settings: the enablement save took its toggles after its yield, and the protected-principal and
/// log-level saves took the directory-read secret ID and the level after the yield and the authorization re-check -
/// aud-a41's "taken before anything waits" was true of the store write, not of those awaits. None of those controls is
/// disabled while a save runs, so a click, a pick or a keystroke landing there was saved with the submission. Each is
/// now read above the yield.
/// </summary>
/// <remarks>
/// The compiled page through <see cref="SettingsPageHarness"/>, its handlers on the harness renderer's dispatcher, the
/// change applied in the same work item that starts the save once the save has yielded - the save's continuation is
/// queued behind it, so the change lands in the yield. Nothing reaches a directory.
/// </remarks>
public sealed class AdminSettingsSubmittedSaveTests : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Saved = "MailboxPermissions";
    private const string SwitchedInTheYield = "CalendarPermissions";

    private readonly SettingsPageHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task ATogglePressedInTheEnablementSavesYield_IsNotSaved_AndStaysUnsaved()
    {
        new ModuleEnablementRepository(TestConfigStore.Create(_h.Root))
            .SaveAll(new Dictionary<string, bool> { [Saved] = true, [SwitchedInTheYield] = true, ["ExchangeOnline"] = true });
        var page = _h.OpenAdminSettings();
        await _h.OnDispatcherAsync(() => Invoke(page, "ToggleModule", Saved, false));

        var save = await StartWithChangeInTheYield(page, "SaveEnablement",
            () => Invoke(page, "ToggleModule", SwitchedInTheYield, false));
        await save;

        Assert.True(new ModuleEnablementRepository(TestConfigStore.Create(_h.Root)).TryGetAll(out var stored));
        Assert.False(stored[Saved]);
        Assert.True(stored[SwitchedInTheYield]);
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal($"Module:{Saved}", Text(record, "section"));
        // Unsaved, and still shown as unsaved: the baseline took only what the save wrote (aud-a41).
        Assert.True(SettingsPageHarness.Field<Dictionary<string, bool>>(page, typeof(AdminSettingsPage), "moduleEnablementBaseline")[SwitchedInTheYield]);
        Assert.False(SettingsPageHarness.Field<Dictionary<string, bool>>(page, typeof(AdminSettingsPage), "moduleEnablement")[SwitchedInTheYield]);
    }

    [Fact]
    public async Task APickLandingInTheLogLevelSavesYield_IsNotTheLevelWritten()
    {
        var page = _h.OpenAdminSettings();
        SetField(page, "selectedLogLevel", "Warning");

        var save = await StartWithChangeInTheYield(page, "SaveLogLevel", () => SetField(page, "selectedLogLevel", "Debug"));
        await save;

        Assert.Equal("Warning", _h.StoredLogLevel());
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("ExtendedLogLevel", Text(record, "section"));
        Assert.Equal(["Warning"], Names(record, "added"));
    }

    [Fact]
    public async Task AKeystrokeLandingInThePrincipalsSavesYield_IsNotTheSecretIdWritten()
    {
        new ProtectedPrincipalRepository(TestConfigStore.Create(_h.Root))
            .Save(new ProtectedPrincipalData(["cfo@contoso.com"], [], [], [], []));
        new ModuleConfigRepository(TestConfigStore.Create(_h.Root)).SaveModule(
            ProtectedPrincipalService.ProtectedPrincipalsModuleKey,
            new Dictionary<string, string> { [ProtectedPrincipalService.DirectoryReadSecretConfigKey] = "777" });
        var page = _h.OpenAdminSettings();
        await _h.OnDispatcherAsync(() => Invoke(page, "SetPpDirectoryReadSecretId", "4242"));

        var save = await StartWithChangeInTheYield(page, "SaveProtectedPrincipals",
            () => Invoke(page, "SetPpDirectoryReadSecretId", "4242999"));
        await save;

        Assert.True(new ModuleConfigRepository(TestConfigStore.Create(_h.Root))
            .TryReadModule(ProtectedPrincipalService.ProtectedPrincipalsModuleKey, out var values));
        Assert.Equal("4242", values.GetValueOrDefault(ProtectedPrincipalService.DirectoryReadSecretConfigKey));
        Assert.False(SettingsPageHarness.Field<bool>(page, typeof(AdminSettingsPage), "statusIsError"));
    }

    /// <summary>
    /// Starts <paramref name="method"/> on the dispatcher and applies <paramref name="change"/> in the same work item,
    /// once it has yielded. Returns the save, still running.
    /// </summary>
    private async Task<Task> StartWithChangeInTheYield(AdminSettingsPage page, string method, Action change)
    {
        var running = Task.CompletedTask;
        await _h.OnDispatcherAsync(() =>
        {
            running = (Task)Invoke(page, method)!;
            Assert.False(running.IsCompleted, $"{method} finished without yielding, so nothing could change under it.");
            change();
        });
        return running;
    }

    private static object? Invoke(AdminSettingsPage page, string method, params object?[] arguments) =>
        (typeof(AdminSettingsPage).GetMethod(method, PrivateInstance)
            ?? throw new InvalidOperationException($"{method} not found")).Invoke(page, arguments.Length == 0 ? null : arguments);

    private static void SetField(AdminSettingsPage page, string field, object? value) =>
        SettingsPageHarness.SetField(page, typeof(AdminSettingsPage), field, value);

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    private static string[] Names(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(v => v.GetString() ?? "").ToArray()
            : [];
}
