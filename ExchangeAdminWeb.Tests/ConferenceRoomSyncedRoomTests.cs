using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Covers the synced-room (DirSync'd / on-prem mastered) Set-Mailbox handling in
/// ConferenceRoomService. See docs/ConferenceRooms-SyncedRoomSetMailbox-Plan.md.
///
/// Two pure decisions are extracted from the EXO-dependent flow so they can be unit-tested:
///  - IsOnPremMasteredWriteError: classify the cloud-write rejection (informational) vs a
///    real failure (visible).
///  - OnPremSkipCountsAsSuccess: a skipped on-prem write is success ONLY when the cloud write
///    already set the attribute; if the cloud write was deferred because the room is on-prem
///    mastered, a skip means the attribute is written nowhere -> must fail (guards the silent
///    success-aggregation class).
/// </summary>
public class ConferenceRoomSyncedRoomTests
{
    [Fact]
    public void IsOnPremMasteredWriteError_WriteScopeMessage_True()
    {
        const string msg = "The operation on mailbox \"e94787fa\" failed because it's out of the current user's write scope. The action 'Set-Mailbox', 'CustomAttribute9', can't be performed on the object.";
        Assert.True(ConferenceRoomService.IsOnPremMasteredWriteError(msg));
    }

    [Fact]
    public void IsOnPremMasteredWriteError_SynchronizedFromOnPremMessage_True()
    {
        const string msg = "...because the object is being synchronized from your on-premises organization. This action should be performed on the object in your on-premises organization.";
        Assert.True(ConferenceRoomService.IsOnPremMasteredWriteError(msg));
    }

    [Theory]
    [InlineData("The term 'Set-Mailbox' is not recognized.")]
    [InlineData("Insufficient permissions to perform the operation.")]
    [InlineData("")]
    [InlineData(null)]
    public void IsOnPremMasteredWriteError_UnrelatedOrEmpty_False(string? msg)
    {
        Assert.False(ConferenceRoomService.IsOnPremMasteredWriteError(msg));
    }

    [Fact]
    public void OnPremSkip_WhenCloudWriteSucceeded_IsSuccess()
    {
        // Cloud write was NOT deferred (room is cloud-mastered) -> on-prem genuinely optional.
        Assert.True(ConferenceRoomService.OnPremSkipCountsAsSuccess(cloudAttrDeferredToOnPrem: false));
    }

    [Fact]
    public void OnPremSkip_WhenCloudWriteDeferred_IsFailure()
    {
        // Cloud write was deferred (synced room) -> skipping on-prem writes the attribute
        // nowhere, so it must NOT count as success.
        Assert.False(ConferenceRoomService.OnPremSkipCountsAsSuccess(cloudAttrDeferredToOnPrem: true));
    }

    [Fact]
    public void PermissionRemovalStep_NoErrors_Succeeds()
    {
        var step = ConferenceRoomService.BuildPermissionRemovalStep([]);

        Assert.True(step.Success);
        Assert.Equal("Remove existing permissions", step.Stage);
    }

    [Fact]
    public void PermissionRemovalStep_AnyError_FailsWithDetail()
    {
        // A CEO conversion must never report success while previous permission
        // holders retain access; every captured removal error fails the step.
        var step = ConferenceRoomService.BuildPermissionRemovalStep(
            ["alice@contoso.com: Access is denied.", "Get-MailboxFolderPermission: timeout"]);

        Assert.False(step.Success);
        Assert.Contains("alice@contoso.com", step.Error);
        Assert.Contains("timeout", step.Error);
    }
}
