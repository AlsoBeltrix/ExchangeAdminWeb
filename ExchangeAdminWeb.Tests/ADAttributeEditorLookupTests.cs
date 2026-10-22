namespace ExchangeAdminWeb.Tests;

/// <summary>
/// psl-6: with several search bases, one base's failure decided the whole lookup - and only when
/// it was the last base.
/// </summary>
/// <remarks>
/// PerformLookup ran one Get-ADUser per base under SilentlyContinue and read <c>HadErrors</c>
/// once, after the loop. That flag describes only the most recent invocation, so a failure on the
/// LAST base failed a lookup that had already found the user, and a failure on any EARLIER base
/// vanished - a user living there was reported "not found". Driven through
/// <see cref="AttributeEditorHarness"/>; the stand-in Get-ADUser fails per base on the cmdlet's own
/// two paths (see <see cref="LocalAd"/>), so each case runs as both a terminating and a
/// non-terminating error.
/// </remarks>
public sealed class ADAttributeEditorLookupTests : IDisposable
{
    private const string StaffOu = "OU=Staff,DC=example,DC=test";
    private const string ContractorsOu = "OU=Contractors,DC=example,DC=test";

    private readonly AttributeEditorHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Theory]
    [InlineData("terminating")]
    [InlineData("nonterminating")]
    public async Task AUserFoundInOneBase_IsNotLostToALaterBaseFailing(string failure)
    {
        var alex = _h.Ad.AddAlex(ou: StaffOu);
        _h.ConfigureSearchBases(StaffOu, ContractorsOu);
        _h.Ad.FailingBases[ContractorsOu] = failure;

        var result = await _h.Service.LookupAsync(alex.Upn);

        Assert.True(result.Success, result.Error);
        Assert.Equal(alex.Guid.ToString(), result.Principal!.ObjectGuid);
        Assert.Equal("Analyst", result.CurrentValues!["title"]);

        // Success, but not silently: the base that failed is named in the log.
        var warning = Assert.Single(_h.Logger.Warnings);
        Assert.Contains(ContractorsOu, warning, StringComparison.Ordinal);
        Assert.DoesNotContain("OU=Staff", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("terminating")]
    [InlineData("nonterminating")]
    public async Task AFailingBase_IsNotLost_WhenNobodyIsFoundInTheOthers(string failure)
    {
        // The user lives in the base that failed; the last base answered and had nobody. "Not
        // found" would be a wrong answer: the lookup could not be completed, and says where.
        var alex = _h.Ad.AddAlex(ou: StaffOu);
        _h.ConfigureSearchBases(StaffOu, ContractorsOu);
        _h.Ad.FailingBases[StaffOu] = failure;

        var result = await _h.Service.LookupAsync(alex.Upn);

        Assert.False(result.Success);
        Assert.Null(result.Principal);
        Assert.DoesNotContain("not found", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(StaffOu, result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("OU=Contractors", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("terminating")]
    [InlineData("nonterminating")]
    public async Task AUserFoundInALaterBase_IsNotLostToAnEarlierBaseFailing_AndTheFailureIsLogged(string failure)
    {
        // Which base fails must not matter. Before, an earlier failure here was simply forgotten.
        var alex = _h.Ad.AddAlex(ou: ContractorsOu);
        _h.ConfigureSearchBases(StaffOu, ContractorsOu);
        _h.Ad.FailingBases[StaffOu] = failure;

        var result = await _h.Service.LookupAsync(alex.Upn);

        Assert.True(result.Success, result.Error);
        Assert.Equal(alex.Guid.ToString(), result.Principal!.ObjectGuid);
        var warning = Assert.Single(_h.Logger.Warnings);
        Assert.Contains(StaffOu, warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUserReturnedByABaseThatAlsoFailed_IsKept_AndTheBaseIsStillReported()
    {
        // A base can answer and complain in the same call. What it returned is real; that it did
        // not finish cleanly is still logged against it.
        var alex = _h.Ad.AddAlex(ou: StaffOu);
        _h.ConfigureSearchBases(StaffOu, ContractorsOu);
        _h.Ad.FailingBases[StaffOu] = "partial";

        var result = await _h.Service.LookupAsync(alex.Upn);

        Assert.True(result.Success, result.Error);
        Assert.Equal(alex.Guid.ToString(), result.Principal!.ObjectGuid);
        var warning = Assert.Single(_h.Logger.Warnings);
        Assert.Contains(StaffOu, warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnErrorRecordLeftByOneBase_IsJudgedAgainstThatBaseOnly()
    {
        // psl-2..psl-5's class: HadErrors is true while ANY record is retained, so a record that
        // survived into the next base's invocation would mark that base failed too, with the
        // first base's message. Each base's records are cleared before the next base runs.
        var alex = _h.Ad.AddAlex(ou: ContractorsOu);
        _h.ConfigureSearchBases(StaffOu, ContractorsOu);
        _h.Ad.FailingBases[StaffOu] = "recorded";

        var result = await _h.Service.LookupAsync(alex.Upn);

        Assert.True(result.Success, result.Error);
        var warning = Assert.Single(_h.Logger.Warnings);
        Assert.Contains($"search base '{StaffOu}' failed (Access to the search base was denied", warning, StringComparison.Ordinal);
        Assert.DoesNotContain($"search base '{ContractorsOu}'", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryBaseFailing_FailsAndNamesEachBaseWithItsReason()
    {
        var alex = _h.Ad.AddAlex(ou: StaffOu);
        _h.ConfigureSearchBases(StaffOu, ContractorsOu);
        _h.Ad.FailingBases[StaffOu] = "terminating";
        _h.Ad.FailingBases[ContractorsOu] = "nonterminating";

        var result = await _h.Service.LookupAsync(alex.Upn);

        Assert.False(result.Success);
        Assert.Contains(StaffOu, result.Error, StringComparison.Ordinal);
        Assert.Contains(ContractorsOu, result.Error, StringComparison.Ordinal);
        // The detail reaches the operator. Before, the stream was cleared first, so it never did.
        Assert.Contains("The server is not operational", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUndo_StillWorks_WhileTheLastSearchBaseIsDown()
    {
        // The consequence that mattered: undo looks its target up first, so a down last base made
        // every reversal of a user in another base impossible.
        var alex = _h.Ad.AddAlex(title: "Lead Analyst", ou: StaffOu);
        _h.ConfigureSearchBases(StaffOu, ContractorsOu);
        _h.Ad.FailingBases[ContractorsOu] = "terminating";
        var undo = _h.CreateUndoService();

        var result = await undo.ExecuteUndoAsync(
            AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst"),
            "EXAMPLE\\operator", "10.0.0.5", "CHG0004", AttributeEditorHarness.Operator());

        Assert.True(result.Success, result.Error);
        Assert.Equal("Analyst", Assert.Single(_h.Ad.Sets).Replace["title"]);
    }

    // ---- controls: what must not have changed --------------------------------------------------

    [Fact]
    public async Task ACleanLookup_FindsTheUser_AndWarnsAboutNothing()
    {
        var alex = _h.Ad.AddAlex(ou: StaffOu);
        _h.ConfigureSearchBases(StaffOu, ContractorsOu);

        var result = await _h.Service.LookupAsync(alex.Upn);

        Assert.True(result.Success, result.Error);
        Assert.Empty(_h.Logger.Warnings);
    }

    [Fact]
    public async Task NobodyFound_WithNoBaseFailing_IsStillNotFound()
    {
        _h.Ad.AddAlex(ou: StaffOu);
        _h.ConfigureSearchBases(StaffOu, ContractorsOu);

        var result = await _h.Service.LookupAsync("nobody@example.test");

        Assert.False(result.Success);
        Assert.Equal("User 'nobody@example.test' not found.", result.Error);
        Assert.Empty(_h.Logger.Warnings);
    }

    [Fact]
    public async Task EveryBaseIsStillSearchedOnce_InTheConfiguredOrder()
    {
        // A failure does not stop the loop, and nothing reorders it.
        var alex = _h.Ad.AddAlex(ou: StaffOu);
        _h.ConfigureSearchBases(ContractorsOu, StaffOu);
        _h.Ad.FailingBases[ContractorsOu] = "terminating";

        var result = await _h.Service.LookupAsync(alex.Upn);

        Assert.True(result.Success, result.Error);
        Assert.Equal(
            ["Import-Module|ActiveDirectory", $"Get-ADUser||{ContractorsOu}", $"Get-ADUser||{StaffOu}"],
            _h.Ad.Calls);
    }

    [Fact]
    public async Task TwoMatchesAcrossBases_AreStillAmbiguous()
    {
        _h.Ad.AddAlex(ou: StaffOu);
        _h.Ad.Add(new LocalAd.User
        {
            Guid = Guid.NewGuid(),
            Dn = $"CN=Alex Morgan (contract),{ContractorsOu}",
            Upn = "a.morgan.contract@example.test",
            Sam = "amorganc",
            Mail = "alex.morgan@example.test",
            DisplayName = "Alex Morgan (contract)",
        });
        _h.ConfigureSearchBases(StaffOu, ContractorsOu);

        var result = await _h.Service.LookupAsync("alex.morgan@example.test");

        Assert.False(result.Success);
        Assert.StartsWith("Ambiguous:", result.Error, StringComparison.Ordinal);
    }
}
