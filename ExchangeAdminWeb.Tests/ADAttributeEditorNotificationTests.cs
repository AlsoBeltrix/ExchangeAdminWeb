using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a26b: an attribute save announces its own result, so no caller has to remember to.
/// </summary>
/// <remarks>
/// Before aud-a26b the forward-edit PAGE sent these notices around its own call to SaveAsync, so
/// a save made from anywhere else was silent - which is exactly how undo shipped (aud-a26).
/// Behavioural, through <see cref="AttributeEditorHarness"/>: the real save and undo paths run
/// against local stand-ins for the AD cmdlets and the notice is observed on a substituted
/// EmailService. Nothing here renders the page; the one page test is a source scan, and says so.
/// </remarks>
public sealed class ADAttributeEditorNotificationTests : IDisposable
{
    private readonly AttributeEditorHarness _h = new();

    public void Dispose() => _h.Dispose();

    private Task<AttributeSaveResult> SaveTitle(LocalAd.User user, string title, string ticket = "CHG0001") =>
        _h.Service.SaveAsync(
            AttributeEditorHarness.PrincipalFor(user),
            new Dictionary<string, string?> { ["title"] = title },
            "EXAMPLE\\operator", "10.0.0.5", ticket, AttributeEditorHarness.Operator());

    [Fact]
    public async Task ASave_AnnouncesItself_WithNoPageInvolved()
    {
        var alex = _h.Ad.AddAlex();

        var result = await SaveTitle(alex, "Lead Analyst");

        Assert.True(result.Success, result.Error);
        var notice = Assert.Single(_h.Notices);
        Assert.Equal("ADAttributeEditor_Update", notice.Action);
        Assert.True(notice.Success);
        Assert.Equal("EXAMPLE\\operator", notice.PerformedBy);
        Assert.Equal("10.0.0.5", notice.Ip);
        Assert.Equal("CHG0001", notice.Ticket);
        Assert.Equal(alex.Upn, notice.Details["Target"]);
        Assert.Equal("title", notice.Details["Changed"]);
        Assert.False(notice.Details.ContainsKey("Error"));
        Assert.Null(notice.Error);
    }

    [Fact]
    public async Task ARefusedSave_AnnouncesTheRefusal()
    {
        // A refusal used to be announced by the page's failure branch. It still has to be: an
        // attempt on a protected account is the notice administrators most need.
        var alex = _h.Ad.AddAlex();
        _h.Protection.Decide = _ => ProtectedPrincipalResult.Protected("protected for test", "User: alex.morgan@example.test");

        var result = await SaveTitle(alex, "Lead Analyst");

        Assert.False(result.Success);
        var notice = Assert.Single(_h.Notices);
        Assert.Equal("ADAttributeEditor_Update", notice.Action);
        Assert.False(notice.Success);
        Assert.Equal(alex.Upn, notice.Details["Target"]);
        Assert.Equal(result.Error, notice.Details["Error"]);
        Assert.Equal(result.Error, notice.Error);
    }

    [Fact]
    public async Task AFailedWrite_AnnouncesTheFailure()
    {
        var alex = _h.Ad.AddAlex();
        _h.Ad.SetFailure = "Insufficient access rights to perform the operation";

        var result = await SaveTitle(alex, "Lead Analyst");

        Assert.False(result.Success);
        var notice = Assert.Single(_h.Notices);
        Assert.Equal("ADAttributeEditor_Update", notice.Action);
        Assert.False(notice.Success);
        Assert.Equal("Insufficient access rights to perform the operation", notice.Details["Error"]);
        Assert.Equal("Insufficient access rights to perform the operation", notice.Error);
    }

    [Fact]
    public async Task ASaveThatFindsNothingToChange_SaysSo_InsteadOfNamingAttributes()
    {
        // The page named what it ASKED for, so a save the directory had already satisfied was
        // announced as changing it. The service names what it applied, and here that is nothing.
        var alex = _h.Ad.AddAlex(title: "Analyst");

        var result = await SaveTitle(alex, "Analyst");

        Assert.True(result.Success, result.Error);
        Assert.Empty(_h.Ad.Sets);
        var notice = Assert.Single(_h.Notices);
        Assert.True(notice.Success);
        Assert.Equal(ADAttributeEditorService.NothingNeededChanging, notice.Details["Changed"]);
    }

    [Fact]
    public async Task ANewCaller_IsAnnouncedWithoutAskingForIt()
    {
        // The third caller aud-a26b predicted. It knows nothing about notification and was
        // written after the dispatch moved; every save it makes is announced anyway.
        var alex = _h.Ad.AddAlex();
        var sam = _h.Ad.Add("sam.lee@example.test", "CN=Sam Lee,OU=Staff,DC=example,DC=test", "Sam Lee",
            ("title", "Engineer"), ("department", "Finance"));

        var cleanup = new DepartmentRenameJob(_h.Service);
        var saved = await cleanup.RunAsync([alex, sam], "Finance and Operations");

        Assert.Equal(2, saved);
        Assert.Equal(2, _h.Notices.Count);
        Assert.All(_h.Notices, n =>
        {
            Assert.Equal("ADAttributeEditor_Update", n.Action);
            Assert.True(n.Success);
            Assert.Equal("department", n.Details["Changed"]);
            Assert.Equal("CHG-BULK-7", n.Ticket);
        });
        Assert.Equal([alex.Upn, sam.Upn], _h.Notices.Select(n => n.Details["Target"]));
    }

    [Fact]
    public async Task AnUndo_IsAnnouncedOnce_AsAnUndo()
    {
        // Driven end to end: lookup, identity proof, protection, conflict check, the save. The
        // directory holds the forward edit's new value; the record names the old one.
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");
        var undo = _h.CreateUndoService();

        var result = await undo.ExecuteUndoAsync(
            AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst"),
            "EXAMPLE\\operator", "10.0.0.5", "CHG0002", AttributeEditorHarness.Operator());

        Assert.True(result.Success, result.Error);
        Assert.Equal("Analyst", Assert.Single(_h.Ad.Sets).Replace["title"]);

        // ONE notice, and it is the undo's: not an _Update from the save and an _Undo besides.
        var notice = Assert.Single(_h.Notices);
        Assert.Equal("ADAttributeEditor_Undo", notice.Action);
        Assert.True(notice.Success);
        Assert.Equal("CHG0002", notice.Ticket);
        Assert.Equal(alex.Upn, notice.Details["Target"]);
        Assert.Equal("title", notice.Details["Reverted"]);
        Assert.Equal("op-forward-1", notice.Details["Original operation"]);
        Assert.False(notice.Details.ContainsKey("Error"));
    }

    [Fact]
    public async Task AFailedUndo_IsAnnouncedOnce_AsAFailedUndo()
    {
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");
        _h.Ad.SetFailure = "Insufficient access rights to perform the operation";
        var undo = _h.CreateUndoService();

        var result = await undo.ExecuteUndoAsync(
            AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst"),
            "EXAMPLE\\operator", "10.0.0.5", "CHG0003", AttributeEditorHarness.Operator());

        Assert.False(result.Success);
        var notice = Assert.Single(_h.Notices);
        Assert.Equal("ADAttributeEditor_Undo", notice.Action);
        Assert.False(notice.Success);
        Assert.Equal("Insufficient access rights to perform the operation", notice.Details["Error"]);
        Assert.Equal("Insufficient access rights to perform the operation", notice.Error);
        Assert.Equal("op-forward-1", notice.Details["Original operation"]);
    }

    [Fact]
    public async Task AnUnsendableNotice_DoesNotFailTheSave()
    {
        // The write has happened and the audit record exists; a mail server being down must not
        // turn that into a reported failure.
        var alex = _h.Ad.AddAlex();
        _h.FailSends = true;

        var result = await SaveTitle(alex, "Lead Analyst");

        Assert.True(result.Success, result.Error);
        Assert.Single(_h.Ad.Sets);
        Assert.Single(_h.Notices);
    }

    [Fact]
    public void TheEditorPage_LeavesTheResultNoticeToTheService()
    {
        // Source scan: there is no bUnit harness in this repo. The removal is the risky half of
        // aud-a26b - a dispatch left in the page is a DUPLICATE notice on every save - so the
        // page's code may hold exactly one, in ConfirmSave's outer catch, for an exception
        // SaveAsync never returns. Comments are blanked first, so prose cannot satisfy it.
        var page = RazorSyntax.CommentsBlanked(File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "ADAttributeEditor.razor")));
        var body = AuditCategoryFilingTests.MethodBody(page, "private async Task ConfirmSave()");

        var dispatches = Regex.Matches(body, @"Email\.SendAdminNotificationAsync\(").Select(m => m.Index).ToList();
        var outerCatch = body.LastIndexOf("catch (Exception ex)", StringComparison.Ordinal);

        Assert.True(dispatches.Count == 1,
            $"ConfirmSave sends {dispatches.Count} notices; SaveAsync announces every result it returns, "
            + "so only the outer catch may.");
        Assert.True(outerCatch >= 0 && dispatches[0] > outerCatch,
            "the page's one remaining notice must be its outer catch's");
    }

    /// <summary>
    /// A caller that did not exist when the dispatch moved - a bulk department rename - written
    /// the way a new caller would be: it saves and counts, and does nothing about notification.
    /// </summary>
    private sealed class DepartmentRenameJob(ADAttributeEditorService editor)
    {
        public async Task<int> RunAsync(IEnumerable<LocalAd.User> users, string department)
        {
            var saved = 0;
            foreach (var user in users)
            {
                var result = await editor.SaveAsync(
                    AttributeEditorHarness.PrincipalFor(user),
                    new Dictionary<string, string?> { ["department"] = department },
                    "svc-bulk-jobs", "-", "CHG-BULK-7", actingUser: null);
                if (result.Success)
                    saved++;
            }

            return saved;
        }
    }
}
