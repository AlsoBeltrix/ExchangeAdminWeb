using System.Security.Claims;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;

namespace ExchangeAdminWeb.Services;

/// <summary>Which config pane a Verify press covers: one module's verified settings (docs/DirectoryObjectVerify-Plan.md A5-A8).</summary>
internal sealed record ModuleConfigVerifyScope(string ModuleId)
{
    public string Store => VerifyIgnoreList.ConfigStore;

    /// <summary>The <c>verify_last</c> key of this pane: <c>Config:&lt;ModuleId&gt;</c>, in the shape of the other panes' (plan C.1).</summary>
    public string LastVerifiedKey => $"{Store}:{ModuleId}";
}

/// <summary>What a Verify press of one config pane read (plan B.1, B.3), from one read transaction.</summary>
internal sealed record ModuleConfigVerifyWorkset(ModuleConfigVerifyScope Scope, ModuleConfigVerifySnapshot Snapshot)
{
    public string Version => Snapshot.Version;

    /// <summary>The items this workset verifies: one per entry of a list setting, one per single value.</summary>
    public IReadOnlyList<VerifyItem> Items => ModuleConfigVerifyAdapter.ItemsFrom(Scope.ModuleId, Snapshot.Values, Version);

    public string? IgnoresRaw => Snapshot.IgnoresRaw;

    /// <summary>The ignore list; empty, with a Warning, when it does not parse.</summary>
    public IReadOnlyList<VerifyIgnoreEntry> Ignores(ILogger? logger) => VerifyIgnoreList.ParseOrEmpty(IgnoresRaw, logger);
}

/// <summary>
/// What one config-pane Verify action did. <see cref="NeedsConfirmation"/>: nothing was written because the action takes the
/// last entry of a setting whose empty value widens its module, and the admin has not yet confirmed it; the message is the
/// warning to confirm.
/// </summary>
internal sealed record ModuleConfigVerifyActionResult(
    bool Succeeded, string Message, ModuleGroupVerifyWriteOutcome? Outcome = null, bool NeedsConfirmation = false);

/// <summary>
/// docs/DirectoryObjectVerify-Plan.md C.2 (slice V4). The Edit, Remove and Ignore actions of the Verify results on a Module
/// Configuration config pane (Migration, Emergency Disable, AD Attribute Editor, BitLocker Recovery, Conference Rooms) - the
/// shape of <see cref="ModuleGroupVerifyActions"/>.
/// </summary>
/// <remarks>
/// <para><b>The page's own gate, fresh at every click (R3).</b> A config pane may be configured by a global admin or by that
/// module's module admin (<c>ModuleConfig.razor</c>'s load and its config save), so every action - and the press, and Edit -
/// asks the <c>AdminSettings</c> policy, or else whether the clicking admin is a module admin of this module, for their
/// current principal. Anyone else is refused, nothing is written, and the refusal is audited.</para>
/// <para><b>Remove takes one entry out by the module's own parsing</b> (R3-1): one comma entry of Migration's excluded groups,
/// one semicolon entry of AD Attribute Editor's search bases with the others kept; Emergency Disable's OU, BitLocker's search
/// base and a Conference Rooms address are cleared. The last AD Attribute Editor search base goes only once confirmed, because
/// an empty list lets the editor search the whole directory (<see cref="LastEntryWarning"/>). Each write checks, under the lock,
/// that the pane's verified values are still the version Verify read, and compare-and-swaps the ignore list (B.3); a list
/// changed since is read again and the write tried once more.</para>
/// <para><b>Edit</b> (the Replace action) is the pane's ordinary edit and Save: only authorized and checked as offered here.
/// <b>Ignore</b> changes only what Verify reports: no module reads the list.</para>
/// <para><b>Audit, success and refusal separately</b>, under <c>Verify:Config</c>.</para>
/// </remarks>
internal sealed class ModuleConfigVerifyActions
{
    /// <summary>The warning a removal of the last AD Attribute Editor search base is confirmed with (plan C.2).</summary>
    public const string LastEntryWarning = "no search-base boundary will remain: the editor may then search the whole directory";

    /// <summary>The audit section of every config-pane Verify record.</summary>
    public static string Section => ModuleGroupVerifyActions.SectionOf(VerifyIgnoreList.ConfigStore);

    private readonly ModuleConfigService _moduleConfig;
    private readonly ModuleAdminService _moduleAdmins;
    private readonly IAuthorizationService _authorization;
    private readonly AuditService _audit;
    private readonly ILogger _logger;
    private static readonly ModuleConfigVerifyAdapter Adapter = new();

    public ModuleConfigVerifyActions(
        ModuleConfigService moduleConfig, ModuleAdminService moduleAdmins, IAuthorizationService authorization, AuditService audit, ILogger logger)
    {
        _moduleConfig = moduleConfig;
        _moduleAdmins = moduleAdmins;
        _authorization = authorization;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>What a press of <paramref name="scope"/> reads, from one read transaction; null when the config cannot be read.</summary>
    public ModuleConfigVerifyWorkset? ReadForVerify(ModuleConfigVerifyScope scope)
    {
        var snapshot = _moduleConfig.ReadForVerify(scope.ModuleId);
        return snapshot.Readable ? new ModuleConfigVerifyWorkset(scope, snapshot) : null;
    }

    /// <summary>A Verify press: re-authorized as the page's gate. False - nothing recorded but the refusal - otherwise.</summary>
    public async Task<bool> AuthorizePressAsync(ClaimsPrincipal user, string performedBy, string ip, ModuleConfigVerifyScope scope) =>
        await RefuseUnlessAuthorizedAsync(user, performedBy, ip, scope, "Verify") is null;

    /// <summary>Records a completed press: one success audit row with the counts, and the pane's last-verified line.</summary>
    public void RecordPress(string performedBy, string ip, ModuleConfigVerifyScope scope, VerifyReport report)
    {
        try
        {
            _audit.LogSettingsChange(performedBy, ip, Section, [], [], new Dictionary<string, object?>
            {
                ["action"] = "Verify",
                ["module"] = scope.ModuleId,
                ["total"] = report.Total,
                ["verified"] = report.Verified,
                ["findings"] = report.Findings,
                ["ignored"] = report.Ignored,
                ["unreadable"] = report.Unreadable,
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Audit failed for a Verify press of {Module}'s settings", scope.ModuleId); }

        try
        {
            _moduleConfig.WriteVerifyLast(scope.LastVerifiedKey, new VerifyLastEntry(
                report.At.UtcDateTime.ToString("O"), performedBy, report.Total, report.Findings, report.Ignored, report.Unreadable));
        }
        catch (Exception ex)
        {
            // Display-only: a press whose line could not be stored still verified.
            _logger.LogWarning(ex, "The last-verified line of {Key} could not be stored", scope.LastVerifiedKey);
        }
    }

    /// <summary>The last press of <paramref name="scope"/>, display-only; null when none is stored.</summary>
    public VerifyLastEntry? LastVerified(ModuleConfigVerifyScope scope) =>
        _moduleConfig.ReadVerifyLast().GetValueOrDefault(scope.LastVerifiedKey);

    /// <summary>
    /// Whether removing <paramref name="item"/> from <paramref name="readValue"/> (the setting as Verify read it) takes the last
    /// entry of a setting whose empty value widens its module - the removal that must be confirmed first.
    /// </summary>
    public static bool RemovesLastGuardedEntry(VerifyItem item, string? readValue) =>
        ModuleConfigVerifyAdapter.SettingFor(item.RowKey) is { WarnBeforeLastEntry: true } setting
        && setting.EntriesOf(setting.WithoutEntry(readValue, item.StoredValue)).Count == 0;

    /// <summary>
    /// Edit (the Replace action; plan C.2 "edit the text"): authorized at the click and checked as offered; nothing is written
    /// here - the page opens the field for editing and the admin saves through the pane's ordinary Save.
    /// </summary>
    public async Task<ModuleConfigVerifyActionResult> AuthorizeEditAsync(
        ClaimsPrincipal user, string performedBy, string ip, ModuleConfigVerifyScope scope, VerifyItem item, VerifyFinding finding)
    {
        if (await RefuseUnlessReadyAsync(user, performedBy, ip, scope, item, finding, VerifyAction.Replace) is { } refusal)
            return refusal;
        return new ModuleConfigVerifyActionResult(true,
            $"Edit '{item.StoredValue}' in the field above and press Save. Until you save, nothing has changed.");
    }

    /// <summary>Remove: the one entry, by the module's own parsing, and every ignore no entry names any more, in one transaction.</summary>
    /// <param name="readValue">The setting's value as Verify read it - what decides whether this is its last guarded entry.</param>
    /// <param name="confirmed">The admin confirmed <see cref="LastEntryWarning"/> (only asked for the last guarded entry).</param>
    public async Task<ModuleConfigVerifyActionResult> RemoveAsync(
        ClaimsPrincipal user, string performedBy, string ip, ModuleConfigVerifyScope scope, VerifyItem item, VerifyFinding finding,
        string? ignoresRaw, string? readValue, bool confirmed)
    {
        if (await RefuseUnlessReadyAsync(user, performedBy, ip, scope, item, finding, VerifyAction.Remove) is { } refusal)
            return refusal;

        if (!confirmed && RemovesLastGuardedEntry(item, readValue))
            return new ModuleConfigVerifyActionResult(false,
                $"'{item.StoredValue}' is the last search base: {LastEntryWarning}. Confirm to remove it, or Edit it instead.",
                NeedsConfirmation: true);

        return await RunAsync(performedBy, ip, scope, item, finding, VerifyAction.Remove, ignoresRaw, ignores =>
            _moduleConfig.ApplyVerifyWrite(scope.ModuleId, r => r.RemoveVerifyEntry(
                scope.ModuleId, item.RowKey, item.StoredValue, confirmed, item.SnapshotVersion, ignores)));
    }

    /// <summary>Ignore: records the finding after checking the entry under the write lock; changes no setting. Display-only.</summary>
    public async Task<ModuleConfigVerifyActionResult> IgnoreAsync(
        ClaimsPrincipal user, string performedBy, string ip, ModuleConfigVerifyScope scope, VerifyItem item, VerifyFinding finding, string? ignoresRaw)
    {
        if (await RefuseUnlessReadyAsync(user, performedBy, ip, scope, item, finding, VerifyAction.Ignore) is { } refusal)
            return refusal;

        var entry = new VerifyIgnoreEntry(item.Store, item.RowKey, item.IdentifierOrText, finding.Kind.ToString(), finding.Observed,
            performedBy, DateTime.UtcNow.ToString("O"));
        return await RunAsync(performedBy, ip, scope, item, finding, VerifyAction.Ignore, ignoresRaw, ignores =>
            _moduleConfig.ApplyVerifyWrite(scope.ModuleId, r => r.AddVerifyIgnore(
                scope.ModuleId, item.RowKey, item.StoredValue, entry, item.SnapshotVersion, ignores)));
    }

    // The page's own gate (ModuleConfig.razor's load, SaveConfigAsync): a global admin, or a module admin of this module.
    private async Task<string?> RefuseUnlessAuthorizedAsync(ClaimsPrincipal user, string performedBy, string ip, ModuleConfigVerifyScope scope, string action)
    {
        bool allowed;
        try
        {
            allowed = (await _authorization.AuthorizeAsync(user, "AdminSettings")).Succeeded || _moduleAdmins.IsModuleAdmin(scope.ModuleId, user);
        }
        catch (Exception ex)
        {
            return _audit.LogSettingsSaveException(_logger, performedBy, ip, Section, ex,
                $"Not done: the authorization check for {action} failed ({ex.Message}). Nothing was changed.");
        }

        if (allowed)
            return null;

        const string message = "Only global admins and this module's admins can act on Verify results here. Nothing was changed.";
        _audit.LogSettingsSaveRefused(_logger, performedBy, ip, Section, "Authorization", message);
        return message;
    }

    // Authorized, an item of this pane, and an action its finding offers - or the refusal, audited.
    private async Task<ModuleConfigVerifyActionResult?> RefuseUnlessReadyAsync(
        ClaimsPrincipal user, string performedBy, string ip, ModuleConfigVerifyScope scope, VerifyItem item, VerifyFinding finding, VerifyAction action)
    {
        if (await RefuseUnlessAuthorizedAsync(user, performedBy, ip, scope, action.ToString()) is { } refusal)
            return new ModuleConfigVerifyActionResult(false, refusal);

        var inScope = string.Equals(item.Store, scope.Store, StringComparison.Ordinal)
            && ModuleConfigVerifyAdapter.SettingsOf(scope.ModuleId).Any(s => string.Equals(s.Key, item.RowKey, StringComparison.OrdinalIgnoreCase));
        if (!inScope || !Adapter.ActionsFor(item, finding).Contains(action))
        {
            var message = $"Not done: {action} is not offered for an entry that is {finding.Kind}. Nothing was changed.";
            _audit.LogSettingsSaveRefused(_logger, performedBy, ip, Section, "NotOffered", message);
            return new ModuleConfigVerifyActionResult(false, message);
        }

        return null;
    }

    private async Task<ModuleConfigVerifyActionResult> RunAsync(
        string performedBy, string ip, ModuleConfigVerifyScope scope, VerifyItem item, VerifyFinding finding,
        VerifyAction action, string? ignoresRaw, Func<string?, ModuleGroupVerifyWriteOutcome> write)
    {
        ModuleGroupVerifyWriteOutcome outcome;
        try
        {
            outcome = await Task.Run(() => write(ignoresRaw));
            if (outcome.Status == VerifyWriteStatus.IgnoresChanged)
            {
                // B.3: the list changed since it was read beside the item. Read it again and try once more; the write re-checks
                // the values' version and the entry itself.
                var fresh = ReadForVerify(scope)?.IgnoresRaw;
                outcome = await Task.Run(() => write(fresh));
            }
        }
        catch (Exception ex)
        {
            var failure = _audit.LogSettingsSaveException(_logger, performedBy, ip, Section, ex, $"Not done: {ex.Message}");
            return new ModuleConfigVerifyActionResult(false, failure);
        }

        if (outcome.Status != VerifyWriteStatus.Written)
        {
            var message = outcome.Status switch
            {
                VerifyWriteStatus.StoreUnreadable => "Not done: this module's settings could not be read under the lock. Nothing was changed.",
                VerifyWriteStatus.IgnoresChanged => "Not done: the ignore list changed again while this was being written - by another administrator, or on the other instance. Nothing was changed. Run Verify again.",
                VerifyWriteStatus.IgnoresUnreadable => "Not done: the stored ignore list cannot be read, so nothing can be added to it. Nothing was changed.",
                _ => ProtectedPrincipalVerifyActions.ChangedSinceVerify,
            };
            _audit.LogSettingsSaveRefused(_logger, performedBy, ip, Section, outcome.Status.ToString(), message);
            return new ModuleConfigVerifyActionResult(false, message, outcome);
        }

        try
        {
            var extra = new Dictionary<string, object?>
            {
                ["action"] = action.ToString(),
                ["finding"] = finding.Kind.ToString(),
                ["observed"] = finding.Observed,
                ["module"] = scope.ModuleId,
                ["row"] = $"{item.RowKey}:{item.StoredValue}",
            };
            if (action == VerifyAction.Ignore)
                _audit.LogSettingsChange(performedBy, ip, Section, [], [$"Ignored {item.RowKey}:{item.IdentifierOrText} {finding.Kind} ({finding.Observed})"], extra);
            else
                _audit.LogSettingsChange(performedBy, ip, Section, Rows(outcome.Before, item.RowKey), Rows(outcome.After, item.RowKey), extra);
        }
        catch (Exception ex) { _logger.LogError(ex, "Audit failed for a Verify {Action} of {Module}'s settings", action, scope.ModuleId); }

        return new ModuleConfigVerifyActionResult(true, action == VerifyAction.Ignore
            ? $"Ignored '{finding.Kind}' for '{item.StoredValue}'. This changes only what Verify reports."
            : $"Removed '{item.StoredValue}' from {item.RowKey}. In effect now.", outcome);
    }

    // The one setting the action changed, as the audit row names it.
    private static string[] Rows(IReadOnlyList<GroupGrantRow>? rows, string key) =>
        rows?.Where(r => string.Equals(r.RowKey, key, StringComparison.OrdinalIgnoreCase)).Select(r => $"{r.RowKey}={r.Value}").ToArray() ?? [];

    /// <summary>
    /// The report after one landed action: every entry the write left stored keeps its finding at the write's new version; the
    /// acted entry stays only as ignored; an entry the write took out leaves the report.
    /// </summary>
    internal static VerifyReport Rebase(VerifyReport report, VerifyItemResult acted, ModuleGroupVerifyWriteOutcome outcome)
    {
        if (outcome.Status != VerifyWriteStatus.Written || outcome.After is not { } after || outcome.AfterVersion is not { } version)
            return report;

        var values = after.ToDictionary(r => r.RowKey, r => r.Value, StringComparer.OrdinalIgnoreCase);
        VerifyIgnoreList.TryParse(outcome.AfterIgnores, out var ignores);

        var results = new List<VerifyItemResult>(report.Results.Count);
        foreach (var r in report.Results)
        {
            if (ModuleConfigVerifyAdapter.SettingFor(r.Item.RowKey) is not { } setting
                || !setting.EntriesOf(values.GetValueOrDefault(r.Item.RowKey)).Contains(r.Item.StoredValue, StringComparer.OrdinalIgnoreCase))
                continue;

            var item = r.Item with { SnapshotVersion = version };
            if (ReferenceEquals(r, acted))
            {
                var entry = ignores.FirstOrDefault(e => VerifyIgnoreList.Matches(
                    e, item.Store, item.RowKey, item.IdentifierOrText, r.Finding.Kind.ToString(), r.Finding.Observed));
                if (entry is not null)
                    results.Add(new VerifyItemResult(item, VerifyFinding.Ignored(r.Finding, entry)));
                continue;
            }

            results.Add(new VerifyItemResult(item, r.Finding));
        }

        return report with { Results = results };
    }
}
