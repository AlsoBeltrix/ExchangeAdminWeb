using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Services;

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

        // The stand-in took the import, so nothing real was loaded; then one re-read BY DN and one
        // write, carrying the replace and the clear together.
        Assert.Equal(["Import-Module|ActiveDirectory", $"Get-ADUser|{alex.Dn}|", $"Set-ADUser|{alex.Dn}"], _h.Ad.Calls);
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
}
