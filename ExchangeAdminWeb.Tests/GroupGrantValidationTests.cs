using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a1b: a group-SID grant was validated by SHAPE only, so a pasted user SID, or one that
/// resolves to nothing, was saved and authorized nobody while the grant list looked populated.
/// New grants are now confirmed against the directory when the section is saved.
/// </summary>
public class GroupGrantValidationTests
{
    private const string GroupSid = "S-1-5-21-1111111111-2222222222-3333333333-1200";
    private const string UserSid = "S-1-5-21-1111111111-2222222222-3333333333-1101";
    private const string GoneSid = "S-1-5-21-1111111111-2222222222-3333333333-1999";

    private static DirectoryValidationResult Found(string objectType)
        => new(DirectoryLookupOutcome.Found, new ADSearchResult("x", "CN=x,DC=a,DC=example", "x", null, null, objectType));

    private static readonly DirectoryValidationResult NotFound = new(DirectoryLookupOutcome.NotFound, null);
    private static readonly DirectoryValidationResult Unavailable = new(DirectoryLookupOutcome.Unavailable, null);

    [Fact]
    public void AGroupTheDirectoryHolds_MayBeSaved()
    {
        Assert.Null(GroupGrantValidation.Decide(Found("Group")));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("computer")]
    [InlineData("inetOrgPerson")]
    public void AUsersOrComputersSid_IsRefused(string objectClass)
    {
        // The finding's first case: a SID that is perfectly shaped and belongs to a user.
        Assert.Equal(GroupGrantRefusal.NotAGroup, GroupGrantValidation.Decide(Found(objectClass)));
    }

    [Fact]
    public void ASidThatResolvesToNothing_IsRefused()
    {
        // The second: a deleted group's SID from an old ticket.
        Assert.Equal(GroupGrantRefusal.NoSuchObject, GroupGrantValidation.Decide(NotFound));
    }

    [Fact]
    public void ADirectoryThatCannotBeAsked_IsRefused_AsAnOutageNotATypo()
    {
        Assert.Equal(GroupGrantRefusal.DirectoryUnavailable, GroupGrantValidation.Decide(Unavailable));
    }

    [Fact]
    public void Refusals_AskTheDirectoryAboutEachNewGrant_AndReportEveryRefusal()
    {
        var asked = new List<string>();
        var answers = new Dictionary<string, DirectoryValidationResult>
        {
            [GroupSid] = Found("Group"),
            [UserSid] = Found("user"),
            [GoneSid] = NotFound,
        };

        var refusals = GroupGrantValidation.Refusals([GroupSid, UserSid, GoneSid, GroupSid], sid =>
        {
            asked.Add(sid);
            return answers[sid];
        });

        Assert.Equal([GroupSid, UserSid, GoneSid], asked);
        Assert.Equal(
            [(UserSid, GroupGrantRefusal.NotAGroup), (GoneSid, GroupGrantRefusal.NoSuchObject)],
            refusals);
    }

    [Theory]
    [InlineData("Domain Admins")]
    [InlineData("S-1-5-32-544")]
    [InlineData("S-1-1-0")]
    [InlineData("BA")]
    public void AValueThatIsNotAUsableGroupSid_IsRefusedWithoutALookup(string value)
    {
        var asked = 0;

        var refusals = GroupGrantValidation.Refusals([value], _ =>
        {
            asked++;
            return Found("Group");
        });

        Assert.Equal([(value, GroupGrantRefusal.NotAGroupSid)], refusals);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void NoNewGrants_AskNothing()
    {
        Assert.Empty(GroupGrantValidation.Refusals([], _ => throw new InvalidOperationException("must not be asked")));
    }

    [Fact]
    public void TheRefusalNamesEveryRefusedGrant()
    {
        var message = GroupGrantValidation.Message(
            [(UserSid, GroupGrantRefusal.NotAGroup), (GoneSid, GroupGrantRefusal.NoSuchObject)],
            sid => "label-" + sid);

        Assert.Contains("label-" + UserSid, message, StringComparison.Ordinal);
        Assert.Contains("label-" + GoneSid, message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSidFilter_KeysOnObjectSid()
    {
        Assert.Equal($"(objectSid={GroupSid})", ADDirectorySearchService.BuildSidFilter($" {GroupSid} "));
    }

    // ----- Source tripwires: the page and the directory read need a live circuit and a DC -----

    private static string PageCode() => ProgressScan.CodeView(File.ReadAllText(
        AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "ModuleConfig.razor")));

    private static string Body(string code, string signature, string next)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " not found - tripwire is stale.");
        var end = code.IndexOf(next, start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound " + signature + " - update the tripwire.");
        return code[start..end];
    }

    [Fact]
    public void TheAccessSave_ConfirmsNewGrantsBeforeItPersists_AndPersistsTheSnapshot()
    {
        var body = Body(PageCode(), "private async Task SaveSectionAccessAsync()", "private async Task SaveModuleAdminsAsync()");

        var iConfirm = body.IndexOf("var refused = await ConfirmNewGroupGrantsAsync(newGrants);", StringComparison.Ordinal);
        var iRefuse = body.IndexOf("if (refused is not null)", StringComparison.Ordinal);
        // aud-a28k: the save writes only the aliases it edited, decided under the write lock.
        var iPersist = body.IndexOf("SectionAccess.SaveEditedAliases(loaded, edited);", StringComparison.Ordinal);

        Assert.True(iConfirm >= 0, "The access save no longer confirms new grants.");
        Assert.True(iRefuse > iConfirm && iPersist > iRefuse, "The confirmation must refuse BEFORE the store write.");
        Assert.Contains(".ToDictionary(a => a, a => snapshot[a], StringComparer.OrdinalIgnoreCase);", body, StringComparison.Ordinal);
        Assert.DoesNotContain(".ToDictionary(a => a, a => accessState[a], StringComparer.OrdinalIgnoreCase)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheModuleAdminSave_ConfirmsNewGrantsBeforeItPersists_AndPersistsTheSnapshot()
    {
        var body = Body(PageCode(), "private async Task SaveModuleAdminsAsync()", "private async Task<string?> ConfirmNewGroupGrantsAsync(");

        var iConfirm = body.IndexOf("var refused = await ConfirmNewGroupGrantsAsync(", StringComparison.Ordinal);
        var iRefuse = body.IndexOf("if (refused is not null)", StringComparison.Ordinal);
        var iPersist = body.IndexOf("ModuleAdmin.SetModuleAdmins(module.Id, snapshot);", StringComparison.Ordinal);

        Assert.True(iConfirm >= 0, "The module-admin save no longer confirms new grants.");
        Assert.True(iRefuse > iConfirm && iPersist > iRefuse, "The confirmation must refuse BEFORE the store write.");
        Assert.DoesNotContain("SetModuleAdmins(module.Id, moduleAdminGroups", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheConfirmation_AsksTheDirectoryForEachGrant()
    {
        var body = Body(PageCode(), "private async Task<string?> ConfirmNewGroupGrantsAsync(", "private string MaskIfSecret(");

        Assert.Contains("GroupGrantValidation.Refusals(newGrants,", body, StringComparison.Ordinal);
        Assert.Contains("ADSearch.ValidateGroupSid(sid)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSidLookup_NeverFallsBackToTheLocalDomain()
    {
        // The picker's fail-soft local fallback is right for suggestions and wrong here: a foreign
        // domain's group would read as nonexistent. No catalog must be Unavailable.
        var code = ProgressScan.CodeView(File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Services", "ADDirectorySearchService.cs")));
        var body = Body(code, "private DirectoryValidationResult ExecuteValidateGroupSid(", "internal static string BuildSidFilter(");

        var iCatalog = body.IndexOf("var catalog = ResolveGlobalCatalog(runspace);", StringComparison.Ordinal);
        // The ':' is a char literal, which the code view blanks; the code around it survives.
        var iGuard = body.IndexOf("if (catalog is null || catalog.StartsWith(", StringComparison.Ordinal);
        var iUnavailable = body.IndexOf("ClassifyOutcome(ValidationStep.DirectoryUnavailable)", StringComparison.Ordinal);
        var iQuery = body.IndexOf("ps.AddCommand(", StringComparison.Ordinal);

        Assert.True(iCatalog >= 0 && iGuard > iCatalog, "The catalog must be resolved and guarded first.");
        Assert.True(iUnavailable > iGuard && iUnavailable < iQuery, "No catalog must answer Unavailable before any query runs.");
        Assert.Contains(".AddParameter(", body, StringComparison.Ordinal);
        Assert.Contains(", catalog)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOldFalseMessage_IsGone()
    {
        // "typed text is not accepted here" was false: a SID-shaped typed value was accepted.
        var raw = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "ModuleConfig.razor"));

        Assert.DoesNotContain("typed text is not accepted here", raw, StringComparison.Ordinal);
    }
}
