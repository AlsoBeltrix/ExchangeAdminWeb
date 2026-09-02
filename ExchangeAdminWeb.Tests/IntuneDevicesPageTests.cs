using System.Text.RegularExpressions;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Models;
using Xunit;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the IntuneDevices page (docs/IntuneDeviceManagement-Plan.md S2): the pure projectors,
/// and the source-text wiring for the read-only search/detail shape - the visible truncation
/// notice (T1), the hasSearched three-state rule (the blr-3 class), the audit-on-read via
/// LogLookupAction (Read-alerting classification), the D3 standing note, and the absence of any
/// write action in this slice (S3-S5 are not yet implemented).
/// </summary>
/// <remarks>
/// Source-text guards, explicitly NOT behavioural coverage: there is no bUnit harness in this
/// repo (plan, Verification / Test plan), so no test can render the page or observe which branch
/// a handler takes. Stated as tripwires so a green suite is never read as proof the page behaves
/// correctly.
/// </remarks>
public class IntuneDevicesPageTests
{
    [Fact]
    public void DescribeSearch_NoTerm_ReturnsNoSearchTermMarker()
    {
        Assert.Equal("(no search term)", IntuneDevices.DescribeSearch(null));
        Assert.Equal("(no search term)", IntuneDevices.DescribeSearch(""));
        Assert.Equal("(no search term)", IntuneDevices.DescribeSearch("   "));
    }

    [Fact]
    public void DescribeSearch_TrimsAndReturnsTheTerm()
    {
        Assert.Equal("contoso-laptop-01", IntuneDevices.DescribeSearch("  contoso-laptop-01  "));
    }

    [Theory]
    [InlineData("compliant", "bg-success")]
    [InlineData("noncompliant", "bg-danger")]
    [InlineData("conflict", "bg-warning text-dark")]
    [InlineData("error", "bg-danger")]
    [InlineData("inGracePeriod", "bg-warning text-dark")]
    [InlineData("configManager", "bg-info text-dark")]
    [InlineData("unknown", "bg-secondary")]
    [InlineData("COMPLIANT", "bg-success")]
    public void ComplianceBadgeClass_DocumentedValue_MapsToExpectedBadge(string complianceState, string expected)
    {
        Assert.Equal(expected, IntuneDevices.ComplianceBadgeClass(complianceState));
    }

    [Fact]
    public void ComplianceBadgeClass_UnknownFutureValue_FallsBackToNeutralBadge()
    {
        // complianceState is stored as a plain string and must still render with a neutral badge
        // rather than being dropped or miscategorized, mirroring RiskyUsers' RiskLevelBadgeClass
        // rule for the same reason (Graph extends these enums without notice).
        Assert.Equal("bg-light text-dark border", IntuneDevices.ComplianceBadgeClass("somethingNewMicrosoftAdded"));
    }

    [Fact]
    public void FormatStorage_ZeroTotal_ReportsUnknownRatherThanZeroOfZero()
    {
        Assert.Equal("(unknown)", IntuneDevices.FormatStorage(0, 0));
    }

    [Fact]
    public void FormatStorage_ComputesGigabytesFromBytes()
    {
        var oneGb = 1024L * 1024 * 1024;
        Assert.Equal("2.0 GB free of 64.0 GB", IntuneDevices.FormatStorage(2 * oneGb, 64 * oneGb));
    }

    [Fact]
    public void IntuneDevices_ReadPathNeverAlertEmails()
    {
        // Read-alerting classification (owner-reviewed, plan): reads are audited, never
        // alert-emailed. Scoped to the two read handlers so a later write slice's legitimate
        // admin notification cannot be mistaken for a violation here.
        var read = MethodBody("SearchAsync") + MethodBody("ToggleDetailAsync");

        Assert.DoesNotContain("Email.", read);
        Assert.DoesNotContain("SendAdminNotificationAsync", read);
        Assert.Contains("LogLookupAction", read);
    }

    [Fact]
    public void IntuneDevices_HasOnlyTheDeleteWriteActionInThisSlice()
    {
        // S3 adds Delete behind IntuneDevicesDelete. Retire/Wipe (S4) and the Entra ID removal (S5)
        // are later slices; this page must not reach ahead of its own slice.
        var text = PageSource();

        Assert.Contains("DeleteDeviceAsync", text);
        Assert.Contains("IntuneDevicesDelete\"", text);
        Assert.DoesNotContain("RetireDeviceAsync", text);
        Assert.DoesNotContain("WipeDeviceAsync", text);
        Assert.DoesNotContain("RemoveEntraDeviceAsync", text);
        Assert.DoesNotContain("IntuneDevicesPrivileged\"", text);
        Assert.DoesNotContain("IntuneDevicesEntraDelete\"", text);
    }

    [Fact]
    public void IntuneDevices_AuditsSearchOnBothSuccessAndFailure()
    {
        var text = PageSource();

        var calls = Regex.Matches(
            text,
            @"LogLookupAction\(\s*[^;]*?""IntuneDevices_Search""",
            RegexOptions.Singleline);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public void IntuneDevices_AuditsDetailLookupOnBothSuccessAndFailure()
    {
        var text = PageSource();

        var calls = Regex.Matches(
            text,
            @"LogLookupAction\(\s*[^;]*?""IntuneDevices_Detail""",
            RegexOptions.Singleline);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public void IntuneDevices_ClearsHasSearchedAtStartOfEverySearch()
    {
        // blr-3 class defect: a second search must retract the first search's verdict before the
        // new one resolves, not only on the page's first ever search.
        var body = MethodBody("SearchAsync");

        Assert.Contains("hasSearched = false;", body);
        Assert.Contains("await Task.Yield();", body);
        Assert.Contains("hasSearched = true;", body);
    }

    [Fact]
    public void IntuneDevices_RendersVisibleTruncationNotice()
    {
        // T1: a response carrying @odata.nextLink must render a visible truncation notice - a
        // silently truncated device list is the exact failure mode T1 exists to prevent.
        var text = PageSource();

        Assert.Contains("@if (truncated)", text);
        Assert.Contains("more devices exist. Narrow the search.", text);
    }

    [Fact]
    public void IntuneDevices_TruncatedEmptyResult_DoesNotClaimTheDeviceDoesNotExist()
    {
        // T2 fallback rule: a client-side match over a truncated page must never render as "no
        // such device" - it renders as "no match in the first N devices searched".
        var text = PageSource();

        Assert.Contains("No match in the first", text);
        Assert.Contains("searchedCount", text);
    }

    [Fact]
    public void IntuneDevices_DisplaysModuleVersionNextToHeading()
    {
        Assert.Contains("<ModuleVersion", PageSource());
    }

    [Fact]
    public void IntuneDevices_StandingNoteAboutEntraAndCompanyDataSurvives()
    {
        // D3 / T3: a standing note that deleting the Intune record neither removes company data
        // from the device nor removes the Entra ID device object.
        var text = PageSource();

        Assert.Contains("does not remove company data", text);
        Assert.Contains("does not remove the device's Entra ID object", text);
    }

    [Fact]
    public void IntuneDevices_PageAuthorizesOnTheMainPolicy()
    {
        var text = PageSource();

        Assert.Contains("[Authorize(Policy = \"IntuneDevices\")]", text);
        Assert.Contains("AuthorizeAsync(user, \"IntuneDevices\")", text);
    }

    // ---- S3: the delete gate chain ------------------------------------------------------------

    [Fact]
    public void IntuneDevices_ExecuteAction_ReChecksTheGranularPolicyBeforeTheWrite()
    {
        // T6: not at page load. The page-load check hides the button; the decision is re-evaluated
        // inside the handler after the confirmation step, because a grant can be revoked while the
        // page is open.
        var body = MethodBody("ExecuteActionAsync");

        var authIndex = body.IndexOf("AuthorizationService.AuthorizeAsync(authState.User, policy)", StringComparison.Ordinal);
        Assert.True(authIndex >= 0, "ExecuteActionAsync does not re-check the granular policy.");
        Assert.True(WriteIndex(body) > authIndex, "the granular re-check does not precede the Graph write.");
        Assert.Contains("PolicyFor(action)", body);
    }

    [Fact]
    public void IntuneDevices_ExecuteAction_RequiresATicketBeforeAnyGraphCall()
    {
        // Presence and validation are separate checks on purpose: ServiceNowService passes every
        // ticket while the integration is dormant, so validation alone would admit a blank ticket on
        // a deployment without ServiceNow.
        var body = MethodBody("ExecuteActionAsync");

        var presenceIndex = body.IndexOf("string.IsNullOrWhiteSpace(ticket)", StringComparison.Ordinal);
        var validateIndex = body.IndexOf("ServiceNow.ValidateTicketAsync(ticket)", StringComparison.Ordinal);

        Assert.True(presenceIndex >= 0, "ExecuteActionAsync does not require a ticket to be present.");
        Assert.True(validateIndex >= 0, "ExecuteActionAsync does not validate the ticket.");
        Assert.True(WriteIndex(body) > presenceIndex, "the ticket presence check does not precede the Graph write.");
        Assert.True(WriteIndex(body) > validateIndex, "ticket validation does not precede the Graph write.");
    }

    [Fact]
    public void IntuneDevices_ExecuteAction_ChecksProtectionBeforeTheWrite()
    {
        // T4, the load-bearing ordering of the whole slice: a protected-principal check that runs
        // after the write has already deleted a protected user's device record.
        var body = MethodBody("ExecuteActionAsync");

        var resolveIndex = body.IndexOf("ProtectedPrincipalService.ResolveWithExchangeFallbackAsync", StringComparison.Ordinal);
        var firstCheck = body.IndexOf("ProtectedPrincipalService.CheckAsync", StringComparison.Ordinal);
        var lastCheck = body.LastIndexOf("ProtectedPrincipalService.CheckAsync", StringComparison.Ordinal);

        Assert.True(resolveIndex >= 0, "ExecuteActionAsync does not resolve the device's primary user for protection.");
        Assert.True(firstCheck > resolveIndex, "the protection check does not follow resolution.");
        Assert.True(WriteIndex(body) > lastCheck, "a protection check runs AFTER the Graph write.");
    }

    [Fact]
    public void IntuneDevices_ExecuteAction_ProtectionBindsToTheDevicesPrimaryUser()
    {
        // T4: a device is not a principal, so the guard binds to the device's userPrincipalName -
        // not to the device id, and not to the acting operator.
        var body = MethodBody("ExecuteActionAsync");

        Assert.Contains("var upn = device.UserPrincipalName;", body);
        Assert.Contains("ResolveWithExchangeFallbackAsync(upn)", body);
    }

    [Fact]
    public void IntuneDevices_ExecuteAction_ChecksProtectionOnBothResolutionBranches()
    {
        // T4: Intune devices belong to CLOUD identities, so the DN-based rules structurally cannot
        // match them and an unresolved lookup means "check the protected USER rows against the raw
        // identity", never "nothing to protect". Skipping that branch is the defect MfaReset shipped
        // before 1.1.0.
        var body = MethodBody("ExecuteActionAsync");

        Assert.Equal(2, Regex.Matches(body, @"ProtectedPrincipalService\.CheckAsync\(").Count);
        Assert.Contains("Source: \"IntuneDevices-Unresolved\"", body);
        // T4's one improvement over MfaReset: managedDevice.userId IS the primary user's Entra
        // object id, so object-id-based protection is genuinely effective here.
        Assert.Contains("EntraObjectId: string.IsNullOrWhiteSpace(device.UserId) ? null : device.UserId", body);
        Assert.DoesNotContain("EntraObjectId: null", body);
    }

    [Fact]
    public void IntuneDevices_ExecuteAction_RefusesWhenResolutionOrTheCheckItselfFails()
    {
        // AC9 / Known Failure Class 3: an unavailable, ambiguous, failed or throwing protection
        // check must refuse, never fall through to the write. Fail closed outranks servicing.
        var body = MethodBody("ExecuteActionAsync");
        var gate = body[..WriteIndex(body)];

        Assert.Contains("ResolutionStatus.Unavailable or ProtectedPrincipalService.ResolutionStatus.Ambiguous", gate);
        Assert.Equal(2, Regex.Matches(gate, @"\.CheckFailed\)").Count);
        Assert.Contains("Protection check exception:", gate);
    }

    [Fact]
    public void IntuneDevices_ExecuteAction_DeviceWithNoPrimaryUserIsDeterminateNotFailClosed()
    {
        // T4 / AC10: an empty userPrincipalName on a SUCCESSFULLY read device (shared, kiosk, or
        // Autopilot pre-provisioned) is a determinate answer - there is no principal to protect - so
        // the action proceeds. Get it wrong in this direction and every shared device is stranded.
        var body = MethodBody("ExecuteActionAsync");
        var gate = body[..WriteIndex(body)];

        var emptyUpnBranch = gate.IndexOf("if (string.IsNullOrWhiteSpace(upn))", StringComparison.Ordinal);
        Assert.True(emptyUpnBranch >= 0, "the no-primary-user case is no longer handled explicitly.");

        // The branch logs and proceeds; it must not refuse, or shared devices become unactionable.
        var branch = gate[emptyUpnBranch..gate.IndexOf("else", emptyUpnBranch, StringComparison.Ordinal)];
        Assert.DoesNotContain("Refuse(", branch);
        Assert.Contains("no principal to protect", branch);
    }

    [Fact]
    public void IntuneDevices_ExecuteAction_EveryEarlyExitBeforeTheWriteIsAnAuditedRefusal()
    {
        // The shape that makes "every path audits" structural rather than remembered: Refuse() is
        // the only way to record a failure, it audits as it refuses, and every early exit before the
        // write goes through it. A refusal that returned without calling Refuse would be a silent
        // block - a protected principal denied with nothing in the audit trail.
        var body = MethodBody("ExecuteActionAsync");
        var gate = body[..WriteIndex(body)];

        // Nine: authorization, ticket presence, ticket validation, unavailable-or-ambiguous
        // resolution, CheckFailed and unserviced-protected on each of the two resolution branches,
        // and the fail-closed catch for a throwing check.
        var returns = Regex.Matches(gate, @"\breturn;");
        Assert.Equal(9, returns.Count);

        var previousExit = 0;
        foreach (Match exit in returns)
        {
            Assert.Contains("Refuse(", gate[previousExit..exit.Index]);
            previousExit = exit.Index + exit.Length;
        }
    }

    [Fact]
    public void IntuneDevices_Refuse_AuditsTheRefusalWithTheTicketAndTheTarget()
    {
        var body = MethodBody("ExecuteActionAsync");
        var refuse = Between(body, "void Refuse(", "\n        }");

        Assert.Contains("SetOutcome(deviceId, deviceName, false, message, null)", refuse);
        Assert.Matches(
            new Regex(@"LogModuleAction\([^;]*?""IntuneDevices"",\s*target,\s*false,\s*ticket", RegexOptions.Singleline),
            refuse);
    }

    [Fact]
    public void IntuneDevices_ExecuteAction_HonoursTheServicerGrantOnBothBranches()
    {
        // T5: both gates honour servicing, or a cloud-only protected identity becomes the one case a
        // servicer cannot service - precisely this module's population.
        var body = MethodBody("ExecuteActionAsync");

        Assert.Equal(2, Regex.Matches(body, @"ServicerNoteFor\(authState\.User,").Count);
        Assert.Contains("ProtectedPrincipalServicing.NoteFor", PageSource());
        Assert.Contains("ServicerModuleId = \"IntuneDevices\"", PageSource());
    }

    [Fact]
    public void IntuneDevices_ServicedOverride_IsVisibleOnScreenNotOnlyInTheAuditLog()
    {
        // T5: an override the operator cannot see is one they cannot decline.
        var text = PageSource();

        Assert.Contains("outcome.ServicedNote != null", text);
        Assert.Contains("Protected Principal - override in effect:", text);
        Assert.Contains("@outcome.ServicedNote", text);
    }

    [Fact]
    public void IntuneDevices_ExecuteAction_CarriesTheServicedNoteInExtraNotErrorDetail()
    {
        // pps-3: LogModuleAction writes ["error"] = success ? null : errorDetail, so a note placed in
        // errorDetail is discarded on exactly the success path that needs it - the one path where an
        // override actually happened.
        var body = MethodBody("ExecuteActionAsync");
        var successAudit = body[WriteIndex(body)..];

        Assert.Contains("extra: ServicedExtra(servicedAuditDetail)", successAudit);
        Assert.DoesNotContain("errorDetail: ServicedExtra", successAudit);
        Assert.DoesNotContain("errorDetail: servicedAuditDetail", successAudit);
    }

    [Fact]
    public void IntuneDevices_ExecuteAction_AuditsTheWriteOutcomeUnderItsOwnCategoryWithTheTicket()
    {
        var body = MethodBody("ExecuteActionAsync");
        var successAudit = body[WriteIndex(body)..];

        Assert.Matches(
            new Regex(@"LogModuleAction\([^;]*?auditAction,\s*""IntuneDevices"",\s*target,\s*applied\.Success,\s*ticket", RegexOptions.Singleline),
            successAudit);
    }

    [Fact]
    public void IntuneDevices_ExecuteAction_NotifiesAdminsFromFinallyWrappedAgainstSendFailure()
    {
        // AC13 / Constitution, Notifications: every mutating action notifies administrators, and a
        // send failure must not change the reported result - so the send lives in finally, inside its
        // own try/catch.
        var body = MethodBody("ExecuteActionAsync");

        var finallyIndex = body.LastIndexOf("finally", StringComparison.Ordinal);
        var sendIndex = body.IndexOf("Email.SendAdminNotificationAsync", StringComparison.Ordinal);

        Assert.True(sendIndex >= 0, "ExecuteActionAsync does not notify administrators.");
        Assert.True(finallyIndex >= 0 && sendIndex > finallyIndex,
            "the admin notification is not sent from the finally block.");

        var tail = body[finallyIndex..];
        var tryIndex = tail.IndexOf("try", StringComparison.Ordinal);
        Assert.True(tryIndex >= 0 && tryIndex < tail.IndexOf("Email.SendAdminNotificationAsync", StringComparison.Ordinal),
            "the admin notification is not wrapped in a try/catch.");
        Assert.Contains("Failed to send Intune Devices admin notification", tail);
    }

    [Fact]
    public void IntuneDevices_ConfirmBar_RendersBeneathTheActingRowWithTheTicketAdjacent()
    {
        // MigrationBatchSelection-Plan.md D1 / mbs-1: a confirm bar far from the acting control reads
        // to the operator as a dead button, and a top-of-table bar puts the ticket box off-screen.
        var text = PageSource();

        var tbody = text.IndexOf("<tbody>", StringComparison.Ordinal);
        var loop = text.IndexOf("@foreach (var device in devices)", StringComparison.Ordinal);
        var confirmBar = text.IndexOf("confirmDeviceId == device.Id", StringComparison.Ordinal);

        Assert.True(tbody >= 0 && loop > tbody, "the results loop was not found inside the table body.");
        Assert.True(confirmBar > loop, "the confirm bar is not rendered per row beneath the acting row.");
        Assert.Contains("intuneDeviceTicket", text);
    }

    [Fact]
    public void IntuneDevices_PerDeviceOutcomes_AreKeyedByDeviceAndNameTheirDevice()
    {
        // Known Failure Class 2: acting on several rows must leave several separately named verdicts.
        var text = PageSource();

        Assert.Contains("Dictionary<string, DeviceOutcome> deviceOutcomes", text);
        Assert.Contains("OutcomeFor(device.Id) is { } outcome", text);
        Assert.Contains("<strong>@outcome.DeviceName</strong>", text);
    }

    [Fact]
    public void IntuneDevices_ClearsWritePathStateAtStartOfEverySearch()
    {
        // The blr-3 shape applied to the write UI: results emptied for a new search but a per-device
        // outcome or an open confirm bar left behind would attach the old verdict to whatever row now
        // occupies that id.
        var body = MethodBody("SearchAsync");

        Assert.Contains("deviceOutcomes.Clear();", body);
        Assert.Contains("confirmDeviceId = null;", body);
    }

    [Fact]
    public void IntuneDevices_DeleteButtonIsGatedOnTheGranularGrantForRenderingOnly()
    {
        var text = PageSource();

        Assert.Contains("AuthorizeAsync(user, \"IntuneDevicesDelete\")", text);
        Assert.Contains("BeginAction(device, IntuneDeviceAction.Delete)", text);
        Assert.Contains("@if (canDelete)", text);
    }

    [Fact]
    public void IntuneDevices_IsOfferedTheServicerEditorInModuleConfig()
    {
        // S3, explicitly: ProtectedServicer:IntuneDevices is only grantable if the module is on
        // ModuleConfig.razor's explicit opt-in list, and this slice is the commit that adds the
        // Evaluate call the list exists to certify. Shipping the gate without the list entry is the
        // unreachable-capability defect this repo has shipped twice (ppsvc-1, pgwt-1).
        var moduleConfig = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "ModuleConfig.razor"));

        var declaration = Regex.Match(moduleConfig,
            @"ModulesWithProtectedPrincipalServicing\s*=\s*new\([^)]*\)\s*\{(?<members>[^}]*)\}");
        Assert.True(declaration.Success, "the servicer opt-in list is no longer an explicit set literal.");
        Assert.Contains("\"IntuneDevices\"", declaration.Groups["members"].Value);

        // The pairing, asserted in both directions: the page's gate calls the servicing helper, so
        // the id must be in that set.
        Assert.Contains("ProtectedPrincipalServicing.NoteFor", PageSource());
    }

    [Fact]
    public void PolicyFor_Delete_IsTheDescriptorsGranularAlias()
    {
        Assert.Equal("IntuneDevicesDelete", IntuneDevices.PolicyFor(IntuneDeviceAction.Delete));
    }

    [Fact]
    public void AuditActionFor_Delete_FilesUnderItsOwnActionName()
    {
        Assert.Equal("IntuneDevices_Delete", IntuneDevices.AuditActionFor(IntuneDeviceAction.Delete));
    }

    [Fact]
    public void ActionLabel_Delete_IsNotABareRemove()
    {
        // MigrationBatchSelection-Plan.md D6: no bare "Remove", and no two actions share a leading
        // verb (enforced across all three once S4 lands).
        var label = IntuneDevices.ActionLabel(IntuneDeviceAction.Delete);

        Assert.Equal("Delete record", label);
        Assert.DoesNotContain("Remove", label);
    }

    [Fact]
    public void ConfirmPrompt_Delete_SaysItRemovesTheIntuneRecordOnly()
    {
        // T3 / AC11 at the moment of acting, not only in the result.
        var prompt = IntuneDevices.ConfirmPrompt(IntuneDeviceAction.Delete, "laptop-1");

        Assert.Contains("laptop-1", prompt);
        Assert.Contains("Intune record only", prompt);
        Assert.Contains("company data stays on the device", prompt);
        Assert.Contains("Entra ID object still exists", prompt);
    }

    [Fact]
    public void DeviceLabel_NoDeviceName_FallsBackToTheId()
    {
        Assert.Equal("dev-1", IntuneDevices.DeviceLabel(new ExchangeAdminWeb.Models.IntuneDevice { Id = "dev-1" }));
        Assert.Equal("laptop-1", IntuneDevices.DeviceLabel(
            new ExchangeAdminWeb.Models.IntuneDevice { Id = "dev-1", DeviceName = "laptop-1" }));
    }

    // ---- harness ------------------------------------------------------------------------------

    private static string PageSource() =>
        File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "IntuneDevices.razor"));

    /// <summary>Index of the single Graph write call inside ExecuteActionAsync's body.</summary>
    private static int WriteIndex(string body)
    {
        var calls = Regex.Matches(body, @"PerformActionAsync\(action");
        Assert.Single(calls);
        return calls[0].Index;
    }

    private static string Between(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{startMarker}' was not found.");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"'{endMarker}' was not found after '{startMarker}'.");
        return source[start..end];
    }

    /// <summary>
    /// A brace-balanced method body from the page's @code block, so a marker appearing later in
    /// the file cannot end the slice early and report a real change as missing.
    /// </summary>
    private static string MethodBody(string methodName)
    {
        var source = PageSource();
        var signature = Regex.Match(source, $@"(private|internal|protected)[^\r\n]*\b{Regex.Escape(methodName)}\(");
        Assert.True(signature.Success, $"'{methodName}' is no longer declared in IntuneDevices.razor.");

        var open = source.IndexOf('{', signature.Index);
        var arrow = source.IndexOf("=>", signature.Index, StringComparison.Ordinal);
        if (arrow >= 0 && (open < 0 || arrow < open))
        {
            // Expression-bodied member: everything up to its terminating semicolon.
            var semicolon = source.IndexOf(';', arrow);
            return source[signature.Index..(semicolon + 1)];
        }

        Assert.True(open > 0, $"no body found for '{methodName}'.");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[signature.Index..(i + 1)];
        }

        throw new InvalidOperationException($"unbalanced braces after '{methodName}'.");
    }
}
