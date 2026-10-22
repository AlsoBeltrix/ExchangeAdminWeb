using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a28b: SaveAsync driven for real, so a version that refuses every call cannot pass.
/// </summary>
/// <remarks>
/// Before this, SaveAsync's coverage was source scans (RefusalAuditTests, the servicer guards),
/// which read the file as text and cannot see reachability: an edit that made SaveAsync return the
/// corrupt-allowlist refusal on EVERY call passed the whole suite. These run the real method
/// through <see cref="AttributeEditorHarness"/> - real allowlist store, validation, servicer grant
/// and audit writer, a fake protection decision, and local stand-ins for the AD cmdlets - and
/// assert on the result, on what reached the stand-in directory, on whether a directory session
/// was opened at all, and on the audit records read back from the JSONL the writer produced.
/// </remarks>
public sealed class ADAttributeEditorSaveTests : IDisposable
{
    private const string Actor = "EXAMPLE\\operator";
    private const string Ip = "10.0.0.5";
    private const string Ticket = "CHG0100";

    private readonly AttributeEditorHarness _h = new();

    public void Dispose() => _h.Dispose();

    private Task<AttributeSaveResult> Save(
        LocalAd.User user,
        Dictionary<string, string?> values,
        ClaimsPrincipal? actingUser = null,
        int? maxLevel = null,
        ResolvedDirectoryPrincipal? principal = null) =>
        _h.Service.SaveAsync(
            principal ?? AttributeEditorHarness.PrincipalFor(user), values, Actor, Ip, Ticket,
            actingUser ?? AttributeEditorHarness.Operator(), maxLevel);

    private static string Text(JsonElement record, string property) =>
        record.GetProperty(property).GetString() ?? "";

    /// <summary>
    /// A refusal made before the directory: no session opened, nothing written, and exactly one
    /// audit record, which says it was refused, where, and that no mutation was attempted.
    /// </summary>
    private void AssertRefusedBeforeTheDirectory(AttributeSaveResult result, string error, string refusedAt)
    {
        Assert.False(result.Success);
        Assert.Equal(error, result.Error);
        Assert.Null(result.AppliedChanges);
        Assert.Equal(0, _h.Service.SessionsOpened);
        Assert.Empty(_h.Ad.Sets);

        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("ADAttributeEdit_Refused", Text(record, "action"));
        Assert.Equal("ADAttributeEditor", Text(record, "category"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(refusedAt, Text(record, "refusedAt"));
        Assert.False(record.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(error, Text(record, "error"));
        Assert.Equal("alex.morgan@example.test", Text(record, "target"));
        Assert.Equal("operator", Text(record, "user"));
        Assert.Equal(Ip, Text(record, "ip"));
        Assert.Equal(Ticket, Text(record, "ticket"));
    }

    // ---- the happy path ------------------------------------------------------------------------

    [Fact]
    public async Task AnOrdinaryEdit_IsWrittenOnceToTheBoundObject_AndAudited()
    {
        var alex = _h.Ad.AddAlex();

        var result = await Save(alex, new() { ["title"] = "Lead Analyst", ["department"] = "" });

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Error);
        Assert.Collection(result.AppliedChanges!,
            change =>
            {
                Assert.Equal("title", change.Name);
                Assert.Equal("Analyst", change.OldValue);
                Assert.Equal("Lead Analyst", change.NewValue);
            },
            change =>
            {
                Assert.Equal("department", change.Name);
                Assert.Equal("Finance", change.OldValue);
                Assert.Null(change.NewValue);
            });

        // The stand-in took the import, so nothing real was loaded; then one re-read BY DN, one write
        // carrying the replace and the clear together, and the read of what it wrote by the object's
        // GUID (aud-a26j).
        Assert.Equal(["Import-Module|ActiveDirectory", $"Get-ADUser|{alex.Dn}|", $"Set-ADUser|{alex.Dn}", $"Get-ADUser|{alex.Guid}|"], _h.Ad.Calls);
        var write = Assert.Single(_h.Ad.Sets);
        Assert.Equal(alex.Dn, write.Identity);
        Assert.Equal("Lead Analyst", Assert.Single(write.Replace, kv => kv.Key == "title").Value);
        Assert.Single(write.Replace);
        Assert.Equal(["department"], write.Clear);

        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("ADAttributeEditor_Update", Text(record, "action"));
        Assert.Equal("Success", Text(record, "result"));
        Assert.Equal("Analyst", Text(record, "old_title"));
        Assert.Equal("Lead Analyst", Text(record, "new_title"));
        Assert.Equal("Finance", Text(record, "old_department"));
        Assert.Equal(alex.Guid.ToString(), Text(record, "targetObjectGuid"));
        Assert.Equal(alex.Upn, Text(record, "target"));
        Assert.Equal("operator", Text(record, "user"));
        Assert.Equal(Ip, Text(record, "ip"));
        Assert.Equal(Ticket, Text(record, "ticket"));
        Assert.False(record.TryGetProperty("refusedAt", out _));
    }

    // ---- refusals before the directory, every one audited --------------------------------------

    [Fact]
    public async Task AProtectedTarget_IsRefusedBeforeTheDirectory_AndTheRefusalIsAudited()
    {
        var alex = _h.Ad.AddAlex();
        _h.Protection.Decide = _ => ProtectedPrincipalResult.Protected("protected for test", "User: alex.morgan@example.test");

        var result = await Save(alex, new() { ["title"] = "Lead Analyst" });

        AssertRefusedBeforeTheDirectory(result, "Target is a protected principal and cannot be modified.", "ProtectedPrincipalCheck");
    }

    [Fact]
    public async Task AFailedProtectionRead_Refuses_FailClosed()
    {
        // The check could not be made. That is not "not protected": it refuses with the reason.
        var alex = _h.Ad.AddAlex();
        _h.Protection.Decide = _ => ProtectedPrincipalResult.Failed("Protected principal configuration could not be read.");

        var result = await Save(alex, new() { ["title"] = "Lead Analyst" });

        AssertRefusedBeforeTheDirectory(result, "Protected principal configuration could not be read.", "ProtectedPrincipalCheck");
    }

    [Fact]
    public async Task AnAuthorisedServicer_IsWritten_AndTheAuditNamesTheGrant()
    {
        // The control for the test below: with the grant readable, the servicer path is open and
        // the success record says who permitted it (pps-3).
        var alex = _h.Ad.AddAlex();
        _h.Protection.Decide = _ => ProtectedPrincipalResult.Protected("protected for test", "User: alex.morgan@example.test");
        _h.GrantServicer();

        var result = await Save(alex, new() { ["title"] = "Lead Analyst" },
            actingUser: AttributeEditorHarness.Operator(AttributeEditorHarness.ServicerSid));

        Assert.True(result.Success, result.Error);
        Assert.Single(_h.Ad.Sets);
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("ADAttributeEditor_Update", Text(record, "action"));
        Assert.Equal("Success", Text(record, "result"));
        var note = Text(record, ProtectedPrincipalServicing.AuditKey);
        Assert.Contains(AttributeEditorHarness.ServicerSid, note, StringComparison.Ordinal);
        Assert.Contains("User: alex.morgan@example.test", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreadableServicerGrant_Refuses_FailClosed()
    {
        // The same operator and grant as above, with the section-access store unreadable: an
        // unreadable grant says nothing about membership, so the protected target is refused.
        var alex = _h.Ad.AddAlex();
        _h.Protection.Decide = _ => ProtectedPrincipalResult.Protected("protected for test", "User: alex.morgan@example.test");
        _h.GrantServicer();
        _h.SectionStore.ReadsFail = true;

        var result = await Save(alex, new() { ["title"] = "Lead Analyst" },
            actingUser: AttributeEditorHarness.Operator(AttributeEditorHarness.ServicerSid));

        AssertRefusedBeforeTheDirectory(result, "Target is a protected principal and cannot be modified.", "ProtectedPrincipalCheck");
    }

    [Fact]
    public async Task MissingCredentials_Refuse_AndTheRefusalIsAudited()
    {
        var alex = _h.Ad.AddAlex();
        _h.CredentialsAvailable = false;

        var result = await Save(alex, new() { ["title"] = "Lead Analyst" });

        AssertRefusedBeforeTheDirectory(result, "AD credentials unavailable.", "GetADCredentials");
    }

    [Fact]
    public async Task AnAttributeAboveTheCallersLevel_IsRefused()
    {
        // extensionAttribute5 is level 2; the caller holds level 1. The service is the one that
        // must not trust any caller to have filtered (aud-a28g moved the page's own check here).
        var alex = _h.Ad.AddAlex();

        var result = await Save(alex, new() { ["extensionAttribute5"] = "CC-42" }, maxLevel: 1);

        AssertRefusedBeforeTheDirectory(result,
            "Access denied: you do not have permission to edit extensionAttribute5.", "LevelCheck");
    }

    [Fact]
    public async Task AnAttributeTheAllowlistDoesNotHold_IsAValidationRefusal_WhateverTheLevel()
    {
        // The control for the level refusal above: a name the allowlist does not hold at all is
        // not a level question, and is refused as what it is.
        var alex = _h.Ad.AddAlex();

        var result = await Save(alex, new() { ["employeeNumber"] = "42" }, maxLevel: 3);

        AssertRefusedBeforeTheDirectory(result, "Attribute 'employeeNumber' is not in the allowlist.", "Validation");
    }

    [Fact]
    public async Task ADenylistedAttribute_IsRefused()
    {
        var alex = _h.Ad.AddAlex();

        var result = await Save(alex, new() { ["userAccountControl"] = "512" });

        AssertRefusedBeforeTheDirectory(result,
            "Attribute 'userAccountControl' is on the hard denylist and cannot be modified.", "Validation");
    }

    [Fact]
    public async Task ACorruptAllowlist_Refuses()
    {
        var alex = _h.Ad.AddAlex();
        _h.CorruptAllowlist();

        var result = await Save(alex, new() { ["title"] = "Lead Analyst" });

        AssertRefusedBeforeTheDirectory(result, "Attribute allowlist configuration is corrupt.", "AllowlistConfig");
    }

    // ---- the bound-object check, after the directory -------------------------------------------

    [Fact]
    public async Task AReReadThatIsNotTheLookedUpObject_IsRefused_WithNoWrite()
    {
        // The DN now names a different object than the one the operator looked at. The write must
        // not happen, and the failure is audited.
        var alex = _h.Ad.AddAlex();
        var stale = AttributeEditorHarness.PrincipalFor(alex) with { ObjectGuid = Guid.NewGuid().ToString() };

        var result = await Save(alex, new() { ["title"] = "Lead Analyst" }, principal: stale);

        Assert.False(result.Success);
        Assert.Equal("Bound-object mismatch: the resolved object no longer matches the lookup snapshot.", result.Error);
        Assert.Empty(_h.Ad.Sets);
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("ADAttributeEditor_Update", Text(record, "action"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(result.Error, Text(record, "error"));
    }

    // ---- failures that throw before the write (aud-a28c) ---------------------------------------

    [Fact]
    public async Task ATargetMovedSinceTheLookup_FailsAudited_InsteadOfThrowing()
    {
        // The DN the lookup captured no longer resolves - the account was moved or deleted - and
        // under -ErrorAction Stop Get-ADUser -Identity THROWS for that rather than returning
        // nothing. It escaped SaveAsync with no audit record; the Count == 0 branch written for
        // exactly this case could not be reached.
        var alex = _h.Ad.AddAlex();
        var moved = AttributeEditorHarness.PrincipalFor(alex) with
        {
            DistinguishedName = "CN=Alex Morgan,OU=Leavers,DC=example,DC=test",
        };

        var result = await Save(alex, new() { ["title"] = "Lead Analyst" }, principal: moved);

        Assert.False(result.Success);
        Assert.StartsWith("Target object could not be re-read before the write:", result.Error, StringComparison.Ordinal);
        Assert.Contains("Cannot find an object with identity", result.Error, StringComparison.Ordinal);
        Assert.Empty(_h.Ad.Sets);

        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("ADAttributeEditor_Update", Text(record, "action"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(result.Error, Text(record, "error"));
        Assert.Equal(alex.Upn, Text(record, "target"));
        Assert.Equal(Ticket, Text(record, "ticket"));

        // And announced, because SaveAsync announces every result it returns (aud-a26b).
        var notice = Assert.Single(_h.Notices);
        Assert.False(notice.Success);
        Assert.Equal(result.Error, notice.Details["Error"]);
    }

    [Fact]
    public async Task AHostWithoutTheDirectoryModule_FailsAudited_InsteadOfThrowing()
    {
        var alex = _h.Ad.AddAlex();
        _h.Ad.ImportFailure =
            "The specified module 'ActiveDirectory' was not loaded because no valid module file was found in any module directory.";

        var result = await Save(alex, new() { ["title"] = "Lead Analyst" });

        Assert.False(result.Success);
        Assert.Contains("no valid module file was found", result.Error, StringComparison.Ordinal);
        Assert.Empty(_h.Ad.Sets);

        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("ADAttributeEditor_Update", Text(record, "action"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(result.Error, Text(record, "error"));
    }

    // ---- the write is judged by its throw alone (aud-a26g) ------------------------------------

    [Fact]
    public async Task AnErrorTheWriteReportsWithoutThrowing_IsNotTakenForAFailedWrite()
    {
        // The case the removed HadErrors check existed for, and the one it got wrong: the write was
        // applied, and a record no throw carried sat in the stream. Only a thrown error is the write's.
        const string reported = "A non-terminating error reported beside the write.";
        var alex = _h.Ad.AddAlex();
        _h.Ad.SetReportsErrorWithoutThrowing = reported;

        var result = await Save(alex, new() { ["title"] = "Lead Analyst" });

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Error);
        Assert.Equal("title", Assert.Single(result.AppliedChanges!).Name);
        Assert.Equal("Lead Analyst", Assert.Single(Assert.Single(_h.Ad.Sets).Replace).Value);
        Assert.Equal("Lead Analyst", alex.Attributes["title"]);

        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("ADAttributeEditor_Update", Text(record, "action"));
        Assert.Equal("Success", Text(record, "result"));
        Assert.Equal("Analyst", Text(record, "old_title"));
        Assert.Equal("Lead Analyst", Text(record, "new_title"));
        Assert.DoesNotContain(reported, record.GetRawText(), StringComparison.Ordinal);

        Assert.True(Assert.Single(_h.Notices).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWriteTheDirectoryRefuses_IsTheAuditedFailure_ThrownEitherWay(bool nonTerminating)
    {
        // aud-a28c's failed-write record, for a refusal thrown either way: a terminating one, and a
        // non-terminating one that -ErrorAction Stop turns into a throw. With no HadErrors check left,
        // the second is caught only because of Stop - which this pins.
        const string refusal = "Insufficient access rights to perform the operation";
        var alex = _h.Ad.AddAlex();
        _h.Ad.SetFailure = refusal;
        _h.Ad.SetFailureNonTerminating = nonTerminating;

        var result = await Save(alex, new() { ["title"] = "Lead Analyst" });

        Assert.False(result.Success);
        Assert.Contains(refusal, result.Error, StringComparison.Ordinal);
        Assert.Null(result.AppliedChanges);
        Assert.Contains($"Set-ADUser|{alex.Dn}", _h.Ad.Calls);
        Assert.Empty(_h.Ad.Sets);
        Assert.Equal("Analyst", alex.Attributes["title"]);

        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("ADAttributeEditor_Update", Text(record, "action"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(result.Error, Text(record, "error"));
        Assert.Equal(["title"], record.GetProperty("changedAttributes").EnumerateArray().Select(a => a.GetString()));
        Assert.False(record.TryGetProperty("refusedAt", out _));
        Assert.False(record.TryGetProperty("mutationAttempted", out _));

        Assert.False(Assert.Single(_h.Notices).Success);
    }

    [Fact]
    public void TheHarness_SetADUser_ReportsAnErrorWithoutThrowing_AndRefusesWithoutTerminating()
    {
        // The two knobs, so the tests above cannot pass for want of what they describe: an error that
        // is reported and not thrown, and a refusal that only -ErrorAction Stop throws.
        var alex = _h.Ad.AddAlex();
        var replace = new Hashtable { ["title"] = "Lead Analyst" };

        _h.Ad.SetReportsErrorWithoutThrowing = "Reported, not thrown.";
        using (var session = Session())
        {
            SetTitle(session.Shell, alex, replace, stop: true).Invoke();
            Assert.True(session.Shell.HadErrors);
            Assert.Equal("Reported, not thrown.", Assert.Single(session.Shell.Streams.Error).Exception.Message);
            Assert.Equal("Lead Analyst", Assert.Single(_h.Ad.Sets).Replace["title"]);
        }

        _h.Ad.SetReportsErrorWithoutThrowing = null;
        _h.Ad.SetFailure = "Insufficient access rights to perform the operation";
        _h.Ad.SetFailureNonTerminating = true;
        using (var session = Session())
        {
            Assert.ThrowsAny<RuntimeException>(() => SetTitle(session.Shell, alex, replace, stop: true).Invoke());
            Assert.Single(_h.Ad.Sets);
        }

        using (var session = Session())
        {
            SetTitle(session.Shell, alex, replace, stop: false).Invoke();
            Assert.True(session.Shell.HadErrors);
            Assert.Contains("Insufficient access rights", Assert.Single(session.Shell.Streams.Error).Exception.Message, StringComparison.Ordinal);
            Assert.Single(_h.Ad.Sets);
        }
    }

    private sealed class HarnessSession(Runspace runspace, PowerShell shell) : IDisposable
    {
        public PowerShell Shell { get; } = shell;

        public void Dispose()
        {
            Shell.Dispose();
            runspace.Dispose();
        }
    }

    private HarnessSession Session()
    {
        var runspace = RunspaceFactory.CreateRunspace(_h.Ad.CreateSessionState());
        runspace.Open();
        var shell = PowerShell.Create();
        shell.Runspace = runspace;
        return new HarnessSession(runspace, shell);
    }

    private static PowerShell SetTitle(PowerShell shell, LocalAd.User user, Hashtable replace, bool stop)
    {
        shell.Commands.Clear();
        shell.AddCommand("Set-ADUser").AddParameter("Identity", user.Dn).AddParameter("Replace", replace);
        if (stop)
            shell.AddParameter("ErrorAction", "Stop");
        return shell;
    }

    // ---- the write is done when it reads back as written (aud-a26j) ----------------------------

    /// <summary>
    /// A write that was not confirmed: one failed ADAttributeEditor_Update carrying the message, with
    /// no old/new pair, and nothing the event log would offer to undo.
    /// </summary>
    private void AssertUnconfirmedRecord(JsonElement record, string error, LocalAd.User user)
    {
        Assert.Equal("ADAttributeEditor_Update", Text(record, "action"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(error, Text(record, "error"));
        Assert.Equal(user.Guid.ToString(), Text(record, "targetObjectGuid"));
        Assert.Equal(0, record.GetProperty("changedAttributes").GetArrayLength());
        Assert.DoesNotContain(record.EnumerateObject(), p =>
            p.Name.StartsWith("old_", StringComparison.Ordinal) || p.Name.StartsWith("new_", StringComparison.Ordinal));
        Assert.False(record.TryGetProperty("refusedAt", out _));
        Assert.False(record.TryGetProperty("mutationAttempted", out _));

        var logged = record.EnumerateObject()
            .ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        Assert.False(_h.CreateUndoService().CanUndo(logged));
    }

    [Fact]
    public async Task AWriteThatApplies_IsConfirmedByReadingItBack_AndRecordedAsRead()
    {
        // The normal case through the read-back: after the write, by the objectGUID, for exactly what was
        // written; recorded and returned as the directory now holds it.
        var alex = _h.Ad.AddAlex();

        var result = await Save(alex, new() { ["title"] = "Lead Analyst", ["department"] = "" });

        Assert.True(result.Success, result.Error);
        Assert.Equal($"Set-ADUser|{alex.Dn}", _h.Ad.Calls[^2]);
        Assert.Equal($"Get-ADUser|{alex.Guid}|", _h.Ad.Calls[^1]);
        var readBack = _h.Ad.Reads[^1];
        Assert.Equal(alex.Guid.ToString(), readBack.Identity);
        Assert.Equal(["department", "title"], readBack.Properties.Order(StringComparer.Ordinal));

        Assert.Equal(alex.Attributes["title"], result.AppliedChanges!.Single(c => c.Name == "title").NewValue);
        Assert.Null(alex.Attributes["department"]);
        Assert.Null(result.AppliedChanges!.Single(c => c.Name == "department").NewValue);

        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Success", Text(record, "result"));
        Assert.Equal(alex.Attributes["title"], Text(record, "new_title"));
        Assert.Equal("Finance", Text(record, "old_department"));
        Assert.False(record.TryGetProperty("new_department", out _));
        Assert.True(Assert.Single(_h.Notices).Success);
    }

    [Fact]
    public async Task AWriteThatRaisesNothingAndAppliesNothing_IsAFailedSave_ThatOffersNoUndo()
    {
        // The finding: no error, and nothing applied. It was recorded and announced as a success, with the
        // proposed values as new and an Undo offered for a change that never happened.
        var alex = _h.Ad.AddAlex(title: "Analyst");
        _h.Ad.SetChangesNothing = true;

        var result = await Save(alex, new() { ["title"] = "Lead Analyst" });

        Assert.False(result.Success);
        Assert.Null(result.AppliedChanges);
        Assert.Contains("title", result.Error, StringComparison.Ordinal);
        Assert.Contains("'Analyst'", result.Error, StringComparison.Ordinal);
        Assert.Contains("'Lead Analyst'", result.Error, StringComparison.Ordinal);
        // aud-a26l: it names the server the read-back was sent to, and that the value may not have reached it.
        Assert.Contains("example.test", result.Error, StringComparison.Ordinal);
        Assert.Contains("replicated", result.Error, StringComparison.Ordinal);
        Assert.Single(_h.Ad.Sets);
        Assert.Equal("Analyst", alex.Attributes["title"]);

        AssertUnconfirmedRecord(Assert.Single(_h.AuditEvents()), result.Error!, alex);
        var notice = Assert.Single(_h.Notices);
        Assert.False(notice.Success);
        Assert.Equal(result.Error, notice.Details["Error"]);
    }

    [Fact]
    public async Task AReadBackThatFails_IsAFailedSave_ThoughTheWriteWasSent()
    {
        // The write may well have landed - here it did - but a save the directory cannot be asked about is
        // not recorded as done: failed, with nothing for the event log to undo.
        const string unreachable = "The server is not operational.";
        var alex = _h.Ad.AddAlex(title: "Analyst");
        _h.Ad.ReadBackFailure = unreachable;

        var result = await Save(alex, new() { ["title"] = "Lead Analyst" });

        Assert.False(result.Success);
        Assert.Null(result.AppliedChanges);
        Assert.Contains(unreachable, result.Error, StringComparison.Ordinal);
        Assert.Single(_h.Ad.Sets);
        Assert.Equal("Lead Analyst", alex.Attributes["title"]);

        AssertUnconfirmedRecord(Assert.Single(_h.AuditEvents()), result.Error!, alex);
        Assert.False(Assert.Single(_h.Notices).Success);
    }

    [Fact]
    public async Task AServerThatStoresTheValueInAnotherForm_IsNotConfirmed_AndTheRecordCarriesTheValueRead()
    {
        // The directory holds a value, just not the one sent: not confirmed, and the record says what it holds.
        var alex = _h.Ad.AddAlex(title: "Analyst");
        _h.Ad.SetStoresAs = value => value?.ToUpperInvariant();

        var result = await Save(alex, new() { ["title"] = "Lead Analyst" });

        Assert.False(result.Success);
        Assert.Null(result.AppliedChanges);
        Assert.Equal("LEAD ANALYST", alex.Attributes["title"]);
        Assert.Contains("'LEAD ANALYST'", result.Error, StringComparison.Ordinal);
        Assert.Contains("'Lead Analyst'", result.Error, StringComparison.Ordinal);

        AssertUnconfirmedRecord(Assert.Single(_h.AuditEvents()), result.Error!, alex);
        Assert.False(Assert.Single(_h.Notices).Success);
    }

    [Fact]
    public void TheReadBack_IsOrdinal_AnEmptyValueIsAnAbsentOne_AndAOneElementCollectionIsItsElement()
    {
        // A case difference is a difference; a clear read back as "" is confirmed; a collection-shaped
        // answer is its one element; an attribute the object does not hold is not set.
        var read = new PSObject();
        read.Properties.Add(new PSNoteProperty("title", new[] { "Lead Analyst" }));
        read.Properties.Add(new PSNoteProperty("department", ""));
        read.Properties.Add(new PSNoteProperty("company", "contoso"));

        var (unconfirmed, asRead) = ADAttributeEditorService.UnconfirmedWrites(
        [
            new AttributeChange("title", "Job Title", "Analyst", "Lead Analyst"),
            new AttributeChange("department", "Department", "Finance", null),
            new AttributeChange("company", "Company", null, "Contoso"),
            new AttributeChange("extensionAttribute5", "Cost Centre", null, "CC-1"),
        ], read, TypesOf(("title", "String"), ("department", "String"), ("company", "String"), ("extensionAttribute5", "String")));

        Assert.Equal(2, unconfirmed.Count);
        Assert.Contains(unconfirmed, u => u.StartsWith("company", StringComparison.Ordinal)
            && u.Contains("'contoso'", StringComparison.Ordinal) && u.Contains("'Contoso'", StringComparison.Ordinal));
        Assert.Contains(unconfirmed, u => u.StartsWith("extensionAttribute5", StringComparison.Ordinal)
            && u.Contains("'CC-1'", StringComparison.Ordinal) && !u.Contains("is '", StringComparison.Ordinal));
        Assert.Equal("Lead Analyst", asRead.Single(c => c.Name == "title").NewValue);
        Assert.Null(asRead.Single(c => c.Name == "department").NewValue);
        Assert.Equal("contoso", asRead.Single(c => c.Name == "company").NewValue);
        Assert.Null(asRead.Single(c => c.Name == "extensionAttribute5").NewValue);
    }

    [Fact]
    public void TheHarness_SetADUser_CanApplyNothingOrNormalise_AndAReadAfterItCanFail()
    {
        // The three knobs, so the tests above cannot pass for want of what they describe.
        var alex = _h.Ad.AddAlex(title: "Analyst");
        var replace = new Hashtable { ["title"] = "Lead Analyst" };

        _h.Ad.SetChangesNothing = true;
        using (var session = Session())
        {
            SetTitle(session.Shell, alex, replace, stop: true).Invoke();
            Assert.False(session.Shell.HadErrors);
            Assert.Single(_h.Ad.Sets);
            Assert.Equal("Analyst", alex.Attributes["title"]);
        }

        _h.Ad.SetChangesNothing = false;
        _h.Ad.SetStoresAs = value => value?.ToUpperInvariant();
        using (var session = Session())
        {
            SetTitle(session.Shell, alex, replace, stop: true).Invoke();
            Assert.Equal("LEAD ANALYST", alex.Attributes["title"]);
        }

        _h.Ad.SetStoresAs = null;
        _h.Ad.ReadBackFailure = "The server is not operational.";
        using (var session = Session())
        {
            Assert.Single(ReadTitle(session.Shell, alex).Invoke());
            SetTitle(session.Shell, alex, replace, stop: true).Invoke();
            var thrown = Assert.ThrowsAny<RuntimeException>(() => ReadTitle(session.Shell, alex).Invoke());
            Assert.Contains("The server is not operational.", thrown.Message, StringComparison.Ordinal);
        }

        Assert.Equal(["title"], _h.Ad.Reads[^1].Properties);
    }

    private static PowerShell ReadTitle(PowerShell shell, LocalAd.User user, string? server = null)
    {
        shell.Commands.Clear();
        shell.AddCommand("Get-ADUser")
            .AddParameter("Identity", user.Guid.ToString())
            .AddParameter("Properties", new[] { "title" })
            .AddParameter("ErrorAction", "Stop");
        if (server is not null)
            shell.AddParameter("Server", server);
        return shell;
    }

    // ---- a value the directory holds in its own form (aud-a26l) --------------------------------

    private const string Flag = "msExchHideFromAddressLists";
    private const string OldBoss = "CN=Pat Lee,OU=Staff,DC=example,DC=test";
    private const string NewBoss = "CN=Sam Rivera,OU=Staff,DC=example,DC=test";
    private const string NewBossAsTyped = "cn=sam rivera, ou=staff, dc=example, dc=test";

    /// <summary>The allowlist's Type for each attribute, as the read-back is handed it.</summary>
    private static Dictionary<string, string> TypesOf(params (string Name, string Type)[] rows) =>
        rows.ToDictionary(r => r.Name, r => r.Type, StringComparer.OrdinalIgnoreCase);

    /// <summary>The harness's allowlist with a DN-valued attribute and a Boolean one beside it.</summary>
    private void AllowAManagerAndAFlag()
    {
        _h.Allowlist.SaveAllowlist(
        [
            new AttributeRow("title", "Job Title", "String", null, false, true, null, null, 1),
            new AttributeRow("department", "Department", "String", null, false, true, null, null, 1),
            new AttributeRow("manager", "Manager", "String", null, false, true, null, null, 1),
            new AttributeRow(Flag, "Hidden From Address Lists", "String", null, false, true, null, null, 1),
        ]);
        _h.Service.InvalidateAllowlistCache();
    }

    private LocalAd.User AddJordan() =>
        _h.Ad.Add("jordan.blake@example.test", "CN=Jordan Blake,OU=Staff,DC=example,DC=test", "Jordan Blake",
            ("title", "Analyst"), ("manager", OldBoss), (Flag, "False"));

    /// <summary>
    /// The directory's side of a DN-valued write: it resolves the DN written to its object and stores that
    /// object's own DN, whatever case and spacing it was sent in.
    /// </summary>
    private static Func<string?, string?> StoresTheObjectsOwnDn(params string[] objects) =>
        written => written is null
            ? null
            : objects.FirstOrDefault(dn => string.Equals(Squeezed(dn), Squeezed(written), StringComparison.OrdinalIgnoreCase)) ?? written;

    private static string Squeezed(string dn) => System.Text.RegularExpressions.Regex.Replace(dn, @"\s*([,=])\s*", "$1");

    /// <summary>Get-ADUser types the flag as the module types a Boolean-syntax attribute.</summary>
    private static object? TypesTheFlag(string name, string? stored) =>
        string.Equals(name, Flag, StringComparison.OrdinalIgnoreCase) && stored is not null ? bool.Parse(stored) : stored;

    /// <summary>A record as the event log hands it to undo: every property, by a case-insensitive name.</summary>
    private static Dictionary<string, object?> AsTheEventLogReadsIt(JsonElement record) =>
        record.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task AManagerTheDirectoryHoldsInItsOwnForm_IsConfirmed_AndRecordedAsTheDnRead()
    {
        // The finding's DN case: written in lower case with spaces after its commas, held as the object's own DN.
        AllowAManagerAndAFlag();
        var jordan = AddJordan();
        _h.Ad.SetStoresAs = StoresTheObjectsOwnDn(OldBoss, NewBoss);

        var result = await Save(jordan, new() { ["manager"] = NewBossAsTyped });

        Assert.True(result.Success, result.Error);
        Assert.Equal(NewBoss, jordan.Attributes["manager"]);
        var applied = Assert.Single(result.AppliedChanges!);
        Assert.Equal(OldBoss, applied.OldValue);
        Assert.Equal(NewBoss, applied.NewValue);

        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("ADAttributeEditor_Update", Text(record, "action"));
        Assert.Equal("Success", Text(record, "result"));
        Assert.Equal(OldBoss, Text(record, "old_manager"));
        Assert.Equal(NewBoss, Text(record, "new_manager"));
        Assert.True(Assert.Single(_h.Notices).Success);
    }

    [Fact]
    public async Task ABooleanWrittenTrue_ThatTheDirectoryHoldsAsTrue_IsConfirmed_AndRecordedAsTrue()
    {
        AllowAManagerAndAFlag();
        var jordan = AddJordan();
        _h.Ad.SetStoresAs = value => value is null ? null : bool.Parse(value).ToString();
        _h.Ad.ReadsAs = TypesTheFlag;

        var result = await Save(jordan, new() { [Flag] = "true" });

        Assert.True(result.Success, result.Error);
        Assert.Equal("True", jordan.Attributes[Flag]);
        var applied = Assert.Single(result.AppliedChanges!);
        Assert.Equal("False", applied.OldValue);
        Assert.Equal("True", applied.NewValue);

        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Success", Text(record, "result"));
        Assert.Equal("False", Text(record, $"old_{Flag}"));
        Assert.Equal("True", Text(record, $"new_{Flag}"));
        Assert.True(Assert.Single(_h.Notices).Success);
    }

    [Theory]
    [InlineData("manager", NewBossAsTyped, "CN=Someone Else,OU=Staff,DC=example,DC=test")]
    [InlineData(Flag, "true", "False")]
    public async Task AValueTheDirectoryHoldsWithOtherContent_StillFails_WhateverItsSyntax(string attribute, string written, string held)
    {
        // The control: a form is forgiven, a different value is not.
        AllowAManagerAndAFlag();
        var jordan = AddJordan();
        _h.Ad.SetStoresAs = _ => held;
        _h.Ad.ReadsAs = TypesTheFlag;

        var result = await Save(jordan, new() { [attribute] = written });

        Assert.False(result.Success);
        Assert.Null(result.AppliedChanges);
        Assert.Contains($"'{held}'", result.Error, StringComparison.Ordinal);
        Assert.Contains($"'{written}'", result.Error, StringComparison.Ordinal);
        AssertUnconfirmedRecord(Assert.Single(_h.AuditEvents()), result.Error!, jordan);
        Assert.False(Assert.Single(_h.Notices).Success);
    }

    [Fact]
    public async Task TheUndoOfAManagerHeldInTheDirectorysForm_RoundTrips()
    {
        // The record carries the DN as the directory holds it, so the undo's conflict check - the lookup's
        // value against the recorded new one - finds it unchanged, and the old DN goes back.
        AllowAManagerAndAFlag();
        var jordan = AddJordan();
        _h.Ad.SetStoresAs = StoresTheObjectsOwnDn(OldBoss, NewBoss);
        var forward = await Save(jordan, new() { ["manager"] = NewBossAsTyped });
        Assert.True(forward.Success, forward.Error);

        var edit = AsTheEventLogReadsIt(Assert.Single(_h.AuditEvents()));
        edit["_undoMaxLevel"] = 1;
        var undo = _h.CreateUndoService();
        Assert.True(undo.CanUndo(edit));

        var reversed = await undo.ExecuteUndoAsync(edit, Actor, Ip, Ticket, AttributeEditorHarness.Operator());

        Assert.True(reversed.Success, reversed.Error);
        Assert.Equal(OldBoss, jordan.Attributes["manager"]);
        Assert.Equal([NewBossAsTyped, OldBoss], _h.Ad.Sets.Select(s => s.Replace["manager"]));
        var undone = Assert.Single(_h.AuditEvents(), e => Text(e, "action") == "ADAttributeEditor_Undo");
        Assert.Equal("Success", Text(undone, "result"));
    }

    [Fact]
    public async Task AReadBackSentWhereTheWriteWent_IsConfirmed_ThoughAnotherReplicaLags()
    {
        // The folded-in finding: a read sent anywhere but where the write went is answered by a replica that
        // has not seen it. One -Server for the re-read, the write and the read-back - the object's own domain.
        var alex = _h.Ad.AddAlex(title: "Analyst");
        _h.Ad.ReplicaLag = true;

        var result = await Save(alex, new() { ["title"] = "Lead Analyst" });

        Assert.True(result.Success, result.Error);
        Assert.Equal("Lead Analyst", alex.Attributes["title"]);
        Assert.Equal(["example.test", "example.test"], _h.Ad.Reads.Select(r => r.Server));
        Assert.Equal("example.test", Assert.Single(_h.Ad.Sets).Server);
        Assert.Equal("Lead Analyst", Text(Assert.Single(_h.AuditEvents()), "new_title"));
    }

    [Fact]
    public async Task ADnThatNamesNoDomain_GoesToTheDefaultDomainController_AndSaysSo()
    {
        // The explicit fallback: no DC components, so no server to route to - all three calls unrouted, a
        // warning, and an unconfirmed read names the default domain controller as the server asked.
        var robin = _h.Ad.Add("robin.hart@example.test", "CN=Robin Hart,OU=Staff,O=Example", "Robin Hart", ("title", "Analyst"));
        _h.Ad.ReplicaLag = true;

        var result = await Save(robin, new() { ["title"] = "Lead Analyst" });

        Assert.False(result.Success);
        Assert.Equal(2, _h.Ad.Reads.Count);
        Assert.All(_h.Ad.Reads, read => Assert.Null(read.Server));
        Assert.Null(Assert.Single(_h.Ad.Sets).Server);
        Assert.Contains("the default domain controller", result.Error, StringComparison.Ordinal);
        Assert.Contains(_h.Logger.Warnings, w => w.Contains(robin.Upn, StringComparison.Ordinal)
            && w.Contains("default domain controller", StringComparison.Ordinal));
        AssertUnconfirmedRecord(Assert.Single(_h.AuditEvents()), result.Error!, robin);
    }

    [Theory]
    [InlineData(NewBossAsTyped, NewBoss, true)]
    [InlineData("CN = Sam Rivera , OU=Staff,DC=example,DC=test", NewBoss, true)]
    [InlineData(@"CN=Rivera\, Sam,OU=Staff,DC=example,DC=test", @"cn=rivera\, sam,ou=staff,dc=example,dc=test", true)]
    [InlineData("CN=Sam Rivera", NewBoss, false)]
    [InlineData(NewBoss + ",DC=extra", NewBoss, false)]
    [InlineData("CN=Sam  Rivera,OU=Staff,DC=example,DC=test", NewBoss, false)]
    [InlineData(@"CN=Rivera\, Sam,OU=Staff,DC=example,DC=test", "CN=Rivera,CN=Sam,OU=Staff,DC=example,DC=test", false)]
    [InlineData("CN=Someone Else,OU=Staff,DC=example,DC=test", NewBoss, false)]
    public void TwoDns_AreTheSame_OnlyRdnForRdn_IgnoringCaseAndTheSpacesAroundTheSeparators(string written, string read, bool same) =>
        Assert.Equal(same, ADAttributeEditorService.SameDistinguishedName(written, read));

    [Fact]
    public void TheReadBack_ComparesInTheAttributesOwnSyntax_AndNeverLooser()
    {
        var read = new PSObject();
        read.Properties.Add(new PSNoteProperty("manager", NewBoss));
        read.Properties.Add(new PSNoteProperty("assistant", NewBoss));
        read.Properties.Add(new PSNoteProperty("hidden", true));
        read.Properties.Add(new PSNoteProperty("enabled", new object[] { false }));
        read.Properties.Add(new PSNoteProperty("shown", false));
        read.Properties.Add(new PSNoteProperty("count", 7));
        read.Properties.Add(new PSNoteProperty("total", 7L));
        read.Properties.Add(new PSNoteProperty("employeeNumber", "007"));
        read.Properties.Add(new PSNoteProperty("note", "True"));
        read.Properties.Add(new PSNoteProperty("title", "Lead Analyst"));
        read.Properties.Add(new PSNoteProperty("company", "Contoso"));
        read.Properties.Add(new PSNoteProperty("legacy", "CC-1"));

        var (unconfirmed, asRead) = ADAttributeEditorService.UnconfirmedWrites(
        [
            new AttributeChange("manager", "Manager", null, NewBossAsTyped),       // a DN in another form
            new AttributeChange("assistant", "Assistant", null, "CN=Sam Rivera"), // a prefix of the DN held
            new AttributeChange("hidden", "Hidden", null, "TRUE"),                // a Boolean, any case
            new AttributeChange("enabled", "Enabled", null, "false"),             // ... read as a one-element collection
            new AttributeChange("shown", "Shown", null, "yes"),                   // not a Boolean at all
            new AttributeChange("count", "Count", null, "007"),                   // an integer, its own digits
            new AttributeChange("total", "Total", null, "8"),                     // a different integer
            new AttributeChange("employeeNumber", "Employee Number", null, "7"),  // a string that looks like one
            new AttributeChange("note", "Note", null, "true"),                    // a string that looks like a Boolean
            new AttributeChange("title", "Job Title", null, " Lead Analyst "),    // a plain string, trimmed
            new AttributeChange("company", "Company", null, "CONTOSO"),           // ... whose case still counts
            new AttributeChange("legacy", "Legacy", null, "CC-1 "),               // a Type that names neither: untrimmed
        ], read, TypesOf(
            ("manager", "String"), ("assistant", "String"), ("hidden", "String"), ("enabled", "String"),
            ("shown", "String"), ("count", "String"), ("total", "String"), ("employeeNumber", "String"),
            ("note", "String"), ("title", "String"), ("company", "Choice"), ("legacy", "Legacy")));

        string[] differ = ["assistant", "shown", "total", "employeeNumber", "note", "company", "legacy"];
        Assert.Equal(differ, unconfirmed.Select(u => u.Split(' ')[0]));

        // Whatever the verdict, each value is recorded as the directory holds it.
        Assert.Equal(NewBoss, asRead.Single(c => c.Name == "manager").NewValue);
        Assert.Equal("True", asRead.Single(c => c.Name == "hidden").NewValue);
        Assert.Equal("False", asRead.Single(c => c.Name == "enabled").NewValue);
        Assert.Equal("7", asRead.Single(c => c.Name == "count").NewValue);
        Assert.Equal("Lead Analyst", asRead.Single(c => c.Name == "title").NewValue);
    }

    [Fact]
    public void TheHarness_TypesAReadValue_RecordsEachServer_AndLetsAReplicaLagBehindTheWrite()
    {
        // The new knobs, so the tests above cannot pass for want of what they describe.
        var alex = _h.Ad.AddAlex(title: "Analyst");
        var replace = new Hashtable { ["title"] = "Lead Analyst" };

        _h.Ad.ReadsAs = (name, stored) => name == "title" && stored is not null ? stored.Length : stored;
        using (var session = Session())
        {
            var typed = Assert.Single(ReadTitle(session.Shell, alex, server: "example.test").Invoke());
            Assert.Equal("Analyst".Length, typed.Properties["title"].Value);
            Assert.Equal("example.test", _h.Ad.Reads[^1].Server);
        }

        _h.Ad.ReadsAs = null;
        _h.Ad.ReplicaLag = true;
        using (var session = Session())
        {
            SetTitle(session.Shell, alex, replace, stop: true).AddParameter("Server", "example.test").Invoke();
            Assert.Equal("example.test", Assert.Single(_h.Ad.Sets).Server);
            Assert.Equal("Lead Analyst", alex.Attributes["title"]);

            Assert.Equal("Analyst", Assert.Single(ReadTitle(session.Shell, alex).Invoke()).Properties["title"].Value);
            Assert.Equal("Analyst", Assert.Single(ReadTitle(session.Shell, alex, server: "elsewhere.test").Invoke()).Properties["title"].Value);
            Assert.Equal("Lead Analyst", Assert.Single(ReadTitle(session.Shell, alex, server: "example.test").Invoke()).Properties["title"].Value);
            Assert.Null(_h.Ad.Reads[^3].Server);
        }

        using (var session = Session())
        {
            // A new session has sent no write, so nothing it reads lags.
            Assert.Equal("Lead Analyst", Assert.Single(ReadTitle(session.Shell, alex).Invoke()).Properties["title"].Value);
        }
    }

    // ---- an unconfirmed save names what it tried to set (aud-a28o) -----------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnconfirmedSave_RecordsWhatItTriedToSet_NotAsAPairToUndo(bool readBackFails)
    {
        // Not confirmed (nothing applied), or not confirmable (the read-back failed): either way the record has
        // no old/new pair, so it held neither the value before the write nor the one proposed.
        var alex = _h.Ad.AddAlex(title: "Analyst");
        if (readBackFails)
            _h.Ad.ReadBackFailure = "The server is not operational.";
        else
            _h.Ad.SetChangesNothing = true;

        var result = await Save(alex, new() { ["title"] = "Lead Analyst", ["department"] = "" });

        Assert.False(result.Success);
        var record = Assert.Single(_h.AuditEvents());
        AssertUnconfirmedRecord(record, result.Error!, alex);

        var title = record.GetProperty("attempted_title");
        Assert.Equal("Analyst", title.GetProperty("old").GetString());
        Assert.Equal("Lead Analyst", title.GetProperty("proposed").GetString());
        var department = record.GetProperty("attempted_department");
        Assert.Equal("Finance", department.GetProperty("old").GetString());
        Assert.Equal(JsonValueKind.Null, department.GetProperty("proposed").ValueKind);

        // And its trace step lists them, beside an empty list of what was written.
        var step = Assert.Single(_h.TraceEvents(), e => e.TryGetProperty("stage", out var stage) && stage.GetString() == "AttributeWriteCompleted");
        Assert.Equal("Failed", step.GetProperty("result").GetString());
        var details = step.GetProperty("details");
        Assert.Equal(0, details.GetProperty("changedAttributes").GetArrayLength());
        var listed = details.GetProperty("attemptedChanges").EnumerateArray().ToList();
        Assert.Equal(["title", "department"], listed.Select(c => c.GetProperty("name").GetString()));
        Assert.Equal("Analyst", listed[0].GetProperty("oldValue").GetString());
        Assert.Equal("Lead Analyst", listed[0].GetProperty("proposedValue").GetString());
        Assert.Equal("Finance", listed[1].GetProperty("oldValue").GetString());
        Assert.False(listed[1].TryGetProperty("proposedValue", out _));
    }

    // ---- a save that finds nothing to change (aud-a26d) ----------------------------------------

    [Fact]
    public async Task ASaveThatFindsNothingToChange_IsAudited_AndItsNoticeMatchesTheRow()
    {
        // SaveAsync announces every result it returns (aud-a26b), this one included, so a no-op
        // with no record sent administrators a notice nothing in the audit log answered to.
        var alex = _h.Ad.AddAlex(title: "Analyst");

        var result = await Save(alex, new() { ["title"] = "Analyst" });

        Assert.True(result.Success, result.Error);
        Assert.Empty(result.AppliedChanges!);
        Assert.Empty(_h.Ad.Sets);

        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Success", Text(record, "result"));
        Assert.Equal(0, record.GetProperty("changedAttributes").GetArrayLength());
        Assert.False(record.GetProperty("mutationAttempted").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(Text(record, "detail")));
        Assert.False(record.TryGetProperty("refusedAt", out _));
        Assert.DoesNotContain(record.EnumerateObject(), p =>
            p.Name.StartsWith("old_", StringComparison.Ordinal) || p.Name.StartsWith("new_", StringComparison.Ordinal));
        Assert.Equal(alex.Guid.ToString(), Text(record, "targetObjectGuid"));
        Assert.Equal("operator", Text(record, "user"));
        Assert.Equal(Ip, Text(record, "ip"));

        // The notice and the row name the same operation.
        var notice = Assert.Single(_h.Notices);
        Assert.Equal(Text(record, "action"), notice.Action);
        Assert.True(notice.Success);
        Assert.Equal(Text(record, "target"), notice.Details["Target"]);
        Assert.Equal(Text(record, "ticket"), notice.Ticket);
        Assert.Equal(ADAttributeEditorService.NothingNeededChanging, notice.Details["Changed"]);
    }

    [Fact]
    public async Task ARealChange_StillLeavesTheNormalRow()
    {
        // The control: the no-op's markers stay on the no-op. A write after it is recorded as a
        // write - its old and new values, and no claim that nothing was attempted.
        var alex = _h.Ad.AddAlex(title: "Analyst");

        await Save(alex, new() { ["title"] = "Analyst" });
        var changed = await Save(alex, new() { ["title"] = "Lead Analyst" });

        Assert.True(changed.Success, changed.Error);
        Assert.Single(_h.Ad.Sets);
        var row = Assert.Single(_h.AuditEvents(), e => e.GetProperty("changedAttributes").GetArrayLength() > 0);
        Assert.Equal("ADAttributeEditor_Update", Text(row, "action"));
        Assert.Equal("Success", Text(row, "result"));
        Assert.Equal("Analyst", Text(row, "old_title"));
        Assert.Equal("Lead Analyst", Text(row, "new_title"));
        Assert.False(row.TryGetProperty("mutationAttempted", out _));
        Assert.False(row.TryGetProperty("detail", out _));
    }

    // ---- a change is a difference the read-back would see, read as the read-back reads it (aud-a26o) -------

    private const string Phones = "otherTelephone";

    /// <summary>The harness's allowlist with a multi-valued attribute beside the usual two.</summary>
    private void AllowAPhoneList()
    {
        _h.Allowlist.SaveAllowlist(
        [
            new AttributeRow("title", "Job Title", "String", null, false, true, null, null, 1),
            new AttributeRow("department", "Department", "String", null, false, true, null, null, 1),
            new AttributeRow(Phones, "Other Phones", "String", null, false, true, null, null, 1),
        ]);
        _h.Service.InvalidateAllowlistCache();
    }

    /// <summary>Get-ADUser answers the phone list as the module answers a multi-valued attribute: a collection.</summary>
    private static object? ReadsThePhonesAsACollection(string name, string? stored) =>
        string.Equals(name, Phones, StringComparison.OrdinalIgnoreCase) && stored is not null ? stored.Split('|') : stored;

    [Theory]
    [InlineData(Flag, "FALSE")]
    [InlineData("manager", "cn=pat lee, ou=staff, dc=example, dc=test")]
    [InlineData("title", "  Analyst ")]
    public async Task AnEditThatDiffersOnlyInForm_IsNoChange_AndNothingIsWritten(string attribute, string proposed)
    {
        // A Boolean in another case, the DN held in another case and spacing, the string held with spaces round it:
        // each reads back as the value held (aud-a26l), so writing it changes nothing. Compared ordinal it was
        // written, and recorded as a change whose old and new values read the same.
        AllowAManagerAndAFlag();
        var jordan = AddJordan();
        _h.Ad.ReadsAs = TypesTheFlag;

        var result = await Save(jordan, new() { [attribute] = proposed });

        Assert.True(result.Success, result.Error);
        Assert.Empty(result.AppliedChanges!);
        Assert.Empty(_h.Ad.Sets);
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal(0, record.GetProperty("changedAttributes").GetArrayLength());
        Assert.False(record.GetProperty("mutationAttempted").GetBoolean());

        // The preview, asked the same, finds the same.
        var preview = await _h.Service.PreviewAsync(AttributeEditorHarness.PrincipalFor(jordan), new() { [attribute] = proposed });
        Assert.True(preview.Success, preview.Error);
        Assert.Empty(preview.Changes!);
    }

    [Fact]
    public async Task ThePreview_ReReadsThroughTheSessionSeam_FromTheObjectsOwnDomain()
    {
        // The preview's re-read opened its own session, outside the seam the save uses, and sent no -Server, so it
        // could ask another domain controller than the save would (aud-a26l routed the save's chain).
        var alex = _h.Ad.AddAlex(title: "Analyst");

        var preview = await _h.Service.PreviewAsync(AttributeEditorHarness.PrincipalFor(alex), new() { ["title"] = "Lead Analyst" });

        Assert.True(preview.Success, preview.Error);
        var change = Assert.Single(preview.Changes!);
        Assert.Equal("title", change.Name);
        Assert.Equal("Analyst", change.OldValue);
        Assert.Equal("Lead Analyst", change.NewValue);
        Assert.Equal(1, _h.Service.SessionsOpened);
        Assert.Equal(["Import-Module|ActiveDirectory", $"Get-ADUser|{alex.Dn}|"], _h.Ad.Calls);
        Assert.Equal("example.test", Assert.Single(_h.Ad.Reads).Server);
        Assert.Empty(_h.Ad.Sets);
        Assert.Empty(_h.AuditEvents());
    }

    [Fact]
    public async Task ThePreviewOfADnThatNamesNoDomain_GoesToTheDefaultDomainController_AndSaysSo()
    {
        var robin = _h.Ad.Add("robin.hart@example.test", "CN=Robin Hart,OU=Staff,O=Example", "Robin Hart", ("title", "Analyst"));

        var preview = await _h.Service.PreviewAsync(AttributeEditorHarness.PrincipalFor(robin), new() { ["title"] = "Lead Analyst" });

        Assert.True(preview.Success, preview.Error);
        Assert.Null(Assert.Single(_h.Ad.Reads).Server);
        Assert.Contains(_h.Logger.Warnings, w => w.Contains(robin.Upn, StringComparison.Ordinal)
            && w.Contains("default domain controller", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAttributeHoldingSeveralValues_IsRefusedByName_AndNothingIsWritten()
    {
        // Read by ToString the list was its type name, so the save wrote the one value proposed over all of them.
        AllowAPhoneList();
        var alex = _h.Ad.AddAlex();
        alex.Attributes[Phones] = "555-0100|555-0101";
        _h.Ad.ReadsAs = ReadsThePhonesAsACollection;

        var result = await Save(alex, new() { [Phones] = "555-0199" });

        Assert.False(result.Success);
        Assert.Null(result.AppliedChanges);
        Assert.Contains($"'Other Phones' ({Phones}) holds 2 values", result.Error, StringComparison.Ordinal);
        Assert.Contains("Nothing was written.", result.Error, StringComparison.Ordinal);
        Assert.Empty(_h.Ad.Sets);
        Assert.Equal("555-0100|555-0101", alex.Attributes[Phones]);

        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("ADAttributeEditor_Update", Text(record, "action"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(result.Error, Text(record, "error"));
        Assert.False(Assert.Single(_h.Notices).Success);

        // The preview refuses it in the same words.
        var preview = await _h.Service.PreviewAsync(AttributeEditorHarness.PrincipalFor(alex), new() { [Phones] = "555-0199" });
        Assert.False(preview.Success);
        Assert.Equal(result.Error, preview.Error);
    }

    [Fact]
    public async Task AnAttributeReadAsAOneElementCollection_IsItsElement_BeforeTheWriteAsAfterIt()
    {
        // The read-back already took a one-element collection as its element; the re-read before the write took its
        // type name, so the value held could never equal the value proposed, and the record's old value was that name.
        AllowAPhoneList();
        var alex = _h.Ad.AddAlex();
        alex.Attributes[Phones] = "555-0100";
        _h.Ad.ReadsAs = ReadsThePhonesAsACollection;

        var unchanged = await Save(alex, new() { [Phones] = "555-0100" });

        Assert.True(unchanged.Success, unchanged.Error);
        Assert.Empty(unchanged.AppliedChanges!);
        Assert.Empty(_h.Ad.Sets);

        var result = await Save(alex, new() { [Phones] = "555-0199" });

        Assert.True(result.Success, result.Error);
        var applied = Assert.Single(result.AppliedChanges!);
        Assert.Equal("555-0100", applied.OldValue);
        Assert.Equal("555-0199", applied.NewValue);
        Assert.Equal("555-0199", alex.Attributes[Phones]);
        var record = Assert.Single(_h.AuditEvents(), e => e.GetProperty("changedAttributes").GetArrayLength() > 0);
        Assert.Equal("555-0100", Text(record, $"old_{Phones}"));
        Assert.Equal("555-0199", Text(record, $"new_{Phones}"));
    }

    [Fact]
    public async Task ARecordedSideThatIsAJsonNull_ReadsAsNotSet_SoTheUndoClearsTheAttribute()
    {
        // A record whose old side is a JSON null - hand-made, or written by a writer that kept its nulls. GetString
        // read it as the four characters "null", which the preview showed and the undo would have written.
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["category"] = "ADAttributeEditor",
            ["action"] = "ADAttributeEditor_Update",
            ["result"] = "Success",
            ["target"] = alex.Upn,
            ["operationId"] = "op-forward-json-null",
            ["targetObjectGuid"] = alex.Guid.ToString(),
            ["changedAttributes"] = new[] { "title" },
            ["old_title"] = null,
            ["new_title"] = "Lead Analyst",
        }));
        var edit = AsTheEventLogReadsIt(document.RootElement);
        edit["_undoMaxLevel"] = 1;
        var undo = _h.CreateUndoService();
        Assert.True(undo.CanUndo(edit));

        var preview = await undo.PreviewUndoAsync(edit, AttributeEditorHarness.Operator());

        Assert.True(preview.CanUndo, preview.Error);
        Assert.Null(Assert.Single(preview.Changes!).RevertToValue);

        var reversed = await undo.ExecuteUndoAsync(edit, Actor, Ip, Ticket, AttributeEditorHarness.Operator());

        Assert.True(reversed.Success, reversed.Error);
        var write = Assert.Single(_h.Ad.Sets);
        Assert.Empty(write.Replace);
        Assert.Equal(["title"], write.Clear);
        Assert.Null(alex.Attributes["title"]);
    }

    // ---- the lookup reads a value as the save reads it, and a binary value is named as binary (aud-a26r) ----------

    private const string Photo = "thumbnailPhoto";

    private static readonly byte[] PhotoBytes = [.. Enumerable.Range(1, 16).Select(i => (byte)i)];

    /// <summary>The harness's allowlist with a multi-valued attribute and a binary one beside the usual two.</summary>
    private void AllowAPhoneListAndAPhoto()
    {
        _h.Allowlist.SaveAllowlist(
        [
            new AttributeRow("title", "Job Title", "String", null, false, true, null, null, 1),
            new AttributeRow("department", "Department", "String", null, false, true, null, null, 1),
            new AttributeRow(Phones, "Other Phones", "String", null, false, true, null, null, 1),
            new AttributeRow(Photo, "Photo", "String", null, false, true, null, null, 1),
        ]);
        _h.Service.InvalidateAllowlistCache();
    }

    /// <summary>
    /// Get-ADUser answers the photo as the module answers a binary attribute - a byte array, here sixteen bytes - and the
    /// phone list as a collection.
    /// </summary>
    private static object? ReadsThePhotoAsBytes(string name, string? stored) =>
        string.Equals(name, Photo, StringComparison.OrdinalIgnoreCase) && stored is not null
            ? PhotoBytes
            : ReadsThePhonesAsACollection(name, stored);

    [Fact]
    public async Task AnAttributeReadAsAOneElementCollection_IsItsElementInTheLookup_AndItsEditCanBeUndone()
    {
        // The lookup read the collection by ToString: its type's name filled the form, and both undo conflict checks
        // compared that name with the element the save's record holds, so the edit could never be undone.
        AllowAPhoneList();
        var alex = _h.Ad.AddAlex();
        alex.Attributes[Phones] = "555-0199";
        _h.Ad.ReadsAs = ReadsThePhonesAsACollection;

        var lookup = await _h.Service.LookupAsync(alex.Upn);

        Assert.True(lookup.Success, lookup.Error);
        Assert.Equal("555-0199", lookup.CurrentValues![Phones]);

        var edit = AttributeEditorHarness.ForwardEdit(alex, Phones, from: "555-0100", to: "555-0199");
        var undo = _h.CreateUndoService();
        var preview = await undo.PreviewUndoAsync(edit, AttributeEditorHarness.Operator());

        Assert.True(preview.CanUndo, preview.Error);
        Assert.False(preview.HasConflict);
        Assert.Equal("555-0199", Assert.Single(preview.Changes!).CurrentValue);

        var reversed = await undo.ExecuteUndoAsync(edit, Actor, Ip, Ticket, AttributeEditorHarness.Operator());

        Assert.True(reversed.Success, reversed.Error);
        Assert.Equal("555-0100", Assert.Single(_h.Ad.Sets).Replace[Phones]);
        Assert.Equal("555-0100", alex.Attributes[Phones]);
    }

    [Fact]
    public async Task SeveralValuesAndABinaryValue_AreNamedInTheLookup()
    {
        // Not their types' names, and not a binary value's bytes joined as if each were a value.
        AllowAPhoneListAndAPhoto();
        var alex = _h.Ad.AddAlex();
        alex.Attributes[Phones] = "555-0100|555-0101";
        alex.Attributes[Photo] = "a picture";
        _h.Ad.ReadsAs = ReadsThePhotoAsBytes;

        var lookup = await _h.Service.LookupAsync(alex.Upn);

        Assert.True(lookup.Success, lookup.Error);
        Assert.Equal("555-0100; 555-0101", lookup.CurrentValues![Phones]);
        Assert.Equal("binary (16 bytes)", lookup.CurrentValues[Photo]);
        Assert.Equal("Analyst", lookup.CurrentValues["title"]);
    }

    [Fact]
    public async Task ABinaryAttribute_IsRefusedAsBinary_AndNothingIsWritten()
    {
        // A byte array is a collection, so it was refused as holding one value per byte: safe, but the message named
        // sixteen values the attribute does not have. It is one binary value, and the editor writes text only.
        AllowAPhoneListAndAPhoto();
        var alex = _h.Ad.AddAlex();
        alex.Attributes[Photo] = "a picture";
        _h.Ad.ReadsAs = ReadsThePhotoAsBytes;

        var result = await Save(alex, new() { [Photo] = "a caption" });

        Assert.False(result.Success);
        Assert.Null(result.AppliedChanges);
        Assert.Equal(
            $"'Photo' ({Photo}) is binary (16 bytes) in the directory; the attribute editor writes text attributes only. "
            + "Nothing was written.",
            result.Error);
        Assert.Empty(_h.Ad.Sets);
        Assert.Equal("a picture", alex.Attributes[Photo]);

        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("ADAttributeEditor_Update", Text(record, "action"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(result.Error, Text(record, "error"));
        Assert.False(Assert.Single(_h.Notices).Success);

        // The preview refuses it in the same words.
        var preview = await _h.Service.PreviewAsync(AttributeEditorHarness.PrincipalFor(alex), new() { [Photo] = "a caption" });
        Assert.False(preview.Success);
        Assert.Equal(result.Error, preview.Error);

        // Beside an attribute holding several values, each is named under its own rule.
        alex.Attributes[Phones] = "555-0100|555-0101";
        var both = await Save(alex, new() { [Photo] = "a caption", [Phones] = "555-0199" });

        Assert.False(both.Success);
        Assert.Equal(
            $"'Other Phones' ({Phones}) holds 2 values in the directory; the attribute editor writes single-valued attributes only. "
            + $"'Photo' ({Photo}) is binary (16 bytes) in the directory; the attribute editor writes text attributes only. "
            + "Nothing was written.",
            both.Error);
        Assert.Empty(_h.Ad.Sets);
    }
}
