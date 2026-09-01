using System.Globalization;
using CsvHelper;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Shared CSV writer for module exports (docs/ModuleCsvExport-Plan.md AC1). Uses
/// CsvWriter.WriteField per cell (the EventLogCsvFormatter quoting contract: commas,
/// quotes, and newlines in any cell survive round-tripping) and neutralizes CSV
/// formula injection before quoting: a cell whose first character is =, +, -, @,
/// tab, CR, or LF is prefixed with a single quote (matching
/// MessageTraceDetailReport.CsvEscape). No DI registration - static, like
/// EventLogCsvFormatter.
/// </summary>
public static class CsvExport
{
    public static string Write(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows)
    {
        using var writer = new StringWriter();
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);

        foreach (var cell in header)
            csv.WriteField(NeutralizeFormula(cell));
        csv.NextRecord();

        foreach (var row in rows)
        {
            if (row.Count != header.Count)
                throw new ArgumentException(
                    $"Row has {row.Count} cell(s) but header has {header.Count}.", nameof(rows));

            foreach (var cell in row)
                csv.WriteField(NeutralizeFormula(cell));
            csv.NextRecord();
        }

        csv.Flush();
        return writer.ToString();
    }

    /// <summary>
    /// Prefixes a single quote when the cell's first character could be read as a
    /// spreadsheet formula or control character; leaves the rest of the cell alone.
    /// </summary>
    private static string NeutralizeFormula(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n'
            ? "'" + value
            : value;
    }
}
