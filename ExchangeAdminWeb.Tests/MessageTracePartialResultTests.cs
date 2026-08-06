using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// A failed backend must be distinguishable from an empty one
/// (docs/MessageTraceAccuracy-Plan.md slice 2).
/// </summary>
/// <remarks>
/// A trace queries Exchange Online and on-prem together and merges the two result sets. If one
/// backend fails and the other returns rows, the operator sees a plausible table that is missing
/// every message from the failed half - partial success presented as success, Known Failure Class
/// #2. The prod audit log for 2026-07-29 carries repeated "Exchange Online trace failed" entries
/// for real operator searches, so this is not hypothetical.
///
/// The merge itself needs live backends, so what is asserted here is the shape the merge produces
/// and the page renders from.
/// </remarks>
public class MessageTracePartialResultTests
{
    [Fact]
    public void AResponseWithNoFailedBackendsIsNotPartial()
    {
        var response = new MessageTraceResponse();
        response.Results.Add(new MessageTraceResult
        {
            SenderAddress = "a@contoso.com",
            RecipientAddress = "b@contoso.com",
            Subject = "s",
            Status = "Delivered",
            MessageId = "m1"
        });

        Assert.False(response.IsPartial);
    }

    [Fact]
    public void AResponseCarryingAFailedBackendIsPartial()
    {
        var response = new MessageTraceResponse();
        response.FailedBackends.Add("Exchange Online");

        Assert.True(response.IsPartial);
    }

    [Fact]
    public void PartialIsIndependentOfWhetherRowsWereReturned()
    {
        // The dangerous case: rows PRESENT and a backend failed. An empty result at least looks
        // suspicious; a populated one looks finished.
        var response = new MessageTraceResponse();
        response.Results.Add(new MessageTraceResult
        {
            SenderAddress = "a@contoso.com",
            RecipientAddress = "b@contoso.com",
            Subject = "s",
            Status = "Delivered",
            MessageId = "m1"
        });
        response.FailedBackends.Add("Exchange Online");

        Assert.True(response.IsPartial);
        Assert.NotEmpty(response.Results);
    }

    [Fact]
    public void BothBackendsCanFail()
    {
        var response = new MessageTraceResponse();
        response.FailedBackends.Add("Exchange Online");
        response.FailedBackends.Add("On-prem");

        Assert.True(response.IsPartial);
        Assert.Equal(2, response.FailedBackends.Count);
    }
}
