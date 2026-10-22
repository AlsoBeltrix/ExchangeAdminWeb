using System.Text.Json;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a28d: an undo refused before its save - a protected target included - wrote no audit
/// record. aud-a26m: an edit that cleared an attribute, or set one that had no value, offered no
/// undo at all.
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

    // ---- aud-a26m: an edit with one side not set is undoable --------------------------------------

    /// <summary>A record as the event log hands it to undo: every property, by a case-insensitive name.</summary>
    private static Dictionary<string, object?> AsTheEventLogReadsIt(JsonElement record) =>
        record.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A forward save through the real service, and its record read back from the JSONL as the event log
    /// reads it, with the operator's level added as the page adds it.
    /// </summary>
    private async Task<Dictionary<string, object?>> SavedEdit(LocalAd.User user, Dictionary<string, string?> values)
    {
        var saved = await _h.Service.SaveAsync(
            AttributeEditorHarness.PrincipalFor(user), values, "EXAMPLE\\operator", "10.0.0.5", "CHG0100",
            AttributeEditorHarness.Operator());
        Assert.True(saved.Success, saved.Error);

        var edit = AsTheEventLogReadsIt(Assert.Single(_h.AuditEvents()));
        edit["_undoMaxLevel"] = 1;
        return edit;
    }

    [Fact]
    public async Task AClearOnlyEdit_IsUndoable_AndItsUndoPutsTheValueBack()
    {
        // The cleared side is null, and the writer drops a null field, so the record holds old_department alone.
        var alex = _h.Ad.AddAlex();
        var edit = await SavedEdit(alex, new() { ["department"] = "" });
        Assert.False(edit.ContainsKey("new_department"));
        Assert.Equal("Finance", ((JsonElement)edit["old_department"]!).GetString());

        var undo = _h.CreateUndoService();
        Assert.True(undo.CanUndo(edit));

        var result = await Undo(edit, undo);

        Assert.True(result.Success, result.Error);
        Assert.Equal("Finance", alex.Attributes["department"]);
        Assert.Equal("Finance", _h.Ad.Sets[^1].Replace["department"]);
        var undone = Assert.Single(_h.AuditEvents(), e => Text(e, "action") == "ADAttributeEditor_Undo");
        Assert.Equal("Success", Text(undone, "result"));
    }

    [Fact]
    public async Task ASetFromEmptyEdit_IsUndoable_AndItsUndoClearsIt()
    {
        // The other way round: the attribute had no value, so the record holds new_department alone.
        var sam = _h.Ad.Add("sam.rivera@example.test", "CN=Sam Rivera,OU=Staff,DC=example,DC=test", "Sam Rivera", ("title", "Analyst"));
        var edit = await SavedEdit(sam, new() { ["department"] = "Finance" });
        Assert.False(edit.ContainsKey("old_department"));
        Assert.Equal("Finance", ((JsonElement)edit["new_department"]!).GetString());

        var undo = _h.CreateUndoService();
        Assert.True(undo.CanUndo(edit));

        var result = await Undo(edit, undo);

        Assert.True(result.Success, result.Error);
        Assert.Null(sam.Attributes["department"]);
        Assert.Equal(["department"], _h.Ad.Sets[^1].Clear);
        var undone = Assert.Single(_h.AuditEvents(), e => Text(e, "action") == "ADAttributeEditor_Undo");
        Assert.Equal("Success", Text(undone, "result"));
    }

    [Fact]
    public void AnAttributeWithNeitherSideRecorded_IsNotUndoable()
    {
        // Nothing to restore from and nothing to check the directory against.
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");
        var undo = _h.CreateUndoService();

        var both = AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst");
        Assert.True(undo.CanUndo(both));

        var oneWithout = AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst");
        oneWithout["changedAttributes"] = new[] { "title", "department" };
        Assert.False(undo.CanUndo(oneWithout));

        // A key held null is no side: the JSONL writer would have dropped it.
        var heldNull = AttributeEditorHarness.ForwardEdit(alex, "department", from: null, to: null);
        Assert.False(undo.CanUndo(heldNull));
    }

    [Fact]
    public async Task AFailedEdit_IsNotUndoable_ThoughItRecordsBothSides()
    {
        // A write the directory refused is recorded with the change it tried (aud-a28c), and stays not
        // undoable: only a successful edit is.
        var alex = _h.Ad.AddAlex(title: "Analyst");
        _h.Ad.SetFailure = "Insufficient access rights to perform the operation";
        var saved = await _h.Service.SaveAsync(
            AttributeEditorHarness.PrincipalFor(alex), new() { ["title"] = "Lead Analyst" }, "EXAMPLE\\operator", "10.0.0.5",
            "CHG0100", AttributeEditorHarness.Operator());
        Assert.False(saved.Success);

        var failed = AsTheEventLogReadsIt(Assert.Single(_h.AuditEvents()));
        Assert.Equal("Failed", ((JsonElement)failed["result"]!).GetString());
        Assert.True(failed.ContainsKey("old_title") && failed.ContainsKey("new_title"));
        Assert.False(_h.CreateUndoService().CanUndo(failed));
    }

    // ---- aud-a28o: a trace that cannot begin is the undo's recorded failure -----------------------

    [Fact]
    public async Task AnUndoWhoseTraceCannotBegin_LeavesOneFailureRecord()
    {
        // The operation was begun before the try, outside the catch that records an undo's failure, so a throw
        // there left the event log an exception and the audit log nothing.
        const string traceDown = "The operation trace could not be started.";
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");

        UndoResult? result = null;
        var thrown = await Record.ExceptionAsync(async () => result = await Undo(
            AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst"),
            _h.CreateUndoServiceWhoseTraceFails(traceDown)));

        Assert.Null(thrown);
        Assert.False(result!.Success);
        Assert.Contains(traceDown, result.Error, StringComparison.Ordinal);
        Assert.Equal(0, _h.Service.SessionsOpened);
        Assert.Empty(_h.Ad.Sets);

        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("ADAttributeEditor_Undo", Text(record, "action"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(traceDown, Text(record, "error"));
        Assert.Equal("op-forward-1", Text(record, "originalOperationId"));
        Assert.Equal(alex.Upn, Text(record, "target"));
        Assert.False(record.TryGetProperty("refusedAt", out _));

        var notice = Assert.Single(_h.Notices);
        Assert.Equal(AttributeEditReversal.NoticeAction, notice.Action);
        Assert.False(notice.Success);
    }
}
