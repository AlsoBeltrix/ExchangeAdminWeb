using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a28i: the allowlist save read the store, then replaced the whole table in a later
/// transaction, so a save landing in between - another admin's, or the other instance's on the
/// shared database - was overwritten, and the success record's before-state was the stale read.
/// </summary>
/// <remarks>
/// The aud-a33b style: "the other instance" is a second service over a second store, its own
/// connection factory, on the same database file. The windows are driven, not hoped for: a page's
/// load and its save are separate calls with the other save between them, and the save that lands
/// inside a save runs from a hook on the first service's store as its write opens - where the
/// window was. Conditions are asserted, not wording.
/// </remarks>
public sealed class AllowlistConcurrentSaveTests : IDisposable
{
    private const string AdminA = "EXAMPLE\\allowlist-admin-a";
    private const string AdminB = "EXAMPLE\\allowlist-admin-b";
    private const string Ip = "10.0.0.9";

    private readonly AttributeEditorHarness _h = new();
    private readonly ADAttributeEditorService _other;

    public AllowlistConcurrentSaveTests() => _other = _h.CreateOtherInstance();

    public void Dispose() => _h.Dispose();

    private static EditableAttribute Row(string name, string label) =>
        new(name, label, "String", null, false, true, null, null);

    private static List<EditableAttribute> With(AllowlistForEditing view, EditableAttribute added) =>
        view.Attributes!.Append(added).ToList();

    /// <summary>The other admin's whole save: load, add a row, save what was loaded.</summary>
    private void OtherInstanceAdds(EditableAttribute added)
    {
        var view = _other.LoadAllowlistForEditing();
        var result = _other.SaveAllowlistAndAudit(With(view, added), view.Version, AdminB, Ip);
        Assert.True(result.Success, result.Error);
    }

    private List<string> StoredNames()
    {
        Assert.True(_h.Allowlist.TryReadAllowlist(out var rows, out _), "the allowlist store could not be read");
        return rows.Select(r => r.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    private static string[] Names(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value)
            ? value.EnumerateArray().Select(v => v.GetString() ?? "").ToArray()
            : [];

    private JsonElement OnlyRecordBy(string admin) =>
        Assert.Single(_h.AuditEvents(), r => Text(r, "user") == admin.Split('\\')[1]);

    private void AssertConflictRefusal(AllowlistSaveResult result)
    {
        Assert.False(result.Success);
        Assert.False(result.StoreCorrupt);
        var record = OnlyRecordBy(AdminA);
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(ADAttributeEditorService.AllowlistAuditSection, Text(record, "section"));
        Assert.Equal("ConcurrentChange", Text(record, "refusedAt"));
        Assert.False(record.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(result.Error, Text(record, "error"));
    }

    [Fact]
    public void ASaveFromAPageLoadedBeforeAnotherSave_IsRefused_AndTheOtherSaveSurvives()
    {
        var page = _h.Service.LoadAllowlistForEditing();
        OtherInstanceAdds(Row("employeeID", "Employee ID"));

        var result = _h.Service.SaveAllowlistAndAudit(With(page, Row("employeeNumber", "Employee Number")), page.Version, AdminA, Ip);

        AssertConflictRefusal(result);
        Assert.Contains("employeeID", StoredNames());
        Assert.DoesNotContain("employeeNumber", StoredNames());
    }

    [Fact]
    public void ASaveLandingInsideASave_BeforeItsWrite_IsNotOverwritten()
    {
        // The window the finding names: the other instance commits after this save read the store
        // and before it wrote. The hook runs it as this save's write opens.
        var page = _h.Service.LoadAllowlistForEditing();
        _h.AllowlistStore.BeforeWrite = () => OtherInstanceAdds(Row("employeeID", "Employee ID"));

        var result = _h.Service.SaveAllowlistAndAudit(With(page, Row("employeeNumber", "Employee Number")), page.Version, AdminA, Ip);

        Assert.Null(_h.AllowlistStore.BeforeWrite);
        AssertConflictRefusal(result);
        Assert.Contains("employeeID", StoredNames());
        Assert.DoesNotContain("employeeNumber", StoredNames());
    }

    [Fact]
    public void ARefusedConflict_WritesNothing_NotEvenTheChangeToken()
    {
        // A refusal is decided inside the write and rolled back with it: the other instances'
        // caches have no write to reload for.
        var page = _h.Service.LoadAllowlistForEditing();
        OtherInstanceAdds(Row("employeeID", "Employee ID"));
        var token = _h.AllowlistStore.GetChangeToken();

        var result = _h.Service.SaveAllowlistAndAudit(With(page, Row("employeeNumber", "Employee Number")), page.Version, AdminA, Ip);

        Assert.False(result.Success);
        Assert.Equal(token, _h.AllowlistStore.GetChangeToken());
    }

    [Fact]
    public void ASaveFromAPageReloadedAfterAnotherSave_GoesThrough_AndItsRecordNamesWhatItReplaced()
    {
        OtherInstanceAdds(Row("employeeID", "Employee ID"));
        var page = _h.Service.LoadAllowlistForEditing();
        var edited = page.Attributes!
            .Where(a => a.Name != "employeeID")
            .Append(Row("employeeNumber", "Employee Number"))
            .ToList();

        var result = _h.Service.SaveAllowlistAndAudit(edited, page.Version, AdminA, Ip);

        Assert.True(result.Success, result.Error);
        Assert.Contains("employeeNumber", StoredNames());
        Assert.DoesNotContain("employeeID", StoredNames());
        // The before-state is what the write replaced - the other admin's row included - so the
        // record says who removed it.
        var record = OnlyRecordBy(AdminA);
        Assert.Equal("Success", Text(record, "result"));
        Assert.Equal(new[] { "employeeID" }, Names(record, "removed"));
        Assert.Equal(new[] { "employeeNumber" }, Names(record, "added"));
    }

    [Fact]
    public void TwoSavesFromOnePage_BothGoThrough()
    {
        // The page keeps the version its own save returns, so its next save is not a conflict.
        var page = _h.Service.LoadAllowlistForEditing();
        var first = With(page, Row("employeeID", "Employee ID"));

        var saved = _h.Service.SaveAllowlistAndAudit(first, page.Version, AdminA, Ip);
        Assert.True(saved.Success, saved.Error);
        var second = _h.Service.SaveAllowlistAndAudit(
            first.Append(Row("employeeNumber", "Employee Number")).ToList(), saved.Version!, AdminA, Ip);

        Assert.True(second.Success, second.Error);
        Assert.Contains("employeeID", StoredNames());
        Assert.Contains("employeeNumber", StoredNames());
    }

    [Fact]
    public void ThePage_SavesAgainstTheVersionItLoaded_AndKeepsTheOneItsSaveReturns()
    {
        // Tripwire: no bUnit host renders the page.
        var code = ProgressScan.CodeView(File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "ModuleConfig.razor")));

        var load = AuditCategoryFilingTests.MethodBody(code, "private void LoadAllowlist()");
        Assert.Contains("AttrEditorService.LoadAllowlistForEditing()", load, StringComparison.Ordinal);
        Assert.Contains("allowlistVersion = ", load, StringComparison.Ordinal);

        var save = AuditCategoryFilingTests.MethodBody(code, "private async Task SaveAllowlistAsync()");
        Assert.Contains("SaveAllowlistAndAudit(rows, allowlistVersion, ", save, StringComparison.Ordinal);
        Assert.Contains("allowlistVersion = result.Version", save, StringComparison.Ordinal);
    }

    // ---- aud-a28l: no allowlist writer goes round the version check -------------------------------

    [Fact]
    public void TheServicesOnlyAllowlistWriter_IsTheSaveThatChecksTheLoadedVersion()
    {
        // A method a page can call that is handed allowlist rows is taken for a writer. The save is the
        // only one there should be, and it must take the version its page loaded (aud-a28i); the public
        // SaveAllowlist removed in aud-a28l was a writer with neither the version nor a record.
        var writers = typeof(ADAttributeEditorService)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsPrivate && m.GetParameters().Any(p => HoldsAllowlistRows(p.ParameterType)))
            .Select(m => m.Name)
            .ToList();

        Assert.Equal([nameof(ADAttributeEditorService.SaveAllowlistAndAudit)], writers);
        Assert.Contains(
            typeof(ADAttributeEditorService).GetMethod(nameof(ADAttributeEditorService.SaveAllowlistAndAudit))!.GetParameters(),
            p => p.Name == "loadedVersion" && p.ParameterType == typeof(string));
    }

    [Fact]
    public void NoProductionCode_ReplacesTheStoredAllowlist_OutsideTheSaveThatChecksTheLoadedVersion()
    {
        // The repository is a DI singleton, so the version check could also be gone round by calling
        // its whole-table writers directly. Only the audited save may.
        var calls = new List<string>();
        foreach (var file in ProductionSources())
        {
            var code = RazorSyntax.CommentsBlanked(File.ReadAllText(file));
            calls.AddRange(Regex.Matches(code, @"\.(SaveAllowlist|ReplaceAllowlist)\(")
                .Select(m => $"{Path.GetFileName(file)}: {m.Value}"));
        }

        Assert.Equal(["ADAttributeEditorService.cs: .ReplaceAllowlist("], calls);
        var service = RazorSyntax.CommentsBlanked(File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Services", "ADAttributeEditorService.cs")));
        var save = AuditCategoryFilingTests.MethodBody(service, "public AllowlistSaveResult SaveAllowlistAndAudit(");
        Assert.Contains("_repository.ReplaceAllowlist(", save, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRepositorysWholeTableSave_IsNotPublic()
    {
        var save = typeof(AttributeEditorRepository).GetMethod(
            nameof(AttributeEditorRepository.SaveAllowlist), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotNull(save);
        Assert.False(save!.IsPublic);
        Assert.True(save.IsAssembly);
    }

    private static bool HoldsAllowlistRows(Type type) =>
        type.GetInterfaces().Append(type).Any(i =>
            i.IsGenericType
            && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            && i.GetGenericArguments()[0] is var row
            && (row == typeof(EditableAttribute) || row == typeof(AttributeRow)));

    private static IEnumerable<string> ProductionSources()
    {
        var root = Path.GetDirectoryName(AuditCategoryFilingTests.FindRepoFile("Program.cs"))!;
        return Directory.EnumerateFiles(Path.Combine(root, "Services"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "Components"), "*.razor", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "Components"), "*.cs", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "Modules"), "*.cs", SearchOption.AllDirectories))
            .Append(Path.Combine(root, "Program.cs"));
    }
}
