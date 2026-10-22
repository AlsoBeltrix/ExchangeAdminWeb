using System.Text.Json;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a28d: an undo refused before its save - a protected target included - wrote no audit
/// record.
/// </summary>
/// <remarks>
/// ExecuteUndoAsync has eight refusal returns ahead of SaveAsync. Each completed only the
/// diagnostic trace, and the event-log page that calls it audited nothing, so "who tried to reverse
/// a change to this protected account, and was stopped?" had no answer in the audit log. Driven
/// end to end through <see cref="AttributeEditorHarness"/>; each refusal's record is read back from
/// the JSONL the real audit writer produced.
/// </remarks>
public sealed class ADAttributeEditorUndoRefusalTests : IDisposable
{
    private readonly AttributeEditorHarness _h = new();

    public void Dispose() => _h.Dispose();

    private Task<UndoResult> Undo(Dictionary<string, object?> record, ADAttributeEditorUndoService? service = null) =>
        (service ?? _h.CreateUndoService()).ExecuteUndoAsync(
            record, "EXAMPLE\\operator", "10.0.0.5", "CHG0200", AttributeEditorHarness.Operator());

    private static string Text(JsonElement record, string property) =>
        record.GetProperty(property).GetString() ?? "";

    /// <summary>
    /// Refused, nothing written, and exactly one record: the undo's, failed, saying where it was
    /// refused and that no mutation was attempted.
    /// </summary>
    private JsonElement AssertRefusalRecorded(UndoResult result, string error, string refusedAt, string target)
    {
        Assert.False(result.Success);
        Assert.Equal(error, result.Error);
        Assert.Empty(_h.Ad.Sets);

        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("ADAttributeEditor_Undo", Text(record, "action"));
        Assert.Equal("ADAttributeEditor", Text(record, "category"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(error, Text(record, "error"));
        Assert.Equal(refusedAt, Text(record, "refusedAt"));
        Assert.False(record.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(target, Text(record, "target"));
        Assert.Equal("operator", Text(record, "user"));
        Assert.Equal("10.0.0.5", Text(record, "ip"));
        Assert.Equal("CHG0200", Text(record, "ticket"));
        return record;
    }

    [Fact]
    public async Task AProtectedTarget_IsRefused_AndTheDenialIsAudited()
    {
        // The case the Constitution names: refuse, fail closed, and audit the denial.
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");
        _h.Protection.Decide = _ => ProtectedPrincipalResult.Protected("protected for test", "User: alex.morgan@example.test");

        var result = await Undo(AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst"));

        var record = AssertRefusalRecorded(result, "Target is a protected principal and cannot be modified.", "ProtectedPrincipalCheck", alex.Upn);
        Assert.Equal("op-forward-1", Text(record, "originalOperationId"));

        // Naming the original does not make the log call it reversed: only a successful reversal
        // marks it (aud-a26c, which is why that fix landed first).
        Assert.DoesNotContain("op-forward-1", ReversedOperations.From(
            _h.AuditEvents(),
            e => e.TryGetProperty("originalOperationId", out var id) ? id.GetString() : null,
            e => e.TryGetProperty("result", out var outcome) ? outcome.GetString() : null));
    }

    [Fact]
    public async Task AFailedProtectionCheck_IsRefused_FailClosed_AndAudited()
    {
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");
        _h.Protection.Decide = _ => ProtectedPrincipalResult.Failed("Protected principal configuration could not be read.");

        var result = await Undo(AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst"));

        AssertRefusalRecorded(result,
            "Protected principal check failed: Protected principal configuration could not be read.",
            "ProtectedPrincipalCheck", alex.Upn);
    }

    [Fact]
    public async Task ATargetThatNowResolvesElsewhere_IsRefused_AndAudited()
    {
        // The record's object id is not the object the name resolves to now (A9).
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");
        var record = AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst");
        record["targetObjectGuid"] = Guid.NewGuid().ToString();

        var result = await Undo(record);

        AssertRefusalRecorded(result, result.Error!, "IdentityCheck", alex.Upn);
        Assert.Contains("different directory object", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValueChangedSinceTheEdit_IsRefused_AndAudited()
    {
        var alex = _h.Ad.AddAlex(title: "Principal Analyst");

        var result = await Undo(AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst"));

        AssertRefusalRecorded(result, result.Error!, "ConflictCheck", alex.Upn);
        Assert.StartsWith("Conflict on attribute 'title'", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATargetThatCannotBeLookedUp_IsRefused_AndAudited()
    {
        var gone = new LocalAd.User
        {
            Guid = Guid.NewGuid(),
            Dn = "CN=Former Staff,OU=Staff,DC=example,DC=test",
            Upn = "former.staff@example.test",
        };

        var result = await Undo(AttributeEditorHarness.ForwardEdit(gone, "title", from: "Analyst", to: "Lead Analyst"));

        AssertRefusalRecorded(result, result.Error!, "Lookup", gone.Upn);
        Assert.StartsWith("Cannot read current state of 'former.staff@example.test'", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUndoWhileTheModuleIsDisabled_IsRefused_AndAudited()
    {
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");

        var result = await Undo(
            AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst"),
            _h.CreateUndoService(moduleEnabled: false));

        AssertRefusalRecorded(result, "ADAttributeEditor module is not enabled.", "ModuleEnablement", alex.Upn);
    }

    [Fact]
    public async Task ARecordWithNoTarget_IsRefused_AndAudited()
    {
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");
        var record = AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst");
        record.Remove("target");

        var result = await Undo(record);

        AssertRefusalRecorded(result, "No target identity in audit event.", "AuditRecord", "(none)");
    }

    [Fact]
    public async Task ARecordWithNoChangedAttributes_IsRefused_AndAudited()
    {
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");
        var record = AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst");
        record["changedAttributes"] = Array.Empty<string>();

        var result = await Undo(record);

        AssertRefusalRecorded(result, "No changed attributes recorded in audit event.", "AuditRecord", alex.Upn);
    }

    [Fact]
    public async Task AReversalThatReachedTheDirectoryAndFailed_IsNotMarkedAsARefusal()
    {
        // The control that makes the flag mean something: this one DID try to write.
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");
        _h.Ad.SetFailure = "Insufficient access rights to perform the operation";

        var result = await Undo(AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst"));

        Assert.False(result.Success);
        var undoRecord = Assert.Single(_h.AuditEvents(), e => Text(e, "action") == "ADAttributeEditor_Undo");
        Assert.Equal("Failed", Text(undoRecord, "result"));
        Assert.False(undoRecord.TryGetProperty("refusedAt", out _));
        Assert.False(undoRecord.TryGetProperty("mutationAttempted", out _));
    }
}
