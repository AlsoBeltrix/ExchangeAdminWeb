using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;

namespace ExchangeAdminWeb.Services;

/// <summary>What one Verify action did: whether it landed, what the admin is told, and the write's outcome when one ran.</summary>
internal sealed record VerifyActionResult(bool Succeeded, string Message, VerifyWriteOutcome? Outcome = null);

/// <summary>
/// docs/DirectoryObjectVerify-Plan.md C.2. The Fix, Remove and Ignore actions of the Admin Settings Protected Principals
/// pane's Verify results (A1/A2), each a direct primitive - none goes through the pane's whole-store save.
/// </summary>
/// <remarks>
/// <para><b>Authorization, fresh at every click (R3).</b> The primitives inherit nothing from the pane's save, so each action
/// asks the <c>AdminSettings</c> policy for the clicking admin's current principal before it writes. A failed authorization is
/// a refusal: nothing written, audited as one.</para>
/// <para><b>The action must be one the finding offers</b> (<see cref="IVerifyStoreAdapter.ActionsFor"/>): an Ignore of a
/// not-converted or unreadable row is refused here as well as never offered.</para>
/// <para><b>Snapshot version.</b> Every write checks, under the write lock, that the rules are still the version the item was
/// read with, and refuses otherwise ("the entry has changed since Verify ran; run Verify again"). Remove, Replace and Ignore
/// also compare-and-swap the ignore list against the value read beside the item; a list changed since is read again and the
/// write tried once more, and a second mismatch is refused (B.3).</para>
/// <para><b>Audit, success and refusal separately.</b> A landed action writes <see cref="AuditService.LogSettingsChange"/> under
/// <see cref="Section"/> with the rows it changed and <c>{finding, observed, action}</c>; a refusal writes
/// <see cref="SettingsSaveAudit.LogSettingsSaveRefused"/>, a failure <see cref="SettingsSaveAudit.LogSettingsSaveException"/>.
/// A Verify press writes one <see cref="AuditService.LogSettingsChange"/> row with its counts.</para>
/// </remarks>
internal sealed class ProtectedPrincipalVerifyActions
{
    /// <summary>The audit section of every Verify record of this store.</summary>
    public const string Section = "Verify:" + VerifyIgnoreList.ProtectedPrincipalsStore;

    /// <summary>What a refused write whose item is stale says.</summary>
    public const string ChangedSinceVerify = "The entry has changed since Verify ran; run Verify again. Nothing was changed.";

    private readonly ProtectedPrincipalService _service;
    private readonly IAuthorizationService _authorization;
    private readonly AuditService _audit;
    private readonly ILogger _logger;
    private readonly IVerifyStoreAdapter _adapter = new ProtectedPrincipalVerifyAdapter();

    public ProtectedPrincipalVerifyActions(ProtectedPrincipalService service, IAuthorizationService authorization, AuditService audit, ILogger logger)
    {
        _service = service;
        _authorization = authorization;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>Rename (LabelDrift): the row's label and its companion become the object's current name (B.5).</summary>
    public Task<VerifyActionResult> RenameAsync(ClaimsPrincipal user, string performedBy, string ip, VerifyItem item, VerifyFinding finding) =>
        RunAsync(user, performedBy, ip, item, finding, VerifyAction.Rename, ignoresRaw: null, (repository, _) =>
            item.Identifier is { } identifier && !string.IsNullOrWhiteSpace(finding.Detail)
                ? repository.RewriteLabel(item.RowKey, identifier, finding.Detail, item.SnapshotVersion)
                : new VerifyWriteOutcome(VerifyWriteStatus.RowChanged, null, null, null, null));

    /// <summary>Convert (NotConverted, convertible): the row stored by name gets the identifier row its resolution found.</summary>
    public Task<VerifyActionResult> ConvertAsync(ClaimsPrincipal user, string performedBy, string ip, VerifyItem item, VerifyFinding finding) =>
        RunAsync(user, performedBy, ip, item, finding, VerifyAction.Convert, ignoresRaw: null, (repository, _) =>
            finding.Resolution is { Kind: VerifyResolutionKind.Convertible, IdKind: { } idKind, Identifier: { } identifier }
                ? repository.ConvertRow(new ProtectedPrincipalIdConversion(item.RowKey, item.StoredValue, idKind, identifier), item.SnapshotVersion)
                : new VerifyWriteOutcome(VerifyWriteStatus.RowChanged, null, null, null, null));

    /// <summary>Remove: the row, its companion and its ignore entries, in one transaction.</summary>
    public Task<VerifyActionResult> RemoveAsync(
        ClaimsPrincipal user, string performedBy, string ip, VerifyItem item, VerifyFinding finding, string? ignoresRaw) =>
        RunAsync(user, performedBy, ip, item, finding, VerifyAction.Remove, ignoresRaw, (repository, ignores) =>
            repository.RemoveWithCompanionAndIgnores(new VerifyRowRef(item.RowKey, item.StoredValue), item.SnapshotVersion, ignores));

    /// <summary>
    /// Replace: the row gives way, in place, to <paramref name="newRow"/> - the picked object, validated by the caller as an
    /// Add is - with <paramref name="newCompanion"/>; the old row's ignore entries go in the same transaction.
    /// </summary>
    public Task<VerifyActionResult> ReplaceAsync(
        ClaimsPrincipal user, string performedBy, string ip, VerifyItem item, VerifyFinding finding,
        string newRow, string? newCompanion, string? ignoresRaw) =>
        RunAsync(user, performedBy, ip, item, finding, VerifyAction.Replace, ignoresRaw, (repository, ignores) =>
            repository.ReplaceWithCompanionAndIgnores(new VerifyRowRef(item.RowKey, item.StoredValue), newRow, newCompanion, item.SnapshotVersion, ignores),
            replacement: newRow);

    /// <summary>Ignore: records the finding in the ignore list after checking the row under the write lock; changes no rule.</summary>
    public Task<VerifyActionResult> IgnoreAsync(
        ClaimsPrincipal user, string performedBy, string ip, VerifyItem item, VerifyFinding finding, string? ignoresRaw)
    {
        var entry = new VerifyIgnoreEntry(item.Store, item.RowKey, item.IdentifierOrText, finding.Kind.ToString(), finding.Observed,
            performedBy, DateTime.UtcNow.ToString("O"));
        return RunAsync(user, performedBy, ip, item, finding, VerifyAction.Ignore, ignoresRaw, (repository, ignores) =>
            repository.AddVerifyIgnore(new VerifyRowRef(item.RowKey, item.StoredValue), entry, item.SnapshotVersion, ignores));
    }

    /// <summary>
    /// A Verify press: re-authorized, then one audit row with its counts and the store's <c>verify_last</c> (plain set,
    /// display-only). False - nothing recorded but the refusal - when the admin is no longer authorized.
    /// </summary>
    public async Task<bool> AuthorizePressAsync(ClaimsPrincipal user, string performedBy, string ip)
    {
        var refusal = await RefuseUnlessAuthorizedAsync(user, performedBy, ip, "Verify");
        return refusal is null;
    }

    /// <summary>Records a completed press: one success audit row with the counts, and the store's last-verified line.</summary>
    public void RecordPress(string performedBy, string ip, VerifyReport report)
    {
        try
        {
            _audit.LogSettingsChange(performedBy, ip, Section, [], [], new Dictionary<string, object?>
            {
                ["action"] = "Verify",
                ["total"] = report.Total,
                ["verified"] = report.Verified,
                ["findings"] = report.Findings,
                ["ignored"] = report.Ignored,
                ["unreadable"] = report.Unreadable,
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Audit failed for a Verify press of the protected principals"); }

        try
        {
            _service.WriteVerifyLast(VerifyIgnoreList.ProtectedPrincipalsStore, new VerifyLastEntry(
                report.At.UtcDateTime.ToString("O"), performedBy, report.Total, report.Findings, report.Ignored, report.Unreadable));
        }
        catch (Exception ex)
        {
            // Display-only: a press whose line could not be stored still verified.
            _logger.LogWarning(ex, "The last-verified line of the protected principals could not be stored");
        }
    }

    private async Task<string?> RefuseUnlessAuthorizedAsync(ClaimsPrincipal user, string performedBy, string ip, string action)
    {
        AuthorizationResult result;
        try
        {
            result = await _authorization.AuthorizeAsync(user, "AdminSettings");
        }
        catch (Exception ex)
        {
            return _audit.LogSettingsSaveException(_logger, performedBy, ip, Section, ex,
                $"Not done: the authorization check for {action} failed ({ex.Message}). Nothing was changed.");
        }

        if (result.Succeeded)
            return null;

        const string message = "Authorization check failed. Nothing was changed.";
        _audit.LogSettingsSaveRefused(_logger, performedBy, ip, Section, "Authorization", message);
        return message;
    }

    private async Task<VerifyActionResult> RunAsync(
        ClaimsPrincipal user, string performedBy, string ip, VerifyItem item, VerifyFinding finding, VerifyAction action,
        string? ignoresRaw, Func<ProtectedPrincipalRepository, string?, VerifyWriteOutcome> write, string? replacement = null)
    {
        var refusal = await RefuseUnlessAuthorizedAsync(user, performedBy, ip, action.ToString());
        if (refusal is not null)
            return new VerifyActionResult(false, refusal);

        if (!string.Equals(item.Store, _adapter.Store, StringComparison.Ordinal) || !_adapter.ActionsFor(item, finding).Contains(action))
        {
            var message = $"Not done: {action} is not offered for an entry that is {finding.Kind}. Nothing was changed.";
            _audit.LogSettingsSaveRefused(_logger, performedBy, ip, Section, "NotOffered", message);
            return new VerifyActionResult(false, message);
        }

        VerifyWriteOutcome outcome;
        try
        {
            outcome = await Task.Run(() => _service.ApplyVerifyWrite(repository => write(repository, ignoresRaw)));
            if (outcome.Status == VerifyWriteStatus.IgnoresChanged)
            {
                // B.3: the list changed since it was read beside the item - by another admin, or the other instance. Read it
                // again and try once more; the write re-checks the rules' version and the row itself.
                var fresh = _service.ReadForVerify()?.IgnoresRaw;
                outcome = await Task.Run(() => _service.ApplyVerifyWrite(repository => write(repository, fresh)));
            }
        }
        catch (Exception ex)
        {
            var failure = _audit.LogSettingsSaveException(_logger, performedBy, ip, Section, ex, $"Not done: {ex.Message}");
            return new VerifyActionResult(false, failure);
        }

        if (outcome.Status != VerifyWriteStatus.Written)
        {
            var message = outcome.Status switch
            {
                VerifyWriteStatus.StoreUnreadable => "Not done: the protection rules could not be read under the lock. Nothing was changed.",
                VerifyWriteStatus.IgnoresChanged => "Not done: the ignore list changed again while this was being written - by another administrator, or on the other instance. Nothing was changed. Run Verify again.",
                VerifyWriteStatus.IgnoresUnreadable => "Not done: the stored ignore list cannot be read, so nothing can be added to it. Nothing was changed.",
                _ => ChangedSinceVerify,
            };
            _audit.LogSettingsSaveRefused(_logger, performedBy, ip, Section, outcome.Status.ToString(), message);
            return new VerifyActionResult(false, message, outcome);
        }

        try
        {
            var extra = new Dictionary<string, object?>
            {
                ["action"] = action.ToString(),
                ["finding"] = finding.Kind.ToString(),
                ["observed"] = finding.Observed,
                ["row"] = $"{item.RowKey}:{item.StoredValue}",
            };
            if (replacement is not null)
                extra["replacement"] = replacement;

            if (action == VerifyAction.Ignore)
                _audit.LogSettingsChange(performedBy, ip, Section, [], [$"Ignored {item.RowKey}:{item.IdentifierOrText} {finding.Kind} ({finding.Observed})"], extra);
            else
                _audit.LogSettingsChange(performedBy, ip, Section, RuleEntries(outcome.Before), RuleEntries(outcome.After), extra);
        }
        catch (Exception ex) { _logger.LogError(ex, "Audit failed for a Verify {Action} of the protected principals", action); }

        return new VerifyActionResult(true, SuccessText(action, item, finding), outcome);
    }

    /// <summary>
    /// The report after one landed action (<paramref name="acted"/>, <paramref name="outcome"/>): the acted row leaves it - its row
    /// changed - unless it was ignored, when it stays as ignored; every other row the write left stored exactly as read keeps its
    /// finding at the write's new version - the write read those rows back under its own lock, so they are what the store now
    /// holds - and any row the write did not leave leaves the report. A refused outcome changes nothing.
    /// </summary>
    internal static VerifyReport Rebase(VerifyReport report, VerifyItemResult acted, VerifyWriteOutcome outcome)
    {
        if (outcome.Status != VerifyWriteStatus.Written || outcome.After is not { } after || outcome.AfterVersion is not { } version)
            return report;

        var stored = ProtectedPrincipalVerifyAdapter.ItemsFrom(after, version)
            .Select(i => (i.RowKey, i.StoredValue))
            .ToHashSet();
        VerifyIgnoreList.TryParse(outcome.AfterIgnores, out var ignores);

        var results = new List<VerifyItemResult>(report.Results.Count);
        foreach (var r in report.Results)
        {
            if (!stored.Contains((r.Item.RowKey, r.Item.StoredValue)))
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

            results.Add(r with { Item = item });
        }

        return report with { Results = results };
    }

    private static string SuccessText(VerifyAction action, VerifyItem item, VerifyFinding finding) => action switch
    {
        VerifyAction.Rename => $"Renamed '{item.Label}' to '{finding.Detail}'. In effect now.",
        VerifyAction.Convert => $"Converted '{item.Label}' to its identifier. In effect now.",
        VerifyAction.Remove => $"Removed '{(item.Label.Length > 0 ? item.Label : item.StoredValue)}'. In effect now.",
        VerifyAction.Replace => $"Replaced '{(item.Label.Length > 0 ? item.Label : item.StoredValue)}'. In effect now.",
        VerifyAction.Ignore => $"Ignored '{finding.Kind}' for '{(item.Label.Length > 0 ? item.Label : item.StoredValue)}'.",
        _ => "Done.",
    };

    /// <summary>Every row as <c>kind:value</c>, so the record lists what the action added and removed.</summary>
    internal static string[] RuleEntries(ProtectedPrincipalData? rules) =>
        rules is null
            ? []
            : rules.Users.Select(v => "user:" + v)
                .Concat(rules.Groups.Select(v => "group:" + v))
                .Concat(rules.OrganizationalUnits.Select(v => "ou:" + v))
                .Concat(rules.SamAccountNamePatterns.Select(v => "sam_pattern:" + v))
                .Concat(rules.GroupTargets.Select(v => "group_target:" + v))
                .Concat(rules.UserIds.Select(v => "user_sid:" + v))
                .Concat(rules.GroupIds.Select(v => "group_sid:" + v))
                .Concat(rules.OuIds.Select(v => "ou_guid:" + v))
                .ToArray();
}

/// <summary>The last Verify of one store (plan C.1): when, by whom, and its counts. Display-only.</summary>
internal sealed record VerifyLastEntry(string At, string By, int Total, int Findings, int Ignored, int Unreadable)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The stored <c>verify_last</c> map, store to entry; an empty map when unset or unreadable (display-only).</summary>
    public static Dictionary<string, VerifyLastEntry> ParseMap(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new(StringComparer.Ordinal);
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, VerifyLastEntry>>(raw, Json);
            return map is null ? new(StringComparer.Ordinal) : new(map.Where(kv => kv.Value is not null), StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    public static string SerializeMap(Dictionary<string, VerifyLastEntry> map) => JsonSerializer.Serialize(map, Json);
}
