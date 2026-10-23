using System.Collections;
using System.Security.Claims;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a23p. (1) aud-a23l fixed the all-excluded message, but where a batch IS created with some targets
/// excluded, every site still called every excluded target a protected principal - the NOTE on the message, the Detail
/// header, the page's two banners, the exclusion audit rows and the admin notification - though a target whose protection
/// check could not answer is not known to be one. (2) The ExcludedADGroups setting did not say what a value must be. (3)
/// The mailbox-load banner said "Unable to load mailboxes" and nothing of why.
/// </summary>
/// <remarks>
/// The real service and page over <see cref="MigrationStubExchange"/>, its protection stand-in answering per target
/// (aud-a23m), its recording mail stand-in keeping each notification's note. No Exchange, no directory.
/// </remarks>
public sealed class MigrationExclusionWordingTests : IDisposable
{
    private const string Protected = "pat@contoso.test";
    private const string Unverified = "una@contoso.test";
    private const string Clear = "cleo@contoso.test";
    private const string ProtectedEntry = $"{Protected} - This mailbox is a protected principal. Operation not permitted.";
    private const string UnverifiedEntry = $"{Unverified} - Protection check unavailable. Cannot verify if this mailbox is protected.";
    private const string Mixed = "2 target(s) were excluded and NOT migrated: 1 protected principal(s) and 1 that could not be verified as unprotected.";
    private const string Escalate = "Migrating a protected principal is not permitted in this tool. Escalate to an administrator outside this tool if migration is required.";
    private const string Recheck = "A target whose protection check could not answer is excluded until it can be verified as unprotected; check it again once the protection check is available.";
    private const string Warning = "\u26A0";
    private const string ProtectedRow = "Excluded \u2014 protected principal, not migrated";
    private const string UnverifiedRow = "Excluded - could not be verified as unprotected, not migrated";

    private readonly MigrationStubExchange _exchange = new();

    public MigrationExclusionWordingTests()
    {
        _exchange.Protection = new Hashtable(StringComparer.OrdinalIgnoreCase) { [Protected] = "protected", [Unverified] = "unavailable" };
    }

    public void Dispose() => _exchange.Dispose();

    private static ClaimsPrincipal Operator() => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "CONTOSO\\operator")], "test"));

    // ---- the service: the NOTE and the Detail ----------------------------------------------------

    // The finding. One protected, one unverified, one clear: the batch is created for the clear one, and the two exclusions
    // are counted and named apart. Before: "NOTE: 2 protected principal(s) were excluded and NOT migrated." and "EXCLUDED -
    // protected principals, NOT migrated (2):".
    [Fact]
    public async Task ABatchCreatedWithBothKindsExcluded_NamesThemApart()
    {
        var result = await _exchange.Service().CreateMigrationBatchAsync(MigrationDirection.ToCloud, [Protected, Unverified, Clear], "Wave_12", false, false, Operator());

        Assert.True(result.Success, result.Message);
        Assert.Equal($"Mailbox \"{Clear}\" migration batch created. Please monitor it in the portal. NOTE: {Mixed}", result.Message);
        Assert.Equal([ProtectedEntry, UnverifiedEntry], result.ExcludedTargets);
        Assert.Equal([UnverifiedEntry], result.UnverifiedTargets);
        Assert.Contains($"EXCLUDED, NOT migrated (2) - {Mixed}\n{ProtectedEntry}\n{UnverifiedEntry}\n{Escalate} {Recheck}", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("EXCLUDED - protected principals", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABatchCreatedWithOnlyUnverifiedExclusions_CallsNoneOfThemProtected()
    {
        var result = await _exchange.Service().CreateMigrationBatchAsync(MigrationDirection.ToCloud, [Unverified, Clear], "Wave_12", false, false, Operator());

        const string summary = "1 target(s) could not be verified as unprotected and were excluded, NOT migrated.";
        Assert.True(result.Success, result.Message);
        Assert.EndsWith($"NOTE: {summary}", result.Message, StringComparison.Ordinal);
        Assert.Contains($"EXCLUDED, NOT migrated (1) - {summary}\n{UnverifiedEntry}\n{Recheck}", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("protected principal", result.Message, StringComparison.Ordinal);
    }

    // The control: only protected exclusions read exactly as before.
    [Fact]
    public async Task ABatchCreatedWithOnlyProtectedExclusions_ReadsAsBefore()
    {
        var result = await _exchange.Service().CreateMigrationBatchAsync(MigrationDirection.ToCloud, [Protected, Clear], "Wave_12", false, false, Operator());

        Assert.True(result.Success, result.Message);
        Assert.EndsWith("NOTE: 1 protected principal(s) were excluded and NOT migrated.", result.Message, StringComparison.Ordinal);
        Assert.Contains($"EXCLUDED - protected principals, NOT migrated (1):\n{ProtectedEntry}\nEscalate these to an administrator outside this tool if migration is required.",
            result.Detail, StringComparison.Ordinal);
        Assert.Empty(result.UnverifiedTargets ?? []);
    }

    // ---- the page: banners, audit rows, notification ----------------------------------------------

    [Fact]
    public async Task ThePagesBulkCreate_NamesEachKindApart_InTheBanner_TheAuditRows_AndTheNotification()
    {
        var email = _exchange.Email();
        var page = _exchange.Page(_exchange.Service());
        Set(page, "Email", email);
        Field("tabIndex").SetValue(page, 1);
        Field("bulkResult").SetValue(page, Eligible(Protected, Unverified, Clear));
        Field("bulkTicketNumber").SetValue(page, "INC0007");
        Field("bulkBatchName").SetValue(page, "Wave_12");

        await Call(page, "CreateMigrationBatch");
        var rendered = page.RenderText();

        Assert.Contains($"{Warning} {Mixed}", rendered, StringComparison.Ordinal);
        Assert.Contains($"{Escalate} {Recheck}", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("2 protected principal(s) were excluded", rendered, StringComparison.Ordinal);
        var rows = _exchange.AuditEvents().Where(e => e.GetProperty("action").GetString() == "CreateMigrationBatch_ToCloud_ProtectedExclusion").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(ProtectedRow, rows.Single(r => r.GetProperty("target").GetString() == ProtectedEntry).GetProperty("error").GetString());
        Assert.Equal(UnverifiedRow, rows.Single(r => r.GetProperty("target").GetString() == UnverifiedEntry).GetProperty("error").GetString());
        var note = Assert.Single(email.Notes);
        Assert.Contains($" | EXCLUDED (not migrated): {Mixed} {ProtectedEntry}; {UnverifiedEntry}", note, StringComparison.Ordinal);
    }

    // A single create whose one target could not be verified creates nothing: its banner, row and note name it unverified.
    [Fact]
    public async Task ThePagesSingleCreate_OfAnUnverifiedTarget_DoesNotCallItProtected()
    {
        var email = _exchange.Email();
        var page = _exchange.Page(_exchange.Service());
        Set(page, "Email", email);
        Field("singleResult").SetValue(page, new MigrationEligibilityResult { EmailAddress = Unverified, Status = MigrationStatus.Eligible });
        Field("singleTicketNumber").SetValue(page, "INC0007");

        await Call(page, "CreateSingleMigrationBatch");
        var rendered = page.RenderText();

        Assert.Contains($"{Warning} 1 target(s) could not be verified as unprotected and were excluded, NOT migrated.", rendered, StringComparison.Ordinal);
        Assert.Contains(Recheck, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("protected principal(s) were excluded", rendered, StringComparison.Ordinal);
        var row = Assert.Single(_exchange.AuditEvents(), e => e.GetProperty("action").GetString() == "CreateMigrationBatch_ToCloud_ProtectedExclusion");
        Assert.Equal(UnverifiedRow, row.GetProperty("error").GetString());
        Assert.Contains(" | EXCLUDED (not migrated): 1 target(s) could not be verified as unprotected", Assert.Single(email.Notes), StringComparison.Ordinal);
    }

    // ---- the setting ------------------------------------------------------------------------

    [Fact]
    public void TheExcludedGroupsSetting_SaysWhatAValueMustBe()
    {
        var field = Assert.Single(new ModuleCatalog().GetById("Migration")!.ConfigFields, f => f.Key == "ExcludedADGroups");

        Assert.Contains("Get-ADGroup -Identity", field.Description, StringComparison.Ordinal);
        Assert.Contains("exactly one domain of the forest", field.Description, StringComparison.Ordinal);
        Assert.Contains("A distinguished name cannot be entered", field.Description, StringComparison.Ordinal);
    }

    // ---- the load banner ------------------------------------------------------------------------

    // The reason reached only the activity's text (aud-a23i); the pane said "Unable to load mailboxes. Refresh to try again."
    [Fact]
    public async Task AFailedMailboxLoad_SaysWhyInThePane()
    {
        const string Batch = "Wave_12";
        _exchange.Failures["Get-MigrationUser"] = Refused("the mailbox query was refused");
        var page = _exchange.Page(_exchange.Service());
        Field("tabIndex").SetValue(page, 2);
        Field("batchesLoaded").SetValue(page, true);
        Field("canManage").SetValue(page, true);
        Field("expandedBatch").SetValue(page, Batch);
        Field("migrationBatches").SetValue(page, new List<MigrationBatchInfo> { new() { BatchName = Batch, Status = "Synced", TotalCount = ReportedCount.Of(1) } });
        ((HashSet<string>)Field("selectedBatches").GetValue(page)!).Add(Batch);

        await Call(page, "LoadMailboxesFor", Batch);

        Assert.Contains($"Unable to load mailboxes in {Batch}: the mailbox query was refused. Refresh to try again.", page.RenderText(), StringComparison.Ordinal);
    }

    private static MigrationBatchResult Eligible(params string[] addresses) => new()
    {
        BatchName = "2026-10-22_Move",
        Success = true,
        TotalUsers = addresses.Length,
        EligibleUsers = addresses.Length,
        EligibilityResults = [.. addresses.Select(a => new MigrationEligibilityResult { EmailAddress = a, Status = MigrationStatus.Eligible })],
    };
}
