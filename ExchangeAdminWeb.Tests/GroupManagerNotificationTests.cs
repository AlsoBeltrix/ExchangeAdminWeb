namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding A25: security-group membership changes notified administrators only.
/// </summary>
/// <remarks>
/// The owner ruled this one himself on 2026-10-19, and the ruling is narrower than the
/// audit's: <i>"it ABSOLUTELY needs to email users when mailbox or calendar permissions are
/// changed. NOT for group membership changes. Email the manager of the group with a summary
/// of the changes, but do not email every user in the group."</i>
///
/// So the valuable guard here is not only that the manager is told - it is that the members
/// are NOT, and that a batch sends one message rather than one per row. Those are the parts
/// a later well-meaning edit would "fix" by copying what aud-a24 did for mailbox and
/// calendar permissions, which is the exact opposite shape.
///
/// Source guards. Both halves need a live directory: the manager lookup runs Get-ADObject
/// against the forest, and the page method around it needs a rendered circuit.
/// </remarks>
public class GroupManagerNotificationTests
{
    private static string Page() =>
        File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "GroupManagement.razor"));

    private static string Service() =>
        File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "GroupManagementService.cs"));

    [Fact]
    public void TheMembersOfAGroupAreNeverEmailed()
    {
        // The owner's refusal, in executable form. aud-a24 made exactly these calls the
        // right answer for mailbox and calendar permissions; here they are forbidden.
        var page = Page();

        Assert.DoesNotContain("SendUserNotificationAsync", page, StringComparison.Ordinal);
        Assert.DoesNotContain("SendOwnerNotificationAsync", page, StringComparison.Ordinal);
    }

    [Fact]
    public void ABatchSendsOneSummary_NotOneMessagePerRow()
    {
        // The per-row helpers are called in a loop with sendAdminEmail: false, so their
        // manager notice must be gated on the same flag or a 50-row paste mails the
        // manager 50 times.
        var page = Page();

        // BOTH per-row helpers, counted. Asserting mere presence passed while one of the
        // two had lost its gate - caught by the per-row-fanout mutation.
        var gated = System.Text.RegularExpressions.Regex.Matches(
            page, @"if \(sendAdminEmail && result\.Success && result\.Changed\)");
        Assert.Equal(2, gated.Count);

        // And every call site is one of the four we know about. Review of A25: counting
        // the GATES let an extra ungated call be added somewhere else entirely and still
        // pass. Counting the CALLS closes that.
        var calls = System.Text.RegularExpressions.Regex.Matches(page, @"await NotifyGroupManagerAsync\(");
        Assert.Equal(4, calls.Count);

        // And the batch paths notify once, after the loop, from the summary.
        var batchCalls = System.Text.RegularExpressions.Regex.Matches(
            page, @"if \(summary\.ChangedCount > 0\)\s*\r?\n\s*await NotifyGroupManagerAsync");
        Assert.Equal(2, batchCalls.Count);
    }

    [Fact]
    public void NothingIsSentWhenThereIsNoManager()
    {
        // No fallback to the admin address, no fallback to the members. The ruling says the
        // manager; absent a manager there is no recipient.
        var body = AuditCategoryFilingTests.MethodBody(
            Page(), "private async Task NotifyGroupManagerAsync");

        var nullBranch = body[body.IndexOf("if (manager is null)", StringComparison.Ordinal)..];
        var returnAt = nullBranch.IndexOf("return;", StringComparison.Ordinal);
        Assert.True(returnAt > 0, "the no-manager branch must return");

        // Nothing is dispatched before that return.
        Assert.DoesNotContain("SendModuleNotificationAsync", nullBranch[..returnAt], StringComparison.Ordinal);
    }

    [Fact]
    public void TheAbsentManagerIsRecordedInTheAudit()
    {
        // "Say so in the audit record rather than falling back" - the operator's change
        // succeeded, and who did not hear about it is a durable fact.
        var body = AuditCategoryFilingTests.MethodBody(
            Page(), "private async Task NotifyGroupManagerAsync");

        // Ordering, not presence. Review of A25: asserting both strings exist somewhere in
        // the method passed even if the no-manager branch returned before reaching them.
        var nullBranch = body[body.IndexOf("if (manager is null)", StringComparison.Ordinal)..];
        var auditAt = nullBranch.IndexOf("_ManagerNotNotified", StringComparison.Ordinal);
        var returnAt = nullBranch.IndexOf("return;", StringComparison.Ordinal);

        Assert.True(auditAt > 0, "the no-manager branch must audit");
        Assert.True(auditAt < returnAt, "the audit must happen BEFORE the branch returns");
        Assert.Contains("LogModuleAction", nullBranch[..returnAt], StringComparison.Ordinal);
    }

    [Fact]
    public void AnUndeliveredSummaryIsAlsoRecorded()
    {
        // A resolved manager whose mail bounces is the same gap as no manager at all.
        var body = AuditCategoryFilingTests.MethodBody(
            Page(), "private async Task NotifyGroupManagerAsync");

        Assert.Contains("dispatch.AnyDelivered", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNoticeGoesToTheManagersOwnAddress()
    {
        // Not SendAdminNotificationAsync, which addresses Email:AdminNotificationEmail -
        // the mistake aud-a27 found in Emergency Disable.
        var body = AuditCategoryFilingTests.MethodBody(
            Page(), "private async Task NotifyGroupManagerAsync");

        Assert.Contains("SendModuleNotificationAsync(manager.Email", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheManagerLookupFailsSoftAndSaysWhichWay()
    {
        // Four ways of having no manager, and the operator's change already succeeded, so
        // none of them may throw out of the notification.
        var service = Service();

        Assert.Contains("public async Task<GroupManagerContact?> GetGroupManagerAsync", service, StringComparison.Ordinal);
        Assert.Contains("\"ManagedBy\"", service, StringComparison.Ordinal);

        var body = AuditCategoryFilingTests.MethodBody(service, "public async Task<GroupManagerContact?> GetGroupManagerAsync");

        // Every exit is a null, never an exception.
        Assert.Contains("catch (Exception ex)", body, StringComparison.Ordinal);
        Assert.Contains("return null;", body, StringComparison.Ordinal);

        // Review of A25: the lookup must route to the DN's own domain. Get-ADObject binds
        // to the credential's domain otherwise, and a forest search returns groups from
        // several - so an unrouted lookup returned null for every out-of-domain group and
        // that null is indistinguishable from "this group has no manager".
        Assert.Contains("ServerFromDn(managedBy)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AMailEnabledGroupIsNeverAcceptedAsTheManager()
    {
        // Review of A25, and the sharpest edge in this finding: managedBy can name a
        // mail-enabled GROUP, including the managed group itself. Sending there delivers to
        // every member - the exact outcome the owner refused. An unexpected object class is
        // no recipient, not a best effort.
        var body = AuditCategoryFilingTests.MethodBody(
            Service(), "public async Task<GroupManagerContact?> GetGroupManagerAsync");

        Assert.Contains("objectClass", body, StringComparison.Ordinal);

        // The check must come BEFORE the address is read, or it decides nothing.
        var classCheckAt = body.IndexOf("\"user\", StringComparison.OrdinalIgnoreCase", StringComparison.Ordinal);
        var mailReadAt = body.IndexOf("found.Properties[\"mail\"]", StringComparison.Ordinal);

        Assert.True(classCheckAt > 0, "the object class must be checked");
        Assert.True(classCheckAt < mailReadAt, "the class check must precede reading the address");
    }

    [Fact]
    public void OnlyManagedByIsConsulted_NotTheExchangeCoManagers()
    {
        // Deliberate narrowness: the ruling says manager, singular. Exchange groups can
        // carry msExchCoManagedByLink co-owners and widening to them is a decision, not an
        // inference - so if this ever starts matching, it was a choice someone made.
        var body = AuditCategoryFilingTests.MethodBody(
            Service(), "public async Task<GroupManagerContact?> GetGroupManagerAsync");

        Assert.DoesNotContain("msExchCoManagedByLink", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIdempotentNoOpIsNotAnnouncedAsAChange()
    {
        // Review of A25. "Already a member" and "is not a member" are SUCCESSES. Gating the
        // manager notice on success alone meant adding fifty people who were already in a
        // group mailed the manager "50 of 50 applied" after changing nothing.
        var service = Service();

        Assert.Contains("PermissionResult.NoChange($\"{member} is already a member", service, StringComparison.Ordinal);
        Assert.Contains("PermissionResult.NoChange($\"{member} is not a member", service, StringComparison.Ordinal);
    }

    [Fact]
    public void ABatchNoticeCountsOnlyWhatChanged_AndDoesNotMixInFailures()
    {
        // Review of A25. Done counts no-ops; MemberLines lists "Not done" rows alongside
        // successful ones. Using either to tell a manager what changed is wrong in a
        // different way each time.
        var page = Page();

        Assert.Contains("summary.ChangedCount > 0", page, StringComparison.Ordinal);
        Assert.Contains("summary.ChangedLines", page, StringComparison.Ordinal);

        // A partial batch must not be framed as a clean success in the mail.
        Assert.Contains("summary.ChangedLines)}\", summary.Success)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("string.Join(\"; \", summary.MemberLines)}\")", page, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedSurvivesAServicedOverride()
    {
        // WithServicedNote rebuilds the record field by field - the same way it nearly lost
        // AppliedRights in aud-a23.
        var model = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Models", "PermissionResult.cs"));
        var body = AuditCategoryFilingTests.MethodBody(model, "public PermissionResult WithServicedNote");

        Assert.Contains("Changed = Changed", body, StringComparison.Ordinal);
    }
}
