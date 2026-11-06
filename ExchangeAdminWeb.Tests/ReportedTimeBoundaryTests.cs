using System.Management.Automation;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Sweep-f item 26. A reported time that the remoting layer hands over as Kind Local names an instant - the deserializer
/// converted an offset-bearing time into this process's zone - so it is read as that instant, Kind Utc, and shown in the
/// operator's zone. Only a time that names no zone (Kind Unspecified) is shown "(time zone not reported)".
/// </summary>
/// <remarks>
/// The values come through PowerShell's own CliXml deserializer, as an Invoke-Command result does, written at offsets that
/// are not this machine's, so the test holds whatever zone the machine running it is in.
/// </remarks>
public sealed class ReportedTimeBoundaryTests
{
    private static readonly TimeZoneInfo Kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    /// <summary>What a remote session's CliXml result carrying <paramref name="serialized"/> deserializes to.</summary>
    private static object Deserialized(string serialized) =>
        PSSerializer.Deserialize($"""<Objs Version="1.1.0.1" xmlns="http://schemas.microsoft.com/powershell/2004/04"><DT>{serialized}</DT></Objs>""");

    [Theory]
    [InlineData("2026-11-02T09:00:00+05:30")]
    [InlineData("2026-11-01T22:30:00-05:00")]
    [InlineData("2026-11-02T04:30:00+01:00")]
    public void ATimeTheDeserializerMadeLocal_IsReadAsTheInstantItNames(string serialized)
    {
        var raw = Assert.IsType<DateTime>(Deserialized(serialized));
        Assert.Equal(DateTimeKind.Local, raw.Kind); // the shape the item names: the remoting layer's Kind

        var read = ReportedTime.Read(raw)!.Time!.Value;

        Assert.Equal(DateTimeKind.Utc, read.Kind);
        Assert.Equal(new DateTime(2026, 11, 2, 3, 30, 0, DateTimeKind.Utc), read);
    }

    [Fact]
    public void ALocalTime_IsShownInTheOperatorsZone_WithNoZoneNotReportedLabel()
    {
        var read = ReportedTime.Read(Deserialized("2026-11-01T22:30:00-05:00"))!;

        Assert.Equal("2026-11-02 09:00 (Asia/Kolkata, UTC+05:30)", OutOfOfficeRequest.ShowReported(read, Kolkata));
    }

    [Fact]
    public void AUtcTime_IsReadAsItIs()
    {
        var read = ReportedTime.Read(Deserialized("2026-11-02T03:30:00Z"))!.Time!.Value;

        Assert.Equal(DateTimeKind.Utc, read.Kind);
        Assert.Equal(new DateTime(2026, 11, 2, 3, 30, 0, DateTimeKind.Utc), read);
    }

    [Fact]
    public void ATimeThatNamesNoZone_IsStillShownSaying_SoNothingIsGuessed()
    {
        var raw = Assert.IsType<DateTime>(Deserialized("2026-11-02T09:00:00"));
        Assert.Equal(DateTimeKind.Unspecified, raw.Kind);

        var read = ReportedTime.Read(raw)!;

        Assert.Equal(DateTimeKind.Unspecified, read.Time!.Value.Kind);
        Assert.Equal("2026-11-02 09:00 (time zone not reported)", OutOfOfficeRequest.ShowReported(read, Kolkata));
    }

    // The Out of Office round trip: the window typed in the browser's zone is written as its UTC instant (aud-a7e), and the
    // same instant read back as Exchange hands it over - at an offset of its own, deserialized Local - shows the clock typed.
    [Fact]
    public void AnAutoReplyWindowTypedInTheBrowsersZone_ReadsBackAsTheSameClock()
    {
        var typed = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Unspecified);

        var written = OutOfOfficeRequest.ToUtc(typed, Kolkata)!.Value;
        Assert.Equal(DateTimeKind.Utc, written.Kind);
        Assert.Equal(new DateTime(2026, 11, 2, 3, 30, 0, DateTimeKind.Utc), written);

        var asReported = new DateTimeOffset(written).ToOffset(TimeSpan.FromHours(-5)).ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
        var readBack = ReportedTime.Read(Deserialized(asReported))!;

        Assert.Equal(written, readBack.Time);
        Assert.Equal("2026-11-02 09:00 (Asia/Kolkata, UTC+05:30)", OutOfOfficeRequest.ShowReported(readBack, Kolkata));
    }

    [Fact]
    public void ALocalTimeHeldByTheApp_NotReadFromAReport_IsStillShownAsNamingNoZone()
    {
        // Of is for defaults and tests, not a reader: the boundary rule is Read's alone.
        var held = ReportedTime.Of(new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Local));

        Assert.EndsWith("(time zone not reported)", OutOfOfficeRequest.ShowReported(held, Kolkata), StringComparison.Ordinal);
    }
}
