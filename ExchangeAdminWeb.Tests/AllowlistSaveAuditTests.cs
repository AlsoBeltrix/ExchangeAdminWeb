using System.Text.Json;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a28f: the Module Configuration page's allowlist save wrote no audit record for a refusal or
/// for its exception path, and its one record - on success - named every attribute as added and
/// none as removed, because the "before" it passed was an empty list.
/// </summary>
/// <remarks>
/// Two kinds of guard, and they fail differently. The save itself now runs in
/// <see cref="ADAttributeEditorService.SaveAllowlistAndAudit"/>, so every path the page can reach
/// after its authorisation re-check is driven here for real through
/// <see cref="AttributeEditorHarness"/>: its allowlist store, the audit writer, a write made to fail.
/// The page keeps that re-check - it holds the principal and the policies - and no bUnit host exists,
/// so the two records the page writes itself (the authorisation refusal and its catch) and the
/// absence of any other are pinned by source tripwires over the method's code. Conditions are
/// asserted, not wording.
/// </remarks>
public sealed class AllowlistSaveAuditTests : IDisposable
{
    private const string Admin = "EXAMPLE\\allowlist-admin";
    private const string Ip = "10.0.0.9";

    private readonly AttributeEditorHarness _h = new();

    public void Dispose() => _h.Dispose();

    /// <summary>The allowlist as the page loads it and would send it back untouched.</summary>
    private List<EditableAttribute> Loaded() => _h.Service.GetAllowlist()!.ToList();

    private AllowlistSaveResult Save(List<EditableAttribute> attributes) =>
        _h.Service.SaveAllowlistAndAudit(attributes, Admin, Ip);

    /// <summary>The stored rows, name-ordered, to show a refused save left them alone.</summary>
    private List<ExchangeAdminWeb.Services.Storage.AttributeRow> StoredRows()
    {
        Assert.True(_h.Allowlist.TryReadAllowlist(out var rows, out _), "the allowlist store could not be read");
        return rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Text(JsonElement record, string property) =>
        record.GetProperty(property).GetString() ?? "";

    private static string[] Names(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value)
            ? value.EnumerateArray().Select(v => v.GetString() ?? "").ToArray()
            : [];

    private static EditableAttribute MemberOf() =>
        new("memberOf", "Member Of", "String", null, false, true, null, null);

    /// <summary>A refusal record: filed with the settings changes, failed, saying where and that nothing was tried.</summary>
    private static void AssertRefusal(JsonElement record, string refusedAt, string? shown)
    {
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal("AdminSettings", Text(record, "category"));
        Assert.Equal(ADAttributeEditorService.AllowlistAuditSection, Text(record, "section"));
        Assert.Equal(refusedAt, Text(record, "refusedAt"));
        Assert.False(record.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(shown, Text(record, "error"));
        Assert.Equal("allowlist-admin", Text(record, "user"));
        Assert.Equal(Ip, Text(record, "ip"));
    }

    // ---- The success record says what the save actually changed -------------------------------

    [Fact]
    public void ARowRemoved_IsRecordedAsExactlyThatRemoval()
    {
        var kept = Loaded().Where(a => a.Name != "extensionAttribute5").ToList();

        var result = Save(kept);

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain(StoredRows(), r => r.Name == "extensionAttribute5");

        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Success", Text(record, "result"));
        Assert.Equal("AdminSettings", Text(record, "category"));
        Assert.Equal(ADAttributeEditorService.AllowlistAuditSection, Text(record, "section"));
        Assert.Equal(new[] { "extensionAttribute5" }, Names(record, "removed"));
        // The defect: an empty "before" made every kept row read as newly added.
        Assert.False(record.TryGetProperty("added", out _));
        Assert.False(record.TryGetProperty("changed", out _));
    }

    [Fact]
    public void ARowAdded_IsRecordedAsAnAddition_AndNothingElse()
    {
        var grown = Loaded()
            .Append(new EditableAttribute("employeeID", "Employee ID", "String", null, false, true, 20, null))
            .ToList();

        var result = Save(grown);

        Assert.True(result.Success, result.Error);
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal(new[] { "employeeID" }, Names(record, "added"));
        Assert.False(record.TryGetProperty("removed", out _));
        Assert.False(record.TryGetProperty("changed", out _));
    }

    [Fact]
    public void ARowChanged_RecordsEachFieldThatChanged_WithItsOldAndNewValue()
    {
        var edited = Loaded()
            .Select(a => a.Name == "title" ? a with { Pattern = "^[A-Z]", Level = 2, AllowClear = false } : a)
            .ToList();

        var result = Save(edited);

        Assert.True(result.Success, result.Error);
        var record = Assert.Single(_h.AuditEvents());
        Assert.False(record.TryGetProperty("added", out _));
        Assert.False(record.TryGetProperty("removed", out _));

        var changed = record.GetProperty("changed").EnumerateArray().ToList();
        Assert.All(changed, c => Assert.Equal("title", Text(c, "attribute")));
        Assert.Equal(
            new[] { "AllowClear", "Level", "Pattern" },
            changed.Select(c => Text(c, "field")).Order(StringComparer.Ordinal).ToArray());

        var pattern = Assert.Single(changed, c => Text(c, "field") == "Pattern");
        Assert.False(pattern.TryGetProperty("old", out _));
        Assert.Equal("^[A-Z]", Text(pattern, "new"));

        var level = Assert.Single(changed, c => Text(c, "field") == "Level");
        Assert.Equal("1", Text(level, "old"));
        Assert.Equal("2", Text(level, "new"));

        var allowClear = Assert.Single(changed, c => Text(c, "field") == "AllowClear");
        Assert.Equal("true", Text(allowClear, "old"));
        Assert.Equal("false", Text(allowClear, "new"));
    }

    [Fact]
    public void ASaveThatChangesNothing_StillLeavesExactlyOneRecord()
    {
        var result = Save(Loaded());

        Assert.True(result.Success, result.Error);
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Success", Text(record, "result"));
        Assert.False(record.TryGetProperty("added", out _));
        Assert.False(record.TryGetProperty("removed", out _));
        Assert.False(record.TryGetProperty("changed", out _));
    }

    // ---- Every refusal is recorded, and nothing is written -------------------------------------

    [Fact]
    public void ADenylistedName_IsRefused_Audited_AndNothingIsWritten()
    {
        var before = StoredRows();

        var result = Save(Loaded().Append(MemberOf()).ToList());

        Assert.False(result.Success);
        Assert.False(result.StoreCorrupt);
        Assert.Equal(before, StoredRows());
        AssertRefusal(Assert.Single(_h.AuditEvents()), "Denylist", result.Error);
    }

    [Fact]
    public void AnUncompilablePattern_IsRefused_Audited_NamingTheAttribute()
    {
        var before = StoredRows();
        var edited = Loaded().Select(a => a.Name == "title" ? a with { Pattern = "[" } : a).ToList();

        var result = Save(edited);

        Assert.False(result.Success);
        // Which attribute: the admin is not left to search the table for it.
        Assert.Contains("Job Title", result.Error, StringComparison.Ordinal);
        Assert.Equal(before, StoredRows());
        AssertRefusal(Assert.Single(_h.AuditEvents()), "PatternCheck", result.Error);
    }

    [Fact]
    public void ACorruptStore_IsRefused_Audited_AndNotSavedOver()
    {
        _h.CorruptAllowlist();

        var result = Save([new EditableAttribute("title", "Job Title", "String", null, false, true, null, null)]);

        Assert.False(result.Success);
        Assert.True(result.StoreCorrupt);
        // Still corrupt: the save did not replace what the store holds.
        Assert.True(_h.Service.IsAllowlistCorrupt());
        AssertRefusal(Assert.Single(_h.AuditEvents()), "AllowlistConfig", result.Error);
    }

    // ---- aud-a26f: blank and repeated rows are refused, not dropped -----------------------------

    [Theory]
    [InlineData("", "Employee ID")]
    [InlineData("employeeID", "")]
    [InlineData("  ", "  ")]
    public void ARowWithABlankNameOrLabel_IsRefused_NamingTheRow_AndNothingIsWritten(string name, string label)
    {
        var before = StoredRows();
        var edited = Loaded()
            .Append(new EditableAttribute(name, label, "String", null, false, true, null, null))
            .ToList();

        var result = Save(edited);

        Assert.False(result.Success);
        Assert.False(result.StoreCorrupt);
        // Which row: the admin is told where, not left to find it.
        Assert.Contains($"row {edited.Count}", result.Error, StringComparison.Ordinal);
        Assert.Equal(before, StoredRows());
        AssertRefusal(Assert.Single(_h.AuditEvents()), "BlankRow", result.Error);
    }

    [Fact]
    public void AnLdapNameOnTwoRows_IsRefused_NamingTheName_AndNothingIsWritten()
    {
        // The store keeps one row per name, ignoring case, so the second row used to be merged into
        // the first without a word.
        var before = StoredRows();
        var edited = Loaded()
            .Append(new EditableAttribute("Title", "Another Title", "String", null, false, true, null, null))
            .ToList();
        var first = edited.FindIndex(a => a.Name == "title") + 1;

        var result = Save(edited);

        Assert.False(result.Success);
        Assert.Contains("'title'", result.Error, StringComparison.Ordinal);
        Assert.Contains($"rows {first}, {edited.Count}", result.Error, StringComparison.Ordinal);
        Assert.Equal(before, StoredRows());
        AssertRefusal(Assert.Single(_h.AuditEvents()), "DuplicateName", result.Error);
    }

    [Fact]
    public void TheCorruptStoreRefusal_NamesTheConfigDatabaseAndItsFix_NotTheLegacyFile()
    {
        // This finding is about the text: the allowlist has lived in the config database since the
        // legacy import, so the file the refusal used to name is one nothing reads.
        _h.CorruptAllowlist();

        var result = Save([new EditableAttribute("title", "Job Title", "String", null, false, true, null, null)]);

        Assert.True(result.StoreCorrupt);
        Assert.Contains("editable_attribute", result.Error, StringComparison.Ordinal);
        Assert.Contains("docs/ConfigDbOperations.md", result.Error, StringComparison.Ordinal);
        Assert.Contains("both application pools stopped", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("ad-editable-attributes.json", result.Error, StringComparison.Ordinal);
    }

    // ---- The exception path is recorded as a failure, not a refusal ----------------------------

    [Fact]
    public void AWriteThatThrows_IsAuditedAsAFailure_AndTheStoreIsUnchanged()
    {
        var before = StoredRows();
        var shrunk = Loaded().Where(a => a.Name != "department").ToList();
        _h.AllowlistStore.WritesFail = true;

        var result = Save(shrunk);

        _h.AllowlistStore.WritesFail = false;
        Assert.False(result.Success);
        Assert.False(result.StoreCorrupt);
        Assert.Equal(before, StoredRows());

        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(ADAttributeEditorService.AllowlistAuditSection, Text(record, "section"));
        Assert.Equal(result.Error, Text(record, "error"));
        // Not a refusal: the write was tried, so neither refusal field may claim otherwise.
        Assert.False(record.TryGetProperty("refusedAt", out _));
        Assert.False(record.TryGetProperty("mutationAttempted", out _));
    }

    [Fact]
    public void ARefusal_IsFiledBesideTheChangesItWouldHaveMade()
    {
        // An admin filtering the event log for allowlist changes must find the refused attempts too.
        Save(Loaded().Append(MemberOf()).ToList());
        Save(Loaded());

        var records = _h.AuditEvents();
        Assert.Equal(2, records.Count);
        Assert.Equal("Failed", Text(records[0], "result"));
        Assert.Equal("Success", Text(records[1], "result"));
        foreach (var property in new[] { "action", "category", "section" })
            Assert.Equal(Text(records[1], property), Text(records[0], property));
    }

    // ---- The two records the page writes itself ------------------------------------------------

    [Fact]
    public void ThePageHelpers_WriteARefusalAndAFailure()
    {
        _h.Service.LogAllowlistSaveRefused(Admin, Ip, "Authorization", "not authorised");
        _h.Service.LogAllowlistSaveFailed(Admin, Ip, "Failed to save: boom");

        var records = _h.AuditEvents();
        Assert.Equal(2, records.Count);
        AssertRefusal(records[0], "Authorization", "not authorised");
        Assert.Equal("Failed", Text(records[1], "result"));
        Assert.Equal(ADAttributeEditorService.AllowlistAuditSection, Text(records[1], "section"));
        Assert.Equal("Failed to save: boom", Text(records[1], "error"));
        Assert.False(records[1].TryGetProperty("refusedAt", out _));
        Assert.False(records[1].TryGetProperty("mutationAttempted", out _));
    }

    private static string PageSaveCode() =>
        AuditCategoryFilingTests.MethodBody(
            ProgressScan.CodeView(File.ReadAllText(
                AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "ModuleConfig.razor"))),
            "private async Task SaveAllowlistAsync()");

    [Fact]
    public void ThePageSave_WritesThroughTheAuditedSave_AndWritesNoRecordOfItsOwn()
    {
        // The service writes the success record; a page that also wrote one would leave two.
        var code = PageSaveCode();

        Assert.Contains("AttrEditorService.SaveAllowlistAndAudit(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Audit.Log", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePageAuthorisationRefusal_IsAuditedBeforeItReturns()
    {
        var code = PageSaveCode();

        var check = code.IndexOf("ModuleAdmin.IsModuleAdmin(", StringComparison.Ordinal);
        Assert.True(check >= 0, "the authorisation re-check is gone from SaveAllowlistAsync - this guard is stale");
        var refusalReturn = code.IndexOf("return;", check, StringComparison.Ordinal);
        Assert.True(refusalReturn > check, "the authorisation refusal no longer returns - this guard is stale");

        var audited = code.IndexOf("AttrEditorService.LogAllowlistSaveRefused(", check, StringComparison.Ordinal);
        Assert.True(audited > check && audited < refusalReturn,
            "SaveAllowlistAsync's authorisation refusal returns without writing its refusal record");
    }

    [Fact]
    public void ThePageSave_HandsEveryRowToTheService_DroppingNone()
    {
        // aud-a26f: the page used to filter out a row with a blank name or label before the service
        // saw it, so it vanished from the allowlist without a word. Every row goes to the service,
        // which refuses a blank one by its position.
        var code = PageSaveCode();

        Assert.DoesNotContain("IsNullOrWhiteSpace", code, StringComparison.Ordinal);
        Assert.DoesNotContain(".Where(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePageCatch_AuditsTheFailure()
    {
        var code = PageSaveCode();

        var caught = code.IndexOf("catch (Exception", StringComparison.Ordinal);
        Assert.True(caught >= 0, "SaveAllowlistAsync has no catch - this guard is stale");
        var end = code.IndexOf("finally", caught, StringComparison.Ordinal);
        var block = code[caught..(end > caught ? end : code.Length)];

        Assert.Contains("AttrEditorService.LogAllowlistSaveFailed(", block, StringComparison.Ordinal);
    }

    // ---- aud-a26e: a pattern can be seen and edited on the page --------------------------------

    [Fact]
    public void APatternEnteredOnARow_IsSaved_ReadBack_AndRecorded()
    {
        const string pattern = "^[A-Z][A-Za-z ]*$";
        var entered = Loaded().Select(a => a.Name == "department" ? a with { Pattern = pattern } : a).ToList();

        var result = Save(entered);

        Assert.True(result.Success, result.Error);
        Assert.Equal(pattern, Assert.Single(_h.Service.GetAllowlist()!, a => a.Name == "department").Pattern);
        var change = Assert.Single(Assert.Single(_h.AuditEvents()).GetProperty("changed").EnumerateArray());
        Assert.Equal("department", Text(change, "attribute"));
        Assert.Equal("Pattern", Text(change, "field"));
        Assert.Equal(pattern, Text(change, "new"));

        // Cleared on the row, it is no pattern at all.
        var cleared = Loaded().Select(a => a.Name == "department" ? a with { Pattern = null } : a).ToList();
        Assert.True(Save(cleared).Success);
        Assert.Null(Assert.Single(_h.Service.GetAllowlist()!, a => a.Name == "department").Pattern);
    }

    [Fact]
    public void ThePatternInput_CarriesTheRowsPatternInAndOut()
    {
        // Tripwire: no bUnit host renders the page. The row must show the stored pattern and hand an
        // edit to UpdateAttrPattern, which must write what was typed; LoadAllowlist fills the row and
        // the save passes it into the attribute it saves.
        var raw = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "ModuleConfig.razor"));
        Assert.Matches(@"<input[^>]*\bvalue=""@attr\.Pattern""[^>]*@onchange=""e => UpdateAttrPattern\(idx, e\)""", raw);

        var code = ProgressScan.CodeView(raw);
        var handler = AuditCategoryFilingTests.MethodBody(code, "private void UpdateAttrPattern(");
        Assert.Contains("allowlistAttributes[idx].Pattern =", handler, StringComparison.Ordinal);
        Assert.Contains("e.Value", handler, StringComparison.Ordinal);
        Assert.Contains("dirty.Increment(SecAttrs)", handler, StringComparison.Ordinal);

        Assert.Contains("Pattern = a.Pattern",
            AuditCategoryFilingTests.MethodBody(code, "private void LoadAllowlist()"), StringComparison.Ordinal);
        Assert.Contains("a.MaxLength, a.Pattern, a.Level", PageSaveCode(), StringComparison.Ordinal);
    }
}
