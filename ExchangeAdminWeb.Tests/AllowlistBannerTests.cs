using System.Text.Json;
using ExchangeAdminWeb.Services.Storage;
using ModuleConfigPage = ExchangeAdminWeb.Components.Pages.ModuleConfig;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a26f: the Module Configuration page's corrupt-allowlist banner named a JSON file the
/// allowlist no longer lives in, and when the legacy import refused that file it showed nothing of
/// why - the attribute was named only in the application log.
/// </summary>
/// <remarks>
/// The page is the compiled ModuleConfig, loaded through its own OnParametersSetAsync over real
/// stores by <see cref="SettingsPageHarness"/> and rendered to its frame list - the text and markup it
/// emits, child components excluded. No renderer or browser is involved. This finding is about the
/// text, so the text is what is asserted: which store and fix it names, and which it does not.
/// </remarks>
public sealed class AllowlistBannerTests : IDisposable
{
    private readonly SettingsPageHarness _h = new();

    public void Dispose() => _h.Dispose();

    private string LoadedBanner(out bool corrupt)
    {
        var page = _h.OpenModuleConfig("ADAttributeEditor", _h.CreateAttributeEditor(), handlersOnly: false);
        corrupt = SettingsPageHarness.Field<bool>(page, typeof(ModuleConfigPage), "allowlistCorrupt");
        return SettingsPageHarness.RenderedText(page);
    }

    [Fact]
    public void ARefusedLegacyImport_IsExplainedInTheBanner_NamingTheAttribute()
    {
        var config = Path.Combine(_h.Root, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "ad-editable-attributes.json"), JsonSerializer.Serialize(new
        {
            Attributes = new[]
            {
                new { Name = "title", Label = "Job Title", Type = "String", Required = false, AllowClear = true, Pattern = "[" },
            },
        }));

        var text = LoadedBanner(out var corrupt);

        Assert.True(corrupt);
        // The one place the legacy file is the thing to fix: named, with the attribute that stopped it.
        Assert.Contains("ad-editable-attributes.json", text, StringComparison.Ordinal);
        Assert.Contains("Job Title", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACorruptTable_IsExplainedInTheBanner_NamingTheDatabaseAndItsFix_NotTheLegacyFile()
    {
        var store = TestConfigStore.Create(_h.Root);
        new AttributeEditorRepository(store).SaveAllowlist(
            [new AttributeRow("title", "Job Title", "String", null, false, true, null, null, 1)]);
        store.Write((connection, transaction) =>
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO editable_attribute (name, label, type, choices_json, required, allow_clear, level) "
                + "VALUES ('employeeType', 'Employee Type', 'Choice', '{not valid json', 0, 1, 1);";
            insert.ExecuteNonQuery();
        }, TestContext.Current.CancellationToken);

        var text = LoadedBanner(out var corrupt);

        Assert.True(corrupt);
        Assert.Contains("editable_attribute", text, StringComparison.Ordinal);
        Assert.Contains("docs/ConfigDbOperations.md", text, StringComparison.Ordinal);
        Assert.Contains("both application pools stopped", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ad-editable-attributes.json", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AHealthyAllowlist_ShowsNoCorruptBanner()
    {
        // The control: neither variant appears when nothing is wrong.
        new AttributeEditorRepository(TestConfigStore.Create(_h.Root)).SaveAllowlist(
            [new AttributeRow("title", "Job Title", "String", null, false, true, null, null, 1)]);

        var text = LoadedBanner(out var corrupt);

        Assert.False(corrupt);
        Assert.DoesNotContain("store is corrupt", text, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/ConfigDbOperations.md", text, StringComparison.Ordinal);
    }
}
