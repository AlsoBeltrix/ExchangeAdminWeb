using System.Management.Automation;
using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Sweep-f item 27. An attribute-editor save is judged done by its read-back, and the read-back no longer rests on
/// unprobed assumptions: the re-read, the write and the read-back are pinned to one domain controller (the DC that
/// answered the domain's RootDSE, discovered at runtime), and each value is compared in the syntax the schema of that
/// DC gives the attribute - a Boolean or an integer by value from whatever CLR type comes back, a string included, a
/// DN-syntax attribute by DN whatever its name. A real difference still fails closed, with no Undo and a failure notice.
/// </summary>
public sealed class ADAttributeEditorPinAndSyntaxTests : IDisposable
{
    private const string Flag = "msExchHideFromAddressLists";
    private const string DisplayType = "msExchRecipientDisplayType";
    private const string Forward = "altRecipient";
    private const string Target = "CN=Sam Rivera,OU=Staff,DC=example,DC=test";
    private const string TargetAsTyped = "cn=sam rivera, ou=staff, dc=example, dc=test";

    private readonly AttributeEditorHarness _h = new();

    public ADAttributeEditorPinAndSyntaxTests()
    {
        _h.Allowlist.SaveAllowlist(
        [
            new AttributeRow("title", "Job Title", "String", null, false, true, null, null, 1),
            new AttributeRow("employeeNumber", "Employee Number", "String", null, false, true, null, null, 1),
            new AttributeRow(Flag, "Hidden From Address Lists", "String", null, false, true, null, null, 1),
            new AttributeRow(DisplayType, "Recipient Display Type", "String", null, false, true, null, null, 1),
            new AttributeRow(Forward, "Forward To", "String", null, false, true, null, null, 1),
        ]);
        _h.Service.InvalidateAllowlistCache();
        _h.Ad.Schema["title"] = "2.5.5.12";
        _h.Ad.Schema["employeeNumber"] = "2.5.5.12";
        _h.Ad.Schema[Flag] = "2.5.5.8";
        _h.Ad.Schema[DisplayType] = "2.5.5.9";
        _h.Ad.Schema[Forward] = "2.5.5.1";
    }

    public void Dispose() => _h.Dispose();

    private LocalAd.User AddJordan() =>
        _h.Ad.Add("jordan.blake@example.test", "CN=Jordan Blake,OU=Staff,DC=example,DC=test", "Jordan Blake",
            ("title", "Analyst"), ("employeeNumber", "007"), (Flag, "FALSE"), (DisplayType, "1073741824"), (Forward, null));

    private Task<AttributeSaveResult> Save(LocalAd.User user, Dictionary<string, string?> values) =>
        _h.Service.SaveAsync(AttributeEditorHarness.PrincipalFor(user), values, "EXAMPLE\\operator", "10.0.0.5", "CHG0100",
            AttributeEditorHarness.Operator(), null);

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    /// <summary>The module hands every value back as its text: a Boolean as "TRUE", an integer as its digits.</summary>
    private static object? AsText(string name, string? stored) => stored is null ? null : stored.ToUpperInvariant();

    // ---- the pin -------------------------------------------------------------------------------

    [Fact]
    public async Task TheChain_IsPinnedToTheDcThatAnsweredTheRootDse_SoALaggingDomainCannotFailALandedSave()
    {
        // A domain name may reach any DC; only the pin keeps the read-back on the DC the write went to.
        var jordan = AddJordan();
        _h.Ad.ReplicaLag = true;
        _h.Ad.DomainNameReachesAnyDc = true;

        var result = await Save(jordan, new() { ["title"] = "Lead Analyst" });

        Assert.True(result.Success, result.Error);
        // rev-d1: a writable DC of the DN's domain is located first, and its own RootDSE read.
        Assert.Equal(["example.test"], _h.Ad.Discoveries);
        Assert.Equal(["dc01.example.test"], _h.Ad.RootDseServers);
        Assert.Equal(["dc01.example.test", "dc01.example.test"], _h.Ad.Reads.Select(r => r.Server));
        Assert.Equal("dc01.example.test", Assert.Single(_h.Ad.Sets).Server);
        Assert.All(_h.Ad.SchemaReads, read => Assert.Equal("dc01.example.test", read.Server));
    }

    [Fact]
    public async Task TheChain_IsPinnedToAWritableDc_NotTheReadOnlyOneTheDomainNameAnswersFrom()
    {
        // rev-d1. The domain's name is answered by a read-only DC: its RootDSE and reads succeed, and every write is refused.
        // The chain goes to the writable DC the locator names instead, and the save lands and is confirmed.
        var jordan = AddJordan();
        _h.Ad.ReadOnlyDcs.Add("rodc01.example.test");
        _h.Ad.RootDseHost = server => server == "example.test" ? "rodc01.example.test" : server;
        _h.Ad.WritableHost = _ => "dc02.example.test";

        var result = await Save(jordan, new() { ["title"] = "Lead Analyst" });

        Assert.True(result.Success, result.Error);
        Assert.Equal(["dc02.example.test"], _h.Ad.RootDseServers);
        Assert.Equal("dc02.example.test", Assert.Single(_h.Ad.Sets).Server);
        Assert.All(_h.Ad.Reads, read => Assert.Equal("dc02.example.test", read.Server));
    }

    [Fact]
    public async Task AReadOnlyDcThatAnswersTheRootDse_IsNeverPinned_WhenNoWritableDcIsDiscovered()
    {
        // The locator could not be asked: the domain's own RootDSE answers from a read-only DC, which is not pinned. The
        // chain stays on the domain name, logged, as when the RootDSE cannot be read (here the stand-in's domain name takes
        // the write); a mismatch on that path still fails closed (ARootDseThatCannotBeRead_...).
        var jordan = AddJordan();
        _h.Ad.ReadOnlyDcs.Add("rodc01.example.test");
        _h.Ad.RootDseHost = server => server == "example.test" ? "rodc01.example.test" : server;
        _h.Ad.DiscoverFailure = "The specified domain either does not exist or could not be contacted";

        var result = await Save(jordan, new() { ["title"] = "Lead Analyst" });

        Assert.Equal(["example.test"], _h.Ad.RootDseServers);
        Assert.NotEqual("rodc01.example.test", Assert.Single(_h.Ad.Sets).Server);
        Assert.Contains(_h.Logger.Warnings, w => w.Contains("is read-only, so it is not pinned", StringComparison.Ordinal));
        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task ARootDseThatCannotBeRead_LeavesTheChainOnTheDomainName_AndAMismatchStillFailsClosed()
    {
        var jordan = AddJordan();
        _h.Ad.ReplicaLag = true;
        _h.Ad.DomainNameReachesAnyDc = true;
        _h.Ad.RootDseFailure = "The server is not operational";

        var result = await Save(jordan, new() { ["title"] = "Lead Analyst" });

        Assert.False(result.Success);
        Assert.Null(result.AppliedChanges);
        Assert.Contains("was not confirmed", result.Error, StringComparison.Ordinal);
        Assert.Contains("replicated", result.Error, StringComparison.Ordinal);
        Assert.Equal("example.test", Assert.Single(_h.Ad.Sets).Server);
        Assert.Contains(_h.Logger.Warnings, w => w.Contains("DC affinity assumed", StringComparison.Ordinal));
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Failed", Text(record, "result"));
        Assert.False(Assert.Single(_h.Notices).Success);
    }

    // ---- the syntax, from the schema -----------------------------------------------------------

    [Theory]
    [InlineData(Flag, "true", "TRUE")]
    [InlineData(DisplayType, "-2147483642", "-2147483642")]
    [InlineData(DisplayType, "6", "6")]
    public async Task ABooleanOrIntegerReadBackAsText_IsConfirmedByValue(string attribute, string written, string heldAsText)
    {
        var jordan = AddJordan();
        _h.Ad.ReadsAs = AsText;

        var result = await Save(jordan, new() { [attribute] = written });

        Assert.True(result.Success, result.Error);
        Assert.Equal(heldAsText, Assert.Single(result.AppliedChanges!).NewValue);
        Assert.Equal("Success", Text(Assert.Single(_h.AuditEvents()), "result"));
        Assert.True(Assert.Single(_h.Notices).Success);
    }

    [Fact]
    public async Task AnIntegerReadBackAsAnInt64_IsConfirmedByValue()
    {
        var jordan = AddJordan();
        _h.Ad.ReadsAs = (name, stored) => name == DisplayType && stored is not null ? long.Parse(stored, System.Globalization.CultureInfo.InvariantCulture) : stored;

        var result = await Save(jordan, new() { [DisplayType] = "7" });

        Assert.True(result.Success, result.Error);
    }

    [Theory]
    [InlineData(Flag, "true", "FALSE")]
    [InlineData(Flag, "true", "YES")]
    [InlineData(DisplayType, "6", "7")]
    [InlineData(DisplayType, "6", "6.0")]
    public async Task AValueThatParsesToAnotherFlagOrNumber_OrNotAtAll_StillFailsClosed(string attribute, string written, string held)
    {
        var jordan = AddJordan();
        _h.Ad.SetStoresAs = _ => held;
        _h.Ad.ReadsAs = AsText;

        var result = await Save(jordan, new() { [attribute] = written });

        Assert.False(result.Success);
        Assert.Null(result.AppliedChanges);
        Assert.Contains($"'{held}'", result.Error, StringComparison.Ordinal);
        Assert.Equal("Failed", Text(Assert.Single(_h.AuditEvents()), "result"));
        Assert.False(Assert.Single(_h.Notices).Success);
    }

    [Fact]
    public async Task ADnSyntaxAttributeOffTheNamedList_IsComparedByDn()
    {
        var jordan = AddJordan();
        _h.Ad.SetStoresAs = written => written is null ? null : Target;

        var result = await Save(jordan, new() { [Forward] = TargetAsTyped });

        Assert.True(result.Success, result.Error);
        Assert.Equal(Target, Assert.Single(result.AppliedChanges!).NewValue);
    }

    [Fact]
    public async Task AStringAttribute_IsNeverParsed_EvenWithTheSchemaRead()
    {
        // "7" written, "007" held: a Unicode-string attribute keeps its digits as text.
        var jordan = AddJordan();
        _h.Ad.SetStoresAs = _ => "007";

        var result = await Save(jordan, new() { ["employeeNumber"] = "7" });

        Assert.False(result.Success);
        Assert.Contains("'007'", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASchemaThatCannotBeRead_FallsBackToTheValuesOwnTypes_LoudNeverFalse()
    {
        // Without the schema a Boolean read back as text is a string, and "TRUE" is not "true": unconfirmed, as before.
        var jordan = AddJordan();
        _h.Ad.ReadsAs = AsText;
        _h.Ad.SchemaFailure = "Access is denied";

        var result = await Save(jordan, new() { [Flag] = "true" });

        Assert.False(result.Success);
        Assert.Contains(_h.Logger.Warnings, w => w.Contains("schema syntax", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSchemaRead_AsksThePinnedDcsSchemaFor_EachProposedAttribute()
    {
        var jordan = AddJordan();

        await Save(jordan, new() { [Flag] = "true", [DisplayType] = "6" });

        var read = Assert.Single(_h.Ad.SchemaReads);
        Assert.Equal(_h.Ad.SchemaContext, read.SearchBase);
        Assert.Contains($"(lDAPDisplayName={Flag})", read.Filter, StringComparison.Ordinal);
        Assert.Contains($"(lDAPDisplayName={DisplayType})", read.Filter, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Boolean", "String", "Boolean")]
    [InlineData("Integer", "String", "Integer")]
    [InlineData("DistinguishedName", "Choice", "DistinguishedName")]
    [InlineData("Ordinal", "String", "PlainString")]
    [InlineData("Ordinal", "Legacy", "Ordinal")]
    public void TheSchemasSyntax_DecidesBeforeTheValuesType(
        string schema, string type, string expected)
    {
        // A System.Boolean read of a string attribute is still a string when the schema says so, and vice versa.
        Assert.Equal(Enum.Parse<ADAttributeEditorService.ReadBackSyntax>(expected),
            ADAttributeEditorService.SyntaxOf(type, "anything", true, Enum.Parse<ADAttributeEditorService.ReadBackSyntax>(schema)));
    }
}
