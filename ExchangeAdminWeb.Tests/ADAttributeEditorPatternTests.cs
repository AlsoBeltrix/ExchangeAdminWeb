using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a28e: an allowlist pattern that cannot be used threw out of SaveAsync, and the attempt left
/// no audit record.
/// </summary>
/// <remarks>
/// The pattern is the allowlist's, not the operator's, and the case that matters is one already
/// stored: these plant it in the repository, as the legacy-file import or a database edit would,
/// and drive the real save through <see cref="AttributeEditorHarness"/>. A pattern that runs past
/// the match timeout takes the same catch but is not driven here: the timeout is a private two
/// seconds the harness cannot shorten, and the record's guard proof says how that half was checked.
/// </remarks>
public sealed class ADAttributeEditorPatternTests : IDisposable
{
    private const string Ticket = "CHG0300";

    private readonly AttributeEditorHarness _h = new();

    public void Dispose() => _h.Dispose();

    /// <summary>Stores the harness allowlist with <paramref name="pattern"/> on Job Title.</summary>
    private void StoreTitlePattern(string pattern)
    {
        _h.Allowlist.SaveAllowlist(
        [
            new AttributeRow("title", "Job Title", "String", null, false, true, null, pattern, 1),
            new AttributeRow("department", "Department", "String", null, false, true, null, null, 1),
        ]);
        _h.Service.InvalidateAllowlistCache();
    }

    private Task<AttributeSaveResult> SaveTitle(LocalAd.User user, string title) =>
        _h.Service.SaveAsync(
            AttributeEditorHarness.PrincipalFor(user),
            new Dictionary<string, string?> { ["title"] = title },
            "EXAMPLE\\operator", "10.0.0.5", Ticket, AttributeEditorHarness.Operator());

    private static string Text(JsonElement record, string property) =>
        record.GetProperty(property).GetString() ?? "";

    [Fact]
    public async Task AnUncompilablePattern_IsAnAuditedRefusal_NotAnException()
    {
        var alex = _h.Ad.AddAlex();
        StoreTitlePattern("[");

        var result = await SaveTitle(alex, "Lead Analyst");

        Assert.False(result.Success);
        Assert.Null(result.AppliedChanges);
        // Which attribute, and that the allowlist is at fault: the operator is not left to guess
        // what is wrong with a value that was never checked.
        Assert.Contains("Job Title", result.Error, StringComparison.Ordinal);
        Assert.Contains("allowlist", result.Error, StringComparison.OrdinalIgnoreCase);

        // Refused before the directory.
        Assert.Equal(0, _h.Service.SessionsOpened);
        Assert.Empty(_h.Ad.Sets);

        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("ADAttributeEdit_Refused", Text(record, "action"));
        Assert.Equal("ADAttributeEditor", Text(record, "category"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal("Validation", Text(record, "refusedAt"));
        Assert.False(record.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(result.Error, Text(record, "error"));
        Assert.Equal(alex.Upn, Text(record, "target"));
        Assert.Equal(Ticket, Text(record, "ticket"));

        // Announced once, by SaveAsync (aud-a26b), with what the operator was told.
        var notice = Assert.Single(_h.Notices);
        Assert.Equal("ADAttributeEditor_Update", notice.Action);
        Assert.False(notice.Success);
        Assert.Equal(alex.Upn, notice.Details["Target"]);
        Assert.Equal(result.Error, notice.Details["Error"]);
    }

    [Fact]
    public async Task AValidPattern_IsStillEnforced_BothWays()
    {
        // The control: the catch must not weaken the check it wraps. A value the pattern rejects is
        // the ordinary validation refusal; one it accepts is written.
        var alex = _h.Ad.AddAlex();
        StoreTitlePattern("^[A-Z][A-Za-z ]+$");

        var refused = await SaveTitle(alex, "lead analyst");

        Assert.False(refused.Success);
        Assert.Equal(0, _h.Service.SessionsOpened);
        Assert.Equal("Validation", Text(Assert.Single(_h.AuditEvents()), "refusedAt"));

        var saved = await SaveTitle(alex, "Lead Analyst");

        Assert.True(saved.Success, saved.Error);
        Assert.Equal("Lead Analyst", Assert.Single(_h.Ad.Sets).Replace["title"]);
    }

    [Fact]
    public void AnAllowlistSaveWithAnUncompilablePattern_IsRefused_NamingTheAttribute()
    {
        // The admin side: caught when the allowlist is saved, before the store is touched.
        var view = _h.Service.LoadAllowlistForEditing();
        var stored = view.Attributes!;
        var edited = stored.Select(a => a.Name == "title" ? a with { Pattern = "[" } : a).ToList();

        var refused = _h.Service.SaveAllowlistAndAudit(edited, view.Version, "EXAMPLE\\allowlist-admin", "10.0.0.9", TestContext.Current.CancellationToken);

        Assert.False(refused.Success);
        Assert.Contains("Job Title", refused.Error, StringComparison.Ordinal);
        Assert.Equal("PatternCheck", Text(Assert.Single(_h.AuditEvents()), "refusedAt"));
        Assert.True(_h.Allowlist.TryReadAllowlist(out var rows, out _));
        Assert.Equal(stored.Select(a => (a.Name, a.Pattern)), rows.Select(r => (r.Name, r.Pattern)));
    }

    [Fact]
    public void AnAllowlistSaveWithAValidPattern_IsStored()
    {
        // The control for the admin side: only a pattern that does not compile is refused.
        var view = _h.Service.LoadAllowlistForEditing();
        var edited = view.Attributes!.Select(a => a.Name == "title" ? a with { Pattern = "^[A-Z]" } : a).ToList();

        var saved = _h.Service.SaveAllowlistAndAudit(edited, view.Version, "EXAMPLE\\allowlist-admin", "10.0.0.9", TestContext.Current.CancellationToken);

        Assert.True(saved.Success, saved.Error);
        Assert.True(_h.Allowlist.TryReadAllowlist(out var rows, out _));
        Assert.Equal("^[A-Z]", Assert.Single(rows, r => r.Name == "title").Pattern);
    }
}
