using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Services;
using Xunit;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the BitLockerRecovery CSV export (docs/ModuleCsvExport-Plan.md AC2-AC5,
/// AC4b, S5): the projector's column shape including the D1-ruled RecoveryKey
/// column and the Ticket stamp, and the page's wiring of the download button, the
/// distinct bulk-disclosure audit event, and the empty-set guard. Source-text
/// guard for the wiring because there is no bUnit harness in this repo.
/// </summary>
public class BitLockerRecoveryCsvTests
{
    private static BitLockerRecoveryKey MakeKey(
        int rowId = 1,
        string computerName = "PC-01",
        string recoveryPassword = "111111-222222-333333-444444-555555-666666-777777-888888",
        string? keyId = "KEY-1",
        DateTime? createdUtc = null,
        DateTime? lastSeenInAdUtc = null,
        BitLockerRecoveryKeySource resultSource = BitLockerRecoveryKeySource.Archive) =>
        new()
        {
            RowId = rowId,
            ComputerName = computerName,
            RecoveryPassword = recoveryPassword,
            KeyId = keyId,
            CreatedUtc = createdUtc,
            FirstSeenSource = "archive",
            LastSeenInAdUtc = lastSeenInAdUtc,
            ResultSource = resultSource,
        };

    [Fact]
    public void BuildCsv_HeaderMatchesSpec()
    {
        var csv = BitLockerRecovery.BuildCsv([], "INC0001");
        var header = csv.Split('\n')[0].TrimEnd('\r');

        Assert.Equal("Computer,RecoveryKey,Created,KeyId,Source,LastSeenInAd,Ticket", header);
    }

    [Fact]
    public void BuildCsv_MapsARow()
    {
        var created = new DateTime(2026, 1, 2, 10, 30, 0, DateTimeKind.Utc);
        var lastSeen = new DateTime(2026, 1, 3, 11, 45, 0, DateTimeKind.Utc);
        var rows = new List<BitLockerRecoveryKey>
        {
            MakeKey(
                computerName: "PC-42",
                keyId: "KEY-42",
                createdUtc: created,
                lastSeenInAdUtc: lastSeen,
                resultSource: BitLockerRecoveryKeySource.Archive),
        };

        var csv = BitLockerRecovery.BuildCsv(rows, "INC0042");
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).ToArray();

        var expectedCreated = created.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var expectedLastSeen = lastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var key = rows[0];
        Assert.Equal(
            $"PC-42,{key.RecoveryPassword},{expectedCreated},KEY-42,Archive,{expectedLastSeen},INC0042",
            lines[1]);
    }

    [Fact]
    public void BuildCsv_ContainsRecoveryKeyVerbatim()
    {
        const string knownKey = "123456-234567-345678-456789-567890-678901-789012-890123";
        var rows = new List<BitLockerRecoveryKey> { MakeKey(recoveryPassword: knownKey) };

        var csv = BitLockerRecovery.BuildCsv(rows, "INC0099");
        var cells = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1]
            .TrimEnd('\r').Split(',');

        // The RecoveryKey cell is the second column; it must be the key unmodified
        // by the AC1b formula-injection neutralization (no leading single quote) -
        // a recovery password starts with a digit, so that rule must never touch it.
        Assert.Equal(knownKey, cells[1]);
        Assert.DoesNotContain("'" + knownKey, csv);
    }

    [Fact]
    public void BuildCsv_StampsTicketOnEveryRow()
    {
        var rows = new List<BitLockerRecoveryKey>
        {
            MakeKey(rowId: 1, computerName: "PC-A"),
            MakeKey(rowId: 2, computerName: "PC-B"),
        };

        var csv = BitLockerRecovery.BuildCsv(rows, "INC0001");
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r')).ToArray();

        Assert.Equal(3, lines.Length); // header + 2 rows
        Assert.EndsWith(",INC0001", lines[1]);
        Assert.EndsWith(",INC0001", lines[2]);
    }

    [Fact]
    public void BitLockerRecovery_WiresDownloadCsv()
    {
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "BitLockerRecovery.razor"));

        var start = text.IndexOf("private async Task DownloadCsvAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0, "DownloadCsvAsync method not found in BitLockerRecovery.razor.");
        var end = text.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        var body = end > start ? text[start..end] : text[start..];

        Assert.Contains("BuildCsv", body);
        Assert.Contains("downloadFile", body);
        Assert.Contains("if (results.Count == 0)", body);
        Assert.Contains("\"ExportRecoveryKeysCsv\"", body);
        Assert.Contains("LogModuleAction", body);
        Assert.Contains("ticketNumber: searchTicket", body);

        // blr-1: the bulk-disclosure audit must never reference key material.
        Assert.DoesNotContain("RecoveryPassword", body);
    }
}
