using System.Text.RegularExpressions;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
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
    public void IntuneDevices_HasAllFourActionsEachBehindItsOwnAlias()
    {
        // S3 Delete behind IntuneDevicesDelete; S4 Retire and Wipe behind IntuneDevicesPrivileged;
        // S5 the Entra ID device object removal behind IntuneDevicesEntraDelete. Three aliases, four
        // actions - and the Entra one never rides the privileged alias (D3).
        var text = PageSource();

        Assert.Contains("DeleteDeviceAsync", text);
        Assert.Contains("RetireDeviceAsync", text);
        Assert.Contains("WipeDeviceAsync", text);
        Assert.Contains("RemoveEntraDeviceAsync", text);
        Assert.Contains("IntuneDevicesDelete\"", text);
        Assert.Contains("IntuneDevicesPrivileged\"", text);
        Assert.Contains("IntuneDevicesEntraDelete\"", text);
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

        Assert.Contains("extra: ActionAuditExtra(action, wipeOptions, servicedAuditDetail, notification.Note)", successAudit);
        Assert.DoesNotContain("errorDetail: ActionAuditExtra", successAudit);
        Assert.DoesNotContain("errorDetail: servicedAuditDetail", successAudit);
        // ActionAuditExtra is the one place the serviced note is wrapped, so the note still travels.
        Assert.Contains("ProtectedPrincipalServicing.Extra(servicedNote)", MethodBody("ActionAuditExtra"));
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

    // ---- S4: retire and wipe ------------------------------------------------------------------

    [Fact]
    public void PolicyFor_RetireAndWipe_AreTheSecondTierAliasAndNotTheDeleteOne()
    {
        // D1 / AC3: two tiers. An operator with Delete alone cannot retire or wipe, which is only
        // true if the two actions ask a different policy.
        Assert.Equal("IntuneDevicesPrivileged", IntuneDevices.PolicyFor(IntuneDeviceAction.Retire));
        Assert.Equal("IntuneDevicesPrivileged", IntuneDevices.PolicyFor(IntuneDeviceAction.Wipe));
        Assert.NotEqual(IntuneDevices.PolicyFor(IntuneDeviceAction.Delete), IntuneDevices.PolicyFor(IntuneDeviceAction.Wipe));
    }

    [Fact]
    public void AuditActionFor_RetireAndWipe_FileUnderTheirOwnActionNames()
    {
        Assert.Equal("IntuneDevices_Retire", IntuneDevices.AuditActionFor(IntuneDeviceAction.Retire));
        Assert.Equal("IntuneDevices_Wipe", IntuneDevices.AuditActionFor(IntuneDeviceAction.Wipe));
    }

    [Fact]
    public void ActionLabel_NoTwoActionsShareALeadingVerbAndNoneIsABareRemove()
    {
        // docs/MigrationBatchSelection-Plan.md D6, now assertable across all four.
        var labels = new[]
        {
            IntuneDevices.ActionLabel(IntuneDeviceAction.Delete),
            IntuneDevices.ActionLabel(IntuneDeviceAction.Retire),
            IntuneDevices.ActionLabel(IntuneDeviceAction.Wipe),
            IntuneDevices.ActionLabel(IntuneDeviceAction.EntraDelete)
        };

        Assert.Equal(["Delete record", "Retire", "Wipe", "Remove Entra ID object"], labels);
        var leadingVerbs = labels.Select(l => l.Split(' ')[0]).ToArray();
        Assert.Equal(leadingVerbs.Length, leadingVerbs.Distinct().Count());
        Assert.DoesNotContain("Remove", labels);
    }

    [Fact]
    public void ConfirmPrompt_RetireAndWipe_SayQueuedBehaviourAndTheWipeAsksForTheName()
    {
        var retire = IntuneDevices.ConfirmPrompt(IntuneDeviceAction.Retire, "laptop-1");
        var wipe = IntuneDevices.ConfirmPrompt(IntuneDeviceAction.Wipe, "laptop-1");

        Assert.Contains("next check-in", retire);
        Assert.Contains("re-enrolled", retire);
        Assert.Contains("next check-in", wipe);
        Assert.Contains("Type the device name", wipe);
    }

    [Fact]
    public void SummarizeWipeFlags_NamesEveryFlagAtTheValueSent()
    {
        // AC21: the audit event must name the exact flag set, so every parameter appears - including
        // the ones left at their defaults.
        var summary = IntuneDevices.SummarizeWipeFlags(new IntuneWipeOptions());

        Assert.Equal(
            "keepUserData=false; keepEnrollmentData=false; persistEsimDataPlan=false; "
            + "obliterationBehavior=(unset); macOsUnlockCode=(not set)",
            summary);
    }

    [Fact]
    public void SummarizeWipeFlags_KeepUserDataWipeIsDistinguishableFromAFullReset()
    {
        // The only question anyone asks afterwards. A summary that rendered these identically could
        // not answer it.
        var fullReset = IntuneDevices.SummarizeWipeFlags(new IntuneWipeOptions());
        var keepingData = IntuneDevices.SummarizeWipeFlags(new IntuneWipeOptions(KeepUserData: true));

        Assert.NotEqual(fullReset, keepingData);
        Assert.Contains("keepUserData=true", keepingData);
    }

    [Fact]
    public void SummarizeWipeFlags_MacOsUnlockCode_IsSetOrNotSetNeverItsValue()
    {
        // T4b applied to an operator-supplied secret rather than a returned one.
        var withPin = IntuneDevices.SummarizeWipeFlags(new IntuneWipeOptions(MacOsUnlockCode: "123456"));

        Assert.Contains("macOsUnlockCode=(set)", withPin);
        Assert.DoesNotContain("123456", withPin);
    }

    [Fact]
    public void SummarizeWipeFlags_ObliterationBehaviour_IsNamedWhenSet()
    {
        Assert.Contains("obliterationBehavior=alwaysObliterate",
            IntuneDevices.SummarizeWipeFlags(new IntuneWipeOptions(ObliterationBehavior: "alwaysObliterate")));
    }

    [Fact]
    public void ActionAuditExtra_Wipe_CarriesTheFlagSetAndNeverThePin()
    {
        // AC21: a wipe's audit event names the exact flag set used. The PIN's literal value appears
        // in no audit field.
        var extra = IntuneDevices.ActionAuditExtra(
            IntuneDeviceAction.Wipe,
            new IntuneWipeOptions(KeepUserData: true, MacOsUnlockCode: "123456"),
            servicedNote: null);

        Assert.NotNull(extra);
        Assert.True(extra!.ContainsKey("wipeFlags"));
        Assert.Contains("keepUserData=true", (string)extra["wipeFlags"]!);
        Assert.DoesNotContain("123456", string.Join("|", extra.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    [Fact]
    public void ActionAuditExtra_Wipe_KeepsTheServicedNoteAlongsideTheFlagSet()
    {
        var extra = IntuneDevices.ActionAuditExtra(
            IntuneDeviceAction.Wipe, new IntuneWipeOptions(), servicedNote: "serviced by group X");

        Assert.NotNull(extra);
        Assert.True(extra!.ContainsKey("wipeFlags"));
        Assert.True(extra.ContainsKey(ExchangeAdminWeb.Services.ProtectedPrincipalServicing.AuditKey));
    }

    [Fact]
    public void ActionAuditExtra_DeleteAndRetire_CarryNoWipeFlagsAndNoEmptyDictionary()
    {
        Assert.Null(IntuneDevices.ActionAuditExtra(IntuneDeviceAction.Delete, null, null));
        Assert.Null(IntuneDevices.ActionAuditExtra(IntuneDeviceAction.Retire, null, null));

        var serviced = IntuneDevices.ActionAuditExtra(IntuneDeviceAction.Retire, null, "serviced by group X");
        Assert.NotNull(serviced);
        Assert.False(serviced!.ContainsKey("wipeFlags"));
    }

    [Fact]
    public void IntuneDevices_RetireAndWipeButtonsAreGatedOnThePrivilegedGrant()
    {
        var text = PageSource();

        Assert.Contains("AuthorizeAsync(user, \"IntuneDevicesPrivileged\")", text);
        Assert.Contains("BeginAction(device, IntuneDeviceAction.Retire)", text);
        Assert.Contains("BeginAction(device, IntuneDeviceAction.Wipe)", text);
        Assert.Contains("@if (canPrivileged)", text);
    }

    [Fact]
    public void IntuneDevices_WipeRequiresTypingTheDeviceNameAndRetireDoesNot()
    {
        // The asymmetry is deliberate: retire is recoverable by re-enrolling, wipe destroys the
        // machine's contents.
        var text = PageSource();
        var guard = MethodBody("WipeNameConfirmed");

        Assert.Contains("wipeConfirmName", text);
        Assert.Contains("!WipeNameConfirmed(device, confirmAction.Value)", text);
        Assert.Contains("action != IntuneDeviceAction.Wipe", guard);
        Assert.Contains("DeviceLabel(device)", guard);
    }

    [Fact]
    public void IntuneDevices_WipePanelOffersEveryGraphParameterAsAControl()
    {
        // D2: anything that can be an option is an option, with full-reset defaults and every
        // non-default choice visible at the moment of acting.
        var text = PageSource();

        Assert.Contains("id=\"wipeKeepUserData\"", text);
        Assert.Contains("id=\"wipeKeepEnrollmentData\"", text);
        Assert.Contains("id=\"wipePersistEsimDataPlan\"", text);
        Assert.Contains("id=\"wipeMacOsUnlockCode\"", text);
        Assert.Contains("id=\"wipeObliterationBehavior\"", text);
        foreach (var behaviour in new[] { "default", "doNotObliterate", "obliterateWithWarning", "alwaysObliterate" })
            Assert.Contains($"value=\"{behaviour}\"", text);

        // Keeping user data contradicts the button's own label, so the panel says so inline.
        Assert.Contains("contradicts the button's own label", text);
    }

    [Fact]
    public void IntuneDevices_MacOsRecoveryPinIsShownBackOnceAfterASuccessfulQueue()
    {
        // Plan S4: the operator must give it to the device owner and it is not retrievable
        // afterwards - but it is shown only where the wipe was actually queued.
        var text = PageSource();
        var body = MethodBody("ExecuteActionAsync");

        Assert.Contains("outcome.MacOsUnlockCodeShownOnce != null", text);
        Assert.Contains("cannot be retrieved afterwards", text);
        Assert.Contains("applied.Success ? wipeOptions?.MacOsUnlockCode : null", body);
    }

    [Fact]
    public void IntuneDevices_WipeOptionsAreBoundBeforeTheWriteAndResetAfterIt()
    {
        var body = MethodBody("ExecuteActionAsync");

        var bindIndex = body.IndexOf("var wipeOptions = action == IntuneDeviceAction.Wipe ? CurrentWipeOptions() : null;", StringComparison.Ordinal);
        Assert.True(bindIndex >= 0, "the wipe flag set is no longer bound before the request.");
        Assert.True(WriteIndex(body) > bindIndex, "the wipe flag set is bound after the write.");

        // The first `finally` after the write, not the last mention of the word - a later comment
        // saying "finally" would otherwise move the window past the code being asserted.
        var finallyIndex = body.IndexOf("finally", WriteIndex(body), StringComparison.Ordinal);
        Assert.True(finallyIndex > 0, "ExecuteActionAsync no longer has a finally block after the write.");
        Assert.Contains("ResetActionOptions();", body[finallyIndex..]);
    }

    [Fact]
    public void IntuneDevices_AdminNotificationForAWipeNamesTheFlagSet()
    {
        var details = MethodBody("NotificationDetails");

        Assert.Contains("SummarizeWipeFlags(wipeOptions)", details);
        Assert.DoesNotContain("MacOsUnlockCode", details);
    }

    // ---- S5: the Entra ID device object -------------------------------------------------------

    [Fact]
    public void PolicyFor_EntraDelete_IsItsOwnAliasAndNotThePrivilegedOne()
    {
        // D3 / AC3b: an operator with Privileged can wipe and cannot remove directory records, which
        // is only true if the Entra action asks a different policy.
        Assert.Equal("IntuneDevicesEntraDelete", IntuneDevices.PolicyFor(IntuneDeviceAction.EntraDelete));
        Assert.NotEqual(
            IntuneDevices.PolicyFor(IntuneDeviceAction.Wipe),
            IntuneDevices.PolicyFor(IntuneDeviceAction.EntraDelete));
        Assert.NotEqual(
            IntuneDevices.PolicyFor(IntuneDeviceAction.Delete),
            IntuneDevices.PolicyFor(IntuneDeviceAction.EntraDelete));
    }

    [Fact]
    public void AuditActionFor_EntraDelete_FilesUnderItsOwnActionName()
    {
        Assert.Equal("IntuneDevices_EntraDelete", IntuneDevices.AuditActionFor(IntuneDeviceAction.EntraDelete));
    }

    [Fact]
    public void ConfirmPrompt_EntraDelete_SaysItRemovesTheDirectoryRecordOnly()
    {
        var prompt = IntuneDevices.ConfirmPrompt(IntuneDeviceAction.EntraDelete, "laptop-1");

        Assert.Contains("laptop-1", prompt);
        Assert.Contains("directory record only", prompt);
        Assert.Contains("authenticates", prompt);
    }

    [Fact]
    public void IntuneDevices_ExecuteAction_CapturesTheEntraDeviceIdBeforeTheIntuneWrite()
    {
        // The ordering pinned by S5 and Known Failure Class 2: after a successful delete of the
        // Intune record there is nothing left to read azureADDeviceId from, so it must be captured
        // BEFORE the Intune action runs. Moving the capture after the write must fail this.
        var body = MethodBody("ExecuteActionAsync");

        var captureIndex = body.IndexOf("var entraDeviceId = device.AzureADDeviceId;", StringComparison.Ordinal);
        Assert.True(captureIndex >= 0, "the Entra device id is no longer captured in ExecuteActionAsync.");
        Assert.True(WriteIndex(body) > captureIndex,
            "the Entra device id is captured AFTER the Intune write - a deleted Intune record cannot be read.");

        // And it is never re-read from the device afterwards, which would be the same defect with an
        // extra step.
        Assert.DoesNotContain("device.AzureADDeviceId", body[WriteIndex(body)..]);
    }

    [Fact]
    public void IntuneDevices_ExecuteAction_RunsTheEntraRemovalAfterTheIntuneActionAndOnlyOnSuccess()
    {
        var body = MethodBody("ExecuteActionAsync");

        var entraIndex = body.IndexOf("RemoveEntraObjectAsync(authState.User, entraDeviceId)", StringComparison.Ordinal);
        Assert.True(entraIndex > WriteIndex(body), "the Entra removal does not run after the Intune action.");
        // Conditional on the first step succeeding: the operator's intent was conditional, and
        // removing the directory object after a failed retire leaves a device that cannot
        // authenticate and still holds company data.
        Assert.Contains("if (removeEntraObject && applied.Success)", body);
    }

    [Fact]
    public void IntuneDevices_ExecuteAction_ReChecksTheEntraGrantImmediatelyBeforeTheEntraWrite()
    {
        // T6 for the SECOND write and its own, wider permission: it is never inherited from the
        // Intune action's check at step 1. Removing this re-check must fail here.
        var body = MethodBody("RemoveEntraObjectAsync");

        var checkIndex = body.IndexOf("AuthorizeAsync(user, PolicyFor(IntuneDeviceAction.EntraDelete))", StringComparison.Ordinal);
        Assert.True(checkIndex >= 0, "the Entra half no longer re-checks IntuneDevicesEntraDelete before writing.");

        var writeIndex = body.IndexOf("RemoveEntraDeviceAsync(entraDeviceId)", StringComparison.Ordinal);
        Assert.True(writeIndex > checkIndex, "the Entra grant is re-checked after the Entra write.");
        Assert.Contains("!entraCheck.Succeeded", body);
    }

    [Fact]
    public void IntuneDevices_ExecuteAction_EntraHalfNeverThrowsIntoTheIntuneAuditPath()
    {
        // Known Failure Class 1: an exception escaping the Entra half would reach
        // ExecuteActionAsync's catch, which Refuses - reporting the whole action as failed even
        // though the Intune half succeeded, and skipping the Intune audit write entirely.
        var body = MethodBody("RemoveEntraObjectAsync");

        Assert.Contains("catch (Exception ex)", body);
        Assert.Contains("could not be removed", body);
        Assert.DoesNotContain("throw", body);
    }

    [Fact]
    public void IntuneDevices_ExecuteAction_ReportsTheTwoHalvesSeparatelyAndNeverAsOneVerdict()
    {
        // AC23: an Intune action that succeeded followed by an Entra removal that failed must report
        // BOTH outcomes. Merging them into one message or one audit event must fail this.
        var text = PageSource();
        var body = MethodBody("ExecuteActionAsync");

        // Two distinct outcome fields on the record, not one message with text appended.
        Assert.Contains("string? EntraMessage = null, bool? EntraSuccess = null", text);
        Assert.Contains("entraResult?.Message, entraResult?.Success", body);

        // Two audit events after the write: the Intune action's own name, and the Entra one's.
        var afterWrite = body[WriteIndex(body)..];
        Assert.Matches(
            new Regex(@"LogModuleAction\([^;]*?auditAction,\s*""IntuneDevices"",\s*target,\s*applied\.Success", RegexOptions.Singleline),
            afterWrite);
        Assert.Matches(
            new Regex(@"LogModuleAction\([^;]*?AuditActionFor\(IntuneDeviceAction\.EntraDelete\),\s*""IntuneDevices"",\s*EntraAuditTarget\(", RegexOptions.Singleline),
            afterWrite);
        Assert.Equal(2, Regex.Matches(afterWrite, @"Audit\.LogModuleAction\(").Count);

        // And on screen: its own alert, with its own success styling.
        Assert.Contains("outcome.EntraMessage != null", text);
        Assert.Contains("outcome.EntraSuccess == true", text);
    }

    [Fact]
    public void IntuneDevices_ExecuteAction_EntraHalfGetsItsOwnAdminNotification()
    {
        // AC13: an administrator notification on every audited action, and two audited actions here.
        var body = MethodBody("ExecuteActionAsync");
        var finallyIndex = body.LastIndexOf("finally", StringComparison.Ordinal);
        var tail = body[finallyIndex..];

        Assert.Equal(2, Regex.Matches(tail, @"Email\.SendAdminNotificationAsync\(").Count);
        Assert.Contains("AuditActionFor(IntuneDeviceAction.EntraDelete), entraResult.Success", tail);
        Assert.Contains("Failed to send Intune Devices Entra removal admin notification", tail);
    }

    [Fact]
    public void IntuneMessageFor_DeleteWithTheEntraObjectRemoved_StopsClaimingItSurvives()
    {
        // AC11's conditional half: the standing claim is false once the Entra removal succeeded, and
        // a result that kept making it would tell the operator the opposite of what happened.
        var withEntra = IntuneDevices.IntuneMessageFor(
            IntuneDeviceAction.Delete, IntuneDeviceService.DeleteSuccessMessage, entraRemoved: true);
        var withoutEntra = IntuneDevices.IntuneMessageFor(
            IntuneDeviceAction.Delete, IntuneDeviceService.DeleteSuccessMessage, entraRemoved: false);

        Assert.DoesNotContain("Entra ID object still exists", withEntra);
        Assert.Contains("Entra ID object was removed as well", withEntra);
        Assert.Equal(IntuneDeviceService.DeleteSuccessMessage, withoutEntra);

        // Retire and wipe never make that claim, so their messages pass through untouched.
        Assert.Equal("queued", IntuneDevices.IntuneMessageFor(IntuneDeviceAction.Retire, "queued", entraRemoved: true));
        Assert.Equal("queued", IntuneDevices.IntuneMessageFor(IntuneDeviceAction.Wipe, "queued", entraRemoved: true));
    }

    [Fact]
    public void EntraAuditTarget_NamesTheDeviceAndTheDeviceIdUsed()
    {
        // S5: the audit target is the device name plus the deviceId the DELETE actually addressed.
        var target = IntuneDevices.EntraAuditTarget("laptop-1", "6fa60d52-01e7-4b18-8fc7-8f9d1b9b1a5c");

        Assert.Contains("laptop-1", target);
        Assert.Contains("6fa60d52-01e7-4b18-8fc7-8f9d1b9b1a5c", target);
        Assert.Contains("(none)", IntuneDevices.EntraAuditTarget("laptop-1", "  "));
    }

    [Fact]
    public void IntuneDevices_EntraCheckboxStartsUntickedAndIsGatedOnTheGrantForRenderingOnly()
    {
        // AC18's shape for this option, under the owner ruling of 2026-09-02: the starting state is
        // fixed in code (unticked) rather than read from Module Config, the operator opts in at the
        // moment of acting, and the rendering gate is not the decision.
        var text = PageSource();

        Assert.Contains("AuthorizeAsync(user, \"IntuneDevicesEntraDelete\")", text);
        Assert.Contains("@if (confirmAction != IntuneDeviceAction.EntraDelete && canEntraDelete)", text);
        Assert.Contains("@bind=\"alsoRemoveEntra\"", text);
        Assert.Contains(
            "alsoRemoveEntra = IntuneDeviceService.EntraRemovalStartsTicked;",
            MethodBody("BeginAction"));
        Assert.False(IntuneDeviceService.EntraRemovalStartsTicked);
        // Reset with every other per-confirm option, so a choice made for one device never rides
        // along into the next one.
        Assert.Contains("alsoRemoveEntra = false;", MethodBody("ResetActionOptions"));
    }

    [Fact]
    public void IntuneDevices_DeviceWithNoUsableEntraId_IsNotOfferedTheOptionAndIsToldWhy()
    {
        // AC24: not offered-and-silently-skipped. The reason is on screen, in both places the option
        // would otherwise appear.
        var text = PageSource();

        Assert.Contains("EntraIdUsable(device)", text);
        Assert.Contains("EntraIdUsable(detailDevice)", text);
        Assert.Contains("no usable Entra ID device id", text);
        Assert.Contains("IntuneDeviceService.IsUsableEntraDeviceId(device.AzureADDeviceId)", text);
    }

    [Fact]
    public void IntuneDevices_EntraRemovalIsRunnableOnItsOwnFromTheDetailPanel()
    {
        // S5: the Entra record often outlives the Intune one, so an operator cleaning up must not
        // have to run a second Intune action to reach it. Same handler, so the same gate chain.
        var text = PageSource();

        Assert.Contains("BeginAction(device, IntuneDeviceAction.EntraDelete)", text);
        Assert.Contains("Remove Entra ID object</button>", text);
    }

    // ---- S6: affected-user notification -------------------------------------------------------

    [Fact]
    public void NotificationOffered_TheThreeIntuneActionsOfferItAndTheEntraRemovalDoesNot()
    {
        // Exactly the three Intune actions offer it. The standalone Entra removal changes nothing
        // the user can observe on their device.
        Assert.True(IntuneDevices.NotificationOffered(IntuneDeviceAction.Delete));
        Assert.True(IntuneDevices.NotificationOffered(IntuneDeviceAction.Retire));
        Assert.True(IntuneDevices.NotificationOffered(IntuneDeviceAction.Wipe));
        Assert.False(IntuneDevices.NotificationOffered(IntuneDeviceAction.EntraDelete));
    }

    [Fact]
    public void IntuneDevices_NotifyCheckboxStartsFromTheFixedPerActionStateAndIsOverridableAtActTime()
    {
        // AC18 under the owner ruling of 2026-09-02: THIS action's fixed starting state seeds the
        // box - no Module Config read - and the operator's change at the moment of acting is what
        // takes effect, so the choice is bound with the request.
        var text = PageSource();

        Assert.Contains("@bind=\"notifyPrimaryUser\"", text);
        Assert.Contains("notifyPrimaryUser = IntuneDeviceService.NotifyUserStartsTicked(action) ?? false;", MethodBody("BeginAction"));
        Assert.Contains("notifyPrimaryUser = false;", MethodBody("ResetActionOptions"));

        var body = MethodBody("ExecuteActionAsync");
        var bindIndex = body.IndexOf("var notifyRequested = notifyPrimaryUser;", StringComparison.Ordinal);
        Assert.True(bindIndex >= 0, "the notification choice is no longer bound with the request.");
        Assert.True(WriteIndex(body) > bindIndex, "the notification choice is bound after the write.");
        // The starting state travels too, so the audit can say WHICH not-sent reason applied.
        Assert.Contains("var notifyDefault = IntuneDeviceService.NotifyUserStartsTicked(action) ?? false;", body);
    }

    [Fact]
    public void IntuneDevices_SendsTheUserNotificationThroughEmailServiceAfterTheWrite()
    {
        // The send lives in NotifyPrimaryUserAsync and runs after the device action, because there is
        // nothing to tell the user about until it happened.
        var body = MethodBody("ExecuteActionAsync");
        var notifyIndex = body.IndexOf("await NotifyPrimaryUserAsync(", StringComparison.Ordinal);

        Assert.True(notifyIndex > WriteIndex(body), "the user notification does not run after the write.");
        Assert.Contains("Email.SendDeviceActionUserNotificationAsync(", MethodBody("NotifyPrimaryUserAsync"));
    }

    [Fact]
    public void IntuneDevices_SuppressedSendIsStatedOnScreenNotOnlyInTheAuditLog()
    {
        // AC19 / the S6 trap: EmailService's app-wide _notifyUsers switch outranks this module, so a
        // ticked box on a deployment with user notifications off must SAY nothing was sent - on
        // screen as well as in the audit event. Without this the checkbox silently does nothing,
        // which is the decorative-control defect from the other direction. Removing either the
        // outcome note or the confirm-time warning must fail here.
        var text = PageSource();

        Assert.Contains("outcome.NotificationNote != null", text);
        Assert.Contains("@outcome.NotificationNote", text);
        Assert.Contains("outcome.NotificationSent", text);
        Assert.Contains("@if (!Email.UserNotificationsEnabled)", text);
        Assert.Contains("Nothing will be sent even if this is ticked", text);
        // The page reads the switch from EmailService - the same field that gates the send - so the
        // statement and the send cannot disagree.
        Assert.Contains("Email.UserNotificationsEnabled", MethodBody("NotifyPrimaryUserAsync"));
    }

    [Fact]
    public void IntuneDevices_NotificationOutcomeIsRecordedInTheAuditEvent()
    {
        // AC19 / AC20: notified, or not notified and why - the same sentence the operator saw.
        var extra = MethodBody("ActionAuditExtra");

        Assert.Contains("extra[\"userNotification\"] = userNotificationNote;", extra);
        Assert.Contains("userNotificationNote", MethodBody("ActionAuditExtra"));

        // And it reaches that audit event: the notification is attempted BEFORE the audit write.
        var body = MethodBody("ExecuteActionAsync");
        var notifyIndex = body.IndexOf("await NotifyPrimaryUserAsync(", StringComparison.Ordinal);
        var auditIndex = body.IndexOf("extra: ActionAuditExtra(action, wipeOptions, servicedAuditDetail, notification.Note)",
            StringComparison.Ordinal);
        Assert.True(notifyIndex >= 0 && auditIndex > notifyIndex,
            "the audit event is written before the notification outcome exists, so it cannot record it.");
    }

    [Fact]
    public void IntuneDevices_NotificationFailureNeverChangesTheReportedResult()
    {
        // Constitution, Notifications / plan S6: the device is already wiped by then, so a mail
        // failure is caught and logged and the action still reports what it did.
        var body = MethodBody("NotifyPrimaryUserAsync");

        Assert.Contains("catch (Exception ex)", body);
        Assert.Contains("could not be sent", body);
        Assert.Contains("The action itself completed.", body);
        Assert.DoesNotContain("throw", body);
    }

    [Fact]
    public void IntuneDevices_DeviceWithNoPrimaryUserAddress_IsNotOfferedTheNotification()
    {
        // AC20: not offered, and the reason is on screen - never offered-and-silently-skipped.
        var text = PageSource();

        Assert.Contains("@if (NotificationOffered(confirmAction.Value))", text);
        Assert.Contains("string.IsNullOrWhiteSpace(device.UserPrincipalName)", text);
        Assert.Contains("Intune holds no primary user address for it", text);
    }

    [Fact]
    public void IntuneDevices_FailedActionWithATickedBox_StillSaysNothingWasSent()
    {
        // A ticked box whose action failed must still get an answer, or the operator is left
        // assuming their user was told.
        var body = MethodBody("NotifyPrimaryUserAsync");

        Assert.Contains("if (!actionSucceeded)", body);
        Assert.Contains("the action did not succeed", body);
    }

    [Fact]
    public void DeviceLabel_NoDeviceName_FallsBackToTheId()
    {
        Assert.Equal("dev-1", IntuneDevices.DeviceLabel(new ExchangeAdminWeb.Models.IntuneDevice { Id = "dev-1" }));
        Assert.Equal("laptop-1", IntuneDevices.DeviceLabel(
            new ExchangeAdminWeb.Models.IntuneDevice { Id = "dev-1", DeviceName = "laptop-1" }));
    }

    // ---- plain-language action help (owner finding 2026-09-02) --------------------------------

    /// <summary>
    /// The exact operator-facing wording, one test per action. Exact-string assertions on purpose:
    /// this text is the whole deliverable of the owner's finding - it is written for L2 support
    /// desk staff and deliberately simpler than Microsoft's - so a silent reword must fail here
    /// rather than reach the support desk unreviewed.
    /// </summary>
    [Fact]
    public void ActionHelp_Delete_SaysIntuneOnlyAndThatCompanyDataStays()
    {
        Assert.Equal(
            "Removes the device from Intune only. Company data and apps stay on the device until it next checks in, "
            + "and if it never checks in, they stay forever. The device's Entra ID entry is not touched. "
            + "Use this to clean up a device that is already gone.",
            IntuneDevices.ActionHelp(IntuneDeviceAction.Delete));
    }

    [Fact]
    public void ActionHelp_Retire_SaysCompanyDataGoesPersonalStaysAndItWaitsForCheckIn()
    {
        Assert.Equal(
            "Tells the device to remove company data, apps and settings, and to leave Intune management. "
            + "Personal data stays. Happens the next time the device checks in, which for a powered-off device may "
            + "be never. Use this for a device leaving the company that the person keeps.",
            IntuneDevices.ActionHelp(IntuneDeviceAction.Retire));
    }

    [Fact]
    public void ActionHelp_Wipe_SaysFactoryResetAndCannotBeUndone()
    {
        Assert.Equal(
            "Factory reset. Everything on the device is erased, personal and company, the next time it checks in. "
            + "Cannot be undone. Use this for a lost, stolen, or reassigned device.",
            IntuneDevices.ActionHelp(IntuneDeviceAction.Wipe));
    }

    [Fact]
    public void ActionHelp_EntraDelete_SaysItIsSeparateFromIntuneAndCostsTheDeviceItsSignIn()
    {
        Assert.Equal(
            "Also deletes the device's entry in Entra ID, which is separate from Intune. Do this only when the "
            + "device is gone for good; a device still in use will lose its sign-in and may need to be re-joined.",
            IntuneDevices.ActionHelp(IntuneDeviceAction.EntraDelete));
    }

    [Fact]
    public void NotifyUserHelp_SaysWhatTheMailTellsTheUser()
    {
        Assert.Equal(
            "Sends the device's primary user a plain notice that the action was taken and which ticket authorized it.",
            IntuneDevices.NotifyUserHelp);
    }

    /// <summary>
    /// The tooltip text (one sentence) and the confirmation-bar text (two) are DERIVED from the
    /// block, not second copies of it - a reworded block cannot leave a stale tooltip behind.
    /// </summary>
    [Fact]
    public void ActionHelpSummary_FirstSentence_IsTheTooltipText()
    {
        Assert.Equal("Removes the device from Intune only.",
            IntuneDevices.ActionHelpSummary(IntuneDeviceAction.Delete, 1));
        Assert.Equal("Factory reset.",
            IntuneDevices.ActionHelpSummary(IntuneDeviceAction.Wipe, 1));
    }

    [Fact]
    public void ActionHelpSummary_FirstTwoSentences_IsTheConfirmationBarText()
    {
        Assert.Equal(
            "Removes the device from Intune only. Company data and apps stay on the device until it next checks in, "
            + "and if it never checks in, they stay forever.",
            IntuneDevices.ActionHelpSummary(IntuneDeviceAction.Delete, 2));
        Assert.Equal(
            "Factory reset. Everything on the device is erased, personal and company, the next time it checks in.",
            IntuneDevices.ActionHelpSummary(IntuneDeviceAction.Wipe, 2));
    }

    /// <summary>
    /// A block with fewer sentences than asked for returns whole rather than empty or truncated -
    /// the Entra text is two sentences and the notification text is one, so both hit this path.
    /// </summary>
    [Fact]
    public void FirstSentences_FewerSentencesThanAsked_ReturnsTheWholeText()
    {
        Assert.Equal(IntuneDevices.ActionHelp(IntuneDeviceAction.EntraDelete),
            IntuneDevices.ActionHelpSummary(IntuneDeviceAction.EntraDelete, 2));
        Assert.Equal(IntuneDevices.NotifyUserHelp, IntuneDevices.FirstSentences(IntuneDevices.NotifyUserHelp, 1));
    }

    [Fact]
    public void ActionHelp_EveryActionHasText_NoneEmpty()
    {
        foreach (var action in Enum.GetValues<IntuneDeviceAction>())
            Assert.False(string.IsNullOrWhiteSpace(IntuneDevices.ActionHelp(action)),
                $"no help text for {action}.");
    }

    /// <summary>
    /// Source guard: the help panel exists, is CLOSED by default, and renders all five blocks from
    /// the helpers above rather than from hand-copied markup.
    /// </summary>
    [Fact]
    public void IntuneDevices_RendersACollapsibleActionHelpPanelClosedByDefault()
    {
        var text = PageSource();

        Assert.Contains("What do these actions do?", text);
        Assert.Contains("id=\"intuneActionHelp\"", text);
        // Closed by default: the flag it is bound to starts false, and the open state is the
        // conditional branch rather than the literal class.
        Assert.Contains("private bool showActionHelp;", text);
        Assert.Contains("showActionHelp ? \"collapse show\" : \"collapse\"", text);

        var panel = Between(text, "id=\"intuneActionHelp\"", "</dl>");
        Assert.Contains("ActionHelp(IntuneDeviceAction.Delete)", panel);
        Assert.Contains("ActionHelp(IntuneDeviceAction.Retire)", panel);
        Assert.Contains("ActionHelp(IntuneDeviceAction.Wipe)", panel);
        Assert.Contains("ActionHelp(IntuneDeviceAction.EntraDelete)", panel);
        Assert.Contains("NotifyUserHelp", panel);
    }

    /// <summary>
    /// Source guard: this app ships Bootstrap's stylesheet with no Bootstrap JS, so the panel must
    /// NOT be toggled by data-bs-toggle - that would render a button that does nothing.
    /// </summary>
    [Fact]
    public void IntuneDevices_ActionHelpPanel_IsToggledFromCSharpNotByBootstrapJs()
    {
        var text = PageSource();

        // The attribute form, not the string: the page's own comment explains why data-bs-toggle
        // is not used, and a bare substring check would fire on that explanation.
        Assert.DoesNotContain("data-bs-toggle=", text);
        Assert.DoesNotContain("data-bs-target=", text);
        Assert.Contains("showActionHelp = !showActionHelp", text);
    }

    /// <summary>Source guard: each action button carries its first help sentence as a tooltip.</summary>
    [Fact]
    public void IntuneDevices_EveryActionButton_CarriesItsHelpSentenceAsATitleTooltip()
    {
        var text = PageSource();

        Assert.Contains("title=\"@ActionHelpSummary(IntuneDeviceAction.Delete, 1)\"", text);
        Assert.Contains("title=\"@ActionHelpSummary(IntuneDeviceAction.Retire, 1)\"", text);
        Assert.Contains("title=\"@ActionHelpSummary(IntuneDeviceAction.Wipe, 1)\"", text);
    }

    /// <summary>
    /// Source guard: the confirmation bar states the consequence in plain words ABOVE the ticket
    /// and confirm controls, so the operator reads it at the moment of committing.
    /// </summary>
    [Fact]
    public void IntuneDevices_ConfirmationBar_ShowsThePlainLanguageConsequenceAboveTheTicketBox()
    {
        var confirmBar = Between(
            PageSource(),
            "confirmDeviceId == device.Id && confirmAction is not null",
            "Ticket number (required)");

        Assert.Contains("ActionHelpSummary(confirmAction.Value, 2)", confirmBar);
    }

    /// <summary>
    /// Source guard for the owner's search finding: the box must SAY that the two name fields
    /// match from the start and that a serial must be exact, because
    /// IntuneDeviceService.BuildFilterExpression cannot do a substring match on this resource.
    /// </summary>
    [Fact]
    public void IntuneDevices_SearchBox_SaysItMatchesTheStartOfANameAndAnExactSerial()
    {
        var searchCard = Between(PageSource(), "for=\"searchTerm\"", "@onclick=\"SearchAsync\"");

        Assert.Contains("Start of a device name or user principal name, or an exact serial number", searchCard);
        Assert.Contains("match from the START of the value", searchCard);
        Assert.Contains("A serial number must be exact.", searchCard);
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
