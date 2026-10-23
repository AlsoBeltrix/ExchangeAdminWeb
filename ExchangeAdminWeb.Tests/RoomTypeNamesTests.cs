using System.Text.RegularExpressions;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-28: a room type is read by its name only. Enum.TryParse took a number for any enum and ORed a comma list into one value,
/// so a CSV type of "4" applied CEO - which removes every existing calendar permission - and "9" wrote CustomAttribute9
/// "RoomType:9".
/// </summary>
public sealed class RoomTypeNamesTests
{
    [Theory]
    [InlineData("Standard", RoomType.Standard)]
    [InlineData("Video", RoomType.Video)]
    [InlineData("Restricted", RoomType.Restricted)]
    [InlineData("Exception", RoomType.Exception)]
    [InlineData("CEO", RoomType.CEO)]
    [InlineData("Executive", RoomType.Executive)]
    [InlineData("  Executive  ", RoomType.Executive)]
    public void EachName_IsItsType(string text, RoomType expected)
    {
        Assert.True(RoomTypeNames.TryParse(text, ignoreCase: false, out var type));
        Assert.Equal(expected, type);
    }

    [Fact]
    public void Case_MattersOnlyWhenTheCallerSaysSo()
    {
        Assert.True(RoomTypeNames.TryParse("ceo", ignoreCase: true, out var type));
        Assert.Equal(RoomType.CEO, type);
        Assert.False(RoomTypeNames.TryParse("ceo", ignoreCase: false, out _));
    }

    [Theory]
    [InlineData("4")]
    [InlineData("9")]
    [InlineData(" 2 ")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("Video,Restricted")]
    [InlineData("Video, Restricted")]
    [InlineData("Boardroom")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ANumberACommaListOrAnyOtherText_IsNoType(string? text)
    {
        // Each of the first six is a value Enum.TryParse would have answered; "4" would have been CEO, "Video,Restricted"
        // Exception.
        Assert.False(RoomTypeNames.TryParse(text, ignoreCase: true, out _));
        Assert.False(RoomTypeNames.TryParse(text, ignoreCase: false, out _));
    }

    // ---- the sites ----------------------------------------------------------------------------
    // The page's CSV preview cannot be driven here - HandleTypeCsvUpload calls StateHasChanged, which throws on a component
    // with no renderer - so where the type is parsed is pinned in source, the code view (comments, literals and markup
    // blanked), so neither a comment nor a string can satisfy it.

    [Theory]
    [InlineData("page", "private async Task SetSingleRoomType()", "RoomTypeNames.TryParse(typeName, ignoreCase: false, out var roomType)")]
    [InlineData("page", "private async Task HandleTypeCsvUpload(", "RoomTypeNames.TryParse(row.Type, ignoreCase: true, out var roomType)")]
    [InlineData("processor", "private async Task<RoomOperationResult> ApplyTypeRowAsync(", "RoomTypeNames.TryParse(row.Type, ignoreCase: true, out var roomType)")]
    public void EverySiteThatParsesARoomType_ParsesItByName(string file, string method, string call)
    {
        var body = MethodBody(Code(file), method);

        Assert.Contains(call, body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("page")]
    [InlineData("processor")]
    public void NoRoomTypeIsParsedByEnumTryParse(string file)
    {
        var code = Code(file);

        Assert.DoesNotMatch(new Regex(@"Enum\s*\.\s*(Try)?Parse\s*<\s*RoomType\s*>"), code);
        Assert.Equal(file == "page" ? 2 : 1, Regex.Matches(code, Regex.Escape("RoomTypeNames.TryParse(")).Count);
    }

    private static string Code(string file) => RazorSyntax.CodeView(File.ReadAllText(file == "page"
        ? AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "ConferenceRooms.razor")
        : AuditCategoryFilingTests.FindRepoFile("Services", "Jobs", "ConferenceRoomBulkProcessor.cs")));

    // The method's text, from its signature to the next member at the same indent.
    private static string MethodBody(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} not found - this guard is stale.");
        var end = Regex.Match(code[(start + signature.Length)..], @"\n    (private|public|internal|protected) ");
        Assert.True(end.Success, $"Could not bound {signature} - this guard is stale.");
        return code.Substring(start, signature.Length + end.Index);
    }
}
