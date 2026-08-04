using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The bulk-upload row cap, previously duplicated verbatim in the two permission services and
/// unreachable by any test (both sat at 0% coverage).
///
/// This is a safety control: every row is a permission grant against a real mailbox, so the cap
/// bounds the damage one mistaken upload can do.
/// </summary>
public class BulkCsvRowLimitTests
{
    [Fact]
    public void ExactlyTheCapIsAccepted()
    {
        // The boundary. A plain >= here would silently break a working 200-row upload, and the
        // condition is written out in two services - so it is pinned rather than left to whoever
        // reads it next.
        Assert.False(BulkCsvRowLimit.Exceeds(200));
    }

    [Fact]
    public void OneOverTheCapIsRejected()
    {
        Assert.True(BulkCsvRowLimit.Exceeds(201));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(199)]
    public void AnythingUnderTheCapIsAccepted(int rows)
    {
        Assert.False(BulkCsvRowLimit.Exceeds(rows));
    }

    [Fact]
    public void ReadingStopsOnePastTheCap()
    {
        // Reading must continue THROUGH row 200 - stopping at 200 would make a valid 200-row file
        // indistinguishable from an oversized one.
        Assert.False(BulkCsvRowLimit.ShouldStopReading(200));
        Assert.True(BulkCsvRowLimit.ShouldStopReading(201));
    }

    [Fact]
    public void RejectionAppliesNothingAndCountsEveryRowFailed()
    {
        // An oversized file must be refused whole. Partially applying the first 200 grants and
        // reporting a limit error would leave real permissions in place that nobody reviewed.
        var result = BulkCsvRowLimit.Rejected(201);

        Assert.Equal(201, result.TotalRows);
        Assert.Equal(201, result.FailedCount);
        Assert.Equal(0, result.SuccessCount);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void RejectionTellsTheOperatorWhatToDo()
    {
        var result = BulkCsvRowLimit.Rejected(500);

        Assert.Single(result.Errors);
        Assert.Contains("200 row limit", result.Errors[0]);
        Assert.Contains("split", result.Errors[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadCeilingIsOnePastTheCap()
    {
        Assert.Equal(BulkCsvRowLimit.MaxRows + 1, BulkCsvRowLimit.ReadCeiling);
    }
}
