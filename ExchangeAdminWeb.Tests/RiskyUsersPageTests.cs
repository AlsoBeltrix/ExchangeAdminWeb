using System.Text.RegularExpressions;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Services;
using Xunit;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the RiskyUsers page (docs/RiskyUsersModule-Plan.md, S3 read UI and S6 write UI): the
/// pure projectors, and the source-text wiring for the D2 audit-only read shape, the hasQueried
/// three-state rule, the visible truncation notice, and every S6 write gate - remediate
/// permission, mandatory ticket, the two-branch protected-principal check BEFORE the write,
/// fail-closed refusals, the servicer grant, per-path audit, and the admin notification.
/// </summary>
/// <remarks>
/// Source-text guards, explicitly NOT behavioural coverage: there is no bUnit harness in this repo
/// (plan, Verification), so no test can render the page or observe which branch a handler takes.
/// Stated as tripwires so a green suite is never read as proof the page behaves correctly.
/// </remarks>
public class RiskyUsersPageTests
{
    [Fact]
    public void DescribeFilter_NoFieldsSet_ReturnsNoFilterMarker()
    {
        var target = RiskyUsers.DescribeFilter(new RiskyUserFilter(null, null, null));

        Assert.Equal("(no filter)", target);
    }

    [Fact]
    public void DescribeFilter_CombinesGivenFields()
    {
        var target = RiskyUsers.DescribeFilter(new RiskyUserFilter("high", "atRisk", "contoso"));

        Assert.Equal("riskLevel=high;riskState=atRisk;upnContains=contoso", target);
    }

    [Fact]
    public void DescribeFilter_BlankFieldsAreOmitted()
    {
        var target = RiskyUsers.DescribeFilter(new RiskyUserFilter("", "  ", null));

        Assert.Equal("(no filter)", target);
    }

    [Theory]
    [InlineData("high", "bg-danger")]
    [InlineData("medium", "bg-warning text-dark")]
    [InlineData("low", "bg-info text-dark")]
    [InlineData("hidden", "bg-secondary")]
    [InlineData("none", "bg-success")]
    [InlineData("HIGH", "bg-danger")]
    public void RiskLevelBadgeClass_DocumentedValue_MapsToExpectedBadge(string riskLevel, string expected)
    {
        Assert.Equal(expected, RiskyUsers.RiskLevelBadgeClass(riskLevel));
    }

    [Fact]
    public void RiskLevelBadgeClass_UnknownFutureValue_FallsBackToNeutralBadge()
    {
        // riskLevel/riskState are stored as plain strings and must still render with a neutral
        // badge rather than being dropped or miscategorized (S2 rule 4 / AC5). Both Microsoft's
        // own placeholder and an entirely undocumented literal must land here.
        Assert.Equal("bg-light text-dark border", RiskyUsers.RiskLevelBadgeClass("unknownFutureValue"));
        Assert.Equal("bg-light text-dark border", RiskyUsers.RiskLevelBadgeClass("somethingNewMicrosoftAdded"));
    }

    [Fact]
    public void RiskyUsers_ReadPath_NeverAlertEmails()
    {
        // D2 (owner, 2026-08-31): reads are audited, never alert-emailed - while a mutating action
        // MUST notify administrators (Constitution, Notifications). Before S6 this was a whole-file
        // "EmailService does not appear" assertion; the write path legitimately injects and sends,
        // so the guard is now scoped to the two READ handlers instead of weakened away. AC17's
        // audit-only shape still fails the instant an alert is wired into a query.
        var read = MethodBody("LoadRiskyUsersAsync") + MethodBody("ToggleHistoryAsync");

        Assert.DoesNotContain("Email.", read);
        Assert.DoesNotContain("SendAdminNotificationAsync", read);
        // ...and the read handlers do still audit, which is the other half of the ruling.
        Assert.Contains("LogModuleAction", read);
    }

    [Fact]
    public void RiskyUsers_AuditsListQueryOnBothSuccessAndFailure()
    {
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "RiskyUsers.razor"));

        var calls = Regex.Matches(
            text,
            @"LogModuleAction\(\s*[^;]*?""RiskyUsers_List""\s*,\s*""RiskyUsers""",
            RegexOptions.Singleline);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public void RiskyUsers_AuditsHistoryQueryOnBothSuccessAndFailure()
    {
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "RiskyUsers.razor"));

        var calls = Regex.Matches(
            text,
            @"LogModuleAction\(\s*[^;]*?""RiskyUsers_History""\s*,\s*""RiskyUsers""",
            RegexOptions.Singleline);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public void RiskyUsers_ClearsHasQueriedAtStartOfEveryQuery()
    {
        // blr-3 class defect: a second query must retract the first query's verdict before the
        // new one resolves, not only on the page's first ever query.
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "RiskyUsers.razor"));

        var start = text.IndexOf("private async Task LoadRiskyUsersAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0, "LoadRiskyUsersAsync method not found in RiskyUsers.razor.");
        var end = text.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        var body = end > start ? text[start..end] : text[start..];

        Assert.Contains("hasQueried = false;", body);
        Assert.Contains("await Task.Yield();", body);
        Assert.Contains("hasQueried = true;", body);
    }

    [Fact]
    public void RiskyUsers_RendersVisibleTruncationNotice()
    {
        // AC7: a response carrying @odata.nextLink must render a visible truncation notice naming
        // the cap - a silently truncated risky-user list is the BitLocker cap-before-match defect
        // class recurring on a security surface.
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "RiskyUsers.razor"));

        Assert.Contains("@if (truncated)", text);
        Assert.Contains("more exist. Narrow the filter.", text);
    }

    [Fact]
    public void RiskyUsers_DisplaysModuleVersionNextToHeading()
    {
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "RiskyUsers.razor"));

        Assert.Contains("<ModuleVersion", text);
    }

    // ---- S6: pure projectors for the write UI --------------------------------------------------

    [Theory]
    [InlineData(RiskyUserAction.Dismiss, "RiskyUsers_Dismiss")]
    [InlineData(RiskyUserAction.ConfirmSafe, "RiskyUsers_ConfirmSafe")]
    [InlineData(RiskyUserAction.ConfirmCompromised, "RiskyUsers_ConfirmCompromised")]
    public void AuditActionFor_FilesEachActionUnderItsOwnName(RiskyUserAction action, string expected)
    {
        Assert.Equal(expected, RiskyUsers.AuditActionFor(action));
    }

    [Fact]
    public void AuditActionFor_NeverGivesTwoActionsTheSameName()
    {
        // A shared audit name would make a dismissal indistinguishable from a confirm-compromised
        // in the audit trail, which is the one distinction this module's records exist for.
        var names = Enum.GetValues<RiskyUserAction>().Select(RiskyUsers.AuditActionFor).ToArray();

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AuditActionFor_UnknownMember_ThrowsRatherThanFilingUnderAGenericName()
    {
        // Fail loud: a member added to RiskyUserAction without updating this map must not file
        // silently under a name that hides which action ran.
        Assert.Throws<ArgumentOutOfRangeException>(() => { RiskyUsers.AuditActionFor((RiskyUserAction)99); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { RiskyUsers.ActionLabel((RiskyUserAction)99); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { RiskyUsers.ActionConsequence((RiskyUserAction)99); });
    }

    // ---- L2-plain wording (owner ruling 2026-09-02) --------------------------------------------
    //
    // The Dismiss / Confirm safe / Confirm compromised vocabulary is Microsoft's own, and the
    // owner ruled it ambiguous for L2 support desk staff. These pin the exact approved strings so
    // a future edit cannot drift the button label or its consequence line without failing loud.

    [Theory]
    [InlineData(RiskyUserAction.Dismiss, "Close as handled")]
    [InlineData(RiskyUserAction.ConfirmSafe, "This was the real user")]
    [InlineData(RiskyUserAction.ConfirmCompromised, "Account was breached")]
    public void ActionLabel_MatchesTheOwnerApprovedL2Wording(RiskyUserAction action, string expected)
    {
        Assert.Equal(expected, RiskyUsers.ActionLabel(action));
    }

    [Theory]
    [InlineData(RiskyUserAction.Dismiss,
        "Clears the alert. Nothing is reported as right or wrong; it can fire again.")]
    [InlineData(RiskyUserAction.ConfirmSafe,
        "Clears the alert and tells the risk engine this activity is normal for them. Only if you have verified with the user.")]
    [InlineData(RiskyUserAction.ConfirmCompromised,
        "Raises the user to high risk. Their sign-in will be blocked or forced to reset their password, immediately.")]
    public void ActionConsequence_MatchesTheOwnerApprovedConfirmationLine(RiskyUserAction action, string expected)
    {
        Assert.Equal(expected, RiskyUsers.ActionConsequence(action));
    }

    [Fact]
    public void ConfirmPrompt_NamesBothTheActionAndTheUser()
    {
        // The confirmation step must not be satisfiable without seeing which user it applies to
        // (S6 item 3).
        var prompt = RiskyUsers.ConfirmPrompt(RiskyUserAction.ConfirmCompromised, "risky@contoso.com", "atRisk");

        Assert.Contains("Account was breached", prompt);
        Assert.Contains("risky@contoso.com", prompt);
        Assert.Contains("atRisk", prompt);
    }

    [Fact]
    public void ConfirmPrompt_BlankRiskState_StillRendersAndSaysUnknown()
    {
        var prompt = RiskyUsers.ConfirmPrompt(RiskyUserAction.Dismiss, "risky@contoso.com", "");

        Assert.Contains("Close as handled", prompt);
        Assert.Contains("unknown", prompt);
    }

    // ---- S6: write-gate tripwires -------------------------------------------------------------

    [Fact]
    public void RiskyUsers_ActionControls_AreRenderedOnlyWithTheRemediateGrant()
    {
        // AC13, rendering half: the granular grant is read at page load and gates the action cell.
        var text = PageSource();

        Assert.Contains(
            "canRemediate = (await AuthorizationService.AuthorizeAsync(user, \"RiskyUsersRemediate\")).Succeeded;",
            text);
        Assert.Contains("@if (canRemediate)", text);
    }

    [Fact]
    public void RiskyUsers_ExecuteAction_ReChecksTheRemediateGrantBeforeTheWrite()
    {
        // AC13, decision half. Hiding a button is presentation; the handler must re-check the
        // policy immediately before it acts (Spec, Page Authorization item 3), because a grant can
        // be revoked while the page is open.
        var body = MethodBody("ExecuteActionAsync");

        var authIndex = body.IndexOf("AuthorizeAsync(authState.User, \"RiskyUsersRemediate\")", StringComparison.Ordinal);
        Assert.True(authIndex >= 0, "ExecuteActionAsync does not re-check RiskyUsersRemediate.");
        Assert.True(WriteIndex(body) > authIndex, "the remediate re-check does not precede the Graph write.");
    }

    [Fact]
    public void RiskyUsers_ExecuteAction_RequiresATicketBeforeAnyGraphCall()
    {
        // AC14. Presence and validation are separate checks on purpose: ServiceNowService passes
        // every ticket while the integration is dormant, so validation alone would admit a blank
        // ticket on a deployment without ServiceNow.
        var body = MethodBody("ExecuteActionAsync");

        var presenceIndex = body.IndexOf("string.IsNullOrWhiteSpace(ticket)", StringComparison.Ordinal);
        var validateIndex = body.IndexOf("ServiceNow.ValidateTicketAsync(ticket)", StringComparison.Ordinal);

        Assert.True(presenceIndex >= 0, "ExecuteActionAsync does not require a ticket to be present.");
        Assert.True(validateIndex >= 0, "ExecuteActionAsync does not validate the ticket.");
        Assert.True(WriteIndex(body) > presenceIndex, "the ticket presence check does not precede the Graph write.");
        Assert.True(WriteIndex(body) > validateIndex, "ticket validation does not precede the Graph write.");
    }

    [Fact]
    public void RiskyUsers_ExecuteAction_ChecksProtectionBeforeTheWrite()
    {
        // The load-bearing ordering of the whole slice: a protected-principal check that runs after
        // the write has already changed the risk state of a protected account.
        var body = MethodBody("ExecuteActionAsync");

        var resolveIndex = body.IndexOf("ProtectedPrincipalService.ResolveWithExchangeFallbackAsync", StringComparison.Ordinal);
        var firstCheck = body.IndexOf("ProtectedPrincipalService.CheckAsync", StringComparison.Ordinal);
        var lastCheck = body.LastIndexOf("ProtectedPrincipalService.CheckAsync", StringComparison.Ordinal);

        Assert.True(resolveIndex >= 0, "ExecuteActionAsync does not resolve the target for protection.");
        Assert.True(firstCheck > resolveIndex, "the protection check does not follow resolution.");
        Assert.True(WriteIndex(body) > lastCheck, "a protection check runs AFTER the Graph write.");
    }

    [Fact]
    public void RiskyUsers_ExecuteAction_ChecksProtectionOnBothResolutionBranches()
    {
        // AC11. Risky users are cloud identities, so the DN-based rules structurally cannot match
        // them and an unresolved lookup means "check the protected USER rows against the raw
        // identity", never "nothing to protect". Skipping that branch is the exact defect MfaReset
        // shipped before 1.1.0 and leaves protection inert for this module's normal population.
        var body = MethodBody("ExecuteActionAsync");

        var checks = Regex.Matches(body, @"ProtectedPrincipalService\.CheckAsync\(");
        Assert.Equal(2, checks.Count);

        // The unresolved branch builds its own principal, and populates the Entra object id, which
        // ProtectedPrincipalService.MatchesIdentity does compare against every protected USER row.
        Assert.Contains("Source: \"RiskyUsers-Unresolved\"", body);
        Assert.Contains("EntraObjectId: string.IsNullOrWhiteSpace(userId) ? null : userId", body);
        Assert.DoesNotContain("EntraObjectId: null", body);
    }

    [Fact]
    public void RiskyUsers_ExecuteAction_RefusesWhenResolutionOrTheCheckItselfFails()
    {
        // Fail-closed outranks everything (Known Failure Class 3): an unavailable, ambiguous,
        // failed or throwing protection check must refuse, never fall through to the write.
        var body = MethodBody("ExecuteActionAsync");
        var gate = body[..WriteIndex(body)];

        Assert.Contains("ResolutionStatus.Unavailable or ProtectedPrincipalService.ResolutionStatus.Ambiguous", gate);
        Assert.Equal(2, Regex.Matches(gate, @"\.CheckFailed\)").Count);
        // The wrapping catch is the fail-closed backstop for a throwing check.
        Assert.Contains("Protection check exception:", gate);
    }

    [Fact]
    public void RiskyUsers_ExecuteAction_EveryEarlyExitBeforeTheWriteIsAnAuditedRefusal()
    {
        // The shape that makes "every path audits" structural rather than remembered: Refuse() is
        // the only way to record a failure, it audits as it refuses, and every early exit before
        // the write goes through it. A refusal that returned without calling Refuse would be a
        // silent block - a protected principal denied with nothing in the audit trail.
        var body = MethodBody("ExecuteActionAsync");
        var gate = body[..WriteIndex(body)];

        var returns = Regex.Matches(gate, @"\breturn;");
        Assert.Equal(9, returns.Count);

        // Each exit must have its OWN Refuse between it and the previous exit - not merely a
        // Refuse somewhere nearby, which a neighbouring gate's refusal would satisfy.
        var previousExit = 0;
        foreach (Match exit in returns)
        {
            Assert.Contains("Refuse(", gate[previousExit..exit.Index]);
            previousExit = exit.Index + exit.Length;
        }
    }

    [Fact]
    public void RiskyUsers_Refuse_AuditsTheRefusalWithTheTicketAndTheTarget()
    {
        var body = MethodBody("ExecuteActionAsync");
        var refuse = Between(body, "void Refuse(", "\n        }");

        Assert.Contains("SetOutcome(userId, upn, false, message)", refuse);
        Assert.Matches(
            new Regex(@"LogModuleAction\([^;]*?""RiskyUsers"",\s*upn,\s*false,\s*ticket", RegexOptions.Singleline),
            refuse);
    }

    [Fact]
    public void RiskyUsers_ExecuteAction_HonoursTheServicerGrantOnBothBranches()
    {
        // AC12. Both sites, or a cloud-only protected identity becomes the one case a servicer
        // cannot service - precisely this module's population (S6, Servicer override).
        var body = MethodBody("ExecuteActionAsync");

        Assert.Equal(2, Regex.Matches(body, @"ServicerNoteFor\(authState\.User,").Count);
        Assert.Contains("qualifier", MethodBody("ServicerNoteFor"));
        Assert.Contains("ProtectedPrincipalServicing.NoteFor", PageSource());
        Assert.Contains("ServicerModuleId = \"RiskyUsers\"", PageSource());
    }

    [Fact]
    public void RiskyUsers_ExecuteAction_CarriesTheServicedNoteInExtraNotErrorDetail()
    {
        // pps-3: LogModuleAction writes ["error"] = success ? null : errorDetail, so a note placed
        // in errorDetail is discarded on exactly the success path that needs it - the one path
        // where an override actually happened.
        var body = MethodBody("ExecuteActionAsync");
        var successAudit = body[WriteIndex(body)..];

        Assert.Contains("extra: ServicedExtra(servicedAuditDetail)", successAudit);
        Assert.DoesNotContain("errorDetail: ServicedExtra", successAudit);
        Assert.DoesNotContain("errorDetail: servicedAuditDetail", successAudit);
    }

    [Fact]
    public void RiskyUsers_ExecuteAction_AuditsTheWriteOutcomeWithTheTicket()
    {
        var body = MethodBody("ExecuteActionAsync");
        var successAudit = body[WriteIndex(body)..];

        Assert.Matches(
            new Regex(@"LogModuleAction\([^;]*?auditAction,\s*""RiskyUsers"",\s*upn,\s*applied\.Success,\s*ticket", RegexOptions.Singleline),
            successAudit);
    }

    [Fact]
    public void RiskyUsers_ExecuteAction_NotifiesAdminsFromFinallyWrappedAgainstSendFailure()
    {
        // AC15 / Constitution, Notifications: every mutating action notifies administrators, and a
        // send failure must not change the reported result - so the send lives in finally, inside
        // its own try/catch.
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
        Assert.Contains("Failed to send Risky Users admin notification", tail);
    }

    [Fact]
    public void RiskyUsers_ActionButtons_AreNotGatedOnRiskStateOrProcessingFlags()
    {
        // AC16 / S5: no client-side eligibility allowlist over an enum Microsoft extends. Graph
        // decides what it will accept and its refusal becomes that row's own named failure; a
        // hardcoded allowlist would silently hide rows instead (the CompletedWithErrors class).
        var group = Between(PageSource(), "<div class=\"btn-group btn-group-sm\"", "</div>");

        Assert.Equal(3, Regex.Matches(group, @"BeginAction\(user, RiskyUserAction\.").Count);
        Assert.DoesNotContain("RiskState", group);
        Assert.DoesNotContain("IsProcessing", group);
        Assert.DoesNotContain("IsDeleted", group);
    }

    [Fact]
    public void RiskyUsers_ActionButtons_UseTheOwnerApprovedLabelAndTooltip()
    {
        // Owner ruling 2026-09-02: each trigger button shows the L2-plain label as its text and
        // the matching consequence line as its tooltip, driven off the same static helpers the
        // confirm bar uses - not a second, driftable copy of the wording.
        var group = Between(PageSource(), "<div class=\"btn-group btn-group-sm\"", "</div>");

        Assert.Equal(3, Regex.Matches(group, @"title=""@ActionConsequence\(RiskyUserAction\.").Count);
        Assert.Equal(3, Regex.Matches(group, @">@ActionLabel\(RiskyUserAction\.").Count);
        Assert.DoesNotContain("Dismiss risk", group);
        Assert.DoesNotContain("Confirm safe", group);
        Assert.DoesNotContain("Confirm compromised", group);
    }

    [Fact]
    public void RiskyUsers_ConfirmBar_ShowsTheConsequenceLineVerbatimAboveTheTicketControls()
    {
        // Owner ruling 2026-09-02, item 2: the exact confirmation line for the chosen action must
        // appear above the ticket/confirm controls, not folded into or paraphrased by the
        // action+user prompt line.
        var text = PageSource();

        var promptIndex = text.IndexOf("@ConfirmPrompt(confirmAction.Value", StringComparison.Ordinal);
        var consequenceIndex = text.IndexOf("@ActionConsequence(confirmAction.Value)", StringComparison.Ordinal);
        var ticketIndex = text.IndexOf("riskyUsersTicket", StringComparison.Ordinal);

        Assert.True(promptIndex >= 0, "the confirm bar no longer renders ConfirmPrompt.");
        Assert.True(consequenceIndex > promptIndex, "the consequence line is not rendered after the prompt line.");
        Assert.True(ticketIndex > consequenceIndex, "the consequence line does not precede the ticket/confirm controls.");
    }

    [Fact]
    public void RiskyUsers_ConfirmBar_RendersBeneathTheActingRowNotAboveTheTable()
    {
        // docs/MigrationBatchSelection-Plan.md slice 3: a top-of-table confirm for row 47 puts the
        // ticket box off-screen while that row's buttons go disabled, which reads as the buttons
        // breaking. Anchored to the confirm bar being inside the per-row loop.
        var text = PageSource();

        var tbody = text.IndexOf("<tbody>", StringComparison.Ordinal);
        var loop = text.IndexOf("@foreach (var user in results)", StringComparison.Ordinal);
        var confirmBar = text.IndexOf("confirmUserId == user.Id", StringComparison.Ordinal);

        Assert.True(tbody >= 0 && loop > tbody, "the results loop was not found inside the table body.");
        Assert.True(confirmBar > loop, "the confirm bar is not rendered per row beneath the acting row.");
        Assert.Contains("riskyUsersTicket", text);
    }

    [Fact]
    public void RiskyUsers_PerUserOutcomes_AreKeyedByUserAndNameTheirUser()
    {
        // Known Failure Class 2 / AC10: acting on three rows must leave three separately named
        // verdicts. A single shared result field is how a refusal on one row reads as a verdict on
        // another, so the outcome is keyed by user id and rendered with its own UPN.
        var text = PageSource();

        Assert.Contains("Dictionary<string, RowOutcome> rowOutcomes", text);
        Assert.Contains("record RowOutcome(string UserPrincipalName, bool Success, string Message)", text);
        Assert.Contains("OutcomeFor(user.Id) is { } outcome", text);
        Assert.Contains("<strong>@outcome.UserPrincipalName</strong>", text);
    }

    [Fact]
    public void RiskyUsers_ClearsWritePathStateAtStartOfEveryQuery()
    {
        // The blr-3 shape applied to the write UI: results emptied for a new query but a per-row
        // outcome or an open confirm bar left behind would attach the old verdict to whatever row
        // now occupies that id.
        var body = MethodBody("LoadRiskyUsersAsync");

        Assert.Contains("rowOutcomes.Clear();", body);
        Assert.Contains("confirmUserId = null;", body);
    }

    [Fact]
    public void RiskyUsers_IsOfferedTheServicerEditorInModuleConfig()
    {
        // idm-3 / ru-3: do not ship a capability no operator can reach. ProtectedServicer:RiskyUsers
        // is only grantable if the module is on ModuleConfig.razor's explicit opt-in list, and this
        // slice is the commit that adds the Evaluate call the list exists to certify.
        var moduleConfig = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "ModuleConfig.razor"));

        var declaration = Regex.Match(moduleConfig,
            @"ModulesWithProtectedPrincipalServicing\s*=\s*new\([^)]*\)\s*\{(?<members>[^}]*)\}");
        Assert.True(declaration.Success, "the servicer opt-in list is no longer an explicit set literal.");
        Assert.Contains("\"RiskyUsers\"", declaration.Groups["members"].Value);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static string PageSource() => File.ReadAllText(
        AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "RiskyUsers.razor"));

    /// <summary>
    /// A brace-balanced method body from the page's @code block, so a marker appearing later in
    /// the file cannot end the slice early and report a real change as missing.
    /// </summary>
    private static string MethodBody(string methodName)
    {
        var source = PageSource();
        var signature = Regex.Match(source, $@"(private|internal|protected)[^\r\n]*\b{Regex.Escape(methodName)}\(");
        Assert.True(signature.Success, $"'{methodName}' is no longer declared in RiskyUsers.razor.");

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

    /// <summary>Index of the single Graph write call inside ExecuteActionAsync's body.</summary>
    private static int WriteIndex(string body)
    {
        var calls = Regex.Matches(body, @"RiskyUsersService\.ApplyActionAsync\(");
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
}
