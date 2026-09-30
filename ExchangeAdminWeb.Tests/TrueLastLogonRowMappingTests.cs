using System.Management.Automation;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// How one domain controller's raw row becomes an answer or a skip
/// (docs/TrueLastLogon-Plan.md S1).
/// </summary>
/// <remarks>
/// The sweep itself needs a domain and cannot be unit-tested here. This maps the part that
/// can be: turning a PowerShell row into a <see cref="DomainControllerLogon"/>, where the
/// distinction between "this DC saw no logon" and "this DC did not answer" is decided. Get
/// that wrong and a broken DC votes for dormancy.
/// </remarks>
public class TrueLastLogonRowMappingTests
{
    private static PSObject Row(string? dc, string? lastLogon, string? error)
    {
        var o = new PSObject();
        o.Properties.Add(new PSNoteProperty("DomainController", dc));
        o.Properties.Add(new PSNoteProperty("LastLogon", lastLogon));
        o.Properties.Add(new PSNoteProperty("Error", error));
        return o;
    }

    [Fact]
    public void ADateComesBackAsUtc()
    {
        var row = TrueLastLogonService.MapRow(Row("dc-a", "2026-09-28T17:30:00.0000000Z", null));

        Assert.NotNull(row);
        Assert.Equal("dc-a", row!.DomainController);
        Assert.Equal(new DateTime(2026, 9, 28, 17, 30, 0, DateTimeKind.Utc), row.LastLogon);
        Assert.Null(row.Error);
    }

    [Fact]
    public void ADomainControllerThatSawNothingIsAnAnswerAndNotAnError()
    {
        // lastLogon of 0 means this DC has never handled a logon for the user. That is a real
        // answer and must count toward coverage - treating it as an error would shrink the
        // answering set and make a genuinely dormant account look unverifiable.
        var row = TrueLastLogonService.MapRow(Row("dc-a", null, null));

        Assert.NotNull(row);
        Assert.Null(row!.LastLogon);
        Assert.Null(row.Error);
    }

    [Fact]
    public void AnUnreadableDateIsASkipAndNeverAQuietNull()
    {
        // The direction that matters. A value that will not parse means this DC did not answer
        // usefully; mapping it to "no logon recorded" would let a malfunctioning DC cast a vote
        // for dormancy, which is the reading that gets a live account disabled.
        var row = TrueLastLogonService.MapRow(Row("dc-a", "not-a-date", null));

        Assert.NotNull(row);
        Assert.Null(row!.LastLogon);
        Assert.NotNull(row.Error);
        Assert.Contains("not-a-date", row.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnErrorRowKeepsItsReason()
    {
        var row = TrueLastLogonService.MapRow(Row("dc-a", null, "No answer on TCP 389 within 2s"));

        Assert.NotNull(row);
        Assert.Equal("No answer on TCP 389 within 2s", row!.Error);
        Assert.Null(row.LastLogon);
    }

    [Fact]
    public void ARowWithNoDomainControllerIsDropped()
    {
        // Nothing can be said about an answer that does not name who gave it, and inventing a
        // name would corrupt the skipped list.
        Assert.Null(TrueLastLogonService.MapRow(Row(null, "2026-09-28T17:30:00.0000000Z", null)));
        Assert.Null(TrueLastLogonService.MapRow(Row("   ", null, null)));
    }
}
