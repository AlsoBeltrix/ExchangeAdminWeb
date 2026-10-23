using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a28y: after its string match, the self-grant check compared the acting user and the grantee by object id through a
/// lookup that answered a failure as "no recipient". The comparison was skipped and the grant allowed exactly while Exchange
/// could not answer - so a grant to an alias of the operator's own mailbox, which the string match misses, went through
/// whenever Exchange was unreachable or its config could not be read.
/// </summary>
/// <remarks>
/// The validator over <see cref="ExchangeWriteHarness"/> with nothing protected, Exchange answering through a stand-in
/// resolver; the bulk loops write through the harness's pool and stand-in cmdlets. Nothing here reaches Exchange or a
/// directory. <see cref="PermissionValidatorConfigReadTests"/> holds the case where the real resolver cannot read the
/// Exchange Online config.
/// </remarks>
public sealed class PermissionValidatorSelfGrantTests : IDisposable
{
    private const string Operator = @"CONTOSO\jdoe";
    private const string OperatorMailbox = "jdoe@contoso.test";

    // The operator's own mailbox by another name: "jdoe" and "john.doe" do not match as strings, so only Exchange can say so.
    private const string OperatorAlias = "john.doe@contoso.test";
    private const string Other = "other@contoso.test";
    private const string Target = "shared@contoso.test";
    private static readonly Guid TargetMailbox = Guid.Parse("0f0f0f0f-2222-4333-8444-666666666666");

    private const string SelfGrantRefusal = "Access denied: You cannot grant permissions to yourself.";

    private readonly ExchangeWriteHarness _h = new();

    public PermissionValidatorSelfGrantTests() => _h.AddMailbox(Target, TargetMailbox);

    public void Dispose() => _h.Dispose();

    private static string Incomplete(string identity) =>
        $"Access denied: The self-grant check could not be completed: Exchange Online could not resolve '{identity}'; nothing was written. Contact your administrator.";

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    private static ResolvedRecipient Recipient(string primary, string? objectId) =>
        new(primary, objectId, "UserMailbox", ExistsOnPrem: false, ExchangeGuid: Guid.NewGuid());

    /// <summary>Exchange knows the bulk loops' target mailbox; every other identity is "no such recipient" until stubbed.</summary>
    private static IIdentityResolver ExchangeKnowsTheTarget() =>
        ExchangeWriteHarness.ExchangeKnows(Target, Target, TargetMailbox);

    /// <summary>A lookup of <paramref name="identity"/> that cannot run: Exchange gives no answer at all.</summary>
    private static void Unreachable(IIdentityResolver resolver, string identity) =>
        resolver.ResolveRecipientAsync(identity).Returns(
            Task.FromException<ResolvedRecipient?>(new InvalidOperationException("Exchange Online is unreachable")));

    private EmailService Email() => Substitute.For<EmailService>(_h.Config, Substitute.For<ILogger<EmailService>>());

    [Theory]
    [InlineData(Operator)]
    [InlineData(OperatorAlias)]
    public async Task ACheckExchangeCannotAnswer_IsRefused_NamingWhoCouldNotBeResolved(string unreachable)
    {
        // The other side is what Exchange would name as the operator's own mailbox, so the comparison - had it been
        // made - refuses. Clearing the grant on the string match alone because one lookup could not run was the defect.
        var resolver = ExchangeKnowsTheTarget();
        var own = Recipient(OperatorMailbox, "ext-jdoe");
        resolver.ResolveRecipientAsync(Operator).Returns(own);
        resolver.ResolveRecipientAsync(OperatorAlias).Returns(own);
        Unreachable(resolver, unreachable);

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Operator, OperatorAlias);

        Assert.Equal(Incomplete(unreachable), result);
        Assert.Equal(0, _h.Connects);
    }

    [Fact]
    public async Task NoIdentityResolver_RefusesTheGrant_AsACheckThatCouldNotBeCompleted()
    {
        var result = await _h.NothingProtectedValidator(null!).ValidateSelfGrantAsync(Operator, Other);

        Assert.Equal(Incomplete(Operator), result);
    }

    [Theory]
    [InlineData("ext-jdoe", "ext-jdoe")]
    [InlineData(null, null)]
    [InlineData("", "")]
    public async Task AnAliasExchangeNamesAsTheOperator_IsRefused_AsASelfGrant(string? operatorId, string? aliasId)
    {
        // One recipient, by its object id and its address; then by the address alone, when Exchange reports no object id
        // (absent or blank) - the primary address is Exchange's own name for the recipient, compared regardless of case.
        var resolver = ExchangeKnowsTheTarget();
        resolver.ResolveRecipientAsync(Operator).Returns(Recipient(OperatorMailbox, operatorId));
        resolver.ResolveRecipientAsync(OperatorAlias).Returns(Recipient(OperatorMailbox.ToUpperInvariant(), aliasId));

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Operator, OperatorAlias);

        Assert.Equal(SelfGrantRefusal, result);
    }

    [Theory]
    [InlineData("ext-jdoe", "ext-other")]
    [InlineData(null, null)]
    [InlineData("", "")]
    public async Task TwoRecipientsExchangeNamesApart_AreAllowed(string? operatorId, string? otherId)
    {
        // The control. Two blank object ids are no object id, not one shared id: they no longer make two recipients one.
        var resolver = ExchangeKnowsTheTarget();
        resolver.ResolveRecipientAsync(Operator).Returns(Recipient(OperatorMailbox, operatorId));
        resolver.ResolveRecipientAsync(Other).Returns(Recipient(Other, otherId));

        Assert.Null(await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Operator, Other));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ExchangeReportingNoSuchRecipient_IsAnAnswer_AndTheStringMatchStands(bool operatorKnown, bool otherKnown)
    {
        // A lookup that ran and found no recipient leaves nothing to compare: the string match is the whole answer, as the
        // excluded-user check reads the same null (aud-a2b). It still refuses on its own.
        var resolver = ExchangeKnowsTheTarget();
        if (operatorKnown)
            resolver.ResolveRecipientAsync(Operator).Returns(Recipient(OperatorMailbox, "ext-jdoe"));
        if (otherKnown)
            resolver.ResolveRecipientAsync(Other).Returns(Recipient(Other, "ext-other"));
        var validator = _h.NothingProtectedValidator(resolver);

        Assert.Null(await validator.ValidateSelfGrantAsync(Operator, Other));
        Assert.Equal(SelfGrantRefusal, await validator.ValidateSelfGrantAsync(Operator, OperatorMailbox));
    }

    [Theory]
    [InlineData("unreachable")]
    [InlineData("alias")]
    public async Task TheMailboxPermissionsBulkLoop_RefusesTheRow_AndRecordsTheValidatorsReason(string shape)
    {
        // The row passes the protected-target check, so the self-grant check is what refuses it - and the record carries
        // its reason: a check that could not be completed is not recorded as an operator granting themselves access.
        var (resolver, grantee, expected) = Shape(shape);
        var csv = "Target,User,FullAccess,SendAs,AutoMapping\n" + $"{Target},{grantee},True,False,False\n";

        var bulk = await new MailboxPermissionService(_h.Pool, null!, NullLogger<MailboxPermissionService>.Instance, _h.Config, null!, _h.Trace)
            .ProcessMailboxPermissionsCsvAsync(
                new MemoryStream(Encoding.UTF8.GetBytes(csv)), isAdd: true, _h.NothingProtectedValidator(resolver), Operator,
                _h.Audit, Email(), "10.0.0.5", "INC100", actingUser: null);

        Assert.Equal(0, bulk.SuccessCount);
        Assert.Equal(1, bulk.FailedCount);
        Assert.Empty(_h.Writes);
        Assert.Equal(0, _h.Connects);

        var record = Assert.Single(_h.AuditRows());
        Assert.Equal("BulkAddFullAccess_SelfGrantAttempt", Text(record, "action"));
        Assert.Equal(expected, Text(record, "error"));
    }

    [Theory]
    [InlineData("unreachable")]
    [InlineData("alias")]
    public async Task TheCalendarPermissionsBulkLoop_RefusesTheRow_AndRecordsTheValidatorsReason(string shape)
    {
        var (resolver, grantee, expected) = Shape(shape);
        var csv = "Target,User,AccessRight\n" + $"{Target},{grantee},Reviewer\n";

        var bulk = await new CalendarPermissionService(_h.Pool, null!, NullLogger<CalendarPermissionService>.Instance, _h.Config, null!, _h.Trace)
            .ProcessCalendarPermissionsCsvAsync(
                new MemoryStream(Encoding.UTF8.GetBytes(csv)), isSet: true, _h.NothingProtectedValidator(resolver), Operator,
                _h.Audit, Email(), "10.0.0.5", "INC100", actingUser: null);

        Assert.Equal(0, bulk.SuccessCount);
        Assert.Equal(1, bulk.FailedCount);
        Assert.Empty(_h.Writes);
        Assert.Equal(0, _h.Connects);

        var record = Assert.Single(_h.AuditRows());
        Assert.Equal("BulkSetCalendar_SelfGrantAttempt", Text(record, "action"));
        Assert.Equal(expected, Text(record, "error"));
    }

    /// <summary>
    /// "unreachable": Exchange cannot answer for the operator, so the check cannot be completed. "alias": Exchange names
    /// the grantee as the operator's own mailbox, a self-grant the string match misses.
    /// </summary>
    private static (IIdentityResolver resolver, string grantee, string expected) Shape(string shape)
    {
        var resolver = ExchangeKnowsTheTarget();
        if (shape == "unreachable")
        {
            Unreachable(resolver, Operator);
            return (resolver, Other, Incomplete(Operator));
        }

        var own = Recipient(OperatorMailbox, "ext-jdoe");
        resolver.ResolveRecipientAsync(Operator).Returns(own);
        resolver.ResolveRecipientAsync(OperatorAlias).Returns(own);
        return (resolver, OperatorAlias, SelfGrantRefusal);
    }

    [Theory]
    [InlineData("Components/Pages/MailboxPermissions.razor")]
    [InlineData("Components/Pages/CalendarPermissions.razor")]
    [InlineData("Services/MailboxPermissionService.cs")]
    [InlineData("Services/CalendarPermissionService.cs")]
    public void EverySelfGrantRecord_CarriesTheValidatorsOwnRefusal(string relativePath)
    {
        // The two pages' single-grant handlers have no seam to drive here, so their record is read from the source,
        // comments blanked; the two bulk loops are driven above as well. Each records the refusal it was handed, as their
        // protected-target denials already do, rather than a fixed sentence that says every refusal was a self-grant.
        var path = AuditCategoryFilingTests.FindRepoFile(relativePath.Split('/'));
        var code = RazorSyntax.CommentsBlanked(File.ReadAllText(path));

        var records = Regex.Matches(code, @"_SelfGrantAttempt""").ToList();
        var record = Assert.Single(records);
        var detail = Regex.Match(code[record.Index..], @"errorDetail:\s*([^,)]+)");

        Assert.True(detail.Success, $"{relativePath}: the self-grant record names no errorDetail.");
        Assert.Equal("selfGrantError", detail.Groups[1].Value.Trim());
    }
}
