using ExchangeAdminWeb.Services.Jobs;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Job kind labelling (docs/ConferenceRoomsBulkJobPanel-Plan.md F2).
/// </summary>
public class ConferenceRoomJobPayloadTests
{
    [Fact]
    public void KindLabel_NamesTheFinderJob()
    {
        Assert.Equal("Room Finder (bulk)",
            ConferenceRoomJobPayload.KindLabel(ConferenceRoomJobPayload.FinderJobType));
    }

    [Fact]
    public void KindLabel_NamesTheTypeJob()
    {
        Assert.Equal("Room Type (bulk)",
            ConferenceRoomJobPayload.KindLabel(ConferenceRoomJobPayload.TypeJobType));
    }

    [Fact]
    public void KindLabel_ReturnsTheRawTypeForAnUnknownKind()
    {
        // The defect this replaces: a two-way ternary meant anything that was not the Finder type
        // rendered as "Room Type (bulk)". A MessageTrace_DetailExport therefore appeared on the
        // Conference Rooms page as a plausible Conference Rooms job, which is what hid the
        // cross-module leak. An unrecognised type must look unrecognised.
        Assert.Equal("MessageTrace_DetailExport",
            ConferenceRoomJobPayload.KindLabel("MessageTrace_DetailExport"));
    }

    [Fact]
    public void KindLabel_DoesNotFoldAnUnknownKindIntoAKnownOne()
    {
        var label = ConferenceRoomJobPayload.KindLabel("SomeFutureJob_Bulk");

        Assert.NotEqual("Room Type (bulk)", label);
        Assert.NotEqual("Room Finder (bulk)", label);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void KindLabel_PassesThroughEmptyInputRatherThanInventingAKind(string jobType)
    {
        Assert.Equal(jobType, ConferenceRoomJobPayload.KindLabel(jobType));
    }
}
