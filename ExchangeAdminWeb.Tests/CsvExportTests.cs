using System.Globalization;
using CsvHelper;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the shared CSV writer's contract (docs/ModuleCsvExport-Plan.md AC1, AC1b):
/// CsvHelper quoting round-trips comma/quote/newline cells, header precedes rows in
/// order, a row/header cell-count mismatch throws, empty rows yield header-only
/// output, and formula-injection neutralization prefixes a leading =, +, -, @, tab,
/// CR, or LF without touching characters elsewhere in the cell.
/// </summary>
public class CsvExportTests
{
    private static string[] Lines(string csv) =>
        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
           .Select(l => l.TrimEnd('\r')).ToArray();

    private static string ParseSingleCell(string csv)
    {
        using var reader = new StringReader(csv);
        using var parser = new CsvReader(reader, CultureInfo.InvariantCulture);
        parser.Read();
        parser.Read();
        return parser.GetField(0)!;
    }

    [Fact]
    public void Write_QuotesCommaQuoteNewlineCells()
    {
        var cell = "a,\"b\nc";

        var csv = CsvExport.Write(["H"], [[cell]]);

        Assert.Equal(cell, ParseSingleCell(csv));
    }

    [Fact]
    public void Write_HeaderThenRowsInOrder()
    {
        var csv = CsvExport.Write(
            ["Col1", "Col2"],
            [
                ["r1c1", "r1c2"],
                ["r2c1", "r2c2"],
            ]);

        var lines = Lines(csv);
        Assert.Equal("Col1,Col2", lines[0]);
        Assert.Equal("r1c1,r1c2", lines[1]);
        Assert.Equal("r2c1,r2c2", lines[2]);
    }

    [Fact]
    public void Write_MismatchedRowThrows()
    {
        Assert.Throws<ArgumentException>(() =>
            CsvExport.Write(["Col1", "Col2"], [["a", "b", "c"]]));
    }

    [Fact]
    public void Write_EmptyRowsYieldsHeaderOnly()
    {
        var csv = CsvExport.Write(["Col1", "Col2"], []);

        var lines = Lines(csv);
        Assert.Single(lines);
        Assert.Equal("Col1,Col2", lines[0]);
    }

    [Fact]
    public void Write_NeutralizesFormulaLeadingCells()
    {
        string[] leadingChars = ["=", "+", "-", "@", "\t", "\r", "\n"];

        foreach (var prefix in leadingChars)
        {
            var cell = prefix + "value";
            var csv = CsvExport.Write(["H"], [[cell]]);

            Assert.Equal("'" + cell, ParseSingleCell(csv));
        }

        var midCell = "value=1+1";
        var untouchedCsv = CsvExport.Write(["H"], [[midCell]]);
        Assert.Equal(midCell, ParseSingleCell(untouchedCsv));
    }
}
