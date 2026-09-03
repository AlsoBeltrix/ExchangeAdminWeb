using System.Text.RegularExpressions;
using Xunit;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/GroupBulkActions-Plan.md source guards (section 8). There is no bUnit harness, so the
/// page wiring the plan's acceptance criteria depend on is pinned by reading the page text:
/// the single button and the bulk loop share ONE per-member handler (AC3/AC8), the
/// authorization re-check lives inside that handler so it runs per row (gba-1), the batch
/// audit's success comes from the summary (AC5), the loop sends no per-member admin email
/// (AC6), select-all uses the row checkbox's own predicate (AC1), and the bulk handler
/// snapshots the group before its first await (AC12). Behavior lives in
/// BulkIdentityListTests; the end-to-end proof is the plan's manual checks.
/// </summary>
public class GroupBulkActionsWiringTests
{
    private static string AdminPage() => File.ReadAllText(
        AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "GroupManagement.razor"));

    /// <summary>Bounds a method body from its signature to the next member at the same indent.</summary>
    private static string Body(string text, string signature)
    {
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " not found - tripwire is stale.");
        var end = text.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        var endDoc = text.IndexOf("\n    /// <summary>", start + signature.Length, StringComparison.Ordinal);
        var endMarker = text.IndexOf("\n    // -----", start + signature.Length, StringComparison.Ordinal);
        foreach (var candidate in new[] { endDoc, endMarker })
            if (candidate >= 0 && (end < 0 || candidate < end))
                end = candidate;
        if (end < 0)
            end = text.LastIndexOf('}');
        Assert.True(end > start, "Could not bound " + signature + " - update the tripwire.");
        return text[start..end];
    }

    private static int CountOf(string text, string needle) => Regex.Matches(text, Regex.Escape(needle)).Count;

    // ----- S2: GroupManagement bulk remove -----

    [Fact]
    public void GroupManagement_SingleAndBulkRemove_ShareRemoveOneAsync()
    {
        var page = AdminPage();

        // Exactly one service remove call on the page, inside RemoveOneAsync.
        Assert.Equal(1, CountOf(page, "GroupService.RemoveMemberAsync("));
        Assert.Contains("GroupService.RemoveMemberAsync(", Body(page, "private async Task<BulkRowOutcome> RemoveOneAsync("), StringComparison.Ordinal);

        Assert.Contains("await RemoveOneAsync(group, listed, ticket, sendAdminEmail: true)", Body(page, "private async Task RemoveMember(GroupMemberInfo listed)"), StringComparison.Ordinal);
        Assert.Contains("await RemoveOneAsync(group, row, ticket, sendAdminEmail: false)", Body(page, "private async Task RemoveSelectedAsync()"), StringComparison.Ordinal);
    }

    [Fact]
    public void GroupManagement_PerRowRemoveHandler_RechecksAuthorization()
    {
        var page = AdminPage();
        var one = Body(page, "private async Task<BulkRowOutcome> RemoveOneAsync(");

        // gba-1: the check that counts runs immediately before EVERY row's write, inside the
        // shared handler, and its denial is audited.
        Assert.Contains("AuthorizationService.AuthorizeAsync(authState.User, \"GroupManagementOnPrem\")", one, StringComparison.Ordinal);
        Assert.Contains("\"Authorization denied\"", one, StringComparison.Ordinal);
        var iAuth = one.IndexOf("AuthorizationService.AuthorizeAsync(", StringComparison.Ordinal);
        var iWrite = one.IndexOf("GroupService.RemoveMemberAsync(", StringComparison.Ordinal);
        Assert.True(iAuth >= 0 && iWrite > iAuth, "The authorization re-check must precede the service write inside RemoveOneAsync.");

        // The bulk loop itself makes no service write of its own.
        var bulk = Body(page, "private async Task RemoveSelectedAsync()");
        Assert.DoesNotContain("GroupService.RemoveMemberAsync(", bulk, StringComparison.Ordinal);
        Assert.DoesNotContain("GroupService.AddMemberAsync(", bulk, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupManagement_BulkRemoveAudit_UsesSummarySuccess()
    {
        var bulk = Body(AdminPage(), "private async Task RemoveSelectedAsync()");

        Assert.Contains("const string action = \"GroupManagement_BulkRemoveMembers\";", bulk, StringComparison.Ordinal);
        Assert.Contains("var summary = BulkOutcomeSummary.Of(outcomes);", bulk, StringComparison.Ordinal);
        Assert.Contains("AuditBatch(action, group, ticket, summary.Success, summary.ErrorDetail, summary.Requested, summary.Done, summary.NotDone, summary.MemberLines);", bulk, StringComparison.Ordinal);
        // Never a hard-coded success on the batch event.
        Assert.DoesNotContain("AuditBatch(action, group, ticket, true,", bulk, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupManagement_BulkRemoveLoop_DoesNotSendPerMemberAdminEmail()
    {
        var page = AdminPage();
        var bulk = Body(page, "private async Task RemoveSelectedAsync()");

        // D1: the loop passes sendAdminEmail: false and ONE summary email follows the loop.
        Assert.Contains("sendAdminEmail: false", bulk, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(bulk, "Email.SendAdminNotificationAsync("));
        var iLoop = bulk.IndexOf("foreach (var row in rows)", StringComparison.Ordinal);
        var iEmail = bulk.IndexOf("Email.SendAdminNotificationAsync(", StringComparison.Ordinal);
        Assert.True(iLoop >= 0 && iEmail > iLoop, "The summary email must follow the loop.");

        // The single button still sends its own per-member email.
        Assert.Contains("sendAdminEmail: true", Body(page, "private async Task RemoveMember(GroupMemberInfo listed)"), StringComparison.Ordinal);
        var one = Body(page, "private async Task<BulkRowOutcome> RemoveOneAsync(");
        Assert.Contains("if (sendAdminEmail)", one, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupManagement_SelectAll_SkipsDisabledRows()
    {
        var page = AdminPage();

        // The row checkbox disables on CanRemove; select-all iterates SelectableMembers, which
        // filters on the same CanRemove predicate - the single Remove button's enablement.
        Assert.Contains("disabled=\"@(isLoading || !CanRemove(member))\"", page, StringComparison.Ordinal);
        Assert.Contains("private List<GroupMemberInfo> SelectableMembers() => memberList?.Members.Where(CanRemove).ToList()", page, StringComparison.Ordinal);
        Assert.Contains("foreach (var m in SelectableMembers())", Body(page, "private void ToggleSelectAll(bool on)"), StringComparison.Ordinal);
        Assert.Contains("private static bool CanRemove(GroupMemberInfo m) => !string.IsNullOrEmpty(m.ObjectGuid) && !m.IsPrimaryMember;", page, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupManagement_BulkRemove_SnapshotsGroupBeforeFirstAwait()
    {
        var bulk = Body(AdminPage(), "private async Task RemoveSelectedAsync()");

        var iSnap = bulk.IndexOf("var group = selectedGroup;", StringComparison.Ordinal);
        var iRows = bulk.IndexOf("var rows = SelectedMembers();", StringComparison.Ordinal);
        var iAwait = bulk.IndexOf("await ", StringComparison.Ordinal);
        Assert.True(iSnap >= 0 && iRows > iSnap, "Group and rows must be snapshotted.");
        Assert.True(iAwait > iRows, "The snapshot must precede the first await in RemoveSelectedAsync.");
        // The refresh guard is the one allowed read of the live selection.
        Assert.DoesNotContain("selectedGroup.", bulk, StringComparison.Ordinal);
        Assert.DoesNotContain("selectedGroup!.", bulk, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupManagement_BulkStateNeverSurvivesAGroupSwitchOrReload()
    {
        var page = AdminPage();

        Assert.Contains("ClearBulkState();", Body(page, "private async Task SelectGroup(GroupInfo group)"), StringComparison.Ordinal);
        Assert.Contains("ClearBulkState();", Body(page, "private async Task Search()"), StringComparison.Ordinal);
        Assert.Contains("selectedGuids.Clear();", Body(page, "private async Task LoadMembers()"), StringComparison.Ordinal);
        // S3: a resolution preview made for one group never commits against another.
        Assert.Contains("resolution = null;", Body(page, "private void ClearBulkState()"), StringComparison.Ordinal);
    }

    // ----- S3: GroupManagement bulk add -----

    [Fact]
    public void GroupManagement_SingleAndBulkAdd_ShareAddOneAsync()
    {
        var page = AdminPage();

        Assert.Equal(1, CountOf(page, "GroupService.AddMemberAsync("));
        Assert.Contains("GroupService.AddMemberAsync(", Body(page, "private async Task<BulkRowOutcome> AddOneAsync("), StringComparison.Ordinal);

        Assert.Contains("await AddOneAsync(group, memberLabel, selection?.DistinguishedName, ticket, sendAdminEmail: true)", Body(page, "private async Task AddMember()"), StringComparison.Ordinal);
        // The batch passes the resolution's DN exactly as the picker passes its held DN (AC8).
        Assert.Contains("await AddOneAsync(group, row.Line.Text, row.Match!.DistinguishedName, ticket, sendAdminEmail: false)", Body(page, "private async Task AddResolvedAsync()"), StringComparison.Ordinal);
    }

    [Fact]
    public void GroupManagement_PerRowAddHandler_RechecksAuthorization()
    {
        var page = AdminPage();
        var one = Body(page, "private async Task<BulkRowOutcome> AddOneAsync(");

        Assert.Contains("AuthorizationService.AuthorizeAsync(authState.User, \"GroupManagementOnPrem\")", one, StringComparison.Ordinal);
        Assert.Contains("\"Authorization denied\"", one, StringComparison.Ordinal);
        var iAuth = one.IndexOf("AuthorizationService.AuthorizeAsync(", StringComparison.Ordinal);
        var iWrite = one.IndexOf("GroupService.AddMemberAsync(", StringComparison.Ordinal);
        Assert.True(iAuth >= 0 && iWrite > iAuth, "The authorization re-check must precede the service write inside AddOneAsync.");

        var bulk = Body(page, "private async Task AddResolvedAsync()");
        Assert.DoesNotContain("GroupService.AddMemberAsync(", bulk, StringComparison.Ordinal);
        Assert.DoesNotContain("GroupService.RemoveMemberAsync(", bulk, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupManagement_BulkAddAudit_UsesSummarySuccess_AndOneSummaryEmail()
    {
        var bulk = Body(AdminPage(), "private async Task AddResolvedAsync()");

        Assert.Contains("const string action = \"GroupManagement_BulkAddMembers\";", bulk, StringComparison.Ordinal);
        Assert.Contains("var summary = BulkOutcomeSummary.Of(outcomes);", bulk, StringComparison.Ordinal);
        Assert.Contains("AuditBatch(action, group, ticket, summary.Success, summary.ErrorDetail, summary.Requested, summary.Done, summary.NotDone, summary.MemberLines);", bulk, StringComparison.Ordinal);
        Assert.DoesNotContain("AuditBatch(action, group, ticket, true,", bulk, StringComparison.Ordinal);
        Assert.Contains("sendAdminEmail: false", bulk, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(bulk, "Email.SendAdminNotificationAsync("));
    }

    [Fact]
    public void GroupManagement_BulkAdd_CommitsOnlyResolvedRows()
    {
        var bulk = Body(AdminPage(), "private async Task AddResolvedAsync()");

        Assert.Contains("resolution?.Where(r => r.Status == BulkIdentityList.Status.Resolved && r.Match != null)", bulk, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupManagement_BulkAdd_SnapshotsGroupBeforeFirstAwait()
    {
        var bulk = Body(AdminPage(), "private async Task AddResolvedAsync()");

        var iSnap = bulk.IndexOf("var group = selectedGroup;", StringComparison.Ordinal);
        var iRows = bulk.IndexOf("var rows = resolution?.Where(", StringComparison.Ordinal);
        var iAwait = bulk.IndexOf("await ", StringComparison.Ordinal);
        Assert.True(iSnap >= 0 && iRows > iSnap, "Group and rows must be snapshotted.");
        Assert.True(iAwait > iRows, "The snapshot must precede the first await in AddResolvedAsync.");
        Assert.DoesNotContain("selectedGroup.", bulk, StringComparison.Ordinal);
        Assert.DoesNotContain("selectedGroup!.", bulk, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupManagement_ResolvePaste_UsesTheServiceBatch_AndListsEveryLine()
    {
        var body = Body(AdminPage(), "private async Task ResolvePasteAsync()");

        Assert.Contains("BulkIdentityList.Parse(pasteText)", body, StringComparison.Ordinal);
        Assert.Contains("await GroupService.ResolveBatchAsync(parsed.Kept)", body, StringComparison.Ordinal);
        // Duplicates and over-cap lines are shown with their reasons, never silently dropped.
        Assert.Contains("parsed.Duplicates.Select(", body, StringComparison.Ordinal);
        Assert.Contains("parsed.OverCap.Select(", body, StringComparison.Ordinal);
        Assert.Contains("BulkIdentityList.Status.NotAttempted", body, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupManagementService_BatchQuery_IsForestWide_ClassAgnostic_AndProjectsName()
    {
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "GroupManagementService.cs"));
        var start = text.IndexOf("internal virtual IReadOnlyList<BulkIdentityList.Candidate> QueryBatchCandidates(", StringComparison.Ordinal);
        Assert.True(start >= 0, "QueryBatchCandidates not found - tripwire is stale.");
        var end = text.IndexOf("\n    // --- Helpers ---", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound QueryBatchCandidates - update the tripwire.");
        var body = text[start..end];

        Assert.Contains("BulkIdentityList.BuildBatchFilter(chunk, allowGroups: true)", body, StringComparison.Ordinal);
        Assert.Contains("ResolveSearchGlobalCatalog(ps, credential)", body, StringComparison.Ordinal);
        Assert.Contains("AddCommand(\"Get-ADObject\")", body, StringComparison.Ordinal);
        // gba-3: Name is projected so a group found by name matches back to its line.
        Assert.Contains("\"Name\", \"DisplayName\", \"UserPrincipalName\", \"SamAccountName\", \"mail\", \"DistinguishedName\", \"ObjectGUID\"", body, StringComparison.Ordinal);
        Assert.Contains("Name: o.Properties[\"Name\"]?.Value?.ToString()", body, StringComparison.Ordinal);
        // A partially errored query fails the whole batch closed.
        Assert.Contains("if (ps.HadErrors)", body, StringComparison.Ordinal);
        Assert.Contains("throw new InvalidOperationException(", body, StringComparison.Ordinal);
    }
}
