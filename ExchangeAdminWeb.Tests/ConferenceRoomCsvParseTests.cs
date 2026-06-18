using System.Text;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Covers the Conference Rooms bulk-CSV parsers. The central regression these
/// guard against: a real Exchange export (Get-Mailbox | Export-Csv) has an
/// <c>Identity</c> column containing a canonical name / DN with no '@'. An
/// earlier version rejected any value without '@', so every export row was
/// silently dropped -> empty preview -> no Apply button (Shaun Hogan's report).
/// The parsers must accept non-SMTP identities, exactly like SetupRoomType.ps1.
/// </summary>
public class ConferenceRoomCsvParseTests
{
    private static Stream ToStream(string csv) => new MemoryStream(Encoding.UTF8.GetBytes(csv));

    // ---- Room Type ---------------------------------------------------------

    [Fact]
    public async Task TypeCsv_DnOnlyIdentity_NoAtSign_IsParsedNotSkipped()
    {
        // Identity is a canonical name (no '@'), no PrimarySmtpAddress column.
        const string csv =
            "Identity,Type,TimeZone\n" +
            "analog.com/Rooms/Conf-A,Standard,Eastern Standard Time\n";

        var results = await ConferenceRoomService.ParseTypeCsvAsync(ToStream(csv));

        var row = Assert.Single(results);
        Assert.False(row.Skipped);
        Assert.Equal("analog.com/Rooms/Conf-A", row.Row!.Email);
        Assert.Equal("Standard", row.Row.Type);
    }

    [Fact]
    public async Task TypeCsv_PrefersPrimarySmtpAddress_OverIdentityDn()
    {
        // Export-style: Identity is a DN, PrimarySmtpAddress is the real SMTP.
        const string csv =
            "Identity,PrimarySmtpAddress,Type,TimeZone\n" +
            "analog.com/Rooms/Conf-A,confa@analog.com,Video,Pacific Standard Time\n";

        var results = await ConferenceRoomService.ParseTypeCsvAsync(ToStream(csv));

        var row = Assert.Single(results);
        Assert.False(row.Skipped);
        Assert.Equal("confa@analog.com", row.Row!.Email);
    }

    [Fact]
    public async Task TypeCsv_AllIdentityColumnsBlank_SkipsWithReasonAndColumns()
    {
        const string csv =
            "Identity,Type,TimeZone\n" +
            ",Standard,Eastern Standard Time\n";

        var results = await ConferenceRoomService.ParseTypeCsvAsync(ToStream(csv));

        var row = Assert.Single(results);
        Assert.True(row.Skipped);
        Assert.Contains("Missing identity", row.SkipReason);
        Assert.Contains("Identity", row.AvailableColumns);
        Assert.Contains("Type", row.AvailableColumns);
    }

    [Fact]
    public async Task TypeCsv_EmptyFile_HeaderOnly_ReturnsNoRows()
    {
        const string csv = "Identity,Type,TimeZone\n";

        var results = await ConferenceRoomService.ParseTypeCsvAsync(ToStream(csv));

        Assert.Empty(results);
    }

    [Fact]
    public async Task TypeCsv_InvalidRemoveExistingPermissions_SkipsWithReason()
    {
        const string csv =
            "Identity,Type,TimeZone,RemoveExistingPermissions\n" +
            "room1@analog.com,Standard,Eastern Standard Time,maybe\n";

        var results = await ConferenceRoomService.ParseTypeCsvAsync(ToStream(csv));

        var row = Assert.Single(results);
        Assert.True(row.Skipped);
        Assert.Contains("RemoveExistingPermissions", row.SkipReason);
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("true", true)]
    [InlineData("FALSE", false)]
    [InlineData("", false)]
    public async Task TypeCsv_RemoveExistingPermissions_ParsesBoolCaseInsensitively(string value, bool expected)
    {
        var csv =
            "Identity,Type,TimeZone,RemoveExistingPermissions\n" +
            $"room1@analog.com,CEO,Eastern Standard Time,{value}\n";

        var results = await ConferenceRoomService.ParseTypeCsvAsync(ToStream(csv));

        var row = Assert.Single(results);
        Assert.False(row.Skipped);
        Assert.Equal(expected, row.Row!.RemoveExistingPermissions);
    }

    [Fact]
    public async Task TypeCsv_BlankSite_DefaultsToNone()
    {
        const string csv =
            "Identity,Type,TimeZone,Site\n" +
            "room1@analog.com,Standard,Eastern Standard Time,\n";

        var results = await ConferenceRoomService.ParseTypeCsvAsync(ToStream(csv));

        var row = Assert.Single(results);
        Assert.Equal("none", row.Row!.Site);
    }

    [Fact]
    public async Task TypeCsv_PreservesRowOrderAndIndexes()
    {
        const string csv =
            "Identity,Type,TimeZone\n" +
            "a@analog.com,Standard,Eastern Standard Time\n" +
            ",Standard,Eastern Standard Time\n" +    // skipped (blank identity)
            "c@analog.com,Video,Pacific Standard Time\n";

        var results = await ConferenceRoomService.ParseTypeCsvAsync(ToStream(csv));

        Assert.Equal(3, results.Count);
        Assert.Equal(1, results[0].RowIndex);
        Assert.Equal(2, results[1].RowIndex);
        Assert.Equal(3, results[2].RowIndex);
        Assert.False(results[0].Skipped);
        Assert.True(results[1].Skipped);
        Assert.False(results[2].Skipped);
        Assert.Equal("c@analog.com", results[2].Row!.Email);
    }

    // ---- Room Finder (parity — works today, hardened against same bug) ------

    [Fact]
    public async Task FinderCsv_DnOnlyIdentity_NoAtSign_IsParsedNotSkipped()
    {
        const string csv =
            "Identity,City,TimeZone\n" +
            "analog.com/Rooms/Conf-A,Boston,Eastern Standard Time\n";

        var results = await ConferenceRoomService.ParseFinderCsvAsync(ToStream(csv));

        var row = Assert.Single(results);
        Assert.False(row.Skipped);
        Assert.Equal("analog.com/Rooms/Conf-A", row.Row!.Email);
        Assert.Equal("Boston", row.Row.City);
    }

    [Fact]
    public async Task FinderCsv_PrefersPrimarySmtpAddress_OverIdentityDn()
    {
        const string csv =
            "Identity,PrimarySmtpAddress,City,TimeZone\n" +
            "analog.com/Rooms/Conf-A,confa@analog.com,Boston,Eastern Standard Time\n";

        var results = await ConferenceRoomService.ParseFinderCsvAsync(ToStream(csv));

        var row = Assert.Single(results);
        Assert.False(row.Skipped);
        Assert.Equal("confa@analog.com", row.Row!.Email);
    }

    [Fact]
    public async Task FinderCsv_DefaultsCapacityToOne_WhenMissingOrInvalid()
    {
        const string csv =
            "PrimarySmtpAddress,Capacity\n" +
            "room1@analog.com,\n" +
            "room2@analog.com,notanumber\n" +
            "room3@analog.com,12\n";

        var results = await ConferenceRoomService.ParseFinderCsvAsync(ToStream(csv));

        Assert.Equal(3, results.Count);
        Assert.Equal(1, results[0].Row!.Capacity);
        Assert.Equal(1, results[1].Row!.Capacity);
        Assert.Equal(12, results[2].Row!.Capacity);
    }

    [Fact]
    public async Task FinderCsv_AllIdentityColumnsBlank_SkipsWithReason()
    {
        const string csv =
            "PrimarySmtpAddress,City\n" +
            ",Boston\n";

        var results = await ConferenceRoomService.ParseFinderCsvAsync(ToStream(csv));

        var row = Assert.Single(results);
        Assert.True(row.Skipped);
        Assert.Contains("Missing identity", row.SkipReason);
    }

    // ---- Preview-row type display -----------------------------------------

    [Fact]
    public void RoomTypePreviewRow_DefaultType_IsNull_NotPhantomStandard()
    {
        // A preview row built for a parse/validation failure leaves Type unset.
        // It must read as "no type" (null), not the enum's first value (Standard),
        // so failed rows render "—" instead of a misleading "Standard" while the
        // Status column reports the type as empty/invalid.
        var row = new RoomTypePreviewRow();

        Assert.Null(row.Type);
    }
}
