using System.Globalization;
using CsvHelper;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Xunit;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the Defender for Endpoint Devices CSV export (docs/DefenderEndpointDevices-Plan.md, S3
/// and "CSV export"): the projector's column set and order, the quoting and formula-neutralisation
/// contract it inherits from <see cref="CsvExport"/>, the multi-value join, the enrichment columns'
/// refusal to report a half-executed run as a blank cell, and the page's wiring of the button, the
/// audit event and the empty-set guard.
/// </summary>
/// <remarks>
/// Source-text guards for the wiring and for the button's absence from the refusal branch, because
/// there is no bUnit harness in this repo and nothing renders the page. The plan says so in its own
/// Verification section; these assertions are the only evidence for that half and they are honest
/// about being text.
/// </remarks>
public class DefenderEndpointDevicesCsvTests
{
    /// <summary>
    /// The twenty-seven columns of the plan's "CSV export" table, in its order. Written out once
    /// here rather than derived from the code, so a column moved in the page has to be moved here
    /// too by a human who can check it against the plan.
    /// </summary>
    private const string ExpectedHeader =
        "DeviceId,ComputerDnsName,DnsDomain,OnboardingStatus,"
        + "OsPlatform,OsVersion,OsBuild,OsArchitecture,HealthStatus,"
        + "LastIpAddress,LastExternalIpAddress,IpAddresses,MacAddresses,"
        + "FirstSeenUtc,LastSeenUtc,RiskScore,ExposureLevel,DeviceValue,"
        + "MachineTags,MachineGroup,IsAadJoined,AadDeviceId,"
        + "DiscoverySources,DeviceType,DeviceCategory,Vendor,Model";

    private const int ExpectedColumnCount = 27;

    /// <summary>
    /// Parses the CSV the way a spreadsheet does, so an assertion about a cell is an assertion
    /// about what the operator receives rather than about the bytes this code happened to emit.
    /// Returns the header row followed by one string array per data row.
    /// </summary>
    private static List<string[]> Parse(string csv)
    {
        using var reader = new StringReader(csv);
        using var parser = new CsvReader(reader, CultureInfo.InvariantCulture);

        var records = new List<string[]>();
        while (parser.Read())
        {
            var row = new string[parser.Parser.Count];
            for (var i = 0; i < row.Length; i++)
                row[i] = parser.GetField(i) ?? "";
            records.Add(row);
        }

        return records;
    }

    /// <summary>A device with every column populated and nothing needing escaping.</summary>
    private static DefenderDevice FullyPopulatedDevice() => new()
    {
        Id = "aaaa1111bbbb2222cccc3333dddd4444eeee5555",
        ComputerDnsName = "ws-0042.example.test",
        OnboardingStatus = "CanBeOnboarded",
        OsPlatform = "Windows11",
        OsVersion = "24H2",
        OsBuild = "26100",
        OsArchitecture = "64-bit",
        HealthStatus = "Active",
        LastIpAddress = "10.20.30.40",
        LastExternalIpAddress = "203.0.113.7",
        IpAddresses =
        [
            new DefenderDeviceIpAddress
            {
                IpAddress = "10.20.30.40",
                MacAddress = "001122334455",
                OperationalStatus = "Up",
            },
            new DefenderDeviceIpAddress
            {
                IpAddress = "10.20.30.41",
                MacAddress = "66778899AABB",
                OperationalStatus = "Up",
            },
        ],
        FirstSeen = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        LastSeen = new DateTimeOffset(2026, 9, 20, 13, 14, 15, TimeSpan.Zero),
        RiskScore = "Medium",
        ExposureLevel = "Low",
        DeviceValue = "Normal",
        MachineTags = ["Finance", "Pilot ring"],
        MachineGroup = "Corp workstations",
        IsAadJoined = true,
        AadDeviceId = "11111111-2222-3333-4444-555555555555",
        DiscoverySources = "MDE; Microsoft Defender for IoT",
        DeviceType = "Workstation",
        DeviceCategory = "Endpoint",
        Vendor = "Contoso Hardware",
        Model = "Model 9000",
    };

    [Fact]
    public void BuildCsv_HeaderIsThePlansColumnsInThePlansOrder()
    {
        // Cheapest broken implementation that still passes: none of the four. "Delete the feature"
        // and "never call the thing under test" fail because BuildCsv must exist and emit a header.
        // "Change nothing" is the correct code and must pass - this is a drift tripwire for the
        // column contract, which is the one part of a CSV another team's importer is keyed to.
        // "Hard-code one value" would mean returning the literal header and no rows, which the
        // mapping and enrichment tests below catch.
        var csv = DefenderEndpointDevices.BuildCsv([], DefenderDiscoveryEnrichmentState.Succeeded);
        var header = csv.Split('\n')[0].TrimEnd('\r');

        Assert.Equal(ExpectedHeader, header);
        Assert.Equal(ExpectedColumnCount, header.Split(',').Length);
    }

    [Fact]
    public void BuildCsv_MapsEveryColumnOfAFullyPopulatedDevice()
    {
        // Cheapest broken implementation that still passes: none. Every one of the twenty-seven
        // cells is asserted by value, so a column that is emitted blank, in the wrong order, or
        // from the wrong property fails and names itself. "Hard-code one value" cannot survive
        // twenty-seven distinct expected values that are not all equal.
        var csv = DefenderEndpointDevices.BuildCsv(
            [FullyPopulatedDevice()], DefenderDiscoveryEnrichmentState.Succeeded);

        var rows = Parse(csv);
        Assert.Equal(2, rows.Count);
        var row = rows[1];

        Assert.Equal(ExpectedColumnCount, row.Length);
        Assert.Equal("aaaa1111bbbb2222cccc3333dddd4444eeee5555", row[0]);
        Assert.Equal("ws-0042.example.test", row[1]);

        // Derived, never read from the API - the machine resource has no domain property. The DNS
        // suffix is everything after the FIRST dot, which is what makes this a suffix rather than a
        // second label.
        Assert.Equal("example.test", row[2]);

        Assert.Equal("CanBeOnboarded", row[3]);
        Assert.Equal("Windows11", row[4]);
        Assert.Equal("24H2", row[5]);
        Assert.Equal("26100", row[6]);
        Assert.Equal("64-bit", row[7]);
        Assert.Equal("Active", row[8]);
        Assert.Equal("10.20.30.40", row[9]);
        Assert.Equal("203.0.113.7", row[10]);
        Assert.Equal("10.20.30.40; 10.20.30.41", row[11]);
        Assert.Equal("001122334455; 66778899AABB", row[12]);

        // Named ...Utc, so written in UTC and not in the server's zone, and in a sortable form that
        // does not change shape with the host's locale.
        Assert.Equal("2026-01-02 03:04:05Z", row[13]);
        Assert.Equal("2026-09-20 13:14:15Z", row[14]);

        Assert.Equal("Medium", row[15]);
        Assert.Equal("Low", row[16]);
        Assert.Equal("Normal", row[17]);
        Assert.Equal("Finance; Pilot ring", row[18]);
        Assert.Equal("Corp workstations", row[19]);
        Assert.Equal("true", row[20]);
        Assert.Equal("11111111-2222-3333-4444-555555555555", row[21]);
        Assert.Equal("MDE; Microsoft Defender for IoT", row[22]);
        Assert.Equal("Workstation", row[23]);
        Assert.Equal("Endpoint", row[24]);
        Assert.Equal("Contoso Hardware", row[25]);
        Assert.Equal("Model 9000", row[26]);
    }

    [Fact]
    public void BuildCsv_ADeviceNameCarryingACommaAQuoteAndANewlineRoundTrips()
    {
        // The plan names this test. A machine name is not operator-supplied here, but it is
        // attacker-influenced on a device-discovery record: the name arrives from whatever the
        // discovered host reported, so a comma in it must not shift every column to its right in
        // the file someone else imports.
        //
        // Cheapest broken implementation that still passes: none of the four. "Delete the feature"
        // and "never call the thing under test" fail on the parse. "Change nothing" is correct.
        // "Hard-code one value" is the interesting one and is why this asserts the FULL row width
        // as well as the cell: hand-rolled escaping that drops the quoting would split this row into
        // extra fields, and the width assertion is what sees that even if the first cell happened to
        // compare equal.
        var hostile = "ws,001\"quoted\"\nsecond-line.example.test";
        var device = FullyPopulatedDevice();
        device.ComputerDnsName = hostile;

        var csv = DefenderEndpointDevices.BuildCsv(
            [device], DefenderDiscoveryEnrichmentState.Succeeded);

        var rows = Parse(csv);
        Assert.Equal(2, rows.Count);
        Assert.Equal(ExpectedColumnCount, rows[1].Length);
        Assert.Equal(hostile, rows[1][1]);

        // And the derived suffix travels with it rather than being re-derived by the importer from
        // a name it would have to un-escape first.
        Assert.Equal("example.test", rows[1][2]);
    }

    [Fact]
    public void BuildCsv_AValueStartingWithAnEqualsSignIsNeutralised()
    {
        // The plan names this test. The export is a file handed to someone else and opened in a
        // spreadsheet, so a cell beginning = is a formula that runs on their machine, not a string.
        //
        // Cheapest broken implementation that still passes: none of the four. "Change nothing" is
        // correct. "Delete the feature" fails on the parse. "Hard-code one value" fails because the
        // untouched control cell below must NOT be prefixed, so a projector that prefixes
        // everything, or nothing, fails one half or the other. "Never call the thing under test"
        // fails because a hand-rolled writer that bypassed CsvExport would lose the neutralisation
        // and this is the assertion that catches it.
        var device = FullyPopulatedDevice();
        device.ComputerDnsName = "=cmd|'/c calc'!A1";
        device.MachineGroup = "Corp=workstations";

        var csv = DefenderEndpointDevices.BuildCsv(
            [device], DefenderDiscoveryEnrichmentState.Succeeded);

        var rows = Parse(csv);
        Assert.Equal("'=cmd|'/c calc'!A1", rows[1][1]);

        // An = that is not the first character is left alone: neutralising it would corrupt data
        // to no purpose, and a projector that mangles everything is not safer, just wrong.
        Assert.Equal("Corp=workstations", rows[1][19]);
    }

    [Fact]
    public void BuildCsv_MultiValuedCellsJoinWithTheHouseSeparator()
    {
        // The plan names this test, and names "; " as the separator. Asserted on all three
        // multi-valued columns at once because they are three different shapes - a projection out
        // of a nested object, a second projection out of the same nested object, and a plain string
        // list - and only the last of them is the trivial case.
        //
        // Cheapest broken implementation that still passes: none of the four. "Hard-code one value"
        // fails on three columns with three different contents. "Change nothing" is correct.
        // Emitting only the first value of each list - the cheapest real mistake here, and the one
        // an operator would never notice - fails on every one of the three.
        var device = FullyPopulatedDevice();
        device.IpAddresses =
        [
            new DefenderDeviceIpAddress { IpAddress = "10.0.0.1", MacAddress = "AA11" },

            // A blank IP with a MAC, and a MAC-less entry: the two columns are projected
            // independently out of the same collection, so neither may be padded or shortened to
            // match the other.
            new DefenderDeviceIpAddress { IpAddress = "", MacAddress = "BB22" },
            new DefenderDeviceIpAddress { IpAddress = "10.0.0.3", MacAddress = "" },

            // A duplicate of the first entry. Distinct, because one NIC reported twice is not two
            // addresses and a repeated MAC in the file reads as a second machine.
            new DefenderDeviceIpAddress { IpAddress = "10.0.0.1", MacAddress = "AA11" },
        ];
        device.MachineTags = ["Finance", "Pilot ring", "Executive"];

        var csv = DefenderEndpointDevices.BuildCsv(
            [device], DefenderDiscoveryEnrichmentState.Succeeded);

        var rows = Parse(csv);
        Assert.Equal("10.0.0.1; 10.0.0.3", rows[1][11]);
        Assert.Equal("AA11; BB22", rows[1][12]);
        Assert.Equal("Finance; Pilot ring; Executive", rows[1][18]);
    }

    [Fact]
    public void BuildCsv_AMismatchedRowWidthThrowsRatherThanShiftingColumns()
    {
        // The plan names this test. The failure it exists for is a column added to the header and
        // not to the projection: every cell to its right slides one place left, the file still
        // parses, and the importer silently files MAC addresses under FirstSeenUtc.
        //
        // Asserted in two halves because neither half alone is the claim. The first is that the
        // module's own projection is square - the live assertion, which fails the moment the header
        // and the row drift apart, because CsvExport.Write throws on the spot. The second is that
        // the mechanism doing the catching is real: a short row THROWS rather than being padded.
        //
        // Cheapest broken implementation that still passes: none of the four. "Change nothing" is
        // correct. "Delete the feature" fails on the first half. "Never call the thing under test"
        // is precisely what the second half refuses - a projector that hand-rolled its own writer
        // would pass the first half and fail nothing, so the second half pins that the square-ness
        // above is enforced rather than coincidental.
        var csv = DefenderEndpointDevices.BuildCsv(
            [FullyPopulatedDevice(), FullyPopulatedDevice()],
            DefenderDiscoveryEnrichmentState.Succeeded);

        var rows = Parse(csv);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal(ExpectedColumnCount, row.Length));

        var header = rows[0];
        var shortRow = rows[1].Take(ExpectedColumnCount - 1).ToList();
        Assert.Throws<ArgumentException>(() =>
            CsvExport.Write(header, [shortRow]));
    }

    [Theory]
    [InlineData(DefenderDiscoveryEnrichmentState.NotAttempted)]
    [InlineData(DefenderDiscoveryEnrichmentState.Failed)]
    public void BuildCsv_AnEnrichmentThatDidNotSucceedWritesUnavailableInAllFiveColumns(
        DefenderDiscoveryEnrichmentState state)
    {
        // Known Failure Class 2 in .agents/repo-guidance.md, on the artefact that leaves the
        // building. On screen a half-executed run is explained by a warning above the table; the
        // CSV carries no warning, so the cells themselves have to say it. A blank here would be
        // read as "this device has no discovery sources" by everyone who opens the file, and the
        // column senior leadership asked for would be silently empty.
        //
        // Both non-succeeded states are exercised, because they reach this code by different paths
        // - the switch being off, no credentials, no devices and a refused listing all arrive as
        // NotAttempted, while 403, 429, a timeout and a malformed body arrive as Failed - and a
        // projector that handled only one of them would be wrong for half the module's failure
        // doors.
        //
        // Cheapest broken implementation that still passes: none of the four. "Change nothing" is
        // correct. "Hard-code one value" - writing "(unavailable)" unconditionally - fails the
        // succeeded test below. "Delete the feature", dropping the five columns, fails the header
        // test. "Never call the thing under test" fails because the row is built by BuildCsv.
        var device = FullyPopulatedDevice();

        var csv = DefenderEndpointDevices.BuildCsv([device], state);
        var row = Parse(csv)[1];

        var unavailable = DefenderEndpointDeviceService.EnrichmentUnavailable;
        Assert.Equal(unavailable, row[22]);
        Assert.Equal(unavailable, row[23]);
        Assert.Equal(unavailable, row[24]);
        Assert.Equal(unavailable, row[25]);
        Assert.Equal(unavailable, row[26]);

        // And nothing else moves. The listing succeeded; only the enrichment did not, and a
        // projector that blanked or annotated the machines-API columns too would be over-reporting
        // the failure.
        Assert.Equal("ws-0042.example.test", row[1]);
        Assert.Equal("Active", row[8]);
        Assert.Equal("10.20.30.40; 10.20.30.41", row[11]);

        // The same string the page shows, from the same constant, so the file and the screen cannot
        // describe the same run differently.
        Assert.Equal(
            DefenderEndpointDeviceService.DescribeEnrichmentCell(state, device.DiscoverySources),
            row[22]);
    }

    [Fact]
    public void BuildCsv_OnASucceededRunAGenuinelyEmptyEnrichmentValueStaysEmpty()
    {
        // The other half of the distinction, and the reason the state is a parameter at all. Learn
        // flags Vendor and Model as populated "only available if device discovery finds enough
        // information about this attribute", so a blank on a run that WORKED is the truth about that
        // device and must not be dressed up as a failure.
        //
        // Cheapest broken implementation that still passes: none of the four. "Hard-code one value"
        // - always emitting "(unavailable)" - fails here; always emitting the raw value fails the
        // test above. "Change nothing" is correct. The two tests together are what make the
        // parameter load-bearing: neither alone forces the code to read it.
        var device = FullyPopulatedDevice();
        device.Vendor = "";
        device.Model = "   ";

        var csv = DefenderEndpointDevices.BuildCsv(
            [device], DefenderDiscoveryEnrichmentState.Succeeded);
        var row = Parse(csv)[1];

        Assert.Equal("", row[25]);
        Assert.Equal("   ", row[26]);
        Assert.Equal("MDE; Microsoft Defender for IoT", row[22]);
    }

    [Fact]
    public void DefenderEndpointDevices_WiresDownloadCsv()
    {
        // Source-text guard: there is no bUnit harness, so this is the only evidence that the
        // export is wired at all. It checks the five things the plan's S3 bullet names - the
        // projector, the interop, the empty-set guard, the audit action and the audit call - plus
        // the one the bullet is emphatic about: that an audit failure is CAUGHT rather than allowed
        // to fail an export whose bytes have already been served.
        //
        // Cheapest broken implementation that still passes: writing a DownloadCsvAsync that
        // contains these tokens and does nothing. Said plainly rather than papered over - this is
        // text containment and cannot prove behaviour. What it does close is the drift class it was
        // written for: a handler that quietly stops auditing, stops guarding the empty set, or
        // starts letting the audit throw. "Delete the feature" fails on the method lookup;
        // "change nothing" is correct.
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "DefenderEndpointDevices.razor"));

        var start = text.IndexOf("private async Task DownloadCsvAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0, "DownloadCsvAsync method not found in DefenderEndpointDevices.razor.");
        var end = text.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        var body = end > start ? text[start..end] : text[start..];

        Assert.Contains("BuildCsv", body);
        Assert.Contains("downloadFile", body);
        Assert.Contains("if (rows.Count == 0)", body);
        Assert.Contains("\"ExportCsv\"", body);
        Assert.Contains("LogModuleAction", body);

        // SafeAudit is the page's own catch-and-log wrapper. Routing the audit through it is what
        // makes "audit failure caught and logged without failing the export" true here, and a
        // direct Audit.LogModuleAction call inside the try would throw past the operator's file.
        Assert.Contains("SafeAudit(() => Audit.LogModuleAction", body);
    }

    [Fact]
    public void DefenderEndpointDevices_OffersNoExportFromTheRefusalBranch()
    {
        // The module's central promise, asserted on the control rather than on the result type. The
        // result type already guarantees a refusal carries zero devices; that stops the CSV being
        // WRONG. It does not stop the page offering one, and a header-only file handed to someone
        // else is indistinguishable from a tenant with no matching devices - the refusal banner
        // that explained it does not travel with the file.
        //
        // Checked by position, and BRACKETED on both sides rather than only anchored below. There
        // is exactly one Download CSV button, and it sits after the first element of the complete
        // branch and before the device table's own closing tag - a span that is entirely inside
        // that branch. An anchor below the branch alone would also be satisfied by a button placed
        // after the whole if/else chain, where it renders in every state including a refusal.
        //
        // Cheapest broken implementation that still passes: none of the four. "Change nothing" is
        // correct. "Delete the feature" fails on the single-button assertion. "Never call the thing
        // under test" fails the same way. "Hard-code one value" has nothing to hard-code - every
        // offset is read out of the file. Honest limitation: this is textual position in the
        // markup, not a render, so it cannot prove the branch is entered only on Complete. The
        // branch condition itself is source, not this test's to assert.
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "DefenderEndpointDevices.razor"));

        var buttons = text.Split("@onclick=\"DownloadCsvAsync\"").Length - 1;
        Assert.True(buttons == 1,
            $"expected exactly one Download CSV button, found {buttons}. A second one is how an "
            + "export gets offered from a state that has nothing complete to export.");

        var refusalBranch = text.IndexOf(
            "else if (result.Outcome != DefenderDeviceListOutcome.Complete)", StringComparison.Ordinal);
        Assert.True(refusalBranch >= 0,
            "the refusal branch is gone from DefenderEndpointDevices.razor, so nothing here can "
            + "still be saying the export sits outside it.");

        // The enrichment warning is the first thing rendered in the complete branch, and the device
        // table's closing tag is near its end. Both are read out of the file, so if either moves
        // the bracket has to be re-derived by hand rather than drifting.
        var completeBranchOpens = text.IndexOf(
            "@if (result.DiscoveryEnrichment != DefenderDiscoveryEnrichmentState.Succeeded)",
            StringComparison.Ordinal);
        var completeBranchTable = text.IndexOf("</table>", StringComparison.Ordinal);
        Assert.True(completeBranchOpens > refusalBranch && completeBranchTable > completeBranchOpens,
            "the complete branch no longer opens with the enrichment warning and close over the "
            + "device table; re-check by hand where the export button now renders before updating "
            + "this test.");

        var button = text.IndexOf("@onclick=\"DownloadCsvAsync\"", StringComparison.Ordinal);
        Assert.True(button > completeBranchOpens && button < completeBranchTable,
            "the Download CSV button is no longer inside the complete branch. A refusal carries "
            + "zero devices, so an export offered from that state writes a header-only file that "
            + "reads as 'no devices matched' to whoever opens it.");
    }
}
