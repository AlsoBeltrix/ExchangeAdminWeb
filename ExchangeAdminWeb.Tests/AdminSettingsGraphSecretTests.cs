using System.Reflection;
using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using AdminSettingsPage = ExchangeAdminWeb.Components.Pages.AdminSettings;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/ProtectedPrincipalIdentifiers-Plan.md S3 and the owner's ruling of 2026-11-03: the Protected Principals pane's
/// "Graph Directory Read Credential (Delinea Secret ID)" field - its own setting beside the Directory Read Credential,
/// stored in the same module, counted as one unsaved change, saved only when changed, under its own record, and refused as
/// the field beside it is. The compiled page through <see cref="SettingsPageHarness"/>; no directory or Graph.
/// </summary>
public sealed class AdminSettingsGraphSecretTests : IDisposable
{
    private const string GraphSection = "ProtectedPrincipals:GraphDirectoryReadSecretId";
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly SettingsPageHarness _h = new();

    public void Dispose() => _h.Dispose();

    private void SeedRules() =>
        new ProtectedPrincipalRepository(TestConfigStore.Create(_h.Root)).Save(new ProtectedPrincipalData(["cfo@contoso.com"], [], [], [], []));

    private void Seed(Dictionary<string, string> values) =>
        new ModuleConfigRepository(TestConfigStore.Create(_h.Root)).SaveModule(ProtectedPrincipalService.ProtectedPrincipalsModuleKey, values);

    private Dictionary<string, string> Stored()
    {
        Assert.True(new ModuleConfigRepository(TestConfigStore.Create(_h.Root))
            .TryReadModule(ProtectedPrincipalService.ProtectedPrincipalsModuleKey, out var values), "the module is unreadable");
        return values;
    }

    private static int ProtectedChanges(AdminSettingsPage page) =>
        SettingsPageHarness.Field<AdminPageDirtyState>(page, typeof(AdminSettingsPage), "dirty").SectionCount("Protected Principals");

    private Task Type(AdminSettingsPage page, string value) =>
        _h.OnDispatcherAsync(() => typeof(AdminSettingsPage).GetMethod("SetPpGraphReadSecretId", PrivateInstance)!.Invoke(page, [value]));

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    private static string[] Names(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(v => v.GetString() ?? "").ToArray()
            : [];

    [Fact]
    public async Task TheField_RoundTripsThroughTheStore_CountsAsOneChange_AndLeavesTheDirectoryReadCredentialAlone()
    {
        SeedRules();
        Seed(new() { [ProtectedPrincipalService.DirectoryReadSecretConfigKey] = "777" });
        var page = _h.OpenAdminSettings();
        Assert.Equal("", SettingsPageHarness.Field<string>(page, typeof(AdminSettingsPage), "ppGraphReadSecretId"));
        Assert.Equal(0, ProtectedChanges(page));

        await Type(page, "4242");
        Assert.Equal(1, ProtectedChanges(page));
        await Type(page, " 4242 ");
        Assert.Equal(1, ProtectedChanges(page));
        await Type(page, "");
        Assert.Equal(0, ProtectedChanges(page));
        await Type(page, "4242");

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.False(SettingsPageHarness.Field<bool>(page, typeof(AdminSettingsPage), "statusIsError"),
            SettingsPageHarness.Field<string?>(page, typeof(AdminSettingsPage), "statusMessage"));
        var stored = Stored();
        Assert.Equal("4242", stored[ProtectedPrincipalService.GraphDirectoryReadSecretConfigKey]);
        Assert.Equal("777", stored[ProtectedPrincipalService.DirectoryReadSecretConfigKey]);
        Assert.Equal(0, ProtectedChanges(page));
        var record = Assert.Single(_h.AuditEvents(), r => Text(r, "section") == GraphSection);
        Assert.Equal(["4242"], Names(record, "added"));

        // The service reads it back, and a fresh page shows it.
        Assert.True(_h.ProtectedPrincipals.TryGetGraphDirectoryReadSecret(out var read));
        Assert.Equal("4242", read);
        Assert.Equal("4242", SettingsPageHarness.Field<string>(_h.OpenAdminSettings(), typeof(AdminSettingsPage), "ppGraphReadSecretId"));
    }

    [Fact]
    public async Task AValueThatIsNotASecretId_IsRefused_AndNothingIsWritten()
    {
        SeedRules();
        var page = _h.OpenAdminSettings();
        await Type(page, "S3cret-pasted-here");

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.True(SettingsPageHarness.Field<bool>(page, typeof(AdminSettingsPage), "statusIsError"));
        Assert.DoesNotContain("S3cret", SettingsPageHarness.Field<string?>(page, typeof(AdminSettingsPage), "statusMessage"));
        Assert.False(Stored().ContainsKey(ProtectedPrincipalService.GraphDirectoryReadSecretConfigKey));
    }

    [Fact]
    public async Task AValueChangedElsewhere_RefusesTheWholeSave()
    {
        SeedRules();
        Seed(new() { [ProtectedPrincipalService.GraphDirectoryReadSecretConfigKey] = "777" });
        var page = _h.OpenAdminSettings();
        _h.CreateOtherModuleConfig().UpdateModuleValues(ProtectedPrincipalService.ProtectedPrincipalsModuleKey,
            new Dictionary<string, string> { [ProtectedPrincipalService.GraphDirectoryReadSecretConfigKey] = "888" });
        await Type(page, "4242");

        await _h.RunAsync(page, "SaveProtectedPrincipals");

        Assert.Equal("888", Stored()[ProtectedPrincipalService.GraphDirectoryReadSecretConfigKey]);
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("ConcurrentChange", Text(record, "refusedAt"));
    }

    [Fact]
    public void TheField_IsBesideTheDirectoryReadCredential_WithHelpSayingWhatItNeedsAndWhatEmptyDoes()
    {
        var page = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "AdminSettings.razor"));

        var directory = page.IndexOf(">Directory Read Credential (Delinea Secret ID)</label>", StringComparison.Ordinal);
        var graph = page.IndexOf(">Graph Directory Read Credential (Delinea Secret ID)</label>", StringComparison.Ordinal);
        Assert.True(directory > 0 && graph > directory, "the Graph field is not beside the Directory Read Credential");
        Assert.Contains("SetPpGraphReadSecretId(e.Value?.ToString()", page, StringComparison.Ordinal);
        Assert.DoesNotContain("@bind=\"ppGraphReadSecretId\"", page, StringComparison.Ordinal);

        var help = (string)typeof(AdminSettingsPage).GetField("GraphSecretHelpText", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;
        Assert.Contains("User.Read.All", help, StringComparison.Ordinal);
        Assert.Contains("While it is empty", help, StringComparison.Ordinal);
        Assert.Contains("refused", help, StringComparison.Ordinal);
    }
}
