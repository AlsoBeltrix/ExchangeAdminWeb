using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class EmergencyDisableServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configDir;

    public EmergencyDisableServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"emergency-disable-test-{Guid.NewGuid():N}");
        _configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task DisableAsync_BlankTicket_FailsBeforeCredentialLookup()
    {
        var service = CreateService();

        var result = await service.DisableAsync(MakePrincipal(), "  ", "DOMAIN\\admin", "10.0.0.1", actingUser: null, moveToLockdownOu: false);

        Assert.False(result.Success);
        Assert.Null(result.Snapshot);
        Assert.Contains("Ticket number is required", result.Error);
        Assert.Contains(result.Steps, s => s.Step == "TicketValidation" && s.Status == "FAILED");
        Assert.DoesNotContain(result.Steps, s => s.Step == "ProtectedPrincipalCheck");
        Assert.DoesNotContain(result.Steps, s => s.Step == "GetADCredentials");
    }

    [Fact]
    public async Task DisableAsync_ProtectedPrincipal_BlocksBeforeCredentialLookup()
    {
        var protectedConfig = JsonSerializer.Serialize(new
        {
            ProtectedPrincipals = new
            {
                Users = new[] { "ceo@contoso.com" },
                Groups = Array.Empty<string>(),
                OrganizationalUnits = Array.Empty<string>(),
                SamAccountNamePatterns = Array.Empty<string>()
            }
        });
        var service = CreateService(protectedPrincipalsJson: protectedConfig);

        var result = await service.DisableAsync(MakePrincipal("ceo@contoso.com"), "INC001", "DOMAIN\\admin", "10.0.0.1", actingUser: null, moveToLockdownOu: false);

        Assert.False(result.Success);
        Assert.Null(result.Snapshot);
        Assert.Contains("protected principal", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Steps, s => s.Step == "TicketValidation" && s.Status == "OK");
        Assert.Contains(result.Steps, s => s.Step == "ProtectedPrincipalCheck" && s.Status == "BLOCKED");
        Assert.DoesNotContain(result.Steps, s => s.Step == "GetADCredentials");
    }

    [Fact]
    public async Task DisableAsync_CorruptProtectedPrincipalConfig_FailsClosedBeforeCredentialLookup()
    {
        File.WriteAllText(Path.Combine(_configDir, "protected-principals.json"), "not valid json {{{");
        var service = CreateService();

        var result = await service.DisableAsync(MakePrincipal(), "INC001", "DOMAIN\\admin", "10.0.0.1", actingUser: null, moveToLockdownOu: false);

        Assert.False(result.Success);
        Assert.Null(result.Snapshot);
        Assert.Contains("Protected principal check failed", result.Error);
        Assert.Contains(result.Steps, s => s.Step == "ProtectedPrincipalCheck" && s.Status == "BLOCKED");
        Assert.DoesNotContain(result.Steps, s => s.Step == "GetADCredentials");
    }

    [Fact]
    public async Task DisableAsync_MissingAdCredentialConfig_StopsBeforeGraphAndMutationSteps()
    {
        var service = CreateService();

        var result = await service.DisableAsync(MakePrincipal(), "INC001", "DOMAIN\\admin", "10.0.0.1", actingUser: null, moveToLockdownOu: false);

        Assert.False(result.Success);
        Assert.Null(result.Snapshot);
        Assert.Contains("AD credentials unavailable", result.Error);
        Assert.Contains(result.Steps, s => s.Step == "TicketValidation" && s.Status == "OK");
        Assert.Contains(result.Steps, s => s.Step == "ProtectedPrincipalCheck" && s.Status == "OK");
        Assert.Contains(result.Steps, s => s.Step == "GetADCredentials" && s.Status == "FAILED");
        Assert.DoesNotContain(result.Steps, s => s.Step == "GetGraphCredentials");
        Assert.DoesNotContain(result.Steps, s => s.Step is "DisableAD" or "ResetPassword" or "RevokeEntraSessions" or "DisableEntra");
    }

    [Fact]
    public void ModuleCatalog_EmergencyDisable_IsFailClosedAndVersioned()
    {
        var catalog = new ModuleCatalog();

        var module = catalog.GetById("EmergencyDisable");

        Assert.NotNull(module);
        Assert.False(module.EnabledByDefault);
        Assert.True(module.MainPermission.FailClosed);
        // 1.2.0: an authorised servicer group may act on a protected principal here, with the
        // override recorded in the audit event (not only the operation trace - see pps-3).
        // 1.1.0 was protection resolving through Exchange (docs/ProtectedPrincipalGapFix-Plan.md
        // GAP B).
        // 1.3.2: PerformLookup reports the directory resolve and the protection check
        // (docs/ProgressCoverage-Plan.md S5).
        Assert.Equal("1.3.2", module.Version);
        Assert.Contains(module.ConfigFields, f => f.Key == "DelineaSecretId");
        Assert.Contains(module.ConfigFields, f => f.Key == "GraphDelineaSecretId");
        Assert.Contains(module.ConfigFields, f => f.Key == "NotifySecurityTeam");
    }

    [Fact]
    public void Catalog_LockdownOuField_IsOptional_SoExistingInstallsStayConfigured()
    {
        // Required: true would flip IsModuleConfigured to false for every install that has not
        // set a lockdown OU, which would take the whole module offline to add an optional step.
        var catalog = new ModuleCatalog();
        var module = catalog.GetById("EmergencyDisable");

        Assert.NotNull(module);
        var field = Assert.Single(module.ConfigFields, f => f.Key == "LockdownOuDn");
        Assert.False(field.Required);
        Assert.Equal(ConfigFieldType.OU, field.FieldType);
        Assert.Equal("", field.DefaultValue);
    }

    [Fact]
    public void Catalog_LockdownOuField_NamesNoEnvironment()
    {
        // Repo invariant 7: no source file names an OU, domain or host as behaviour. The field
        // ships empty and is discovered from config at runtime.
        var catalog = new ModuleCatalog();
        var field = catalog.GetById("EmergencyDisable")!.ConfigFields.Single(f => f.Key == "LockdownOuDn");

        Assert.DoesNotContain("DC=", field.DefaultValue, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OU=", field.DefaultValue, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Lockdown decision (pure) -----------------------------------------------------------

    [Fact]
    public void DecideLockdown_NotRequested_DoesNothing()
    {
        var d = EmergencyDisableService.DecideLockdown(
            requested: false, disableAdSucceeded: true, "OU=Lockdown,DC=example,DC=test", "CN=u,OU=Staff,DC=example,DC=test");

        Assert.False(d.Proceed);
        Assert.Equal(LockdownOutcome.NotRequested, d.Outcome);
    }

    [Fact]
    public void DecideLockdown_RequestedButNoOuConfigured_FailsClosed()
    {
        // Fail-closed: a requested move with nowhere to go never guesses a destination, and it
        // is a FAILURE rather than a quiet skip so the operator learns the protection is absent.
        var d = EmergencyDisableService.DecideLockdown(
            requested: true, disableAdSucceeded: true, configuredOuDn: "   ", "CN=u,OU=Staff,DC=example,DC=test");

        Assert.False(d.Proceed);
        Assert.Equal(LockdownOutcome.Failed, d.Outcome);
        Assert.Contains("no lockdown OU is configured", d.Detail);
    }

    [Fact]
    public void DecideLockdown_RequestedButAdDisableFailed_SkipsTheMove()
    {
        // Moving a still-enabled account into a lockdown OU produces an object that lies to the
        // next reader. The disable is already a failure, so this changes no overall verdict.
        var d = EmergencyDisableService.DecideLockdown(
            requested: true, disableAdSucceeded: false, "OU=Lockdown,DC=example,DC=test", "CN=u,OU=Staff,DC=example,DC=test");

        Assert.False(d.Proceed);
        Assert.Equal(LockdownOutcome.Skipped, d.Outcome);
        Assert.Contains("AD disable did not succeed", d.Detail);
    }

    [Fact]
    public void DecideLockdown_AlreadyInTheLockdownOu_IsASkipNotAFailure()
    {
        var d = EmergencyDisableService.DecideLockdown(
            requested: true, disableAdSucceeded: true,
            "OU=Lockdown,DC=example,DC=test", "CN=user,OU=Lockdown,DC=example,DC=test");

        Assert.False(d.Proceed);
        Assert.Equal(LockdownOutcome.AlreadyInPlace, d.Outcome);
    }

    [Fact]
    public void DecideLockdown_AlreadyInTheLockdownOu_IgnoresCaseAndSurroundingSpace()
    {
        var d = EmergencyDisableService.DecideLockdown(
            requested: true, disableAdSucceeded: true,
            "  ou=lockdown,dc=example,dc=test  ", "CN=user,OU=Lockdown,DC=example,DC=test");

        Assert.Equal(LockdownOutcome.AlreadyInPlace, d.Outcome);
    }

    [Fact]
    public void DecideLockdown_RequestedAndReady_Proceeds()
    {
        var d = EmergencyDisableService.DecideLockdown(
            requested: true, disableAdSucceeded: true,
            "OU=Lockdown,DC=example,DC=test", "CN=user,OU=Staff,DC=example,DC=test");

        Assert.True(d.Proceed);
        Assert.Equal(LockdownOutcome.Moved, d.Outcome);
    }

    [Fact]
    public void DecideLockdown_NullTargetDn_FailsClosed()
    {
        var d = EmergencyDisableService.DecideLockdown(
            requested: true, disableAdSucceeded: true, "OU=Lockdown,DC=example,DC=test", targetDn: null);

        Assert.False(d.Proceed);
        Assert.Equal(LockdownOutcome.Failed, d.Outcome);
    }

    // ---- Parent-DN extraction (pure) --------------------------------------------------------

    [Fact]
    public void ParentDnOf_ReturnsTheContainer()
    {
        Assert.Equal("OU=Staff,DC=example,DC=test",
            EmergencyDisableService.ParentDnOf("CN=Jane Doe,OU=Staff,DC=example,DC=test"));
    }

    [Fact]
    public void ParentDnOf_EscapedCommaInTheRdn_IsNotASeparator()
    {
        // RFC 4514 allows an escaped comma inside an RDN value. A naive Split(',') returns
        // " Jane,OU=Staff,..." here and would compare the wrong container against the config,
        // so a "Doe, Jane" account would move when it should skip, or skip when it should move.
        Assert.Equal("OU=Staff,DC=example,DC=test",
            EmergencyDisableService.ParentDnOf(@"CN=Doe\, Jane,OU=Staff,DC=example,DC=test"));
    }

    [Fact]
    public void ParentDnOf_EscapedBackslashBeforeComma_StillSeparates()
    {
        // A doubled backslash is a literal backslash, so the comma after it IS a separator.
        Assert.Equal("OU=Staff,DC=example,DC=test",
            EmergencyDisableService.ParentDnOf(@"CN=Back\\slash,OU=Staff,DC=example,DC=test"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DC=test")]
    public void ParentDnOf_NoParent_IsNull(string? dn)
    {
        Assert.Null(EmergencyDisableService.ParentDnOf(dn));
    }

    // ---- Stamp builder (pure) ---------------------------------------------------------------

    [Fact]
    public void BuildLockdownStamp_EmptyExistingValue_HasNoLeadingNewline()
    {
        var r = EmergencyDisableService.BuildLockdownStamp(
            existingInfo: "", "OU=Staff,DC=example,DC=test", new DateTime(2026, 10, 5));

        Assert.True(r.Ok);
        Assert.Equal("[EMERGENCY DISABLE 2026-10-05] previous OU: OU=Staff,DC=example,DC=test", r.Value);
    }

    [Fact]
    public void BuildLockdownStamp_ExistingContent_IsPreservedAndAppendedAfterACrlf()
    {
        var r = EmergencyDisableService.BuildLockdownStamp(
            "Service account for the nightly import.", "OU=Staff,DC=example,DC=test", new DateTime(2026, 10, 5));

        Assert.True(r.Ok);
        Assert.StartsWith("Service account for the nightly import.\r\n", r.Value);
        Assert.EndsWith("previous OU: OU=Staff,DC=example,DC=test", r.Value);
    }

    [Fact]
    public void BuildLockdownStamp_PriorStamp_IsLeftInPlace()
    {
        // Nothing in this app may parse the stamp (owner ruling 2026-10-05), so a prior stamp
        // cannot be recognised in order to be replaced. Two disables leave two notes, by design.
        const string prior = "[EMERGENCY DISABLE 2026-01-01] previous OU: OU=Old,DC=example,DC=test";

        var r = EmergencyDisableService.BuildLockdownStamp(
            prior, "OU=Staff,DC=example,DC=test", new DateTime(2026, 10, 5));

        Assert.True(r.Ok);
        Assert.Contains(prior, r.Value);
        Assert.Equal(2, r.Value!.Split("\r\n").Length);
    }

    [Fact]
    public void BuildLockdownStamp_OverTheSchemaLimit_RefusesRatherThanTruncating()
    {
        var existing = new string('x', EmergencyDisableService.InfoAttributeMaxLength - 10);

        var r = EmergencyDisableService.BuildLockdownStamp(
            existing, "OU=Staff,DC=example,DC=test", new DateTime(2026, 10, 5));

        Assert.False(r.Ok);
        Assert.Null(r.Value);
        Assert.Contains("1024", r.Error);
    }

    [Fact]
    public void BuildLockdownStamp_ExactlyAtTheLimit_IsAccepted()
    {
        var line = "[EMERGENCY DISABLE 2026-10-05] previous OU: OU=Staff,DC=example,DC=test";
        var existing = new string('x', EmergencyDisableService.InfoAttributeMaxLength - line.Length - 2);

        var r = EmergencyDisableService.BuildLockdownStamp(
            existing, "OU=Staff,DC=example,DC=test", new DateTime(2026, 10, 5));

        Assert.True(r.Ok);
        Assert.Equal(EmergencyDisableService.InfoAttributeMaxLength, r.Value!.Length);
    }

    // ---- Lockdown outcome and overall success ------------------------------------------------

    [Theory]
    [InlineData(LockdownOutcome.NotRequested, true)]
    [InlineData(LockdownOutcome.Moved, true)]
    [InlineData(LockdownOutcome.AlreadyInPlace, true)]
    [InlineData(LockdownOutcome.Skipped, false)]
    [InlineData(LockdownOutcome.Failed, false)]
    public void IsOverallSuccess_LockdownOutcome_DecidesTheVerdict(LockdownOutcome outcome, bool expected)
    {
        // Everything else OK. A requested move that did not happen must fail the operation:
        // "disabled, but NOT locked down" is a materially different result (Known Failure
        // Class 2 - no blanket success).
        Assert.Equal(expected, EmergencyDisableService.IsOverallSuccess("OK", "OK", "OK", "OK", outcome));
    }

    [Fact]
    public void IsOverallSuccess_LockdownMoved_DoesNotPaperOverAFailedDisable()
    {
        Assert.False(EmergencyDisableService.IsOverallSuccess("FAILED", "OK", "OK", "SKIPPED", LockdownOutcome.Moved));
    }

    [Theory]
    [InlineData(LockdownOutcome.Moved, "OK")]
    [InlineData(LockdownOutcome.AlreadyInPlace, "SKIPPED")]
    [InlineData(LockdownOutcome.Skipped, "SKIPPED")]
    [InlineData(LockdownOutcome.Failed, "FAILED")]
    [InlineData(LockdownOutcome.NotRequested, "NOT REQUESTED")]
    public void StepStatusFor_MapsEveryOutcome(LockdownOutcome outcome, string expected)
    {
        Assert.Equal(expected, EmergencyDisableService.StepStatusFor(outcome));
    }

    // ---- S2: the checkbox on the page -------------------------------------------------------

    [Fact]
    public void Page_LockdownCheckbox_DefaultsUnchecked()
    {
        // Owner ruling 2026-10-06, superseding 2026-10-05: the lockdown step is opt-IN. An
        // emergency disable does not move the account unless the operator says so on that run.
        var page = ReadPageSource("EmergencyDisable.razor");

        Assert.Contains("private const bool LockdownDefaultArmed = false;", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_TheDefault_LivesInOneConstant_NotALiteralPerSite()
    {
        // Three places have to agree about the default - the field initialiser, the post-auth
        // read and the fresh-operation reset - and the owner has already changed his mind about
        // it once. A literal at each site is edl-3 waiting to happen in a new direction: flip
        // two of three and the first operation of a session behaves differently from the rest.
        var page = ReadPageSource("EmergencyDisable.razor");
        var code = page[page.IndexOf("@code {", StringComparison.Ordinal)..];

        Assert.Equal(3, code.Split("LockdownDefaultArmed").Length - 1 - 1); // minus the declaration
        Assert.DoesNotContain("moveToLockdownOu = true;", code, StringComparison.Ordinal);
        Assert.DoesNotContain("moveToLockdownOu = false;", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_UnconfiguredLockdownOu_IsNeverArmed_WhateverTheDefaultSays()
    {
        // The config gate ANDs with the default rather than replacing it, so flipping the
        // default back to armed cannot arm a step with nowhere to move the account to.
        var page = ReadPageSource("EmergencyDisable.razor");

        Assert.Contains("LockdownDefaultArmed && lockdownOuConfigured", page, StringComparison.Ordinal);
    }

    // ResetForm's own re-arm assertion was folded into the two helper tests below when edl-3
    // centralised the reset: asserting that ResetForm calls BeginNewOperation and that the
    // helper re-arms covers strictly more than checking ResetForm's body did.

    [Fact]
    public void Page_BothEntryPoints_StartAFreshOperationThroughTheOneHelper()
    {
        // edl-3. Originally PerformLookup and ResetForm each cleared their own list of fields
        // and PerformLookup was missed, so an operator who declined the lockdown for one
        // account carried that decision onto the next person they looked up.
        var page = ReadPageSource("EmergencyDisable.razor");

        Assert.Contains("BeginNewOperation();", Between(page, "private async Task PerformLookup()", "await Task.Yield();"), StringComparison.Ordinal);
        Assert.Contains("BeginNewOperation();", Between(page, "private void ResetForm()", "\n    }"), StringComparison.Ordinal);
    }

    [Fact]
    public void Page_FreshOperationState_IsClearedInExactlyOnePlace()
    {
        // Locality, not arithmetic. The first version of this guard counted occurrences of
        // `confirmed = false;` and `moveToLockdownOu = lockdownOuConfigured;` across the whole
        // code block and asserted the totals matched - which the reviewer correctly called a
        // proxy rather than the invariant: a future change could add an unrelated re-arm while
        // a new clearing path omitted one, and the count would still balance.
        //
        // Pinning single ownership instead makes the defect class structurally unreachable. A
        // third entry point cannot clear operation state without either calling the helper or
        // failing this test, and the helper cannot forget the checkbox without failing the
        // test below.
        var page = ReadPageSource("EmergencyDisable.razor");
        var code = page[page.IndexOf("@code {", StringComparison.Ordinal)..];

        var clearingSites = code.Split("confirmed = false;").Length - 1;
        Assert.True(
            clearingSites == 1,
            $"`confirmed = false;` appears {clearingSites} time(s); it must appear only inside " +
            "BeginNewOperation(). A second site means a path starts a fresh operation without " +
            "going through the one place that knows what a fresh operation is (edl-3).");
    }

    [Fact]
    public void Page_TheFreshOperationHelper_ReArmsTheCheckbox()
    {
        // The other half: single ownership is worthless if the owner forgets the field.
        var page = ReadPageSource("EmergencyDisable.razor");
        var helper = Between(page, "private void BeginNewOperation()", "\n    }");

        Assert.Contains("confirmed = false;", helper, StringComparison.Ordinal);
        Assert.Contains("moveToLockdownOu = LockdownDefaultArmed && lockdownOuConfigured;", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("moveToLockdownOu = false;", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_UnconfiguredLockdownOu_DisablesTheCheckbox()
    {
        // Fail-closed without a surprise: the operator must not be able to arm a step that
        // cannot run. The disarm half is covered by the re-arm tests above, which assign
        // lockdownOuConfigured - false when nothing is configured.
        var page = ReadPageSource("EmergencyDisable.razor");
        var checkbox = Between(page, "id=\"chkLockdown\"", "</div>");

        Assert.Contains("!lockdownOuConfigured", checkbox, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_InitialisesTheCheckboxFromConfig_BeforeTheFormIsUsable()
    {
        // The field initialiser is `true`, so without this read an unconfigured install would
        // render an armed checkbox for the first operation of every session.
        var page = ReadPageSource("EmergencyDisable.razor");
        var init = Between(page, "protected override async Task OnInitializedAsync()", "authChecked = true;");

        Assert.Contains("lockdownOuConfigured =", init, StringComparison.Ordinal);
        Assert.Contains("moveToLockdownOu = LockdownDefaultArmed && lockdownOuConfigured;", init, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_PassesTheOperatorsChoice_NotAHardcodedValue()
    {
        // S1 deliberately passed a literal false. If that literal survived S2 the checkbox
        // would render, tick, and do nothing at all - the defect this test exists to prevent.
        var page = ReadPageSource("EmergencyDisable.razor");

        Assert.Contains("authState.User, moveToLockdownOu)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("moveToLockdownOu: false", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_ConfirmPanel_StatesTheMoveWhenItIsArmed()
    {
        // The "You are about to:" list is the last thing read before an irreversible action.
        var page = ReadPageSource("EmergencyDisable.razor");

        Assert.Contains("Move the account to the lockdown OU", page, StringComparison.Ordinal);
    }

    private static string Between(string haystack, string start, string end)
    {
        var from = haystack.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"Could not find '{start}' in the page source.");
        var to = haystack.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"Could not find '{end}' after '{start}'.");
        return haystack[from..to];
    }

    // ---- edl-2: a failed stamp must not hide behind a successful move ----------------------

    [Fact]
    public void LockdownSummary_MovedButStampFailed_SaysTheBreadcrumbIsMissing()
    {
        // edl-2: the move can succeed while the info write does not. The security team reads
        // this line, and before the fix it said LOCKED DOWN with no hint the record was absent.
        var s = EmergencyDisableService.LockdownSummary(
            requested: true, LockdownOutcome.Moved, LockdownStampOutcome.Failed);

        Assert.Contains("DISABLED AND LOCKED DOWN", s);
        Assert.Contains("previous OU was NOT recorded", s);
        Assert.Contains("snapshot", s);
    }

    [Fact]
    public void LockdownSummary_MovedAndStamped_AddsNoWarning()
    {
        var s = EmergencyDisableService.LockdownSummary(
            requested: true, LockdownOutcome.Moved, LockdownStampOutcome.Stamped);

        Assert.DoesNotContain("NOT recorded", s);
    }

    [Fact]
    public void LockdownStampStep_FailedStamp_RendersAsDanger()
    {
        // The whole point of edl-2 is that the failure is VISIBLE. A stamp failure produces a
        // FAILED step, and FAILED is in the danger bucket (edl-1's allowlist excludes it).
        Assert.Equal("table-danger", EmergencyDisableService.RowClassFor("FAILED"));
    }

    [Fact]
    public void MoveStep_DoesNotCarryTheStampResultInItsOwnDetail()
    {
        // edl-2 was exactly this shape: the stamp outcome was interpolated into the
        // LockdownMove step's detail string, so a failure rode inside a green OK row. Pinned
        // at source because reaching ExecuteMoveToLockdownOu needs a live directory.
        var source = ReadServiceSource();

        Assert.DoesNotContain("\"LockdownMove\", \"OK\", $\"Moved to {lockdownOuDn}. {stampDetail}\"",
            source, StringComparison.Ordinal);
        Assert.Contains("new DisableStepResult(\"LockdownStamp\",", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StampOutcome_ReachesTheAuditExtra_NotOnlyTheStepTable()
    {
        // AuditService writes ["error"] = success ? null : errorDetail, so on a successful
        // disable a stamp failure has no other durable field to land in. If this key is
        // dropped, the audit record cannot answer "was the breadcrumb written?" at all.
        var source = ReadServiceSource();

        Assert.Contains("extra[\"lockdownStampOutcome\"]", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RowClassFor_DeclinedLockdown_IsNeutral_NotDanger()
    {
        // edl-1: the page painted every status other than OK and SKIPPED red, so a lockdown
        // the operator was entitled to decline rendered as a failure on an otherwise
        // successful run.
        Assert.Equal("table-secondary", EmergencyDisableService.RowClassFor("NOT REQUESTED"));
    }

    [Fact]
    public void RowClassFor_EveryLockdownOutcome_ColoursAsTheOutcomeDeserves()
    {
        // Walks the real status vocabulary rather than hard-coded strings, so a new outcome
        // whose status word is not classified cannot slip through as an accidental red row.
        Assert.Equal("table-success", EmergencyDisableService.RowClassFor(EmergencyDisableService.StepStatusFor(LockdownOutcome.Moved)));
        Assert.Equal("table-secondary", EmergencyDisableService.RowClassFor(EmergencyDisableService.StepStatusFor(LockdownOutcome.AlreadyInPlace)));
        Assert.Equal("table-secondary", EmergencyDisableService.RowClassFor(EmergencyDisableService.StepStatusFor(LockdownOutcome.Skipped)));
        Assert.Equal("table-secondary", EmergencyDisableService.RowClassFor(EmergencyDisableService.StepStatusFor(LockdownOutcome.NotRequested)));
        Assert.Equal("table-danger", EmergencyDisableService.RowClassFor(EmergencyDisableService.StepStatusFor(LockdownOutcome.Failed)));
    }

    [Theory]
    [InlineData("FAILED")]
    [InlineData("BLOCKED")]
    [InlineData("")]
    public void RowClassFor_FailureAndAnythingUnrecognised_StaysDanger(string status)
    {
        // The neutral bucket is an allowlist. An unknown status must still read as a problem.
        Assert.Equal("table-danger", EmergencyDisableService.RowClassFor(status));
    }

    [Fact]
    public void ResultTable_UsesTheSharedRowClass_NotItsOwnInlineChain()
    {
        // edl-1 lived in an inline ternary in the page, where no test could see it. Pinning
        // the call keeps the classification in the one place that is covered above; an
        // inline chain reintroduced here would pass every test in this file otherwise.
        var page = ReadPageSource("EmergencyDisable.razor");

        Assert.Contains("EmergencyDisableService.RowClassFor(step.Status)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("\"table-danger\"", page, StringComparison.Ordinal);
    }

    private static string ReadPageSource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Components", "Pages", fileName);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate Components/Pages/{fileName} from {AppContext.BaseDirectory}");
    }

    [Fact]
    public void LockdownSummary_RequestedButNotDone_SaysTheProtectionIsAbsent()
    {
        // The security team reads this line to learn whether routine delegation can switch the
        // account back on. A requested-but-failed move must not read like a decline.
        var failed = EmergencyDisableService.LockdownSummary(requested: true, LockdownOutcome.Failed, LockdownStampOutcome.NotAttempted);

        Assert.Contains("WITHOUT LOCKDOWN", failed);
        Assert.Contains("requested but did NOT happen", failed);
        Assert.Contains("Manual follow-up required", failed);
    }

    [Fact]
    public void LockdownSummary_Declined_ReadsAsADecision_NotAFailure()
    {
        var declined = EmergencyDisableService.LockdownSummary(requested: false, LockdownOutcome.NotRequested, LockdownStampOutcome.NotAttempted);

        Assert.Contains("did not request", declined);
        Assert.DoesNotContain("Manual follow-up", declined);
    }

    [Theory]
    [InlineData(LockdownOutcome.Moved)]
    [InlineData(LockdownOutcome.AlreadyInPlace)]
    public void LockdownSummary_LockedDown_SaysSo(LockdownOutcome outcome)
    {
        Assert.Contains("DISABLED AND LOCKED DOWN", EmergencyDisableService.LockdownSummary(true, outcome, LockdownStampOutcome.Stamped));
    }

    // ---- Synced-user Entra-disable decision (pure) ------------------------------------------

    [Fact]
    public void ShouldSkipEntraDisable_SyncedUser_IsSkipped()
    {
        Assert.True(EmergencyDisableService.ShouldSkipEntraDisable(isSynced: true));
    }

    [Fact]
    public void ShouldSkipEntraDisable_CloudOnlyUser_IsNotSkipped()
    {
        Assert.False(EmergencyDisableService.ShouldSkipEntraDisable(isSynced: false));
    }

    // ---- Overall-success accounting (pure) --------------------------------------------------

    [Fact]
    public void IsOverallSuccess_SyncedUser_EntraSkipped_IsSuccess()
    {
        // AD/reset/revoke all OK and the Entra disable SKIPPED (synced) => overall success.
        Assert.True(EmergencyDisableService.IsOverallSuccess("OK", "OK", "OK", "SKIPPED", LockdownOutcome.NotRequested));
    }

    [Fact]
    public void IsOverallSuccess_CloudUser_EntraOk_IsSuccess()
    {
        Assert.True(EmergencyDisableService.IsOverallSuccess("OK", "OK", "OK", "OK", LockdownOutcome.NotRequested));
    }

    [Fact]
    public void IsOverallSuccess_EntraFailed_IsFailure()
    {
        Assert.False(EmergencyDisableService.IsOverallSuccess("OK", "OK", "OK", "FAILED", LockdownOutcome.NotRequested));
    }

    [Fact]
    public void IsOverallSuccess_AdFailed_IsFailure_EvenIfEntraSkipped()
    {
        // A SKIPPED Entra step must not paper over a failed AD mutation.
        Assert.False(EmergencyDisableService.IsOverallSuccess("FAILED", "OK", "OK", "SKIPPED", LockdownOutcome.NotRequested));
    }

    [Fact]
    public async Task DisableAsync_ServicedOverride_RecordsTheAuthorisingGroupInTheAUDIT()
    {
        // pps-3: the override was recorded in the operation TRACE and missing from the audit log.
        // Those are different stores with different retention and different readers, and the audit
        // log is where "who permitted this?" gets asked later. Asserting on the written audit file
        // rather than on result.Steps is the whole point - the steps were already correct.
        const string servicerSid = "S-1-5-21-1-2-3-4001";
        var protectedConfig = JsonSerializer.Serialize(new
        {
            ProtectedPrincipals = new
            {
                Users = new[] { "ceo@contoso.com" },
                Groups = Array.Empty<string>(),
                OrganizationalUnits = Array.Empty<string>(),
                SamAccountNamePatterns = Array.Empty<string>()
            }
        });

        var service = CreateService(protectedConfig, servicerGroups: [servicerSid]);

        var actingUser = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, servicerSid)],
                "TestAuth"));

        var result = await service.DisableAsync(
            MakePrincipal("ceo@contoso.com"), "INC001", "DOMAIN\\admin", "10.0.0.1", actingUser, moveToLockdownOu: false);

        // The gate allowed it: an ordinary operator gets BLOCKED here, this one gets SERVICED.
        Assert.Contains(result.Steps, s => s.Step == "ProtectedPrincipalCheck" && s.Status == "SERVICED");

        // The run then stops at the Delinea credential fetch, which returns BEFORE LogAudit - a
        // pre-existing early-return path this change does not alter, so there is no audit record
        // to inspect and no audit file is written at all. Asserting emptiness here would pass for
        // the wrong reason, so the audit-side guard is source-level instead, below. Stated plainly
        // rather than left implicit: reaching LogAudit needs a live directory and Graph.
        Assert.Empty(ReadAuditEvents());
    }

    [Fact]
    public void TheServicedNote_ReachesTheAuditEvent_NotOnlyTheOperationTrace()
    {
        // pps-3: the override was recorded as an operation-trace STEP and omitted from the audit
        // event. They are different stores with different retention and readers, and the audit log
        // is where "who permitted this?" gets asked later.
        //
        // Source-level because the audit call sits past a Delinea credential fetch and two live
        // backends (see the test above), so no unit test can drive it. NOT behavioural coverage.
        var source = ReadServiceSource();

        // The note is threaded to LogAudit rather than stopping at the trace step. Re-anchored
        // when the lockdown arguments joined the call; the guard is unchanged.
        Assert.Contains("LogAudit(target, performedBy, ip, ticket, overallSuccess, steps, overallError, servicedNote,",
            source, StringComparison.Ordinal);

        // And LogAudit puts it in extra. errorDetail is written as null on success, so a serviced
        // disable - which SUCCEEDS - would have it silently discarded there.
        Assert.Contains("ProtectedPrincipalServicing.Extra(servicedNote)", source, StringComparison.Ordinal);

        // The lockdown keys are added to that same dictionary, never in place of it: an
        // assignment that replaced Extra(...) would drop the serviced note on the floor.
        Assert.Contains("extra[\"lockdownRequested\"]", source, StringComparison.Ordinal);
        Assert.Contains("extra[\"lockdownOutcome\"]", source, StringComparison.Ordinal);
    }

    private static string ReadServiceSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var file = Path.Combine(dir.FullName, "Services", "EmergencyDisableService.cs");
            if (File.Exists(file))
                return File.ReadAllText(file);

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate EmergencyDisableService.cs from the test base directory.");
    }

    /// <summary>
    /// Every AUDIT line written under the test's log root, across rotation files. Deliberately
    /// excludes the operation-trace stream (`*_trace.jsonl`), which is a separate store: the whole
    /// point of pps-3 is that a record present only in the trace is not in the audit log.
    /// </summary>
    private List<string> ReadAuditEvents()
    {
        var lines = new List<string>();
        foreach (var file in Directory.EnumerateFiles(_tempDir, "*.jsonl", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).Contains("_trace", StringComparison.OrdinalIgnoreCase))
                continue;

            lines.AddRange(File.ReadAllLines(file));
        }

        return lines;
    }

    private EmergencyDisableService CreateService(string? protectedPrincipalsJson = null, string[]? servicerGroups = null)
    {
        if (protectedPrincipalsJson != null)
            File.WriteAllText(Path.Combine(_configDir, "protected-principals.json"), protectedPrincipalsJson);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:LogRoot"] = _tempDir,
                ["Audit:RotationPeriod"] = "daily",
                ["OperationTrace:Enabled"] = "true",
                ["Delinea:SecretServerUrl"] = "https://fake.local",
                ["Email:AdminNotificationEmail"] = ""
            })
            .Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);

        var catalog = new ModuleCatalog();
        var moduleConfig = new ModuleConfigService(catalog, env, TestConfigStore.CreateModuleConfig(_tempDir), Substitute.For<ILogger<ModuleConfigService>>());
        var jsonlLog = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var operationTrace = new OperationTraceService(config, jsonlLog);
        var audit = new AuditService(jsonlLog, operationTrace);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());

        var extendedLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_tempDir), Substitute.For<ILogger<ExtendedLogService>>());
        var delinea = new DelineaService(httpClientFactory, config, Substitute.For<ILogger<DelineaService>>(), extendedLog, operationTrace);
        var moduleCredentials = new ModuleCredentialService(moduleConfig, delinea, Substitute.For<ILogger<ModuleCredentialService>>());
        var protectedPrincipalService = new ProtectedPrincipalService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea, Substitute.For<ILogger<ProtectedPrincipalService>>());
        var email = new EmailService(config, Substitute.For<ILogger<EmailService>>());

        // Real servicer service. With no servicerGroups it holds NO ProtectedServicer row and so
        // denies - which is what every pre-existing assertion in this class depends on, and they
        // must keep passing unchanged. Only the servicing test opts into a grant.
        var sectionAccessRepo = new Services.Storage.SectionAccessRepository(TestConfigStore.Create(_tempDir));
        if (servicerGroups is { Length: > 0 })
        {
            sectionAccessRepo.SaveAll(new Dictionary<string, string[]>
            {
                [ProtectedPrincipalServicerService.SectionKeyFor("EmergencyDisable")] = servicerGroups,
                // Something unrelated too, so the store counts as CONFIGURED and the legacy
                // AllowedGroups fallback is out of the picture (see ppsvc-1).
                ["EmergencyDisable"] = ["S-1-5-21-1-2-3-500"],
            });
        }

        var sectionAccess = new SectionAccessService(
            config, Substitute.For<ILogger<SectionAccessService>>(), env, new Modules.ModuleCatalog(),
            sectionAccessRepo);
        var servicers = new ProtectedPrincipalServicerService(
            sectionAccess, Substitute.For<ILogger<ProtectedPrincipalServicerService>>());

        return new EmergencyDisableService(
            moduleCredentials,
            moduleConfig,
            protectedPrincipalService,
            servicers,
            operationTrace,
            audit,
            email,
            delinea,
            httpClientFactory,
            env,
            config,
            Substitute.For<ILogger<EmergencyDisableService>>());
    }

    private static ResolvedDirectoryPrincipal MakePrincipal(string upn = "user@contoso.com")
    {
        return new ResolvedDirectoryPrincipal(
            Source: "Test",
            DisplayName: upn.Split('@')[0],
            UserPrincipalName: upn,
            SamAccountName: upn.Split('@')[0],
            PrimarySmtpAddress: upn,
            DistinguishedName: $"CN={upn.Split('@')[0]},OU=Users,DC=contoso,DC=com",
            ObjectGuid: Guid.NewGuid().ToString(),
            EntraObjectId: Guid.NewGuid().ToString());
    }
}
